using Colloid.AgentPanel.Model;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// UXIA-7: the single place a <see cref="PermissionModeOption"/>
    /// becomes text -- same shape and rules as
    /// <see cref="AutoApproveLevelLabels"/>. The labels used to live as
    /// English literals in Model-layer PermissionModeMapping.DisplayName,
    /// which could not be localized without breaking the D9 layering rule
    /// (Model must not reference L10n); the mapping class keeps only its
    /// wire-value responsibility, and this UI-layer class resolves the
    /// human-readable side from the catalog.
    /// </summary>
    public static class PermissionModeLabels
    {
        /// <summary>
        /// Localized label for one option. An unrecognised value falls back
        /// to the Default (ask every time) label -- the SAFEST reading,
        /// mirroring AutoApproveLevelLabels.Describe's rule, so a settings
        /// asset carrying a mode from some future build still reads as the
        /// most-asking behavior rather than an empty string.
        /// </summary>
        public static string Describe(PermissionModeOption option)
        {
            switch (option)
            {
                case PermissionModeOption.Plan:
                    return L10n.S.SettingsPermissionModeOptionPlan;
                case PermissionModeOption.AcceptEdits:
                    return L10n.S.SettingsPermissionModeOptionAcceptEdits;
                case PermissionModeOption.Default:
                default:
                    return L10n.S.SettingsPermissionModeOptionDefault;
            }
        }
    }
}
