using System.Collections.Generic;
using System.Text;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// One profile's resolved status for this domain-load/settings snapshot
    /// -- the shape both <see cref="ExtensionProfileInjector"/> (system-
    /// prompt composition) and the Settings "Extension profiles" card
    /// (design section C4) consume, built once by
    /// <see cref="ExtensionProfileService.BuildStatuses"/> so the two never
    /// disagree about what is detected/trusted.
    /// </summary>
    public sealed class ExtensionProfileStatus
    {
        public ExtensionProfile Profile;

        /// <summary>Whether the SDK this profile describes was found in this project (ExtensionProfileDetector).</summary>
        public bool Detected;

        /// <summary>Whether this profile's content may actually be injected (ExtensionProfileTrust) -- always true for a bundled profile, hash-gated for a user profile.</summary>
        public bool Trusted;

        public bool IsBundled;

        /// <summary>Raw-byte content hash (null for a bundled profile, which is not hash-pinned).</summary>
        public string ContentHashHex;

        /// <summary>Absolute path of the user profile's JSON file (null for a bundled profile).</summary>
        public string FilePath;
    }

    /// <summary>
    /// Pure composition of the Extension Profiles section appended to
    /// --append-system-prompt (design section 3b, C3): one "## DisplayName"
    /// block per DETECTED and TRUSTED profile, its instructionLines
    /// verbatim underneath, blocks joined with a blank line. A profile that
    /// is not detected, or not trusted (an unapproved/tampered user
    /// profile), contributes nothing -- this is the single enforcement
    /// point for design section 8.2 B3's "never injected without approval".
    /// </summary>
    public static class ExtensionProfileInjector
    {
        public static string ComposeProfilesSection(IEnumerable<ExtensionProfileStatus> statuses)
        {
            if (statuses == null)
            {
                return string.Empty;
            }
            var blocks = new List<string>();
            foreach (ExtensionProfileStatus status in statuses)
            {
                if (status == null || status.Profile == null)
                {
                    continue;
                }
                if (!status.Detected || !status.Trusted)
                {
                    continue;
                }
                blocks.Add(BuildBlock(status.Profile));
            }
            return blocks.Count == 0 ? string.Empty : string.Join("\n\n", blocks.ToArray());
        }

        private static string BuildBlock(ExtensionProfile profile)
        {
            var sb = new StringBuilder();
            sb.Append("## ").Append(profile.DisplayName);
            if (profile.InstructionLines != null)
            {
                for (int i = 0; i < profile.InstructionLines.Count; i++)
                {
                    sb.Append('\n').Append(profile.InstructionLines[i]);
                }
            }
            return sb.ToString();
        }
    }
}
