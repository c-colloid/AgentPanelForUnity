using System;
using System.Text;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Text selection in the transcript (docs/design-notes/2026-10-01-
    /// transcript-text-selection.md): the labels that show conversation
    /// text are selectable with the mouse, the selection can be copied
    /// (Ctrl/Cmd+C or the context menu) and quoted into the composer as
    /// the start of a follow-up question ("Ask about selection").
    ///
    /// UI Toolkit selects per TextElement, so a selection never spans two
    /// labels; the last label the user selected in is remembered here
    /// (<see cref="Remember"/>) so the message-level context menu and the
    /// copy shortcut can find it. The selected text is read back from the
    /// label's own text and its selection indices (<see cref="ExtractSelection"/>):
    /// TextCore counts rendered characters (tags stripped, a surrogate
    /// pair as one), so a rich-text label's markup is removed first with
    /// <see cref="StripRichText"/>, the inverse of the tags
    /// InlineMarkupConverter emits.
    /// </summary>
    internal static class TranscriptSelection
    {
        /// <summary>USS hook for the selectable labels (selection colors).</summary>
        public const string SelectableClass = "uap-selectable";

        /// <summary>
        /// Receives the selected text when the user picks "Ask about
        /// selection"; ChatView quotes it into the composer. Null while no
        /// chat view is built, in which case the menu item is disabled.
        /// </summary>
        public static Action<string> AskAboutRequested;

        private static TextElement _current;

        /// <summary>
        /// Turns on mouse selection for <paramref name="element"/> and
        /// tracks it as a selection source. Null-safe; idempotent.
        /// </summary>
        public static void MakeSelectable(TextElement element)
        {
            if (element == null || element.selection.isSelectable)
            {
                return;
            }
            // selectAllOnFocus / selectAllOnMouseUp are internal on
            // ITextSelection in 2022.3; a Label's defaults are already
            // false, so plain click-and-drag selection is what we get.
            element.selection.isSelectable = true;
            element.AddToClassList(SelectableClass);
            element.RegisterCallback<PointerUpEvent>(OnSelectionSourcePointerUp);
            element.RegisterCallback<KeyUpEvent>(OnSelectionSourceKeyUp);
        }

        /// <summary>
        /// Attaches the message context menu (copy selection / copy message
        /// / ask about selection) and the copy shortcut to one message's
        /// root element.
        /// </summary>
        public static void AttachMessageMenu(VisualElement messageRoot, ChatMessage message)
        {
            if (messageRoot == null || message == null)
            {
                return;
            }
            messageRoot.AddManipulator(new ContextualMenuManipulator(
                delegate (ContextualMenuPopulateEvent evt) { PopulateMenu(evt, messageRoot, message); }));
            messageRoot.RegisterCallback<KeyDownEvent>(delegate (KeyDownEvent evt)
            {
                OnMessageKeyDown(evt, messageRoot);
            });
        }

        /// <summary>
        /// The selected text of the most recently used selectable label
        /// under <paramref name="scope"/>, or false when there is none (no
        /// selection, the label was released by the virtualized list, or
        /// it belongs to another message).
        /// </summary>
        public static bool TryGetSelectedText(VisualElement scope, out string text)
        {
            text = null;
            TextElement element = _current;
            if (element == null || element.panel == null || !element.selection.HasSelection())
            {
                return false;
            }
            if (scope != null && !IsDescendantOf(element, scope))
            {
                return false;
            }
            text = ExtractSelection(element.text, element.enableRichText,
                element.selection.cursorIndex, element.selection.selectIndex);
            return !string.IsNullOrEmpty(text);
        }

        /// <summary>Test seam: forgets the remembered label.</summary>
        internal static void ResetForTests()
        {
            _current = null;
        }

        // -- Pure helpers (unit-tested) -------------------------------------

        /// <summary>
        /// The text between two selection indices of a label, as the user
        /// saw it. Indices count rendered text elements the way TextCore
        /// does: tags do not count, a surrogate pair counts once. Either
        /// order; out-of-range indices are clamped.
        /// </summary>
        internal static string ExtractSelection(string labelText, bool richText,
            int cursorIndex, int selectIndex)
        {
            string plain = richText ? StripRichText(labelText) : (labelText ?? string.Empty);
            int start = Math.Min(cursorIndex, selectIndex);
            int end = Math.Max(cursorIndex, selectIndex);
            if (start < 0)
            {
                start = 0;
            }
            if (end <= start)
            {
                return string.Empty;
            }
            int startChar = CharIndexOfElement(plain, start);
            int endChar = CharIndexOfElement(plain, end);
            return plain.Substring(startChar, endChar - startChar);
        }

        /// <summary>
        /// Removes the rich-text tags InlineMarkupConverter emits and
        /// restores the literal '&lt;' it neutralized, yielding the text
        /// TextCore renders. Outside a noparse run every '&lt;' opens a
        /// tag (the converter neutralizes every raw one), so the stripper
        /// is: copy noparse bodies verbatim, drop every other
        /// '&lt;'...'&gt;' run.
        /// </summary>
        internal static string StripRichText(string richText)
        {
            if (string.IsNullOrEmpty(richText))
            {
                return string.Empty;
            }
            const string NoparseOpen = "<noparse>";
            const string NoparseClose = "</noparse>";
            var sb = new StringBuilder(richText.Length);
            int i = 0;
            while (i < richText.Length)
            {
                char c = richText[i];
                if (c != '<')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (string.CompareOrdinal(richText, i, NoparseOpen, 0, NoparseOpen.Length) == 0)
                {
                    int bodyStart = i + NoparseOpen.Length;
                    int close = richText.IndexOf(NoparseClose, bodyStart, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        // Unterminated noparse: TextCore shows the rest
                        // literally.
                        sb.Append(richText, bodyStart, richText.Length - bodyStart);
                        break;
                    }
                    sb.Append(richText, bodyStart, close - bodyStart);
                    i = close + NoparseClose.Length;
                    continue;
                }
                int gt = richText.IndexOf('>', i + 1);
                if (gt < 0)
                {
                    // A lone '<' with no closer is not a tag; TextCore
                    // renders it as text.
                    sb.Append(richText, i, richText.Length - i);
                    break;
                }
                i = gt + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The selected text as a markdown block quote followed by a blank
        /// line, ready for the user's question underneath. Blank leading /
        /// trailing lines are dropped; inner blank lines keep a bare "&gt;".
        /// Empty input yields an empty string.
        /// </summary>
        internal static string FormatQuote(string selected)
        {
            if (selected == null)
            {
                return string.Empty;
            }
            string[] lines = selected.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int first = 0;
            int last = lines.Length - 1;
            while (first <= last && lines[first].Trim().Length == 0)
            {
                first++;
            }
            while (last >= first && lines[last].Trim().Length == 0)
            {
                last--;
            }
            if (first > last)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = first; i <= last; i++)
            {
                string line = lines[i].TrimEnd();
                sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
            }
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// The text of one message for "Copy message": its text blocks as
        /// the markdown source they were received as (what the user typed,
        /// or what the model wrote), joined by blank lines. Tool cards,
        /// thinking, attachments and notes are not part of it.
        /// </summary>
        internal static string MessageText(ChatMessage message)
        {
            if (message == null || message.blocks == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < message.blocks.Count; i++)
            {
                ChatMessageBlock block = message.blocks[i];
                if (block == null || block.kind != ChatBlockKind.Text || string.IsNullOrEmpty(block.text))
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append("\n\n");
                }
                sb.Append(block.text.TrimEnd('\r', '\n'));
            }
            return sb.ToString();
        }

        // -- Internals ------------------------------------------------------

        private static void PopulateMenu(ContextualMenuPopulateEvent evt,
            VisualElement messageRoot, ChatMessage message)
        {
            string selected;
            bool hasSelection = TryGetSelectedText(messageRoot, out selected);
            DropdownMenuAction.Status selectionStatus = hasSelection
                ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            string messageText = MessageText(message);

            evt.menu.AppendAction(L10n.S.ChatMenuCopySelection,
                delegate { EditorGUIUtility.systemCopyBuffer = selected; },
                selectionStatus);
            evt.menu.AppendAction(L10n.S.ChatMenuCopyMessage,
                delegate { EditorGUIUtility.systemCopyBuffer = messageText; },
                messageText.Length > 0
                    ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendSeparator();
            evt.menu.AppendAction(L10n.S.ChatMenuAskAboutSelection,
                delegate { RaiseAskAbout(selected); },
                hasSelection && AskAboutRequested != null
                    ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        private static void RaiseAskAbout(string selected)
        {
            Action<string> handler = AskAboutRequested;
            if (handler != null && !string.IsNullOrEmpty(selected))
            {
                handler(selected);
            }
        }

        /// <summary>
        /// Ctrl/Cmd+C over a message copies the remembered selection. The
        /// label's own selection handling copies too when it has focus;
        /// writing the same text twice is harmless, and this path also
        /// covers the case where focus moved to a parent.
        /// </summary>
        private static void OnMessageKeyDown(KeyDownEvent evt, VisualElement messageRoot)
        {
            if (evt.keyCode != KeyCode.C || !(evt.ctrlKey || evt.commandKey))
            {
                return;
            }
            string selected;
            if (!TryGetSelectedText(messageRoot, out selected))
            {
                return;
            }
            EditorGUIUtility.systemCopyBuffer = selected;
            evt.StopPropagation();
        }

        private static void OnSelectionSourcePointerUp(PointerUpEvent evt)
        {
            Remember(evt.currentTarget as TextElement);
        }

        private static void OnSelectionSourceKeyUp(KeyUpEvent evt)
        {
            Remember(evt.currentTarget as TextElement);
        }

        private static void Remember(TextElement element)
        {
            if (element == null)
            {
                return;
            }
            if (element.selection.HasSelection())
            {
                _current = element;
            }
            else if (_current == element)
            {
                _current = null;
            }
        }

        private static bool IsDescendantOf(VisualElement element, VisualElement ancestor)
        {
            for (VisualElement e = element; e != null; e = e.parent)
            {
                if (e == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Char index in <paramref name="plain"/> of the text element with
        /// index <paramref name="elementIndex"/>, counting a surrogate pair
        /// as one element; clamps to the string length.
        /// </summary>
        private static int CharIndexOfElement(string plain, int elementIndex)
        {
            int chars = 0;
            int elements = 0;
            while (chars < plain.Length && elements < elementIndex)
            {
                if (char.IsHighSurrogate(plain[chars]) && chars + 1 < plain.Length
                    && char.IsLowSurrogate(plain[chars + 1]))
                {
                    chars += 2;
                }
                else
                {
                    chars++;
                }
                elements++;
            }
            return chars;
        }
    }
}
