using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The manifest.json helpers the uLoop REMOVAL path (UloopUninstaller)
    /// and the Pro update-registry writer (ProRegistryAccess) share: locating
    /// a scoped registry by URL, a stable 2-space pretty-printer, line-ending
    /// matching and the small file-IO seams.
    ///
    /// <para>Until 2026-10-02 this was UloopInstaller, which also planned and
    /// applied a one-click uLoop INSTALL. That button is gone
    /// (docs/design-notes/2026-10-02-uloop-card-final.md): the panel's own
    /// uap_* tools cover what uLoop did, so it no longer offers to put the
    /// package into a project. Only the parts the remaining callers use were
    /// kept; the install planner, the apply routine and their network probe
    /// went with it.</para>
    /// </summary>
    public static class UloopManifestSupport
    {
        /// <summary>
        /// The uLoop package id, MEASURED verbatim from a real project's
        /// "dependencies" key (UloopDetector also knows the renamed
        /// unity-cli-loop id; this is the one that has actually shipped).
        /// </summary>
        public const string UloopPackageId = "io.github.hatayama.uloopmcp";

        /// <summary>
        /// The OpenUPM scoped registry's "url" field, MEASURED in a real
        /// project's manifest.json and packages-lock.json (both agree).
        /// </summary>
        public const string OpenUpmRegistryUrl = "https://package.openupm.com";

        /// <summary>Backup filename suffix ("manifest.json.uap-backup").</summary>
        public const string BackupFileSuffix = ".uap-backup";

        // Caveat codes. The UI maps each to localized text
        // (SettingsView.DescribeUloopCaveat); never invent a code without the
        // matching UI string.
        public const string CaveatCodeVccProject = "vcc-project";
        public const string CaveatCodeManifestUnreadable = "manifest-unreadable";

        /// <summary>
        /// Finds the "scopedRegistries" entry, if any, whose "url" refers to the same
        /// registry as <paramref name="targetUrl"/> -- returns null when none does.
        /// The URL is the ONLY key (normalized by <see cref="NormalizeRegistryHost"/>):
        /// the registry's "name" is a user-chosen display label with no bearing on
        /// which host it points at, and matching on it produced a silent duplicate
        /// entry for a project that had OpenUPM under another name, and a false block
        /// for a trailing-slash URL (2026-08-04 measurement).
        /// </summary>
        internal static JsonNode FindMatchingScopedRegistry(JsonNode root, string targetUrl)
        {
            JsonNode registries = root["scopedRegistries"];
            if (!registries.IsArray)
            {
                return null;
            }
            foreach (JsonNode entry in registries.Items)
            {
                if (!entry.IsObject)
                {
                    continue;
                }
                if (UrlsMatchAsRegistry(entry["url"].AsString(string.Empty), targetUrl))
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// The scopedRegistries entry, if any, that is NOT
        /// <paramref name="ours"/> and already lists <see cref="UloopPackageId"/>
        /// among its scopes -- a company mirror, a VPM feed, or any other registry
        /// the project has deliberately pointed this package at.
        ///
        /// The removal path must not touch such an entry: it belongs to whoever
        /// pointed the project at it, not to the OpenUPM stanza this panel tidies.
        /// </summary>
        internal static JsonNode FindForeignRegistryClaimingOurScope(JsonNode root, JsonNode ours)
        {
            JsonNode registries = root["scopedRegistries"];
            if (!registries.IsArray)
            {
                return null;
            }
            foreach (JsonNode entry in registries.Items)
            {
                if (!entry.IsObject || ReferenceEquals(entry, ours))
                {
                    continue;
                }
                if (ScopesContain(entry["scopes"], UloopPackageId))
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// True when <paramref name="a"/> and <paramref name="b"/> identify the same
        /// scoped registry host once a single trailing slash is trimmed from each and
        /// the remaining scheme+host is compared case-insensitively -- e.g.
        /// "https://package.openupm.com" and "https://package.openupm.com/" match;
        /// "https://package.openupm.com" and "https://PACKAGE.OPENUPM.COM" match;
        /// "https://package.openupm.com" and "https://example.invalid" do not.
        /// </summary>
        private static bool UrlsMatchAsRegistry(string a, string b)
        {
            return string.Equals(NormalizeRegistryHost(a), NormalizeRegistryHost(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reduces a registry url to "scheme://host" for comparison, trimming exactly
        /// one trailing slash first (both OpenUPM's own docs and at least one real
        /// project manifest measured during this stream write the trailing-slash form,
        /// so this is not a hypothetical). A url that fails to parse (rare -- would mean
        /// the manifest's "url" field is not actually a URL at all) falls back to the
        /// trimmed literal string rather than throwing: a malformed url should compare
        /// as "not equal to anything sensible", never crash the caller.
        /// </summary>
        private static string NormalizeRegistryHost(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }
            string trimmed = url.Trim();
            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '/')
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }
            Uri parsed;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out parsed))
            {
                return parsed.Scheme + "://" + parsed.Host;
            }
            return trimmed;
        }

        /// <summary>
        /// Rewrites <paramref name="text"/>'s line endings to CRLF when
        /// <paramref name="original"/> predominantly used them.
        ///
        /// PrettyPrintManifest always emits LF. On Windows -- this project's
        /// platform, with git core.autocrlf commonly on -- that silently
        /// rewrites EVERY line of a CRLF manifest, so `git status` reports the
        /// whole file changed for what the confirmation card presented as a
        /// three-line insert. The card normalized both sides before diffing,
        /// so it could not show this; the honest fix is to stop causing it
        /// rather than to disclose it, since nobody wants the line-ending
        /// churn either way.
        /// </summary>
        internal static string MatchLineEndings(string text, string original)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(original))
            {
                return text;
            }
            int crlf = 0;
            int bareLf = 0;
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != '\n')
                {
                    continue;
                }
                if (i > 0 && original[i - 1] == '\r') { crlf++; } else { bareLf++; }
            }
            if (crlf <= bareLf)
            {
                return text;
            }
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>True when <paramref name="scopesArray"/> (a JSON array node) already contains <paramref name="value"/> as a string element.</summary>
        internal static bool ScopesContain(JsonNode scopesArray, string value)
        {
            string[] scopes = scopesArray.AsStringArray();
            for (int i = 0; i < scopes.Length; i++)
            {
                if (string.Equals(scopes[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // -- manifest.json IO helpers ---------------------------------------------

        internal static bool TryReadManifestText(string manifestPath, out string manifestText)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    manifestText = null;
                    return false;
                }
                manifestText = File.ReadAllText(manifestPath);
                return true;
            }
            catch (Exception)
            {
                manifestText = null;
                return false;
            }
        }

        /// <summary>
        /// Where a VCC/VPM-managed project declares itself. One method so
        /// the uninstall path (UloopUninstaller) raises the same caveat
        /// against the same file rather than re-deriving the convention --
        /// a second copy of this path is exactly how the two halves would
        /// drift into warning about different things.
        /// </summary>
        internal static string VpmManifestPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Packages", "vpm-manifest.json");
        }

        internal static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static UloopCaveat NewCaveat(string code, bool blocking, string detail)
        {
            return new UloopCaveat { Code = code, Blocking = blocking, Detail = detail ?? string.Empty };
        }

        // -- Manifest pretty-printer -----------------------------------------------
        //
        // JsonWriter.Write (Colloid.AgentPanel.Core.Json) only ever emits a SINGLE
        // LINE of compact JSON -- correct for this repo's other JSON use (CLI stdin
        // payloads, the session cache file, which nobody hand-edits) but wrong for
        // manifest.json, a file the user reads and hand-edits themselves (design
        // section 2.5's own text: manifest.json is the user's asset). Re-emitting the
        // WHOLE file through JsonWriter.Write would collapse it to one unreadable
        // line -- the opposite of "preserve the user's existing formatting as far as
        // the JSON writer allows" (task spec).
        //
        // This 2-space indenter is the compromise the task spec asks for explicitly:
        // it walks the JsonNode DOM (built by the SAME JsonParser/JsonNode this whole
        // codebase uses -- "Core.Json only for JSON" is honored) and re-serializes
        // every value using JsonWriter.Write/EscapeString for the actual scalar
        // escaping (so string/number/bool/null formatting is Core.Json's, not
        // reinvented here) while handling only indentation and newlines locally. What
        // this does NOT preserve, stated plainly per the task's requirement:
        // - Key ORDER for pre-existing keys mirrors JsonNode.Properties' enumeration
        //   order, which mirrors the underlying Dictionary's insertion order in
        //   practice (every current .NET/Mono runtime preserves insertion order for a
        //   dictionary that only ever has keys ADDED, never removed -- which is all
        //   JsonParser ever does while building this tree) -- but this is an
        //   implementation detail of Dictionary<TKey,TValue>, not a language
        //   guarantee, exactly like every other JsonNode consumer in this codebase
        //   already implicitly relies on (e.g. SessionCacheFile's WriteSession).
        // - A NEWLY added "scopedRegistries" array is always APPENDED at the end of
        //   the top-level object, even though Unity's own generated manifest.json
        //   conventionally lists "scopedRegistries" before "dependencies" -- cosmetic
        //   only, semantically identical JSON. A newly added SCOPE inside an existing
        //   registry entry's "scopes" array is appended at the end of that array for
        //   the same reason.
        // - Blank lines, inline comments (JSON has none, and JsonParser does not
        //   tolerate them anyway -- a manifest.json containing any would already be
        //   reported as unreadable/malformed before reaching this code), unusual
        //   indent widths, or trailing whitespace in the ORIGINAL file are not
        //   preserved; every value is re-indented at a fixed 2 spaces per level (the
        //   width Unity itself writes when it rewrites this file).
        // - Line endings are always "\n", regardless of what the original file used.
        // Nothing is ever DROPPED: every original key/value survives the round trip
        // through the DOM (JsonNode holds the entire document), only its exact textual
        // LAYOUT can change.
        // ---------------------------------------------------------------------------

        internal static string PrettyPrintManifest(JsonNode root)
        {
            var sb = new StringBuilder(1024);
            WritePretty(root, sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WritePretty(JsonNode node, StringBuilder sb, int indent)
        {
            JsonNode safeNode = node ?? JsonNode.Null;
            switch (safeNode.Type)
            {
                case JsonNodeType.Object:
                    WritePrettyObject(safeNode, sb, indent);
                    break;
                case JsonNodeType.Array:
                    WritePrettyArray(safeNode, sb, indent);
                    break;
                default:
                    sb.Append(JsonWriter.Write(safeNode));
                    break;
            }
        }

        private static void WritePrettyObject(JsonNode node, StringBuilder sb, int indent)
        {
            if (node.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            sb.Append("{\n");
            bool first = true;
            foreach (KeyValuePair<string, JsonNode> pair in node.Properties)
            {
                if (!first)
                {
                    sb.Append(",\n");
                }
                first = false;
                AppendIndent(sb, indent + 1);
                sb.Append('"').Append(JsonWriter.EscapeString(pair.Key)).Append("\": ");
                WritePretty(pair.Value, sb, indent + 1);
            }
            sb.Append('\n');
            AppendIndent(sb, indent);
            sb.Append('}');
        }

        private static void WritePrettyArray(JsonNode node, StringBuilder sb, int indent)
        {
            if (node.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append("[\n");
            bool first = true;
            foreach (JsonNode item in node.Items)
            {
                if (!first)
                {
                    sb.Append(",\n");
                }
                first = false;
                AppendIndent(sb, indent + 1);
                WritePretty(item, sb, indent + 1);
            }
            sb.Append('\n');
            AppendIndent(sb, indent);
            sb.Append(']');
        }

        private static void AppendIndent(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++)
            {
                sb.Append("  ");
            }
        }
    }
}
