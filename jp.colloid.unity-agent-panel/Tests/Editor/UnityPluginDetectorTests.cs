using System.Collections.Generic;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Ops.UnityPlugin;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure detection tests for Unity's official plugin (docs/design-notes/
    /// 2026-09-10-unity-official-plugin-integration.md sections 1.2-1.3).
    /// The JSON shapes are the ones a real `claude plugin install` wrote on
    /// 2026-09-10 (docs/verify/2026-09-10-unity-plugin-cli-install.txt).
    /// </summary>
    [TestFixture]
    public class UnityPluginDetectorTests
    {
        private const string InstalledJson =
            "{\"version\":2,\"plugins\":{\"unity@unity-agent-plugin\":[{\"scope\":\"user\","
            + "\"installPath\":\"/root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta\","
            + "\"version\":\"0.1.2-beta\",\"installedAt\":\"2026-09-10T11:08:27.164Z\","
            + "\"lastUpdated\":\"2026-09-10T11:08:27.164Z\","
            + "\"gitCommitSha\":\"673d9c45ceeb0ef46044cd68bcd90fa0254b248f\"}]}}";

        private const string SettingsJson =
            "{\"extraKnownMarketplaces\":{\"unity-agent-plugin\":{\"source\":{\"source\":\"github\","
            + "\"repo\":\"Unity-Technologies/unity-agent-plugin\"}}},"
            + "\"enabledPlugins\":{\"unity@unity-agent-plugin\":true}}";

        // -- identity ---------------------------------------------------------

        [Test]
        public void Matches_BareNameOrQualifiedSource()
        {
            Assert.IsTrue(UnityPluginIdentity.Matches("unity", "unity@unity-agent-plugin"));
            Assert.IsTrue(UnityPluginIdentity.Matches("unity", null));
            Assert.IsTrue(UnityPluginIdentity.Matches(null, "unity@unity-agent-plugin"));
            Assert.IsTrue(UnityPluginIdentity.Matches("unity@unity-agent-plugin", null));
            Assert.IsFalse(UnityPluginIdentity.Matches("unity-ui-toolkit", null));
            Assert.IsFalse(UnityPluginIdentity.Matches("Unity", null), "identifiers are case-sensitive");
            Assert.IsFalse(UnityPluginIdentity.Matches(null, null));
        }

        // -- installed_plugins.json ------------------------------------------------

        [Test]
        public void ParseInstalledPlugins_MeasuredShape_ReturnsUserEntry()
        {
            InstalledPluginEntry entry = UnityPluginDetector.ParseInstalledPlugins(InstalledJson);
            Assert.IsNotNull(entry);
            Assert.AreEqual("user", entry.Scope);
            Assert.AreEqual("0.1.2-beta", entry.Version);
            Assert.AreEqual("/root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta", entry.InstallPath);
            Assert.AreEqual("673d9c45ceeb0ef46044cd68bcd90fa0254b248f", entry.GitCommitSha);
        }

        [Test]
        public void ParseInstalledPlugins_PrefersUserScopeOverOthers()
        {
            string json = "{\"version\":2,\"plugins\":{\"unity@unity-agent-plugin\":["
                + "{\"scope\":\"project\",\"version\":\"0.1.0-beta\"},"
                + "{\"scope\":\"user\",\"version\":\"0.1.2-beta\"}]}}";
            Assert.AreEqual("0.1.2-beta", UnityPluginDetector.ParseInstalledPlugins(json).Version);
        }

        [Test]
        public void ParseInstalledPlugins_NoUserScope_FallsBackToFirst()
        {
            string json = "{\"version\":2,\"plugins\":{\"unity@unity-agent-plugin\":["
                + "{\"scope\":\"project\",\"version\":\"0.1.0-beta\"}]}}";
            InstalledPluginEntry entry = UnityPluginDetector.ParseInstalledPlugins(json);
            Assert.AreEqual("project", entry.Scope);
        }

        [Test]
        public void ParseInstalledPlugins_OtherPluginsOnly_ReturnsNull()
        {
            string json = "{\"version\":2,\"plugins\":{\"other@mkt\":[{\"scope\":\"user\"}]}}";
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins(json));
        }

        [Test]
        public void ParseInstalledPlugins_EmptyMalformedOrNonObject_ReturnsNull()
        {
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins(null));
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins(string.Empty));
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins("{not json"));
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins("[]"));
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins("{\"version\":2}"));
            Assert.IsNull(UnityPluginDetector.ParseInstalledPlugins(
                "{\"plugins\":{\"unity@unity-agent-plugin\":\"not-an-array\"}}"));
        }

        // -- settings.json enabledPlugins ---------------------------------------

        [Test]
        public void IsEnabledInSettingsJson_MeasuredShape_True()
        {
            Assert.IsTrue(UnityPluginDetector.IsEnabledInSettingsJson(SettingsJson));
        }

        [Test]
        public void IsEnabledInSettingsJson_BareKeyForm_True()
        {
            Assert.IsTrue(UnityPluginDetector.IsEnabledInSettingsJson("{\"enabledPlugins\":{\"unity\":true}}"));
        }

        [Test]
        public void IsEnabledInSettingsJson_ExplicitFalse_False()
        {
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson(
                "{\"enabledPlugins\":{\"unity@unity-agent-plugin\":false}}"));
        }

        [Test]
        public void IsEnabledInSettingsJson_MissingKeyEmptyOrMalformed_False()
        {
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson(null));
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson(string.Empty));
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson("{}"));
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson("{\"enabledPlugins\":{\"other\":true}}"));
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson("{\"enabledPlugins\":\"unity\"}"));
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson("{\"enabledPlugins\":{\"unity\":\"true\"}}"),
                "a string \"true\" is not the boolean the CLI writes");
            Assert.IsFalse(UnityPluginDetector.IsEnabledInSettingsJson("{oops"));
        }

        // -- Resolve: the section 1.2 state table --------------------------------

        private static InstalledPluginEntry Installed()
        {
            return UnityPluginDetector.ParseInstalledPlugins(InstalledJson);
        }

        private static List<PluginEntry> LoadedInit()
        {
            return new List<PluginEntry>
            {
                new PluginEntry
                {
                    Name = "unity", Source = "unity@unity-agent-plugin", Version = "0.1.2-beta",
                    Path = "/root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta"
                }
            };
        }

        [Test]
        public void Resolve_CliUnavailable_WinsOverEverything()
        {
            UnityPluginStatus s = UnityPluginDetector.Resolve(false, Installed(), true, LoadedInit(), null);
            Assert.AreEqual(UnityPluginState.CliUnavailable, s.State);
        }

        [Test]
        public void Resolve_NoSession_NothingOnDisk_NotInstalled()
        {
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, null, false, null, null);
            Assert.AreEqual(UnityPluginState.NotInstalled, s.State);
            Assert.IsFalse(s.SessionObserved);
        }

        [Test]
        public void Resolve_NoSession_EnabledOnlyWithoutInstallRecord_NotInstalled()
        {
            // enabledPlugins without a record is a stale flag (cache pruned);
            // the install button must still be offered.
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, null, true, null, null);
            Assert.AreEqual(UnityPluginState.NotInstalled, s.State);
        }

        [Test]
        public void Resolve_NoSession_InstalledButNotEnabled_Disabled()
        {
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), false, null, null);
            Assert.AreEqual(UnityPluginState.Disabled, s.State);
            Assert.AreEqual("0.1.2-beta", s.Version);
        }

        [Test]
        public void Resolve_NoSession_InstalledAndEnabled_EnabledNotLoaded_WithoutSessionObserved()
        {
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), true, null, null);
            Assert.AreEqual(UnityPluginState.EnabledNotLoaded, s.State);
            Assert.IsFalse(s.SessionObserved, "pre-connect: the card must say 'installed', not 'not loaded'");
            Assert.AreEqual("0.1.2-beta", s.Version);
            Assert.AreEqual("/root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta", s.InstallPath);
        }

        [Test]
        public void Resolve_Session_ListsPlugin_Loaded_TakesVersionAndPathFromInit()
        {
            var init = LoadedInit();
            init[0].Version = "0.2.0-beta";
            init[0].Path = "/elsewhere";
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), true, init, null);
            Assert.AreEqual(UnityPluginState.Loaded, s.State);
            Assert.IsTrue(s.SessionObserved);
            Assert.AreEqual("0.2.0-beta", s.Version, "the session's own report beats the disk record");
            Assert.AreEqual("/elsewhere", s.InstallPath);
        }

        [Test]
        public void Resolve_Session_ListsPlugin_LoadedEvenWithoutDiskRecord()
        {
            // e.g. loaded via --plugin-dir, or a config dir the probe could not read
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, null, false, LoadedInit(), null);
            Assert.AreEqual(UnityPluginState.Loaded, s.State);
            Assert.AreEqual("0.1.2-beta", s.Version);
        }

        [Test]
        public void Resolve_Session_EmptyPlugins_InstalledAndEnabled_EnabledNotLoaded_WithSessionObserved()
        {
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), true, new List<PluginEntry>(), null);
            Assert.AreEqual(UnityPluginState.EnabledNotLoaded, s.State);
            Assert.IsTrue(s.SessionObserved, "post-connect: the card may say 'this chat did not load it'");
        }

        [Test]
        public void Resolve_Session_OtherPluginsOnly_NothingOnDisk_NotInstalled()
        {
            var init = new List<PluginEntry> { new PluginEntry { Name = "other", Source = "other@mkt" } };
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, null, false, init, null);
            Assert.AreEqual(UnityPluginState.NotInstalled, s.State);
        }

        [Test]
        public void Resolve_Session_PluginError_LoadError_CarriesMessage()
        {
            var errors = new List<PluginError>
            {
                new PluginError { Plugin = "unity", ErrorType = "invalid_manifest", Message = "bad plugin.json" }
            };
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), true, new List<PluginEntry>(), errors);
            Assert.AreEqual(UnityPluginState.LoadError, s.State);
            Assert.AreEqual("bad plugin.json", s.ErrorMessage);
        }

        [Test]
        public void Resolve_Session_ErrorForOtherPlugin_Ignored()
        {
            var errors = new List<PluginError> { new PluginError { Plugin = "other", Message = "x" } };
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), true, LoadedInit(), errors);
            Assert.AreEqual(UnityPluginState.Loaded, s.State);
            Assert.AreEqual(string.Empty, s.ErrorMessage);
        }

        [Test]
        public void Resolve_NullEntriesInLists_AreSkipped()
        {
            var init = new List<PluginEntry> { null };
            var errors = new List<PluginError> { null };
            UnityPluginStatus s = UnityPluginDetector.Resolve(true, Installed(), true, init, errors);
            Assert.AreEqual(UnityPluginState.EnabledNotLoaded, s.State);
        }
    }
}
