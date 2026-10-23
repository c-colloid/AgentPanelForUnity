using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The background half of uap_input_replay (design note
    /// docs/design-notes/2026-09-23-uloop-phase2.md section 3.4, uLoop's
    /// replay-input): reads a file <see cref="UapInputRecorder"/> wrote and
    /// queues the SAME keyboard/mouse states back through
    /// UapInputSystemKeyboardAccess/UapInputSystemMouseAccess -- the exact
    /// QueueStateEvent paths uap_game_key/uap_game_mouse already use -- at
    /// the recorded times (optionally sped up/slowed down), on
    /// EditorApplication.update, entirely outside UapMainThreadDispatcher's
    /// per-call lifecycle, the same "start returns immediately, the work
    /// runs on later ticks" shape as UapVideoRecorder/UapInputRecorder.
    ///
    /// Every path that ends a replay -- action:stop, reaching the end of the
    /// file, Play Mode exiting, a domain reload, or an error mid-tick --
    /// goes through <see cref="ReleaseEverything"/> first, so a replay can
    /// never leave a key or mouse button stuck down in the running game.
    /// This mirrors uap_game_key's OnAbandoned/ExitingPlayMode cleanup, but
    /// is slightly more defensive: uap_game_key skips sending a release on
    /// ExitingPlayMode ("the device this call was driving is gone with
    /// it"), where this class still ATTEMPTS one (best-effort, swallowing
    /// any failure) on every stop path including Play Mode exit and a
    /// domain reload, because unlike a single key/mouse call, a replay can
    /// be holding several keys/buttons at once and the exact ordering of
    /// playModeStateChanged/beforeAssemblyReload versus the device actually
    /// being torn down is not something this class can rely on -- a
    /// harmless extra release attempt costs nothing when the device is
    /// already gone, and might reach a still-live one when it is not.
    ///
    /// The timeline scheduling itself -- which events are due at a given
    /// scaled time, and the "hold a press edge for at least
    /// UapGameInputPolicy.MinHoldPolls ticks even if the recording held it
    /// for less" guarantee -- is Unity-free (UapInputRecordingPolicy.
    /// AdvanceReplay); this class only turns its output into actual
    /// QueueStateEvent calls.
    /// </summary>
    public static class UapInputReplayer
    {
        private const string SessionStateKey = "Colloid.AgentPanel.Ops.UapInputReplayResult";

        private sealed class ActiveSession
        {
            public string Path;
            public double Speed;
            public List<UapInputRecordEvent> Events;
            public int CursorIndex;
            public int TickCounter;
            public double StartedAtEditorTime;
            public long StartedAtUtcTicks;
            public readonly Dictionary<string, int> DownTickById = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly UapInputReplayState ReplayState = new UapInputReplayState();
            public object Keyboard;
            public object Mouse;
        }

        private static ActiveSession _active;
        private static bool _hooksInstalled;

        /// <summary>True while THIS domain holds a live replay session -- see UapInputRecorder.IsActive for why a persisted record alone must never gate a new start/refuse a recording.</summary>
        public static bool IsActive
        {
            get { return _active != null; }
        }

        // -- Public entry points (called from UapInputReplayTool.Execute) --

        public static JsonNode Start(JsonNode input)
        {
            // Argument SHAPE first (ArgumentException), Editor/environment
            // STATE after (InvalidOperationException) -- see UapInputRecorder.Start's
            // identical reasoning.
            double speed = input["speed"].IsNull ? UapInputRecordingPolicy.DefaultSpeed : input["speed"].AsDouble(UapInputRecordingPolicy.DefaultSpeed);
            string speedError;
            if (!UapInputRecordingPolicy.ValidateSpeed(speed, out speedError))
            {
                throw new ArgumentException(speedError);
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string requestedPath = input["path"].AsString(null);
            string absolutePath;
            string pathError;
            if (!UapInputRecordingPolicy.TryResolveReplayPath(projectRoot, requestedPath, out absolutePath, out pathError))
            {
                throw new ArgumentException(pathError);
            }

            string activeError;
            if (!UapInputRecordingPolicy.DecideReplayStart(IsActive, UapInputRecorder.IsActive, out activeError))
            {
                throw new InvalidOperationException(activeError);
            }

            string playError;
            if (!UapGameInputPolicy.CheckPlayState(ReadPlayState(), allowPaused: false, out playError))
            {
                throw new InvalidOperationException(playError);
            }

            string keyboardLoadError;
            string mouseLoadError;
            bool keyboardOk = UapInputSystemKeyboardAccess.TryLoad(out keyboardLoadError);
            bool mouseOk = UapInputSystemMouseAccess.TryLoad(out mouseLoadError);
            if (!keyboardOk || !mouseOk)
            {
                throw new InvalidOperationException(keyboardLoadError ?? mouseLoadError);
            }

            string text;
            try
            {
                text = AtomicFile.ReadAllText(absolutePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not read '" + absolutePath + "': " + ex.Message);
            }
            if (text == null)
            {
                throw new InvalidOperationException("No recording file at '" + absolutePath
                    + "' -- pass the path a previous uap_input_record action:stop/status returned.");
            }
            JsonNode node;
            string parseError;
            if (!JsonParser.TryParse(text, out node, out parseError))
            {
                throw new InvalidOperationException("'" + absolutePath + "' is not valid JSON: " + parseError);
            }
            UapInputRecordingFile file;
            string validateError;
            if (!UapInputRecordingPolicy.TryParseFile(node, out file, out validateError))
            {
                throw new InvalidOperationException("'" + absolutePath + "' is not a valid recording: " + validateError);
            }

            string keyboardError;
            object keyboard = UapInputSystemKeyboardAccess.CurrentKeyboard(out keyboardError);
            if (keyboard == null)
            {
                throw new InvalidOperationException(keyboardError);
            }
            string mouseError;
            object mouse = UapInputSystemMouseAccess.CurrentMouse(out mouseError);
            if (mouse == null)
            {
                throw new InvalidOperationException(mouseError);
            }

            var session = new ActiveSession
            {
                Path = absolutePath,
                Speed = speed,
                Events = file.Events,
                StartedAtEditorTime = EditorApplication.timeSinceStartup,
                StartedAtUtcTicks = DateTime.UtcNow.Ticks,
                Keyboard = keyboard,
                Mouse = mouse,
            };
            _active = session;

            // Apply whatever is due at t=0 synchronously (an empty
            // recording, or one whose first events are all at t=0), the
            // same "do not waste a tick on nothing" reasoning as
            // UapVideoRecorder capturing frame 0 inside Start.
            bool finished = ProcessTick(session, 0.0);
            if (finished)
            {
                return LoadPersisted();
            }

            EnsureHooksInstalled();
            JsonNode started = BuildStatusJson("replaying", session, 0.0, string.Empty, "Replay started.");
            SavePersisted(started);
            return started;
        }

        public static JsonNode Stop()
        {
            if (!IsActive)
            {
                throw new InvalidOperationException("No replay is in progress; call uap_input_replay action:start first.");
            }
            StopInternal(UapInputStopReason.Stop, null);
            return LoadPersisted();
        }

        public static JsonNode Status()
        {
            if (IsActive)
            {
                ActiveSession session = _active;
                double elapsed = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
                return BuildStatusJson("replaying", session, elapsed < 0 ? 0 : elapsed, string.Empty, "Replay in progress.");
            }
            JsonNode persisted = LoadPersisted();
            string state = persisted["state"].AsString("idle");
            if (string.Equals(state, "replaying", StringComparison.Ordinal))
            {
                persisted.Set("message", "No replay is active in this Editor session; the last known state"
                    + " was 'replaying' from before a reload/restart this tool did not observe finishing cleanly.");
            }
            else if (string.Equals(state, "idle", StringComparison.Ordinal))
            {
                persisted.Set("message", "No replay has been started through uap_input_replay yet.");
            }
            return persisted;
        }

        // -- Editor-event-driven playback loop --------------------------------

        private static void OnUpdate()
        {
            ActiveSession session = _active;
            if (session == null)
            {
                UnhookAll();
                return;
            }
            if (EditorApplication.isCompiling)
            {
                return;
            }
            session.TickCounter++;
            double elapsedReal = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
            double elapsedScaled = (elapsedReal < 0 ? 0 : elapsedReal) * session.Speed;
            ProcessTick(session, elapsedScaled);
        }

        /// <summary>Applies every event due by <paramref name="elapsedScaledSeconds"/>, queues the resulting device state(s), and finishes the replay once the cursor reaches the end. Returns true when the replay is now finished (caller must not touch <paramref name="session"/> again).</summary>
        private static bool ProcessTick(ActiveSession session, double elapsedScaledSeconds)
        {
            double scrollDx;
            double scrollDy;
            bool keysChanged;
            bool mouseChanged;
            try
            {
                session.CursorIndex = UapInputRecordingPolicy.AdvanceReplay(session.Events, session.CursorIndex,
                    elapsedScaledSeconds, session.TickCounter, UapGameInputPolicy.MinHoldPolls, session.DownTickById,
                    session.ReplayState, out scrollDx, out scrollDy, out keysChanged, out mouseChanged);

                if (keysChanged)
                {
                    QueueKeyboardState(session);
                }
                if (mouseChanged)
                {
                    QueueMouseState(session, scrollDx, scrollDy);
                }
            }
            catch (Exception ex)
            {
                FinishInternal(session, UapInputStopReason.Error, "Replay failed: " + ex.Message);
                return true;
            }

            if (session.CursorIndex >= session.Events.Count)
            {
                FinishInternal(session, UapInputStopReason.Completed, null);
                return true;
            }
            return false;
        }

        private static void QueueKeyboardState(ActiveSession session)
        {
            var keys = new List<object>(session.ReplayState.Keys.Count);
            foreach (string name in session.ReplayState.Keys)
            {
                string error;
                object key = UapInputSystemKeyboardAccess.ParseKey(name, out error);
                if (key == null)
                {
                    throw new InvalidOperationException(error);
                }
                keys.Add(key);
            }
            UapInputSystemKeyboardAccess.QueueState(session.Keyboard, keys);
            UapInputSystemKeyboardAccess.FlushIfManual();
        }

        private static void QueueMouseState(ActiveSession session, double scrollDx, double scrollDy)
        {
            int buttonsMask = 0;
            foreach (string name in session.ReplayState.MouseButtons)
            {
                var button = (UapGamePointerButton)Enum.Parse(typeof(UapGamePointerButton), name, true);
                buttonsMask |= 1 << UapInputSystemMouseAccess.ButtonBit(button);
            }
            var position = new Vector2((float)session.ReplayState.MouseX, (float)session.ReplayState.MouseY);
            var scroll = new Vector2((float)scrollDx, (float)scrollDy);
            object mouseState = UapInputSystemMouseAccess.BuildState(position, Vector2.zero, scroll, buttonsMask);
            UapInputSystemMouseAccess.QueueState(session.Mouse, mouseState);
            UapInputSystemMouseAccess.FlushIfManual();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }
            if (_active != null)
            {
                FinishInternal(_active, UapInputStopReason.PlayModeExit, null);
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_active == null)
            {
                return;
            }
            FinishInternal(_active, UapInputStopReason.AssemblyReload,
                "The Editor is reloading the domain (a script compile, or entering/leaving Play Mode with"
                + " domain reload enabled); every key/button this replay held was released first.");
        }

        private static void StopInternal(string stoppedBy, string message)
        {
            if (_active == null)
            {
                return;
            }
            FinishInternal(_active, stoppedBy, message);
        }

        /// <summary>The one place every stop path (explicit stop, completion, Play Mode exit, a domain reload, an error) converges on: release everything this replay holds, persist the result, unhook. Never re-entrant -- callers null out/replace <see cref="_active"/> via this method only.</summary>
        private static void FinishInternal(ActiveSession session, string stoppedBy, string message)
        {
            if (_active != session)
            {
                return;
            }
            _active = null;
            UnhookAll();
            ReleaseEverything(session);

            double elapsed = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
            JsonNode result = BuildStatusJson("stopped", session, elapsed < 0 ? 0 : elapsed, stoppedBy,
                message ?? UapInputRecordingPolicy.DescribeReplayStopReason(stoppedBy));
            SavePersisted(result);
        }

        /// <summary>
        /// Best-effort: queues an empty KeyboardState and a MouseState with
        /// every button up (position left where it is -- there is no
        /// "correct" place to snap the cursor back to) so nothing this
        /// replay pressed stays stuck down. Swallows any failure (the
        /// device may already be gone by the time some stop paths run) --
        /// see the class comment for why this differs from uap_game_key's
        /// own, narrower cleanup.
        /// </summary>
        private static void ReleaseEverything(ActiveSession session)
        {
            if (session.ReplayState.Keys.Count > 0)
            {
                try
                {
                    UapInputSystemKeyboardAccess.QueueState(session.Keyboard, Array.Empty<object>());
                    UapInputSystemKeyboardAccess.FlushIfManual();
                }
                catch (Exception) { }
                session.ReplayState.Keys.Clear();
            }
            if (session.ReplayState.MouseButtons.Count > 0)
            {
                try
                {
                    var position = new Vector2((float)session.ReplayState.MouseX, (float)session.ReplayState.MouseY);
                    object mouseState = UapInputSystemMouseAccess.BuildState(position, Vector2.zero, Vector2.zero, 0);
                    UapInputSystemMouseAccess.QueueState(session.Mouse, mouseState);
                    UapInputSystemMouseAccess.FlushIfManual();
                }
                catch (Exception) { }
                session.ReplayState.MouseButtons.Clear();
            }
        }

        // -- Hooks (lazy on first Start, unsubscribed once the replay ends) --

        private static void EnsureHooksInstalled()
        {
            if (_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = true;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void UnhookAll()
        {
            if (!_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = false;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
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

        // -- Persistence (SessionState; survives the domain reload a replay itself may end in) --

        private static JsonNode BuildStatusJson(string state, ActiveSession session, double elapsedSeconds, string stoppedBy, string message)
        {
            int eventCount = session.Events != null ? session.Events.Count : 0;
            return JsonNode.NewObject()
                .Set("state", state)
                .Set("path", session.Path ?? string.Empty)
                .Set("speed", session.Speed)
                .Set("eventCount", eventCount)
                .Set("appliedEventCount", session.CursorIndex)
                .Set("progress", Math.Round(UapInputRecordingPolicy.ReplayProgress(session.CursorIndex, eventCount), 4))
                .Set("elapsedSeconds", Math.Round(elapsedSeconds, 3))
                .Set("startedAtUtcTicks", session.StartedAtUtcTicks)
                .Set("stoppedBy", stoppedBy ?? string.Empty)
                .Set("message", message ?? string.Empty);
        }

        private static JsonNode LoadPersisted()
        {
            string json = SessionState.GetString(SessionStateKey, string.Empty);
            JsonNode node;
            if (string.IsNullOrEmpty(json) || !JsonParser.TryParse(json, out node, out _))
            {
                return JsonNode.NewObject().Set("state", "idle").Set("path", string.Empty).Set("speed", UapInputRecordingPolicy.DefaultSpeed)
                    .Set("eventCount", 0).Set("appliedEventCount", 0).Set("progress", 0.0).Set("elapsedSeconds", 0.0)
                    .Set("startedAtUtcTicks", 0L).Set("stoppedBy", string.Empty).Set("message", string.Empty);
            }
            return node;
        }

        private static void SavePersisted(JsonNode node)
        {
            SessionState.SetString(SessionStateKey, JsonWriter.Write(node));
        }

        // -- Test seam (InternalsVisibleTo Colloid.AgentPanel.Editor.Tests) --

        /// <summary>Forgets any live session (releasing best-effort, without persisting) and the persisted record. A real replay must never be left running into the next test.</summary>
        internal static void ResetForTests()
        {
            if (_active != null)
            {
                ReleaseEverything(_active);
            }
            _active = null;
            UnhookAll();
            SessionState.SetString(SessionStateKey, string.Empty);
        }
    }
}
