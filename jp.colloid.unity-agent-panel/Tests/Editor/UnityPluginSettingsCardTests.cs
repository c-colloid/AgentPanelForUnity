using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops.UnityPlugin;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Stream B glue (design note 2026-09-10 section 2): the steering
    /// toggle's settings field is next-spawn-only like extensionProfilesEnabled,
    /// and the status line covers every detector state.
    /// </summary>
    [TestFixture]
    public class UnityPluginSettingsCardTests
    {
        [Test]
        public void SteeringField_DefaultsOn()
        {
            Assert.IsTrue(new PanelSettings().unityPluginSteeringEnabled);
        }

        [Test]
        public void SteeringField_ChangeRequiresReconnect()
        {
            var a = new PanelSettings { unityPluginSteeringEnabled = true };
            var b = new PanelSettings { unityPluginSteeringEnabled = false };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
            b.unityPluginSteeringEnabled = true;
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void SteeringField_CarriedByNextSpawnSnapshot()
        {
            var source = new PanelSettings { unityPluginSteeringEnabled = false };
            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);
            Assert.IsFalse(clone.unityPluginSteeringEnabled);
            source.unityPluginSteeringEnabled = true;
            Assert.IsFalse(clone.unityPluginSteeringEnabled, "snapshot must not alias the live settings");
        }

        private static UnityPluginStatus Status(UnityPluginState state, bool sessionObserved = false,
            string version = "0.1.2-beta", string error = "")
        {
            return new UnityPluginStatus
            {
                State = state, SessionObserved = sessionObserved, Version = version, ErrorMessage = error
            };
        }

        [Test]
        public void DescribeStatus_EveryState_NonEmptyAndDistinct()
        {
            string cli = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.CliUnavailable));
            string none = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.NotInstalled));
            string disabled = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.Disabled));
            string installed = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.EnabledNotLoaded, false));
            string notLoaded = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.EnabledNotLoaded, true));
            string loaded = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.Loaded, true));
            string error = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.LoadError, true, error: "bad manifest"));
            string[] all = { cli, none, disabled, installed, notLoaded, loaded, error };
            for (int i = 0; i < all.Length; i++)
            {
                Assert.IsNotEmpty(all[i]);
                for (int j = i + 1; j < all.Length; j++)
                {
                    Assert.AreNotEqual(all[i], all[j], "states " + i + " and " + j + " read the same");
                }
            }
            StringAssert.Contains("0.1.2-beta", installed);
            StringAssert.Contains("0.1.2-beta", loaded);
            StringAssert.Contains("bad manifest", error);
        }

        [Test]
        public void DescribeStatus_PreConnectInstalled_DoesNotClaimNotLoaded()
        {
            // Section 1.3: without a session observed, "installed" only.
            string installed = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.EnabledNotLoaded, false));
            string notLoaded = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.EnabledNotLoaded, true));
            Assert.AreEqual(L10n.F(L10n.S.SettingsUnityPluginStatusInstalledFmt, "0.1.2-beta"), installed);
            Assert.AreEqual(L10n.F(L10n.S.SettingsUnityPluginStatusEnabledNotLoadedFmt, "0.1.2-beta"), notLoaded);
        }

        [Test]
        public void DescribeStatus_UnknownVersion_ShowsQuestionMark_NullStatus_Empty()
        {
            string s = SettingsView.DescribeUnityPluginStatus(Status(UnityPluginState.Loaded, true, version: string.Empty));
            StringAssert.Contains("v?", s);
            Assert.AreEqual(string.Empty, SettingsView.DescribeUnityPluginStatus(null));
        }

        [Test]
        public void DescribeFailure_NamesStageExitCodeAndLastLine()
        {
            var r = new UnityPluginInstallResult
            {
                Success = false, Stage = UnityPluginInstallStage.MarketplaceAdd, ExitCode = 1, LastLine = "git not found"
            };
            string s = SettingsView.DescribeUnityPluginFailure(r);
            StringAssert.Contains(L10n.S.SettingsUnityPluginStageMarketplace, s);
            StringAssert.Contains("1", s);
            StringAssert.Contains("git not found", s);
            r.Stage = UnityPluginInstallStage.PluginInstall;
            r.LastLine = string.Empty;
            StringAssert.Contains(L10n.S.SettingsUnityPluginStageInstall, SettingsView.DescribeUnityPluginFailure(r));
            Assert.AreEqual(string.Empty, SettingsView.DescribeUnityPluginFailure(null));
        }
    }
}
