using System;
using System.IO;
using System.Net;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Production <see cref="IUapWebFetcher"/> over HttpWebRequest (present
    /// in every Editor scripting profile, no extra assembly). Follows
    /// redirects BY HAND so every hop's destination goes through
    /// <see cref="UapWebFetchPolicy"/> -- automatic redirects would let a
    /// public host 302 the request into 127.0.0.1 or the LAN. Sends only a
    /// User-Agent and Accept header: no cookies, no Authorization, nothing
    /// the agent could steer. Streams the body with a hard byte cap so a
    /// missing or lying Content-Length cannot pull an unbounded response
    /// into memory. Runs on the UapOps HTTP worker thread (the tool is
    /// IUapOffThreadTool), so a slow server never stalls the Editor.
    /// </summary>
    public sealed class UapHttpWebFetcher : IUapWebFetcher
    {
        private readonly string _userAgent;
        private readonly Func<UapWebHostRules> _rules;

        public UapHttpWebFetcher(string unityVersion)
            : this(unityVersion, null)
        {
        }

        /// <param name="rules">The user's host lists, re-read per hop so a redirect cannot leave the allowed set; null = allow all.</param>
        public UapHttpWebFetcher(string unityVersion, Func<UapWebHostRules> rules)
        {
            _userAgent = UapWebFetchPolicy.UserAgent + " (Unity " + (string.IsNullOrEmpty(unityVersion) ? "dev" : unityVersion) + ")";
            _rules = rules;
        }

        public UapWebFetchResponse Fetch(Uri url)
        {
            if (url == null)
            {
                throw new ArgumentNullException("url");
            }
            Uri first = url;
            Uri current = url;
            for (int hop = 0; hop <= UapWebFetchPolicy.MaxRedirects; hop++)
            {
                CheckDestination(current);
                HttpWebResponse response = Send(current);
                try
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status <= 399)
                    {
                        string location = response.Headers[HttpResponseHeader.Location];
                        Uri next;
                        if (!UapWebFetchPolicy.TryResolveRedirect(current, location, out next))
                        {
                            throw new UapWebFetchException("HTTP " + status + " from " + current
                                + " without a usable Location header.");
                        }
                        if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        {
                            throw new UapWebFetchException("Redirect from " + current + " to a non-http URL ("
                                + next.Scheme + ":) was not followed.");
                        }
                        if (hop == UapWebFetchPolicy.MaxRedirects)
                        {
                            throw new UapWebFetchException("Too many redirects (more than "
                                + UapWebFetchPolicy.MaxRedirects + ") starting from " + first + ".");
                        }
                        current = next;
                        continue;
                    }
                    if (status < 200 || status > 299)
                    {
                        throw new UapWebFetchException("HTTP " + status + " " + response.StatusDescription
                            + " for " + current + ".");
                    }
                    long declared = response.ContentLength;
                    if (declared > UapWebFetchPolicy.MaxBytes)
                    {
                        throw new UapWebFetchException("The response is " + FormatBytes(declared) + ", over the "
                            + FormatBytes(UapWebFetchPolicy.MaxBytes) + " limit; not downloaded.");
                    }
                    byte[] body = ReadCapped(response, current);
                    return new UapWebFetchResponse
                    {
                        StatusCode = status,
                        ContentType = response.ContentType,
                        FinalUrl = current,
                        RedirectedFrom = current == first ? null : first,
                        Body = body
                    };
                }
                finally
                {
                    response.Close();
                }
            }
            throw new UapWebFetchException("Too many redirects starting from " + first + ".");
        }

        private void CheckDestination(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new UapWebFetchException("Only http and https URLs can be fetched: " + uri);
            }
            if (UapWebFetchPolicy.IsBlockedHostName(uri.Host))
            {
                throw new UapWebFetchException(UapWebFetchPolicy.DescribeBlocked(uri.Host));
            }
            UapWebHostRules rules = _rules == null ? null : _rules();
            string rulesReason;
            if (rules != null && !rules.IsAllowed(uri.Host, out rulesReason))
            {
                throw new UapWebFetchException(rulesReason);
            }
            IPAddress literal;
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out literal))
            {
                if (UapWebFetchPolicy.IsBlockedAddress(literal))
                {
                    throw new UapWebFetchException(UapWebFetchPolicy.DescribeBlocked(uri.Host));
                }
                return;
            }
            // Known limit: HttpWebRequest resolves the name again when it
            // connects, so a host whose record flips to a private address
            // between the two lookups (TTL-0 "DNS rebinding") can slip
            // through. Pinning the connection to the vetted address would
            // need connecting by IP with a Host header, which breaks TLS
            // certificate matching on https. The panel's own UapOps server
            // still needs its bearer token, so a rebinding to loopback
            // cannot drive the editor; the residual exposure is other
            // unauthenticated LAN services. Design note section 5.2.
            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(uri.Host);
            }
            catch (Exception ex)
            {
                throw new UapWebFetchException("Could not resolve host " + uri.Host + ": " + ex.Message, ex);
            }
            if (addresses == null || addresses.Length == 0)
            {
                throw new UapWebFetchException("Host " + uri.Host + " has no address.");
            }
            foreach (IPAddress address in addresses)
            {
                if (UapWebFetchPolicy.IsBlockedAddress(address))
                {
                    throw new UapWebFetchException(UapWebFetchPolicy.DescribeBlocked(uri.Host + " (" + address + ")"));
                }
            }
        }

        private HttpWebResponse Send(Uri uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = UapWebFetchPolicy.TimeoutMillis;
            request.ReadWriteTimeout = UapWebFetchPolicy.TimeoutMillis;
            request.UserAgent = _userAgent;
            request.Accept = "*/*";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.CookieContainer = null;
            request.Credentials = null;
            request.UseDefaultCredentials = false;
            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    // 3xx/4xx/5xx surface as WebException; the caller reads
                    // the status itself.
                    return response;
                }
                throw new UapWebFetchException("Fetching " + uri + " failed: " + ex.Message, ex);
            }
        }

        private static byte[] ReadCapped(HttpWebResponse response, Uri uri)
        {
            using (Stream stream = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                if (stream == null)
                {
                    return new byte[0];
                }
                var chunk = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > UapWebFetchPolicy.MaxBytes)
                    {
                        throw new UapWebFetchException("The response from " + uri + " exceeds the "
                            + FormatBytes(UapWebFetchPolicy.MaxBytes) + " limit; download aborted.");
                    }
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            }
            if (bytes >= 1024L)
            {
                return (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            }
            return bytes + " bytes";
        }
    }
}
