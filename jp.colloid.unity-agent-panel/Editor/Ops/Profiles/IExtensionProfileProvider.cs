namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// The open-core registration seam for bundled Extension Profiles
    /// (docs/design-notes/2026-09-11-core-pro-split.md "seam 2"): an
    /// add-on package (Agent Panel Pro, or any other third party) that
    /// ships its own reviewed *.json profiles implements this on a
    /// non-abstract class with a public parameterless constructor, and
    /// <see cref="ExtensionProfileCatalog.LoadBundled"/> discovers it via
    /// <c>UnityEditor.TypeCache.GetTypesDerivedFrom&lt;IExtensionProfileProvider&gt;()</c>
    /// and merges its directory's *.json files into the bundled catalog --
    /// a provider-supplied profile is exactly as trusted
    /// (<see cref="ExtensionProfileTrust"/>) as one Core ships directly,
    /// since it is reviewed source shipped as part of an installed
    /// package, not user/third-party content dropped into the project.
    /// </summary>
    public interface IExtensionProfileProvider
    {
        /// <summary>
        /// Absolute directory containing this provider's *.json profile
        /// files (non-recursive, same shape <see cref="ExtensionProfileCatalog.LoadFromDirectory"/>
        /// expects of Core's own directory). Resolve it the same
        /// [CallerFilePath]-relative way <see cref="ExtensionProfileCatalog.ResolveProfilesDirectory"/>
        /// does, so it keeps working regardless of where the package is
        /// physically installed (embedded, git package, or a dev junction).
        /// </summary>
        string ProfilesDirectory { get; }
    }
}
