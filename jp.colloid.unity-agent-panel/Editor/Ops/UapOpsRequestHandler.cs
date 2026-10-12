using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Pure JSON-RPC 2.0 / MCP Streamable HTTP request handler for the
    /// UapOps server (design section 1.1/8.1, docs/research/
    /// 08-mcp-transport.md section 5's exact measured CLI sequence: POST
    /// initialize / notifications/initialized / GET-&gt;405 / tools/list /
    /// tools/call). Bytes/JsonNode in, response out -- no HttpListener, no
    /// threading of its own (tool execution goes through the injected
    /// IUapToolExecutor, letting tests substitute DirectUapToolExecutor and
    /// the real server substitute UapMainThreadDispatcher). Unit tested
    /// directly by UapOpsRequestHandlerTests using the R08 section 5
    /// sequence as fixtures.
    /// </summary>
    public sealed class UapOpsRequestHandler
    {
        /// <summary>
        /// protocolVersion this server advertises on "initialize". P2
        /// (design section 8.1) measured the real CLI (v2.1.220) requesting
        /// 2025-11-25 but correctly negotiating down to a server that
        /// answers 2025-06-18 -- serve the OLDER, verified-accepted value
        /// rather than guess at the newer one.
        /// </summary>
        public const string ProtocolVersion = "2025-06-18";

        public const string ServerInfoName = "unity-uap-ops";
        public const string ServerInfoVersion = "0.12.0";

        // The two endpoint paths themselves are UapToolListModePolicy's
        // (single source of truth also read by AgentHub when it builds the
        // URL a spawn connects to) -- see that class's own doc comment.
        // Compact-mode tools/list differs from full only in what
        // HandleToolsList returns; tools/call is UNCHANGED by which path a
        // client connects on: a direct call of any enabled tool by its own
        // name keeps working on either path, and uap_call/uap_tools (see
        // HandleToolsCall) are recognized by NAME regardless of path, so a
        // full-mode client that somehow learns the gateway names (they are
        // simply never advertised to it) could use them too -- there is no
        // separate code path to keep in sync.

        private readonly ToolRegistry _registry;
        private readonly Func<string> _tokenProvider;
        private readonly Func<IEnumerable<string>> _enabledModulesProvider;
        private readonly IUapToolExecutor _executor;
        private readonly Func<string> _sessionIdFactory;
        private readonly Func<string> _noticeProvider;

        private string _sessionId;

        /// <summary>
        /// <paramref name="noticeProvider"/> (optional): returns a warning
        /// to append as an extra text block to every SUCCESSFUL tools/call
        /// result while it is non-empty -- the UapOps server supplies the
        /// "Play Mode is running and the Editor is unfocused, calls may be
        /// slow" notice (design note 2026-09-06 section 7), the same
        /// courtesy uloop's own tools extend. Null/empty = nothing appended.
        /// </summary>
        public UapOpsRequestHandler(ToolRegistry registry, Func<string> tokenProvider,
            Func<IEnumerable<string>> enabledModulesProvider, IUapToolExecutor executor,
            Func<string> sessionIdFactory = null, Func<string> noticeProvider = null)
        {
            _noticeProvider = noticeProvider;
            if (registry == null) throw new ArgumentNullException("registry");
            if (tokenProvider == null) throw new ArgumentNullException("tokenProvider");
            if (enabledModulesProvider == null) throw new ArgumentNullException("enabledModulesProvider");
            if (executor == null) throw new ArgumentNullException("executor");
            _registry = registry;
            _tokenProvider = tokenProvider;
            _enabledModulesProvider = enabledModulesProvider;
            _executor = executor;
            _sessionIdFactory = sessionIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        }

        /// <summary>The Mcp-Session-Id issued by the most recent successful "initialize", or null before one has happened.</summary>
        public string CurrentSessionId
        {
            get { return _sessionId; }
        }

        /// <summary>
        /// Handles one HTTP request to the UapOps endpoint. Never throws --
        /// any unexpected exception surfaces as a 500 with no body (the
        /// caller/server layer logs it; design section 1.3's "try/catch so
        /// a tool error never crashes the editor" extends to the handler
        /// itself).
        /// </summary>
        public UapOpsHttpResponse Handle(UapOpsHttpRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            try
            {
                return HandleCore(request);
            }
            catch (Exception)
            {
                return UapOpsHttpResponse.StatusOnly(500);
            }
        }

        private UapOpsHttpResponse HandleCore(UapOpsHttpRequest request)
        {
            string path = NormalizePath(request.Path);
            bool compact;
            if (string.Equals(path, UapToolListModePolicy.McpPath, StringComparison.Ordinal))
            {
                compact = false;
            }
            else if (string.Equals(path, UapToolListModePolicy.McpCompactPath, StringComparison.Ordinal))
            {
                compact = true;
            }
            else
            {
                return UapOpsHttpResponse.StatusOnly(404);
            }

            // GET/DELETE: no server-initiated SSE stream implemented, and no
            // session-termination endpoint -- both spec-compliant 405s (R08
            // section 5 measured the real CLI tolerating exactly this on
            // GET; DELETE is symmetric and never exercised by the CLI).
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                return UapOpsHttpResponse.StatusOnly(405);
            }

            string expectedToken = _tokenProvider();
            if (!UapOpsAuth.IsAuthorized(request.AuthorizationHeader, expectedToken))
            {
                JsonNode idForAuthError = TryExtractId(request.Body);
                return JsonResponse(401, JsonRpcError(idForAuthError, -32001,
                    "Unauthorized: missing or invalid bearer token"));
            }

            JsonNode root;
            string parseError;
            if (!JsonParser.TryParse(request.Body ?? string.Empty, out root, out parseError) || !root.IsObject)
            {
                return JsonResponse(200, JsonRpcError(JsonNode.Null, -32700, "Parse error"));
            }

            string method = root["method"].AsString(string.Empty);
            JsonNode id = root["id"];
            bool isNotification = !root.HasKey("id");

            switch (method)
            {
                case "initialize":
                    return HandleInitialize(id);
                case "notifications/initialized":
                    return UapOpsHttpResponse.StatusOnly(202);
                case "tools/list":
                    return HandleToolsList(id, compact);
                case "tools/call":
                    return HandleToolsCall(id, root["params"]);
                default:
                    if (isNotification)
                    {
                        // Forward compatibility: unrecognized notifications
                        // are acknowledged, never errored (mirrors
                        // AgentClient's own "unknown inbound -> ignored"
                        // rule for the CLI's own protocol).
                        return UapOpsHttpResponse.StatusOnly(202);
                    }
                    return JsonResponse(200, JsonRpcError(id, -32601, "Method not found: " + method));
            }
        }

        private UapOpsHttpResponse HandleInitialize(JsonNode id)
        {
            _sessionId = _sessionIdFactory();
            JsonNode result = JsonNode.NewObject()
                .Set("protocolVersion", ProtocolVersion)
                .Set("capabilities", JsonNode.NewObject().Set("tools", JsonNode.NewObject()))
                .Set("serverInfo", JsonNode.NewObject()
                    .Set("name", ServerInfoName)
                    .Set("version", ServerInfoVersion));
            UapOpsHttpResponse response = JsonResponse(200, JsonRpcResult(id, result));
            response.McpSessionId = _sessionId;
            return response;
        }

        /// <summary>
        /// Full mode: every enabled tool, unchanged since before compact
        /// mode existed. Compact mode (design note docs/design-notes/
        /// 2026-09-22-uloop-phase1.md section 2.8 B): only the enabled
        /// tools of <see cref="UapToolGateway.DirectlyListedModule"/>
        /// ("core") are listed with full descriptions, plus the two
        /// gateway entries -- NEVER the other way around, so a full-mode
        /// tools/list can never show uap_tools/uap_call (they are added
        /// only inside the `compact` branch below). Compact entries carry
        /// the LEAN schema (<see cref="UapLeanSchema"/>: one-line field
        /// descriptions, every structural keyword intact; design note
        /// 2026-10-02-session-context-cost.md section 4); full mode and the
        /// uap_help / uap_tools "names" detail keep the tool's schema as is,
        /// and descriptions are never trimmed in either mode.
        /// </summary>
        private UapOpsHttpResponse HandleToolsList(JsonNode id, bool compact)
        {
            JsonNode toolsArray = JsonNode.NewArray();
            IEnumerable<string> enabledModules = _enabledModulesProvider();
            List<IUapTool> listed = _registry.ListEnabled(compact ? DirectlyListedModules(enabledModules) : enabledModules);
            for (int i = 0; i < listed.Count; i++)
            {
                IUapTool tool = listed[i];
                toolsArray.Add(JsonNode.NewObject()
                    .Set("name", tool.Name)
                    .Set("description", tool.Description)
                    .Set("inputSchema", compact ? UapLeanSchema.Lean(tool.InputSchema) : tool.InputSchema));
            }
            if (compact)
            {
                toolsArray.Add(UapToolGateway.ToolsListingEntry());
                toolsArray.Add(UapToolGateway.CallListingEntry());
            }
            return JsonResponse(200, JsonRpcResult(id, JsonNode.NewObject().Set("tools", toolsArray)));
        }

        /// <summary><see cref="UapToolGateway.DirectlyListedModule"/> alone, when it is actually enabled -- else empty (nothing listed directly, everything through the gateway).</summary>
        private static List<string> DirectlyListedModules(IEnumerable<string> enabledModules)
        {
            var result = new List<string>();
            if (enabledModules != null)
            {
                foreach (string module in enabledModules)
                {
                    if (string.Equals(module, UapToolGateway.DirectlyListedModule, StringComparison.Ordinal))
                    {
                        result.Add(module);
                        break;
                    }
                }
            }
            return result;
        }

        private UapOpsHttpResponse HandleToolsCall(JsonNode id, JsonNode paramsNode)
        {
            string toolName = paramsNode["name"].AsString(string.Empty);
            JsonNode input = paramsNode["arguments"];
            if (input == null || input.IsNull)
            {
                input = JsonNode.NewObject();
            }

            // uap_tools runs right here -- it only reads the registry (pure
            // C#, no Unity API), so there is nothing for the main-thread
            // dispatcher to marshal. Wrapped through the SAME success/
            // notice/error path as every other tool below (SuccessResult),
            // just fed from UapToolGateway instead of _executor.Execute.
            if (string.Equals(toolName, UapToolGateway.ToolsToolName, StringComparison.Ordinal))
            {
                try
                {
                    return SuccessResult(id, UapToolGateway.ExecuteTools(_registry, _enabledModulesProvider(), input));
                }
                catch (Exception ex)
                {
                    return JsonResponse(200, JsonRpcResult(id, ToolErrorResult(ex.Message)));
                }
            }

            // uap_call: unwrapped HERE, before both the module-enabled
            // check and the executor dispatch below, so the INNER tool
            // runs exactly as if the client had called it directly by name
            // -- a pollable/off-thread tool reached through uap_call still
            // goes through _executor.Execute itself rather than nesting
            // inside another tool's Execute, which would deadlock a
            // pollable like uap_scripts_commit (the dispatcher can only
            // service one tool invocation's ticks at a time). Neither
            // "uap_call" nor "uap_tools" is ever registered in _registry
            // (see UapToolGateway's class doc comment), so an inner name of
            // "uap_call" (a self-call) or "uap_tools" falls straight
            // through to the ordinary "Unknown tool" branch just below
            // instead of ever recursing.
            if (string.Equals(toolName, UapToolGateway.CallToolName, StringComparison.Ordinal))
            {
                string innerName;
                JsonNode innerArguments;
                if (!UapToolGateway.TryUnwrapCall(input, out innerName, out innerArguments))
                {
                    return JsonResponse(200, JsonRpcResult(id, ToolErrorResult(
                        "uap_call requires 'name' (the target tool's wire name) and optionally 'arguments'.")));
                }
                toolName = innerName;
                input = innerArguments;
            }

            IUapTool tool = _registry.Find(toolName);
            if (tool == null)
            {
                return JsonResponse(200, JsonRpcResult(id, ToolErrorResult("Unknown tool: " + toolName)));
            }

            // Defense in depth: a disabled-module tool must never execute
            // even if a client somehow calls it by name after a module was
            // turned off (e.g. a stale tools/list cached client-side) --
            // tools/list already filters it out, this is the enforcement
            // point.
            bool moduleEnabled = false;
            foreach (string module in _enabledModulesProvider())
            {
                if (string.Equals(module, tool.Module, StringComparison.Ordinal))
                {
                    moduleEnabled = true;
                    break;
                }
            }
            if (!moduleEnabled)
            {
                return JsonResponse(200, JsonRpcResult(id,
                    ToolErrorResult("Tool module '" + tool.Module + "' is disabled.")));
            }

            try
            {
                return SuccessResult(id, _executor.Execute(tool, input));
            }
            catch (Exception ex)
            {
                return JsonResponse(200, JsonRpcResult(id, ToolErrorResult(ex.Message)));
            }
        }

        /// <summary>
        /// Shared success-wrapping tail for a tools/call result content
        /// array, whichever of the two producers built it (a registered
        /// tool via <see cref="IUapToolExecutor.Execute"/>, or
        /// UapToolGateway.ExecuteTools for uap_tools) -- both get the same
        /// null-content normalization and the same stall/throttle notice
        /// appended to a SUCCESSFUL result (see the constructor's
        /// noticeProvider doc comment).
        /// </summary>
        private UapOpsHttpResponse SuccessResult(JsonNode id, JsonNode content)
        {
            if (content == null)
            {
                content = JsonNode.NewArray();
            }
            string notice = ReadNotice();
            if (notice != null && content.IsArray)
            {
                content.Add(JsonNode.NewObject()
                    .Set("type", "text")
                    .Set("text", "Warning: " + notice));
            }
            JsonNode successResult = JsonNode.NewObject()
                .Set("content", content)
                .Set("isError", false);
            return JsonResponse(200, JsonRpcResult(id, successResult));
        }

        /// <summary>Trims one trailing '/' (e.g. "/mcp/" -&gt; "/mcp"); empty/null normalizes to "/". Defensive -- the CLI itself never sends a trailing slash for the configured URL, but callers building requests by hand (tests, any future transport) should not have to worry about it.</summary>
        private static string NormalizePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                return "/";
            }
            string trimmed = rawPath.Length > 1 ? rawPath.TrimEnd('/') : rawPath;
            return string.IsNullOrEmpty(trimmed) ? "/" : trimmed;
        }

        private string ReadNotice()
        {
            if (_noticeProvider == null)
            {
                return null;
            }
            try
            {
                string notice = _noticeProvider();
                return string.IsNullOrEmpty(notice) ? null : notice;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static JsonNode ToolErrorResult(string message)
        {
            return JsonNode.NewObject()
                .Set("content", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("type", "text").Set("text", message)))
                .Set("isError", true);
        }

        private static JsonNode TryExtractId(string body)
        {
            JsonNode root;
            string error;
            if (!string.IsNullOrEmpty(body) && JsonParser.TryParse(body, out root, out error) && root.IsObject)
            {
                return root["id"];
            }
            return JsonNode.Null;
        }

        private static JsonNode JsonRpcResult(JsonNode id, JsonNode result)
        {
            return JsonNode.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", NormalizeId(id))
                .Set("result", result);
        }

        private static JsonNode JsonRpcError(JsonNode id, int code, string message)
        {
            return JsonNode.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", NormalizeId(id))
                .Set("error", JsonNode.NewObject().Set("code", code).Set("message", message));
        }

        private static JsonNode NormalizeId(JsonNode id)
        {
            return id ?? JsonNode.Null;
        }

        private static UapOpsHttpResponse JsonResponse(int statusCode, JsonNode payload)
        {
            return new UapOpsHttpResponse
            {
                StatusCode = statusCode,
                ContentType = "application/json",
                Body = JsonWriter.Write(payload)
            };
        }
    }
}
