using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Thin production wrapper around the pure <see cref="ExtensionProfileDetector"/>
    /// (design section 3b/C2 "cache per domain-load"): reads
    /// Packages/manifest.json and the Library/PackageCache directory
    /// listing ONCE per domain-load (plain static fields, so they reset
    /// naturally on the next domain reload -- no explicit invalidation
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
        private static List<string> _packageCacheDirectoryNames;

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
                profile, _manifestJsonText, _packageCacheDirectoryNames, TypeResolver);
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
            _packageCacheDirectoryNames = SafeReadPackageCacheDirectoryNames(projectRoot);
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

        private static List<string> SafeReadPackageCacheDirectoryNames(string projectRoot)
        {
            var result = new List<string>();
            try
            {
                string path = Path.Combine(projectRoot ?? ".", "Library", "PackageCache");
                if (Directory.Exists(path))
                {
                    string[] dirs = Directory.GetDirectories(path);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        result.Add(Path.GetFileName(dirs[i]));
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort: an unreadable Library/PackageCache just means
                // package-id detection falls back to the manifest.json half
                // alone (still checked above) plus the typeNames half.
            }
            return result;
        }

        /// <summary>Test seam: drops the cached environment snapshot and per-profile results so a test can re-run detection against a different fake environment within the same domain.</summary>
        internal static void ResetForTests()
        {
            _resultCache = null;
            _environmentLoaded = false;
            _manifestJsonText = null;
            _packageCacheDirectoryNames = null;
        }
    }
}
