using System;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure decision + text-composition behind Phase 5c L3 item 3
    /// (auto-continue after compile) -- docs/design-notes/2026-08-01-
    /// phase5-unity-ops-design.md section 3 item 3, together with the
    /// section 8.3 risk bullet requiring the auto-continue feature and the
    /// compile batch window to agree on the turn-end sequence, ARE the
    /// specification this class implements; see also PanelSettings.
    /// uapOpsAutoContinueAfterCompile's own doc comment.
    ///
    /// The problem: when the agent's own staged script commit
    /// (uap_scripts_commit, see UapScriptsCommitTool/ScriptGate) causes a
    /// Unity compile and the resulting domain reload kills the CLI
    /// process, AgentHub.RestoreAfterReload's PRE-EXISTING behaviour
    /// (unconditional, unrelated to this feature) reconnects with
    /// --resume -- but sends nothing. The conversation just sits idle
    /// until the human notices and re-prompts. With this feature ON,
    /// AgentHub sends exactly ONE follow-up user turn carrying the real
    /// compile result, so the agent -- not the human -- is the one who
    /// has to notice and react.
    ///
    /// This is the only feature in the panel that makes the agent act
    /// without a human sending anything, which is why every branch below
    /// exists: see each parameter's own doc comment for exactly what goes
    /// wrong without that specific guard. No Unity API dependency at all
    /// (JsonNode, used by <see cref="ExtractTextContent"/>, is this
    /// project's own hand-rolled Core.Json type and has no UnityEngine/
    /// UnityEditor reference) -- every branch here is exercised directly
    /// by EditMode tests with no live editor and no real domain reload,
    /// exactly like AutoApprovePolicy/AutoApplySettingsPolicy. AgentHub is
    /// deliberately a thin caller around this class: see AgentHub.
    /// HandleAutoContinueArming/TryAutoContinueAfterCompile.
    /// </summary>
    public static class AutoContinueAfterCompilePolicy
    {
        /// <summary>
        /// IUapTool.Name of Colloid.AgentPanel.Ops.UapScriptsCommitTool.
        /// Duplicated here as a literal, the same way ScriptGate.cs
        /// hardcodes the CLI's own "Write"/"Edit"/"MultiEdit" tool names
        /// rather than importing a shared constant: this stream
        /// deliberately does not touch Editor/Ops/*.cs (a different
        /// stream's files), and "uap_scripts_commit" is a stable,
        /// already-shipped wire identifier, not a value expected to drift
        /// without a deliberate, reviewed rename on both sides.
        /// </summary>
        public const string ScriptsCommitToolName = "uap_scripts_commit";

        /// <summary>
        /// The exact prefix UapScriptsCommitTool.FinishCommit's result text
        /// starts with ONLY when it actually moved staged files into
        /// Assets/ ("Committed N file(s) into Assets/ (validated, 0
        /// compile errors): ..."). Its other two possible outcomes --
        /// "Nothing staged under UapStaging/; nothing to commit." and
        /// "Compilation failed; nothing was moved into Assets/. Fix these
        /// errors and commit again:\n..." -- both leave Assets/ completely
        /// untouched, so neither one can be the cause of a LATER domain
        /// reload. Treating either of those as attributable would be
        /// exactly the false-positive attribution design guardrail 2
        /// forbids ("if you cannot attribute reliably, DO NOT resume").
        ///
        /// PUBLIC (2026-08-04 durability fix): UapScriptsCommitTool.
        /// FinishCommit builds its real "Committed ..." message by
        /// concatenating THIS constant rather than a second, independent
        /// "Committed " literal of its own. Before this fix the two
        /// strings simply happened to match -- Tests/Editor/
        /// AutoContinueAfterCompilePolicyTests.cs's own ScriptCommitMovedFiles_
        /// RealSuccessMessage_ReturnsTrue test hand-copied the literal
        /// instead of deriving it, so nothing anywhere would have caught a
        /// wording change to ONE of the two copies silently breaking
        /// attribution for every real commit while every test in both
        /// fixtures stayed green (they would keep testing whichever
        /// literal each file happened to hand-copy, never the real
        /// production string). Sharing one constant makes that drift
        /// impossible: a rename here is a compile error in
        /// UapScriptsCommitTool.cs instead of a silent runtime mismatch.
        /// </summary>
        public const string MovedFilesPrefix = "Committed ";

        /// <summary>
        /// True when a uap_scripts_commit tool_result's text reports that
        /// it actually moved staged files into Assets/ -- the ONLY
        /// evidence this policy trusts to attribute an upcoming reload to
        /// the agent's own work (design section 3 item 3: "only when it can be determined
        /// that the agent's own .cs/.asmdef write caused it"). Deliberately an
        /// Ordinal prefix check, not "contains" or a case-insensitive
        /// match: this is matching a FIXED message this same codebase
        /// generates verbatim (UapScriptsCommitTool.FinishCommit), not
        /// free-form model output that might vary in casing/wording.
        /// Null/empty/anything else (including a truncated, garbled, or
        /// merely similar-looking string) returns false -- FAIL CLOSED,
        /// per guardrail 2's explicit instruction. There is no "maybe":
        /// uncertain input collapses to "not attributable", never to
        /// "attributable".
        /// </summary>
        public static bool ScriptCommitMovedFiles(string resultText)
        {
            return !string.IsNullOrEmpty(resultText)
                && resultText.StartsWith(MovedFilesPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Extracts the plain text of an MCP tool_result "content" node.
        /// ContentBlock.ResultContent reaches AgentHub in EITHER of two
        /// shapes depending on how the CLI relays it back: a plain string
        /// (the common case -- see AgentHub.OnToolResultReceived's own
        /// "block.ResultContent.IsString" check, used for the transcript
        /// summary) or the raw MCP array-of-blocks shape
        /// Colloid.AgentPanel.Ops.UapToolResults.Text actually constructs
        /// server-side (<c>[{"type":"text","text":"..."}]</c>), for a CLI
        /// version/path that does not collapse a single-text-block array
        /// down to a bare string. Concatenates every "text" block found in
        /// array form (there is only ever one in practice for this tool,
        /// but nothing here assumes that). Null, or neither shape, returns
        /// string.Empty -- which <see cref="ScriptCommitMovedFiles"/>
        /// above already treats as "not attributable": fail closed, never
        /// an exception.
        /// </summary>
        public static string ExtractTextContent(JsonNode content)
        {
            if (content == null)
            {
                return string.Empty;
            }
            if (content.IsString)
            {
                return content.AsString(string.Empty);
            }
            if (content.IsArray)
            {
                var sb = new StringBuilder();
                foreach (JsonNode item in content.Items)
                {
                    if (item.IsObject
                        && string.Equals(item["type"].AsString(string.Empty), "text", StringComparison.Ordinal))
                    {
                        sb.Append(item["text"].AsString(string.Empty));
                    }
                }
                return sb.ToString();
            }
            return string.Empty;
        }

        /// <summary>
        /// The single decision point for every guardrail PanelSettings.
        /// uapOpsAutoContinueAfterCompile's doc comment lists. AgentHub
        /// calls this exactly once per domain reload
        /// (TryAutoContinueAfterCompile), with values it persisted to
        /// SessionStateBridge BEFORE the reload (plain statics do not
        /// survive it) -- this function itself is nothing more than a
        /// pure function of its three inputs.
        /// </summary>
        /// <param name="enabled">
        /// PanelSettings.uapOpsAutoContinueAfterCompile, read LIVE at the
        /// moment the reload completes (never baked into a spawn argument
        /// -- see that field's own doc comment). False means the feature
        /// is off: WITHOUT this check the panel would resume the agent
        /// even for a user who never opted in, defeating the entire
        /// point of this being an opt-in (default OFF) feature.
        /// </param>
        /// <param name="attributableToAgentScripts">
        /// True only when THIS reload followed a turn whose OWN
        /// uap_scripts_commit call reported moving staged files into
        /// Assets/ (see <see cref="ScriptCommitMovedFiles"/>). WITHOUT
        /// this check, ANY domain reload -- the user hand-editing a script
        /// in their own IDE, a package install/resolve, a manual
        /// "Reimport All" -- would resume the agent's conversation as if
        /// it had asked for exactly this. That is not a cosmetic bug: it
        /// hands the agent an unsolicited turn about a compile it had
        /// nothing to do with, and could make it start "fixing" code the
        /// human was still mid-edit on. An unattributable/unknown reload
        /// must map to FALSE here, never to true -- there is no value of
        /// this parameter that means "maybe"; the caller (AgentHub)
        /// collapses uncertainty to false before ever calling this method.
        ///
        /// 2026-08-04 defect 1 fix: this now ALSO covers ticket staleness.
        /// AgentHub folds <see cref="TicketIsFresh"/> into the value it
        /// passes here (a stale ticket becomes false) rather than
        /// ShouldAutoContinue growing a fourth parameter of its own --
        /// "the caller collapses uncertainty to false before ever calling
        /// this method" already covered exactly this case in spirit,
        /// before the staleness check existed to need it.
        /// </param>
        /// <param name="alreadyContinuedThisTurn">
        /// True when the turn that just completed -- the one whose
        /// possible attribution is being evaluated above -- was ITSELF
        /// sent as a previous auto-continuation (AgentHub persists this
        /// alongside the attribution ticket; both are written together by
        /// OnTurnCompleted/HandleAutoContinueArming and consumed together
        /// here). WITHOUT this check, a continuation that itself ends up
        /// committing more scripts (the agent tries a fix, stages a new
        /// file, calls uap_scripts_commit again) would arm a SECOND
        /// reload cycle, which would send a SECOND continuation, which
        /// could do the same again -- an unbounded compile-and-reprompt
        /// loop, explicitly called out in the design as "the worst
        /// possible failure here". This parameter is what makes the
        /// feature strictly one-shot per genuine (human- or externally-
        /// triggered) turn: it can only ever be true for the turn
        /// immediately following a continuation, never for two turns in a
        /// row, because AgentHub resets the live flag it is sourced from
        /// the moment ANY turn completes, continuation or not.
        /// </param>
        /// <returns>
        /// True only when every guardrail passes: the feature is on, the
        /// reload is attributable, and sending would not be continuing a
        /// continuation.
        /// </returns>
        public static bool ShouldAutoContinue(bool enabled, bool attributableToAgentScripts,
            bool alreadyContinuedThisTurn)
        {
            if (!enabled)
            {
                return false;
            }
            if (!attributableToAgentScripts)
            {
                return false;
            }
            if (alreadyContinuedThisTurn)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Every distinct reason HandleAutoContinueArming's pending-reload
        /// note can give the user for whether a continuation is coming --
        /// see <see cref="DescribeOutcome"/>. Defect 6 fix (2026-08-04):
        /// before this existed, AgentHub decided the note's wording with a
        /// single `willAutoContinue ? ... : ...` ternary, which folded TWO
        /// independent negative causes (the setting is off; guardrail 1
        /// blocked a continuation continuing itself) onto the SAME "off in
        /// Settings" text -- so a user who had the feature ON, but whose
        /// turn was itself a continuation, was flatly told the feature was
        /// off. Confirmed live by this fixture's own
        /// ContinuationTurnItselfCommitsScripts_DoesNotArmASecondContinuation
        /// test, which sets the setting ON and still hits the "off"
        /// branch.
        /// </summary>
        public enum AutoContinueSkipReason
        {
            /// <summary>Every guardrail passed; the continuation will be sent.</summary>
            WillContinue,
            /// <summary>PanelSettings.uapOpsAutoContinueAfterCompile is off.</summary>
            DisabledInSettings,
            /// <summary>
            /// The setting IS on and the reload IS attributable, but
            /// guardrail 1 blocked it: the turn that just completed was
            /// itself an auto-continuation, so it may not arm a second one.
            /// </summary>
            AlreadyContinuedThisCycle
        }

        /// <summary>
        /// Classifies WHY a just-armed, attributable ticket will or will
        /// not lead to an auto-continuation, as three mutually exclusive,
        /// independently testable outcomes -- the fix for defect 6
        /// (2026-08-04, see <see cref="AutoContinueSkipReason"/>'s own doc
        /// comment for the bug this replaces). Callers MUST only call this
        /// when <paramref name="attributableToAgentScripts"/> is true: the
        /// only case HandleAutoContinueArming ever shows this note for at
        /// all is an attributable ticket (an unattributable turn shows no
        /// pending-reload note whatsoever, so there is nothing to
        /// describe). This method does not re-derive that guardrail
        /// itself -- passing false here would fall through to the SAME
        /// AlreadyContinuedThisCycle result an actually-already-continued
        /// call gets, which would be a confusing, wrong label for "not
        /// attributable" to wear. Rather than add a fourth enum value
        /// nobody would ever legitimately see (HandleAutoContinueArming's
        /// own `if (!attributable) return;` guard, immediately above its
        /// call to this method, already makes that case unreachable in
        /// practice), the precondition is documented here instead.
        /// </summary>
        public static AutoContinueSkipReason DescribeOutcome(bool enabled,
            bool attributableToAgentScripts, bool alreadyContinuedThisTurn)
        {
            if (ShouldAutoContinue(enabled, attributableToAgentScripts, alreadyContinuedThisTurn))
            {
                return AutoContinueSkipReason.WillContinue;
            }
            if (!enabled)
            {
                return AutoContinueSkipReason.DisabledInSettings;
            }
            return AutoContinueSkipReason.AlreadyContinuedThisCycle;
        }

        /// <summary>
        /// HUB-5: whether the queued continuation's drain window has run
        /// out. While Unity is COMPILING the answer is always false and the
        /// caller re-bases its start time -- a long compile must not eat
        /// the window, because the send is blocked on that very compile
        /// (TrySendPendingAutoContinueMessage returns false outright while
        /// isCompiling). Without the freeze, a two-minute compile alone
        /// exhausted the 60s budget and the panel retracted a continuation
        /// it had never actually had a chance to send. This mirrors
        /// CompileGate.OnUpdate, which has always reset its own
        /// _waitStartedAt while compiling; the idiom now exists in one
        /// testable place. A NEGATIVE elapsed (clock adjusted backwards)
        /// is never a timeout -- same guardrail-2 stance as
        /// <see cref="TicketIsFresh"/>.
        /// </summary>
        public static bool ShouldAbandonForTimeout(double nowSeconds, double waitStartedAtSeconds,
            bool isCompiling, double timeoutSeconds)
        {
            if (isCompiling)
            {
                return false;
            }
            double elapsed = nowSeconds - waitStartedAtSeconds;
            if (elapsed < 0.0)
            {
                return false;
            }
            return elapsed > timeoutSeconds;
        }

        /// <summary>
        /// How long SessionStateBridge.AutoContinuePendingAttribution may
        /// sit armed before <see cref="TicketIsFresh"/> must treat it as
        /// stale (2026-08-04 defect 1 fix -- see AutoContinuePendingArmedAtUtcTicks's
        /// own doc comment for the concrete "user hand-fixes a compile
        /// error hours later" scenario this bounds). Five minutes is
        /// deliberately generous next to every OTHER timeout this feature
        /// already uses -- AgentHub.AutoContinueDrainTimeoutSeconds gives
        /// up waiting for a merely-not-yet-sendable CLIENT after 60
        /// seconds, a much shorter bar for a much narrower wait -- because
        /// the goal here is not to flag "the compile ran a little long", it
        /// is to reject "so much wall-clock time passed that the compile
        /// this ticket was betting on is obviously not the one that
        /// eventually reloaded". A real Unity full recompile can run for
        /// tens of seconds on a large project; five minutes stays well
        /// clear of that while still excluding "hours later" by two full
        /// orders of magnitude.
        /// </summary>
        public const double TicketMaxAgeSeconds = 300.0;

        /// <summary>
        /// True only when the reload currently being processed arrives
        /// within <see cref="TicketMaxAgeSeconds"/> of the moment the
        /// attribution ticket was armed. This is the fix for defect 1
        /// (2026-08-04): a plain "how many domain reloads have happened
        /// since arm" counter CANNOT distinguish the concrete failure this
        /// closes, because in that scenario the compile the ticket was
        /// betting on FAILS (Unity performs no domain reload at all after
        /// a failed compile), so the eventual unrelated reload -- hours
        /// later, after the user fixes the same error by hand in their own
        /// IDE -- really is, arithmetically, "the very next reload since
        /// arm" in both the legitimate and the buggy case. Wall-clock time
        /// is the one signal that actually separates them. Guardrail 2
        /// ("if you cannot attribute reliably, DO NOT resume") applies
        /// here exactly as it does to ShouldAutoContinue's own parameters:
        /// a NEGATIVE elapsed time (clock skew, e.g. the system clock was
        /// adjusted backwards between arm and consume) is ALSO treated as
        /// stale, never as "extra fresh" -- there is no legitimate reason
        /// for the reload to be processed before its own arm timestamp.
        /// </summary>
        public static bool TicketIsFresh(long armedAtUtcTicks, long nowUtcTicks)
        {
            long elapsedTicks = nowUtcTicks - armedAtUtcTicks;
            if (elapsedTicks < 0)
            {
                return false;
            }
            double elapsedSeconds = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
            return elapsedSeconds <= TicketMaxAgeSeconds;
        }

        // -- Message composition (guardrail 4) --------------------------

        // The two TRANSCRIPT notes that used to live here (pre-reload
        // pending note, post-reload resuming note) moved to L10n and are
        // emitted by AgentHub: they are user-facing, and every other
        // system note this panel writes is localized. Only the
        // MODEL-facing continuation text stays here, in English, for the
        // same reason the UapOps steering section is English -- it is
        // wire content the model reads, not UI chrome the user reads.

        /// <summary>
        /// The continuation turn's own wire/display text (guardrail 4:
        /// "the continuation must carry the compile result... resuming on
        /// the stale pre-compile assumption is the specific accident this
        /// feature exists to prevent"). <paramref name="compileSucceeded"/>
        /// and <paramref name="errorDigest"/> come from Colloid.AgentPanel.
        /// Integration.ConsoleErrorProvider, queried fresh in the
        /// just-reloaded domain by AgentHub.TryAutoContinueAfterCompile.
        ///
        /// In practice the FAILED branch below is expected to be reached
        /// only rarely, if ever, through this specific call site: Unity
        /// does not perform a domain reload at all when the triggering
        /// compile has errors (it keeps the previously-compiled assemblies
        /// loaded and leaves the errors in the Console instead), so simply
        /// REACHING AgentHub.TryAutoContinueAfterCompile already implies
        /// the compile that caused the reload had none by the time Unity
        /// decided to reload. This method still queries and reports REAL
        /// data rather than hard-coding "SUCCEEDED", both because the
        /// design explicitly asks for that and because "no reload after a
        /// failed compile" is a property of Unity's editor this codebase
        /// observes, not one it enforces -- do not delete the FAILED
        /// branch as dead code.
        /// </summary>
        /// <summary>
        /// The pure decision behind AgentHub.TryAutoContinueInterruptedTurn
        /// (PanelSettings.autoContinueInterruptedTurn). True only when every
        /// guardrail passes: the user opted in, the reload really did
        /// interrupt an open turn (AgentHub.ResumedMidTurn), the crash-loop
        /// guard is not holding the connection, and no OTHER
        /// auto-continuation (the compile one) is already queued for this
        /// same reload. Every "no" here leaves the manual ResumeBanner nudge
        /// in place -- this feature only ever replaces the click, never the
        /// fallback.
        ///
        /// The "at most three in a row without a turn completing" cap that
        /// used to be the fifth condition was removed (design note
        /// 2026-09-10-auto-approve-all-tools-and-lean-auto-continue section
        /// 3): it stopped legitimate work -- an agent that commits scripts,
        /// gets reloaded and continues, three times over, is doing exactly
        /// what the user asked -- while the failure it guarded against (a
        /// reload storm) is already covered by the crash-loop suspension
        /// and by the user's own Interrupt.
        /// </summary>
        public static bool ShouldAutoContinueInterrupted(bool enabled, bool resumedMidTurn,
            bool crashLoopSuspended, bool otherContinuationPending)
        {
            return enabled && resumedMidTurn && !crashLoopSuspended && !otherContinuationPending;
        }

        /// <summary>
        /// Back-compat overload for callers that predate the pending-
        /// permission argument (design note 2026-09-10 section 3):
        /// equivalent to passing null for <paramref name="errorDigest"/>'s
        /// sibling parameter.
        /// </summary>
        public static string ComposeInterruptedContinuationMessage(string errorDigest)
        {
            return ComposeInterruptedContinuationMessage(errorDigest, null);
        }

        /// <summary>
        /// Model-facing wire text for an interrupted-turn continuation
        /// (English by design, like <see cref="ComposeContinuationMessage"/>).
        /// Names the cause class the model can act on -- a Unity domain
        /// reload it did not trigger -- and, when the reload left compiler
        /// errors behind, appends the digest so the model does not resume
        /// on a stale "the project compiles" assumption.
        ///
        /// <paramref name="pendingPermissionTool"/> (design note 2026-09-10
        /// section 3): the display name of a can_use_tool request that was
        /// still awaiting the user's answer when the reload hit, or null/
        /// empty when none was. AgentHub.ShutdownForReload's teardown
        /// discards such a request unconditionally (the CLI process that
        /// would receive the answer is gone), which previously left no
        /// trace at all -- the model could read "interrupted" as "ran
        /// partway" and treat a tool call that never even started as
        /// having completed. When non-empty, a sentence naming the tool is
        /// inserted after the first paragraph and before the compiler-
        /// error note, so the model sees "not yet answered, not run" ahead
        /// of "here is the current compile state".
        /// </summary>
        public static string ComposeInterruptedContinuationMessage(string errorDigest, string pendingPermissionTool)
        {
            var sb = new StringBuilder();
            // Kept to the essentials (design note 2026-09-10-auto-approve-
            // all-tools-and-lean-auto-continue section 3): this text is
            // sent on EVERY reload, so every word costs tokens for the
            // rest of the session. The "check before re-running" clause
            // stays -- a live session (design note 2026-09-08 section 1)
            // showed the agent re-issuing a 9-minute menu action that had
            // already completed when the message only said "re-run".
            sb.Append("A Unity domain reload interrupted your turn; tool calls in flight did not finish."
                + " Verify state (uap_ping, a query or the tool's status action) before re-running"
                + " anything with side effects, then continue.");
            if (!string.IsNullOrEmpty(pendingPermissionTool))
            {
                sb.Append(" The ").Append(pendingPermissionTool)
                    .Append(" call was still awaiting permission and did not run; ask again if needed.");
            }
            if (!string.IsNullOrEmpty(errorDigest))
            {
                sb.Append("\n\nCompiler errors present:\n\n").Append(errorDigest);
            }
            return sb.ToString();
        }

        public static string ComposeContinuationMessage(bool compileSucceeded, string errorDigest)
        {
            if (compileSucceeded)
            {
                return "Unity recompiled and reloaded after your script commit: SUCCEEDED. Continue.";
            }
            var sb = new StringBuilder();
            sb.Append("Unity recompiled and reloaded after your script commit: FAILED.");
            if (!string.IsNullOrEmpty(errorDigest))
            {
                sb.Append("\n\n").Append(errorDigest);
            }
            sb.Append("\n\nFix these errors first.");
            return sb.ToString();
        }
    }
}
