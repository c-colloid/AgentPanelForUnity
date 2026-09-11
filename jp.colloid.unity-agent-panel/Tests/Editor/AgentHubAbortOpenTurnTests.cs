using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// HUB-1: AbortOpenTurn is the ONE non-clean turn-exit cleanup, called
    /// from stall/errored/died/teardown. Before it existed the silence
    /// backstop (OnTurnStalled) only finalized the streaming message and
    /// leaked everything else the turn held open -- most visibly the
    /// UapTurnScope, which kept AssetDatabase auto-refresh DISALLOWED for
    /// the rest of the editor session. The direct tests pin the helper's
    /// contract; the wired tests replay the stall through a real
    /// AgentClient over FakeCliProcess (the same seam
    /// AgentHubTurnNonUndoableWarningTests uses) to prove the handler
    /// actually calls it.
    /// </summary>
    [TestFixture]
    public class AgentHubAbortOpenTurnTests
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
            // TurnStalled is deliberately NOT in WireClientForTests' fixed
            // event set (see that seam's doc comment) -- the stall tests
            // below exercise AgentHub.OnTurnStalled, so opt in explicitly.
            AgentHub.WireTurnStalledForTests(_client);
            AgentHub.SetClientForTests(_client);
            SessionStateBridge.AutoContinueTurnIsContinuation = false;
        }

        [TearDown]
        public void TearDown()
        {
            AgentHub.SetClientForTests(null);
            _client.Dispose();
            AgentHub.ResetForTests();
            SessionStateBridge.AutoContinueTurnIsContinuation = false;
            // A test that opened a scope and asserted a failure before the
            // abort ran must not leave auto-refresh disallowed for the
            // rest of the suite.
            UapTurnScope.EndIfActive();
        }

        // -- Direct contract ------------------------------------------------

        [Test]
        public void AbortOpenTurn_ClosesAnOpenTurnScope()
        {
            UapTurnScope.BeginIfNeeded();
            Assert.IsTrue(UapTurnScope.IsActive, "precondition");
            AgentHub.AbortOpenTurn(clearContinuationFlag: true);
            Assert.IsFalse(UapTurnScope.IsActive,
                "the scope (AssetDatabase auto-refresh suppression + Undo"
                + " group) must be released on every non-clean exit");
        }

        [Test]
        public void AbortOpenTurn_ClearContinuationTrue_ClearsTheFlag()
        {
            SessionStateBridge.AutoContinueTurnIsContinuation = true;
            AgentHub.AbortOpenTurn(clearContinuationFlag: true);
            Assert.IsFalse(SessionStateBridge.AutoContinueTurnIsContinuation);
        }

        [Test]
        public void AbortOpenTurn_ClearContinuationFalse_PreservesTheFlag()
        {
            // The teardown semantics: TearDownClient underlies
            // ShutdownForReload, which runs on the exact domain reload this
            // flag exists to survive.
            SessionStateBridge.AutoContinueTurnIsContinuation = true;
            AgentHub.AbortOpenTurn(clearContinuationFlag: false);
            Assert.IsTrue(SessionStateBridge.AutoContinueTurnIsContinuation,
                "teardown (reload) must NOT clear the continuation flag");
        }

        [Test]
        public void AbortOpenTurn_IsIdempotent()
        {
            UapTurnScope.BeginIfNeeded();
            AgentHub.AbortOpenTurn(clearContinuationFlag: true);
            Assert.DoesNotThrow(delegate
            {
                AgentHub.AbortOpenTurn(clearContinuationFlag: true);
                AgentHub.AbortOpenTurn(clearContinuationFlag: false);
            }, "several terminal paths fire for one exit (ProcessDied AND"
                + " the Errored transition AND an eventual teardown) -- the"
                + " cleanup must be freely repeatable");
            Assert.IsFalse(UapTurnScope.IsActive);
        }

        // -- Wired: the silence backstop actually aborts ---------------------

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

        private void ForceStall()
        {
            // The silence backstop compares wall-clock seconds since the
            // last activity; a near-zero timeout plus a real sleep crosses
            // it deterministically on the next pump.
            _client.SilenceTimeoutSeconds = 0.001;
            System.Threading.Thread.Sleep(15);
            _client.Pump();
        }

        [Test]
        public void StalledTurn_ReleasesTheTurnScope()
        {
            StartReadyClient();
            Assert.IsTrue(AgentHub.SendUserMessage("do something", "do something", null),
                "precondition: the send must open a real turn");
            Assert.IsTrue(UapTurnScope.IsActive,
                "precondition: SendUserMessage arms the turn scope");
            Assert.IsTrue(_client.TurnActive, "precondition");

            ForceStall();

            Assert.IsFalse(_client.TurnActive, "the backstop forces the turn closed");
            Assert.IsFalse(UapTurnScope.IsActive,
                "the ORIGINAL HUB-1 symptom: a stalled turn left AssetDatabase"
                + " auto-refresh disallowed for the rest of the session");
        }

        [Test]
        public void StalledContinuationTurn_ClearsTheContinuationFlag()
        {
            StartReadyClient();
            AgentHub.SendUserMessage("continue", "continue", null);
            SessionStateBridge.AutoContinueTurnIsContinuation = true;

            ForceStall();

            Assert.IsFalse(SessionStateBridge.AutoContinueTurnIsContinuation,
                "a stalled continuation turn never reaches"
                + " HandleAutoContinueArming; leaving the flag stranded true"
                + " makes the next human-prompted turn refuse to arm"
                + " (defect 5's mechanics via the stall path)");
        }

        [Test]
        public void StalledTurnsNonUndoableTools_DoNotWarnOnTheNextTurn()
        {
            StartReadyClient();
            AgentHub.SendUserMessage("first", "first", null);
            // A non-undoable UapOps tool runs, then the turn stalls before
            // any result arrives.
            _fake.ScriptLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\""
                + ",\"id\":\"t1\",\"name\":\"mcp__" + UapOpsMcpConfig.ServerName
                + "__uap_asset_create\",\"input\":{}}]}}");
            PumpAll();
            ForceStall();
            int messagesAfterStall = AgentHub.Session.messages.Count;

            // A fresh, fully clean second turn completes.
            AgentHub.SendUserMessage("second", "second", null);
            _fake.ScriptLine("{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s1\"}");
            PumpAll();

            for (int i = messagesAfterStall; i < AgentHub.Session.messages.Count; i++)
            {
                ChatMessage message = AgentHub.Session.messages[i];
                for (int b = 0; b < message.blocks.Count; b++)
                {
                    if (message.blocks[b].kind == ChatBlockKind.SystemNote)
                    {
                        StringAssert.DoesNotContain("uap_asset_create", message.blocks[b].text,
                            "the STALLED turn's non-undoable warning must not"
                            + " attach to a later, unrelated turn");
                    }
                }
            }
        }
    }
}
