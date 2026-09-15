using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The third copy layer made visible (docs/design-notes/2026-09-05-ui-
    /// redesign.md, settings review 2026-09-06). Label says WHAT, the hint
    /// says the one line needed to choose, and the tooltip carries WHY and
    /// the edge cases -- but a tooltip is invisible until hovered, so a
    /// 300-character explanation next to a toggle was undiscoverable. Rows
    /// whose tooltip is long enough to be an explanation rather than a
    /// caption get a small "?" mark at their end: hover the mark for the
    /// text, click it for the same text in a popover where it wraps and
    /// can be read at leisure. Short tooltips stay as they are.
    ///
    /// 2026-09-15 (docs/design-notes/2026-09-15-panel-ux-followups.md
    /// section 3), from user feedback that the tooltips get in the way:
    /// once a row has a mark, the paragraph moves ONTO the mark and off
    /// the row. Settings tooltips are set on a field's whole scope
    /// (SettingsView.AddHint) so that hovering either the label or the
    /// control shows them -- which also made the hover target the size of
    /// the row, and reading down a settings page popped a paragraph-sized
    /// balloon over the next control every time the pointer crossed one.
    /// The explanation is not lost and not less discoverable: the "?" is
    /// the visible thing this class exists to add, and it is now the one
    /// place that offers the paragraph, on purpose rather than in passing.
    /// Short captions ("next reconnect", "applies immediately") never earn
    /// a mark and keep hovering exactly as before -- a one-line caption is
    /// the tooltip working as intended.
    /// </summary>
    public static class HelpAffordance
    {
        public enum Kind
        {
            None,
            Tooltip,
            Mark
        }

        /// <summary>
        /// Tooltips at or above this DISPLAY WIDTH get a mark. 90 is about
        /// two lines of the small hint font at the 300px minimum dock
        /// width -- the point where a hover balloon stops being a caption
        /// and starts being a paragraph.
        /// </summary>
        public const int MarkThresholdWidth = 90;

        public const string MarkClass = "uap-helpmark";
        public const string MarkedSwitchClass = "uap-switch--helpmarked";
        public const string HintRowClass = "uap-settings-hintrow";
        /// <summary>Any row that received a mark; pulls the row over the reserved gutter.</summary>
        public const string MarkedRowClass = "uap-helpmarked";

        /// <summary>Pure decision: what affordance a tooltip text earns.</summary>
        public static Kind Resolve(string tooltip)
        {
            if (string.IsNullOrEmpty(tooltip))
            {
                return Kind.None;
            }
            return MeasureWidth(tooltip.Trim()) >= MarkThresholdWidth ? Kind.Mark : Kind.Tooltip;
        }

        /// <summary>
        /// Display width in half-widths: CJK (and other East Asian
        /// Wide/Fullwidth) characters count 2, everything else 1. The
        /// threshold is about how much SPACE the balloon takes, and
        /// counting raw characters got that wrong by a factor of two in
        /// Japanese -- a 45-character Japanese sentence fills the same two
        /// lines as a 90-character English one, so the marks the 2026-09-06
        /// review added were appearing on barely half the rows that needed
        /// them while running the panel in Japanese, leaving the longest
        /// balloons hovering off the row. Ranges are the Unicode East Asian
        /// Width W/F blocks the panel's own copy actually uses (kana, CJK
        /// ideographs and their extensions, fullwidth forms, CJK
        /// punctuation); anything outside them, Latin and the emoji-free
        /// symbols included, counts 1. ASCII text measures exactly as
        /// before, so the English copy's marks are unchanged.
        /// </summary>
        public static int MeasureWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            int width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                width += IsWide(text[i]) ? 2 : 1;
            }
            return width;
        }

        private static bool IsWide(char c)
        {
            return (c >= '\u1100' && c <= '\u115F')      // Hangul Jamo
                || (c >= '\u2E80' && c <= '\u303E')      // CJK radicals, kangxi, CJK punctuation
                || (c >= '\u3041' && c <= '\u33FF')      // kana, Hangul compat, CJK compat
                || (c >= '\u3400' && c <= '\u4DBF')      // CJK ext A
                || (c >= '\u4E00' && c <= '\u9FFF')      // CJK unified ideographs
                || (c >= '\uA000' && c <= '\uA4CF')      // Yi
                || (c >= '\uAC00' && c <= '\uD7A3')      // Hangul syllables
                || (c >= '\uF900' && c <= '\uFAFF')      // CJK compat ideographs
                || (c >= '\uFE30' && c <= '\uFE6F')      // CJK compat forms, small forms
                || (c >= '\uFF00' && c <= '\uFF60')      // fullwidth forms
                || (c >= '\uFFE0' && c <= '\uFFE6');     // fullwidth signs
        }

        /// <summary>
        /// A 22px icon-family button carrying <paramref name="text"/> as its
        /// tooltip; click opens the popover. Unity's own component-header
        /// "?" (d__Help) is the glyph, so it reads as "documentation here".
        /// </summary>
        public static Button CreateMark(string text)
        {
            var mark = new Button();
            mark.AddToClassList(MarkClass);
            mark.tooltip = text;
            VisualElement icon = IconLoader.CreateIcon("d__Help", "?",
                "uap-helpmark-icon", "uap-helpmark-glyph");
            mark.Add(icon);
            mark.clicked += delegate
            {
                Rect screenRect = GUIUtility.GUIToScreenRect(mark.worldBound);
                HelpPopover.ShowBelow(screenRect, text);
            };
            return mark;
        }

        /// <summary>
        /// Walks <paramref name="root"/> and appends a mark to every row
        /// container that carries a long tooltip. A "row" is the first
        /// child of the tooltip-bearing scope that is a BaseField (label +
        /// control) or a .uap-settings-row; scopes with neither (a lone
        /// hint) get the mark after their last child instead.
        /// </summary>
        public static int ApplyMarks(VisualElement root)
        {
            int applied = 0;
            if (root == null)
            {
                return 0;
            }
            foreach (VisualElement scope in root.Query<VisualElement>().ToList())
            {
                if (Resolve(scope.tooltip) != Kind.Mark || scope.ClassListContains(MarkClass))
                {
                    continue;
                }
                if (scope.Q<VisualElement>(className: MarkClass) != null)
                {
                    continue;
                }
                VisualElement row = FindRow(scope);
                if (row == null)
                {
                    continue;
                }
                if (row == scope)
                {
                    // No field row: sit the mark at the end of the scope's
                    // hint line instead of leaving it alone on a new line.
                    row = WrapTrailingHint(scope) ?? scope;
                }
                if (row.ClassListContains("uap-switch"))
                {
                    row.AddToClassList(MarkedSwitchClass);
                }
                row.AddToClassList(MarkedRowClass);
                row.Add(CreateMark(scope.tooltip));
                // The paragraph now belongs to the mark, not to the whole
                // row -- see this class's doc comment. Clearing the scope
                // also keeps ApplyMarks idempotent by construction: a
                // second pass resolves the (now empty) tooltip to None.
                scope.tooltip = null;
                applied++;
            }
            return applied;
        }

        /// <summary>
        /// Moves the scope's first .uap-settings-hint label into a
        /// horizontal .uap-settings-hintrow container (hint grows, mark
        /// hugs its right end). Returns null when the scope has no hint.
        /// </summary>
        internal static VisualElement WrapTrailingHint(VisualElement scope)
        {
            Label hint = null;
            foreach (VisualElement child in scope.Children())
            {
                if (child is Label && child.ClassListContains("uap-settings-hint"))
                {
                    hint = (Label)child;
                    break;
                }
            }
            if (hint == null)
            {
                return null;
            }
            int index = scope.IndexOf(hint);
            var row = new VisualElement();
            row.AddToClassList(HintRowClass);
            scope.Insert(index, row);
            hint.RemoveFromHierarchy();
            row.Add(hint);
            return row;
        }

        internal static VisualElement FindRow(VisualElement scope)
        {
            foreach (VisualElement child in scope.Children())
            {
                if (child.ClassListContains("unity-base-field") || child.ClassListContains("uap-settings-row"))
                {
                    return child;
                }
            }
            return scope.childCount > 0 ? scope : null;
        }

        /// <summary>
        /// Borderless dropdown window (same host pattern as StatusBarView's
        /// usage popover) that shows one wrapped paragraph.
        /// </summary>
        private sealed class HelpPopover : EditorWindow
        {
            private const float Width = 340f;
            private string _text;

            public static void ShowBelow(Rect screenRect, string text)
            {
                var window = CreateInstance<HelpPopover>();
                window._text = text ?? string.Empty;
                window.BuildContent();
                window.ShowAsDropDown(screenRect, new Vector2(Width, EstimateHeight(window._text)));
            }

            /// <summary>Static estimate (no live layout available before the window exists): ~48 characters per line at the small font.</summary>
            internal static float EstimateHeight(string text)
            {
                int length = text != null ? text.Length : 0;
                int lines = Mathf.Max(1, Mathf.CeilToInt(length / 48f));
                return Mathf.Clamp(24f + lines * 16f, 48f, 420f);
            }

            private void BuildContent()
            {
                VisualElement root = rootVisualElement;
                AgentPanelWindow.ApplyThemeAndStyles(root);
                root.AddToClassList("uap-help-popover");
                var label = new Label(IconLoader.SanitizeForDisplay(_text));
                label.AddToClassList("uap-help-popover-text");
                label.enableRichText = false;
                root.Add(label);
            }
        }
    }
}
