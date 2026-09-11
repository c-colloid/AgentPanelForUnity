using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Shared asset-path validation for the asset-writing UapOps tools
    /// (OPS-3, SR-asset-path). The old per-tool checks were a bare
    /// StartsWith("Assets/"), which "Assets/../Evil.mat" walks straight
    /// past -- the string starts with Assets/ but resolves outside it.
    /// Dot segments are collapsed FIRST (the same pure logic
    /// ScriptGate's traversal guard has always used; the implementation
    /// moved here so both share one definition), then the collapsed
    /// result must still live under Assets/. Pure string logic (no Unity
    /// API -- this file is also compiled by the license-free smoke tier
    /// alongside ScriptGate), so the normalization table is directly
    /// testable in both tiers. The post-create existence check that pairs
    /// with this (OPS-3's second half) lives inline in each asset-creating
    /// tool: AssetDatabase is editor-only.
    /// </summary>
    public static class UapAssetPath
    {
        /// <summary>
        /// Collapses "." and ".." segments out of a '/'-separated path
        /// WITHOUT touching the filesystem or depending on the current
        /// working directory (unlike Path.GetFullPath) -- pure string
        /// logic, safe to run on a path from an untrusted tool_use
        /// request. A ".." with no preceding real segment to pop (already
        /// at the root, or another unresolved "..") is kept literally
        /// rather than throwing, matching common normpath semantics.
        /// Leading empty segments (a UNIX-style absolute path) collapse
        /// away like any other segment; that loses the leading '/', but
        /// every caller here only cares where the RESULT lands, not
        /// whether it is absolute. (Moved verbatim from ScriptGate, which
        /// now delegates here.)
        /// </summary>
        public static string CollapseDotSegments(string normalizedPath)
        {
            string[] parts = normalizedPath.Split('/');
            var stack = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }
                if (part == "..")
                {
                    if (stack.Count > 0 && stack[stack.Count - 1] != "..")
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        stack.Add(part);
                    }
                    continue;
                }
                stack.Add(part);
            }
            return string.Join("/", stack.ToArray());
        }

        /// <summary>
        /// Normalizes backslashes to '/', collapses dot segments, and
        /// requires the RESULT to sit under "Assets/" (case-insensitive
        /// match on the leading segment, preserving the caller's casing in
        /// the returned path -- AssetDatabase accepts it either way and
        /// the old per-tool checks were case-insensitive too). Returns the
        /// normalized path, or null with <paramref name="error"/> set.
        /// </summary>
        public static string NormalizeUnderAssets(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "'path' is required.";
                return null;
            }
            string normalized = CollapseDotSegments(path.Replace('\\', '/'));
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "'" + path + "' must resolve to a path under 'Assets/'"
                    + " (\".\"/\"..\" segments are collapsed before checking"
                    + (string.Equals(normalized, path, StringComparison.Ordinal)
                        ? string.Empty : "; it resolves to '" + normalized + "'")
                    + ").";
                return null;
            }
            return normalized;
        }
    }
}
