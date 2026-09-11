using System;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Accumulates Console errors for the "Ask Claude to fix" chip
    /// (R05 section 4.2). Unity 2022.3 has no public read API for the
    /// Console, so this hooks Application.logMessageReceivedThreaded
    /// (runtime errors, exceptions, asserts -- from EVERY thread; HUB-7
    /// switched off the main-thread-only variant, which silently dropped
    /// background-thread errors) plus the CompilationPipeline events
    /// (compiler errors, which do not flow through the log callback).
    ///
    /// Documented limitation: only errors raised AFTER this domain load
    /// are seen -- errors that were already sitting in the Console when
    /// the editor started (or that arrived before InitializeOnLoad ran)
    /// are invisible, and clearing the Console window does not clear this
    /// list (use Clear()). Compiler errors from the previous compile run
    /// are dropped when a new compilation starts.
    ///
    /// Identical messages are deduplicated (an exception spamming every
    /// frame counts once); Changed is raised only when the visible set
    /// changes, never per repeated occurrence.
    ///
    /// Threading/re-entrancy (2026-08-02 design note section 1 -- the
    /// measured amplifier behind the 1106x MissingReferenceException
    /// flood): Application.logMessageReceived can fire off the main
    /// thread, and OnLogMessage used to call RaiseChanged() synchronously,
    /// which ran ContextBarView.UpdateErrorChip and assigned Label.text --
    /// if that happened during a repaint it threw, which was itself
    /// logged, which re-entered OnLogMessage. OnLogMessage now does the
    /// bare minimum inside the callback (filter + string formatting, no
    /// Unity UI/API calls) and enqueues under a lock; the queue is drained
    /// and _entries/Changed are updated only on the next
    /// EditorApplication.update tick (the pump idiom used elsewhere in
    /// this codebase -- see EditorUpdatePump/StreamingLabelPump), with a
    /// re-entrancy guard so a log raised while that tick is applying can
    /// never recurse into a second concurrent apply.
    /// </summary>
    public static class ConsoleErrorProvider
    {
        /// <summary>One log line captured off the callback, not yet applied.</summary>
        private struct QueuedLogEntry
        {
            public string Message;
            public string Location;
        }

        private static readonly object _queueLock = new object();
        private static readonly List<QueuedLogEntry> _queuedLogEntries = new List<QueuedLogEntry>();
        private static bool _applyingQueuedLogEntries;

        /// <summary>One captured error (deduplicated by Message).</summary>
        public struct Entry
        {
            /// <summary>The error/exception message (first log line).</summary>
            public string Message;
            /// <summary>Best location hint: file:line or first stack line.</summary>
            public string Location;
            /// <summary>True when it came from the compiler pipeline.</summary>
            public bool FromCompiler;
            /// <summary>How many times the same message was seen.</summary>
            public int Occurrences;
        }

        /// <summary>Digest size used by the prepared fix prompt.</summary>
        public const int DefaultDigestEntries = 10;

        private const int MaxEntries = 100;
        private const int MaxMessageChars = 500;

        private static readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Raised when the set of captured errors changes.</summary>
        public static event Action Changed;

        /// <summary>Number of distinct captured errors.</summary>
        public static int Count
        {
            get { return _entries.Count; }
        }

        [InitializeOnLoadMethod]
        private static void Install()
        {
            // Statics reset on domain reload, so this normally subscribes
            // exactly once per load; the -=/+= pair keeps it idempotent if
            // Install is ever invoked again within the same domain.
            // HUB-7: the THREADED variant. Application.logMessageReceived
            // only delivers main-thread logs, so an Error/Exception raised
            // on a background thread (a Task, an async importer, a
            // third-party SDK's worker) never reached the error chip or the
            // compile digest at all. logMessageReceivedThreaded delivers
            // every thread's logs through this one subscription -- do NOT
            // also subscribe the non-threaded event, that would double-count
            // main-thread entries. OnLogMessage was already written for
            // off-thread callers (lock + enqueue) with the apply deferred to
            // PumpQueuedLogEntries on the main thread, so the queue side
            // needs no change.
            Application.logMessageReceivedThreaded -= OnLogMessage;
            Application.logMessageReceivedThreaded += OnLogMessage;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.update -= PumpQueuedLogEntries;
            EditorApplication.update += PumpQueuedLogEntries;
        }

        /// <summary>
        /// Changed hook for the ignore store (Settings management list /
        /// patterns field): the captured entries themselves did not change,
        /// but what is VISIBLE did, and every consumer (ContextBarView
        /// chip, EmptyStateView suggestion) refreshes off Changed only --
        /// un-ignoring a message must move the chip NOW, not at the next
        /// real error event.
        /// </summary>
        public static void NotifyIgnoreStoreChanged()
        {
            RaiseChanged();
        }

        /// <summary>
        /// The permanent-ignore persistence step (2026-08-13 design note
        /// decision 1, reachable since UXO-3 from the error chip's menu
        /// rather than its bare X), owned by the provider so EditMode tests
        /// can drive the REAL production path through the settings seam:
        /// every currently VISIBLE message goes into the resolved settings'
        /// ignoredConsoleErrors (bounded FIFO), the store is saved, and
        /// Changed is raised so every consumer re-reads visibility. SaveNow
        /// targets the real ScriptableSingleton; under an injected test
        /// settings source the singleton's own snapshot is untouched and
        /// SaveNow's changed-only check makes it a no-op. Returns the
        /// messages that were just persisted (empty when nothing was done)
        /// so the caller can offer an immediate undo via
        /// <see cref="Unignore"/>.
        /// </summary>
        public static List<string> IgnoreCurrentlyVisible()
        {
            var messages = new List<string>();
            Entry[] visible = VisibleSnapshot();
            if (visible.Length == 0)
            {
                return messages;
            }
            PanelSettings settings = ResolveSettings();
            if (settings == null)
            {
                return messages;
            }
            for (int i = 0; i < visible.Length; i++)
            {
                messages.Add(visible[i].Message);
            }
            ConsoleErrorIgnoreFilter.AddIgnores(settings.ignoredConsoleErrors, messages,
                ConsoleErrorIgnoreFilter.IgnoredConsoleErrorsMax);
            PanelStateStore.instance.SaveNow();
            RaiseChanged();
            return messages;
        }

        /// <summary>
        /// The undo path for <see cref="IgnoreCurrentlyVisible"/> (UXO-3):
        /// removes exactly the given messages from the settings'
        /// ignoredConsoleErrors (Ordinal match, the same basis the filter
        /// uses), saves, and raises Changed so the entries -- whose raw
        /// captures were never touched -- become visible again at once.
        /// Same store the Settings management list edits, so this is a
        /// subset of the already-supported un-ignore behavior, not a new
        /// mechanism.
        /// </summary>
        public static void Unignore(IEnumerable<string> messages)
        {
            if (messages == null)
            {
                return;
            }
            PanelSettings settings = ResolveSettings();
            if (settings == null || settings.ignoredConsoleErrors == null)
            {
                return;
            }
            bool removed = false;
            foreach (string message in messages)
            {
                if (!string.IsNullOrEmpty(message)
                    && settings.ignoredConsoleErrors.Remove(message))
                {
                    removed = true;
                }
            }
            if (!removed)
            {
                return;
            }
            PanelStateStore.instance.SaveNow();
            RaiseChanged();
        }

        /// <summary>Forgets every captured error (chip dismiss / reset).</summary>
        public static void Clear()
        {
            if (_entries.Count == 0)
            {
                return;
            }
            _entries.Clear();
            RaiseChanged();
        }

        /// <summary>Copy of the captured entries (oldest first).</summary>
        public static Entry[] Snapshot()
        {
            return _entries.ToArray();
        }

        /// <summary>
        /// Number of captured errors NOT hidden by the ignore store/
        /// patterns (docs/design-notes/2026-08-13-error-chip-ignore.md) --
        /// what ContextBarView's chip count, EmptyStateView's fix
        /// suggestion, and AgentHub's compile-succeeded check all read.
        /// Always &lt;= Count; Count stays the RAW total for tests and for
        /// any consumer that genuinely needs "how much has been captured"
        /// (none of the do-something-about-errors paths do: an error the
        /// user marked do-not-fix must not drive the chip, the empty-state
        /// suggestion, or the post-compile auto-continue verdict).
        /// </summary>
        public static int VisibleCount
        {
            get { return VisibleSnapshot().Length; }
        }

        /// <summary>
        /// Copy of the captured entries (oldest first), with every ignored
        /// one (exact match in PanelSettings.ignoredConsoleErrors, or
        /// substring match against ignoredConsoleErrorPatterns --
        /// ConsoleErrorIgnoreFilter.IsIgnored) filtered out. _entries
        /// itself is NEVER mutated here -- removing a message from the
        /// ignore store must immediately restore it, and recomputing fresh
        /// on every call (rather than caching) is what makes that hold with
        /// no invalidation to get wrong. MAIN THREAD ONLY: reads
        /// PanelStateStore.instance (a ScriptableSingleton) via
        /// ResolveSettings, the same constraint every other
        /// PanelStateStore.instance access in this codebase already has.
        /// </summary>
        public static Entry[] VisibleSnapshot()
        {
            PanelSettings settings = ResolveSettings();
            List<string> exactIgnores = settings != null ? settings.ignoredConsoleErrors : null;
            List<string> patterns = settings != null
                ? ConsoleErrorIgnoreFilter.ParsePatterns(settings.ignoredConsoleErrorPatterns)
                : null;
            var result = new List<Entry>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!ConsoleErrorIgnoreFilter.IsIgnored(_entries[i].Message, exactIgnores, patterns))
                {
                    result.Add(_entries[i]);
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// Digest of the currently VISIBLE (non-ignored) errors -- the fix
        /// button's payload (design note "Provider-side filtering": "the
        /// fix prompt never contains ignored noise"). Redirected from the
        /// pre-2026-08-13 all-entries behavior onto <see cref="VisibleSnapshot"/>;
        /// the pure <see cref="FormatDigest(IList{Entry}, int)"/> overload
        /// below is untouched and still formats whatever list it is handed,
        /// which is what every existing EditMode test against it exercises.
        /// </summary>
        public static string FormatDigest(int maxEntries = DefaultDigestEntries)
        {
            return FormatDigest(VisibleSnapshot(), maxEntries);
        }

        /// <summary>
        /// Pure digest formatting (EditMode-testable): numbered entries,
        /// location and occurrence annotations, and an overflow line when
        /// there are more errors than maxEntries. Null when empty.
        /// </summary>
        public static string FormatDigest(IList<Entry> entries, int maxEntries)
        {
            if (entries == null || entries.Count == 0 || maxEntries <= 0)
            {
                return null;
            }
            var sb = new StringBuilder(256);
            sb.Append(L10n.F(L10n.S.CtxErrorsDigestHeaderFmt, entries.Count));
            int listed = Math.Min(entries.Count, maxEntries);
            for (int i = 0; i < listed; i++)
            {
                Entry entry = entries[i];
                sb.Append("\n").Append(L10n.F(L10n.S.CtxErrorsDigestEntryFmt,
                    i + 1, entry.Message ?? string.Empty));
                if (entry.Occurrences > 1)
                {
                    sb.Append(L10n.F(L10n.S.CtxErrorsDigestOccurrencesFmt, entry.Occurrences));
                }
                if (!string.IsNullOrEmpty(entry.Location))
                {
                    sb.Append("\n    ").Append(L10n.F(L10n.S.CtxErrorsDigestLocationFmt, entry.Location));
                }
            }
            if (entries.Count > listed)
            {
                sb.Append("\n").Append(L10n.F(L10n.S.CtxErrorsDigestMoreFmt, entries.Count - listed));
            }
            return sb.ToString();
        }

        // -- Hooks ------------------------------------------------------------

        /// <summary>
        /// Application.logMessageReceived callback. May run OFF the main
        /// thread (undocumented but observed for Unity's log pipeline), so
        /// this does the absolute minimum: filter by type and format the
        /// two strings (pure, no Unity API beyond what was already handed
        /// in), then enqueue under a lock. No Unity UI/editor API is
        /// touched here and _entries/Changed are never touched here --
        /// see PumpQueuedLogEntries.
        /// </summary>
        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception
                && type != LogType.Assert)
            {
                return;
            }
            string message = FirstLine(condition, MaxMessageChars);
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            string location = FirstLine(stackTrace, MaxMessageChars);
            lock (_queueLock)
            {
                _queuedLogEntries.Add(new QueuedLogEntry
                {
                    Message = message,
                    Location = location
                });
            }
        }

        /// <summary>
        /// Drains whatever OnLogMessage queued since the last tick and
        /// applies it to _entries on the main thread, raising Changed at
        /// most once for the whole batch. The _applyingQueuedLogEntries
        /// guard means a log raised as a SIDE EFFECT of this apply (e.g. a
        /// Changed subscriber itself logging, or a nested editor-update
        /// pump) can never recurse into a second concurrent apply pass --
        /// it is simply left queued for the next tick instead.
        /// </summary>
        private static void PumpQueuedLogEntries()
        {
            if (_applyingQueuedLogEntries)
            {
                return;
            }
            List<QueuedLogEntry> drained;
            lock (_queueLock)
            {
                if (_queuedLogEntries.Count == 0)
                {
                    return;
                }
                drained = new List<QueuedLogEntry>(_queuedLogEntries);
                _queuedLogEntries.Clear();
            }

            _applyingQueuedLogEntries = true;
            try
            {
                bool changed = false;
                for (int i = 0; i < drained.Count; i++)
                {
                    changed |= Add(drained[i].Message, drained[i].Location, false);
                }
                if (changed)
                {
                    RaiseChanged();
                }
            }
            finally
            {
                _applyingQueuedLogEntries = false;
            }
        }

        private static void OnCompilationStarted(object context)
        {
            // The compile that is starting supersedes the previous one's
            // errors; runtime errors are kept.
            int removed = _entries.RemoveAll(delegate (Entry entry)
            {
                return entry.FromCompiler;
            });
            if (removed > 0)
            {
                RaiseChanged();
            }
        }

        private static void OnAssemblyCompiled(string assemblyPath,
            CompilerMessage[] messages)
        {
            if (messages == null)
            {
                return;
            }
            bool changed = false;
            for (int i = 0; i < messages.Length; i++)
            {
                if (messages[i].type != CompilerMessageType.Error)
                {
                    continue;
                }
                string location = string.IsNullOrEmpty(messages[i].file)
                    ? null : messages[i].file + ":" + messages[i].line;
                changed |= Add(FirstLine(messages[i].message, MaxMessageChars), location, true);
            }
            if (changed)
            {
                RaiseChanged();
            }
        }

        // -- Internals ------------------------------------------------------------

        /// <summary>
        /// Adds/coalesces one entry into _entries (main thread only).
        /// Returns true when the visible set changed (a new distinct
        /// message), false for a repeat-occurrence bump -- callers batch
        /// these into a single RaiseChanged() rather than one per entry.
        /// </summary>
        private static bool Add(string message, string location, bool fromCompiler)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Message, message, StringComparison.Ordinal))
                {
                    Entry existing = _entries[i];
                    existing.Occurrences++;
                    _entries[i] = existing;
                    // Same visible set: no Changed (avoids per-frame churn
                    // while an exception repeats every Update).
                    return false;
                }
            }
            if (_entries.Count >= MaxEntries)
            {
                _entries.RemoveAt(0);
            }
            _entries.Add(new Entry
            {
                Message = message,
                Location = location,
                FromCompiler = fromCompiler,
                Occurrences = 1
            });
            return true;
        }

        // -- Test seams (InternalsVisibleTo Colloid.AgentPanel.Editor.Tests) ------
        //
        // EditMode tests must not depend on Unity actually invoking
        // Application.logMessageReceived off-thread or on a real
        // EditorApplication.update tick; these call the exact same private
        // methods the real hooks call, so the pure queue/coalesce/
        // re-entrancy behavior is exercised directly and deterministically.

        /// <summary>Drives OnLogMessage exactly as the real callback would.</summary>
        internal static void EnqueueLogMessageForTests(string condition, string stackTrace, LogType type)
        {
            OnLogMessage(condition, stackTrace, type);
        }

        /// <summary>Drives PumpQueuedLogEntries exactly as the real update tick would.</summary>
        internal static void PumpQueuedLogEntriesForTests()
        {
            PumpQueuedLogEntries();
        }

        /// <summary>
        /// Test seam for the ignore-filter settings source
        /// (docs/design-notes/2026-08-13-error-chip-ignore.md): when set,
        /// VisibleSnapshot/ResolveSettings use this instead of the real
        /// PanelStateStore.instance ScriptableSingleton, so
        /// ConsoleErrorProviderVisibilityTests can inject an isolated
        /// PanelSettings (ignoredConsoleErrors/ignoredConsoleErrorPatterns)
        /// without touching the sandbox project's real, shared settings
        /// asset. Null (the default) falls back to the real store -- see
        /// ResolveSettings. Tests MUST null this out again in TearDown
        /// (ResetForTests does NOT clear it, since it is not part of the
        /// captured-entries state this reset otherwise covers, and clearing
        /// it implicitly here would let a test's own explicit teardown
        /// silently become redundant instead of being the one place this
        /// is documented to matter).
        /// </summary>
        internal static Func<PanelSettings> SettingsSourceForTests;

        /// <summary>
        /// Resolves the PanelSettings VisibleSnapshot filters against:
        /// <see cref="SettingsSourceForTests"/> when a test has set it,
        /// otherwise the real PanelStateStore.instance.Settings.
        /// </summary>
        private static PanelSettings ResolveSettings()
        {
            if (SettingsSourceForTests != null)
            {
                return SettingsSourceForTests();
            }
            return PanelStateStore.instance.Settings;
        }

        /// <summary>Number of raw log entries queued but not yet applied.</summary>
        internal static int QueuedLogEntryCountForTests
        {
            get
            {
                lock (_queueLock)
                {
                    return _queuedLogEntries.Count;
                }
            }
        }

        /// <summary>
        /// Full reset for test isolation: forgets captured entries AND any
        /// not-yet-applied queued log lines (plain Clear() only does the
        /// former). Never touches the Changed subscriber list.
        /// </summary>
        internal static void ResetForTests()
        {
            _entries.Clear();
            lock (_queueLock)
            {
                _queuedLogEntries.Clear();
            }
            _applyingQueuedLogEntries = false;
        }

        private static string FirstLine(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            int cut = text.IndexOf('\n');
            string line = cut >= 0 ? text.Substring(0, cut) : text;
            line = line.TrimEnd('\r');
            if (line.Length > maxChars)
            {
                line = line.Substring(0, maxChars - 3) + "...";
            }
            return line;
        }

        private static void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
