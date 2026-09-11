using System.Collections.Generic;
using System.Text;

namespace Colloid.AgentPanel.UI.Markdown
{
    /// <summary>Column alignment from the separator-row markers.</summary>
    public enum MarkdownTableAlignment
    {
        Left,
        Center,
        Right
    }

    /// <summary>
    /// Parsed pipe table: header cells, per-column alignment and body
    /// rows normalized to the header column count (short rows padded with
    /// empty cells, overflow cells joined into the last column). Cell
    /// strings are RAW markdown -- the renderer runs each one through
    /// InlineMarkupConverter (the escape chokepoint) before display.
    /// </summary>
    public sealed class MarkdownTableModel
    {
        public readonly List<string> Header = new List<string>();
        public readonly List<MarkdownTableAlignment> Alignments =
            new List<MarkdownTableAlignment>();
        public readonly List<List<string>> Rows = new List<List<string>>();

        public int ColumnCount
        {
            get { return Header.Count; }
        }
    }

    /// <summary>
    /// Detector/parser for GFM-style pipe tables (ARCHITECTURE.md D7
    /// extension): a header row, a |---|-style separator row (optional
    /// ':' alignment markers), then body rows. Pipes inside inline-code
    /// spans and escaped pipes ("\|") never split cells. Anything that
    /// does not match the header+separator shape is NOT a table -- the
    /// caller falls back to plain paragraphs, so lone pipe lines can
    /// never be misrendered. Pure string logic, no Unity dependency
    /// (EditMode-testable).
    /// </summary>
    public static class MarkdownTableParser
    {
        /// <summary>
        /// Tries to parse a table starting at lines[startIndex]. On
        /// success returns the model and the number of consumed lines
        /// (header + separator + body rows).
        /// </summary>
        public static bool TryParse(string[] lines, int startIndex,
            out MarkdownTableModel table, out int lineCount)
        {
            table = null;
            lineCount = 0;
            if (lines == null || startIndex < 0 || startIndex + 1 >= lines.Length)
            {
                return false;
            }

            string headerLine = lines[startIndex];
            string separatorLine = lines[startIndex + 1];
            if (headerLine.IndexOf('|') < 0 || separatorLine.IndexOf('|') < 0)
            {
                return false;
            }

            List<string> header = SplitRow(headerLine);
            if (header.Count < 1)
            {
                return false;
            }
            // Confidence gate against false positives ("a | b" prose): a
            // one-column table must use explicit outer pipes.
            if (header.Count < 2
                && !headerLine.TrimStart().StartsWith("|", System.StringComparison.Ordinal))
            {
                return false;
            }

            List<string> separator = SplitRow(separatorLine);
            if (separator.Count != header.Count)
            {
                return false;
            }
            var alignments = new List<MarkdownTableAlignment>(separator.Count);
            for (int i = 0; i < separator.Count; i++)
            {
                MarkdownTableAlignment alignment;
                if (!TryParseSeparatorCell(separator[i], out alignment))
                {
                    return false;
                }
                alignments.Add(alignment);
            }

            table = new MarkdownTableModel();
            table.Header.AddRange(header);
            table.Alignments.AddRange(alignments);

            int consumed = 2;
            for (int i = startIndex + 2; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.IndexOf('|') < 0 || line.Trim().Length == 0)
                {
                    break;
                }
                List<string> cells = SplitRow(line);
                if (cells.Count == 0)
                {
                    break;
                }
                table.Rows.Add(NormalizeRow(cells, header.Count));
                consumed++;
            }
            lineCount = consumed;
            return true;
        }

        /// <summary>
        /// Splits one table row on '|' delimiters, honoring inline-code
        /// spans (a '|' between backticks does not split) and escaped
        /// pipes ("\|" emits a literal '|'). Outer pipes are dropped;
        /// each cell is trimmed.
        /// </summary>
        public static List<string> SplitRow(string line)
        {
            var cells = new List<string>();
            if (line == null)
            {
                return cells;
            }
            var sb = new StringBuilder();
            bool inCode = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '`')
                {
                    inCode = !inCode;
                    sb.Append(c);
                    continue;
                }
                if (c == '\\' && i + 1 < line.Length && line[i + 1] == '|')
                {
                    sb.Append('|');
                    i++;
                    continue;
                }
                if (c == '|' && !inCode)
                {
                    cells.Add(sb.ToString().Trim());
                    sb.Length = 0;
                    continue;
                }
                sb.Append(c);
            }
            cells.Add(sb.ToString().Trim());

            // Drop the empty edge cells produced by outer pipes
            // ("| a | b |" -> "", "a", "b", "").
            string trimmed = line.Trim();
            if (cells.Count > 1 && cells[0].Length == 0
                && trimmed.StartsWith("|", System.StringComparison.Ordinal))
            {
                cells.RemoveAt(0);
            }
            if (cells.Count > 1 && cells[cells.Count - 1].Length == 0
                && trimmed.EndsWith("|", System.StringComparison.Ordinal)
                && !trimmed.EndsWith("\\|", System.StringComparison.Ordinal))
            {
                cells.RemoveAt(cells.Count - 1);
            }
            return cells;
        }

        /// <summary>
        /// Validates one separator cell (":---", "---:", ":-:", "-"...)
        /// and derives the column alignment. At least one dash, nothing
        /// but dashes between the optional colon markers.
        /// </summary>
        private static bool TryParseSeparatorCell(string cell,
            out MarkdownTableAlignment alignment)
        {
            alignment = MarkdownTableAlignment.Left;
            if (string.IsNullOrEmpty(cell))
            {
                return false;
            }
            int start = 0;
            int end = cell.Length;
            bool colonLeft = cell[0] == ':';
            if (colonLeft)
            {
                start++;
            }
            bool colonRight = end > start && cell[end - 1] == ':';
            if (colonRight)
            {
                end--;
            }
            if (end - start < 1)
            {
                return false;
            }
            for (int i = start; i < end; i++)
            {
                if (cell[i] != '-')
                {
                    return false;
                }
            }
            if (colonLeft && colonRight)
            {
                alignment = MarkdownTableAlignment.Center;
            }
            else if (colonRight)
            {
                alignment = MarkdownTableAlignment.Right;
            }
            return true;
        }

        /// <summary>
        /// Ragged-row tolerance: pads short rows with empty cells and
        /// joins overflow cells into the last column (never dropped).
        /// </summary>
        private static List<string> NormalizeRow(List<string> cells, int columnCount)
        {
            if (cells.Count > columnCount)
            {
                var joined = new StringBuilder(cells[columnCount - 1]);
                for (int i = columnCount; i < cells.Count; i++)
                {
                    joined.Append(" | ").Append(cells[i]);
                }
                cells.RemoveRange(columnCount, cells.Count - columnCount);
                cells[columnCount - 1] = joined.ToString();
            }
            while (cells.Count < columnCount)
            {
                cells.Add(string.Empty);
            }
            return cells;
        }
    }
}
