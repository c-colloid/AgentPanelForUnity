using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure JSON-shape tests for the inline `--mcp-config` payload (docs/
    /// research/08-mcp-transport.md section 1.2's measured accepted shape:
    /// {"mcpServers":{"&lt;name&gt;":{"type":"http","url":"...","headers":{...}}}}).
    /// </summary>
    [TestFixture]
    public class UapOpsMcpConfigTests
    {
        [Test]
        public void BuildConfigJson_ProducesTheMeasuredShape()
        {
            string json = UapOpsMcpConfig.BuildConfigJson(54321, "tok-abc");
            JsonNode root = JsonParser.Parse(json);

            JsonNode server = root["mcpServers"][UapOpsMcpConfig.ServerName];
            Assert.AreEqual("http", server["type"].AsString());
            Assert.AreEqual("http://127.0.0.1:54321/mcp", server["url"].AsString());
            Assert.AreEqual("Bearer tok-abc", server["headers"]["Authorization"].AsString());
        }

        [Test]
        public void BuildConfigJson_IsSingleLine_SafeForCliArgument()
        {
            string json = UapOpsMcpConfig.BuildConfigJson(1, "t");
            StringAssert.DoesNotContain("\n", json);
            StringAssert.DoesNotContain("\r", json);
        }

        [Test]
        public void BuildConfigJson_ServerNameMatches_McpReconnectTarget()
        {
            // The mcp_reconnect control_request addresses a server by this
            // exact name (AgentHub.SendMcpReconnectIfUapOpsFailed) -- if
            // these two ever drifted apart, reconnect would silently no-op
            // against a server name the CLI never heard of.
            string json = UapOpsMcpConfig.BuildConfigJson(1, "t");
            JsonNode root = JsonParser.Parse(json);
            Assert.IsTrue(root["mcpServers"].HasKey(UapOpsMcpConfig.ServerName));
        }

        [Test]
        public void BuildConfigJson_NullToken_OmitsNothingButProducesEmptyBearer()
        {
            string json = UapOpsMcpConfig.BuildConfigJson(1, null);
            JsonNode root = JsonParser.Parse(json);
            Assert.AreEqual("Bearer ",
                root["mcpServers"][UapOpsMcpConfig.ServerName]["headers"]["Authorization"].AsString());
        }

        // -- StripToolNamePrefix (design section 8.2 B2: resolving a wire
        // tool_use name back to the registered IUapTool) --------------------

        [Test]
        public void StripToolNamePrefix_MatchesTheVerifiedWireFormat()
        {
            // docs/research/08-mcp-transport.md section 1.2/5: a real CLI
            // round trip named a server "toy" tool "ping" as
            // "mcp__toy__ping" -- i.e. literal "mcp__<server>__<tool>".
            Assert.AreEqual("uap_asset_create",
                UapOpsMcpConfig.StripToolNamePrefix("mcp__" + UapOpsMcpConfig.ServerName + "__uap_asset_create"));
        }

        [Test]
        public void StripToolNamePrefix_NonUapOpsToolName_ReturnsNull()
        {
            Assert.IsNull(UapOpsMcpConfig.StripToolNamePrefix("Write"));
            Assert.IsNull(UapOpsMcpConfig.StripToolNamePrefix("Bash"));
            Assert.IsNull(UapOpsMcpConfig.StripToolNamePrefix("mcp__some-other-server__ping"));
        }

        [Test]
        public void StripToolNamePrefix_NullOrEmpty_ReturnsNull()
        {
            Assert.IsNull(UapOpsMcpConfig.StripToolNamePrefix(null));
            Assert.IsNull(UapOpsMcpConfig.StripToolNamePrefix(string.Empty));
        }

        // -- EnsureConfigFileWritten (SEC-2: the token-carrying payload
        // goes to a file so only its PATH rides the world-readable process
        // command line) -- real temp-directory IO, mirroring
        // GateHookInstallerTests' EnsureInstalled coverage. -----------------

        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(),
                "uap_mcpcfg_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }

        [Test]
        public void EnsureConfigFileWritten_WritesExactlyTheBuildConfigJsonPayload()
        {
            string path = UapOpsMcpConfig.EnsureConfigFileWritten(_tempRoot, 54321, "tok-abc");
            Assert.IsNotNull(path);
            Assert.AreEqual(UapOpsMcpConfig.BuildConfigJson(54321, "tok-abc"),
                File.ReadAllText(path));
        }

        [Test]
        public void EnsureConfigFileWritten_ReturnsAnAbsolutePath_UnderTheGateDirectory()
        {
            string path = UapOpsMcpConfig.EnsureConfigFileWritten(_tempRoot, 1, "t");
            Assert.IsNotNull(path);
            Assert.IsTrue(Path.IsPathRooted(path),
                "the CLI resolves --mcp-config relative to its own cwd, so"
                + " the returned path must be absolute");
            string expected = Path.GetFullPath(Path.Combine(_tempRoot,
                "UserSettings", "AgentPanel", UapOpsMcpConfig.ConfigFileName));
            Assert.AreEqual(expected, path);
        }

        [Test]
        public void EnsureConfigFileWritten_NewToken_RewritesTheFile()
        {
            // The token rotates per server start; a reconnect spawn after a
            // domain reload MUST NOT leave the previous token's file in
            // place, or the CLI would connect with a dead credential.
            UapOpsMcpConfig.EnsureConfigFileWritten(_tempRoot, 1, "old-token");
            string path = UapOpsMcpConfig.EnsureConfigFileWritten(_tempRoot, 1, "new-token");
            StringAssert.Contains("new-token", File.ReadAllText(path));
        }

        [Test]
        public void EnsureConfigFileWritten_SameContentTwice_StillSucceeds()
        {
            // Same port/token (the common reconnect-within-a-session case)
            // must keep returning the path -- WriteIfChanged's skip is an
            // mtime optimization, not a failure.
            string first = UapOpsMcpConfig.EnsureConfigFileWritten(_tempRoot, 1, "t");
            string second = UapOpsMcpConfig.EnsureConfigFileWritten(_tempRoot, 1, "t");
            Assert.AreEqual(first, second);
            Assert.IsTrue(File.Exists(second));
        }

        [Test]
        public void EnsureConfigFileWritten_NullOrEmptyProjectRoot_ReturnsNull()
        {
            Assert.IsNull(UapOpsMcpConfig.EnsureConfigFileWritten(null, 1, "t"));
            Assert.IsNull(UapOpsMcpConfig.EnsureConfigFileWritten(string.Empty, 1, "t"));
        }
    }
}
