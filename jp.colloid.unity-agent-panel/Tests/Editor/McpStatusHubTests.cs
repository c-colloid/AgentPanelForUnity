using System.Collections.Generic;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The panel's /mcp through AgentHub with a real AgentClient over
    /// FakeCliProcess (the CompactionHubTests pattern): the user echo, the
    /// McpStatus block built from system/init, the card's rows, and the
    /// Reconnect round trip (mcp_reconnect out, control_response in, row
    /// status + note updated). Design note docs/design-notes/
    /// 2026-09-27-mcp-slash-command.md section 6.
    /// </summary>
    public class McpStatusHubTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;

        private const string InitLine =
            "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\","
            + "\"tools\":[\"Read\",\"mcp__unity-ops__uap_ping\",\"mcp__unity-ops__uap_asset_find\",\"mcp__figma__shot\"],"
            + "\"mcp_servers\":[{\"name\":\"unity-ops\",\"status\":\"connected\"},"
            + "{\"name\":\"github\",\"status\":\"failed\"},{\"name\":\"figma\",\"status\":\"needs-auth\"}]}";

        [SetUp]
        public void SetUp()
        {
            AgentHub.ResetForTests();
            ExpandStateMemory.ResetForTests();
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
            AgentHub.WireClientForTests(_client);
            AgentHub.WireControlRequestResolvedForTests(_client);
            // WireClientForTests only subscribes the handlers; ShowMcpStatus
            // and ReconnectMcpServer read the hub's own _client, so install
            // it too (and restore the no-connection invariant in TearDown).
            AgentHub.SetClientForTests(_client);
        }

        [TearDown]
        public void TearDown()
        {
            AgentHub.SetClientForTests(null);
            _client.Dispose();
            AgentHub.ResetForTests();
            ExpandStateMemory.ResetForTests();
        }

        private void StartWithInit()
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            _fake.ScriptLine(InitLine);
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(100, 50.0) > 0)
            {
            }
        }

        private static string ExtractRequestId(string line)
        {
            Match m = Regex.Match(line, "\"request_id\":\"([^\"]+)\"");
            Assert.IsTrue(m.Success, "no request_id in: " + line);
            return m.Groups[1].Value;
        }

        private static ChatMessageBlock LastMcpBlock()
        {
            List<ChatMessage> messages = AgentHub.Session.messages;
            for (int m = messages.Count - 1; m >= 0; m--)
            {
                for (int b = messages[m].blocks.Count - 1; b >= 0; b--)
                {
                    if (messages[m].blocks[b].kind == ChatBlockKind.McpStatus)
                    {
                        return messages[m].blocks[b];
                    }
                }
            }
            return null;
        }

        [Test]
        public void ShowMcpStatus_WithoutClient_EchoesTheCommand_AndSaysNoConnection()
        {
            AgentHub.SetClientForTests(null);
            AgentHub.ShowMcpStatus();

            List<ChatMessage> messages = AgentHub.Session.messages;
            Assert.AreEqual(2, messages.Count);
            Assert.AreEqual(ChatMessage.RoleUser, messages[0].role);
            Assert.AreEqual("/mcp", messages[0].blocks[0].text);
            Assert.AreEqual(ChatMessage.RoleSystem, messages[1].role);
            Assert.AreEqual(ChatBlockKind.SystemNote, messages[1].blocks[0].kind);
            Assert.AreEqual(L10n.S.HubMcpNoSession, messages[1].blocks[0].text);
            Assert.IsFalse(AgentHub.CanReconnectMcpServer);
        }

        [Test]
        public void ShowMcpStatus_WithInit_AppendsTheCardBlock_WithToolsPerServer()
        {
            StartWithInit();
            AgentHub.ShowMcpStatus();

            List<ChatMessage> messages = AgentHub.Session.messages;
            Assert.AreEqual(ChatMessage.RoleUser, messages[messages.Count - 2].role);
            Assert.AreEqual("/mcp", messages[messages.Count - 2].blocks[0].text);
            ChatMessageBlock block = LastMcpBlock();
            Assert.IsNotNull(block);
            Assert.AreEqual(3, block.mcpServers.Count);
            Assert.AreEqual("unity-ops", block.mcpServers[0].name);
            Assert.AreEqual("connected", block.mcpServers[0].status);
            CollectionAssert.AreEqual(new[] { "mcp__unity-ops__uap_ping", "mcp__unity-ops__uap_asset_find" },
                block.mcpServers[0].tools);
            Assert.AreEqual("github", block.mcpServers[1].name);
            Assert.IsEmpty(block.mcpServers[1].tools);
            Assert.AreEqual("needs-auth", block.mcpServers[2].status);
            Assert.AreEqual(McpStatusNote.Describe(block.mcpServers), block.text,
                "the plain-text fallback carries the same facts");
            Assert.IsTrue(AgentHub.CanReconnectMcpServer);
        }

        [Test]
        public void Card_RendersOneRowPerServer_WithReconnectButtons_AndToolFoldouts()
        {
            StartWithInit();
            AgentHub.ShowMcpStatus();
            ChatMessageBlock block = LastMcpBlock();

            var card = new McpStatusCard(block, "msg/0");

            Assert.AreEqual(3, card.Query<VisualElement>(className: "uap-mcpcard-row").ToList().Count);
            List<Button> buttons = card.Query<Button>(className: "uap-mcpcard-reconnect").ToList();
            Assert.AreEqual(3, buttons.Count);
            Assert.IsTrue(buttons[0].enabledSelf, "a live client makes Reconnect available");
            List<Foldout> foldouts = card.Query<Foldout>(className: "uap-mcpcard-tools").ToList();
            Assert.AreEqual(2, foldouts.Count, "only servers with tools get a tool list");
            Assert.AreEqual(2, foldouts[0].Query<Label>(className: "uap-mcpcard-tool").ToList().Count);
            Assert.AreEqual("uap_ping", foldouts[0].Query<Label>(className: "uap-mcpcard-tool").First().text);
            Assert.AreEqual(1, card.Query<Label>(className: "uap-mcpcard-hint").ToList().Count,
                "the needs-auth row explains that authentication happens in a terminal");
            Assert.AreEqual(1, card.Query<Button>(className: "uap-mcpcard-terminal").ToList().Count,
                "and offers to open one");
            Assert.AreEqual(1, card.Query<Button>(className: "uap-mcpcard-add").ToList().Count,
                "the header offers to add a server");
            // The registry may leave a tool out when uloop covers it
            // (ToolRegistry.RegisterUnlessCovered), so count what it holds.
            int registered = 0;
            for (int i = 0; i < block.mcpServers[0].tools.Count; i++)
            {
                if (UapOpsServer.FindByWireName(block.mcpServers[0].tools[i]) != null)
                {
                    registered++;
                }
            }
            Assert.Greater(registered, 0, "uap_ping is always registered");
            Assert.AreEqual(registered, foldouts[0].Query<Label>(className: "uap-mcpcard-tool-desc").ToList().Count,
                "the panel's own server's tools carry their registry descriptions");
        }

        [Test]
        public void Card_WithoutClient_DisablesReconnect()
        {
            AgentHub.SetClientForTests(null);
            var block = ChatMessageBlock.MakeMcpStatus("x", new List<McpServerEntry>
            {
                new McpServerEntry { name = "github", status = "failed" }
            });
            var card = new McpStatusCard(block, "msg/0");
            Button button = card.Query<Button>(className: "uap-mcpcard-reconnect").First();
            Assert.IsFalse(button.enabledSelf);
            Assert.AreEqual(L10n.S.McpCardReconnectOfflineTooltip, button.tooltip);
        }

        [Test]
        public void Card_ToolFoldout_RemembersItsState_AcrossRebuilds()
        {
            StartWithInit();
            AgentHub.ShowMcpStatus();
            ChatMessageBlock block = LastMcpBlock();

            ExpandStateMemory.Set("msg/0/mcp/unity-ops", true);
            var card = new McpStatusCard(block, "msg/0");
            List<Foldout> foldouts = card.Query<Foldout>(className: "uap-mcpcard-tools").ToList();
            Assert.IsTrue(foldouts[0].value, "unity-ops was left open");
            Assert.IsFalse(foldouts[1].value, "figma was never opened");
        }

        [Test]
        public void Reconnect_SendsMcpReconnect_AndSuccessUpdatesTheRowAndNotes()
        {
            StartWithInit();
            AgentHub.ShowMcpStatus();
            int before = AgentHub.Session.messages.Count;

            AgentHub.ReconnectMcpServer("github");

            string sent = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            StringAssert.Contains("\"subtype\":\"mcp_reconnect\"", sent);
            StringAssert.Contains("\"serverName\":\"github\"", sent);
            List<ChatMessage> messages = AgentHub.Session.messages;
            Assert.AreEqual(before + 1, messages.Count);
            Assert.AreEqual(L10n.F(L10n.S.HubMcpReconnectRequestedFmt, "github"),
                messages[messages.Count - 1].blocks[0].text);

            string requestId = ExtractRequestId(sent);
            _fake.ScriptLine("{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\",\"response\":{}}}");
            PumpAll();

            Assert.AreEqual("connected", LastMcpBlock().mcpServers[1].status);
            Assert.AreEqual(L10n.F(L10n.S.HubMcpReconnectedFmt, "github"),
                messages[messages.Count - 1].blocks[0].text);
            Assert.IsFalse(messages[messages.Count - 1].blocks[0].warning);
        }

        [Test]
        public void Reconnect_Failure_MarksTheRowFailed_AndWarns()
        {
            StartWithInit();
            AgentHub.ShowMcpStatus();

            AgentHub.ReconnectMcpServer("unity-ops");
            string requestId = ExtractRequestId(_fake.WrittenLines[_fake.WrittenLines.Count - 1]);
            _fake.ScriptLine("{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"error\",\"request_id\":\"" + requestId + "\",\"error\":\"spawn failed\"}}");
            PumpAll();

            Assert.AreEqual("failed", LastMcpBlock().mcpServers[0].status);
            List<ChatMessage> messages = AgentHub.Session.messages;
            ChatMessageBlock last = messages[messages.Count - 1].blocks[0];
            Assert.AreEqual(L10n.F(L10n.S.HubMcpReconnectFailedFmt, "unity-ops", "spawn failed"), last.text);
            Assert.IsTrue(last.warning);
        }

        [Test]
        public void Reconnect_WithoutClient_IsANoOp()
        {
            AgentHub.SetClientForTests(null);
            AgentHub.ShowMcpStatus();
            int before = AgentHub.Session.messages.Count;
            AgentHub.ReconnectMcpServer("github");
            Assert.AreEqual(before, AgentHub.Session.messages.Count);
        }
    }
}
