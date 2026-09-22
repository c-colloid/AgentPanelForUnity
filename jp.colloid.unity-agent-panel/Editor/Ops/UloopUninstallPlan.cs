using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// What the second phase of a removal would do to manifest.json's
    /// "scopedRegistries" (docs/design-notes/2026-09-22-uloop-remove-from-
    /// panel.md section 3). Three outcomes, because "remove the registry"
    /// and "remove one scope from it" are not the same promise and a user
    /// reading the confirmation card is entitled to know which one they are
    /// agreeing to.
    /// </summary>
    public enum UloopRegistryCleanup
    {
        /// <summary>
        /// Leave scopedRegistries exactly as it is. Either no entry points
        /// at OpenUPM, or the only uLoop scope on it is still needed by
        /// another dependency, or the scope is a broader prefix this panel
        /// never wrote.
        /// </summary>
        None = 0,

        /// <summary>Drop the uLoop id from the entry's "scopes"; the entry stays for its other scopes.</summary>
        DropScope = 1,

        /// <summary>
        /// Drop the whole entry: removing the uLoop id would leave its
        /// "scopes" empty, and a registry with no scopes serves nothing.
        /// </summary>
        DropRegistry = 2,
    }

    /// <summary>
    /// The complete, inspectable answer to "what would pressing Remove
    /// do?", computed BEFORE anything is written -- the mirror of
    /// <see cref="UloopInstallPlan"/> and, for the same reason it exists:
    /// manifest.json is the user's asset (ARCHITECTURE risk #12), so a
    /// one-click edit of it is only defensible when the exact diff can be
    /// shown first, which means the plan has to be computable with no side
    /// effects.
    ///
    /// <para>Caveats deliberately reuse <see cref="UloopInstallCaveat"/>
    /// rather than growing a parallel type of the same shape: the UI's
    /// SettingsView.DescribeUloopCaveat already maps a code + detail to
    /// localized text, and a removal caveat wants exactly that rendering.
    /// The type's name says "Install" only because it was introduced by the
    /// install path first.</para>
    /// </summary>
    public sealed class UloopUninstallPlan
    {
        /// <summary>False when uLoop is not a dependency at all; nothing to remove.</summary>
        public bool Installed;

        /// <summary>
        /// The package id actually found in "dependencies" --
        /// <see cref="UloopInstaller.UloopPackageId"/> in practice, but read
        /// from the manifest rather than assumed, because UloopDetector
        /// knows a second (renamed) id and removing the one that is NOT
        /// there would silently do nothing.
        /// </summary>
        public string PackageId = string.Empty;

        /// <summary>Blockers and warnings, in the order they should be shown.</summary>
        public readonly List<UloopInstallCaveat> Caveats = new List<UloopInstallCaveat>();

        /// <summary>What phase two would do to scopedRegistries. See <see cref="UloopRegistryCleanup"/>.</summary>
        public UloopRegistryCleanup RegistryCleanup = UloopRegistryCleanup.None;

        /// <summary>Display fields for the matched OpenUPM entry; empty when none matched.</summary>
        public string RegistryName = string.Empty;
        public string RegistryUrl = string.Empty;

        /// <summary>
        /// The scopes that would REMAIN on the entry after the cleanup.
        /// Empty with <see cref="UloopRegistryCleanup.DropRegistry"/> (that
        /// emptiness is exactly why the entry goes), and unused with
        /// <see cref="UloopRegistryCleanup.None"/>.
        /// </summary>
        public readonly List<string> RetainedScopes = new List<string>();

        /// <summary>
        /// Other dependency ids still covered by a uLoop scope, which is why
        /// that scope is NOT dropped (section 3 rule 2). Non-empty here
        /// means the cleanup was downgraded, and the card says so by name
        /// rather than silently doing less than it offered.
        /// </summary>
        public readonly List<string> ScopeHolders = new List<string>();

        /// <summary>manifest.json's path, and where the pre-change copy would be written.</summary>
        public string ManifestPath = string.Empty;
        public string BackupPath = string.Empty;

        /// <summary>The manifest exactly as <see cref="UloopUninstaller.Plan"/> read it.</summary>
        public string ManifestBefore = string.Empty;

        /// <summary>
        /// What the manifest is EXPECTED to look like once both phases have
        /// run: the dependency key removed (which Unity's Client.Remove
        /// does, not this package) plus the registry cleanup above. A
        /// PROJECTION, not a text that will ever be written verbatim --
        /// phase two recomputes against whatever Unity actually left behind
        /// (design note section 2, case (b): writing this text after
        /// Client.Remove would resurrect the dependency it just deleted).
        /// It exists so the confirmation card can show a real diff.
        /// </summary>
        public string ManifestAfterProjected = string.Empty;

        /// <summary>True when uLoop is present and no caveat blocks the removal.</summary>
        public bool CanProceed()
        {
            if (!Installed || string.IsNullOrEmpty(PackageId))
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
