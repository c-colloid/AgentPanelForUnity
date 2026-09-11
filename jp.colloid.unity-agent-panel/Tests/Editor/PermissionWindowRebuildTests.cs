using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UICODE-1: RebuildAllOpenPanels re-invokes CreateGUI directly on every
    /// open PermissionWindow (language switch), and Unity itself can call
    /// CreateGUI twice on one window instance -- AgentPanelWindow gained a
    /// re-entry guard for exactly that (its doc comment), but
    /// PermissionWindow had none: each re-entry stacked a second
    /// host+PermissionCard under the never-cleared root (duplicate
    /// permission cards, the stale one frozen in the old language) and
    /// double-subscribed AgentHub.Changed. Headless
    /// ScriptableObject.CreateInstance + CreateGUI(), the same pattern as
    /// AgentPanelWindowStyleSheetTests.
    /// </summary>
    [TestFixture]
    public class PermissionWindowRebuildTests
    {
        /// <summary>
        /// Regression note (2026-09-08, full-suite order dependence):
        /// CreateGUI applies the persisted language setting (Auto ->
        /// Japanese on this OS) and this fixture used to leave it applied,
        /// which broke SceneMarkerPinTests' English label assertion later
        /// in the run. Restore the suite default after every test (same
        /// note as PermissionWindowFontScaleTests).
        /// </summary>
        [TearDown]
        public void RestoreLanguage()
        {
            L10n.OverrideForTests(null);
        }

        [Test]
        public void CreateGUI_CalledTwice_KeepsExactlyOneCardHost()
        {
            var window = ScriptableObject.CreateInstance<PermissionWindow>();
            try
            {
                window.CreateGUI();
                int afterFirst = CountCardHosts(window.rootVisualElement);
                Assert.AreEqual(1, afterFirst, "precondition: one host after the first build");

                window.CreateGUI();

                Assert.AreEqual(1, CountCardHosts(window.rootVisualElement),
                    "a re-entrant CreateGUI (RebuildAllOpenPanels on a"
                    + " language switch) must rebuild in place, not stack a"
                    + " second permission card under the old one");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void CreateGUI_CalledTwice_DoesNotThrow_AndStaysBuilt()
        {
            var window = ScriptableObject.CreateInstance<PermissionWindow>();
            try
            {
                Assert.DoesNotThrow(delegate
                {
                    window.CreateGUI();
                    window.CreateGUI();
                    window.CreateGUI();
                });
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        private static int CountCardHosts(VisualElement root)
        {
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                if (root[i].ClassListContains("uap-permwin-root"))
                {
                    count++;
                }
            }
            return count;
        }
    }
}
