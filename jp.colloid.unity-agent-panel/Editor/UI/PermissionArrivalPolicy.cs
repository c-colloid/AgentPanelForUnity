namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// UXA-7: what the PANEL WINDOW does when a can_use_tool request
    /// arrives (edge-triggered in AgentPanelWindow.RefreshIfDirty). The old
    /// behavior was SetActiveView(Chat) whenever any other view was active
    /// -- which yanked the user out of a half-edited Settings field or a
    /// History rename mid-keystroke. The reason was real (ChatView's
    /// refresh loop, and with it the inline-card/floating-window hosting
    /// decision, is paused while Chat is not the active view, so the
    /// request would sit invisible behind the title badge), but the remedy
    /// was disproportionate: the floating PermissionWindow is
    /// view-independent and self-driving (its own AgentHub.Changed hook +
    /// refresh loop), so opening IT keeps the request reachable without
    /// touching what the user is doing. Forcing a view switch is
    /// deliberately not a possible outcome any more.
    /// </summary>
    internal static class PermissionArrivalPolicy
    {
        internal enum Arrival
        {
            /// <summary>The request is already reachable (Chat active -- ChatView's own hybrid hosting decides inline vs window -- or a window already open); the title badge is the only extra signal.</summary>
            BadgeOnly,
            /// <summary>Open the self-driving floating window (auto-opened: keyboard Y/N stays unarmed per PermissionWindow's guard) and leave the active view alone.</summary>
            OpenFloatingWindow
        }

        internal static Arrival Decide(PanelViewKind activeView, bool windowAlreadyOpen)
        {
            if (windowAlreadyOpen || activeView == PanelViewKind.Chat)
            {
                return Arrival.BadgeOnly;
            }
            return Arrival.OpenFloatingWindow;
        }
    }
}
