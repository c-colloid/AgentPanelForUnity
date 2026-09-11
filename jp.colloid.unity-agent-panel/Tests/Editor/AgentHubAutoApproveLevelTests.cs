using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapAutoApproveLevel end-to-end wiring (2026-08-03): replays real
    /// can_use_tool control_requests through a real AgentClient
    /// (FakeCliProcess, no process spawn) wired to AgentHub's OWN
    /// OnPermissionRequested handler (AgentHub.WireClientForTests), the same
    /// idiom AgentHubReadOnlyAutoApproveTests/AgentHubScriptGateTests use.
    /// Four things this fixture pins that those two do not:
    /// <list type="bullet">
    /// <item>the Undoable and AllUnityOps levels widen auto-approval exactly
    /// as AutoApprovePolicy.ShouldAutoApprove specifies, and a non-UapOps
    /// tool is NEVER auto-approved at any level BELOW AllTools
    /// (AutoApprovePolicy's "no escape hatch" rule) -- design note
    /// 2026-09-10-auto-approve-all-tools-and-lean-auto-continue section 1
    /// makes AllTools the sole, deliberate exception: at that level a
    /// non-UapOps tool (Bash included) IS auto-approved, unless the
    /// request is requires_user_interaction (AskUserQuestion), which is
    /// never auto-answered at any level;</item>
    /// <item>TryAutoDenyForScriptGate still wins over even the most
    /// permissive level -- OnPermissionRequested's fixed ordering;</item>
    /// <item>AgentHub.ApplyAutoApproveLevelChanged resolves an
    /// ALREADY-PENDING permission the instant the header UI raises the
    /// level far enough to cover it, and never answers the same request
    /// twice.</item>
    /// </list>
    /// Sample tool names (pinned elsewhere -- UapReadOnlyToolMetadataTests /
    /// PermissionCardUndoBadgeTests): uap_query_hierarchy is ReadOnly;
    /// uap_scene_create_object mutates but is Undoable; uap_asset_create
    /// mutates and is NOT Undoable.
    /// </summary>
    [TestFixture]
    public class AgentHubAutoApproveLevelTests
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

        // -- Undoable level: read-only AND undoable-mutating tools auto-
        // approve; a non-undoable mutating tool still falls through. --------

        [Test]
        public void LevelUndoable_ReadOnlyTool_IsAutoApproved()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            int baseline = StartReadyClient();

            SendCanUseTool("req-u-1", WireName("uap_query_hierarchy"), "{}");

            Assert.IsNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);
            StringAssert.Contains("\"behavior\":\"allow\"", LastWrittenLine());
        }

        [Test]
        public void LevelUndoable_UndoableMutatingTool_IsAutoApproved()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            int baseline = StartReadyClient();

            SendCanUseTool("req-u-2", WireName("uap_scene_create_object"), "{\"name\":\"Foo\"}");

            Assert.IsNull(AgentHub.PendingPermission,
                "an Undoable mutating tool must auto-approve at the Undoable level.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);
            StringAssert.Contains("\"behavior\":\"allow\"", LastWrittenLine());
        }

        [Test]
        public void LevelUndoable_NonUndoableMutatingTool_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            int baseline = StartReadyClient();

            SendCanUseTool("req-u-3", WireName("uap_asset_create"), "{\"path\":\"Assets/Foo.asset\"}");

            Assert.IsNotNull(AgentHub.PendingPermission,
                "a non-undoable mutating tool must still show a card at the Undoable level.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void LevelUndoable_NonUapOpsTool_FallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            int baseline = StartReadyClient();

            SendWriteCanUseTool("req-u-4", "Assets/Foo.txt");

            Assert.IsNotNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        // -- AllUnityOps level: every UapOps tool auto-approves, but a
        // non-UapOps tool (the CLI's own Bash/Write/etc.) NEVER does -- the
        // "no escape hatch" rule (AutoApprovePolicy.ShouldAutoApprove's own
        // doc comment). ------------------------------------------------------

        [Test]
        public void LevelAllUnityOps_NonUndoableMutatingTool_IsAutoApproved()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            int baseline = StartReadyClient();

            SendCanUseTool("req-a-1", WireName("uap_asset_create"), "{\"path\":\"Assets/Foo.asset\"}");

            Assert.IsNull(AgentHub.PendingPermission,
                "AllUnityOps must auto-approve even a non-undoable mutating UapOps tool.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);
            StringAssert.Contains("\"behavior\":\"allow\"", LastWrittenLine());
        }

        [Test]
        public void LevelAllUnityOps_NonUapOpsTool_StillFallsThroughToNormalCard()
        {
            // The load-bearing "no escape hatch" case: AllUnityOps only ever
            // means "all UapOps tools", never "all tools" -- Bash stays a
            // slow, confirmation-heavy path no matter how permissive the
            // level is (AutoApprovePolicy.ShouldAutoApprove's own doc
            // comment on isUapOpsTool).
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            int baseline = StartReadyClient();

            SendWriteCanUseTool("req-a-2", "Assets/Foo.txt");

            Assert.IsNotNull(AgentHub.PendingPermission,
                "even AllUnityOps must never auto-approve a non-UapOps tool.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void LevelAllUnityOps_BashTool_StillFallsThroughToNormalCard()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            int baseline = StartReadyClient();

            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"req-a-3\""
                + ",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\""
                + ",\"input\":{\"command\":\"git status\"}}}");
            PumpAll();

            Assert.IsNotNull(AgentHub.PendingPermission,
                "Bash must never auto-approve at any level, including AllUnityOps.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        // -- Script gate ordering: TryAutoDenyForScriptGate must win over
        // even the most permissive auto-approve level. ----------------------

        [Test]
        public void ScriptGateDeny_StillWinsAtAllUnityOpsLevel()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            PanelStateStore.instance.Settings.uapScriptGateEnabled = true;
            int baseline = StartReadyClient();

            SendWriteCanUseTool("req-gate-allunity", "Assets/Foo/Bar.cs");

            Assert.IsNull(AgentHub.PendingPermission,
                "the gate answers before a card would ever show, at any level.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count,
                "exactly one control_response must be written.");
            string written = LastWrittenLine();
            StringAssert.Contains("\"behavior\":\"deny\"", written,
                "a script-gate-triggering write must still be DENIED, never silently allowed"
                + " by a permissive auto-approve level.");
            StringAssert.Contains("req-gate-allunity", written);
        }

        // -- Raising the level resolves an ALREADY-PENDING permission
        // (AgentHub.ApplyAutoApproveLevelChanged) -- the actual point of the
        // feature (UapAutoApproveLevel's own doc comment): the user raises
        // the level BECAUSE a card is already up blocking a long task. -----

        [Test]
        public void RaisingLevel_ResolvesAnAlreadyPendingMutatingRequest()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            int baseline = StartReadyClient();

            SendCanUseTool("req-raise-1", WireName("uap_scene_create_object"), "{\"name\":\"Foo\"}");
            Assert.IsNotNull(AgentHub.PendingPermission, "must be pending at Ask before the raise.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);

            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            AgentHub.ApplyAutoApproveLevelChanged();

            Assert.IsNull(AgentHub.PendingPermission,
                "raising the level to Undoable must resolve the pending Undoable-mutating request.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count,
                "exactly one control_response must now be written.");
            string written = LastWrittenLine();
            StringAssert.Contains("\"behavior\":\"allow\"", written);
            StringAssert.Contains("req-raise-1", written);
        }

        [Test]
        public void RaisingLevel_InsufficientForThePendingRequest_LeavesItPending()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            int baseline = StartReadyClient();

            // uap_asset_create is mutating AND non-undoable: Undoable is not
            // enough to cover it, only AllUnityOps is.
            SendCanUseTool("req-raise-2", WireName("uap_asset_create"), "{\"path\":\"Assets/Foo.asset\"}");
            Assert.IsNotNull(AgentHub.PendingPermission);

            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            AgentHub.ApplyAutoApproveLevelChanged();

            Assert.IsNotNull(AgentHub.PendingPermission,
                "Undoable still does not cover a non-undoable mutating tool -- the card must stay up.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count,
                "nothing may be auto-answered when the new level still does not cover the request.");
        }

        [Test]
        public void RaisingLevel_NonUapOpsToolPending_NeverResolvesEvenAtAllUnityOps()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            int baseline = StartReadyClient();

            SendWriteCanUseTool("req-raise-3", "Assets/Foo.txt");
            Assert.IsNotNull(AgentHub.PendingPermission);

            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            AgentHub.ApplyAutoApproveLevelChanged();

            Assert.IsNotNull(AgentHub.PendingPermission,
                "a non-UapOps tool must never resolve via the raise-the-level path either.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
        }

        [Test]
        public void ApplyAutoApproveLevelChanged_NothingPending_IsANoOp()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            int baseline = StartReadyClient();

            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;
            Assert.DoesNotThrow(delegate { AgentHub.ApplyAutoApproveLevelChanged(); });

            Assert.IsNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline, _fake.WrittenLines.Count,
                "nothing was pending, so nothing may be written.");
        }

        [Test]
        public void ApplyAutoApproveLevelChanged_CalledTwice_NeverAnswersTheSameRequestTwice()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            int baseline = StartReadyClient();

            SendCanUseTool("req-raise-4", WireName("uap_scene_create_object"), "{\"name\":\"Foo\"}");
            Assert.IsNotNull(AgentHub.PendingPermission);

            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            AgentHub.ApplyAutoApproveLevelChanged();
            Assert.IsNull(AgentHub.PendingPermission);
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);

            // A stray second call (e.g. the header fires the callback again,
            // or the level is toggled back and forth quickly) must be a
            // pure no-op: nothing is pending anymore, so nothing more may be
            // written, and calling it must not throw.
            Assert.DoesNotThrow(delegate { AgentHub.ApplyAutoApproveLevelChanged(); });
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count,
                "a second call must never send a second control_response for the same request.");
        }

        [Test]
        public void RaisingLevel_AutoApprovedPendingRequest_AppendsNoTranscriptNote()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Ask;
            StartReadyClient();

            SendCanUseTool("req-raise-5", WireName("uap_scene_create_object"), "{\"name\":\"Foo\"}");
            int before = AgentHub.Session.messages.Count;

            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.Undoable;
            AgentHub.ApplyAutoApproveLevelChanged();

            Assert.AreEqual(before, AgentHub.Session.messages.Count,
                "resolving via the raise-the-level path is still an ALLOW, which never gets a transcript note.");
        }

        // -- AllTools level (design note 2026-09-10-auto-approve-all-
        // tools-and-lean-auto-continue section 1): the one level that
        // breaks the "non-UapOps tool is never auto-approved" rule -- a
        // Bash can_use_tool is silently allowed, UNLESS the request is
        // requires_user_interaction (AskUserQuestion), which still becomes
        // a pending permission card even at this level. --------------------

        [Test]
        public void LevelAllTools_BashTool_IsAutoApproved()
        {
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllTools;
            int baseline = StartReadyClient();

            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"req-alltools-1\""
                + ",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\""
                + ",\"input\":{\"command\":\"git status\"}}}");
            PumpAll();

            Assert.IsNull(AgentHub.PendingPermission,
                "AllTools must auto-approve a non-UapOps tool -- the reversal of the v0.14.0 decision.");
            Assert.AreEqual(baseline + 1, _fake.WrittenLines.Count);
            StringAssert.Contains("\"behavior\":\"allow\"", LastWrittenLine());
        }

        [Test]
        public void LevelAllTools_RequiresUserInteractionRequest_StillBecomesPendingPermission()
        {
            // The AskUserQuestion wire shape (Tests/Editor/Fixtures/
            // askuser3_inbound.jsonl / PermissionFlowTests.
            // AskUserQuestion_SurfacesInteractionFlag_AndParsedQuestions):
            // requires_user_interaction:true must fall through to the
            // normal card even at the most permissive level -- answering
            // the question is the human's job, never the panel's.
            PanelStateStore.instance.Settings.autoApproveLevel = UapAutoApproveLevel.AllTools;
            int baseline = StartReadyClient();

            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"req-alltools-2\""
                + ",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"AskUserQuestion\""
                + ",\"display_name\":\"AskUserQuestion\""
                + ",\"input\":{\"questions\":[{\"question\":\"Which color do you prefer?\""
                + ",\"header\":\"Color\",\"options\":[{\"label\":\"Red\",\"description\":\"Prefer red.\"}"
                + ",{\"label\":\"Blue\",\"description\":\"Prefer blue.\"}],\"multiSelect\":false}]}"
                + ",\"tool_use_id\":\"toolu_askuser1\",\"requires_user_interaction\":true}}");
            PumpAll();

            Assert.IsNotNull(AgentHub.PendingPermission,
                "requires_user_interaction must still show a card, even at AllTools.");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count,
                "nothing may be auto-answered for a question the human must answer.");
        }
    }
}
