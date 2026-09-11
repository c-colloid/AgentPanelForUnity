using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Pure filesystem scan over the script staging folder (design section
    /// 7.4/8.2 B1, staging location moved OUTSIDE Assets/ by section 8.7:
    /// `&lt;projectRoot&gt;/UapStaging/`). Plain System.IO -- no Unity
    /// dependency -- so it is unit tested directly against a real temp
    /// directory tree.
    /// </summary>
    public static class ScriptStagingScanner
    {
        /// <summary>
        /// Recursively lists `.cs`/`.asmdef` files under
        /// <paramref name="stagingAbsoluteRoot"/>, returned as paths
        /// RELATIVE to that root with forward slashes (e.g. "Foo/Bar.cs").
        /// Sorted for deterministic output. Empty (never null) when the
        /// root does not exist.
        /// </summary>
        public static List<string> ListStagedScriptFiles(string stagingAbsoluteRoot)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(stagingAbsoluteRoot) || !Directory.Exists(stagingAbsoluteRoot))
            {
                return result;
            }
            string rootFull = Path.GetFullPath(stagingAbsoluteRoot);
            foreach (string file in Directory.GetFiles(stagingAbsoluteRoot, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ext, ".asmdef", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string fileFull = Path.GetFullPath(file);
                string rel = fileFull.Length > rootFull.Length
                    ? fileFull.Substring(rootFull.Length)
                    : Path.GetFileName(fileFull);
                rel = rel.TrimStart('\\', '/').Replace('\\', '/');
                result.Add(rel);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Maps a staged-relative path to its Assets/ destination-relative
        /// path. Identity today (staging mirrors the Assets/ subtree
        /// 1:1) -- kept as its own function so a future remap rule has one
        /// seam to change.
        /// </summary>
        public static string ToAssetsRelativePath(string stagedRelativePath)
        {
            return stagedRelativePath;
        }
    }
}
