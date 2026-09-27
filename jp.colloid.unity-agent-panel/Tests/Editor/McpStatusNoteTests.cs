using System.Collections.Generic;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The panel's own /mcp wording (McpStatusNote) -- design note
    /// docs/design-notes/2026-09-27-mcp-slash-command.md.
    /// </summary>
    public class McpStatusNoteTests
    {
        private static McpServerStatus Server(string name, string status)
        {
            return new McpServerStatus { Name = name, Status = status };
        }

        [Test]
        public void Describe_NullInit_SaysNoConnectionYet()
        {
            Assert.AreEqual(L10n.S.HubMcpNoSession, McpStatusNote.Describe((SystemInitMessage)null));
        }

        [Test]
        public void Describe_NoServers_SaysSo()
        {
            Assert.AreEqual(L10n.S.HubMcpNoServers, McpStatusNote.Describe(null, null));
            Assert.AreEqual(L10n.S.HubMcpNoServers, McpStatusNote.Describe(new McpServerStatus[0], new[] { "Read" }));
        }

        [Test]
        public void Describe_ListsEveryServer_WithStatusAndToolCount()
        {
            var servers = new[]
            {
                Server("uap-ops", "connected"),
                Server("github", "failed"),
                Server("figma", "pending")
            };
            var tools = new[]
            {
                "Read", "Bash",
                "mcp__uap-ops__uap_ping", "mcp__uap-ops__uap_query_hierarchy",
                "mcp__figma__get_screenshot"
            };

            string text = McpStatusNote.Describe(servers, tools);

            string[] lines = text.Split('\n');
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual(L10n.F(L10n.S.HubMcpSummaryFmt, 3, 1, 1, 1), lines[0]);
            Assert.AreEqual(L10n.F(L10n.S.HubMcpServerLineFmt, "uap-ops", L10n.S.HubMcpStatusConnected, 2), lines[1]);
            Assert.AreEqual(L10n.F(L10n.S.HubMcpServerLineNoToolsFmt, "github", L10n.S.HubMcpStatusFailed), lines[2],
                "a server that contributed no tools gets no tool clause");
            Assert.AreEqual(L10n.F(L10n.S.HubMcpServerLineFmt, "figma", L10n.S.HubMcpStatusPending, 1), lines[3]);
        }

        [Test]
        public void Describe_UnknownStatus_IsShownVerbatim_AndCountedAsOther()
        {
            string text = McpStatusNote.Describe(new[] { Server("x", "weird") }, null);
            string[] lines = text.Split('\n');
            Assert.AreEqual(L10n.F(L10n.S.HubMcpSummaryFmt, 1, 0, 0, 1), lines[0]);
            Assert.AreEqual(L10n.F(L10n.S.HubMcpServerLineNoToolsFmt, "x", "weird"), lines[1]);
        }

        [Test]
        public void CountToolsPerServer_SplitsOnTheLastSeparator_AndIgnoresNonMcpNames()
        {
            Dictionary<string, int> counts = McpStatusNote.CountToolsPerServer(new[]
            {
                "Read", "mcp__", "mcp__a", "mcp__a__t1", "mcp__a__t2", "mcp__a__b__t3", null, ""
            });

            Assert.AreEqual(2, counts.Count);
            Assert.AreEqual(2, counts["a"]);
            Assert.AreEqual(1, counts["a__b"]);
        }
    }
}
