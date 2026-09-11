using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    /// <summary>
    /// Where the CLI keeps its user-scope state (design note section 1.4).
    /// The CLI honours `CLAUDE_CONFIG_DIR` -- measured 2026-09-10: pointing
    /// it at an empty directory made the very same spawn report
    /// `plugins: []` -- so the panel's static detection must read the same
    /// directory the CLI will, or the card and the session disagree.
    /// Pure: the environment and home directory are parameters.
    /// </summary>
    public static class UnityPluginPaths
    {
        public const string ConfigDirEnvVar = "CLAUDE_CONFIG_DIR";

        public const string SettingsFileName = "settings.json";

        /// <summary>`$CLAUDE_CONFIG_DIR` when set and non-blank, else `&lt;home&gt;/.claude`. Null when neither is known.</summary>
        public static string ResolveConfigDir(IDictionary<string, string> environment, string homeDirectory)
        {
            string fromEnv;
            if (environment != null && environment.TryGetValue(ConfigDirEnvVar, out fromEnv)
                && !string.IsNullOrEmpty(fromEnv) && fromEnv.Trim().Length > 0)
            {
                return fromEnv.Trim();
            }
            if (string.IsNullOrEmpty(homeDirectory))
            {
                return null;
            }
            return Path.Combine(homeDirectory, ".claude");
        }

        public static string SettingsPath(string configDir)
        {
            return configDir == null ? null : Path.Combine(configDir, SettingsFileName);
        }

        public static string InstalledPluginsPath(string configDir)
        {
            return configDir == null ? null : Path.Combine(configDir, "plugins", "installed_plugins.json");
        }
    }
}
