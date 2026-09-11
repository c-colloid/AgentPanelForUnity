using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Extends the keyed expand-state memory pattern SubagentCard already
    /// used for its OUTER card (see SubagentCardExpandStateTests, and
    /// design note 2026-08-03-subagent-ux-and-midturn-input.md section 1)
    /// to ToolActivityCard's own <c>_expanded</c> field, which was instance-
    /// only before this fix. MessageListController rebuilds a
    /// ToolActivityCard's row whenever ITS OWN ToolCallRecord's status
    /// changes (e.g. Running -&gt; Succeeded), discarding the old instance
    /// and its _expanded field; a card rendered NESTED inside a
    /// SubagentCard's expanded details (via MessageBlockFactory.
    /// CreateBlockElement) is discarded even more often, since it rebuilds
    /// whenever the WHOLE SubagentCard rebuilds for any reason while the
    /// subagent is still running.
    ///
    /// This suite constructs plain ToolActivityCards directly (no
    /// SubagentCard involved) -- the fix lives entirely in
    /// ToolActivityCard's own static ExpandedByToolUseId dictionary, so the
    /// nested case is exercised by the exact same code path; a card built
    /// standalone and a card built by MessageBlockFactory for a nested
    /// block are otherwise identical `new ToolActivityCard(record)` calls.
    ///
    /// Uses ToolActivityCard's internal *ForTests seam (ToggleExpandedForTests
    /// / IsExpandedForTests / ResetExpandedStateForTests), visible via the
    /// existing InternalsVisibleTo("Colloid.AgentPanel.Editor.Tests")
    /// (Editor/AssemblyInfo.cs) -- same idiom SubagentCardExpandStateTests
    /// already uses for the outer card.
    /// </summary>
    public class ToolActivityCardExpandStateTests
    {
        [SetUp]
        public void SetUp()
        {
            ToolActivityCard.ResetExpandedStateForTests();
        }

        [TearDown]
        public void TearDown()
        {
            ToolActivityCard.ResetExpandedStateForTests();
        }

        /// <summary>A completed tool call with a non-empty result summary so
        /// hasDetails is true (chevron + expandable details exist).</summary>
        private static ToolCallRecord MakeCompletedToolCall(string toolUseId)
        {
            return new ToolCallRecord
            {
                toolUseId = toolUseId,
                toolName = "Read",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"file_path\":\"Assets/Scripts/Player.cs\"}",
                resultSummary = "12 lines"
            };
        }

        [Test]
        public void FreshCard_StartsCollapsed()
        {
            ToolCallRecord record = MakeCompletedToolCall("toolu_fresh");
            var card = new ToolActivityCard(record);

            Assert.IsFalse(card.IsExpandedForTests);
        }

        [Test]
        public void RebuiltCard_ForSameToolUseId_RestoresExpandedState()
        {
            ToolCallRecord record = MakeCompletedToolCall("toolu_remember_me");

            var first = new ToolActivityCard(record);
            Assert.IsFalse(first.IsExpandedForTests, "sanity: starts collapsed");

            first.ToggleExpandedForTests();
            Assert.IsTrue(first.IsExpandedForTests, "sanity: toggle expands");

            // Simulates MessageListController (for a standalone card) or
            // SubagentCard.PopulateDetails (for a nested one) rebuilding the
            // element for the SAME tool call -- a fresh instance is built
            // from scratch, exactly like production does on any structural
            // change.
            var rebuilt = new ToolActivityCard(record);

            Assert.IsTrue(rebuilt.IsExpandedForTests,
                "a rebuilt card for the same toolUseId must restore the expanded state instead "
                    + "of silently re-collapsing");
        }

        [Test]
        public void ToggleExpandedTwice_ReturnsToCollapsed_AndRebuiltCardStaysCollapsed()
        {
            ToolCallRecord record = MakeCompletedToolCall("toolu_toggle_twice");

            var first = new ToolActivityCard(record);
            first.ToggleExpandedForTests();
            first.ToggleExpandedForTests();
            Assert.IsFalse(first.IsExpandedForTests, "sanity: two toggles collapse again");

            var rebuilt = new ToolActivityCard(record);
            Assert.IsFalse(rebuilt.IsExpandedForTests,
                "a card left collapsed must not spuriously restore as expanded");
        }

        [Test]
        public void DifferentToolUseId_StartsCollapsed_EvenAfterAnotherCardWasExpanded()
        {
            ToolCallRecord recordA = MakeCompletedToolCall("toolu_a");
            var cardA = new ToolActivityCard(recordA);
            cardA.ToggleExpandedForTests();
            Assert.IsTrue(cardA.IsExpandedForTests, "sanity: card A is expanded");

            ToolCallRecord recordB = MakeCompletedToolCall("toolu_b");
            var cardB = new ToolActivityCard(recordB);

            Assert.IsFalse(cardB.IsExpandedForTests,
                "expand-state memory is keyed by toolUseId; an unrelated tool call must still "
                    + "start collapsed");
        }

        [Test]
        public void MissingToolUseId_DoesNotThrow_AndNeverRestoresExpanded()
        {
            ToolCallRecord record = MakeCompletedToolCall(string.Empty);
            record.toolUseId = string.Empty;

            var first = new ToolActivityCard(record);
            Assert.DoesNotThrow(() => first.ToggleExpandedForTests(),
                "toggling with an empty toolUseId must not throw (nothing to key the memory on)");

            var second = new ToolActivityCard(record);
            Assert.IsFalse(second.IsExpandedForTests,
                "with no toolUseId to key on, state can never be remembered across instances");
        }

        [Test]
        public void NullRecord_DoesNotThrow_AndHasNoDetails()
        {
            var card = new ToolActivityCard(null);

            Assert.DoesNotThrow(() => card.ToggleExpandedForTests());
            Assert.IsFalse(card.IsExpandedForTests);
        }

        [Test]
        public void CardWithNoDetails_ToggleIsANoOp()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_no_details",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded
                // no inputJson, no resultSummary -- hasDetails is false.
            };
            var card = new ToolActivityCard(record);

            card.ToggleExpandedForTests();

            Assert.IsFalse(card.IsExpandedForTests,
                "a card with nothing to expand must ignore toggle attempts entirely");
        }

        // -----------------------------------------------------------------
        // Redundant summary label skip (design note 2026-08-14-ui-polish-
        // audit.md contract item 4, "belt and suspenders"): when the
        // describer's summary has nothing more to say than the card's own
        // header name, ToolActivityCard must not render a second Label
        // that just repeats it. Structural: build the real card and count
        // .uap-toolcard-summary labels in the header, rather than calling
        // ToolCardDescriber directly -- the skip decision is made in
        // ToolActivityCard's constructor, comparing its OWN displayText
        // against ToolCardDescriber.Describe's Summary.
        // -----------------------------------------------------------------

        private static int CountSummaryLabels(ToolActivityCard card)
        {
            return card.Query<Label>(className: "uap-toolcard-summary").ToList().Count;
        }

        [Test]
        public void SummaryEqualsDisplayName_SkipsTheSummaryLabel()
        {
            // ToolCardDescriber's own Bash branch falls back to echoing the
            // tool name verbatim as the summary when the input carries no
            // "command" key -- exactly the redundant case the card's
            // comparison (OrdinalIgnoreCase, defensive against a future
            // describer/display-name change introducing a case difference
            // between two otherwise-identical strings) must catch.
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_redundant_summary",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded,
                inputJson = "{}",
                resultSummary = "ok"
            };
            var card = new ToolActivityCard(record);

            Assert.AreEqual(0, CountSummaryLabels(card),
                "a summary identical to the header name must not render a second, "
                    + "redundant label");
        }

        [Test]
        public void McpToolWithNoUsefulKey_SkipsTheSummaryLabel_AndKeepsASpacer()
        {
            // Live-observed bug: header showed "uap_scripts_compile" and,
            // next to it, "mcp__unity-ops__uap_scripts_compile" as the
            // summary -- the raw wire name rendered twice.
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_mcp_echo_summary",
                toolName = "mcp__unity-ops__uap_scripts_compile",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"paths\":[]}",
                resultSummary = "ok"
            };
            var card = new ToolActivityCard(record);

            Assert.AreEqual(0, CountSummaryLabels(card));
            Assert.AreEqual(1, card.Query<VisualElement>(className: "uap-toolcard-spacer").ToList().Count,
                "without a summary label the header needs a flex-grow spacer so the name "
                    + "does not float to the middle of the row");
        }

        [Test]
        public void SkillTool_RendersTheSkillNameAsSummary()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_skill_summary",
                toolName = "Skill",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"skill\":\"run\"}",
                resultSummary = "ok"
            };
            var card = new ToolActivityCard(record);

            Assert.AreEqual(1, CountSummaryLabels(card));
            Assert.AreEqual("run", card.Q<Label>(className: "uap-toolcard-summary").text);
            Assert.AreEqual(0, card.Query<VisualElement>(className: "uap-toolcard-spacer").ToList().Count);
        }

        [Test]
        public void SummaryDiffersFromDisplayName_StillRendersTheSummaryLabel()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_distinct_summary",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"command\":\"echo hi\"}",
                resultSummary = "ok"
            };
            var card = new ToolActivityCard(record);

            Assert.AreEqual(1, CountSummaryLabels(card),
                "a genuinely informative summary must still render alongside the header name");
        }
    }
}
