using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// CLI update check and update (design note
    /// docs/design-notes/2026-09-23-cli-update-and-pro-version.md): version
    /// parsing and ordering, the registry answer, the update plan and the
    /// settings status line.
    /// </summary>
    [TestFixture]
    public class CliUpdateCheckTests
    {
        [TestCase("2.1.218 (Claude Code)", "2.1.218")]
        [TestCase("  v1.0.3\n", "1.0.3")]
        [TestCase("3.0.0-beta.2", "3.0.0-beta.2")]
        [TestCase("1.2.3+build", "1.2.3")]
        [TestCase("Claude Code", "")]
        [TestCase("1.2", "")]
        [TestCase(null, "")]
        public void ExtractSemver_TakesTheLeadingVersionToken(string input, string expected)
        {
            Assert.AreEqual(expected, CliUpdateCheck.ExtractSemver(input));
        }

        [TestCase("2.1.218", "2.1.219", -1)]
        [TestCase("2.10.0", "2.9.9", 1)]
        [TestCase("2.1.218 (Claude Code)", "2.1.218", 0)]
        [TestCase("3.0.0-beta.1", "3.0.0", -1)]
        [TestCase("3.0.0-beta.2", "3.0.0-beta.3", -1)]
        [TestCase("garbage", "1.0.0", 0)]
        public void CompareVersions_FollowsSemverPrecedence(string a, string b, int expected)
        {
            Assert.AreEqual(expected, CliUpdateCheck.CompareVersions(a, b));
        }

        [Test]
        public void ParseLatestFromDistTags_ReadsTheLatestTag()
        {
            Assert.AreEqual("2.1.220",
                CliUpdateCheck.ParseLatestFromDistTags("{\"stable\":\"2.1.200\",\"latest\":\"2.1.220\",\"next\":\"2.2.0\"}"));
            Assert.AreEqual(string.Empty, CliUpdateCheck.ParseLatestFromDistTags("{\"next\":\"2.2.0\"}"));
            Assert.AreEqual(string.Empty, CliUpdateCheck.ParseLatestFromDistTags("<html>blocked</html>"));
            Assert.AreEqual(string.Empty, CliUpdateCheck.ParseLatestFromDistTags(null));
        }

        [Test]
        public void Classify_ComparesInstalledWithLatest()
        {
            Assert.AreEqual(CliUpdateCheckOutcome.UpdateAvailable, CliUpdateCheck.Classify("2.1.218 (Claude Code)", "2.1.220"));
            Assert.AreEqual(CliUpdateCheckOutcome.UpToDate, CliUpdateCheck.Classify("2.1.220 (Claude Code)", "2.1.220"));
            Assert.AreEqual(CliUpdateCheckOutcome.UpToDate, CliUpdateCheck.Classify("2.2.0", "2.1.220"));
            Assert.AreEqual(CliUpdateCheckOutcome.InstalledUnknown, CliUpdateCheck.Classify(null, "2.1.220"));
            Assert.AreEqual(CliUpdateCheckOutcome.Failed, CliUpdateCheck.Classify("2.1.218", string.Empty));
        }

        [Test]
        public void BuildClaudeUpdate_RunsTheResolvedBinarysOwnUpdater()
        {
            CliInstallPlan plan = CliInstallPlan.BuildClaudeUpdate("/home/u/.local/bin/claude");
            Assert.AreEqual("/home/u/.local/bin/claude", plan.FileName);
            Assert.AreEqual("update", plan.Arguments);
            Assert.AreEqual(AgentBackend.ClaudeCode, plan.Backend);
            Assert.IsFalse(plan.RequiresNpm);
            Assert.IsNull(CliInstallPlan.BuildClaudeUpdate(string.Empty));
        }

        [Test]
        public void DescribeCliUpdateState_HiddenUntilTheFirstCheck()
        {
            Assert.IsNull(SettingsView.DescribeCliUpdateState(false, false, null, "2.1.218", false, null, false, 0, 0));
        }

        [Test]
        public void DescribeCliUpdateState_NamesBothVersionsWhenAnUpdateExists()
        {
            string text = SettingsView.DescribeCliUpdateState(false, true, "2.1.220", "2.1.218 (Claude Code)",
                false, null, false, 0, 0);
            StringAssert.Contains("2.1.220", text);
            StringAssert.Contains("2.1.218", text);
            StringAssert.DoesNotContain("(Claude Code)", text);
        }

        [Test]
        public void DescribeCliUpdateState_ARunningOrFinishedUpdateOutranksTheCheck()
        {
            long start = 10 * System.TimeSpan.TicksPerSecond;
            string running = SettingsView.DescribeCliUpdateState(false, true, "2.1.220", "2.1.218", true, null,
                false, start, start + 7 * System.TimeSpan.TicksPerSecond);
            StringAssert.Contains("7", running);

            var failed = new CliInstallResult { Success = false, Failure = CliInstallFailureKind.CommandFailed,
                ExitCode = 1, LastLine = "EACCES" };
            string failedText = SettingsView.DescribeCliUpdateState(false, true, "2.1.220", "2.1.218", false, failed, false, 0, 0);
            StringAssert.Contains("EACCES", failedText);
        }

        [Test]
        public void DescribeCliUpdateState_TheDoneNoticeEndsWithTheReconnect()
        {
            var done = new CliInstallResult { Success = true, LastLine = "Successfully updated from 2.1.218 to 2.1.220" };
            string pending = SettingsView.DescribeCliUpdateState(false, true, "2.1.220", "2.1.218", false, done,
                true, 0, 0);
            StringAssert.Contains("Successfully updated", pending);

            // Reconnected: the probe now reads the new binary and the line
            // reports the check result again instead of the old notice.
            string after = SettingsView.DescribeCliUpdateState(false, true, "2.1.220", "2.1.220 (Claude Code)",
                false, done, false, 0, 0);
            StringAssert.DoesNotContain("Successfully updated", after);
            StringAssert.Contains("2.1.220", after);
        }
    }
}
