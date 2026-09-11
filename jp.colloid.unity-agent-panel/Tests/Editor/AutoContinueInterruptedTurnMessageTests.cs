using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The interrupted-turn continuation must not tell the model to blindly
    /// re-run: design note 2026-09-08-menu-timeout-and-tool-steering
    /// section 1 measured an agent re-issuing a 9-minute scene build after
    /// EVERY reload because the message said "re-run anything whose result
    /// you did not receive".
    ///
    /// Design note 2026-09-10-auto-approve-all-tools-and-lean-auto-continue
    /// section 3: the text was then compressed to the essentials (from
    /// ~600 characters/~150 tokens down to ~40 tokens) because it is sent
    /// on EVERY reload and every word costs tokens for the rest of the
    /// session -- only the "check before re-running" clause survives from
    /// the original expanded wording, since that is the specific clause
    /// the 2026-09-08 incident needed.
    /// </summary>
    [TestFixture]
    public class AutoContinueInterruptedTurnMessageTests
    {
        [Test]
        public void Message_TellsTheModelToCheckBeforeReRunning()
        {
            string text = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(null);

            StringAssert.Contains("before re-running anything with side effects", text);
            StringAssert.Contains("uap_ping", text);
            StringAssert.Contains("status", text);
            StringAssert.DoesNotContain("re-run anything whose result you did not receive", text,
                "the literal re-run instruction is what caused the repeated rebuilds");
        }

        /// <summary>
        /// Token-budget pin (design note 2026-09-10 section 3, the actual
        /// defect this test guards): the base message, with neither a
        /// pending-permission tool name nor a compiler-error digest
        /// appended, must stay short. 400 characters is a generous upper
        /// bound next to the ~200-character actual text -- comfortably
        /// catching a regression back toward the old ~600-character
        /// wording without being a brittle exact-length pin.
        /// </summary>
        [Test]
        public void BaseMessage_StaysUnderFourHundredCharacters()
        {
            string text = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(null, null);

            Assert.Less(text.Length, 400,
                "the interrupted-turn continuation is sent on EVERY reload -- it must stay lean");
        }

        // -- Design note 2026-09-10 section 3: pending-permission sentence -----

        [Test]
        public void PendingPermissionTool_Present_AddsSentenceNamingTheTool()
        {
            string text = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(null, "Bash");

            StringAssert.Contains("The Bash call was still awaiting permission and did not run", text);
        }

        [Test]
        public void PendingPermissionTool_Present_IsPlacedBeforeTheCompilerErrorBlock()
        {
            string text = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(
                "some compiler error digest", "Bash");

            int toolSentenceIndex = text.IndexOf("The Bash call", System.StringComparison.Ordinal);
            int compilerNoteIndex = text.IndexOf("Compiler errors present:", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(toolSentenceIndex, 0, "the tool sentence must be present");
            Assert.GreaterOrEqual(compilerNoteIndex, 0, "the compiler-error note must be present");
            Assert.Less(toolSentenceIndex, compilerNoteIndex,
                "the pending-permission sentence must come before the compiler-error block");
        }

        [Test]
        public void PendingPermissionTool_NullOrEmpty_AddsNoSentence()
        {
            string withNull = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(null, null);
            string withEmpty = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(null, string.Empty);

            StringAssert.DoesNotContain("was still awaiting permission", withNull);
            StringAssert.DoesNotContain("was still awaiting permission", withEmpty);
        }

        [Test]
        public void OldOverload_IsEquivalentToNewOverloadWithNullPendingPermissionTool()
        {
            string viaOldOverload = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage("digest");
            string viaNewOverload = AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage("digest", null);

            Assert.AreEqual(viaNewOverload, viaOldOverload,
                "the old-signature overload must delegate to the new one with a null tool name");
        }
    }
}
