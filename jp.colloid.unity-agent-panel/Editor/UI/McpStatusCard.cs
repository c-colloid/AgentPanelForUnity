using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The panel's answer to "/mcp" (design note docs/design-notes/
    /// 2026-09-27-mcp-slash-command.md section 6): the CLI's own /mcp is a
    /// screen where you pick a server to see its tools and reconnect it,
    /// so the card offers the same choices in place of the one-line
    /// summary. One row per server: status glyph (the tool-card vocabulary:
    /// check / cross / bullet), name, status label, and a Reconnect button
    /// that sends the CLI's mcp_reconnect through AgentHub; a server with
    /// tools gets a Foldout listing them by short name. Authentication
    /// ("needs-auth") has no wire request the panel could send, so that
    /// row says to use the terminal instead.
    ///
    /// The Foldouts remember their state through ExpandStateMemory keyed
    /// by block + server, because MessageListController rebuilds the
    /// message element whenever the block's text changes (a reconnect
    /// outcome rewrites it).
    /// </summary>
    public sealed class McpStatusCard : VisualElement
    {
        public McpStatusCard(ChatMessageBlock block, string stateKey)
        {
            AddToClassList("uap-toolcard");
            AddToClassList("uap-mcpcard");
            List<McpServerEntry> servers = block.mcpServers ?? new List<McpServerEntry>();

            var headerLine = new VisualElement();
            headerLine.AddToClassList("uap-mcpcard-line");
            var header = new Label(SummaryText(servers));
            header.AddToClassList("uap-mcpcard-summary");
            header.enableRichText = false;
            headerLine.Add(header);
            // Design note 2026-09-27-mcp-servers-in-panel.md section 3: the
            // CLI's /mcp screen has no "add" (that is `claude mcp add`),
            // the panel's does -- it opens the Settings card that feeds
            // the next spawn's --mcp-config.
            var addServer = new Button(() => AgentPanelWindow.ShowSettings(SettingsTab.Unity, SettingsView.McpServersCardId));
            addServer.text = L10n.S.McpCardAddServer;
            addServer.AddToClassList("uap-card-btn");
            addServer.AddToClassList("uap-mcpcard-add");
            addServer.tooltip = L10n.S.McpCardAddServerTooltip;
            headerLine.Add(addServer);
            Add(headerLine);

            for (int i = 0; i < servers.Count; i++)
            {
                McpServerEntry server = servers[i];
                if (server != null)
                {
                    Add(CreateRow(server, (stateKey ?? string.Empty) + "/mcp/" + server.name));
                }
            }
        }

        private static string SummaryText(List<McpServerEntry> servers)
        {
            if (servers.Count == 0)
            {
                return L10n.S.HubMcpNoServers;
            }
            int connected = 0;
            int failed = 0;
            for (int i = 0; i < servers.Count; i++)
            {
                if (servers[i] == null)
                {
                    continue;
                }
                if (servers[i].status == "connected")
                {
                    connected++;
                }
                else if (servers[i].status == "failed")
                {
                    failed++;
                }
            }
            return L10n.F(L10n.S.HubMcpSummaryFmt, servers.Count, connected, failed,
                servers.Count - connected - failed);
        }

        private static VisualElement CreateRow(McpServerEntry server, string stateKey)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-mcpcard-row");

            var line = new VisualElement();
            line.AddToClassList("uap-mcpcard-line");
            line.Add(CreateStatusIcon(server.status));

            var name = new Label(IconLoader.SanitizeForDisplay(server.name));
            name.AddToClassList("uap-mcpcard-name");
            name.enableRichText = false;
            line.Add(name);

            string statusText = McpStatusNote.StatusLabel(server.status);
            if (server.tools.Count > 0)
            {
                statusText += " " + L10n.F(L10n.S.McpCardToolCountFmt, server.tools.Count);
            }
            var status = new Label(IconLoader.SanitizeForDisplay(statusText));
            status.AddToClassList("uap-mcpcard-status");
            status.enableRichText = false;
            line.Add(status);

            var reconnect = new Button(() => AgentHub.ReconnectMcpServer(server.name));
            reconnect.text = L10n.S.McpCardReconnect;
            reconnect.AddToClassList("uap-card-btn");
            reconnect.AddToClassList("uap-mcpcard-reconnect");
            reconnect.SetEnabled(AgentHub.CanReconnectMcpServer);
            reconnect.tooltip = AgentHub.CanReconnectMcpServer
                ? L10n.S.McpCardReconnectTooltip
                : L10n.S.McpCardReconnectOfflineTooltip;
            line.Add(reconnect);
            row.Add(line);

            if (server.status == "needs-auth")
            {
                var authLine = new VisualElement();
                authLine.AddToClassList("uap-mcpcard-line");
                var hint = new Label(L10n.S.McpCardNeedsAuthHint);
                hint.AddToClassList("uap-mcpcard-hint");
                hint.enableRichText = false;
                authLine.Add(hint);
                // Section 4 of the same note: the OAuth flow itself needs a
                // real terminal; this opens one on the panel's own config.
                var terminal = new Button(() => AgentHub.OpenTerminalForMcp());
                terminal.text = L10n.S.McpCardAuthTerminal;
                terminal.AddToClassList("uap-card-btn");
                terminal.AddToClassList("uap-mcpcard-terminal");
                terminal.tooltip = L10n.S.McpCardAuthTerminalTooltip;
                authLine.Add(terminal);
                row.Add(authLine);
            }

            if (server.tools.Count > 0)
            {
                var foldout = new Foldout();
                foldout.text = L10n.S.McpCardToolsFoldout;
                foldout.AddToClassList("uap-mcpcard-tools");
                foldout.SetValueWithoutNotify(ExpandStateMemory.Get(stateKey, false));
                foldout.RegisterValueChangedCallback(evt =>
                {
                    if (evt.target == foldout)
                    {
                        ExpandStateMemory.Set(stateKey, evt.newValue);
                    }
                });
                for (int i = 0; i < server.tools.Count; i++)
                {
                    string wire = server.tools[i];
                    var tool = new Label(IconLoader.SanitizeForDisplay(McpStatusNote.ShortToolName(wire)));
                    tool.AddToClassList("uap-mcpcard-tool");
                    tool.enableRichText = false;
                    tool.tooltip = IconLoader.SanitizeForDisplay(wire);
                    foldout.Add(tool);
                    // Section 5: descriptions exist only for the panel's own
                    // server (stream-json carries names alone); the registry
                    // answers null for every other server's tools.
                    IUapTool own = UapOpsServer.FindByWireName(wire);
                    if (own != null && !string.IsNullOrEmpty(own.Description))
                    {
                        var description = new Label(IconLoader.SanitizeForDisplay(own.Description));
                        description.AddToClassList("uap-mcpcard-tool-desc");
                        description.enableRichText = false;
                        foldout.Add(description);
                    }
                }
                row.Add(foldout);
            }
            return row;
        }

        private static VisualElement CreateStatusIcon(string status)
        {
            switch (status)
            {
                case "connected":
                    return IconLoader.CreateIcon("TestPassed", IconLoader.GlyphCheck,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--ok");
                case "failed":
                    return IconLoader.CreateIcon("TestFailed", IconLoader.GlyphCross,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--fail");
                case "needs-auth":
                    return IconLoader.CreateIcon("TestIgnored", IconLoader.GlyphCross,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--denied");
                default:
                    var pending = new Label(IconLoader.GlyphBullet);
                    pending.AddToClassList("uap-tool-glyph");
                    pending.AddToClassList("uap-tool-glyph--pending");
                    return pending;
            }
        }
    }
}
