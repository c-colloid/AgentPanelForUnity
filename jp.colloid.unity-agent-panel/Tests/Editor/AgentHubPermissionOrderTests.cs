using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design note 2026-09-10 section 2: OnPermissionRequested
    /// (AgentHub.cs:3785) calls TryAutoDenyForScriptGate BEFORE
    /// TryAutoApproveUapOpsTool, and the method's own comment admits this
    /// ordering is "an accident of today's naming, not a guarantee" --
    /// Write/Edit/MultiEdit (the only tools the gate ever inspects) never
    /// resolve via UapOpsServer.FindByWireName, so the two branches happen
    /// to never overlap YET. Nothing in the code pins the order itself.
    ///
    /// This fixture is the pin: with the most permissive auto-approve
    /// level (AllUnityOps) AND the script gate enabled, a gated Write must
    /// still be DENIED, never silently allowed by a future reordering or a
    /// future widening of UapOpsServer's wire-name namespace into
    /// Write/Edit/MultiEdit territory. A second case proves this is not
    /// "everything is denied": a genuinely read-only UapOps tool (
    /// uap_query_hierarchy, pinned ReadOnly by
    /// UapReadOnlyToolMetadataTests) still auto-approves silently at the
    /// same level, so the fixture demonstrates both branches of
    /// OnPermissionRequested actually firing, not just the deny path.
    ///
    /// Same AgentHub.WireClientForTests/SetClientForTests seam as
    /// AgentHubScriptGateTests/AgentHubAutoApproveLevelTests.
    /// </summary>
    [TestFixture]
    public class AgentHubPermissionOrderTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private UapAutoApproveLevel _originalLevel;
        private bool _originalGateEnabled;

        [SetUp]
        public void SetUp()
        {
            _originalLevel = PanelStateStore.instance.Settings.autoApproveLevel;
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
            PanelStateStore.instance.Settings.autoApproveLevel = _originalLevel;
            PanelStateStore.instance.Settings.uapScriptGateEnabled = _originalGateEnabled;
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

        private void SendWriteCanUseTool(string requestId, string filePath)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Write\""
                + ",\"input\":{\"file_path\":\"" + filePath + "\"}}}");
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private string LastWrittenLine()
        {
            return _fake.WrittenLines[_fake.WrittenLines.Count - 1];
        }

        [Test]
        public void ScriptGatedWrite_AtAllUnityOpsLevel_IsAlwaysDenied_NeverAllowed()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendWriteCanUseTool("req-order-deny", "Assets/Foo/Bar.cs");

            Assert.IsNull(AgentHub.PendingPermission,
                "the gate must answer before a card is ever shown, at any auto-approve level.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count,
                "exactly one control_response must be written.");
            string written = LastWrittenLine();
            StringAssert.Contains("\"behavior\":\"deny\"", written,
                "TryAutoDenyForScriptGate must run BEFORE TryAutoApproveUapOpsTool -- reversing the"
                + " order (or widening UapOps wire names into Write/Edit/MultiEdit) must never let"
                + " a gated script write reach \"allow\".");
            StringAssert.DoesNotContain("\"behavior\":\"allow\"", written);
            StringAssert.Contains("req-order-deny", written);
        }

        [Test]
        public void ReadOnlyUapOpsTool_AtAllUnityOpsLevel_StillAutoApprovesSilently()
        {
            // The other branch of OnPermissionRequested: with the exact
            // same settings as above, a tool that the gate never inspects
            // at all (a genuine UapOps tool, ReadOnly) must still sail
            // through TryAutoApproveUapOpsTool -- proving the fixture is
            // not just "everything denies now".
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendCanUseTool("req-order-allow", WireName("uap_query_hierarchy"), "{}");

            Assert.IsNull(AgentHub.PendingPermission,
                "a read-only UapOps tool must auto-approve without ever showing a card.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);
            StringAssert.Contains("\"behavior\":\"allow\"", LastWrittenLine());
        }
    }
}
