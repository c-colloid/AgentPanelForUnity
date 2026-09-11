using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guards for the CLI binary version probe fix
    /// (docs/design-notes/2026-08-01-cli-binary-version-probe.md):
    /// <see cref="CliVersionProbe.ParseVersionOutput"/> (raw `--version`
    /// stdout -&gt; stored form) and
    /// <see cref="SettingsView.ResolveCliVersionDisplay"/> (the About row's
    /// live &gt; persisted-init &gt; binary-probe precedence). Both are
    /// exercised here without spawning a real subprocess or AgentClient --
    /// the async subprocess plumbing in <c>CliVersionProbe.BeginProbe</c>
    /// itself is intentionally NOT covered by an automated test (it would
    /// require a real "claude" binary on the test machine's PATH/manual
    /// path, which EditMode tests cannot assume).
    /// </summary>
    public class CliVersionProbeTests
    {
        // -- CliVersionProbe.ParseVersionOutput --------------------------------------

        [Test]
        public void ParseVersionOutput_RealCliOutput_ReturnsTrimmedFirstLine()
        {
            Assert.AreEqual("2.1.218 (Claude Code)",
                CliVersionProbe.ParseVersionOutput("2.1.218 (Claude Code)\n"));
        }

        [Test]
        public void ParseVersionOutput_TrimsSurroundingWhitespace()
        {
            Assert.AreEqual("2.1.218 (Claude Code)",
                CliVersionProbe.ParseVersionOutput("   2.1.218 (Claude Code)   \n"));
        }

        [Test]
        public void ParseVersionOutput_CrLf_IsNormalized()
        {
            Assert.AreEqual("2.1.218 (Claude Code)",
                CliVersionProbe.ParseVersionOutput("2.1.218 (Claude Code)\r\n"));
        }

        [Test]
        public void ParseVersionOutput_SkipsLeadingBlankLines()
        {
            Assert.AreEqual("2.1.218 (Claude Code)",
                CliVersionProbe.ParseVersionOutput("\n\n2.1.218 (Claude Code)\nsome trailer line\n"));
        }

        [Test]
        public void ParseVersionOutput_NullOrEmpty_ReturnsEmptyString_NeverNull()
        {
            Assert.AreEqual(string.Empty, CliVersionProbe.ParseVersionOutput(null));
            Assert.AreEqual(string.Empty, CliVersionProbe.ParseVersionOutput(string.Empty));
        }

        [Test]
        public void ParseVersionOutput_WhitespaceOnly_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, CliVersionProbe.ParseVersionOutput("   \n\t\n  "));
        }

        // -- SettingsView.ResolveCliVersionDisplay -----------------------------------

        [Test]
        public void ResolveCliVersionDisplay_LiveVersion_WinsOverEverything()
        {
            bool fromBinaryProbeOnly;
            string result = SettingsView.ResolveCliVersionDisplay(
                "2.1.218", "2.1.100", "2.0.0", out fromBinaryProbeOnly);
            Assert.AreEqual("2.1.218", result);
            Assert.IsFalse(fromBinaryProbeOnly);
        }

        [Test]
        public void ResolveCliVersionDisplay_NoLive_FallsBackToPersistedInit()
        {
            bool fromBinaryProbeOnly;
            string result = SettingsView.ResolveCliVersionDisplay(
                null, "2.1.100", "2.0.0", out fromBinaryProbeOnly);
            Assert.AreEqual("2.1.100", result);
            Assert.IsFalse(fromBinaryProbeOnly);
        }

        [Test]
        public void ResolveCliVersionDisplay_NoLiveOrPersisted_FallsBackToBinaryProbe()
        {
            bool fromBinaryProbeOnly;
            string result = SettingsView.ResolveCliVersionDisplay(
                null, null, "2.0.0", out fromBinaryProbeOnly);
            Assert.AreEqual("2.0.0", result);
            Assert.IsTrue(fromBinaryProbeOnly,
                "the idle-resumed-connection fix: a binary-only result must be flagged " +
                "so the caller shows the \"not yet connected\" variant, not a confirmed-live one");
        }

        [Test]
        public void ResolveCliVersionDisplay_NothingKnown_ReturnsNull()
        {
            bool fromBinaryProbeOnly;
            string result = SettingsView.ResolveCliVersionDisplay(null, null, null, out fromBinaryProbeOnly);
            Assert.IsNull(result);
            Assert.IsFalse(fromBinaryProbeOnly);
        }

        [Test]
        public void ResolveCliVersionDisplay_EmptyStringsTreatedSameAsNull()
        {
            bool fromBinaryProbeOnly;
            string result = SettingsView.ResolveCliVersionDisplay(
                string.Empty, string.Empty, "2.0.0", out fromBinaryProbeOnly);
            Assert.AreEqual("2.0.0", result);
            Assert.IsTrue(fromBinaryProbeOnly);
        }
    }
}
