using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// A reason the removal must not proceed, or must proceed with a
    /// warning. Separating the two matters: a blocker disables the button,
    /// a warning only annotates the confirmation card.
    /// </summary>
    public sealed class UloopCaveat
    {
        /// <summary>True = refuse to proceed; false = proceed but say this first.</summary>
        public bool Blocking;

        /// <summary>
        /// Stable identifier for the condition (e.g. "vcc-project",
        /// "manifest-unreadable"). The UI maps this to localized text; the
        /// planner never produces display strings, so the same plan reads
        /// correctly in either language.
        /// </summary>
        public string Code = string.Empty;

        /// <summary>Extra context for the message (a path, a registry name); may be empty.</summary>
        public string Detail = string.Empty;
    }

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
    /// do?", computed BEFORE anything is written. It exists because
    /// manifest.json is the user's asset (ARCHITECTURE risk #12), so a
    /// one-click edit of it is only defensible when the exact diff can be
    /// shown first, which means the plan has to be computable with no side
    /// effects.
    ///
    /// <para>Caveats are <see cref="UloopCaveat"/> values: the UI's
    /// SettingsView.DescribeUloopCaveat maps a code + detail to localized
    /// text.</para>
    /// </summary>
    public sealed class UloopUninstallPlan
    {
        /// <summary>False when uLoop is not a dependency at all; nothing to remove.</summary>
        public bool Installed;

        /// <summary>
        /// The package id actually found in "dependencies" --
        /// <see cref="UloopManifestSupport.UloopPackageId"/> in practice, but read
        /// from the manifest rather than assumed, because UloopDetector
        /// knows a second (renamed) id and removing the one that is NOT
        /// there would silently do nothing.
        /// </summary>
        public string PackageId = string.Empty;

        /// <summary>Blockers and warnings, in the order they should be shown.</summary>
        public readonly List<UloopCaveat> Caveats = new List<UloopCaveat>();

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
