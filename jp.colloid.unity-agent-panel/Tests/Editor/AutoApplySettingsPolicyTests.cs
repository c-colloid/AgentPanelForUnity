using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guards for AutoApplySettingsPolicy.Evaluate (docs/design-
    /// notes/2026-08-01-settings-auto-apply.md section 3): the decision
    /// AgentHub.RequestAutoApplyReconnect's debounce tick and TurnCompleted
    /// hook both defer to. AgentHub itself (the stateful debounce clock,
    /// EditorApplication.update pump, and the real Reconnect() call) is
    /// integration-only and not unit-tested here -- see
    /// LiveCliIntegrationTests for the real end-to-end CLI round trip.
    /// </summary>
    public class AutoApplySettingsPolicyTests
    {
        private const double Debounce = AutoApplySettingsPolicy.DebounceSeconds;

        // -- No pending change / not connected -------------------------------------

        [Test]
        public void NoReconnectNeeded_DoesNothing_EvenWhenIdleAndPastDebounce()
        {
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: false, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 10.0));
        }

        [Test]
        public void Disconnected_DoesNothing_EvenWhenPastDebounceAndReconnectNeeded()
        {
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: false, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 10.0));
        }

        [Test]
        public void Disconnected_DoesNothing_EvenWhenBusy()
        {
            // Not connected wins over every other input: "not connected -> do nothing".
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: false, turnRunning: true,
                pendingPermission: true, secondsSinceLastEdit: 10.0));
        }

        // -- Debounce / coalescing --------------------------------------------------

        [Test]
        public void WithinDebounceWindow_DoesNothing_EvenWhenIdle()
        {
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: Debounce - 0.1));
        }

        [Test]
        public void ExactlyAtDebounceBoundary_Applies()
        {
            Assert.AreEqual(AutoApplyDecision.ApplyNow, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: Debounce));
        }

        [Test]
        public void IdleFiresOnce_AfterDebounceElapses()
        {
            // A single edit: nothing happens for 1.5s, then exactly one
            // ApplyNow once the debounce window has elapsed.
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 0.0));
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 1.0));
            Assert.AreEqual(AutoApplyDecision.ApplyNow, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 1.6));
        }

        [Test]
        public void RapidSuccessiveEdits_Coalesce_IntoOneEventualApply()
        {
            // Each edit resets "seconds since last edit" back to 0 (AgentHub's
            // job); as long as edits keep arriving faster than the debounce
            // window, the policy keeps saying DoNothing -- only once 1.5s of
            // silence finally elapses does it fire, exactly once.
            double[] secondsSinceEachEdit = { 0.0, 0.3, 0.2, 0.5, 0.1 };
            foreach (double seconds in secondsSinceEachEdit)
            {
                Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                    reconnectNeeded: true, isConnected: true, turnRunning: false,
                    pendingPermission: false, secondsSinceLastEdit: seconds),
                    "an edit within the debounce window must never fire early");
            }
            Assert.AreEqual(AutoApplyDecision.ApplyNow, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: Debounce + 0.01),
                "once the debounce window is finally silent, it fires exactly once");
        }

        // -- Busy defers, TurnCompleted resolves -------------------------------------

        [Test]
        public void TurnRunning_PastDebounce_Defers()
        {
            Assert.AreEqual(AutoApplyDecision.DeferToTurnEnd, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: true,
                pendingPermission: false, secondsSinceLastEdit: 5.0));
        }

        [Test]
        public void TurnRunning_WithinDebounce_StillDoesNothing_NotDeferredYet()
        {
            // Debounce takes priority: a busy client that just received a
            // fresh edit does not immediately flip to "deferred" -- it keeps
            // coalescing exactly like the idle case would.
            Assert.AreEqual(AutoApplyDecision.DoNothing, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: true,
                pendingPermission: false, secondsSinceLastEdit: 0.2));
        }

        [Test]
        public void PendingPermission_PastDebounce_Defers()
        {
            Assert.AreEqual(AutoApplyDecision.DeferToTurnEnd, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: true, secondsSinceLastEdit: 5.0));
        }

        [Test]
        public void BusyDefers_ThenFiresOnTurnCompleted_WhenPermissionAlsoResolved()
        {
            // Simulates AgentHub's flow: the debounce elapses while a turn is
            // running -> DeferToTurnEnd. TurnCompleted later re-evaluates with
            // turnRunning/pendingPermission both false (the turn ended and no
            // permission is outstanding) -> ApplyNow, without re-arming the
            // debounce clock (AgentHub never resets secondsSinceLastEdit for
            // the deferred path -- TurnCompleted alone drives it).
            AutoApplyDecision deferred = AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: true,
                pendingPermission: false, secondsSinceLastEdit: 5.0);
            Assert.AreEqual(AutoApplyDecision.DeferToTurnEnd, deferred);

            AutoApplyDecision afterTurnCompleted = AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 5.0);
            Assert.AreEqual(AutoApplyDecision.ApplyNow, afterTurnCompleted);
        }

        [Test]
        public void BusyDefers_StaysDeferred_WhenAnotherTurnStartsImmediately()
        {
            // A queued mid-turn send can start a new turn the instant the
            // previous one's result arrives (AgentClient.TurnActive stays
            // true) -- the policy must keep deferring in that case, not
            // apply mid-conversation.
            AutoApplyDecision stillBusy = AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: true,
                pendingPermission: false, secondsSinceLastEdit: 5.0);
            Assert.AreEqual(AutoApplyDecision.DeferToTurnEnd, stillBusy);
        }

        [Test]
        public void PendingPermissionAlone_BlocksApply_EvenWithoutTurnRunning()
        {
            // TurnActive is normally true throughout WaitingPermission too,
            // but the policy takes pendingPermission as an independent input
            // (per the design note's explicit "no permission pending"
            // condition) so a permission prompt blocks apply on its own.
            Assert.AreEqual(AutoApplyDecision.DeferToTurnEnd, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: true, secondsSinceLastEdit: 5.0));
        }

        [Test]
        public void IdleAndNoPendingPermission_PastDebounce_AppliesNow()
        {
            Assert.AreEqual(AutoApplyDecision.ApplyNow, AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded: true, isConnected: true, turnRunning: false,
                pendingPermission: false, secondsSinceLastEdit: 100.0));
        }
    }
}
