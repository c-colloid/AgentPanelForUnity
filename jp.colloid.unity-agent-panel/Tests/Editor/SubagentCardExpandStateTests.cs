using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Review defect fix (docs/design-notes/2026-07-31-subagent-display.md
    /// section 7b, "expand-state memory"): MessageListController still
    /// rebuilds a subagent's row -- discarding the old SubagentCard instance
    /// and its _expanded field -- whenever a real structural change lands
    /// (status transition, a nested block appended, a drop-count bump). This
    /// suite pins SubagentCard's static ExpandedByToolUseId dictionary,
    /// which survives that rebuild by being keyed on SubagentRecord.
    /// toolUseId rather than living on the discarded instance.
    ///
    /// Cards are constructed with status "completed" (not "running") so the
    /// live-progress scheduler (SubagentCard.StartLiveUpdate) never engages
    /// -- these are plain EditMode VisualElement constructions with no
    /// panel, same as other UI logic tests in this suite (e.g.
    /// ContextUiRegressionTests), so nothing here depends on scheduler
    /// behavior while unattached.
    ///
    /// Uses SubagentCard's internal *ForTests seam (ToggleExpandedForTests /
    /// IsExpandedForTests / ResetExpandedStateForTests), visible via the
    /// existing InternalsVisibleTo("Colloid.AgentPanel.Editor.Tests")
    /// (Editor/AssemblyInfo.cs) -- the same idiom as AgentHub's *ForTests
    /// methods used throughout SubagentGroupingTests.
    /// </summary>
    public class SubagentCardExpandStateTests
    {
        private bool _originalSubagentDefaultExpanded;

        [SetUp]
        public void SetUp()
        {
            SubagentCard.ResetExpandedStateForTests();
            // Pin the ambient setting to its documented default (OFF) for
            // every test that does not explicitly flip it, and restore the
            // real value in TearDown -- this suite mutates
            // PanelStateStore.instance.Settings directly (never SaveNow(),
            // so nothing reaches disk) and must never leak state into any
            // other test or the sandbox project's real settings.
            _originalSubagentDefaultExpanded = PanelStateStore.instance.Settings.subagentDefaultExpanded;
            PanelStateStore.instance.Settings.subagentDefaultExpanded = false;
        }

        [TearDown]
        public void TearDown()
        {
            SubagentCard.ResetExpandedStateForTests();
            PanelStateStore.instance.Settings.subagentDefaultExpanded = _originalSubagentDefaultExpanded;
        }

        /// <summary>A completed subagent with a non-empty summary so
        /// hasDetails is true (chevron + expandable details exist) without
        /// ever touching the "running" live-update scheduler path.</summary>
        private static ToolCallRecord MakeCompletedSubagentToolCall(string toolUseId)
        {
            return new ToolCallRecord
            {
                toolUseId = toolUseId,
                toolName = "Agent",
                status = ToolCallStatus.Succeeded,
                subagent = new SubagentRecord
                {
                    toolUseId = toolUseId,
                    status = "completed",
                    subagentType = "general-purpose",
                    description = "desc",
                    summaryMarkdown = "done"
                }
            };
        }

        [Test]
        public void FreshCard_StartsCollapsed()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_fresh");
            var card = new SubagentCard(record);

            Assert.IsFalse(card.IsExpandedForTests);
        }

        [Test]
        public void RebuiltCard_ForSameToolUseId_RestoresExpandedState()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_remember_me");

            var first = new SubagentCard(record);
            Assert.IsFalse(first.IsExpandedForTests, "sanity: starts collapsed");

            first.ToggleExpandedForTests();
            Assert.IsTrue(first.IsExpandedForTests, "sanity: toggle expands");

            // Simulates MessageListController rebuilding the row for the
            // SAME subagent (e.g. a nested block just got appended, which
            // does change the signature) -- a fresh instance is built from
            // scratch, exactly like AppendRow/Refresh do.
            var rebuilt = new SubagentCard(record);

            Assert.IsTrue(rebuilt.IsExpandedForTests,
                "a rebuilt card for the same toolUseId must restore the expanded state instead "
                    + "of silently re-collapsing");
        }

        [Test]
        public void ToggleExpandedTwice_ReturnsToCollapsed_AndRebuiltCardStaysCollapsed()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_toggle_twice");

            var first = new SubagentCard(record);
            first.ToggleExpandedForTests();
            first.ToggleExpandedForTests();
            Assert.IsFalse(first.IsExpandedForTests, "sanity: two toggles collapse again");

            var rebuilt = new SubagentCard(record);
            Assert.IsFalse(rebuilt.IsExpandedForTests,
                "a card left collapsed must not spuriously restore as expanded");
        }

        [Test]
        public void DifferentToolUseId_StartsCollapsed_EvenAfterAnotherCardWasExpanded()
        {
            ToolCallRecord recordA = MakeCompletedSubagentToolCall("toolu_a");
            var cardA = new SubagentCard(recordA);
            cardA.ToggleExpandedForTests();
            Assert.IsTrue(cardA.IsExpandedForTests, "sanity: card A is expanded");

            ToolCallRecord recordB = MakeCompletedSubagentToolCall("toolu_b");
            var cardB = new SubagentCard(recordB);

            Assert.IsFalse(cardB.IsExpandedForTests,
                "expand-state memory is keyed by toolUseId; an unrelated subagent must still "
                    + "start collapsed");
        }

        [Test]
        public void MissingToolUseId_DoesNotThrow_AndNeverRestoresExpanded()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall(string.Empty);
            record.subagent.toolUseId = string.Empty;

            var first = new SubagentCard(record);
            Assert.DoesNotThrow(() => first.ToggleExpandedForTests(),
                "toggling with an empty toolUseId must not throw (nothing to key the memory on)");

            var second = new SubagentCard(record);
            Assert.IsFalse(second.IsExpandedForTests,
                "with no toolUseId to key on, state can never be remembered across instances");
        }

        // -- PanelSettings.subagentDefaultExpanded (docs/design-notes/
        // 2026-08-01-settings-enrichment.md #2) ----------------------------------------

        [Test]
        public void NoMemory_DefaultExpandedOn_StartsExpanded()
        {
            PanelStateStore.instance.Settings.subagentDefaultExpanded = true;
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_default_on");

            var card = new SubagentCard(record);

            Assert.IsTrue(card.IsExpandedForTests,
                "with no per-card memory yet, a fresh card must honor the Settings default");
        }

        [Test]
        public void NoMemory_DefaultExpandedOff_StartsCollapsed()
        {
            PanelStateStore.instance.Settings.subagentDefaultExpanded = false;
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_default_off");

            var card = new SubagentCard(record);

            Assert.IsFalse(card.IsExpandedForTests);
        }

        [Test]
        public void ExistingCollapsedMemory_OverridesDefaultExpandedOn()
        {
            // A card the user explicitly left collapsed (two toggles, same
            // as ToggleExpandedTwice_ReturnsToCollapsed above) while the
            // default was OFF.
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_memory_wins");
            var first = new SubagentCard(record);
            first.ToggleExpandedForTests();
            first.ToggleExpandedForTests();
            Assert.IsFalse(first.IsExpandedForTests, "sanity: two toggles collapse again");

            // The default flips ON afterward (a later Settings change) --
            // memory for THIS toolUseId must still take priority, per the
            // design note ("memory is always preferred over the default").
            PanelStateStore.instance.Settings.subagentDefaultExpanded = true;
            var rebuilt = new SubagentCard(record);

            Assert.IsFalse(rebuilt.IsExpandedForTests,
                "existing per-card memory must never be overridden by a later default change");
        }

        [Test]
        public void ExistingExpandedMemory_UnaffectedByDefaultExpandedOff()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_memory_expanded");
            var first = new SubagentCard(record);
            first.ToggleExpandedForTests();
            Assert.IsTrue(first.IsExpandedForTests, "sanity: one toggle expands");

            PanelStateStore.instance.Settings.subagentDefaultExpanded = false;
            var rebuilt = new SubagentCard(record);

            Assert.IsTrue(rebuilt.IsExpandedForTests,
                "existing expanded memory must survive even though the default is OFF");
        }

        // -- Expandability of restored (detail-less) records ---------------
        // Regression guards for the 2026-08-02 report "subagent cards can no
        // longer be expanded" (design note 2026-08-02-subagent-card-not-
        // expandable.md). A restored record legitimately arrives with NO
        // in-memory blocks and NO summary -- its nested transcript lives in
        // subagents/agent-<taskId>.jsonl and is lazy-loaded on first expand
        // -- so expandability must key off the toolUseId that makes that
        // lookup possible, not off content that is not there yet.

        private static ToolCallRecord MakeRestoredSubagentToolCall(string toolUseId)
        {
            return new ToolCallRecord
            {
                toolUseId = toolUseId,
                toolName = "Agent",
                status = ToolCallStatus.Succeeded,
                subagent = new SubagentRecord
                {
                    toolUseId = toolUseId,
                    status = "completed",
                    subagentType = "general-purpose",
                    description = "desc"
                    // no summaryMarkdown, no blocks: exactly what a restored
                    // record looked like in the field.
                }
            };
        }

        [Test]
        public void RestoredCard_WithoutSummaryOrBlocks_IsStillExpandable()
        {
            ToolCallRecord record = MakeRestoredSubagentToolCall("toolu_restored");
            var card = new SubagentCard(record);

            Assert.IsFalse(card.IsExpandedForTests, "sanity: starts collapsed");
            card.ToggleExpandedForTests();
            Assert.IsTrue(card.IsExpandedForTests,
                "a completed subagent with a toolUseId must stay expandable -- its nested "
                + "transcript is lazy-loaded from the sidechain on first expand");
        }

        [Test]
        public void SubagentWithoutToolUseId_AndNoContent_IsNotExpandable()
        {
            // The other direction: nothing in memory AND no sidechain key,
            // so there is nothing an expand could ever reveal. Offering the
            // affordance here would be a lie.
            var record = new ToolCallRecord
            {
                toolName = "Agent",
                status = ToolCallStatus.Succeeded,
                subagent = new SubagentRecord
                {
                    status = "completed",
                    subagentType = "general-purpose",
                    description = "desc"
                }
            };
            var card = new SubagentCard(record);

            card.ToggleExpandedForTests();
            Assert.IsFalse(card.IsExpandedForTests,
                "a record with no content and no sidechain key must not pretend to expand");
        }

        // -- Nested-scroll-offset memory (design note section 1: "the
        // nested ScrollView's offset" is the second of the two state-
        // survival gaps closed alongside expand state -- PopulateDetails
        // builds a brand new ScrollView every time it runs, so it always
        // starts at 0 without this fix). RecordNestedScrollOffsetForTests
        // drives the exact same code path a real user scroll would
        // (Scroller.valueChanged -> OnNestedScrollValueChanged) without
        // needing a live panel/layout pass to actually move a ScrollView --
        // these tests pin the KEYED PERSISTENCE half of the fix (record,
        // rebuild, still on record for the same toolUseId), the same half
        // the tests above pin for ExpandedByToolUseId. The APPLY half
        // (re-positioning a freshly built ScrollView once real layout
        // reports a non-zero highValue) is covered by
        // SubagentCard.ClampNestedScrollOffset's pure-function math plus
        // live verification -- EditMode cannot drive a real Yoga layout
        // pass, the same testing boundary MessageListControllerScrollTests
        // already draws around the outer list's equivalent restore path.

        [Test]
        public void NestedScroll_NothingRecordedYet_TryGetReturnsFalse()
        {
            float offset;
            bool found = SubagentCard.TryGetNestedScrollOffsetForTests("toolu_never_scrolled", out offset);

            Assert.IsFalse(found);
        }

        [Test]
        public void NestedScroll_RecordedOffset_IsRetrievableByToolUseId()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_scrolled");
            var card = new SubagentCard(record);

            card.RecordNestedScrollOffsetForTests(42f);

            float offset;
            bool found = SubagentCard.TryGetNestedScrollOffsetForTests("toolu_scrolled", out offset);
            Assert.IsTrue(found);
            Assert.AreEqual(42f, offset);
        }

        [Test]
        public void NestedScroll_RecordedOffset_SurvivesASimulatedRebuild()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall("toolu_scroll_rebuild");
            var first = new SubagentCard(record);
            first.RecordNestedScrollOffsetForTests(77f);

            // Simulates MessageListController discarding this instance and
            // building a fresh one for the SAME subagent -- exactly the
            // same "rebuild" the expand-state tests above simulate.
            var rebuilt = new SubagentCard(record);
            Assert.IsNotNull(rebuilt, "sanity: rebuild must not throw");

            float offset;
            bool found = SubagentCard.TryGetNestedScrollOffsetForTests("toolu_scroll_rebuild", out offset);
            Assert.IsTrue(found,
                "a scroll offset recorded before a rebuild must still be on record for the same "
                    + "toolUseId afterwards, the same way ExpandedByToolUseId survives above");
            Assert.AreEqual(77f, offset);
        }

        [Test]
        public void NestedScroll_DifferentToolUseId_DoesNotSeeAnotherCardsOffset()
        {
            ToolCallRecord recordA = MakeCompletedSubagentToolCall("toolu_scroll_a");
            var cardA = new SubagentCard(recordA);
            cardA.RecordNestedScrollOffsetForTests(10f);

            float offset;
            bool found = SubagentCard.TryGetNestedScrollOffsetForTests("toolu_scroll_b", out offset);
            Assert.IsFalse(found,
                "nested-scroll memory is keyed by toolUseId; an unrelated subagent must not see it");
        }

        [Test]
        public void NestedScroll_MissingToolUseId_RecordIsANoOp_AndDoesNotThrow()
        {
            ToolCallRecord record = MakeCompletedSubagentToolCall(string.Empty);
            record.subagent.toolUseId = string.Empty;
            var card = new SubagentCard(record);

            Assert.DoesNotThrow(() => card.RecordNestedScrollOffsetForTests(99f),
                "recording with an empty toolUseId must not throw (nothing to key the memory on)");

            float offset;
            bool found = SubagentCard.TryGetNestedScrollOffsetForTests(string.Empty, out offset);
            Assert.IsFalse(found,
                "an empty toolUseId must never be usable as a dictionary key");
        }

        // -- ClampNestedScrollOffset: pure clamp math behind the apply side ------

        [Test]
        public void ClampNestedScrollOffset_SavedOffsetWithinRange_ReturnsSavedOffset()
        {
            float result = SubagentCard.ClampNestedScrollOffset(savedOffset: 50f, highValue: 100f);

            Assert.AreEqual(50f, result);
        }

        [Test]
        public void ClampNestedScrollOffset_SavedOffsetExceedsHighValue_ClampsToHighValue()
        {
            float result = SubagentCard.ClampNestedScrollOffset(savedOffset: 150f, highValue: 100f);

            Assert.AreEqual(100f, result,
                "content got shorter since the offset was saved -- clamp to the new maximum "
                    + "instead of leaving an out-of-range value on the scroller");
        }

        [Test]
        public void ClampNestedScrollOffset_NegativeSavedOffset_ClampsToZero()
        {
            float result = SubagentCard.ClampNestedScrollOffset(savedOffset: -10f, highValue: 100f);

            Assert.AreEqual(0f, result);
        }

        [Test]
        public void ClampNestedScrollOffset_HighValueZero_ClampsToZero()
        {
            // The "layout not settled yet" state RestoreNestedScrollOffset's
            // GeometryChangedEvent guard already skips in production; the
            // pure function itself must still degrade safely if ever called
            // with it directly.
            float result = SubagentCard.ClampNestedScrollOffset(savedOffset: 50f, highValue: 0f);

            Assert.AreEqual(0f, result);
        }
    }
}
