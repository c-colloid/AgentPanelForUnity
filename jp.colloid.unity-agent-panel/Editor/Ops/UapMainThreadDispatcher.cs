using System;
using System.Collections.Generic;
using System.Threading;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Thread-safe hop from an arbitrary HttpListener worker thread to the
    /// Unity main thread (design section 1: "Tool EXECUTION must be
    /// marshaled to the Unity main thread ... The HTTP response waits (with
    /// timeout) for the main-thread result"). A worker thread calls
    /// <see cref="Execute"/>, which enqueues the work and BLOCKS (bounded by
    /// <see cref="DefaultTimeoutMillis"/>) until <see cref="Pump"/> -- called
    /// from EditorApplication.update by the Integration layer, the same
    /// idiom as EditorUpdatePump -- runs it. Deliberately has NO
    /// UnityEditor/UnityEngine dependency: unit tests drive it by calling
    /// Pump() directly instead of through the real update loop.
    /// </summary>
    public sealed class UapMainThreadDispatcher : IUapToolExecutor
    {
        /// <summary>
        /// Hard wall-clock bound for one tool call (millis). Mutable for
        /// tests only needing much shorter bounds. Applies to how long the
        /// HTTP worker BLOCKS in Execute; since SEC-7 a pollable item whose
        /// waiter timed out is also ABANDONED (WorkItem.Cancelled) rather
        /// than left running -- its terminal side effect (FinishCommit
        /// moving files into Assets/) must not happen after the caller was
        /// already told the call failed. The CLI-side MCP_TOOL_TIMEOUT /
        /// HTTP layer remains the outer budget authority for pollables.
        /// </summary>
        public int DefaultTimeoutMillis = 15000;

        /// <summary>
        /// Wall-clock bound used INSTEAD of <see cref="DefaultTimeoutMillis"/>
        /// while <see cref="StallHintProvider"/> reports the main thread as
        /// throttled (Play Mode running with the Editor unfocused and Run In
        /// Background off -- the Editor ticks sporadically, if at all).
        /// Deliberately SHORTER: the CLI has its own HTTP budget for the
        /// call, and a tools/call that dies there surfaces as a bare
        /// "The operation timed out." with no hint at all (live report,
        /// design note 2026-09-06 section 7). Failing first, with the cause
        /// and the fix ("focus the Editor window") in the message, beats
        /// waiting out a budget the caller may not have.
        /// </summary>
        public int ThrottledTimeoutMillis = 8000;

        /// <summary>
        /// Optional, set by the Integration layer (UapOpsServer): returns a
        /// human-readable reason the main thread is currently expected to
        /// be slow or stalled (e.g. "Play Mode is running and the Unity
        /// Editor is unfocused"), or null when nothing is known to throttle
        /// it. Read from the HTTP worker thread, so implementations must
        /// only consult a snapshot the main thread refreshed on its last
        /// tick -- never a Unity API directly. Keeps this class Unity-free.
        /// </summary>
        public Func<string> StallHintProvider;

        /// <summary>
        /// Where a call that outlives its waiter while ALREADY running is
        /// recorded so uap_job_status can hand its result back later
        /// (design note 2026-09-09-jobs-and-destructive-confirm section
        /// 1). Defaults to the Editor-session ledger; tests substitute
        /// their own for isolation.
        /// </summary>
        public UapJobLedger Jobs = UapJobLedger.Shared;

        /// <summary>
        /// Optional, set by the Integration layer (UapOpsServer): opens a
        /// window around one tool tick in which Console errors raised on
        /// the pumping thread belong to the TOOL CALL, not to the project
        /// (design note 2026-09-17-tool-caused-console-errors.md). Paired
        /// with <see cref="EndToolLogScope"/>, which closes the window and
        /// returns what was captured; those lines are appended to the
        /// tool's result so the agent still sees them. Hooks rather than a
        /// direct ConsoleErrorProvider call: this class stays Unity-free.
        /// </summary>
        public Action BeginToolLogScope;

        /// <summary>Closes the <see cref="BeginToolLogScope"/> window; returns the captured error lines (may be null/empty).</summary>
        public Func<string[]> EndToolLogScope;

        /// <summary>Heading of the block <see cref="AppendScopedErrors"/> adds to a result.</summary>
        public const string ScopedErrorsHeading = "Unity Console errors logged during this call:";

        private long _lastPumpUtcTicks;

        /// <summary>
        /// UTC ticks of the most recent <see cref="Pump"/> call (0 = never).
        /// Stamped by the main thread, read by worker threads to say how
        /// long ago the Editor loop last ran when a call times out.
        /// </summary>
        public long LastPumpUtcTicks
        {
            get { return Interlocked.Read(ref _lastPumpUtcTicks); }
        }

        /// <summary>
        /// Seconds since the last <see cref="Pump"/>, or -1 when it never
        /// ran (the server was just started and the Editor has not ticked
        /// yet).
        /// </summary>
        public double SecondsSinceLastPump
        {
            get
            {
                long last = LastPumpUtcTicks;
                if (last == 0)
                {
                    return -1;
                }
                return (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerSecond;
            }
        }

        /// <summary>
        /// The text appended to a main-thread timeout: how long ago the
        /// Editor loop last ticked plus whatever <see cref="StallHintProvider"/>
        /// knows, so the model (and the person reading the transcript) can
        /// tell "Unity is throttled, focus the window" apart from "the tool
        /// hung". Pure and public so the exact wording is testable.
        /// </summary>
        public static string DescribeStall(double secondsSinceLastPump, string stallHint)
        {
            var sb = new System.Text.StringBuilder();
            if (secondsSinceLastPump < 0)
            {
                sb.Append(" The Editor loop has not ticked since the UapOps server started.");
            }
            else
            {
                sb.Append(" The Editor loop last ticked ")
                    .Append(secondsSinceLastPump.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" s ago.");
            }
            if (!string.IsNullOrEmpty(stallHint))
            {
                sb.Append(' ').Append(stallHint);
            }
            return sb.ToString();
        }

        private sealed class WorkItem
        {
            public IUapTool Tool;
            public JsonNode Input;
            public JsonNode Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            /// <summary>Console error lines captured across this item's ticks (null until the first one).</summary>
            public List<string> ScopedErrors;
            /// <summary>Opaque per-invocation state for IUapPollableTool.Poll (see its doc comment). Unused for a plain IUapTool.</summary>
            public object PollState;
            /// <summary>
            /// The job record every item carries from enqueue. Started/
            /// completed by the main thread in ProcessOnce; registered in
            /// the ledger by the waiter ONLY on a StillRunning timeout, so
            /// a call that finishes within its budget never clutters
            /// uap_job_status.
            /// </summary>
            public UapJob Job;
            /// <summary>
            /// SEC-7: set by the timed-out Execute waiter (and only read on
            /// the main thread); ProcessOnce discards a cancelled item
            /// without polling or re-enqueueing it, so a pollable whose
            /// caller already gave up never silently completes its side
            /// effects. For uap_scripts_commit this means the staged files
            /// stay put -- exactly the "failure leaves the stage untouched"
            /// semantics the tool documents. Narrow race: a timeout landing
            /// DURING the item's terminal tick can still complete it; the
            /// flag closes the every-later-tick window, not that one tick.
            /// </summary>
            public volatile bool Cancelled;
            /// <summary>
            /// Lifecycle word settled by ONE Interlocked exchange between the
            /// main thread (ProcessOnce: Queued -> Started on the first tick)
            /// and the timed-out waiter (Execute: Queued -> Dropped). Whoever
            /// wins decides what the timeout MEANS (design note
            /// 2026-09-08-menu-timeout-and-tool-steering section 2): a live
            /// session got "timed out waiting for the Unity main thread" for
            /// a 9-minute scene build the menu DID start, and the identical
            /// text for a menu the reload-busy Editor never picked up. The
            /// agent could not tell the two apart and polled Editor.log for a
            /// completion marker that, in the second case, never came.
            /// </summary>
            public int StartState;
        }

        private const int StateQueued = 0;
        private const int StateStarted = 1;
        private const int StateDropped = 2;

        /// <summary>What a main-thread timeout turned out to mean, for <see cref="DescribeTimeout"/>.</summary>
        public enum TimeoutOutcome
        {
            /// <summary>The main thread never picked the item up; it was discarded and nothing ran.</summary>
            NeverStarted,
            /// <summary>A plain IUapTool.Execute is still on the main thread; it will finish on its own.</summary>
            StillRunning,
            /// <summary>An IUapPollableTool started but is abandoned before its terminal tick (SEC-7).</summary>
            Abandoned
        }

        /// <summary>
        /// The first sentence of a main-thread timeout, one per
        /// <see cref="TimeoutOutcome"/>. Pure and public so the exact
        /// wording -- which the steering prompt quotes back to the agent
        /// ("still running" / "was never started") -- is pinned by tests.
        /// </summary>
        public static string DescribeTimeout(string toolName, int budgetMillis, TimeoutOutcome outcome)
        {
            return DescribeTimeout(toolName, budgetMillis, outcome, null);
        }

        /// <summary>
        /// As <see cref="DescribeTimeout(string,int,TimeoutOutcome)"/>; for
        /// <see cref="TimeoutOutcome.StillRunning"/> a non-null
        /// <paramref name="jobId"/> tells the agent the call's result is
        /// kept under that id for uap_job_status (design note 2026-09-09
        /// section 1), instead of leaving "verify the effect" as the only
        /// way to learn what the call did.
        /// </summary>
        public static string DescribeTimeout(string toolName, int budgetMillis, TimeoutOutcome outcome, string jobId)
        {
            string budget = (budgetMillis / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            switch (outcome)
            {
                case TimeoutOutcome.StillRunning:
                    return "Tool '" + toolName + "' is still running on the Unity main thread after " + budget
                        + " s: it DID start and will finish on its own (the Editor is blocked until it returns)."
                        + " Do not re-issue it and do not poll Editor.log."
                        + (!string.IsNullOrEmpty(jobId)
                            ? " Its result is kept as job \"" + jobId + "\": call uap_job_status with job_id \""
                              + jobId + "\" (wait_ms up to " + UapJobStatusTool.MaxWaitMillis
                              + ") until its state is succeeded or failed -- it answers even while the Editor is"
                              + " blocked and returns the tool's own result."
                            : " Call uap_ping until it answers (a ping that times out means the Editor is"
                              + " still busy), then verify the effect.")
                        + " uap_ping is the liveness check when you only need to know the Editor is free again.";
                case TimeoutOutcome.Abandoned:
                    return "Tool '" + toolName + "' timed out after " + budget
                        + " s and was abandoned before completing; its terminal side effects did not run.";
                default:
                    return "Tool '" + toolName + "' was never started: the Unity main thread did not tick within "
                        + budget + " s, so the call was discarded and NOTHING ran. Wait for uap_ping to answer,"
                        + " then issue it again.";
            }
        }

        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private readonly object _lock = new object();

        /// <summary>Items currently queued, not yet picked up by Pump (test/diagnostics seam).</summary>
        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>
        /// Fails every item CURRENTLY queued (not yet picked up by a Pump
        /// call) with an exception carrying <paramref name="reason"/>,
        /// unblocking whichever HTTP worker thread(s) are waiting inside
        /// <see cref="Execute"/> for it -- called by UapOpsServer.Stop right
        /// before it unhooks the pump and disposes the listener, so a
        /// tools/call in flight when the server stops (master toggle OFF,
        /// or the beforeAssemblyReload path) fails FAST instead of blocking
        /// its worker thread for up to <see cref="DefaultTimeoutMillis"/>
        /// with nothing left to ever run it (nobody calls Pump once the
        /// pump is unhooked). Safe to call with nothing queued (no-op). Only
        /// ever reaches items still WAITING in the queue -- Pump/Stop both
        /// run on the main thread, so nothing can be mid-Execute on another
        /// thread concurrently with this call.
        /// </summary>
        public void CancelAll(string reason)
        {
            List<WorkItem> pending;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    return;
                }
                pending = new List<WorkItem>(_queue);
                _queue.Clear();
            }
            for (int i = 0; i < pending.Count; i++)
            {
                WorkItem item = pending[i];
                item.Error = new InvalidOperationException(reason);
                item.Done.Set();
            }
        }

        /// <summary>
        /// Enqueues the tool call and blocks the CALLING thread until Pump()
        /// runs it (or DefaultTimeoutMillis elapses). Never runs
        /// <paramref name="tool"/>.Execute on the calling thread itself.
        /// </summary>
        public JsonNode Execute(IUapTool tool, JsonNode input)
        {
            if (tool == null)
            {
                throw new ArgumentNullException("tool");
            }
            if (tool is IUapOffThreadTool)
            {
                // Declared main-thread-free (uap_job_status): run right here
                // on the worker so it answers even while the main thread is
                // blocked by a long tool -- the very situation it exists for.
                return tool.Execute(input);
            }
            UapJobLedger ledger = Jobs ?? UapJobLedger.Shared;
            var item = new WorkItem { Tool = tool, Input = input, Job = new UapJob(ledger.NewId(), tool.Name) };
            lock (_lock)
            {
                _queue.Enqueue(item);
            }
            string stallHint = ReadStallHint();
            int budget = stallHint != null ? Math.Min(ThrottledTimeoutMillis, DefaultTimeoutMillis) : DefaultTimeoutMillis;
            if (!item.Done.Wait(budget))
            {
                // Settle what this timeout means with the main thread: if
                // the item is still Queued we win and it becomes Dropped
                // (Pump discards it on arrival); if ProcessOnce already
                // moved it to Started, it is running right now.
                bool neverStarted = Interlocked.CompareExchange(
                    ref item.StartState, StateDropped, StateQueued) == StateQueued;
                // SEC-7: nobody is waiting for this item's result any more,
                // so a pollable must not keep ticking (it would otherwise
                // re-enqueue every tick and eventually commit its side
                // effects with the caller long gone). A plain IUapTool that
                // already started cannot be stopped -- it owns the main
                // thread until it returns -- so the message says exactly
                // that instead of pretending it was cancelled.
                item.Cancelled = true;
                TimeoutOutcome outcome = neverStarted ? TimeoutOutcome.NeverStarted
                    : (tool is IUapPollableTool ? TimeoutOutcome.Abandoned : TimeoutOutcome.StillRunning);
                string jobId = null;
                if (outcome == TimeoutOutcome.StillRunning)
                {
                    // The main thread owns this call now and WILL produce a
                    // result; keep it. (Order against the main thread's
                    // Complete does not matter: UapJob is shared state.)
                    ledger.Register(item.Job);
                    jobId = item.Job.Id;
                }
                // Re-read the hint: the Editor may have become throttled
                // (or recovered) while this call was waiting.
                string hintNow = ReadStallHint() ?? stallHint;
                throw new TimeoutException(DescribeTimeout(tool.Name, budget, outcome, jobId)
                    + DescribeStall(SecondsSinceLastPump, hintNow));
            }
            if (item.Error != null)
            {
                throw item.Error;
            }
            return item.Result;
        }

        /// <summary>
        /// Runs every work item queued AS OF THE START of this call,
        /// synchronously, on whichever thread calls this. Production wiring
        /// calls this from EditorApplication.update (always the main
        /// thread); tests call it directly to simulate that tick without a
        /// real editor loop. New items enqueued by a concurrent Execute()
        /// call WHILE this is running, and any IUapPollableTool item that
        /// re-enqueues itself for another tick (see ProcessOnce), are picked
        /// up by the NEXT Pump() call, not this one -- the snapshot bound
        /// below is what guarantees this call always terminates even though
        /// a still-in-progress poll re-enters the same queue.
        /// </summary>
        public void Pump()
        {
            Interlocked.Exchange(ref _lastPumpUtcTicks, DateTime.UtcNow.Ticks);
            int budget;
            lock (_lock)
            {
                budget = _queue.Count;
            }
            for (int i = 0; i < budget; i++)
            {
                WorkItem item;
                lock (_lock)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                    item = _queue.Dequeue();
                }
                ProcessOnce(item);
            }
        }

        private string ReadStallHint()
        {
            Func<string> provider = StallHintProvider;
            if (provider == null)
            {
                return null;
            }
            try
            {
                string hint = provider();
                return string.IsNullOrEmpty(hint) ? null : hint;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs one work item for one tick. A plain IUapTool completes (and
        /// signals Done) synchronously, same as before this method existed.
        /// An IUapPollableTool is polled once; while it reports "not done"
        /// it is re-enqueued (NOT re-processed within this same Pump() call
        /// -- see the budget loop above) instead of blocking this thread
        /// until it finishes (design section 7.4/8.1: uap_scripts_commit
        /// must never freeze the editor for the whole AssemblyBuilder
        /// compile).
        /// </summary>
        private void ProcessOnce(WorkItem item)
        {
            // First tick: claim the item. Losing the exchange means the
            // waiter already timed out and dropped it (StateDropped):
            // discard without ever running it, so a tool never fires AFTER
            // its caller was told nothing ran. Later ticks of a pollable
            // (already StateStarted) fall through to the SEC-7 check.
            bool firstTick = Interlocked.CompareExchange(
                ref item.StartState, StateStarted, StateQueued) == StateQueued;
            if (!firstTick && item.StartState == StateDropped)
            {
                item.Done.Set();
                return;
            }
            if (firstTick && item.Job != null)
            {
                item.Job.MarkStarted();
            }
            if (item.Cancelled)
            {
                // SEC-7: the Execute waiter timed out and already threw --
                // discard silently (no poll, no re-enqueue). Done is set
                // for hygiene; nobody can be waiting on it.
                item.Done.Set();
                return;
            }
            var pollable = item.Tool as IUapPollableTool;
            if (pollable == null)
            {
                OpenToolLogScope();
                try
                {
                    item.Result = item.Tool.Execute(item.Input);
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    CloseToolLogScope(item);
                    if (item.Error == null)
                    {
                        item.Result = AppendScopedErrors(item.Result, item.ScopedErrors);
                    }
                    if (item.Job != null)
                    {
                        item.Job.Complete(item.Result, item.Error);
                    }
                    item.Done.Set();
                }
                return;
            }

            JsonNode result;
            bool done;
            OpenToolLogScope();
            try
            {
                done = pollable.Poll(item.Input, ref item.PollState, out result);
            }
            catch (Exception ex)
            {
                CloseToolLogScope(item);
                item.Error = ex;
                if (item.Job != null)
                {
                    item.Job.Complete(null, ex);
                }
                item.Done.Set();
                return;
            }
            CloseToolLogScope(item);
            if (!done)
            {
                lock (_lock)
                {
                    _queue.Enqueue(item);
                }
                return;
            }
            result = AppendScopedErrors(result, item.ScopedErrors);
            item.Result = result;
            if (item.Job != null)
            {
                item.Job.Complete(result, null);
            }
            item.Done.Set();
        }

        private void OpenToolLogScope()
        {
            Action begin = BeginToolLogScope;
            if (begin != null)
            {
                begin();
            }
        }

        /// <summary>
        /// Closes the log scope and keeps what it captured on the item: a
        /// pollable tool spans several ticks, so its lines accumulate until
        /// the tick that completes it.
        /// </summary>
        private void CloseToolLogScope(WorkItem item)
        {
            Func<string[]> end = EndToolLogScope;
            if (end == null)
            {
                return;
            }
            string[] captured = end();
            if (captured == null || captured.Length == 0)
            {
                return;
            }
            if (item.ScopedErrors == null)
            {
                item.ScopedErrors = new List<string>();
            }
            for (int i = 0; i < captured.Length && item.ScopedErrors.Count < MaxScopedErrorLines; i++)
            {
                item.ScopedErrors.Add(captured[i]);
            }
        }

        /// <summary>Most Console error lines one result carries.</summary>
        public const int MaxScopedErrorLines = 5;

        /// <summary>
        /// Adds one text block listing the Console errors the call raised,
        /// so the agent learns about them from the result (they are kept
        /// out of the panel's "ask the agent to fix" chip). A result that
        /// is not a content array is returned untouched.
        /// </summary>
        internal static JsonNode AppendScopedErrors(JsonNode result, List<string> errors)
        {
            if (errors == null || errors.Count == 0 || result == null || !result.IsArray)
            {
                return result;
            }
            var text = new System.Text.StringBuilder(ScopedErrorsHeading);
            for (int i = 0; i < errors.Count; i++)
            {
                text.Append("\n- ").Append(errors[i]);
            }
            result.Add(JsonNode.NewObject().Set("type", "text").Set("text", text.ToString()));
            return result;
        }
    }
}
