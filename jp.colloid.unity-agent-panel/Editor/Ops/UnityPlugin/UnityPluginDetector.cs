using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    /// <summary>
    /// What the Settings card and the steering line can say about Unity's
    /// official plugin (design note section 1.2). The five states of the
    /// design note plus <see cref="Disabled"/>: an install record exists
    /// but `enabledPlugins` does not say true, which is what
    /// `claude plugin disable` leaves behind -- a different user intent
    /// from "never installed", so it gets its own word.
    /// </summary>
    public enum UnityPluginState
    {
        /// <summary>No claude executable could be resolved; nothing else can be known.</summary>
        CliUnavailable,
        /// <summary>No install record and no enabledPlugins entry (and, when a session is observed, not loaded either).</summary>
        NotInstalled,
        /// <summary>Install record present but enabledPlugins does not enable it.</summary>
        Disabled,
        /// <summary>Installed and enabled on disk. With a session observed: that session did NOT load it. Without a session: merely "installed".</summary>
        EnabledNotLoaded,
        /// <summary>The observed session's system/init lists it under plugins[].</summary>
        Loaded,
        /// <summary>The observed session's system/init lists it under plugin_errors[].</summary>
        LoadError
    }

    /// <summary>One install record from `~/.claude/plugins/installed_plugins.json` (version 2 shape, measured 2026-09-10).</summary>
    public sealed class InstalledPluginEntry
    {
        public string Scope = string.Empty;
        public string InstallPath = string.Empty;
        public string Version = string.Empty;
        public string GitCommitSha = string.Empty;
    }

    /// <summary>The resolved answer: a state plus whatever detail the state's source carried.</summary>
    public sealed class UnityPluginStatus
    {
        public UnityPluginState State;

        /// <summary>True when the answer took a session's system/init into account (so EnabledNotLoaded really means "not loaded").</summary>
        public bool SessionObserved;

        /// <summary>Version from the init entry when Loaded, else from the install record; empty when unknown.</summary>
        public string Version = string.Empty;

        /// <summary>Path from the init entry when Loaded, else installPath from the install record; empty when unknown.</summary>
        public string InstallPath = string.Empty;

        /// <summary>plugin_errors[].message when LoadError; empty otherwise.</summary>
        public string ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Pure detection for Unity's official plugin (design note section 1.3).
    /// Every environment fact arrives as a string or a parsed list; nothing
    /// here reads a file, so the state table (section 1.2) is unit tested
    /// with hand-built inputs. <see cref="UnityPluginProbe"/> is the thin
    /// production wrapper that supplies the real disk contents.
    /// </summary>
    public static class UnityPluginDetector
    {
        // -- static source 1: installed_plugins.json ------------------------------

        /// <summary>
        /// Parses `installed_plugins.json` and returns this plugin's record,
        /// preferring the user-scope entry (the only scope the panel
        /// installs into) over any other. Null for missing/malformed text or
        /// when the plugin has no record. Never throws.
        /// </summary>
        public static InstalledPluginEntry ParseInstalledPlugins(string installedPluginsJsonText)
        {
            if (string.IsNullOrEmpty(installedPluginsJsonText))
            {
                return null;
            }
            JsonNode root;
            string error;
            if (!JsonParser.TryParse(installedPluginsJsonText, out root, out error) || !root.IsObject)
            {
                return null;
            }
            JsonNode entries = root["plugins"][UnityPluginIdentity.QualifiedName];
            if (!entries.IsArray)
            {
                return null;
            }
            InstalledPluginEntry first = null;
            foreach (JsonNode entry in entries.Items)
            {
                if (!entry.IsObject)
                {
                    continue;
                }
                var parsed = new InstalledPluginEntry
                {
                    Scope = entry["scope"].AsString(string.Empty),
                    InstallPath = entry["installPath"].AsString(string.Empty),
                    Version = entry["version"].AsString(string.Empty),
                    GitCommitSha = entry["gitCommitSha"].AsString(string.Empty)
                };
                if (string.Equals(parsed.Scope, "user", StringComparison.Ordinal))
                {
                    return parsed;
                }
                if (first == null)
                {
                    first = parsed;
                }
            }
            return first;
        }

        // -- static source 2: settings.json enabledPlugins -----------------------

        /// <summary>
        /// True when the user-scope settings.json enables this plugin under
        /// either key form the CLI documents (`unity@unity-agent-plugin`,
        /// the one actually written on 2026-09-10, or bare `unity`). An
        /// explicit false is false; a missing key, malformed JSON or empty
        /// text is false. Never throws.
        /// </summary>
        public static bool IsEnabledInSettingsJson(string settingsJsonText)
        {
            if (string.IsNullOrEmpty(settingsJsonText))
            {
                return false;
            }
            JsonNode root;
            string error;
            if (!JsonParser.TryParse(settingsJsonText, out root, out error) || !root.IsObject)
            {
                return false;
            }
            JsonNode enabled = root["enabledPlugins"];
            if (!enabled.IsObject)
            {
                return false;
            }
            return IsTrueBool(enabled[UnityPluginIdentity.QualifiedName])
                || IsTrueBool(enabled[UnityPluginIdentity.PluginName]);
        }

        /// <summary>Strictly the JSON boolean true -- the CLI writes a boolean, and JsonNode.AsBool would also accept the string "true".</summary>
        private static bool IsTrueBool(JsonNode node)
        {
            return node != null && node.IsBool && node.AsBool(false);
        }

        // -- resolution -----------------------------------------------------------

        /// <summary>
        /// The state table of design note section 1.2. <paramref name="initPlugins"/>
        /// null means "no session observed" (pre-connect); an empty list
        /// means a session was observed and loaded nothing.
        /// </summary>
        public static UnityPluginStatus Resolve(bool cliAvailable, InstalledPluginEntry installed,
            bool enabledInSettings, IList<PluginEntry> initPlugins, IList<PluginError> initErrors)
        {
            var status = new UnityPluginStatus { SessionObserved = initPlugins != null };
            if (installed != null)
            {
                status.Version = installed.Version ?? string.Empty;
                status.InstallPath = installed.InstallPath ?? string.Empty;
            }
            if (!cliAvailable)
            {
                status.State = UnityPluginState.CliUnavailable;
                return status;
            }

            if (initErrors != null)
            {
                for (int i = 0; i < initErrors.Count; i++)
                {
                    PluginError e = initErrors[i];
                    if (e != null && UnityPluginIdentity.Matches(e.Plugin, null))
                    {
                        status.State = UnityPluginState.LoadError;
                        status.ErrorMessage = e.Message ?? string.Empty;
                        return status;
                    }
                }
            }
            if (initPlugins != null)
            {
                for (int i = 0; i < initPlugins.Count; i++)
                {
                    PluginEntry p = initPlugins[i];
                    if (p != null && UnityPluginIdentity.Matches(p.Name, p.Source))
                    {
                        status.State = UnityPluginState.Loaded;
                        if (!string.IsNullOrEmpty(p.Version))
                        {
                            status.Version = p.Version;
                        }
                        if (!string.IsNullOrEmpty(p.Path))
                        {
                            status.InstallPath = p.Path;
                        }
                        return status;
                    }
                }
            }

            if (installed == null)
            {
                status.State = UnityPluginState.NotInstalled;
            }
            else if (!enabledInSettings)
            {
                status.State = UnityPluginState.Disabled;
            }
            else
            {
                status.State = UnityPluginState.EnabledNotLoaded;
            }
            return status;
        }
    }
}
