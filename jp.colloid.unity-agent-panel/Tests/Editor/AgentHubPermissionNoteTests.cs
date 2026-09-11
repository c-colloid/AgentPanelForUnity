using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-08-02 design note section 2 (permission fatigue / transcript
    /// bloat, measured 32-of-36 messages as pure "&lt;Tool&gt; approved"
    /// noise): an ALLOW decision must no longer append a transcript system
    /// note at all -- the tool-use card already shows the call/input/
    /// result. DENY keeps its note (a refusal is real information). The
    /// script-gate auto-deny note (a DIFFERENT code path,
    /// TryAutoDenyForScriptGate) is untouched by this change and is
    /// exercised by AgentHubScriptGateTests; this fixture only pins that
    /// its own note text still appears since it shares the transcript with
    /// the behavior under test here. Replays a real can_use_tool
    /// control_request through AgentHub.WireClientForTests, the same seam
    /// AgentHubScriptGateTests/AgentHubTurnNonUndoableWarningTests use.
    /// </summary>
    [TestFixture]
    public class AgentHubPermissionNoteTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;

        [SetUp]
        public void SetUp()
        {
            L10n.OverrideForTests(PanelLanguage.English);
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
            L10n.OverrideForTests(null);
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

        private void SendCanUseTool(string requestId, string toolName, string filePath)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + toolName
                + "\",\"input\":{\"file_path\":\"" + filePath.Replace("\\", "\\\\") + "\"}}}");
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private static List<ChatMessage> Messages()
        {
            return AgentHub.Session.messages;
        }

        private static void AssertNoSystemNoteInTranscript()
        {
            List<ChatMessage> messages = Messages();
            for (int i = 0; i < messages.Count; i++)
            {
                for (int b = 0; b < messages[i].blocks.Count; b++)
                {
                    Assert.AreNotEqual(ChatBlockKind.SystemNote, messages[i].blocks[b].kind,
                        "message " + i + " block " + b + " was an unexpected SystemNote: "
                        + messages[i].blocks[b].text);
                }
            }
        }

        [Test]
        public void Allow_AppendsNoTranscriptNote()
        {
            StartReadyClient();
            SendCanUseTool("req-allow-1", "Write", "Assets/Foo/Bar.txt");
            Assert.IsNotNull(AgentHub.PendingPermission);
            int before = Messages().Count;

            AgentHub.RespondToPendingPermission("req-allow-1", PermissionDecision.AllowTool());

            Assert.AreEqual(before, Messages().Count,
                "an ALLOW decision must not add any transcript message");
            AssertNoSystemNoteInTranscript();
        }

        [Test]
        public void Allow_WithCustomTranscriptNote_IsStillSuppressed()
        {
            // Mirrors the AskUserQuestion Submit/Skip call shape (an ALLOW
            // decision with a caller-supplied note) -- ALLOW suppresses the
            // note regardless of whether one was supplied.
            StartReadyClient();
            SendCanUseTool("req-allow-2", "Write", "Assets/Foo/Bar.txt");
            int before = Messages().Count;

            AgentHub.RespondToPendingPermission("req-allow-2",
                PermissionDecision.AllowTool(), "Answered: some custom note");

            Assert.AreEqual(before, Messages().Count);
            AssertNoSystemNoteInTranscript();
        }

        [Test]
        public void Deny_AppendsTranscriptNote_NamingTheTool()
        {
            StartReadyClient();
            SendCanUseTool("req-deny-1", "Write", "Assets/Foo/Bar.txt");
            int before = Messages().Count;

            AgentHub.RespondToPendingPermission("req-deny-1",
                PermissionDecision.DenyTool("no"));

            List<ChatMessage> messages = Messages();
            Assert.AreEqual(before + 1, messages.Count,
                "a DENY decision must append exactly one transcript message");
            ChatMessage last = messages[messages.Count - 1];
            Assert.AreEqual(ChatMessage.RoleSystem, last.role);
            Assert.AreEqual(ChatBlockKind.SystemNote, last.blocks[0].kind);
            StringAssert.Contains("Denied", last.blocks[0].text);
            StringAssert.Contains("Write", last.blocks[0].text);
        }

        [Test]
        public void Deny_WithCustomTranscriptNote_UsesItVerbatim()
        {
            StartReadyClient();
            SendCanUseTool("req-deny-2", "Write", "Assets/Foo/Bar.txt");

            AgentHub.RespondToPendingPermission("req-deny-2",
                PermissionDecision.DenyTool("no"), "custom deny row text");

            ChatMessage last = Messages()[Messages().Count - 1];
            Assert.AreEqual("custom deny row text", last.blocks[0].text);
        }

        [Test]
        public void ScriptGateAutoDeny_StillAppendsItsOwnNote_UnaffectedByAllowSuppression()
        {
            bool originalGateEnabled = PanelStateStore.instance.Settings.uapScriptGateEnabled;
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            try
            {
                StartReadyClient();
                int before = Messages().Count;

                // Auto-denied by the script gate BEFORE a card is ever
                // shown -- never goes through RespondToPendingPermission at
                // all, so this is a completely separate code path from the
                // ALLOW/DENY note logic under test above.
                _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"req-gate\""
                    + ",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Write\""
                    + ",\"input\":{\"file_path\":\"Assets/Foo/Bar.cs\"}}}");
                PumpAll();

                Assert.IsNull(AgentHub.PendingPermission);
                List<ChatMessage> messages = Messages();
                Assert.AreEqual(before + 1, messages.Count,
                    "the script gate must still append its own auto-deny note");
                ChatMessage last = messages[messages.Count - 1];
                Assert.AreEqual(ChatBlockKind.SystemNote, last.blocks[0].kind);
            }
            finally
            {
                PanelStateStore.instance.Settings.uapScriptGateEnabled = originalGateEnabled;
            }
        }
    }
}
