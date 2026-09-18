using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_web_search (design note 2026-09-17-web-fetch-tool.md section 8):
    /// the request each provider gets, how its JSON is read, the text the
    /// agent receives, and the tool's argument / key handling -- all with
    /// canned bodies, no network.
    /// </summary>
    [TestFixture]
    public class UapWebSearchTests
    {
        private sealed class FakeClient : IUapWebSearchClient
        {
            public string Body = "{}";
            public string Throw;
            public UapWebSearchRequest Last;

            public string Send(UapWebSearchRequest request)
            {
                Last = request;
                if (Throw != null)
                {
                    throw new UapWebFetchException(Throw);
                }
                return Body;
            }
        }

        private UapWebSearchConfig _saved;
        private FakeClient _client;

        [SetUp]
        public void SetUp()
        {
            _saved = UapWebSearchTool.Config;
            UapWebSearchTool.Config = new UapWebSearchConfig(UapWebSearchProvider.Brave, "brave-key");
            _client = new FakeClient();
        }

        [TearDown]
        public void TearDown()
        {
            UapWebSearchTool.Config = _saved ?? UapWebSearchConfig.None;
        }

        private static JsonNode Args(string query)
        {
            return JsonNode.NewObject().Set("query", query);
        }

        private const string BraveJson = "{\"type\":\"search\",\"web\":{\"results\":["
            + "{\"title\":\"Unity - Manual: Texture2D\",\"url\":\"https://docs.unity3d.com/Manual/class-TextureImporter.html\",\"description\":\"How to <strong>import</strong> textures &amp; more\"},"
            + "{\"title\":\"No url here\"},"
            + "{\"title\":\"Second\",\"url\":\"https://example.com/2\",\"description\":\"\"}]}}";

        private const string TavilyJson = "{\"query\":\"q\",\"results\":["
            + "{\"title\":\"T1\",\"url\":\"https://example.com/t1\",\"content\":\"Body one\",\"score\":0.9},"
            + "{\"title\":\"T2\",\"url\":\"https://example.com/t2\",\"content\":\"Body two\",\"score\":0.5}]}";

        // -- requests ----------------------------------------------------------

        [Test]
        public void BuildRequest_Brave_GetWithTokenHeader_QueryEscaped()
        {
            var req = UapWebSearchProviders.BuildRequest(new UapWebSearchConfig(UapWebSearchProvider.Brave, " k1 "), "unity texture import & size", 5);
            Assert.AreEqual("GET", req.Method);
            StringAssert.StartsWith(UapWebSearchProviders.BraveEndpoint + "?q=unity%20texture%20import%20%26%20size&count=5", req.Url);
            Assert.IsNull(req.Body);
            CollectionAssert.Contains(req.Headers, new KeyValuePair<string, string>("X-Subscription-Token", "k1"), "key is trimmed");
        }

        [Test]
        public void BuildRequest_Tavily_PostJsonWithBearer_CountClamped()
        {
            var req = UapWebSearchProviders.BuildRequest(new UapWebSearchConfig(UapWebSearchProvider.Tavily, "tv"), "q \"quoted\"", 99);
            Assert.AreEqual("POST", req.Method);
            Assert.AreEqual(UapWebSearchProviders.TavilyEndpoint, req.Url);
            Assert.AreEqual("application/json", req.ContentType);
            CollectionAssert.Contains(req.Headers, new KeyValuePair<string, string>("Authorization", "Bearer tv"));
            JsonNode body;
            string error;
            Assert.IsTrue(JsonParser.TryParse(req.Body, out body, out error), error);
            Assert.AreEqual("q \"quoted\"", body["query"].AsString());
            Assert.AreEqual(UapWebSearchProviders.MaxCount, body["max_results"].AsInt());
        }

        [Test]
        public void BuildRequest_NoKey_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                UapWebSearchProviders.BuildRequest(UapWebSearchConfig.None, "q", 3);
            });
        }

        // -- parsing / formatting --------------------------------------------------

        [Test]
        public void ParseResults_Brave_SkipsHitsWithoutUrl()
        {
            string error;
            List<UapWebSearchResult> hits = UapWebSearchProviders.ParseResults(UapWebSearchProvider.Brave, BraveJson, out error);
            Assert.IsNull(error);
            Assert.AreEqual(2, hits.Count);
            Assert.AreEqual("Unity - Manual: Texture2D", hits[0].Title);
            Assert.AreEqual("https://docs.unity3d.com/Manual/class-TextureImporter.html", hits[0].Url);
            Assert.AreEqual("How to <strong>import</strong> textures &amp; more", hits[0].Snippet, "raw here; Format strips tags and entities");
        }

        [Test]
        public void ParseResults_Tavily_UsesContentAsSnippet()
        {
            string error;
            List<UapWebSearchResult> hits = UapWebSearchProviders.ParseResults(UapWebSearchProvider.Tavily, TavilyJson, out error);
            Assert.IsNull(error);
            Assert.AreEqual(2, hits.Count);
            Assert.AreEqual("Body two", hits[1].Snippet);
        }

        [Test]
        public void ParseResults_ErrorBodies()
        {
            string error;
            Assert.AreEqual(0, UapWebSearchProviders.ParseResults(UapWebSearchProvider.Brave, "<html>nope</html>", out error).Count);
            StringAssert.Contains("not JSON", error);
            Assert.AreEqual(0, UapWebSearchProviders.ParseResults(UapWebSearchProvider.Brave, "{\"message\":\"Invalid token\"}", out error).Count);
            StringAssert.Contains("Invalid token", error);
            Assert.AreEqual(0, UapWebSearchProviders.ParseResults(UapWebSearchProvider.Tavily, "{\"detail\":{\"error\":\"Unauthorized\"}}", out error).Count);
            StringAssert.Contains("Unauthorized", error);
        }

        [Test]
        public void Format_NumberedList_TagsAndEntitiesStripped_Empty()
        {
            string error;
            List<UapWebSearchResult> hits = UapWebSearchProviders.ParseResults(UapWebSearchProvider.Brave, BraveJson, out error);
            string text = UapWebSearchProviders.Format("unity texture", UapWebSearchProvider.Brave, hits);
            StringAssert.StartsWith("Web search (brave) for \"unity texture\": 2 results.", text);
            StringAssert.Contains("1. Unity - Manual: Texture2D\n   https://docs.unity3d.com/Manual/class-TextureImporter.html\n   How to import textures & more", text);
            StringAssert.Contains("2. Second\n   https://example.com/2", text);
            StringAssert.Contains("uap_web_fetch", text);

            string none = UapWebSearchProviders.Format("zzz", UapWebSearchProvider.Tavily, new List<UapWebSearchResult>());
            Assert.AreEqual("Web search (tavily) for \"zzz\": 0 results. Try different words.", none);
        }

        [Test]
        public void Config_ParseProvider_DefaultsToBrave()
        {
            Assert.AreEqual(UapWebSearchProvider.Tavily, UapWebSearchConfig.ParseProvider("Tavily"));
            Assert.AreEqual(UapWebSearchProvider.Brave, UapWebSearchConfig.ParseProvider("brave"));
            Assert.AreEqual(UapWebSearchProvider.Brave, UapWebSearchConfig.ParseProvider(null));
            Assert.AreEqual("tavily", UapWebSearchConfig.ProviderId(UapWebSearchProvider.Tavily));
            Assert.IsFalse(new UapWebSearchConfig(UapWebSearchProvider.Brave, "   ").HasKey);
        }

        // -- tool --------------------------------------------------------------------

        [Test]
        public void Tool_Metadata()
        {
            var tool = new UapWebSearchTool(_client);
            Assert.AreEqual("uap_web_search", tool.Name);
            Assert.AreEqual("web", tool.Module);
            Assert.IsFalse(tool.ReadOnly, "the query leaves the machine");
            Assert.IsInstanceOf<IUapOffThreadTool>(tool);
            Assert.AreEqual("query", tool.InputSchema["required"][0].AsString());
        }

        [Test]
        public void Tool_Search_UsesConfiguredProvider_ReturnsFormattedText()
        {
            _client.Body = BraveJson;
            JsonNode result = new UapWebSearchTool(_client).Execute(Args("unity texture").Set("count", 3));
            Assert.AreEqual(1, result.Count);
            StringAssert.Contains("&count=3", _client.Last.Url);
            StringAssert.StartsWith("Web search (brave) for \"unity texture\": 2 results.", result[0]["text"].AsString());
        }

        [Test]
        public void Tool_NoKey_ExplainsWhereToSetIt_WithoutCallingOut()
        {
            UapWebSearchTool.Config = UapWebSearchConfig.None;
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapWebSearchTool(_client).Execute(Args("q"));
            });
            StringAssert.Contains("No web search API key", ex.Message);
            StringAssert.Contains("Settings", ex.Message);
            Assert.IsNull(_client.Last);
        }

        [TestCase("{}", "query is required")]
        [TestCase("{\"query\":\"   \"}", "query is required")]
        [TestCase("{\"query\":\"q\",\"count\":0}", "count")]
        [TestCase("{\"query\":\"q\",\"count\":21}", "count")]
        public void Tool_BadArguments(string json, string fragment)
        {
            JsonNode input;
            string error;
            Assert.IsTrue(JsonParser.TryParse(json, out input, out error), error);
            var ex = Assert.Throws<ArgumentException>(delegate { new UapWebSearchTool(_client).Execute(input); });
            StringAssert.Contains(fragment, ex.Message);
            Assert.IsNull(_client.Last);
        }

        [Test]
        public void Tool_TransportFailure_AndServiceError_BecomeToolErrors()
        {
            _client.Throw = "The search service answered HTTP 401 (check the API key in Settings > Web fetch).";
            var ex = Assert.Throws<InvalidOperationException>(delegate { new UapWebSearchTool(_client).Execute(Args("q")); });
            StringAssert.Contains("HTTP 401", ex.Message);

            _client.Throw = null;
            _client.Body = "{\"message\":\"quota exceeded\"}";
            ex = Assert.Throws<InvalidOperationException>(delegate { new UapWebSearchTool(_client).Execute(Args("q")); });
            StringAssert.Contains("quota exceeded", ex.Message);
        }
    }
}
