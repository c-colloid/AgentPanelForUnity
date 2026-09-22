namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The argument rules for uap_game_view_size, with no UnityEditor in
    /// sight (design note docs/design-notes/2026-09-21-console-clear-and-
    /// game-view-size.md section 3). Split out for the same reason
    /// UapPlayModePolicy is: a rule worth getting right should run in the
    /// license-free smoke gate as well as under a Unity Editor -- and the
    /// rest of that tool is reflection into editor internals, which cannot.
    /// </summary>
    public static class UapGameViewSizePolicy
    {
        /// <summary>
        /// Smallest accepted edge. Below this the Game View has no usable
        /// content area, and Unity's own custom-size dialog refuses single
        /// digits too.
        /// </summary>
        public const int MinDimension = 16;

        /// <summary>
        /// Largest accepted edge: the maximum render-texture dimension on
        /// the platforms this package targets. Asking for more produces a
        /// Game View that cannot render rather than an error, so it is
        /// refused here instead.
        /// </summary>
        public const int MaxDimension = 16384;

        /// <summary>
        /// Pure: true when width/height are usable, else false with a
        /// message naming the accepted range. A missing dimension (0) is
        /// reported as missing rather than as out of range, because the two
        /// mistakes have different fixes.
        /// </summary>
        public static bool ValidateDimensions(int width, int height, out string error)
        {
            if (width <= 0 || height <= 0)
            {
                error = "Both 'width' and 'height' are required for action 'set' (pixels, e.g. 1920 x 1080).";
                return false;
            }
            if (width < MinDimension || height < MinDimension)
            {
                error = "Game View size must be at least " + MinDimension + " px on each edge (got "
                    + width + " x " + height + ").";
                return false;
            }
            if (width > MaxDimension || height > MaxDimension)
            {
                error = "Game View size must be at most " + MaxDimension + " px on each edge (got "
                    + width + " x " + height + ").";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Pure: the label this package gives a custom size it adds, so a
        /// person reading the Game View dropdown can tell where the entry
        /// came from, and so a later call reuses that entry instead of
        /// adding a second one.
        /// </summary>
        public static string CustomSizeLabel(int width, int height)
        {
            return "Agent Panel " + width + "x" + height;
        }
    }
}
