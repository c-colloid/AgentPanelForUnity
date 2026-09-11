using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Exhaustive pure-logic coverage for AutoContinueAfterCompilePolicy
    /// (Phase 5c L3 item 3, design section 3 item 3 / 8.3). AgentHub's
    /// wiring around a real domain reload is integration-only and cannot
    /// be exercised here (see AgentHubAutoContinueAfterCompileTests for
    /// the seams that CAN be exercised via AgentHub.WireClientForTests
    /// without a live editor); this file is only about the decision
    /// function and the text it composes.
    /// </summary>
    public class AutoContinueAfterCompilePolicyTests
    {
        // -- ShouldAutoContinue: every guardrail, both directions, and every
        // combination of the three inputs (exhaustive: 2^3 = 8 cases). ------

        [Test]
        public void Enabled_Attributable_NotAlreadyContinued_ReturnsTrue()
        {
            Assert.IsTrue(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: true, attributableToAgentScripts: true, alreadyContinuedThisTurn: false));
        }

        [Test]
        public void Disabled_Attributable_NotAlreadyContinued_ReturnsFalse()
        {
            // Guardrail: the feature must stay off for a user who never
            // opted in, no matter how clearly attributable the reload is.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: false, attributableToAgentScripts: true, alreadyContinuedThisTurn: false));
        }

        [Test]
        public void Enabled_NotAttributable_NotAlreadyContinued_ReturnsFalse()
        {
            // Guardrail 2: an unattributable reload (user's own IDE edit,
            // a package install) must never resume the agent, even with
            // the feature on.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: true, attributableToAgentScripts: false, alreadyContinuedThisTurn: false));
        }

        [Test]
        public void Enabled_Attributable_AlreadyContinued_ReturnsFalse()
        {
            // Guardrail 1: a continuation must never itself trigger another
            // continuation -- this is what stops an unbounded compile-and-
            // reprompt loop.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: true, attributableToAgentScripts: true, alreadyContinuedThisTurn: true));
        }

        [Test]
        public void Disabled_NotAttributable_NotAlreadyContinued_ReturnsFalse()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: false, attributableToAgentScripts: false, alreadyContinuedThisTurn: false));
        }

        [Test]
        public void Disabled_Attributable_AlreadyContinued_ReturnsFalse()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: false, attributableToAgentScripts: true, alreadyContinuedThisTurn: true));
        }

        [Test]
        public void Disabled_NotAttributable_AlreadyContinued_ReturnsFalse()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: false, attributableToAgentScripts: false, alreadyContinuedThisTurn: true));
        }

        [Test]
        public void Enabled_NotAttributable_AlreadyContinued_ReturnsFalse()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAutoContinue(
                enabled: true, attributableToAgentScripts: false, alreadyContinuedThisTurn: true));
        }

        // -- DescribeOutcome: the defect 6 fix (2026-08-04) -- separating the ------
        // TWO independent reasons a note can say "no continuation is coming"
        // (setting off vs. guardrail 1) that used to collapse onto the same
        // "off in Settings" wording. String-independent: this tests the
        // CLASSIFICATION AgentHub.HandleAutoContinueArming now switches on,
        // not which L10n string ships for each case (see this stream's
        // final report for the exact new string still needed there).

        [Test]
        public void DescribeOutcome_EnabledAttributableNotAlreadyContinued_ReturnsWillContinue()
        {
            Assert.AreEqual(AutoContinueAfterCompilePolicy.AutoContinueSkipReason.WillContinue,
                AutoContinueAfterCompilePolicy.DescribeOutcome(
                    enabled: true, attributableToAgentScripts: true, alreadyContinuedThisTurn: false));
        }

        [Test]
        public void DescribeOutcome_Disabled_ReturnsDisabledInSettings()
        {
            Assert.AreEqual(AutoContinueAfterCompilePolicy.AutoContinueSkipReason.DisabledInSettings,
                AutoContinueAfterCompilePolicy.DescribeOutcome(
                    enabled: false, attributableToAgentScripts: true, alreadyContinuedThisTurn: false));
        }

        [Test]
        public void DescribeOutcome_EnabledAttributableAlreadyContinued_ReturnsAlreadyContinuedThisCycle_NotDisabled()
        {
            // The exact defect 6 regression: with the setting ON, guardrail
            // 1 (this turn was itself a continuation) must be reported as
            // its OWN distinct reason, never as "disabled" -- before this
            // fix, AgentHub's single ternary could only ever say "off in
            // Settings" here, which is flatly false when enabled is true.
            Assert.AreEqual(AutoContinueAfterCompilePolicy.AutoContinueSkipReason.AlreadyContinuedThisCycle,
                AutoContinueAfterCompilePolicy.DescribeOutcome(
                    enabled: true, attributableToAgentScripts: true, alreadyContinuedThisTurn: true));
        }

        [Test]
        public void DescribeOutcome_DisabledAndAlreadyContinued_ReturnsDisabledInSettings()
        {
            // When BOTH negative causes apply, "disabled" wins: it is the
            // simpler, more actionable thing to tell the user (turn the
            // setting on), and DescribeOutcome only ever reaches the
            // AlreadyContinuedThisCycle branch once enabled is confirmed
            // true.
            Assert.AreEqual(AutoContinueAfterCompilePolicy.AutoContinueSkipReason.DisabledInSettings,
                AutoContinueAfterCompilePolicy.DescribeOutcome(
                    enabled: false, attributableToAgentScripts: true, alreadyContinuedThisTurn: true));
        }

        // -- TicketIsFresh: the attribution ticket's wall-clock expiry (defect 1, --
        // 2026-08-04). A plain "how many reloads since arm" counter cannot
        // catch the concrete failure this closes (a commit's own compile
        // FAILS, so no domain reload follows it at all -- the eventual
        // unrelated reload really is "the very next reload since arm" in
        // both the legitimate and the buggy case), which is why this is
        // wall-clock-based instead.

        [Test]
        public void TicketIsFresh_ZeroElapsed_ReturnsTrue()
        {
            long now = DateTime.UtcNow.Ticks;
            Assert.IsTrue(AutoContinueAfterCompilePolicy.TicketIsFresh(now, now));
        }

        [Test]
        public void TicketIsFresh_JustUnderMaxAge_ReturnsTrue()
        {
            long armedAt = DateTime.UtcNow.Ticks;
            long now = armedAt + TimeSpan.FromSeconds(AutoContinueAfterCompilePolicy.TicketMaxAgeSeconds - 1.0).Ticks;
            Assert.IsTrue(AutoContinueAfterCompilePolicy.TicketIsFresh(armedAt, now));
        }

        [Test]
        public void TicketIsFresh_ExactlyMaxAge_ReturnsTrue()
        {
            long armedAt = DateTime.UtcNow.Ticks;
            long now = armedAt + TimeSpan.FromSeconds(AutoContinueAfterCompilePolicy.TicketMaxAgeSeconds).Ticks;
            Assert.IsTrue(AutoContinueAfterCompilePolicy.TicketIsFresh(armedAt, now));
        }

        [Test]
        public void TicketIsFresh_JustOverMaxAge_ReturnsFalse()
        {
            long armedAt = DateTime.UtcNow.Ticks;
            long now = armedAt + TimeSpan.FromSeconds(AutoContinueAfterCompilePolicy.TicketMaxAgeSeconds + 1.0).Ticks;
            Assert.IsFalse(AutoContinueAfterCompilePolicy.TicketIsFresh(armedAt, now));
        }

        [Test]
        public void TicketIsFresh_HoursLater_ReturnsFalse_FailClosed()
        {
            // The concrete defect 1 scenario: a compile that fails causes no
            // domain reload at all, so the ticket sits armed with nothing to
            // consume it; hours later an unrelated compile (the user's own
            // fix, applied by hand in their own IDE) succeeds and reloads
            // instead.
            long armedAt = DateTime.UtcNow.Ticks;
            long now = armedAt + TimeSpan.FromHours(3).Ticks;
            Assert.IsFalse(AutoContinueAfterCompilePolicy.TicketIsFresh(armedAt, now));
        }

        [Test]
        public void TicketIsFresh_NegativeElapsed_ReturnsFalse_FailClosed()
        {
            // Clock skew guard: a reload processed "before" its own arm
            // timestamp is nonsensical, and guardrail 2 says uncertain must
            // collapse to false, never to "extra fresh".
            long armedAt = DateTime.UtcNow.Ticks;
            long now = armedAt - TimeSpan.FromSeconds(1).Ticks;
            Assert.IsFalse(AutoContinueAfterCompilePolicy.TicketIsFresh(armedAt, now));
        }

        // -- ScriptCommitMovedFiles: fail-closed attribution parsing --------------

        [Test]
        public void ScriptCommitMovedFiles_RealSuccessMessage_ReturnsTrue()
        {
            // The exact prefix UapScriptsCommitTool.FinishCommit emits on a
            // real, successful move.
            Assert.IsTrue(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                "Committed 2 file(s) into Assets/ (validated, 0 compile errors): Assets/Foo.cs, Assets/Bar.cs."
                + " They will be imported at the end of this turn (one batched refresh)."));
        }

        [Test]
        public void MovedFilesPrefix_IsPublicSharedConstant_MatchesScriptCommitMovedFiles()
        {
            // Durability fix (defect 7, 2026-08-04): UapScriptsCommitTool.
            // FinishCommit now builds its real message by concatenating
            // THIS constant instead of an independent "Committed " literal
            // of its own, so the two can never silently drift apart the way
            // this test used to (a hand-copied literal one line above,
            // matching today only because nobody had reworded either copy
            // yet). This test exercises the constant directly, the same
            // shape a real commit message has.
            Assert.AreEqual("Committed ", AutoContinueAfterCompilePolicy.MovedFilesPrefix);
            Assert.IsTrue(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                AutoContinueAfterCompilePolicy.MovedFilesPrefix
                    + "1 file(s) into Assets/ (validated, 0 compile errors): Assets/Foo.cs."
                    + " They will be imported at the end of this turn (one batched refresh)."));
        }

        [Test]
        public void ScriptCommitMovedFiles_NothingStagedMessage_ReturnsFalse()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                "Nothing staged under UapStaging/; nothing to commit."));
        }

        [Test]
        public void ScriptCommitMovedFiles_CompilationFailedMessage_ReturnsFalse()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                "Compilation failed; nothing was moved into Assets/. Fix these errors and commit again:\n"
                + "Assets/Foo.cs(3,5): error CS1002: ; expected"));
        }

        [Test]
        public void ScriptCommitMovedFiles_Null_ReturnsFalse_FailClosed()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(null));
        }

        [Test]
        public void ScriptCommitMovedFiles_Empty_ReturnsFalse_FailClosed()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(string.Empty));
        }

        [Test]
        public void ScriptCommitMovedFiles_WrongCase_ReturnsFalse_FailClosed()
        {
            // Ordinal, case-sensitive: matching a fixed codebase-generated
            // message, not free-form model output.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                "committed 1 file(s) into Assets/"));
        }

        [Test]
        public void ScriptCommitMovedFiles_LeadingWhitespace_ReturnsFalse_FailClosed()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                " Committed 1 file(s) into Assets/"));
        }

        [Test]
        public void ScriptCommitMovedFiles_SimilarButUnrelatedText_ReturnsFalse_FailClosed()
        {
            // "Contains" would wrongly match this; only a PREFIX match may.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(
                "See the log for what was Committed 1 file(s) previously."));
        }

        [Test]
        public void ScriptCommitMovedFiles_GarbledOrTruncatedText_ReturnsFalse_FailClosed()
        {
            // Attribution is unknown/ambiguous here -- must collapse to
            // false, never to "maybe" or true.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles("Commit"));
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles("????"));
        }

        // -- ExtractTextContent: both MCP content shapes, and everything else ----

        [Test]
        public void ExtractTextContent_Null_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, AutoContinueAfterCompilePolicy.ExtractTextContent(null));
        }

        [Test]
        public void ExtractTextContent_PlainStringNode_ReturnsItsValue()
        {
            JsonNode content = JsonNode.Of("Committed 1 file(s) into Assets/");
            Assert.AreEqual("Committed 1 file(s) into Assets/",
                AutoContinueAfterCompilePolicy.ExtractTextContent(content));
        }

        [Test]
        public void ExtractTextContent_SingleTextBlockArray_ReturnsItsText()
        {
            // The raw MCP shape UapToolResults.Text actually constructs.
            JsonNode content = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("type", "text").Set("text", "Committed 1 file(s) into Assets/"));
            Assert.AreEqual("Committed 1 file(s) into Assets/",
                AutoContinueAfterCompilePolicy.ExtractTextContent(content));
        }

        [Test]
        public void ExtractTextContent_MultipleTextBlocks_ConcatenatesThem()
        {
            JsonNode content = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("type", "text").Set("text", "Committed "))
                .Add(JsonNode.NewObject().Set("type", "text").Set("text", "1 file(s) into Assets/"));
            Assert.AreEqual("Committed 1 file(s) into Assets/",
                AutoContinueAfterCompilePolicy.ExtractTextContent(content));
        }

        [Test]
        public void ExtractTextContent_NonTextBlockInArray_IsIgnored()
        {
            JsonNode content = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("type", "image").Set("text", "should not appear"))
                .Add(JsonNode.NewObject().Set("type", "text").Set("text", "Committed 1 file(s)"));
            Assert.AreEqual("Committed 1 file(s)",
                AutoContinueAfterCompilePolicy.ExtractTextContent(content));
        }

        [Test]
        public void ExtractTextContent_EmptyArray_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AutoContinueAfterCompilePolicy.ExtractTextContent(JsonNode.NewArray()));
        }

        [Test]
        public void ExtractTextContent_PlainObject_ReturnsEmpty()
        {
            JsonNode content = JsonNode.NewObject().Set("unexpected", "shape");
            Assert.AreEqual(string.Empty, AutoContinueAfterCompilePolicy.ExtractTextContent(content));
        }

        [Test]
        public void ExtractTextContent_NumberNode_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AutoContinueAfterCompilePolicy.ExtractTextContent(JsonNode.Of(42)));
        }

        // -- Message composition: guardrail 4 --------------------------------
        //
        // The two TRANSCRIPT notes this fixture used to cover moved into the
        // L10n catalog and are emitted by AgentHub, because they are
        // user-facing and every other system note in this panel is
        // localized. Their wording is now pinned by the L10n catalog's own
        // parity checks rather than here. What remains below is the
        // MODEL-facing continuation text, which stays English on purpose --
        // it is wire content the model reads, not UI the user reads.

        [Test]
        public void ComposeContinuationMessage_Succeeded_SaysSucceeded_AndOmitsDigest()
        {
            string message = AutoContinueAfterCompilePolicy.ComposeContinuationMessage(
                compileSucceeded: true, errorDigest: "should not appear");
            StringAssert.Contains("SUCCEEDED", message);
            StringAssert.DoesNotContain("should not appear", message);
        }

        [Test]
        public void ComposeContinuationMessage_Failed_SaysFailed_AndIncludesDigest()
        {
            string message = AutoContinueAfterCompilePolicy.ComposeContinuationMessage(
                compileSucceeded: false, errorDigest: "Errors (1): [1] CS1002: ; expected");
            StringAssert.Contains("FAILED", message);
            StringAssert.Contains("CS1002", message);
        }

        [Test]
        public void ComposeContinuationMessage_FailedWithNullDigest_DoesNotThrow_AndStillSaysFailed()
        {
            string message = AutoContinueAfterCompilePolicy.ComposeContinuationMessage(
                compileSucceeded: false, errorDigest: null);
            StringAssert.Contains("FAILED", message);
        }

        [Test]
        public void ComposeContinuationMessage_SucceededAndFailed_AreDistinct()
        {
            string succeeded = AutoContinueAfterCompilePolicy.ComposeContinuationMessage(true, null);
            string failed = AutoContinueAfterCompilePolicy.ComposeContinuationMessage(false, null);
            Assert.AreNotEqual(succeeded, failed);
        }

        // -- HUB-5: the drain-window timeout, frozen while compiling ------

        /// <summary>
        /// The send is BLOCKED while Unity compiles
        /// (TrySendPendingAutoContinueMessage returns false outright), so
        /// compile time must not consume the drain budget -- a long compile
        /// alone used to exhaust the 60s window and retract a continuation
        /// that had never had a chance to go out.
        /// </summary>
        [Test]
        public void ShouldAbandonForTimeout_WhileCompiling_IsNeverATimeout_EvenWayPastTheWindow()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout(
                nowSeconds: 10000.0, waitStartedAtSeconds: 0.0, isCompiling: true, timeoutSeconds: 60.0));
        }

        [Test]
        public void ShouldAbandonForTimeout_NotCompiling_TimesOutOnlyPastTheWindow()
        {
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout(
                nowSeconds: 59.9, waitStartedAtSeconds: 0.0, isCompiling: false, timeoutSeconds: 60.0));
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout(
                nowSeconds: 60.0, waitStartedAtSeconds: 0.0, isCompiling: false, timeoutSeconds: 60.0),
                "exactly at the bound is not yet past it");
            Assert.IsTrue(AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout(
                nowSeconds: 60.1, waitStartedAtSeconds: 0.0, isCompiling: false, timeoutSeconds: 60.0));
        }

        [Test]
        public void ShouldAbandonForTimeout_NegativeElapsed_IsNeverATimeout()
        {
            // Clock adjusted backwards between arm and check -- same
            // guardrail-2 stance TicketIsFresh takes: never act on a
            // measurement that cannot be trusted.
            Assert.IsFalse(AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout(
                nowSeconds: 5.0, waitStartedAtSeconds: 500.0, isCompiling: false, timeoutSeconds: 60.0));
        }
    }
}
