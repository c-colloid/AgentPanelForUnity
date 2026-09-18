using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-17 settings redesign phase 4 (design note section 11, D6):
    /// the Conversation card's tool lists and the UapOps card's module
    /// switches sit under collapsed rows with a count pill. Pure formatters
    /// pinned directly; the DOM through the headless CreateGUI harness.
    /// </summary>
    [TestFixture]
    public class SettingsDisclosureRowTests
    {
        [SetUp]
        public void SetUp()
        {
            SessionState.EraseBool(SettingsView.SectionDisclosureKey(SettingsView.ToolListsDisclosureId));
            SessionState.EraseBool(SettingsView.SectionDisclosureKey(SettingsView.ModulesDisclosureId));
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
            SessionState.EraseInt(SettingsView.TabStateKey);
            SessionState.EraseBool(SettingsView.SectionDisclosureKey(SettingsView.ToolListsDisclosureId));
            SessionState.EraseBool(SettingsView.SectionDisclosureKey(SettingsView.ModulesDisclosureId));
        }

        [Test]
        public void FormatToolListsPill_HiddenWhenBothEmpty()
        {
            Assert.IsNull(SettingsView.FormatToolListsPill(0, 0));
            Assert.IsNotNull(SettingsView.FormatToolListsPill(1, 0));
            Assert.IsNotNull(SettingsView.FormatToolListsPill(0, 2));
            StringAssert.Contains("3", SettingsView.FormatToolListsPill(3, 1));
        }

        [Test]
        public void FormatModulesPill_ShowsOnOverTotal()
        {
            string text = SettingsView.FormatModulesPill(4, 13);
            StringAssert.Contains("4", text);
            StringAssert.Contains("13", text);
        }

        private static Foldout Row(VisualElement root, string cardId, int index)
        {
            VisualElement card = root.Q("uap-card-" + cardId);
            var rows = card.Query<Foldout>(className: "uap-settings-disclosure").ToList();
            Assert.Greater(rows.Count, index, cardId + " must hold a disclosure row");
            return rows[index];
        }

        [Test]
        public void ToolLists_SitUnderACollapsedRow_WithBothFieldsInside()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                Foldout row = Row(root, SettingsView.ConversationCardId, 0);
                Assert.IsFalse(row.value, "collapsed by default");
                Assert.AreEqual(L10n.S.SettingsToolListsFoldout, row.text);
                Assert.AreEqual(2, row.contentContainer.Query<TextField>(className: "uap-settings-multiline").ToList().Count,
                    "allowed and disallowed lists live inside the row");
                Assert.IsNotNull(row.Q<Label>(className: "uap-settings-disclosure-pill"));
                // The everyday rows stay outside: the Ctrl+Enter switch is a
                // child of the body, not of the row.
                Assert.AreEqual(0, row.contentContainer.Query<Toggle>().ToList().Count);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Modules_SitUnderACollapsedRow_WithACountPill()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                Foldout row = Row(root, SettingsView.UapOpsCardId, 0);
                Assert.IsFalse(row.value);
                Assert.AreEqual(L10n.S.SettingsModulesFoldout, row.text);
                var switches = row.contentContainer.Query<Toggle>(className: "uap-settings-field--child").ToList();
                Assert.AreEqual(13, switches.Count, "every module switch is inside the row");
                Label pill = row.Q<Label>(className: "uap-settings-disclosure-pill");
                Assert.IsNotNull(pill);
                Assert.AreEqual(DisplayStyle.Flex, pill.style.display.value);
                StringAssert.Contains("13", pill.text);
                // The master switch stays outside the row.
                VisualElement card = root.Q("uap-card-" + SettingsView.UapOpsCardId);
                Assert.AreEqual(0, row.contentContainer.Query<Toggle>().Where(t => t.label == L10n.S.SettingsUapOpsEnabledLabel).ToList().Count);
                Assert.IsNotNull(card.Q<Toggle>().label);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void DisclosureRows_RememberTheirState()
        {
            SessionState.SetBool(SettingsView.SectionDisclosureKey(SettingsView.ModulesDisclosureId), true);
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                Assert.IsTrue(Row(root, SettingsView.UapOpsCardId, 0).value, "a remembered open row reopens");
                Assert.IsFalse(Row(root, SettingsView.ConversationCardId, 0).value);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Search_OpensTheModulesRow_OnAHitInside()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                window.SettingsViewForTests.ApplySearch(L10n.S.SettingsUapOpsModuleMeshLabel);
                Assert.IsTrue(Row(root, SettingsView.UapOpsCardId, 0).value, "a hit inside the row opens it");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
