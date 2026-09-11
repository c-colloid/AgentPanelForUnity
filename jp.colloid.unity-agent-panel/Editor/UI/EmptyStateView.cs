using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Welcome card shown when the session has no messages (R05 section
    /// 3.4): spark mark, short tagline and context-aware suggestion
    /// chips. Clicking a chip inserts the text into the composer (never
    /// auto-sends). Dynamic chips ("Fix the console errors", "Explain
    /// the selected object") appear only when the editor state warrants
    /// them; rebuilds are event-driven (Selection.selectionChanged /
    /// ConsoleErrorProvider.Changed) and only run while visible -- no
    /// per-frame polling.
    /// </summary>
    public sealed class EmptyStateView
    {
        private readonly VisualElement _root;
        private readonly Label _subtitle;
        private readonly Label _spark;
        private readonly VisualElement _chipHost;
        private readonly Action<string> _onSuggestion;

        private bool _visible;
        private bool _subscribed;

        public VisualElement Root
        {
            get { return _root; }
        }

        public EmptyStateView(Action<string> onSuggestion)
        {
            _onSuggestion = onSuggestion;

            _root = new VisualElement();
            _root.AddToClassList("uap-empty");

            _spark = new Label(IconLoader.CurrentAgentGlyph);
            _spark.AddToClassList("uap-empty-spark");
            _root.Add(_spark);

            var title = new Label(L10n.S.EmptyTitle);
            title.AddToClassList("uap-empty-title");
            _root.Add(title);

            _subtitle = new Label(L10n.A(L10n.S.EmptySubtitle));
            _subtitle.AddToClassList("uap-empty-sub");
            _root.Add(_subtitle);

            _chipHost = new VisualElement();
            _chipHost.AddToClassList("uap-empty-chips");
            _root.Add(_chipHost);

            ReloadQuickActions();
            RebuildChips();
        }

        // UICODE-9: quick actions come off DISK (UserSettings/AgentPanel/
        // QuickActions.json) and only change when the user edits them in
        // Settings -- yet RebuildChips used to re-read the file on every
        // Selection.selectionChanged / ConsoleErrorProvider.Changed while
        // the empty state was visible. Cached here; reloaded when the view
        // becomes visible (cheap, and it picks up Settings edits made while
        // Chat was showing a conversation). The dynamic selection/error
        // chips keep rebuilding from their in-memory providers every time.
        private List<QuickAction> _cachedQuickActions = new List<QuickAction>();

        /// <summary>UICODE-9 test seam: how many disk reloads have happened.</summary>
        internal int QuickActionReloadCountForTests;

        private void ReloadQuickActions()
        {
            QuickActionReloadCountForTests++;
            _cachedQuickActions = QuickActionStore.CreateDefault(AgentHub.ProjectRoot).Load();
        }

        public void SetVisible(bool visible)
        {
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
            {
                // The agent name follows the selected backend; the label
                // is built once, so it is re-read whenever the view shows.
                string subtitle = L10n.A(L10n.S.EmptySubtitle);
                if (_subtitle.text != subtitle)
                {
                    _subtitle.text = subtitle;
                }
                string glyph = IconLoader.CurrentAgentGlyph;
                if (_spark.text != glyph)
                {
                    _spark.text = glyph;
                }
            }
            if (_visible == visible)
            {
                return;
            }
            _visible = visible;
            if (visible)
            {
                Subscribe();
                ReloadQuickActions();
                RebuildChips();
            }
            else
            {
                Unsubscribe();
            }
        }

        /// <summary>
        /// Drops the editor-event subscriptions (view teardown /
        /// OnDeactivate). Also resets the visibility latch: without it a
        /// reactivated view whose Refresh calls SetVisible(true) would hit
        /// the equality early-return and never re-subscribe, freezing the
        /// dynamic suggestion chips at their pre-deactivate state.
        /// </summary>
        public void Detach()
        {
            Unsubscribe();
            _visible = false;
        }

        // -- Events -----------------------------------------------------------------

        private void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }
            _subscribed = true;
            Selection.selectionChanged += OnEditorStateChanged;
            ConsoleErrorProvider.Changed += OnEditorStateChanged;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }
            _subscribed = false;
            Selection.selectionChanged -= OnEditorStateChanged;
            ConsoleErrorProvider.Changed -= OnEditorStateChanged;
        }

        private void OnEditorStateChanged()
        {
            if (_visible)
            {
                RebuildChips();
            }
        }

        // -- Chips -------------------------------------------------------------------

        private void RebuildChips()
        {
            _chipHost.Clear();
            // User-defined quick actions (Settings UI, docs/design-notes/
            // 2026-08-01-settings-enrichment.md #3) come first -- the user
            // explicitly configured these, so they take priority over the
            // fixed built-in suggestions below.
            List<QuickAction> quickActions = _cachedQuickActions;
            for (int i = 0; i < quickActions.Count; i++)
            {
                QuickAction action = quickActions[i];
                if (action != null && !string.IsNullOrEmpty(action.label)
                    && !string.IsNullOrEmpty(action.prompt))
                {
                    AddChip(action.label, action.prompt);
                }
            }
            // VisibleCount, not Count: when everything captured is on the
            // user's ignore list (2026-08-13 error-chip-ignore design note)
            // there is nothing they want fixed, so suggesting "fix the
            // console errors" would just re-surface the silenced noise.
            if (ConsoleErrorProvider.VisibleCount > 0)
            {
                AddChip(L10n.S.EmptySuggestionErrors, L10n.S.EmptySuggestionErrors);
            }
            if (SelectionContextProvider.HasSelection)
            {
                AddChip(L10n.S.EmptySuggestionSelection, L10n.S.EmptySuggestionSelection);
            }
            AddChip(L10n.S.EmptySuggestionProject, L10n.S.EmptySuggestionProject);
            AddChip(L10n.S.EmptySuggestionScene, L10n.S.EmptySuggestionScene);
        }

        /// <summary>label is the chip's button text; prompt is what gets inserted
        /// into the composer (a quick action's label and prompt can differ;
        /// the four built-in suggestions above pass the same string for both).</summary>
        private void AddChip(string label, string prompt)
        {
            var chip = new Button(delegate
            {
                if (_onSuggestion != null)
                {
                    _onSuggestion(prompt);
                }
            });
            // label can now be user-authored (a QuickAction's label, typed
            // in Settings) rather than always one of the fixed built-in
            // strings above -- disable rich text so it can never be
            // interpreted as markup (same policy as every other
            // user/model-authored Label in this codebase).
            chip.enableRichText = false;
            chip.text = label;
            chip.AddToClassList("uap-chip");
            _chipHost.Add(chip);
        }
    }
}
