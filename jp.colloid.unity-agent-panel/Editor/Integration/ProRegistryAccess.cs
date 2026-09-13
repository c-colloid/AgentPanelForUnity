using System;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Why an apply attempt failed, for the settings card to word
    /// (<see cref="ProRegistryApplyResult"/>).
    /// </summary>
    public enum ProRegistryApplyError
    {
        None,
        EmptyKey,
        InvalidUrl,
        /// <summary>Another scopedRegistries entry already lists the Pro package id.</summary>
        ForeignRegistry,
        /// <summary>manifest.json could not be read or parsed.</summary>
        ManifestUnreadable,
        /// <summary>A write to .upmconfig.toml or manifest.json failed.</summary>
        WriteFailed
    }

    /// <summary>Outcome of <see cref="ProRegistryAccess.Apply"/>.</summary>
    public sealed class ProRegistryApplyResult
    {
        public bool Success;
        public ProRegistryApplyError Error;
        /// <summary>Free-form detail for the error (registry name, exception text).</summary>
        public string Detail = string.Empty;
        /// <summary>Path of the user config the token was written to.</summary>
        public string UpmConfigPath = string.Empty;
        /// <summary>True when manifest.json changed (false when the registry stanza was already there).</summary>
        public bool ManifestChanged;
    }

    /// <summary>
    /// Wires a project up to the Agent Panel Pro update registry (design note
    /// docs/design-notes/2026-09-12-pro-update-delivery.md section 3.3): the
    /// purchaser pastes the product key once, and this class writes the two
    /// files Unity's Package Manager reads --
    /// <list type="bullet">
    /// <item><c>~/.upmconfig.toml</c>: an <c>[npmAuth."&lt;registry&gt;"]</c>
    /// block with the token (Unity's own credential store for scoped
    /// registries; the panel keeps no copy of the key, same policy as API
    /// keys).</item>
    /// <item><c>Packages/manifest.json</c>: a scopedRegistries entry naming the
    /// registry and the Pro package id, so "Update" in the Package Manager
    /// pulls new Pro versions from it.</item>
    /// </list>
    /// The registry URL is not a secret and lives in settings
    /// (<c>PanelSettings.proRegistryUrl</c>); only the key is.
    /// </summary>
    public static class ProRegistryAccess
    {
        public const string ProPackageId = "jp.colloid.agent-panel-pro";
        public const string RegistryDisplayName = "Agent Panel Pro";

        /// <summary>
        /// Registry origin (with the <c>/npm</c> path the Worker serves)
        /// pre-filled in the settings card. Empty until the production
        /// domain is chosen (design note section 4, Phase 1 follow-up);
        /// purchasers can always type the URL that came with the key.
        /// </summary>
        public const string DefaultRegistryUrl = "";

        /// <summary>Unity honours this env var as the .upmconfig.toml directory.</summary>
        internal const string UpmUserConfigDirEnvVar = "UPM_USER_CONFIG_DIR";

        // -- Pure text rules (unit-tested) ----------------------------------

        /// <summary>
        /// Accepts an https URL (http only for localhost, for a local
        /// wrangler dev run), trims whitespace and trailing slashes, and
        /// rejects anything with a query, fragment or credentials -- the same
        /// string is written to both files, and Unity matches the
        /// .upmconfig.toml key against the manifest URL byte for byte.
        /// </summary>
        internal static bool TryNormalizeRegistryUrl(string input, out string normalized)
        {
            normalized = string.Empty;
            string trimmed = (input ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }
            Uri uri;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                return false;
            }
            bool isLocal = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host == "127.0.0.1";
            if (uri.Scheme != "https" && !(uri.Scheme == "http" && isLocal))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }
            normalized = trimmed.TrimEnd('/');
            return normalized.Length > 0;
        }

        /// <summary>
        /// A product key is a single token: printable ASCII, no whitespace.
        /// (Issued as <c>apu_pk_...</c> / <c>apu_pt_...</c> by the registry.)
        /// </summary>
        internal static bool IsPlausibleKey(string key)
        {
            string k = (key ?? string.Empty).Trim();
            if (k.Length < 8 || k.Length > 256)
            {
                return false;
            }
            foreach (char c in k)
            {
                if (c <= ' ' || c > '~' || c == '"' || c == '\\')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns <paramref name="existingToml"/> with exactly one
        /// <c>[npmAuth."registryUrl"]</c> block holding <paramref name="token"/>:
        /// an existing block for the same URL is replaced in place (up to the
        /// next table header), everything else is left byte-for-byte, and a
        /// missing block is appended. The token is written as a TOML basic
        /// string (quote and backslash escaped).
        /// </summary>
        internal static string UpsertUpmConfig(string existingToml, string registryUrl, string token)
        {
            string header = "[npmAuth.\"" + registryUrl + "\"]";
            string block = header + "\n"
                + "token = \"" + EscapeTomlBasicString(token) + "\"\n"
                + "alwaysAuth = true\n";

            string text = existingToml ?? string.Empty;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Length == 0
                ? new string[0]
                : text.Replace("\r\n", "\n").Split('\n');

            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == header)
                {
                    start = i;
                    break;
                }
            }

            var sb = new StringBuilder(text.Length + block.Length + 2);
            if (start < 0)
            {
                sb.Append(text);
                if (text.Length > 0 && !text.EndsWith("\n"))
                {
                    sb.Append(newline);
                }
                if (text.Length > 0)
                {
                    sb.Append(newline);
                }
                sb.Append(block.Replace("\n", newline));
                return sb.ToString();
            }

            int end = lines.Length;
            for (int i = start + 1; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    end = i;
                    break;
                }
            }
            // Keep a blank line before the next table if there was one.
            while (end - 1 > start && lines[end - 1].Trim().Length == 0)
            {
                end--;
            }
            for (int i = 0; i < start; i++)
            {
                sb.Append(lines[i]).Append(newline);
            }
            sb.Append(block.Replace("\n", newline));
            for (int i = end; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1)
                {
                    sb.Append(newline);
                }
            }
            return sb.ToString();
        }

        internal static string EscapeTomlBasicString(string value)
        {
            var sb = new StringBuilder(value.Length + 4);
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns the manifest text with a scopedRegistries entry for
        /// <paramref name="registryUrl"/> scoping <see cref="ProPackageId"/>:
        /// an entry already at that URL (trailing-slash / case-insensitive
        /// host match, via UloopInstaller's rule) just gains the scope, a
        /// missing one is appended. Fails without touching the text when a
        /// DIFFERENT registry already claims the Pro scope -- two registries
        /// for one package is a resolution the user must make, not this
        /// class. Formatting follows UloopInstaller's manifest printer and
        /// keeps the file's line endings.
        /// </summary>
        internal static bool TryUpsertManifest(string manifestText, string registryUrl,
            out string newText, out ProRegistryApplyError error, out string detail)
        {
            newText = manifestText;
            error = ProRegistryApplyError.None;
            detail = string.Empty;

            JsonNode root;
            string parseError;
            if (!JsonParser.TryParse(manifestText ?? string.Empty, out root, out parseError) || !root.IsObject)
            {
                error = ProRegistryApplyError.ManifestUnreadable;
                detail = parseError ?? "not a JSON object";
                return false;
            }

            JsonNode ours = UloopInstaller.FindMatchingScopedRegistry(root, registryUrl);
            JsonNode registries = root["scopedRegistries"];
            if (registries.IsArray)
            {
                foreach (JsonNode entry in registries.Items)
                {
                    if (!entry.IsObject || ReferenceEquals(entry, ours))
                    {
                        continue;
                    }
                    if (UloopInstaller.ScopesContain(entry["scopes"], ProPackageId))
                    {
                        error = ProRegistryApplyError.ForeignRegistry;
                        detail = entry["name"].AsString(entry["url"].AsString(string.Empty));
                        return false;
                    }
                }
            }

            bool changed;
            if (ours == null)
            {
                if (!registries.IsArray)
                {
                    registries = JsonNode.NewArray();
                    root.Set("scopedRegistries", registries);
                }
                registries.Add(JsonNode.NewObject()
                    .Set("name", RegistryDisplayName)
                    .Set("url", registryUrl)
                    .Set("scopes", JsonNode.NewArray().Add(ProPackageId)));
                changed = true;
            }
            else
            {
                JsonNode scopes = ours["scopes"];
                if (!scopes.IsArray)
                {
                    scopes = JsonNode.NewArray();
                    ours.Set("scopes", scopes);
                }
                changed = !UloopInstaller.ScopesContain(scopes, ProPackageId);
                if (changed)
                {
                    scopes.Add(ProPackageId);
                }
            }

            if (!changed)
            {
                return true;
            }
            newText = UloopInstaller.MatchLineEndings(UloopInstaller.PrettyPrintManifest(root), manifestText);
            return true;
        }

        // -- Paths ------------------------------------------------------------

        /// <summary>
        /// Unity reads <c>.upmconfig.toml</c> from <c>UPM_USER_CONFIG_DIR</c>
        /// when set, else the user's home directory (%USERPROFILE% / $HOME).
        /// </summary>
        internal static string UpmConfigPath(Func<string, string> getEnv, string homeDirectory)
        {
            string dir = getEnv != null ? getEnv(UpmUserConfigDirEnvVar) : null;
            if (string.IsNullOrEmpty(dir))
            {
                dir = homeDirectory;
            }
            return Path.Combine(dir ?? string.Empty, ".upmconfig.toml");
        }

        public static string UpmConfigPath()
        {
            return UpmConfigPath(Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        public static string ManifestPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Packages", "manifest.json"));
        }

        // -- Apply ------------------------------------------------------------

        /// <summary>Writes both files for the running project, then asks the Package Manager to resolve.</summary>
        public static ProRegistryApplyResult Apply(string registryUrl, string key)
        {
            ProRegistryApplyResult result = ApplyWithSeams(registryUrl, key, UpmConfigPath(), ManifestPath(),
                ReadOrEmpty, AtomicFile.WriteAllTextOrThrow);
            if (result.Success)
            {
                try
                {
                    Client.Resolve();
                }
                catch (Exception ex)
                {
                    // The files are written; a failed resolve just means the
                    // user opens the Package Manager themselves.
                    Debug.LogWarning("[AgentPanel] Package Manager resolve after saving the Pro key failed: " + ex.Message);
                }
            }
            return result;
        }

        internal static ProRegistryApplyResult ApplyWithSeams(string registryUrl, string key,
            string upmConfigPath, string manifestPath,
            Func<string, string> readText, Action<string, string> writeText)
        {
            var result = new ProRegistryApplyResult { UpmConfigPath = upmConfigPath };
            string url;
            if (!TryNormalizeRegistryUrl(registryUrl, out url))
            {
                result.Error = ProRegistryApplyError.InvalidUrl;
                return result;
            }
            if (!IsPlausibleKey(key))
            {
                result.Error = ProRegistryApplyError.EmptyKey;
                return result;
            }
            string token = key.Trim();

            string manifestBefore;
            try
            {
                manifestBefore = readText(manifestPath);
            }
            catch (Exception ex)
            {
                result.Error = ProRegistryApplyError.ManifestUnreadable;
                result.Detail = ex.Message;
                return result;
            }
            string manifestAfter;
            ProRegistryApplyError manifestError;
            string detail;
            if (!TryUpsertManifest(manifestBefore, url, out manifestAfter, out manifestError, out detail))
            {
                result.Error = manifestError;
                result.Detail = detail;
                return result;
            }

            try
            {
                string tomlBefore;
                try
                {
                    tomlBefore = readText(upmConfigPath);
                }
                catch (FileNotFoundException)
                {
                    tomlBefore = string.Empty;
                }
                catch (DirectoryNotFoundException)
                {
                    tomlBefore = string.Empty;
                }
                writeText(upmConfigPath, UpsertUpmConfig(tomlBefore, url, token));
                if (!string.Equals(manifestAfter, manifestBefore, StringComparison.Ordinal))
                {
                    writeText(manifestPath, manifestAfter);
                    result.ManifestChanged = true;
                }
            }
            catch (Exception ex)
            {
                result.Error = ProRegistryApplyError.WriteFailed;
                result.Detail = ex.Message;
                return result;
            }
            result.Success = true;
            return result;
        }

        private static string ReadOrEmpty(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(path);
            }
            return File.ReadAllText(path);
        }
    }
}
