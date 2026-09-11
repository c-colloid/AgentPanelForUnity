using System;
using System.Threading;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Lifecycle of a <see cref="UapJob"/> as reported by uap_job_status.</summary>
    public enum UapJobState
    {
        /// <summary>Enqueued for the main thread; no tick has picked it up yet.</summary>
        Queued,
        /// <summary>The tool body is executing on the Unity main thread.</summary>
        Running,
        /// <summary>The tool returned; <see cref="UapJob.Result"/> holds its content array.</summary>
        Succeeded,
        /// <summary>The tool threw; <see cref="UapJob.Error"/> holds the message.</summary>
        Failed
    }

    /// <summary>
    /// One tools/call as the dispatcher sees it across the HTTP waiter's
    /// timeout (design note 2026-09-09-jobs-and-destructive-confirm
    /// section 1). Every work item carries one of these from the moment it
    /// is enqueued; it only becomes VISIBLE (registered in
    /// <see cref="UapJobLedger"/>) when the waiter gives up on a call that
    /// DID start -- the "still running on the Unity main thread" outcome --
    /// so the result the main thread eventually produces is kept for
    /// uap_job_status instead of being dropped on the floor. Written by the
    /// main thread (start/complete) and the HTTP worker (register/wait);
    /// the completion event and volatile fields make the two orders
    /// ("registered, then completed" and "completed, then registered")
    /// indistinguishable to a reader.
    /// </summary>
    public sealed class UapJob
    {
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        private volatile bool _started;
        private JsonNode _result;
        private string _error;
        private DateTime? _startedUtc;
        private DateTime? _finishedUtc;

        public UapJob(string id, string toolName)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("id must be non-empty.", "id");
            }
            Id = id;
            ToolName = toolName ?? string.Empty;
            QueuedUtc = DateTime.UtcNow;
        }

        /// <summary>Ledger-issued id, quoted back to the agent in the timeout message.</summary>
        public string Id { get; private set; }

        /// <summary>Wire name of the tool this call invoked.</summary>
        public string ToolName { get; private set; }

        public DateTime QueuedUtc { get; private set; }

        /// <summary>When the main thread picked the item up, or null while still queued.</summary>
        public DateTime? StartedUtc
        {
            get { return _startedUtc; }
        }

        /// <summary>When the tool returned or threw, or null while it is still running.</summary>
        public DateTime? FinishedUtc
        {
            get { return _finishedUtc; }
        }

        /// <summary>The tool's MCP content array, only meaningful in <see cref="UapJobState.Succeeded"/>.</summary>
        public JsonNode Result
        {
            get { return _done.IsSet ? _result : null; }
        }

        /// <summary>The tool's exception message, only meaningful in <see cref="UapJobState.Failed"/>.</summary>
        public string Error
        {
            get { return _done.IsSet ? _error : null; }
        }

        public bool IsCompleted
        {
            get { return _done.IsSet; }
        }

        public UapJobState State
        {
            get
            {
                if (_done.IsSet)
                {
                    return _error != null ? UapJobState.Failed : UapJobState.Succeeded;
                }
                return _started ? UapJobState.Running : UapJobState.Queued;
            }
        }

        /// <summary>
        /// Blocks the calling thread until the job completes or
        /// <paramref name="millis"/> elapses. Returns true when completed.
        /// Only ever called from an HTTP worker (uap_job_status is an
        /// off-thread tool) -- never from the main thread, which is the
        /// thread that has to complete it.
        /// </summary>
        public bool WaitForCompletion(int millis)
        {
            if (millis <= 0)
            {
                return _done.IsSet;
            }
            return _done.Wait(millis);
        }

        /// <summary>Main thread: the tool body is about to run.</summary>
        internal void MarkStarted()
        {
            if (!_started)
            {
                _startedUtc = DateTime.UtcNow;
                _started = true;
            }
        }

        /// <summary>
        /// Main thread: the tool body returned <paramref name="result"/>
        /// or threw <paramref name="error"/>. Idempotent: the first
        /// completion wins.
        /// </summary>
        internal void Complete(JsonNode result, Exception error)
        {
            if (_done.IsSet)
            {
                return;
            }
            _result = result;
            _error = error != null ? (error.Message ?? error.GetType().Name) : null;
            _finishedUtc = DateTime.UtcNow;
            if (!_started)
            {
                _startedUtc = _finishedUtc;
                _started = true;
            }
            _done.Set();
        }
    }
}
