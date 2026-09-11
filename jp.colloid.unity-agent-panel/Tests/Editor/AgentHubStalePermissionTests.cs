using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UICODE-6: RespondToPendingPermission's return value is the ONE
    /// staleness authority. The Always-allow GenericMenu callback fires
    /// only after the native menu closes, by which time the pending
    /// request may have moved on (other host answered, hotkey, auto-approve
    /// level raise, turn end, process death, supersession by a second
    /// can_use_tool) -- the old code persisted the DURABLE panel-side
    /// allowedTools rule BEFORE the id check, so a dropped (stale) decision
    /// still permanently widened permissions. These tests pin the bool
    /// contract the persistence now gates on, replaying real can_use_tool
    /// lines through the same FakeCliProcess seam AgentHubPermissionNoteTests
    /// uses.
    /// </summary>
    [TestFixture]
    public class AgentHubStalePermissionTests
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

        private void SendCanUseTool(string requestId, string toolName)
        {
            _fake.ScriptLine("{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + toolName
                + "\",\"input\":{}}}");
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private static PermissionDecision AlwaysAllow(string wireToolName)
        {
            JsonNode updatedPermissions = JsonNode.NewArray();
            updatedPermissions.Add(
                Colloid.AgentPanel.UI.PermissionCard.SynthesizeMcpAlwaysAllowRule(wireToolName));
            return PermissionDecision.AllowTool(null, updatedPermissions);
        }

        [Test]
        public void MatchingRequestId_ReturnsTrue_AndWritesTheResponse()
        {
            StartReadyClient();
            SendCanUseTool("req-1", "Write");
            int writesBefore = _fake.WrittenLines.Count;

            Assert.IsTrue(AgentHub.RespondToPendingPermission(
                "req-1", PermissionDecision.AllowTool()));

            Assert.IsNull(AgentHub.PendingPermission);
            Assert.Greater(_fake.WrittenLines.Count, writesBefore,
                "an accepted decision must write the control_response");
        }

        [Test]
        public void StaleRequestId_ReturnsFalse_WritesNothing_KeepsThePendingRequest()
        {
            StartReadyClient();
            SendCanUseTool("req-live", "Write");
            int writesBefore = _fake.WrittenLines.Count;

            Assert.IsFalse(AgentHub.RespondToPendingPermission(
                "req-from-an-earlier-card", AlwaysAllow("mcp__unity-ops__uap_asset_create")),
                "a decision captured for an earlier request must be dropped");

            Assert.AreEqual(writesBefore, _fake.WrittenLines.Count,
                "a dropped decision must never reach the wire (its"
                + " updatedPermissions would durably widen CLI-side rules)");
            Assert.IsNotNull(AgentHub.PendingPermission,
                "the live request stays pending for a fresh answer");
            Assert.AreEqual("req-live", AgentHub.PendingPermission.RequestId);
        }

        [Test]
        public void NothingPending_ReturnsFalse()
        {
            StartReadyClient();
            Assert.IsFalse(AgentHub.RespondToPendingPermission(
                "req-anything", PermissionDecision.AllowTool()));
        }

        [Test]
        public void ConcurrentRequests_AnswerInArrivalOrder_NeverCrossAnswered()
        {
            // CORE-6 rewrote this scenario's meaning: a second can_use_tool
            // no longer OVERWRITES the pending slot (that eviction orphaned
            // the first request forever and wedged the turn); it queues
            // behind it. The first card's decision therefore answers its
            // own, still-live request, and the queued one surfaces right
            // after. What this test still pins is the original concern in
            // its new shape: a decision can only ever answer the request id
            // that is CURRENTLY active -- the queued request's id is
            // refused while the first is live (so nothing can widen
            // permissions for a request the user has not seen resolve), and
            // a genuinely dead id stays covered by
            // StaleRequestId_ReturnsFalse_WritesNothing_KeepsThePendingRequest.
            StartReadyClient();
            SendCanUseTool("req-old", "Write");
            SendCanUseTool("req-new", "Bash");

            Assert.IsNotNull(AgentHub.PendingPermission);
            Assert.AreEqual("req-old", AgentHub.PendingPermission.RequestId,
                "arrival order: the first request stays the active one");
            Assert.IsFalse(AgentHub.RespondToPendingPermission(
                "req-new", AlwaysAllow("mcp__unity-ops__uap_asset_create")),
                "the QUEUED request's id must not be answerable while the first is live");

            Assert.IsTrue(AgentHub.RespondToPendingPermission(
                "req-old", PermissionDecision.AllowTool()),
                "the first card's decision answers its own, still-live request");

            Assert.IsNotNull(AgentHub.PendingPermission,
                "answering the first must surface the queued second");
            Assert.AreEqual("req-new", AgentHub.PendingPermission.RequestId);
            Assert.IsTrue(AgentHub.RespondToPendingPermission(
                "req-new", PermissionDecision.AllowTool()));
            Assert.IsNull(AgentHub.PendingPermission);
        }

        [Test]
        public void NullOrEmptyRequestId_ReturnsFalse()
        {
            StartReadyClient();
            SendCanUseTool("req-live", "Write");
            Assert.IsFalse(AgentHub.RespondToPendingPermission(
                null, PermissionDecision.AllowTool()));
            Assert.IsFalse(AgentHub.RespondToPendingPermission(
                string.Empty, PermissionDecision.AllowTool()));
            Assert.IsNotNull(AgentHub.PendingPermission);
        }
    }
}
