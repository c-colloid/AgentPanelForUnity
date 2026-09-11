namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Outcome of AutoApplySettingsPolicy.Evaluate (see that class's doc
    /// comment for the full flow).
    /// </summary>
    public enum AutoApplyDecision
    {
        /// <summary>Nothing to do yet: no pending change, not connected, or still inside the debounce window.</summary>
        DoNothing,
        /// <summary>The client is idle: AgentHub should call Reconnect() now.</summary>
        ApplyNow,
        /// <summary>A turn is running or a permission is pending: wait for TurnCompleted (with no pending permission) before applying.</summary>
        DeferToTurnEnd
    }

    /// <summary>
    /// Pure decision for the settings auto-apply feature (docs/design-notes/
    /// 2026-08-01-settings-auto-apply.md section 3): given a pending
    /// settings change that requires a reconnect
    /// (SettingsChangeDetector.RequiresReconnect), whether AgentHub should
    /// apply it right now via the existing same-session Reconnect() path,
    /// defer it until the running turn ends, or do nothing yet.
    ///
    /// Deliberately holds no timers or state of its own -- AgentHub owns the
    /// debounce clock (EditorApplication.timeSinceStartup, the same idiom
    /// CompileGate's drain hook already uses) and re-evaluates this on every
    /// tick and at TurnCompleted, so every branch here is a pure function of
    /// its inputs and fully unit-testable without Unity (see
    /// AutoApplySettingsPolicyTests).
    /// </summary>
    public static class AutoApplySettingsPolicy
    {
        /// <summary>
        /// Coalescing window (design note section 3.2): rapid successive
        /// settings edits reset the "seconds since last edit" clock, so only
        /// the LAST edit in a burst ever reaches ApplyNow/DeferToTurnEnd.
        /// </summary>
        public const double DebounceSeconds = 1.5;

        /// <summary>
        /// Evaluates the current auto-apply state.
        /// </summary>
        /// <param name="reconnectNeeded">
        /// SettingsChangeDetector.RequiresReconnect against the current
        /// settings/custom-instructions.
        /// </param>
        /// <param name="isConnected">
        /// A live client in Ready/Streaming/ToolRunning/WaitingPermission
        /// (NOT NotStarted/Starting/Errored) -- the design note's "not
        /// connected (not spawned) -&gt; do nothing" rule.
        /// </param>
        /// <param name="turnRunning">AgentClient.TurnActive (an open turn, spans Streaming/ToolRunning/WaitingPermission).</param>
        /// <param name="pendingPermission">AgentHub.PendingPermission != null.</param>
        /// <param name="secondsSinceLastEdit">
        /// Elapsed time since the most recent change-commit event that
        /// required a reconnect (reset on every such event, so a burst of
        /// edits only ever measures from the LAST one).
        /// </param>
        /// <param name="debounceSeconds">
        /// Normally DebounceSeconds; exposed as a parameter purely so tests
        /// can probe the boundary without depending on the constant staying
        /// 1.5.
        /// </param>
        public static AutoApplyDecision Evaluate(
            bool reconnectNeeded,
            bool isConnected,
            bool turnRunning,
            bool pendingPermission,
            double secondsSinceLastEdit,
            double debounceSeconds = DebounceSeconds)
        {
            if (!reconnectNeeded || !isConnected)
            {
                return AutoApplyDecision.DoNothing;
            }
            if (secondsSinceLastEdit < debounceSeconds)
            {
                return AutoApplyDecision.DoNothing;
            }
            return (turnRunning || pendingPermission)
                ? AutoApplyDecision.DeferToTurnEnd
                : AutoApplyDecision.ApplyNow;
        }
    }
}
