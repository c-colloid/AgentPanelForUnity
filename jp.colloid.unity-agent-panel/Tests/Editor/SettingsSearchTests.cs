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
    /// 2026-09-17 settings redesign phase 3 (D3): the search's text
    /// matching and row filter on a synthetic tree, then the wiring inside
    /// the real Settings view through the same headless CreateGUI harness
    /// as SettingsViewLayoutTests.
    /// </summary>
    [TestFixture]
    public class SettingsSearchTests
    {
        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
            SessionState.EraseInt(SettingsView.TabStateKey);
        }

        [Test]
        public void IsActive_WhitespaceOnly_IsNotASearch()
        {
            Assert.IsFalse(SettingsSearch.IsActive(null));
            Assert.IsFalse(SettingsSearch.IsActive(string.Empty));
            Assert.IsFalse(SettingsSearch.IsActive("   "));
            Assert.IsTrue(SettingsSearch.IsActive(" b "));
        }

        [Test]
        public void Matches_IsCaseInsensitiveSubstring_AndTrimsTheQuery()
        {
            Assert.IsTrue(SettingsSearch.Matches("Beep on permission request", "BEEP"));
            Assert.IsTrue(SettingsSearch.Matches("Beep on permission request", "  permission "));
            Assert.IsFalse(SettingsSearch.Matches("Beep on permission request", "font"));
            Assert.IsFalse(SettingsSearch.Matches(null, "x"));
            Assert.IsTrue(SettingsSearch.Matches(null, "  "), "an inactive query matches everything");
        }

        [Test]
        public void CollectText_GathersLabelsButtonsTooltipsAndFieldLabels_NotFieldValues()
        {
            var row = new VisualElement { tooltip = "row tooltip" };
            row.Add(new Label("a label"));
            row.Add(new Button { text = "a button" });
            var nested = new VisualElement();
            nested.Add(new Label("nested label") { tooltip = "nested tooltip" });
            row.Add(nested);
            var field = new TextField("field label");
            field.SetValueWithoutNotify("secret user data");
            row.Add(field);

            string text = SettingsSearch.CollectText(row);
            StringAssert.Contains("row tooltip", text);
            StringAssert.Contains("a label", text);
            StringAssert.Contains("a button", text);
            StringAssert.Contains("nested label", text);
            StringAssert.Contains("nested tooltip", text);
            StringAssert.Contains("field label", text);
            Assert.IsFalse(text.Contains("secret user data"), "a text field's value is data, not a setting name");
            Assert.AreEqual(string.Empty, SettingsSearch.CollectText(null));
        }

        private static SettingsSearchCard MakeCard(string tab, string title, params VisualElement[] rows)
        {
            var card = new VisualElement();
            var body = new VisualElement();
            card.Add(body);
            foreach (VisualElement row in rows)
            {
                body.Add(row);
            }
            return new SettingsSearchCard { TabLabel = tab, Title = title, Card = card, Body = body };
        }

        private static VisualElement Row(string text)
        {
            var row = new VisualElement();
            row.Add(new Label(text));
            return row;
        }

        [Test]
        public void Filter_HidesNonMatchingRowsAndEmptyCards_ThenRestoresInlineDisplayExactly()
        {
            VisualElement hiddenByState = Row("Beep when a turn completes");
            hiddenByState.style.display = DisplayStyle.None;
            SettingsSearchCard notifications = MakeCard("Panel", "Notifications",
                Row("Beep on permission request"), hiddenByState);
            SettingsSearchCard display = MakeCard("Panel", "Display", Row("Show thinking blocks"));
            var filter = new SettingsSearchFilter(new List<SettingsSearchCard> { notifications, display });

            int visible = filter.Apply("beep");
            Assert.AreEqual(2, visible);
            Assert.IsTrue(filter.IsActive);
            Assert.AreEqual(DisplayStyle.Flex, notifications.Card.style.display.value);
            Assert.AreEqual(DisplayStyle.None, display.Card.style.display.value);
            Assert.AreEqual(DisplayStyle.Flex, hiddenByState.style.display.value,
                "a matching row is shown even if state had hidden it");
            Assert.IsNotNull(notifications.Crumb);
            Assert.AreEqual(DisplayStyle.Flex, notifications.Crumb.style.display.value);
            StringAssert.Contains("Panel", notifications.Crumb.text);
            StringAssert.Contains("Notifications", notifications.Crumb.text);

            filter.Clear();
            Assert.IsFalse(filter.IsActive);
            Assert.AreEqual(DisplayStyle.None, hiddenByState.style.display.value,
                "the state-driven inline display comes back exactly");
            Assert.AreEqual(StyleKeyword.Null, display.Card.style.display.keyword,
                "an element that had no inline display gets none back");
            Assert.AreEqual(DisplayStyle.None, notifications.Crumb.style.display.value);
        }

        [Test]
        public void Filter_TitleMatch_ShowsTheWholeCard_AndNoMatchReturnsZero()
        {
            SettingsSearchCard model = MakeCard("Agent", "Model", Row("Default model"), Row("Subagent cost policy"));
            var filter = new SettingsSearchFilter(new List<SettingsSearchCard> { model });
            Assert.AreEqual(2, filter.Apply("model"));
            Assert.AreEqual(2, filter.Apply("MODEL"));
            Assert.AreEqual(1, filter.Apply("subagent"));
            Assert.AreEqual(0, filter.Apply("zzz"));
            Assert.AreEqual(DisplayStyle.None, model.Card.style.display.value);
            Assert.AreEqual(0, filter.Apply("   "), "an inactive query clears the filter");
            Assert.IsFalse(filter.IsActive);
        }

        [Test]
        public void Filter_OpensAMatchedFoldout()
        {
            var foldout = new Foldout { text = "Per-type overrides (advanced)", value = false };
            foldout.Add(new Label("Force subagent model"));
            SettingsSearchCard model = MakeCard("Agent", "Model", Row("Default model"), foldout);
            var filter = new SettingsSearchFilter(new List<SettingsSearchCard> { model });
            Assert.AreEqual(1, filter.Apply("force"));
            Assert.IsTrue(foldout.value, "a hit inside a collapsed foldout opens it");
        }

        [Test]
        public void SettingsView_Search_HidesTabs_FiltersAcrossTabs_AndRestores()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var search = root.Q<TextField>(className: "uap-settings-search");
                VisualElement tabs = root.Q(className: "uap-settings-tabs");
                VisualElement notifications = root.Q("uap-card-" + SettingsView.NotificationsCardId);
                VisualElement conversation = root.Q("uap-card-" + SettingsView.ConversationCardId);
                VisualElement uapOps = root.Q("uap-card-" + SettingsView.UapOpsCardId);
                var bodies = root.Query(className: "uap-settings-tab-body").ToList();
                SettingsView view = window.SettingsViewForTests;

                // A never-shown window has no panel, so the field's value
                // change raises no ChangeEvent; drive the handler directly.
                search.SetValueWithoutNotify(L10n.S.SettingsPermissionBeepLabel);
                view.ApplySearch(search.value);
                Assert.AreEqual(DisplayStyle.None, tabs.style.display.value, "the tab strip hides while searching");
                Assert.AreEqual(DisplayStyle.Flex, bodies[(int)SettingsTab.Panel].style.display.value);
                Assert.AreEqual(DisplayStyle.None, bodies[(int)SettingsTab.Overview].style.display.value,
                    "the Overview mirrors other settings and is not searched");
                Assert.AreEqual(DisplayStyle.Flex, notifications.style.display.value);
                Assert.AreEqual(DisplayStyle.None, conversation.style.display.value);
                Assert.AreEqual(DisplayStyle.None, uapOps.style.display.value);
                Assert.AreEqual(DisplayStyle.Flex,
                    notifications.Q<Label>(className: "uap-settings-search-crumb").style.display.value);
                Assert.AreEqual(DisplayStyle.Flex,
                    root.Q<Button>(className: "uap-settings-search-clear").style.display.value);

                search.SetValueWithoutNotify("zzzz-no-such-setting");
                view.ApplySearch(search.value);
                Assert.AreEqual(DisplayStyle.Flex,
                    root.Q(className: "uap-settings-search-empty").style.display.value);

                search.SetValueWithoutNotify(string.Empty);
                view.ApplySearch(search.value);
                Assert.AreEqual(DisplayStyle.Flex, tabs.style.display.value, "clearing brings the tabs back");
                Assert.AreEqual(DisplayStyle.Flex, bodies[(int)SettingsTab.Overview].style.display.value,
                    "the active tab (Overview by default) is shown again");
                Assert.AreEqual(DisplayStyle.None, bodies[(int)SettingsTab.Panel].style.display.value);
                Assert.AreEqual(DisplayStyle.None,
                    root.Q(className: "uap-settings-search-empty").style.display.value);
                Assert.AreEqual(DisplayStyle.None,
                    root.Q<Button>(className: "uap-settings-search-clear").style.display.value);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void SettingsView_ShowDuringSearch_ClearsTheSearchFirst()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                VisualElement root = window.rootVisualElement.Q(className: "uap-settings");
                var search = root.Q<TextField>(className: "uap-settings-search");
                search.SetValueWithoutNotify("beep");
                window.SettingsViewForTests.ApplySearch("beep");
                Assert.IsTrue(window.SettingsViewForTests.IsSearchActive);
                window.RequestShowSettingsCard(SettingsTab.Agent, SettingsView.ModelCardId);
                Assert.IsFalse(window.SettingsViewForTests.IsSearchActive);
                Assert.AreEqual(string.Empty, search.value);
                Assert.AreEqual(DisplayStyle.Flex, root.Q(className: "uap-settings-tabs").style.display.value);
                var tabButtons = root.Query<Button>(className: "uap-settings-tab").ToList();
                Assert.IsTrue(tabButtons[(int)SettingsTab.Agent].ClassListContains("uap-settings-tab--active"));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
