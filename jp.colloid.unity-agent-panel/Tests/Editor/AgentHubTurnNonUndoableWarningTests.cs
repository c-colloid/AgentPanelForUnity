using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design section 8.2 B2(c)/8.5 criterion 2: a turn that ran a
    /// non-undoable UapOps tool must leave a system-note transcript warning
    /// naming it, since a single Ctrl+Z cannot revert that operation even
    /// though the rest of the turn's Undo group can. Replays real
    /// assistant/result lines through AgentHub.WireClientForTests, the same
    /// seam AgentHubScriptGateTests uses.
    /// </summary>
    [TestFixture]
    public class AgentHubTurnNonUndoableWarningTests
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
            AgentHub.SetClientForTests(_client);
        }

        [TearDown]
        public void TearDown()
        {
            AgentHub.SetClientForTests(null);
            _client.Dispose();
            AgentHub.ResetForTests();
        }

        private void StartReadyClient()
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            _fake.ScriptLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\"}");
            PumpAll();
        }

        private void SendToolUse(string toolUseId, string wireToolName)
        {
            _fake.ScriptLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\""
                + ",\"id\":\"" + toolUseId + "\",\"name\":\"" + wireToolName + "\",\"input\":{}}]}}");
            PumpAll();
        }

        private void CompleteTurn()
        {
            _fake.ScriptLine("{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s1\"}");
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private static string WireName(string uapToolName)
        {
            return "mcp__" + UapOpsMcpConfig.ServerName + "__" + uapToolName;
        }

        private static ChatMessage LastMessage()
        {
            System.Collections.Generic.List<ChatMessage> messages = AgentHub.Session.messages;
            return messages.Count > 0 ? messages[messages.Count - 1] : null;
        }

        [Test]
        public void TurnWithNonUndoableUapOpsTool_AppendsWarningNote()
        {
            StartReadyClient();

            SendToolUse("t1", WireName("uap_asset_create"));
            CompleteTurn();

            ChatMessage last = LastMessage();
            Assert.IsNotNull(last);
            Assert.AreEqual(ChatMessage.RoleSystem, last.role);
            Assert.AreEqual(1, last.blocks.Count);
            Assert.AreEqual(ChatBlockKind.SystemNote, last.blocks[0].kind);
            StringAssert.Contains("uap_asset_create", last.blocks[0].text);
        }

        [Test]
        public void TurnWithOnlyUndoableUapOpsTool_AppendsNoWarning()
        {
            StartReadyClient();

            SendToolUse("t1", WireName("uap_scene_create_object"));
            CompleteTurn();

            AssertNoSystemNoteInTranscript();
        }

        private static void AssertNoSystemNoteInTranscript()
        {
            System.Collections.Generic.List<ChatMessage> messages = AgentHub.Session.messages;
            for (int i = 0; i < messages.Count; i++)
            {
                for (int b = 0; b < messages[i].blocks.Count; b++)
                {
                    Assert.AreNotEqual(ChatBlockKind.SystemNote, messages[i].blocks[b].kind);
                }
            }
        }

        [Test]
        public void TurnWithOnlyReadOnlyTools_AppendsNoWarning()
        {
            // Read-only tools are Undoable == false because there is
            // nothing to undo -- NOT because they did something
            // irreversible. Live 2026-08-02 a turn that merely listed the
            // hierarchy ended with "this turn ran operations Ctrl+Z cannot
            // undo: uap_query_component_types, uap_query_hierarchy", which
            // is both wrong and exactly the transcript noise v0.14.0
            // removes elsewhere.
            StartReadyClient();

            SendToolUse("t1", WireName("uap_query_hierarchy"));
            SendToolUse("t2", WireName("uap_query_component_types"));
            CompleteTurn();

            AssertNoSystemNoteInTranscript();
        }

        [Test]
        public void TurnMixingReadOnlyAndMutatingTools_WarnsAboutTheMutatingOneOnly()
        {
            StartReadyClient();

            SendToolUse("t1", WireName("uap_query_hierarchy"));
            SendToolUse("t2", WireName("uap_asset_create"));
            CompleteTurn();

            ChatMessage last = LastMessage();
            Assert.IsNotNull(last);
            Assert.AreEqual(ChatBlockKind.SystemNote, last.blocks[0].kind);
            StringAssert.Contains("uap_asset_create", last.blocks[0].text);
            StringAssert.DoesNotContain("uap_query_hierarchy", last.blocks[0].text,
                "a read-only tool must never be named in the not-undoable warning");
        }

        [Test]
        public void TurnWithNonUapOpsTool_AppendsNoWarning()
        {
            StartReadyClient();

            SendToolUse("t1", "Write");
            CompleteTurn();

            AssertNoSystemNoteInTranscript();
        }

        [Test]
        public void TurnWithTwoNonUndoableTools_ListsBothOnce()
        {
            StartReadyClient();

            SendToolUse("t1", WireName("uap_asset_create"));
            SendToolUse("t2", WireName("uap_asset_delete"));
            SendToolUse("t3", WireName("uap_asset_create")); // duplicate -- must not repeat.
            CompleteTurn();

            ChatMessage last = LastMessage();
            Assert.IsNotNull(last);
            string text = last.blocks[0].text;
            StringAssert.Contains("uap_asset_create", text);
            StringAssert.Contains("uap_asset_delete", text);
            int firstOccurrence = text.IndexOf("uap_asset_create");
            int secondOccurrence = text.IndexOf("uap_asset_create", firstOccurrence + 1);
            Assert.AreEqual(-1, secondOccurrence, "each non-undoable tool must be listed only once, even if run twice.");
        }

        [Test]
        public void SecondTurn_DoesNotRepeatAPreviousTurnsWarning()
        {
            StartReadyClient();

            SendToolUse("t1", WireName("uap_asset_create"));
            CompleteTurn();
            int countAfterFirstTurn = AgentHub.Session.messages.Count;

            SendToolUse("t2", WireName("uap_scene_create_object"));
            CompleteTurn();

            // Turn 2 legitimately adds its OWN tool-call transcript message
            // (the assistant bubble for uap_scene_create_object) -- the
            // defect this guards against is a STALE warning note riding
            // along with it, not the tool-call message itself.
            Assert.AreEqual(countAfterFirstTurn + 1, AgentHub.Session.messages.Count,
                "turn 2 must add exactly its own tool-call message, no extra (stale) warning note.");
            ChatMessage last = LastMessage();
            Assert.AreEqual(ChatMessage.RoleAssistant, last.role,
                "the newest message must be turn 2's own tool-call bubble, not a system-note warning.");
        }
    }
}
