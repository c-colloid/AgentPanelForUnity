using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// ReloadLifecycle's post-reload reconciliation is now an idempotent
    /// entry point (EnsureStartupReconciled) shared by the first
    /// EditorApplication.update tick and AgentHub.EnsureStarted, so the
    /// window's deferred boot call can no longer start the CLI ahead of the
    /// restore sequence and clobber SessionStateBridge.TurnRunning / the
    /// auto-continue ticket (docs/design-notes/2026-09-06-domain-reload-
    /// resilience-and-hot-reload.md section 2.1). EditMode tests cannot
    /// perform a real domain reload (the suite's long-standing boundary),
    /// so this pins the one property the fix rests on: the sequence runs
    /// at most once per domain, and every later call is a no-op.
    /// </summary>
    [TestFixture]
    public class ReloadLifecycleTests
    {
        [Test]
        public void EnsureStartupReconciled_RunsAtMostOncePerDomain()
        {
            // The first update tick has normally already consumed the
            // one-shot by the time any test runs; tolerate either order by
            // making the first call here and pinning everything after it.
            ReloadLifecycle.EnsureStartupReconciled();

            Assert.IsTrue(ReloadLifecycle.StartupReconciled,
                "after one call the reconciliation must be marked done for this domain");
            Assert.IsFalse(ReloadLifecycle.EnsureStartupReconciled(),
                "a second call must be a no-op: AgentHub.EnsureStarted calls this on every "
                + "start, and RestoreAfterReload -> EnsureStarted re-enters it mid-sequence");
            Assert.IsFalse(ReloadLifecycle.EnsureStartupReconciled(),
                "and so must every call after that");
        }
    }

    /// <summary>
    /// Design note 2026-09-10 section 3: a can_use_tool request still
    /// awaiting the user's answer used to be discarded silently by
    /// ShutdownForReload's teardown -- no transcript note, nothing in the
    /// interrupted-turn continuation. These pin the capture/consume pair
    /// (CaptureReloadDroppedPermissionTool / AgentHub.
    /// ConsumeReloadDroppedPermission) that fixes that, without ever
    /// triggering a real domain reload -- the same
    /// WireClientForTests/SetClientForTests + FakeCliProcess seam
    /// AgentHubPermissionNoteTests uses to get a real pending can_use_tool
    /// request. Bash is used because it never auto-approves at any
    /// UapAutoApproveLevel (AgentHub.TryAutoApproveUapOpsTool resolves it
    /// to isUapOpsTool:false), unlike Write/Edit which the script gate can
    /// intercept when enabled.
    /// </summary>
    [TestFixture]
    public class ReloadLifecycleDroppedPermissionTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private string _savedReloadDroppedPermissionTool;

        [SetUp]
        public void SetUp()
        {
            L10n.OverrideForTests(PanelLanguage.English);
            _savedReloadDroppedPermissionTool = SessionStateBridge.ReloadDroppedPermissionTool;
            SessionStateBridge.ReloadDroppedPermissionTool = string.Empty;
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
            SessionStateBridge.ReloadDroppedPermissionTool = _savedReloadDroppedPermissionTool;
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

        private void SendCanUseTool(string requestId, string toolName)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + toolName
                + "\",\"input\":{\"command\":\"echo hi\"}}}");
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        [Test]
        public void CaptureReloadDroppedPermissionTool_WritesDisplayName_WhenPermissionPending()
        {
            StartReadyClient();
            SendCanUseTool("req-1", "Bash");
            Assert.IsNotNull(AgentHub.PendingPermission, "the request must actually be pending, not auto-approved");

            ReloadLifecycle.CaptureReloadDroppedPermissionTool();

            Assert.AreEqual("Bash", SessionStateBridge.ReloadDroppedPermissionTool);
        }

        [Test]
        public void CaptureReloadDroppedPermissionTool_WritesEmpty_WhenNothingPending()
        {
            StartReadyClient();
            Assert.IsNull(AgentHub.PendingPermission);

            ReloadLifecycle.CaptureReloadDroppedPermissionTool();

            Assert.AreEqual(string.Empty, SessionStateBridge.ReloadDroppedPermissionTool);
        }

        [Test]
        public void ConsumeReloadDroppedPermission_Announce_AddsExactlyOneSystemNote_AndClearsSessionState()
        {
            StartReadyClient();
            SendCanUseTool("req-2", "Bash");
            ReloadLifecycle.CaptureReloadDroppedPermissionTool();
            int before = AgentHub.Session.messages.Count;

            string result = AgentHub.ConsumeReloadDroppedPermission(true);

            Assert.AreEqual("Bash", result);
            Assert.AreEqual(string.Empty, SessionStateBridge.ReloadDroppedPermissionTool,
                "the SessionState key must be cleared after consuming");
            Assert.AreEqual(before + 1, AgentHub.Session.messages.Count,
                "announce:true must append exactly one transcript message");
            ChatMessage last = AgentHub.Session.messages[AgentHub.Session.messages.Count - 1];
            Assert.AreEqual(ChatMessage.RoleSystem, last.role);
            Assert.AreEqual(ChatBlockKind.SystemNote, last.blocks[0].kind);
            StringAssert.Contains("Bash", last.blocks[0].text);
        }

        [Test]
        public void ConsumeReloadDroppedPermission_NoAnnounce_ClearsWithoutAddingANote()
        {
            StartReadyClient();
            SendCanUseTool("req-3", "Bash");
            ReloadLifecycle.CaptureReloadDroppedPermissionTool();
            int before = AgentHub.Session.messages.Count;

            string result = AgentHub.ConsumeReloadDroppedPermission(false);

            Assert.AreEqual("Bash", result);
            Assert.AreEqual(string.Empty, SessionStateBridge.ReloadDroppedPermissionTool);
            Assert.AreEqual(before, AgentHub.Session.messages.Count,
                "announce:false must not add any transcript message");
        }
    }
}
