using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-05 UI redesign, settings S1/S2: the Settings root is a
    /// column (reconnect banner over the scroll) and the cards sit under
    /// four group headings. Same headless CreateGUI harness as
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
        [TearDown]
        public void RestoreLanguage()
        {
            L10n.OverrideForTests(null);
        }

        [Test]
        public void SettingsRoot_HoldsBannerAboveScroll_AndFourGroupHeadings()
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

                var groups = root.Query<Label>(className: "uap-settings-group").ToList();
                Assert.AreEqual(4, groups.Count, "four topic groups");
                Assert.IsTrue(groups[0].ClassListContains("uap-settings-group--first"));
                Assert.IsFalse(groups[1].ClassListContains("uap-settings-group--first"));

                // Conversation and Model keep the two lead card slots
                // (the 2026-08-14 order SettingsViewSectionIconTests relies on).
                var cards = root.Query(className: "uap-settings-card").ToList();
                Assert.GreaterOrEqual(cards.Count, 15);
                Label firstTitle = cards[0].Q<Label>(className: "uap-settings-card-title");
                Assert.IsNotNull(firstTitle);
                Assert.AreEqual(L10n.S.SettingsSectionConversation, firstTitle.text);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
