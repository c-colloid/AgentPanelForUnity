using System;

namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    /// <summary>
    /// The one place that knows what Unity's official Claude Code plugin is
    /// called (docs/design-notes/2026-09-10-unity-official-plugin-integration.md
    /// section 1.3). Values are MEASURED, not assumed: a real
    /// `claude plugin install unity@unity-agent-plugin` on CLI 2.1.267 wrote
    /// `enabledPlugins["unity@unity-agent-plugin"]` and the session's
    /// system/init then reported `plugins[0] = { name: "unity", source:
    /// "unity@unity-agent-plugin", version, path }` (docs/verify/
    /// 2026-09-10-unity-plugin-verification-report.md sections 1-2).
    /// </summary>
    public static class UnityPluginIdentity
    {
        public const string MarketplaceName = "unity-agent-plugin";

        public const string PluginName = "unity";

        /// <summary>The `plugin@marketplace` id the CLI uses in settings.json / installed_plugins.json.</summary>
        public const string QualifiedName = PluginName + "@" + MarketplaceName;

        /// <summary>The GitHub `owner/repo` form `claude plugin marketplace add` accepts.</summary>
        public const string MarketplaceSource = "Unity-Technologies/unity-agent-plugin";

        /// <summary>
        /// True when a system/init `plugins[]` entry is this plugin: its
        /// `name` is the bare plugin name or its `source` is the qualified
        /// id. Ordinal (case-sensitive) -- both strings are CLI-generated
        /// identifiers, not user text.
        /// </summary>
        public static bool Matches(string name, string source)
        {
            return string.Equals(name, PluginName, StringComparison.Ordinal)
                || string.Equals(name, QualifiedName, StringComparison.Ordinal)
                || string.Equals(source, QualifiedName, StringComparison.Ordinal);
        }
    }
}
