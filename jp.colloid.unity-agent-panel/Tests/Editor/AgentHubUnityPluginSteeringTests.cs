using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops.UnityPlugin;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The Unity official plugin steering section (design note 2026-09-10
    /// section 3.2) and its slot in the --append-system-prompt ordering.
    /// </summary>
    [TestFixture]
    public class AgentHubUnityPluginSteeringTests
    {
        private const string Lts = "2022.3.22f1";
        private const string Six = "6000.0.1f1";

        [Test]
        public void NotInstalled_Empty()
        {
            Assert.AreEqual(string.Empty, AgentHub.ComposeUnityPluginSteeringSection(false, true, true, Lts));
        }

        [Test]
        public void ToggleOff_Empty()
        {
            Assert.AreEqual(string.Empty, AgentHub.ComposeUnityPluginSteeringSection(true, false, true, Lts));
        }

        [Test]
        public void Installed_Unity6_UapOpsOff_RoutingLineOnly()
        {
            string s = AgentHub.ComposeUnityPluginSteeringSection(true, true, false, Six);
            Assert.AreEqual(AgentHub.UnityPluginSteeringRoutingLine, s);
            StringAssert.DoesNotContain("uap_search", s, "no search steering when UapOps (and so uap_search) is off");
        }

        [Test]
        public void Installed_Unity6_UapOpsOn_AddsEditorControlLine()
        {
            string s = AgentHub.ComposeUnityPluginSteeringSection(true, true, true, Six);
            Assert.AreEqual(AgentHub.UnityPluginSteeringRoutingLine + "\n" + AgentHub.UnityPluginSteeringEditorControlLine
                + "\n" + AgentHub.UnityPluginSteeringSearchLine, s);
            StringAssert.Contains("uap_search", s);
            StringAssert.Contains("Do NOT install or invoke the `unity` CLI", s);
            StringAssert.DoesNotContain("Unity 6+", s);
        }

        [Test]
        public void Installed_Lts_AddsCaveatWithVersion()
        {
            string s = AgentHub.ComposeUnityPluginSteeringSection(true, true, true, Lts);
            string[] lines = s.Split('\n');
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual(AgentHub.UnityPluginSteeringRoutingLine, lines[0]);
            Assert.AreEqual(AgentHub.UnityPluginSteeringEditorControlLine, lines[1]);
            Assert.AreEqual(AgentHub.UnityPluginSteeringSearchLine, lines[2]);
            StringAssert.Contains("Unity 2022.3.22f1", lines[3]);
            StringAssert.Contains("Unity 6+", lines[3]);
        }

        [Test]
        public void Installed_UnparseableVersion_StillCaveats()
        {
            string s = AgentHub.ComposeUnityPluginSteeringSection(true, true, false, string.Empty);
            StringAssert.Contains("an unknown version", s);
            StringAssert.Contains("Unity 6+", s);
        }

        [Test]
        public void StatusOverload_InstalledMeansEnabledNotLoadedOrLoaded()
        {
            Assert.IsNotEmpty(AgentHub.ComposeUnityPluginSteeringSection(
                new UnityPluginStatus { State = UnityPluginState.EnabledNotLoaded }, true, false, Six));
            Assert.IsNotEmpty(AgentHub.ComposeUnityPluginSteeringSection(
                new UnityPluginStatus { State = UnityPluginState.Loaded }, true, false, Six));
            Assert.IsEmpty(AgentHub.ComposeUnityPluginSteeringSection(
                new UnityPluginStatus { State = UnityPluginState.Disabled }, true, false, Six));
            Assert.IsEmpty(AgentHub.ComposeUnityPluginSteeringSection(
                new UnityPluginStatus { State = UnityPluginState.NotInstalled }, true, false, Six));
            Assert.IsEmpty(AgentHub.ComposeUnityPluginSteeringSection(
                new UnityPluginStatus { State = UnityPluginState.CliUnavailable }, true, false, Six));
            Assert.IsEmpty(AgentHub.ComposeUnityPluginSteeringSection((UnityPluginStatus)null, true, false, Six));
        }

        [Test]
        public void FiveArgOverload_OrdersPluginSectionBetweenUapOpsAndProfiles()
        {
            string s = AgentHub.ComposeAppendSystemPrompt("custom", SubagentCostPolicy.AgentDecides,
                "UAPOPS", "PLUGIN", "PROFILES");
            Assert.AreEqual("custom\n\nUAPOPS\n\nPLUGIN\n\nPROFILES", s);
        }

        [Test]
        public void FiveArgOverload_EmptyPluginSection_MatchesFourArgOverload()
        {
            string four = AgentHub.ComposeAppendSystemPrompt("custom", SubagentCostPolicy.HaikuForSimpleTasks,
                "UAPOPS", "PROFILES");
            string five = AgentHub.ComposeAppendSystemPrompt("custom", SubagentCostPolicy.HaikuForSimpleTasks,
                "UAPOPS", string.Empty, "PROFILES");
            Assert.AreEqual(four, five);
            Assert.AreEqual(four, AgentHub.ComposeAppendSystemPrompt("custom", SubagentCostPolicy.HaikuForSimpleTasks,
                "UAPOPS", null, "PROFILES"));
        }

        [Test]
        public void FiveArgOverload_AllEmpty_Empty()
        {
            Assert.AreEqual(string.Empty, AgentHub.ComposeAppendSystemPrompt(null, SubagentCostPolicy.AgentDecides,
                null, null, null));
        }

        [Test]
        public void SteeringLines_AreAscii()
        {
            string s = AgentHub.ComposeUnityPluginSteeringSection(true, true, true, Lts);
            foreach (char c in s)
            {
                Assert.LessOrEqual((int)c, 0x7F, "steering text must stay ASCII for the command line");
            }
        }
    }
}
