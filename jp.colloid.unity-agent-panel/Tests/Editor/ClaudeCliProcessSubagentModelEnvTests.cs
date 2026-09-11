using System.Collections.Generic;
using Colloid.AgentPanel.Core.Process;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-seam guard for the blanket subagent model's environment
    /// variable (docs/design-notes/2026-08-01-model-settings-rework.md
    /// section 4.2, docs/research/07-model-configuration.md section 11):
    /// ClaudeCliProcess.ComputeSubagentModelEnvEntries computes the
    /// ProcessStartInfo.EnvironmentVariables entries to apply WITHOUT
    /// spawning a process, so this suite can verify the "empty means never
    /// touch the variable, non-empty means set exactly one pair" contract
    /// directly.
    /// </summary>
    public class ClaudeCliProcessSubagentModelEnvTests
    {
        [Test]
        public void EmptyValue_YieldsNoEntries()
        {
            KeyValuePair<string, string>[] entries =
                ClaudeCliProcess.ComputeSubagentModelEnvEntries(string.Empty);
            Assert.IsEmpty(entries,
                "Empty subagentModel must never touch the environment variable at all -- "
                + "an operator-set inherited value must survive when the panel's own "
                + "setting is \"inherit\".");
        }

        [Test]
        public void NullValue_YieldsNoEntries()
        {
            KeyValuePair<string, string>[] entries =
                ClaudeCliProcess.ComputeSubagentModelEnvEntries(null);
            Assert.IsEmpty(entries);
        }

        [Test]
        public void NonEmptyValue_YieldsExactlyTheOnePair()
        {
            KeyValuePair<string, string>[] entries =
                ClaudeCliProcess.ComputeSubagentModelEnvEntries("haiku");
            Assert.AreEqual(1, entries.Length);
            Assert.AreEqual(ClaudeCliProcess.SubagentModelEnvVarName, entries[0].Key);
            Assert.AreEqual("haiku", entries[0].Value);
        }

        [Test]
        public void EnvVarName_IsTheMeasuredName()
        {
            // R07 section 11 (capture16): binary-grep confirmed exact name.
            Assert.AreEqual("CLAUDE_CODE_SUBAGENT_MODEL", ClaudeCliProcess.SubagentModelEnvVarName);
        }
    }
}
