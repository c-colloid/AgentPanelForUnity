namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Subagent cost policy (v0.11.0, docs/design-notes/2026-08-02-subagent-
    /// model-precedence.md section 3.2): the PRIMARY control for "run
    /// subagents cheaply", steering the agent's own per-call Task/Agent
    /// `model` parameter choice (docs/research/07-model-configuration.md
    /// section 12, P4/P5) rather than crushing it the way the blanket
    /// CLAUDE_CODE_SUBAGENT_MODEL env var (PanelSettings.subagentModel, now
    /// exposed as a "force" control) does. AgentDecides injects nothing into
    /// the spawn's append-system-prompt; HaikuForSimpleTasks appends one
    /// instruction line (AgentHub.ComposeAppendSystemPrompt) asking the
    /// agent to pass model:"haiku" for simple mechanical subtasks and omit
    /// the parameter otherwise, so it inherits the session model.
    ///
    /// A plain enum (YAML-safe, serializes as int) is enough here -- like
    /// PanelLanguage, there is no "decided" flag needed: AgentDecides is a
    /// legitimate persistent default in its own right, not a one-time
    /// default that must never be clobbered once the user picks the other
    /// option.
    /// </summary>
    public enum SubagentCostPolicy
    {
        AgentDecides = 0,
        HaikuForSimpleTasks = 1
    }
}
