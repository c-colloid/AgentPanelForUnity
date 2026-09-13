using System.Collections.Generic;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Splits model-controlled text into pieces small enough for one UI
    /// Toolkit text element each (design note
    /// 2026-09-13-toolcard-vertex-limit.md).
    ///
    /// Why: UITK's text renderer allocates 4 vertices per glyph and a
    /// single VisualElement may not allocate more than 65535 vertices, so
    /// a Label/TextField holding more than ~16,000 characters throws
    /// "A VisualElement must not allocate more than 65535 vertices" from
    /// inside the repaint and draws NOTHING -- a Write tool card whose
    /// input JSON carries a whole file came up blank. Every unbounded
    /// text sink (tool card sections, code blocks, attachment payloads)
    /// therefore goes through this splitter and stacks one element per
    /// chunk.
    ///
    /// Unity-API-free so the boundary rules are unit-testable
    /// (LongTextChunkerTests).
    /// </summary>
    public static class LongTextChunker
    {
        /// <summary>
        /// Characters per element. 65535 / 4 = 16383 glyphs is the hard
        /// ceiling; half of it leaves room for fallback-font glyphs,
        /// underline/strike quads and the generator's own padding, and
        /// keeps each element's layout cheap.
        /// </summary>
        public const int DefaultMaxChars = 8000;

        /// <summary>
        /// How far back from the cap the splitter looks for a newline (then
        /// a space) before hard-cutting: a preferred break at most this
        /// many characters before the cap keeps chunks nearly full.
        /// </summary>
        private const int BreakSearchWindow = 400;

        /// <summary>
        /// Splits <paramref name="text"/> into chunks of at most
        /// <paramref name="maxChars"/> UTF-16 code units. Break points are
        /// chosen, in order of preference, at a newline (the '\n' stays at
        /// the END of the earlier chunk so the visual line structure is
        /// unchanged), at a space, or as a hard cut that never lands
        /// between the halves of a surrogate pair. Null/empty input yields
        /// one empty chunk so callers always have something to render.
        /// </summary>
        public static List<string> Split(string text, int maxChars = DefaultMaxChars)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                chunks.Add(string.Empty);
                return chunks;
            }
            if (maxChars < 2)
            {
                maxChars = 2;
            }

            int start = 0;
            while (text.Length - start > maxChars)
            {
                int cut = FindBreak(text, start, maxChars);
                chunks.Add(text.Substring(start, cut - start));
                start = cut;
            }
            chunks.Add(text.Substring(start));
            return chunks;
        }

        /// <summary>True when a single element could not hold the text.</summary>
        public static bool NeedsSplit(string text, int maxChars = DefaultMaxChars)
        {
            return text != null && text.Length > maxChars;
        }

        /// <summary>
        /// Index of the first character of the NEXT chunk, in
        /// (start, start + maxChars].
        /// </summary>
        private static int FindBreak(string text, int start, int maxChars)
        {
            int limit = start + maxChars; // exclusive end of this chunk
            int floor = System.Math.Max(start + 1, limit - BreakSearchWindow);

            // Newline: cut AFTER it so the '\n' terminates the earlier chunk.
            int nl = text.LastIndexOf('\n', limit - 1, limit - floor);
            if (nl >= floor)
            {
                return nl + 1;
            }
            int sp = text.LastIndexOf(' ', limit - 1, limit - floor);
            if (sp >= floor)
            {
                return sp + 1;
            }
            // Hard cut; step back one if it would split a surrogate pair.
            if (char.IsHighSurrogate(text[limit - 1]) && limit - 1 > start)
            {
                return limit - 1;
            }
            return limit;
        }
    }
}
