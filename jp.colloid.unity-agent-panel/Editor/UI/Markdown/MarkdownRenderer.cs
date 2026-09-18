using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Colloid.AgentPanel.UI.Markdown
{
    /// <summary>
    /// Block-level markdown renderer for FINALIZED messages only
    /// (ARCHITECTURE.md D7: streaming stays plain, one full render per
    /// message). Supported blocks: headings #..####, unordered/ordered
    /// lists (2-level nesting), blockquote, horizontal rule, paragraphs
    /// and fenced code blocks (CodeBlockElement). All inline text goes
    /// through InlineMarkupConverter -- the escape chokepoint -- before it
    /// reaches a rich-text-enabled Label.
    ///
    /// Link clicks use the 2022.3 rich-text &lt;link&gt; tag +
    /// PointerDownLinkTagEvent (UnityEngine.UIElements.Experimental in
    /// this version; verified against the 2022.3.22f1 module API).
    /// http(s) ids open in the browser, Assets/Packages ids are raised
    /// through AssetLinkHub.
    /// </summary>
    public static class MarkdownRenderer
    {
        /// <summary>Light-theme literals (dark ones are the converter defaults).</summary>
        private const string LightLinkColor = "#0F52BA";
        private const string LightCodeColor = "#7A3E2A";
        private const string LightCodeMark = "#00000018";

        /// <summary>Renders finalized markdown into a VisualElement tree.</summary>
        public static VisualElement Render(string markdown)
        {
            ConfigureThemeColors();

            var root = new VisualElement();
            root.AddToClassList("uap-md");

            string[] lines = (markdown ?? string.Empty).Replace("\r\n", "\n")
                .Replace('\r', '\n').Split('\n');

            var paragraph = new StringBuilder();
            bool inFence = false;
            string fenceLanguage = null;
            var fenceBody = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("```", System.StringComparison.Ordinal))
                {
                    if (!inFence)
                    {
                        FlushParagraph(root, paragraph);
                        inFence = true;
                        fenceLanguage = trimmed.Substring(3).Trim();
                        fenceBody.Length = 0;
                    }
                    else
                    {
                        root.Add(new CodeBlockElement(fenceBody.ToString(), fenceLanguage));
                        inFence = false;
                        fenceLanguage = null;
                    }
                    continue;
                }
                if (inFence)
                {
                    if (fenceBody.Length > 0)
                    {
                        fenceBody.Append('\n');
                    }
                    fenceBody.Append(line);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    FlushParagraph(root, paragraph);
                    continue;
                }

                int headingLevel = MatchHeading(trimmed);
                if (headingLevel > 0)
                {
                    FlushParagraph(root, paragraph);
                    string content = trimmed.Substring(headingLevel).TrimStart();
                    root.Add(CreateRichLabel(InlineMarkupConverter.Convert(content),
                        "uap-md-h" + headingLevel));
                    continue;
                }

                if (IsHorizontalRule(trimmed))
                {
                    FlushParagraph(root, paragraph);
                    var rule = new VisualElement();
                    rule.AddToClassList("uap-md-hr");
                    root.Add(rule);
                    continue;
                }

                if (trimmed.StartsWith(">", System.StringComparison.Ordinal))
                {
                    FlushParagraph(root, paragraph);
                    var quote = new StringBuilder();
                    while (i < lines.Length)
                    {
                        string qt = lines[i].TrimStart();
                        if (!qt.StartsWith(">", System.StringComparison.Ordinal))
                        {
                            i--;
                            break;
                        }
                        string inner = qt.Substring(1);
                        if (inner.StartsWith(" ", System.StringComparison.Ordinal))
                        {
                            inner = inner.Substring(1);
                        }
                        if (quote.Length > 0)
                        {
                            quote.Append('\n');
                        }
                        quote.Append(inner);
                        i++;
                    }
                    var quoteBox = new VisualElement();
                    quoteBox.AddToClassList("uap-md-quote");
                    quoteBox.Add(CreateRichLabel(
                        InlineMarkupConverter.Convert(quote.ToString()), "uap-md-quote-text"));
                    root.Add(quoteBox);
                    continue;
                }

                string marker;
                string itemText;
                bool nested;
                if (MatchListItem(line, out marker, out itemText, out nested))
                {
                    FlushParagraph(root, paragraph);
                    root.Add(CreateListItem(marker, itemText, nested));
                    continue;
                }

                // Pipe tables: only a header line followed by a |---|
                // separator row starts one; anything else with pipes
                // falls through to the paragraph path unchanged.
                MarkdownTableModel tableModel;
                int tableLineCount;
                if (line.IndexOf('|') >= 0 && MarkdownTableParser.TryParse(
                        lines, i, out tableModel, out tableLineCount))
                {
                    FlushParagraph(root, paragraph);
                    root.Add(CreateTable(tableModel));
                    i += tableLineCount - 1;
                    continue;
                }

                if (paragraph.Length > 0)
                {
                    paragraph.Append('\n');
                }
                paragraph.Append(line.TrimEnd());
            }

            if (inFence)
            {
                // Unterminated fence (truncated output): defensively render
                // the tail as code so nothing is silently lost. UICODE-12:
                // no body-length guard -- output cut right after "```lang"
                // used to drop the fence AND its language signal entirely,
                // contradicting this very comment; an empty code block with
                // its language badge is the honest rendering of that cut.
                root.Add(new CodeBlockElement(fenceBody.ToString(), fenceLanguage));
            }
            FlushParagraph(root, paragraph);

            if (root.childCount == 0)
            {
                root.Add(CreateRichLabel(string.Empty, "uap-md-p"));
            }
            TextEscapes.Disable(root);
            return root;
        }

        /// <summary>
        /// Rich-text Label whose text ALREADY passed the converter, with
        /// the link click handler attached. The only place in the panel
        /// where enableRichText is turned on.
        /// </summary>
        public static Label CreateRichLabel(string convertedRichText, string ussClass)
        {
            var label = new Label(convertedRichText ?? string.Empty);
            label.enableRichText = true;
            label.parseEscapeSequences = false;
            if (!string.IsNullOrEmpty(ussClass))
            {
                label.AddToClassList(ussClass);
            }
            label.RegisterCallback<PointerDownLinkTagEvent>(OnLinkTagClicked);
            return label;
        }

        private static void OnLinkTagClicked(PointerDownLinkTagEvent evt)
        {
            string raw = InlineMarkupConverter.Unescape(evt.linkID);
            if (InlineMarkupConverter.IsHttpUrl(raw))
            {
                Application.OpenURL(raw);
            }
            else if (InlineMarkupConverter.IsAssetPath(raw))
            {
                AssetLinkHub.RaiseLinkClicked(raw);
            }
            evt.StopPropagation();
        }

        // -- Block helpers ------------------------------------------------------

        private static void ConfigureThemeColors()
        {
            if (EditorGUIUtility.isProSkin)
            {
                InlineMarkupConverter.ConfigureColors(
                    InlineMarkupConverter.DefaultLinkColor,
                    InlineMarkupConverter.DefaultCodeColor,
                    InlineMarkupConverter.DefaultCodeMark);
            }
            else
            {
                InlineMarkupConverter.ConfigureColors(
                    LightLinkColor, LightCodeColor, LightCodeMark);
            }
        }

        private static void FlushParagraph(VisualElement root, StringBuilder paragraph)
        {
            if (paragraph.Length == 0)
            {
                return;
            }
            root.Add(CreateRichLabel(
                InlineMarkupConverter.Convert(paragraph.ToString()), "uap-md-p"));
            paragraph.Length = 0;
        }

        // -- Tables --------------------------------------------------------------

        /// <summary>Per-character width unit estimate (CJK counts double).</summary>
        private const float CellUnitWidth = 7f;
        /// <summary>Per-column clamp before horizontal scrolling kicks in.</summary>
        private const float CellMinWidth = 60f;
        private const float CellMaxWidth = 320f;
        /// <summary>Bold header text runs slightly wider than body text.</summary>
        private const float HeaderUnitScale = 1.1f;
        /// <summary>Extra units per inline-code span (mark chip padding).</summary>
        private const float CodeChipPaddingUnits = 2f;

        /// <summary>
        /// Builds one table: a horizontal ScrollView wrapper (wide tables
        /// scroll instead of overflowing the panel) around header + body
        /// rows. Every cell is a rich-text Label fed through
        /// InlineMarkupConverter -- the chokepoint keeps cell content
        /// injection-safe. Column widths are flexible above a per-column
        /// minimum estimated from the raw cell text.
        /// </summary>
        private static VisualElement CreateTable(MarkdownTableModel model)
        {
            var wrap = new ScrollView(ScrollViewMode.Horizontal);
            wrap.AddToClassList("uap-md-tablewrap");

            var table = new VisualElement();
            table.AddToClassList("uap-md-table");

            int columns = model.ColumnCount;
            var minWidths = new float[columns];
            for (int c = 0; c < columns; c++)
            {
                // Header cells render bold, so their estimate is scaled up.
                float units = EstimateCellUnits(model.Header[c]) * HeaderUnitScale;
                for (int r = 0; r < model.Rows.Count; r++)
                {
                    float rowUnits = EstimateCellUnits(model.Rows[r][c]);
                    if (rowUnits > units)
                    {
                        units = rowUnits;
                    }
                }
                minWidths[c] = Mathf.Clamp(
                    units * CellUnitWidth + 14f, CellMinWidth, CellMaxWidth);
            }

            table.Add(CreateTableRow(model.Header, model.Alignments, minWidths,
                true, false));
            for (int r = 0; r < model.Rows.Count; r++)
            {
                table.Add(CreateTableRow(model.Rows[r], model.Alignments, minWidths,
                    false, (r & 1) == 1));
            }
            wrap.Add(table);

            // Stretch the table to at least the visible width so narrow
            // tables fill the panel; wider content scrolls horizontally.
            // The viewport (not the wrap) is the reference: the wrap rect
            // includes its 1px borders, and matching that width would
            // leave every table ~2px overflowed -- a permanently visible
            // horizontal scrollbar with 2px of dead panning.
            wrap.contentViewport.RegisterCallback<GeometryChangedEvent>(
                delegate(GeometryChangedEvent evt)
            {
                float width = evt.newRect.width;
                if (width > 0f)
                {
                    table.style.minWidth = width;
                }
            });

            // 2022.3 ScrollView(Horizontal) maps a plain vertical wheel
            // to horizontal panning and stops propagation whenever the
            // scroller moved, so a wide table would trap the transcript
            // scroll under the cursor. Intercept in trickle-down (fires
            // before the ScrollView's own handler): vertical-intent
            // wheels go to the nearest vertical ScrollView ancestor;
            // horizontal intent (Shift held or a dominant x delta) still
            // pans the table.
            wrap.RegisterCallback<WheelEvent>(delegate(WheelEvent evt)
            {
                OnTableWheel(wrap, evt);
            }, TrickleDown.TrickleDown);
            return wrap;
        }

        /// <summary>See the WheelEvent registration in CreateTable.</summary>
        private static void OnTableWheel(ScrollView wrap, WheelEvent evt)
        {
            if (evt.shiftKey || Mathf.Abs(evt.delta.x) > Mathf.Abs(evt.delta.y))
            {
                return; // Horizontal intent: let the wrap pan the table.
            }
            // Mirror of the ScrollView consume condition (which compares
            // the content width against the wrap's layout width): when the
            // table does not actually overflow, the wrap cannot pan, never
            // stops propagation, and the transcript scrolls natively.
            // contentContainer sizes to its children, so its layout width
            // is the table width (boundingBox is internal in 2022.3).
            if (wrap.contentContainer.layout.width - wrap.layout.width <= 0f)
            {
                return;
            }
            // Immediate stop: also suppresses the wrap's own bubble-phase
            // handler when this very element is the event target.
            evt.StopImmediatePropagation();
            for (VisualElement parent = wrap.parent; parent != null; parent = parent.parent)
            {
                var outer = parent as ScrollView;
                if (outer != null && outer.mode != ScrollViewMode.Horizontal)
                {
                    // Same step Unity applies for a wheel tick; Scroller
                    // clamps the value, so over-scroll is safe.
                    outer.scrollOffset = new Vector2(
                        outer.scrollOffset.x,
                        outer.scrollOffset.y + evt.delta.y * outer.mouseWheelScrollSize);
                    break;
                }
            }
        }

        private static VisualElement CreateTableRow(IReadOnlyList<string> cells,
            IReadOnlyList<MarkdownTableAlignment> alignments, float[] minWidths,
            bool isHeader, bool isAlt)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-md-tr");
            if (isHeader)
            {
                row.AddToClassList("uap-md-tr--head");
            }
            else if (isAlt)
            {
                // Zebra striping; the token can be transparent to disable.
                row.AddToClassList("uap-md-tr--alt");
            }
            for (int c = 0; c < cells.Count; c++)
            {
                Label cell = CreateRichLabel(
                    InlineMarkupConverter.Convert(cells[c]),
                    isHeader ? "uap-md-th" : "uap-md-td");
                if (c < alignments.Count)
                {
                    switch (alignments[c])
                    {
                        case MarkdownTableAlignment.Center:
                            cell.AddToClassList("uap-md-cell--center");
                            break;
                        case MarkdownTableAlignment.Right:
                            cell.AddToClassList("uap-md-cell--right");
                            break;
                    }
                }
                if (c < minWidths.Length)
                {
                    cell.style.minWidth = minWidths[c];
                }
                row.Add(cell);
            }
            return row;
        }

        /// <summary>
        /// Rough rendered-text width in character units for one RAW
        /// markdown cell. CJK/fullwidth characters count as 2 units
        /// (halfwidth forms as 1); emphasis markers (*), backticks and
        /// variation selectors do not render and are not counted; every
        /// inline-code span adds CodeChipPaddingUnits for the mark-chip
        /// padding. Public for EditMode width-estimation tests.
        /// </summary>
        public static float EstimateCellUnits(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return 3f;
            }
            float units = 0f;
            bool inCode = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '`')
                {
                    // Backticks render as chip edges, not characters.
                    if (!inCode)
                    {
                        units += CodeChipPaddingUnits;
                    }
                    inCode = !inCode;
                    continue;
                }
                if (!inCode && c == '*')
                {
                    continue; // Bold/italic markers do not render.
                }
                if (c == (char)0xFE0F || c == (char)0xFE0E)
                {
                    continue; // Variation selectors are stripped for display.
                }
                units += IsWideChar(c) ? 2f : 1f;
            }
            return units < 3f ? 3f : units;
        }

        /// <summary>CJK and fullwidth forms occupy roughly two cells.</summary>
        private static bool IsWideChar(char c)
        {
            if (c < 0x2E80)
            {
                return false;
            }
            // Halfwidth Katakana / halfwidth symbol range renders narrow.
            if (c >= 0xFF61 && c <= 0xFFDC)
            {
                return false;
            }
            return true;
        }

        private static VisualElement CreateListItem(string marker, string itemText,
            bool nested)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-md-li");
            if (nested)
            {
                row.AddToClassList("uap-md-li--nested");
            }
            var markerLabel = new Label(marker);
            markerLabel.enableRichText = false;
            markerLabel.AddToClassList("uap-md-li-marker");
            row.Add(markerLabel);
            row.Add(CreateRichLabel(InlineMarkupConverter.Convert(itemText),
                "uap-md-li-text"));
            return row;
        }

        /// <summary>Returns the heading level 1..4, or 0 (no heading).</summary>
        private static int MatchHeading(string trimmed)
        {
            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '#')
            {
                level++;
            }
            if (level < 1 || level > 4)
            {
                return 0;
            }
            if (level >= trimmed.Length || trimmed[level] != ' ')
            {
                return 0;
            }
            return level;
        }

        private static bool IsHorizontalRule(string trimmed)
        {
            string compact = trimmed.Replace(" ", string.Empty);
            if (compact.Length < 3)
            {
                return false;
            }
            char first = compact[0];
            if (first != '-' && first != '*' && first != '_')
            {
                return false;
            }
            for (int i = 1; i < compact.Length; i++)
            {
                if (compact[i] != first)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Matches "- item" / "* item" / "+ item" / "1. item" / "1) item"
        /// with an optional 2+ space indent (one nesting level).
        /// </summary>
        private static bool MatchListItem(string line, out string marker,
            out string itemText, out bool nested)
        {
            marker = null;
            itemText = null;
            nested = false;

            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent += 1;
            }
            nested = indent >= 2;
            int i = indent;
            if (i >= line.Length)
            {
                return false;
            }

            char c = line[i];
            if (c == '-' || c == '*' || c == '+')
            {
                if (i + 1 < line.Length && line[i + 1] == ' ')
                {
                    marker = nested
                        ? IconLoader.GlyphWhiteBullet
                        : IconLoader.GlyphBullet;
                    itemText = line.Substring(i + 2).TrimStart();
                    return itemText.Length > 0;
                }
                return false;
            }

            int digits = 0;
            while (i + digits < line.Length && char.IsDigit(line[i + digits]) && digits < 3)
            {
                digits++;
            }
            if (digits > 0 && i + digits + 1 < line.Length
                && (line[i + digits] == '.' || line[i + digits] == ')')
                && line[i + digits + 1] == ' ')
            {
                marker = line.Substring(i, digits) + ".";
                itemText = line.Substring(i + digits + 2).TrimStart();
                return itemText.Length > 0;
            }
            return false;
        }
    }
}
