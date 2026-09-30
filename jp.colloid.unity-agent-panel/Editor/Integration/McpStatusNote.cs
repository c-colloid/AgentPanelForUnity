using System;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Words the panel's own "/mcp" answer (design note docs/design-notes/
    /// 2026-09-27-mcp-slash-command.md): the MCP servers a system/init
    /// reported, one line per server with its status and the number of
    /// tools it contributed. The tool count is derived from init's
    /// tools[] -- an MCP tool is registered under "mcp__&lt;server&gt;__&lt;tool&gt;"
    /// -- because init's mcp_servers[] carries only name and status. Lives
    /// in Integration, not Model, because it reads L10n (D9 one-way
    /// layering), like CompactionNote.
    /// </summary>
    public static class McpStatusNote
    {
        private const string McpToolPrefix = "mcp__";
        private const string McpToolSeparator = "__";

        /// <summary>Localized note text for <paramref name="init"/>; null means "no connection yet".</summary>
        public static string Describe(SystemInitMessage init)
        {
            if (init == null)
            {
                return L10n.S.HubMcpNoSession;
            }
            return Describe(init.McpServers, init.Tools);
        }

        /// <summary>
        /// The card's rows for <paramref name="init"/> (design note section
        /// 6): one McpServerEntry per reported server, carrying the wire
        /// tool names that belong to it. Empty list for a null init or one
        /// without servers; never null.
        /// </summary>
        public static List<McpServerEntry> BuildEntries(SystemInitMessage init)
        {
            var entries = new List<McpServerEntry>();
            if (init == null || init.McpServers == null)
            {
                return entries;
            }
            Dictionary<string, List<string>> toolsByServer = ToolsPerServer(init.Tools);
            for (int i = 0; i < init.McpServers.Length; i++)
            {
                McpServerStatus server = init.McpServers[i];
                if (server == null)
                {
                    continue;
                }
                var entry = new McpServerEntry
                {
                    name = server.Name ?? string.Empty,
                    status = server.Status ?? string.Empty
                };
                List<string> tools;
                if (toolsByServer.TryGetValue(entry.name, out tools))
                {
                    entry.tools.AddRange(tools);
                }
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>The plain-text form of the card's rows (same wording as the init overload).</summary>
        public static string Describe(List<McpServerEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return L10n.S.HubMcpNoServers;
            }
            var servers = new McpServerStatus[entries.Count];
            var tools = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                servers[i] = new McpServerStatus { Name = entries[i].name, Status = entries[i].status };
                tools.AddRange(entries[i].tools);
            }
            return Describe(servers, tools.ToArray());
        }

        /// <summary>The display name of a wire tool name: "mcp__server__tool" -> "tool"; anything else unchanged.</summary>
        public static string ShortToolName(string tool)
        {
            if (string.IsNullOrEmpty(tool) || !tool.StartsWith(McpToolPrefix, StringComparison.Ordinal))
            {
                return tool ?? string.Empty;
            }
            int sep = tool.LastIndexOf(McpToolSeparator, StringComparison.Ordinal);
            return sep > McpToolPrefix.Length ? tool.Substring(sep + McpToolSeparator.Length) : tool;
        }

        /// <summary>Localized label for a wire status value; an unknown non-empty value is shown verbatim.</summary>
        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case "connected":
                    return L10n.S.HubMcpStatusConnected;
                case "failed":
                    return L10n.S.HubMcpStatusFailed;
                case "pending":
                    return L10n.S.HubMcpStatusPending;
                case "needs-auth":
                    return L10n.S.HubMcpStatusNeedsAuth;
                default:
                    return string.IsNullOrEmpty(status) ? L10n.S.HubMcpStatusUnknown : status;
            }
        }

        /// <summary>
        /// Pure form for tests: <paramref name="servers"/> as init reported
        /// them (null or empty means none configured), <paramref name="tools"/>
        /// the session's full tool name list (null tolerated).
        /// </summary>
        public static string Describe(McpServerStatus[] servers, string[] tools)
        {
            if (servers == null || servers.Length == 0)
            {
                return L10n.S.HubMcpNoServers;
            }
            Dictionary<string, int> toolCounts = CountToolsPerServer(tools);
            int connected = 0;
            int failed = 0;
            int other = 0;
            var lines = new List<string>();
            for (int i = 0; i < servers.Length; i++)
            {
                McpServerStatus server = servers[i];
                if (server == null)
                {
                    continue;
                }
                string name = server.Name ?? string.Empty;
                string status = server.Status ?? string.Empty;
                if (string.Equals(status, "connected", StringComparison.Ordinal))
                {
                    connected++;
                }
                else if (string.Equals(status, "failed", StringComparison.Ordinal))
                {
                    failed++;
                }
                else
                {
                    other++;
                }
                int count;
                toolCounts.TryGetValue(name, out count);
                string statusLabel = StatusLabel(status);
                lines.Add(count > 0
                    ? L10n.F(L10n.S.HubMcpServerLineFmt, name, statusLabel, count)
                    : L10n.F(L10n.S.HubMcpServerLineNoToolsFmt, name, statusLabel));
            }
            var sb = new StringBuilder();
            sb.Append(L10n.F(L10n.S.HubMcpSummaryFmt, lines.Count, connected, failed, other));
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append('\n').Append(lines[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Counts "mcp__&lt;server&gt;__&lt;tool&gt;" names per server. A server name
        /// may itself contain "__" only in theory; the split takes the
        /// LAST separator so "mcp__a__b__tool" counts for "a__b", which
        /// is also how the CLI registers such names.
        /// </summary>
        internal static Dictionary<string, int> CountToolsPerServer(string[] tools)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> pair in ToolsPerServer(tools))
            {
                counts[pair.Key] = pair.Value.Count;
            }
            return counts;
        }

        /// <summary>The wire tool names grouped by server, in tools[] order (see CountToolsPerServer for the split rule).</summary>
        internal static Dictionary<string, List<string>> ToolsPerServer(string[] tools)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (tools == null)
            {
                return result;
            }
            for (int i = 0; i < tools.Length; i++)
            {
                string tool = tools[i];
                if (string.IsNullOrEmpty(tool) || !tool.StartsWith(McpToolPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                int sep = tool.LastIndexOf(McpToolSeparator, StringComparison.Ordinal);
                if (sep <= McpToolPrefix.Length)
                {
                    continue;
                }
                string server = tool.Substring(McpToolPrefix.Length, sep - McpToolPrefix.Length);
                List<string> list;
                if (!result.TryGetValue(server, out list))
                {
                    list = new List<string>();
                    result[server] = list;
                }
                list.Add(tool);
            }
            return result;
        }
    }
}
