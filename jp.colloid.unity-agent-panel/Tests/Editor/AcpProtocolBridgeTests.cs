using System.Collections.Generic;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// AcpProtocolBridge translation tests (design note
    /// docs/design-notes/2026-09-10-acp-backends.md section 3). A scripted
    /// ACP agent answers the bridge's JSON-RPC by id; every line the bridge
    /// hands the panel is re-parsed through StreamJsonMessage.ParseLine so
    /// the shapes AgentClient consumes are asserted, not just the JSON text.
    /// No process is spawned.
    /// </summary>
    [TestFixture]
    public class AcpProtocolBridgeTests
    {
        private List<string> _toAgent;
        private List<string> _toPanel;
        private List<string> _log;
        private AcpLaunchSpec _spec;
        private AcpProtocolBridge _bridge;

        [SetUp]
        public void SetUp()
        {
            _toAgent = new List<string>();
            _toPanel = new List<string>();
            _log = new List<string>();
            _spec = new AcpLaunchSpec { Backend = AgentBackend.GeminiCli };
            _bridge = new AcpProtocolBridge(_spec, "C:/proj", _toAgent.Add, _toPanel.Add, _log.Add);
        }

        // -- helpers ---------------------------------------------------------------

        private AcpInbound LastAgentLine()
        {
            Assert.IsTrue(_toAgent.Count > 0, "bridge wrote nothing to the agent");
            return AcpJsonRpc.TryParse(_toAgent[_toAgent.Count - 1]);
        }

        private AcpInbound AgentLine(string method)
        {
            for (int i = _toAgent.Count - 1; i >= 0; i--)
            {
                AcpInbound parsed = AcpJsonRpc.TryParse(_toAgent[i]);
                if (parsed != null && parsed.Method == method)
                {
                    return parsed;
                }
            }
            Assert.Fail("no '" + method + "' was sent to the agent");
            return null;
        }

        private void Respond(AcpInbound request, JsonNode result)
        {
            _bridge.OnAgentLine(AcpJsonRpc.Response(request.Id, result));
        }

        private void RespondError(AcpInbound request, int code, string message)
        {
            _bridge.OnAgentLine(AcpJsonRpc.ErrorResponse(request.Id, code, message));
        }

        private void NotifyWithMeta(string sessionUpdateJson, string metaJson)
        {
            JsonNode update = JsonParser.Parse(sessionUpdateJson);
            _bridge.OnAgentLine(AcpJsonRpc.Notification("session/update",
                JsonNode.NewObject().Set("sessionId", "sess-1").Set("update", update)
                    .Set("_meta", JsonParser.Parse(metaJson))));
        }

        private void Notify(string sessionUpdateJson)
        {
            JsonNode update = JsonParser.Parse(sessionUpdateJson);
            _bridge.OnAgentLine(AcpJsonRpc.Notification("session/update",
                JsonNode.NewObject().Set("sessionId", "sess-1").Set("update", update)));
        }

        private List<StreamJsonMessage> PanelMessages()
        {
            var result = new List<StreamJsonMessage>();
            for (int i = 0; i < _toPanel.Count; i++)
            {
                StreamJsonMessage message = StreamJsonMessage.ParseLine(_toPanel[i], _log.Add);
                Assert.IsNotNull(message, "panel line did not parse: " + _toPanel[i]);
                result.Add(message);
            }
            return result;
        }

        private T LastPanel<T>() where T : StreamJsonMessage
        {
            List<StreamJsonMessage> messages = PanelMessages();
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var typed = messages[i] as T;
                if (typed != null)
                {
                    return typed;
                }
            }
            Assert.Fail("no " + typeof(T).Name + " reached the panel");
            return null;
        }

        private int CountPanel<T>() where T : StreamJsonMessage
        {
            int count = 0;
            foreach (StreamJsonMessage message in PanelMessages())
            {
                if (message is T)
                {
                    count++;
                }
            }
            return count;
        }

        private static JsonNode InitializeResult(bool loadSession = false, bool image = true, bool http = true,
            params string[] authMethodIds)
        {
            JsonNode auth = JsonNode.NewArray();
            for (int i = 0; i < authMethodIds.Length; i++)
            {
                auth.Add(JsonNode.NewObject().Set("id", authMethodIds[i]).Set("name", authMethodIds[i]));
            }
            return JsonNode.NewObject()
                .Set("protocolVersion", 1)
                .Set("agentCapabilities", JsonNode.NewObject()
                    .Set("loadSession", loadSession)
                    .Set("promptCapabilities", JsonNode.NewObject().Set("image", image))
                    .Set("mcpCapabilities", JsonNode.NewObject().Set("http", http)))
                .Set("agentInfo", JsonNode.NewObject().Set("name", "gemini-cli").Set("version", "0.9.0"))
                .Set("authMethods", auth);
        }

        private static JsonNode NewSessionResult()
        {
            return JsonNode.NewObject()
                .Set("sessionId", "sess-1")
                .Set("modes", JsonNode.NewObject()
                    .Set("currentModeId", "default")
                    .Set("availableModes", JsonNode.NewArray()
                        .Add(JsonNode.NewObject().Set("id", "default").Set("name", "Default"))
                        .Add(JsonNode.NewObject().Set("id", "auto-edit").Set("name", "Auto edit"))
                        .Add(JsonNode.NewObject().Set("id", "yolo").Set("name", "YOLO"))))
                .Set("models", JsonNode.NewObject()
                    .Set("currentModelId", "gemini-2.5-pro")
                    .Set("availableModels", JsonNode.NewArray()
                        .Add(JsonNode.NewObject().Set("modelId", "gemini-2.5-pro").Set("name", "Gemini 2.5 Pro"))
                        .Add(JsonNode.NewObject().Set("modelId", "gemini-2.5-flash").Set("name", "Gemini 2.5 Flash"))));
        }

        /// <summary>Runs the whole handshake with a fresh session and returns the panel's messages.</summary>
        private void CompleteHandshake(JsonNode initializeResult = null)
        {
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), initializeResult ?? InitializeResult());
            Respond(AgentLine("session/new"), NewSessionResult());
        }

        private void SendUser(string text)
        {
            _bridge.OnPanelLine(OutboundMessages.UserText(text));
        }

        // -- handshake -------------------------------------------------------------

        [Test]
        public void Initialize_SendsAcpInitializeWithNoFsOrTerminalCapability()
        {
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            AcpInbound init = LastAgentLine();
            Assert.AreEqual("initialize", init.Method);
            Assert.AreEqual(1, init.Params["protocolVersion"].AsInt());
            Assert.IsFalse(init.Params["clientCapabilities"]["fs"]["readTextFile"].AsBool(true));
            Assert.IsFalse(init.Params["clientCapabilities"]["fs"]["writeTextFile"].AsBool(true));
            Assert.IsFalse(init.Params["clientCapabilities"]["terminal"].AsBool(true));
            Assert.AreEqual(0, _toPanel.Count, "nothing reaches the panel before the session exists");
        }

        [Test]
        public void Handshake_EmitsInitializeResponseThenSystemInit_WithModelsAndSession()
        {
            CompleteHandshake();
            List<StreamJsonMessage> messages = PanelMessages();
            Assert.AreEqual(2, messages.Count);
            var response = messages[0] as ControlResponseMessage;
            Assert.IsNotNull(response);
            Assert.AreEqual("req_1", response.RequestId);
            Assert.IsTrue(response.Success);
            Assert.AreEqual(2, response.Response["models"].Count);
            Assert.AreEqual("gemini-2.5-flash", response.Response["models"][1]["value"].AsString());
            Assert.AreEqual("Gemini 2.5 Flash", response.Response["models"][1]["displayName"].AsString());
            var init = messages[1] as SystemInitMessage;
            Assert.IsNotNull(init);
            Assert.AreEqual("sess-1", init.SessionId);
            Assert.AreEqual("gemini-2.5-pro", init.Model);
            Assert.AreEqual("gemini-cli 0.9.0", init.ClaudeCodeVersion);
            Assert.AreEqual("sess-1", _bridge.SessionId);
        }

        [Test]
        public void Handshake_NewSessionCarriesCwdAndHttpMcpServerWithBearerHeader()
        {
            _spec.McpServerName = "unity-ops";
            _spec.McpUrl = "http://127.0.0.1:4321/mcp";
            _spec.McpBearerToken = "tok";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult());
            AcpInbound newSession = AgentLine("session/new");
            Assert.AreEqual("C:/proj", newSession.Params["cwd"].AsString());
            JsonNode server = newSession.Params["mcpServers"][0];
            Assert.AreEqual("http", server["type"].AsString());
            Assert.AreEqual("unity-ops", server["name"].AsString());
            Assert.AreEqual("http://127.0.0.1:4321/mcp", server["url"].AsString());
            Assert.AreEqual("Authorization", server["headers"][0]["name"].AsString());
            Assert.AreEqual("Bearer tok", server["headers"][0]["value"].AsString());
        }

        [Test]
        public void Handshake_AgentWithoutHttpMcp_OmitsTheServerAndReportsItFailed()
        {
            _spec.McpServerName = "unity-ops";
            _spec.McpUrl = "http://127.0.0.1:4321/mcp";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false, true, false));
            Assert.AreEqual(0, AgentLine("session/new").Params["mcpServers"].Count);
            Respond(AgentLine("session/new"), NewSessionResult());
            SystemInitMessage init = LastPanel<SystemInitMessage>();
            Assert.AreEqual("failed", init.McpServers[0].Status);
        }

        [Test]
        public void Handshake_PermissionModeMapsToAgentMode_AndDefaultModelIsSelected()
        {
            _spec.PermissionMode = "acceptEdits";
            _spec.Model = "gemini-2.5-flash";
            CompleteHandshake();
            Assert.AreEqual("auto-edit", AgentLine("session/set_mode").Params["modeId"].AsString());
            Assert.AreEqual("gemini-2.5-flash", AgentLine("session/set_model").Params["modelId"].AsString());
        }

        [Test]
        public void Handshake_ResumeWithLoadSession_UsesSessionLoadAndDropsReplay()
        {
            _spec.ResumeSessionId = "sess-old";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(true));
            AcpInbound load = AgentLine("session/load");
            Assert.AreEqual("sess-old", load.Params["sessionId"].AsString());
            // Replayed history arrives BEFORE the load response.
            _bridge.OnAgentLine(AcpJsonRpc.Notification("session/update", JsonNode.NewObject()
                .Set("sessionId", "sess-old")
                .Set("update", JsonNode.NewObject()
                    .Set("sessionUpdate", "agent_message_chunk")
                    .Set("content", JsonNode.NewObject().Set("type", "text").Set("text", "old text")))));
            Respond(load, JsonNode.Null);
            Assert.AreEqual(0, CountPanel<StreamEventMessage>(), "replayed chunks must not stream into the panel");
            Assert.AreEqual("sess-old", LastPanel<SystemInitMessage>().SessionId);
        }

        [Test]
        public void Handshake_ResumeWithoutLoadSessionCapability_FallsBackToNewSession()
        {
            _spec.ResumeSessionId = "sess-old";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false));
            Assert.AreEqual("session/new", LastAgentLine().Method);
        }

        [Test]
        public void Handshake_LoadFailure_FallsBackToNewSession()
        {
            _spec.ResumeSessionId = "sess-old";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(true));
            RespondError(AgentLine("session/load"), -32602, "unknown session");
            Respond(AgentLine("session/new"), NewSessionResult());
            Assert.AreEqual("sess-1", LastPanel<SystemInitMessage>().SessionId);
        }

        [Test]
        public void Handshake_AuthRequired_AuthenticatesWithFirstMethodThenRetries()
        {
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false, true, true, "oauth-personal", "gemini-api-key"));
            RespondError(AgentLine("session/new"), AcpJsonRpc.AuthRequired, "Authentication required");
            AcpInbound auth = AgentLine("authenticate");
            Assert.AreEqual("oauth-personal", auth.Params["methodId"].AsString());
            Respond(auth, JsonNode.NewObject());
            AcpInbound retry = LastAgentLine();
            Assert.AreEqual("session/new", retry.Method);
            Respond(retry, NewSessionResult());
            Assert.IsTrue(LastPanel<ControlResponseMessage>().Success);
        }

        [Test]
        public void Handshake_PreferredAuthMethod_WinsWhenOffered()
        {
            _spec.AuthMethodId = "gemini-api-key";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false, true, true, "oauth-personal", "gemini-api-key"));
            RespondError(AgentLine("session/new"), AcpJsonRpc.AuthRequired, "Authentication required");
            Assert.AreEqual("gemini-api-key", AgentLine("authenticate").Params["methodId"].AsString());
        }

        [Test]
        public void Handshake_AuthFailure_ReportsErrorResponseAndErrorResult()
        {
            string failed = null;
            _bridge.HandshakeFailed += delegate(string reason) { failed = reason; };
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false, true, true, "oauth-personal"));
            RespondError(AgentLine("session/new"), AcpJsonRpc.AuthRequired, "Authentication required");
            RespondError(AgentLine("authenticate"), -32000, "login cancelled");
            Assert.IsNotNull(failed);
            ControlResponseMessage response = LastPanel<ControlResponseMessage>();
            Assert.IsFalse(response.Success);
            StringAssert.Contains("login cancelled", response.Error);
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.IsTrue(result.IsError);
        }

        [Test]
        public void Handshake_CodexMethodOrder_PrefersChatGptOverApiKey()
        {
            // codex-acp advertises API Key first (live report 2026-09-10):
            // the browser login must still win.
            _spec.Backend = AgentBackend.CodexAcp;
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            _bridge.OnAgentLine(AcpJsonRpc.Response(AgentLine("initialize").Id, JsonNode.NewObject()
                .Set("protocolVersion", 1)
                .Set("agentCapabilities", JsonNode.NewObject().Set("mcpCapabilities", JsonNode.NewObject().Set("http", true)))
                .Set("authMethods", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("id", "api-key").Set("name", "API Key"))
                    .Add(JsonNode.NewObject().Set("id", "chat-gpt").Set("name", "ChatGPT"))
                    .Add(JsonNode.NewObject().Set("id", "chat-gpt-device-code").Set("name", "ChatGPT (device code)"))
                    .Add(JsonNode.NewObject().Set("id", "gateway").Set("name", "Custom model gateway")))));
            RespondError(AgentLine("session/new"), AcpJsonRpc.AuthRequired, "Authentication required");
            Assert.AreEqual("chat-gpt", AgentLine("authenticate").Params["methodId"].AsString());
        }

        [Test]
        public void Handshake_AuthFallback_TriesTheNextMethodAfterAFailure()
        {
            string failed = null;
            var started = new List<string>();
            _bridge.HandshakeFailed += delegate(string reason) { failed = reason; };
            _bridge.AuthenticationStarted += delegate(string id, string name) { started.Add(id); };
            _spec.AuthMethodId = "api-key";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false, true, true, "api-key", "chat-gpt"));
            RespondError(AgentLine("session/new"), AcpJsonRpc.AuthRequired, "Authentication required");
            AcpInbound first = AgentLine("authenticate");
            Assert.AreEqual("api-key", first.Params["methodId"].AsString(), "the explicit preference goes first");
            RespondError(first, -32603, "CODEX_API_KEY or OPENAI_API_KEY is not set");
            AcpInbound second = AgentLine("authenticate");
            Assert.AreNotEqual(first.NumericId, second.NumericId);
            Assert.AreEqual("chat-gpt", second.Params["methodId"].AsString(), "falls back instead of failing");
            Assert.IsNull(failed);
            Respond(second, JsonNode.NewObject());
            Assert.AreEqual("session/new", LastAgentLine().Method);
            CollectionAssert.AreEqual(new[] { "api-key", "chat-gpt" }, started);
        }

        [Test]
        public void Handshake_AllAuthMethodsFail_ReportsEveryFailureOnce()
        {
            string failed = null;
            int finished = 0;
            _bridge.HandshakeFailed += delegate(string reason) { failed = reason; };
            _bridge.AuthenticationFinished += delegate(bool ok, string error) { finished++; };
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(false, true, true, "api-key", "chat-gpt"));
            RespondError(AgentLine("session/new"), AcpJsonRpc.AuthRequired, "Authentication required");
            RespondError(AgentLine("authenticate"), -32603, "browser closed");
            RespondError(AgentLine("authenticate"), -32603, "no key");
            Assert.IsNotNull(failed);
            StringAssert.Contains("browser closed", failed);
            StringAssert.Contains("no key", failed);
            Assert.AreEqual(1, finished);
        }

        [Test]
        public void RankAuthMethods_BrowserLoginsFirst_KeyLikeLast()
        {
            var ids = new List<string> { "gemini-api-key", "vertex-ai", "oauth-personal" };
            var names = new List<string> { "Use Gemini API key", "Vertex AI", "Login with Google" };
            CollectionAssert.AreEqual(new[] { "oauth-personal", "gemini-api-key", "vertex-ai" },
                AcpProtocolBridge.RankAuthMethods(null, ids, names));
            CollectionAssert.AreEqual(new[] { "vertex-ai", "oauth-personal", "gemini-api-key" },
                AcpProtocolBridge.RankAuthMethods("vertex-ai", ids, names));
            CollectionAssert.AreEqual(new[] { "unknown-a", "unknown-b" },
                AcpProtocolBridge.RankAuthMethods(null, new List<string> { "unknown-a", "unknown-b" }, null));
        }

        // -- Which method authenticated (design note 2026-09-10-acp-auth-
        // guidance-and-method-display.md section 4) ------------------------

        [Test]
        public void IsApiKeyAuthMethod_TrueForKeyAndGatewayMethods_FalseForAccountLogins()
        {
            Assert.IsTrue(AcpProtocolBridge.IsApiKeyAuthMethod("gemini-api-key", "Use Gemini API key"));
            Assert.IsTrue(AcpProtocolBridge.IsApiKeyAuthMethod("vertex-ai", "Vertex AI"));
            Assert.IsTrue(AcpProtocolBridge.IsApiKeyAuthMethod("gateway", "Custom model gateway"));
            Assert.IsTrue(AcpProtocolBridge.IsApiKeyAuthMethod("apikey", null));
            Assert.IsFalse(AcpProtocolBridge.IsApiKeyAuthMethod("oauth-personal", "Login with Google"));
            Assert.IsFalse(AcpProtocolBridge.IsApiKeyAuthMethod("chat-gpt", "ChatGPT"));
            // No method at all: the agent was already signed in and never
            // ran `authenticate`, which is not an API key connection.
            Assert.IsFalse(AcpProtocolBridge.IsApiKeyAuthMethod(null, null));
        }

        [Test]
        public void DescribeAuthMethod_PrefersTheDisplayName_ThenTheId_ThenEmpty()
        {
            Assert.AreEqual("Use Gemini API key",
                AcpProtocolBridge.DescribeAuthMethod("gemini-api-key", "Use Gemini API key"));
            Assert.AreEqual("gemini-api-key", AcpProtocolBridge.DescribeAuthMethod("gemini-api-key", null));
            Assert.AreEqual("gemini-api-key", AcpProtocolBridge.DescribeAuthMethod("gemini-api-key", string.Empty));
            Assert.AreEqual(string.Empty, AcpProtocolBridge.DescribeAuthMethod(null, null));
        }

        [Test]
        public void AvailableCommandsUpdate_ReEmitsSystemInitWithSlashCommands()
        {
            CompleteHandshake();
            Notify("{\"sessionUpdate\":\"available_commands_update\",\"availableCommands\":["
                + "{\"name\":\"compact\",\"description\":\"Compact\"},{\"name\":\"help\"}]}");
            SystemInitMessage init = LastPanel<SystemInitMessage>();
            CollectionAssert.AreEqual(new[] { "compact", "help" }, init.SlashCommands);
            Assert.AreEqual(2, CountPanel<SystemInitMessage>());
        }

        // -- prompt turn -----------------------------------------------------------

        [Test]
        public void UserText_BecomesSessionPrompt_AndEchoesReplay()
        {
            CompleteHandshake();
            SendUser("hello");
            AcpInbound prompt = AgentLine("session/prompt");
            Assert.AreEqual("sess-1", prompt.Params["sessionId"].AsString());
            Assert.AreEqual("hello", prompt.Params["prompt"][0]["text"].AsString());
            UserEchoMessage echo = LastPanel<UserEchoMessage>();
            Assert.IsTrue(echo.IsReplay);
            Assert.IsTrue(_bridge.PromptInFlight);
        }

        [Test]
        public void FirstPromptOfNewSession_GetsStandingInstructionsPrepended_OnceOnly()
        {
            _spec.SystemPrompt = "Always answer in Japanese.";
            CompleteHandshake();
            SendUser("hello");
            AcpInbound first = AgentLine("session/prompt");
            Assert.AreEqual(2, first.Params["prompt"].Count);
            StringAssert.Contains(AcpProtocolBridge.SystemPromptHeader, first.Params["prompt"][0]["text"].AsString());
            StringAssert.Contains("Always answer in Japanese.", first.Params["prompt"][0]["text"].AsString());
            Assert.AreEqual("hello", first.Params["prompt"][1]["text"].AsString());
            Respond(first, JsonNode.NewObject().Set("stopReason", "end_turn"));
            SendUser("again");
            AcpInbound second = AgentLine("session/prompt");
            Assert.AreEqual(1, second.Params["prompt"].Count);
            Assert.AreEqual("again", second.Params["prompt"][0]["text"].AsString());
        }

        [Test]
        public void LoadedSession_DoesNotRepeatStandingInstructions()
        {
            _spec.SystemPrompt = "Always answer in Japanese.";
            _spec.ResumeSessionId = "sess-old";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(true));
            Respond(AgentLine("session/load"), JsonNode.Null);
            SendUser("hello");
            Assert.AreEqual(1, AgentLine("session/prompt").Params["prompt"].Count);
        }

        [Test]
        public void ImageBlock_TranslatesToAcpImage_WhenAgentAcceptsImages()
        {
            CompleteHandshake();
            var images = new List<OutboundMessages.ImageBlock>
            {
                new OutboundMessages.ImageBlock { MediaType = "image/png", Base64Data = "AAAA" }
            };
            _bridge.OnPanelLine(OutboundMessages.UserContent("look", images));
            JsonNode prompt = AgentLine("session/prompt").Params["prompt"];
            Assert.AreEqual("image", prompt[1]["type"].AsString());
            Assert.AreEqual("image/png", prompt[1]["mimeType"].AsString());
            Assert.AreEqual("AAAA", prompt[1]["data"].AsString());
        }

        [Test]
        public void ImageBlock_DroppedWhenAgentLacksImageCapability()
        {
            CompleteHandshake(InitializeResult(false, false, true));
            var images = new List<OutboundMessages.ImageBlock>
            {
                new OutboundMessages.ImageBlock { MediaType = "image/png", Base64Data = "AAAA" }
            };
            _bridge.OnPanelLine(OutboundMessages.UserContent("look", images));
            Assert.AreEqual(1, AgentLine("session/prompt").Params["prompt"].Count);
        }

        [Test]
        public void MessageChunks_StreamAsTextDeltas_ThenFoldIntoAssistantAndResult()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Hel\"}}");
            Notify("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"lo!\"}}");
            StreamEventMessage delta = LastPanel<StreamEventMessage>();
            string text;
            Assert.IsTrue(delta.TryGetTextDelta(out text));
            Assert.AreEqual("lo!", text);
            Assert.AreEqual(0, CountPanel<ResultMessage>());

            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn"));
            List<StreamJsonMessage> messages = PanelMessages();
            var assistant = messages[messages.Count - 2] as AssistantMessage;
            Assert.IsNotNull(assistant, "assistant message precedes the result");
            Assert.AreEqual(ContentBlockType.Text, assistant.Content[0].Type);
            Assert.AreEqual("Hello!", assistant.Content[0].Text);
            var result = messages[messages.Count - 1] as ResultMessage;
            Assert.IsNotNull(result);
            Assert.IsFalse(result.IsError);
            Assert.AreEqual("success", result.Subtype);
            Assert.AreEqual("Hello!", result.ResultText);
            Assert.AreEqual("sess-1", result.SessionId);
            Assert.IsFalse(_bridge.PromptInFlight);
        }

        [Test]
        public void ThoughtChunks_OpenAThinkingBlockThenStreamThinkingDeltas()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hmm\"}}");
            List<StreamJsonMessage> messages = PanelMessages();
            var start = messages[messages.Count - 2] as StreamEventMessage;
            Assert.IsNotNull(start);
            Assert.IsTrue(start.IsThinkingBlockStart());
            var delta = messages[messages.Count - 1] as StreamEventMessage;
            string thinking;
            Assert.IsTrue(delta.TryGetThinkingDelta(out thinking));
            Assert.AreEqual("hmm", thinking);
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn"));
            AssistantMessage assistant = LastPanel<AssistantMessage>();
            Assert.AreEqual(ContentBlockType.Thinking, assistant.Content[0].Type);
            Assert.AreEqual("hmm", assistant.Content[0].Thinking);
        }

        [Test]
        public void ToolCall_FlushesPrecedingText_EmitsToolUseThenToolResult()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Running\"}}");
            Notify("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call_1\",\"title\":\"ls -la\","
                + "\"kind\":\"execute\",\"status\":\"pending\",\"rawInput\":{\"command\":\"ls -la\"}}");
            List<StreamJsonMessage> messages = PanelMessages();
            var textMessage = messages[messages.Count - 2] as AssistantMessage;
            Assert.IsNotNull(textMessage);
            Assert.AreEqual("Running", textMessage.Content[0].Text);
            var toolMessage = messages[messages.Count - 1] as AssistantMessage;
            Assert.IsNotNull(toolMessage);
            ContentBlock toolUse = toolMessage.Content[0];
            Assert.AreEqual(ContentBlockType.ToolUse, toolUse.Type);
            Assert.AreEqual("call_1", toolUse.Id);
            Assert.AreEqual("Bash", toolUse.Name);
            Assert.AreEqual("ls -la", toolUse.Input["command"].AsString());
            Assert.AreEqual("execute", toolUse.Input["kind"].AsString());

            Notify("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call_1\",\"status\":\"completed\","
                + "\"content\":[{\"type\":\"content\",\"content\":{\"type\":\"text\",\"text\":\"total 0\"}}]}");
            UserEchoMessage user = LastPanel<UserEchoMessage>();
            ContentBlock toolResult = user.Content[0];
            Assert.AreEqual(ContentBlockType.ToolResult, toolResult.Type);
            Assert.AreEqual("call_1", toolResult.ToolUseId);
            Assert.AreEqual("total 0", toolResult.ResultContent.AsString());
            Assert.IsFalse(toolResult.IsError);
        }

        [Test]
        public void ToolCallFailed_ProducesErrorToolResult_WithDiffContentRendered()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call_2\",\"title\":\"Edit file\",\"kind\":\"edit\","
                + "\"status\":\"in_progress\",\"locations\":[{\"path\":\"Assets/A.cs\"}]}");
            AssistantMessage toolMessage = LastPanel<AssistantMessage>();
            Assert.AreEqual("Edit", toolMessage.Content[0].Name);
            Assert.AreEqual("Assets/A.cs", toolMessage.Content[0].Input["file_path"].AsString());
            Notify("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call_2\",\"status\":\"failed\","
                + "\"content\":[{\"type\":\"diff\",\"path\":\"Assets/A.cs\",\"oldText\":\"a\",\"newText\":\"b\"}]}");
            ContentBlock toolResult = LastPanel<UserEchoMessage>().Content[0];
            Assert.IsTrue(toolResult.IsError);
            StringAssert.Contains("Assets/A.cs", toolResult.ResultContent.AsString());
            StringAssert.Contains("+++ new", toolResult.ResultContent.AsString());
        }

        [Test]
        public void ToolCallStillOpenAtTurnEnd_GetsSyntheticResultBeforeTheTurnResult()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call_3\",\"title\":\"Read\",\"kind\":\"read\"}");
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn"));
            List<StreamJsonMessage> messages = PanelMessages();
            Assert.IsInstanceOf<ResultMessage>(messages[messages.Count - 1]);
            var synthetic = messages[messages.Count - 2] as UserEchoMessage;
            Assert.IsNotNull(synthetic);
            Assert.AreEqual("call_3", synthetic.Content[0].ToolUseId);
        }

        [Test]
        public void UapToolCall_MapsToUnityOpsWireName()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call_4\",\"kind\":\"other\","
                + "\"title\":\"uap_scene_create_object (unity-ops MCP Server)\",\"rawInput\":{\"name\":\"Cube\"}}");
            Assert.AreEqual("mcp__unity-ops__uap_scene_create_object", LastPanel<AssistantMessage>().Content[0].Name);
        }

        [Test]
        public void PromptError_EmitsErrorResult()
        {
            CompleteHandshake();
            SendUser("hello");
            RespondError(AgentLine("session/prompt"), -32603, "model overloaded");
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.IsTrue(result.IsError);
            StringAssert.Contains("model overloaded", result.ResultText);
        }

        [Test]
        public void SteeringSend_QueuesUntilThePromptReturns_ThenOneResultForTheWholeTurn()
        {
            CompleteHandshake();
            SendUser("first");
            AcpInbound first = AgentLine("session/prompt");
            SendUser("second");
            Assert.AreEqual(1, _bridge.QueuedPromptCount);
            Assert.AreEqual(first.NumericId, AgentLine("session/prompt").NumericId, "no second prompt while one is running");
            Respond(first, JsonNode.NewObject().Set("stopReason", "end_turn"));
            Assert.AreEqual(0, CountPanel<ResultMessage>(), "the folded turn is still open");
            AcpInbound second = AgentLine("session/prompt");
            Assert.AreNotEqual(first.NumericId, second.NumericId);
            Assert.AreEqual("second", second.Params["prompt"][0]["text"].AsString());
            Respond(second, JsonNode.NewObject().Set("stopReason", "end_turn"));
            Assert.AreEqual(1, CountPanel<ResultMessage>());
        }

        [Test]
        public void Interrupt_SendsSessionCancel_AndAnswersTheControlRequest()
        {
            CompleteHandshake();
            SendUser("hello");
            _bridge.OnPanelLine(OutboundMessages.Interrupt("int_1"));
            AcpInbound cancel = LastAgentLine();
            Assert.AreEqual("session/cancel", cancel.Method);
            Assert.IsTrue(cancel.IsNotification);
            Assert.AreEqual("sess-1", cancel.Params["sessionId"].AsString());
            ControlResponseMessage response = LastPanel<ControlResponseMessage>();
            Assert.AreEqual("int_1", response.RequestId);
            Assert.IsTrue(response.Success);
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "cancelled"));
            Assert.AreEqual("cancelled", LastPanel<ResultMessage>().StopReason);
        }

        [Test]
        public void AgentExitMidTurn_SettlesWithAnErrorResult()
        {
            CompleteHandshake();
            SendUser("hello");
            _bridge.OnAgentExited();
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.IsTrue(result.IsError);
            Assert.IsFalse(_bridge.PromptInFlight);
        }

        // -- permissions -----------------------------------------------------------

        private AcpInbound RequestPermission(string id = "7", bool withAlways = true)
        {
            JsonNode options = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("optionId", "once").Set("name", "Allow").Set("kind", "allow_once"))
                .Add(JsonNode.NewObject().Set("optionId", "no").Set("name", "Reject").Set("kind", "reject_once"));
            if (withAlways)
            {
                options.Add(JsonNode.NewObject().Set("optionId", "always").Set("name", "Always").Set("kind", "allow_always"));
            }
            string line = AcpJsonRpc.Request(long.Parse(id), "session/request_permission", JsonNode.NewObject()
                .Set("sessionId", "sess-1")
                .Set("toolCall", JsonNode.NewObject()
                    .Set("toolCallId", "call_p")
                    .Set("title", "rm -rf build")
                    .Set("kind", "execute")
                    .Set("rawInput", JsonNode.NewObject().Set("command", "rm -rf build")))
                .Set("options", options));
            _bridge.OnAgentLine(line);
            return AcpJsonRpc.TryParse(line);
        }

        [Test]
        public void RequestPermission_BecomesCanUseTool_WithToolUseAnnouncedFirst()
        {
            CompleteHandshake();
            SendUser("hello");
            RequestPermission();
            List<StreamJsonMessage> messages = PanelMessages();
            var request = messages[messages.Count - 1] as ControlRequestMessage;
            Assert.IsNotNull(request);
            Assert.IsTrue(request.IsCanUseTool);
            StringAssert.StartsWith(AcpProtocolBridge.PermissionRequestIdPrefix, request.RequestId);
            Assert.AreEqual("Bash", request.CanUseTool.ToolName);
            Assert.AreEqual("rm -rf build", request.CanUseTool.DisplayName);
            Assert.AreEqual("rm -rf build", request.CanUseTool.Input["command"].AsString());
            Assert.AreEqual("call_p", request.CanUseTool.ToolUseId);
            Assert.AreEqual(1, request.CanUseTool.PermissionSuggestions.Count, "allow_always offered as an addRules suggestion");
            var announced = messages[messages.Count - 2] as AssistantMessage;
            Assert.IsNotNull(announced);
            Assert.AreEqual("call_p", announced.Content[0].Id);
        }

        [Test]
        public void RequestPermission_WithoutAlwaysOption_OffersNoSuggestions()
        {
            CompleteHandshake();
            SendUser("hello");
            RequestPermission("8", false);
            Assert.AreEqual(0, LastPanel<ControlRequestMessage>().CanUseTool.PermissionSuggestions.Count);
        }

        [Test]
        public void AllowOnce_SelectsAllowOnceOption()
        {
            CompleteHandshake();
            SendUser("hello");
            RequestPermission();
            string requestId = LastPanel<ControlRequestMessage>().RequestId;
            _bridge.OnPanelLine(OutboundMessages.AllowToolUse(requestId));
            AcpInbound answer = LastAgentLine();
            Assert.IsTrue(answer.IsResponse);
            Assert.AreEqual(7, answer.NumericId);
            Assert.AreEqual("selected", answer.Result["outcome"]["outcome"].AsString());
            Assert.AreEqual("once", answer.Result["outcome"]["optionId"].AsString());
        }

        [Test]
        public void AllowWithUpdatedPermissions_SelectsAllowAlwaysOption()
        {
            CompleteHandshake();
            SendUser("hello");
            RequestPermission();
            ControlRequestMessage request = LastPanel<ControlRequestMessage>();
            _bridge.OnPanelLine(OutboundMessages.AllowToolUse(request.RequestId, null,
                request.CanUseTool.PermissionSuggestions));
            Assert.AreEqual("always", LastAgentLine().Result["outcome"]["optionId"].AsString());
        }

        [Test]
        public void Deny_SelectsRejectOnce_AndDenyWithInterruptAlsoCancels()
        {
            CompleteHandshake();
            SendUser("hello");
            RequestPermission();
            string requestId = LastPanel<ControlRequestMessage>().RequestId;
            _bridge.OnPanelLine(OutboundMessages.DenyToolUse(requestId, "no thanks", true));
            AcpInbound cancel = LastAgentLine();
            Assert.AreEqual("session/cancel", cancel.Method);
            AcpInbound answer = AcpJsonRpc.TryParse(_toAgent[_toAgent.Count - 2]);
            Assert.AreEqual("no", answer.Result["outcome"]["optionId"].AsString());
        }

        [Test]
        public void InterruptWhilePermissionPending_CancelsThePermissionRequest()
        {
            CompleteHandshake();
            SendUser("hello");
            RequestPermission();
            _bridge.OnPanelLine(OutboundMessages.Interrupt("int_2"));
            AcpInbound answer = AcpJsonRpc.TryParse(_toAgent[_toAgent.Count - 2]);
            Assert.IsTrue(answer.IsResponse);
            Assert.AreEqual("cancelled", answer.Result["outcome"]["outcome"].AsString());
        }

        // -- control requests ------------------------------------------------------

        [Test]
        public void SetModel_ForwardsToSessionSetModel_AndAnswersOnSuccess()
        {
            CompleteHandshake();
            _bridge.OnPanelLine(OutboundMessages.SetModel("sm_1", "gemini-2.5-flash"));
            AcpInbound set = AgentLine("session/set_model");
            Assert.AreEqual("gemini-2.5-flash", set.Params["modelId"].AsString());
            Respond(set, JsonNode.NewObject());
            ControlResponseMessage response = LastPanel<ControlResponseMessage>();
            Assert.AreEqual("sm_1", response.RequestId);
            Assert.IsTrue(response.Success);
            Assert.AreEqual("gemini-2.5-flash", _bridge.CurrentModelId);
        }

        [Test]
        public void SetPermissionMode_UnknownMode_AnswersWithAnError()
        {
            CompleteHandshake();
            _bridge.OnPanelLine(OutboundMessages.SetPermissionMode("pm_1", "plan"));
            ControlResponseMessage response = LastPanel<ControlResponseMessage>();
            Assert.AreEqual("pm_1", response.RequestId);
            Assert.IsFalse(response.Success);
        }

        [Test]
        public void SetPermissionMode_KnownMode_ForwardsToSessionSetMode()
        {
            CompleteHandshake();
            _bridge.OnPanelLine(OutboundMessages.SetPermissionMode("pm_2", "bypassPermissions"));
            Assert.AreEqual("yolo", AgentLine("session/set_mode").Params["modeId"].AsString());
        }

        [Test]
        public void McpReconnect_IsRefusedWithAnErrorResponse()
        {
            CompleteHandshake();
            _bridge.OnPanelLine(OutboundMessages.McpReconnect("mcpr_1", "unity-ops"));
            Assert.IsFalse(LastPanel<ControlResponseMessage>().Success);
        }

        [Test]
        public void FsRequestFromAgent_GetsMethodNotFound()
        {
            CompleteHandshake();
            _bridge.OnAgentLine(AcpJsonRpc.Request(99, "fs/read_text_file",
                JsonNode.NewObject().Set("sessionId", "sess-1").Set("path", "/x")));
            AcpInbound reply = LastAgentLine();
            Assert.AreEqual(99, reply.NumericId);
            Assert.AreEqual(AcpJsonRpc.MethodNotFound, reply.ErrorCode);
        }

        [Test]
        public void NonJsonStdoutLine_IsLoggedNotForwarded()
        {
            int before = _toPanel.Count;
            _bridge.OnAgentLine("Welcome to Gemini CLI");
            Assert.AreEqual(before, _toPanel.Count);
            Assert.IsTrue(_log.Exists(delegate(string line) { return line.Contains("Welcome to Gemini CLI"); }));
        }

        // -- pure helpers ----------------------------------------------------------

        [TestCase("acceptEdits", "auto-edit")]
        [TestCase("bypassPermissions", "yolo")]
        [TestCase("default", "default")]
        [TestCase("plan", null)]
        public void ResolveModeId_MapsClaudeModesOntoAgentModes(string claudeMode, string expected)
        {
            var modes = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("default", "Default"),
                new KeyValuePair<string, string>("auto-edit", "Auto edit"),
                new KeyValuePair<string, string>("yolo", "YOLO")
            };
            Assert.AreEqual(expected, AcpProtocolBridge.ResolveModeId(claudeMode, modes));
        }

        [Test]
        public void ResolveModeId_CodexStyleModes()
        {
            var modes = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("read-only", "Read only"),
                new KeyValuePair<string, string>("auto", "Auto"),
                new KeyValuePair<string, string>("full-access", "Full access")
            };
            Assert.AreEqual("read-only", AcpProtocolBridge.ResolveModeId("default", modes));
            Assert.AreEqual("auto", AcpProtocolBridge.ResolveModeId("acceptEdits", modes));
            Assert.AreEqual("full-access", AcpProtocolBridge.ResolveModeId("bypassPermissions", modes));
            Assert.AreEqual("read-only", AcpProtocolBridge.ResolveModeId("plan", modes));
        }

        [TestCase("read", "Read")]
        [TestCase("edit", "Edit")]
        [TestCase("execute", "Bash")]
        [TestCase("fetch", "WebFetch")]
        [TestCase("search", "Search")]
        [TestCase("other", "Tool")]
        [TestCase(null, "Tool")]
        public void MapToolName_ByKind(string kind, string expected)
        {
            Assert.AreEqual(expected, AcpProtocolBridge.MapToolName(kind, "anything", null));
        }

        [Test]
        public void MapToolName_UapInRawInputName_WinsOverKind()
        {
            JsonNode raw = JsonNode.NewObject().Set("name", "uap_property_set");
            Assert.AreEqual("mcp__unity-ops__uap_property_set", AcpProtocolBridge.MapToolName("execute", "MCP tool", raw));
        }

        [TestCase("uap_scene_create_object", "uap_scene_create_object")]
        [TestCase("Call uap_prefab_apply_overrides now", "uap_prefab_apply_overrides")]
        [TestCase("xuap_no", null)]
        [TestCase("uap_", null)]
        [TestCase("", null)]
        public void ExtractUapToolName_FindsWholeIdentifiers(string text, string expected)
        {
            Assert.AreEqual(expected, AcpProtocolBridge.ExtractUapToolName(text));
        }

        [Test]
        public void LooksLikeAuthRequired_ByCodeOrMessage()
        {
            var byCode = AcpJsonRpc.TryParse(AcpJsonRpc.ErrorResponse(JsonNode.Of(1), -32000, "nope"));
            Assert.IsTrue(AcpProtocolBridge.LooksLikeAuthRequired(byCode));
            var byMessage = AcpJsonRpc.TryParse(AcpJsonRpc.ErrorResponse(JsonNode.Of(1), -32603, "Please login first"));
            Assert.IsTrue(AcpProtocolBridge.LooksLikeAuthRequired(byMessage));
            var other = AcpJsonRpc.TryParse(AcpJsonRpc.ErrorResponse(JsonNode.Of(1), -32603, "disk full"));
            Assert.IsFalse(AcpProtocolBridge.LooksLikeAuthRequired(other));
        }

        [Test]
        public void JsonRpc_TryParse_ClassifiesShapes()
        {
            Assert.IsNull(AcpJsonRpc.TryParse(""));
            Assert.IsNull(AcpJsonRpc.TryParse("banner text"));
            Assert.IsNull(AcpJsonRpc.TryParse("{\"jsonrpc\":\"2.0\"}"));
            AcpInbound request = AcpJsonRpc.TryParse("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"m\",\"params\":{}}");
            Assert.IsTrue(request.IsRequest);
            Assert.AreEqual(-1, request.NumericId);
            AcpInbound notification = AcpJsonRpc.TryParse("{\"jsonrpc\":\"2.0\",\"method\":\"m\"}");
            Assert.IsTrue(notification.IsNotification);
            AcpInbound response = AcpJsonRpc.TryParse("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":null}");
            Assert.IsTrue(response.IsResponse);
            Assert.AreEqual(3, response.NumericId);
            Assert.IsFalse(response.IsError);
        }
        // -- usage (design note 2026-09-10-acp-usage-display.md) -----------------------

        [Test]
        public void UsageUpdate_FeedsTheContextMeter_ThroughResultIterationsAndModelUsage()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"usage_update\",\"used\":53000,\"size\":200000}");
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn")
                .Set("usage", JsonNode.NewObject()
                    .Set("inputTokens", 1200).Set("outputTokens", 300)
                    .Set("cachedReadTokens", 50000).Set("cachedWriteTokens", 7)
                    .Set("thoughtTokens", 100).Set("totalTokens", 53000)));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(1200, result.Usage.InputTokens);
            Assert.AreEqual(300, result.Usage.OutputTokens);
            Assert.AreEqual(50000, result.Usage.CacheReadInputTokens);
            Assert.AreEqual(7, result.Usage.CacheCreationInputTokens);
            Assert.AreEqual(53000, result.Usage.LastIterationContextTokens, "context reading = usage_update.used");
            ModelUsage row = result.ModelUsage["gemini-2.5-pro"];
            Assert.AreEqual(200000, row.ContextWindow, "meter denominator = usage_update.size");
            Assert.AreEqual(1200, row.InputTokens);
            Assert.AreEqual(300, row.OutputTokens);
            Assert.AreEqual(53000, _bridge.ContextUsedTokens);
            Assert.AreEqual(200000, _bridge.ContextWindowTokens);
        }

        [Test]
        public void PromptResponseWithoutUsage_FallsBackToGeminiQuotaMeta_TokensOnlyNoMeter()
        {
            CompleteHandshake();
            SendUser("hello");
            Respond(AgentLine("session/prompt"), JsonParser.Parse(
                "{\"stopReason\":\"end_turn\",\"_meta\":{\"quota\":{\"token_count\":{\"input_tokens\":800,\"output_tokens\":90},"
                + "\"model_usage\":[{\"model\":\"gemini-2.5-pro\",\"token_count\":{\"input_tokens\":700,\"output_tokens\":80}},"
                + "{\"model\":\"gemini-2.5-flash\",\"token_count\":{\"input_tokens\":100,\"output_tokens\":10}}]}}}"));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(800, result.Usage.InputTokens);
            Assert.AreEqual(90, result.Usage.OutputTokens);
            Assert.AreEqual(-1, result.Usage.LastIterationContextTokens, "no usage_update: no context reading");
            Assert.AreEqual(2, result.ModelUsage.Count);
            Assert.AreEqual(700, result.ModelUsage["gemini-2.5-pro"].InputTokens);
            Assert.AreEqual(10, result.ModelUsage["gemini-2.5-flash"].OutputTokens);
            Assert.AreEqual(0, result.ModelUsage["gemini-2.5-pro"].ContextWindow, "size unknown keeps the meter hidden");
        }

        [Test]
        public void CodexQuotaMeta_CamelCaseTokenCount_IsReadWhenUsageIsAbsent()
        {
            CompleteHandshake();
            SendUser("hello");
            Respond(AgentLine("session/prompt"), JsonParser.Parse(
                "{\"stopReason\":\"end_turn\",\"_meta\":{\"quota\":{\"token_count\":{\"totalTokens\":4321,\"inputTokens\":4000,"
                + "\"cachedInputTokens\":3500,\"outputTokens\":321,\"reasoningOutputTokens\":20},\"model_usage\":[]}}}"));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(4000, result.Usage.InputTokens);
            Assert.AreEqual(321, result.Usage.OutputTokens);
            Assert.AreEqual(3500, result.Usage.CacheReadInputTokens);
        }

        [Test]
        public void UsageUpdate_MidTurn_RidesOnTheNextAssistantMessage_WithoutInflatingTheCounter()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"usage_update\",\"used\":4200,\"size\":100000}");
            Notify("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hi\"}}");
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn"));
            AssistantMessage assistant = LastPanel<AssistantMessage>();
            Assert.AreEqual(4200, assistant.Usage.CacheReadInputTokens, "live context reading");
            Assert.AreEqual(0, assistant.Usage.InputTokens, "the in-flight token counter must not grow by the context");
            Assert.AreEqual(0, assistant.Usage.OutputTokens);
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(4200, result.Usage.LastIterationContextTokens);
            Assert.AreEqual(100000, result.ModelUsage["gemini-2.5-pro"].ContextWindow);
        }

        [Test]
        public void UsageUpdate_CostInUsd_BecomesTotalCostUsd_OtherCurrenciesAreDropped()
        {
            CompleteHandshake();
            SendUser("hello");
            Notify("{\"sessionUpdate\":\"usage_update\",\"used\":10,\"size\":1000,\"cost\":{\"amount\":0.12,\"currency\":\"USD\"}}");
            Notify("{\"sessionUpdate\":\"usage_update\",\"used\":20,\"size\":1000,\"cost\":{\"amount\":9.5,\"currency\":\"EUR\"}}");
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn"));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(0.12, result.TotalCostUsd, 0.0001);
            Assert.AreEqual(20, result.Usage.LastIterationContextTokens, "the reading itself still updates");
        }

        [Test]
        public void NoUsageReported_ResultCarriesZeros_AndNoModelUsageRows()
        {
            CompleteHandshake();
            SendUser("hello");
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn"));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(0, result.Usage.InputTokens);
            Assert.AreEqual(-1, result.Usage.LastIterationContextTokens);
            Assert.AreEqual(0, result.ModelUsage.Count, "no rows: the meter stays hidden rather than reading 0%");
            Assert.AreEqual(0.0, result.TotalCostUsd);
        }

        [Test]
        public void SteeringSend_SumsUsageAcrossTheFoldedPrompts()
        {
            CompleteHandshake();
            SendUser("first");
            AcpInbound first = AgentLine("session/prompt");
            SendUser("second");
            Respond(first, JsonNode.NewObject().Set("stopReason", "end_turn")
                .Set("usage", JsonNode.NewObject().Set("inputTokens", 100).Set("outputTokens", 10)));
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn")
                .Set("usage", JsonNode.NewObject().Set("inputTokens", 200).Set("outputTokens", 20)));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(300, result.Usage.InputTokens);
            Assert.AreEqual(30, result.Usage.OutputTokens);
            SendUser("third");
            Respond(AgentLine("session/prompt"), JsonNode.NewObject().Set("stopReason", "end_turn")
                .Set("usage", JsonNode.NewObject().Set("inputTokens", 5).Set("outputTokens", 1)));
            Assert.AreEqual(5, LastPanel<ResultMessage>().Usage.InputTokens, "a new panel turn starts from zero");
        }

        [Test]
        public void UsageUpdate_DuringSessionLoad_IsKeptAsTheFirstReading()
        {
            _spec.ResumeSessionId = "sess-1";
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult(loadSession: true));
            Notify("{\"sessionUpdate\":\"usage_update\",\"used\":77000,\"size\":128000}");
            Assert.AreEqual(77000, _bridge.ContextUsedTokens);
            Assert.AreEqual(128000, _bridge.ContextWindowTokens);
        }

        // -- Grok Build's usage shape (no usage_update; _meta everywhere) -------------

        private static JsonNode GrokSessionResult()
        {
            return JsonNode.NewObject()
                .Set("sessionId", "sess-1")
                .Set("models", JsonNode.NewObject()
                    .Set("currentModelId", "grok-build")
                    .Set("availableModels", JsonNode.NewArray()
                        .Add(JsonNode.NewObject().Set("modelId", "grok-build").Set("name", "Grok Build")
                            .Set("_meta", JsonNode.NewObject().Set("totalContextTokens", 256000).Set("agentType", "coding")))
                        .Add(JsonNode.NewObject().Set("modelId", "grok-4.3").Set("name", "Grok 4.3")
                            .Set("_meta", JsonNode.NewObject().Set("totalContextTokens", 2000000)))));
        }

        [Test]
        public void Grok_CatalogTotalContextTokens_AndNotificationMetaTotalTokens_DriveTheMeter()
        {
            _spec.Backend = AgentBackend.GrokBuild;
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult());
            Respond(AgentLine("session/new"), GrokSessionResult());
            Assert.AreEqual(256000, _bridge.ContextWindowTokens, "catalog window for the current model");
            SendUser("hello");
            NotifyWithMeta("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hi\"}}",
                "{\"totalTokens\":41000,\"eventId\":\"e1\",\"agentTimestampMs\":1,\"promptId\":\"p1\"}");
            NotifyWithMeta("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\" there\"}}",
                "{\"totalTokens\":41250,\"eventId\":\"e2\",\"agentTimestampMs\":2}");
            // A tool call flushes the streamed text into an assistant line
            // NOW (before the prompt response's own reading arrives).
            NotifyWithMeta("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"c1\",\"title\":\"ls\",\"kind\":\"execute\",\"status\":\"in_progress\"}",
                "{\"totalTokens\":41250,\"eventId\":\"e3\",\"agentTimestampMs\":3}");
            Respond(AgentLine("session/prompt"), JsonParser.Parse(
                "{\"stopReason\":\"end_turn\",\"_meta\":{\"sessionId\":\"sess-1\",\"requestId\":\"p1\",\"promptId\":\"p1\","
                + "\"totalTokens\":41300,\"modelId\":\"grok-build\",\"inputTokens\":40000,\"outputTokens\":300,\"cachedReadTokens\":38000,\"reasoningTokens\":20,"
                + "\"usage\":{\"inputTokens\":80000,\"outputTokens\":600,\"totalTokens\":80600,\"cachedReadTokens\":70000,\"cacheCreationTokens\":0,"
                + "\"reasoningTokens\":40,\"modelCalls\":2,\"apiDurationMs\":900,\"costUsdTicks\":1200000000,"
                + "\"modelUsage\":{\"grok-build\":{\"inputTokens\":80000,\"outputTokens\":600,\"totalTokens\":80600,\"cachedReadTokens\":70000,\"cacheCreationTokens\":0,\"reasoningTokens\":40,\"modelCalls\":2,\"apiDurationMs\":900}},"
                + "\"numTurns\":2}}}"));
            AssistantMessage assistant = LastPanel<AssistantMessage>();
            Assert.AreEqual(41250, assistant.Usage.CacheReadInputTokens, "live reading = latest notification _meta.totalTokens");
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(41300, result.Usage.LastIterationContextTokens, "end-of-turn reading = PromptResponse _meta.totalTokens");
            Assert.AreEqual(10000, result.Usage.InputTokens, "whole-prompt input minus the cache reads it folds in");
            Assert.AreEqual(600, result.Usage.OutputTokens);
            Assert.AreEqual(70000, result.Usage.CacheReadInputTokens);
            ModelUsage row = result.ModelUsage["grok-build"];
            Assert.AreEqual(256000, row.ContextWindow, "meter denominator from the catalog");
            Assert.AreEqual(10000, row.InputTokens);
            Assert.AreEqual(600, row.OutputTokens);
            Assert.AreEqual(0.12, result.TotalCostUsd, 0.0001, "costUsdTicks / 1e10");
        }

        [Test]
        public void Grok_LastCallMetaOnly_StillCountsTokens_AndZeroTotalTokensDoesNotWipeTheReading()
        {
            _spec.Backend = AgentBackend.GrokBuild;
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult());
            Respond(AgentLine("session/new"), GrokSessionResult());
            SendUser("hello");
            NotifyWithMeta("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hi\"}}",
                "{\"totalTokens\":5000}");
            Respond(AgentLine("session/prompt"), JsonParser.Parse(
                "{\"stopReason\":\"cancelled\",\"_meta\":{\"totalTokens\":0,\"modelId\":\"grok-build\",\"inputTokens\":4000,\"outputTokens\":50,\"cachedReadTokens\":3000,\"reasoningTokens\":0}}"));
            ResultMessage result = LastPanel<ResultMessage>();
            Assert.AreEqual(4000, result.Usage.InputTokens);
            Assert.AreEqual(50, result.Usage.OutputTokens);
            Assert.AreEqual(3000, result.Usage.CacheReadInputTokens);
            Assert.AreEqual(5000, result.Usage.LastIterationContextTokens, "totalTokens 0 (removed from queue) keeps the last reading");
            Assert.AreEqual(256000, result.ModelUsage["grok-build"].ContextWindow);
        }

        [Test]
        public void Grok_SetModel_SwitchesTheCatalogWindow_UntilAUsageUpdateOverridesIt()
        {
            _spec.Backend = AgentBackend.GrokBuild;
            _bridge.OnPanelLine(OutboundMessages.Initialize("req_1"));
            Respond(AgentLine("initialize"), InitializeResult());
            Respond(AgentLine("session/new"), GrokSessionResult());
            _bridge.OnPanelLine(OutboundMessages.SetModel("m_1", "grok-4.3"));
            Respond(AgentLine("session/set_model"), JsonNode.NewObject());
            Assert.AreEqual(2000000, _bridge.ContextWindowTokens);
            Notify("{\"sessionUpdate\":\"usage_update\",\"used\":10,\"size\":123456}");
            Assert.AreEqual(123456, _bridge.ContextWindowTokens, "usage_update.size describes the live window");
        }

    }
}
