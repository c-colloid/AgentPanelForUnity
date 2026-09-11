using Colloid.AgentPanel.Ops.UnityPlugin;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Precedence table for the install-progress line (design note section 2.3 items 3-5 and the reload paragraph).</summary>
    [TestFixture]
    public class UnityPluginInstallProgressTests
    {
        private const double Stale = UnityPluginInstallProgress.StaleThresholdSeconds;

        private static UnityPluginInstallProgressResult Eval(bool liveRunning, bool liveCompleted, bool liveSucceeded,
            bool installedNow, bool flagSet, double elapsed)
        {
            return UnityPluginInstallProgress.Evaluate(liveRunning, liveCompleted, liveSucceeded, installedNow,
                flagSet, elapsed, Stale);
        }

        [Test]
        public void NothingInFlight_None()
        {
            var r = Eval(false, false, false, false, false, 0);
            Assert.AreEqual(UnityPluginInstallProgressState.None, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void LiveRunning_Installing_EvenIfFlagIsOld()
        {
            var r = Eval(true, false, false, false, true, Stale + 100);
            Assert.AreEqual(UnityPluginInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void LiveSucceeded_Done_ClearsFlag()
        {
            var r = Eval(false, true, true, false, true, 3);
            Assert.AreEqual(UnityPluginInstallProgressState.Done, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void LiveFailed_Failed_ClearsFlag_EvenIfDetectorSaysInstalled()
        {
            // A failed second stage after a successful first can leave a
            // partial state; the live verdict is still what the user acted
            // on, so show it.
            var r = Eval(false, true, false, true, true, 3);
            Assert.AreEqual(UnityPluginInstallProgressState.Failed, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void FlagAfterReload_DetectorSeesPlugin_Done()
        {
            var r = Eval(false, false, false, true, true, Stale + 100);
            Assert.AreEqual(UnityPluginInstallProgressState.Done, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void FlagAfterReload_Fresh_Installing()
        {
            var r = Eval(false, false, false, false, true, Stale - 1);
            Assert.AreEqual(UnityPluginInstallProgressState.Installing, r.State);
            Assert.IsFalse(r.ShouldClearFlag);
        }

        [Test]
        public void FlagAfterReload_Old_Stalled_ClearsFlag()
        {
            var r = Eval(false, false, false, false, true, Stale);
            Assert.AreEqual(UnityPluginInstallProgressState.Stalled, r.State);
            Assert.IsTrue(r.ShouldClearFlag);
        }

        [Test]
        public void NoFlag_InstalledNow_None()
        {
            // Installed by other means (terminal): nothing to report on the progress line.
            var r = Eval(false, false, false, true, false, 0);
            Assert.AreEqual(UnityPluginInstallProgressState.None, r.State);
        }
    }
}
