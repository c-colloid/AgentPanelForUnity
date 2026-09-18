using System;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Search back ends uap_web_search can talk to (design note 2026-09-17-web-fetch-tool.md section 8).</summary>
    public enum UapWebSearchProvider
    {
        Brave,
        Tavily
    }

    /// <summary>
    /// The user's search settings as one immutable snapshot the worker
    /// thread reads (Settings > Web fetch): which provider and its API key.
    /// Replaced whole by UapOpsServer at start and by SettingsView on edit.
    /// </summary>
    public sealed class UapWebSearchConfig
    {
        public static readonly UapWebSearchConfig None = new UapWebSearchConfig(UapWebSearchProvider.Brave, null);

        public readonly UapWebSearchProvider Provider;
        public readonly string ApiKey;

        public UapWebSearchConfig(UapWebSearchProvider provider, string apiKey)
        {
            Provider = provider;
            ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey.Trim();
        }

        public bool HasKey { get { return !string.IsNullOrEmpty(ApiKey); } }

        /// <summary>Settings string ("brave" / "tavily", case-insensitive; anything else = Brave) to the enum.</summary>
        public static UapWebSearchProvider ParseProvider(string id)
        {
            return string.Equals(id, "tavily", StringComparison.OrdinalIgnoreCase) ? UapWebSearchProvider.Tavily : UapWebSearchProvider.Brave;
        }

        public static string ProviderId(UapWebSearchProvider provider)
        {
            return provider == UapWebSearchProvider.Tavily ? "tavily" : "brave";
        }
    }

    /// <summary>One hit as the agent sees it.</summary>
    public sealed class UapWebSearchResult
    {
        public string Title = string.Empty;
        public string Url = string.Empty;
        public string Snippet = string.Empty;
    }

    /// <summary>The HTTP request a provider needs; built and parsed by pure functions so both sides are unit-tested without a network.</summary>
    public sealed class UapWebSearchRequest
    {
        public string Url;
        public string Method = "GET";
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        /// <summary>UTF-8 JSON body for POST providers; null for GET.</summary>
        public string Body;
        public string ContentType;
    }

    /// <summary>
    /// Request shapes and response parsers for the supported providers.
    /// Brave Search API: GET /res/v1/web/search?q=..&amp;count=.. with
    /// X-Subscription-Token; hits under web.results[] (title, url,
    /// description). Tavily: POST /search with a JSON body and a Bearer
    /// key; hits under results[] (title, url, content). Both return
    /// title / URL / snippet, which is all the agent needs before it
    /// follows a hit with uap_web_fetch.
    /// </summary>
    public static class UapWebSearchProviders
    {
        public const int DefaultCount = 8;
        public const int MaxCount = 20;
        public const string BraveEndpoint = "https://api.search.brave.com/res/v1/web/search";
        public const string TavilyEndpoint = "https://api.tavily.com/search";

        public static UapWebSearchRequest BuildRequest(UapWebSearchConfig config, string query, int count)
        {
            if (config == null || !config.HasKey)
            {
                throw new ArgumentException("no API key configured");
            }
            count = Math.Max(1, Math.Min(MaxCount, count));
            var request = new UapWebSearchRequest();
            if (config.Provider == UapWebSearchProvider.Tavily)
            {
                request.Url = TavilyEndpoint;
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + config.ApiKey));
                request.Body = JsonWriter.Write(JsonNode.NewObject()
                    .Set("query", query)
                    .Set("max_results", count)
                    .Set("include_answer", false)
                    .Set("include_raw_content", false));
                return request;
            }
            request.Url = BraveEndpoint + "?q=" + Uri.EscapeDataString(query) + "&count=" + count;
            request.Headers.Add(new KeyValuePair<string, string>("X-Subscription-Token", config.ApiKey));
            request.Headers.Add(new KeyValuePair<string, string>("Accept", "application/json"));
            return request;
        }

        /// <summary>Hits out of a provider's JSON; an unparsable or unexpected body yields an empty list with <paramref name="error"/> set.</summary>
        public static List<UapWebSearchResult> ParseResults(UapWebSearchProvider provider, string json, out string error)
        {
            error = null;
            var results = new List<UapWebSearchResult>();
            JsonNode root;
            string parseError;
            if (string.IsNullOrEmpty(json) || !JsonParser.TryParse(json, out root, out parseError))
            {
                error = "the search service returned something that is not JSON";
                return results;
            }
            JsonNode hits = provider == UapWebSearchProvider.Tavily ? root["results"] : root["web"]["results"];
            if (!hits.IsArray)
            {
                string message = root["message"].AsString(null) ?? root["error"].AsString(null) ?? root["detail"]["error"].AsString(null);
                error = message != null ? "the search service answered: " + message : "no results array in the response";
                return results;
            }
            for (int i = 0; i < hits.Count; i++)
            {
                JsonNode hit = hits[i];
                string url = hit["url"].AsString(null);
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }
                results.Add(new UapWebSearchResult
                {
                    Title = (hit["title"].AsString(null) ?? string.Empty).Trim(),
                    Url = url.Trim(),
                    Snippet = (hit[provider == UapWebSearchProvider.Tavily ? "content" : "description"].AsString(null) ?? string.Empty).Trim()
                });
            }
            return results;
        }

        /// <summary>The text block the agent gets: a numbered list of title, URL and snippet, plus how to go on.</summary>
        public static string Format(string query, UapWebSearchProvider provider, List<UapWebSearchResult> results)
        {
            var sb = new StringBuilder();
            sb.Append("Web search (").Append(UapWebSearchConfig.ProviderId(provider)).Append(") for \"").Append(query).Append("\": ")
              .Append(results.Count).Append(results.Count == 1 ? " result" : " results");
            if (results.Count == 0)
            {
                sb.Append(". Try different words.");
                return sb.ToString();
            }
            sb.Append(". Open a hit with uap_web_fetch to read the page, see an image or save a file.");
            for (int i = 0; i < results.Count; i++)
            {
                UapWebSearchResult r = results[i];
                sb.Append("\n\n").Append(i + 1).Append(". ").Append(r.Title.Length > 0 ? r.Title : "(untitled)");
                sb.Append("\n   ").Append(r.Url);
                if (r.Snippet.Length > 0)
                {
                    sb.Append("\n   ").Append(Truncate(UapHtmlText.Decode(StripTags(r.Snippet)), 400));
                }
            }
            return sb.ToString();
        }

        private static string StripTags(string s)
        {
            if (s.IndexOf('<') < 0)
            {
                return s;
            }
            var sb = new StringBuilder(s.Length);
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<')
                {
                    inTag = true;
                }
                else if (c == '>')
                {
                    inTag = false;
                }
                else if (!inTag)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }
    }

    /// <summary>The network seam of uap_web_search; tests substitute canned JSON.</summary>
    public interface IUapWebSearchClient
    {
        /// <summary>Sends the request and returns the response body; throws <see cref="UapWebFetchException"/> for a non-2xx status or a transport failure.</summary>
        string Send(UapWebSearchRequest request);
    }

    /// <summary>Production client over HttpWebRequest; fixed provider hosts, 20 s budget, no cookies.</summary>
    public sealed class UapHttpWebSearchClient : IUapWebSearchClient
    {
        public string Send(UapWebSearchRequest request)
        {
            var http = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(request.Url);
            http.Method = request.Method;
            http.Timeout = UapWebFetchPolicy.TimeoutMillis;
            http.ReadWriteTimeout = UapWebFetchPolicy.TimeoutMillis;
            http.UserAgent = UapWebFetchPolicy.UserAgent;
            http.AllowAutoRedirect = false;
            http.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                if (header.Key == "Accept")
                {
                    http.Accept = header.Value;
                }
                else
                {
                    http.Headers[header.Key] = header.Value;
                }
            }
            if (request.Body != null)
            {
                byte[] body = Encoding.UTF8.GetBytes(request.Body);
                http.ContentType = request.ContentType ?? "application/json";
                http.ContentLength = body.Length;
                using (System.IO.Stream s = http.GetRequestStream())
                {
                    s.Write(body, 0, body.Length);
                }
            }
            System.Net.HttpWebResponse response;
            try
            {
                response = (System.Net.HttpWebResponse)http.GetResponse();
            }
            catch (System.Net.WebException ex)
            {
                response = ex.Response as System.Net.HttpWebResponse;
                if (response == null)
                {
                    throw new UapWebFetchException("The search request failed: " + ex.Message, ex);
                }
            }
            using (response)
            {
                string text = ReadAll(response);
                int status = (int)response.StatusCode;
                if (status < 200 || status > 299)
                {
                    string hint = status == 401 || status == 403 ? " (check the API key in Settings > Web fetch)"
                        : status == 429 ? " (rate limit or quota of the search plan)" : string.Empty;
                    throw new UapWebFetchException("The search service answered HTTP " + status + hint
                        + (text.Length > 0 ? ": " + (text.Length > 300 ? text.Substring(0, 300) + "..." : text) : "."));
                }
                return text;
            }
        }

        private static string ReadAll(System.Net.HttpWebResponse response)
        {
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
