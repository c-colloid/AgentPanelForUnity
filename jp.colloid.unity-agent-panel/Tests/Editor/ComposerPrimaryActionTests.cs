using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards ComposerView.ResolvePrimaryAction -- the pure seam behind the
    /// single always-visible composer button's Send/Stop meaning (design
    /// note 2026-08-03-subagent-ux-and-midturn-input.md section 3).
    ///
    /// Measured against the real CLI (twice, including during a Task
    /// subagent fan-out): a user message written mid-turn is accepted and
    /// folded into the running turn -- one result, no error. The panel's
    /// send path never gated on turn state, so pressing Enter mid-turn
    /// already worked; the reported "let me type instead of stopping to
    /// resend" defect was that the only MOUSE-reachable control swapped to
    /// Stop while a turn ran and stayed Stop regardless of what was typed,
    /// so clicking it interrupted instead of sending. This is the
    /// regression pin for the fix: Stop only when a turn is running AND the
    /// field is empty; Send in every other case, including mid-turn with
    /// text. See ComposerView.PrimaryAction's doc comment for the full
    /// justification against both design constraints (Stop must stay
    /// reachable; Send must not be a trap) and the alternatives rejected
    /// (a second permanent Stop button, a modifier-key chord).
    /// </summary>
    public class ComposerPrimaryActionTests
    {
        private const bool TurnIdle = false;
        private const bool TurnActive = true;
        private const bool FieldEmpty = false;
        private const bool FieldHasText = true;

        [Test]
        public void TurnIdle_FieldEmpty_IsSend()
        {
            // Idle composer, nothing typed: unchanged from before this
            // change -- the button is Send and a click no-ops via TrySend's
            // own empty-text guard.
            Assert.AreEqual(ComposerView.PrimaryAction.Send,
                ComposerView.ResolvePrimaryAction(TurnIdle, FieldEmpty));
        }

        [Test]
        public void TurnIdle_FieldHasText_IsSend()
        {
            // Idle composer with a draft: unchanged from before this change.
            Assert.AreEqual(ComposerView.PrimaryAction.Send,
                ComposerView.ResolvePrimaryAction(TurnIdle, FieldHasText));
        }

        [Test]
        public void TurnActive_FieldEmpty_IsStop()
        {
            // A turn is running and there is nothing typed: the only
            // sensible click is "stop this" -- unchanged from before this
            // change, and the one case where mouse-Stop must stay exactly
            // as reachable as it was.
            Assert.AreEqual(ComposerView.PrimaryAction.Stop,
                ComposerView.ResolvePrimaryAction(TurnActive, FieldEmpty));
        }

        [Test]
        public void TurnActive_FieldHasText_IsSend()
        {
            // THE fix: a turn is running AND the user has typed something.
            // Before this change the button still read Stop here, so the
            // one mouse-reachable control could only interrupt -- the typed
            // message could never be sent by clicking it -- exactly the
            // trap the CLI measurement (see class doc) showed was
            // unnecessary, since a mid-turn send is honored as steering.
            Assert.AreEqual(ComposerView.PrimaryAction.Send,
                ComposerView.ResolvePrimaryAction(TurnActive, FieldHasText));
        }

        // -- UXO-8: the hint narrates what Enter/Esc currently do --------

        [Test]
        public void DescribeActionHint_NoTurn_IsEmpty_RegardlessOfText()
        {
            Assert.AreEqual(string.Empty, ComposerView.DescribeActionHint(false, false));
            Assert.AreEqual(string.Empty, ComposerView.DescribeActionHint(false, true));
        }

        [Test]
        public void DescribeActionHint_TurnActive_EmptyField_SaysEscToStop()
        {
            Assert.AreEqual(L10n.S.ComposerHintEscToStop,
                ComposerView.DescribeActionHint(true, false));
        }

        /// <summary>
        /// The state that used to change ONLY the button label in the
        /// user's peripheral vision: typing during a running turn turns
        /// Stop into Send, and the hint must say both live meanings.
        /// </summary>
        [Test]
        public void DescribeActionHint_TurnActive_WithText_SaysSendAndStop()
        {
            Assert.AreEqual(L10n.S.ComposerHintTurnSendAndStop,
                ComposerView.DescribeActionHint(true, true));
        }

    }
}
