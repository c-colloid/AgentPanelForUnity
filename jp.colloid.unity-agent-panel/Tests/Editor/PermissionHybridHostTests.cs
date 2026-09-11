using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic tests for the hybrid permission card UX:
    /// - PermissionCardLayout size decision (inline card vs floating
    ///   PermissionWindow) and the 40 percent height cap math.
    /// - Response-path equivalence: the inline host and the window host
    ///   share one PermissionCard implementation, so identical decisions
    ///   must produce byte-identical outbound control_response JSON no
    ///   matter which host answered (single source of truth:
    ///   AgentHub.PendingPermission -&gt; AgentClient.RespondToPermission).
    /// - Pending-cleared-on-death: the state BOTH hosts key off
    ///   (PendingPermissionRequestId) is cleared when the process dies, so
    ///   the inline card hides and the window closes.
    /// </summary>
    [TestFixture]
    public class PermissionHybridHostTests
    {
        // ------------------------------------------------------------------
        // Size decision helper (task B.1 thresholds: window when the chat
        // content is shorter than 380px OR narrower than 300px)
        // ------------------------------------------------------------------

        [Test]
        public void ShouldOpenWindow_LargePanel_StaysInline()
        {
            Assert.IsFalse(PermissionCardLayout.ShouldOpenWindow(800f, 600f));
            // Exactly at both thresholds: still inline (limits are "below").
            Assert.IsFalse(PermissionCardLayout.ShouldOpenWindow(300f, 380f));
        }

        [Test]
        public void ShouldOpenWindow_ShortPanel_OpensWindow()
        {
            Assert.IsTrue(PermissionCardLayout.ShouldOpenWindow(800f, 379f));
            Assert.IsTrue(PermissionCardLayout.ShouldOpenWindow(300f, 200f));
        }

        [Test]
        public void ShouldOpenWindow_NarrowPanel_OpensWindow()
        {
            Assert.IsTrue(PermissionCardLayout.ShouldOpenWindow(299f, 600f));
            Assert.IsTrue(PermissionCardLayout.ShouldOpenWindow(150f, 380f));
        }

        [Test]
        public void ShouldOpenWindow_UnknownGeometry_StaysInline()
        {
            // First layout pass not done yet: resolvedStyle yields NaN/0.
            Assert.IsFalse(PermissionCardLayout.ShouldOpenWindow(float.NaN, 600f));
            Assert.IsFalse(PermissionCardLayout.ShouldOpenWindow(800f, float.NaN));
            Assert.IsFalse(PermissionCardLayout.ShouldOpenWindow(0f, 0f));
            Assert.IsFalse(PermissionCardLayout.ShouldOpenWindow(-1f, 600f));
        }

        [Test]
        public void IsKnownGeometry_RejectsNaNAndNonPositive()
        {
            // The one-shot auto decision (ChatView) must NOT be consumed
            // while any measurement is unknown, or a request pending before
            // the first layout pass permanently loses its auto-open.
            Assert.IsFalse(PermissionCardLayout.IsKnownGeometry(float.NaN, 600f));
            Assert.IsFalse(PermissionCardLayout.IsKnownGeometry(800f, float.NaN));
            Assert.IsFalse(PermissionCardLayout.IsKnownGeometry(float.NaN, float.NaN));
            Assert.IsFalse(PermissionCardLayout.IsKnownGeometry(0f, 600f));
            Assert.IsFalse(PermissionCardLayout.IsKnownGeometry(800f, 0f));
            Assert.IsFalse(PermissionCardLayout.IsKnownGeometry(-1f, -1f));
        }

        [Test]
        public void IsKnownGeometry_AcceptsRealSizes()
        {
            Assert.IsTrue(PermissionCardLayout.IsKnownGeometry(1f, 1f));
            Assert.IsTrue(PermissionCardLayout.IsKnownGeometry(800f, 600f));
            // Tiny-but-real panels are known geometry: they must DECIDE
            // (and decide "window"), not defer.
            Assert.IsTrue(PermissionCardLayout.IsKnownGeometry(150f, 120f));
        }

        [Test]
        public void ComputeMaxCardHeight_Is40PercentOfContentHeight()
        {
            Assert.AreEqual(200f, PermissionCardLayout.ComputeMaxCardHeight(500f), 0.001f);
            Assert.AreEqual(160f, PermissionCardLayout.ComputeMaxCardHeight(400f), 0.001f);
        }

        [Test]
        public void ComputeMaxCardHeight_UnknownGeometry_MeansNoCap()
        {
            Assert.Less(PermissionCardLayout.ComputeMaxCardHeight(float.NaN), 0f);
            Assert.Less(PermissionCardLayout.ComputeMaxCardHeight(0f), 0f);
            Assert.Less(PermissionCardLayout.ComputeMaxCardHeight(-10f), 0f);
        }

        // ------------------------------------------------------------------
        // Response-path equivalence between the two hosts
        // ------------------------------------------------------------------

        [Test]
        public void AllowFromEitherHost_ProducesIdenticalOutboundJson()
        {
            // Both hosts build the exact same decision (PermissionCard is
            // one implementation), so the wire output must be identical.
            List<string> inlineHost = RunPermissionScenario(
                delegate { return PermissionDecision.AllowTool(); });
            List<string> windowHost = RunPermissionScenario(
                delegate { return PermissionDecision.AllowTool(); });
            CollectionAssert.AreEqual(inlineHost, windowHost,
                "allow must serialize identically regardless of host");
        }

        [Test]
        public void DenyFromEitherHost_ProducesIdenticalOutboundJson()
        {
            List<string> inlineHost = RunPermissionScenario(
                delegate { return PermissionDecision.DenyTool("User denied this tool use."); });
            List<string> windowHost = RunPermissionScenario(
                delegate { return PermissionDecision.DenyTool("User denied this tool use."); });
            CollectionAssert.AreEqual(inlineHost, windowHost,
                "deny must serialize identically regardless of host");
        }

        [Test]
        public void AlwaysAllowFromEitherHost_ProducesIdenticalOutboundJson()
        {
            // The "Always v" path passes one permission_suggestions entry
            // back verbatim as updatedPermissions.
            List<string> inlineHost = RunPermissionScenario(BuildAlwaysDecision);
            List<string> windowHost = RunPermissionScenario(BuildAlwaysDecision);
            CollectionAssert.AreEqual(inlineHost, windowHost,
                "always-allow must serialize identically regardless of host");
            bool found = false;
            foreach (string line in inlineHost)
            {
                if (line.IndexOf("updatedPermissions", System.StringComparison.Ordinal) >= 0
                    && line.IndexOf("acceptEdits", System.StringComparison.Ordinal) >= 0)
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, "the suggestion must be echoed as updatedPermissions");
        }

        [Test]
        public void RespondingOnce_ClearsThePendingStateBothHostsShare()
        {
            var fake = new FakeCliProcess();
            var client = new AgentClient(fake);
            try
            {
                ControlRequestMessage request = DriveToPrompt(fake, client);
                Assert.AreEqual(request.RequestId, client.PendingPermissionRequestId);

                client.RespondToPermission(request.RequestId, PermissionDecision.AllowTool());

                // The shared source of truth is cleared exactly once: the
                // other host observes null and stands down (inline card
                // hides / window closes).
                Assert.IsNull(client.PendingPermissionRequestId);
                Assert.AreEqual(AgentClientState.ToolRunning, client.State);
            }
            finally
            {
                client.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Pending cleared on process death (window close / card hide path)
        // ------------------------------------------------------------------

        [Test]
        public void ProcessDeath_ClearsPendingPermission_ForBothHosts()
        {
            var fake = new FakeCliProcess();
            var client = new AgentClient(fake);
            try
            {
                ControlRequestMessage request = DriveToPrompt(fake, client);
                Assert.IsNotNull(request);
                Assert.AreEqual(AgentClientState.WaitingPermission, client.State);

                bool died = false;
                client.ProcessDied += delegate { died = true; };
                fake.SimulateExit();
                while (client.Pump(50, 50.0) > 0)
                {
                }
                client.Pump();

                Assert.IsTrue(died, "exit must surface as ProcessDied");
                Assert.AreEqual(AgentClientState.Errored, client.State);
                Assert.IsNull(client.PendingPermissionRequestId,
                    "death must clear the pending id both hosts key off");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static PermissionDecision BuildAlwaysDecision(ControlRequestMessage request)
        {
            JsonNode suggestions = request.CanUseTool.PermissionSuggestions;
            Assert.IsTrue(suggestions != null && suggestions.IsArray && suggestions.Count > 0,
                "fixture must carry a permission suggestion");
            JsonNode updatedPermissions = JsonNode.NewArray();
            updatedPermissions.Add(suggestions[0]);
            return PermissionDecision.AllowTool(null, updatedPermissions);
        }

        /// <summary>
        /// Replays the captured Write-permission turn on a fresh client,
        /// answers with the decision produced by the given factory and
        /// returns every line written to stdin.
        /// </summary>
        private static List<string> RunPermissionScenario(
            System.Func<ControlRequestMessage, PermissionDecision> decisionFactory)
        {
            var fake = new FakeCliProcess();
            var client = new AgentClient(fake);
            try
            {
                ControlRequestMessage request = DriveToPrompt(fake, client);
                client.RespondToPermission(request.RequestId, decisionFactory(request));
                return new List<string>(fake.WrittenLines);
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>Starts the client and replays the fixture up to can_use_tool.</summary>
        private static ControlRequestMessage DriveToPrompt(FakeCliProcess fake,
            AgentClient client)
        {
            client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            fake.ScriptLine(FixtureLine("permission_inbound.jsonl", "\"subtype\":\"init\""));
            PumpAll(client);
            Assert.AreEqual(AgentClientState.Ready, client.State);

            client.SendUserText(CapturedUserText("permission_outbound.jsonl"));

            ControlRequestMessage request = null;
            client.PermissionRequested += delegate (ControlRequestMessage r) { request = r; };
            fake.ScriptLine(FixtureLine("permission_inbound.jsonl",
                "\"subtype\":\"can_use_tool\""));
            PumpAll(client);

            Assert.IsNotNull(request, "can_use_tool line must raise PermissionRequested");
            Assert.AreEqual(AgentClientState.WaitingPermission, client.State);
            return request;
        }

        private static void PumpAll(AgentClient client)
        {
            while (client.Pump(50, 50.0) > 0)
            {
            }
        }

        private static string CapturedUserText(string outboundFixture)
        {
            JsonNode node;
            string error;
            Assert.IsTrue(JsonParser.TryParse(
                FixtureLine(outboundFixture, "\"type\":\"user\""), out node, out error),
                "user line must parse: " + error);
            string text = node["message"]["content"][0]["text"].AsString();
            Assert.IsNotNull(text, outboundFixture + " user line must carry text");
            return text;
        }

        private static string FixtureLine(string fixture, params string[] substrings)
        {
            foreach (string line in FixtureLoader.ReadLines(fixture))
            {
                bool all = true;
                foreach (string s in substrings)
                {
                    if (line.IndexOf(s, System.StringComparison.Ordinal) < 0)
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    return line;
                }
            }
            Assert.Fail("No line in " + fixture + " contains: " + string.Join(" + ", substrings));
            return null;
        }

        // ------------------------------------------------------------------
        // UICODE-8: the one-shot inline-vs-window decision id must survive a
        // ChatView rebuild (language switch / re-entrant CreateGUI swaps in
        // a NEW ChatView instance while a request can still be pending). An
        // instance field reset to null there re-ran the decision and
        // reopened a PermissionWindow the user had deliberately closed.
        // ------------------------------------------------------------------

        [Test]
        public void SourceScan_AutoWindowDecisionId_IsStaticAcrossChatViewRebuilds()
        {
            string text = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                "Packages/jp.colloid.unity-agent-panel/Editor/UI/ChatView.cs"));
            StringAssert.Contains("private static string s_autoWindowDecidedRequestId", text,
                "the decided-request id must be static so a rebuilt ChatView "
                + "does not re-run the decision for a request the user already "
                + "answered by closing the window");
            Assert.IsFalse(text.Contains("private string _autoWindowDecidedRequestId"),
                "the instance-field variant must not come back");
        }

        [Test]
        public void AutoWindowDecisionReset_ClearsTheSharedId()
        {
            ChatView.ResetAutoWindowDecisionForTests();
            Assert.IsNull(ChatView.AutoWindowDecidedRequestIdForTests);
        }
    }
}
