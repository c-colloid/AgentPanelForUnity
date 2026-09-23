using System;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>How an update check ended (docs/design-notes/2026-09-23-cli-update-and-pro-version.md).</summary>
    public enum CliUpdateCheckOutcome
    {
        /// <summary>The registry could not be reached or answered something unreadable.</summary>
        Failed,
        /// <summary>The installed version is the latest one (or newer, e.g. a pre-release).</summary>
        UpToDate,
        /// <summary>The registry's latest version is newer than the installed one.</summary>
        UpdateAvailable,
        /// <summary>The latest version is known but the installed one is not (no version probe yet).</summary>
        InstalledUnknown
    }

    /// <summary>
    /// Asks the npm registry for the newest Claude Code release and compares
    /// it with the installed CLI's version (design note
    /// docs/design-notes/2026-09-23-cli-update-and-pro-version.md). The CLI
    /// has no "check only" command -- `claude update` checks AND installs --
    /// so the check reads the same dist-tag the CLI's default update channel
    /// follows. Runs only when the user presses the button; the panel never
    /// phones home on its own.
    ///
    /// The fetch runs on a ThreadPool worker and its callback is marshaled
    /// back through <see cref="AuthCli"/>'s main-thread pump, the same way
    /// every other one-shot helper in this folder reports back.
    /// </summary>
    public static class CliUpdateCheck
    {
        /// <summary>dist-tags of the npm package the native installer and `claude update` both track.</summary>
        public const string DistTagsUrl = "https://registry.npmjs.org/-/package/@anthropic-ai/claude-code/dist-tags";

        private const int TimeoutMillis = 15000;

        // -- Pure seams (unit-tested) -------------------------------------------

        /// <summary>
        /// The leading "MAJOR.MINOR.PATCH[-pre]" token of a version line
        /// ("2.1.218 (Claude Code)" -> "2.1.218"), or empty when the text
        /// does not start with one. Never throws.
        /// </summary>
        public static string ExtractSemver(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(1);
            }
            int end = 0;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '('
                && trimmed[end] != '+')
            {
                end++;
            }
            string token = trimmed.Substring(0, end);
            int[] ignored;
            string ignoredPre;
            return TryParse(token, out ignored, out ignoredPre) ? token : string.Empty;
        }

        /// <summary>
        /// SemVer precedence of two version strings: negative when
        /// <paramref name="a"/> is older, positive when newer, 0 when equal.
        /// A release outranks its own pre-releases; pre-release labels are
        /// compared as plain ordinal strings (enough to order "beta.2" before
        /// "beta.3"). Unparsable input compares as 0 -- "no evidence of an
        /// update" is the safe answer.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            int[] va;
            int[] vb;
            string pa;
            string pb;
            if (!TryParse(ExtractSemver(a), out va, out pa) || !TryParse(ExtractSemver(b), out vb, out pb))
            {
                return 0;
            }
            for (int i = 0; i < 3; i++)
            {
                if (va[i] != vb[i])
                {
                    return va[i] < vb[i] ? -1 : 1;
                }
            }
            if (pa.Length == 0 || pb.Length == 0)
            {
                return pa.Length == pb.Length ? 0 : (pa.Length == 0 ? 1 : -1);
            }
            return Math.Sign(string.CompareOrdinal(pa, pb));
        }

        /// <summary>The "latest" dist-tag of a dist-tags JSON body, or empty when absent/malformed.</summary>
        public static string ParseLatestFromDistTags(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return string.Empty;
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(json.Trim(), out node, out error) || node == null || !node.IsObject)
            {
                return string.Empty;
            }
            return ExtractSemver(node["latest"].AsString(string.Empty));
        }

        /// <summary>Classifies an installed version against the registry's latest.</summary>
        public static CliUpdateCheckOutcome Classify(string installedVersion, string latestVersion)
        {
            if (string.IsNullOrEmpty(ExtractSemver(latestVersion)))
            {
                return CliUpdateCheckOutcome.Failed;
            }
            if (string.IsNullOrEmpty(ExtractSemver(installedVersion)))
            {
                return CliUpdateCheckOutcome.InstalledUnknown;
            }
            return CompareVersions(installedVersion, latestVersion) < 0
                ? CliUpdateCheckOutcome.UpdateAvailable
                : CliUpdateCheckOutcome.UpToDate;
        }

        private static bool TryParse(string token, out int[] numbers, out string prerelease)
        {
            numbers = new int[3];
            prerelease = string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }
            string core = token;
            int dash = token.IndexOf('-');
            if (dash >= 0)
            {
                core = token.Substring(0, dash);
                prerelease = token.Substring(dash + 1);
                if (prerelease.Length == 0)
                {
                    return false;
                }
            }
            string[] parts = core.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }
            for (int i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0 || !int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // -- Network ---------------------------------------------------------------

        /// <summary>
        /// Fetches <see cref="DistTagsUrl"/> on a worker thread;
        /// <paramref name="onComplete"/> runs on the editor main thread with
        /// the latest version, or empty on any failure. Never throws.
        /// </summary>
        public static void FetchLatest(Action<string> onComplete)
        {
            if (onComplete == null)
            {
                return;
            }
            AuthCli.EnsurePumpHooked();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string latest;
                try
                {
                    latest = ParseLatestFromDistTags(FetchBlocking());
                }
                catch (Exception)
                {
                    latest = string.Empty;
                }
                string captured = latest;
                AuthCli.EnqueueCallback(delegate { onComplete(captured); });
            });
        }

        private static string FetchBlocking()
        {
            var http = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(DistTagsUrl);
            http.Method = "GET";
            http.Timeout = TimeoutMillis;
            http.ReadWriteTimeout = TimeoutMillis;
            http.Accept = "application/json";
            http.UserAgent = "AgentPanelForUnity";
            using (var response = (System.Net.HttpWebResponse)http.GetResponse())
            {
                int status = (int)response.StatusCode;
                if (status < 200 || status > 299)
                {
                    return string.Empty;
                }
                using (System.IO.Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return string.Empty;
                    }
                    using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
        }
    }
}
