using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression for the "UapTurnScope.EndIfActive() is only ever reached
    /// through a clean OnTurnCompleted" defect (design section 1.3/8.2):
    /// every OTHER path out of an EXECUTING turn -- a mid-turn CLI process
    /// death (Errored), and TearDownClient (the shared teardown behind
    /// Reconnect/StartFresh/SwitchToSession/Shutdown/ShutdownForReload) --
    /// must ALSO release the turn scope, or a crash/manual reconnect leaves
    /// AssetDatabase auto-refresh disallowed for the rest of the editor
    /// session. Touches the REAL, process-global refresh-suppression
    /// counter (like UapTurnScopeTests), so every test cleans up in
    /// try/finally.
    /// </summary>
    [TestFixture]
    public class AgentHubTurnScopeReleaseTests
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
            AgentHub.WireStateChangedForTests(_client);
            AgentHub.SetClientForTests(_client);
        }

        [TearDown]
        public void TearDown()
        {
            if (UapTurnScope.IsActive)
            {
                UapTurnScope.EndIfActive();
            }
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

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        [Test]
        public void ProcessDeathMidTurn_ClosesTheOpenTurnScope()
        {
            StartReadyClient();
            Assert.IsFalse(UapTurnScope.IsActive);

            Assert.IsTrue(AgentHub.SendUserMessage("hello", null, null));
            Assert.IsTrue(UapTurnScope.IsActive, "the first send must open the turn scope.");

            _fake.FailWrites = true;
            AgentHub.SendUserMessage("continue", null, null);

            Assert.AreEqual(AgentClientState.Errored, _client.State);
            Assert.IsFalse(UapTurnScope.IsActive,
                "a mid-turn process death must release the turn scope, not leave AssetDatabase"
                    + " auto-refresh suppressed for the rest of the editor session.");
        }

        [Test]
        public void TearDownClientForTests_ClosesAnOpenTurnScope()
        {
            Assert.IsFalse(UapTurnScope.IsActive);
            UapTurnScope.BeginIfNeeded();

            AgentHub.TearDownClientForTests();

            Assert.IsFalse(UapTurnScope.IsActive,
                "Reconnect/StartFresh/SwitchToSession/Shutdown all funnel through TearDownClient;"
                    + " none of them may leave a stale turn scope open.");
        }

        [Test]
        public void TearDownClientForTests_WithNoOpenScope_IsANoOp()
        {
            Assert.IsFalse(UapTurnScope.IsActive);
            Assert.DoesNotThrow(delegate { AgentHub.TearDownClientForTests(); });
            Assert.IsFalse(UapTurnScope.IsActive);
        }
    }
}
