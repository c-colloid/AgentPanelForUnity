namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Which authentication method the spawned Claude Code CLI is allowed
    /// to pick (docs/design-notes/2026-09-10-claude-api-key-auth-
    /// passthrough.md): Anthropic's Claude Code legal terms require that a
    /// product running the CLI "may not remove, disable, or restrict any
    /// authentication method built into it (including methods that permit
    /// signing in with a Claude account or the user's own API key)", so the
    /// panel must not force ANTHROPIC_API_KEY out of the child environment
    /// by default.
    ///
    /// PanelSettings.claudeAuth persists this; ClaudeCliProcess.
    /// ComputeEnvVarsToRemove(ClaudeAuthMode) is the pure seam that decides
    /// whether ANTHROPIC_API_KEY is stripped from the spawned process'
    /// environment. Persisted as an int (YAML-safe) -- never renumber, only
    /// append.
    /// </summary>
    public enum ClaudeAuthMode
    {
        /// <summary>
        /// DEFAULT. ANTHROPIC_API_KEY is left completely untouched: the CLI
        /// picks auth exactly as it would in a terminal (an API key in the
        /// editor's environment wins and bills pay-per-use; otherwise the
        /// stored subscription login is used).
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Opt-in. ANTHROPIC_API_KEY is removed from the spawned CLI's
        /// environment so the stored subscription login is always used,
        /// even when the editor process happens to carry that variable.
        /// This is the panel's previous (pre-v0.40.0) unconditional
        /// behaviour, now an explicit choice rather than the default.
        /// </summary>
        SubscriptionOnly = 1
    }
}
