using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// One uGUI element gathered by <see cref="UapUguiElementCatalog"/>,
    /// BEFORE labeling/sorting/coordinate conversion (docs/design-notes/
    /// 2026-09-22-uloop-phase1.md section 2.1). Deliberately plain data
    /// (no UnityEngine types) so the ordering and coordinate math that
    /// <see cref="UapUiAnnotationPolicy"/> does with it stay pinned in the
    /// license-free smoke gate -- exactly the split UapGamePointerPolicy
    /// already uses for its own coordinate flip.
    ///
    /// ScreenCentre/CornerScreen are in the SAME bottom-left-origin "screen"
    /// space uap_game_pointer's internal pointer math uses
    /// (RectTransformUtility.WorldToScreenPoint's convention) -- NOT yet
    /// flipped to the top-left pixel convention the tool result reports.
    /// </summary>
    public struct UapUiElementCandidate
    {
        /// <summary>The governing Canvas' sortingOrder (0 when none could be read).</summary>
        public int CanvasSortingOrder;

        /// <summary>Sibling index at each level from the scene root down to this element, for a stable document-order tie-break.</summary>
        public int[] SiblingPath;

        public string HierarchyPath;
        public string ComponentType;
        public bool Interactable;

        /// <summary>UnityEngine.UI.Text.text / TMPro.TMP_Text.text found on the element or a child, or null.</summary>
        public string Text;

        public double ScreenCentreX;
        public double ScreenCentreY;

        /// <summary>Four RectTransform world corners run through the same screen-space conversion (length 4; a non-RectTransform element repeats its single point).</summary>
        public double[] CornerScreenX;
        public double[] CornerScreenY;

        public bool SupportsClick;
        public bool SupportsDrag;
        public bool SupportsDrop;
    }

    /// <summary>One drawable mark for <see cref="UapScreenCapture.DrawUiAnnotationMarks"/>: a top-left-origin pixel rect plus the same label the JSON element carries.</summary>
    public struct UapUiAnnotationMark
    {
        public string Label;
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    /// <summary>
    /// Unity-free half of uap_editor_screenshot's annotate_ui (docs/design-
    /// notes/2026-09-22-uloop-phase1.md section 2.1): spreadsheet-style
    /// labeling, sort order, the bottom-left-to-top-left coordinate flip
    /// (the same one UapGamePointerPolicy.TryToScreenSpace does in
    /// reverse), the result JSON shape, and the tiny 5x7 bitmap font used
    /// to burn labels into the screenshot. Kept separate from
    /// UapUguiElementCatalog (which does the Unity/reflection-side
    /// discovery) so this file compiles verbatim in the license-free smoke
    /// gate.
    /// </summary>
    public static class UapUiAnnotationPolicy
    {
        /// <summary>Default cap on how many elements a single call reports (the JSON list is meant to fit a chat turn, not enumerate a huge UI).</summary>
        public const int DefaultElementCap = 60;

        public const int GlyphWidth = 5;
        public const int GlyphHeight = 7;

        /// <summary>
        /// Pure: the wire label for the Nth (0-based) element, spreadsheet-
        /// column style -- A, B, ... Z, AA, AB, ... -- so the default cap of
        /// 60 never runs out of single-token labels an agent can quote back
        /// as uap_game_pointer's target/label.
        /// </summary>
        public static string LabelForIndex(int index)
        {
            if (index < 0)
            {
                throw new ArgumentException("'index' must not be negative.", "index");
            }
            var chars = new List<char>();
            int n = index;
            do
            {
                chars.Insert(0, (char)('A' + (n % 26)));
                n = n / 26 - 1;
            }
            while (n >= 0);
            return new string(chars.ToArray());
        }

        /// <summary>
        /// Pure: front-most first -- higher canvas sortingOrder sorts
        /// before lower (a raycast picks the TOPMOST overlapping element,
        /// so that is the one most likely to be the agent's actual target),
        /// then ascending hierarchy (document) order as a stable tie-break
        /// within the same canvas.
        /// </summary>
        public static int CompareOrder(int sortingOrderA, int[] siblingPathA, int sortingOrderB, int[] siblingPathB)
        {
            if (sortingOrderA != sortingOrderB)
            {
                return sortingOrderB.CompareTo(sortingOrderA);
            }
            return CompareSiblingPath(siblingPathA, siblingPathB);
        }

        private static int CompareSiblingPath(int[] a, int[] b)
        {
            if (a == null) { a = Array.Empty<int>(); }
            if (b == null) { b = Array.Empty<int>(); }
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i].CompareTo(b[i]);
                }
            }
            return a.Length.CompareTo(b.Length);
        }

        /// <summary>
        /// Pure: the same bottom-left-to-top-left flip
        /// UapGamePointerTool.ToTopLeft applies to a click point, run on a
        /// listed element's centre/corners instead. X is unchanged; only Y
        /// flips, since "screen" space here is already in the SAME pixel
        /// scale as viewHeight (Handles.GetMainGameViewSize -- see
        /// UapGamePointerTool.WidgetCentre, which feeds the same
        /// RectTransformUtility.WorldToScreenPoint result straight into
        /// pointer events with no separate rescale).
        /// </summary>
        public static void FlipToTopLeft(double screenX, double screenY, double viewHeight, out double pixelX, out double pixelY)
        {
            pixelX = screenX;
            pixelY = viewHeight - screenY;
        }

        /// <summary>Pure: a top-left pixel as a 0..1 fraction of the view (0 when the view has no size, matching UapGamePointerPolicy's hedges elsewhere).</summary>
        public static void PixelToNormalized(double pixelX, double pixelY, double viewWidth, double viewHeight, out double normalizedX, out double normalizedY)
        {
            normalizedX = viewWidth > 0 ? pixelX / viewWidth : 0.0;
            normalizedY = viewHeight > 0 ? pixelY / viewHeight : 0.0;
        }

        /// <summary>
        /// Pure: sorts, caps and labels <paramref name="candidates"/>, and
        /// builds both the JSON "elements" array (top-left render-pixel
        /// centre/rect, matching uap_game_pointer's x/y) and the parallel
        /// draw-mark list <paramref name="marks"/> (same pixel space, one
        /// per emitted element, same order/labels as the JSON array) that
        /// UapScreenCapture.DrawUiAnnotationMarks burns into the image.
        /// <paramref name="cap"/> &lt;= 0 falls back to
        /// <see cref="DefaultElementCap"/>.
        /// </summary>
        public static JsonNode BuildResult(List<UapUiElementCandidate> candidates, double viewWidth, double viewHeight,
            int cap, out bool truncated, out List<UapUiAnnotationMark> marks)
        {
            if (candidates == null)
            {
                candidates = new List<UapUiElementCandidate>();
            }
            if (cap <= 0)
            {
                cap = DefaultElementCap;
            }
            var sorted = new List<UapUiElementCandidate>(candidates);
            sorted.Sort(delegate (UapUiElementCandidate a, UapUiElementCandidate b)
            {
                return CompareOrder(a.CanvasSortingOrder, a.SiblingPath, b.CanvasSortingOrder, b.SiblingPath);
            });
            truncated = sorted.Count > cap;
            int emit = Math.Min(sorted.Count, cap);

            JsonNode elements = JsonNode.NewArray();
            marks = new List<UapUiAnnotationMark>(emit);
            for (int i = 0; i < emit; i++)
            {
                UapUiElementCandidate c = sorted[i];
                string label = LabelForIndex(i);

                double cx, cy;
                FlipToTopLeft(c.ScreenCentreX, c.ScreenCentreY, viewHeight, out cx, out cy);
                double nx, ny;
                PixelToNormalized(cx, cy, viewWidth, viewHeight, out nx, out ny);

                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                double[] cornerScreenX = c.CornerScreenX ?? Array.Empty<double>();
                double[] cornerScreenY = c.CornerScreenY ?? Array.Empty<double>();
                int cornerCount = Math.Min(cornerScreenX.Length, cornerScreenY.Length);
                for (int k = 0; k < cornerCount; k++)
                {
                    double px, py;
                    FlipToTopLeft(cornerScreenX[k], cornerScreenY[k], viewHeight, out px, out py);
                    if (px < minX) { minX = px; }
                    if (px > maxX) { maxX = px; }
                    if (py < minY) { minY = py; }
                    if (py > maxY) { maxY = py; }
                }
                if (cornerCount == 0)
                {
                    // No RectTransform corners (e.g. a plain-Transform
                    // IPointerClickHandler): the rect collapses to the
                    // centre point rather than an inverted/empty box.
                    minX = maxX = cx;
                    minY = maxY = cy;
                }

                JsonNode item = JsonNode.NewObject()
                    .Set("label", label)
                    .Set("path", c.HierarchyPath ?? string.Empty)
                    .Set("componentType", c.ComponentType ?? string.Empty)
                    .Set("interactable", c.Interactable)
                    .Set("x", Math.Round(cx, 1))
                    .Set("y", Math.Round(cy, 1))
                    .Set("normalizedX", Math.Round(nx, 4))
                    .Set("normalizedY", Math.Round(ny, 4))
                    .Set("rect", JsonNode.NewObject()
                        .Set("x", Math.Round(minX, 1))
                        .Set("y", Math.Round(minY, 1))
                        .Set("width", Math.Round(maxX - minX, 1))
                        .Set("height", Math.Round(maxY - minY, 1)))
                    .Set("supportsClick", c.SupportsClick)
                    .Set("supportsDrag", c.SupportsDrag)
                    .Set("supportsDrop", c.SupportsDrop);
                if (!string.IsNullOrEmpty(c.Text))
                {
                    item.Set("text", c.Text);
                }
                elements.Add(item);
                marks.Add(new UapUiAnnotationMark { Label = label, X = minX, Y = minY, Width = maxX - minX, Height = maxY - minY });
            }
            return elements;
        }

        /// <summary>
        /// Pure: rescales marks from the canonical Game View RENDER pixel
        /// space (Handles.GetMainGameViewSize, what BuildResult's JSON
        /// reports) into an actually-captured image's pixel dimensions --
        /// the Game View WINDOW capture is sized in points at the window's
        /// current on-screen size, which is not always the same as its
        /// configured render resolution.
        /// </summary>
        public static List<UapUiAnnotationMark> ScaleMarks(List<UapUiAnnotationMark> marks,
            double fromWidth, double fromHeight, double toWidth, double toHeight)
        {
            var result = new List<UapUiAnnotationMark>(marks != null ? marks.Count : 0);
            if (marks == null)
            {
                return result;
            }
            double sx = fromWidth > 0 ? toWidth / fromWidth : 1.0;
            double sy = fromHeight > 0 ? toHeight / fromHeight : 1.0;
            for (int i = 0; i < marks.Count; i++)
            {
                UapUiAnnotationMark m = marks[i];
                result.Add(new UapUiAnnotationMark { Label = m.Label, X = m.X * sx, Y = m.Y * sy, Width = m.Width * sx, Height = m.Height * sy });
            }
            return result;
        }

        // -- Tiny embedded 5x7 bitmap font (A-Z only -- labels never contain digits) ----

        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
            ['C'] = new[] { ".####", "#....", "#....", "#....", "#....", "#....", ".####" },
            ['D'] = new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
            ['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
            ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
            ['G'] = new[] { ".####", "#....", "#....", "#..##", "#...#", "#...#", ".####" },
            ['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['I'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####" },
            ['J'] = new[] { "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.." },
            ['K'] = new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" },
            ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
            ['M'] = new[] { "#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#", "#...#" },
            ['N'] = new[] { "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#" },
            ['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
            ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
            ['Q'] = new[] { ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" },
            ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
            ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
            ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
            ['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
            ['V'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." },
            ['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" },
            ['X'] = new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" },
            ['Y'] = new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." },
            ['Z'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
        };

        /// <summary>The 5-wide x 7-tall '#'/'.' glyph for an A-Z letter (case-insensitive); false for anything else (labels never need it).</summary>
        public static bool TryGetGlyph(char c, out string[] rows)
        {
            return Glyphs.TryGetValue(char.ToUpperInvariant(c), out rows);
        }
    }
}
