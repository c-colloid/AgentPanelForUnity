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

        private const string McpPath = "/mcp";

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
            if (!string.Equals(path, McpPath, StringComparison.Ordinal))
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
                    return HandleToolsList(id);
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

        private UapOpsHttpResponse HandleToolsList(JsonNode id)
        {
            JsonNode toolsArray = JsonNode.NewArray();
            List<IUapTool> enabled = _registry.ListEnabled(_enabledModulesProvider());
            for (int i = 0; i < enabled.Count; i++)
            {
                IUapTool tool = enabled[i];
                toolsArray.Add(JsonNode.NewObject()
                    .Set("name", tool.Name)
                    .Set("description", tool.Description)
                    .Set("inputSchema", tool.InputSchema));
            }
            return JsonResponse(200, JsonRpcResult(id, JsonNode.NewObject().Set("tools", toolsArray)));
        }

        private UapOpsHttpResponse HandleToolsCall(JsonNode id, JsonNode paramsNode)
        {
            string toolName = paramsNode["name"].AsString(string.Empty);
            JsonNode input = paramsNode["arguments"];
            if (input == null || input.IsNull)
            {
                input = JsonNode.NewObject();
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
                JsonNode content = _executor.Execute(tool, input);
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
            catch (Exception ex)
            {
                return JsonResponse(200, JsonRpcResult(id, ToolErrorResult(ex.Message)));
            }
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
