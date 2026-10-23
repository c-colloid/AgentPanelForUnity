using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Plays a uap_input_record recording back through the same
    /// keyboard/mouse Input System injection uap_game_key/uap_game_mouse
    /// use (uLoop's replay-input; docs/design-notes/2026-09-23-uloop-phase2.md
    /// section 3.4) -- reproduce a real interaction on demand instead of
    /// re-issuing the same tool calls by hand every time.
    ///
    /// A thin action-dispatch shell over <see cref="UapInputReplayer"/> (the
    /// EditorApplication.update-driven background session) and
    /// <see cref="UapInputRecordingPolicy"/>, mirroring UapInputRecordTool's
    /// own split -- and, like it, NOT IUapPollableTool: action:start returns
    /// immediately and playback runs on later editor ticks, since a replay
    /// can run far longer than UapMainThreadDispatcher's ~15 s HTTP wait.
    /// </summary>
    public sealed class UapInputReplayTool : IUapTool
    {
        public const string ToolName = "uap_input_replay";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Plays back a uap_input_record recording, injecting the same keyboard/mouse states at"
                    + " the recorded times through the same Input System path uap_game_key/uap_game_mouse use --"
                    + " use it to reproduce a real interaction (a jump, a UI flow, a combo) on demand instead of"
                    + " re-issuing the same tool calls by hand."
                    + " action: start (default) / stop / status."
                    + " start reads the file, validates it, and plays it back in the BACKGROUND on"
                    + " EditorApplication.update -- other tools keep working while it runs. Only one replay (and"
                    + " no recording) may run at a time; replay refuses while a recording is in progress, so a"
                    + " recording never captures its own injected events."
                    + " path: required, the file a previous uap_input_record action:stop/status returned (or"
                    + " any path inside the project)."
                    + " speed: optional playback rate, " + UapInputRecordingPolicy.MinSpeed + " to "
                    + UapInputRecordingPolicy.MaxSpeed + " (default " + UapInputRecordingPolicy.DefaultSpeed
                    + "; 2.0 plays twice as fast, 0.5 half speed)."
                    + " Every key/button press this tool injects is held for at least "
                    + UapGameInputPolicy.MinHoldPolls + " Editor ticks even if the recording held it for less, so a"
                    + " press edge stays observable to the game; a key/button still down when the replay stops,"
                    + " finishes, Play Mode ends, or an error occurs is ALWAYS released first."
                    + " stop aborts the replay now (releasing everything first)."
                    + " status reports progress (appliedEventCount/eventCount, 0..1) for the current replay, or"
                    + " the last one's result, and survives a domain reload."
                    + " Requires Play Mode (not paused) and the com.unity.inputsystem package (same requirement"
                    + " as uap_game_key/uap_game_mouse) -- start with uap_play_mode action:start first.";
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

        /// <summary>NOT ReadOnly: this tool injects real input into the running game (the same mutation uap_game_key/uap_game_mouse are ReadOnly:false for), and runs a background job across many editor ticks like UapRecordVideoTool/UapInputRecordTool.</summary>
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
                            .Set("description", "'start' (default): begin playback. 'stop': abort the current"
                                + " replay now (releasing every held key/button first). 'status': report progress."))
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "start only, required. The recording file to play back."))
                        .Set("speed", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "start only. Playback rate, " + UapInputRecordingPolicy.MinSpeed
                                + " to " + UapInputRecordingPolicy.MaxSpeed + " (default "
                                + UapInputRecordingPolicy.DefaultSpeed + ").")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            UapInputReplayAction action = UapInputRecordingPolicy.ParseReplayAction(input["action"].AsString(null));
            switch (action)
            {
                case UapInputReplayAction.Stop:
                    return UapToolResults.Text(JsonWriter.Write(UapInputReplayer.Stop()));
                case UapInputReplayAction.Status:
                    return UapToolResults.Text(JsonWriter.Write(UapInputReplayer.Status()));
                default:
                    return UapToolResults.Text(JsonWriter.Write(UapInputReplayer.Start(input)));
            }
        }
    }
}
