using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Installed packages that no loaded Extension Profile names.
    ///
    /// The Extension Profile mechanism had a schema, a trust model and, as
    /// of the authoring tools, a way to write one -- and still no answer to
    /// the question a user actually has: which of the things I have
    /// installed is the agent flying blind about? This is that answer, and
    /// the Settings card turns it into a request the agent can act on.
    ///
    /// Known limit, stated rather than papered over: a profile that detects
    /// only by typeNames (an Asset Store SDK with no package id, like Final
    /// IK or Bakery) covers nothing here, so an installed package could be
    /// reported as uncovered while such a profile already handles it. In
    /// practice those SDKs have no package id to be listed in the first
    /// place, so the overlap is small -- but this is a hint, not a verdict.
    ///
    /// Pure C#: the caller supplies the package listing and the profiles.
    /// </summary>
    public static class ExtensionProfileGapFinder
    {
        /// <summary>Packages named in the Settings hint before it is elided. A hint is read at a glance, not scrolled.</summary>
        public const int DefaultMaxResults = 6;

        /// <summary>
        /// Package ids with no profile, most interesting first.
        /// <paramref name="totalUncovered"/> is the count BEFORE the cap,
        /// so the caller can say how many were not shown.
        ///
        /// Unity's own first-party packages are sorted last rather than
        /// dropped: Timeline, Cinemachine and Animation Rigging are
        /// perfectly reasonable profile subjects, they are just never the
        /// ones a user is looking for in a list of forty.
        /// </summary>
        public static List<string> FindUncovered(IEnumerable<string> installedPackageIds,
            IEnumerable<ExtensionProfile> profiles, int maxResults, out int totalUncovered)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            if (profiles != null)
            {
                foreach (ExtensionProfile profile in profiles)
                {
                    if (profile == null || profile.PackageIds == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < profile.PackageIds.Count; i++)
                    {
                        string id = Normalize(profile.PackageIds[i]);
                        if (id != null)
                        {
                            covered.Add(id);
                        }
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var uncovered = new List<string>();
            if (installedPackageIds != null)
            {
                foreach (string raw in installedPackageIds)
                {
                    string id = Normalize(raw);
                    if (id == null || IsOwnPackage(id) || covered.Contains(id) || !seen.Add(id))
                    {
                        continue;
                    }
                    uncovered.Add(id);
                }
            }
            uncovered.Sort(CompareInterestingFirst);
            totalUncovered = uncovered.Count;

            int cap = maxResults > 0 ? maxResults : DefaultMaxResults;
            if (uncovered.Count > cap)
            {
                uncovered.RemoveRange(cap, uncovered.Count - cap);
            }
            return uncovered;
        }

        /// <summary>
        /// Trims a Library/PackageCache directory name down to its id: that
        /// listing spells a resolved package "&lt;id&gt;@&lt;version&gt;"
        /// (or "@&lt;hash&gt;" for a git dependency), while manifest.json
        /// and the Packages/ listing use the bare id.
        /// </summary>
        public static string Normalize(string packageIdOrDirectory)
        {
            if (string.IsNullOrEmpty(packageIdOrDirectory))
            {
                return null;
            }
            string value = packageIdOrDirectory.Trim();
            int at = value.IndexOf('@');
            if (at > 0)
            {
                value = value.Substring(0, at);
            }
            return value.Length == 0 ? null : value;
        }

        /// <summary>The panel's own packages are never a profile subject.</summary>
        private static bool IsOwnPackage(string id)
        {
            return id.StartsWith("jp.colloid.unity-agent-panel", StringComparison.Ordinal)
                || id.StartsWith("jp.colloid.agent-panel-pro", StringComparison.Ordinal);
        }

        private static int CompareInterestingFirst(string a, string b)
        {
            bool aUnity = a.StartsWith("com.unity.", StringComparison.Ordinal);
            bool bUnity = b.StartsWith("com.unity.", StringComparison.Ordinal);
            if (aUnity != bUnity)
            {
                return aUnity ? 1 : -1;
            }
            return string.CompareOrdinal(a, b);
        }
    }
}
