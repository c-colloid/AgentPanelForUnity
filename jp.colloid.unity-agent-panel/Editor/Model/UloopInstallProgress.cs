namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// What the uLoop section should render about an install that may be in
    /// flight. One value per evaluation of <see cref="UloopInstallProgress.Evaluate"/>;
    /// the UI maps these to L10n strings and never invents a state of its own
    /// (docs/design-notes/2026-08-12-uloop-install-progress.md section 2a).
    /// </summary>
    public enum UloopInstallProgressState
    {
        /// <summary>No install in flight and none detected -- render nothing extra.</summary>
        Idle,

        /// <summary>
        /// An install is observably in flight: either a live AddRequest is
        /// still resolving, or (post-domain-reload, where the request object
        /// no longer exists) the SessionState in-flight flag is fresh.
        /// </summary>
        Installing,

        /// <summary>
        /// The detector sees the dependency in manifest.json -- the install
        /// has landed. Also the steady state of any project where uLoop is
        /// simply installed.
        /// </summary>
        Succeeded,

        /// <summary>
        /// The live AddRequest reported failure -- the first time an async
        /// resolve failure is visible inside the panel at all (before this,
        /// it surfaced only in Unity's own Package Manager UI; see
        /// UloopInstaller.ApplyManifestRoute's doc comment).
        /// </summary>
        FailedAsync,

        /// <summary>
        /// No live request, and the in-flight flag is older than the stale
        /// threshold: whatever happened, this machine can no longer claim
        /// "installing" from a real signal. The user is pointed at the
        /// Package Manager window instead of being shown a counter that
        /// counts forever.
        /// </summary>
        Stalled
    }

    /// <summary>One evaluation's output: the state to render, plus whether the caller should clear the SessionState in-flight flag now.</summary>
    public sealed class UloopInstallProgressResult
    {
        public UloopInstallProgressState State;

        /// <summary>
        /// True when the evaluation reached a terminal state (Succeeded /
        /// FailedAsync / Stalled) AND the in-flight flag was actually set.
        /// The second condition matters: the detector reports "installed" on
        /// EVERY refresh of a project that simply has uLoop, forever -- if
        /// terminal state alone forced a clear, every one of those refreshes
        /// would issue a redundant SessionState write. Clearing only when
        /// there is something to clear keeps the steady state write-free.
        /// </summary>
        public bool ShouldClearFlag;
    }

    /// <summary>
    /// Pure state machine for the uLoop install progress display
    /// (docs/design-notes/2026-08-12-uloop-install-progress.md section 2a).
    /// No Unity API on purpose: SettingsView gathers the inputs (the live
    /// AddRequest's flags, UloopDetector's verdict, the SessionStateBridge
    /// flag pair) and renders the output; everything BETWEEN those two --
    /// which signal wins when they disagree -- lives here, where an EditMode
    /// table test can pin every precedence without an editor, a package
    /// resolve, or a domain reload (design note section 3's verification
    /// plan).
    ///
    /// The whole machine exists because of one measured gap: a real install
    /// takes about 30.5 seconds to resolve (2026-08-04 measurement), the
    /// resolve triggers a domain reload that destroys the AddRequest object,
    /// and before this machine nobody polled the request at all -- the UI
    /// showed a static "Not installed" for the entire window.
    /// </summary>
    public static class UloopInstallProgress
    {
        /// <summary>
        /// Age (seconds) past which a request-less in-flight flag stops
        /// rendering as Installing and becomes Stalled. 300s is the measured
        /// 30.5s happy path (2026-08-04 real install) with a roughly x10
        /// margin -- generous enough that a slow network or a large resolve
        /// is not falsely called stalled, small enough that the label does
        /// not keep counting for an install that died with the reload
        /// (design note section 2a: "(c) old flag -> check Package Manager").
        /// </summary>
        public const double StaleThresholdSeconds = 300.0;

        /// <summary>
        /// Evaluates one frame of "what is the install doing?". Every input
        /// is a fact the caller OBSERVED this evaluation; the precedence
        /// order below decides which fact wins when they disagree, and each
        /// step documents why it outranks everything after it.
        ///
        /// <para><b>1. <paramref name="detectorSaysInstalled"/> wins over
        /// everything -> Succeeded.</b> The detector reads manifest.json's
        /// "dependencies" -- the ground truth the whole section's status
        /// line renders from. Every other input is a claim about an attempt;
        /// this one is the outcome itself. It even beats a simultaneously
        /// failed live request: a request can fail while the package is
        /// nonetheless installed (the user installed it by hand from Package
        /// Manager mid-flight, or a duplicate request lost a race) --
        /// rendering FailedAsync over an actually-present dependency would
        /// report a state contradicted by the file on disk.</para>
        ///
        /// <para><b>2. A live failed request -> FailedAsync.</b> Package
        /// Manager itself said the resolve failed -- a real, observed error,
        /// finally visible in the panel. It outranks the in-flight flag in
        /// either freshness: the flag exists ONLY to survive the domain
        /// reload that destroys the request object, so whenever the object
        /// is still alive, its own status is strictly better information
        /// than the flag's timestamp. Checked on <paramref name="requestFailed"/>
        /// alone (not requestFailed AND requestCompleted) so a contradictory
        /// input pair -- failed but somehow not completed, which the real
        /// AddRequest API cannot produce -- still lands on the honest side.</para>
        ///
        /// <para><b>3. Any other live request -> Installing.</b> Still
        /// resolving, or completed successfully with the detector not yet
        /// reporting the dependency. The second half is deliberate: Succeeded
        /// is only ever derived from the detector (rule 1), never from the
        /// request alone, because "the resolve succeeded" is a prediction of
        /// what manifest.json will say and this project's dominant defect
        /// class is rendering states nobody observed. The cost is at most
        /// one extra evaluation showing Installing before rule 1 takes over.
        /// This rule also outranks the flag's staleness ("live request beats
        /// flag"): with a live object reporting IsCompleted == false, the
        /// resolve is genuinely still running no matter how old the flag is,
        /// and Stalled would be a guess layered over a direct observation.</para>
        ///
        /// <para><b>4. No live request, flag fresh -> Installing.</b> The
        /// post-domain-reload continuation (design note 2a): the resolve's
        /// own success destroys the AddRequest by reloading the domain, so a
        /// missing request plus a recent flag is exactly what a HEALTHY
        /// install mid-landing looks like. Reverting to Idle here is the old
        /// bug (the section silently showing "Not installed" mid-install).</para>
        ///
        /// <para><b>5. No live request, flag stale -> Stalled, clear the
        /// flag.</b> Past <see cref="StaleThresholdSeconds"/> there is no
        /// real signal left to render Installing from -- only an old
        /// timestamp. The boundary is inclusive (elapsed == threshold is
        /// stale): the threshold is defined as "the last age still worth
        /// calling in-flight", and a half-open rule keeps exactly one owner
        /// for the boundary value in the table tests.</para>
        ///
        /// <para><b>6. Nothing -> Idle.</b></para>
        /// </summary>
        public static UloopInstallProgressResult Evaluate(
            bool hasLiveRequest,
            bool requestCompleted,
            bool requestFailed,
            bool detectorSaysInstalled,
            bool inFlightFlagSet,
            double elapsedSeconds,
            double staleThresholdSeconds)
        {
            if (detectorSaysInstalled)
            {
                return Result(UloopInstallProgressState.Succeeded, inFlightFlagSet);
            }
            if (hasLiveRequest && requestFailed)
            {
                return Result(UloopInstallProgressState.FailedAsync, inFlightFlagSet);
            }
            if (hasLiveRequest)
            {
                return Result(UloopInstallProgressState.Installing, false);
            }
            if (inFlightFlagSet && elapsedSeconds < staleThresholdSeconds)
            {
                return Result(UloopInstallProgressState.Installing, false);
            }
            if (inFlightFlagSet)
            {
                return Result(UloopInstallProgressState.Stalled, true);
            }
            return Result(UloopInstallProgressState.Idle, false);
        }

        /// <summary>
        /// The same machine, for a REMOVAL (docs/design-notes/2026-09-22-
        /// uloop-remove-from-panel.md section 5). Every precedence rule
        /// above holds unchanged -- which of the live request, the
        /// SessionState flag and the detector to believe when they disagree
        /// is a question about evidence, not about direction -- so the only
        /// difference is how the detector is read: an install succeeds when
        /// the dependency APPEARS, a removal when it is GONE.
        ///
        /// <para>The returned states keep their install-flavored names
        /// (Installing means "in flight", Succeeded means "the removal
        /// landed"). Renaming them would churn the table tests that pin the
        /// precedence for both callers; the user never sees these names --
        /// the UI maps the removal branch to its own strings, so nobody is
        /// ever shown "Installing..." while a package is being removed.</para>
        /// </summary>
        public static UloopInstallProgressResult EvaluateRemoval(
            bool hasLiveRequest,
            bool requestCompleted,
            bool requestFailed,
            bool detectorSaysInstalled,
            bool inFlightFlagSet,
            double elapsedSeconds,
            double staleThresholdSeconds)
        {
            return Evaluate(hasLiveRequest, requestCompleted, requestFailed, !detectorSaysInstalled,
                inFlightFlagSet, elapsedSeconds, staleThresholdSeconds);
        }

        private static UloopInstallProgressResult Result(UloopInstallProgressState state, bool shouldClearFlag)
        {
            return new UloopInstallProgressResult { State = state, ShouldClearFlag = shouldClearFlag };
        }
    }
}
