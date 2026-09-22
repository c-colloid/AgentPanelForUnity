using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What the caller asked uap_play_mode to do.</summary>
    public enum UapPlayModeAction
    {
        Status = 0,
        Start = 1,
        Stop = 2,
        Pause = 3,
        Resume = 4,
        Step = 5,
    }

    /// <summary>The Editor's play state, snapshotted on the main thread so the decision rule stays pure.</summary>
    public struct UapPlayModeSnapshot
    {
        public bool IsPlaying;
        public bool IsPaused;

        /// <summary>EditorApplication.isPlayingOrWillChangePlaymode while IsPlaying is still false: a transition is under way.</summary>
        public bool WillChangePlayMode;

        public bool IsCompiling;
    }

    /// <summary>What <see cref="UapPlayModePolicy.Decide"/> concluded; the tool maps each to one Editor call (or none).</summary>
    public enum UapPlayModeOutcome
    {
        /// <summary>Nothing to do -- report the state (already playing, already stopped, already paused).</summary>
        Reported = 0,
        Enter = 1,
        Exit = 2,
        Pause = 3,
        Resume = 4,
        Step = 5,

        /// <summary>The request cannot be honoured; the message says why and what to do instead.</summary>
        Refuse = 6,
    }

    /// <summary>
    /// Every rule uap_play_mode decides by, with no UnityEditor in sight
    /// (design note docs/design-notes/2026-09-21-play-mode-and-console-log-
    /// tools.md section 2): which requests are refused, which are no-ops
    /// that still answer, and what the agent must do next.
    ///
    /// Split from <see cref="UapPlayModeTool"/> for the same reason
    /// UapConsoleLogStore is split from its buffer -- a rule that decides
    /// whether to enter Play Mode is worth running in the license-free
    /// smoke gate (ci/SmokeTests) and not only under a Unity Editor.
    /// </summary>
    public static class UapPlayModePolicy
    {
        /// <summary>
        /// Pure: the whole decision rule. Never decides to act while a
        /// compile is in flight or a transition is already under way --
        /// Unity would either swallow the request or enter Play Mode on
        /// assemblies about to be replaced, and both look to the agent like
        /// a tool that silently did nothing.
        /// </summary>
        public static UapPlayModeOutcome Decide(UapPlayModeAction action, UapPlayModeSnapshot state,
            out string message)
        {
            if (action != UapPlayModeAction.Status && state.IsCompiling)
            {
                message = "Scripts are compiling; Play Mode changes are refused until that finishes."
                    + " Wait for uap_ping to answer, then try again.";
                return UapPlayModeOutcome.Refuse;
            }
            if (action != UapPlayModeAction.Status && state.WillChangePlayMode && !state.IsPlaying)
            {
                message = "The Editor is already switching into Play Mode. Wait for uap_ping to answer,"
                    + " then call uap_play_mode action:status.";
                return UapPlayModeOutcome.Refuse;
            }

            switch (action)
            {
                case UapPlayModeAction.Status:
                    message = "Play state: " + DescribeState(state) + ".";
                    return UapPlayModeOutcome.Reported;

                case UapPlayModeAction.Start:
                    if (state.IsPlaying)
                    {
                        message = "Already in Play Mode (" + DescribeState(state) + "); nothing to do.";
                        return UapPlayModeOutcome.Reported;
                    }
                    message = "Entering Play Mode.";
                    return UapPlayModeOutcome.Enter;

                case UapPlayModeAction.Stop:
                    if (!state.IsPlaying)
                    {
                        message = "Not in Play Mode; nothing to do.";
                        return UapPlayModeOutcome.Reported;
                    }
                    message = "Leaving Play Mode.";
                    return UapPlayModeOutcome.Exit;

                case UapPlayModeAction.Pause:
                    if (!state.IsPlaying)
                    {
                        message = "Nothing is running to pause. Start Play Mode first (uap_play_mode action:start).";
                        return UapPlayModeOutcome.Refuse;
                    }
                    if (state.IsPaused)
                    {
                        message = "Play Mode is already paused; nothing to do.";
                        return UapPlayModeOutcome.Reported;
                    }
                    message = "Paused Play Mode.";
                    return UapPlayModeOutcome.Pause;

                case UapPlayModeAction.Resume:
                    if (!state.IsPlaying)
                    {
                        message = "Nothing is running to resume. Start Play Mode first (uap_play_mode action:start).";
                        return UapPlayModeOutcome.Refuse;
                    }
                    if (!state.IsPaused)
                    {
                        message = "Play Mode is already running; nothing to do.";
                        return UapPlayModeOutcome.Reported;
                    }
                    message = "Resumed Play Mode.";
                    return UapPlayModeOutcome.Resume;

                case UapPlayModeAction.Step:
                    if (!state.IsPlaying)
                    {
                        message = "Nothing is running to step. Start Play Mode first (uap_play_mode action:start).";
                        return UapPlayModeOutcome.Refuse;
                    }
                    message = "Stepped one frame; Play Mode is paused.";
                    return UapPlayModeOutcome.Step;
            }

            message = "Unknown action.";
            return UapPlayModeOutcome.Refuse;
        }

        /// <summary>Pure: the wire name for a play state -- edit / entering / playing / paused.</summary>
        public static string DescribeState(UapPlayModeSnapshot state)
        {
            if (state.IsPlaying)
            {
                return state.IsPaused ? "paused" : "playing";
            }
            return state.WillChangePlayMode ? "entering" : "edit";
        }

        /// <summary>
        /// Pure: parses the 'action' argument. An absent action is "status"
        /// -- the harmless one; an unrecognized action is refused BY NAME
        /// rather than silently treated as status, which would report "edit"
        /// to an agent that believes it pressed Play.
        /// </summary>
        public static UapPlayModeAction ParseAction(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return UapPlayModeAction.Status;
            }
            string trimmed = value.Trim();
            if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase)) { return UapPlayModeAction.Status; }
            if (string.Equals(trimmed, "start", StringComparison.OrdinalIgnoreCase)) { return UapPlayModeAction.Start; }
            if (string.Equals(trimmed, "stop", StringComparison.OrdinalIgnoreCase)) { return UapPlayModeAction.Stop; }
            if (string.Equals(trimmed, "pause", StringComparison.OrdinalIgnoreCase)) { return UapPlayModeAction.Pause; }
            if (string.Equals(trimmed, "resume", StringComparison.OrdinalIgnoreCase)) { return UapPlayModeAction.Resume; }
            if (string.Equals(trimmed, "step", StringComparison.OrdinalIgnoreCase)) { return UapPlayModeAction.Step; }
            throw new ArgumentException("Unknown action '" + value
                + "'. Valid actions: status, start, stop, pause, resume, step.");
        }

        /// <summary>
        /// Pure: what to tell the agent to do next, or empty when the call
        /// changed nothing it has to wait for. Only the two transitions that
        /// reload the domain get a follow-up instruction.
        /// </summary>
        public static string NextStepHint(UapPlayModeOutcome outcome)
        {
            if (outcome == UapPlayModeOutcome.Enter || outcome == UapPlayModeOutcome.Exit)
            {
                return "The Editor applies this right after this reply and reloads the domain, so the next"
                    + " uap_* call can fail while that runs. Wait for uap_ping to answer, then call"
                    + " uap_play_mode action:status to confirm, and read uap_console_logs for what the"
                    + " transition logged (the reload empties that buffer first).";
            }
            return string.Empty;
        }
    }
}
