using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Keeps a vertical ScrollView's content from reflowing on every frame
    /// of a window resize (docs/design-notes/2026-09-16-resize-reflow-
    /// throttle.md).
    ///
    /// Why: UI Toolkit lays out and re-generates text for every wrapped
    /// label whose available width changed, and a resize drag changes
    /// that width on every frame. The transcript renders up to 300
    /// messages (markdown, tool cards, code blocks) so each frame of the
    /// drag re-measured thousands of glyph runs and the panel's repaint
    /// dominated the editor frame. Nothing in that per-frame work is
    /// visible to the user beyond the final layout.
    ///
    /// How: on the first viewport width change the content container is
    /// pinned to the width it was JUST laid out at (an inline
    /// `style.width`), so later viewport changes leave the pinned subtree
    /// untouched -- Yoga skips a subtree whose own constraints did not
    /// change. A scheduled one-shot re-armed by every further change
    /// releases the pin once the viewport has been quiet for
    /// <see cref="SettleMillis"/>; the content then reflows ONCE to the
    /// final width. Cost per drag: two reflows instead of one per frame.
    /// The horizontal scroller is hidden while pinned so a content
    /// container momentarily wider than a shrinking viewport does not
    /// flash a scrollbar (which would also change the viewport height and
    /// feed back into the same cascade); the previous visibility is
    /// restored on release.
    ///
    /// The height of the viewport is deliberately not part of the
    /// decision: labels wrap on width only, so a pure height change is
    /// already cheap and pinning would only delay the stick-to-bottom
    /// clamp.
    /// </summary>
    internal sealed class ScrollReflowThrottle
    {
        /// <summary>
        /// Quiet time after the last viewport width change before the
        /// content reflows. A resize drag delivers a GeometryChangedEvent
        /// every editor frame (~16-33 ms), so this is comfortably longer
        /// than one frame and still short enough that the settled layout
        /// appears as soon as the mouse stops.
        /// </summary>
        internal const long SettleMillis = 120;

        private readonly ScrollView _scroll;
        private IVisualElementScheduledItem _settle;
        private bool _frozen;
        private ScrollerVisibility _savedHorizontal;

        /// <summary>
        /// Attaches a throttle to the given ScrollView for its lifetime.
        /// Callers need not keep the result: the callback and the
        /// scheduled item are owned by the ScrollView's elements and die
        /// with them, so there is nothing to tear down.
        /// </summary>
        internal static ScrollReflowThrottle Attach(ScrollView scroll)
        {
            return new ScrollReflowThrottle(scroll);
        }

        private ScrollReflowThrottle(ScrollView scroll)
        {
            if (scroll == null)
            {
                throw new ArgumentNullException("scroll");
            }
            _scroll = scroll;
            _scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(OnViewportGeometryChanged);
        }

        /// <summary>Whether the content width is currently pinned.</summary>
        internal bool IsFrozen
        {
            get { return _frozen; }
        }

        /// <summary>
        /// Pure decision: does a viewport width change from
        /// <paramref name="oldWidth"/> to <paramref name="newWidth"/>
        /// warrant pinning the content? Only a change between two REAL
        /// measurements counts: the first layout (old is 0 or NaN), a
        /// collapsed host (new is 0) and a height-only change (equal
        /// widths) must not pin, or the first paint of every window would
        /// wait out the settle delay for nothing.
        /// </summary>
        internal static bool ShouldFreeze(float oldWidth, float newWidth)
        {
            if (float.IsNaN(oldWidth) || float.IsNaN(newWidth))
            {
                return false;
            }
            if (oldWidth <= 0f || newWidth <= 0f)
            {
                return false;
            }
            return Mathf.Abs(newWidth - oldWidth) > 0.01f;
        }

        private void OnViewportGeometryChanged(GeometryChangedEvent evt)
        {
            if (!ShouldFreeze(evt.oldRect.width, evt.newRect.width))
            {
                return;
            }
            if (!_frozen)
            {
                Freeze();
            }
            if (_settle == null)
            {
                _settle = _scroll.schedule.Execute(Release);
            }
            // ExecuteLater on an existing item cancels its pending run and
            // re-schedules it, so every further change during the drag
            // pushes the reflow back until the viewport is quiet.
            _settle.ExecuteLater(SettleMillis);
        }

        private void Freeze()
        {
            VisualElement content = _scroll.contentContainer;
            float width = content.layout.width;
            if (float.IsNaN(width) || width <= 0f)
            {
                return;
            }
            _frozen = true;
            _savedHorizontal = _scroll.horizontalScrollerVisibility;
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            // The width the children were laid out at in the pass that
            // raised this event: pinning to it re-runs Yoga on the subtree
            // with identical constraints, which its measurement cache
            // answers without touching a single text element.
            content.style.width = width;
        }

        private void Release()
        {
            if (!_frozen)
            {
                return;
            }
            _frozen = false;
            VisualElement content = _scroll.contentContainer;
            content.style.width = StyleKeyword.Null;
            _scroll.horizontalScrollerVisibility = _savedHorizontal;
        }
    }
}
