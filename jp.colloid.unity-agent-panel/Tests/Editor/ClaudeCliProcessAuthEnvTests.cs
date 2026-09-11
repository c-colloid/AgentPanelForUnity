using Colloid.AgentPanel.Core.Process;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-seam guard for the API key auth passthrough (docs/design-notes/
    /// 2026-09-10-claude-api-key-auth-passthrough.md): ClaudeCliProcess.
    /// ComputeEnvVarsToRemove computes the environment variable NAMES to
    /// remove from ProcessStartInfo WITHOUT spawning a process, so this
    /// suite verifies "Auto never touches ANTHROPIC_API_KEY, SubscriptionOnly
    /// always removes it, both always remove the three session/entrypoint
    /// variables" directly. Mirrors ClaudeCliProcessSubagentModelEnvTests.
    /// </summary>
    public class ClaudeCliProcessAuthEnvTests
    {
        [Test]
        public void Auto_NeverRemovesApiKey()
        {
            string[] names = ClaudeCliProcess.ComputeEnvVarsToRemove(ClaudeAuthMode.Auto);
            Assert.IsFalse(Contains(names, "ANTHROPIC_API_KEY"),
                "Auto (the default) must leave ANTHROPIC_API_KEY completely untouched -- "
                + "the CLI's own auth selection must not be blocked (Anthropic's Claude Code "
                + "legal terms).");
        }

        [Test]
        public void Auto_StillRemovesTheThreeSessionEntrypointVariables()
        {
            string[] names = ClaudeCliProcess.ComputeEnvVarsToRemove(ClaudeAuthMode.Auto);
            Assert.IsTrue(Contains(names, "CLAUDECODE"));
            Assert.IsTrue(Contains(names, "CLAUDE_CODE_ENTRYPOINT"));
            Assert.IsTrue(Contains(names, "CLAUDE_CODE_SESSION_ID"));
            Assert.AreEqual(3, names.Length);
        }

        [Test]
        public void SubscriptionOnly_RemovesApiKeyToo()
        {
            string[] names = ClaudeCliProcess.ComputeEnvVarsToRemove(ClaudeAuthMode.SubscriptionOnly);
            Assert.IsTrue(Contains(names, "ANTHROPIC_API_KEY"));
            Assert.IsTrue(Contains(names, "CLAUDECODE"));
            Assert.IsTrue(Contains(names, "CLAUDE_CODE_ENTRYPOINT"));
            Assert.IsTrue(Contains(names, "CLAUDE_CODE_SESSION_ID"));
            Assert.AreEqual(4, names.Length);
        }

        private static bool Contains(string[] names, string name)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == name)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
