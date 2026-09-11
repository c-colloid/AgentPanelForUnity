using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>Resolver seam for the typeNames half of detection (design section 3b/C2) -- a real implementation backed by TypeCache lives in <see cref="TypeCacheTypeExistenceResolver"/>; tests fake this interface instead of touching TypeCache.</summary>
    public interface IUapTypeExistenceResolver
    {
        /// <summary>True when a compiled Component or ScriptableObject type matches <paramref name="typeName"/> exactly -- by FullName when it looks qualified (contains '.'), otherwise by short Name.</summary>
        bool TypeExists(string typeName);
    }

    /// <summary>
    /// Pure Extension Profile detection scanner (design section 3b, C2):
    /// a profile is DETECTED when either (a) one of its declared packageIds
    /// appears in the project's package manifest or package cache, or (b)
    /// one of its declared typeNames resolves to an actually-compiled type
    /// (the fallback that lets an Assets/-installed, package-id-less SDK
    /// like FinalIK be detected at all).
    ///
    /// Deliberately takes every environment fact as a plain string/
    /// interface parameter rather than reading Packages/manifest.json or
    /// TypeCache itself -- see <see cref="ExtensionProfileDetectionCache"/>
    /// for the thin production wrapper that supplies the real environment
    /// and caches the result per domain-load. This class stays 100% pure
    /// so the detection matrix (package hit / type hit / neither) is unit
    /// tested with hand-built strings, no live project state required.
    /// </summary>
    public static class ExtensionProfileDetector
    {
        public static bool IsDetected(ExtensionProfile profile, string manifestJsonText,
            IEnumerable<string> packageCacheDirectoryNames, IUapTypeExistenceResolver typeResolver)
        {
            if (profile == null)
            {
                return false;
            }
            if (profile.PackageIds != null)
            {
                for (int i = 0; i < profile.PackageIds.Count; i++)
                {
                    if (IsPackageIdPresent(profile.PackageIds[i], manifestJsonText, packageCacheDirectoryNames))
                    {
                        return true;
                    }
                }
            }
            if (typeResolver != null && profile.TypeNames != null)
            {
                for (int i = 0; i < profile.TypeNames.Count; i++)
                {
                    if (typeResolver.TypeExists(profile.TypeNames[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="packageId"/> is either a key under
        /// manifest.json's "dependencies" object, OR matches a
        /// Library/PackageCache directory name exactly or as a
        /// "&lt;id&gt;@..." prefix (the on-disk naming for a resolved
        /// registry/git package). Either input may be null/empty (treated
        /// as "nothing to check there").
        /// </summary>
        public static bool IsPackageIdPresent(string packageId, string manifestJsonText,
            IEnumerable<string> packageCacheDirectoryNames)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(manifestJsonText) && ManifestHasDependency(manifestJsonText, packageId))
            {
                return true;
            }
            if (packageCacheDirectoryNames != null)
            {
                foreach (string dir in packageCacheDirectoryNames)
                {
                    if (string.IsNullOrEmpty(dir))
                    {
                        continue;
                    }
                    if (string.Equals(dir, packageId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                    if (dir.StartsWith(packageId + "@", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ManifestHasDependency(string manifestJsonText, string packageId)
        {
            try
            {
                JsonNode root = JsonParser.Parse(manifestJsonText);
                if (root.IsObject)
                {
                    JsonNode deps = root["dependencies"];
                    if (deps.IsObject)
                    {
                        foreach (string key in deps.Keys)
                        {
                            if (string.Equals(key, packageId, StringComparison.Ordinal))
                            {
                                return true;
                            }
                        }
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                // Malformed/non-JSON text (a hand-crafted test string, or a
                // corrupted manifest on disk): fall through to the
                // resilient substring fallback below rather than treating
                // a parse failure as "never detected".
            }
            return manifestJsonText.IndexOf("\"" + packageId + "\"", StringComparison.Ordinal) >= 0;
        }
    }
}
