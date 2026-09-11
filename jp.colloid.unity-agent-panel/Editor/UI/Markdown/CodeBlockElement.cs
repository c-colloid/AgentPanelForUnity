using UnityEditor;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI.Markdown
{
    /// <summary>
    /// Fenced code block: language badge + Copy button header, monospace
    /// body with horizontal scrolling and SELECTABLE text via a read-only
    /// multiline TextField styled flat (docs/research/03-unity-integration.md
    /// section 4.1 -- Label text selection is unreliable in 2022.3). The
    /// body never goes through rich text, so no escaping is needed here.
    /// </summary>
    public sealed class CodeBlockElement : VisualElement
    {
        public CodeBlockElement(string code, string language)
        {
            AddToClassList("uap-code");

            string body = (code ?? string.Empty).TrimEnd('\r', '\n');

            var header = new VisualElement();
            header.AddToClassList("uap-code-header");
            var lang = new Label(string.IsNullOrEmpty(language)
                ? L10n.S.MarkdownCodeDefaultLang : IconLoader.SanitizeForDisplay(language));
            lang.AddToClassList("uap-code-lang");
            lang.enableRichText = false;
            header.Add(lang);

            var copy = new Button(delegate { EditorGUIUtility.systemCopyBuffer = body; });
            copy.text = L10n.S.MarkdownCodeCopyButton;
            copy.AddToClassList("uap-code-copy");
            header.Add(copy);
            Add(header);

            var scroll = new ScrollView(ScrollViewMode.Horizontal);
            scroll.AddToClassList("uap-code-scroll");

            var field = new TextField();
            field.multiline = true;
            field.isReadOnly = true;
            // Display is sanitized (variation selectors and other
            // font-uncovered emoji have no glyph in any editor font); the
            // Copy button keeps the RAW body so clipboard fidelity is
            // untouched.
            field.value = IconLoader.SanitizeForDisplay(body);
            field.AddToClassList("uap-code-field");
            MessageBlockFactory.ApplyMonoFont(field);

            scroll.Add(field);
            Add(scroll);
        }
    }
}
