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
    }
}
