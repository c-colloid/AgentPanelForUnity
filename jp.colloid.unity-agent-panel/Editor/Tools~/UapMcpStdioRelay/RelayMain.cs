using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace Colloid.AgentPanel.Tools.McpStdioRelay
{
    /// <summary>
    /// Entry point for the compiled stdio-to-HTTP relay (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 2.7).
    /// UapStdioRelayBuilder compiles this file together with RelayCore.cs
    /// with Unity's own bundled Roslyn/dotnet and hands the ACP agent a
    /// launch spec equivalent to running:
    /// <c>&lt;Unity's dotnet host&gt; exec &lt;compiled relay.dll&gt; &lt;UapOps MCP URL&gt;</c>.
    /// The agent becomes this process's PARENT (it owns the pipe ends for
    /// stdin/stdout), exactly the role uLoop's Go CLI plays for that
    /// agent's other MCP servers.
    ///
    /// The bearer token travels as the UAP_MCP_TOKEN environment variable
    /// ONLY (set by AcpProtocolBridge.BuildStdioServerJson in the `env`
    /// array of the McpServer::Stdio config the agent spawns this process
    /// from) -- never a command-line argument, for the same reason
    /// UapOpsMcpConfig writes the http path's token to a FILE instead of
    /// an inline --mcp-config string (SEC-2: a process's argv is readable
    /// by every local process/user via /proc/*/cmdline or Task Manager
    /// regardless of umask; an environment variable set on a child this
    /// process's own parent spawned is not exposed that way).
    ///
    /// Not compiled into ci/SmokeTests (only RelayCore.cs is -- see that
    /// project's own comment): this file needs a real Main and real
    /// network I/O, neither of which the license-free smoke harness wants
    /// or needs to exercise; RelayCore's own smoke checks cover the
    /// forwarding logic through an injected transport instead.
    /// </summary>
    public static class RelayMain
    {
        /// <summary>Bounded so a stuck agent-side pipe cannot hang the process forever once stdin closes.</summary>
        private const int ShutdownGraceMillis = 5000;

        public static int Main(string[] args)
        {
            if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0]))
            {
                Console.Error.WriteLine("UapMcpStdioRelay: expected one argument, the UapOps MCP URL.");
                return 2;
            }
            string url = args[0];
            string token = Environment.GetEnvironmentVariable("UAP_MCP_TOKEN") ?? string.Empty;

            using (HttpClient http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                RelayTransport transport = delegate(RelayRequest request)
                {
                    return SendHttp(http, request);
                };
                object stdoutLock = new object();
                Action<string> writeStdout = delegate(string line)
                {
                    // Belt-and-braces on top of RelayCore's own
                    // _stdoutLock: Console's writer is not documented to be
                    // atomic across two WriteLine calls racing from
                    // different threads on every platform/runtime, and
                    // getting this wrong would corrupt the agent's JSON-RPC
                    // framing.
                    lock (stdoutLock)
                    {
                        Console.Out.Write(line);
                        Console.Out.Write('\n');
                        Console.Out.Flush();
                    }
                };
                RelayCore core = new RelayCore(url, token, transport, writeStdout, LogToStderr);

                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    core.HandleLine(line);
                }
                // EOF (the agent closed its write end, e.g. it is shutting
                // down): give in-flight requests a bounded chance to land
                // before this process exits with them.
                core.WaitForInFlight(TimeSpan.FromMilliseconds(ShutdownGraceMillis));
            }
            return 0;
        }

        private static void LogToStderr(string message)
        {
            try
            {
                Console.Error.WriteLine("[UapMcpStdioRelay] " + message);
            }
            catch (Exception)
            {
                // stderr itself can be gone if the parent already exited;
                // never let logging crash the relay.
            }
        }

        private static RelayResponse SendHttp(HttpClient http, RelayRequest request)
        {
            RelayResponse response = new RelayResponse();
            try
            {
                using (HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, request.Url))
                {
                    httpRequest.Content = new StringContent(request.Body ?? string.Empty, Encoding.UTF8, "application/json");
                    httpRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                    if (!string.IsNullOrEmpty(request.BearerToken))
                    {
                        httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + request.BearerToken);
                    }
                    if (!string.IsNullOrEmpty(request.SessionId))
                    {
                        httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", request.SessionId);
                    }
                    // Synchronous wait is fine here: this call already runs
                    // on its own ThreadPool work item (RelayCore.HandleLine),
                    // never the stdin-reading thread.
                    using (HttpResponseMessage httpResponse =
                        http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                    {
                        response.StatusCode = (int)httpResponse.StatusCode;
                        response.ContentType = httpResponse.Content != null && httpResponse.Content.Headers.ContentType != null
                            ? (httpResponse.Content.Headers.ContentType.MediaType ?? string.Empty)
                            : string.Empty;
                        response.Body = httpResponse.Content != null
                            ? httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            : string.Empty;
                        IEnumerable<string> sessionValues;
                        if (httpResponse.Headers.TryGetValues("Mcp-Session-Id", out sessionValues))
                        {
                            foreach (string value in sessionValues)
                            {
                                response.SessionId = value;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                response.TransportError = ex.GetType().Name + ": " + ex.Message;
            }
            return response;
        }
    }
}
