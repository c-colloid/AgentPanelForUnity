using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Settings search (docs/design-notes/2026-09-17-settings-redesign-
    /// plan.md D3, phase 3). Text matching and the row filter live here,
    /// away from SettingsView's builders, so they are unit-testable on a
    /// synthetic tree. A "row" is a direct child of a card body: the
    /// hint scopes, field rows, foldouts and lists every Build*Section
    /// appends. Its searchable text is every TextElement (labels, button
    /// captions, a field's label and current popup value) and every
    /// tooltip in its subtree, read live at filter time so state-driven
    /// text (status lines) is searched as displayed. A card whose TITLE
    /// matches shows whole.
    /// </summary>
    public static class SettingsSearch
    {
        /// <summary>Whitespace-only input is no search.</summary>
        public static bool IsActive(string query)
        {
            return !string.IsNullOrEmpty(query) && query.Trim().Length > 0;
        }

        /// <summary>Case-insensitive substring, query trimmed; an inactive query matches everything.</summary>
        public static bool Matches(string text, string query)
        {
            if (!IsActive(query))
            {
                return true;
            }
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return text.IndexOf(query.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Every TextElement text and every tooltip in the subtree, joined
        /// with newlines. Text fields' VALUES are deliberately skipped
        /// (user data, not a setting name); their label is a TextElement
        /// and is included.
        /// </summary>
        public static string CollectText(VisualElement element)
        {
            if (element == null)
            {
                return string.Empty;
            }
            var parts = new List<string>();
            Collect(element, parts);
            return string.Join("\n", parts);
        }

        private static void Collect(VisualElement element, List<string> parts)
        {
            if (element is TextField)
            {
                // The label child is what we want; the input's text is data.
                TextField field = (TextField)element;
                if (!string.IsNullOrEmpty(field.label))
                {
                    parts.Add(field.label);
                }
                if (!string.IsNullOrEmpty(element.tooltip))
                {
                    parts.Add(element.tooltip);
                }
                return;
            }
            var text = element as TextElement;
            if (text != null && !string.IsNullOrEmpty(text.text))
            {
                parts.Add(text.text);
            }
            if (!string.IsNullOrEmpty(element.tooltip))
            {
                parts.Add(element.tooltip);
            }
            int count = element.childCount;
            for (int i = 0; i < count; i++)
            {
                Collect(element[i], parts);
            }
        }
    }

    /// <summary>
    /// One searchable card: where it lives (tab label for the crumb), its
    /// title, the card element and the body whose direct children are the
    /// rows.
    /// </summary>
    public sealed class SettingsSearchCard
    {
        public string TabLabel;
        public string Title;
        public VisualElement Card;
        public VisualElement Body;
        /// <summary>The "Tab > Card" line shown at the top of the card while a search is on; null until the filter creates it.</summary>
        public Label Crumb;
    }

    /// <summary>
    /// Applies a query to a set of cards by toggling inline display on
    /// rows and cards, and restores EXACTLY the inline display each
    /// element had before the search started (rows carry their own
    /// state-driven inline display, so a USS class could not do this).
    /// Opening a matched Foldout is one-way: a user who searched into a
    /// collapsed section keeps it open after clearing.
    /// </summary>
    public sealed class SettingsSearchFilter
    {
        private readonly List<SettingsSearchCard> _cards;
        private readonly Dictionary<VisualElement, StyleEnum<DisplayStyle>> _saved =
            new Dictionary<VisualElement, StyleEnum<DisplayStyle>>();
        private bool _active;

        public SettingsSearchFilter(List<SettingsSearchCard> cards)
        {
            _cards = cards ?? new List<SettingsSearchCard>();
        }

        public bool IsActive
        {
            get { return _active; }
        }

        /// <summary>
        /// Filters to `query`. Returns the number of visible rows (a
        /// whole-card title match counts its rows). An inactive query is
        /// the same as Clear().
        /// </summary>
        public int Apply(string query)
        {
            if (!SettingsSearch.IsActive(query))
            {
                Clear();
                return 0;
            }
            _active = true;
            int visibleRows = 0;
            foreach (SettingsSearchCard card in _cards)
            {
                EnsureCrumb(card);
                Remember(card.Card);
                bool titleMatch = SettingsSearch.Matches(card.Title, query);
                int cardRows = 0;
                int count = card.Body.childCount;
                for (int i = 0; i < count; i++)
                {
                    VisualElement row = card.Body[i];
                    Remember(row);
                    bool show = titleMatch || SettingsSearch.Matches(SettingsSearch.CollectText(row), query);
                    row.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                    if (show)
                    {
                        cardRows++;
                        var foldout = row as Foldout;
                        if (foldout != null && !titleMatch)
                        {
                            foldout.value = true;
                        }
                    }
                }
                bool showCard = cardRows > 0;
                card.Card.style.display = showCard ? DisplayStyle.Flex : DisplayStyle.None;
                card.Crumb.style.display = showCard ? DisplayStyle.Flex : DisplayStyle.None;
                if (showCard)
                {
                    Foldout sectionFoldout = card.Card.Q<Foldout>(className: "uap-settings-section-foldout");
                    if (sectionFoldout != null)
                    {
                        sectionFoldout.value = true;
                    }
                }
                visibleRows += cardRows;
            }
            return visibleRows;
        }

        /// <summary>Restores every inline display the filter touched and hides the crumbs.</summary>
        public void Clear()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            foreach (KeyValuePair<VisualElement, StyleEnum<DisplayStyle>> pair in _saved)
            {
                pair.Key.style.display = pair.Value;
            }
            _saved.Clear();
            foreach (SettingsSearchCard card in _cards)
            {
                if (card.Crumb != null)
                {
                    card.Crumb.style.display = DisplayStyle.None;
                }
            }
        }

        private void Remember(VisualElement element)
        {
            if (!_saved.ContainsKey(element))
            {
                _saved[element] = element.style.display;
            }
        }

        private static void EnsureCrumb(SettingsSearchCard card)
        {
            if (card.Crumb != null)
            {
                return;
            }
            var crumb = new Label(card.TabLabel + "  >  " + card.Title);
            crumb.AddToClassList("uap-settings-search-crumb");
            crumb.enableRichText = false;
            crumb.parseEscapeSequences = false;
            crumb.style.display = DisplayStyle.None;
            card.Card.Insert(0, crumb);
            card.Crumb = crumb;
        }
    }
}
