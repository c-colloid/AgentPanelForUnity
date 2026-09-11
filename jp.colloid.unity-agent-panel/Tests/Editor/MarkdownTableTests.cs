using System.Collections.Generic;
using Colloid.AgentPanel.UI;
using Colloid.AgentPanel.UI.Markdown;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pipe-table suite: detection (header + |---| separator), cell
    /// splitting (inline-code and escaped pipes), alignment markers,
    /// ragged-row tolerance, false-positive rejection (plain text with
    /// pipes must stay a paragraph) and the injection boundary for cell
    /// content (cells feed rich-text Labels through the
    /// InlineMarkupConverter chokepoint). All source literals are strict
    /// ASCII; CJK test data uses \u escapes.
    /// </summary>
    public class MarkdownTableTests
    {
        private const string Lt = InlineMarkupConverter.NeutralizedLt;

        [SetUp]
        public void SetUp()
        {
            AssetLinkHub.PathExists = null;
            InlineMarkupConverter.ConfigureColors(
                InlineMarkupConverter.DefaultLinkColor,
                InlineMarkupConverter.DefaultCodeColor,
                InlineMarkupConverter.DefaultCodeMark);
        }

        private static string[] Lines(params string[] lines)
        {
            return lines;
        }

        // -- Parser: detection and shape -----------------------------------------

        [Test]
        public void TryParse_BasicTable_ParsesHeaderAndRows()
        {
            MarkdownTableModel table;
            int consumed;
            bool ok = MarkdownTableParser.TryParse(Lines(
                "| Name | Value |",
                "| --- | --- |",
                "| a | 1 |",
                "| b | 2 |"), 0, out table, out consumed);

            Assert.IsTrue(ok);
            Assert.AreEqual(4, consumed);
            Assert.AreEqual(new List<string> { "Name", "Value" }, table.Header);
            Assert.AreEqual(2, table.Rows.Count);
            Assert.AreEqual(new List<string> { "a", "1" }, table.Rows[0]);
            Assert.AreEqual(new List<string> { "b", "2" }, table.Rows[1]);
        }

        [Test]
        public void TryParse_WithoutOuterPipes_StillParses()
        {
            MarkdownTableModel table;
            int consumed;
            bool ok = MarkdownTableParser.TryParse(Lines(
                "Name | Value",
                "--- | ---",
                "a | 1"), 0, out table, out consumed);
            Assert.IsTrue(ok);
            Assert.AreEqual(3, consumed);
            Assert.AreEqual(2, table.ColumnCount);
        }

        [Test]
        public void TryParse_TableEndsAtBlankLineOrNonRow()
        {
            MarkdownTableModel table;
            int consumed;
            bool ok = MarkdownTableParser.TryParse(Lines(
                "| a | b |",
                "| - | - |",
                "| 1 | 2 |",
                "",
                "| not | part |"), 0, out table, out consumed);
            Assert.IsTrue(ok);
            Assert.AreEqual(3, consumed);
            Assert.AreEqual(1, table.Rows.Count);
        }

        [Test]
        public void TryParse_AlignmentMarkers_MapToColumns()
        {
            MarkdownTableModel table;
            int consumed;
            bool ok = MarkdownTableParser.TryParse(Lines(
                "| L | C | R | D |",
                "| :--- | :---: | ---: | --- |",
                "| 1 | 2 | 3 | 4 |"), 0, out table, out consumed);
            Assert.IsTrue(ok);
            Assert.AreEqual(MarkdownTableAlignment.Left, table.Alignments[0]);
            Assert.AreEqual(MarkdownTableAlignment.Center, table.Alignments[1]);
            Assert.AreEqual(MarkdownTableAlignment.Right, table.Alignments[2]);
            Assert.AreEqual(MarkdownTableAlignment.Left, table.Alignments[3]);
        }

        // -- Parser: cell splitting ------------------------------------------------

        [Test]
        public void SplitRow_PipeInsideInlineCode_DoesNotSplit()
        {
            List<string> cells = MarkdownTableParser.SplitRow("| `a|b` | x |");
            Assert.AreEqual(2, cells.Count);
            Assert.AreEqual("`a|b`", cells[0]);
            Assert.AreEqual("x", cells[1]);
        }

        [Test]
        public void SplitRow_EscapedPipe_BecomesLiteralPipe()
        {
            List<string> cells = MarkdownTableParser.SplitRow("| a \\| b | c |");
            Assert.AreEqual(2, cells.Count);
            Assert.AreEqual("a | b", cells[0]);
            Assert.AreEqual("c", cells[1]);
        }

        [Test]
        public void TryParse_RaggedRows_PadShortAndJoinOverflow()
        {
            MarkdownTableModel table;
            int consumed;
            bool ok = MarkdownTableParser.TryParse(Lines(
                "| a | b |",
                "| - | - |",
                "| only |",
                "| 1 | 2 | 3 | 4 |"), 0, out table, out consumed);
            Assert.IsTrue(ok);
            Assert.AreEqual(new List<string> { "only", "" }, table.Rows[0]);
            Assert.AreEqual(new List<string> { "1", "2 | 3 | 4" }, table.Rows[1]);
        }

        [Test]
        public void TryParse_CjkCells_RoundTrip()
        {
            // Header "koumoku | atai", row "namae | tarou" (the exact
            // user-visible failure shape) built from \u escapes so the
            // source file stays strictly ASCII.
            string koumoku = "\u9805\u76EE";
            string atai = "\u5024";
            string namae = "\u540D\u524D";
            string tarou = "\u592A\u90CE";
            MarkdownTableModel table;
            int consumed;
            bool ok = MarkdownTableParser.TryParse(Lines(
                "| " + koumoku + " | " + atai + " |",
                "| --- | --- |",
                "| " + namae + " | " + tarou + " |"), 0, out table, out consumed);
            Assert.IsTrue(ok);
            Assert.AreEqual(koumoku, table.Header[0]);
            Assert.AreEqual(atai, table.Header[1]);
            Assert.AreEqual(namae, table.Rows[0][0]);
            Assert.AreEqual(tarou, table.Rows[0][1]);
        }

        // -- Parser: false positives ------------------------------------------------

        [Test]
        public void TryParse_ProseWithPipes_NoSeparator_IsNotATable()
        {
            MarkdownTableModel table;
            int consumed;
            Assert.IsFalse(MarkdownTableParser.TryParse(Lines(
                "either A | B works",
                "and C | D too"), 0, out table, out consumed));
        }

        [Test]
        public void TryParse_SinglePipeLastLine_IsNotATable()
        {
            MarkdownTableModel table;
            int consumed;
            Assert.IsFalse(MarkdownTableParser.TryParse(Lines(
                "just a | pipe"), 0, out table, out consumed));
        }

        [Test]
        public void TryParse_SeparatorColumnCountMismatch_IsNotATable()
        {
            MarkdownTableModel table;
            int consumed;
            Assert.IsFalse(MarkdownTableParser.TryParse(Lines(
                "| a | b | c |",
                "| --- | --- |"), 0, out table, out consumed));
        }

        [Test]
        public void TryParse_SingleColumnWithoutOuterPipes_IsNotATable()
        {
            // "a" / "-" would otherwise match ("-" is a valid separator
            // cell): a one-column table requires explicit outer pipes.
            MarkdownTableModel table;
            int consumed;
            Assert.IsFalse(MarkdownTableParser.TryParse(Lines(
                "a", "-"), 0, out table, out consumed));
        }

        // -- Renderer integration ---------------------------------------------------

        [Test]
        public void Render_BasicTable_ProducesTableClasses()
        {
            VisualElement root = MarkdownRenderer.Render(
                "before\n| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\nafter");
            Assert.IsNotNull(root.Q(className: "uap-md-tablewrap"));
            Assert.IsNotNull(root.Q(className: "uap-md-table"));
            Assert.AreEqual(3, root.Query(className: "uap-md-tr").ToList().Count);
            Assert.AreEqual(2, root.Query<Label>(className: "uap-md-th").ToList().Count);
            Assert.AreEqual(4, root.Query<Label>(className: "uap-md-td").ToList().Count);
            // Zebra: the second body row carries the alt class.
            Assert.AreEqual(1, root.Query(className: "uap-md-tr--alt").ToList().Count);
            // Wrapper is a horizontal ScrollView.
            Assert.IsNotNull(root.Q<ScrollView>(className: "uap-md-tablewrap"));
            // Neighboring text still renders as paragraphs.
            Assert.AreEqual(2, root.Query<Label>(className: "uap-md-p").ToList().Count);
        }

        [Test]
        public void Render_TableCells_GoThroughTheEscapeChokepoint()
        {
            VisualElement root = MarkdownRenderer.Render(
                "| type | note |\n| --- | --- |\n| List<int> | <color=red>x</color> |");
            var cells = root.Query<Label>(className: "uap-md-td").ToList();
            Assert.AreEqual(2, cells.Count);
            StringAssert.Contains("List" + Lt + "int>", cells[0].text);
            StringAssert.DoesNotContain("<color", cells[1].text);
            StringAssert.Contains(Lt + "color=red>", cells[1].text);
        }

        [Test]
        public void Render_TableCellInlineMarkup_Converts()
        {
            VisualElement root = MarkdownRenderer.Render(
                "| a |\n| --- |\n| **bold** and `code` |");
            Label cell = root.Q<Label>(className: "uap-md-td");
            Assert.IsNotNull(cell);
            StringAssert.Contains("<b>bold</b>", cell.text);
            // Inline code is color-only (no <mark>) -- see the regression
            // guard in MarkdownConverterTests for why.
            StringAssert.Contains("<color=", cell.text);
            StringAssert.DoesNotContain("<mark", cell.text);
        }

        [Test]
        public void Render_AlignmentMarkers_ProduceAlignmentClasses()
        {
            VisualElement root = MarkdownRenderer.Render(
                "| l | c | r |\n| :-- | :-: | --: |\n| 1 | 2 | 3 |");
            Assert.AreEqual(2, root.Query(className: "uap-md-cell--center").ToList().Count,
                "header + body cell of the centered column");
            Assert.AreEqual(2, root.Query(className: "uap-md-cell--right").ToList().Count);
        }

        [Test]
        public void Render_PipesWithoutSeparator_FallBackToParagraph()
        {
            VisualElement root = MarkdownRenderer.Render(
                "either A | B\nor C | D");
            Assert.IsNull(root.Q(className: "uap-md-table"));
            Assert.IsNotNull(root.Q<Label>(className: "uap-md-p"));
        }

        [Test]
        public void Render_TableInsideCodeFence_StaysCode()
        {
            VisualElement root = MarkdownRenderer.Render(
                "```\n| a | b |\n| --- | --- |\n| 1 | 2 |\n```");
            Assert.IsNull(root.Q(className: "uap-md-table"));
            Assert.AreEqual(1, root.Query<CodeBlockElement>().ToList().Count);
        }
    }
}
