using System;
using System.Collections.Generic;
using System.Threading;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Presses, holds and releases keyboard keys in the running game
    /// through the Input System (design note docs/design-notes/
    /// 2026-09-22-game-input-tools.md section 4). With uap_play_mode the
    /// agent could run the scene and with uap_console_logs read what it
    /// said, but "does the jump actually work" still needed a human at the
    /// keyboard -- or `uloop simulate-keyboard`, the last capability that
    /// made a project install uLoop for nothing else.
    ///
    /// Pollable rather than synchronous because a press is not one event:
    /// the down must be applied in one input update and the up in a later
    /// one, or the game never sees a press edge. Each Poll is one Editor
    /// tick, so "hold for two ticks, then release, then read the device
    /// back" is three polls, none of which blocks the main thread.
    ///
    /// The held set is this tool's, kept across calls so that a
    /// KeyboardState snapshot (which is complete, not a delta) always
    /// carries every key the agent is holding, and cleared when Play Mode
    /// ends, because the device it described is gone with it.
    /// </summary>
    public sealed class UapGameKeyTool : IUapPollableTool, IUapAbandonableTool
    {
        public const string ToolName = "uap_game_key";

        private static readonly Dictionary<string, object> HeldKeys = new Dictionary<string, object>(StringComparer.Ordinal);
        private static bool _playModeHookInstalled;

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Presses, holds or releases a keyboard key in the RUNNING game (Play Mode) through the"
                    + " Input System, so you can test controls yourself: press Space to jump, hold W to walk,"
                    + " then check with uap_console_logs / uap_editor_screenshot. action: press (default; down,"
                    + " hold 'duration' seconds, up -- at least one frame) / down (hold until 'up') / up /"
                    + " release_all. key: an Input System Key name (W, Space, Enter, LeftShift, Digit1, F5;"
                    + " case-insensitive, and shorthand like 'shift', 'esc', '1', 'up' is accepted)."
                    + " Requires Play Mode (not paused, except release_all), the com.unity.inputsystem package"
                    + " and game code that reads Keyboard.current -- the legacy Input class cannot be driven,"
                    + " and the reply says so. Held keys are released when Play Mode ends. For UI buttons use"
                    + " uap_game_pointer instead.";
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
                            .Set("enum", JsonNode.NewArray().Add("press").Add("down").Add("up").Add("release_all"))
                            .Set("description", "'press' (the default) taps the key: down, hold 'duration',"
                                + " up. 'down' holds it until a later 'up'. 'release_all' releases every key"
                                + " this tool holds (allowed while paused) and reports what the device still"
                                + " shows pressed."))
                        .Set("key", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Key name (Input System Key enum, e.g. W, Space, LeftShift,"
                                + " Digit1, UpArrow, F5). Required except for release_all."))
                        .Set("duration", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "For 'press': seconds to hold before releasing, 0 to "
                                + UapGameInputPolicy.MaxHoldSeconds + " (default 0 = one frame). For a"
                                + " longer hold use 'down' then 'up'.")))
                    .Set("additionalProperties", false);
            }
        }

        /// <summary>Synchronous fallback for callers that bypass the dispatcher (DirectUapToolExecutor); production dispatch polls.</summary>
        public JsonNode Execute(JsonNode input)
        {
            object state = null;
            JsonNode result;
            while (!Poll(input, ref state, out result))
            {
                Thread.Sleep(10);
            }
            return result;
        }

        public bool Poll(JsonNode input, ref object stateObj, out JsonNode result)
        {
            var state = stateObj as KeyState;
            if (state == null)
            {
                state = Begin(input);
                stateObj = state;
                if (state.Phase == Phase.Done)
                {
                    result = state.Result;
                    return true;
                }
                result = null;
                return false;
            }
            return Continue(state, out result);
        }

        private KeyState Begin(JsonNode input)
        {
            UapGameKeyAction action = UapGameKeyPolicy.ParseAction(input["action"].AsString(null));
            var state = new KeyState { Action = action, Keyboard = null };

            if (action != UapGameKeyAction.ReleaseAll)
            {
                string keyError;
                state.KeyName = UapGameKeyPolicy.NormalizeKeyName(input["key"].AsString(null), out keyError);
                if (state.KeyName == null)
                {
                    throw new ArgumentException(keyError);
                }
            }
            if (action == UapGameKeyAction.Press)
            {
                state.HoldSeconds = input["duration"].AsDouble(0.0);
                string holdError;
                if (!UapGameInputPolicy.ValidateHold(state.HoldSeconds, "duration", out holdError))
                {
                    throw new ArgumentException(holdError);
                }
            }

            string stateError;
            if (!UapGameInputPolicy.CheckPlayState(ReadPlayState(), action == UapGameKeyAction.ReleaseAll, out stateError))
            {
                throw new InvalidOperationException(stateError);
            }
            string keyboardError;
            state.Keyboard = UapInputSystemKeyboardAccess.CurrentKeyboard(out keyboardError);
            if (state.Keyboard == null)
            {
                throw new InvalidOperationException(keyboardError);
            }
            EnsurePlayModeHook();

            if (action == UapGameKeyAction.ReleaseAll)
            {
                state.ReleasedNames = new List<string>(HeldKeys.Keys);
                HeldKeys.Clear();
                QueueHeld(state.Keyboard);
                state.Phase = Phase.AwaitReadback;
                return state;
            }

            string heldError;
            if (!UapGameKeyPolicy.CheckHeldState(action, HeldKeys.ContainsKey(state.KeyName), state.KeyName, out heldError))
            {
                throw new InvalidOperationException(heldError);
            }
            string parseError;
            state.Key = UapInputSystemKeyboardAccess.ParseKey(state.KeyName, out parseError);
            if (state.Key == null)
            {
                throw new ArgumentException(parseError);
            }

            switch (action)
            {
                case UapGameKeyAction.Press:
                    HeldKeys[state.KeyName] = state.Key;
                    QueueHeld(state.Keyboard);
                    state.StartedAt = EditorApplication.timeSinceStartup;
                    state.Phase = Phase.Holding;
                    break;
                case UapGameKeyAction.Down:
                    HeldKeys[state.KeyName] = state.Key;
                    QueueHeld(state.Keyboard);
                    state.Phase = Phase.AwaitReadback;
                    break;
                case UapGameKeyAction.Up:
                    HeldKeys.Remove(state.KeyName);
                    QueueHeld(state.Keyboard);
                    state.Phase = Phase.AwaitReadback;
                    break;
            }
            return state;
        }

        private bool Continue(KeyState state, out JsonNode result)
        {
            state.Polls++;
            if (!EditorApplication.isPlaying)
            {
                // The device this call was driving is gone; the held set is
                // cleared by the play-mode hook. Say so rather than report
                // a press that no game observed.
                HeldKeys.Clear();
                throw new InvalidOperationException("Play Mode ended while '" + state.KeyName
                    + "' was being " + (state.Phase == Phase.Holding ? "held" : "applied")
                    + "; the game did not observe the whole input. Restart it with uap_play_mode and retry.");
            }
            switch (state.Phase)
            {
                case Phase.Holding:
                    if (state.Polls < UapGameInputPolicy.MinHoldPolls
                        || EditorApplication.timeSinceStartup - state.StartedAt < state.HoldSeconds)
                    {
                        result = null;
                        return false;
                    }
                    state.HeldFor = EditorApplication.timeSinceStartup - state.StartedAt;
                    HeldKeys.Remove(state.KeyName);
                    QueueHeld(state.Keyboard);
                    state.Phase = Phase.AwaitReadback;
                    result = null;
                    return false;
                case Phase.AwaitReadback:
                    // One tick after the last queued state: the input
                    // update has run, so the device answers for what the
                    // game now sees.
                    state.Result = Describe(state);
                    state.Phase = Phase.Done;
                    result = state.Result;
                    return true;
                default:
                    result = state.Result;
                    return true;
            }
        }

        /// <summary>
        /// The dispatcher gave up on this call: a 'press' still in its hold
        /// releases its key (a key held by a completed 'down' call is the
        /// agent's to release and stays held). IUapAbandonableTool; design
        /// note 2026-09-22-uloop-phase1 section 3.
        /// </summary>
        public void OnAbandoned(object pollState)
        {
            var state = pollState as KeyState;
            if (state == null || state.Action != UapGameKeyAction.Press || state.Phase != Phase.Holding)
            {
                return;
            }
            HeldKeys.Remove(state.KeyName);
            state.Phase = Phase.Done;
            if (EditorApplication.isPlaying && state.Keyboard != null)
            {
                QueueHeld(state.Keyboard);
            }
        }

        private static JsonNode Describe(KeyState state)
        {
            JsonNode result = JsonNode.NewObject().Set("action", ActionName(state.Action));
            if (state.KeyName != null)
            {
                result.Set("key", state.KeyName);
            }
            List<string> devicePressed = UapInputSystemKeyboardAccess.PressedKeyNames(state.Keyboard);
            JsonNode held = JsonNode.NewArray();
            foreach (string name in HeldKeys.Keys)
            {
                held.Add(name);
            }
            result.Set("held", held);
            JsonNode pressedNode = JsonNode.NewArray();
            for (int i = 0; i < devicePressed.Count; i++)
            {
                pressedNode.Add(devicePressed[i]);
            }
            result.Set("devicePressed", pressedNode);

            string message;
            switch (state.Action)
            {
                case UapGameKeyAction.Press:
                    result.Set("heldSeconds", Math.Round(state.HeldFor, 3));
                    bool stillPressed = devicePressed.Contains(state.KeyName);
                    message = "Pressed '" + state.KeyName + "' for " + Math.Round(state.HeldFor, 3) + " s and released it."
                        + (stillPressed
                            ? " The device still reports it pressed -- something else (a physical key, another"
                              + " injector) is holding it."
                            : string.Empty);
                    break;
                case UapGameKeyAction.Down:
                    bool observed = devicePressed.Contains(state.KeyName);
                    message = "'" + state.KeyName + "' is now held" + (observed ? "" : " (queued; the device has not reported it pressed yet)")
                        + ". Release it with action:up.";
                    break;
                case UapGameKeyAction.Up:
                    message = "Released '" + state.KeyName + "'."
                        + (devicePressed.Contains(state.KeyName)
                            ? " The device still reports it pressed -- something else is holding it."
                            : string.Empty);
                    break;
                default:
                    JsonNode released = JsonNode.NewArray();
                    for (int i = 0; i < state.ReleasedNames.Count; i++)
                    {
                        released.Add(state.ReleasedNames[i]);
                    }
                    result.Set("released", released);
                    message = state.ReleasedNames.Count == 0
                        ? "This tool was holding no keys."
                        : "Released " + string.Join(", ", state.ReleasedNames.ToArray()) + ".";
                    if (devicePressed.Count > 0)
                    {
                        message += " The device still reports pressed: " + string.Join(", ", devicePressed.ToArray())
                            + " -- held by something other than this tool (a physical key, or a game that"
                            + " changes the device state).";
                    }
                    break;
            }
            result.Set("message", message);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static string ActionName(UapGameKeyAction action)
        {
            switch (action)
            {
                case UapGameKeyAction.Press: return "press";
                case UapGameKeyAction.Down: return "down";
                case UapGameKeyAction.Up: return "up";
                default: return "release_all";
            }
        }

        private static void QueueHeld(object keyboard)
        {
            UapInputSystemKeyboardAccess.QueueState(keyboard, HeldKeys.Values);
            UapInputSystemKeyboardAccess.FlushIfManual();
        }

        private static UapPlayModeSnapshot ReadPlayState()
        {
            return new UapPlayModeSnapshot
            {
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                WillChangePlayMode = EditorApplication.isPlayingOrWillChangePlaymode,
                IsCompiling = EditorApplication.isCompiling
            };
        }

        /// <summary>
        /// Clears the held set when Play Mode ends. Installed on first use,
        /// never from [InitializeOnLoadMethod]: a project that never calls
        /// this tool pays nothing for it (the 2026-09-21 note's rule for
        /// everything that could otherwise become always-on).
        /// </summary>
        private static void EnsurePlayModeHook()
        {
            if (_playModeHookInstalled)
            {
                return;
            }
            _playModeHookInstalled = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                HeldKeys.Clear();
            }
        }

        /// <summary>Test seam: the keys this tool believes it is holding.</summary>
        internal static List<string> HeldKeyNamesForTests()
        {
            return new List<string>(HeldKeys.Keys);
        }

        /// <summary>Test seam: forget every held key without touching a device.</summary>
        internal static void ResetHeldForTests()
        {
            HeldKeys.Clear();
        }

        private enum Phase
        {
            Holding,
            AwaitReadback,
            Done
        }

        private sealed class KeyState
        {
            public UapGameKeyAction Action;
            public string KeyName;
            public object Key;
            public object Keyboard;
            public double HoldSeconds;
            public double StartedAt;
            public double HeldFor;
            public int Polls;
            public Phase Phase;
            public List<string> ReleasedNames;
            public JsonNode Result;
        }
    }
}
