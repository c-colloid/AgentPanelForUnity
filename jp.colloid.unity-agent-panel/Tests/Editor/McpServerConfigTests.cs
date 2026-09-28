using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The user's own MCP servers (design note docs/design-notes/
    /// 2026-09-27-mcp-servers-in-panel.md): the wire object each entry
    /// becomes, the import parser over Claude Code's own config shapes,
    /// and the merge into the --mcp-config document next to UapOps.
    /// </summary>
    public class McpServerConfigTests
    {
        [Test]
        public void ToJson_Stdio_ProducesCommandArgsEnv()
        {
            var config = new McpServerConfig
            {
                name = "fs", command = "npx",
                args = new List<string> { "-y", "@modelcontextprotocol/server-filesystem", "/tmp" },
                env = new List<string> { "TOKEN = abc", "broken-line", "=novalue" }
            };
            JsonNode node = config.ToJson();
            Assert.AreEqual("stdio", node["type"].AsString());
            Assert.AreEqual("npx", node["command"].AsString());
            CollectionAssert.AreEqual(new[] { "-y", "@modelcontextprotocol/server-filesystem", "/tmp" },
                node["args"].AsStringArray());
            Assert.AreEqual("abc", node["env"]["TOKEN"].AsString(), "key and value are trimmed");
            int envCount = 0;
            foreach (KeyValuePair<string, JsonNode> pair in node["env"].Properties)
            {
                envCount++;
            }
            Assert.AreEqual(1, envCount, "lines without a separator or key are skipped");
        }

        [Test]
        public void ToJson_Http_ProducesUrlAndHeaders_OmittingEmptyHeaders()
        {
            var config = new McpServerConfig
            {
                name = "gh", transport = "http", url = " https://example.com/mcp ",
                headers = new List<string> { "Authorization: Bearer x:y" }
            };
            JsonNode node = config.ToJson();
            Assert.AreEqual("http", node["type"].AsString());
            Assert.AreEqual("https://example.com/mcp", node["url"].AsString());
            Assert.AreEqual("Bearer x:y", node["headers"]["Authorization"].AsString(),
                "only the first colon separates name and value");

            var bare = new McpServerConfig { name = "s", transport = "sse", url = "http://h/sse" };
            Assert.IsFalse(bare.ToJson()["headers"].IsObject);
        }

        [Test]
        public void IsComplete_NeedsNameAndCommandOrUrl()
        {
            Assert.IsFalse(new McpServerConfig().IsComplete);
            Assert.IsFalse(new McpServerConfig { name = "a" }.IsComplete);
            Assert.IsTrue(new McpServerConfig { name = "a", command = "x" }.IsComplete);
            Assert.IsFalse(new McpServerConfig { name = "a", transport = "http", command = "x" }.IsComplete);
            Assert.IsTrue(new McpServerConfig { name = "a", transport = "http", url = "u" }.IsComplete);
        }

        [Test]
        public void ReadAll_ReadsTopLevelAndProjectScopedServers_FromClaudeJson()
        {
            string json = "{\"mcpServers\":{\"user-one\":{\"type\":\"stdio\",\"command\":\"node\",\"args\":[\"a.js\"],"
                + "\"env\":{\"K\":\"v\"}}},\"projects\":{\"C:\\\\proj\\\\\":{\"mcpServers\":{\"proj-one\":"
                + "{\"type\":\"http\",\"url\":\"https://x/mcp\",\"headers\":{\"H\":\"1\"}}}},"
                + "\"C:\\\\other\":{\"mcpServers\":{\"other\":{\"command\":\"z\"}}}}}";

            List<McpServerConfig> found = McpServerConfig.ReadAll(json, "c:/proj");

            Assert.AreEqual(2, found.Count);
            Assert.AreEqual("user-one", found[0].name);
            Assert.AreEqual("stdio", found[0].transport);
            CollectionAssert.AreEqual(new[] { "a.js" }, found[0].args);
            CollectionAssert.AreEqual(new[] { "K=v" }, found[0].env);
            Assert.AreEqual("proj-one", found[1].name);
            Assert.AreEqual("http", found[1].transport);
            Assert.AreEqual("https://x/mcp", found[1].url);
            CollectionAssert.AreEqual(new[] { "H: 1" }, found[1].headers);
        }

        [Test]
        public void ReadAll_TypeOmitted_IsStdio_AndGarbageYieldsNothing()
        {
            List<McpServerConfig> found = McpServerConfig.ReadAll("{\"mcpServers\":{\"m\":{\"command\":\"c\"}}}", null);
            Assert.AreEqual(1, found.Count);
            Assert.AreEqual("stdio", found[0].transport);
            Assert.IsEmpty(McpServerConfig.ReadAll("not json", null));
            Assert.IsEmpty(McpServerConfig.ReadAll("[1,2]", null));
            Assert.IsEmpty(McpServerConfig.ReadAll(null, null));
        }

        [Test]
        public void MergeNew_SkipsNamesAlreadyListed()
        {
            var into = new List<McpServerConfig> { new McpServerConfig { name = "a" } };
            var found = new List<McpServerConfig>
            {
                new McpServerConfig { name = "a", command = "dup" },
                new McpServerConfig { name = "b", command = "new" },
                new McpServerConfig { name = "b", command = "again" }
            };
            Assert.AreEqual(1, McpServerImport.MergeNew(into, found));
            Assert.AreEqual(2, into.Count);
            Assert.AreEqual("b", into[1].name);
        }

        [Test]
        public void ListsEqual_And_CloneList_AreFieldWise()
        {
            var a = new List<McpServerConfig> { new McpServerConfig { name = "a", args = new List<string> { "1" } } };
            List<McpServerConfig> clone = McpServerConfig.CloneList(a);
            Assert.IsTrue(McpServerConfig.ListsEqual(a, clone));
            Assert.AreNotSame(a[0], clone[0]);
            clone[0].args.Add("2");
            Assert.IsFalse(McpServerConfig.ListsEqual(a, clone), "a nested list edit is a difference");
            Assert.IsTrue(McpServerConfig.ListsEqual(null, new List<McpServerConfig>()));
        }

        [Test]
        public void BuildConfigJson_AppendsEnabledCompleteServers_AfterUapOps()
        {
            var extra = new List<McpServerConfig>
            {
                new McpServerConfig { name = "fs", command = "npx" },
                new McpServerConfig { name = "off", command = "x", enabled = false },
                new McpServerConfig { name = "incomplete" },
                new McpServerConfig { name = UapOpsMcpConfig.ServerName, command = "impostor" },
                new McpServerConfig { name = "fs", command = "dup" }
            };
            JsonNode servers = JsonParser.Parse(UapOpsMcpConfig.BuildConfigJson(true, 1234, "tok", extra))["mcpServers"];
            var names = new List<string>();
            foreach (KeyValuePair<string, JsonNode> pair in servers.Properties)
            {
                names.Add(pair.Key);
            }
            CollectionAssert.AreEqual(new[] { UapOpsMcpConfig.ServerName, "fs" }, names);
            Assert.AreEqual("npx", servers["fs"]["command"].AsString(), "the first 'fs' wins");
            Assert.AreEqual("http://127.0.0.1:1234/mcp", servers[UapOpsMcpConfig.ServerName]["url"].AsString(),
                "the panel's own server is never replaced by a same-named entry");
        }

        [Test]
        public void BuildConfigJson_WithoutUapOps_HoldsOnlyTheUserServers()
        {
            var extra = new List<McpServerConfig> { new McpServerConfig { name = "fs", command = "npx" } };
            JsonNode servers = JsonParser.Parse(UapOpsMcpConfig.BuildConfigJson(false, 0, null, extra))["mcpServers"];
            Assert.IsFalse(servers[UapOpsMcpConfig.ServerName].IsObject);
            Assert.IsTrue(servers["fs"].IsObject);
            Assert.IsTrue(UapOpsMcpConfig.HasUsableServer(extra));
            Assert.IsFalse(UapOpsMcpConfig.HasUsableServer(new List<McpServerConfig> { new McpServerConfig { name = "x" } }));
            Assert.IsFalse(UapOpsMcpConfig.HasUsableServer(null));
        }

        [Test]
        public void BuildConfigJson_LegacyOverload_IsUnchanged()
        {
            Assert.AreEqual(UapOpsMcpConfig.BuildConfigJson(7, "t"), UapOpsMcpConfig.BuildConfigJson(true, 7, "t", null));
        }
    }
}
