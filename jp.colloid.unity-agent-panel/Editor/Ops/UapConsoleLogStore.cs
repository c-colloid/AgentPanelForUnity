using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>One captured Console line, already truncated to the store's per-entry caps.</summary>
    public struct UapConsoleLogEntry
    {
        /// <summary>Monotonic within one Editor domain; the agent pages with since_id.</summary>
        public int Id;

        /// <summary>"error" / "exception" / "assert" / "warning" / "log" (<see cref="UapConsoleLogBuffer.TypeName"/> maps Unity's enum).</summary>
        public string Type;

        public string Message;

        /// <summary>Empty unless the type is one <see cref="UapConsoleLogStore.CarriesStackTrace"/> keeps it for.</summary>
        public string StackTrace;

        /// <summary>Collapsed runs carry how many identical lines they stand for; 1 otherwise.</summary>
        public int Occurrences;
    }

    /// <summary>What a uap_console_logs call asks for. Plain data, no Unity types, so the selection rule is testable.</summary>
    public sealed class UapConsoleLogSelection
    {
        /// <summary>Type names to keep. Null/empty = every type.</summary>
        public List<string> Types = new List<string>();

        /// <summary>Case-insensitive substring the message must contain. Null/empty = no filter.</summary>
        public string Contains;

        /// <summary>Only entries with a higher Id. 0 = from the start of the buffer.</summary>
        public int SinceId;

        /// <summary>How many entries to return (the NEWEST ones).</summary>
        public int Count = UapConsoleLogStore.DefaultSelectionCount;

        /// <summary>Fold identical (type, message) pairs into one entry carrying Occurrences.</summary>
        public bool Collapse = true;
    }

    /// <summary>
    /// The bounded Console-line buffer behind uap_console_logs, and the pure
    /// rules that read it (design note docs/design-notes/2026-09-21-play-
    /// mode-and-console-log-tools.md section 3).
    ///
    /// Unity-free on purpose: <see cref="UapConsoleLogBuffer"/> is the thin
    /// shell that subscribes to Application.logMessageReceivedThreaded and
    /// maps Unity's LogType onto <see cref="Capture"/>, so everything worth
    /// getting wrong -- the ring, the truncation, the filter/collapse/page
    /// order -- compiles and runs in the license-free smoke gate
    /// (ci/SmokeTests) as well as under the EditMode suite.
    ///
    /// Bounded by construction: <see cref="Capacity"/> entries, each
    /// truncated, so a log flood -- this project has measured 1106 copies of
    /// one exception in a single session -- costs a fixed amount of memory
    /// rather than a growing one. Lifetime is one Editor domain; a domain
    /// reload drops everything, and the tool says so rather than implying
    /// the history is complete.
    /// </summary>
    public static class UapConsoleLogStore
    {
        /// <summary>Entries kept; the oldest is dropped past this.</summary>
        public const int Capacity = 200;

        /// <summary>Per-entry message cap. Longer messages keep the head and are marked truncated.</summary>
        public const int MaxMessageChars = 500;

        /// <summary>Per-entry stack-trace cap (errors/exceptions/asserts only).</summary>
        public const int MaxStackTraceChars = 1000;

        /// <summary>Entries returned when a caller does not say how many it wants.</summary>
        public const int DefaultSelectionCount = 30;

        /// <summary>Appended to a value that hit its cap, so a reader never mistakes a cut for the end of the text.</summary>
        public const string TruncationMarker = " [...truncated]";

        private static readonly object _lock = new object();
        private static readonly List<UapConsoleLogEntry> _entries = new List<UapConsoleLogEntry>();
        private static int _nextId = 1;
        private static int _dropped;
        private static bool _capturing;

        /// <summary>True while <see cref="Capture"/> records anything (set by UapConsoleLogBuffer.Install/Uninstall).</summary>
        public static bool Capturing
        {
            get
            {
                lock (_lock)
                {
                    return _capturing;
                }
            }
        }

        /// <summary>How many entries fell off the front of the buffer since the last <see cref="Clear"/>.</summary>
        public static int Dropped
        {
            get
            {
                lock (_lock)
                {
                    return _dropped;
                }
            }
        }

        /// <summary>Opens or closes capture. Returns true when the state actually changed, so the caller can hook/unhook exactly once.</summary>
        public static bool SetCapturing(bool capturing)
        {
            lock (_lock)
            {
                if (_capturing == capturing)
                {
                    return false;
                }
                _capturing = capturing;
                return true;
            }
        }

        /// <summary>Forgets every captured entry (and the dropped counter); ids keep increasing.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _dropped = 0;
            }
        }

        /// <summary>
        /// Records one line. Safe on any thread (Unity delivers
        /// logMessageReceivedThreaded off the main thread) and deliberately
        /// does nothing but append: the 2026-08-02 error-flood post-mortem
        /// pins "a log callback must never touch UI synchronously" as the
        /// rule whose breach amplified one exception into 1106.
        /// Ignored while capture is off.
        /// </summary>
        public static void Capture(string typeName, string message, string stackTrace)
        {
            string type = string.IsNullOrEmpty(typeName) ? "log" : typeName;
            var entry = new UapConsoleLogEntry
            {
                Type = type,
                Message = Truncate(message, MaxMessageChars),
                StackTrace = CarriesStackTrace(type) ? Truncate(stackTrace, MaxStackTraceChars) : string.Empty,
                Occurrences = 1
            };
            lock (_lock)
            {
                if (!_capturing)
                {
                    return;
                }
                entry.Id = _nextId++;
                _entries.Add(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.RemoveAt(0);
                    _dropped++;
                }
            }
        }

        /// <summary>A copy of the live buffer, oldest first.</summary>
        public static List<UapConsoleLogEntry> Snapshot()
        {
            lock (_lock)
            {
                return new List<UapConsoleLogEntry>(_entries);
            }
        }

        /// <summary>The id of the newest entry in <paramref name="entries"/>, or 0 when there is none.</summary>
        public static int LastId(List<UapConsoleLogEntry> entries)
        {
            return entries == null || entries.Count == 0 ? 0 : entries[entries.Count - 1].Id;
        }

        /// <summary>Per-type totals over the whole list handed in (not over a filtered selection).</summary>
        public static Dictionary<string, int> CountByType(List<UapConsoleLogEntry> entries)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (entries == null)
            {
                return counts;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                string type = entries[i].Type ?? string.Empty;
                int existing;
                counts.TryGetValue(type, out existing);
                counts[type] = existing + 1;
            }
            return counts;
        }

        /// <summary>
        /// Pure: applies <paramref name="selection"/> to
        /// <paramref name="entries"/> and returns what the tool should
        /// report, oldest first.
        ///
        /// The order of the three steps is the contract, pinned by
        /// UapConsoleLogBufferTests and ci/SmokeTests: filter (since_id /
        /// type / contains)
        /// BEFORE collapsing, and collapse BEFORE taking the newest
        /// <see cref="UapConsoleLogSelection.Count"/>. Any other order lets
        /// one repeated error push every other line out of an answer that
        /// asked for it to be collapsed -- which is exactly the situation
        /// the agent reads logs in. A collapsed entry carries the NEWEST id
        /// of its run, so paging with since_id can never re-deliver it.
        /// </summary>
        public static List<UapConsoleLogEntry> Select(List<UapConsoleLogEntry> entries,
            UapConsoleLogSelection selection)
        {
            var result = new List<UapConsoleLogEntry>();
            if (entries == null || entries.Count == 0)
            {
                return result;
            }
            if (selection == null)
            {
                selection = new UapConsoleLogSelection();
            }

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selection.Types != null)
            {
                for (int i = 0; i < selection.Types.Count; i++)
                {
                    if (!string.IsNullOrEmpty(selection.Types[i]))
                    {
                        wanted.Add(selection.Types[i]);
                    }
                }
            }
            string contains = string.IsNullOrEmpty(selection.Contains) ? null : selection.Contains;

            var filtered = new List<UapConsoleLogEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                UapConsoleLogEntry entry = entries[i];
                if (entry.Id <= selection.SinceId)
                {
                    continue;
                }
                if (wanted.Count > 0 && !wanted.Contains(entry.Type ?? string.Empty))
                {
                    continue;
                }
                if (contains != null
                    && (entry.Message == null
                        || entry.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                filtered.Add(entry);
            }

            if (selection.Collapse)
            {
                filtered = CollapseIdentical(filtered);
            }

            int count = selection.Count < 1 ? 1 : selection.Count;
            int first = filtered.Count > count ? filtered.Count - count : 0;
            for (int i = first; i < filtered.Count; i++)
            {
                result.Add(filtered[i]);
            }
            return result;
        }

        /// <summary>
        /// Pure: folds identical (type, message) pairs into the position of
        /// their NEWEST occurrence, summing Occurrences. Input must be in
        /// ascending id order (the buffer always is).
        /// </summary>
        public static List<UapConsoleLogEntry> CollapseIdentical(List<UapConsoleLogEntry> entries)
        {
            var result = new List<UapConsoleLogEntry>();
            if (entries == null || entries.Count == 0)
            {
                return result;
            }
            var occurrences = new Dictionary<EntryKey, int>();
            var newestId = new Dictionary<EntryKey, int>();
            for (int i = 0; i < entries.Count; i++)
            {
                EntryKey key = new EntryKey(entries[i]);
                int seen;
                occurrences.TryGetValue(key, out seen);
                occurrences[key] = seen + (entries[i].Occurrences < 1 ? 1 : entries[i].Occurrences);
                newestId[key] = entries[i].Id;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                EntryKey key = new EntryKey(entries[i]);
                if (newestId[key] != entries[i].Id)
                {
                    continue;
                }
                // The newest occurrence also keeps the stack trace: it
                // describes the state the Editor is in now, and an agent
                // paging with since_id must never be handed an id it has
                // already been given.
                UapConsoleLogEntry entry = entries[i];
                entry.Occurrences = occurrences[key];
                result.Add(entry);
            }
            return result;
        }

        /// <summary>True for the types whose stack trace is worth the tokens.</summary>
        public static bool CarriesStackTrace(string typeName)
        {
            return string.Equals(typeName, "error", StringComparison.Ordinal)
                || string.Equals(typeName, "exception", StringComparison.Ordinal)
                || string.Equals(typeName, "assert", StringComparison.Ordinal);
        }

        /// <summary>Keeps the head of an over-long string and says that it did.</summary>
        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            if (text.Length <= max)
            {
                return text;
            }
            return text.Substring(0, max) + TruncationMarker;
        }

        /// <summary>
        /// (type, message) identity for collapsing, as a VALUE rather than a
        /// joined string. Any separator character could itself appear inside
        /// a log message, and the one that could not -- a NUL escape -- is
        /// refused by this repo's glyph audit over Editor sources
        /// (GlyphAuditTests.SourceScan_AllConstructedCodepoints_AreWhitelisted,
        /// which caught exactly that on this file's first real-Editor run).
        /// A struct key has neither problem.
        /// </summary>
        private struct EntryKey : IEquatable<EntryKey>
        {
            private readonly string _type;
            private readonly string _message;

            public EntryKey(UapConsoleLogEntry entry)
            {
                _type = entry.Type ?? string.Empty;
                _message = entry.Message ?? string.Empty;
            }

            public bool Equals(EntryKey other)
            {
                return string.Equals(_type, other._type, StringComparison.Ordinal)
                    && string.Equals(_message, other._message, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is EntryKey && Equals((EntryKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_type.GetHashCode() * 397) ^ _message.GetHashCode();
                }
            }
        }
    }
}
