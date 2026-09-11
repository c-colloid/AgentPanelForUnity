using Colloid.AgentPanel.UI;
using Colloid.AgentPanel.UI.Markdown;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Table column min-width estimation and inline-code wrap polish
    /// (live feedback: columns came out too narrow because the raw
    /// char-count ignored inline-code chip padding, and mark-styled code
    /// spans broke across lines untidily). All non-ASCII test data is
    /// constructed from codepoints; the source stays strict ASCII.
    /// </summary>
    public class TableWidthEstimateTests
    {
        private static readonly string Cjk =
            char.ConvertFromUtf32(0x65E5) + char.ConvertFromUtf32(0x672C)
            + char.ConvertFromUtf32(0x8A9E); // "Japanese" in CJK
        private static readonly string HalfwidthKana =
            char.ConvertFromUtf32(0xFF76); // HALFWIDTH KATAKANA KA
        private static readonly char NoBreakSpace = (char)0x00A0;

        [SetUp]
        public void SetUp()
        {
            AssetLinkHub.PathExists = null;
            InlineMarkupConverter.ConfigureColors(
                InlineMarkupConverter.DefaultLinkColor,
                InlineMarkupConverter.DefaultCodeColor,
                InlineMarkupConverter.DefaultCodeMark);
        }

        // -- EstimateCellUnits ------------------------------------------------

        [Test]
        public void Estimate_InlineCode_AddsChipPadding_NotBackticks()
        {
            float plain = MarkdownRenderer.EstimateCellUnits("value");
            float code = MarkdownRenderer.EstimateCellUnits("`value`");
            Assert.AreEqual(plain + 2f, code,
                "a code span adds chip padding; backticks themselves"
                + " render as chip edges, not characters");
        }

        [Test]
        public void Estimate_EmphasisMarkers_AreNotCounted()
        {
            Assert.AreEqual(
                MarkdownRenderer.EstimateCellUnits("bold"),
                MarkdownRenderer.EstimateCellUnits("**bold**"));
        }

        [Test]
        public void Estimate_Cjk_CountsDouble()
        {
            Assert.AreEqual(6f, MarkdownRenderer.EstimateCellUnits(Cjk));
        }

        [Test]
        public void Estimate_HalfwidthKana_CountsSingle()
        {
            Assert.AreEqual(3f,
                MarkdownRenderer.EstimateCellUnits(HalfwidthKana + "ab"));
        }

        [Test]
        public void Estimate_VariationSelectors_AreNotCounted()
        {
            string warn = char.ConvertFromUtf32(0x26A0);
            string selector = ((char)0xFE0F).ToString();
            Assert.AreEqual(
                MarkdownRenderer.EstimateCellUnits(warn + "ok"),
                MarkdownRenderer.EstimateCellUnits(warn + selector + "ok"));
        }

        [Test]
        public void Estimate_EmptyCell_HasFloor()
        {
            Assert.AreEqual(3f, MarkdownRenderer.EstimateCellUnits(null));
            Assert.AreEqual(3f, MarkdownRenderer.EstimateCellUnits(""));
            Assert.AreEqual(3f, MarkdownRenderer.EstimateCellUnits("a"));
        }

        // -- Inline-code no-wrap assembly ---------------------------------------

        // All substring checks are ORDINAL (string.Contains / IndexOf(char)):
        // culture-sensitive constraints collate NBSP like a plain space and
        // would misreport on these strings.

        [Test]
        public void Convert_ShortCodeSpanWithSpaces_UsesNoBreakSpaces()
        {
            string converted = InlineMarkupConverter.Convert("`var x = 1`");
            Assert.IsTrue(converted.Contains("var" + NoBreakSpace + "x"),
                "short code spans keep their chip on one line via NBSP: "
                + converted);
            Assert.IsFalse(converted.Contains("var x"),
                "no plain space may remain inside the short span");
        }

        [Test]
        public void Convert_LongCodeSpan_KeepsNormalSpaces()
        {
            string longSpan = "`this is a rather long inline code span that wraps`";
            string converted = InlineMarkupConverter.Convert(longSpan);
            Assert.IsTrue(converted.Contains("this is a rather"),
                "long spans keep normal wrapping instead of overflowing");
            Assert.IsTrue(converted.IndexOf(NoBreakSpace) < 0);
        }

        [Test]
        public void Convert_SpanText_OutsideCode_KeepsPlainSpaces()
        {
            string converted = InlineMarkupConverter.Convert("a b `c d` e f");
            Assert.IsTrue(converted.Contains("a b "));
            Assert.IsTrue(converted.Contains("c" + NoBreakSpace + "d"));
            Assert.IsTrue(converted.Contains(" e f"));
        }
    }
}
