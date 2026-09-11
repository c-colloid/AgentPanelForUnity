using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// AgentPanelWindow.CreateGUI re-entry guard (fix round: Unity can invoke
    /// CreateGUI a second time on the same window instance without an
    /// intervening OnDisable/OnEnable). Uses ScriptableObject.CreateInstance
    /// directly (no GetWindow/Show, no docking) so this stays a pure
    /// EditMode test with no window-manager side effects; CreateGUI itself
    /// is a public method Unity's UI Toolkit window host calls, so invoking
    /// it directly here is a faithful simulation of the reported defect.
    /// </summary>
    [TestFixture]
    public class AgentPanelWindowTests
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
            UI.L10n.OverrideForTests(null);
        }

        [Test]
        public void CreateGUI_ReentrantCall_DoesNotThrow_OrDuplicateTheSkeleton()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                int childCountAfterFirst = window.rootVisualElement.childCount;
                Assert.Greater(childCountAfterFirst, 0,
                    "First CreateGUI must populate the root with the header/"
                    + "viewContainer/statusBar skeleton (or the fallback build).");

                // The re-entry guard must tear the previous build down before
                // rebuilding -- without it, a second CloneTree/fallback build
                // would append onto the already-populated root and leave the
                // old ChatView/SettingsView/HistoryView instances (and their
                // AgentHub.Changed / EditorUpdatePump.RepaintRequested
                // subscriptions) dangling.
                Assert.DoesNotThrow(delegate { window.CreateGUI(); },
                    "A re-entrant CreateGUI must not throw.");

                Assert.AreEqual(childCountAfterFirst, window.rootVisualElement.childCount,
                    "A re-entrant CreateGUI must leave the root with exactly the "
                    + "same top-level child count as a single build, never a "
                    + "second skeleton appended onto the first.");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        /// <summary>
        /// AgentPanelWindow.SetActiveView (the switching logic ShowSettings/
        /// ShowChat/ShowHistory delegate to -- see
        /// docs/design-notes/2026-07-31-show-settings-timing.md) correctly
        /// toggles the Chat/Settings root visibility on an ALREADY-BUILT
        /// window, in both directions. Uses ScriptableObject.CreateInstance
        /// + CreateGUI (no GetWindow/Show) like the re-entrancy test above;
        /// SetActiveView is `internal` (Editor/AssemblyInfo.cs grants this
        /// test assembly InternalsVisibleTo) specifically so this can be
        /// exercised without real window-manager side effects.
        /// </summary>
        [Test]
        public void SetActiveView_TogglesChatAndSettingsRoots_BothDirections()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement chatRoot = window.rootVisualElement.Q(className: "uap-chat");
                VisualElement settingsRoot = window.rootVisualElement.Q(className: "uap-settings");
                Assert.NotNull(chatRoot, "ChatView root (.uap-chat) must exist after CreateGUI.");
                Assert.NotNull(settingsRoot,
                    "SettingsView root (.uap-settings) must exist after CreateGUI.");
                Assert.AreEqual(DisplayStyle.Flex, chatRoot.style.display.value,
                    "Chat must be the default active view after a fresh CreateGUI.");
                Assert.AreEqual(DisplayStyle.None, settingsRoot.style.display.value,
                    "Settings must start hidden.");

                window.SetActiveView(UI.PanelViewKind.Settings);

                Assert.AreEqual(DisplayStyle.None, chatRoot.style.display.value,
                    "Chat root must hide once Settings becomes active.");
                Assert.AreEqual(DisplayStyle.Flex, settingsRoot.style.display.value,
                    "Settings root must show once active.");

                window.SetActiveView(UI.PanelViewKind.Chat);

                Assert.AreEqual(DisplayStyle.Flex, chatRoot.style.display.value,
                    "Chat root must show again after switching back.");
                Assert.AreEqual(DisplayStyle.None, settingsRoot.style.display.value,
                    "Settings root must hide again after switching back to Chat.");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        /// <summary>
        /// Reproduces defect (3) exactly: a view switch requested BEFORE
        /// CreateGUI has run (the ShowSettings()/GetWindow timing gap -- see
        /// docs/design-notes/2026-07-31-show-settings-timing.md) must not be
        /// silently dropped -- it must apply once CreateGUI finishes
        /// building. RequestActiveView is the internal method
        /// ShowSettings/ShowChat/ShowHistory route through instead of
        /// calling SetActiveView directly, for exactly this reason.
        /// </summary>
        [Test]
        public void RequestActiveView_BeforeCreateGUI_IsAppliedOnceBuilt()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                // Simulates AgentPanelWindow.ShowSettings() landing on a
                // just-created window whose CreateGUI has not executed yet.
                window.RequestActiveView(UI.PanelViewKind.Settings);

                // Sanity: with the OLD (SetActiveView-only) implementation
                // this call would have silently no-op'd (the reported
                // defect) -- nothing to assert yet since the roots do not
                // exist before CreateGUI.
                window.CreateGUI();

                VisualElement chatRoot = window.rootVisualElement.Q(className: "uap-chat");
                VisualElement settingsRoot = window.rootVisualElement.Q(className: "uap-settings");
                Assert.AreEqual(DisplayStyle.None, chatRoot.style.display.value,
                    "The pending Settings request must have applied once CreateGUI built "
                    + "the skeleton -- Chat must NOT be showing.");
                Assert.AreEqual(DisplayStyle.Flex, settingsRoot.style.display.value,
                    "The pending Settings request must have applied once CreateGUI built "
                    + "the skeleton -- Settings must be showing.");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        /// <summary>
        /// A no-op request (Chat, already the default) queued before
        /// CreateGUI must not throw and must leave Chat active -- guards
        /// against a careless implementation of the pending-view field that
        /// assumes a non-Chat value.
        /// </summary>
        [Test]
        public void RequestActiveView_ChatBeforeCreateGUI_LeavesChatActiveWithoutThrowing()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.RequestActiveView(UI.PanelViewKind.Chat);
                Assert.DoesNotThrow(delegate { window.CreateGUI(); });

                VisualElement chatRoot = window.rootVisualElement.Q(className: "uap-chat");
                Assert.AreEqual(DisplayStyle.Flex, chatRoot.style.display.value);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
