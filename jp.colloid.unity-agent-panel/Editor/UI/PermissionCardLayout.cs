namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Pure layout decisions for the hybrid permission card UX (compact
    /// inline card vs floating PermissionWindow). No Unity API on purpose:
    /// every rule here is exercised directly by EditMode tests, and the two
    /// hosts (ChatView inline / PermissionWindow) must share one source of
    /// truth for "is the panel too small".
    /// </summary>
    public static class PermissionCardLayout
    {
        /// <summary>
        /// Below this chat-content width the inline card cannot show its
        /// summary row + actions without wrapping into the composer.
        /// </summary>
        public const float MinInlinePanelWidth = 300f;

        /// <summary>
        /// Below this chat-content height the capped card + transcript
        /// minimum + composer no longer fit together.
        /// </summary>
        public const float MinInlinePanelHeight = 380f;

        /// <summary>Expanded card cap as a fraction of the chat content height.</summary>
        public const float CardHeightFraction = 0.4f;

        /// <summary>
        /// True when both measurements come from a real layout pass (not
        /// NaN / non-positive, which resolvedStyle yields before the first
        /// layout or right after a re-attach). Callers that make a ONE-SHOT
        /// decision (ChatView's inline-vs-window auto decision) must not
        /// consume that decision while this is false -- otherwise a request
        /// that arrives before the first layout pass would permanently lose
        /// its auto-opened window in exactly the tiny panels the hybrid
        /// design exists for.
        /// </summary>
        public static bool IsKnownGeometry(float panelContentWidth, float panelContentHeight)
        {
            return !float.IsNaN(panelContentWidth) && !float.IsNaN(panelContentHeight)
                && panelContentWidth > 0f && panelContentHeight > 0f;
        }

        /// <summary>
        /// Decides the host for a newly arrived permission request: true =
        /// open the floating PermissionWindow, false = keep the inline card.
        /// Unknown geometry (NaN / non-positive, i.e. the first layout pass
        /// has not run yet) stays inline -- the window can still be opened
        /// manually and the inline card is always reachable via scrolling.
        /// </summary>
        public static bool ShouldOpenWindow(float panelContentWidth, float panelContentHeight)
        {
            if (!IsKnownGeometry(panelContentWidth, panelContentHeight))
            {
                return false;
            }
            return panelContentHeight < MinInlinePanelHeight
                || panelContentWidth < MinInlinePanelWidth;
        }

        /// <summary>
        /// Max height (pixels) for the expanded inline card: 40 percent of
        /// the chat content height. Returns a negative value ("no cap") for
        /// unknown geometry. The usability floor is NOT applied here -- it
        /// lives in USS as min-height: var(--uap-perm-card-min), which
        /// outranks the inline max-height when the two conflict, so the cap
        /// can never crush the card below the token minimum.
        /// </summary>
        public static float ComputeMaxCardHeight(float panelContentHeight)
        {
            if (float.IsNaN(panelContentHeight) || panelContentHeight <= 0f)
            {
                return -1f;
            }
            return panelContentHeight * CardHeightFraction;
        }

        /// <summary>
        /// What ChatView does to an INLINE card when the chat root is
        /// resized (docs/design-notes/2026-09-16-inline-card-refit-on-
        /// shrink.md).
        /// </summary>
        public enum InlineFitAction
        {
            /// <summary>Leave the card as it is.</summary>
            None,
            /// <summary>Collapse the expanded card to its summary row.</summary>
            Collapse,
            /// <summary>Re-expand a card this policy collapsed earlier.</summary>
            Expand,
        }

        /// <summary>
        /// Pure decision: the inline-vs-window choice runs ONCE per request
        /// (ChatView, when the request arrives), so a card that was decided
        /// inline in a large panel used to keep its expanded body -- and
        /// the 96px / 220px usability floors -- after the panel was dragged
        /// below the inline minimum. The floors plus the transcript and
        /// composer minimums no longer fit, there is no outer scrollbar,
        /// and the composer and status bar were pushed out of the window.
        ///
        /// This reacts to the TRANSITION only, in both directions:
        /// - Entering "too small" (<see cref="ShouldOpenWindow"/> flips to
        ///   true) with the body expanded: collapse to the summary row, the
        ///   same answer "[Show here]" already gives in a panel that small.
        /// - Leaving "too small" with a card that THIS policy collapsed
        ///   (<paramref name="collapsedBySize"/>) and that is still
        ///   collapsed: expand it again, so a momentary shrink costs the
        ///   user nothing.
        /// Anything the user did in between is respected: a card they
        /// re-expanded by the chevron while small stays expanded on the
        /// next resize frame (no transition), and a card they collapsed
        /// themselves is not re-expanded on growth (not collapsedBySize).
        /// </summary>
        public static InlineFitAction ResolveInlineFit(
            bool wasTooSmall, bool tooSmall, bool expanded, bool collapsedBySize)
        {
            if (tooSmall == wasTooSmall)
            {
                return InlineFitAction.None;
            }
            if (tooSmall)
            {
                return expanded ? InlineFitAction.Collapse : InlineFitAction.None;
            }
            return !expanded && collapsedBySize ? InlineFitAction.Expand : InlineFitAction.None;
        }
    }
}
