using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Protocol-layer regression tests: every fixture line (real claude
    /// v2.1.218 output) must dispatch to the correct typed message, unknown
    /// types must be ignored (not dropped), and drops must be logged through
    /// the injectable logger without ever throwing.
    /// </summary>
    public class ProtocolMappingTests
    {
        private List<string> _log;

        private void LogSink(string message)
        {
            _log.Add(message);
        }

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
        }

        // "Not logged in <U+00B7> Please run /login" (middle dot built at runtime
        // to keep this source file strict ASCII).
        private static string NotLoggedInText()
        {
            return "Not logged in " + (char)0x00B7 + " Please run /login";
        }

        // ---------------------------------------------------------------
        // Whole-fixture dispatch
        // ---------------------------------------------------------------

        [Test]
        public void EveryFixtureLine_DispatchesToTypedMessage_NothingDropped()
        {
            foreach (string fixture in FixtureLoader.AllFixtureNames)
            {
                var lines = FixtureLoader.ReadLines(fixture);
                for (int i = 0; i < lines.Count; i++)
                {
                    StreamJsonMessage msg = StreamJsonMessage.ParseLine(lines[i], LogSink);
                    Assert.IsNotNull(msg, fixture + " line " + (i + 1) + " should map");
                    Assert.AreNotEqual(InboundType.Unknown, msg.Type,
                        fixture + " line " + (i + 1) + " should map to a known type");
                }
            }
            Assert.IsEmpty(_log, "no fixture line may be dropped: " + string.Join("; ", _log));
        }

        // ---------------------------------------------------------------
        // system/init
        // ---------------------------------------------------------------

        [Test]
        public void SystemInit_Out1_MapsAllFields()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("out1.jsonl")[0], LogSink) as SystemInitMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.SystemInit, msg.Type);
            Assert.AreEqual("814fe056-eea6-42c5-b339-32e680525e25", msg.SessionId);
            Assert.AreEqual("claude-opus-4-8[1m]", msg.Model);
            Assert.AreEqual("default", msg.PermissionMode);
            Assert.AreEqual("2.1.218", msg.ClaudeCodeVersion);
            Assert.AreEqual("none", msg.ApiKeySource);
            StringAssert.Contains("UnityAgentPanel", msg.Cwd);
            Assert.Contains("Bash", msg.Tools);
            Assert.Contains("Read", msg.Tools);
            Assert.IsNotEmpty(msg.SlashCommands);
            Assert.AreEqual(0, msg.McpServers.Length);
            Assert.Contains("interrupt_receipt_v1", msg.Capabilities);
        }

        [Test]
        public void SystemInit_Out2_ParsesTheAgentsArray()
        {
            // docs/design-notes/2026-08-01-model-settings-rework.md work
            // item C: system/init.agents[] feeds PanelSettings.
            // agentTypeCatalog (AgentHub.RefreshAgentTypeCatalogCache) so
            // the per-type override rows' agent-name PopupField never
            // needs a hard-coded or free-typed type name.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("out2.jsonl")[0], LogSink) as SystemInitMessage;

            Assert.IsNotNull(msg);
            Assert.IsNotNull(msg.Agents);
            Assert.Contains("general-purpose", msg.Agents);
            Assert.Contains("Explore", msg.Agents);
            Assert.Contains("Plan", msg.Agents);
        }

        [Test]
        public void SystemInit_InitFlagsFixture_ReflectsFlags()
        {
            // init_flags.json: captured with --permission-mode plan,
            // --model sonnet and a dummy --mcp-config server.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("init_flags.json")[0], LogSink) as SystemInitMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("plan", msg.PermissionMode);
            Assert.AreEqual("claude-sonnet-5", msg.Model);
            Assert.AreEqual(1, msg.McpServers.Length);
            Assert.AreEqual("dummy", msg.McpServers[0].Name);
            Assert.AreEqual("pending", msg.McpServers[0].Status);
        }

        [Test]
        public void SystemInit_MissingAgentsField_YieldsEmptyArrayNotNull()
        {
            // Tolerant parse (docs/design-notes/2026-08-01-model-settings-
            // rework.md work item C): a missing/malformed agents[] must
            // never surface as null to AgentHub.RefreshAgentTypeCatalogCache,
            // which treats null specially (no-op, never clobbers a cached
            // list) -- a genuinely-empty array must behave identically.
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}", LogSink) as SystemInitMessage;

            Assert.IsNotNull(msg);
            Assert.IsNotNull(msg.Agents);
            Assert.AreEqual(0, msg.Agents.Length);
        }

        [Test]
        public void SystemInit_MissingSessionId_IsDroppedWithLoggedReason()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"m\"}", LogSink);
            Assert.IsNull(msg);
            Assert.AreEqual(1, _log.Count);
            StringAssert.Contains("session_id", _log[0]);
        }

        [Test]
        public void System_UnhandledSubtype_MapsToSystemAndIsIgnorable()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"informational\",\"session_id\":\"s\"}",
                LogSink);
            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.System, msg.Type);
            Assert.IsEmpty(_log);
        }

        // ---------------------------------------------------------------
        // system/status (design note 2026-09-10-compacting-indicator.md)
        // ---------------------------------------------------------------

        [Test]
        public void Status_Compacting_MapsToTypedMessage()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"compacting\","
                + "\"uuid\":\"u1\",\"session_id\":\"s\"}",
                LogSink) as SystemStatusMessage;
            Assert.IsNotNull(msg, "system/status must map to its own typed message now");
            Assert.AreEqual(InboundType.SystemStatus, msg.Type);
            Assert.AreEqual("compacting", msg.Status);
            Assert.IsTrue(msg.IsCompacting);
            Assert.AreEqual("s", msg.SessionId);
            Assert.AreEqual("u1", msg.Uuid);
            Assert.IsEmpty(_log);
        }

        [Test]
        public void Status_Requesting_RealCapture_IsNotCompacting()
        {
            // Tests/Editor/Fixtures/success_bidi_inbound.jsonl line 3.
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"requesting\","
                + "\"uuid\":\"0de853bd-e632-4f99-9941-50d28c37ce7e\","
                + "\"session_id\":\"2df17788-3181-42aa-83b8-7a52d985214d\"}",
                LogSink) as SystemStatusMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("requesting", msg.Status);
            Assert.IsFalse(msg.IsCompacting);
            Assert.IsEmpty(_log);
        }

        [Test]
        public void Status_NullOrAbsent_MapsEmpty_NeverDrops()
        {
            var nulled = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"status\",\"status\":null,\"session_id\":\"s\"}",
                LogSink) as SystemStatusMessage;
            Assert.IsNotNull(nulled);
            Assert.AreEqual(string.Empty, nulled.Status);
            Assert.IsFalse(nulled.IsCompacting);

            var bare = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"status\"}", LogSink) as SystemStatusMessage;
            Assert.IsNotNull(bare);
            Assert.AreEqual(string.Empty, bare.Status);
            Assert.IsFalse(bare.IsCompacting);
            Assert.IsEmpty(_log);
        }

        // ---------------------------------------------------------------
        // system/compact_boundary (design note 2026-09-07-slash-commands-
        // and-compaction.md section 2.1)
        // ---------------------------------------------------------------

        [Test]
        public void CompactBoundary_WireShape_MapsTriggerAndPreTokens()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s\","
                + "\"compact_metadata\":{\"trigger\":\"manual\",\"pre_tokens\":84213},\"uuid\":\"u1\"}",
                LogSink) as SystemCompactBoundaryMessage;
            Assert.IsNotNull(msg, "compact_boundary must map to its own typed message now");
            Assert.AreEqual(InboundType.SystemCompactBoundary, msg.Type);
            Assert.AreEqual("manual", msg.Trigger);
            Assert.IsTrue(msg.IsManual);
            Assert.AreEqual(84213L, msg.PreTokens);
            Assert.AreEqual("s", msg.SessionId);
            Assert.AreEqual("u1", msg.Uuid);
            Assert.IsEmpty(_log);
        }

        [Test]
        public void CompactBoundary_OnDiskCamelCase_AlsoMaps()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"content\":\"Conversation compacted\","
                + "\"compactMetadata\":{\"trigger\":\"auto\",\"preTokens\":150000},\"isMeta\":false}",
                LogSink) as SystemCompactBoundaryMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("auto", msg.Trigger);
            Assert.IsFalse(msg.IsManual);
            Assert.AreEqual(150000L, msg.PreTokens);
        }

        [Test]
        public void CompactBoundary_WithoutMetadata_StillMaps_AsUnknownAutoLike()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s\"}",
                LogSink) as SystemCompactBoundaryMessage;
            Assert.IsNotNull(msg, "the boundary itself is the fact; metadata only decorates it");
            Assert.AreEqual(string.Empty, msg.Trigger);
            Assert.IsFalse(msg.IsManual);
            Assert.AreEqual(-1L, msg.PreTokens);
            Assert.IsEmpty(_log);
        }

        [Test]
        public void CompactBoundary_NonNumericPreTokens_ReadsMinusOne()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\","
                + "\"compact_metadata\":{\"trigger\":\"manual\",\"pre_tokens\":\"lots\"}}",
                LogSink) as SystemCompactBoundaryMessage;
            Assert.IsNotNull(msg);
            Assert.IsTrue(msg.IsManual);
            Assert.AreEqual(-1L, msg.PreTokens);
        }

        // ---------------------------------------------------------------
        // assistant
        // ---------------------------------------------------------------

        [Test]
        public void Assistant_Out1_MapsContentUsageAndError()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("out1.jsonl")[1], LogSink) as AssistantMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.Assistant, msg.Type);
            Assert.AreEqual("7b03ffe2-e9a3-4da3-ac81-ef2efb78d9c2", msg.MessageId);
            Assert.AreEqual("<synthetic>", msg.Model);
            Assert.IsTrue(msg.IsSynthetic);
            Assert.AreEqual("authentication_failed", msg.Error);
            Assert.IsNull(msg.ParentToolUseId);
            Assert.AreEqual("814fe056-eea6-42c5-b339-32e680525e25", msg.SessionId);

            Assert.AreEqual(1, msg.Content.Length);
            Assert.AreEqual(ContentBlockType.Text, msg.Content[0].Type);
            Assert.AreEqual(NotLoggedInText(), msg.Content[0].Text);

            Assert.IsNotNull(msg.Usage);
            Assert.AreEqual(0, msg.Usage.InputTokens);
            Assert.AreEqual(0, msg.Usage.OutputTokens);
        }

        [Test]
        public void Assistant_ToolUseBlock_Maps()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\","
                + "\"input\":{\"command\":\"git status\"}},{\"type\":\"text\",\"text\":\"ok\"},"
                + "{\"type\":\"brand_new_block\",\"x\":1}]}}",
                LogSink) as AssistantMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(3, msg.Content.Length);
            Assert.AreEqual(ContentBlockType.ToolUse, msg.Content[0].Type);
            Assert.AreEqual("toolu_1", msg.Content[0].Id);
            Assert.AreEqual("Bash", msg.Content[0].Name);
            Assert.AreEqual("git status", msg.Content[0].Input["command"].AsString());
            Assert.AreEqual(ContentBlockType.Text, msg.Content[1].Type);
            Assert.AreEqual(ContentBlockType.Unknown, msg.Content[2].Type);
            Assert.IsEmpty(_log, "unknown content blocks are ignored, not dropped");
        }

        // ---------------------------------------------------------------
        // redacted_thinking (design note 2026-08-01-thinking-content-loss.md
        // section 6 -- no real fixture exists yet, so this is a synthetic
        // line shaped exactly like the API spec: type + an opaque "data"
        // string, no readable text).
        // ---------------------------------------------------------------

        [Test]
        public void Assistant_RedactedThinkingBlock_MapsToRedactedThinkingType()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                + "\"content\":[{\"type\":\"redacted_thinking\",\"data\":\"EmwKAhgBEgy3v"
                + "ZcSlmb3S7oaGgw=\"},{\"type\":\"text\",\"text\":\"ok\"}]}}",
                LogSink) as AssistantMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(2, msg.Content.Length);
            Assert.AreEqual(ContentBlockType.RedactedThinking, msg.Content[0].Type);
            // No readable text is ever exposed for this block type.
            Assert.IsNull(msg.Content[0].Thinking);
            Assert.AreEqual(ContentBlockType.Text, msg.Content[1].Type);
            Assert.IsEmpty(_log, "redacted_thinking is a known type, not a drop");
        }

        [Test]
        public void Assistant_MissingMessage_IsDroppedWithLoggedReason()
        {
            var msg = StreamJsonMessage.ParseLine("{\"type\":\"assistant\"}", LogSink);
            Assert.IsNull(msg);
            Assert.AreEqual(1, _log.Count);
            StringAssert.Contains("message", _log[0]);
        }

        // ---------------------------------------------------------------
        // user (echo / tool_result)
        // ---------------------------------------------------------------

        [Test]
        public void UserEcho_OutBidi_IsReplayAck()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("out_bidi.jsonl")[2], LogSink) as UserEchoMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.User, msg.Type);
            Assert.IsTrue(msg.IsReplay);
            Assert.AreEqual("19fe4284-bb73-4dcb-841e-5e3ce932e834", msg.SessionId);
            Assert.AreEqual(1, msg.Content.Length);
            Assert.AreEqual(ContentBlockType.Text, msg.Content[0].Type);
            Assert.AreEqual("Reply with exactly: OK", msg.Content[0].Text);
            Assert.IsFalse(msg.HasToolResult);
        }

        [Test]
        public void User_ToolResultBlock_Maps()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                + "[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\","
                + "\"content\":\"done\",\"is_error\":false}]}}",
                LogSink) as UserEchoMessage;

            Assert.IsNotNull(msg);
            Assert.IsFalse(msg.IsReplay);
            Assert.IsTrue(msg.HasToolResult);
            Assert.AreEqual("toolu_1", msg.Content[0].ToolUseId);
            Assert.AreEqual("done", msg.Content[0].ResultContent.AsString());
            Assert.IsFalse(msg.Content[0].IsError);
        }

        [Test]
        public void User_PlainStringContent_MapsToSingleTextBlock()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}",
                LogSink) as UserEchoMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual(1, msg.Content.Length);
            Assert.AreEqual("hello", msg.Content[0].Text);
        }

        // ---------------------------------------------------------------
        // result
        // ---------------------------------------------------------------

        [Test]
        public void Result_Out1_MapsUsageCostAndDenials()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("out1.jsonl")[2], LogSink) as ResultMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.Result, msg.Type);
            Assert.AreEqual("success", msg.Subtype);
            Assert.IsTrue(msg.IsError);
            Assert.AreEqual(NotLoggedInText(), msg.ResultText);
            Assert.AreEqual(0.0, msg.TotalCostUsd, 1e-12);
            Assert.AreEqual(412, msg.DurationMs);
            Assert.AreEqual(0, msg.DurationApiMs);
            Assert.AreEqual(1, msg.NumTurns);
            Assert.AreEqual("api_error", msg.TerminalReason);
            Assert.AreEqual("814fe056-eea6-42c5-b339-32e680525e25", msg.SessionId);

            Assert.IsNotNull(msg.Usage);
            Assert.AreEqual(0, msg.Usage.OutputTokens);
            Assert.AreEqual("standard", msg.Usage.ServiceTier);
            Assert.AreEqual(0, msg.ModelUsage.Count);
            Assert.AreEqual(0, msg.PermissionDenials.Length);
        }

        [Test]
        public void Result_ResumeFixture_Maps()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("resume_out.json")[0], LogSink) as ResultMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("19fe4284-bb73-4dcb-841e-5e3ce932e834", msg.SessionId);
            Assert.AreEqual(221, msg.DurationMs);
        }

        [Test]
        public void Result_ModelUsageEntries_Map()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,"
                + "\"modelUsage\":{\"claude-sonnet-5\":{\"inputTokens\":10,"
                + "\"outputTokens\":20,\"cacheReadInputTokens\":5,"
                + "\"cacheCreationInputTokens\":3,\"webSearchRequests\":1,"
                + "\"costUSD\":0.012,\"contextWindow\":200000,\"maxOutputTokens\":64000}}}",
                LogSink) as ResultMessage;

            Assert.IsNotNull(msg);
            ModelUsage usage = msg.ModelUsage["claude-sonnet-5"];
            Assert.AreEqual(10, usage.InputTokens);
            Assert.AreEqual(20, usage.OutputTokens);
            Assert.AreEqual(200000, usage.ContextWindow);
            Assert.AreEqual(0.012, usage.CostUsd, 1e-12);
        }

        // ---------------------------------------------------------------
        // stream_event
        // ---------------------------------------------------------------

        [Test]
        public void StreamEvent_TextDelta_Extracts()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\","
                + "\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}}",
                LogSink) as StreamEventMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("content_block_delta", msg.EventType);
            Assert.AreEqual(0, msg.BlockIndex);
            string text;
            Assert.IsTrue(msg.TryGetTextDelta(out text));
            Assert.AreEqual("Hel", text);
            string thinking;
            Assert.IsFalse(msg.TryGetThinkingDelta(out thinking));
        }

        [Test]
        public void StreamEvent_NonDeltaEvents_AreCarriedButNotExtracted()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\","
                + "\"message\":{\"id\":\"m\"}}}",
                LogSink) as StreamEventMessage;
            Assert.IsNotNull(msg);
            string text;
            Assert.IsFalse(msg.TryGetTextDelta(out text));
            Assert.IsEmpty(_log);
        }

        [Test]
        public void StreamEvent_ThinkingBlockStart_TaskSubagentFixture_IsDetected()
        {
            // Line 6: content_block_start of a thinking block (design note
            // 2026-08-01-thinking-content-loss.md section 5 point 2).
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[5], LogSink) as StreamEventMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("content_block_start", msg.EventType);
            Assert.IsTrue(msg.IsThinkingBlockStart());
        }

        [Test]
        public void StreamEvent_ToolUseBlockStart_TaskSubagentFixture_IsNotAThinkingBlockStart()
        {
            // Line 14: content_block_start of a tool_use block.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[13], LogSink) as StreamEventMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("content_block_start", msg.EventType);
            Assert.IsFalse(msg.IsThinkingBlockStart());
        }

        [Test]
        public void StreamEvent_ThinkingDeltaEvent_IsNotAThinkingBlockStart()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\","
                + "\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"\","
                + "\"estimated_tokens\":5}}}",
                LogSink) as StreamEventMessage;
            Assert.IsNotNull(msg);
            Assert.IsFalse(msg.IsThinkingBlockStart());
        }

        // ---------------------------------------------------------------
        // control_request / control_response
        // ---------------------------------------------------------------

        [Test]
        public void ControlResponse_OutBidi_InitializePayloadMaps()
        {
            // out_bidi.jsonl line 1 is the REAL initialize response.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("out_bidi.jsonl")[0], LogSink) as ControlResponseMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.ControlResponse, msg.Type);
            Assert.AreEqual("req_1", msg.RequestId);
            Assert.IsTrue(msg.Success);
            Assert.IsNull(msg.Error);

            JsonNode payload = msg.Response;
            Assert.IsTrue(payload.IsObject);
            Assert.Greater(payload["models"].Count, 0, "initialize must list models");
            Assert.AreEqual("default", payload["models"][0]["value"].AsString());
            Assert.AreEqual("claude-opus-4-8[1m]",
                payload["models"][0]["resolvedModel"].AsString());
            Assert.Greater(payload["commands"].Count, 0, "initialize must list slash commands");
            Assert.AreEqual("none", payload["account"]["tokenSource"].AsString());
            Assert.AreEqual("firstParty", payload["account"]["apiProvider"].AsString());
            Assert.AreEqual(51324, payload["pid"].AsInt());
        }

        [Test]
        public void ControlRequest_CanUseTool_Maps()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"control_request\",\"request_id\":\"abc-1\",\"request\":"
                + "{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\","
                + "\"display_name\":\"Bash\",\"input\":{\"command\":\"git status\"},"
                + "\"tool_use_id\":\"toolu_9\",\"permission_suggestions\":[{\"rule\":\"x\"}],"
                + "\"requires_user_interaction\":true}}",
                LogSink) as ControlRequestMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("abc-1", msg.RequestId);
            Assert.IsTrue(msg.IsCanUseTool);
            Assert.IsNotNull(msg.CanUseTool);
            Assert.AreEqual("Bash", msg.CanUseTool.ToolName);
            Assert.AreEqual("git status", msg.CanUseTool.Input["command"].AsString());
            Assert.AreEqual("toolu_9", msg.CanUseTool.ToolUseId);
            Assert.AreEqual(1, msg.CanUseTool.PermissionSuggestions.Count);
            Assert.IsTrue(msg.CanUseTool.RequiresUserInteraction);
        }

        [Test]
        public void ControlRequest_UnhandledSubtype_StillMapsWithoutCanUseTool()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"control_request\",\"request_id\":\"h1\","
                + "\"request\":{\"subtype\":\"hook_callback\",\"callback_id\":\"cb\"}}",
                LogSink) as ControlRequestMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("hook_callback", msg.Subtype);
            Assert.IsNull(msg.CanUseTool);
            Assert.IsEmpty(_log);
        }

        // ---------------------------------------------------------------
        // Subagent (task_subagent_inbound.jsonl -- Phase 4, R02c)
        // ---------------------------------------------------------------

        [Test]
        public void Assistant_AgentToolUse_HasNullParent_TaskSubagentFixture()
        {
            // Line 22: the spawning "Agent" tool_use, top-level (parent_tool_use_id null).
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[21], LogSink) as AssistantMessage;

            Assert.IsNotNull(msg);
            Assert.IsNull(msg.ParentToolUseId);
            Assert.AreEqual(1, msg.Content.Length);
            Assert.AreEqual(ContentBlockType.ToolUse, msg.Content[0].Type);
            Assert.AreEqual("Agent", msg.Content[0].Name);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.Content[0].Id);
            Assert.AreEqual("general-purpose", msg.Content[0].Input["subagent_type"].AsString());
            Assert.AreEqual("Run echo fixture command", msg.Content[0].Input["description"].AsString());
        }

        [Test]
        public void Assistant_NestedToolUse_CarriesParentToolUseId_TaskSubagentFixture()
        {
            // Line 29: the subagent's own Bash tool_use, parent_tool_use_id
            // set to the spawning Agent tool_use's id.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[28], LogSink) as AssistantMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.ParentToolUseId);
            Assert.AreEqual(1, msg.Content.Length);
            Assert.AreEqual("Bash", msg.Content[0].Name);
        }

        [Test]
        public void User_NestedToolResult_CarriesParentToolUseId_TaskSubagentFixture()
        {
            // Line 30: the subagent's own tool_result, same parent id.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[29], LogSink) as UserEchoMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.ParentToolUseId);
            Assert.IsTrue(msg.HasToolResult);
            Assert.AreEqual("SUBAGENT_FIXTURE_OK", msg.Content[0].ResultContent.AsString());
        }

        [Test]
        public void User_TopLevelToolResult_HasNullParent_TaskSubagentFixture()
        {
            // Line 33: the Agent tool_result closing at the top level.
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[32], LogSink) as UserEchoMessage;

            Assert.IsNotNull(msg);
            Assert.IsNull(msg.ParentToolUseId);
            Assert.IsTrue(msg.HasToolResult);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.Content[0].ToolUseId);
        }

        [Test]
        public void SystemTaskEvent_TaskStarted_Maps()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[22], LogSink) as SystemTaskEventMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.SystemTaskEvent, msg.Type);
            Assert.AreEqual("task_started", msg.Subtype);
            Assert.AreEqual("ac1ae679e88d103e8", msg.TaskId);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.ToolUseId);
            Assert.AreEqual("Run echo fixture command", msg.Description);
            Assert.AreEqual("general-purpose", msg.SubagentType);
        }

        [Test]
        public void SystemTaskEvent_TaskProgress_MapsUsage()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[27], LogSink) as SystemTaskEventMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("task_progress", msg.Subtype);
            Assert.AreEqual("ac1ae679e88d103e8", msg.TaskId);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.ToolUseId);
            Assert.AreEqual("Running Echo fixture string", msg.Description);
            Assert.AreEqual("Bash", msg.LastToolName);
            Assert.AreEqual(20406, msg.TotalTokens);
            Assert.AreEqual(1, msg.ToolUses);
            Assert.AreEqual(2099, msg.DurationMs);
        }

        [Test]
        public void SystemTaskEvent_TaskUpdated_MapsPatchStatus()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[30], LogSink) as SystemTaskEventMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("task_updated", msg.Subtype);
            Assert.AreEqual("ac1ae679e88d103e8", msg.TaskId);
            Assert.IsTrue(string.IsNullOrEmpty(msg.ToolUseId), "task_updated carries no tool_use_id");
            Assert.AreEqual("completed", msg.PatchStatus);
        }

        [Test]
        public void SystemTaskEvent_TaskNotification_MapsSummaryAndUsage()
        {
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[31], LogSink) as SystemTaskEventMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual("task_notification", msg.Subtype);
            Assert.AreEqual("ac1ae679e88d103e8", msg.TaskId);
            Assert.AreEqual("toolu_01X1fFH9irZFSYzquGCCEXW6", msg.ToolUseId);
            Assert.AreEqual("completed", msg.Status);
            StringAssert.Contains("SUBAGENT_FIXTURE_OK", msg.SummaryMarkdown);
            Assert.AreEqual(21038, msg.TotalTokens);
            Assert.AreEqual(1, msg.ToolUses);
            Assert.AreEqual(4801, msg.DurationMs);
        }

        [Test]
        public void SystemThinkingTokens_FirstFixtureEvent_MapsCumulativeAndDelta()
        {
            // Line 8: the first system/thinking_tokens event (estimated 50,
            // delta 50 -- the whole estimate arrived in one jump).
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[7],
                LogSink) as SystemThinkingTokensMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.SystemThinkingTokens, msg.Type);
            Assert.AreEqual(50, msg.EstimatedTokens);
            Assert.AreEqual(50, msg.EstimatedTokensDelta);
        }

        [Test]
        public void SystemThinkingTokens_SecondFixtureEvent_MapsCumulativeAndDelta()
        {
            // Line 10: the second event for the same block (cumulative 143,
            // this increment 93) -- demonstrates it fires more than once
            // per thinking_delta (only one thinking_delta appears between
            // lines 8 and 10 in this fixture).
            var msg = StreamJsonMessage.ParseLine(
                FixtureLoader.ReadLines("task_subagent_inbound.jsonl")[9],
                LogSink) as SystemThinkingTokensMessage;

            Assert.IsNotNull(msg);
            Assert.AreEqual(143, msg.EstimatedTokens);
            Assert.AreEqual(93, msg.EstimatedTokensDelta);
        }

        [Test]
        public void System_UnhandledTaskLikeSubtype_StillMapsToSystemAndIsIgnorable()
        {
            // Forward-compat: a task_* subtype outside the known 4 must not
            // throw or get dropped -- it degrades to the same "ignored
            // System" bucket as any other unhandled subtype.
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"system\",\"subtype\":\"task_cancelled\",\"session_id\":\"s\"}", LogSink);
            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.System, msg.Type);
            Assert.IsEmpty(_log);
        }

        // ---------------------------------------------------------------
        // Unknown types and drops
        // ---------------------------------------------------------------

        [Test]
        public void UnknownType_MapsToUnknown_NotDropped_NotLogged()
        {
            var msg = StreamJsonMessage.ParseLine(
                "{\"type\":\"totally_new_thing\",\"payload\":{\"x\":1}}", LogSink);
            Assert.IsNotNull(msg);
            Assert.AreEqual(InboundType.Unknown, msg.Type);
            Assert.IsInstanceOf<UnknownMessage>(msg);
            Assert.AreEqual("totally_new_thing", ((UnknownMessage)msg).RawType);
            Assert.IsEmpty(_log, "unknown types are silently ignored");
        }

        [Test]
        public void MissingType_IsDroppedWithLoggedReason()
        {
            var msg = StreamJsonMessage.ParseLine("{\"no_type\":true}", LogSink);
            Assert.IsNull(msg);
            Assert.AreEqual(1, _log.Count);
            StringAssert.Contains("type", _log[0]);
        }

        [Test]
        public void MalformedLine_IsDroppedWithLoggedReason_NeverThrows()
        {
            StreamJsonMessage msg = null;
            Assert.DoesNotThrow(delegate
            {
                msg = StreamJsonMessage.ParseLine("this is not json", LogSink);
            });
            Assert.IsNull(msg);
            Assert.AreEqual(1, _log.Count);
        }

        [Test]
        public void NullLogger_IsTolerated()
        {
            Assert.DoesNotThrow(delegate
            {
                StreamJsonMessage.ParseLine("still not json", null);
                StreamJsonMessage.ParseLine("{\"type\":\"assistant\"}", null);
            });
        }

        // ---------------------------------------------------------------
        // OutboundMessages (the only stdin factory)
        // ---------------------------------------------------------------

        [Test]
        public void Outbound_UserText_ArrayForm_SingleLine_RoundTrips()
        {
            // CJK + newline content (built at runtime; source stays ASCII).
            string text = "fix this: " + (char)0x65E5 + (char)0x672C + (char)0x8A9E
                + "\nsecond line";
            string json = OutboundMessages.UserText(text);

            StringAssert.DoesNotContain("\n", json);
            StringAssert.DoesNotContain("\r", json);

            JsonNode node = JsonParser.Parse(json);
            Assert.AreEqual("user", node["type"].AsString());
            Assert.AreEqual("user", node["message"]["role"].AsString());
            Assert.AreEqual(1, node["message"]["content"].Count);
            Assert.AreEqual("text", node["message"]["content"][0]["type"].AsString());
            Assert.AreEqual(text, node["message"]["content"][0]["text"].AsString());
        }

        [Test]
        public void Outbound_Initialize_Interrupt_SetPermissionMode_SetModel()
        {
            JsonNode init = JsonParser.Parse(OutboundMessages.Initialize("req_1"));
            Assert.AreEqual("control_request", init["type"].AsString());
            Assert.AreEqual("req_1", init["request_id"].AsString());
            Assert.AreEqual("initialize", init["request"]["subtype"].AsString());

            JsonNode intr = JsonParser.Parse(OutboundMessages.Interrupt("int_1"));
            Assert.AreEqual("int_1", intr["request_id"].AsString());
            Assert.AreEqual("interrupt", intr["request"]["subtype"].AsString());

            JsonNode pm = JsonParser.Parse(
                OutboundMessages.SetPermissionMode("pm_1", "acceptEdits"));
            Assert.AreEqual("set_permission_mode", pm["request"]["subtype"].AsString());
            Assert.AreEqual("acceptEdits", pm["request"]["mode"].AsString());

            JsonNode sm = JsonParser.Parse(OutboundMessages.SetModel("sm_1", "sonnet"));
            Assert.AreEqual("set_model", sm["request"]["subtype"].AsString());
            Assert.AreEqual("sonnet", sm["request"]["model"].AsString());
        }

        [Test]
        public void Outbound_McpReconnect_CarriesSubtypeAndServerName()
        {
            JsonNode node = JsonParser.Parse(OutboundMessages.McpReconnect("mcpr_1", "unity-ops"));
            Assert.AreEqual("control_request", node["type"].AsString());
            Assert.AreEqual("mcpr_1", node["request_id"].AsString());
            Assert.AreEqual("mcp_reconnect", node["request"]["subtype"].AsString());
            Assert.AreEqual("unity-ops", node["request"]["serverName"].AsString());
        }

        [Test]
        public void Outbound_AllowToolUse_WithUpdatedInputAndPermissions()
        {
            JsonNode updatedInput = JsonNode.NewObject().Set("command", "git status");
            JsonNode updatedPermissions = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("type", "addRules"));

            JsonNode node = JsonParser.Parse(
                OutboundMessages.AllowToolUse("uuid-1", updatedInput, updatedPermissions));

            Assert.AreEqual("control_response", node["type"].AsString());
            JsonNode envelope = node["response"];
            Assert.AreEqual("success", envelope["subtype"].AsString());
            Assert.AreEqual("uuid-1", envelope["request_id"].AsString());
            JsonNode inner = envelope["response"];
            Assert.AreEqual("allow", inner["behavior"].AsString());
            Assert.AreEqual("git status", inner["updatedInput"]["command"].AsString());
            Assert.AreEqual(1, inner["updatedPermissions"].Count);
        }

        [Test]
        public void Outbound_AllowToolUse_OmitsAbsentOptionalFields()
        {
            JsonNode inner = JsonParser.Parse(
                OutboundMessages.AllowToolUse("uuid-2"))["response"]["response"];
            Assert.AreEqual("allow", inner["behavior"].AsString());
            Assert.IsFalse(inner.HasKey("updatedInput"));
            Assert.IsFalse(inner.HasKey("updatedPermissions"));
        }

        [Test]
        public void Outbound_DenyToolUse_CarriesMessageAndInterrupt()
        {
            JsonNode node = JsonParser.Parse(
                OutboundMessages.DenyToolUse("uuid-3", "User denied this action"));
            JsonNode envelope = node["response"];
            Assert.AreEqual("uuid-3", envelope["request_id"].AsString());
            JsonNode inner = envelope["response"];
            Assert.AreEqual("deny", inner["behavior"].AsString());
            Assert.AreEqual("User denied this action", inner["message"].AsString());
            Assert.IsFalse(inner["interrupt"].AsBool(true));

            JsonNode hard = JsonParser.Parse(
                OutboundMessages.DenyToolUse("uuid-4", "stop", true))
                ["response"]["response"];
            Assert.IsTrue(hard["interrupt"].AsBool());
        }

        [Test]
        public void Outbound_AllPayloads_AreSingleLine()
        {
            var payloads = new[]
            {
                OutboundMessages.UserText("a\nb\rc"),
                OutboundMessages.Initialize("r1"),
                OutboundMessages.Interrupt("r2"),
                OutboundMessages.SetPermissionMode("r3", "plan"),
                OutboundMessages.SetModel("r4", "haiku"),
                OutboundMessages.AllowToolUse("r5"),
                OutboundMessages.DenyToolUse("r6", "line1\nline2"),
                OutboundMessages.McpReconnect("r7", "unity-ops")
            };
            foreach (string payload in payloads)
            {
                StringAssert.DoesNotContain("\n", payload);
                StringAssert.DoesNotContain("\r", payload);
                Assert.DoesNotThrow(delegate { JsonParser.Parse(payload); });
            }
        }

        [Test]
        public void Outbound_RejectsEmptyIdentifiers()
        {
            Assert.Throws<System.ArgumentException>(
                delegate { OutboundMessages.Initialize(""); });
            Assert.Throws<System.ArgumentException>(
                delegate { OutboundMessages.SetModel("r", null); });
            Assert.Throws<System.ArgumentException>(
                delegate { OutboundMessages.McpReconnect("r", ""); });
            Assert.Throws<System.ArgumentNullException>(
                delegate { OutboundMessages.UserText(null); });
        }
    }
}
