namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One user-defined quick action: a short label plus the prompt text it
    /// inserts into the composer (docs/design-notes/2026-08-01-settings-
    /// enrichment.md #3). Persisted via QuickActionStore, never
    /// PanelSettings/UnityYAML -- prompt text can be code-shaped, same
    /// rationale as CustomInstructionsFile.
    /// </summary>
    public sealed class QuickAction
    {
        public string label = string.Empty;
        public string prompt = string.Empty;
    }
}
