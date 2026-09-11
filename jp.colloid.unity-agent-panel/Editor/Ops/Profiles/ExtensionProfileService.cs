using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Production wiring/single source of truth for Extension Profiles
    /// (design section 3b/C3/C4): combines the bundled catalog
    /// (<see cref="ExtensionProfileCatalog"/>) and the user sidecar
    /// (<see cref="UserExtensionProfileStore"/>) with live detection
    /// (<see cref="ExtensionProfileDetectionCache"/>) and trust
    /// (<see cref="ExtensionProfileTrust"/>) into one
    /// <see cref="ExtensionProfileStatus"/> list. Both AgentHub (system-
    /// prompt composition, via <see cref="ComposeAppendSection"/>) and
    /// SettingsView (the "Extension profiles" card's detected/status list)
    /// call <see cref="BuildStatuses"/> so they can never disagree about
    /// what is detected or trusted.
    /// </summary>
    public static class ExtensionProfileService
    {
        public static List<ExtensionProfileStatus> BuildStatuses(string projectRoot,
            List<string> approvedHashes, Action<string> log = null)
        {
            var result = new List<ExtensionProfileStatus>();
            // OPS-11: the approved list holds machine-bound tokens; resolve
            // this machine's salt once for the whole pass.
            string approvalSalt = MachineApprovalSalt.Get();

            List<ExtensionProfile> bundled = ExtensionProfileCatalog.LoadBundled(log);
            for (int i = 0; i < bundled.Count; i++)
            {
                ExtensionProfile profile = bundled[i];
                bool detected = ExtensionProfileDetectionCache.IsDetected("bundled:" + profile.Id, profile, projectRoot);
                result.Add(new ExtensionProfileStatus
                {
                    Profile = profile,
                    Detected = detected,
                    Trusted = ExtensionProfileTrust.IsTrusted(true, null, null, approvalSalt),
                    IsBundled = true
                });
            }

            List<UserExtensionProfileEntry> userEntries = UserExtensionProfileStore.Load(projectRoot, log);
            for (int i = 0; i < userEntries.Count; i++)
            {
                UserExtensionProfileEntry entry = userEntries[i];
                bool detected = ExtensionProfileDetectionCache.IsDetected(
                    "user:" + entry.ContentHashHex, entry.Profile, projectRoot);
                bool trusted = ExtensionProfileTrust.IsTrusted(false, entry.ContentHashHex, approvedHashes, approvalSalt);
                result.Add(new ExtensionProfileStatus
                {
                    Profile = entry.Profile,
                    Detected = detected,
                    Trusted = trusted,
                    IsBundled = false,
                    ContentHashHex = entry.ContentHashHex,
                    FilePath = entry.FilePath
                });
            }

            return result;
        }

        /// <summary>
        /// The composed --append-system-prompt Extension Profiles section
        /// for this spawn, or empty when <paramref name="profilesEnabled"/>
        /// (the Settings master toggle) is off -- the toggle is checked
        /// HERE (not inside ExtensionProfileInjector) so a disabled toggle
        /// short-circuits before even touching disk/TypeCache.
        /// </summary>
        public static string ComposeAppendSection(string projectRoot, bool profilesEnabled,
            List<string> approvedHashes, Action<string> log = null)
        {
            if (!profilesEnabled)
            {
                return string.Empty;
            }
            return ExtensionProfileInjector.ComposeProfilesSection(BuildStatuses(projectRoot, approvedHashes, log));
        }
    }
}
