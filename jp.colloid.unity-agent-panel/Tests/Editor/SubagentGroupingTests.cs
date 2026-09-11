using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// End-to-end subagent routing (Phase 4 design note section 7.2):
    /// replays real captured CLI lines (task_subagent_inbound.jsonl)
    /// through a real AgentClient (backed by FakeCliProcess, no process
    /// spawn) wired to AgentHub's OWN production event handlers via the
    /// AgentHub.WireClientForTests seam (Colloid.AgentPanel.Editor.Tests
    /// InternalsVisibleTo, see AssemblyInfo.cs), then inspects
    /// AgentHub.Session directly -- the same mutation logic a live session
    /// runs, not a reimplementation of it.
    /// </summary>
    [TestFixture]
    public class SubagentGroupingTests
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

        /// <summary>Finds the first ToolCallRecord with a non-null subagent
        /// anywhere in the session's top-level messages.</summary>
        private static ToolCallRecord FindSubagentToolCall(ChatSession session)
        {
            foreach (ChatMessage message in session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.kind == ChatBlockKind.ToolCall
                        && block.toolCall != null && block.toolCall.subagent != null)
                    {
                        return block.toolCall;
                    }
                }
            }
            return null;
        }

        /// <summary>Finds the top-level ToolCallRecord with the given toolUseId
        /// (regardless of whether it carries a subagent), or null.</summary>
        private static ToolCallRecord FindToolCall(ChatSession session, string toolUseId)
        {
            foreach (ChatMessage message in session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.kind == ChatBlockKind.ToolCall && block.toolCall != null
                        && block.toolCall.toolUseId == toolUseId)
                    {
                        return block.toolCall;
                    }
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // (a) + (b): full fixture replay
        // ---------------------------------------------------------------

        [Test]
        public void RealFixture_SubagentContent_NeverMixesIntoTopLevelTranscript()
        {
            StartAndReplay(FixtureLoader.ReadLines("task_subagent_inbound.jsonl"));

            ChatSession session = AgentHub.Session;
            foreach (ChatMessage message in session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.kind == ChatBlockKind.Text || block.kind == ChatBlockKind.Thinking)
                    {
                        // The subagent's own "Command ran successfully..."
                        // text (its final reply) must never surface as a
                        // top-level text block -- only the parent's own
                        // "DONE" text belongs at the top level.
                        StringAssert.DoesNotContain("Command ran successfully", block.text ?? string.Empty);
                    }
                    if (block.kind == ChatBlockKind.ToolCall && block.toolCall != null
                        && block.toolCall.subagent == null)
                    {
                        // The nested Bash call must never appear as its own
                        // top-level tool card -- only via the outer Agent
                        // card's subagent.blocks.
                        Assert.AreNotEqual("Bash", block.toolCall.toolName,
                            "the subagent's nested Bash call leaked into the top-level transcript");
                    }
                }
            }
        }

        [Test]
        public void RealFixture_AgentToolCall_CarriesNestedBashCardAndSummary()
        {
            StartAndReplay(FixtureLoader.ReadLines("task_subagent_inbound.jsonl"));

            ToolCallRecord agentCall = FindSubagentToolCall(AgentHub.Session);
            Assert.IsNotNull(agentCall, "the Agent tool_use must carry a SubagentRecord");
            Assert.AreEqual("Agent", agentCall.toolName);

            SubagentRecord subagent = agentCall.subagent;
            Assert.AreEqual("general-purpose", subagent.subagentType);
            Assert.AreEqual("Run echo fixture command", subagent.description);
            Assert.AreEqual("completed", subagent.status,
                "the top-level tool_result is the final authority on status");

            Assert.AreEqual(1, subagent.blocks.Count, "one nested Bash tool card");
            Assert.AreEqual(ChatBlockKind.ToolCall, subagent.blocks[0].kind);
            ToolCallRecord nestedBash = subagent.blocks[0].toolCall;
            Assert.IsNotNull(nestedBash);
            Assert.AreEqual("Bash", nestedBash.toolName);
            Assert.IsNull(nestedBash.subagent, "depth-1 cutoff: no grandchild SubagentRecord");
            Assert.AreEqual(ToolCallStatus.Succeeded, nestedBash.status);
            Assert.AreEqual("SUBAGENT_FIXTURE_OK", nestedBash.resultSummary);

            StringAssert.Contains("SUBAGENT_FIXTURE_OK", subagent.summaryMarkdown);
            Assert.Greater(subagent.totalTokens, 0);
        }

        /// <summary>
        /// Review defect (docs/design-notes/2026-07-31-subagent-display.md
        /// section 3): _taskIdToToolUseId used to be populated by
        /// task_started and never pruned on normal completion, growing by
        /// one entry per subagent spawn for the life of the client
        /// connection. The fixture's top-level tool_result (the normal-
        /// completion closure path in OnToolResultReceived) must remove the
        /// task_started-registered entry, not just the _openSubagents one.
        /// </summary>
        [Test]
        public void RealFixture_TaskIdMapping_PrunedOnNormalSubagentCompletion()
        {
            StartAndReplay(FixtureLoader.ReadLines("task_subagent_inbound.jsonl"));

            Assert.IsFalse(AgentHub.HasTaskIdMappingForTests("ac1ae679e88d103e8"),
                "a completed subagent's task_id->tool_use_id entry must be pruned, "
                    + "not left to grow forever");
        }

        [Test]
        public void RealFixture_TopLevelTranscript_KeepsUserSendAndFinalDone()
        {
            StartAndReplay(FixtureLoader.ReadLines("task_subagent_inbound.jsonl"));

            ChatSession session = AgentHub.Session;
            bool sawFinalDone = false;
            foreach (ChatMessage message in session.messages)
            {
                if (message.role == ChatMessage.RoleAssistant)
                {
                    foreach (ChatMessageBlock block in message.blocks)
                    {
                        if (block.kind == ChatBlockKind.Text && block.text == "DONE")
                        {
                            sawFinalDone = true;
                        }
                    }
                }
            }
            Assert.IsTrue(sawFinalDone, "the parent's own final 'DONE' text must still render at the top level");
        }

        // ---------------------------------------------------------------
        // (c) Unknown parent id: fallback to top level
        // ---------------------------------------------------------------

        [Test]
        public void UnknownParentToolUseId_FallsBackToTopLevelRendering()
        {
            var lines = new List<string>
            {
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"text\",\"text\":\"orphaned subagent text\"}]},"
                    + "\"parent_tool_use_id\":\"toolu_never_seen\"}",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_orphan_bash\",\"name\":\"Bash\","
                    + "\"input\":{\"command\":\"echo hi\"}}]},"
                    + "\"parent_tool_use_id\":\"toolu_never_seen\"}"
            };
            StartAndReplay(lines);

            ChatSession session = AgentHub.Session;
            bool sawOrphanedText = false;
            bool sawOrphanedTool = false;
            foreach (ChatMessage message in session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.kind == ChatBlockKind.Text && block.text == "orphaned subagent text")
                    {
                        sawOrphanedText = true;
                    }
                    if (block.kind == ChatBlockKind.ToolCall && block.toolCall != null
                        && block.toolCall.toolUseId == "toolu_orphan_bash")
                    {
                        sawOrphanedTool = true;
                        Assert.IsNull(block.toolCall.subagent);
                    }
                }
            }
            Assert.IsTrue(sawOrphanedText,
                "an assistant message with an unrecognized parent id must still render at top level");
            Assert.IsTrue(sawOrphanedTool,
                "a tool_use with an unrecognized parent id must still render at top level");
        }

        // ---------------------------------------------------------------
        // (d) TurnCompleted while running -> "stopped"
        // ---------------------------------------------------------------

        [Test]
        public void RunningSubagent_MarkedStopped_WhenTurnCompletesWithoutToolResult()
        {
            var lines = new List<string>
            {
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_interrupted\",\"name\":\"Agent\","
                    + "\"input\":{\"subagent_type\":\"general-purpose\",\"description\":\"desc\"}}]},"
                    + "\"parent_tool_use_id\":null}",
                "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"task_1\","
                    + "\"tool_use_id\":\"toolu_interrupted\",\"description\":\"desc\","
                    + "\"subagent_type\":\"general-purpose\"}",
                "{\"is_error\":true,\"subtype\":\"error\",\"session_id\":\"s\",\"type\":\"result\","
                    + "\"result\":\"interrupted\"}"
            };
            StartAndReplay(lines);

            ToolCallRecord agentCall = FindSubagentToolCall(AgentHub.Session);
            Assert.IsNotNull(agentCall);
            Assert.AreEqual("stopped", agentCall.subagent.status,
                "a subagent still running when the turn ends must be demoted to 'stopped'");
        }

        // ---------------------------------------------------------------
        // TearDownClient (domain reload / editor quit mid-tool) --
        // review defect: TearDownClient used to Clear() _openToolCalls/
        // _openSubagents without demoting their still-"running"/Running
        // status first, unlike FinalizeStreamingMessage. A SessionCache
        // save right after (Shutdown/ShutdownForReload both save) would
        // then persist a permanently-spinning card.
        // ---------------------------------------------------------------

        [Test]
        public void TearDownClient_DemotesRunningSubagentAndOrdinaryToolCall_AndPrunesTaskIdMap()
        {
            var lines = new List<string>
            {
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_interrupted\",\"name\":\"Agent\","
                    + "\"input\":{\"subagent_type\":\"general-purpose\",\"description\":\"desc\"}},"
                    + "{\"type\":\"tool_use\",\"id\":\"toolu_plain_bash\",\"name\":\"Bash\","
                    + "\"input\":{\"command\":\"sleep 999\"}}]},"
                    + "\"parent_tool_use_id\":null}",
                "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"task_reload\","
                    + "\"tool_use_id\":\"toolu_interrupted\",\"description\":\"desc\","
                    + "\"subagent_type\":\"general-purpose\"}"
            };
            StartAndReplay(lines);

            Assert.IsTrue(AgentHub.HasTaskIdMappingForTests("task_reload"),
                "sanity: task_started must have registered the mapping before teardown");

            // Simulates ReloadLifecycle.OnBeforeAssemblyReload -> ShutdownForReload
            // -> TearDownClient while both a subagent and an ordinary tool call
            // are still mid-flight.
            AgentHub.TearDownClientForTests();

            ToolCallRecord agentCall = FindSubagentToolCall(AgentHub.Session);
            Assert.IsNotNull(agentCall);
            Assert.AreEqual("stopped", agentCall.subagent.status,
                "a subagent still running at teardown must be demoted to 'stopped', "
                    + "not left spinning forever after --resume");

            ToolCallRecord plainBash = FindToolCall(AgentHub.Session, "toolu_plain_bash");
            Assert.IsNotNull(plainBash);
            Assert.AreEqual(ToolCallStatus.Pending, plainBash.status,
                "an ordinary tool call still running at teardown must be demoted to Pending");

            Assert.IsFalse(AgentHub.HasTaskIdMappingForTests("task_reload"),
                "teardown must prune the task_id->tool_use_id map too, not just clear _openSubagents");
        }
    }
}
