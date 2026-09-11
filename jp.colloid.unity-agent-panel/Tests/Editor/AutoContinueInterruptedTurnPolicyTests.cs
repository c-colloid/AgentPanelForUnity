using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The pure decision behind PanelSettings.autoContinueInterruptedTurn
    /// (AgentHub.TryAutoContinueInterruptedTurn, design note 2026-09-06
    /// section 5-1). Every "no" here is a guardrail the user would lose a
    /// turn of trust over: the feature must never send for a user who did
    /// not opt in, for a reload that interrupted nothing, while the
    /// crash-loop guard holds, or on top of an already queued compile
    /// continuation.
    ///
    /// Design note 2026-09-10-auto-approve-all-tools-and-lean-auto-continue
    /// section 3: the "at most three in a row" streak cap (and the
    /// SessionStateBridge.AutoContinueInterruptedStreak counter it read)
    /// were removed -- they stopped legitimate work (a commit/reload/
    /// continue cycle repeated three times over is exactly what the user
    /// asked for), while the failure they guarded against (a reload storm)
    /// is already covered by crash-loop suspension and the user's own
    /// Interrupt. ShouldAutoContinueInterrupted is now a pure function of
    /// four booleans, with no count/cap parameter at all.
    /// </summary>
    [TestFixture]
    public class AutoContinueInterruptedTurnPolicyTests
    {
        [Test]
        public void AllGuardrailsPass_Continues()
        {
            Assert.IsTrue(AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                true, true, false, false));
        }

        [Test]
        public void Disabled_NeverContinues()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                false, true, false, false), "default OFF must mean off");
        }

        [Test]
        public void ReloadThatInterruptedNothing_NeverContinues()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                true, false, false, false),
                "a reload between turns has nothing to continue -- sending would open an unsolicited turn");
        }

        [Test]
        public void CrashLoopSuspended_Blocks()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                true, true, true, false),
                "HUB-2/defect 4: never respawn-fight the suspension");
        }

        [Test]
        public void CompileContinuationAlreadyQueued_Blocks()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                true, true, false, true),
                "one reload, one continuation: the compile one already owns the queue slot");
        }

        /// <summary>
        /// Regression guard for the removed streak cap: repeated calls with
        /// every guardrail passing must never be refused by a count. Ten is
        /// well past the old cap of three, which is exactly the point.
        /// </summary>
        [Test]
        public void RepeatedCalls_AreNeverRefusedByACount()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.IsTrue(AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                    true, true, false, false),
                    "call #" + i + " must still continue -- there is no streak cap anymore");
            }
        }
    }
}
