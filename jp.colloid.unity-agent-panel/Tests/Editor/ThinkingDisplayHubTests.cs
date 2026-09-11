using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// End-to-end thinking-display routing (design note 2026-08-01-
    /// thinking-content-loss.md section 5): replays synthetic stream-json
    /// lines shaped exactly like the real captures in Tests/Editor/
    /// Fixtures/task_subagent_inbound.jsonl (content_block_start of type
    /// thinking, an empty thinking_delta carrying only estimated_tokens,
    /// and system/thinking_tokens) through a real AgentClient wired to
    /// AgentHub's OWN production handlers (the SubagentGroupingTests
    /// pattern via AgentHub.WireClientForTests), then inspects
    /// AgentHub.Session directly.
    /// </summary>
    [TestFixture]
    public class ThinkingDisplayHubTests
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
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
            AgentHub.ResetForTests();
        }

        private void StartAndReplay(IEnumerable<string> lines)
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            _fake.ScriptLines(lines);
            while (_client.Pump(100, 50.0) > 0)
            {
            }
        }

        private const string ThinkingBlockStartLine =
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,"
                + "\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}},"
                + "\"parent_tool_use_id\":null}";

        private static string ThinkingDeltaLine(string thinking, int estimatedTokens)
        {
            return "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,"
                + "\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"" + thinking + "\","
                + "\"estimated_tokens\":" + estimatedTokens + "}},\"parent_tool_use_id\":null}";
        }

        private static string ThinkingTokensLine(int estimatedTokens, int delta)
        {
            return "{\"type\":\"system\",\"subtype\":\"thinking_tokens\","
                + "\"estimated_tokens\":" + estimatedTokens + ",\"estimated_tokens_delta\":" + delta + "}";
        }

        private static string FinalizedAssistantLine(string thinkingText)
        {
            return "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                + "\"content\":[{\"type\":\"thinking\",\"thinking\":\"" + thinkingText + "\","
                + "\"signature\":\"sig\"}]},\"parent_tool_use_id\":null}";
        }

        private const string RedactedThinkingAssistantLine =
            "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                + "\"content\":[{\"type\":\"redacted_thinking\",\"data\":\"opaque\"}]},"
                + "\"parent_tool_use_id\":null}";

        /// <summary>Last block of any kind in the last session message, or null.</summary>
        private static ChatMessageBlock LastBlockOfLastMessage(ChatSession session)
        {
            if (session.messages.Count == 0)
            {
                return null;
            }
            return session.messages[session.messages.Count - 1].LastBlock();
        }

        /// <summary>The last Thinking-kind block anywhere in the session, or null.</summary>
        private static ChatMessageBlock FindLastThinkingBlock(ChatSession session)
        {
            ChatMessageBlock found = null;
            foreach (ChatMessage message in session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.kind == ChatBlockKind.Thinking)
                    {
                        found = block;
                    }
                }
            }
            return found;
        }

        // ---------------------------------------------------------------
        // Block creation on first signal (acceptance criterion 2)
        // ---------------------------------------------------------------

        [Test]
        public void ThinkingBlockStart_Alone_CreatesEmptyStreamingThinkingBlock()
        {
            StartAndReplay(new[] { ThinkingBlockStartLine });

            ChatMessageBlock last = LastBlockOfLastMessage(AgentHub.Session);
            Assert.IsNotNull(last);
            Assert.AreEqual(ChatBlockKind.Thinking, last.kind);
            Assert.IsTrue(last.streaming);
            Assert.AreEqual(string.Empty, last.text);
            Assert.AreEqual(0, last.thinkingTokens);
        }

        [Test]
        public void ThinkingTokensEvent_Alone_CreatesThinkingBlockWithEstimate()
        {
            StartAndReplay(new[] { ThinkingTokensLine(7, 7) });

            ChatMessageBlock last = LastBlockOfLastMessage(AgentHub.Session);
            Assert.IsNotNull(last);
            Assert.AreEqual(ChatBlockKind.Thinking, last.kind);
            Assert.IsTrue(last.streaming);
            Assert.AreEqual(string.Empty, last.text);
            Assert.AreEqual(7, last.thinkingTokens);
        }

        // ---------------------------------------------------------------
        // Main scenario: full synthetic replay -> finalized empty-text
        // Thinking block with a non-zero token estimate survives.
        // ---------------------------------------------------------------

        [Test]
        public void SyntheticThinkingSignals_ProduceFinalizedEmptyTextThinkingBlockWithTokenEstimate()
        {
            var lines = new List<string>
            {
                ThinkingBlockStartLine,
                ThinkingDeltaLine(string.Empty, 50),
                ThinkingTokensLine(143, 93),
                FinalizedAssistantLine(string.Empty)
            };
            StartAndReplay(lines);

            ChatMessageBlock thinking = FindLastThinkingBlock(AgentHub.Session);
            Assert.IsNotNull(thinking, "a Thinking block must exist even though its text is empty");
            Assert.IsTrue(string.IsNullOrEmpty(thinking.text));
            Assert.AreEqual(143, thinking.thinkingTokens,
                "the cumulative estimate from the last system/thinking_tokens event must survive finalization");
            Assert.IsFalse(thinking.streaming,
                "the block must be finalized once the assistant message arrives");
        }

        // ---------------------------------------------------------------
        // Forward-compat (design note section 2): a CLI that someday sends
        // real thinking text must not lose it either direction.
        // ---------------------------------------------------------------

        [Test]
        public void FinalizedThinkingWithText_WinsOverAccumulatedStreamedText()
        {
            var lines = new List<string>
            {
                ThinkingBlockStartLine,
                ThinkingDeltaLine("partial reasoning", 10),
                FinalizedAssistantLine("final reasoning")
            };
            StartAndReplay(lines);

            ChatMessageBlock thinking = FindLastThinkingBlock(AgentHub.Session);
            Assert.IsNotNull(thinking);
            Assert.AreEqual("final reasoning", thinking.text,
                "a non-empty finalized thinking text must win over the accumulated streamed tail");
            Assert.IsFalse(thinking.streaming);
        }

        [Test]
        public void FinalizedThinkingEmpty_KeepsAccumulatedStreamedTextAndTokens()
        {
            var lines = new List<string>
            {
                ThinkingBlockStartLine,
                ThinkingDeltaLine("some reasoning", 10),
                ThinkingTokensLine(10, 10),
                FinalizedAssistantLine(string.Empty)
            };
            StartAndReplay(lines);

            ChatMessageBlock thinking = FindLastThinkingBlock(AgentHub.Session);
            Assert.IsNotNull(thinking);
            Assert.AreEqual("some reasoning", thinking.text,
                "an empty finalized thinking text must keep the accumulated streamed tail");
            Assert.AreEqual(10, thinking.thinkingTokens);
            Assert.IsFalse(thinking.streaming);
        }

        // ---------------------------------------------------------------
        // redacted_thinking (design note 2026-08-01-thinking-content-loss.md
        // section 6): arrives complete, independent of the thinking_delta
        // tail-merge machinery above.
        // ---------------------------------------------------------------

        [Test]
        public void RedactedThinkingBlock_ProducesFinalizedRedactedThinkingBlock()
        {
            StartAndReplay(new[] { RedactedThinkingAssistantLine });

            ChatMessageBlock thinking = FindLastThinkingBlock(AgentHub.Session);
            Assert.IsNotNull(thinking);
            Assert.IsTrue(thinking.thinkingRedacted);
            Assert.IsTrue(string.IsNullOrEmpty(thinking.text));
            Assert.IsFalse(thinking.streaming);
        }

        [Test]
        public void RedactedThinkingBlock_DoesNotDisturbAnUnrelatedPendingThinkingTail()
        {
            // A trailing streamed (non-redacted) thinking tail must still
            // survive finalization even when a LATER, unrelated message in
            // the same turn carries a redacted_thinking block -- the two
            // paths (pendingThinking correlation vs. direct append) must
            // not interfere with each other.
            var lines = new List<string>
            {
                ThinkingBlockStartLine,
                ThinkingDeltaLine("some reasoning", 10),
                FinalizedAssistantLine(string.Empty),
                RedactedThinkingAssistantLine
            };
            StartAndReplay(lines);

            ChatMessage message = AgentHub.Session.messages[AgentHub.Session.messages.Count - 1];
            var thinkingBlocks = new List<ChatMessageBlock>();
            foreach (ChatMessageBlock block in message.blocks)
            {
                if (block.kind == ChatBlockKind.Thinking)
                {
                    thinkingBlocks.Add(block);
                }
            }
            Assert.AreEqual(2, thinkingBlocks.Count);
            Assert.IsFalse(thinkingBlocks[0].thinkingRedacted);
            Assert.AreEqual("some reasoning", thinkingBlocks[0].text);
            Assert.IsTrue(thinkingBlocks[1].thinkingRedacted);
        }

        // ---------------------------------------------------------------
        // Real fixture replay: sanity that production wiring reaches the
        // same outcome against the actual captured CLI lines.
        // ---------------------------------------------------------------

        [Test]
        public void RealFixture_TaskSubagentInbound_ProducesFinalizedThinkingBlockWithTokenEstimate()
        {
            StartAndReplay(FixtureLoader.ReadLines("task_subagent_inbound.jsonl"));

            ChatMessageBlock thinking = FindLastThinkingBlock(AgentHub.Session);
            Assert.IsNotNull(thinking, "the real fixture's thinking block must survive finalization");
            Assert.IsTrue(string.IsNullOrEmpty(thinking.text),
                "the real CLI capture never carries thinking body text");
            Assert.AreEqual(143, thinking.thinkingTokens,
                "the last system/thinking_tokens estimate before finalization (143) must be preserved");
            Assert.IsFalse(thinking.streaming);
        }
    }
}
