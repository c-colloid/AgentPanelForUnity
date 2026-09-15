using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Thin production wrapper around the pure <see cref="ExtensionProfileDetector"/>
    /// (design section 3b/C2 "cache per domain-load"): reads
    /// Packages/manifest.json, the Library/PackageCache directory listing
    /// and the Packages/ directory listing ONCE per domain-load (plain
    /// static fields, so they reset naturally on the next domain reload
    /// -- no explicit invalidation
    /// needed for the common case), then memoizes each profile's detected
    /// result by an opaque caller-supplied key (distinct per bundled
    /// profile id and per user-profile content hash, so an approved user
    /// profile that gets edited -- and therefore gets a new hash -- is
    /// never confused with its previous version's cached result).
    /// </summary>
    internal static class ExtensionProfileDetectionCache
    {
        private static readonly IUapTypeExistenceResolver TypeResolver = new TypeCacheTypeExistenceResolver();

        private static Dictionary<string, bool> _resultCache;
        private static bool _environmentLoaded;
        private static string _manifestJsonText;
        private static List<string> _packageDirectoryNames;

        public static bool IsDetected(string cacheKey, ExtensionProfile profile, string projectRoot)
        {
            EnsureEnvironmentLoaded(projectRoot);
            if (_resultCache == null)
            {
                _resultCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            }
            bool cached;
            if (!string.IsNullOrEmpty(cacheKey) && _resultCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }
            bool detected = ExtensionProfileDetector.IsDetected(
                profile, _manifestJsonText, _packageDirectoryNames, TypeResolver);
            if (!string.IsNullOrEmpty(cacheKey))
            {
                _resultCache[cacheKey] = detected;
            }
            return detected;
        }

        private static void EnsureEnvironmentLoaded(string projectRoot)
        {
            if (_environmentLoaded)
            {
                return;
            }
            _manifestJsonText = SafeReadManifest(projectRoot);
            _packageDirectoryNames = SafeReadPackageDirectoryNames(projectRoot);
            _environmentLoaded = true;
        }

        private static string SafeReadManifest(string projectRoot)
        {
            try
            {
                string path = Path.Combine(projectRoot ?? ".", "Packages", "manifest.json");
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Directory names under BOTH Library/PackageCache (resolved
        /// registry/git packages) and Packages/ (embedded packages). The
        /// Packages/ half is what makes package-id detection work at all
        /// for anything installed by VCC/ALCOM through VPM -- the VRChat
        /// SDK, NDMF, Modular Avatar and the rest of that ecosystem are
        /// copied into Packages/&lt;id&gt;/ and tracked in
        /// Packages/vpm-manifest.json, so they appear neither in
        /// manifest.json's dependencies nor in Library/PackageCache.
        /// </summary>
        private static List<string> SafeReadPackageDirectoryNames(string projectRoot)
        {
            var result = new List<string>();
            string root = projectRoot ?? ".";
            AppendDirectoryNames(result, Path.Combine(root, "Library", "PackageCache"));
            AppendDirectoryNames(result, Path.Combine(root, "Packages"));
            return result;
        }

        private static void AppendDirectoryNames(List<string> into, string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    string[] dirs = Directory.GetDirectories(path);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        into.Add(Path.GetFileName(dirs[i]));
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort: an unreadable directory just means package-id
                // detection falls back to the manifest.json half (still
                // checked above), the other directory, and the typeNames half.
            }
        }

        /// <summary>Test seam: drops the cached environment snapshot and per-profile results so a test can re-run detection against a different fake environment within the same domain.</summary>
        internal static void ResetForTests()
        {
            _resultCache = null;
            _environmentLoaded = false;
            _manifestJsonText = null;
            _packageDirectoryNames = null;
        }
    }
}
