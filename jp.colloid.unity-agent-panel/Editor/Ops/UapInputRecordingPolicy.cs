using System;
using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What uap_input_record was asked to do. Default is Start, same reasoning as uap_record_video's UapRecordVideoAction (docs/design-notes/2026-09-23-uloop-phase2.md section 3.4).</summary>
    public enum UapInputRecordAction
    {
        Start = 0,
        Stop = 1,
        Status = 2,
    }

    /// <summary>What uap_input_replay was asked to do.</summary>
    public enum UapInputReplayAction
    {
        Start = 0,
        Stop = 1,
        Status = 2,
    }

    /// <summary>One recorded device-state change. Only the fields that apply to <see cref="Type"/> are meaningful; the rest are left at their default.</summary>
    public enum UapInputRecordEventType
    {
        KeyDown,
        KeyUp,
        MouseMove,
        MouseButtonDown,
        MouseButtonUp,
        MouseScroll,
    }

    /// <summary>
    /// One entry in the recording's "events" array: a single device-state
    /// CHANGE (not a full snapshot) at time <see cref="T"/> seconds since the
    /// recording started. Plain data; <see cref="UapInputRecordingPolicy"/>
    /// is the only (de)serializer and validator.
    /// </summary>
    public sealed class UapInputRecordEvent
    {
        public double T;
        public UapInputRecordEventType Type;
        /// <summary>KeyDown/KeyUp: an Input System Key enum member name, exactly as UapInputSystemKeyboardAccess.PressedKeyNames reports it (canonical spelling -- no alias resolution needed, this is machine-generated).</summary>
        public string Key;
        /// <summary>MouseButtonDown/Up: "Left" | "Right" | "Middle" (UapGamePointerButton's member names).</summary>
        public string Button;
        /// <summary>MouseMove: the device's raw position (Mouse.current.position.ReadValue(), bottom-left origin, pixels) -- NOT the top-left/y-down convention uap_game_mouse's own x/y arguments use. See the class comment on UapInputRecorder for why.</summary>
        public double X;
        public double Y;
        /// <summary>MouseScroll: the device's raw scroll delta for that one sample (Mouse.current.scroll.ReadValue()).</summary>
        public double Dx;
        public double Dy;
    }

    /// <summary>The whole recording file uap_input_record writes and uap_input_replay reads.</summary>
    public sealed class UapInputRecordingFile
    {
        public int FormatVersion;
        public long RecordedAtUtcTicks;
        public double DurationSeconds;
        public readonly List<UapInputRecordEvent> Events = new List<UapInputRecordEvent>();
    }

    /// <summary>Mutable device state a replay is driving towards, built up by applying events in order. Unity-free (no InputSystem types) so <see cref="UapInputRecordingPolicy.AdvanceReplay"/> can be smoke-tested without a Keyboard/Mouse device.</summary>
    public sealed class UapInputReplayState
    {
        public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> MouseButtons = new HashSet<string>(StringComparer.Ordinal);
        public double MouseX;
        public double MouseY;
        public bool HasMousePosition;
    }

    /// <summary>The wire "stoppedBy" vocabulary shared by uap_input_record and uap_input_replay (some values apply to only one of the two).</summary>
    public static class UapInputStopReason
    {
        public const string Stop = "stop";
        public const string Duration = "duration";
        public const string MaxEvents = "max-events";
        public const string Completed = "completed";
        public const string PlayModeExit = "play-mode-exit";
        public const string AssemblyReload = "assembly-reload";
        public const string Error = "error";
    }

    /// <summary>
    /// Every rule uap_input_record / uap_input_replay decide by, with no
    /// UnityEditor/UnityEngine/InputSystem in sight (design note
    /// docs/design-notes/2026-09-23-uloop-phase2.md section 3.4): the
    /// recording file format and its versioning, parse/validate, event
    /// diffing (turning two device snapshots into zero or more change
    /// events), replay timeline scheduling (which events are due at a given
    /// scaled time, and the press-edge hold guarantee), speed/path/cap
    /// argument rules and output-file naming/pruning. UapInputRecorder /
    /// UapInputReplayer (Unity-aware: EditorApplication.update, the
    /// InputSystem reflection accessors, SessionState, file IO) are the only
    /// things that are not here.
    /// </summary>
    public static class UapInputRecordingPolicy
    {
        public const int CurrentFormatVersion = 1;

        // -- Caps (design note section 3.4 "Caps") --------------------------

        /// <summary>Default action:start max_seconds when omitted.</summary>
        public const double DefaultMaxSeconds = 120.0;

        /// <summary>Hard ceiling on action:start's max_seconds argument.</summary>
        public const double MaxMaxSeconds = 300.0;

        /// <summary>
        /// Hard, non-negotiable safety cap on events per recording (not a
        /// user-facing argument): a pathological input storm (a stuck key
        /// bouncing, a scroll wheel spun continuously) must not grow the
        /// in-memory buffer -- or a replayed file -- without bound. Reaching
        /// it while recording auto-stops with stoppedBy:"max-events"; a
        /// hand-edited or corrupted file that exceeds it is refused by
        /// <see cref="TryParseFile"/> rather than replayed.
        /// </summary>
        public const int MaxEvents = 20000;

        /// <summary>How many past recording files are kept under the output folder; the rest are pruned right after action:stop writes a new one (same idea as UapRecordVideoPolicy.DefaultKeepCount, kept independent rather than shared -- see the design note's rejected-options list).</summary>
        public const int DefaultKeepCount = 20;

        // -- Replay speed -----------------------------------------------------

        public const double MinSpeed = 0.25;
        public const double MaxSpeed = 4.0;
        public const double DefaultSpeed = 1.0;

        // -- Output path ------------------------------------------------------

        public const string DefaultFolder = "Library/UnityAgentPanel/InputRecordings";

        // -- Action parsing -----------------------------------------------------

        public static UapInputRecordAction ParseRecordAction(string value)
        {
            if (string.IsNullOrEmpty(value)) { return UapInputRecordAction.Start; }
            string trimmed = value.Trim();
            if (string.Equals(trimmed, "start", StringComparison.OrdinalIgnoreCase)) { return UapInputRecordAction.Start; }
            if (string.Equals(trimmed, "stop", StringComparison.OrdinalIgnoreCase)) { return UapInputRecordAction.Stop; }
            if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase)) { return UapInputRecordAction.Status; }
            throw new ArgumentException("Unknown action '" + value + "'. Valid actions: start, stop, status.");
        }

        public static UapInputReplayAction ParseReplayAction(string value)
        {
            if (string.IsNullOrEmpty(value)) { return UapInputReplayAction.Start; }
            string trimmed = value.Trim();
            if (string.Equals(trimmed, "start", StringComparison.OrdinalIgnoreCase)) { return UapInputReplayAction.Start; }
            if (string.Equals(trimmed, "stop", StringComparison.OrdinalIgnoreCase)) { return UapInputReplayAction.Stop; }
            if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase)) { return UapInputReplayAction.Status; }
            throw new ArgumentException("Unknown action '" + value + "'. Valid actions: start, stop, status.");
        }

        // -- Argument validation ----------------------------------------------

        public static bool ValidateMaxSeconds(double seconds, out string error)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            {
                error = "'max_seconds' must be a positive number of seconds, up to "
                    + MaxMaxSeconds.ToString(CultureInfo.InvariantCulture) + " (got " + seconds + ").";
                return false;
            }
            if (seconds > MaxMaxSeconds)
            {
                error = "'max_seconds' must be at most " + MaxMaxSeconds.ToString(CultureInfo.InvariantCulture)
                    + " (got " + seconds + "). Stop and start a new recording for a longer capture.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool ValidateSpeed(double speed, out string error)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed))
            {
                error = "'speed' must be a number from " + MinSpeed.ToString(CultureInfo.InvariantCulture)
                    + " to " + MaxSpeed.ToString(CultureInfo.InvariantCulture) + " (got " + speed + ").";
                return false;
            }
            if (speed < MinSpeed || speed > MaxSpeed)
            {
                error = "'speed' must be from " + MinSpeed.ToString(CultureInfo.InvariantCulture) + " to "
                    + MaxSpeed.ToString(CultureInfo.InvariantCulture) + " (got " + speed + ").";
                return false;
            }
            error = null;
            return true;
        }

        // -- Start refusals -----------------------------------------------------

        /// <summary>Pure: uap_input_record action:start's whole refusal rule. Recording and replay are mutually exclusive (design note section 3.4: "refuse record during replay and vice versa") because both drive the SAME InputSystem devices, and a recording would otherwise capture its own replayed injection.</summary>
        public static bool DecideRecordStart(bool recordingActive, bool replayActive, out string error)
        {
            if (recordingActive)
            {
                error = "A recording is already in progress. Call uap_input_record action:stop to end it, or"
                    + " action:status to check on it, before starting another.";
                return false;
            }
            if (replayActive)
            {
                error = "uap_input_replay is currently injecting input; recording it would just capture the"
                    + " replay's own events. Call uap_input_replay action:stop first, or wait for it to finish.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>The mirror image of <see cref="DecideRecordStart"/> for uap_input_replay action:start.</summary>
        public static bool DecideReplayStart(bool replayActive, bool recordingActive, out string error)
        {
            if (replayActive)
            {
                error = "A replay is already in progress. Call uap_input_replay action:stop to end it, or"
                    + " action:status to check on it, before starting another.";
                return false;
            }
            if (recordingActive)
            {
                error = "uap_input_record is currently recording; replaying into it would just capture the"
                    + " replay's own events. Call uap_input_record action:stop first, or wait for it to finish.";
                return false;
            }
            error = null;
            return true;
        }

        // -- Output/input path resolution ---------------------------------------

        public static string BuildDefaultFileName(DateTime utcTimestamp)
        {
            return "uap_input_" + utcTimestamp.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".json";
        }

        public static string BuildDefaultRelativePath(DateTime utcTimestamp)
        {
            return DefaultFolder + "/" + BuildDefaultFileName(utcTimestamp);
        }

        /// <summary>
        /// Pure: resolves uap_input_record's optional 'path' argument (empty
        /// means "use the default name under Library/.../InputRecordings")
        /// to an absolute path, refusing anything that would land outside
        /// the project. See <see cref="ResolveInsideProject"/> for the
        /// traversal/absolute-path rules.
        /// </summary>
        public static bool TryResolveRecordPath(string projectRoot, string requestedPath, DateTime utcTimestampForDefault,
            out string absolutePath, out string error)
        {
            string root = NormalizeRoot(projectRoot);
            if (string.IsNullOrEmpty(requestedPath))
            {
                absolutePath = root + "/" + BuildDefaultRelativePath(utcTimestampForDefault);
                error = null;
                return true;
            }
            return ResolveInsideProject(root, requestedPath, out absolutePath, out error);
        }

        /// <summary>Pure: resolves uap_input_replay's REQUIRED 'path' argument (no default -- there is nothing sensible to replay without one).</summary>
        public static bool TryResolveReplayPath(string projectRoot, string requestedPath, out string absolutePath, out string error)
        {
            if (string.IsNullOrEmpty(requestedPath))
            {
                absolutePath = null;
                error = "'path' is required (the file a previous uap_input_record action:stop/status returned).";
                return false;
            }
            return ResolveInsideProject(NormalizeRoot(projectRoot), requestedPath, out absolutePath, out error);
        }

        private static string NormalizeRoot(string projectRoot)
        {
            return (projectRoot ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// Pure: a relative path is combined directly onto <paramref name="root"/>
        /// (already inside the project by construction); an absolute path
        /// must already start with <paramref name="root"/>. Either way a
        /// ".."/"." segment anywhere is refused outright rather than
        /// resolved -- this class deliberately never calls Path.GetFullPath
        /// (platform-specific absolute-path semantics -- drive letters, case
        /// sensitivity, symlinks -- that a Unity-free policy cannot safely
        /// reason about; see the design note's rejected-options list), so a
        /// traversal segment is refused instead of collapsed.
        /// </summary>
        private static bool ResolveInsideProject(string root, string requestedPath, out string absolutePath, out string error)
        {
            string normalized = requestedPath.Replace('\\', '/');
            if (ContainsDotSegment(normalized))
            {
                absolutePath = null;
                error = "'path' ('" + requestedPath + "') must not contain '.' or '..' segments.";
                return false;
            }
            if (IsAbsolute(normalized))
            {
                string candidate = normalized.TrimEnd('/');
                if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                    && !candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    absolutePath = null;
                    error = "'path' ('" + requestedPath + "') must be inside the project ('" + root + "').";
                    return false;
                }
                absolutePath = candidate;
                error = null;
                return true;
            }
            absolutePath = root + "/" + normalized.TrimStart('/');
            error = null;
            return true;
        }

        private static bool IsAbsolute(string normalizedForwardSlashPath)
        {
            if (string.IsNullOrEmpty(normalizedForwardSlashPath)) { return false; }
            if (normalizedForwardSlashPath[0] == '/') { return true; }
            return normalizedForwardSlashPath.Length >= 2 && normalizedForwardSlashPath[1] == ':';
        }

        private static bool ContainsDotSegment(string normalizedForwardSlashPath)
        {
            string[] parts = normalizedForwardSlashPath.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "." || parts[i] == "..")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: given every existing recording file's path in the output
        /// folder, sorted OLDEST FIRST (the embedded timestamp makes plain
        /// ordinal sort correct, same convention as
        /// UapRecordVideoPolicy.SelectFilesToPrune), returns the ones beyond
        /// the newest <paramref name="keep"/> for the caller to delete.
        /// <paramref name="justWritten"/> is never returned, so a file that
        /// just finished writing can never delete itself when keep is 0 or
        /// negative.
        /// </summary>
        public static List<string> SelectFilesToPrune(IList<string> existingSortedOldestFirst, int keep, string justWritten)
        {
            var toDelete = new List<string>();
            if (existingSortedOldestFirst == null) { return toDelete; }
            if (keep < 0) { keep = 0; }
            int overflow = existingSortedOldestFirst.Count - keep;
            for (int i = 0; i < overflow; i++)
            {
                string path = existingSortedOldestFirst[i];
                if (!string.Equals(path, justWritten, StringComparison.Ordinal))
                {
                    toDelete.Add(path);
                }
            }
            return toDelete;
        }

        // -- Event diffing (recorder side) ---------------------------------------

        /// <summary>Fixed order (Left, Right, Middle) matching UapGamePointerButton's member names -- shared by diffing and validation so both agree on the legal set without a second copy.</summary>
        public static readonly string[] MouseButtonNames = { "Left", "Right", "Middle" };

        /// <summary>
        /// Pure: appends KeyUp events for keys held in <paramref name="previous"/>
        /// but not <paramref name="current"/>, then KeyDown for the reverse,
        /// both sorted ordinally for a deterministic file across runs.
        /// Releases are emitted before presses so a key that is up in one
        /// sample and held by a DIFFERENT key next sample never appears to
        /// overlap in the diff (immaterial to replay, which applies events
        /// in file order regardless, but keeps the file readable).
        /// </summary>
        public static void DiffKeys(ISet<string> previous, ISet<string> current, double t, List<UapInputRecordEvent> outEvents)
        {
            AppendSorted(previous, current, t, UapInputRecordEventType.KeyUp, outEvents, key: true);
            AppendSorted(current, previous, t, UapInputRecordEventType.KeyDown, outEvents, key: true);
        }

        /// <summary>Pure: same idea as <see cref="DiffKeys"/> for the three mouse buttons, always scanned in <see cref="MouseButtonNames"/> order.</summary>
        public static void DiffButtons(ISet<string> previous, ISet<string> current, double t, List<UapInputRecordEvent> outEvents)
        {
            for (int i = 0; i < MouseButtonNames.Length; i++)
            {
                string name = MouseButtonNames[i];
                if (previous.Contains(name) && !current.Contains(name))
                {
                    outEvents.Add(new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseButtonUp, Button = name });
                }
            }
            for (int i = 0; i < MouseButtonNames.Length; i++)
            {
                string name = MouseButtonNames[i];
                if (!previous.Contains(name) && current.Contains(name))
                {
                    outEvents.Add(new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseButtonDown, Button = name });
                }
            }
        }

        private static void AppendSorted(ISet<string> from, ISet<string> to, double t, UapInputRecordEventType type,
            List<UapInputRecordEvent> outEvents, bool key)
        {
            var names = new List<string>();
            foreach (string name in from)
            {
                if (!to.Contains(name)) { names.Add(name); }
            }
            names.Sort(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                outEvents.Add(key
                    ? new UapInputRecordEvent { T = t, Type = type, Key = names[i] }
                    : new UapInputRecordEvent { T = t, Type = type, Button = names[i] });
            }
        }

        /// <summary>Pure: true (with the event) when the mouse position differs from the last recorded sample, or there is no previous sample yet -- the first sample always emits its position so replay knows where to start the cursor.</summary>
        public static bool TryDiffMouseMove(bool hasPrevious, double previousX, double previousY, double currentX, double currentY,
            double t, out UapInputRecordEvent evt)
        {
            if (hasPrevious && previousX == currentX && previousY == currentY)
            {
                evt = null;
                return false;
            }
            evt = new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseMove, X = currentX, Y = currentY };
            return true;
        }

        /// <summary>Pure: true (with the event) when this sample's scroll delta is non-zero. Scroll is inherently a per-sample delta (the InputSystem auto-resets it every update whether or not a new state is queued), never a held state, so unlike keys/buttons/position there is no "previous" to diff against.</summary>
        public static bool TryScrollEvent(double dx, double dy, double t, out UapInputRecordEvent evt)
        {
            if (dx == 0 && dy == 0)
            {
                evt = null;
                return false;
            }
            evt = new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseScroll, Dx = dx, Dy = dy };
            return true;
        }

        // -- Replay timeline scheduling -------------------------------------------

        /// <summary>
        /// Pure: applies every event due by <paramref name="elapsedScaledSeconds"/>
        /// (the recorded time already multiplied by the replay speed) onto
        /// <paramref name="state"/>, starting at <paramref name="fromIndex"/>
        /// in the (time-ordered) <paramref name="events"/> list, and returns
        /// the new cursor index. <paramref name="downTickById"/> tracks, per
        /// key ("key:NAME") / button ("btn:NAME"), the replay TICK (not
        /// time -- UapGameInputPolicy.MinHoldPolls is a tick count, matching
        /// uap_game_key/uap_game_mouse's own hold guarantee) its most recent
        /// Down/press was applied at: a KeyUp/MouseButtonUp whose recorded
        /// time has already arrived is DEFERRED (the loop stops before it,
        /// so a later call retries it) until at least
        /// <paramref name="minHoldPolls"/> ticks have passed since the
        /// matching press, so a tap recorded faster than that still leaves a
        /// press EDGE the game can observe (mirrors uap_game_key's own
        /// Holding phase, which enforces the identical minimum before
        /// queuing its own release). <paramref name="scrollDx"/>/
        /// <paramref name="scrollDy"/> accumulate this call's MouseScroll
        /// events (usually zero or one; several scroll events landing in the
        /// same tick sum together, since InputSystem's delta is a single
        /// number per update).
        /// </summary>
        public static int AdvanceReplay(IList<UapInputRecordEvent> events, int fromIndex, double elapsedScaledSeconds,
            int currentTick, int minHoldPolls, IDictionary<string, int> downTickById, UapInputReplayState state,
            out double scrollDx, out double scrollDy, out bool keysChanged, out bool mouseChanged)
        {
            scrollDx = 0.0;
            scrollDy = 0.0;
            keysChanged = false;
            mouseChanged = false;
            int index = fromIndex;
            while (index < events.Count)
            {
                UapInputRecordEvent evt = events[index];
                if (evt.T > elapsedScaledSeconds)
                {
                    break;
                }
                if (evt.Type == UapInputRecordEventType.KeyUp && IsHoldTooShort("key:" + evt.Key, currentTick, minHoldPolls, downTickById))
                {
                    break;
                }
                if (evt.Type == UapInputRecordEventType.MouseButtonUp && IsHoldTooShort("btn:" + evt.Button, currentTick, minHoldPolls, downTickById))
                {
                    break;
                }

                switch (evt.Type)
                {
                    case UapInputRecordEventType.KeyDown:
                        state.Keys.Add(evt.Key);
                        downTickById["key:" + evt.Key] = currentTick;
                        keysChanged = true;
                        break;
                    case UapInputRecordEventType.KeyUp:
                        state.Keys.Remove(evt.Key);
                        downTickById.Remove("key:" + evt.Key);
                        keysChanged = true;
                        break;
                    case UapInputRecordEventType.MouseMove:
                        state.MouseX = evt.X;
                        state.MouseY = evt.Y;
                        state.HasMousePosition = true;
                        mouseChanged = true;
                        break;
                    case UapInputRecordEventType.MouseButtonDown:
                        state.MouseButtons.Add(evt.Button);
                        downTickById["btn:" + evt.Button] = currentTick;
                        mouseChanged = true;
                        break;
                    case UapInputRecordEventType.MouseButtonUp:
                        state.MouseButtons.Remove(evt.Button);
                        downTickById.Remove("btn:" + evt.Button);
                        mouseChanged = true;
                        break;
                    case UapInputRecordEventType.MouseScroll:
                        scrollDx += evt.Dx;
                        scrollDy += evt.Dy;
                        mouseChanged = true;
                        break;
                }
                index++;
            }
            return index;
        }

        private static bool IsHoldTooShort(string id, int currentTick, int minHoldPolls, IDictionary<string, int> downTickById)
        {
            int downTick;
            return downTickById.TryGetValue(id, out downTick) && currentTick - downTick < minHoldPolls;
        }

        /// <summary>Pure: 0..1 progress through the recording for a status report (1.0 once every event has been applied, i.e. cursor has reached the end).</summary>
        public static double ReplayProgress(int cursorIndex, int eventCount)
        {
            if (eventCount <= 0) { return 1.0; }
            double progress = cursorIndex / (double)eventCount;
            return progress < 0 ? 0 : (progress > 1.0 ? 1.0 : progress);
        }

        // -- Wire names ---------------------------------------------------------

        public static string EventTypeName(UapInputRecordEventType type)
        {
            switch (type)
            {
                case UapInputRecordEventType.KeyDown: return "key_down";
                case UapInputRecordEventType.KeyUp: return "key_up";
                case UapInputRecordEventType.MouseMove: return "mouse_move";
                case UapInputRecordEventType.MouseButtonDown: return "mouse_button_down";
                case UapInputRecordEventType.MouseButtonUp: return "mouse_button_up";
                default: return "mouse_scroll";
            }
        }

        public static bool TryParseEventTypeName(string value, out UapInputRecordEventType type)
        {
            if (string.Equals(value, "key_down", StringComparison.Ordinal)) { type = UapInputRecordEventType.KeyDown; return true; }
            if (string.Equals(value, "key_up", StringComparison.Ordinal)) { type = UapInputRecordEventType.KeyUp; return true; }
            if (string.Equals(value, "mouse_move", StringComparison.Ordinal)) { type = UapInputRecordEventType.MouseMove; return true; }
            if (string.Equals(value, "mouse_button_down", StringComparison.Ordinal)) { type = UapInputRecordEventType.MouseButtonDown; return true; }
            if (string.Equals(value, "mouse_button_up", StringComparison.Ordinal)) { type = UapInputRecordEventType.MouseButtonUp; return true; }
            if (string.Equals(value, "mouse_scroll", StringComparison.Ordinal)) { type = UapInputRecordEventType.MouseScroll; return true; }
            type = UapInputRecordEventType.KeyDown;
            return false;
        }

        // -- JSON (de)serialization + validation ---------------------------------

        public static JsonNode EventToJson(UapInputRecordEvent evt)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("t", Math.Round(evt.T, 4))
                .Set("type", EventTypeName(evt.Type));
            switch (evt.Type)
            {
                case UapInputRecordEventType.KeyDown:
                case UapInputRecordEventType.KeyUp:
                    node.Set("key", evt.Key ?? string.Empty);
                    break;
                case UapInputRecordEventType.MouseMove:
                    node.Set("x", evt.X).Set("y", evt.Y);
                    break;
                case UapInputRecordEventType.MouseButtonDown:
                case UapInputRecordEventType.MouseButtonUp:
                    node.Set("button", evt.Button ?? string.Empty);
                    break;
                case UapInputRecordEventType.MouseScroll:
                    node.Set("dx", evt.Dx).Set("dy", evt.Dy);
                    break;
            }
            return node;
        }

        public static JsonNode ToJson(UapInputRecordingFile file)
        {
            JsonNode events = JsonNode.NewArray();
            for (int i = 0; i < file.Events.Count; i++)
            {
                events.Add(EventToJson(file.Events[i]));
            }
            return JsonNode.NewObject()
                .Set("formatVersion", file.FormatVersion)
                .Set("recordedAtUtcTicks", file.RecordedAtUtcTicks)
                .Set("durationSeconds", file.DurationSeconds)
                .Set("eventCount", file.Events.Count)
                .Set("events", events);
        }

        /// <summary>
        /// Parses AND validates a recording file's JSON in one pass (there
        /// is no separate lossy "FromJson" the way UapRecordVideoPolicy has
        /// for a small SessionState summary -- a recording file is either a
        /// well-formed thing this tool wrote, or it is not safe to replay at
        /// all). Checks: known format version; events sorted
        /// non-decreasing by time; each event's type known and its
        /// type-specific fields present, well-typed and (for key/button) a
        /// name this package recognizes; total event count within
        /// <see cref="MaxEvents"/>. Never throws.
        /// </summary>
        public static bool TryParseFile(JsonNode node, out UapInputRecordingFile file, out string error)
        {
            file = null;
            if (node == null || !node.IsObject)
            {
                error = "The recording file is not a JSON object.";
                return false;
            }
            int formatVersion = node["formatVersion"].AsInt(-1);
            if (formatVersion != CurrentFormatVersion)
            {
                error = "Unsupported recording format version " + formatVersion + " (this build reads version "
                    + CurrentFormatVersion + " only).";
                return false;
            }
            JsonNode eventsNode = node["events"];
            if (!eventsNode.IsArray)
            {
                error = "The recording file has no 'events' array.";
                return false;
            }
            if (eventsNode.Count > MaxEvents)
            {
                error = "The recording file has " + eventsNode.Count + " events, more than the "
                    + MaxEvents + " this build will replay.";
                return false;
            }

            var parsed = new UapInputRecordingFile
            {
                FormatVersion = formatVersion,
                RecordedAtUtcTicks = node["recordedAtUtcTicks"].AsLong(0L),
                DurationSeconds = node["durationSeconds"].AsDouble(0.0),
            };
            double lastT = double.NegativeInfinity;
            int i = 0;
            foreach (JsonNode eventNode in eventsNode.Items)
            {
                UapInputRecordEvent evt;
                if (!TryParseEvent(eventNode, i, out evt, out error))
                {
                    file = null;
                    return false;
                }
                if (evt.T < lastT)
                {
                    error = "Event " + i + " has t=" + evt.T + ", earlier than the previous event's t=" + lastT
                        + " -- events must be in non-decreasing time order.";
                    file = null;
                    return false;
                }
                lastT = evt.T;
                parsed.Events.Add(evt);
                i++;
            }
            if (double.IsNaN(parsed.DurationSeconds) || double.IsInfinity(parsed.DurationSeconds) || parsed.DurationSeconds < 0)
            {
                error = "'durationSeconds' must be a non-negative finite number (got " + parsed.DurationSeconds + ").";
                file = null;
                return false;
            }

            file = parsed;
            error = null;
            return true;
        }

        private static bool TryParseEvent(JsonNode node, int index, out UapInputRecordEvent evt, out string error)
        {
            evt = null;
            if (node == null || !node.IsObject)
            {
                error = "Event " + index + " is not a JSON object.";
                return false;
            }
            if (!node.HasKey("t"))
            {
                error = "Event " + index + " is missing 't' (seconds since the recording started).";
                return false;
            }
            double t = node["t"].AsDouble(double.NaN);
            if (double.IsNaN(t) || double.IsInfinity(t) || t < 0)
            {
                error = "Event " + index + " has an invalid 't' (must be a non-negative finite number).";
                return false;
            }
            string typeText = node["type"].AsString(null);
            UapInputRecordEventType type;
            if (typeText == null || !TryParseEventTypeName(typeText, out type))
            {
                error = "Event " + index + " has an unknown 'type' ('" + typeText + "'). Valid types: key_down,"
                    + " key_up, mouse_move, mouse_button_down, mouse_button_up, mouse_scroll.";
                return false;
            }
            evt = new UapInputRecordEvent { T = t, Type = type };
            switch (type)
            {
                case UapInputRecordEventType.KeyDown:
                case UapInputRecordEventType.KeyUp:
                    string key = node["key"].AsString(null);
                    if (string.IsNullOrEmpty(key))
                    {
                        error = "Event " + index + " (" + typeText + ") is missing 'key'.";
                        return false;
                    }
                    if (!IsKnownKeyName(key))
                    {
                        error = "Event " + index + " names an unknown key '" + key + "'.";
                        return false;
                    }
                    evt.Key = key;
                    break;
                case UapInputRecordEventType.MouseMove:
                    if (!node.HasKey("x") || !node.HasKey("y"))
                    {
                        error = "Event " + index + " (mouse_move) is missing 'x'/'y'.";
                        return false;
                    }
                    evt.X = node["x"].AsDouble(0.0);
                    evt.Y = node["y"].AsDouble(0.0);
                    break;
                case UapInputRecordEventType.MouseButtonDown:
                case UapInputRecordEventType.MouseButtonUp:
                    string button = node["button"].AsString(null);
                    if (button == null || Array.IndexOf(MouseButtonNames, button) < 0)
                    {
                        error = "Event " + index + " (" + typeText + ") has an unknown 'button' ('" + button
                            + "'). Valid buttons: " + string.Join(", ", MouseButtonNames) + ".";
                        return false;
                    }
                    evt.Button = button;
                    break;
                case UapInputRecordEventType.MouseScroll:
                    if (!node.HasKey("dx") || !node.HasKey("dy"))
                    {
                        error = "Event " + index + " (mouse_scroll) is missing 'dx'/'dy'.";
                        return false;
                    }
                    evt.Dx = node["dx"].AsDouble(0.0);
                    evt.Dy = node["dy"].AsDouble(0.0);
                    break;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Key enum members that exist in newer Input System releases but
        /// are not in <see cref="UapGameKeyPolicy.KnownKeys"/> (that list is
        /// scoped to what uap_game_key lets an agent TYPE, not to every Key
        /// this build's package can report). uap_input_record samples the
        /// device through UapInputSystemKeyboardAccess.PressedKeyNames,
        /// which walks the REAL runtime Key enum, so a key outside
        /// KnownKeys can legitimately end up in a recording (verified: the
        /// Key enum gained F13-F24 and three media keys between
        /// com.unity.inputsystem 1.7.0 and 1.19.0, InputSystem/Devices/
        /// Keyboard.cs -- absent from KnownKeys either way, since that list
        /// predates this file and was written against 1.7.0's set). Without
        /// this list, a perfectly valid recording made on 1.19+ would be
        /// refused as "unknown key" the moment uap_input_replay tried to
        /// load it. Kept local to this Unity-free class rather than adding
        /// entries to UapGameKeyPolicy.KnownKeys itself, which also governs
        /// uap_game_key's own agent-facing key name validation and is not
        /// owned by this change.
        /// </summary>
        private static readonly string[] ExtraKnownKeys =
        {
            "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
            "MediaPlayPause", "MediaRewind", "MediaForward",
        };

        /// <summary>Cross-checks a recorded key name against UapGameKeyPolicy's own known-Key-enum list plus <see cref="ExtraKnownKeys"/>, so a corrupted/hand-edited file is refused with a clear message at load time rather than failing deep inside a replay tick.</summary>
        private static bool IsKnownKeyName(string key)
        {
            string[] known = UapGameKeyPolicy.KnownKeys;
            for (int i = 0; i < known.Length; i++)
            {
                if (string.Equals(known[i], key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            for (int i = 0; i < ExtraKnownKeys.Length; i++)
            {
                if (string.Equals(ExtraKnownKeys[i], key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // -- Human-readable stop reasons ------------------------------------------

        public static string DescribeRecordStopReason(string stoppedBy)
        {
            if (string.Equals(stoppedBy, UapInputStopReason.Stop, StringComparison.Ordinal)) { return "Stopped by request (action:stop)."; }
            if (string.Equals(stoppedBy, UapInputStopReason.Duration, StringComparison.Ordinal)) { return "Stopped automatically: reached its max_seconds cap."; }
            if (string.Equals(stoppedBy, UapInputStopReason.MaxEvents, StringComparison.Ordinal)) { return "Stopped automatically: reached the " + MaxEvents + "-event safety cap."; }
            if (string.Equals(stoppedBy, UapInputStopReason.PlayModeExit, StringComparison.Ordinal)) { return "Stopped automatically: Play Mode ended."; }
            if (string.Equals(stoppedBy, UapInputStopReason.AssemblyReload, StringComparison.Ordinal)) { return "Stopped by a domain reload (a script compile, or entering/leaving Play Mode with domain reload enabled); the recording was finalized up to the last captured sample."; }
            if (string.Equals(stoppedBy, UapInputStopReason.Error, StringComparison.Ordinal)) { return "Stopped because of an error."; }
            return "Not stopped.";
        }

        public static string DescribeReplayStopReason(string stoppedBy)
        {
            if (string.Equals(stoppedBy, UapInputStopReason.Stop, StringComparison.Ordinal)) { return "Stopped by request (action:stop)."; }
            if (string.Equals(stoppedBy, UapInputStopReason.Completed, StringComparison.Ordinal)) { return "Finished: every event in the file was applied."; }
            if (string.Equals(stoppedBy, UapInputStopReason.PlayModeExit, StringComparison.Ordinal)) { return "Stopped automatically: Play Mode ended."; }
            if (string.Equals(stoppedBy, UapInputStopReason.AssemblyReload, StringComparison.Ordinal)) { return "Stopped by a domain reload (a script compile, or entering/leaving Play Mode with domain reload enabled)."; }
            if (string.Equals(stoppedBy, UapInputStopReason.Error, StringComparison.Ordinal)) { return "Stopped because of an error."; }
            return "Not stopped.";
        }
    }
}
