using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Full table over the pure uLoop install progress machine
    /// (docs/design-notes/2026-08-12-uloop-install-progress.md section 2a;
    /// its section 3 verification plan asks for exactly this: "cut the
    /// polling state machine out into a pure class and table-test it in
    /// EditMode"). Every state is pinned, plus every precedence PAIR that
    /// could plausibly be ordered wrong in a refactor: detector vs failed
    /// request, live request vs flag freshness, and the fresh/stale
    /// boundary landing exactly on the threshold. ShouldClearFlag is
    /// asserted on every row -- a wrong clear either strands a flag
    /// (Installing forever after a dead install) or spams SessionState
    /// writes on every steady-state refresh of an installed project.
    ///
    /// No Unity API anywhere below: the machine's inputs are plain bools/
    /// doubles precisely so these tests need no editor state, no package
    /// resolve, and no domain reload.
    /// </summary>
    public class UloopInstallProgressTests
    {
        private static UloopInstallProgressResult Eval(
            bool hasLiveRequest, bool requestCompleted, bool requestFailed,
            bool detectorSaysInstalled, bool inFlightFlagSet, double elapsedSeconds)
        {
            return UloopInstallProgress.Evaluate(
                hasLiveRequest, requestCompleted, requestFailed,
                detectorSaysInstalled, inFlightFlagSet, elapsedSeconds,
                UloopInstallProgress.StaleThresholdSeconds);
        }

        // -- The threshold itself ------------------------------------------------

        [Test]
        public void StaleThreshold_Is300Seconds_TenTimesTheMeasuredHappyPath()
        {
            // 30.5s measured real resolve (2026-08-04) x roughly 10. If
            // someone lowers this below the measured happy path, healthy
            // installs start rendering Stalled mid-resolve; this pin makes
            // that a deliberate, test-breaking decision instead of a tweak.
            Assert.AreEqual(300.0, UloopInstallProgress.StaleThresholdSeconds);
            Assert.Greater(UloopInstallProgress.StaleThresholdSeconds, 30.5);
        }

        // -- Idle ----------------------------------------------------------------

        [Test]
        public void NoSignalsAtAll_IsIdle_NoClear()
        {
            UloopInstallProgressResult r = Eval(false, false, false, false, false, 0.0);
            Assert.AreEqual(UloopInstallProgressState.Idle, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void NoFlagNoRequest_HugeElapsed_IsStillIdle_ElapsedMeansNothingWithoutAFlag()
        {
            UloopInstallProgressResult r = Eval(false, false, false, false, false, 99999.0);
            Assert.AreEqual(UloopInstallProgressState.Idle, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        // -- Installing: live request --------------------------------------------

        [Test]
        public void LivePendingRequest_IsInstalling_NoClear()
        {
            UloopInstallProgressResult r = Eval(true, false, false, false, true, 5.0);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void LivePendingRequest_WithoutAnyFlag_IsStillInstalling()
        {
            // The request alone is a full signal; the flag only exists for
            // the post-reload stretch where the request object is gone.
            UloopInstallProgressResult r = Eval(true, false, false, false, false, 0.0);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void LivePendingRequest_BeatsAStaleFlag_InstallingNotStalled()
        {
            // Precedence pair "live request beats flag": IsCompleted ==
            // false is a direct observation that the resolve is still
            // running; calling it Stalled off an old timestamp would layer
            // a guess over that observation.
            UloopInstallProgressResult r = Eval(true, false, false, false, true,
                UloopInstallProgress.StaleThresholdSeconds + 100.0);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void LiveRequestCompletedSuccessfully_DetectorNotYetFlipped_IsStillInstalling()
        {
            // Succeeded is only ever derived from the detector (the file on
            // disk), never from the request alone -- "the resolve
            // succeeded" is a prediction of what manifest.json will say,
            // and this project does not render predictions. Costs at most
            // one evaluation before the detector scan lands on Succeeded.
            UloopInstallProgressResult r = Eval(true, true, false, false, true, 31.0);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        // -- Installing: post-reload flag ------------------------------------------

        [Test]
        public void NoLiveRequest_FreshFlag_IsInstalling_ThePostDomainReloadReentry()
        {
            // The resolve's own success destroys the AddRequest by
            // reloading the domain -- a missing request plus a recent flag
            // is what a HEALTHY install mid-landing looks like. Reverting
            // to Idle here is the original reported defect.
            UloopInstallProgressResult r = Eval(false, false, false, false, true, 12.0);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void NoLiveRequest_FlagWithZeroElapsed_IsInstalling()
        {
            UloopInstallProgressResult r = Eval(false, false, false, false, true, 0.0);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        // -- Fresh/stale boundary ---------------------------------------------------

        [Test]
        public void FlagJustUnderTheThreshold_IsStillInstalling()
        {
            UloopInstallProgressResult r = Eval(false, false, false, false, true,
                UloopInstallProgress.StaleThresholdSeconds - 0.001);
            Assert.AreEqual(UloopInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void FlagAtExactlyTheThreshold_IsStalled_TheBoundaryIsInclusive()
        {
            // elapsed == threshold is STALE (half-open interval): the
            // threshold is "the last age still worth calling in-flight",
            // and pinning the boundary's owner here keeps a refactor from
            // silently flipping >= to >.
            UloopInstallProgressResult r = Eval(false, false, false, false, true,
                UloopInstallProgress.StaleThresholdSeconds);
            Assert.AreEqual(UloopInstallProgressState.Stalled, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void FlagWayPastTheThreshold_IsStalled_AndClearsTheFlag()
        {
            UloopInstallProgressResult r = Eval(false, false, false, false, true, 86400.0);
            Assert.AreEqual(UloopInstallProgressState.Stalled, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        // -- FailedAsync -------------------------------------------------------------

        [Test]
        public void LiveFailedRequest_IsFailedAsync_AndClearsTheFlag()
        {
            UloopInstallProgressResult r = Eval(true, true, true, false, true, 40.0);
            Assert.AreEqual(UloopInstallProgressState.FailedAsync, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void LiveFailedRequest_FlagAlreadyCleared_IsStillFailedAsync_ButNoRedundantClear()
        {
            // The first FailedAsync evaluation clears the flag; every later
            // refresh re-derives the same state from the still-live request
            // WITHOUT ordering another SessionState write.
            UloopInstallProgressResult r = Eval(true, true, true, false, false, 40.0);
            Assert.AreEqual(UloopInstallProgressState.FailedAsync, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void FailedButNotCompleted_ContradictoryInput_StillLandsOnFailedAsync()
        {
            // The real AddRequest API cannot produce failed-without-
            // completed, but the machine checks requestFailed alone on
            // purpose so a contradictory input pair lands on the honest
            // (failure-visible) side instead of rendering Installing over
            // a reported failure.
            UloopInstallProgressResult r = Eval(true, false, true, false, true, 40.0);
            Assert.AreEqual(UloopInstallProgressState.FailedAsync, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void LiveFailedRequest_BeatsAFreshFlag()
        {
            // The flag exists only to survive the reload that destroys the
            // request object; while the object is alive its own status is
            // strictly better information than the flag's timestamp.
            UloopInstallProgressResult r = Eval(true, true, true, false, true, 1.0);
            Assert.AreEqual(UloopInstallProgressState.FailedAsync, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        // -- Succeeded: the detector wins over everything ------------------------------

        [Test]
        public void DetectorInstalled_WithInFlightFlag_IsSucceeded_AndClearsTheFlag()
        {
            UloopInstallProgressResult r = Eval(false, false, false, true, true, 33.0);
            Assert.AreEqual(UloopInstallProgressState.Succeeded, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void DetectorInstalled_NoFlagSet_IsSucceeded_WithNoClear_TheSteadyStateWritesNothing()
        {
            // Every refresh of a project that simply HAS uLoop lands here,
            // forever. ShouldClearFlag must be false or each of those
            // refreshes would issue a redundant SessionState write.
            UloopInstallProgressResult r = Eval(false, false, false, true, false, 0.0);
            Assert.AreEqual(UloopInstallProgressState.Succeeded, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void DetectorInstalled_AndLiveRequestFailed_SimultaneouslyIsSucceeded()
        {
            // THE precedence pair most worth pinning: the detector reads
            // the outcome itself (the dependency line on disk) while the
            // failed request only describes an attempt -- a request can
            // fail while the package is nonetheless installed (hand-install
            // from Package Manager mid-flight, a duplicate request losing a
            // race). Rendering FailedAsync here would contradict the file
            // on disk.
            UloopInstallProgressResult r = Eval(true, true, true, true, true, 40.0);
            Assert.AreEqual(UloopInstallProgressState.Succeeded, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void DetectorInstalled_AndStaleFlag_IsSucceededNotStalled()
        {
            UloopInstallProgressResult r = Eval(false, false, false, true, true,
                UloopInstallProgress.StaleThresholdSeconds + 500.0);
            Assert.AreEqual(UloopInstallProgressState.Succeeded, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void DetectorInstalled_AndLivePendingRequest_IsSucceeded()
        {
            // Detector beats a still-pending request too (e.g. the user
            // hand-installed from Package Manager while our request was
            // still resolving): installed is installed.
            UloopInstallProgressResult r = Eval(true, false, false, true, true, 10.0);
            Assert.AreEqual(UloopInstallProgressState.Succeeded, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }
    }
}
