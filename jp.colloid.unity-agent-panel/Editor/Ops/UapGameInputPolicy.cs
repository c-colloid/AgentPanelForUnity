namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The rules the two Play Mode input tools (uap_game_pointer and
    /// uap_game_key) share, with no UnityEditor in sight (design note
    /// docs/design-notes/2026-09-22-game-input-tools.md section 2). Split
    /// out for the same reason UapPlayModePolicy is: a rule worth getting
    /// right should run in the license-free smoke gate as well as under a
    /// Unity Editor, and the rest of each tool is reflection into packages
    /// the Editor may not even have installed, which cannot.
    /// </summary>
    public static class UapGameInputPolicy
    {
        /// <summary>
        /// The longest a key or pointer may be held in one call. The
        /// dispatcher gives a tool call 15 s before its HTTP caller gives
        /// up (UapMainThreadDispatcher.DefaultTimeoutMillis) and an
        /// abandoned pollable is never ticked again -- which for these
        /// tools would mean a key that is never released. Ten seconds keeps
        /// the release inside the budget with room for the polls around
        /// it; a longer hold is two calls (down, then up).
        /// </summary>
        public const double MaxHoldSeconds = 10.0;

        /// <summary>
        /// The fewest dispatcher ticks a press stays applied before the
        /// release is queued, whatever the requested duration. Input
        /// System events queued in one frame are processed at the next
        /// input update; releasing in the same tick would collapse the
        /// press and the release into one update and no
        /// wasPressedThisFrame edge would ever be visible to the game.
        /// </summary>
        public const int MinHoldPolls = 2;

        /// <summary>
        /// Pure: true when input may be injected in this Editor state, else
        /// false with a message that names the tool that gets the Editor
        /// into the right state. <paramref name="allowPaused"/> is for the
        /// recovery actions (release everything) that must work while the
        /// game is paused; ordinary input is refused there because the game
        /// would not see it until resumed, which reads as a tool that did
        /// nothing.
        /// </summary>
        public static bool CheckPlayState(UapPlayModeSnapshot state, bool allowPaused, out string error)
        {
            if (state.IsCompiling)
            {
                error = "Scripts are compiling; the game cannot receive input until that finishes."
                    + " Wait for uap_ping to answer, then retry.";
                return false;
            }
            if (!state.IsPlaying)
            {
                error = "The Editor is not in Play Mode, so there is no running game to receive input."
                    + " Start it with uap_play_mode action:start, wait for uap_ping to answer, then retry.";
                return false;
            }
            if (state.IsPaused && !allowPaused)
            {
                error = "Play Mode is paused, so the game would not process this input until it resumes."
                    + " Resume with uap_play_mode action:resume first.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Pure: true when <paramref name="seconds"/> is a usable hold
        /// duration (0 to <see cref="MaxHoldSeconds"/>), else false with a
        /// message that names the range and the two-call alternative.
        /// NaN and infinity are refused as out of range rather than
        /// silently clamped.
        /// </summary>
        public static bool ValidateHold(double seconds, string parameterName, out string error)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            {
                error = "'" + parameterName + "' must be a number of seconds from 0 to " + MaxHoldSeconds
                    + " (got " + seconds + ").";
                return false;
            }
            if (seconds > MaxHoldSeconds)
            {
                error = "'" + parameterName + "' must be at most " + MaxHoldSeconds + " seconds per call (got "
                    + seconds + "). The unit is seconds, not milliseconds; for a longer hold use a 'down'"
                    + " call followed later by an 'up' call.";
                return false;
            }
            error = null;
            return true;
        }
    }
}
