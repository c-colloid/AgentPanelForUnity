using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guard for the fix-round finding: BuildConversationSection
    /// and BuildModelSection both passed the same built-in icon name
    /// ("d__Popup") to AddSection, so once that name resolved to a real
    /// texture (it does, in this editor version) the two settings cards
    /// rendered the IDENTICAL icon, breaking the per-section icon
    /// differentiation every other card relies on. Builds the real window
    /// (ScriptableObject.CreateInstance + CreateGUI, same headless harness
    /// as AgentPanelWindowTests -- no GetWindow/Show, no window-manager
    /// side effects) and inspects the actual rendered icon elements instead
    /// of the source-level icon name strings, so this fails the same way a
    /// future accidental re-introduction of the shared name would.
    /// </summary>
    [TestFixture]
    public class SettingsViewSectionIconTests
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
        public void ConversationAndModelSections_DoNotRenderTheSameIcon()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                List<VisualElement> icons = window.rootVisualElement
                    .Query<VisualElement>(className: "uap-settings-card-icon").ToList();

                // SettingsView.CreateGUI build order (2026-08-14 ui-polish
                // audit item 8: Conversation and Model lead, CLI moved down
                // near Appearance/Diagnostics): Conversation(0), Model(1), ...
                Assert.GreaterOrEqual(icons.Count, 2,
                    "Expected at least the Conversation/Model section icons to exist.");
                VisualElement conversationIcon = icons[0];
                VisualElement modelIcon = icons[1];

                if (conversationIcon is Image conversationImage && modelIcon is Image modelImage)
                {
                    // Both icon names resolved to a real built-in texture --
                    // this is the exact case the reported defect lived in:
                    // they must not be the SAME texture.
                    Assert.AreNotSame(conversationImage.image, modelImage.image,
                        "Conversation and Model settings cards must not share the same icon "
                        + "texture -- give BuildModelSection its own distinct icon name.");
                }
                else if (conversationIcon is Label conversationLabel && modelIcon is Label modelLabel)
                {
                    // Both icon names fell back to a glyph Label in this
                    // editor version -- still must not be the same glyph.
                    Assert.AreNotEqual(conversationLabel.text, modelLabel.text,
                        "Conversation and Model settings cards must not share the same "
                        + "fallback glyph.");
                }
                // else: one resolved to a real Image and the other fell back
                // to a glyph Label -- inherently distinct, nothing to assert.
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
