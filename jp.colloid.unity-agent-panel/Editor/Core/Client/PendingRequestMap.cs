using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Core.Client
{
    /// <summary>
    /// Correlates outbound control_requests with their control_responses by
    /// request_id, with a per-request timeout. Single-threaded by contract:
    /// all calls happen on the pump thread. Responses may legitimately
    /// arrive before or after any related stream events -- resolution is
    /// order-independent.
    /// </summary>
    public sealed class PendingRequestMap
    {
        /// <summary>One in-flight control_request.</summary>
        public sealed class PendingRequest
        {
            public string RequestId;
            /// <summary>Request subtype ("initialize", "interrupt", ...).</summary>
            public string Kind;
            public double DeadlineSeconds;
            /// <summary>Invoked on resolution; null response = timed out.</summary>
            public Action<ControlResponseMessage> Callback;
        }

        /// <summary>Default control_request timeout in seconds.</summary>
        public const double DefaultTimeoutSeconds = 60.0;

        private readonly Dictionary<string, PendingRequest> _pending =
            new Dictionary<string, PendingRequest>();
        private int _counter;

        public int Count
        {
            get { return _pending.Count; }
        }

        /// <summary>Generates the next request id, e.g. "req_1", "int_2".</summary>
        public string NextRequestId(string prefix)
        {
            _counter++;
            return (string.IsNullOrEmpty(prefix) ? "req" : prefix) + "_" + _counter;
        }

        /// <summary>Registers an in-flight request.</summary>
        public void Track(string requestId, string kind, double nowSeconds,
            double timeoutSeconds = DefaultTimeoutSeconds,
            Action<ControlResponseMessage> callback = null)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }
            _pending[requestId] = new PendingRequest
            {
                RequestId = requestId,
                Kind = kind,
                DeadlineSeconds = nowSeconds + (timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds),
                Callback = callback
            };
        }

        /// <summary>
        /// Resolves a response against its pending request. Returns the
        /// matched request (already removed) or null when the response is
        /// unsolicited (late/duplicate -- callers just log it).
        /// The callback, when present, is invoked with the response.
        /// </summary>
        public PendingRequest TryResolve(ControlResponseMessage response)
        {
            if (response == null || string.IsNullOrEmpty(response.RequestId))
            {
                return null;
            }
            PendingRequest request;
            if (!_pending.TryGetValue(response.RequestId, out request))
            {
                return null;
            }
            _pending.Remove(response.RequestId);
            if (request.Callback != null)
            {
                request.Callback(response);
            }
            return request;
        }

        /// <summary>
        /// Removes every request whose deadline has passed and returns them.
        /// Callbacks are invoked with null to signal the timeout.
        /// </summary>
        public List<PendingRequest> CollectTimedOut(double nowSeconds)
        {
            List<PendingRequest> expired = null;
            foreach (KeyValuePair<string, PendingRequest> pair in _pending)
            {
                if (nowSeconds >= pair.Value.DeadlineSeconds)
                {
                    if (expired == null)
                    {
                        expired = new List<PendingRequest>();
                    }
                    expired.Add(pair.Value);
                }
            }
            if (expired == null)
            {
                return new List<PendingRequest>();
            }
            foreach (PendingRequest request in expired)
            {
                _pending.Remove(request.RequestId);
                if (request.Callback != null)
                {
                    request.Callback(null);
                }
            }
            return expired;
        }

        /// <summary>Drops all pending requests without callbacks (process restart).</summary>
        public void Clear()
        {
            _pending.Clear();
        }
    }
}
