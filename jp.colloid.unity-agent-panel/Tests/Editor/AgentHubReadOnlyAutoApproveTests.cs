using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// End-to-end auto-approve wiring at UapAutoApproveLevel.ReadOnly
    /// (2026-08-02 design note section 2 B2, superseded 2026-08-03 by the
    /// panel-side auto-approve LEVEL feature -- see UapAutoApproveLevel's own
    /// doc comment for why this replaced a plain on/off toggle): replays a
    /// real can_use_tool control_request through a real AgentClient
    /// (FakeCliProcess, no process spawn) wired to AgentHub's OWN
    /// OnPermissionRequested handler (AgentHub.WireClientForTests), verifying
    /// a ReadOnly UapOps tool is answered via the wire WITHOUT ever surfacing
    /// a permission card (AgentHub.PendingPermission stays null) -- mirrors
    /// AgentHubScriptGateTests' shape for the auto-deny path. This fixture
    /// specifically pins the ReadOnly level, i.e. exactly the behaviour the
    /// old v0.14.0 autoApproveReadOnlyOps==true bool used to provide;
    /// AgentHubAutoApproveLevelTests covers the Ask/Undoable/AllUnityOps
    /// levels and the raise-the-level-mid-card path.
    /// </summary>
    [TestFixture]
    public class AgentHubReadOnlyAutoApproveTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private UapAutoApproveLevel _originalLevel;

        [SetUp]
        public void SetUp()
        {
            _originalLevel = PanelStateStore.instance.Settings.autoApproveLevel;
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
            PanelStateStore.instance.Settings.autoApproveLevel = _originalLevel;
        }

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

        private static string WireName(string uapToolName)
        {
            return "mcp__" + UapOpsMcpConfig.ServerName + "__" + uapToolName;
        }

        private void SendCanUseTool(string requestId, string wireToolName, string inputJson)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + wireToolName
                + "\",\"input\":" + inputJson + "}}");
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        [Test]
        public void ReadOnlyTool_IsAutoApproved_WithoutShowingCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.ReadOnly;
            int baseline = StartReadyClient();

            SendCanUseTool("req-ro-1", WireName("uap_query_hierarchy"), "{}");

            Assert.IsNull(AgentHub.PendingPermission,
                "a read-only tool must be answered BEFORE a card is ever shown.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count,
                "exactly one control_response must be written.");
            string written = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("\"behavior\":\"allow\"", written);
            StringAssert.Contains("req-ro-1", written);
        }

        [Test]
        public void MutatingTool_IsNeverAutoApproved_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.ReadOnly;
            int baseline = StartReadyClient();

            SendCanUseTool("req-mut-1", WireName("uap_scene_create_object"),
                "{\"name\":\"Foo\"}");

            Assert.IsNotNull(AgentHub.PendingPermission,
                "a mutating UapOps tool must fall through to the normal permission card at ReadOnly level.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count,
                "nothing should be auto-answered for a mutating tool.");
        }

        [Test]
        public void NonUapOpsTool_IsNeverAutoApproved_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.ReadOnly;
            int baseline = StartReadyClient();

            SendCanUseTool("req-write-1", "Write", "{\"file_path\":\"Assets/Foo.txt\"}");

            Assert.IsNotNull(AgentHub.PendingPermission,
                "a non-UapOps tool (Write/Edit/Bash/etc.) never resolves via FindByWireName"
                + " and must always fall through to the normal permission card.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void LevelAsk_ReadOnlyTool_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            int baseline = StartReadyClient();

            SendCanUseTool("req-ro-2", WireName("uap_query_hierarchy"), "{}");

            Assert.IsNotNull(AgentHub.PendingPermission,
                "Ask must restore the plain permission card even for a read-only tool.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void ReadOnlyTool_AutoApproved_AppendsNoTranscriptNote()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.ReadOnly;
            StartReadyClient();
            int before = AgentHub.Session.messages.Count;

            SendCanUseTool("req-ro-3", WireName("uap_asset_find"), "{\"query\":\"t:Material\"}");

            Assert.AreEqual(before, AgentHub.Session.messages.Count,
                "auto-approval is an ALLOW decision, which never gets a transcript note (Stream B1).");
        }
    }
}
