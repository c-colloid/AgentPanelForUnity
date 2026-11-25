using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What uap_game_pointer is asked to do.</summary>
    public enum UapGamePointerAction
    {
        Click,
        LongPress,
        Drag
    }

    /// <summary>Which mouse button the pointer event carries.</summary>
    public enum UapGamePointerButton
    {
        Left,
        Right,
        Middle
    }

    /// <summary>
    /// The argument rules for uap_game_pointer, with no UnityEngine in
    /// sight (design note docs/design-notes/2026-09-22-game-input-tools.md
    /// section 3). The coordinate conversion in particular is the kind of
    /// thing that silently clicks the wrong widget when it is off by a
    /// flip, so it is pure and pinned in the license-free smoke gate.
    /// </summary>
    public static class UapGamePointerPolicy
    {
        /// <summary>How long a long_press holds when no duration is given.</summary>
        public const double DefaultLongPressSeconds = 0.5;

        /// <summary>
        /// Drag speed when none is given: fast enough that a drag across a
        /// 1080p screen takes about a second, slow enough that a game's
        /// drag threshold and per-frame drag handlers see several events.
        /// </summary>
        public const double DefaultDragPixelsPerSecond = 2000.0;

        /// <summary>Pure: maps the wire action names to the enum; the default is click. Throws ArgumentException on anything else.</summary>
        public static UapGamePointerAction ParseAction(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_");
            switch (normalized)
            {
                case "":
                case "click":
                    return UapGamePointerAction.Click;
                case "long_press":
                case "longpress":
                    return UapGamePointerAction.LongPress;
                case "drag":
                    return UapGamePointerAction.Drag;
                default:
                    throw new ArgumentException("Unknown action '" + value + "'. Valid actions: click, long_press, drag.");
            }
        }

        /// <summary>Pure: maps the wire button names to the enum; the default is left. Throws ArgumentException on anything else.</summary>
        public static UapGamePointerButton ParseButton(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "":
                case "left":
                    return UapGamePointerButton.Left;
                case "right":
                    return UapGamePointerButton.Right;
                case "middle":
                    return UapGamePointerButton.Middle;
                default:
                    throw new ArgumentException("Unknown button '" + value + "'. Valid buttons: left, right, middle.");
            }
        }

        /// <summary>
        /// Pure: converts a point given the way a screenshot is read --
        /// origin at the TOP-left, y growing downwards, in the Game View's
        /// render resolution (or 0..1 fractions of it when
        /// <paramref name="normalized"/>) -- into the BOTTOM-left-origin
        /// screen space every uGUI raycast and pointer event uses. Refuses
        /// a point outside the view instead of letting a raycast at it
        /// report "nothing there", because "outside the view" and "empty
        /// spot" need different fixes.
        /// </summary>
        public static bool TryToScreenSpace(double x, double y, double viewWidth, double viewHeight, bool normalized,
            out double screenX, out double screenY, out string error)
        {
            screenX = 0;
            screenY = 0;
            if (viewWidth <= 0 || viewHeight <= 0)
            {
                error = "The Game View has no size (is one open? Window > General > Game).";
                return false;
            }
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                error = "'x' and 'y' must be finite numbers.";
                return false;
            }
            double px = x;
            double py = y;
            if (normalized)
            {
                if (x < 0 || x > 1 || y < 0 || y > 1)
                {
                    error = "With normalized:true, 'x' and 'y' are fractions of the view from 0 to 1 (got "
                        + x + ", " + y + ").";
                    return false;
                }
                px = x * viewWidth;
                py = y * viewHeight;
            }
            if (px < 0 || px > viewWidth || py < 0 || py > viewHeight)
            {
                error = "Point (" + px + ", " + py + ") is outside the Game View, which is " + viewWidth + " x "
                    + viewHeight + " px (origin top-left). Take the coordinates from a screenshot of that size,"
                    + " or pass normalized:true with 0..1 fractions.";
                return false;
            }
            screenX = px;
            screenY = viewHeight - py;
            error = null;
            return true;
        }

        /// <summary>
        /// Pure: how far along a drag is after <paramref name="elapsedSeconds"/>
        /// at <paramref name="pixelsPerSecond"/>, from 0 to 1. A speed of
        /// zero (or a zero-length drag) means instant: 1 at once.
        /// </summary>
        public static double DragProgress(double elapsedSeconds, double distancePixels, double pixelsPerSecond)
        {
            if (pixelsPerSecond <= 0 || distancePixels <= 0)
            {
                return 1.0;
            }
            double progress = elapsedSeconds * pixelsPerSecond / distancePixels;
            if (progress < 0)
            {
                return 0;
            }
            return progress > 1.0 ? 1.0 : progress;
        }

        /// <summary>Pure: true when the drag speed is usable (0 = instant, otherwise positive pixels per second).</summary>
        public static bool ValidateSpeed(double pixelsPerSecond, out string error)
        {
            if (double.IsNaN(pixelsPerSecond) || double.IsInfinity(pixelsPerSecond) || pixelsPerSecond < 0)
            {
                error = "'speed' must be pixels per second, 0 for an instant drag (got " + pixelsPerSecond + ").";
                return false;
            }
            error = null;
            return true;
        }
    }
}
