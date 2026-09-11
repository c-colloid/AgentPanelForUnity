using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// End-to-end script gate wiring: replays a real can_use_tool
    /// control_request through a real AgentClient (FakeCliProcess, no
    /// process spawn) wired to AgentHub's OWN OnPermissionRequested
    /// handler (AgentHub.WireClientForTests), verifying the auto-deny path
    /// answers via the wire (WrittenLines) WITHOUT ever surfacing a
    /// permission card (AgentHub.PendingPermission stays null) -- design
    /// section 7.4/8.2 B1.
    /// </summary>
    [TestFixture]
    public class AgentHubScriptGateTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private bool _originalGateEnabled;

        [SetUp]
        public void SetUp()
        {
            _originalGateEnabled = PanelStateStore.instance.Settings.uapScriptGateEnabled;
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
            PanelStateStore.instance.Settings.uapScriptGateEnabled = _originalGateEnabled;
        }

        /// <summary>
        /// Starts the client and returns the WrittenLines count right after
        /// (the "initialize" handshake AgentClient.Start() writes on its
        /// own counts as 1 line here) -- every test compares against this
        /// baseline instead of an absolute count, since that handshake line
        /// is unrelated to the gate.
        /// </summary>
        private int StartReadyClient()
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            _fake.ScriptLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\"}");
            PumpAll();
            return _fake.WrittenLines.Count;
        }

        private void SendCanUseTool(string requestId, string toolName, string filePath)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + toolName
                + "\",\"input\":{\"file_path\":\"" + filePath.Replace("\\", "\\\\") + "\"}}}");
            PumpAll();
        }

        /// <summary>Real Bash tool_use shape: the path lives in "command", never "file_path".</summary>
        private void SendBashCanUseTool(string requestId, string command)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\""
                + ",\"input\":{\"command\":\"" + EscapeJson(command) + "\"}}}");
            PumpAll();
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        [Test]
        public void GateEnabled_WriteToAssetsScript_AutoDeniesWithoutShowingCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendCanUseTool("req-gate-1", "Write", "Assets/Foo/Bar.cs");

            Assert.IsNull(AgentHub.PendingPermission, "the gate must answer BEFORE a card is ever shown.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count, "exactly one control_response must be written.");
            string written = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("\"behavior\":\"deny\"", written);
            StringAssert.Contains("req-gate-1", written);
            StringAssert.Contains("uap_scripts_commit", written);
        }

        [Test]
        public void GateEnabled_StagingPathWrite_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendCanUseTool("req-gate-2", "Write", "UapStaging/Foo.cs");

            Assert.IsNotNull(AgentHub.PendingPermission, "a staging-path write must use the normal permission flow.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count, "nothing should be auto-answered yet.");
        }

        [Test]
        public void GateEnabled_NonScriptWrite_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendCanUseTool("req-gate-3", "Write", "Assets/Foo/Bar.txt");

            Assert.IsNotNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void GateDisabled_WriteToAssetsScript_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = false;
            int baseline = StartReadyClient();

            SendCanUseTool("req-gate-4", "Write", "Assets/Foo/Bar.cs");

            Assert.IsNotNull(AgentHub.PendingPermission, "disabling the gate must restore the plain permission card.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void GateEnabled_BashToolWithUnrelatedFilePathField_IsNeverGated()
        {
            // A Bash tool_use has NO "file_path" field on the real wire
            // shape (the path/command lives in "command") -- this pins that
            // an incidental "file_path" key some other caller might set is
            // never consulted for Bash, only "command" is (see the
            // Bash-redirection tests below for the ACTUAL gated case).
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendCanUseTool("req-gate-5", "Bash", "Assets/Foo/Bar.cs");

            Assert.IsNotNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        /// <summary>
        /// Regression: a shell redirection through the Bash tool used to
        /// bypass the gate entirely (only Write/Edit/MultiEdit were ever
        /// inspected) -- ScriptGate.TryFindGatedBashTarget now closes this
        /// for the reported "cat &gt; Assets/Evil.cs &lt;&lt;EOF" /
        /// "echo ... &gt; Assets/Evil.cs" idiom.
        /// </summary>
        [Test]
        public void GateEnabled_BashRedirectionToAssetsScript_AutoDeniesWithoutShowingCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendBashCanUseTool("req-gate-6", "echo 'class Evil {}' > Assets/Evil.cs");

            Assert.IsNull(AgentHub.PendingPermission, "the Bash redirection bypass must be auto-denied, never shown as a card.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);
            string written = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("\"behavior\":\"deny\"", written);
            StringAssert.Contains("req-gate-6", written);
            StringAssert.Contains("uap_scripts_commit", written);
        }

        [Test]
        public void GateEnabled_BashRedirectionIntoStagingFolder_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendBashCanUseTool("req-gate-7", "echo 'ok' > UapStaging/Foo.cs");

            Assert.IsNotNull(AgentHub.PendingPermission, "a staging-path write must use the normal permission flow, even via Bash.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void GateEnabled_BashCommandNotWritingAScript_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendBashCanUseTool("req-gate-8", "git status");

            Assert.IsNotNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void GateDisabled_BashRedirectionToAssetsScript_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = false;
            int baseline = StartReadyClient();

            SendBashCanUseTool("req-gate-9", "echo 'class Evil {}' > Assets/Evil.cs");

            Assert.IsNotNull(AgentHub.PendingPermission, "disabling the gate must restore the plain permission card.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }
    }
}
