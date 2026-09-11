using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// AgentHub-seam coverage for Phase 5c L3 item 3 (auto-continue after
    /// compile, design section 3 item 3 / 8.3) that does NOT require a real
    /// domain reload or a live editor:
    ///
    /// 1. The attribution wiring (TryTrackScriptsCommitAttribution/
    ///    HandleAutoContinueArming), replayed through
    ///    AgentHub.WireClientForTests exactly like
    ///    AgentHubTurnNonUndoableWarningTests -- a real uap_scripts_commit
    ///    tool_use/tool_result/turn-completion sequence, asserting on the
    ///    resulting SessionStateBridge tickets and transcript notes.
    /// 2. AgentHub.TryAutoContinueAfterCompile itself, called directly with
    ///    the tickets pre-set (standing in for "as if a reload just
    ///    happened") against a real AgentClient backed by FakeCliProcess
    ///    (SetClientForTests, no process spawn) -- this is the actual send
    ///    path ReloadLifecycle.OnFirstUpdate drives unconditionally on
    ///    every real domain reload (2026-08-04 defect 2 fix moved this
    ///    call out of RestoreAfterReload and in front of its wasRunning
    ///    gate -- see RestoreAfterReload's own doc comment), minus the
    ///    reload itself.
    /// 3. Guardrail 1 end-to-end (a continuation must never trigger
    ///    another).
    /// 4. The 2026-08-04 defect fixes (ticket staleness, the crash-loop
    ///    guard, and a stranded AutoContinueTurnIsContinuation flag) each
    ///    get their own dedicated regression test: StaleTicket_*,
    ///    CrashLoopSuspended_*, and the two ProcessDeath_*/ProcessDied_*
    ///    tests in Part 4. Defect 6 (the wrong "off in Settings" reason
    ///    string) is guarded structurally in
    ///    ContinuationTurnItselfCommitsScripts_DoesNotArmASecondContinuation
    ///    and, string-independently, by AutoContinueAfterCompilePolicyTests'
    ///    DescribeOutcome tests -- see this stream's final report for the
    ///    exact new L10n string still needed to fix the wording itself.
    ///
    /// What this file deliberately does NOT exercise: an actual
    /// AssemblyReloadEvents reload, or anything that would make
    /// AgentHub.EnsureStarted resolve a real CLI path (StartClient is
    /// private and does exactly that -- see AgentHubStartClientArgTests'
    /// own doc comment for why this test suite avoids it). Every test here
    /// keeps the fake client in a state
    /// (Ready, Starting, NotStarted, or a FakeCliProcess-simulated Errored)
    /// that never triggers that codepath.
    /// </summary>
    [TestFixture]
    public class AgentHubAutoContinueAfterCompileTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private bool _originalAutoContinueEnabled;

        [SetUp]
        public void SetUp()
        {
            _originalAutoContinueEnabled = PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile;
            AgentHub.ResetForTests();
            ConsoleErrorProvider.ResetForTests();
            // The compile-succeeded check reads VisibleCount, which filters
            // through the REAL PanelStateStore ignore settings unless a
            // source is injected (2026-08-13 error-chip-ignore). Without
            // this seam, a user exercising the ignore feature in this
            // sandbox (an X press, or a pattern like "Exception") would
            // silently flip this fixture's failed-continuation cases green.
            ConsoleErrorProvider.SettingsSourceForTests =
                delegate { return new Colloid.AgentPanel.Model.PanelSettings(); };
            // HUB-8: the carry-over pair is SessionState-backed and thus
            // survives between tests (and between editor sessions); clear
            // it so one test's staged compile failure cannot leak into the
            // next test's continuation text.
            SessionStateBridge.ClearLastCompileResult();
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
            AgentHub.WireClientForTests(_client);
            AgentHub.SetClientForTests(_client);
        }

        [TearDown]
        public void TearDown()
        {
            // TryAutoContinueAfterCompile's send path opens a REAL
            // UapTurnScope (SendUserMessage -> BeginIfNeeded) touching the
            // process-global refresh-suppression counter, exactly like
            // AgentHubTurnScopeReleaseTests -- a test that fails before its
            // own CompleteTurn() must not leave it open for every test that
            // runs after.
            if (UapTurnScope.IsActive)
            {
                UapTurnScope.EndIfActive();
            }
            AgentHub.SetClientForTests(null);
            _client.Dispose();
            AgentHub.ResetForTests();
            ConsoleErrorProvider.SettingsSourceForTests = null;
            ConsoleErrorProvider.ResetForTests();
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = _originalAutoContinueEnabled;
            SessionStateBridge.ClearLastCompileResult();
        }

        // -- Helpers (mirroring AgentHubTurnNonUndoableWarningTests/AgentHubScriptGateTests) --

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

        private void StartClientWithoutReachingReady()
        {
            // Start() alone leaves the client in "Starting" -- no
            // system/init has been scripted, so it never reaches Ready.
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
        }

        private void SendToolUse(string toolUseId, string wireToolName)
        {
            _fake.ScriptLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\""
                + ",\"id\":\"" + toolUseId + "\",\"name\":\"" + wireToolName + "\",\"input\":{}}]}}");
            PumpAll();
        }

        private void SendToolResult(string toolUseId, string resultText, bool isError)
        {
            _fake.ScriptLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                + "[{\"type\":\"tool_result\",\"tool_use_id\":\"" + toolUseId + "\",\"content\":\""
                + EscapeJson(resultText) + "\",\"is_error\":" + (isError ? "true" : "false") + "}]}}");
            PumpAll();
        }

        private void CompleteTurn()
        {
            _fake.ScriptLine("{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s1\"}");
            PumpAll();
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        /// <summary>
        /// HUB-6: what ReloadLifecycle.OnFirstUpdate actually does now --
        /// judge and QUEUE the continuation, then (standing in for the
        /// RestoreAfterReload step these tests deliberately skip, since it
        /// would resolve a real CLI path) release the queue. The two halves
        /// were one call before HUB-6, which sent ahead of the resume and
        /// clobbered the mid-turn TurnRunning flag.
        /// </summary>
        private void ReloadSequence(bool clientWasRunning)
        {
            AgentHub.TryAutoContinueAfterCompile(clientWasRunning);
            AgentHub.StartAutoContinueDrain();
        }

        /// <summary>
        /// Stands in for HandleAutoContinueArming's real write for tests
        /// that simulate "as if a reload just happened" without replaying a
        /// whole turn (Part 2/3 below). MUST also stamp a fresh arm
        /// timestamp (2026-08-04 defect 1 fix) -- without it,
        /// AutoContinuePendingArmedAtUtcTicks stays at its zero default,
        /// which AutoContinueAfterCompilePolicy.TicketIsFresh correctly
        /// reads as billions of seconds old (year-1 epoch vs. now) and
        /// rejects as stale, silently turning every "should continue" test
        /// that used to set the two tickets by hand into a "does nothing"
        /// test instead.
        /// </summary>
        private static void ArmTicket(bool wasContinuation)
        {
            SessionStateBridge.AutoContinuePendingAttribution = true;
            SessionStateBridge.AutoContinuePendingWasContinuation = wasContinuation;
            SessionStateBridge.AutoContinuePendingArmedAtUtcTicks = DateTime.UtcNow.Ticks;
        }

        private static string WireName(string uapToolName)
        {
            return "mcp__" + UapOpsMcpConfig.ServerName + "__" + uapToolName;
        }

        private static List<ChatMessage> Messages
        {
            get { return AgentHub.Session.messages; }
        }

        /// <summary>
        /// True when ANY block of ANY transcript message contains
        /// <paramref name="substring"/>. Used instead of a raw
        /// Messages.Count comparison for "no compile-pending note was
        /// added" assertions, because uap_scripts_commit is ALSO tracked
        /// by the PRE-EXISTING non-undoable-tool warning (design section
        /// 8.2 B2(c) -- Undoable == false AND ReadOnly == false for this
        /// tool) regardless of whether its own commit succeeded, so a
        /// turn that calls it always adds AT LEAST that one unrelated
        /// note. Only the wording distinguishes "my new note" from that.
        /// </summary>
        private static bool TranscriptContains(string substring)
        {
            foreach (ChatMessage message in Messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.text != null && block.text.Contains(substring))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Design note 2026-09-10-auto-approve-all-tools-and-lean-auto-
        /// continue section 3: TrySendPendingAutoContinueMessage suppresses
        /// the user-role transcript bubble for an automatic continuation
        /// (AgentHub._suppressTranscriptBubble around its SendUserMessage
        /// call) -- the wire message still goes out (assert on
        /// _fake.WrittenLines instead), but no Session ChatMessage with
        /// role == RoleUser may appear for it. The one-line system note
        /// (guardrail 3) is unaffected and still appears.
        /// </summary>
        private static void AssertNoUserBubbleWasAdded()
        {
            foreach (ChatMessage message in Messages)
            {
                Assert.AreNotEqual(ChatMessage.RoleUser, message.role,
                    "an automatic continuation must never add a user-role transcript bubble.");
            }
        }

        private const string CommittedOneFile =
            "Committed 1 file(s) into Assets/ (validated, 0 compile errors): Assets/Foo.cs."
            + " They will be imported at the end of this turn (one batched refresh).";

        private const string NothingStaged = "Nothing staged under UapStaging/; nothing to commit.";

        private const string CompilationFailed =
            "Compilation failed; nothing was moved into Assets/. Fix these errors and commit again:\n"
            + "Assets/Foo.cs(3,5): error CS1002: ; expected";

        // == Part 1: attribution wiring (HandleAutoContinueArming) via a replayed turn ==

        [Test]
        public void SuccessfulCommit_EnabledSetting_ArmsTicket_AndNotePromisesAutoContinue()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            SendToolUse("t1", WireName(AutoContinueAfterCompilePolicy.ScriptsCommitToolName));
            SendToolResult("t1", CommittedOneFile, isError: false);
            CompleteTurn();

            Assert.IsTrue(SessionStateBridge.AutoContinuePendingAttribution);
            Assert.IsFalse(SessionStateBridge.AutoContinuePendingWasContinuation);
            ChatMessage last = Messages[Messages.Count - 1];
            Assert.AreEqual(ChatMessage.RoleSystem, last.role);
            StringAssert.Contains("continuation turn automatically", last.blocks[0].text);
        }

        [Test]
        public void SuccessfulCommit_DisabledSetting_ArmsTicket_ButNoteSaysPromptAgain()
        {
            // The pending-reload announcement itself is not gated on the
            // setting (design section 8.3's "never a silent background
            // reload" is unconditional) -- only its wording changes.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = false;
            StartReadyClient();

            SendToolUse("t1", WireName(AutoContinueAfterCompilePolicy.ScriptsCommitToolName));
            SendToolResult("t1", CommittedOneFile, isError: false);
            CompleteTurn();

            Assert.IsTrue(SessionStateBridge.AutoContinuePendingAttribution,
                "attribution tracking must not depend on the feature being enabled.");
            ChatMessage last = Messages[Messages.Count - 1];
            Assert.AreEqual(ChatMessage.RoleSystem, last.role);
            StringAssert.Contains("prompt again", last.blocks[0].text);
            StringAssert.DoesNotContain("continuation turn automatically", last.blocks[0].text);
        }

        [Test]
        public void NothingStagedCommit_DoesNotArmTicket_AndAddsNoCompilePendingNote()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            SendToolUse("t1", WireName(AutoContinueAfterCompilePolicy.ScriptsCommitToolName));
            SendToolResult("t1", NothingStaged, isError: false);
            CompleteTurn();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution);
            // uap_scripts_commit is ALSO tracked by the pre-existing
            // non-undoable-tool warning regardless of outcome -- see
            // TranscriptContains' doc comment -- so this checks the
            // WORDING, not the raw message count.
            Assert.IsFalse(TranscriptContains("Unity will compile and reload"),
                "a non-attributable commit must add no compile-pending note.");
        }

        [Test]
        public void CompilationFailedCommit_DoesNotArmTicket_AndAddsNoCompilePendingNote()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            SendToolUse("t1", WireName(AutoContinueAfterCompilePolicy.ScriptsCommitToolName));
            SendToolResult("t1", CompilationFailed, isError: false);
            CompleteTurn();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution);
            Assert.IsFalse(TranscriptContains("Unity will compile and reload"));
        }

        [Test]
        public void ErroredCommitToolResult_DoesNotArmTicket_EvenIfTextSaysCommitted()
        {
            // is_error:true must short-circuit before the text is even
            // inspected -- a defensive belt-and-suspenders guard.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            SendToolUse("t1", WireName(AutoContinueAfterCompilePolicy.ScriptsCommitToolName));
            SendToolResult("t1", CommittedOneFile, isError: true);
            CompleteTurn();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution);
        }

        [Test]
        public void NoScriptsCommitCallThisTurn_DoesNotArmTicket()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            SendToolUse("t1", WireName("uap_scene_create_object"));
            SendToolResult("t1", "{}", isError: false);
            CompleteTurn();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution);
        }

        [Test]
        public void UnrelatedNonUapToolCommittingSomething_DoesNotArmTicket()
        {
            // The CLI's OWN Write tool is not uap_scripts_commit -- must
            // never be confused with it regardless of its result text.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            SendToolUse("t1", "Write");
            SendToolResult("t1", CommittedOneFile, isError: false);
            CompleteTurn();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution);
        }

        // == Part 2: TryAutoContinueAfterCompile (the post-reload send path) ==

        /// <summary>
        /// HUB-6: the judge/queue step must NOT send. Sending from there
        /// runs ahead of RestoreAfterReload, which opens a new turn before
        /// the resume is established -- clobbering the mid-turn
        /// TurnRunning flag and spawning the CLI before the transcript
        /// restore. The message must sit queued until the drain step.
        /// </summary>
        [Test]
        public void TryAutoContinue_QueuesOnly_SendsNothingUntilTheDrainStep()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            int baseline = _fake.WrittenLines.Count;

            AgentHub.TryAutoContinueAfterCompile(true);

            Assert.IsTrue(AgentHub.HasPendingAutoContinueMessageForTests,
                "the continuation must be QUEUED by the judge step");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count,
                "but nothing may reach the wire before the resume is established");
            Assert.AreEqual(0, Messages.Count,
                "not even the 'panel is sending' note -- no send has been attempted yet");

            AgentHub.StartAutoContinueDrain();
            PumpAll();

            Assert.IsFalse(AgentHub.HasPendingAutoContinueMessageForTests);
            Assert.Greater(_fake.WrittenLines.Count, baseline, "the drain step performs the send");
        }

        /// <summary>
        /// HUB-6, the flag this ordering exists to protect: TurnRunning is
        /// what RestoreAfterReload reads to decide "we resumed mid-turn".
        /// The queue step must leave it untouched.
        /// </summary>
        [Test]
        public void TryAutoContinue_LeavesTurnRunningFlagIntactForTheResume()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            SessionStateBridge.TurnRunning = true;

            AgentHub.TryAutoContinueAfterCompile(true);

            Assert.IsTrue(SessionStateBridge.TurnRunning,
                "the mid-turn resume flag must survive the queue step -- RestoreAfterReload reads it next");
        }

        /// <summary>
        /// HUB-8: the compile result is captured before the reload, in the
        /// domain where compiler errors actually land. Reading only the
        /// post-reload (freshly empty) ConsoleErrorProvider reported
        /// "SUCCEEDED" for every reload, including the partial-failure case
        /// -- telling the model a result nothing observed.
        /// </summary>
        [Test]
        public void CarriedCompileFailure_ProducesAFailedContinuation_WithTheCarriedDigest()
        {
            // Design note 2026-09-10-auto-approve-all-tools-and-lean-auto-
            // continue section 3: the automatic continuation no longer adds
            // a user-role ChatMessage bubble to the Session (TrySendPendingAutoContinueMessage's
            // _suppressTranscriptBubble) -- the actual send is only visible
            // on the wire (the FakeCliProcess outbound line), so this now
            // reads _fake.WrittenLines instead of a Session message.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            SessionStateBridge.LastCompileHadErrors = true;
            SessionStateBridge.LastCompileErrorDigest = "Assets/Foo.cs(12,3): error CS1002: ; expected";

            ReloadSequence(true);
            PumpAll();

            string sentLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("FAILED", sentLine);
            StringAssert.Contains("CS1002", sentLine,
                "the carried digest must reach the model, not an empty error list");
            AssertNoUserBubbleWasAdded();

            Assert.IsFalse(SessionStateBridge.LastCompileHadErrors, "the carry-over must be consumed");
            Assert.AreEqual(string.Empty, SessionStateBridge.LastCompileErrorDigest);
        }

        [Test]
        public void NoCarriedCompileResult_FallsBackToTheLiveProvider_AndStillSucceeds()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            SessionStateBridge.ClearLastCompileResult();

            ReloadSequence(true);
            PumpAll();

            string sentLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("SUCCEEDED", sentLine);
            AssertNoUserBubbleWasAdded();
        }


        [Test]
        public void ClientWasNotRunning_ConsumesTheTicket_ButSendsNothing()
        {
            // The consume/act split. Moving this call out from behind
            // ReloadLifecycle's wasRunning gate was necessary -- a ticket
            // left armed misleads whichever later reload finally has a
            // running client -- but it must not also make the SEND
            // unconditional. If no client was running when the domain went
            // down there was no interrupted turn, so "continue the task"
            // would mean spawning a CLI and instructing it, unattended, on
            // behalf of a session that had already ended. Spend the ticket;
            // send nothing.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            int baseline = _fake.WrittenLines.Count;

            ReloadSequence(false);
            PumpAll();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution,
                "the ticket must still be consumed, or it misleads a later reload");
            Assert.IsFalse(SessionStateBridge.AutoContinuePendingWasContinuation);
            Assert.AreEqual(0, SessionStateBridge.AutoContinuePendingArmedAtUtcTicks);
            Assert.IsFalse(AgentHub.HasPendingAutoContinueMessageForTests,
                "nothing may be queued for a turn that was never interrupted");
            Assert.AreEqual(baseline, _fake.WrittenLines.Count, "and nothing may reach the wire");
        }

        [Test]
        public void ShouldContinue_SendsResumingNote_ThenSucceededContinuation_AndConsumesTickets()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            int baseline = _fake.WrittenLines.Count;

            ReloadSequence(true);
            PumpAll();

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution, "the ticket must be consumed.");
            Assert.IsFalse(SessionStateBridge.AutoContinuePendingWasContinuation);
            Assert.IsTrue(SessionStateBridge.AutoContinueTurnIsContinuation,
                "the turn just sent must be marked as a continuation for the NEXT OnTurnCompleted to see.");
            Assert.IsFalse(AgentHub.HasPendingAutoContinueMessageForTests, "the send must have happened immediately.");
            Assert.Greater(_fake.WrittenLines.Count, baseline, "a user message must have been written to the wire.");
            StringAssert.Contains("SUCCEEDED", _fake.WrittenLines[_fake.WrittenLines.Count - 1],
                "the actual continuation text goes out on the wire.");

            // Design note 2026-09-10-auto-approve-all-tools-and-lean-auto-
            // continue section 3: the automatic continuation gets ONLY the
            // one-line system note (guardrail 3's "panel is sending") --
            // no user-role bubble repeating the wire text back into the
            // transcript.
            Assert.AreEqual(1, Messages.Count,
                "only the system note may be added -- no user bubble for the automatic send.");
            Assert.AreEqual(ChatMessage.RoleSystem, Messages[0].role);
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.HubAutoContinueResuming, Messages[0].blocks[0].text);
            AssertNoUserBubbleWasAdded();
        }

        [Test]
        public void NotAttributable_DoesNothing_EvenWhenEnabled()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            SessionStateBridge.AutoContinuePendingAttribution = false;
            SessionStateBridge.AutoContinuePendingWasContinuation = false;
            int baseline = _fake.WrittenLines.Count;

            ReloadSequence(true);

            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
            Assert.AreEqual(0, Messages.Count);
            Assert.IsFalse(SessionStateBridge.AutoContinueTurnIsContinuation);
        }

        [Test]
        public void Disabled_DoesNothing_EvenWhenAttributable()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = false;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            int baseline = _fake.WrittenLines.Count;

            ReloadSequence(true);

            Assert.AreEqual(baseline, _fake.WrittenLines.Count);
            Assert.AreEqual(0, Messages.Count);
        }

        [Test]
        public void AlwaysConsumesTickets_EvenWhenNotSending()
        {
            // Guardrail 2's "fail closed" must not leave a stale ticket for
            // a LATER, unrelated reload to misread.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = false;
            StartReadyClient();
            ArmTicket(wasContinuation: true);

            ReloadSequence(true);

            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution);
            Assert.IsFalse(SessionStateBridge.AutoContinuePendingWasContinuation);
        }

        [Test]
        public void StaleTicket_ArmedPastMaxAge_DoesNotAutoContinue_EvenWhenAttributableAndEnabled()
        {
            // Defect 1 regression guard: the concrete failure was a commit
            // whose own compile fails (no domain reload follows a failed
            // compile at all), leaving the ticket armed until an unrelated
            // LATER reload -- the user hand-fixing the same error in their
            // own IDE, potentially hours afterwards -- reads a still-true
            // ticket and wrongly resumes the agent.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            SessionStateBridge.AutoContinuePendingAttribution = true;
            SessionStateBridge.AutoContinuePendingWasContinuation = false;
            SessionStateBridge.AutoContinuePendingArmedAtUtcTicks = DateTime.UtcNow
                .AddSeconds(-(AutoContinueAfterCompilePolicy.TicketMaxAgeSeconds + 60.0)).Ticks;
            int baseline = _fake.WrittenLines.Count;

            ReloadSequence(true);

            Assert.AreEqual(baseline, _fake.WrittenLines.Count,
                "a ticket armed long before this reload must not resume the agent -- it is almost"
                    + " certainly attributed to the wrong reload.");
            Assert.AreEqual(0, Messages.Count);
            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution, "still consumed, just not acted on.");
        }

        [Test]
        public void CompileErrorsAlreadyPresent_SendsFailedContinuation_WithDigest()
        {
            // Defensive branch (see AutoContinueAfterCompilePolicy.
            // ComposeContinuationMessage's doc comment): queried from REAL
            // ConsoleErrorProvider data, not assumed.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();
            ArmTicket(wasContinuation: false);
            ConsoleErrorProvider.EnqueueLogMessageForTests(
                "NullReferenceException: Object reference not set", "at Foo.Bar()", LogType.Exception);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            ReloadSequence(true);
            PumpAll();

            Assert.AreEqual(1, Messages.Count, "only the system note -- no user bubble for the automatic send.");
            string continuationText = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("FAILED", continuationText);
            StringAssert.Contains("NullReferenceException", continuationText);
            AssertNoUserBubbleWasAdded();
        }

        [Test]
        public void ClientNotYetSendable_QueuesTheMessage_InsteadOfSpawningANewClient()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartClientWithoutReachingReady();
            ArmTicket(wasContinuation: false);

            ReloadSequence(true);

            Assert.IsTrue(AgentHub.HasPendingAutoContinueMessageForTests,
                "with the client still Starting, the continuation must stay queued rather than being dropped.");
            Assert.IsFalse(SessionStateBridge.AutoContinueTurnIsContinuation,
                "must not be marked as sent until an actual wire write is confirmed.");
            // Defect 3 fix: the "panel is sending" note must not appear
            // until a send is actually attempted against a SENDABLE
            // client, which this one never becomes.
            Assert.AreEqual(0, Messages.Count,
                "no note may claim the panel is sending anything while the client is still Starting.");
        }

        [Test]
        public void CrashLoopSuspended_DropsContinuation_WithoutCallingEnsureStarted()
        {
            // Defect 4 regression guard: OnProcessDied stops restarting once
            // _consecutiveDeaths exceeds MaxConsecutiveRestarts (3) and
            // posts its own "connection suspended" note. Before this fix,
            // TrySendPendingAutoContinueMessage had no idea that had
            // happened and would call EnsureStarted() -- respawning a REAL
            // claude process -- on every single drain tick for the rest of
            // the 60-second window, fighting the suspension it was just
            // told about.
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            AgentHub.SetConsecutiveDeathsForTests(4); // > MaxConsecutiveRestarts (3)
            ArmTicket(wasContinuation: false);

            ReloadSequence(true);

            Assert.IsFalse(AgentHub.HasPendingAutoContinueMessageForTests,
                "a crash-loop-suspended connection must drop the queued continuation immediately"
                    + " rather than sit there waiting out the full 60s drain window.");
            Assert.AreEqual(AgentClientState.NotStarted, _client.State,
                "must never call EnsureStarted() while the crash-loop guard has the connection"
                    + " suspended -- every editor update tick for the whole drain window would"
                    + " otherwise respawn a real claude process.");
        }

        // == Part 3: guardrail 1 end-to-end -- a continuation must never trigger another ==

        [Test]
        public void ContinuationTurnItselfCommitsScripts_DoesNotArmASecondContinuation()
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = true;
            StartReadyClient();

            // Step 1: as if a reload just happened after a genuine,
            // attributable turn -- send the continuation.
            ArmTicket(wasContinuation: false);
            ReloadSequence(true);
            PumpAll();
            Assert.IsTrue(SessionStateBridge.AutoContinueTurnIsContinuation);
            int writtenAfterFirstContinuation = _fake.WrittenLines.Count;

            // Step 2: the continuation turn (C1) itself stages and commits
            // MORE scripts successfully.
            SendToolUse("t2", WireName(AutoContinueAfterCompilePolicy.ScriptsCommitToolName));
            SendToolResult("t2", CommittedOneFile, isError: false);
            CompleteTurn();

            // HandleAutoContinueArming must see thisTurnWasContinuation ==
            // true and refuse to promise a second auto-continue, even
            // though this turn WAS genuinely attributable and the setting
            // is on.
            Assert.IsTrue(SessionStateBridge.AutoContinuePendingAttribution,
                "the ticket itself is still armed (attribution tracking does not know about guardrail 1).");
            Assert.IsTrue(SessionStateBridge.AutoContinuePendingWasContinuation,
                "but it must be flagged as having come from a continuation turn.");
            ChatMessage pendingNote = Messages[Messages.Count - 1];
            StringAssert.Contains("prompt again", pendingNote.blocks[0].text,
                "the pending-reload note must NOT promise a second auto-continuation.");
            // Defect 6 regression guard: this note must not claim the
            // setting is off -- it is ON throughout this test. (The
            // wording currently still says this: see
            // AutoContinueAfterCompilePolicyTests' DescribeOutcome_
            // EnabledAttributableAlreadyContinued_ReturnsAlreadyContinuedThisCycle_
            // NotDisabled test for the actual, string-independent
            // regression guard on the underlying cause classification,
            // and this stream's final report for the exact new L10n
            // string needed to fix the wording itself.)

            // Step 3: as if C1's own commit caused a SECOND reload --
            // nothing must be sent this time.
            ReloadSequence(true);

            Assert.AreEqual(writtenAfterFirstContinuation, _fake.WrittenLines.Count,
                "guardrail 1: a continuation must never itself trigger another continuation.");
            Assert.IsFalse(SessionStateBridge.AutoContinuePendingAttribution, "the ticket must still be consumed.");
        }

        // == Part 4: defect 5 -- an abnormal CLI death must not strand ==
        // == AutoContinueTurnIsContinuation true forever                ==

        [Test]
        public void ProcessDeath_ViaOnStateChangedErrored_ClearsStrandedContinuationFlag()
        {
            // AgentHubTurnScopeReleaseTests' own precedent for simulating a
            // mid-turn crash without any real process: FailWrites + a send
            // lands the client in Errored and fires StateChanged, entirely
            // inside FakeCliProcess.
            AgentHub.WireStateChangedForTests(_client);
            StartReadyClient();
            SessionStateBridge.AutoContinueTurnIsContinuation = true;

            _fake.FailWrites = true;
            AgentHub.SendUserMessage("continue", null, null);

            Assert.AreEqual(AgentClientState.Errored, _client.State);
            Assert.IsFalse(SessionStateBridge.AutoContinueTurnIsContinuation,
                "a continuation turn that crashes instead of reaching OnTurnCompleted must not leave"
                    + " this flag stranded true for whatever unrelated turn completes next -- it would"
                    + " silently refuse to arm a genuinely attributable continuation, and (defect 6)"
                    + " claim the feature is off in Settings when it is not.");
        }

        [Test]
        public void ProcessDied_ClearsStrandedContinuationFlag()
        {
            // Isolated from OnStateChanged on purpose (only ProcessDied is
            // wired here): OnProcessDied must clear this flag on its own,
            // not merely as a side effect of also having StateChanged
            // wired -- some Errored transitions never raise ProcessDied at
            // all (e.g. StartClient failing before a process ever spawns),
            // and the reverse should hold too.
            AgentHub.WireProcessDiedForTests(_client);
            StartReadyClient();
            SessionStateBridge.AutoContinueTurnIsContinuation = true;

            _fake.FailWrites = true;
            AgentHub.SendUserMessage("continue", null, null);

            Assert.IsFalse(SessionStateBridge.AutoContinueTurnIsContinuation,
                "OnProcessDied must clear the flag independently of OnStateChanged.");
        }
        // == HUB-10: wantsToQuit must not kill a live CLI ==

        /// <summary>
        /// EditorApplication.wantsToQuit fires while the quit can still be
        /// VETOED (another package returning false, or Cancel on the
        /// unsaved-scenes dialog). The old handler called AgentHub.Shutdown()
        /// there, so a cancelled quit left the panel silently disconnected
        /// in an editor that went right on running. The replacement only
        /// refreshes the orphan-reaper record -- the client must survive
        /// untouched.
        /// </summary>
        [Test]
        public void RefreshProcessRecordForQuit_LeavesTheLiveClientRunning()
        {
            StartReadyClient();
            AgentClientState stateBefore = _client.State;

            AgentHub.RefreshProcessRecordForQuit();

            Assert.AreSame(_client, AgentHub.Client, "the client must not be torn down by a vetoable quit");
            Assert.AreEqual(stateBefore, AgentHub.Client.State, "and its state must be untouched");
        }

        [Test]
        public void RefreshProcessRecordForQuit_WithNoClient_IsASilentNoOp()
        {
            AgentHub.SetClientForTests(null);
            Assert.DoesNotThrow(delegate { AgentHub.RefreshProcessRecordForQuit(); });
        }

    }
}
