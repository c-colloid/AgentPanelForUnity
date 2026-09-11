using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    /// <summary>
    /// The thin production wrapper around <see cref="UnityPluginDetector"/>:
    /// reads the real environment, home directory and the two CLI state
    /// files, then hands the strings to the pure detector. Every read is
    /// guarded -- a missing or unreadable file is "not there", never an
    /// exception in the Settings view. Not cached: the files are two small
    /// JSON documents and the card refreshes on its own coalesced tick.
    /// </summary>
    public static class UnityPluginProbe
    {
        /// <summary>The CLI config directory this machine's spawn will use (see <see cref="UnityPluginPaths.ResolveConfigDir"/>).</summary>
        public static string ResolveConfigDir()
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    string key = entry.Key as string;
                    if (key != null)
                    {
                        env[key] = entry.Value as string;
                    }
                }
            }
            catch (Exception)
            {
                // fall through with whatever was read
            }
            string home = null;
            try
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch (Exception)
            {
            }
            return UnityPluginPaths.ResolveConfigDir(env, home);
        }

        /// <summary>
        /// Static-only answer (no session): reads installed_plugins.json and
        /// settings.json from the resolved config directory.
        /// </summary>
        public static UnityPluginStatus ReadStatic(bool cliAvailable)
        {
            return Read(cliAvailable, null, null);
        }

        /// <summary>
        /// Full answer: static files plus, when <paramref name="init"/> is
        /// non-null, that session's plugins[] / plugin_errors[].
        /// </summary>
        public static UnityPluginStatus Read(bool cliAvailable, SystemInitMessage init)
        {
            return Read(cliAvailable,
                init != null ? init.Plugins : null,
                init != null ? init.PluginErrors : null);
        }

        private static UnityPluginStatus Read(bool cliAvailable, IList<PluginEntry> initPlugins,
            IList<PluginError> initErrors)
        {
            string configDir = ResolveConfigDir();
            InstalledPluginEntry installed = UnityPluginDetector.ParseInstalledPlugins(
                SafeRead(UnityPluginPaths.InstalledPluginsPath(configDir)));
            bool enabled = UnityPluginDetector.IsEnabledInSettingsJson(
                SafeRead(UnityPluginPaths.SettingsPath(configDir)));
            return UnityPluginDetector.Resolve(cliAvailable, installed, enabled, initPlugins, initErrors);
        }

        private static string SafeRead(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
