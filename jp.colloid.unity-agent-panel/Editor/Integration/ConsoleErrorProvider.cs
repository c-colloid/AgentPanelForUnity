using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
    /// Compile window (docs/design-notes/2026-09-15-panel-ux-followups.md
    /// section 1): a compilation run's compiler errors are buffered while
    /// the run is in flight and published in one batch at
    /// compilationFinished, and <see cref="Settling"/> tells the UI that
    /// the "ask the agent to fix these" affordances should stay down until
    /// then. What the panel's own staged-script validation build logs
    /// (BeginValidationBuild) is dropped outright -- that code is not in
    /// the project and the agent already has the compiler messages.
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

        /// <summary>
        /// Compiler errors of the compilation run currently in flight,
        /// held back until the run ENDS (docs/design-notes/2026-09-15-
        /// panel-ux-followups.md section 1). assemblyCompilationFinished
        /// fires per assembly, in the middle of a run, so adding straight
        /// into _entries published errors that the rest of the same run --
        /// or the very next run Unity queues behind it -- routinely made
        /// obsolete: the chip appeared on every agent-driven compile and
        /// then vanished on its own, which is exactly the "was that real?"
        /// confusion this buffer removes. Flushed synchronously from
        /// OnCompilationFinished (NOT from an update tick: Unity can go
        /// straight from compilationFinished into beforeAssemblyReload
        /// without one, and ReloadLifecycle's pre-reload snapshot -- the
        /// HUB-8 carry-over that tells the model whether the compile
        /// failed -- has to see these entries).
        /// </summary>
        private static readonly List<Entry> _pendingCompilerEntries = new List<Entry>();

        /// <summary>Nesting depth of compilation runs in flight (compilationStarted/Finished).</summary>
        private static int _compileRunDepth;

        /// <summary>
        /// Nesting depth of the panel's own staged-script validation build
        /// (UapScriptsCommitTool's AssemblyBuilder). Written from the main
        /// thread but READ from OnLogMessage, which Unity may call on any
        /// thread -- hence Interlocked/volatile access, never a plain read.
        /// </summary>
        private static int _validationBuildDepth;

        /// <summary>Most messages one tool scope keeps (the rest are dropped, not queued).</summary>
        internal const int MaxToolScopeErrors = 5;

        // Tool scope state (BeginToolScope): guarded by _queueLock, because
        // OnLogMessage reads it from whatever thread Unity logs on.
        private static int _toolScopeDepth;
        private static int _toolScopeThreadId;
        private static readonly List<string> _toolScopeCaptured = new List<string>();

        /// <summary>
        /// Set when the validation-build window opens or closes; drained by
        /// PumpQueuedLogEntries on the next main-thread tick. The raise is
        /// deferred for the same reason OnLogMessage's is (2026-08-02
        /// design note section 1): Changed subscribers touch VisualElements,
        /// and this pair is driven by an AssemblyBuilder callback whose
        /// thread the panel does not get to choose.
        /// </summary>
        private static volatile bool _windowChangePending;

        /// <summary>Raised when the set of captured errors changes.</summary>
        public static event Action Changed;

        /// <summary>
        /// True while nothing the panel could show is final yet: a
        /// compilation run is in flight, or the panel is compiling staged
        /// scripts to validate them. The "ask the agent to fix these"
        /// affordances (ContextBarView's error chip, EmptyStateView's fix
        /// suggestion) stay hidden while this holds -- mid-compile the
        /// error set is a moving target, the user cannot act on it, and a
        /// chip that appears and disappears by itself reads as a bug.
        /// Captured entries are NOT hidden by this: VisibleSnapshot /
        /// VisibleCount / FormatDigest keep reporting exactly what has
        /// been captured, so AgentHub's post-compile verdict and the
        /// pre-reload carry-over are untouched.
        /// </summary>
        public static bool Settling
        {
            get
            {
                return _compileRunDepth > 0
                    || Interlocked.CompareExchange(ref _validationBuildDepth, 0, 0) > 0;
            }
        }

        /// <summary>
        /// Opens a window in which captured errors are DROPPED rather than
        /// recorded, for the staged-script validation build in
        /// UapScriptsCommitTool: that build compiles code which is not in
        /// the project yet, its diagnostics are already returned to the
        /// agent verbatim in the tool result, and the files it names do not
        /// exist under Assets/ -- so offering the user "ask the agent to
        /// fix these errors" about them is duplicate, unactionable noise.
        /// Whether Unity routes AssemblyBuilder diagnostics through the
        /// Console log callback varies by editor version; this window makes
        /// the panel behave the same either way. Always pair with
        /// <see cref="EndValidationBuild"/>; beforeAssemblyReload
        /// force-resets the depth so an abandoned build can never leave
        /// capture suppressed forever (same unbalanced-counter guard
        /// UapTurnScope applies to DisallowAutoRefresh).
        /// </summary>
        public static void BeginValidationBuild()
        {
            if (Interlocked.Increment(ref _validationBuildDepth) == 1)
            {
                _windowChangePending = true;
            }
        }

        /// <summary>Closes one <see cref="BeginValidationBuild"/> window; never goes below zero.</summary>
        public static void EndValidationBuild()
        {
            int depth = Interlocked.Decrement(ref _validationBuildDepth);
            if (depth < 0)
            {
                Interlocked.Exchange(ref _validationBuildDepth, 0);
                return;
            }
            if (depth == 0)
            {
                _windowChangePending = true;
            }
        }

        /// <summary>
        /// Opens a window in which errors logged ON THE CALLING THREAD are
        /// attributed to the UapOps tool call that is running, instead of
        /// being recorded for the "ask the agent to fix these" chip
        /// (docs/design-notes/2026-09-17-tool-caused-console-errors.md).
        /// Measured case: uap_editor_execute_menu with a menu path that
        /// does not exist -- Unity itself logs an Error, the tool already
        /// answers found:false, the agent works around it, and the chip
        /// then offered the user a "fix" for something that is not wrong
        /// with the project (and kept offering it to the next agent).
        /// UapMainThreadDispatcher opens this around every tool tick and
        /// appends what <see cref="EndToolScope"/> returns to the tool's
        /// result, so the agent still learns about the error. Only the
        /// opening thread is affected: an error from any other thread, or
        /// one the project raises again after the call, reaches the chip
        /// as before. Nestable; the outermost End returns the lines.
        /// </summary>
        public static void BeginToolScope()
        {
            lock (_queueLock)
            {
                if (_toolScopeDepth == 0)
                {
                    _toolScopeThreadId = Thread.CurrentThread.ManagedThreadId;
                    _toolScopeCaptured.Clear();
                }
                _toolScopeDepth++;
            }
        }

        /// <summary>
        /// Closes one <see cref="BeginToolScope"/> window. The outermost
        /// close returns the captured messages (first line each, at most
        /// <see cref="MaxToolScopeErrors"/>); inner closes and unbalanced
        /// calls return an empty array.
        /// </summary>
        public static string[] EndToolScope()
        {
            lock (_queueLock)
            {
                if (_toolScopeDepth <= 0)
                {
                    _toolScopeDepth = 0;
                    return new string[0];
                }
                _toolScopeDepth--;
                if (_toolScopeDepth > 0)
                {
                    return new string[0];
                }
                string[] captured = _toolScopeCaptured.ToArray();
                _toolScopeCaptured.Clear();
                return captured;
            }
        }

        /// <summary>True when the message was taken by an open tool scope on this thread (and must not be queued).</summary>
        private static bool TryCaptureForToolScope(string message)
        {
            lock (_queueLock)
            {
                if (_toolScopeDepth <= 0
                    || _toolScopeThreadId != Thread.CurrentThread.ManagedThreadId)
                {
                    return false;
                }
                if (_toolScopeCaptured.Count < MaxToolScopeErrors
                    && !_toolScopeCaptured.Contains(message))
                {
                    _toolScopeCaptured.Add(message);
                }
                return true;
            }
        }

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
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            // Unbalanced-counter guard (UapTurnScope's idiom): a staged
            // build abandoned by a domain reload must not leave capture
            // suppressed in the next domain -- these are statics, so the
            // reset simply starts the new domain clean.
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseWindowsForDomainReload;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseWindowsForDomainReload;
            EditorApplication.update -= PumpQueuedLogEntries;
            EditorApplication.update += PumpQueuedLogEntries;
        }

        private static void ReleaseWindowsForDomainReload()
        {
            Interlocked.Exchange(ref _validationBuildDepth, 0);
            ResetToolScope();
            _compileRunDepth = 0;
            _pendingCompilerEntries.Clear();
            _windowChangePending = false;
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
            // Staged-script validation build in flight: whatever it logs is
            // about code that is not in the project yet and is already on
            // its way back to the agent in the tool result -- see
            // BeginValidationBuild. Dropped here rather than filtered later
            // so it never reaches _entries at all. Interlocked because this
            // callback can run on any thread.
            if (Interlocked.CompareExchange(ref _validationBuildDepth, 0, 0) > 0)
            {
                return;
            }
            string message = FirstLine(condition, MaxMessageChars);
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            // A UapOps tool is running on this thread: the error belongs to
            // that call and goes back to the agent in its result -- see
            // BeginToolScope.
            if (TryCaptureForToolScope(message))
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
            if (_windowChangePending)
            {
                _windowChangePending = false;
                RaiseChanged();
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
            _compileRunDepth++;
            // The compile that is starting supersedes the previous one's
            // errors; runtime errors are kept. The pending buffer goes with
            // them: a run that never reached compilationFinished has no
            // result worth publishing.
            _pendingCompilerEntries.Clear();
            _entries.RemoveAll(delegate (Entry entry)
            {
                return entry.FromCompiler;
            });
            // Unconditional, unlike the pre-2026-09-15 "only when something
            // was removed": Settling has just flipped, and the chip hides
            // off that, not off the entry list.
            RaiseChanged();
        }

        /// <summary>
        /// The run is over: its compiler errors are final, so publish them
        /// in one batch. Synchronous on purpose -- see
        /// _pendingCompilerEntries' doc comment for why this cannot wait
        /// for an update tick.
        /// </summary>
        private static void OnCompilationFinished(object context)
        {
            if (_compileRunDepth > 0)
            {
                _compileRunDepth--;
            }
            for (int i = 0; i < _pendingCompilerEntries.Count; i++)
            {
                Entry entry = _pendingCompilerEntries[i];
                Add(entry.Message, entry.Location, true);
            }
            _pendingCompilerEntries.Clear();
            RaiseChanged();
        }

        private static void OnAssemblyCompiled(string assemblyPath,
            CompilerMessage[] messages)
        {
            if (messages == null)
            {
                return;
            }
            // Same window the log callback honours: should an editor version
            // route the staged-script AssemblyBuilder through this event too,
            // its diagnostics must not ride the next real run's flush into
            // the chip. See BeginValidationBuild.
            if (Interlocked.CompareExchange(ref _validationBuildDepth, 0, 0) > 0)
            {
                return;
            }
            for (int i = 0; i < messages.Length; i++)
            {
                if (messages[i].type != CompilerMessageType.Error)
                {
                    continue;
                }
                string location = string.IsNullOrEmpty(messages[i].file)
                    ? null : messages[i].file + ":" + messages[i].line;
                // Buffered, not published: this fires mid-run. No Changed
                // here either -- there is nothing new for a consumer to see
                // until OnCompilationFinished flushes.
                BufferCompilerEntry(FirstLine(messages[i].message, MaxMessageChars), location);
            }
        }

        /// <summary>
        /// Adds one compiler error to the pending buffer, deduplicated by
        /// Message exactly as <see cref="Add"/> deduplicates _entries, so
        /// the same error reported by several assemblies of one run still
        /// publishes once.
        /// </summary>
        private static void BufferCompilerEntry(string message, string location)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            for (int i = 0; i < _pendingCompilerEntries.Count; i++)
            {
                if (string.Equals(_pendingCompilerEntries[i].Message, message, StringComparison.Ordinal))
                {
                    return;
                }
            }
            if (_pendingCompilerEntries.Count >= MaxEntries)
            {
                _pendingCompilerEntries.RemoveAt(0);
            }
            _pendingCompilerEntries.Add(new Entry
            {
                Message = message,
                Location = location,
                FromCompiler = true,
                Occurrences = 1
            });
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

        /// <summary>Drives CompilationPipeline.compilationStarted exactly as the real hook would.</summary>
        internal static void NotifyCompilationStartedForTests()
        {
            OnCompilationStarted(null);
        }

        /// <summary>
        /// Drives CompilationPipeline.assemblyCompilationFinished exactly as
        /// the real hook would, with one compiler error in one assembly.
        /// </summary>
        internal static void NotifyCompilerErrorForTests(string message, string file, int line)
        {
            OnAssemblyCompiled("Library/ScriptAssemblies/Test.dll", new[]
            {
                new CompilerMessage
                {
                    message = message,
                    file = file,
                    line = line,
                    type = CompilerMessageType.Error
                }
            });
        }

        /// <summary>Drives CompilationPipeline.compilationFinished exactly as the real hook would.</summary>
        internal static void NotifyCompilationFinishedForTests()
        {
            OnCompilationFinished(null);
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
            _pendingCompilerEntries.Clear();
            _compileRunDepth = 0;
            Interlocked.Exchange(ref _validationBuildDepth, 0);
            ResetToolScope();
            _windowChangePending = false;
        }

        private static void ResetToolScope()
        {
            lock (_queueLock)
            {
                _toolScopeDepth = 0;
                _toolScopeCaptured.Clear();
            }
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
