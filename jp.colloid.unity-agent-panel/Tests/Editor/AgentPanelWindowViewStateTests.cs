using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guards for the view-container state invariant: after ANY
    /// CreateGUI pass, the per-view display flags must match _activeView.
    /// The 2026-08-01 live-diagnosed defect: CreateGUI hardcoded
    /// chat-visible/others-hidden, so a re-entrant rebuild while Settings
    /// was active left _activeView == Settings with the chat view on
    /// screen -- and SetActiveView's same-view early return then made
    /// every later ShowSettings() a permanent no-op (the strip showed
    /// "&lt; Chat" over chat content and the panel could never reach
    /// Settings again).
    /// </summary>
    [TestFixture]
    public class AgentPanelWindowViewStateTests
    {
        /// <summary>
        /// 2026-09-08: CreateGUI applies the persisted language setting
        /// (Auto resolves to the OS language); restore the suite default
        /// after every test so later fixtures see English -- see the
        /// regression note on SceneMarkerPinTests. (No SetUp pin: the
        /// CreateGUI_AppliesThePersistedLanguageSetting test needs
        /// ApplyFromSettings unblocked.)
        /// </summary>
        [TearDown]
        public void RestoreLanguage()
        {
            UI.L10n.OverrideForTests(null);
        }

        private static DisplayStyle Display(UI.AgentPanelWindow window, string className)
        {
            VisualElement root = window.rootVisualElement.Q<VisualElement>(className: className);
            Assert.IsNotNull(root, "view root not found: " + className);
            return root.style.display.value;
        }

        [Test]
        public void CreateGUI_ReentrantRebuild_PreservesANonChatActiveView()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                window.SetActiveView(UI.PanelViewKind.Settings);
                Assert.AreEqual(DisplayStyle.Flex, Display(window, "uap-settings"));

                // Re-entrant rebuild on the same instance (the documented
                // teardown/rebuild path). Build-time visibility must honor
                // _activeView, not reset to the chat-visible default.
                window.CreateGUI();

                Assert.AreEqual(DisplayStyle.Flex, Display(window, "uap-settings"),
                    "a rebuild while Settings is active must keep the Settings root displayed");
                Assert.AreEqual(DisplayStyle.None, Display(window, "uap-chat"),
                    "a rebuild while Settings is active must keep the chat root hidden");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void CreateGUI_ReentrantRebuild_SerializesLiveChatStateFirst()
        {
            string savedDraft = Model.SessionStateBridge.InputDraft;
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                Model.SessionStateBridge.InputDraft = string.Empty;
                window.CreateGUI();

                // Live typing that never went through the change event (the
                // bridge mirror has not seen it) -- exactly the state the
                // old re-entry teardown dropped by skipping SerializeState.
                TextField field = window.rootVisualElement.Q<TextField>(
                    className: "uap-composer-field");
                Assert.IsNotNull(field, "composer field not found");
                field.SetValueWithoutNotify("typed but not yet mirrored");

                window.CreateGUI();

                Assert.AreEqual("typed but not yet mirrored",
                    Model.SessionStateBridge.InputDraft,
                    "the re-entrant rebuild must serialize the live composer "
                    + "draft (shared TeardownLiveState) before discarding the "
                    + "old ChatView");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Model.SessionStateBridge.InputDraft = savedDraft;
            }
        }

        /// <summary>
        /// Boot-time language application (2026-08-01): PanelSettings.language
        /// is persisted, but only the Settings dropdown ever called
        /// L10n.ApplyFromSettings -- so every domain reload / editor start
        /// rendered English regardless of the setting until the user visited
        /// Settings (live-diagnosed: setting=Auto, effective=English).
        /// CreateGUI must apply the persisted setting before building labels.
        /// </summary>
        [Test]
        public void CreateGUI_AppliesThePersistedLanguageSetting()
        {
            Model.PanelLanguage saved = Model.PanelStateStore.instance.Settings.language;
            UI.L10n.OverrideForTests(null);
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                Model.PanelStateStore.instance.Settings.language = Model.PanelLanguage.Japanese;
                window.CreateGUI();
                Assert.AreEqual(Model.PanelLanguage.Japanese, UI.L10n.EffectiveLanguage,
                    "CreateGUI must apply the persisted language setting so a "
                    + "reloaded panel builds its labels from the right catalog");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Model.PanelStateStore.instance.Settings.language = saved;
                // Back to the suite-wide deterministic default (English,
                // no pin) regardless of this machine's OS language.
                UI.L10n.OverrideForTests(null);
            }
        }

        [Test]
        public void SetActiveView_AfterReentrantRebuild_StillSwitchesBothWays()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                window.SetActiveView(UI.PanelViewKind.Settings);
                window.CreateGUI();

                // The early-return lockout: with displays desynced this
                // switch used to no-op forever. After the fix the state is
                // consistent, so switching must work in both directions.
                window.SetActiveView(UI.PanelViewKind.Chat);
                Assert.AreEqual(DisplayStyle.Flex, Display(window, "uap-chat"));
                Assert.AreEqual(DisplayStyle.None, Display(window, "uap-settings"));

                window.SetActiveView(UI.PanelViewKind.Settings);
                Assert.AreEqual(DisplayStyle.Flex, Display(window, "uap-settings"));
                Assert.AreEqual(DisplayStyle.None, Display(window, "uap-chat"));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
