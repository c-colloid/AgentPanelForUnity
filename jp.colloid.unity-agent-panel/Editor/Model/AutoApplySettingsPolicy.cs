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
        /// <summary>
        /// Whether a pending settings change should be ARMED (kept, so the
        /// debounce tick can apply it) or discarded outright.
        ///
        /// <para><b>The bug this exists for (2026-09-22, reported from a
        /// real editor): flip a reconnect-relevant setting twice in quick
        /// succession -- the second flip landing before the first one's
        /// reconnect finishes -- and the pending pill sticks forever.</b>
        /// Both callers used to collapse this into "connected? act :
        /// discard". But a client that is <c>Starting</c> is not a client
        /// that will never spawn: very often it is mid-flight on this
        /// feature's OWN reconnect for the previous edit. The second edit
        /// was therefore thrown away, and nothing re-evaluates when the
        /// client reaches Ready (the debounce tick had been unhooked, and
        /// only a completed turn calls back in). The spawn snapshot kept
        /// edit #1's value, the live settings held edit #2's, and the
        /// detector compared the two forever -- the panel promising a
        /// reconnect that nothing would ever perform. Only a MANUAL
        /// reconnect, which re-snapshots, could clear it.</para>
        ///
        /// <para>So unavailability is split in two. TRANSIENT
        /// (<paramref name="spawnInFlight"/>) keeps the change armed until
        /// the client settles. PERMANENT (never spawned, errored) still
        /// discards it -- the original "not connected -&gt; do nothing"
        /// rule, still right, because the next real spawn reads the
        /// current settings anyway.</para>
        /// </summary>
        public static bool ShouldArm(bool reconnectNeeded, bool isConnected, bool spawnInFlight)
        {
            return reconnectNeeded && (isConnected || spawnInFlight);
        }

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
