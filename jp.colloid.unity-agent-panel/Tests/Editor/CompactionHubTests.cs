using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// system/compact_boundary through a real AgentClient into AgentHub's
    /// own handlers (the ThinkingDisplayHubTests pattern via
    /// AgentHub.WireClientForTests): the transcript note, and the context
    /// meter's "compacted" state across the result that follows (design
    /// note docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 2.2 -- a manual /compact's own result reads the OLD
    /// context, an auto compaction's result reads the new one).
    /// </summary>
    public class CompactionHubTests
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

        private static string BoundaryLine(string trigger, long preTokens)
        {
            return "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s\","
                + "\"compact_metadata\":{\"trigger\":\"" + trigger + "\",\"pre_tokens\":" + preTokens + "}}";
        }

        /// <summary>A success result whose last iteration read <paramref name="contextTokens"/> tokens.</summary>
        private static string ResultLine(long contextTokens)
        {
            return "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"s\","
                + "\"result\":\"ok\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,"
                + "\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0,"
                + "\"iterations\":[{\"input_tokens\":" + contextTokens + ",\"output_tokens\":0,"
                + "\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}]},"
                + "\"modelUsage\":{\"claude-sonnet-5\":{\"inputTokens\":1,\"outputTokens\":1,"
                + "\"cacheReadInputTokens\":0,\"cacheCreationInputTokens\":0,\"costUSD\":0.0,"
                + "\"contextWindow\":200000}}}";
        }

        private static ChatMessage LastSystemNote()
        {
            List<ChatMessage> messages = AgentHub.Session.messages;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].role == ChatMessage.RoleSystem)
                {
                    return messages[i];
                }
            }
            return null;
        }

        [Test]
        public void ManualBoundary_AppendsLocalizedNote_WithPreTokens()
        {
            StartAndReplay(new[] { BoundaryLine("manual", 84213) });

            ChatMessage note = LastSystemNote();
            Assert.IsNotNull(note, "a compaction must leave a visible mark in the transcript");
            Assert.AreEqual(1, note.blocks.Count);
            Assert.AreEqual(ChatBlockKind.SystemNote, note.blocks[0].kind);
            Assert.AreEqual(CompactionNote.Describe("manual", 84213), note.blocks[0].text);
            Assert.AreEqual(L10n.F(L10n.S.HubCompactedManualFmt, "84.2k"), note.blocks[0].text);
            Assert.IsFalse(note.blocks[0].warning, "a compaction is bookkeeping, not a failure");
        }

        [Test]
        public void ManualBoundary_MeterUnknown_ThroughItsOwnResult_UntilTheNextTurn()
        {
            StartAndReplay(new[] { BoundaryLine("manual", 84213) });
            Assert.IsTrue(AgentHub.ContextUnknownAfterCompaction);
            Assert.AreEqual(-1, AgentHub.LastContextTokens);

            // The /compact turn's own result: its last iteration is the
            // summarization call over the OLD context -- must be discarded.
            _fake.ScriptLines(new[] { ResultLine(84000) });
            while (_client.Pump(100, 50.0) > 0)
            {
            }
            Assert.IsTrue(AgentHub.ContextUnknownAfterCompaction,
                "the compaction turn's reading describes the discarded context");
            Assert.AreEqual(-1, AgentHub.LastContextTokens);
            Assert.IsTrue(StatusBarView.ShowCompactedState(
                AgentHub.ContextUnknownAfterCompaction, AgentHub.LastContextTokens));

            // The next ordinary turn measures the real, smaller window.
            _fake.ScriptLines(new[] { ResultLine(12000) });
            while (_client.Pump(100, 50.0) > 0)
            {
            }
            Assert.IsFalse(AgentHub.ContextUnknownAfterCompaction);
            Assert.AreEqual(12000, AgentHub.LastContextTokens);
        }

        [Test]
        public void AutoBoundary_MidTurn_ResultReadingIsTrusted()
        {
            StartAndReplay(new[] { BoundaryLine("auto", 190000) });
            Assert.IsTrue(AgentHub.ContextUnknownAfterCompaction,
                "between the boundary and the result the old number is stale");
            ChatMessage note = LastSystemNote();
            Assert.IsNotNull(note);
            Assert.AreEqual(CompactionNote.Describe("auto", 190000), note.blocks[0].text);

            _fake.ScriptLines(new[] { ResultLine(30000) });
            while (_client.Pump(100, 50.0) > 0)
            {
            }
            Assert.IsFalse(AgentHub.ContextUnknownAfterCompaction,
                "the turn went on after the boundary; its last iteration ran on the compacted context");
            Assert.AreEqual(30000, AgentHub.LastContextTokens);
        }

        [Test]
        public void NoBoundary_TurnReadingStandsAsBefore()
        {
            StartAndReplay(new[] { ResultLine(45000) });
            Assert.IsFalse(AgentHub.ContextUnknownAfterCompaction);
            Assert.AreEqual(45000, AgentHub.LastContextTokens);
            Assert.IsNull(LastSystemNote());
        }

        [Test]
        public void BoundaryWithoutMetadata_StillNotes_WithoutATokenClause()
        {
            StartAndReplay(new[]
            {
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s\"}"
            });
            ChatMessage note = LastSystemNote();
            Assert.IsNotNull(note);
            Assert.AreEqual(L10n.S.HubCompactedAuto, note.blocks[0].text);
        }
    }
}
