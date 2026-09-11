using Colloid.AgentPanel.Core.Client;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design note 2026-09-10 section 1 (ULTRA-3 first half): CORE-6 queues
    /// every can_use_tool request behind the active one with NO limit
    /// (AgentClient._permissionQueue, AgentClient.cs:218), but nothing ever
    /// exposed how many were waiting -- a burst of parallel subagent
    /// tool_use calls looked identical to a single request from outside
    /// the client. QueuedPermissionCount fixes that; this fixture drains a
    /// three-deep queue one answer at a time and pins that the count
    /// tracks the FIFO exactly (decrementing by one per answer, the active
    /// request itself never counted).
    /// </summary>
    [TestFixture]
    public class AgentClientPermissionQueueDepthTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
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

        /// <summary>Schedules (but does not yet pump) one can_use_tool request with a distinct id.</summary>
        private void ScriptCanUseTool(string requestId)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Write\""
                + ",\"input\":{\"file_path\":\"Assets/Foo/" + requestId + ".txt\"}}}");
        }

        [Test]
        public void NothingPending_QueuedPermissionCountIsZero()
        {
            Assert.AreEqual(0, _client.QueuedPermissionCount);
            Assert.IsNull(_client.PendingPermissionRequestId);
        }

        [Test]
        public void ThreeRequestsQueuedInOneTurn_DrainOneAtATime_CountTracksTheFifo()
        {
            StartReadyClient();

            // Three concurrent can_use_tool prompts arrive in one turn
            // (parallel tool_use, e.g. subagents) before any is answered --
            // CORE-6 queues all three; only the first is ever promoted to
            // "active" (PendingPermissionRequestId) until it is resolved.
            ScriptCanUseTool("perm-q-1");
            ScriptCanUseTool("perm-q-2");
            ScriptCanUseTool("perm-q-3");
            PumpAll();

            Assert.AreEqual("perm-q-1", _client.PendingPermissionRequestId);
            Assert.AreEqual(2, _client.QueuedPermissionCount,
                "two requests must wait behind the active one.");

            _client.RespondToPermission("perm-q-1", PermissionDecision.AllowTool());
            Assert.AreEqual("perm-q-2", _client.PendingPermissionRequestId,
                "answering the active request must promote the next queued one.");
            Assert.AreEqual(1, _client.QueuedPermissionCount);

            _client.RespondToPermission("perm-q-2", PermissionDecision.AllowTool());
            Assert.AreEqual("perm-q-3", _client.PendingPermissionRequestId);
            Assert.AreEqual(0, _client.QueuedPermissionCount,
                "the last queued request has been promoted; nothing waits behind it.");

            _client.RespondToPermission("perm-q-3", PermissionDecision.AllowTool());
            Assert.IsNull(_client.PendingPermissionRequestId,
                "no request remains active once the last one is answered.");
            Assert.AreEqual(0, _client.QueuedPermissionCount);
        }
    }
}
