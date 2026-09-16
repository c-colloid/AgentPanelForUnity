using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// docs/design-notes/2026-09-16-resize-reflow-throttle.md: the pin
    /// decision is a pure function of the two viewport widths, exposed as
    /// ScrollReflowThrottle.ShouldFreeze (internal seam, same idiom as
    /// MessageListController.ComputeRestoredScrollValue). Pins the cases
    /// that must NOT freeze -- the first layout, a collapsed host and a
    /// height-only change -- since freezing there would make every window
    /// wait out the settle delay before its first real paint.
    /// </summary>
    public class ScrollReflowThrottleTests
    {
        [Test]
        public void ShouldFreeze_WidthChangedBetweenRealMeasurements()
        {
            Assert.IsTrue(ScrollReflowThrottle.ShouldFreeze(400f, 380f));
            Assert.IsTrue(ScrollReflowThrottle.ShouldFreeze(380f, 400f));
            Assert.IsTrue(ScrollReflowThrottle.ShouldFreeze(400f, 401f));
        }

        [Test]
        public void ShouldFreeze_FirstLayout_DoesNotFreeze()
        {
            // The first GeometryChangedEvent reports 0 (or NaN before any
            // layout) as the old width.
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(0f, 400f));
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(float.NaN, 400f));
        }

        [Test]
        public void ShouldFreeze_CollapsedHost_DoesNotFreeze()
        {
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(400f, 0f));
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(400f, -1f));
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(400f, float.NaN));
        }

        [Test]
        public void ShouldFreeze_HeightOnlyChange_DoesNotFreeze()
        {
            // Equal widths: the event came from a height change, which
            // never re-wraps a label.
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(400f, 400f));
            Assert.IsFalse(ScrollReflowThrottle.ShouldFreeze(400f, 400.001f));
        }

        [Test]
        public void SettleDelay_IsLongerThanOneEditorFrame()
        {
            // A drag raises one event per frame; the settle window has to
            // outlast a frame or the pin would release mid-drag and reflow
            // anyway.
            Assert.Greater(ScrollReflowThrottle.SettleMillis, 33);
        }

        [Test]
        public void Attach_DoesNotFreezeUntilAGeometryChangeArrives()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            ScrollReflowThrottle throttle = ScrollReflowThrottle.Attach(scroll);
            Assert.IsFalse(throttle.IsFrozen);
            // No inline width was written: an unset inline style reads as 0.
            Assert.AreEqual(0f, scroll.contentContainer.style.width.value.value);
        }
    }
}
