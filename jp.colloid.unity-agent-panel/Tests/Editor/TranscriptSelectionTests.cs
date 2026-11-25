using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The pure halves of TranscriptSelection (design note 2026-10-01-
    /// transcript-text-selection.md): reading a selection back out of a
    /// label's text by TextCore's element indices, the quote the composer
    /// receives, and the whole-message copy text.
    /// </summary>
    public class TranscriptSelectionTests
    {
        // -- StripRichText ---------------------------------------------------

        [Test]
        public void StripRichText_RemovesConverterTags()
        {
            string rich = "see <b>bold</b> and <link=\"https://x.y\"><color=#5aa0ff>https://x.y</color></link>.";
            Assert.AreEqual("see bold and https://x.y.", TranscriptSelection.StripRichText(rich));
        }

        [Test]
        public void StripRichText_KeepsNeutralizedLtLiterally()
        {
            string rich = "List<noparse><</noparse>int> x";
            Assert.AreEqual("List<int> x", TranscriptSelection.StripRichText(rich));
        }

        [Test]
        public void StripRichText_UnterminatedNoparseShowsRest()
        {
            Assert.AreEqual("a <b> c", TranscriptSelection.StripRichText("a <noparse><b> c"));
        }

        [Test]
        public void StripRichText_LoneLtWithoutCloserIsText()
        {
            Assert.AreEqual("a <b", TranscriptSelection.StripRichText("a <b"));
        }

        [Test]
        public void StripRichText_NullAndEmpty()
        {
            Assert.AreEqual(string.Empty, TranscriptSelection.StripRichText(null));
            Assert.AreEqual(string.Empty, TranscriptSelection.StripRichText(string.Empty));
        }

        // -- ExtractSelection --------------------------------------------------

        [Test]
        public void ExtractSelection_PlainLabel_EitherIndexOrder()
        {
            Assert.AreEqual("ell", TranscriptSelection.ExtractSelection("hello", false, 1, 4));
            Assert.AreEqual("ell", TranscriptSelection.ExtractSelection("hello", false, 4, 1));
        }

        [Test]
        public void ExtractSelection_RichLabel_IndicesCountRenderedCharacters()
        {
            // Rendered: "see bold now" -- "bold" is elements 4..8.
            string rich = "see <b>bold</b> now";
            Assert.AreEqual("bold", TranscriptSelection.ExtractSelection(rich, true, 4, 8));
        }

        [Test]
        public void ExtractSelection_SurrogatePairCountsAsOneElement()
        {
            string text = "a" + char.ConvertFromUtf32(0x1F600) + "bc";
            // Elements: a, <emoji>, b, c -> selecting elements 1..3 is emoji + b.
            Assert.AreEqual(char.ConvertFromUtf32(0x1F600) + "b",
                TranscriptSelection.ExtractSelection(text, false, 1, 3));
        }

        [Test]
        public void ExtractSelection_ClampsAndRejectsEmptyRange()
        {
            Assert.AreEqual("lo", TranscriptSelection.ExtractSelection("hello", false, 3, 99));
            Assert.AreEqual(string.Empty, TranscriptSelection.ExtractSelection("hello", false, 2, 2));
            Assert.AreEqual("he", TranscriptSelection.ExtractSelection("hello", false, -5, 2));
            Assert.AreEqual(string.Empty, TranscriptSelection.ExtractSelection(null, false, 0, 3));
        }

        // -- FormatQuote ---------------------------------------------------------

        [Test]
        public void FormatQuote_PrefixesEveryLineAndLeavesABlankLineForTheQuestion()
        {
            Assert.AreEqual("> first\n> second\n\n", TranscriptSelection.FormatQuote("first\nsecond"));
        }

        [Test]
        public void FormatQuote_TrimsOuterBlankLinesKeepsInnerOnes()
        {
            Assert.AreEqual("> a\n>\n> b\n\n", TranscriptSelection.FormatQuote("\na\r\n\r\nb  \n\n"));
        }

        [Test]
        public void FormatQuote_KeepsLeadingIndentation()
        {
            // A quoted code fragment keeps its indentation; only trailing
            // whitespace is dropped.
            Assert.AreEqual(">   if (x)\n>     y();\n\n",
                TranscriptSelection.FormatQuote("  if (x)\n    y();  "));
        }

        [Test]
        public void FormatQuote_EmptyOrWhitespaceYieldsEmpty()
        {
            Assert.AreEqual(string.Empty, TranscriptSelection.FormatQuote(null));
            Assert.AreEqual(string.Empty, TranscriptSelection.FormatQuote("  \n "));
        }

        // -- MessageText ---------------------------------------------------------

        [Test]
        public void MessageText_JoinsTextBlocksOnlyWithBlankLines()
        {
            var message = new ChatMessage();
            message.blocks.Add(new ChatMessageBlock { kind = ChatBlockKind.Thinking, text = "hmm" });
            message.blocks.Add(new ChatMessageBlock { kind = ChatBlockKind.Text, text = "**one**\n" });
            message.blocks.Add(new ChatMessageBlock { kind = ChatBlockKind.ToolCall, toolCall = new ToolCallRecord() });
            message.blocks.Add(new ChatMessageBlock { kind = ChatBlockKind.Text, text = "two" });
            message.blocks.Add(new ChatMessageBlock { kind = ChatBlockKind.Text, text = "" });
            Assert.AreEqual("**one**\n\ntwo", TranscriptSelection.MessageText(message));
        }

        [Test]
        public void MessageText_NullOrNoText()
        {
            Assert.AreEqual(string.Empty, TranscriptSelection.MessageText(null));
            var message = new ChatMessage();
            message.blocks.Add(new ChatMessageBlock { kind = ChatBlockKind.SystemNote, text = "n" });
            Assert.AreEqual(string.Empty, TranscriptSelection.MessageText(message));
        }
    }
}
