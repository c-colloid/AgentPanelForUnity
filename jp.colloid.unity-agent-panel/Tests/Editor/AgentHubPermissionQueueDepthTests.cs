using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design note 2026-09-10 section 1 (ULTRA-3 first half): the Hub-level
    /// half of AgentClientPermissionQueueDepthTests -- AgentHub.
    /// PendingPermissionQueueDepth must mirror the wired client's own
    /// QueuedPermissionCount, and PermissionCard's header label must show
    /// "N more waiting" whenever that depth is non-zero and stay hidden at
    /// zero. Same AgentHub.WireClientForTests/SetClientForTests seam as
    /// AgentHubPermissionNoteTests/AgentHubScriptGateTests.
    /// </summary>
    [TestFixture]
    public class AgentHubPermissionQueueDepthTests
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

        /// <summary>
        /// Write to a plain .txt path -- never auto-approved (Write is not
        /// a UapOps tool) and never script-gated (not a .cs/.asmdef under
        /// Assets/), so it always surfaces a real pending card, same as
        /// AgentHubPermissionNoteTests/AgentHubScriptGateTests use it.
        /// </summary>
        private void SendCanUseTool(string requestId, string filePath)
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

        [Test]
        public void NothingPending_QueueDepthIsZero()
        {
            Assert.AreEqual(0, AgentHub.PendingPermissionQueueDepth);
        }

        [Test]
        public void TwoRequestsQueued_DepthIsOneWhileFirstPending_ThenZeroAfterEachAnswer()
        {
            StartReadyClient();

            SendCanUseTool("req-qd-1", "Assets/Foo/A.txt");
            SendCanUseTool("req-qd-2", "Assets/Foo/B.txt");

            Assert.AreEqual("req-qd-1", AgentHub.PendingPermission.RequestId);
            Assert.AreEqual(1, AgentHub.PendingPermissionQueueDepth,
                "one request must wait behind the active one.");

            AgentHub.RespondToPendingPermission("req-qd-1", PermissionDecision.AllowTool());

            Assert.AreEqual("req-qd-2", AgentHub.PendingPermission.RequestId,
                "answering the active request must promote the queued one.");
            Assert.AreEqual(0, AgentHub.PendingPermissionQueueDepth,
                "nothing waits behind the now-active second request.");

            AgentHub.RespondToPendingPermission("req-qd-2", PermissionDecision.AllowTool());

            Assert.IsNull(AgentHub.PendingPermission);
            Assert.AreEqual(0, AgentHub.PendingPermissionQueueDepth);
        }

        // -- PermissionCard structural: the queue-depth label mirrors the
        // hub's depth on every Refresh(). ------------------------------------

        private static Label FindQueueDepthLabel(PermissionCard card)
        {
            return card.Root.Q<Label>(className: "uap-perm-queue-depth");
        }

        [Test]
        public void Card_QueueDepthOne_ShowsFormattedLabel()
        {
            StartReadyClient();
            SendCanUseTool("req-qd-card-1", "Assets/Foo/A.txt");
            SendCanUseTool("req-qd-card-2", "Assets/Foo/B.txt");
            Assert.AreEqual(1, AgentHub.PendingPermissionQueueDepth);

            var card = new PermissionCard();
            card.Refresh(AgentHub.PendingPermission);

            Label label = FindQueueDepthLabel(card);
            Assert.IsNotNull(label);
            Assert.AreEqual(DisplayStyle.Flex, label.style.display.value);
            Assert.AreEqual("1 more waiting", label.text);
        }

        [Test]
        public void Card_QueueDepthZero_HidesLabel()
        {
            StartReadyClient();
            SendCanUseTool("req-qd-card-3", "Assets/Foo/A.txt");
            Assert.AreEqual(0, AgentHub.PendingPermissionQueueDepth);

            var card = new PermissionCard();
            card.Refresh(AgentHub.PendingPermission);

            Label label = FindQueueDepthLabel(card);
            Assert.IsNotNull(label);
            Assert.AreEqual(DisplayStyle.None, label.style.display.value);
        }

        [Test]
        public void Card_DepthChangesBehindTheSameActiveRequest_UpdatesOnRefreshWithoutRebuild()
        {
            // The active request id does NOT change when a SECOND request
            // queues up behind it -- this pins that RefreshQueueDepthLabel
            // runs even on Refresh()'s early-return path (same request id,
            // no Build()), not only when a brand new request rebuilds the
            // whole card.
            StartReadyClient();
            SendCanUseTool("req-qd-card-4", "Assets/Foo/A.txt");
            Assert.AreEqual(0, AgentHub.PendingPermissionQueueDepth);

            var card = new PermissionCard();
            card.Refresh(AgentHub.PendingPermission);
            Assert.AreEqual(DisplayStyle.None, FindQueueDepthLabel(card).style.display.value);

            // A second request queues up BEHIND the still-active first one.
            SendCanUseTool("req-qd-card-5", "Assets/Foo/B.txt");
            Assert.AreEqual("req-qd-card-4", AgentHub.PendingPermission.RequestId,
                "the active request must not change while a second one only queues.");
            Assert.AreEqual(1, AgentHub.PendingPermissionQueueDepth);

            card.Refresh(AgentHub.PendingPermission);
            Assert.AreEqual(DisplayStyle.Flex, FindQueueDepthLabel(card).style.display.value);
            Assert.AreEqual("1 more waiting", FindQueueDepthLabel(card).text);
        }
    }
}
