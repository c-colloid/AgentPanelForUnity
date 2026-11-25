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
    /// The background half of uap_input_record (design note
    /// docs/design-notes/2026-09-23-uloop-phase2.md section 3.4, uLoop's
    /// record-input): a static, EditorApplication.update-driven sampling
    /// session, entirely outside UapMainThreadDispatcher's per-call
    /// lifecycle -- action:start validates and arms it, then returns
    /// immediately; the actual sampling happens on later, ordinary editor
    /// ticks, the same shape as <see cref="UapVideoRecorder"/> (uap_record_video)
    /// and UapCompileTool's deferred compile.
    ///
    /// SAMPLING vs onEvent: this class polls device state once per Editor
    /// tick (PressedKeyNames, mouse position/buttons/scroll) and stores only
    /// the DIFF from the previous tick, rather than subscribing
    /// InputSystem.onEvent and decoding each InputEventPtr by reflection.
    /// uLoop 3.6.3's own record-input (Packages/src, Editor+Runtime) hooks
    /// onEvent for exactly that reason -- it is the only way to see EVERY
    /// physical event, including one shorter than a single Editor tick (a
    /// very fast tap between two ticks) -- but decoding InputEventPtr's
    /// internal layout by reflection would have to track it across every
    /// InputSystem version this package claims to support (1.7-1.19), each
    /// of which is free to change that layout, where QueueStateEvent's
    /// public surface (already relied on by uap_game_key/uap_game_mouse)
    /// is not. This class accepts the resulting gap -- a key held for less
    /// than one Editor tick is invisible to it -- and mitigates it exactly
    /// the way uap_game_key mitigates the SAME gap on the injection side:
    /// UapGameInputPolicy.MinHoldPolls already requires uap_game_key/
    /// uap_game_mouse to hold a key/button for at least 2 ticks so a game
    /// can observe the press edge, so anything worth reproducing was
    /// already at least 2 ticks long when it happened, and this class's
    /// sampling loses nothing a REPLAY of the SAME recording (also
    /// tick-based, see UapInputReplayer) would have reproduced faithfully
    /// either way. A genuinely sub-tick human keypress recorded from a
    /// physical keyboard is the one case this cannot capture -- see the
    /// design note's "unverified" list.
    ///
    /// Recorded coordinates are the device's RAW position/scroll
    /// (Mouse.current.position/scroll, bottom-left origin) rather than
    /// uap_game_mouse's top-left/y-down convention: this format exists to
    /// be replayed bit-for-bit by <see cref="UapInputReplayer"/>, which
    /// queues the very same numbers straight back through
    /// UapInputSystemMouseAccess.QueueState, so there is no reason to
    /// convert coordinates in either direction and risk a round-trip bug.
    ///
    /// All of the reasoning that does not need Unity (the file format,
    /// diffing, path/argument rules) lives in UapInputRecordingPolicy
    /// (ci/SmokeTests-covered); this class is the Editor half, mirroring
    /// UapVideoRecorder's own split from UapRecordVideoPolicy.
    /// </summary>
    public static class UapInputRecorder
    {
        private const string SessionStateKey = "Colloid.AgentPanel.Ops.UapInputRecordResult";
        private const string FilePrefix = "uap_input_";

        private sealed class ActiveSession
        {
            public string Path;
            public double MaxSeconds;
            public double StartedAtEditorTime;
            public long StartedAtUtcTicks;
            public object Keyboard;
            public object Mouse;
            public readonly List<UapInputRecordEvent> Events = new List<UapInputRecordEvent>();
            public HashSet<string> PreviousKeys = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> PreviousButtons = new HashSet<string>(StringComparer.Ordinal);
            public double PreviousMouseX;
            public double PreviousMouseY;
            public bool HasPreviousMousePosition;
        }

        private static ActiveSession _active;
        private static bool _hooksInstalled;

        /// <summary>True while THIS domain holds a live recording session. The one thing action:start's "already recording" refusal is allowed to trust -- a persisted "recording" record surviving a reload with no live session must never block a new start (same reasoning as UapVideoRecorder.IsActive).</summary>
        public static bool IsActive
        {
            get { return _active != null; }
        }

        // -- Public entry points (called from UapInputRecordTool.Execute) --

        public static JsonNode Start(JsonNode input)
        {
            // Argument SHAPE first (ArgumentException), Editor/environment
            // STATE after (InvalidOperationException) -- same order as
            // UapGameKeyTool.Begin (key/duration parsed before
            // CheckPlayState), so a bad argument is diagnosed without first
            // needing Play Mode, and a batch-mode test can exercise it.
            double maxSeconds = input["max_seconds"].IsNull
                ? UapInputRecordingPolicy.DefaultMaxSeconds
                : input["max_seconds"].AsDouble(UapInputRecordingPolicy.DefaultMaxSeconds);
            string maxSecondsError;
            if (!UapInputRecordingPolicy.ValidateMaxSeconds(maxSeconds, out maxSecondsError))
            {
                throw new ArgumentException(maxSecondsError);
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string requestedPath = input["path"].AsString(null);
            string absolutePath;
            string pathError;
            if (!UapInputRecordingPolicy.TryResolveRecordPath(projectRoot, requestedPath, DateTime.UtcNow, out absolutePath, out pathError))
            {
                throw new ArgumentException(pathError);
            }

            string activeError;
            if (!UapInputRecordingPolicy.DecideRecordStart(IsActive, UapInputReplayer.IsActive, out activeError))
            {
                throw new InvalidOperationException(activeError);
            }

            string playError;
            if (!UapGameInputPolicy.CheckPlayState(ReadPlayState(), allowPaused: true, out playError))
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
                MaxSeconds = maxSeconds,
                StartedAtEditorTime = EditorApplication.timeSinceStartup,
                StartedAtUtcTicks = DateTime.UtcNow.Ticks,
                Keyboard = keyboard,
                Mouse = mouse,
            };
            _active = session;

            // Sample the baseline (t=0) against an empty previous state, so
            // any key/button already held when recording starts, and the
            // cursor's starting position, are captured as t=0 events --
            // otherwise a replay would have no idea where to put the mouse
            // or which keys to hold until the first CHANGE happened to occur.
            Sample(session, 0.0);
            if (!IsActive)
            {
                // Pathological (the baseline sample alone would need
                // MaxEvents+ keys/buttons already held to happen), but
                // Sample can call StopInternal, which already persisted a
                // "stopped" result -- do not clobber it with a "recording
                // started" one below.
                return LoadPersisted();
            }

            EnsureHooksInstalled();
            JsonNode started = BuildStatusJson(UapInputRecordState.Recording, session, 0.0, string.Empty, "Recording started.");
            SavePersisted(started);
            return started;
        }

        public static JsonNode Stop()
        {
            if (!IsActive)
            {
                throw new InvalidOperationException("No recording is in progress; call uap_input_record action:start first.");
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
                return BuildStatusJson(UapInputRecordState.Recording, session, elapsed < 0 ? 0 : elapsed,
                    string.Empty, "Recording in progress.");
            }
            JsonNode persisted = LoadPersisted();
            string state = persisted["state"].AsString("idle");
            if (string.Equals(state, "recording", StringComparison.Ordinal))
            {
                // A "recording" record with nothing live in THIS domain means
                // a reload/restart happened without OnBeforeAssemblyReload
                // getting to finalize it -- say so rather than claim a
                // recording is still running (same story as UapVideoRecorder.Status).
                persisted.Set("message", "No recording is active in this Editor session; the last known state"
                    + " was 'recording' from before a reload/restart this tool did not observe finishing cleanly.");
            }
            else if (string.Equals(state, "idle", StringComparison.Ordinal))
            {
                persisted.Set("message", "No recording has been started through uap_input_record yet.");
            }
            return persisted;
        }

        // -- Editor-event-driven sampling loop -------------------------------

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
            double elapsed = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
            if (elapsed >= session.MaxSeconds)
            {
                StopInternal(UapInputStopReason.Duration, null);
                return;
            }
            Sample(session, elapsed);
        }

        /// <summary>Reads the device once and appends whatever changed since the previous sample; auto-stops once <see cref="UapInputRecordingPolicy.MaxEvents"/> is reached mid-append so a burst never overshoots the cap.</summary>
        private static void Sample(ActiveSession session, double elapsed)
        {
            List<string> pressedNames = UapInputSystemKeyboardAccess.PressedKeyNames(session.Keyboard);
            var currentKeys = new HashSet<string>(pressedNames, StringComparer.Ordinal);
            Vector2 position = UapInputSystemMouseAccess.ReadPosition(session.Mouse);
            Vector2 scroll = UapInputSystemMouseAccess.ReadScroll(session.Mouse);
            var currentButtons = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < UapInputRecordingPolicy.MouseButtonNames.Length; i++)
            {
                string name = UapInputRecordingPolicy.MouseButtonNames[i];
                UapGamePointerButton button = (UapGamePointerButton)Enum.Parse(typeof(UapGamePointerButton), name, true);
                if (UapInputSystemMouseAccess.IsPressed(session.Mouse, button))
                {
                    currentButtons.Add(name);
                }
            }

            var diff = new List<UapInputRecordEvent>();
            UapInputRecordingPolicy.DiffKeys(session.PreviousKeys, currentKeys, elapsed, diff);
            UapInputRecordingPolicy.DiffButtons(session.PreviousButtons, currentButtons, elapsed, diff);
            UapInputRecordEvent moveEvt;
            if (UapInputRecordingPolicy.TryDiffMouseMove(session.HasPreviousMousePosition, session.PreviousMouseX, session.PreviousMouseY,
                    position.x, position.y, elapsed, out moveEvt))
            {
                diff.Add(moveEvt);
            }
            UapInputRecordEvent scrollEvt;
            if (UapInputRecordingPolicy.TryScrollEvent(scroll.x, scroll.y, elapsed, out scrollEvt))
            {
                diff.Add(scrollEvt);
            }

            for (int i = 0; i < diff.Count; i++)
            {
                if (session.Events.Count >= UapInputRecordingPolicy.MaxEvents)
                {
                    StopInternal(UapInputStopReason.MaxEvents, null);
                    return;
                }
                session.Events.Add(diff[i]);
            }

            session.PreviousKeys = currentKeys;
            session.PreviousButtons = currentButtons;
            session.PreviousMouseX = position.x;
            session.PreviousMouseY = position.y;
            session.HasPreviousMousePosition = true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }
            if (_active != null)
            {
                StopInternal(UapInputStopReason.PlayModeExit, null);
            }
        }

        /// <summary>Last chance to write the file before the domain tears down -- the in-memory event buffer would otherwise be lost with nothing on disk at all (unlike UapVideoRecorder, which streams frames to disk as it goes, this class buffers events in memory until a stop).</summary>
        private static void OnBeforeAssemblyReload()
        {
            if (_active == null)
            {
                return;
            }
            StopInternal(UapInputStopReason.AssemblyReload,
                "The Editor is reloading the domain (a script compile, or entering/leaving Play Mode with"
                + " domain reload enabled); the recording was finalized up to the last captured sample.");
        }

        private static void StopInternal(string stoppedBy, string message)
        {
            ActiveSession session = _active;
            if (session == null)
            {
                return;
            }
            _active = null;
            UnhookAll();

            double durationSeconds = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
            if (durationSeconds < 0) { durationSeconds = 0; }

            var file = new UapInputRecordingFile
            {
                FormatVersion = UapInputRecordingPolicy.CurrentFormatVersion,
                RecordedAtUtcTicks = session.StartedAtUtcTicks,
                DurationSeconds = durationSeconds,
            };
            file.Events.AddRange(session.Events);

            string writeError = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(session.Path));
                AtomicFile.WriteAllTextOrThrow(session.Path, JsonWriter.Write(UapInputRecordingPolicy.ToJson(file)));
            }
            catch (Exception ex)
            {
                writeError = "Could not write '" + session.Path + "': " + ex.Message;
            }

            string finalMessage = writeError ?? message ?? UapInputRecordingPolicy.DescribeRecordStopReason(stoppedBy);
            JsonNode result = BuildStatusJson(UapInputRecordState.Stopped, session, durationSeconds, stoppedBy, finalMessage);
            SavePersisted(result);

            if (writeError == null)
            {
                try { PruneOldRecordings(Path.GetDirectoryName(session.Path), session.Path); }
                catch (Exception) { }
            }
        }

        // -- Hooks (lazy on first Start, unsubscribed once the recording ends) --

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

        private static void PruneOldRecordings(string folder, string justWritten)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }
            string[] files = Directory.GetFiles(folder, FilePrefix + "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            List<string> toDelete = UapInputRecordingPolicy.SelectFilesToPrune(files, UapInputRecordingPolicy.DefaultKeepCount, justWritten);
            for (int i = 0; i < toDelete.Count; i++)
            {
                try { File.Delete(toDelete[i]); }
                catch (Exception) { }
            }
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

        // -- Persistence (SessionState; survives the domain reload a recording itself may end in) --

        private enum UapInputRecordState { Idle, Recording, Stopped }

        private static JsonNode BuildStatusJson(UapInputRecordState state, ActiveSession session, double elapsedOrDuration,
            string stoppedBy, string message)
        {
            return JsonNode.NewObject()
                .Set("state", state == UapInputRecordState.Recording ? "recording" : state == UapInputRecordState.Stopped ? "stopped" : "idle")
                .Set("path", session != null ? session.Path : string.Empty)
                .Set("eventCount", session != null ? session.Events.Count : 0)
                .Set("durationSeconds", Math.Round(elapsedOrDuration, 3))
                .Set("maxSeconds", session != null ? session.MaxSeconds : 0.0)
                .Set("startedAtUtcTicks", session != null ? session.StartedAtUtcTicks : 0L)
                .Set("stoppedBy", stoppedBy ?? string.Empty)
                .Set("message", message ?? string.Empty);
        }

        private static JsonNode LoadPersisted()
        {
            string json = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return JsonNode.NewObject().Set("state", "idle").Set("path", string.Empty).Set("eventCount", 0)
                    .Set("durationSeconds", 0.0).Set("maxSeconds", 0.0).Set("startedAtUtcTicks", 0L)
                    .Set("stoppedBy", string.Empty).Set("message", string.Empty);
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(json, out node, out error))
            {
                return JsonNode.NewObject().Set("state", "idle").Set("path", string.Empty).Set("eventCount", 0)
                    .Set("durationSeconds", 0.0).Set("maxSeconds", 0.0).Set("startedAtUtcTicks", 0L)
                    .Set("stoppedBy", string.Empty).Set("message", string.Empty);
            }
            return node;
        }

        private static void SavePersisted(JsonNode node)
        {
            SessionState.SetString(SessionStateKey, JsonWriter.Write(node));
        }

        // -- Test seam (InternalsVisibleTo Colloid.AgentPanel.Editor.Tests) --

        /// <summary>Forgets any live session (without writing or pruning) and the persisted record. A real recording must never be left running into the next test.</summary>
        internal static void ResetForTests()
        {
            _active = null;
            UnhookAll();
            SessionState.SetString(SessionStateKey, string.Empty);
        }
    }
}
