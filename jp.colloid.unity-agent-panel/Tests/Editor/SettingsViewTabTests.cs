using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-17 settings redesign, phase 1 (docs/design-notes/2026-09-17-
    /// settings-redesign-plan.md D1/D9): five tabs, a remembered tab, and
    /// one "go to this setting" entry point. The pure pieces (card -> tab
    /// table, int -> tab clamp) are pinned directly; the DOM side rides the
    /// same headless CreateGUI harness as SettingsViewLayoutTests.
    /// </summary>
    [TestFixture]
    public class SettingsViewTabTests
    {
        [SetUp]
        public void SetUp()
        {
            SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
            SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [Test]
        public void TabFor_EveryCardId_LandsOnItsTab()
        {
            Assert.AreEqual(SettingsTab.Agent, SettingsView.TabFor(SettingsView.ConversationCardId));
            Assert.AreEqual(SettingsTab.Agent, SettingsView.TabFor(SettingsView.ModelCardId));
            Assert.AreEqual(SettingsTab.Agent, SettingsView.TabFor(SettingsView.InstructionsCardId));
            Assert.AreEqual(SettingsTab.Agent, SettingsView.TabFor(SettingsView.QuickActionsCardId));
            Assert.AreEqual(SettingsTab.Agent, SettingsView.TabFor(SettingsView.DangerCardId));
            Assert.AreEqual(SettingsTab.Panel, SettingsView.TabFor(SettingsView.DisplayCardId));
            Assert.AreEqual(SettingsTab.Panel, SettingsView.TabFor(SettingsView.AppearanceCardId));
            Assert.AreEqual(SettingsTab.Panel, SettingsView.TabFor(SettingsView.NotificationsCardId));
            Assert.AreEqual(SettingsTab.Panel, SettingsView.TabFor(SettingsView.ConsoleErrorsCardId));
            Assert.AreEqual(SettingsTab.Unity, SettingsView.TabFor(SettingsView.UapOpsCardId));
            Assert.AreEqual(SettingsTab.Unity, SettingsView.TabFor(SettingsView.ProfilesCardId));
            Assert.AreEqual(SettingsTab.Unity, SettingsView.TabFor(SettingsView.UloopCardId));
            Assert.AreEqual(SettingsTab.Unity, SettingsView.TabFor(SettingsView.UnityPluginCardId));
            Assert.AreEqual(SettingsTab.Connection, SettingsView.TabFor(SettingsView.AgentCardId));
            Assert.AreEqual(SettingsTab.Connection, SettingsView.TabFor(SettingsView.DiagnosticsCardId));
            Assert.AreEqual(SettingsTab.Connection, SettingsView.TabFor(SettingsView.ProCardId));
            Assert.AreEqual(SettingsTab.Overview, SettingsView.TabFor(SettingsView.SetupCardId));
            Assert.AreEqual(SettingsTab.Overview, SettingsView.TabFor("no-such-card"));
            Assert.AreEqual(SettingsTab.Overview, SettingsView.TabFor(null));
        }

        [Test]
        public void ClampTab_OutOfRange_FallsBackToOverview()
        {
            Assert.AreEqual(SettingsTab.Overview, SettingsView.ClampTab(0));
            Assert.AreEqual(SettingsTab.Connection, SettingsView.ClampTab(4));
            Assert.AreEqual(SettingsTab.Overview, SettingsView.ClampTab(5));
            Assert.AreEqual(SettingsTab.Overview, SettingsView.ClampTab(-1));
            Assert.AreEqual(SettingsTab.Overview, SettingsView.ClampTab(int.MaxValue));
        }

        [Test]
        public void TabStateKey_IsStable()
        {
            Assert.AreEqual("Colloid.AgentPanel.Settings.Tab", SettingsView.TabStateKey);
        }

        [Test]
        public void TabLabels_AreDistinctPerTab_AndShortLabelsNeverLongerThanFull()
        {
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < SettingsView.TabCount; i++)
            {
                var tab = (SettingsTab)i;
                string full = SettingsView.TabLabel(tab);
                string brief = SettingsView.TabShortLabel(tab);
                Assert.IsFalse(string.IsNullOrEmpty(full));
                Assert.IsFalse(string.IsNullOrEmpty(brief));
                Assert.IsTrue(seen.Add(full), "duplicate tab label: " + full);
                Assert.LessOrEqual(brief.Length, full.Length, "the narrow label must not be longer");
            }
        }

        [Test]
        public void RememberedTab_ReopensOnBuild()
        {
            SessionState.SetInt(SettingsView.TabStateKey, (int)SettingsTab.Unity);
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var tabButtons = root.Query<Button>(className: "uap-settings-tab").ToList();
                Assert.IsTrue(tabButtons[(int)SettingsTab.Unity].ClassListContains("uap-settings-tab--active"));
                Assert.IsFalse(tabButtons[(int)SettingsTab.Overview].ClassListContains("uap-settings-tab--active"));
                var bodies = root.Query(className: "uap-settings-tab-body").ToList();
                Assert.AreEqual(DisplayStyle.Flex, bodies[(int)SettingsTab.Unity].style.display.value);
                Assert.AreEqual(DisplayStyle.None, bodies[(int)SettingsTab.Overview].style.display.value);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void RequestShowSettingsCard_SelectsTheTab_OpensACollapsedCard_AndRemembersIt()
        {
            SessionState.EraseBool(SettingsView.SectionDisclosureKey(SettingsView.UapOpsCardId));
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                window.RequestShowSettingsCard(SettingsTab.Unity, SettingsView.UapOpsCardId);

                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var tabButtons = root.Query<Button>(className: "uap-settings-tab").ToList();
                Assert.IsTrue(tabButtons[(int)SettingsTab.Unity].ClassListContains("uap-settings-tab--active"));
                VisualElement card = root.Q("uap-card-" + SettingsView.UapOpsCardId);
                Assert.IsNotNull(card);
                Foldout foldout = card.Q<Foldout>(className: "uap-settings-section-foldout");
                Assert.IsNotNull(foldout);
                Assert.IsTrue(foldout.value, "a deep link opens the collapsed card it points at");
                Assert.AreEqual((int)SettingsTab.Unity, SessionState.GetInt(SettingsView.TabStateKey, -1),
                    "the selected tab is remembered for the editor session");
            }
            finally
            {
                Object.DestroyImmediate(window);
                SessionState.EraseBool(SettingsView.SectionDisclosureKey(SettingsView.UapOpsCardId));
            }
        }

        [Test]
        public void RequestShowSettingsCard_BeforeBuild_IsAppliedByCreateGUI()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.RequestShowSettingsCard(SettingsTab.Connection, SettingsView.AgentCardId);
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var tabButtons = root.Query<Button>(className: "uap-settings-tab").ToList();
                Assert.IsTrue(tabButtons[(int)SettingsTab.Connection].ClassListContains("uap-settings-tab--active"));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void OverviewIcon_EveryTone_YieldsAnElement()
        {
            foreach (SettingsView.OverviewTone tone in System.Enum.GetValues(typeof(SettingsView.OverviewTone)))
            {
                Assert.IsNotNull(SettingsView.CreateOverviewIcon(tone), tone.ToString());
            }
        }
    }
}
