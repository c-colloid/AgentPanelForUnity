namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// UI display language for PanelSettings.language
    /// (docs/design-notes/2026-08-01-i18n.md). Auto defers to
    /// Application.systemLanguage, resolved LAZILY by
    /// Colloid.AgentPanel.UI.L10n.ApplyFromSettings -- never read here,
    /// and never in any ScriptableObject constructor/field initializer (see
    /// PanelSettings.preferCjkUiFont's doc comment for why that specific
    /// mistake broke the whole window before -- f445c22).
    ///
    /// A plain enum (YAML-safe, serializes as int) is enough here: unlike
    /// preferCjkUiFont there is no "decided" flag needed, because Auto is a
    /// legitimate persistent user choice in its own right, not a one-time
    /// default that must never be clobbered once the user picks something
    /// else.
    /// </summary>
    public enum PanelLanguage
    {
        Auto = 0,
        English = 1,
        Japanese = 2
    }
}
