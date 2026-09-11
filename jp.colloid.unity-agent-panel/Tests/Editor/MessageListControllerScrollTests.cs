using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Review defect fix (docs/design-notes/2026-08-03-subagent-ux-and-
    /// midturn-input.md section 1.1): Refresh's swap loop
    /// (MessageListController.cs, around the RemoveAt+Insert pair) rebuilds
    /// a message row's element in place whenever its structural signature
    /// changes, but used to never save or restore the outer message-list
    /// scroll offset around that swap -- a subagent card updating every
    /// couple of seconds yanked the list out from under a user who was
    /// reading it.
    ///
    /// The fix captures whether the list was "sticking" to the bottom and
    /// its current scroller value BEFORE the swap, then re-applies a
    /// decision AFTER every mutation in that Refresh call has already
    /// landed. That decision is pure (no VisualElement, no live
    /// ScrollView/EditorWindow needed to exercise it) and is exposed as
    /// MessageListController.ComputeRestoredScrollValue, an internal seam
    /// visible via the existing InternalsVisibleTo("Colloid.AgentPanel.
    /// Editor.Tests") (Editor/AssemblyInfo.cs) -- the same idiom as
    /// ComputeSignatureForTests in MessageListControllerSignatureTests.
    ///
    /// This suite pins the two cases the design note calls out explicitly:
    /// a user pinned at the bottom must stay pinned, and a user who had
    /// scrolled up must stay at the same absolute offset rather than being
    /// carried to wherever the swap happens to leave the scroller.
    /// </summary>
    public class MessageListControllerScrollTests
    {
        [Test]
        public void Sticking_ReturnsNewHighValue_RegardlessOfSavedOffset()
        {
            // A user pinned at the bottom before the swap must land back at
            // the bottom afterward, even if the pre-swap offset (captured
            // before content height could have changed) does not match the
            // post-swap extent at all.
            float result = MessageListController.ComputeRestoredScrollValue(
                wasSticking: true, savedOffset: 120f, newHighValue: 480f);

            Assert.AreEqual(480f, result,
                "a user who was following the bottom must be pinned to the current highValue, "
                    + "not left at their pre-swap offset");
        }

        [Test]
        public void Sticking_WithShrunkenHighValue_StillReturnsNewHighValue()
        {
            // The content got shorter (e.g. a card that had a large nested
            // ScrollView collapsed back down) -- stick-to-bottom must still
            // land exactly at the new, smaller extent.
            float result = MessageListController.ComputeRestoredScrollValue(
                wasSticking: true, savedOffset: 500f, newHighValue: 50f);

            Assert.AreEqual(50f, result);
        }

        [Test]
        public void NotSticking_WithinRange_PreservesExactAbsoluteOffset()
        {
            // A user who had scrolled up must stay at the exact same pixel
            // offset -- the card they were reading should not move under
            // them just because some other row's card updated.
            float result = MessageListController.ComputeRestoredScrollValue(
                wasSticking: false, savedOffset: 240f, newHighValue: 900f);

            Assert.AreEqual(240f, result,
                "a scrolled-up user's absolute offset must be preserved when it is still "
                    + "within the post-swap range");
        }

        [Test]
        public void NotSticking_OffsetExceedsShrunkenHighValue_ClampsDownToHighValue()
        {
            // If the content actually got shorter than the saved offset
            // (e.g. rows above were pruned or collapsed), there is nowhere
            // further to preserve -- clamp to the new maximum instead of
            // leaving an out-of-range value on the scroller.
            float result = MessageListController.ComputeRestoredScrollValue(
                wasSticking: false, savedOffset: 700f, newHighValue: 300f);

            Assert.AreEqual(300f, result,
                "an offset beyond the new extent must clamp down to the new highValue");
        }

        [Test]
        public void NotSticking_ZeroOffset_StaysAtTop()
        {
            float result = MessageListController.ComputeRestoredScrollValue(
                wasSticking: false, savedOffset: 0f, newHighValue: 900f);

            Assert.AreEqual(0f, result);
        }

        [Test]
        public void NotSticking_NegativeHighValue_NeverReturnsNegative()
        {
            // Defensive: an unscrollable/degenerate list (highValue <= 0)
            // must never hand the Scroller a negative value regardless of
            // what was saved before the swap.
            float result = MessageListController.ComputeRestoredScrollValue(
                wasSticking: false, savedOffset: 50f, newHighValue: 0f);

            Assert.AreEqual(0f, result);
            Assert.GreaterOrEqual(
                MessageListController.ComputeRestoredScrollValue(false, 50f, -10f), 0f,
                "must clamp to zero rather than propagate a negative highValue");
        }

        // ------------------------------------------------------------------
        // UICODE-3: the prune anchor holds within max + slack (appends past
        // the cap stay incremental) and re-anchors only when the slack is
        // exhausted (the caller then prunes head rows in O(drop)).
        // ------------------------------------------------------------------

        private const int Max = 300;
        private const int Slack = 50;

        [Test]
        public void PruneAnchor_NothingRenderedYet_AnchorsToNewestMax()
        {
            Assert.AreEqual(0, MessageListController.ComputePruneAnchor(120, -1, Max, Slack));
            Assert.AreEqual(100, MessageListController.ComputePruneAnchor(400, -1, Max, Slack));
        }

        [Test]
        public void PruneAnchor_WithinSlack_HoldsTheCurrentAnchor()
        {
            // 300 rendered from 0, message 301 arrives: the old anchor
            // math moved to 1 and full-rebuilt all 300 rows; the slack
            // keeps the anchor (incremental append of row 301).
            Assert.AreEqual(0, MessageListController.ComputePruneAnchor(301, 0, Max, Slack));
            // Right at the edge: max + slack rendered is still held.
            Assert.AreEqual(0, MessageListController.ComputePruneAnchor(Max + Slack, 0, Max, Slack));
            Assert.AreEqual(100, MessageListController.ComputePruneAnchor(
                100 + Max + Slack, 100, Max, Slack));
        }

        [Test]
        public void PruneAnchor_SlackExhausted_ReanchorsToNewestMax()
        {
            Assert.AreEqual(51, MessageListController.ComputePruneAnchor(
                Max + Slack + 1, 0, Max, Slack));
            Assert.AreEqual(151, MessageListController.ComputePruneAnchor(
                100 + Max + Slack + 1, 100, Max, Slack));
        }

        [Test]
        public void PruneAnchor_TranscriptShrankBelowTheAnchor_ReanchorsCleanly()
        {
            // Session switch/clear: the old anchor is beyond the new
            // count; the anchor must land back inside the transcript, and
            // never leave a negative window.
            Assert.AreEqual(0, MessageListController.ComputePruneAnchor(10, 200, Max, Slack));
            Assert.AreEqual(0, MessageListController.ComputePruneAnchor(0, 200, Max, Slack));
        }

        [Test]
        public void SourceScan_FullRebuild_RestoresScrollIntent()
        {
            string text = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                "Packages/jp.colloid.unity-agent-panel/Editor/UI/MessageListController.cs"));
            int start = text.IndexOf("private void FullRebuild", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            int end = text.IndexOf("\n        private ", start + 1, System.StringComparison.Ordinal);
            string body = end > start ? text.Substring(start, end - start) : text.Substring(start);
            StringAssert.Contains("wasSticking", body,
                "FullRebuild must capture scroll intent like the incremental path");
            StringAssert.Contains("ComputeRestoredScrollValue", body,
                "FullRebuild must restore through the shared pure decision");
        }
    }
}
