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
    /// caption get a small "?" mark at their end: hover shows the tooltip
    /// as before, click opens the same text in a popover where it wraps
    /// and can be read at leisure. Short tooltips stay as they are.
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
        /// Tooltips at or above this length get a mark. 90 characters is
        /// about two lines of the small hint font at the 300px minimum
        /// dock width -- the point where a hover balloon stops being a
        /// caption and starts being a paragraph.
        /// </summary>
        public const int MarkThresholdChars = 90;

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
            return tooltip.Trim().Length >= MarkThresholdChars ? Kind.Mark : Kind.Tooltip;
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
