using System.Collections.Generic;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UXIA-2: the fifteen always-expanded settings cards gained
    /// progressive disclosure -- five advanced/plumbing sections (UapOps,
    /// Extension Profiles, uLoop, CLI, Diagnostics) render as
    /// collapsed-by-default foldout cards while the everyday sections stay
    /// plain expanded cards. Same headless CreateGUI harness as
    /// SettingsViewSectionIconTests.
    /// </summary>
    [TestFixture]
    public class SettingsViewSectionDisclosureTests
    {
        private static readonly string[] SectionIds =
            { "uapops", "profiles", "pro", "uloop", "unity-plugin", "diagnostics" };

        [SetUp]
        public void SetUp()
        {
            // Disclosure state persists in SessionState for the editor
            // session; start each test from the genuine default.
            foreach (string id in SectionIds)
            {
                SessionState.EraseBool(SettingsView.SectionDisclosureKey(id));
            }
        }

        [TearDown]
        public void TearDown()
        {
            // 2026-09-08: CreateGUI applies the persisted language setting
            // (Auto resolves to the OS language); restore the suite default
            // so later fixtures see English -- see the regression note on
            // SceneMarkerPinTests.
            L10n.OverrideForTests(null);
            foreach (string id in SectionIds)
            {
                SessionState.EraseBool(SettingsView.SectionDisclosureKey(id));
            }
        }

        private static List<Foldout> SectionFoldouts(UI.AgentPanelWindow window)
        {
            return window.rootVisualElement
                .Query<Foldout>(className: "uap-settings-section-foldout").ToList();
        }

        [Test]
        public void AdvancedSections_AreFoldouts_CollapsedByDefault()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                List<Foldout> foldouts = SectionFoldouts(window);
                Assert.AreEqual(SectionIds.Length, foldouts.Count,
                    "exactly the advanced sections are collapsible");
                foreach (Foldout foldout in foldouts)
                {
                    Assert.IsFalse(foldout.value,
                        "'" + foldout.text + "' must start collapsed");
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void BasicSections_StayPlainExpandedCards()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                List<VisualElement> cards = window.rootVisualElement
                    .Query<VisualElement>(className: "uap-settings-card").ToList();
                int collapsible = 0;
                foreach (VisualElement card in cards)
                {
                    if (card.ClassListContains("uap-settings-card--collapsible"))
                    {
                        collapsible++;
                    }
                }
                Assert.AreEqual(SectionIds.Length, collapsible);
                Assert.Greater(cards.Count - collapsible, 5,
                    "the everyday sections must remain plain expanded cards");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void EachCollapsibleHeader_KeepsItsSectionIcon()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                foreach (Foldout foldout in SectionFoldouts(window))
                {
                    // MEASURED (CI): IconLoader.CreateIcon returns an Image
                    // with the image class when the built-in icon name
                    // resolves in this editor version, and a glyph Label
                    // with the GLYPH class when it does not -- "d_Script
                    // ableObject Icon" (Extension profiles) takes the
                    // fallback branch here. Either form is the icon
                    // surviving the foldout conversion, which is what this
                    // guards; SettingsViewSectionIconTests handles the same
                    // two-shape reality for the plain cards.
                    bool hasIcon =
                        foldout.Q<VisualElement>(className: "uap-settings-danger-icon") != null
                        || foldout.Q<VisualElement>(className: "uap-settings-danger-icon-glyph") != null;
                    Assert.IsTrue(hasIcon, "'" + foldout.text + "' lost its header icon");
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void DisclosureState_PersistsViaSessionState()
        {
            // "uapops" since 2026-09-17: the CLI card merged into the
            // (plain, always-open) Agent card and its "cli" key is gone.
            SessionState.SetBool(SettingsView.SectionDisclosureKey("uapops"), true);
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                Foldout uapOps = null;
                foreach (Foldout foldout in SectionFoldouts(window))
                {
                    if (foldout.text == L10n.S.SettingsSectionUapOps)
                    {
                        uapOps = foldout;
                    }
                }
                Assert.IsNotNull(uapOps);
                Assert.IsTrue(uapOps.value, "a session-remembered open section reopens");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void SectionDisclosureKey_IsStableAndPerSection()
        {
            Assert.AreEqual("Colloid.AgentPanel.Settings.SectionOpen.uapops",
                SettingsView.SectionDisclosureKey("uapops"));
            Assert.AreNotEqual(SettingsView.SectionDisclosureKey("uapops"),
                SettingsView.SectionDisclosureKey("uloop"));
        }
    }
}
