using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards ComposerView.ResolveEnterAction -- the pure seam behind
    /// "Enter sends / Shift+Enter inserts a newline" (design note
    /// 2026-08-02-composer-newline-and-profile-row.md section 1).
    ///
    /// Unity delivers one Enter press as TWO KeyDownEvents: a keycode
    /// event (keyCode == Return, no character) and a paired character
    /// event (character == '\n', keyCode == None). The handler used to
    /// re-derive the send/newline decision on BOTH from evt.shiftKey; when
    /// the character event arrives without the Shift flag that second
    /// evaluation flipped to "send", swallowed the event via
    /// PreventDefault, and -- since only the keycode branch actually sends
    /// -- dropped the newline silently. That is the reported bug, and the
    /// MODIFIERS-MISSING cases below are its regression pins.
    /// </summary>
    public class ComposerEnterActionTests
    {
        private const bool KeyCodeEvent = true;
        private const bool CharEvent = false;
        private const bool EnterSends = false;   // ctrlEnterToSend == false
        private const bool CtrlEnterSends = true;

        // -- Default mode: Enter sends, Shift+Enter newlines ---------------

        [Test]
        public void EnterSendsMode_PlainEnter_KeyCodeEvent_Sends()
        {
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
        }

        [Test]
        public void EnterSendsMode_ShiftEnter_KeyCodeEvent_Newlines()
        {
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(EnterSends, false, true,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
        }

        [Test]
        public void ShiftEnter_PairedCharEvent_WithoutShiftFlag_StillNewlines()
        {
            // THE regression: the character event reports shift == false,
            // but the keycode event already decided Newline. Re-deriving
            // here is what ate the line break.
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Newline));
        }

        [Test]
        public void ShiftEnter_PairedCharEvent_WithShiftFlag_StillNewlines()
        {
            // The same press on a platform that DOES repeat the modifier:
            // the stored decision and a fresh derivation agree, so the
            // outcome must not depend on which one the code trusts.
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(EnterSends, false, true,
                    CharEvent, ComposerView.EnterAction.Newline));
        }

        [Test]
        public void PlainEnter_PairedCharEvent_StaysSend_SoItIsSwallowed()
        {
            // Send must still swallow the paired character event, or a
            // stray newline lands in the field after the message goes out.
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Send));
        }

        // -- Ctrl+Enter mode (the IME escape hatch) ------------------------

        [Test]
        public void CtrlEnterMode_PlainEnter_Newlines()
        {
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(CtrlEnterSends, false, false,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
        }

        [Test]
        public void CtrlEnterMode_CtrlEnter_Sends()
        {
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(CtrlEnterSends, true, false,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
        }

        [Test]
        public void CtrlEnterMode_PairedCharEvent_WithoutCtrlFlag_StillSends()
        {
            // Mirror image of the reported bug: in Ctrl+Enter mode a
            // character event stripped of its Ctrl flag would re-derive to
            // Newline and leak a line break into a message being sent.
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(CtrlEnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Send));
        }

        [Test]
        public void CtrlEnterMode_ShiftEnter_Newlines()
        {
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(CtrlEnterSends, false, true,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
        }

        // -- No pending decision (IME paths) -------------------------------

        [Test]
        public void CharEvent_WithNoPendingDecision_FallsBackToModifiers()
        {
            // A character event with no preceding keycode event must still
            // behave sensibly rather than defaulting to one branch.
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(EnterSends, false, true,
                    CharEvent, ComposerView.EnterAction.Unknown));
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Unknown));
        }

        // -- ComputeNewlineInsertion ---------------------------------------
        // The panel inserts the line break itself: UI Toolkit's multiline
        // editing engine spends Shift+Enter on "end editing"
        // (MoveFocusToCompositeRoot -- measured live: the caret hops to the
        // outer TextField and no ChangeEvent fires), so the newline can
        // never be delegated to the default handler.

        [Test]
        public void NewlineInsertion_AtCaret_SplitsTheText()
        {
            int caret;
            Assert.AreEqual("AB\nCD",
                ComposerView.ComputeNewlineInsertion("ABCD", 2, 2, out caret));
            Assert.AreEqual(3, caret, "caret must land after the inserted break");
        }

        [Test]
        public void NewlineInsertion_AtEnd_Appends()
        {
            int caret;
            Assert.AreEqual("AB\n", ComposerView.ComputeNewlineInsertion("AB", 2, 2, out caret));
            Assert.AreEqual(3, caret);
        }

        [Test]
        public void NewlineInsertion_ReplacesTheSelection()
        {
            int caret;
            Assert.AreEqual("A\nD", ComposerView.ComputeNewlineInsertion("ABCD", 1, 3, out caret));
            Assert.AreEqual(2, caret);
        }

        [Test]
        public void NewlineInsertion_ReversedSelection_IsNormalized()
        {
            // cursorIndex > selectIndex when the user dragged backwards.
            int caret;
            Assert.AreEqual("A\nD", ComposerView.ComputeNewlineInsertion("ABCD", 3, 1, out caret));
            Assert.AreEqual(2, caret);
        }

        [Test]
        public void NewlineInsertion_OutOfRangeIndices_ClampInsteadOfThrowing()
        {
            // A stale caret is reachable right after the draft is restored
            // across a domain reload; degrade to an append, never throw.
            int caret;
            Assert.AreEqual("AB\n", ComposerView.ComputeNewlineInsertion("AB", 99, 99, out caret));
            Assert.AreEqual(3, caret);

            Assert.AreEqual("\nAB", ComposerView.ComputeNewlineInsertion("AB", -5, -5, out caret));
            Assert.AreEqual(1, caret);
        }

        [Test]
        public void NewlineInsertion_NullText_IsTreatedAsEmpty()
        {
            int caret;
            Assert.AreEqual("\n", ComposerView.ComputeNewlineInsertion(null, 0, 0, out caret));
            Assert.AreEqual(1, caret);
        }

        [Test]
        public void NewlineInsertion_PreservesMultibyteTextAroundTheCaret()
        {
            int caret;
            // Two Japanese kana, built from char codes: this repo's
            // sources are ASCII-only (GlyphAuditTests).
            string a = ((char)0x3042).ToString();
            string i = ((char)0x3044).ToString();
            Assert.AreEqual(a + "\n" + i,
                ComposerView.ComputeNewlineInsertion(a + i, 1, 1, out caret));
            Assert.AreEqual(2, caret);
        }

        [Test]
        public void KeyCodeEvent_IgnoresAnyStalePendingDecision()
        {
            // A new press must never inherit the previous press's outcome.
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(EnterSends, false, true,
                    KeyCodeEvent, ComposerView.EnterAction.Send));
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    KeyCodeEvent, ComposerView.EnterAction.Newline));
        }

        // -- UXO-2: IME composition guard ----------------------------------
        // The UX spec's "most important detail for Japanese users": an
        // Enter that CONFIRMS a composition must neither send nor insert.
        // The probe itself (Input.compositionString) has no test seam by
        // design -- these pin the pure decision for every event shape.

        [Test]
        public void ImeComposing_PlainEnter_KeyCodeEvent_IsIgnored_NotSent()
        {
            Assert.AreEqual(ComposerView.EnterAction.Ignore,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown,
                    imeComposing: true));
        }

        [Test]
        public void ImeComposing_ShiftEnter_KeyCodeEvent_IsIgnored_NotNewlined()
        {
            // Inserting a newline mid-composition would corrupt the
            // uncommitted text -- composing swallows every variant.
            Assert.AreEqual(ComposerView.EnterAction.Ignore,
                ComposerView.ResolveEnterAction(EnterSends, false, true,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown,
                    imeComposing: true));
        }

        [Test]
        public void ImeComposing_CtrlEnterMode_IsIgnoredToo()
        {
            Assert.AreEqual(ComposerView.EnterAction.Ignore,
                ComposerView.ResolveEnterAction(CtrlEnterSends, true, false,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown,
                    imeComposing: true));
        }

        [Test]
        public void ImeComposing_LoneCharEvent_IsIgnored()
        {
            // The second IME-shaped route into a send: a character-only
            // '\n' event with no preceding keycode event used to fall back
            // to modifiers and resolve to Send in default mode.
            Assert.AreEqual(ComposerView.EnterAction.Ignore,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Unknown,
                    imeComposing: true));
        }

        [Test]
        public void PendingIgnore_CharEvent_ObeysThePair_EvenAfterCompositionEnded()
        {
            // The confirm commits the composition between the two paired
            // events, so the char event can see composing == false -- the
            // stored keycode decision must still win, or the pair splits.
            Assert.AreEqual(ComposerView.EnterAction.Ignore,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Ignore,
                    imeComposing: false));
        }

        [Test]
        public void PendingSend_CharEvent_IsNotReclassified_ByACompositionStartingInBetween()
        {
            // The inverse split: a keycode event decided Send while NOT
            // composing; its paired char event must not flip to Ignore
            // because a composition began in the gap.
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    CharEvent, ComposerView.EnterAction.Send,
                    imeComposing: true));
        }

        [Test]
        public void NotComposing_DefaultParameter_KeepsEveryPreExistingDecision()
        {
            // The fail-safe contract: with the signal absent (batchmode,
            // platforms where compositionString never fills) behavior is
            // byte-identical to before UXO-2.
            Assert.AreEqual(ComposerView.EnterAction.Send,
                ComposerView.ResolveEnterAction(EnterSends, false, false,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
            Assert.AreEqual(ComposerView.EnterAction.Newline,
                ComposerView.ResolveEnterAction(EnterSends, false, true,
                    KeyCodeEvent, ComposerView.EnterAction.Unknown));
        }

        [Test]
        public void IsImeCompositionActive_InBatchMode_IsFalse_NeverThrows()
        {
            // CI runs headless: the probe must be inert there (this is the
            // fail-safe half of the guard). On an interactive editor with a
            // live Japanese IME this returns true mid-composition -- which
            // only a real-IME manual pass can verify (risk 11).
            Assert.DoesNotThrow(delegate
            {
                ComposerView.IsImeCompositionActive();
            });
        }
        // -- UICODE-7: the keycode->char bridge resets on any other key ---

        /// <summary>
        /// _pendingEnterAction hands one keycode Enter's decision to its
        /// immediately-following character event. When that pair never
        /// completes (some IME/platform paths suppress the char event after
        /// PreventDefault), the stale action used to sit armed until the
        /// next ORPHAN Enter character -- an IME newline commit -- and
        /// replay the old Send decision on it. Any unrelated key now breaks
        /// the bridge. Driven through OnKeyDown itself via the test seam
        /// (panel-less SendEvent is a no-op in EditMode).
        /// </summary>
        [Test]
        public void KeycodeEnter_ThenUnrelatedKey_ResetsThePendingBridge()
        {
            bool savedCtrlEnter = PanelStateStore.instance.Settings.ctrlEnterToSend;
            PanelStateStore.instance.Settings.ctrlEnterToSend = false; // Enter sends
            try
            {
                var composer = new ComposerView();

                using (KeyDownEvent enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
                {
                    composer.SimulateKeyDownForTests(enter);
                }
                Assert.AreEqual(ComposerView.EnterAction.Send, composer.PendingEnterActionForTests,
                    "the keycode Enter arms the bridge for its paired char event");

                using (KeyDownEvent letter = KeyDownEvent.GetPooled('a', KeyCode.A, EventModifiers.None))
                {
                    composer.SimulateKeyDownForTests(letter);
                }
                Assert.AreEqual(ComposerView.EnterAction.Unknown, composer.PendingEnterActionForTests,
                    "any unrelated key must break the bridge -- a later orphan Enter char"
                    + " must resolve from its own modifiers, not replay the stale Send");
            }
            finally
            {
                PanelStateStore.instance.Settings.ctrlEnterToSend = savedCtrlEnter;
            }
        }


        // -- UXA-4: the composer doubles as the deny-reason entry --------

        [Test]
        public void PendingPermissionHint_SwapsThePlaceholder_AndBack()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);

            composer.SetPendingPermissionHint(true);
            Assert.AreEqual(L10n.S.ComposerPlaceholderPermissionPending,
                composer.PlaceholderTextForTests);

            composer.SetPendingPermissionHint(false);
            Assert.AreNotEqual(L10n.S.ComposerPlaceholderPermissionPending,
                composer.PlaceholderTextForTests,
                "resolving the request must restore the normal placeholder");
        }

        [Test]
        public void ConsumeTextForDeny_ReturnsTrimmedText_AndClearsTheField()
        {
            var composer = new ComposerView();
            composer.InsertText("  use the staging folder instead  ");

            Assert.AreEqual("use the staging folder instead", composer.ConsumeTextForDeny());
            Assert.AreEqual(string.Empty, composer.ConsumeTextForDeny(),
                "a second read must find the field already consumed");
        }

    }
}
