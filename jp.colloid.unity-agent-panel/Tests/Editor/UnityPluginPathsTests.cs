using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Ops.UnityPlugin;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Config-directory resolution (design note section 1.4): CLAUDE_CONFIG_DIR first, then &lt;home&gt;/.claude.</summary>
    [TestFixture]
    public class UnityPluginPathsTests
    {
        [Test]
        public void ResolveConfigDir_EnvVarSet_WinsOverHome()
        {
            var env = new Dictionary<string, string> { { "CLAUDE_CONFIG_DIR", "/tmp/uap-cfg-empty" } };
            Assert.AreEqual("/tmp/uap-cfg-empty", UnityPluginPaths.ResolveConfigDir(env, "/home/u"));
        }

        [Test]
        public void ResolveConfigDir_EnvVarBlank_FallsBackToHome()
        {
            var env = new Dictionary<string, string> { { "CLAUDE_CONFIG_DIR", "   " } };
            Assert.AreEqual(Path.Combine("/home/u", ".claude"), UnityPluginPaths.ResolveConfigDir(env, "/home/u"));
        }

        [Test]
        public void ResolveConfigDir_NoEnvVar_UsesHome()
        {
            Assert.AreEqual(Path.Combine("/home/u", ".claude"),
                UnityPluginPaths.ResolveConfigDir(new Dictionary<string, string>(), "/home/u"));
            Assert.AreEqual(Path.Combine("/home/u", ".claude"), UnityPluginPaths.ResolveConfigDir(null, "/home/u"));
        }

        [Test]
        public void ResolveConfigDir_WindowsHome_UsesHome()
        {
            string home = "C:\\Users\\colloid";
            Assert.AreEqual(Path.Combine(home, ".claude"), UnityPluginPaths.ResolveConfigDir(null, home));
        }

        [Test]
        public void ResolveConfigDir_NothingKnown_Null()
        {
            Assert.IsNull(UnityPluginPaths.ResolveConfigDir(null, null));
            Assert.IsNull(UnityPluginPaths.ResolveConfigDir(new Dictionary<string, string>(), string.Empty));
        }

        [Test]
        public void FilePaths_FollowTheMeasuredLayout()
        {
            string dir = Path.Combine("/root", ".claude");
            Assert.AreEqual(Path.Combine(dir, "settings.json"), UnityPluginPaths.SettingsPath(dir));
            Assert.AreEqual(Path.Combine(dir, "plugins", "installed_plugins.json"),
                UnityPluginPaths.InstalledPluginsPath(dir));
            Assert.IsNull(UnityPluginPaths.SettingsPath(null));
            Assert.IsNull(UnityPluginPaths.InstalledPluginsPath(null));
        }
    }
}
