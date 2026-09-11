using System.Collections.Generic;
using Colloid.AgentPanel.Ops.UnityPlugin;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The two-stage install sequence with the process runner faked
    /// (design note section 2.3 items 2 and 5). Command strings are pinned
    /// to what was run for real on 2026-09-10.
    /// </summary>
    [TestFixture]
    public class UnityPluginInstallerTests
    {
        private sealed class FakeRunner
        {
            public readonly List<string> Calls = new List<string>();
            private readonly Queue<KeyValuePair<int, string>> _replies = new Queue<KeyValuePair<int, string>>();

            public FakeRunner Reply(int exitCode, string stdout)
            {
                _replies.Enqueue(new KeyValuePair<int, string>(exitCode, stdout));
                return this;
            }

            public string Run(string cliPath, string arguments, int timeoutMillis, out int exitCode)
            {
                Calls.Add(arguments);
                Assert.AreEqual(UnityPluginInstaller.StageTimeoutMillis, timeoutMillis);
                Assert.AreEqual("/bin/claude", cliPath);
                var reply = _replies.Dequeue();
                exitCode = reply.Key;
                return reply.Value;
            }
        }

        [Test]
        public void CommandStrings_MatchTheMeasuredInvocation()
        {
            Assert.AreEqual("plugin marketplace add Unity-Technologies/unity-agent-plugin",
                UnityPluginInstaller.MarketplaceAddArguments);
            Assert.AreEqual("plugin install unity@unity-agent-plugin --scope user --yes",
                UnityPluginInstaller.InstallArguments);
            Assert.AreEqual("plugin install unity@unity-agent-plugin --scope user",
                UnityPluginInstaller.InstallArgumentsWithoutYes);
        }

        [Test]
        public void BothStagesExitZero_Success_NoRetry()
        {
            var runner = new FakeRunner()
                .Reply(0, "Adding marketplace...\nSuccessfully added marketplace: unity-agent-plugin\n")
                .Reply(0, "Installing plugin \"unity@unity-agent-plugin\"...Successfully installed plugin: unity@unity-agent-plugin (scope: user)\n");
            var r = UnityPluginInstaller.RunStages("/bin/claude", runner.Run);
            Assert.IsTrue(r.Success);
            Assert.AreEqual(UnityPluginInstallStage.PluginInstall, r.Stage);
            Assert.AreEqual(0, r.ExitCode);
            Assert.IsFalse(r.RetriedWithoutYes);
            Assert.AreEqual(2, runner.Calls.Count);
            Assert.AreEqual(UnityPluginInstaller.MarketplaceAddArguments, runner.Calls[0]);
            Assert.AreEqual(UnityPluginInstaller.InstallArguments, runner.Calls[1]);
            StringAssert.StartsWith("Installing plugin", r.LastLine);
        }

        [Test]
        public void AlreadyRegisteredAndInstalled_ExitZeroLines_StillSuccess()
        {
            // Measured second run: both stages exit 0 with an "already" line.
            var runner = new FakeRunner()
                .Reply(0, "Marketplace 'unity-agent-plugin' already on disk\n")
                .Reply(0, "Plugin \"unity@unity-agent-plugin\" is already installed (scope: user)\n");
            var r = UnityPluginInstaller.RunStages("/bin/claude", runner.Run);
            Assert.IsTrue(r.Success);
            Assert.AreEqual("Plugin \"unity@unity-agent-plugin\" is already installed (scope: user)", r.LastLine);
        }

        [Test]
        public void MarketplaceFails_StopsBeforeInstall()
        {
            var runner = new FakeRunner().Reply(1, "Error: git not found\n");
            var r = UnityPluginInstaller.RunStages("/bin/claude", runner.Run);
            Assert.IsFalse(r.Success);
            Assert.AreEqual(UnityPluginInstallStage.MarketplaceAdd, r.Stage);
            Assert.AreEqual(1, r.ExitCode);
            Assert.AreEqual("Error: git not found", r.LastLine);
            Assert.AreEqual(1, runner.Calls.Count);
        }

        [Test]
        public void MarketplaceTimesOut_NullOutput_IsFailure()
        {
            var runner = new FakeRunner().Reply(-1, null);
            var r = UnityPluginInstaller.RunStages("/bin/claude", runner.Run);
            Assert.IsFalse(r.Success);
            Assert.AreEqual(UnityPluginInstallStage.MarketplaceAdd, r.Stage);
            Assert.AreEqual(string.Empty, r.LastLine);
        }

        [Test]
        public void InstallRejectsYes_RetriedOnceWithoutYes_Succeeds()
        {
            var runner = new FakeRunner()
                .Reply(0, "ok\n")
                .Reply(1, "error: unknown option '--yes'\n")
                .Reply(0, "Successfully installed plugin: unity@unity-agent-plugin (scope: user)\n");
            var r = UnityPluginInstaller.RunStages("/bin/claude", runner.Run);
            Assert.IsTrue(r.Success);
            Assert.IsTrue(r.RetriedWithoutYes);
            Assert.AreEqual(3, runner.Calls.Count);
            Assert.AreEqual(UnityPluginInstaller.InstallArgumentsWithoutYes, runner.Calls[2]);
        }

        [Test]
        public void InstallFailsTwice_Failure_SecondVerdictIsFinal()
        {
            var runner = new FakeRunner()
                .Reply(0, "ok\n")
                .Reply(1, "first failure\n")
                .Reply(6, "second failure\n");
            var r = UnityPluginInstaller.RunStages("/bin/claude", runner.Run);
            Assert.IsFalse(r.Success);
            Assert.AreEqual(UnityPluginInstallStage.PluginInstall, r.Stage);
            Assert.AreEqual(6, r.ExitCode);
            Assert.AreEqual("second failure", r.LastLine);
            Assert.IsTrue(r.RetriedWithoutYes);
            Assert.AreEqual(3, runner.Calls.Count);
        }

        [Test]
        public void LastNonEmptyLine_SkipsTrailingBlanks_HandlesCrLf_EmptyForNothing()
        {
            Assert.AreEqual("last", UnityPluginInstaller.LastNonEmptyLine("first\r\nlast\r\n\r\n  \n"));
            Assert.AreEqual("only", UnityPluginInstaller.LastNonEmptyLine("  only  "));
            Assert.AreEqual(string.Empty, UnityPluginInstaller.LastNonEmptyLine(null));
            Assert.AreEqual(string.Empty, UnityPluginInstaller.LastNonEmptyLine("\n \n"));
        }
    }
}
