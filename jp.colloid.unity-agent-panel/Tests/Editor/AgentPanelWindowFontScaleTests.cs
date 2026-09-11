using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guards for the font-size-slider-is-unusable defect (see
    /// docs/design-notes/2026-08-01-fontscale-scope.md): the
    /// `uap-fontscale-N` class used to be applied at the WINDOW root, so
    /// every live slider change also reflowed Settings/History and the
    /// window chrome -- shifting the very slider the user was dragging out
    /// from under the cursor. The fix moves the anchor to each window's own
    /// conversation-content root (AgentPanelWindow's chat view root,
    /// PermissionWindow's card-hosting content root) and leaves the CJK
    /// font/theme/stylesheets at the window root exactly as before.
    ///
    /// Same headless harness as AgentPanelWindowViewStateTests/
    /// AgentPanelWindowStyleSheetTests: ScriptableObject.CreateInstance +
    /// CreateGUI(), no GetWindow/Show, no window-manager side effects.
    /// </summary>
    [TestFixture]
    public class AgentPanelWindowFontScaleTests
    {
        private int _originalFontSizePx;

        [SetUp]
        public void SetUp()
        {
            // Save/restore around every test (same convention as
            // SubagentCardExpandStateTests/MessageBlockFactoryThinkingTests)
            // so this suite can freely change the live font-size setting
            // without leaking state into any other test or the sandbox
            // project's real settings.
            _originalFontSizePx = Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.fontSizePx;
        }

        [TearDown]
        public void TearDown()
        {
            // 2026-09-08: CreateGUI applies the persisted language setting
            // (Auto resolves to the OS language); restore the suite default
            // so later fixtures see English -- see the regression note on
            // SceneMarkerPinTests.
            Colloid.AgentPanel.UI.L10n.OverrideForTests(null);
            Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.fontSizePx = _originalFontSizePx;
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
        public void CreateGUI_PutsFontScaleClass_OnChatRoot_NotOnWindowRoot()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();

                VisualElement chatRoot = window.rootVisualElement.Q<VisualElement>(className: "uap-chat");
                Assert.IsNotNull(chatRoot, "chat root not found");
                Assert.IsTrue(HasAnyFontScaleClass(chatRoot),
                    "the chat content root must receive a uap-fontscale-N class");
                Assert.IsFalse(HasAnyFontScaleClass(window.rootVisualElement),
                    "the window root must NOT receive a uap-fontscale-N class -- Settings/"
                    + "History/chrome must stay fixed size regardless of the font-size "
                    + "setting (docs/design-notes/2026-08-01-fontscale-scope.md).");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void CreateGUI_DoesNotPutFontScaleClass_OnSettingsOrHistoryRoots()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();

                VisualElement settingsRoot = window.rootVisualElement.Q<VisualElement>(className: "uap-settings");
                VisualElement historyRoot = window.rootVisualElement.Q<VisualElement>(className: "uap-history");
                Assert.IsNotNull(settingsRoot, "settings root not found");
                Assert.IsNotNull(historyRoot, "history root not found");

                Assert.IsFalse(HasAnyFontScaleClass(settingsRoot),
                    "Settings must never receive a uap-fontscale-N class -- it must stay "
                    + "fixed size while the font-size slider (which lives inside it) is "
                    + "being dragged.");
                Assert.IsFalse(HasAnyFontScaleClass(historyRoot),
                    "History must never receive a uap-fontscale-N class.");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void ReapplyContentRootStyling_RetargetsChatRoot_NeverTheWindowRoot()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement chatRoot = window.rootVisualElement.Q<VisualElement>(className: "uap-chat");
                Assert.IsNotNull(chatRoot);

                // A live slider change: SettingsView.OnFontSizeChanged writes the
                // clamped value then calls ReapplyContentRootStyling() on every
                // ChangeEvent while dragging.
                int changed = Colloid.AgentPanel.Model.PanelSettings.MinFontSizePx
                    == _originalFontSizePx
                        ? Colloid.AgentPanel.Model.PanelSettings.MaxFontSizePx
                        : Colloid.AgentPanel.Model.PanelSettings.MinFontSizePx;
                Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.fontSizePx = changed;

                UI.AgentPanelWindow.ReapplyContentRootStyling();

                Assert.IsTrue(chatRoot.ClassListContains("uap-fontscale-" + changed),
                    "ReapplyContentRootStyling must update the chat root to the new "
                    + "font-size class.");
                Assert.IsFalse(HasAnyFontScaleClass(window.rootVisualElement),
                    "ReapplyContentRootStyling must never add a uap-fontscale-N class to "
                    + "the window root, even on a live update.");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void ApplyFontScale_AppliedToAnArbitraryElement_OnlyAffectsThatElement()
        {
            // ApplyFontScale is now internal (was private) specifically so
            // PermissionWindow can target its own content root instead of
            // its window root -- pin that it never reaches outside the
            // element it is given.
            var container = new VisualElement();
            var target = new VisualElement();
            container.Add(target);

            UI.AgentPanelWindow.ApplyFontScale(target);

            Assert.IsTrue(HasAnyFontScaleClass(target));
            Assert.IsFalse(HasAnyFontScaleClass(container),
                "ApplyFontScale must only touch the element it is given, never its parent.");
        }
    }
}
