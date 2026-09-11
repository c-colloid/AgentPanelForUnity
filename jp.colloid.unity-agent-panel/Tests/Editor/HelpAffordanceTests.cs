using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-06 settings review: the "?" help mark (HelpAffordance) and
    /// the value-driven warning tone. Pure decisions pinned as tables; the
    /// mark application is checked on a detached tree.
    /// </summary>
    [TestFixture]
    public class HelpAffordanceTests
    {
        [Test]
        public void Resolve_EmptyOrNull_IsNone()
        {
            Assert.AreEqual(HelpAffordance.Kind.None, HelpAffordance.Resolve(null));
            Assert.AreEqual(HelpAffordance.Kind.None, HelpAffordance.Resolve(""));
        }

        [Test]
        public void Resolve_ShortCaption_StaysAPlainTooltip()
        {
            Assert.AreEqual(HelpAffordance.Kind.Tooltip, HelpAffordance.Resolve("Applies immediately."));
            Assert.AreEqual(HelpAffordance.Kind.Tooltip,
                HelpAffordance.Resolve(new string('x', HelpAffordance.MarkThresholdChars - 1)));
        }

        [Test]
        public void Resolve_Paragraph_EarnsAMark()
        {
            Assert.AreEqual(HelpAffordance.Kind.Mark,
                HelpAffordance.Resolve(new string('x', HelpAffordance.MarkThresholdChars)));
        }

        [Test]
        public void CreateMark_IsAnIconFamilyButton_CarryingTheText()
        {
            string text = new string('y', 120);
            Button mark = HelpAffordance.CreateMark(text);

            Assert.IsTrue(mark.ClassListContains(HelpAffordance.MarkClass));
            Assert.AreEqual(text, mark.tooltip);
            Assert.Greater(mark.childCount, 0, "the mark carries an icon (or its glyph fallback)");
        }

        [Test]
        public void ApplyMarks_AppendsOneMarkPerLongTooltipRow_AndFlagsSwitches()
        {
            var root = new VisualElement();
            // A field scope with a long tooltip: mark lands on the field row.
            var scopeA = new VisualElement { tooltip = new string('a', 150) };
            var fieldA = new TextField("Path");
            scopeA.Add(fieldA);
            root.Add(scopeA);
            // A switch scope with a long tooltip: mark lands on the toggle,
            // which also gets the marked-switch layout class.
            var scopeB = new VisualElement { tooltip = new string('b', 150) };
            var toggle = new Toggle("Enable");
            toggle.AddToClassList("uap-switch");
            scopeB.Add(toggle);
            root.Add(scopeB);
            // A short tooltip: no mark.
            var scopeC = new VisualElement { tooltip = "Applies immediately." };
            scopeC.Add(new TextField("Short"));
            root.Add(scopeC);

            int applied = HelpAffordance.ApplyMarks(root);

            Assert.AreEqual(2, applied);
            Assert.IsNotNull(fieldA.Q<Button>(className: HelpAffordance.MarkClass));
            Assert.IsNotNull(toggle.Q<Button>(className: HelpAffordance.MarkClass));
            Assert.IsTrue(toggle.ClassListContains(HelpAffordance.MarkedSwitchClass));
            Assert.IsTrue(fieldA.ClassListContains(HelpAffordance.MarkedRowClass), "marked rows pull over the gutter");
            Assert.IsTrue(toggle.ClassListContains(HelpAffordance.MarkedRowClass));
            Assert.IsFalse(scopeC.Q<TextField>().ClassListContains(HelpAffordance.MarkedRowClass));
            Assert.IsNull(scopeC.Q<Button>(className: HelpAffordance.MarkClass));

            // Idempotent: a second pass adds nothing.
            Assert.AreEqual(0, HelpAffordance.ApplyMarks(root));
        }

        [Test]
        public void ApplyMarks_LoneHintScope_WrapsHintIntoRowWithMarkInline()
        {
            var root = new VisualElement();
            var scope = new VisualElement { tooltip = new string('h', 150) };
            var hint = new Label("Appended every turn.");
            hint.AddToClassList("uap-settings-hint");
            scope.Add(hint);
            // Body is a plain container (a text area lives one level
            // deeper), so the scope has no field row of its own.
            var body = new VisualElement();
            body.Add(new TextField());
            scope.Add(body);
            root.Add(scope);

            Assert.AreEqual(1, HelpAffordance.ApplyMarks(root));

            var row = scope.Q<VisualElement>(className: HelpAffordance.HintRowClass);
            Assert.IsNotNull(row, "hint is wrapped into a hint row");
            Assert.AreSame(row, hint.parent);
            Assert.IsNotNull(row.Q<Button>(className: HelpAffordance.MarkClass), "mark sits inside the hint row");
            Assert.IsTrue(row.ClassListContains(HelpAffordance.MarkedRowClass));
            Assert.AreEqual(0, scope.IndexOf(row), "row keeps the hint's original position");
            Assert.AreEqual(body, scope[1]);
        }

        // -- value-driven warning tone -------------------------------------------

        [TestCase(UapAutoApproveLevel.Ask, false)]
        [TestCase(UapAutoApproveLevel.ReadOnly, false)]
        [TestCase(UapAutoApproveLevel.Undoable, true)]
        [TestCase(UapAutoApproveLevel.AllUnityOps, true)]
        public void IsRiskyAutoApproveLevel_Table(UapAutoApproveLevel level, bool risky)
        {
            Assert.AreEqual(risky, SettingsView.IsRiskyAutoApproveLevel(level));
        }

        [Test]
        public void GateAndAutoContinue_RiskIsTheNonDefaultValue()
        {
            Assert.IsFalse(SettingsView.IsRiskyScriptGateSetting(true), "gate on is the recommended default");
            Assert.IsTrue(SettingsView.IsRiskyScriptGateSetting(false));
            Assert.IsFalse(SettingsView.IsRiskyAutoContinueSetting(false), "auto-continue off is the default");
            Assert.IsTrue(SettingsView.IsRiskyAutoContinueSetting(true));
        }

        [Test]
        public void SetWarningTone_TogglesTheWarningClass_BothWays()
        {
            var label = new Label("x");
            label.AddToClassList("uap-settings-hint");

            SettingsView.SetWarningTone(label, true);
            Assert.IsTrue(label.ClassListContains("uap-settings-hint--warning"));
            SettingsView.SetWarningTone(label, false);
            Assert.IsFalse(label.ClassListContains("uap-settings-hint--warning"));
            SettingsView.SetWarningTone(null, true); // never throws
        }

        [TestCase("osasset:Noto Sans CJK JP", "Noto Sans CJK JP")]
        [TestCase("osasset-reused:UapJapaneseUi", "UapJapaneseUi")]
        [TestCase("Noto Sans CJK JP", "Noto Sans CJK JP")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void DescribeFontSource_StripsTheInternalTag(string source, string expected)
        {
            Assert.AreEqual(expected, SettingsView.DescribeFontSource(source));
        }
    }
}
