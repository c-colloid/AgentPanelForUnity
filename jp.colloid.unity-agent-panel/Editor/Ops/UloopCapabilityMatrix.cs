using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The "who owns this capability" table behind design section 7.2:
    /// when uLoop is detected (<see cref="UloopDetector"/>), UapOps must
    /// NOT register any tool whose capability uLoop already covers (uLoop
    /// runs over Bash, so it never appears in tools/list and costs zero
    /// tokens). Empty in the 5a core tool set today -- every core tool is
    /// something UapOps' loopback-MCP/Undo-integrated model does that a
    /// Bash-CLI tool structurally cannot (typed args, permission-card
    /// integration, per-turn Undo) -- but the mechanism is wired for real
    /// so 5b/5c tool additions (e.g. a future compile/test-runner tool)
    /// have a place to declare the overlap instead of duplicating uLoop.
    /// </summary>
    public static class UloopCapabilityMatrix
    {
        private static readonly HashSet<string> CoveredToolNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True when uLoop already covers this tool name's capability -- ToolRegistry must skip registering it whenever uLoop is detected.</summary>
        public static bool IsCoveredByUloop(string toolName)
        {
            return toolName != null && CoveredToolNames.Contains(toolName);
        }

        /// <summary>
        /// Test-only: registers <paramref name="toolName"/> as
        /// uLoop-covered for the duration of the returned scope (a fake
        /// matrix entry -- 5a ships none for real). Dispose to remove it
        /// again.
        /// </summary>
        internal static IDisposable OverrideForTests(string toolName)
        {
            CoveredToolNames.Add(toolName);
            return new RemovalScope(toolName);
        }

        private sealed class RemovalScope : IDisposable
        {
            private readonly string _toolName;

            public RemovalScope(string toolName)
            {
                _toolName = toolName;
            }

            public void Dispose()
            {
                CoveredToolNames.Remove(_toolName);
            }
        }
    }
}
