using System;
using System.Net;
using System.Net.Sockets;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// What uap_web_fetch may reach and how much it may pull (design note
    /// docs/design-notes/2026-09-17-web-fetch-tool.md section 5.2). Pure
    /// functions so the whole allow/deny surface is unit-tested without a
    /// network: only http/https, never a loopback / private / link-local /
    /// multicast destination (the panel's own UapOps server listens on
    /// 127.0.0.1, and a cloud metadata endpoint lives on 169.254.169.254),
    /// a hard byte cap and a hard time cap. The fetcher re-checks the
    /// destination on EVERY redirect hop, so a public host cannot bounce
    /// the request into the local network.
    /// </summary>
    public static class UapWebFetchPolicy
    {
        /// <summary>Largest body accepted (Content-Length or streamed), 20 MiB.</summary>
        public const long MaxBytes = 20L * 1024L * 1024L;

        /// <summary>Wall-clock budget for connect + read of one hop.</summary>
        public const int TimeoutMillis = 20000;

        /// <summary>Redirect hops followed before giving up.</summary>
        public const int MaxRedirects = 5;

        /// <summary>Default and hard maximum of the text window a call returns.</summary>
        public const int DefaultMaxChars = 60000;
        public const int MaxMaxChars = 400000;

        /// <summary>
        /// Largest image handed to the model WITHOUT re-encoding (GIF / WebP
        /// the editor cannot decode, or a PNG/JPEG when the main-thread
        /// encoder is unavailable). Same cap as a composer attachment.
        /// </summary>
        public const long PassthroughImageMaxBytes = 5L * 1024L * 1024L;

        public const string UserAgent = "AgentPanelForUnity";

        /// <summary>
        /// Parses and vets the URL argument: absolute, http or https, has a
        /// host, carries no user-info (a "user:pass@host" URL is the classic
        /// way to smuggle credentials or disguise the real host).
        /// </summary>
        public static bool TryParseUrl(string url, out Uri uri, out string error)
        {
            uri = null;
            error = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                error = "url is required.";
                return false;
            }
            Uri parsed;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed))
            {
                error = "url is not an absolute URL: " + url;
                return false;
            }
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                error = "Only http and https URLs can be fetched (got scheme \"" + parsed.Scheme + "\").";
                return false;
            }
            if (string.IsNullOrEmpty(parsed.Host))
            {
                error = "url has no host: " + url;
                return false;
            }
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                error = "URLs with embedded credentials (user:password@host) are not fetched.";
                return false;
            }
            if (IsBlockedHostName(parsed.Host))
            {
                error = DescribeBlocked(parsed.Host);
                return false;
            }
            uri = parsed;
            return true;
        }

        /// <summary>
        /// Host names that are local by definition, refused before any DNS
        /// lookup. IP literals are handled by <see cref="IsBlockedAddress"/>.
        /// </summary>
        public static bool IsBlockedHostName(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return true;
            }
            string h = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal))
            {
                return true;
            }
            if (h.EndsWith(".local", StringComparison.Ordinal) || h.EndsWith(".internal", StringComparison.Ordinal))
            {
                return true;
            }
            IPAddress literal;
            if (IPAddress.TryParse(h.Trim('[', ']'), out literal))
            {
                return IsBlockedAddress(literal);
            }
            return false;
        }

        /// <summary>
        /// Addresses a fetch must never be sent to: loopback, RFC 1918
        /// private, carrier-grade NAT (100.64/10), link-local (which holds
        /// the cloud metadata endpoint), multicast, broadcast, unspecified,
        /// and the IPv6 equivalents (::1, fc00::/7, fe80::/10, ff00::/8,
        /// site-local). IPv4-mapped IPv6 is unwrapped and judged as IPv4.
        /// </summary>
        public static bool IsBlockedAddress(IPAddress address)
        {
            if (address == null)
            {
                return true;
            }
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                if (b[0] == 0 || b[0] == 10 || b[0] == 127)
                {
                    return true;
                }
                if (b[0] == 169 && b[1] == 254)
                {
                    return true;
                }
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                {
                    return true;
                }
                if (b[0] == 192 && b[1] == 168)
                {
                    return true;
                }
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                {
                    return true;
                }
                if (b[0] >= 224)
                {
                    return true;
                }
                return false;
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
                {
                    return true;
                }
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                {
                    return true;
                }
                byte[] b = address.GetAddressBytes();
                // fc00::/7 unique local.
                if ((b[0] & 0xFE) == 0xFC)
                {
                    return true;
                }
                return false;
            }
            return true;
        }

        /// <summary>Message for a refused destination, quoted back to the agent.</summary>
        public static string DescribeBlocked(string hostOrAddress)
        {
            return "Refusing to fetch " + hostOrAddress + ": local, private and link-local destinations are"
                + " not reachable through uap_web_fetch (only public http/https hosts are).";
        }

        /// <summary>
        /// Resolves the redirect target of a hop: absolute, or relative to
        /// the hop's URL. Returns false for an unusable Location header.
        /// </summary>
        public static bool TryResolveRedirect(Uri current, string location, out Uri target)
        {
            target = null;
            if (current == null || string.IsNullOrWhiteSpace(location))
            {
                return false;
            }
            Uri resolved;
            if (!Uri.TryCreate(current, location.Trim(), out resolved))
            {
                return false;
            }
            target = resolved;
            return true;
        }
    }
}
