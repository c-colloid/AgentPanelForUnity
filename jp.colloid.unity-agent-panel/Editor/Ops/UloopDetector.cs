using System;
using System.IO;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Detects whether the uLoop / unity-cli-loop package is present in
    /// this project (design section 7.2's capability matrix precondition:
    /// "detect uLoop, then never register UapOps tools it already
    /// covers"). Package-presence detection via a plain string scan of
    /// Packages/manifest.json -- pure and testable without touching
    /// PackageManager.Client (design line: "package presence via Client.List
    /// cache or Packages/manifest.json string scan, pure-testable").
    /// </summary>
    public static class UloopDetector
    {
        /// <summary>Known package identifiers across uLoopMCP's history and its unity-cli-loop rename (design section 8.4).</summary>
        private static readonly string[] KnownPackageIds =
        {
            "io.github.hatayama.uloopmcp",
            "io.github.hatayama.unitycliloop"
        };

        /// <summary>
        /// The same ids, for callers that need to ACT on the one a project
        /// actually has rather than ask a yes/no question -- the uninstall
        /// path has to name the id it removes, and removing the one that is
        /// not there would silently do nothing. Single-sourced here so a
        /// third id can never be taught to the detector and forgotten by
        /// the remover. A copy, so a caller cannot edit the live array.
        /// </summary>
        public static string[] KnownPackageIdList()
        {
            return (string[])KnownPackageIds.Clone();
        }

        /// <summary>
        /// Pure: true when <paramref name="manifestJsonText"/> declares any
        /// known uLoop package id as a dependency KEY -- that is, the quoted
        /// id followed by a colon, which is the one place in manifest.json
        /// where a package id means "this package is a dependency".
        ///
        /// The colon is not decoration. This used to match the quoted id
        /// ANYWHERE in the file, with a doc comment asserting that the quotes
        /// alone were "good enough to avoid false positives". MEASURED
        /// 2026-08-04 by running the one-click install end to end for the
        /// first time: they are not, and the false positive is self-inflicted.
        /// UloopInstaller writes the very same quoted id into
        /// scopedRegistries[].scopes, so the instant the install began -- and
        /// permanently afterwards, even if OpenUPM resolution never
        /// succeeded -- this method reported uLoop present in a project that
        /// did not have it. A scopes entry is `"&lt;id&gt;"` followed by a
        /// comma, newline or `]`; a dependency is `"&lt;id&gt;":`. Only the
        /// latter counts.
        ///
        /// Whitespace between the closing quote and the colon is legal JSON
        /// (`"id" : "2.2.0"`) and is skipped, so the check does not trade one
        /// wrong answer for another.
        /// </summary>
        public static bool IsPresentInManifest(string manifestJsonText)
        {
            if (string.IsNullOrEmpty(manifestJsonText))
            {
                return false;
            }
            for (int i = 0; i < KnownPackageIds.Length; i++)
            {
                if (DeclaresDependencyKey(manifestJsonText, KnownPackageIds[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="text"/> contains `"id"` followed (after
        /// any whitespace) by a colon. Scans every occurrence rather than
        /// just the first, so an id appearing in a scopes array before its
        /// real dependency entry does not hide the dependency.
        /// </summary>
        private static bool DeclaresDependencyKey(string text, string packageId)
        {
            string quoted = "\"" + packageId + "\"";
            int at = text.IndexOf(quoted, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                int after = at + quoted.Length;
                while (after < text.Length && char.IsWhiteSpace(text[after]))
                {
                    after++;
                }
                if (after < text.Length && text[after] == ':')
                {
                    return true;
                }
                at = text.IndexOf(quoted, at + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>
        /// Real-project convenience: reads Packages/manifest.json under
        /// <paramref name="projectRoot"/> and scans it. False (never
        /// throws) when the file is missing or unreadable.
        /// </summary>
        public static bool DetectInProject(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return false;
            }
            string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return false;
                }
                return IsPresentInManifest(File.ReadAllText(manifestPath));
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
