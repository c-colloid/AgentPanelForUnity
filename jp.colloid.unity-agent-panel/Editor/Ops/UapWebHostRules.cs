using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The user's host allow / deny lists for uap_web_fetch (design note
    /// 2026-09-17-web-fetch-tool.md section 5.2, stage 2), on top of the
    /// built-in private-address policy which these can never loosen. An
    /// entry names a host ("example.com", or "*.example.com" -- both mean
    /// the host itself and every subdomain; case-insensitive; blank lines
    /// and "#" comments ignored). Deny wins over allow; an empty allow list
    /// means "any public host". Immutable and pure: the tool reads one
    /// snapshot per call from the worker thread while the settings UI
    /// replaces it on the main thread.
    /// </summary>
    public sealed class UapWebHostRules
    {
        public static readonly UapWebHostRules AllowAll = new UapWebHostRules(null, null);

        private readonly string[] _allowed;
        private readonly string[] _blocked;

        public UapWebHostRules(IEnumerable<string> allowed, IEnumerable<string> blocked)
        {
            _allowed = Normalize(allowed);
            _blocked = Normalize(blocked);
        }

        /// <summary>Entries after trimming, lower-casing, wildcard stripping and comment removal.</summary>
        public IList<string> Allowed { get { return _allowed; } }
        public IList<string> Blocked { get { return _blocked; } }

        public bool HasAllowList { get { return _allowed.Length > 0; } }

        /// <summary>
        /// True when <paramref name="host"/> may be fetched under these
        /// lists; otherwise false with the sentence the agent gets.
        /// </summary>
        public bool IsAllowed(string host, out string reason)
        {
            reason = null;
            string h = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            for (int i = 0; i < _blocked.Length; i++)
            {
                if (Matches(h, _blocked[i]))
                {
                    reason = "Refusing to fetch " + host + ": it is on the blocked-hosts list in the panel's"
                        + " Web fetch settings (\"" + _blocked[i] + "\").";
                    return false;
                }
            }
            if (_allowed.Length == 0)
            {
                return true;
            }
            for (int i = 0; i < _allowed.Length; i++)
            {
                if (Matches(h, _allowed[i]))
                {
                    return true;
                }
            }
            reason = "Refusing to fetch " + host + ": the panel's Web fetch settings only allow "
                + DescribeList(_allowed) + ".";
            return false;
        }

        /// <summary>Host equals the entry or is a subdomain of it.</summary>
        public static bool Matches(string host, string entry)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(entry))
            {
                return false;
            }
            return host == entry || host.EndsWith("." + entry, StringComparison.Ordinal);
        }

        /// <summary>One entry per line as typed in the settings field: trims, lower-cases, drops "*." / scheme / path, skips blanks and "#" comments.</summary>
        public static string[] Normalize(IEnumerable<string> entries)
        {
            var list = new List<string>();
            if (entries == null)
            {
                return list.ToArray();
            }
            foreach (string raw in entries)
            {
                if (raw == null)
                {
                    continue;
                }
                string e = raw.Trim();
                int hash = e.IndexOf('#');
                if (hash >= 0)
                {
                    e = e.Substring(0, hash).Trim();
                }
                if (e.Length == 0)
                {
                    continue;
                }
                int scheme = e.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0)
                {
                    e = e.Substring(scheme + 3);
                }
                int slash = e.IndexOf('/');
                if (slash >= 0)
                {
                    e = e.Substring(0, slash);
                }
                if (e.StartsWith("*.", StringComparison.Ordinal))
                {
                    e = e.Substring(2);
                }
                e = e.TrimEnd('.').ToLowerInvariant();
                if (e.Length > 0 && !list.Contains(e))
                {
                    list.Add(e);
                }
            }
            return list.ToArray();
        }

        private static string DescribeList(string[] entries)
        {
            if (entries.Length <= 3)
            {
                return string.Join(", ", entries);
            }
            return string.Join(", ", entries, 0, 3) + " and " + (entries.Length - 3) + " more";
        }
    }
}
