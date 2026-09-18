using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops.UnityPlugin;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-17 settings redesign phase 2 (design note section 9): every
    /// card header carries one status pill (D4), the status lines that
    /// used to sit at the bottom of a card body are gone, and the hint
    /// diet (D5) moved the per-switch descriptions into tooltips. Pure
    /// pieces pinned directly; the DOM through the headless CreateGUI
    /// harness of SettingsViewLayoutTests.
    /// </summary>
    [TestFixture]
    public class SettingsSectionStatusTests
    {
        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
            SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [Test]
        public void FormatCountPill_ZeroHidesThePill()
        {
            Assert.IsNull(SettingsView.FormatCountPill("{0} items", 0));
            Assert.IsNull(SettingsView.FormatCountPill("{0} items", -1));
            Assert.AreEqual("3 items", SettingsView.FormatCountPill("{0} items", 3));
        }

        [Test]
        public void ResolveModelPillText_PrefersTheResolvedId_ThenDisplayName_ThenValue()
        {
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "default", displayName = "Default", resolvedModel = "claude-opus-5" },
                new ModelCatalogEntry { value = "haiku", displayName = "Haiku" }
            };
            Assert.AreEqual(SettingsView.ShortenResolvedModel("claude-opus-5"),
                SettingsView.ResolveModelPillText(catalog, "default"));
            Assert.AreEqual("Haiku", SettingsView.ResolveModelPillText(catalog, "haiku"));
            Assert.AreEqual("sonnet-x", SettingsView.ResolveModelPillText(catalog, "sonnet-x"));
            Assert.IsNull(SettingsView.ResolveModelPillText(catalog, string.Empty));
            Assert.IsNull(SettingsView.ResolveModelPillText(null, null));
        }

        [Test]
        public void ResolveUnityPluginPill_MapsEveryState()
        {
            Assert.IsNull(SettingsView.ResolveUnityPluginPillText(UnityPluginState.CliUnavailable));
            Assert.AreEqual(L10n.S.SettingsUloopStatusMissing, SettingsView.ResolveUnityPluginPillText(UnityPluginState.NotInstalled));
            Assert.AreEqual(L10n.S.SettingsPillDisabled, SettingsView.ResolveUnityPluginPillText(UnityPluginState.Disabled));
            Assert.AreEqual(L10n.S.SettingsPillInstalled, SettingsView.ResolveUnityPluginPillText(UnityPluginState.EnabledNotLoaded));
            Assert.AreEqual(L10n.S.SettingsPillInstalled, SettingsView.ResolveUnityPluginPillText(UnityPluginState.Loaded));
            Assert.AreEqual(L10n.S.SettingsPillLoadError, SettingsView.ResolveUnityPluginPillText(UnityPluginState.LoadError));
            Assert.AreEqual(SettingsView.PillTone.Ok, SettingsView.ResolveUnityPluginPillTone(UnityPluginState.Loaded));
            Assert.AreEqual(SettingsView.PillTone.Warn, SettingsView.ResolveUnityPluginPillTone(UnityPluginState.LoadError));
            Assert.AreEqual(SettingsView.PillTone.Neutral, SettingsView.ResolveUnityPluginPillTone(UnityPluginState.NotInstalled));
        }

        [Test]
        public void EveryCardHeader_CarriesExactlyOneStatusPill()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var cards = root.Query(className: "uap-settings-card").ToList();
                Assert.GreaterOrEqual(cards.Count, 17);
                foreach (VisualElement card in cards)
                {
                    var pills = card.Query<Label>(className: "uap-settings-card-status").ToList();
                    Assert.AreEqual(1, pills.Count, card.name + " must have one status pill");
                    Foldout foldout = card.Q<Foldout>(className: "uap-settings-section-foldout");
                    if (foldout != null)
                    {
                        Assert.IsNotNull(foldout.Q<Toggle>(className: "unity-foldout__toggle")
                            .Q<Label>(className: "uap-settings-card-status"),
                            card.name + ": a collapsible card's pill sits in its header toggle row");
                    }
                    else
                    {
                        Assert.IsNotNull(card.Q(className: "uap-settings-card-header")
                            .Q<Label>(className: "uap-settings-card-status"),
                            card.name + ": a plain card's pill sits in its header strip");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        private static bool IsHiddenOrCollapsed(VisualElement element, VisualElement stopAt)
        {
            for (VisualElement e = element; e != null && e != stopAt; e = e.parent)
            {
                if (e.style.display.keyword == StyleKeyword.Undefined && e.style.display.value == DisplayStyle.None)
                {
                    return true;
                }
                var foldout = e as Foldout;
                if (foldout != null && !foldout.value)
                {
                    return true;
                }
            }
            return false;
        }

        private static Label Pill(VisualElement root, string cardId)
        {
            return root.Q("uap-card-" + cardId).Q<Label>(className: "uap-settings-card-status");
        }

        [Test]
        public void StatePills_ShowTheState_WithTextNotJustColour()
        {
            bool skip = PanelStateStore.instance.Settings.dangerouslySkipPermissions;
            bool uapOps = PanelStateStore.instance.Settings.uapOpsEnabled;
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");

                Label danger = Pill(root, SettingsView.DangerCardId);
                Assert.AreEqual(DisplayStyle.Flex, danger.style.display.value);
                Assert.AreEqual(skip ? L10n.S.SettingsPillSkippingChecks : L10n.S.SettingsPillAllOff, danger.text);
                Assert.AreEqual(skip, danger.ClassListContains("uap-pill--warn"));

                Label ops = Pill(root, SettingsView.UapOpsCardId);
                Assert.AreEqual(DisplayStyle.Flex, ops.style.display.value);
                if (!uapOps)
                {
                    Assert.AreEqual(L10n.S.SettingsPillOff, ops.text);
                    Assert.IsTrue(ops.ClassListContains("uap-pill--neutral"));
                }
                Assert.IsFalse(string.IsNullOrEmpty(ops.tooltip), "the full status sentence is the pill's tooltip");

                Label instructions = Pill(root, SettingsView.InstructionsCardId);
                Assert.AreEqual(DisplayStyle.Flex, instructions.style.display.value);
                Assert.IsFalse(string.IsNullOrEmpty(instructions.text));

                Label agent = Pill(root, SettingsView.AgentCardId);
                Assert.AreEqual(DisplayStyle.Flex, agent.style.display.value);
                Assert.IsFalse(string.IsNullOrEmpty(agent.text));

                // The body status lines the pills replaced are gone.
                Assert.IsNull(root.Q("uap-card-" + SettingsView.UapOpsCardId).Q<Label>(className: "uap-settings-status"));
                Assert.IsNull(root.Q("uap-card-" + SettingsView.AppearanceCardId).Q<Label>(className: "uap-settings-status"));
                Assert.IsNull(root.Q("uap-card-" + SettingsView.UloopCardId).Q<Label>(className: "uap-settings-status"));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void HintDiet_ModuleAndNotificationDescriptions_AreTooltipsNotLines()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");

                VisualElement ops = root.Q("uap-card-" + SettingsView.UapOpsCardId);
                Assert.AreEqual(1, ops.Query<Label>(className: "uap-settings-hint--child").ToList().Count,
                    "only the allowed-hosts hint (the example) stays as a child line");
                foreach (Toggle toggle in ops.Query<Toggle>(className: "uap-settings-field--child").ToList())
                {
                    // A long description is moved onto the row's ? mark by
                    // HelpAffordance.ApplyMarks (which clears the tooltip it
                    // came from); a short one stays a plain hover tooltip.
                    Assert.IsTrue(!string.IsNullOrEmpty(toggle.tooltip) || toggle.ClassListContains(HelpAffordance.MarkedRowClass),
                        toggle.label + " carries its description as a tooltip or a ? mark");
                }

                VisualElement notifications = root.Q("uap-card-" + SettingsView.NotificationsCardId);
                Assert.AreEqual(0, notifications.Query<Label>(className: "uap-settings-hint").ToList().Count);
                foreach (Toggle toggle in notifications.Query<Toggle>().ToList())
                {
                    Assert.AreEqual(L10n.S.SettingsNotificationsHint, toggle.tooltip);
                }

                // Ceiling for the whole page (design note target: about 25
                // always-visible hint/warning lines, down from about 50).
                // "Visible" = non-empty text, not hidden inline, not inside
                // a collapsed foldout, not a status line.
                var visible = new List<string>();
                foreach (Label hint in root.Query<Label>(className: "uap-settings-hint").ToList())
                {
                    if (string.IsNullOrEmpty(hint.text) || hint.ClassListContains("uap-settings-status"))
                    {
                        continue;
                    }
                    if (IsHiddenOrCollapsed(hint, root))
                    {
                        continue;
                    }
                    visible.Add(hint.text);
                }
                Assert.LessOrEqual(visible.Count, 30,
                    "always-visible hint lines crept back up (" + visible.Count + "):\n" + string.Join("\n", visible));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
