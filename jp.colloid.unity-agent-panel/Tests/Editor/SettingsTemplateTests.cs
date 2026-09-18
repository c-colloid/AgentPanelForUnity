using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-18 (design note section 13): the Settings card and Overview
    /// row skeletons are UXML templates instantiated without their
    /// TemplateContainer wrapper, the search box has a magnifier and a
    /// placeholder, and the Overview's Change links carry a chevron.
    /// </summary>
    [TestFixture]
    public class SettingsTemplateTests
    {
        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
            SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [Test]
        public void Templates_LoadFromThePackage_AndInstantiateUnwrapped()
        {
            foreach (string file in new[] { SettingsView.CardTemplateFile, SettingsView.OverviewRowTemplateFile })
            {
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(SettingsView.UiFolder + file), file);
                VisualElement root = SettingsView.InstantiateTemplate(file);
                Assert.IsNotNull(root);
                Assert.IsFalse(root is TemplateContainer, file + " must come back without the wrapper");
                Assert.IsNull(root.parent);
            }
            Assert.IsTrue(SettingsView.InstantiateTemplate(SettingsView.CardTemplateFile)
                .ClassListContains("uap-settings-card"));
            Assert.IsTrue(SettingsView.InstantiateTemplate(SettingsView.OverviewRowTemplateFile)
                .ClassListContains("uap-settings-overview-row"));
        }

        [Test]
        public void InstantiateTemplate_MissingFile_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                SettingsView.InstantiateTemplate("NoSuchTemplate.uxml");
            });
        }

        [Test]
        public void SearchBox_HasAMagnifierAndAPlaceholder_ThatHidesWhileSearching()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                VisualElement bar = root.Q(className: "uap-settings-titlebar");
                bool hasIcon = bar.Q(className: "uap-settings-search-icon") != null
                    || bar.Q(className: "uap-settings-search-icon-glyph") != null;
                Assert.IsTrue(hasIcon, "a magnifier (icon or glyph) precedes the box");
                var placeholder = root.Q<Label>(className: "uap-settings-search-placeholder");
                Assert.IsNotNull(placeholder);
                Assert.AreEqual(L10n.S.SettingsSearchPlaceholder, placeholder.text);
                Assert.AreEqual(PickingMode.Ignore, placeholder.pickingMode);
                Assert.AreNotEqual(DisplayStyle.None, placeholder.resolvedStyle.display,
                    "visible while the box is empty");
                // The placeholder follows the box's text (a never-shown window
                // raises no ChangeEvent, so set the text and drive the handler).
                root.Q<TextField>(className: "uap-settings-search").SetValueWithoutNotify("beep");
                window.SettingsViewForTests.ApplySearch("beep");
                Assert.AreEqual(DisplayStyle.None, placeholder.style.display.value, "hidden once there is a query");
                root.Q<TextField>(className: "uap-settings-search").SetValueWithoutNotify(string.Empty);
                window.SettingsViewForTests.ApplySearch(string.Empty);
                Assert.AreEqual(DisplayStyle.Flex, placeholder.style.display.value, "back once the box is empty again");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void OverviewChangeLinks_CarryAChevron_PrimaryActionsDoNot()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var rows = root.Q("uap-card-" + SettingsView.EffectiveCardId)
                    .Query(className: "uap-settings-overview-row").ToList();
                foreach (VisualElement row in rows)
                {
                    var action = row.Q<Button>(className: "uap-settings-overview-action");
                    bool primary = action.ClassListContains("uap-settings-btn--primary");
                    VisualElement chevron = action.Q(className: "uap-settings-overview-chevron")
                        ?? action.Q(className: "uap-settings-overview-chevron-glyph");
                    Assert.IsNotNull(chevron);
                    Assert.AreEqual(primary ? DisplayStyle.None : DisplayStyle.Flex, chevron.style.display.value);
                    Assert.IsTrue(action.ClassListContains("uap-settings-link-btn") != primary);
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
