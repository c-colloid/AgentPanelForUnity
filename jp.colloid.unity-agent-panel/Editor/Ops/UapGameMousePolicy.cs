using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What uap_game_mouse is asked to do.</summary>
    public enum UapGameMouseAction
    {
        Click,
        LongPress,
        Move,
        MoveDelta,
        Scroll
    }

    /// <summary>
    /// The argument rules for uap_game_mouse that do not need a Unity
    /// device or Editor state, with no UnityEngine in sight (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 2.2). Button,
    /// hold-duration and coordinate rules are NOT re-derived here: this
    /// tool reuses UapGamePointerPolicy.ParseButton/TryToScreenSpace and
    /// UapGameInputPolicy.CheckPlayState/ValidateHold directly, since it
    /// drives the same running game through the same Editor as
    /// uap_game_pointer and uap_game_key. Only the pieces unique to a raw
    /// Mouse.current injection -- the action name, the coordinate-to-
    /// device-delta flip and the move_delta smoothing schedule -- live
    /// here.
    /// </summary>
    public static class UapGameMousePolicy
    {
        /// <summary>Pure: maps the wire action names to the enum; the default is click. Throws ArgumentException on anything else.</summary>
        public static UapGameMouseAction ParseAction(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_");
            switch (normalized)
            {
                case "":
                case "click":
                    return UapGameMouseAction.Click;
                case "long_press":
                case "longpress":
                    return UapGameMouseAction.LongPress;
                case "move":
                    return UapGameMouseAction.Move;
                case "move_delta":
                case "movedelta":
                    return UapGameMouseAction.MoveDelta;
                case "scroll":
                    return UapGameMouseAction.Scroll;
                default:
                    throw new ArgumentException("Unknown action '" + value
                        + "'. Valid actions: click, long_press, move, move_delta, scroll.");
            }
        }

        /// <summary>
        /// Pure: every coordinate this tool takes (like uap_game_pointer's)
        /// is top-left-origin with y growing DOWN, the way a screenshot
        /// reads. A MouseState's "delta" and the device's "position" are
        /// both bottom-left-origin with y growing UP, so injecting a
        /// relative move needs the same flip TryToScreenSpace applies to an
        /// absolute one. dx is unchanged (x does not flip in either
        /// convention).
        /// </summary>
        public static void ToDeviceDelta(double dx, double dy, out double deviceDx, out double deviceDy)
        {
            deviceDx = dx;
            deviceDy = -dy;
        }

        /// <summary>Pure: true when dx/dy are usable (finite numbers), else false with a message naming both parameters.</summary>
        public static bool ValidateDelta(double dx, double dy, string parameterNameX, string parameterNameY, out string error)
        {
            if (double.IsNaN(dx) || double.IsInfinity(dx) || double.IsNaN(dy) || double.IsInfinity(dy))
            {
                error = "'" + parameterNameX + "'/'" + parameterNameY + "' must be finite numbers (got "
                    + dx + ", " + dy + ").";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Pure: how far a move_delta spread over 'duration' seconds of
        /// real Editor ticks has progressed, 0 to 1. Zero or negative
        /// duration means instant: 1 at once (mirrors
        /// UapGamePointerPolicy.DragProgress's zero-speed case, but against
        /// a fixed duration rather than a distance/speed ratio, since a
        /// delta injection has no "distance" of its own to divide by).
        /// </summary>
        public static double DurationProgress(double elapsedSeconds, double durationSeconds)
        {
            if (durationSeconds <= 0)
            {
                return 1.0;
            }
            double progress = elapsedSeconds / durationSeconds;
            if (progress < 0)
            {
                return 0;
            }
            return progress > 1.0 ? 1.0 : progress;
        }

        /// <summary>Pure: true when max_distance is a usable dry_run raycast distance (positive, +Infinity allowed for "unbounded"), else false with a message.</summary>
        public static bool ValidateMaxDistance(double maxDistance, out string error)
        {
            if (double.IsNaN(maxDistance) || maxDistance <= 0)
            {
                error = "'max_distance' must be a positive number of world units, or omitted for unbounded (got "
                    + maxDistance + ").";
                return false;
            }
            error = null;
            return true;
        }
    }
}
