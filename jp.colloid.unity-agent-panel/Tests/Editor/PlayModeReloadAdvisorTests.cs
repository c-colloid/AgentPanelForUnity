using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEditor;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design note 2026-09-10 section 4: pins PlayModeReloadAdvisor.
    /// ReloadsOnPlay against the exact predicate
    /// ReloadLifecycleLiveTests.cs (around line 155) uses to gate its own
    /// "Play reloads the domain" assumption -- a reload is skipped only
    /// when Enter Play Mode Options are enabled AND DisableDomainReload is
    /// one of the selected options.
    /// </summary>
    [TestFixture]
    public class PlayModeReloadAdvisorTests
    {
        [Test]
        public void OptionsDisabled_AlwaysReloads_RegardlessOfFlags()
        {
            Assert.IsTrue(PlayModeReloadAdvisor.ReloadsOnPlay(false, EnterPlayModeOptions.None));
            Assert.IsTrue(PlayModeReloadAdvisor.ReloadsOnPlay(false, EnterPlayModeOptions.DisableDomainReload));
        }

        [Test]
        public void OptionsEnabled_WithoutDisableDomainReload_StillReloads()
        {
            Assert.IsTrue(PlayModeReloadAdvisor.ReloadsOnPlay(true, EnterPlayModeOptions.None));
        }

        [Test]
        public void OptionsEnabled_WithDisableDomainReload_DoesNotReload()
        {
            Assert.IsFalse(PlayModeReloadAdvisor.ReloadsOnPlay(true, EnterPlayModeOptions.DisableDomainReload));
        }

        [Test]
        public void OptionsEnabled_WithDisableDomainReload_CombinedWithOtherFlags_DoesNotReload()
        {
            Assert.IsFalse(PlayModeReloadAdvisor.ReloadsOnPlay(true,
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload));
        }

        [Test]
        public void OptionsEnabled_DisableSceneReloadOnly_StillReloadsTheDomain()
        {
            // DisableSceneReload alone does not touch the domain -- only
            // DisableDomainReload does. This is the case the design note
            // calls out explicitly: it is easy to assume "options enabled"
            // alone means no reload.
            Assert.IsTrue(PlayModeReloadAdvisor.ReloadsOnPlay(true, EnterPlayModeOptions.DisableSceneReload));
        }
    }
}
