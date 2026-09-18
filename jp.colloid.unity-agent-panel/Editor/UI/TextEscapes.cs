using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Turns off UI Toolkit's escape-sequence parsing on every text
    /// element of a subtree (docs/design-notes/2026-09-18-text-escape-
    /// sequences.md).
    ///
    /// In Unity 2022.3 a <c>TextElement</c> constructed from C# starts with
    /// <c>parseEscapeSequences = true</c> (the UXML attribute defaults to
    /// false; the C# field does not), so the text engine rewrites the two
    /// characters <c>\n</c> into a line break and <c>\t</c> into a tab
    /// before layout. A Windows path such as
    /// <c>C:\Users\x\AppData\Roaming\npm\node_modules\...</c> therefore
    /// rendered as "Roaming" / "pm" / "ode_modules" on three lines, and a
    /// code sample containing <c>"\t"</c> lost the letter. Nothing the
    /// panel shows is authored with escape sequences -- paths, tool
    /// output, model text and translations all mean their backslashes
    /// literally -- so the flag is off everywhere.
    ///
    /// Called where a subtree is built or grown: the window roots after
    /// their views are built, and each place that creates rows, cards,
    /// chips or markdown later (a scan of a finished subtree is cheap;
    /// walking on every text change would not be).
    /// </summary>
    internal static class TextEscapes
    {
        /// <summary>
        /// Sets <c>parseEscapeSequences = false</c> on <paramref name="root"/>
        /// (when it is a text element) and on every text element below it.
        /// Null-safe.
        /// </summary>
        public static void Disable(VisualElement root)
        {
            if (root == null)
            {
                return;
            }
            var self = root as TextElement;
            if (self != null)
            {
                self.parseEscapeSequences = false;
            }
            root.Query<TextElement>().ForEach(DisableOne);
        }

        private static void DisableOne(TextElement element)
        {
            element.parseEscapeSequences = false;
        }
    }
}
