using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Records the Game/Scene view to a video file in the background
    /// (uLoop's record-video; docs/design-notes/2026-09-23-uloop-phase2.md
    /// section 3.1). Before this tool existed the only "evidence" a
    /// multi-step interaction (a jump arc, a UI animation, a sequence of
    /// clicks) could produce was a handful of separate
    /// uap_editor_screenshot frames; this captures the whole thing as one
    /// file the agent (or the person reading the transcript) can actually
    /// play back.
    ///
    /// action:start validates arguments, opens a
    /// UnityEditor.Media.MediaEncoder and returns immediately with the
    /// recording id/path/fps/size -- the actual frame-by-frame capture runs
    /// on EditorApplication.update afterwards (see UapVideoRecorder), so
    /// this tool's own Execute call never blocks for the recording's
    /// duration and every other tool keeps working while it records. This
    /// is a deliberate difference from uap_game_mouse/uap_game_key's
    /// IUapPollableTool pattern: those hold the dispatcher's call open
    /// tick-by-tick for a few seconds at most, but a recording can run for
    /// up to UapRecordVideoPolicy.MaxMaxSeconds (300 s) -- far longer than
    /// UapMainThreadDispatcher's ~15 s HTTP wait -- so it has to live
    /// entirely outside that call's lifecycle instead, the same way
    /// uap_compile's CompilationPipeline subscription runs outside the
    /// Execute call that armed it.
    ///
    /// All of the actual reasoning (argument ranges, even-dimension
    /// rounding, wall-clock pacing target, output naming/pruning, the
    /// StoppedBy vocabulary, the persisted JSON shape) lives in
    /// UapRecordVideoPolicy (Unity-free, ci/SmokeTests-covered); the Editor
    /// half (MediaEncoder, the camera/window pixel capture, the
    /// EditorApplication.update/playModeStateChanged/beforeAssemblyReload
    /// hooks) lives in UapVideoRecorder. This class is a thin
    /// action-dispatch shell over the two, mirroring UapCompileTool's own
    /// split between itself and UapCompilePolicy/UapCompileResultStore.
    ///
    /// Module "editor": the same module uap_editor_screenshot is in, since
    /// this is the same "look at what happened" family of tools, and it
    /// reuses uap_editor_screenshot's own view/capture argument parsing
    /// (UapEditorScreenshotPaths.TryParseView/TryParseCapture) so the two
    /// tools agree on vocabulary and defaults exactly.
    /// </summary>
    public sealed class UapRecordVideoTool : IUapTool
    {
        public const string ToolName = "uap_record_video";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Records the Game/Scene view to a video file (mp4/H.264 on Windows and macOS; webm/VP8"
                    + " on Linux, where the Editor has no H.264 encoder) and returns its path -- use it to"
                    + " capture a short clip as evidence for a multi-step interaction, the way"
                    + " uap_editor_screenshot captures one frame."
                    + " action: start (default) / stop / status."
                    + " start validates and begins recording in the BACKGROUND -- frames are captured on"
                    + " EditorApplication.update at wall-clock pacing (duplicating or skipping frames so the"
                    + " video's duration matches real time) -- and returns immediately; other tools keep"
                    + " working while it records, and only ONE recording runs at a time."
                    + " capture:\"camera\" (default, same convention as uap_editor_screenshot) renders the"
                    + " view's camera offscreen: works without the window visible or focused, but never shows"
                    + " Screen Space - Overlay UI. capture:\"window\" reads the view's actual displayed pixels"
                    + " (Overlay UI included) but needs it visible on screen; for view:\"game\" this includes"
                    + " the Game view's own toolbar at the top (the Editor gives no per-frame hook to crop that"
                    + " out the way capture:\"window\" already does for view:\"scene\")."
                    + " Caps: max_seconds (default " + UapRecordVideoPolicy.DefaultMaxSeconds + ", max "
                    + UapRecordVideoPolicy.MaxMaxSeconds + ", auto-stops with stoppedBy:\"duration\"), fps"
                    + " (default " + UapRecordVideoPolicy.DefaultFps + ", max " + UapRecordVideoPolicy.MaxFps
                    + "). Width/height come from the view and are rounded down to even numbers (video codecs"
                    + " require it); max_width optionally downscales (aspect kept)."
                    + " A recording started in Edit Mode is KILLED by entering Play Mode afterward (domain"
                    + " reload) -- start Play Mode first (uap_play_mode action:start) if you intend to record"
                    + " gameplay. stop_on_play_exit (default true) stops the recording cleanly when Play Mode"
                    + " ends instead of leaving it to whatever the domain reload does."
                    + " status reports frames written, dropped frames, seconds recorded, file size and"
                    + " stoppedBy (\"stop\"/\"duration\"/\"play-mode-exit\"/\"assembly-reload\"/\"error\") for"
                    + " the current or last recording, and survives a domain reload.";
            }
        }

        public string Module
        {
            get { return "editor"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        /// <summary>
        /// NOT ReadOnly. uap_editor_screenshot is ReadOnly:true, but only as
        /// one of a small, INDIVIDUALLY verified set pinned by
        /// UapReadOnlyToolMetadataTests (see IUapTool.ReadOnly's own doc
        /// comment) -- this is a new tool, not part of that pinned set, and
        /// the default for a tool that writes anything is false until it
        /// goes through that same review. It also writes more than a
        /// screenshot ever does: a video file that can run up to
        /// UapRecordVideoPolicy.MaxMaxSeconds (300 s) and several
        /// megabytes, kept under Temp/ across multiple past recordings
        /// (pruned to the newest UapRecordVideoPolicy.DefaultKeepCount
        /// rather than one throwaway PNG per call), and action:start
        /// installs editor-event subscriptions and runs a background job
        /// for the whole recording -- materially more than "captures one
        /// frame to a scratch file".
        /// </summary>
        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("action", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("start").Add("stop").Add("status"))
                            .Set("description", "'start' (default): begin recording. 'stop': end the current"
                                + " recording now. 'status': report the current/last recording."))
                        .Set("view", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("scene").Add("game"))
                            .Set("description", "start only. \"game\" (default) or \"scene\" -- same meaning as"
                                + " uap_editor_screenshot's 'view'."))
                        .Set("capture", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("camera").Add("window"))
                            .Set("description", "start only. \"camera\" (default): offscreen camera render, no"
                                + " Screen Space - Overlay UI, works without the window visible. \"window\":"
                                + " the view's actual displayed pixels (Overlay UI included); needs it visible"
                                + " on screen."))
                        .Set("fps", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "start only. Frames per second, " + UapRecordVideoPolicy.MinFps
                                + " to " + UapRecordVideoPolicy.MaxFps + " (default "
                                + UapRecordVideoPolicy.DefaultFps + ")."))
                        .Set("max_seconds", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "start only. Auto-stop after this many seconds (stoppedBy:"
                                + "\"duration\"), up to " + UapRecordVideoPolicy.MaxMaxSeconds + " (default "
                                + UapRecordVideoPolicy.DefaultMaxSeconds + ")."))
                        .Set("max_width", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "start only. Downscale to at most this width in pixels (aspect"
                                + " kept, both dimensions rounded down to even). Omit to use the view's own"
                                + " size."))
                        .Set("stop_on_play_exit", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "start only. true (default): stop cleanly (stoppedBy:"
                                + "\"play-mode-exit\") when Play Mode ends, instead of leaving it to whatever a"
                                + " domain reload does.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            UapRecordVideoAction action = UapRecordVideoPolicy.ParseAction(input["action"].AsString(null));
            switch (action)
            {
                case UapRecordVideoAction.Stop:
                    return UapToolResults.Text(JsonWriter.Write(UapVideoRecorder.Stop()));
                case UapRecordVideoAction.Status:
                    return UapToolResults.Text(JsonWriter.Write(UapVideoRecorder.Status()));
                default:
                    return UapToolResults.Text(JsonWriter.Write(UapVideoRecorder.Start(input)));
            }
        }
    }
}
