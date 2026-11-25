namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Optional companion to <see cref="IUapPollableTool"/> for a tool whose
    /// in-flight call holds something that must be let go of when nobody is
    /// waiting for the call any more (design note docs/design-notes/
    /// 2026-09-22-uloop-phase1.md section 3).
    ///
    /// When a pollable call outlives its HTTP wait, UapMainThreadDispatcher
    /// marks it cancelled and never polls it again (SEC-7), which is right
    /// for a compile but wrong for an input tool mid-hold: uap_game_key /
    /// uap_game_mouse would leave a key or button pressed in the running
    /// game for the rest of Play Mode, and uap_game_pointer would leave a
    /// press or drag open. The throttled wait (8 s while the Editor is
    /// unfocused in Play Mode) is shorter than the longest legal hold, so
    /// this is reachable with valid arguments.
    ///
    /// Called at most once per abandoned call, on the main thread, with the
    /// same opaque state the tool's own Poll last wrote. Must not throw
    /// (the dispatcher swallows it anyway) and must release only what THIS
    /// call took -- a key held by an earlier, completed 'down' call stays
    /// held.
    /// </summary>
    public interface IUapAbandonableTool
    {
        /// <summary>Releases whatever the abandoned call identified by <paramref name="pollState"/> still holds.</summary>
        void OnAbandoned(object pollState);
    }
}
