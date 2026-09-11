using Colloid.AgentPanel.Integration;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// INFRA-2c: the drain tick's decision table, extracted from
    /// CompileGate.OnUpdate into the pure DecideDrainStep. The precedence
    /// encoded here is behavior users lose messages over if it drifts:
    /// compiling outranks suspension (the reload resolves it), suspension
    /// outranks sendability (HUB-2: never respawn-fight the suspension),
    /// and a timeout parks the queue without dropping it.
    /// </summary>
    [TestFixture]
    public class CompileGateDrainDecisionTests
    {
        private const double Timeout = 60.0;

        [Test]
        public void EmptyQueue_UnhooksRegardlessOfEverythingElse()
        {
            Assert.AreEqual(CompileGate.DrainStep.UnhookIdle,
                CompileGate.DecideDrainStep(0, true, true, true, 999.0, Timeout));
            Assert.AreEqual(CompileGate.DrainStep.UnhookIdle,
                CompileGate.DecideDrainStep(0, false, false, false, 0.0, Timeout));
        }

        [Test]
        public void Compiling_WaitsAndOutranksSuspension()
        {
            Assert.AreEqual(CompileGate.DrainStep.WaitCompiling,
                CompileGate.DecideDrainStep(1, true, true, false, 0.0, Timeout),
                "the reload will resolve a compile; suspension is judged after it");
            Assert.AreEqual(CompileGate.DrainStep.WaitCompiling,
                CompileGate.DecideDrainStep(1, true, false, true, 999.0, Timeout),
                "compiling also outranks the wait timeout -- the clock resets");
        }

        [Test]
        public void Suspended_UnhooksAndOutranksSendability()
        {
            Assert.AreEqual(CompileGate.DrainStep.SuspendedUnhook,
                CompileGate.DecideDrainStep(1, false, true, true, 0.0, Timeout),
                "HUB-2: a suspended connection must never be respawn-fought, "
                + "even if a client currently looks sendable");
        }

        [Test]
        public void NotSendable_WaitsWithinBudget_TimesOutPastIt()
        {
            Assert.AreEqual(CompileGate.DrainStep.WaitForClient,
                CompileGate.DecideDrainStep(1, false, false, false, 59.9, Timeout));
            Assert.AreEqual(CompileGate.DrainStep.WaitForClient,
                CompileGate.DecideDrainStep(1, false, false, false, Timeout, Timeout),
                "the budget is exclusive: exactly at the limit still waits");
            Assert.AreEqual(CompileGate.DrainStep.TimeoutUnhook,
                CompileGate.DecideDrainStep(1, false, false, false, 60.1, Timeout));
        }

        [Test]
        public void Sendable_ReleasesExactlyOneMessage()
        {
            Assert.AreEqual(CompileGate.DrainStep.SendOne,
                CompileGate.DecideDrainStep(1, false, false, true, 0.0, Timeout));
            Assert.AreEqual(CompileGate.DrainStep.SendOne,
                CompileGate.DecideDrainStep(5, false, false, true, 999.0, Timeout),
                "a sendable client sends -- the wait clock only matters while blocked");
        }
    }
}
