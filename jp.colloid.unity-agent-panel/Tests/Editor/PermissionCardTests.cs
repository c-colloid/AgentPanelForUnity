using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Structure tests for the AskUserQuestion stepper card (design note
    /// 2026-08-05-askuserquestion-stepper.md) plus the window-host height
    /// chain and the PermissionWindow initial-size decision.
    ///
    /// A PermissionCard builds a full element tree without any live panel
    /// (same approach as PermissionCardUndoBadgeTests), so tab/section
    /// structure and inline display styles are directly assertable.
    /// Navigation and option selection are driven through the card's
    /// internal seams (NavigateToQuestion / SelectOptionForTests) rather
    /// than synthetic ClickEvents: a detached Clickable never fires
    /// (measured -- see UapUiClickDispatcherTests' class comment), and a
    /// test that dispatches an event and asserts around the resulting
    /// silence pins the wrong behavior, which has bitten this project
    /// repeatedly.
    ///
    /// The height-chain checks are source scans in the UssHygieneTests
    /// style because the fix is pure USS: EditMode layout without a panel
    /// never runs Yoga against a real window height, so the design note
    /// (section 2b) requires the structural declarations to be pinned here
    /// and the actual viewport-vs-content measurement to be done live.
    /// </summary>
    [TestFixture]
    public class PermissionCardTests
    {
        private const string PackageUssPath =
            "Packages/jp.colloid.unity-agent-panel/Editor/UI/Uss/AgentPanel.uss";

        private const string PermissionWindowCsPath =
            "Packages/jp.colloid.unity-agent-panel/Editor/UI/PermissionWindow.cs";

        // -- Request builders --------------------------------------------------------

        private static string QuestionJson(string text, string header,
            bool multiSelect, int optionCount)
        {
            var sb = new StringBuilder();
            sb.Append("{\"question\":\"").Append(text).Append('\"');
            if (header != null)
            {
                sb.Append(",\"header\":\"").Append(header).Append('\"');
            }
            sb.Append(",\"multiSelect\":").Append(multiSelect ? "true" : "false");
            sb.Append(",\"options\":[");
            for (int i = 0; i < optionCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"label\":\"opt").Append(i)
                    .Append("\",\"description\":\"desc").Append(i).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static ControlRequestMessage BuildQuestionRequest(string requestId,
            params string[] questionJson)
        {
            string json = "{\"request_id\":\"" + requestId + "\",\"request\":{"
                + "\"subtype\":\"can_use_tool\","
                + "\"tool_name\":\"AskUserQuestion\","
                + "\"requires_user_interaction\":true,"
                + "\"input\":{\"questions\":[" + string.Join(",", questionJson) + "]}}}";
            return ControlRequestMessage.FromJson(JsonParser.Parse(json), null);
        }

        private static ControlRequestMessage BuildToolRequest(string requestId)
        {
            string json = "{\"request_id\":\"" + requestId + "\",\"request\":{"
                + "\"subtype\":\"can_use_tool\",\"tool_name\":\"Write\",\"input\":{}}}";
            return ControlRequestMessage.FromJson(JsonParser.Parse(json), null);
        }

        private static ControlRequestMessage BuildToolRequestWithDescription(
            string requestId, string description)
        {
            string json = "{\"request_id\":\"" + requestId + "\",\"request\":{"
                + "\"subtype\":\"can_use_tool\",\"tool_name\":\"Write\","
                + "\"description\":\"" + description + "\",\"input\":{}}}";
            return ControlRequestMessage.FromJson(JsonParser.Parse(json), null);
        }

        private static PermissionCard BuildCard(ControlRequestMessage request)
        {
            var card = new PermissionCard();
            card.Refresh(request);
            return card;
        }

        private static PermissionCard BuildWindowCard(ControlRequestMessage request)
        {
            var card = new PermissionCard(PermissionCard.HostKind.Window);
            card.Refresh(request);
            return card;
        }

        private static ControlRequestMessage BuildToolRequestWithInput(
            string requestId, string toolName, string inputJson)
        {
            string json = "{\"request_id\":\"" + requestId + "\",\"request\":{"
                + "\"subtype\":\"can_use_tool\",\"tool_name\":\"" + toolName + "\","
                + "\"input\":" + inputJson + "}}";
            return ControlRequestMessage.FromJson(JsonParser.Parse(json), null);
        }

        private static Label FindSummaryTarget(PermissionCard card)
        {
            return card.Root.Q<Label>(className: "uap-perm-summary-target");
        }

        // ------------------------------------------------------------------
        // UXA-1: the collapsed summary row must always show the operation
        // target (file name / command), via the same ToolCardDescriber the
        // activity rows use.
        // ------------------------------------------------------------------

        [Test]
        public void SummaryTarget_WriteWithFilePath_ShowsFileName()
        {
            PermissionCard card = BuildCard(BuildToolRequestWithInput("t1", "Write",
                "{\"file_path\":\"Assets/Scripts/Player.cs\"}"));

            Label target = FindSummaryTarget(card);
            Assert.IsNotNull(target);
            Assert.AreEqual(DisplayStyle.Flex, target.style.display.value);
            Assert.AreEqual("Player.cs", target.text);
        }

        [Test]
        public void SummaryTarget_Bash_ShowsCommandSummary()
        {
            PermissionCard card = BuildCard(BuildToolRequestWithInput("t2", "Bash",
                "{\"command\":\"git commit -m message\"}"));

            Label target = FindSummaryTarget(card);
            Assert.AreEqual(DisplayStyle.Flex, target.style.display.value);
            StringAssert.StartsWith("git ", target.text);
        }

        [Test]
        public void SummaryTarget_EmptyInput_StaysHidden()
        {
            // The describer echoes the bare tool name back for empty input;
            // repeating "Write" next to "Allow Write?" adds nothing.
            PermissionCard card = BuildCard(BuildToolRequest("t3"));

            Assert.AreEqual(DisplayStyle.None, FindSummaryTarget(card).style.display.value);
        }

        [Test]
        public void SummaryTarget_QuestionVariant_StaysHidden()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("t4",
                QuestionJson("Pick one", null, false, 2)));

            Assert.AreEqual(DisplayStyle.None, FindSummaryTarget(card).style.display.value);
        }

        // ------------------------------------------------------------------
        // UXA-2: Edit approvals render a +/- diff, not the raw block dump.
        // ------------------------------------------------------------------

        [Test]
        public void EditRequest_RendersDiffLines_NotTheRawPreview()
        {
            PermissionCard card = BuildCard(BuildToolRequestWithInput("d1", "Edit",
                "{\"file_path\":\"A.cs\",\"old_string\":\"a\\nb\",\"new_string\":\"a\\nc\"}"));

            Assert.IsNotNull(card.Root.Q<VisualElement>(className: "uap-perm-diff"),
                "Edit must get the diff host");
            Assert.IsNotNull(card.Root.Q<Label>(className: "uap-perm-diff-del"));
            Assert.IsNotNull(card.Root.Q<Label>(className: "uap-perm-diff-add"));
            Assert.IsNull(card.Root.Q<Label>(className: "uap-perm-preview"),
                "the raw key: value dump must not render alongside the diff");
        }

        [Test]
        public void EditRequest_WithoutEditShape_FallsBackToRawPreview()
        {
            PermissionCard card = BuildCard(BuildToolRequestWithInput("d2", "Edit",
                "{\"file_path\":\"A.cs\"}"));

            Assert.IsNull(card.Root.Q<VisualElement>(className: "uap-perm-diff"));
            Assert.IsNotNull(card.Root.Q<Label>(className: "uap-perm-preview"),
                "an Edit input without old/new strings must fall back, never render a wrong diff");
        }

        [Test]
        public void EditRequest_LongDiff_TruncatesWithShowAllExpander()
        {
            var oldSb = new StringBuilder();
            var newSb = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                if (i > 0)
                {
                    oldSb.Append("\\n");
                    newSb.Append("\\n");
                }
                oldSb.Append("old").Append(i);
                newSb.Append("new").Append(i);
            }
            PermissionCard card = BuildCard(BuildToolRequestWithInput("d3", "Edit",
                "{\"old_string\":\"" + oldSb + "\",\"new_string\":\"" + newSb + "\"}"));

            VisualElement host = card.Root.Q<VisualElement>(className: "uap-perm-diff");
            Assert.IsNotNull(host);
            Assert.IsNotNull(host.Q<Button>(className: "uap-perm-diff-expand"),
                "40 diff lines must truncate behind the expander");
            int truncatedCount = host.Query<Label>(className: "uap-perm-diff-line").ToList().Count;
            Assert.Less(truncatedCount, 40, "the collapsed render must not show every line");

            // What the expander's click handler does (detached elements
            // deliver no click events, so drive the internal re-render).
            var lines = PermissionEditPreview.BuildEditDiff(
                JsonParser.Parse("{\"old_string\":\"" + oldSb + "\",\"new_string\":\"" + newSb + "\"}"));
            PermissionCard.RenderDiffLines(host, lines, int.MaxValue);

            int expandedCount = host.Query<Label>(className: "uap-perm-diff-line").ToList().Count;
            Assert.AreEqual(40, expandedCount, "the expander must render every line");
            Assert.IsNull(host.Q<Button>(className: "uap-perm-diff-expand"),
                "once everything is shown the expander goes away");
        }

        private static Label SummaryTitle(PermissionCard card)
        {
            Label title = card.Root.Q<Label>(className: "uap-perm-summary-title");
            Assert.IsNotNull(title, "every variant has a summary title label");
            return title;
        }

        /// <summary>
        /// Position of <paramref name="child"/> among <paramref name="container"/>'s
        /// logical children (Children(), the Add()-order view -- see
        /// UapUiChildAccessor's class comment for why this differs from
        /// hierarchy.Children() for a ScrollView), or -1 if absent. Manual
        /// walk rather than Children().ToList(): the latter would need
        /// System.Linq's Enumerable.ToList alongside UQueryBuilder's own
        /// (different) ToList extension already in scope via
        /// UnityEngine.UIElements, and this test file has no reason to
        /// invite that ambiguity for a one-line index lookup.
        /// </summary>
        private static int ChildIndex(VisualElement container, VisualElement child)
        {
            int index = 0;
            foreach (VisualElement candidate in container.Children())
            {
                if (candidate == child)
                {
                    return index;
                }
                index++;
            }
            return -1;
        }

        private static ControlRequestMessage ThreeSingleSelectQuestions(string requestId)
        {
            return BuildQuestionRequest(requestId,
                QuestionJson("Pick A", "First", false, 2),
                QuestionJson("Pick B", null, false, 2),
                QuestionJson("Pick C", "Third", false, 2));
        }

        private static List<VisualElement> Sections(PermissionCard card)
        {
            return card.Root.Query<VisualElement>(className: "uap-perm-question").ToList();
        }

        private static List<Button> Tabs(PermissionCard card)
        {
            return card.Root.Query<Button>(className: "uap-perm-qtab").ToList();
        }

        private static int VisibleSectionIndex(List<VisualElement> sections)
        {
            int visibleIndex = -1;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].style.display.value != DisplayStyle.None)
                {
                    Assert.AreEqual(-1, visibleIndex,
                        "exactly one question section may be visible at a time");
                    visibleIndex = i;
                }
            }
            Assert.AreNotEqual(-1, visibleIndex, "one section must be visible");
            return visibleIndex;
        }

        private static Button SubmitButton(PermissionCard card)
        {
            Button submit = card.Root.Q<Button>(className: "uap-card-btn--primary");
            Assert.IsNotNull(submit, "the question variant must have a Submit button");
            return submit;
        }

        // Query order is DOM order, and each question section holds exactly
        // one Other button/field, so list index == question index.

        private static Button OtherButton(PermissionCard card, int questionIndex)
        {
            List<Button> buttons = card.Root
                .Query<Button>(className: "uap-perm-option--other").ToList();
            Assert.Greater(buttons.Count, questionIndex,
                "every question must build an Other button");
            return buttons[questionIndex];
        }

        private static TextField OtherField(PermissionCard card, int questionIndex)
        {
            List<TextField> fields = card.Root
                .Query<TextField>(className: "uap-perm-other-field").ToList();
            Assert.Greater(fields.Count, questionIndex,
                "every question must build an Other text field");
            return fields[questionIndex];
        }

        // -- Structure: single question keeps today's layout ---------------------------

        [Test]
        public void SingleQuestion_BuildsNoTabStrip_AndSectionIsVisible()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("q1",
                QuestionJson("Only one", "H", false, 3)));

            Assert.IsNull(card.Root.Q<VisualElement>(className: "uap-perm-qtabs"),
                "a single question renders without the stepper tab strip");
            List<VisualElement> sections = Sections(card);
            Assert.AreEqual(1, sections.Count);
            Assert.AreEqual(0, VisibleSectionIndex(sections));
        }

        [Test]
        public void SingleQuestion_AnswerEnablesSubmit_WithoutNavigation()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("q2",
                QuestionJson("Only one", "H", false, 3)));

            Assert.IsFalse(SubmitButton(card).enabledSelf);
            card.SelectOptionForTests(0, 1);
            Assert.IsTrue(SubmitButton(card).enabledSelf);
            Assert.AreEqual(0, VisibleSectionIndex(Sections(card)),
                "with one question there is nowhere to advance to");
        }

        // -- Structure: multi-question stepper ---------------------------------------

        [Test]
        public void ThreeQuestions_BuildThreeTabs_AndOnlyTheFirstSectionIsVisible()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q3"));

            Assert.IsNotNull(card.Root.Q<VisualElement>(className: "uap-perm-qtabs"));
            List<Button> tabs = Tabs(card);
            Assert.AreEqual(3, tabs.Count);
            List<VisualElement> sections = Sections(card);
            Assert.AreEqual(3, sections.Count);
            Assert.AreEqual(0, VisibleSectionIndex(sections));
            Assert.IsTrue(tabs[0].ClassListContains("uap-perm-qtab--current"));
            Assert.IsFalse(tabs[1].ClassListContains("uap-perm-qtab--current"));
        }

        [Test]
        public void TabText_UsesHeader_OrTheLocalizedFallbackWhenAbsent()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q4"));

            List<Button> tabs = Tabs(card);
            Assert.AreEqual(IconLoader.SanitizeForDisplay("First"), tabs[0].text);
            Assert.AreEqual(L10n.F(L10n.S.PermQuestionTabFallbackFmt, 2), tabs[1].text,
                "a header-less question falls back to the numbered tab label");
            Assert.AreEqual(IconLoader.SanitizeForDisplay("Third"), tabs[2].text);
        }

        [Test]
        public void TabText_EmojiOnlyHeader_FallsBackToTheNumberedLabel()
        {
            // The emptiness check must run on the SANITIZED header. The
            // first version checked the raw header first, so a header the
            // sanitizer strips to nothing (an emoji-only header -- U+1F600
            // sits in the dropped pictograph block) rendered a blank,
            // unclickable-looking tab instead of the Q{n} fallback.
            PermissionCard card = BuildCard(BuildQuestionRequest("q-emoji",
                QuestionJson("Pick A", "\U0001F600", false, 2),
                QuestionJson("Pick B", "Real", false, 2)));

            List<Button> tabs = Tabs(card);
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay("\U0001F600"),
                "fixture premise: the sanitizer drops this codepoint entirely");
            Assert.AreEqual(L10n.F(L10n.S.PermQuestionTabFallbackFmt, 1), tabs[0].text);
        }

        // -- Real Button wiring (via the measured Clickable.Invoke path) ----------------

        [Test]
        public void TabButtonClick_ThroughTheRealClickable_NavigatesToThatQuestion()
        {
            // The other stepper tests drive NavigateToQuestion directly, so
            // a wrong index captured in the tab's click delegate would leave
            // them all green. UapUiClickDispatcher invokes the Button's own
            // Clickable manipulator -- the one dispatch route measured to
            // work on a detached element (see that class's strategy table)
            // -- so this exercises the actual delegate the user's click runs.
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q-wire1"));

            Colloid.AgentPanel.Ops.UapUiClickDispatcher.ClickOutcome outcome =
                Colloid.AgentPanel.Ops.UapUiClickDispatcher.Click(Tabs(card)[2]);

            Assert.IsTrue(outcome.Observed, outcome.Detail);
            Assert.AreEqual(2, VisibleSectionIndex(Sections(card)),
                "the delegate captured by tab 2 must navigate to question 2");
        }

        [Test]
        public void OptionButtonClick_ThroughTheRealClickable_SelectsAndAutoAdvances()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q-wire2"));
            List<Button> firstQuestionOptions = card.Root
                .Query<Button>(className: "uap-perm-option").ToList();

            Colloid.AgentPanel.Ops.UapUiClickDispatcher.ClickOutcome outcome =
                Colloid.AgentPanel.Ops.UapUiClickDispatcher.Click(firstQuestionOptions[1]);

            Assert.IsTrue(outcome.Observed, outcome.Detail);
            Assert.IsTrue(firstQuestionOptions[1].ClassListContains("uap-perm-option--selected"),
                "the real click delegate must record the selection");
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)),
                "and a single-select answer must auto-advance to the next unanswered question");
        }

        [Test]
        public void NavigateToQuestion_SwitchesTheVisibleSection_AndTheCurrentTab()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q5"));

            card.NavigateToQuestion(2);

            Assert.AreEqual(2, VisibleSectionIndex(Sections(card)));
            List<Button> tabs = Tabs(card);
            Assert.IsFalse(tabs[0].ClassListContains("uap-perm-qtab--current"));
            Assert.IsTrue(tabs[2].ClassListContains("uap-perm-qtab--current"));
        }

        [Test]
        public void NavigateToQuestion_OutOfRange_ClampsInsteadOfBreaking()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q6"));

            card.NavigateToQuestion(99);
            Assert.AreEqual(2, VisibleSectionIndex(Sections(card)));
            card.NavigateToQuestion(-1);
            Assert.AreEqual(0, VisibleSectionIndex(Sections(card)));
        }

        // -- Auto-advance behavior ---------------------------------------------------

        [Test]
        public void SingleSelectAnswer_AdvancesToTheNextUnanswered_AndMarksTheTab()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q7"));

            card.SelectOptionForTests(0, 0);

            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)));
            List<Button> tabs = Tabs(card);
            Assert.IsTrue(tabs[0].ClassListContains("uap-perm-qtab--answered"));
            Assert.IsTrue(tabs[1].ClassListContains("uap-perm-qtab--current"));
            Assert.IsFalse(tabs[1].ClassListContains("uap-perm-qtab--answered"));
        }

        [Test]
        public void SingleSelectAnswers_SkipAnswered_WrapOnce_AndStayPutWhenDone()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q8"));

            // Answer 0 -> advance to 1. Jump ahead and answer 2 -> the
            // advance search wraps past the end and lands on 1 (0 is
            // answered and must be skipped).
            card.SelectOptionForTests(0, 0);
            card.NavigateToQuestion(2);
            card.SelectOptionForTests(2, 1);
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)));

            // Answering the last open question: nothing unanswered is
            // left, so the card stays put with Submit enabled.
            card.SelectOptionForTests(1, 0);
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)));
            Assert.IsTrue(SubmitButton(card).enabledSelf);
        }

        [Test]
        public void MultiSelectToggles_NeverNavigate_AndTrackAnsweredBothWays()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("q9",
                QuestionJson("Pick A", "First", false, 2),
                QuestionJson("Pick many", "Second", true, 3),
                QuestionJson("Pick C", "Third", false, 2)));

            card.NavigateToQuestion(1);
            card.SelectOptionForTests(1, 0);
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)),
                "a multiSelect toggle must not yank the user to another question");
            Assert.IsTrue(Tabs(card)[1].ClassListContains("uap-perm-qtab--answered"));

            card.SelectOptionForTests(1, 2);
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)));

            // Toggling both back off un-answers the question again.
            card.SelectOptionForTests(1, 0);
            card.SelectOptionForTests(1, 2);
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)));
            Assert.IsFalse(Tabs(card)[1].ClassListContains("uap-perm-qtab--answered"));
        }

        [Test]
        public void SubmitGating_RequiresEveryQuestionAnswered()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("q10"));

            Assert.IsFalse(SubmitButton(card).enabledSelf);
            card.SelectOptionForTests(0, 0);
            Assert.IsFalse(SubmitButton(card).enabledSelf);
            card.SelectOptionForTests(1, 0);
            Assert.IsFalse(SubmitButton(card).enabledSelf);
            card.SelectOptionForTests(2, 0);
            Assert.IsTrue(SubmitButton(card).enabledSelf);
        }

        // -- Other free-text option (Claude Desktop parity) -----------------------------
        //
        // Selection and text entry are driven through the internal seams
        // (SelectOtherForTests / SetOtherTextForTests) for the same reason
        // as everything else in this fixture: a detached BaseField's value
        // setter never raises ChangeEvent and a detached Clickable never
        // fires, so the seams run exactly the handlers the real
        // interactions are wired to.

        [Test]
        public void EveryQuestion_GetsAnOtherButton_AndAFieldHiddenUntilSelected()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot1",
                QuestionJson("Pick A", "First", false, 2),
                QuestionJson("Pick many", "Second", true, 3)));

            for (int q = 0; q < 2; q++)
            {
                Button other = OtherButton(card, q);
                Assert.IsTrue(other.ClassListContains("uap-perm-option"),
                    "the Other button must be option-shaped (same class)");
                Assert.IsFalse(other.ClassListContains("uap-perm-option--selected"));
                Assert.AreEqual(DisplayStyle.None,
                    OtherField(card, q).style.display.value,
                    "the text field stays hidden until Other is selected");
            }
        }

        [Test]
        public void SingleSelect_OtherWithEmptyText_IsUnanswered_UntilTextIsTyped()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot2",
                QuestionJson("Only one", "H", false, 2)));

            card.SelectOtherForTests(0);
            Assert.IsTrue(OtherButton(card, 0).ClassListContains("uap-perm-option--selected"));
            Assert.AreEqual(DisplayStyle.Flex, OtherField(card, 0).style.display.value,
                "selecting Other must reveal the text field");
            Assert.IsFalse(SubmitButton(card).enabledSelf,
                "Other with no text is not an answer");

            card.SetOtherTextForTests(0, "   ");
            Assert.IsFalse(SubmitButton(card).enabledSelf,
                "whitespace-only text is not an answer either (the payload trims it)");

            card.SetOtherTextForTests(0, "my own words");
            Assert.IsTrue(SubmitButton(card).enabledSelf);

            card.SetOtherTextForTests(0, string.Empty);
            Assert.IsFalse(SubmitButton(card).enabledSelf,
                "deleting the text un-answers the question again");
        }

        [Test]
        public void SingleSelect_RealOptionClick_ClearsOther_AndDropsItsText()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot3",
                QuestionJson("Only one", "H", false, 2)));

            card.SelectOtherForTests(0);
            card.SetOtherTextForTests(0, "typed then abandoned");
            card.SelectOptionForTests(0, 1);

            Assert.IsFalse(OtherButton(card, 0).ClassListContains("uap-perm-option--selected"));
            Assert.AreEqual(DisplayStyle.None, OtherField(card, 0).style.display.value);
            Assert.AreEqual(string.Empty, OtherField(card, 0).value,
                "abandoned free text must not survive invisibly into Submit");
            Assert.IsTrue(SubmitButton(card).enabledSelf, "the real option answers");
            List<KeyValuePair<string, string>> answers = card.CollectAnswersForSubmit();
            Assert.AreEqual(1, answers.Count);
            Assert.AreEqual("opt1", answers[0].Value,
                "only the real option's label may reach the payload");
        }

        [Test]
        public void SingleSelect_OtherClick_ClearsTheRealOptionSelection()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot4",
                QuestionJson("Only one", "H", false, 2)));

            card.SelectOptionForTests(0, 0);
            Assert.IsTrue(SubmitButton(card).enabledSelf);

            card.SelectOtherForTests(0);
            List<Button> options = card.Root
                .Query<Button>(className: "uap-perm-option").ToList();
            Assert.IsFalse(options[0].ClassListContains("uap-perm-option--selected"),
                "single-select: Other replaces the real pick (radio semantics)");
            Assert.IsFalse(SubmitButton(card).enabledSelf,
                "and with no text yet the question is unanswered again");
        }

        [Test]
        public void SelectingOther_AndTyping_NeverAutoAdvanceTheStepper()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("ot5"));

            card.SelectOtherForTests(0);
            Assert.AreEqual(0, VisibleSectionIndex(Sections(card)),
                "selecting Other starts an answer; advancing would hide the field");

            card.SetOtherTextForTests(0, "typing in progress");
            Assert.AreEqual(0, VisibleSectionIndex(Sections(card)),
                "typing must never yank the field out from under the user");
            Assert.IsTrue(Tabs(card)[0].ClassListContains("uap-perm-qtab--answered"),
                "but the answered tab styling tracks the text");
        }

        [Test]
        public void EnterInOtherField_AdvancesOnlyWithNonEmptyText()
        {
            PermissionCard card = BuildCard(ThreeSingleSelectQuestions("ot6"));

            card.SelectOtherForTests(0);
            card.EnterInOtherFieldForTests(0);
            Assert.AreEqual(0, VisibleSectionIndex(Sections(card)),
                "Enter with empty text has no answer to be done with");

            card.SetOtherTextForTests(0, "done typing");
            card.EnterInOtherFieldForTests(0);
            Assert.AreEqual(1, VisibleSectionIndex(Sections(card)),
                "Enter with text is the explicit completion signal and advances");
        }

        [Test]
        public void MultiSelect_OtherCoexists_AndEmptyOtherAddsNothing()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot7",
                QuestionJson("Pick many", "H", true, 3)));

            card.SelectOptionForTests(0, 0);
            card.SelectOtherForTests(0);
            Assert.IsTrue(SubmitButton(card).enabledSelf,
                "the checked real option already answers the question");
            List<KeyValuePair<string, string>> answers = card.CollectAnswersForSubmit();
            Assert.AreEqual(1, answers.Count);
            Assert.AreEqual("opt0", answers[0].Value,
                "a selected Other with empty text must not add an empty string");

            card.SetOtherTextForTests(0, "  extra thought  ");
            answers = card.CollectAnswersForSubmit();
            Assert.AreEqual("opt0, extra thought", answers[0].Value,
                "non-empty Other text is appended (trimmed) after the real labels");

            // Toggling Other back off drops its contribution again.
            card.SelectOtherForTests(0);
            answers = card.CollectAnswersForSubmit();
            Assert.AreEqual("opt0", answers[0].Value);
        }

        [Test]
        public void Payload_TwoQuestions_SingleOtherText_AndMultiOptionPlusOther()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot8",
                QuestionJson("Pick A", "First", false, 2),
                QuestionJson("Pick many", "Second", true, 3)));

            card.SelectOtherForTests(0);
            card.SetOtherTextForTests(0, "my own words");
            card.NavigateToQuestion(1);
            card.SelectOptionForTests(1, 2);
            card.SelectOtherForTests(1);
            card.SetOtherTextForTests(1, "and this");

            Assert.IsTrue(SubmitButton(card).enabledSelf);
            List<KeyValuePair<string, string>> answers = card.CollectAnswersForSubmit();
            Assert.AreEqual(2, answers.Count);
            Assert.AreEqual("Pick A", answers[0].Key,
                "answers stay keyed by question text (the only verified key)");
            Assert.AreEqual("my own words", answers[0].Value,
                "single-select Other sends the bare text -- no 'Other:' prefix");
            Assert.AreEqual("Pick many", answers[1].Key);
            Assert.AreEqual("opt2, and this", answers[1].Value,
                "multiSelect appends the text to the labels before joining");
        }

        [Test]
        public void SecondRequest_DoesNotInheritTheOtherStateOfTheFirst()
        {
            // Mirror of the strip-stacking regression below: per-request
            // reset must cover the Other selection AND its typed text.
            var card = new PermissionCard();
            card.Refresh(BuildQuestionRequest("ot9",
                QuestionJson("Old question", "H", false, 2)));
            card.SelectOtherForTests(0);
            card.SetOtherTextForTests(0, "stale text");

            card.Refresh(BuildQuestionRequest("ot10",
                QuestionJson("New question", "H", false, 2)));

            Assert.IsFalse(OtherButton(card, 0).ClassListContains("uap-perm-option--selected"));
            Assert.AreEqual(string.Empty, OtherField(card, 0).value,
                "a second request must never inherit the previous request's typed text");
            Assert.AreEqual(DisplayStyle.None, OtherField(card, 0).style.display.value);
            Assert.IsFalse(SubmitButton(card).enabledSelf);
            Assert.AreEqual(0, card.CollectAnswersForSubmit().Count);
        }

        [Test]
        public void OtherButtonClick_ThroughTheRealClickable_SelectsOther_AndClearsTheRealPick()
        {
            // The seam tests above drive OnOtherClicked directly, so a
            // wrong index captured in the Other button's click delegate
            // would leave them all green -- same rationale as the tab and
            // option wiring tests.
            PermissionCard card = BuildCard(BuildQuestionRequest("ot11",
                QuestionJson("Only one", "H", false, 2)));
            card.SelectOptionForTests(0, 0);

            Colloid.AgentPanel.Ops.UapUiClickDispatcher.ClickOutcome outcome =
                Colloid.AgentPanel.Ops.UapUiClickDispatcher.Click(OtherButton(card, 0));

            Assert.IsTrue(outcome.Observed, outcome.Detail);
            Assert.IsTrue(OtherButton(card, 0).ClassListContains("uap-perm-option--selected"));
            List<Button> options = card.Root
                .Query<Button>(className: "uap-perm-option").ToList();
            Assert.IsFalse(options[0].ClassListContains("uap-perm-option--selected"),
                "the real click delegate must clear the real single-select pick");
        }

        [Test]
        public void RealOptionClick_ThroughTheRealClickable_ClearsOtherAndItsText()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("ot12",
                QuestionJson("Only one", "H", false, 2)));
            card.SelectOtherForTests(0);
            card.SetOtherTextForTests(0, "typed");
            List<Button> options = card.Root
                .Query<Button>(className: "uap-perm-option").ToList();

            Colloid.AgentPanel.Ops.UapUiClickDispatcher.ClickOutcome outcome =
                Colloid.AgentPanel.Ops.UapUiClickDispatcher.Click(options[1]);

            Assert.IsTrue(outcome.Observed, outcome.Detail);
            Assert.IsFalse(OtherButton(card, 0).ClassListContains("uap-perm-option--selected"));
            Assert.AreEqual(string.Empty, OtherField(card, 0).value);
        }

        // -- 2026-08-14 UI polish audit item 5 -------------------------------------------

        [Test]
        public void QuestionVariant_SkipButton_IsQuiet()
        {
            // Skip is the "no answer" escape hatch, not a peer of Submit --
            // it must not compete visually with it the way Deny
            // legitimately competes with Allow in the tool variant.
            PermissionCard card = BuildCard(BuildQuestionRequest("cap1",
                QuestionJson("Only one", "H", false, 2)));

            Button skip = card.Root.Q<Button>(className: "uap-card-btn--quiet");
            Assert.IsNotNull(skip, "the question variant's Skip button must carry --quiet");
            Assert.AreEqual(L10n.S.PermSkipButton, skip.text);
        }

        // -- 2026-09-05 UI redesign D4 ------------------------------------------------

        [Test]
        public void ToolVariant_DenyButton_IsTheDangerMember_AndAllowIsNot()
        {
            // Deny is "stop this", not a peer of Always: it carries --danger
            // on top of the plain shell. Allow must stay the only --primary
            // and must never pick up --danger.
            PermissionCard card = BuildCard(BuildToolRequest("danger1"));

            Button deny = card.Root.Q<Button>(className: "uap-card-btn--danger");
            Assert.IsNotNull(deny, "the tool variant's Deny button must carry --danger");
            Assert.AreEqual(L10n.S.PermDenyButton, deny.text);
            Assert.IsTrue(deny.ClassListContains("uap-card-btn"),
                "--danger is a modifier on the plain shell, not a replacement for it");
            Assert.IsFalse(deny.ClassListContains("uap-card-btn--primary"));

            Button allow = card.Root.Q<Button>(className: "uap-card-btn--primary");
            Assert.IsNotNull(allow);
            Assert.IsFalse(allow.ClassListContains("uap-card-btn--danger"));
        }

        [Test]
        public void QuestionVariant_DoesNotBuildADangerButton()
        {
            // Skip is quiet, not dangerous -- answering nothing is safe.
            PermissionCard card = BuildCard(BuildQuestionRequest("danger2",
                QuestionJson("Only one", "H", false, 2)));

            Assert.IsNull(card.Root.Q<Button>(className: "uap-card-btn--danger"));
        }

        [Test]
        public void ToolVariant_DoesNotBuildAQuietButton()
        {
            // --quiet is scoped to the question variant's Skip only -- Deny
            // in the normal tool variant must not pick it up.
            PermissionCard card = BuildCard(BuildToolRequest("cap2"));

            Assert.IsNull(card.Root.Q<Button>(className: "uap-card-btn--quiet"));
        }

        [Test]
        public void ToolVariant_InlineHost_KeepsTheCombinedTitle_WhenDescriptionPresent()
        {
            // 2026-09-06 review fix 1: the inline host still combines name +
            // description on its collapsed row (the only place the
            // description is seen without expanding), but WITHOUT the
            // "Claude wants to use" prefix -- the warn icon, the card colour
            // and the Allow/Deny row already say permission, and the prefix
            // was what pushed the target off the row.
            PermissionCard card = BuildCard(
                BuildToolRequestWithDescription("cap3", "does a thing"));

            Assert.AreEqual(
                IconLoader.SanitizeForDisplay(
                    L10n.F(L10n.S.PermInlineTitleWithDescriptionFmt, "Write", "does a thing")),
                SummaryTitle(card).text);
        }

        [Test]
        public void ToolVariant_InlineHost_TitleIsTheBareToolName_WithoutDescription()
        {
            PermissionCard card = BuildCard(BuildToolRequest("cap3b"));

            Assert.AreEqual(IconLoader.SanitizeForDisplay("Write"), SummaryTitle(card).text);
        }

        // -- 2026-09-06 review fix 1: wrap-capable summary row --------------------

        [Test]
        public void SummaryRow_GroupsTextAndControls_SoTheTargetNeverYieldsToTheButtons()
        {
            PermissionCard card = BuildCard(BuildToolRequest("wrap1"));

            VisualElement summary = card.Root.Q<VisualElement>(className: "uap-perm-summary");
            VisualElement text = summary.Q<VisualElement>(className: "uap-perm-summary-text");
            VisualElement controls = summary.Q<VisualElement>(className: "uap-perm-summary-controls");
            Assert.IsNotNull(text, "title + target live in one text block");
            Assert.IsNotNull(controls, "buttons live in one controls block");
            Assert.IsNotNull(text.Q<Label>(className: "uap-perm-summary-title"));
            Assert.IsNotNull(text.Q<Label>(className: "uap-perm-summary-target"));
            Assert.IsNotNull(controls.Q<Button>(className: "uap-card-btn--primary"),
                "Allow sits in the controls block");
            Assert.IsNotNull(controls.Q<Button>(className: "uap-card-btn--danger"),
                "Deny sits in the controls block");
            Assert.Less(ChildIndex(summary, text), ChildIndex(summary, controls),
                "text precedes controls, so a wrap puts the buttons on the SECOND line");
        }

        [Test]
        public void ToolVariant_WindowHost_UsesTheNameOnlyTitle_WhenDescriptionPresent()
        {
            // The Window host also renders the description as its own
            // .uap-perm-desc label below -- combining it into the title
            // too would show it twice.
            PermissionCard card = BuildWindowCard(
                BuildToolRequestWithDescription("cap4", "does a thing"));

            Assert.AreEqual(
                IconLoader.SanitizeForDisplay(L10n.F(L10n.S.PermTitleFmt, "Write")),
                SummaryTitle(card).text);
            Label desc = card.Root.Q<Label>(className: "uap-perm-desc");
            Assert.IsNotNull(desc, "the description still renders once, as its own label");
            Assert.AreEqual(IconLoader.SanitizeForDisplay("does a thing"), desc.text);
        }

        [Test]
        public void ToolVariant_WindowHost_UsesTheNameOnlyTitle_EvenWithNoDescription()
        {
            // No description means both formats already agree, but pin the
            // Window-host branch is not accidentally description-gated.
            PermissionCard card = BuildWindowCard(BuildToolRequest("cap5"));

            Assert.AreEqual(
                IconLoader.SanitizeForDisplay(L10n.F(L10n.S.PermTitleFmt, "Write")),
                SummaryTitle(card).text);
        }

        /// <summary>
        /// UXA-4: the in-card deny field is the WINDOW host's entry (no
        /// composer there); the caption sits immediately above it, same as
        /// before.
        /// </summary>
        [Test]
        public void WindowVariant_DenyField_HasACaptionLabel_AboveIt()
        {
            PermissionCard card = BuildWindowCard(BuildToolRequest("cap6"));

            Label caption = card.Root.Q<Label>(className: "uap-perm-field-caption");
            Assert.IsNotNull(caption, "the deny field must have a caption above it");
            Assert.AreEqual(L10n.A(L10n.S.PermDenyFieldCaption), caption.text);

            TextField denyField = card.Root.Q<TextField>(className: "uap-perm-denyfield");
            VisualElement details = card.Root.Q<ScrollView>(className: "uap-perm-details");
            int captionIndex = ChildIndex(details, caption);
            int fieldIndex = ChildIndex(details, denyField);
            Assert.AreNotEqual(-1, captionIndex);
            Assert.AreEqual(fieldIndex - 1, captionIndex,
                "the caption must sit immediately above the deny field");
        }

        /// <summary>
        /// UXA-4: the INLINE card ships no in-card deny field at all -- it
        /// lived inside the collapsed details where nobody found it. The
        /// always-visible composer is the inline deny-reason entry
        /// (ConsumeExternalDenyMessage, wired by ChatView).
        /// </summary>
        [Test]
        public void ToolVariant_InlineCard_HasNoInCardDenyField()
        {
            PermissionCard card = BuildCard(BuildToolRequest("cap6i"));

            Assert.IsNull(card.Root.Q<TextField>(className: "uap-perm-denyfield"));
            Assert.IsNull(card.Root.Q<Label>(className: "uap-perm-field-caption"));
        }

        [Test]
        public void QuestionVariant_OtherField_HasACaptionLabel_HiddenUntilSelected()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("cap7",
                QuestionJson("Only one", "H", false, 2)));

            Label caption = card.Root.Q<Label>(className: "uap-perm-field-caption");
            Assert.IsNotNull(caption, "the Other field must have a caption above it");
            Assert.AreEqual(L10n.S.PermOtherFieldCaption, caption.text);
            Assert.AreEqual(DisplayStyle.None, caption.style.display.value,
                "hidden together with the field until Other is selected");

            card.SelectOtherForTests(0);
            Assert.AreEqual(DisplayStyle.Flex, caption.style.display.value,
                "shown together with the field once Other is selected");

            card.SelectOptionForTests(0, 1);
            Assert.AreEqual(DisplayStyle.None, caption.style.display.value,
                "hidden again together with the field once a real option replaces Other");
        }

        [Test]
        public void QuestionVariant_TwoQuestions_EachGetsItsOwnOtherCaption()
        {
            PermissionCard card = BuildCard(BuildQuestionRequest("cap8",
                QuestionJson("Pick A", "First", false, 2),
                QuestionJson("Pick B", "Second", false, 2)));

            List<Label> captions = card.Root
                .Query<Label>(className: "uap-perm-field-caption").ToList();
            Assert.AreEqual(2, captions.Count, "one Other-field caption per question");

            card.NavigateToQuestion(1);
            card.SelectOtherForTests(1);

            Assert.AreEqual(DisplayStyle.None, captions[0].style.display.value,
                "question 0's caption is unaffected by question 1's Other selection");
            Assert.AreEqual(DisplayStyle.Flex, captions[1].style.display.value);
        }

        // -- Rebuild hygiene -----------------------------------------------------------

        [Test]
        public void SecondQuestionRequest_ReplacesTheTabStrip_InsteadOfStackingASecondOne()
        {
            // The strip lives in _body (outside _details), so the
            // _details.Clear() in Build() alone would leave the previous
            // request's strip behind -- ResetStepperState() must remove it.
            var card = new PermissionCard();
            card.Refresh(ThreeSingleSelectQuestions("q11"));
            card.Refresh(BuildQuestionRequest("q12",
                QuestionJson("New A", "NA", false, 2),
                QuestionJson("New B", "NB", false, 2)));

            List<VisualElement> strips =
                card.Root.Query<VisualElement>(className: "uap-perm-qtabs").ToList();
            Assert.AreEqual(1, strips.Count, "exactly one strip after a rebuild");
            Assert.AreEqual(2, Tabs(card).Count, "and it belongs to the NEW request");
        }

        [Test]
        public void ToolRequestAfterQuestionRequest_RemovesTheTabStrip()
        {
            var card = new PermissionCard();
            card.Refresh(ThreeSingleSelectQuestions("q13"));
            card.Refresh(BuildToolRequest("q14"));

            Assert.IsNull(card.Root.Q<VisualElement>(className: "uap-perm-qtabs"));
        }

        // -- PermissionWindow initial-size decision ------------------------------------
        //
        // 2026-08-14 UI polish audit item 6: the flat 560x520 default over-
        // allocated for a short single question. The new height is a pure
        // function of the parsed question count -- 170 chrome + 175 for the
        // ONE currently visible section (the stepper never shows more than
        // one at a time, QuestionStepperModel) + 24 for the tab strip only
        // when there is one to show (2+ questions) -- so it does NOT scale
        // further with additional questions beyond that. Width stays 560.

        [Test]
        public void WindowInitialSize_SingleQuestion_IsShort_NotTheOldFlat520()
        {
            ControlRequestMessage request = BuildQuestionRequest("w1",
                QuestionJson("Only one", "H", false, 2));

            Assert.IsTrue(PermissionWindow.IsQuestionVariantRequest(request));
            Assert.AreEqual(new Vector2(560f, 345f),
                PermissionWindow.ComputeInitialSize(request),
                "one question has no tab strip to show: 170 chrome + 175 for its one section");
        }

        [Test]
        public void WindowInitialSize_TwoQuestions_AddsTheTabStripHeight()
        {
            Assert.AreEqual(new Vector2(560f, 369f),
                PermissionWindow.ComputeInitialSize(BuildQuestionRequest("w2",
                    QuestionJson("Pick A", "First", false, 2),
                    QuestionJson("Pick B", "Second", false, 2))),
                "170 chrome + 175 for the one visible section + 24 for the tab strip");
        }

        [Test]
        public void WindowInitialSize_FourQuestions_DoesNotGrowPastTheTwoQuestionHeight()
        {
            // The stepper shows exactly one section regardless of how many
            // questions there are, so 4 questions must size IDENTICALLY to
            // 2 -- not the old per-question multiplication (4 * 175px was
            // the very over-allocation this item replaces).
            ControlRequestMessage fourQuestions = BuildQuestionRequest("w2b",
                QuestionJson("Q1", null, false, 2),
                QuestionJson("Q2", null, false, 2),
                QuestionJson("Q3", null, false, 2),
                QuestionJson("Q4", null, false, 2));

            Assert.AreEqual(new Vector2(560f, 369f),
                PermissionWindow.ComputeInitialSize(fourQuestions));
        }

        [Test]
        public void ComputeQuestionVariantHeight_IsAPureFunctionOfQuestionCount()
        {
            // Direct coverage of the extracted pure function (also
            // EditMode-testable without building a ControlRequestMessage):
            // 0/1 -> no tab strip; 2 and above -> the flat +24, never more.
            Assert.AreEqual(345f, PermissionWindow.ComputeQuestionVariantHeight(1));
            Assert.AreEqual(369f, PermissionWindow.ComputeQuestionVariantHeight(2));
            Assert.AreEqual(369f, PermissionWindow.ComputeQuestionVariantHeight(3));
            Assert.AreEqual(369f, PermissionWindow.ComputeQuestionVariantHeight(10),
                "many questions must not grow the window past the 2-question height");
        }

        /// <summary>
        /// Regression guard for the clamp floor/ceiling (2026-08-14 UI
        /// polish audit item 6): the realistic question-count formula above
        /// never itself reaches [320, 520]'s bounds (345/369 both sit
        /// comfortably inside), so no behavioral test through
        /// ComputeInitialSize/ComputeQuestionVariantHeight can catch their
        /// removal -- pin the literal clamp call and bounds in source
        /// instead, the same source-scan approach the height-chain checks
        /// below already use for USS.
        /// </summary>
        [Test]
        public void SourceScan_ComputeQuestionVariantHeight_ClampsToDesignedBounds()
        {
            string text = File.ReadAllText(Path.GetFullPath(PermissionWindowCsPath));
            StringAssert.Contains("Mathf.Clamp(height, QuestionHeightMin, QuestionHeightMax)",
                text);
            StringAssert.Contains("QuestionHeightMin = 320f", text);
            StringAssert.Contains("QuestionHeightMax = 520f", text);
        }

        [Test]
        public void WindowInitialSize_ToolVariant_KeepsTheClassic420x360()
        {
            Assert.IsFalse(PermissionWindow.IsQuestionVariantRequest(BuildToolRequest("w3")));
            Assert.AreEqual(new Vector2(420f, 360f),
                PermissionWindow.ComputeInitialSize(BuildToolRequest("w4")));
            Assert.AreEqual(new Vector2(420f, 360f),
                PermissionWindow.ComputeInitialSize(null));
        }

        [Test]
        public void WindowInitialSize_InteractionFlagWithNoParsableQuestions_IsToolVariant()
        {
            // PermissionCard.Build() falls back to the tool variant when
            // questions parse empty, so the size decision must agree.
            string json = "{\"request_id\":\"w5\",\"request\":{"
                + "\"subtype\":\"can_use_tool\",\"tool_name\":\"AskUserQuestion\","
                + "\"requires_user_interaction\":true,\"input\":{\"questions\":[]}}}";
            ControlRequestMessage request =
                ControlRequestMessage.FromJson(JsonParser.Parse(json), null);

            Assert.IsFalse(PermissionWindow.IsQuestionVariantRequest(request));
            Assert.AreEqual(new Vector2(420f, 360f),
                PermissionWindow.ComputeInitialSize(request));

            // "Must agree" has to be PINNED, not narrated: the window's
            // decision and the card's are independently written pieces of
            // logic, and the first version of this test asserted only the
            // window half -- so the two could drift apart (window sizes for
            // a tool card, card renders a stepper, or vice versa) with the
            // suite green. Build the card from the SAME request and assert
            // it actually took the tool variant.
            PermissionCard card = BuildCard(request);
            Assert.AreEqual(0, Sections(card).Count,
                "the card must fall back to the tool variant for this request, matching the size decision");
        }

        [Test]
        public void WindowInitialSize_AgreesWithTheCardVariant_ForTheQuestionShape()
        {
            // The other half of the same agreement: a request the window
            // sizes as a question card must actually render as one.
            ControlRequestMessage request = ThreeSingleSelectQuestions("w6");

            Assert.IsTrue(PermissionWindow.IsQuestionVariantRequest(request));
            Assert.AreEqual(new Vector2(560f, 369f),
                PermissionWindow.ComputeInitialSize(request));
            PermissionCard card = BuildCard(request);
            Assert.AreEqual(3, Sections(card).Count,
                "the card must take the question variant for the request the window sized as one");
        }

        // -- Window-host height chain (USS source scan; design note section 2b) --------

        /// <summary>
        /// The measured defect: UITK defaults BOTH flex-grow and
        /// flex-shrink to 0, so a 4-question card grew to 736px inside the
        /// 360px window and the _details ScrollView (viewport 707px vs
        /// content 699px) never scrolled. The fix is a pure-USS chain --
        /// every element from .uap-permwin-root down to .uap-perm-details
        /// opts into grow/shrink/min-height -- and EditMode cannot run
        /// Yoga against a real window height, so this pins the
        /// declarations and the design note requires the live
        /// viewport-smaller-than-content measurement separately.
        /// </summary>
        [Test]
        public void SourceScan_WindowHostHeightChain_DeclaresGrowShrinkAndMinHeight()
        {
            string text = File.ReadAllText(Path.GetFullPath(PackageUssPath));

            string permwinRoot = ExtractRuleBlock(text, ".uap-permwin-root");
            StringAssert.Contains("flex-grow: 1", permwinRoot);
            StringAssert.Contains("flex-shrink: 1", permwinRoot,
                ".uap-permwin-root must shrink or the host itself overflows the window");
            StringAssert.Contains("min-height: 0", permwinRoot);

            string cardRoot = ExtractRuleBlock(text, ".uap-perm--windowhost");
            AssertDeclaration(cardRoot, "flex-grow", "1");
            AssertDeclaration(cardRoot, "flex-shrink", "1",
                ".uap-perm--windowhost must override the base .uap-perm flex-shrink: 0 "
                + "(the collapsed-crush guard protects the INLINE card only)");
            AssertDeclaration(cardRoot, "min-height", "0");

            string body = ExtractRuleBlock(text, ".uap-perm--windowhost .uap-perm-body");
            AssertDeclaration(body, "flex-grow", "1");
            AssertDeclaration(body, "flex-shrink", "1");
            AssertDeclaration(body, "min-height", "0");

            string details = ExtractRuleBlock(text, ".uap-perm--windowhost .uap-perm-details");
            AssertDeclaration(details, "flex-grow", "1");
            AssertDeclaration(details, "flex-shrink", "1");
            AssertDeclaration(details, "min-height", "0");
        }

        /// <summary>
        /// The stepper strip must never be the element that gives height
        /// back -- the details area below it is the designated shrinker --
        /// and its repeated tabs must stay one line tall in one row
        /// (standing rule: docs/design-notes/
        /// 2026-08-03-list-row-control-alignment.md).
        /// </summary>
        [Test]
        public void SourceScan_QuestionTabStrip_KeepsRowAlignmentGuards()
        {
            string text = File.ReadAllText(Path.GetFullPath(PackageUssPath));

            string strip = ExtractRuleBlock(text, ".uap-perm-qtabs");
            AssertDeclaration(strip, "flex-direction", "row");
            AssertDeclaration(strip, "flex-shrink", "0");

            string tab = ExtractRuleBlock(text, ".uap-perm-qtab");
            AssertDeclaration(tab, "flex-shrink", "1",
                "long localized headers must give width back instead of pushing "
                + "later tabs out of the strip");
            StringAssert.Contains("min-width", tab);
            AssertDeclaration(tab, "white-space", "nowrap",
                "wrapping would make one tab two lines tall and break the "
                + "one-height rule for repeated controls");
            AssertDeclaration(tab, "text-overflow", "ellipsis");
        }

        /// <summary>Same selector-at-line-start block match as
        /// UssHygieneTests.ExtractRuleBlock (duplicated because that helper
        /// is private to its fixture) -- with two hardenings the diff
        /// review proved necessary, because the first version of these
        /// scans could not fail:
        /// (1) comments are stripped BEFORE matching, so a block-commented
        /// rule no longer matches (dead CSS passed both scans) and a
        /// comment inside a live block no longer satisfies a declaration
        /// assertion (the windowhost body block's explanatory comment
        /// contained the very strings the test looked for -- deleting the
        /// actual declarations kept the test green);
        /// (2) callers assert whole declarations via AssertDeclaration
        /// (exact property: value pair up to the semicolon), because the
        /// substring form accepted "flex-grow: 10" for "flex-grow: 1" and
        /// "min-width: 0" for "min-width".</summary>
        private static string ExtractRuleBlock(string text, string selector)
        {
            string noComments = Regex.Replace(text, @"/\*.*?\*/", string.Empty,
                RegexOptions.Singleline);
            var rule = new Regex(
                "^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}",
                RegexOptions.Multiline);
            Match m = rule.Match(noComments);
            Assert.IsTrue(m.Success, "rule block not found for selector: " + selector);
            return m.Groups["body"].Value;
        }

        /// <summary>Asserts the exact declaration "property: value;" exists
        /// in the (already comment-stripped) rule body.</summary>
        private static void AssertDeclaration(string ruleBody, string property, string value,
            string because = null)
        {
            var decl = new Regex(@"(^|;|\{)\s*" + Regex.Escape(property)
                + @"\s*:\s*" + Regex.Escape(value) + @"\s*(;|$)");
            Assert.IsTrue(decl.IsMatch(ruleBody),
                "expected declaration '" + property + ": " + value + ";'"
                + (because != null ? " -- " + because : string.Empty)
                + "\nrule body was:\n" + ruleBody);
        }

        // -- UXA-4: deny-reason resolution --------------------------------

        [Test]
        public void ResolveDenyMessage_InCardTextWins_OverExternal()
        {
            Assert.AreEqual("card reason",
                PermissionCard.ResolveDenyMessage("  card reason  ", "composer text"));
        }

        [Test]
        public void ResolveDenyMessage_EmptyInCard_FallsBackToExternal()
        {
            Assert.AreEqual("composer text",
                PermissionCard.ResolveDenyMessage("   ", " composer text "));
            Assert.AreEqual("composer text",
                PermissionCard.ResolveDenyMessage(null, "composer text"));
        }

        [Test]
        public void ResolveDenyMessage_BothBlank_UsesTheProtocolDefault()
        {
            Assert.AreEqual("User denied this tool use.",
                PermissionCard.ResolveDenyMessage(null, null));
            Assert.AreEqual("User denied this tool use.",
                PermissionCard.ResolveDenyMessage("  ", "  "));
        }

    }
}
