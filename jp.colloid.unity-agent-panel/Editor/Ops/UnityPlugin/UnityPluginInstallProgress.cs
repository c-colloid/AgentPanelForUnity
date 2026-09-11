namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    public enum UnityPluginInstallProgressState
    {
        /// <summary>Nothing in flight; show the detector's own status line only.</summary>
        None,
        /// <summary>A run is in flight (live worker, or a fresh SessionState flag after a domain reload).</summary>
        Installing,
        /// <summary>The run reported success, or the flag was set and the detector now sees the plugin installed.</summary>
        Done,
        /// <summary>The live run reported failure.</summary>
        Failed,
        /// <summary>The flag is old (a reload lost the worker) and the detector still sees nothing.</summary>
        Stalled
    }

    public sealed class UnityPluginInstallProgressResult
    {
        public UnityPluginInstallProgressState State;

        /// <summary>The SessionState pair is spent (Done/Stalled): the caller clears it so the next refresh reads None.</summary>
        public bool ShouldClearFlag;
    }

    /// <summary>
    /// The install-progress decision table (design note section 2.3 items
    /// 3-5 and the domain-reload paragraph), pure so the precedence between
    /// the live worker, the reload-surviving SessionState flag and the
    /// detector is pinned by tests -- the same split UloopInstallProgress
    /// uses. Precedence: a live verdict beats the flag; the detector
    /// seeing the plugin beats a flag of any age; only then does age decide
    /// Installing vs Stalled.
    /// </summary>
    public static class UnityPluginInstallProgress
    {
        /// <summary>After this, a reload-orphaned flag reads as Stalled (design note section 2.3: five minutes).</summary>
        public const double StaleThresholdSeconds = 300.0;

        public static UnityPluginInstallProgressResult Evaluate(bool liveRunning, bool liveCompleted,
            bool liveSucceeded, bool installedNow, bool flagSet, double elapsedSeconds, double staleThresholdSeconds)
        {
            var result = new UnityPluginInstallProgressResult();
            if (liveRunning)
            {
                result.State = UnityPluginInstallProgressState.Installing;
                return result;
            }
            if (liveCompleted)
            {
                result.State = liveSucceeded
                    ? UnityPluginInstallProgressState.Done
                    : UnityPluginInstallProgressState.Failed;
                result.ShouldClearFlag = true;
                return result;
            }
            if (!flagSet)
            {
                result.State = UnityPluginInstallProgressState.None;
                return result;
            }
            if (installedNow)
            {
                result.State = UnityPluginInstallProgressState.Done;
                result.ShouldClearFlag = true;
                return result;
            }
            if (elapsedSeconds < staleThresholdSeconds)
            {
                result.State = UnityPluginInstallProgressState.Installing;
                return result;
            }
            result.State = UnityPluginInstallProgressState.Stalled;
            result.ShouldClearFlag = true;
            return result;
        }
    }
}
