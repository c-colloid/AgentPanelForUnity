using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Records keyboard/mouse device-state changes during Play Mode to a
    /// JSON file for uap_input_replay to play back later (uLoop's
    /// record-input; docs/design-notes/2026-09-23-uloop-phase2.md section
    /// 3.4). Use it to capture a real interaction once -- by hand, or
    /// through uap_game_key/uap_game_mouse -- and replay it as many times as
    /// a regression check needs, instead of re-issuing the same sequence of
    /// tool calls every time.
    ///
    /// A thin action-dispatch shell over <see cref="UapInputRecorder"/> (the
    /// EditorApplication.update-driven background session) and
    /// <see cref="UapInputRecordingPolicy"/> (the Unity-free file format,
    /// diffing and argument rules), mirroring UapRecordVideoTool's own split
    /// between itself, UapVideoRecorder and UapRecordVideoPolicy -- and, like
    /// that tool, NOT IUapPollableTool: action:start returns immediately and
    /// the actual sampling runs on later editor ticks entirely outside this
    /// call, since a recording can run far longer than
    /// UapMainThreadDispatcher's ~15 s HTTP wait.
    /// </summary>
    public sealed class UapInputRecordTool : IUapTool
    {
        public const string ToolName = "uap_input_record";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Records keyboard/mouse state changes during Play Mode to a JSON file, so"
                    + " uap_input_replay can play the same interaction back later -- use it to capture a real"
                    + " sequence once and replay it as evidence or a regression check, instead of re-issuing"
                    + " the same uap_game_key/uap_game_mouse calls every time."
                    + " action: start (default) / stop / status."
                    + " start samples the keyboard/mouse every Editor tick in the BACKGROUND and stores only"
                    + " what CHANGED (key/button presses and releases, mouse moves, scroll) with a timestamp --"
                    + " a press shorter than one Editor tick is not captured (a physical keypress that fast is"
                    + " rare; anything injected through uap_game_key/uap_game_mouse is already held at least "
                    + UapGameInputPolicy.MinHoldPolls + " ticks, so recording never loses one of THOSE). Only one"
                    + " recording (and no replay) may run at a time; recording refuses while a replay is in"
                    + " progress, so it never captures its own injected events back."
                    + " path: optional; defaults to a timestamped file under Library/UnityAgentPanel/"
                    + "InputRecordings/. An explicit path must resolve inside the project (no '..' segments, no"
                    + " absolute path outside it)."
                    + " max_seconds: auto-stop cap, default " + UapInputRecordingPolicy.DefaultMaxSeconds
                    + ", max " + UapInputRecordingPolicy.MaxMaxSeconds + " (stoppedBy:\"duration\"); recording"
                    + " also auto-stops at " + UapInputRecordingPolicy.MaxEvents + " events (stoppedBy:"
                    + "\"max-events\") regardless of max_seconds."
                    + " stop writes the file and returns its path, event count and duration."
                    + " status reports progress for the current recording, or the last one's result, and"
                    + " survives a domain reload (a script compile, or leaving Play Mode, finalizes and writes"
                    + " the file first -- stoppedBy:\"assembly-reload\"/\"play-mode-exit\")."
                    + " Requires Play Mode and the com.unity.inputsystem package (same requirement as"
                    + " uap_game_key/uap_game_mouse) -- start with uap_play_mode action:start first.";
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
        /// NOT ReadOnly: action:start writes a JSON file (under Library/,
        /// outside version control, but still a write this session did not
        /// ask for by name) and runs a background job across many editor
        /// ticks, the same reasoning UapRecordVideoTool gives for its own
        /// ReadOnly:false. It is also not one of the small, individually
        /// verified set UapReadOnlyToolMetadataTests pins.
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
                                + " recording now and write the file. 'status': report the current/last recording."))
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "start only. Where to write the recording. Omit for a"
                                + " timestamped default under Library/UnityAgentPanel/InputRecordings/. Must"
                                + " resolve inside the project."))
                        .Set("max_seconds", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "start only. Auto-stop after this many seconds (stoppedBy:"
                                + "\"duration\"), up to " + UapInputRecordingPolicy.MaxMaxSeconds + " (default "
                                + UapInputRecordingPolicy.DefaultMaxSeconds + ").")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            UapInputRecordAction action = UapInputRecordingPolicy.ParseRecordAction(input["action"].AsString(null));
            switch (action)
            {
                case UapInputRecordAction.Stop:
                    return UapToolResults.Text(JsonWriter.Write(UapInputRecorder.Stop()));
                case UapInputRecordAction.Status:
                    return UapToolResults.Text(JsonWriter.Write(UapInputRecorder.Status()));
                default:
                    return UapToolResults.Text(JsonWriter.Write(UapInputRecorder.Start(input)));
            }
        }
    }
}
