using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// PermissionWindow half of the font-size-slider-is-unusable fix (see
    /// docs/design-notes/2026-08-01-fontscale-scope.md and
    /// AgentPanelWindowFontScaleTests, its AgentPanelWindow counterpart):
    /// the `uap-fontscale-N` class must land on the content root that wraps
    /// the PermissionCard (PermissionWindow.ContentRoot), never on the
    /// window's rootVisualElement, so a floating permission window's own
    /// chrome/theme are unaffected by the slider.
    ///
    /// Same headless harness as the rest of this suite:
    /// ScriptableObject.CreateInstance + CreateGUI(), no ShowUtility, no
    /// window-manager side effects. AgentHub.PendingPermission is left null
    /// throughout -- CreateGUI only synchronously refreshes the card when
    /// something is pending (see its own doc comment), so styling can be
    /// asserted without standing up a live AgentClient/AgentHub session.
    /// </summary>
    [TestFixture]
    public class PermissionWindowFontScaleTests
    {
        /// <summary>
        /// Regression note (2026-09-08, full-suite order dependence):
        /// PermissionWindow.CreateGUI applies the persisted language
        /// setting (L10n.ApplyFromSettings). In the AITemp sandbox that
        /// setting is Auto, which resolves to Japanese on a Japanese OS,
        /// and this fixture used to leave the catalog switched -- so
        /// SceneMarkerPinTests, run later, asserted "P1  pin" against
        /// the Japanese label. Restore the suite default (English, no pin) after
        /// every test.
        /// </summary>
        [TearDown]
        public void RestoreLanguage()
        {
            UI.L10n.OverrideForTests(null);
        }

        private static bool HasAnyFontScaleClass(VisualElement element)
        {
            foreach (string cls in element.GetClasses())
            {
                if (cls.StartsWith("uap-fontscale-", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        [Test]
        public void CreateGUI_PutsFontScaleClass_OnContentRoot_NotOnWindowRoot()
        {
            var window = ScriptableObject.CreateInstance<UI.PermissionWindow>();
            try
            {
                window.CreateGUI();

                VisualElement content = window.ContentRoot;
                Assert.IsNotNull(content, "ContentRoot was not set by CreateGUI");
                Assert.IsTrue(HasAnyFontScaleClass(content),
                    "the permission-preview content root must receive a uap-fontscale-N class");
                Assert.IsFalse(HasAnyFontScaleClass(window.rootVisualElement),
                    "the window root must NOT receive a uap-fontscale-N class (docs/"
                    + "design-notes/2026-08-01-fontscale-scope.md).");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void ReapplyContentRootStyling_RetargetsContentRoot_NeverTheWindowRoot()
        {
            var window = ScriptableObject.CreateInstance<UI.PermissionWindow>();
            int originalFontSizePx = Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.fontSizePx;
            try
            {
                window.CreateGUI();
                VisualElement content = window.ContentRoot;
                Assert.IsNotNull(content);

                int changed = Colloid.AgentPanel.Model.PanelSettings.MinFontSizePx == originalFontSizePx
                    ? Colloid.AgentPanel.Model.PanelSettings.MaxFontSizePx
                    : Colloid.AgentPanel.Model.PanelSettings.MinFontSizePx;
                Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.fontSizePx = changed;

                UI.AgentPanelWindow.ReapplyContentRootStyling();

                Assert.IsTrue(content.ClassListContains("uap-fontscale-" + changed),
                    "ReapplyContentRootStyling must update PermissionWindow's content root "
                    + "to the new font-size class.");
                Assert.IsFalse(HasAnyFontScaleClass(window.rootVisualElement),
                    "ReapplyContentRootStyling must never add a uap-fontscale-N class to "
                    + "PermissionWindow's window root.");
            }
            finally
            {
                Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.fontSizePx = originalFontSizePx;
                Object.DestroyImmediate(window);
            }
        }
    }
}
