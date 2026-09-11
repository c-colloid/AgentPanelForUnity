using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Loads the BUNDLED Extension Profiles (design section 3b: "v1 is
    /// bundled-only auto-injection -- repository = reviewed", section 8.2
    /// B3) from the *.json files shipped alongside this class inside the
    /// package (Editor/Ops/Profiles/*.json).
    ///
    /// Package-path resolution: rather than depending on a Resources/
    /// folder or AssetDatabase (which would require the package to be
    /// imported as an Asset, and would not resolve the same way from a
    /// plain EditMode test process), <see cref="ResolveProfilesDirectory"/>
    /// uses a [CallerFilePath] sentinel captured for THIS SOURCE FILE at
    /// compile time. Whatever physical path Unity's Roslyn invocation
    /// compiled this file from -- an embedded package, a git package under
    /// Library/PackageCache, or (this repo's dev setup) a directory
    /// junction into a package developed outside the sandbox project --
    /// that is the same physical directory the bundled *.json files live
    /// in, since they sit right next to this .cs file on disk. Plain
    /// System.IO from there, so it also works unmodified from a bare
    /// EditMode test process with no AssetDatabase import pass.
    /// </summary>
    public static class ExtensionProfileCatalog
    {
        private static List<ExtensionProfile> _bundledCache;

        /// <summary>Absolute directory this class's own compiled source file lives in -- see the class doc comment.</summary>
        public static string ResolveProfilesDirectory()
        {
            return Path.GetDirectoryName(ThisFilePath());
        }

        private static string ThisFilePath([CallerFilePath] string path = null)
        {
            return path;
        }

        /// <summary>
        /// The bundled profile list, parsed once per domain-load and cached
        /// (design section 8.2's "cache per domain-load" -- a plain static
        /// field resets naturally on the next domain reload). Combines
        /// Core's own directory (the framework's code path; it now ships
        /// no *.json of its own -- see docs/design-notes/
        /// 2026-09-11-core-pro-split.md) with every add-on package's
        /// directory discovered through <see cref="IExtensionProfileProvider"/>
        /// (seam 2), deduplicated by profile id -- a later provider whose
        /// id collides with an already-loaded one is skipped and logged,
        /// never silently overwrites it.
        /// </summary>
        public static List<ExtensionProfile> LoadBundled(Action<string> log = null)
        {
            if (_bundledCache == null)
            {
                var result = new List<ExtensionProfile>();
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                MergeDirectory(result, seenIds, ResolveProfilesDirectory(), log);
                foreach (string providerDir in DiscoverProviderDirectories(log))
                {
                    MergeDirectory(result, seenIds, providerDir, log);
                }
                _bundledCache = result;
            }
            return _bundledCache;
        }

        private static void MergeDirectory(List<ExtensionProfile> into, HashSet<string> seenIds,
            string directory, Action<string> log)
        {
            foreach (ExtensionProfile profile in LoadFromDirectory(directory, log))
            {
                if (!seenIds.Add(profile.Id))
                {
                    Log(log, "Duplicate bundled profile id '" + profile.Id + "' from '" + directory
                        + "' -- keeping the first one loaded, this one is skipped.");
                    continue;
                }
                into.Add(profile);
            }
        }

        /// <summary>
        /// Every <see cref="IExtensionProfileProvider"/>'s
        /// <see cref="IExtensionProfileProvider.ProfilesDirectory"/> across
        /// loaded assemblies (non-abstract classes with a public
        /// parameterless constructor), sorted by full type name for
        /// deterministic load order. A provider that fails to construct or
        /// throws while resolving its directory is logged and skipped.
        /// </summary>
        private static List<string> DiscoverProviderDirectories(Action<string> log)
        {
            var result = new List<string>();
            List<Type> types;
            try
            {
                // Only PUBLIC classes are providers. Test stubs are private nested
                // classes inside test fixtures (e.g. ToolRegistryTests.
                // ThrowingToolProvider) and live in the same domain as this
                // code under the EditMode runner -- without this filter
                // TypeCache hands them to production CreateDefault, and a
                // stub that throws on purpose logs an error into every
                // unrelated test that touches UapOpsServer.Registry.
                types = TypeCache.GetTypesDerivedFrom<IExtensionProfileProvider>()
                    .Where(t => !t.IsAbstract && !t.IsInterface && (t.IsPublic || t.IsNestedPublic)
                        && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log(log, "Failed to enumerate IExtensionProfileProvider types via TypeCache: " + ex.Message);
                return result;
            }
            foreach (Type type in types)
            {
                try
                {
                    var provider = (IExtensionProfileProvider)Activator.CreateInstance(type);
                    if (!string.IsNullOrEmpty(provider.ProfilesDirectory))
                    {
                        result.Add(provider.ProfilesDirectory);
                    }
                }
                catch (Exception ex)
                {
                    Log(log, "Failed to instantiate profile provider '" + type.FullName + "'; skipped: " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Test/production seam: parses every *.json file directly under <paramref name="directory"/> (non-recursive), in deterministic (ordinal filename) order. Unreadable/unparsable files are skipped and logged; never throws.</summary>
        public static List<ExtensionProfile> LoadFromDirectory(string directory, Action<string> log = null)
        {
            var result = new List<ExtensionProfile>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return result;
            }
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.json");
            }
            catch (Exception ex)
            {
                Log(log, "Failed to list '" + directory + "': " + ex.Message);
                return result;
            }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string json;
                try
                {
                    json = File.ReadAllText(file, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Log(log, "Failed to read '" + file + "': " + ex.Message);
                    continue;
                }
                string error;
                ExtensionProfile profile = ExtensionProfile.Parse(json, out error);
                if (profile == null)
                {
                    Log(log, "Failed to parse '" + file + "': " + error);
                    continue;
                }
                result.Add(profile);
            }
            return result;
        }

        /// <summary>Test seam: clears the bundled-catalog cache so a test that mutates the on-disk files (or wants a fresh read) is not stuck with a stale first-call result within the same domain.</summary>
        internal static void ResetForTests()
        {
            _bundledCache = null;
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[ExtensionProfileCatalog] " + message);
            }
        }
    }
}
