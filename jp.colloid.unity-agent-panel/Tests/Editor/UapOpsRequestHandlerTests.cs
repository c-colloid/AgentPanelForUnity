using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapOpsRequestHandler is the pure "bytes/JsonNode in -&gt; response
    /// out" core of the UapOps server (design section 1/8.1). These tests
    /// replay the EXACT CLI request sequence measured in docs/research/
    /// 08-mcp-transport.md section 5 (initialize -&gt; notifications/
    /// initialized -&gt; GET-&gt;405 -&gt; tools/list -&gt; tools/call) as fixtures,
    /// plus the Bearer-auth and error-path cases -- all without a real
    /// HttpListener/socket (UapOpsHttpServer, which owns the actual socket,
    /// is intentionally NOT covered by EditMode tests -- see its class doc
    /// comment).
    /// </summary>
    [TestFixture]
    public class UapOpsRequestHandlerTests
    {
        private const string Token = "test-token-123";
        private const string McpPath = "/mcp";

        private ToolRegistry _registry;
        private UapOpsRequestHandler _handler;

        [SetUp]
        public void SetUp()
        {
            _registry = ToolRegistry.CreateDefault(); // uap_ping, module "core"
            _handler = NewHandler(new[] { "core" });
        }

        private UapOpsRequestHandler NewHandler(string[] enabledModules, int sessionCounter = 0)
        {
            int counter = sessionCounter;
            return new UapOpsRequestHandler(_registry, () => Token, () => enabledModules,
                new DirectUapToolExecutor(),
                () => "fixed-session-" + (counter++));
        }

        private static UapOpsHttpRequest Post(string body, string authHeader = "Bearer " + Token)
        {
            return new UapOpsHttpRequest
            {
                Method = "POST",
                Path = McpPath,
                AuthorizationHeader = authHeader,
                Body = body
            };
        }

        private static string BuildRequest(JsonNode idOrNull, string method, JsonNode paramsNode = null)
        {
            JsonNode obj = JsonNode.NewObject().Set("jsonrpc", "2.0");
            if (idOrNull != null)
            {
                obj.Set("id", idOrNull);
            }
            obj.Set("method", method);
            if (paramsNode != null)
            {
                obj.Set("params", paramsNode);
            }
            return JsonWriter.Write(obj);
        }

        // -----------------------------------------------------------------
        // R08 section 5 sequence, replayed in order against one handler
        // -----------------------------------------------------------------

        [Test]
        public void Sequence_Initialize_NotificationsInitialized_Get405_ToolsList_ToolsCall()
        {
            // 1. POST /mcp initialize
            UapOpsHttpResponse initResponse = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "initialize")));
            Assert.AreEqual(200, initResponse.StatusCode);
            JsonNode initBody = JsonParser.Parse(initResponse.Body);
            Assert.AreEqual("2.0", initBody["jsonrpc"].AsString());
            Assert.AreEqual(1, initBody["id"].AsInt());
            Assert.AreEqual(UapOpsRequestHandler.ProtocolVersion, initBody["result"]["protocolVersion"].AsString());
            Assert.AreEqual(UapOpsRequestHandler.ServerInfoName, initBody["result"]["serverInfo"]["name"].AsString());
            Assert.IsFalse(string.IsNullOrEmpty(initResponse.McpSessionId));

            // 2. POST /mcp notifications/initialized -> 202, no body
            UapOpsHttpResponse ackResponse = _handler.Handle(
                Post(BuildRequest(null, "notifications/initialized")));
            Assert.AreEqual(202, ackResponse.StatusCode);
            Assert.IsNull(ackResponse.Body);

            // 3. GET /mcp -> 405 (no server push implemented, spec-compliant)
            var getRequest = new UapOpsHttpRequest { Method = "GET", Path = McpPath };
            UapOpsHttpResponse getResponse = _handler.Handle(getRequest);
            Assert.AreEqual(405, getResponse.StatusCode);

            // 4. POST /mcp tools/list
            UapOpsHttpResponse listResponse = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(2L), "tools/list")));
            Assert.AreEqual(200, listResponse.StatusCode);
            JsonNode listBody = JsonParser.Parse(listResponse.Body);
            JsonNode tools = listBody["result"]["tools"];
            // Phase 5a stream B registered the rest of the "core" module
            // tools (scene/component/property/asset/query + the script
            // gate's commit tool) alongside uap_ping -- this sequence test
            // only cares that uap_ping (registered first) is still present
            // and shaped correctly, not the exact total tool count (which
            // will keep growing across phases).
            Assert.GreaterOrEqual(tools.Count, 1);
            Assert.AreEqual("uap_ping", tools[0]["name"].AsString());
            Assert.IsTrue(tools[0]["description"].AsString().Length > 0);
            Assert.IsTrue(tools[0]["inputSchema"].IsObject);

            // 5. POST /mcp tools/call
            JsonNode callParams = JsonNode.NewObject()
                .Set("name", "uap_ping")
                .Set("arguments", JsonNode.NewObject().Set("message", "hello"));
            UapOpsHttpResponse callResponse = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(3L), "tools/call", callParams)));
            Assert.AreEqual(200, callResponse.StatusCode);
            JsonNode callBody = JsonParser.Parse(callResponse.Body);
            Assert.IsFalse(callBody["result"]["isError"].AsBool(true));
            Assert.AreEqual("hello", callBody["result"]["content"][0]["text"].AsString());
        }

        [Test]
        public void Initialize_AssignsADifferentSessionId_OnEachCall()
        {
            UapOpsHttpResponse first = _handler.Handle(Post(BuildRequest(JsonNode.Of(1L), "initialize")));
            UapOpsHttpResponse second = _handler.Handle(Post(BuildRequest(JsonNode.Of(2L), "initialize")));
            Assert.AreNotEqual(first.McpSessionId, second.McpSessionId);
        }

        [Test]
        public void Initialize_UpdatesCurrentSessionId()
        {
            _handler.Handle(Post(BuildRequest(JsonNode.Of(1L), "initialize")));
            Assert.AreEqual("fixed-session-0", _handler.CurrentSessionId);
        }

        // -----------------------------------------------------------------
        // tools/call error paths
        // -----------------------------------------------------------------

        [Test]
        public void ToolsCall_DefaultsMessage_WhenArgumentsOmitted()
        {
            JsonNode callParams = JsonNode.NewObject().Set("name", "uap_ping");
            UapOpsHttpResponse response = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "tools/call", callParams)));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.AreEqual(UapPingTool.DefaultMessage, body["result"]["content"][0]["text"].AsString());
        }

        [Test]
        public void ToolsCall_UnknownTool_ReturnsIsErrorTrue_ButHttp200_NotJsonRpcError()
        {
            JsonNode callParams = JsonNode.NewObject().Set("name", "does_not_exist");
            UapOpsHttpResponse response = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "tools/call", callParams)));
            Assert.AreEqual(200, response.StatusCode);
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsFalse(body.HasKey("error"));
            Assert.IsTrue(body["result"]["isError"].AsBool());
            StringAssert.Contains("does_not_exist", body["result"]["content"][0]["text"].AsString());
        }

        [Test]
        public void ToolsCall_ModuleDisabled_ReturnsIsErrorTrue()
        {
            // uap_ping is registered under "core", but the module filter
            // (defense in depth -- design section 1) enables nothing.
            UapOpsRequestHandler handler = NewHandler(new string[0]);
            JsonNode callParams = JsonNode.NewObject().Set("name", "uap_ping");
            UapOpsHttpResponse response = handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "tools/call", callParams)));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["result"]["isError"].AsBool());
        }

        [Test]
        public void ToolsCall_ToolThrows_ReportsIsErrorTrue_NeverHttp500()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool
            {
                Name = "boom_tool",
                Module = "core",
                ExecuteImpl = delegate { throw new InvalidOperationException("kaboom"); }
            });
            var handler = new UapOpsRequestHandler(registry, () => Token, () => new[] { "core" },
                new DirectUapToolExecutor());

            JsonNode callParams = JsonNode.NewObject().Set("name", "boom_tool");
            UapOpsHttpResponse response = handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "tools/call", callParams)));

            Assert.AreEqual(200, response.StatusCode);
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["result"]["isError"].AsBool());
            StringAssert.Contains("kaboom", body["result"]["content"][0]["text"].AsString());
        }

        [Test]
        public void ToolsList_HidesToolsFromDisabledModules()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "core_tool", Module = "core" });
            registry.Register(new StubUapTool { Name = "prefab_tool", Module = "prefab" });
            var handler = new UapOpsRequestHandler(registry, () => Token, () => new[] { "core" },
                new DirectUapToolExecutor());

            UapOpsHttpResponse response = handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "tools/list")));
            JsonNode tools = JsonParser.Parse(response.Body)["result"]["tools"];
            Assert.AreEqual(1, tools.Count);
            Assert.AreEqual("core_tool", tools[0]["name"].AsString());
        }

        // -----------------------------------------------------------------
        // Auth (Probe A territory, design section 1.1)
        // -----------------------------------------------------------------

        [Test]
        public void MissingAuthorizationHeader_Returns401_WithJsonRpcError()
        {
            UapOpsHttpResponse response = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(5L), "tools/list"), authHeader: null));
            Assert.AreEqual(401, response.StatusCode);
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.AreEqual(5, body["id"].AsInt());
            Assert.AreEqual(-32001, body["error"]["code"].AsInt());
        }

        [Test]
        public void WrongBearerToken_Returns401()
        {
            UapOpsHttpResponse response = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(1L), "tools/list"), authHeader: "Bearer wrong-token"));
            Assert.AreEqual(401, response.StatusCode);
        }

        [Test]
        public void Unauthorized_PreservesTheRequestId_EvenWithBadBody()
        {
            // The auth check must extract the id from the body itself for
            // the error response, even though the body is never otherwise
            // trusted until authorization has passed.
            UapOpsHttpResponse response = _handler.Handle(
                Post(BuildRequest(JsonNode.Of("abc"), "tools/list"), authHeader: null));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.AreEqual("abc", body["id"].AsString());
        }

        [Test]
        public void Unauthorized_UnparsableBody_FallsBackToNullId_DoesNotThrow()
        {
            UapOpsHttpResponse response = _handler.Handle(Post("not json", authHeader: null));
            Assert.AreEqual(401, response.StatusCode);
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["id"].IsNull);
        }

        // -----------------------------------------------------------------
        // Transport-shape edges
        // -----------------------------------------------------------------

        [Test]
        public void UnknownPath_Returns404()
        {
            var request = new UapOpsHttpRequest { Method = "POST", Path = "/nope", Body = "{}" };
            Assert.AreEqual(404, _handler.Handle(request).StatusCode);
        }

        [Test]
        public void Delete_Returns405()
        {
            var request = new UapOpsHttpRequest { Method = "DELETE", Path = McpPath };
            Assert.AreEqual(405, _handler.Handle(request).StatusCode);
        }

        [Test]
        public void MalformedJson_ReturnsParseError32700()
        {
            UapOpsHttpResponse response = _handler.Handle(Post("{not valid json"));
            Assert.AreEqual(200, response.StatusCode);
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.AreEqual(-32700, body["error"]["code"].AsInt());
        }

        [Test]
        public void UnknownMethod_WithId_ReturnsMethodNotFound32601()
        {
            UapOpsHttpResponse response = _handler.Handle(
                Post(BuildRequest(JsonNode.Of(9L), "not/a/real/method")));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.AreEqual(-32601, body["error"]["code"].AsInt());
        }

        [Test]
        public void UnknownMethod_AsNotification_ReturnsBareAck_NeverAnError()
        {
            UapOpsHttpResponse response = _handler.Handle(Post(BuildRequest(null, "not/a/real/method")));
            Assert.AreEqual(202, response.StatusCode);
            Assert.IsNull(response.Body);
        }

        [Test]
        public void Handle_NullRequest_Throws()
        {
            Assert.Throws<ArgumentNullException>(delegate { _handler.Handle(null); });
        }

        [Test]
        public void TrailingSlashOnPath_IsTrimmed_StillMatches()
        {
            var request = new UapOpsHttpRequest
            {
                Method = "POST",
                Path = "/mcp/",
                AuthorizationHeader = "Bearer " + Token,
                Body = BuildRequest(JsonNode.Of(1L), "tools/list")
            };
            Assert.AreEqual(200, _handler.Handle(request).StatusCode);
        }
    }
}
