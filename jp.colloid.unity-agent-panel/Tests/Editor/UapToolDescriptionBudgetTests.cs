using System.Collections.Generic;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Claude Code truncates every MCP tool description at 2,048 characters
    /// by default (https://code.claude.com/docs/en/mcp), so whatever a
    /// description says after that point never reaches the agent -- for
    /// uap_execute_code that was the tail of the safety text. This guard
    /// keeps every registered tool under the cut with a margin: procedure
    /// detail belongs in a uap_help playbook, the description carries the
    /// contract and the safety rules
    /// (docs/design-notes/2026-10-02-session-context-cost.md).
    /// </summary>
    [TestFixture]
    public class UapToolDescriptionBudgetTests
    {
        /// <summary>Claude Code's per-tool description cut.</summary>
        public const int ClaudeCodeDescriptionLimit = 2048;

        [Test]
        public void EveryRegisteredToolDescription_FitsClaudeCodesCut()
        {
            // The default registry: Core's tools plus every tool provider the
            // project has loaded (Agent Panel Pro's, when installed).
            ToolRegistry registry = ToolRegistry.CreateDefault();
            var tooLong = new List<string>();
            foreach (string name in registry.AllNames())
            {
                int length = registry.Find(name).Description.Length;
                if (length > ClaudeCodeDescriptionLimit)
                {
                    tooLong.Add(name + " (" + length + ")");
                }
            }
            CollectionAssert.IsEmpty(tooLong,
                "tool descriptions over " + ClaudeCodeDescriptionLimit + " characters are cut by Claude Code: "
                + string.Join(", ", tooLong.ToArray()));
        }

        [Test]
        public void ExecuteCodeDescription_KeepsItsSafetyText_AndPointsAtThePlaybook()
        {
            string description = new UapExecuteCodeTool().Description;

            StringAssert.Contains("NEVER auto-approved", description);
            StringAssert.Contains("confirm:true", description);
            StringAssert.Contains("NOT A SANDBOX", description);
            StringAssert.Contains("Undo", description);
            StringAssert.Contains("uap_help topic:\"" + UapBuiltInPlaybooks.ExecuteCode + "\"", description);
            Assert.IsNotNull(UapPlaybookCatalog.CreateBuiltIn().Find(UapBuiltInPlaybooks.ExecuteCode));
        }

        [Test]
        public void ExecuteCodeDescription_FirstSentenceIsSelfSufficient()
        {
            // UapToolSummary.FirstSentence is what the compact tool list shows.
            string summary = UapToolSummary.FirstSentence(new UapExecuteCodeTool().Description);

            StringAssert.Contains("C# snippet", summary);
            StringAssert.Contains("PREFER THE TYPED", summary);
        }
    }
}
