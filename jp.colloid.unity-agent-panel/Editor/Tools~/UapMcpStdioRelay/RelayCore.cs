using System;
using System.Collections.Generic;
using System.Threading;

namespace Colloid.AgentPanel.Tools.McpStdioRelay
{
    /// <summary>
    /// One outbound HTTP request RelayCore wants made against the UapOps
    /// endpoint. Built fresh per stdin line; <see cref="Body"/> carries the
    /// raw JSON-RPC text the agent wrote to stdin UNCHANGED -- RelayCore
    /// never parses or re-serializes it (see <see cref="RelayCore"/>'s own
    /// doc comment for why: the real server already answers a malformed
    /// body with a proper JSON-RPC parse-error response, so this relay has
    /// no need to understand JSON-RPC at all).
    /// </summary>
    public sealed class RelayRequest
    {
        public string Url;
        public string Body;
        public string BearerToken;

        /// <summary>Mcp-Session-Id to send, or null/empty before one has been learned from a response.</summary>
        public string SessionId;
    }

    /// <summary>
    /// The result of one HTTP round trip, already reduced to what
    /// RelayCore needs. A genuine transport-level failure (connect
    /// refused, timeout, DNS, ...) is reported through
    /// <see cref="TransportError"/> -- StatusCode/Body are meaningless
    /// then -- so RelayCore has exactly one failure path to check,
    /// regardless of whether the failure happened before or after an HTTP
    /// status line existed.
    /// </summary>
    public sealed class RelayResponse
    {
        public int StatusCode;
        public string ContentType = string.Empty;
        public string Body = string.Empty;

        /// <summary>The Mcp-Session-Id response header, or null when the response carried none.</summary>
        public string SessionId;

        /// <summary>Non-null when the transport itself failed; StatusCode/Body/ContentType are then meaningless.</summary>
        public string TransportError;
    }

    /// <summary>
    /// Makes one HTTP round trip for <see cref="RelayCore"/>. Injectable so
    /// a test can exercise RelayCore end to end without a real socket
    /// (design note docs/design-notes/2026-09-22-uloop-phase1.md section
    /// 2.7 item 2); RelayMain's production wiring backs this with a real
    /// HttpClient POST. MUST NOT throw -- a failure is reported through
    /// <see cref="RelayResponse.TransportError"/>, never an exception, so
    /// RelayCore's own try/catch around the call is pure defense in depth,
    /// not the primary contract.
    /// </summary>
    public delegate RelayResponse RelayTransport(RelayRequest request);

    /// <summary>
    /// Pure stdio&lt;-&gt;HTTP relay core for the UapOps MCP endpoint (design
    /// note docs/design-notes/2026-09-22-uloop-phase1.md section 2.7): the
    /// one job uLoop's Go CLI plays for THIS panel's other MCP servers is
    /// "forward the JSON-RPC line an ACP agent wrote to stdin to UapOps
    /// over HTTP, and write whatever comes back to stdout" -- nothing
    /// else. This class does not parse the JSON-RPC payload it carries at
    /// all: the real server (UapOpsRequestHandler) already answers a
    /// malformed request with a spec-correct JSON-RPC parse-error
    /// response, so a dumb byte pipe is both simpler and MORE correct than
    /// a relay that tries to interpret ids/methods itself.
    ///
    /// No Unity dependency: a Tools~ folder is invisible to Unity's Asset
    /// Database/compiler, and this file is instead compiled by
    /// UapStdioRelayBuilder directly against Unity's OWN bundled Roslyn +
    /// dotnet runtime, entirely outside the package's own asmdef -- so it
    /// also compiles verbatim into ci/SmokeTests (net8, LangVersion 9).
    ///
    /// Threading: <see cref="HandleLine"/> is called once per stdin line,
    /// synchronously, by whatever reads stdin (RelayMain's main loop).
    /// Every request is dispatched onto its own pool thread immediately so
    /// a slow tool call never blocks the next line from being read -- the
    /// ACP agent may have several tool calls in flight, or a notification
    /// queued right behind a request. <see cref="_stdoutLock"/> serializes
    /// the actual stdout writes so two completions racing each other can
    /// never interleave a partial line. <see cref="WaitForInFlight"/> is
    /// how the EOF path (RelayMain, stdin closed) gives outstanding
    /// requests a bounded chance to finish before the process exits.
    /// </summary>
    public sealed class RelayCore
    {
        private readonly string _url;
        private readonly string _bearerToken;
        private readonly RelayTransport _transport;
        private readonly Action<string> _writeStdout;
        private readonly Action<string> _log;
        private readonly object _stdoutLock = new object();
        private readonly object _sessionLock = new object();
        private string _sessionId;

        private readonly object _inFlightLock = new object();
        private int _inFlightCount;

        // Signalled whenever _inFlightCount reaches 0; WaitForInFlight
        // waits on it instead of polling because the shutdown grace period
        // (RelayMain) is short and bounded either way.
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);

        public RelayCore(string url, string bearerToken, RelayTransport transport,
            Action<string> writeStdout, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentException("url is required.", "url");
            }
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }
            if (writeStdout == null)
            {
                throw new ArgumentNullException("writeStdout");
            }
            _url = url;
            _bearerToken = bearerToken ?? string.Empty;
            _transport = transport;
            _writeStdout = writeStdout;
            _log = log;
        }

        /// <summary>The Mcp-Session-Id learned from the server's last response that carried one (diagnostics/tests).</summary>
        public string CurrentSessionId
        {
            get { lock (_sessionLock) { return _sessionId; } }
        }

        /// <summary>Requests forwarded to the transport and not yet resolved (diagnostics/tests).</summary>
        public int InFlightCount
        {
            get { lock (_inFlightLock) { return _inFlightCount; } }
        }

        /// <summary>
        /// Handles one stdin line. A blank/whitespace-only line is dropped
        /// silently (common between JSON-RPC messages on some writers);
        /// anything else -- valid JSON-RPC or not -- is forwarded AS-IS:
        /// see the class doc comment for why RelayCore never tries to
        /// parse it itself. Returns immediately; the HTTP round trip and
        /// any stdout write happen on a pool thread, so many lines can be
        /// in flight at once (the class doc's threading note).
        /// </summary>
        public void HandleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            BeginInFlight();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Forward(line);
                }
                catch (Exception ex)
                {
                    Log("Relay request failed: " + ex.Message);
                }
                finally
                {
                    EndInFlight();
                }
            });
        }

        /// <summary>
        /// Blocks up to <paramref name="timeout"/> for every request
        /// dispatched so far to finish (RelayMain calls this once stdin
        /// hits EOF, before the process exits, so a reply already on the
        /// wire is not silently dropped just because the agent closed the
        /// pipe). Returns false on timeout -- the caller exits anyway; a
        /// relay outliving its parent forever would be worse than losing
        /// one straggler's reply.
        /// </summary>
        public bool WaitForInFlight(TimeSpan timeout)
        {
            return _idle.Wait(timeout);
        }

        private void BeginInFlight()
        {
            lock (_inFlightLock)
            {
                _inFlightCount++;
                _idle.Reset();
            }
        }

        private void EndInFlight()
        {
            lock (_inFlightLock)
            {
                _inFlightCount--;
                if (_inFlightCount <= 0)
                {
                    _inFlightCount = 0;
                    _idle.Set();
                }
            }
        }

        private void Forward(string line)
        {
            string sessionId;
            lock (_sessionLock)
            {
                sessionId = _sessionId;
            }
            RelayResponse response;
            try
            {
                response = _transport(new RelayRequest
                {
                    Url = _url,
                    Body = line,
                    BearerToken = _bearerToken,
                    SessionId = sessionId
                });
            }
            catch (Exception ex)
            {
                // Contract violation (RelayTransport is documented not to
                // throw) -- treated exactly like a reported TransportError:
                // logged, nothing written to stdout, no crash.
                Log("Transport threw (nothing written to stdout): " + ex.Message);
                return;
            }
            if (response == null)
            {
                Log("Transport returned null for a request; nothing written to stdout.");
                return;
            }
            if (!string.IsNullOrEmpty(response.SessionId))
            {
                lock (_sessionLock)
                {
                    _sessionId = response.SessionId;
                }
            }
            if (!string.IsNullOrEmpty(response.TransportError))
            {
                Log("Request failed: " + response.TransportError);
                return;
            }
            if (response.StatusCode == 202)
            {
                // MCP Streamable HTTP: 202 Accepted means "no synchronous
                // reply" (the server's own answer to a notification, or to
                // an id it will never resolve here) -- nothing to write,
                // by design, not a failure.
                return;
            }
            string contentType = response.ContentType ?? string.Empty;
            if (contentType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                List<string> messages = ParseSseMessages(response.Body);
                for (int i = 0; i < messages.Count; i++)
                {
                    WriteStdoutLine(messages[i]);
                }
                return;
            }
            if (!string.IsNullOrEmpty(response.Body))
            {
                WriteStdoutLine(response.Body);
            }
        }

        private void WriteStdoutLine(string message)
        {
            // A JSON-RPC message never legitimately contains a raw newline
            // (compact serialization escapes them inside strings); this is
            // a defensive normalization so a malformed/oddly-wrapped
            // upstream body can never split into more stdout lines than it
            // should and desynchronize the agent's own line reader.
            string safe = (message ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            lock (_stdoutLock)
            {
                _writeStdout(safe);
            }
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }

        /// <summary>
        /// Splits an SSE response body into the JSON-RPC message text of
        /// each event (per the SSE spec: consecutive "data:" lines within
        /// one event are joined with '\n', a blank line ends the event;
        /// any other field -- "event:", "id:", "retry:", a ":" comment --
        /// carries no JSON-RPC payload of its own and is ignored, since
        /// the MCP Streamable HTTP transport never needs them). Pure and
        /// internal so a smoke/EditMode test can pin it directly. Never
        /// throws on malformed input: worst case is an empty list.
        /// </summary>
        internal static List<string> ParseSseMessages(string body)
        {
            var messages = new List<string>();
            if (string.IsNullOrEmpty(body))
            {
                return messages;
            }
            string normalized = body.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            var current = new List<string>();
            for (int i = 0; i <= lines.Length; i++)
            {
                bool isEventBoundary = i == lines.Length || lines[i].Length == 0;
                if (isEventBoundary)
                {
                    if (current.Count > 0)
                    {
                        messages.Add(string.Join("\n", current.ToArray()));
                        current.Clear();
                    }
                    continue;
                }
                string l = lines[i];
                if (l.Length >= 5 && l.Substring(0, 5) == "data:")
                {
                    string data = l.Substring(5);
                    if (data.Length > 0 && data[0] == ' ')
                    {
                        data = data.Substring(1);
                    }
                    current.Add(data);
                }
            }
            return messages;
        }
    }
}
