using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure translation between PermissionModeOption (Settings dropdown)
    /// and the raw --permission-mode / set_permission_mode wire string
    /// (ARCHITECTURE.md 3.2). Kept separate from PanelSettings so the
    /// enum<->string mapping is unit-testable without touching the
    /// persisted asset shape, and so a CLI value the dropdown does not
    /// recognize degrades to "Default" in the UI without corrupting the
    /// stored string (FromCliValue never writes back; callers keep the
    /// original string until the user picks a new option).
    /// </summary>
    public static class PermissionModeMapping
    {
        private const string CliDefault = "default";
        private const string CliPlan = "plan";
        private const string CliAcceptEdits = "acceptEdits";

        /// <summary>Wire value the CLI expects for --permission-mode / set_permission_mode.</summary>
        public static string ToCliValue(PermissionModeOption option)
        {
            switch (option)
            {
                case PermissionModeOption.Plan:
                    return CliPlan;
                case PermissionModeOption.AcceptEdits:
                    return CliAcceptEdits;
                case PermissionModeOption.Default:
                default:
                    return CliDefault;
            }
        }

        /// <summary>
        /// Maps a raw CLI string to the closest dropdown option. Unknown or
        /// empty values (including future CLI modes this UI does not yet
        /// know about) map to Default rather than throwing -- the stored
        /// string itself is untouched by this call.
        /// </summary>
        public static PermissionModeOption FromCliValue(string value)
        {
            if (string.Equals(value, CliPlan, StringComparison.OrdinalIgnoreCase))
            {
                return PermissionModeOption.Plan;
            }
            if (string.Equals(value, CliAcceptEdits, StringComparison.OrdinalIgnoreCase))
            {
                return PermissionModeOption.AcceptEdits;
            }
            return PermissionModeOption.Default;
        }

        // UXIA-7: the English DisplayName that lived here moved to the UI
        // layer as PermissionModeLabels.Describe (L10n-resolved) -- Model
        // must not reference the catalog (D9), and wire conversion is this
        // class's whole remaining responsibility.
    }
}
