using Colloid.AgentPanel.Model;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Unity-free rules behind PanelSettings.uapToolListMode (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 2.8 B): which
    /// MCP endpoint path a spawn's UapOps connection should use, and the
    /// one steering sentence a compact connection needs so the agent knows
    /// where the tools it does not see went. AgentHub.StartClient calls
    /// <see cref="ResolveCompact"/> once per spawn to pick between
    /// <see cref="McpPath"/> (acpSpec.McpUrl / UapOpsMcpConfig's
    /// --mcp-config) and <see cref="McpCompactPath"/>; AgentHub.
    /// ComposeUapOpsSteeringSection appends <see cref="SteeringSentence"/>
    /// exactly when that same call resolved to compact.
    /// </summary>
    public static class UapToolListModePolicy
    {
        /// <summary>Full-mode endpoint path (unchanged since before this feature -- every tool, listed directly).</summary>
        public const string McpPath = "/mcp";

        /// <summary>Compact-mode endpoint path: "core" listed directly, everything else through uap_tools/uap_call.</summary>
        public const string McpCompactPath = "/mcp/compact";

        /// <summary>
        /// True when the spawn about to happen should use
        /// <see cref="McpCompactPath"/>. Full/Compact are explicit; Auto
        /// resolves by backend -- Claude Code (<paramref name="isAcpBackend"/>
        /// false) already defers loading a tool's full schema client-side
        /// (ToolSearch), so Auto stays Full there; every ACP backend has no
        /// such deferral, so Auto is Compact there (design section 1.2).
        /// </summary>
        public static bool ResolveCompact(UapToolListMode mode, bool isAcpBackend)
        {
            switch (mode)
            {
                case UapToolListMode.Full:
                    return false;
                case UapToolListMode.Compact:
                    return true;
                case UapToolListMode.Auto:
                default:
                    return isAcpBackend;
            }
        }

        /// <summary>The endpoint path for the resolved mode -- what AgentHub actually puts in the MCP URL.</summary>
        public static string EndpointPath(bool compact)
        {
            return compact ? McpCompactPath : McpPath;
        }

        // Deliberately NOT a reference to UapToolGateway's own constants:
        // this class stays fully Unity-free (ToolRegistry, which
        // UapToolGateway's other members touch, pulls in UnityEditor/
        // UnityEngine, which would make this whole file unusable in the
        // license-free smoke gate). Kept in sync BY CONSTRUCTION -- see
        // UapToolListModePolicyTests.SteeringSentence_TheThreeDuplicated
        // LiteralsMatchUapToolGateways, which asserts these three literals
        // equal UapToolGateway.DirectlyListedModule/ToolsToolName/
        // CallToolName (and HelpToolName equals UapHelpTool.ToolName), so a
        // rename of either breaks the build's test run, not just this
        // comment's promise.
        private const string DirectlyListedModule = "core";
        private const string ToolsToolName = "uap_tools";
        private const string CallToolName = "uap_call";
        private const string HelpToolName = "uap_help";

        /// <summary>
        /// The one sentence ComposeUapOpsSteeringSection appends when (and
        /// only when) this spawn resolved to compact -- names both gateway
        /// tools by their wire names so the agent reaches for them by name
        /// instead of assuming a module's tools are simply unavailable.
        /// Deliberately does not name which modules are enabled: that list
        /// is already composed elsewhere in the same steering section, and
        /// duplicating it here would drift. The closing clause (design note
        /// 2026-10-02-session-context-cost.md section 4) tells the agent
        /// that the schemas it can see are lean (see
        /// <see cref="UapLeanSchema"/>) and where the full one is.
        /// </summary>
        public static string SteeringSentence()
        {
            return "This connection lists only the \"" + DirectlyListedModule
                + "\" module's tools directly; find and run any other enabled module's tools with "
                + ToolsToolName + " (search by name/module/summary) and "
                + CallToolName + " (run one by name); listed schemas carry one-line field descriptions; "
                + HelpToolName + " names:[...] returns the full schema.";
        }
    }
}
