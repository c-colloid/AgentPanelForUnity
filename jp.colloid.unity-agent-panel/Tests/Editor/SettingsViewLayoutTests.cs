using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-05 UI redesign, settings S2, then the 2026-09-17 settings
    /// redesign phase 1: the Settings root is a column (reconnect banner,
    /// title, tab strip, then the scroll) and the cards sit on five tab
    /// bodies inside the scroll. Same headless CreateGUI harness as
    /// AgentPanelWindowTests -- SettingsView has no standalone build seam,
    /// so the window is the smallest thing that builds it.
    /// </summary>
    [TestFixture]
    public class SettingsViewLayoutTests
    {
        /// <summary>
        /// 2026-09-08: CreateGUI applies the persisted language setting
        /// (Auto resolves to the OS language); restore the suite default
        /// after every test so later fixtures see English -- see the
        /// regression note on SceneMarkerPinTests.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            UnityEditor.SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [TearDown]
        public void RestoreLanguage()
        {
            L10n.OverrideForTests(null);
            UnityEditor.SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [Test]
        public void SettingsRoot_HoldsBannerTitleTabsThenScroll_AndFiveTabs()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                Assert.IsNotNull(root, "settings root must keep the uap-settings class");

                VisualElement banner = root.Q(className: "uap-settings-banner");
                VisualElement scroll = root.Q(className: "uap-settings-scroll");
                Assert.IsNotNull(banner, "the reconnect banner must exist (hidden until needed)");
                Assert.IsNotNull(scroll, "the scrolling body must carry uap-settings-scroll");
                Assert.IsInstanceOf<ScrollView>(scroll);
                Assert.Less(root.IndexOf(banner), root.IndexOf(scroll),
                    "the banner sits ABOVE the scroll so it stays visible while scrolling");
                // The banner mirrors SettingsChangeDetector against the
                // hub's last-spawn snapshot. That snapshot is process
                // state (null until some spawn -- another fixture's test
                // double included -- has happened), so pin the mirror
                // rather than one fixed answer; the fixed answer would make
                // this test pass or fail depending on run order.
                bool pending = Model.SettingsChangeDetector.RequiresReconnect(
                    Integration.AgentHub.LastSpawnedSettingsSnapshot,
                    Model.PanelStateStore.instance.Settings,
                    Integration.AgentHub.LastSpawnedCustomInstructions,
                    root.Q<TextField>(className: "uap-settings-custom-field")?.value ?? string.Empty);
                Assert.AreEqual(pending ? DisplayStyle.Flex : DisplayStyle.None, banner.style.display.value,
                    "the banner shows exactly when a next-spawn-only change is pending");
                Assert.IsNotNull(banner.Q<Button>(className: "uap-settings-btn--primary"),
                    "the banner carries the one action that resolves it");

                VisualElement title = root.Q(className: "uap-settings-titlebar");
                VisualElement tabs = root.Q(className: "uap-settings-tabs");
                Assert.IsNotNull(title);
                Assert.IsNotNull(title.Q(className: "uap-settings-title"));
                Assert.IsNotNull(title.Q<TextField>(className: "uap-settings-search"),
                    "the search field sits in the title bar (phase 3)");
                Assert.IsNotNull(tabs, "the tab strip must exist");
                Assert.Less(root.IndexOf(banner), root.IndexOf(title));
                Assert.Less(root.IndexOf(title), root.IndexOf(tabs));
                Assert.Less(root.IndexOf(tabs), root.IndexOf(scroll),
                    "title and tabs sit ABOVE the scroll so they stay put while a tab body scrolls");

                var tabButtons = tabs.Query<Button>(className: "uap-settings-tab").ToList();
                Assert.AreEqual(SettingsView.TabCount, tabButtons.Count, "five tabs");
                Assert.IsTrue(tabButtons[0].ClassListContains("uap-settings-tab--active"),
                    "Overview is the default tab when nothing was remembered");
                Assert.AreEqual(L10n.S.SettingsTabOverview,
                    tabButtons[0].Q<Label>(className: "uap-settings-tab-label").text);
                for (int i = 1; i < tabButtons.Count; i++)
                {
                    Assert.IsFalse(tabButtons[i].ClassListContains("uap-settings-tab--active"));
                }

                var bodies = scroll.Query(className: "uap-settings-tab-body").ToList();
                Assert.AreEqual(SettingsView.TabCount, bodies.Count, "one body per tab");
                Assert.AreEqual(DisplayStyle.Flex, bodies[(int)SettingsTab.Overview].style.display.value);
                Assert.AreEqual(DisplayStyle.None, bodies[(int)SettingsTab.Agent].style.display.value);

                // Conversation and Model keep the two lead card slots of the
                // Agent tab (the 2026-08-14 order SettingsViewSectionIconTests
                // relies on); the danger zone closes that tab (D7).
                var agentCards = bodies[(int)SettingsTab.Agent].Query(className: "uap-settings-card").ToList();
                Assert.AreEqual(5, agentCards.Count);
                Assert.AreEqual("uap-card-" + SettingsView.ConversationCardId, agentCards[0].name);
                Assert.AreEqual("uap-card-" + SettingsView.ModelCardId, agentCards[1].name);
                Assert.AreEqual("uap-card-" + SettingsView.DangerCardId, agentCards[4].name);
                Label firstTitle = agentCards[0].Q<Label>(className: "uap-settings-card-title");
                Assert.IsNotNull(firstTitle);
                Assert.AreEqual(L10n.S.SettingsSectionConversation, firstTitle.text);

                // The Overview holds no control: two read-only cards and the
                // footer that replaced the About card.
                VisualElement overview = bodies[(int)SettingsTab.Overview];
                Assert.AreEqual(2, overview.Query(className: "uap-settings-card").ToList().Count);
                Assert.AreEqual(0, overview.Query<Toggle>().ToList().Count);
                Assert.AreEqual(0, overview.Query<TextField>().ToList().Count);
                Assert.IsNotNull(overview.Q(className: "uap-settings-overview-footer"));
                Assert.IsNotNull(overview.Q(className: "uap-settings-version-pill"));

                var cards = root.Query(className: "uap-settings-card").ToList();
                Assert.GreaterOrEqual(cards.Count, 17);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
