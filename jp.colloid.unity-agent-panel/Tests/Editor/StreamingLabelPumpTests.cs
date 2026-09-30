using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pins the Suspend/Resume lifecycle added for UICODE-2: a view switch
    /// must PAUSE the typewriter (entries kept, update loop unhooked), not
    /// destroy it -- Clear() on deactivate froze in-progress streaming
    /// labels at the switch-away text because text growth alone never
    /// re-Tracks a label.
    /// </summary>
    [TestFixture]
    public class StreamingLabelPumpTests
    {
        private StreamingLabelPump _pump;

        [SetUp]
        public void SetUp()
        {
            _pump = new StreamingLabelPump();
        }

        [TearDown]
        public void TearDown()
        {
            // Always unhook from EditorApplication.update.
            _pump.Clear();
        }

        private static ChatMessageBlock StreamingBlock(string text)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.Text,
                text = text,
                streaming = true
            };
        }

        [Test]
        public void Track_AddsEntry_AndStartsDriving()
        {
            _pump.Track(StreamingBlock("hello"), new Label());

            Assert.AreEqual(1, _pump.TrackedEntryCountForTests);
            Assert.IsTrue(_pump.IsDrivingForTests);
        }

        [Test]
        public void Suspend_KeepsEntries_ButStopsDriving()
        {
            _pump.Track(StreamingBlock("hello"), new Label());

            _pump.Suspend();

            Assert.AreEqual(1, _pump.TrackedEntryCountForTests,
                "Suspend must keep the entries -- that is the whole difference from Clear");
            Assert.IsFalse(_pump.IsDrivingForTests);
        }

        [Test]
        public void Resume_AfterSuspend_DrivesTheSameEntries()
        {
            _pump.Track(StreamingBlock("hello"), new Label());
            _pump.Suspend();

            _pump.Resume();

            Assert.AreEqual(1, _pump.TrackedEntryCountForTests);
            Assert.IsTrue(_pump.IsDrivingForTests);
        }

        [Test]
        public void Resume_WithNoEntries_StaysUnhooked()
        {
            _pump.Resume();

            Assert.AreEqual(0, _pump.TrackedEntryCountForTests);
            Assert.IsFalse(_pump.IsDrivingForTests);
        }

        [Test]
        public void Clear_DropsEntries_AndStopsDriving()
        {
            _pump.Track(StreamingBlock("hello"), new Label());

            _pump.Clear();

            Assert.AreEqual(0, _pump.TrackedEntryCountForTests);
            Assert.IsFalse(_pump.IsDrivingForTests);
        }

        // -- Long text (65535-vertex ceiling) --------------------------------

        private static string Repeat(string unit, int count)
        {
            var sb = new System.Text.StringBuilder(unit.Length * count);
            for (int i = 0; i < count; i++)
            {
                sb.Append(unit);
            }
            return sb.ToString();
        }

        private static void AssertNoLabelOverCap(VisualElement parent)
        {
            foreach (Label l in parent.Query<Label>().ToList())
            {
                Assert.LessOrEqual((l.text ?? string.Empty).Length, LongTextChunker.DefaultMaxChars,
                    "one label must stay under the per-element vertex ceiling");
            }
        }

        private static string Joined(VisualElement parent)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Label l in parent.Query<Label>().ToList())
            {
                sb.Append(l.text);
            }
            return sb.ToString();
        }

        [Test]
        public void Track_LongText_SpreadsAcrossContinuationLabels_LosingNothing()
        {
            string text = Repeat("0123456789abcdef\n", 3000); // 51,000 chars
            var parent = new VisualElement();
            var label = new Label();
            label.AddToClassList("uap-thinking-text");
            parent.Add(label);

            _pump.Track(StreamingBlock(text), label);

            Assert.Greater(_pump.LabelCountForTests(label), 1);
            Assert.AreEqual(parent.childCount, _pump.LabelCountForTests(label));
            AssertNoLabelOverCap(parent);
            Assert.AreEqual(text + IconLoader.GlyphCursor, Joined(parent),
                "the chunks concatenate back to the shown text, cursor last");
            Label cont = (Label)parent[1];
            Assert.IsTrue(cont.ClassListContains("uap-thinking-text"),
                "a continuation label wears the tracked label's classes");
            Assert.IsTrue(cont.ClassListContains(StreamingLabelPump.ContinuationClass));
            Assert.IsFalse(cont.enableRichText);
        }

        [Test]
        public void Flush_GrowingText_AddsLabels_AndShrinkingText_RemovesThem()
        {
            var parent = new VisualElement();
            var label = new Label();
            parent.Add(label);
            ChatMessageBlock block = StreamingBlock("short");
            _pump.Track(block, label);
            Assert.AreEqual(1, parent.childCount);

            block.text = Repeat("word ", 5000); // 25,000 chars
            // The typewriter advances a third of the remainder per flush;
            // a handful of flushes catches up.
            for (int i = 0; i < 40; i++)
            {
                _pump.FlushNowForTests();
            }
            Assert.Greater(parent.childCount, 1, "text past the cap needs continuation labels");
            AssertNoLabelOverCap(parent);
            Assert.AreEqual(block.text + IconLoader.GlyphCursor, Joined(parent));

            block.text = "short again";
            _pump.FlushNowForTests();
            Assert.AreEqual(1, parent.childCount, "a snapped-back target drops the labels it no longer needs");
            Assert.AreEqual("short again" + IconLoader.GlyphCursor, label.text);
        }

        [Test]
        public void Flush_FinalizedLongText_StaysChunked()
        {
            string text = Repeat("line of thought\n", 2000); // 32,000 chars
            var parent = new VisualElement();
            var label = new Label();
            parent.Add(label);
            ChatMessageBlock block = StreamingBlock(text);
            _pump.Track(block, label);

            block.streaming = false;
            _pump.FlushNowForTests();

            Assert.AreEqual(0, _pump.TrackedEntryCountForTests);
            AssertNoLabelOverCap(parent);
            Assert.AreEqual(text, Joined(parent), "the final flush shows the whole text without the cursor");
        }

        [Test]
        public void Track_SameLabelAgain_ReplacesItsContinuationLabels()
        {
            var parent = new VisualElement();
            var label = new Label();
            parent.Add(label);
            _pump.Track(StreamingBlock(Repeat("a", 20000)), label);
            Assert.Greater(parent.childCount, 1);

            _pump.Track(StreamingBlock("tiny"), label);

            Assert.AreEqual(1, parent.childCount, "old continuation labels must not linger");
            Assert.AreEqual("tiny" + IconLoader.GlyphCursor, label.text);
        }

        [Test]
        public void Track_LongText_WithoutParent_KeepsTheWholeTextInTheLabel()
        {
            // No hierarchy to insert continuations into: the caller gets
            // the full text rather than a silently truncated one.
            string text = Repeat("x", 20000);
            var label = new Label();
            _pump.Track(StreamingBlock(text), label);
            Assert.AreEqual(text + IconLoader.GlyphCursor, label.text);
            Assert.AreEqual(1, _pump.LabelCountForTests(label));
        }

        [Test]
        public void Resume_AfterClear_HasNothingToDrive()
        {
            _pump.Track(StreamingBlock("hello"), new Label());
            _pump.Clear();

            _pump.Resume();

            Assert.IsFalse(_pump.IsDrivingForTests,
                "Clear drops the entries, so a later Resume must be a no-op");
        }
    }
}
