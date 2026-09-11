using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One subagent name -&gt; model alias override (docs/design-notes/
    /// 2026-08-01-model-settings.md section 2). Persisted inside
    /// PanelSettings.agentModelOverrides -- UnityYAML-safe: both fields are
    /// short plain strings, never brace-heavy JSON/prompt text (unlike
    /// CustomInstructionsFile/QuickActionStore's sidecar files, this needs
    /// no sidecar of its own).
    ///
    /// The panel's own saved list is the SOLE source of truth for which
    /// overrides are active; the CLI's own system/init.agents listing
    /// cannot be used to detect one (R07 section 5/9.2): overriding a
    /// built-in agent name like "general-purpose" does not change how it
    /// appears in init.agents. This table is no longer sent to the CLI as
    /// a `--agents` argument at all -- R07 section 10 found that flag is
    /// silently ignored whenever `--resume` is also passed (which the
    /// panel always does once a session exists), so
    /// AgentDefinitionFileWriter materializes each entry as a
    /// `.claude/agents/&lt;agentName&gt;.md` file instead (verified to survive
    /// --resume; see that class's doc comment for the exact file format).
    /// </summary>
    [Serializable]
    public sealed class AgentModelOverride
    {
        /// <summary>
        /// Subagent name (e.g. "general-purpose", "Explore", or a custom
        /// name). Empty is a valid, inert placeholder row -- skipped
        /// entirely when building the --agents argument.
        /// </summary>
        public string agentName = string.Empty;

        /// <summary>
        /// Model alias/name for this agent's --agents JSON "model" field.
        /// Empty means "inherits the Default model" -- also skipped when
        /// building the --agents argument (R07 section 6: an agent absent
        /// from --agents always inherits the parent session's resolved
        /// model).
        /// </summary>
        public string modelAlias = string.Empty;
    }
}
