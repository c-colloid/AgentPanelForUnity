using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Owns the HttpListener on 127.0.0.1:&lt;dynamic port&gt; and bridges
    /// each request to UapOpsRequestHandler. The accept loop is
    /// CALLBACK-based (EndGetContext -&gt; immediately re-arm
    /// BeginGetContext) per docs/research/08-mcp-transport.md section 5's
    /// measured pitfall: a poll/timeout accept loop orphans in-flight
    /// accepts and hangs the client forever (symptom actually reproduced
    /// during the design-gate probe: "client hangs, server log empty,
    /// `claude mcp get` reports Unexpected content type: null"). This class
    /// never itself touches a Unity API -- HttpListener worker threads only
    /// ever call UapOpsRequestHandler.Handle, which marshals tool execution
    /// through the injected IUapToolExecutor (UapMainThreadDispatcher in
    /// production).
    ///
    /// HTTP-socket integration tests are intentionally NOT part of the
    /// EditMode suite (thread/timing, per the phase brief) -- the
    /// request/response mapping this class is a thin wrapper around is
    /// covered directly by UapOpsRequestHandlerTests instead.
    /// </summary>
    public sealed class UapOpsHttpServer : IDisposable
    {
        private readonly UapOpsRequestHandler _handler;
        private readonly Action<string> _logger;
        private HttpListener _listener;
        private volatile bool _running;

        public UapOpsHttpServer(UapOpsRequestHandler handler, Action<string> logger = null)
        {
            if (handler == null)
            {
                throw new ArgumentNullException("handler");
            }
            _handler = handler;
            _logger = logger;
        }

        /// <summary>OS-assigned port this server is listening on, or 0 before Start()/after Stop().</summary>
        public int Port { get; private set; }

        /// <summary>True once Start() has succeeded and Stop()/Dispose() has not since been called.</summary>
        public bool IsListening
        {
            get { return _running && _listener != null && _listener.IsListening; }
        }

        /// <summary>Starts listening on 127.0.0.1 at an OS-assigned free port. Throws on failure (caller decides fallback/retry, design section 6 risk table).</summary>
        public void Start()
        {
            if (_running)
            {
                return;
            }
            int port = PickFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();
            _listener = listener;
            Port = port;
            _running = true;
            ArmAccept();
        }

        /// <summary>Stops listening. Safe to call when not running.</summary>
        public void Stop()
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex)
            {
                Log("Stop failed: " + ex.Message);
            }
            _listener = null;
            Port = 0;
        }

        public void Dispose()
        {
            Stop();
        }

        private static int PickFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private void ArmAccept()
        {
            if (!_running)
            {
                return;
            }
            try
            {
                _listener.BeginGetContext(OnAccept, null);
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Log("BeginGetContext failed: " + ex.Message);
                }
            }
        }

        private void OnAccept(IAsyncResult ar)
        {
            if (!_running)
            {
                return;
            }
            HttpListenerContext ctx = null;
            try
            {
                ctx = _listener.EndGetContext(ar);
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Log("EndGetContext failed: " + ex.Message);
                }
            }
            // Re-arm BEFORE handling the request so a slow/blocked handler
            // (tools/call waiting on the main thread) never stalls
            // acceptance of the NEXT connection -- the exact fix for the
            // measured poll-loop pitfall this class's doc comment
            // describes.
            ArmAccept();
            if (ctx != null)
            {
                try
                {
                    HandleContext(ctx);
                }
                catch (Exception ex)
                {
                    Log("HandleContext failed: " + ex);
                }
            }
        }

        /// <summary>
        /// SEC-3: maximum accepted request body size. Body buffering happens
        /// BEFORE authentication (the Bearer check lives inside
        /// UapOpsRequestHandler.Handle), so without a bound any local
        /// process could POST an arbitrarily large body and balloon the
        /// Unity Editor's memory straight into an OOM crash -- no token
        /// needed. 4 MB is far above any real MCP JSON-RPC payload this
        /// server sees (tool calls, initialize handshakes; even a whole
        /// script file inside a tool argument is a fraction of this) while
        /// keeping the worst-case pre-auth allocation harmless.
        /// </summary>
        public const int MaxRequestBodyBytes = 4 * 1024 * 1024;

        private void HandleContext(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            // Fast path: an honestly declared oversized Content-Length is
            // rejected without reading a byte. A client that lies (or
            // streams chunked) is caught by the bounded reader below --
            // this check is an optimization, not the guard.
            if (req.ContentLength64 > MaxRequestBodyBytes)
            {
                WriteResponse(ctx.Response, UapOpsHttpResponse.StatusOnly(413));
                return;
            }
            bool overLimit;
            string body;
            using (Stream input = req.InputStream)
            {
                body = ReadBodyBounded(input, req.ContentEncoding, MaxRequestBodyBytes, out overLimit);
            }
            if (overLimit)
            {
                Log("Request body exceeded " + MaxRequestBodyBytes + " bytes; rejected with 413.");
                WriteResponse(ctx.Response, UapOpsHttpResponse.StatusOnly(413));
                return;
            }
            string path = req.Url.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
            var uapRequest = new UapOpsHttpRequest
            {
                Method = req.HttpMethod,
                Path = path,
                AuthorizationHeader = req.Headers["Authorization"],
                Body = body
            };

            UapOpsHttpResponse response;
            try
            {
                response = _handler.Handle(uapRequest);
            }
            catch (Exception ex)
            {
                Log("Handler threw: " + ex);
                response = UapOpsHttpResponse.StatusOnly(500);
            }
            WriteResponse(ctx.Response, response);
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end, refusing to buffer
        /// more than <paramref name="maxBytes"/>: the moment the cumulative
        /// size would exceed the limit, sets
        /// <paramref name="overLimit"/> and returns null WITHOUT reading
        /// further (an attacker's remaining gigabytes are never pulled off
        /// the socket). Decoding goes through a StreamReader over the
        /// buffered bytes so BOM handling is byte-identical to the previous
        /// direct StreamReader-on-InputStream path. Internal seam: unit
        /// tested with a MemoryStream, no socket involved (HTTP-socket
        /// tests are deliberately out of the EditMode suite, per the class
        /// doc comment).
        /// </summary>
        internal static string ReadBodyBounded(Stream stream, Encoding encoding, int maxBytes, out bool overLimit)
        {
            overLimit = false;
            var buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + read > maxBytes)
                    {
                        overLimit = true;
                        return null;
                    }
                    ms.Write(buffer, 0, read);
                }
                ms.Position = 0;
                using (var reader = new StreamReader(ms, encoding ?? Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static void WriteResponse(HttpListenerResponse res, UapOpsHttpResponse response)
        {
            res.StatusCode = response.StatusCode;
            if (!string.IsNullOrEmpty(response.McpSessionId))
            {
                res.Headers["Mcp-Session-Id"] = response.McpSessionId;
            }
            if (!string.IsNullOrEmpty(response.Body))
            {
                res.ContentType = response.ContentType ?? "application/json";
                byte[] bytes = Encoding.UTF8.GetBytes(response.Body);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            res.OutputStream.Close();
        }

        private void Log(string message)
        {
            if (_logger != null)
            {
                _logger("[UapOps] " + message);
            }
        }
    }
}
