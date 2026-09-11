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
