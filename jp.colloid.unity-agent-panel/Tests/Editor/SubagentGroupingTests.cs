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
        // run_in_background:true (design note 2026-09-27-background-
        // subagent-card.md): the Agent tool_result arrives at LAUNCH, the
        // parent's turn ends, and only then does the subagent do its work.
        // Before the fix, OnToolResultReceived closed the record at launch,
        // so every later parent-tagged line fell through the "unknown
        // parent" fallback onto the top-level transcript and task_* events
        // found no open record (no progress, no summary, card stuck at
        // "completed").
        // ---------------------------------------------------------------

        private static List<string> BackgroundSpawnLines()
        {
            return new List<string>
            {
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_bg\",\"name\":\"Agent\","
                    + "\"input\":{\"subagent_type\":\"general-purpose\",\"description\":\"bg desc\","
                    + "\"run_in_background\":true,\"prompt\":\"echo BG_OK\"}}]},"
                    + "\"parent_tool_use_id\":null}",
                "{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"task_bg\","
                    + "\"tool_use_id\":\"toolu_bg\",\"description\":\"bg desc\","
                    + "\"subagent_type\":\"general-purpose\"}",
                // The launch acknowledgement: the Agent tool_use closes NOW.
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_bg\","
                    + "\"type\":\"tool_result\",\"content\":\"Async agent launched. agentId: task_bg\","
                    + "\"is_error\":false}]},\"parent_tool_use_id\":null}",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"text\",\"text\":\"LAUNCHED\"}]},\"parent_tool_use_id\":null}",
                // The parent's turn ends while the subagent is still working.
                "{\"is_error\":false,\"subtype\":\"success\",\"session_id\":\"s\",\"type\":\"result\","
                    + "\"result\":\"LAUNCHED\"}"
            };
        }

        private static List<string> BackgroundWorkLines()
        {
            return new List<string>
            {
                "{\"type\":\"system\",\"subtype\":\"task_progress\",\"task_id\":\"task_bg\","
                    + "\"tool_use_id\":\"toolu_bg\",\"description\":\"Running echo\","
                    + "\"last_tool_name\":\"Bash\",\"usage\":{\"total_tokens\":1234,\"tool_uses\":1,\"duration_ms\":900}}",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m3\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_bg_bash\",\"name\":\"Bash\","
                    + "\"input\":{\"command\":\"echo BG_OK\"}}]},\"parent_tool_use_id\":\"toolu_bg\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_bg_bash\","
                    + "\"type\":\"tool_result\",\"content\":\"BG_OK\",\"is_error\":false}]},"
                    + "\"parent_tool_use_id\":\"toolu_bg\"}",
                "{\"type\":\"assistant\",\"message\":{\"id\":\"m4\",\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"text\",\"text\":\"bg subagent final reply\"}]},"
                    + "\"parent_tool_use_id\":\"toolu_bg\"}",
                "{\"type\":\"system\",\"subtype\":\"task_updated\",\"task_id\":\"task_bg\","
                    + "\"patch\":{\"status\":\"completed\"}}",
                "{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"task_bg\","
                    + "\"tool_use_id\":\"toolu_bg\",\"status\":\"completed\","
                    + "\"summary\":\"Printed BG_OK\",\"usage\":{\"total_tokens\":2222,\"tool_uses\":1,\"duration_ms\":1500}}"
            };
        }

        [Test]
        public void BackgroundSpawn_StaysRunning_AfterLaunchResultAndTurnEnd()
        {
            StartAndReplay(BackgroundSpawnLines());

            ToolCallRecord agentCall = FindSubagentToolCall(AgentHub.Session);
            Assert.IsNotNull(agentCall);
            Assert.IsTrue(agentCall.subagent.background, "run_in_background:true must be recorded");
            Assert.AreEqual(ToolCallStatus.Succeeded, agentCall.status,
                "the Agent tool_use itself did complete (launch acknowledged)");
            Assert.AreEqual("running", agentCall.subagent.status,
                "a background subagent is still working after the launch result and the parent's result");
            Assert.IsTrue(AgentHub.HasTaskIdMappingForTests("task_bg"),
                "the task_id mapping must survive the turn boundary so task_updated can still resolve");
        }

        [Test]
        public void BackgroundSpawn_LaterWork_RoutesIntoTheCard_NotTopLevel()
        {
            var lines = BackgroundSpawnLines();
            lines.AddRange(BackgroundWorkLines());
            StartAndReplay(lines);

            ToolCallRecord agentCall = FindSubagentToolCall(AgentHub.Session);
            Assert.IsNotNull(agentCall);
            SubagentRecord subagent = agentCall.subagent;

            Assert.AreEqual("completed", subagent.status);
            Assert.AreEqual("Printed BG_OK", subagent.summaryMarkdown);
            Assert.AreEqual(2222, subagent.totalTokens);
            Assert.AreEqual("Running echo", subagent.progressLine);

            Assert.AreEqual(2, subagent.blocks.Count, "nested Bash card + the subagent's final text");
            Assert.AreEqual("Bash", subagent.blocks[0].toolCall.toolName);
            Assert.AreEqual(ToolCallStatus.Succeeded, subagent.blocks[0].toolCall.status);
            Assert.AreEqual("BG_OK", subagent.blocks[0].toolCall.resultSummary);
            Assert.AreEqual(ChatBlockKind.Text, subagent.blocks[1].kind);
            Assert.AreEqual("bg subagent final reply", subagent.blocks[1].text);

            Assert.IsNull(FindToolCall(AgentHub.Session, "toolu_bg_bash"),
                "the nested Bash call must not leak onto the top-level transcript");
            foreach (ChatMessage message in AgentHub.Session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.kind == ChatBlockKind.Text)
                    {
                        Assert.AreNotEqual("bg subagent final reply", block.text,
                            "the subagent's own reply must not surface as top-level text");
                    }
                }
            }
            Assert.IsFalse(AgentHub.HasTaskIdMappingForTests("task_bg"),
                "the terminal task_notification must close the background record and prune the map");
        }

        [Test]
        public void BackgroundSpawn_NestedCallStillRunningAtTurnEnd_ResolvesLater()
        {
            var lines = BackgroundSpawnLines();
            // Insert the nested tool_use BEFORE the parent's result so it is
            // mid-flight across the turn boundary, then complete it after.
            string result = lines[lines.Count - 1];
            lines.RemoveAt(lines.Count - 1);
            List<string> work = BackgroundWorkLines();
            lines.Add(work[1]);   // parent-tagged Bash tool_use
            lines.Add(result);    // turn ends
            lines.Add(work[2]);   // parent-tagged Bash tool_result
            lines.Add(work[5]);   // task_notification completed
            StartAndReplay(lines);

            SubagentRecord subagent = FindSubagentToolCall(AgentHub.Session).subagent;
            Assert.AreEqual(1, subagent.blocks.Count);
            Assert.AreEqual(ToolCallStatus.Succeeded, subagent.blocks[0].toolCall.status,
                "a nested call open across the parent's result must still receive its tool_result");
            Assert.AreEqual("completed", subagent.status);
        }

        [Test]
        public void BackgroundSpawn_TearDownClient_StillDemotesToStopped()
        {
            StartAndReplay(BackgroundSpawnLines());
            AgentHub.TearDownClientForTests();

            SubagentRecord subagent = FindSubagentToolCall(AgentHub.Session).subagent;
            Assert.AreEqual("stopped", subagent.status,
                "a process teardown ends background subagents too: nothing will ever report them");
            Assert.IsFalse(AgentHub.HasTaskIdMappingForTests("task_bg"));
        }

        [Test]
        public void ForegroundSpawn_ToolResultStillClosesTheRecord()
        {
            // Regression guard for the unchanged foreground path: the real
            // fixture's Agent tool_use has run_in_background:false.
            StartAndReplay(FixtureLoader.ReadLines("task_subagent_inbound.jsonl"));
            SubagentRecord subagent = FindSubagentToolCall(AgentHub.Session).subagent;
            Assert.IsFalse(subagent.background);
            Assert.AreEqual("completed", subagent.status);
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
