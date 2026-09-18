using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// uap_web_search: a web search for agents whose own toolset has none
    /// (design note 2026-09-17-web-fetch-tool.md section 8, stage 3).
    /// Talks to the provider the user picked in Settings > Web fetch with
    /// the user's API key (Brave Search API or Tavily); without a key the
    /// call fails with the sentence that says where to put one, so the
    /// tool is always listed but never silently useless. Returns title,
    /// URL and snippet per hit; the agent then opens a hit with
    /// uap_web_fetch. Runs off the main thread like the fetch tool, and
    /// is not read-only for the same reason: the query leaves the machine.
    /// </summary>
    public sealed class UapWebSearchTool : IUapOffThreadTool
    {
        /// <summary>Provider + key snapshot; replaced whole by UapOpsServer at start and SettingsView on edit. Never null.</summary>
        public static volatile UapWebSearchConfig Config = UapWebSearchConfig.None;

        private readonly IUapWebSearchClient _client;

        public UapWebSearchTool() : this(new UapHttpWebSearchClient())
        {
        }

        public UapWebSearchTool(IUapWebSearchClient client)
        {
            if (client == null)
            {
                throw new ArgumentNullException("client");
            }
            _client = client;
        }

        public string Name
        {
            get { return "uap_web_search"; }
        }

        public string Description
        {
            get
            {
                return "Searches the web and returns up to 20 hits (title, URL, snippet) through the search"
                    + " provider and API key configured in the panel's Web fetch settings (Brave Search or"
                    + " Tavily). Use it when you have no web search of your own; then open a hit with"
                    + " uap_web_fetch. Fails with instructions when no API key is configured.";
            }
        }

        public string Module
        {
            get { return "web"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("query", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Search words, as you would type them into a search engine."))
                        .Set("count", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Hits to return, 1-" + UapWebSearchProviders.MaxCount + " (default "
                                + UapWebSearchProviders.DefaultCount + ").")))
                    .Set("required", JsonNode.NewArray().Add("query"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string query = (input["query"].AsString(null) ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                throw new ArgumentException("query is required.");
            }
            if (query.Length > 400)
            {
                throw new ArgumentException("query is too long (max 400 characters).");
            }
            int count = input["count"].AsInt(UapWebSearchProviders.DefaultCount);
            if (count < 1 || count > UapWebSearchProviders.MaxCount)
            {
                throw new ArgumentException("count must be between 1 and " + UapWebSearchProviders.MaxCount + ".");
            }
            UapWebSearchConfig config = Config ?? UapWebSearchConfig.None;
            if (!config.HasKey)
            {
                throw new InvalidOperationException("No web search API key is configured. Ask the user to pick a provider"
                    + " (Brave Search API or Tavily) and paste its key under Settings > Unity operations (UapOps) > Web fetch;"
                    + " until then, use your own web search if you have one and uap_web_fetch to open URLs.");
            }
            UapWebSearchRequest request = UapWebSearchProviders.BuildRequest(config, query, count);
            string body;
            try
            {
                body = _client.Send(request);
            }
            catch (UapWebFetchException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            string parseError;
            List<UapWebSearchResult> results = UapWebSearchProviders.ParseResults(config.Provider, body, out parseError);
            if (parseError != null && results.Count == 0)
            {
                throw new InvalidOperationException("Search failed: " + parseError + ".");
            }
            return UapToolResults.Text(UapWebSearchProviders.Format(query, config.Provider, results));
        }
    }
}
