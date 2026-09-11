using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>One parsed user-supplied profile plus the raw-byte identity design section 8.2 B3 pins approval to.</summary>
    public sealed class UserExtensionProfileEntry
    {
        public ExtensionProfile Profile;
        public string FilePath;

        /// <summary>Lowercase hex SHA-256 of the file's raw bytes (see <see cref="UserExtensionProfileStore.ComputeContentHash"/>) -- ANY byte change (including whitespace/re-save) yields a different hash, which is exactly the "any change invalidates" behavior B3 requires.</summary>
        public string ContentHashHex;
    }

    /// <summary>
    /// Loads user/third-party-supplied Extension Profiles from
    /// `&lt;projectRoot&gt;/.uap-profiles/*.json` (design section 8.2 B3):
    /// same JSON schema as the bundled profiles
    /// (<see cref="ExtensionProfile.Parse(string,out string)"/>), but NEVER
    /// auto-trusted -- a profile from here is only injected once its exact
    /// content hash appears in
    /// <see cref="Colloid.AgentPanel.Model.PanelSettings.approvedProfileHashes"/>
    /// (see <see cref="ExtensionProfileTrust"/>).
    /// </summary>
    public static class UserExtensionProfileStore
    {
        /// <summary>Default sidecar directory relative to the project root.</summary>
        public const string DefaultRelativeDirectory = ".uap-profiles";

        public static string ResolveDirectory(string projectRoot)
        {
            return Path.Combine(projectRoot ?? ".", DefaultRelativeDirectory);
        }

        public static List<UserExtensionProfileEntry> Load(string projectRoot, Action<string> log = null)
        {
            return LoadFromDirectory(ResolveDirectory(projectRoot), log);
        }

        /// <summary>Test/production seam: parses every *.json file directly under <paramref name="directory"/> (non-recursive), in deterministic (ordinal filename) order. Unreadable/unparsable files are skipped and logged; a missing directory yields an empty list. Never throws.</summary>
        public static List<UserExtensionProfileEntry> LoadFromDirectory(string directory, Action<string> log = null)
        {
            var result = new List<UserExtensionProfileEntry>();
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
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(file);
                }
                catch (Exception ex)
                {
                    Log(log, "Failed to read '" + file + "': " + ex.Message);
                    continue;
                }
                string json;
                try
                {
                    json = Encoding.UTF8.GetString(bytes);
                }
                catch (Exception ex)
                {
                    Log(log, "Failed to decode '" + file + "' as UTF-8: " + ex.Message);
                    continue;
                }
                string error;
                ExtensionProfile profile = ExtensionProfile.Parse(json, out error);
                if (profile == null)
                {
                    Log(log, "Failed to parse '" + file + "': " + error);
                    continue;
                }
                result.Add(new UserExtensionProfileEntry
                {
                    Profile = profile,
                    FilePath = file,
                    ContentHashHex = ComputeContentHash(bytes)
                });
            }
            return result;
        }

        /// <summary>Lowercase hex SHA-256 of <paramref name="bytes"/>. Pure/deterministic -- the same bytes always yield the same hash, and any byte difference (including a trailing-newline or encoding change) yields a different one.</summary>
        public static string ComputeContentHash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[UserExtensionProfileStore] " + message);
            }
        }
    }
}
