using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// `--plugin-dir` composition (design note section 1.5) and the one
    /// invariant the whole Unity-plugin integration rests on: the panel
    /// never passes `--bare`, because that is the flag that would stop a
    /// marketplace-installed plugin from loading under `-p`.
    /// </summary>
    [TestFixture]
    public class AgentClientOptionsPluginDirTests
    {
        private static AgentClientOptions Base()
        {
            return new AgentClientOptions { CliPath = "claude", WorkingDirectory = "/p" };
        }

        [Test]
        public void NullOrEmptyPluginDirs_ArgumentStringUnchanged()
        {
            string without = AgentClient.BuildArguments(Base());
            var opts = Base();
            opts.PluginDirs = new List<string>();
            Assert.AreEqual(without, AgentClient.BuildArguments(opts));
            opts.PluginDirs = new List<string> { null, string.Empty };
            Assert.AreEqual(without, AgentClient.BuildArguments(opts));
            StringAssert.DoesNotContain("--plugin-dir", without);
        }

        [Test]
        public void OnePluginDir_OneFlag()
        {
            var opts = Base();
            opts.PluginDirs = new List<string> { "/root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta" };
            string args = AgentClient.BuildArguments(opts);
            StringAssert.EndsWith(" --plugin-dir /root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta", args);
        }

        [Test]
        public void TwoPluginDirs_TwoFlags_InOrder_QuotedWhenNeeded()
        {
            var opts = Base();
            opts.PluginDirs = new List<string> { "/a/one", "C:\\Users\\me\\my plugin" };
            string args = AgentClient.BuildArguments(opts);
            int first = args.IndexOf(" --plugin-dir /a/one", System.StringComparison.Ordinal);
            int second = args.IndexOf(" --plugin-dir \"C:\\Users\\me\\my plugin\"", System.StringComparison.Ordinal);
            Assert.Greater(first, 0);
            Assert.Greater(second, first);
        }

        [Test]
        public void PluginDirs_ComeAfterSettings()
        {
            var opts = Base();
            opts.SettingsFilePath = "/p/UserSettings/AgentPanel/settings.json";
            opts.PluginDirs = new List<string> { "/plug" };
            string args = AgentClient.BuildArguments(opts);
            Assert.Less(args.IndexOf("--settings", System.StringComparison.Ordinal),
                args.IndexOf("--plugin-dir", System.StringComparison.Ordinal));
        }

        [Test]
        public void ArgumentString_NeverContainsBare()
        {
            var opts = Base();
            opts.PluginDirs = new List<string> { "/plug" };
            opts.SettingsFilePath = "/s.json";
            opts.McpConfigJson = "/m.json";
            opts.AppendSystemPrompt = "x";
            opts.ThinkingDisplaySummarized = true;
            opts.DangerouslySkipPermissions = true;
            string args = AgentClient.BuildArguments(opts);
            StringAssert.DoesNotContain("--bare", args,
                "--bare skips plugin discovery; the Unity-plugin integration relies on plain -p loading it");
        }
    }
}
