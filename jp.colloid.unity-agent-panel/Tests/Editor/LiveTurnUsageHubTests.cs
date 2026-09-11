using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The status bar's context meter and token counter used to move only
    /// when a turn's result line landed, so a long tool-heavy turn left
    /// both frozen for minutes (design note
    /// docs/design-notes/2026-09-09-live-usage-during-turn.md). These
    /// drive real assistant lines through AgentClient into AgentHub's
    /// handlers (the CompactionHubTests pattern via WireClientForTests)
    /// and check the hub's live readings at each step.
    /// </summary>
    public class LiveTurnUsageHubTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;

        [SetUp]
        public void SetUp()
        {
            AgentHub.ResetForTests();
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
            AgentHub.WireClientForTests(_client);
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
            AgentHub.ResetForTests();
        }

        private void Replay(params string[] lines)
        {
            _fake.ScriptLines(lines);
            while (_client.Pump(100, 50.0) > 0)
            {
            }
        }

        /// <summary>
        /// One assistant line as the CLI emits it mid-turn: a text block
        /// with this API call's usage (input + output + cache fields).
        /// </summary>
        private static string AssistantLine(string messageId, long input, long output,
            long cacheRead, long cacheCreate, string parentToolUseId)
        {
            string parent = parentToolUseId == null ? "null" : "\"" + parentToolUseId + "\"";
            return "{\"type\":\"assistant\",\"message\":{\"id\":\"" + messageId + "\","
                + "\"model\":\"claude-sonnet-5\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],"
                + "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ","
                + "\"cache_read_input_tokens\":" + cacheRead + ","
                + "\"cache_creation_input_tokens\":" + cacheCreate + "}},"
                + "\"parent_tool_use_id\":" + parent + "}";
        }

        private static string ResultLine(long input, long output, long contextTokens)
        {
            return "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"s\","
                + "\"result\":\"ok\",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ","
                + "\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0,"
                + "\"iterations\":[{\"input_tokens\":" + contextTokens + ",\"output_tokens\":0,"
                + "\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}]},"
                + "\"modelUsage\":{\"claude-sonnet-5\":{\"inputTokens\":" + input + ",\"outputTokens\":" + output + ","
                + "\"cacheReadInputTokens\":0,\"cacheCreationInputTokens\":0,\"costUSD\":0.0,"
                + "\"contextWindow\":200000}}}";
        }

        private static string BoundaryLine(string trigger)
        {
            return "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s\","
                + "\"compact_metadata\":{\"trigger\":\"" + trigger + "\",\"pre_tokens\":150000}}";
        }

        [Test]
        public void Idle_NoLiveReading()
        {
            Assert.AreEqual(-1, AgentHub.LiveContextTokens);
            Assert.AreEqual(-1, AgentHub.CurrentContextTokens);
            Assert.AreEqual(0, AgentHub.InFlightTurnTokens);
        }

        [Test]
        public void AssistantMessage_MidTurn_UpdatesContextAndTokens_BeforeAnyResult()
        {
            Replay(AssistantLine("m1", 4, 120, 30000, 2000, null));

            Assert.AreEqual(32124, AgentHub.LiveContextTokens,
                "the four-field sum of the latest API call IS the context right now");
            Assert.AreEqual(32124, AgentHub.CurrentContextTokens);
            Assert.AreEqual(124, AgentHub.InFlightTurnTokens,
                "the counter adds input + output, matching FormatUsage");
            Assert.AreEqual(0, AgentHub.Session.totalInputTokens + AgentHub.Session.totalOutputTokens,
                "the session's completed-turn totals are untouched until the result");
        }

        [Test]
        public void LaterIteration_ReplacesContext_AndAccumulatesTokens()
        {
            Replay(
                AssistantLine("m1", 4, 120, 30000, 2000, null),
                AssistantLine("m2", 6, 300, 32000, 900, null));

            Assert.AreEqual(33206, AgentHub.LiveContextTokens, "the LATEST call's context, not a sum");
            Assert.AreEqual(430, AgentHub.InFlightTurnTokens, "input + output summed over both calls");
        }

        [Test]
        public void SameMessageId_RepeatedPerContentBlock_CountedOnce()
        {
            // The CLI emits one assistant line per content block, each
            // repeating the message id and the same usage.
            Replay(
                AssistantLine("m1", 10, 200, 5000, 0, null),
                AssistantLine("m1", 10, 200, 5000, 0, null));

            Assert.AreEqual(210, AgentHub.InFlightTurnTokens);
            Assert.AreEqual(5210, AgentHub.LiveContextTokens);
        }

        [Test]
        public void Result_ReplacesLiveReading_AndClearsInFlight()
        {
            Replay(
                AssistantLine("m1", 4, 120, 30000, 2000, null),
                ResultLine(4, 120, 28000));

            Assert.AreEqual(-1, AgentHub.LiveContextTokens);
            Assert.AreEqual(28000, AgentHub.LastContextTokens);
            Assert.AreEqual(28000, AgentHub.CurrentContextTokens, "the result's reading takes over");
            Assert.AreEqual(0, AgentHub.InFlightTurnTokens, "folded into the session, never added twice");
            Assert.AreEqual(124, AgentHub.Session.totalInputTokens + AgentHub.Session.totalOutputTokens);
            Assert.AreEqual("124 tok", StatusBarView.FormatUsage(AgentHub.Session, false,
                AgentHub.InFlightTurnTokens));
        }

        [Test]
        public void NextTurn_LiveReadingWins_OverPreviousResult()
        {
            Replay(
                AssistantLine("m1", 4, 120, 30000, 2000, null),
                ResultLine(4, 120, 28000),
                AssistantLine("m2", 8, 50, 40000, 1000, null));

            Assert.AreEqual(28000, AgentHub.LastContextTokens, "the completed reading is kept");
            Assert.AreEqual(41058, AgentHub.CurrentContextTokens, "but the meter shows the fresher one");
            Assert.AreEqual(58, AgentHub.InFlightTurnTokens);
            Assert.AreEqual("182 tok", StatusBarView.FormatUsage(AgentHub.Session, false,
                AgentHub.InFlightTurnTokens), "completed 124 + in-flight 58");
        }

        [Test]
        public void SubagentMessage_DoesNotTouchLiveReading()
        {
            // A subagent's context is its own window (design note
            // 2026-09-0x context meter: subagents start with cache_read 0
            // and build their own context); result.usage excludes it too.
            Replay(
                "{\"type\":\"assistant\",\"message\":{\"id\":\"p\",\"model\":\"claude-sonnet-5\","
                + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Task\","
                + "\"input\":{\"description\":\"d\",\"prompt\":\"p\",\"subagent_type\":\"Explore\"}}],"
                + "\"usage\":{\"input_tokens\":4,\"output_tokens\":40,"
                + "\"cache_read_input_tokens\":30000,\"cache_creation_input_tokens\":0}},"
                + "\"parent_tool_use_id\":null}",
                AssistantLine("s1", 900, 300, 12000, 0, "toolu_1"));

            Assert.AreEqual(30044, AgentHub.LiveContextTokens, "the parent's own call, not the subagent's");
            Assert.AreEqual(44, AgentHub.InFlightTurnTokens);
        }

        [Test]
        public void AutoCompaction_MidTurn_NextAssistantMessage_IsTheFirstTrustedReading()
        {
            Replay(
                AssistantLine("m1", 4, 120, 140000, 2000, null),
                BoundaryLine("auto"));
            Assert.IsTrue(AgentHub.ContextUnknownAfterCompaction);
            Assert.AreEqual(-1, AgentHub.CurrentContextTokens,
                "the pre-compaction live number is stale the moment the boundary lands");
            Assert.IsTrue(StatusBarView.ShowCompactedState(
                AgentHub.ContextUnknownAfterCompaction, AgentHub.CurrentContextTokens));

            Replay(AssistantLine("m2", 6, 80, 20000, 5000, null));
            Assert.AreEqual(25086, AgentHub.CurrentContextTokens,
                "this call ran on the compacted context -- a real reading beats the flag");
            Assert.IsFalse(StatusBarView.ShowCompactedState(
                AgentHub.ContextUnknownAfterCompaction, AgentHub.CurrentContextTokens));
        }

        [Test]
        public void ManualCompaction_ItsOwnAssistantMessage_IsIgnored()
        {
            // /compact's summarization call reads the WHOLE old context;
            // its number is exactly what the boundary just invalidated.
            Replay(
                BoundaryLine("manual"),
                AssistantLine("m1", 4, 500, 140000, 0, null));

            Assert.AreEqual(-1, AgentHub.CurrentContextTokens);
            Assert.AreEqual(0, AgentHub.InFlightTurnTokens);
            Assert.IsTrue(StatusBarView.ShowCompactedState(
                AgentHub.ContextUnknownAfterCompaction, AgentHub.CurrentContextTokens));
        }

        [Test]
        public void AbortOpenTurn_DropsLiveReading()
        {
            Replay(AssistantLine("m1", 4, 120, 30000, 2000, null));
            Assert.AreEqual(124, AgentHub.InFlightTurnTokens);

            AgentHub.AbortOpenTurn(false);

            Assert.AreEqual(-1, AgentHub.LiveContextTokens);
            Assert.AreEqual(0, AgentHub.InFlightTurnTokens);
        }
    }
}
