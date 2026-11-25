using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Virtualized transcript (docs/design-notes/2026-09-30-transcript-
    /// virtualization.md): only the rows near the viewport are live, two
    /// spacers stand in for the rest, and the window pass builds rows as
    /// they approach the viewport and releases rows that moved far away.
    /// Every element of the tree is re-styled and re-measured when the
    /// docked tab is re-shown, so the live row count must not grow with
    /// the conversation. The decisions are pure seams on
    /// MessageListController (InternalsVisibleTo, same idiom as
    /// MessageListControllerScrollTests).
    /// </summary>
    public class MessageListControllerWindowTests
    {
        [Test]
        public void Margins_ReleaseIsFartherThanLoad_SoARowIsNeverBuiltAndReleasedByTheSameScroll()
        {
            Assert.Greater(MessageListController.ReleaseMarginViewports,
                MessageListController.LoadMarginViewports);
            Assert.Greater(MessageListController.InitialRows, MessageListController.LoadChunk);
        }

        // -- ComputeWindowStart -----------------------------------------------------

        [Test]
        public void WindowStart_NoAnchor_IsTheNewestRows()
        {
            Assert.AreEqual(0, MessageListController.ComputeWindowStart(10, -1, 24));
            Assert.AreEqual(76, MessageListController.ComputeWindowStart(100, -1, 24));
        }

        [Test]
        public void WindowStart_WithAnchor_StartsAFewRowsAboveIt()
        {
            // 24 rows: 6 above the anchor so the viewport can scroll up a
            // little before the first head insert.
            Assert.AreEqual(44, MessageListController.ComputeWindowStart(1000, 50, 24));
        }

        [Test]
        public void WindowStart_AnchorNearTheEnds_StaysInsideTheTranscript()
        {
            Assert.AreEqual(0, MessageListController.ComputeWindowStart(1000, 2, 24));
            // Anchor near the tail: the window must still hold `rows` rows.
            Assert.AreEqual(976, MessageListController.ComputeWindowStart(1000, 998, 24));
            Assert.AreEqual(0, MessageListController.ComputeWindowStart(10, 8, 24));
        }

        // -- Release / load decisions ---------------------------------------------------

        [Test]
        public void ReleaseAbove_OnlyBeyondTheReleaseMargin()
        {
            // Visible top at 1000, margin 500: a row ending at 400 is out,
            // a row ending at 600 stays.
            Assert.IsTrue(MessageListController.ShouldReleaseAbove(400f, 1000f, 500f));
            Assert.IsFalse(MessageListController.ShouldReleaseAbove(600f, 1000f, 500f));
        }

        [Test]
        public void ReleaseBelow_OnlyBeyondTheReleaseMargin()
        {
            Assert.IsTrue(MessageListController.ShouldReleaseBelow(2600f, 2000f, 500f));
            Assert.IsFalse(MessageListController.ShouldReleaseBelow(2400f, 2000f, 500f));
        }

        [Test]
        public void LoadAbove_WhenTheFirstRowIsWithinTheLoadMargin()
        {
            // Visible top at 1000, margin 300: a first row starting at 800
            // leaves the margin uncovered (load); at 600 it is covered.
            Assert.IsTrue(MessageListController.ShouldLoadAbove(800f, 1000f, 300f));
            Assert.IsFalse(MessageListController.ShouldLoadAbove(600f, 1000f, 300f));
        }

        [Test]
        public void LoadBelow_WhenTheLastRowIsWithinTheLoadMargin()
        {
            Assert.IsTrue(MessageListController.ShouldLoadBelow(2200f, 2000f, 300f));
            Assert.IsFalse(MessageListController.ShouldLoadBelow(2400f, 2000f, 300f));
        }

        [Test]
        public void LoadAndRelease_AreDisjoint_ForTheSameRow()
        {
            // With the load margin smaller than the release margin, no
            // edge position both loads and releases.
            float load = 300f;
            float release = 750f;
            for (float edge = 0f; edge <= 3000f; edge += 50f)
            {
                Assert.IsFalse(MessageListController.ShouldLoadAbove(edge, 1500f, load)
                    && MessageListController.ShouldReleaseAbove(edge, 1500f, release), "edge " + edge);
                Assert.IsFalse(MessageListController.ShouldLoadBelow(edge, 1500f, load)
                    && MessageListController.ShouldReleaseBelow(edge, 1500f, release), "edge " + edge);
            }
        }

        // -- ComputeAnchoredScrollValue -------------------------------------------------

        [Test]
        public void AnchoredScrollValue_ShiftsByTheHeightDelta()
        {
            // Rows built above replaced 300px of estimate with 380px of
            // real height: the same content stays in view when the offset
            // grows by exactly the 80px difference.
            Assert.AreEqual(280f, MessageListController.ComputeAnchoredScrollValue(200f, 1000f, 1080f));
        }

        [Test]
        public void AnchoredScrollValue_NeverNegative()
        {
            Assert.AreEqual(0f, MessageListController.ComputeAnchoredScrollValue(10f, 1000f, 900f));
        }
    }
}
