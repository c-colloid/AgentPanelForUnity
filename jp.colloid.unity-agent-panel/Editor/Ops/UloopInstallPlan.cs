using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// How a one-click uLoop install would be performed. Two routes, in
    /// preference order (design section 2.5).
    /// </summary>
    public enum UloopInstallMethod
    {
        /// <summary>Nothing to do -- already installed, or blocked.</summary>
        None = 0,

        /// <summary>
        /// UnityEditor.PackageManager.Client.Add with a Git URL. Preferred
        /// because it is the official API and never touches manifest.json
        /// by hand.
        /// </summary>
        GitUrl = 1,

        /// <summary>
        /// Fallback: add an OpenUPM scoped registry plus the dependency to
        /// manifest.json ourselves. Only taken when the Git route is not
        /// viable, because it edits a file the user owns.
        /// </summary>
        ManifestScopedRegistry = 2,
    }

    /// <summary>
    /// A reason the install must not proceed, or must proceed with a
    /// warning. Separating the two matters: a blocker disables the button,
    /// a warning only annotates the confirmation card.
    /// </summary>
    public sealed class UloopInstallCaveat
    {
        /// <summary>True = refuse to install; false = install but say this first.</summary>
        public bool Blocking;

        /// <summary>
        /// Stable identifier for the condition (e.g. "vcc-project",
        /// "offline", "manifest-unreadable"). The UI maps this to localized
        /// text; the planner never produces display strings, so the same
        /// plan reads correctly in either language.
        /// </summary>
        public string Code = string.Empty;

        /// <summary>Extra context for the message (a path, a registry name); may be empty.</summary>
        public string Detail = string.Empty;
    }

    /// <summary>
    /// The complete, inspectable answer to "what would pressing Install
    /// do?", computed BEFORE anything is written.
    ///
    /// This exists as a separate value from the act of installing for one
    /// reason: manifest.json is the user's asset, and this package's
    /// standing rule (ARCHITECTURE risk #12) is that the panel does not
    /// edit it. A one-click install is a deliberate exception the user asks
    /// for by pressing a button -- so the exception is only defensible if
    /// they can see the exact diff first, which means the plan has to be
    /// computable and renderable without side effects.
    /// </summary>
    public sealed class UloopInstallPlan
    {
        /// <summary>True when uLoop is already present; the UI shows status, not a button.</summary>
        public bool AlreadyInstalled;

        /// <summary>Route that would be taken. None when already installed or blocked.</summary>
        public UloopInstallMethod Method = UloopInstallMethod.None;

        /// <summary>Blockers and warnings, in the order they should be shown.</summary>
        public readonly List<UloopInstallCaveat> Caveats = new List<UloopInstallCaveat>();

        /// <summary>
        /// For <see cref="UloopInstallMethod.GitUrl"/>: the exact URL that
        /// would be passed to Client.Add. Empty otherwise.
        /// </summary>
        public string GitUrl = string.Empty;

        /// <summary>
        /// For <see cref="UloopInstallMethod.ManifestScopedRegistry"/>: the
        /// manifest.json path, and the before/after text the confirmation
        /// card diffs. Empty for the Git route.
        /// </summary>
        public string ManifestPath = string.Empty;
        public string ManifestBefore = string.Empty;
        public string ManifestAfter = string.Empty;

        /// <summary>
        /// Where the pre-change copy of manifest.json would be written.
        /// Empty for the Git route (which writes no backup because it
        /// changes nothing by hand).
        /// </summary>
        public string BackupPath = string.Empty;

        /// <summary>True when no caveat blocks the install.</summary>
        public bool CanProceed()
        {
            if (AlreadyInstalled || Method == UloopInstallMethod.None)
            {
                return false;
            }
            for (int i = 0; i < Caveats.Count; i++)
            {
                if (Caveats[i] != null && Caveats[i].Blocking)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
