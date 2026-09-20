using System.Collections.Generic;
using Colloid.AgentPanel.UI;
using Colloid.AgentPanel.UI.Markdown;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// docs/design-notes/2026-09-18-text-escape-sequences.md: a C#-made
    /// TextElement parses "\n" / "\t" in 2022.3, so a Windows path such
    /// as ...\Roaming\npm\node_modules\... broke into three lines. Every
    /// text element the panel builds must carry parseEscapeSequences =
    /// false.
    /// </summary>
    [TestFixture]
    public class TextEscapesTests
    {
        private const string WindowsPath = @"C:\Users\x\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";

        [Test]
        public void Disable_ClearsTheFlagOnTheRootAndEveryDescendant()
        {
            var root = new Label(WindowsPath);
            var inner = new VisualElement();
            var child = new Button { text = @"a\tb" };
            inner.Add(child);
            root.Add(inner);
            var field = new TextField { value = WindowsPath };
            root.Add(field);
            // The premise (a fresh C# TextElement parses escapes) holds on
            // 2022.3 and not on Unity 6, where the default is already
            // false (CI, 2026-09-18), so it is not asserted: the sweep must
            // leave every element at false on both, whatever it started at.
            child.parseEscapeSequences = true;
            root.parseEscapeSequences = true;

            TextEscapes.Disable(root);

            Assert.IsFalse(root.parseEscapeSequences);
            Assert.IsFalse(child.parseEscapeSequences);
            foreach (TextElement te in field.Query<TextElement>().ToList())
            {
                Assert.IsFalse(te.parseEscapeSequences, "the TextField's inner text element is swept too");
            }
            Assert.AreEqual(WindowsPath, root.text, "the text itself is untouched");
        }

        [Test]
        public void Disable_NullIsANoOp()
        {
            Assert.DoesNotThrow(delegate { TextEscapes.Disable(null); });
        }

        [Test]
        public void MarkdownRender_LeavesNoParsingTextElement()
        {
            VisualElement tree = MarkdownRenderer.Render(
                "Path `" + WindowsPath + "`\n\n```c\nprintf(\"a\\tb\\n\");\n```\n\n- item \\n one\n\n| h |\n|---|\n| \\t |");
            AssertNoneParse(tree);
        }

        [Test]
        public void AgentPanelWindow_BuildsNoParsingTextElement()
        {
            var window = ScriptableObject.CreateInstance<AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                AssertNoneParse(window.rootVisualElement);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        private static void AssertNoneParse(VisualElement root)
        {
            List<TextElement> all = root.Query<TextElement>().ToList();
            Assert.Greater(all.Count, 0, "the tree holds text elements");
            var offenders = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].parseEscapeSequences)
                {
                    offenders.Add(all[i].GetType().Name + " '" + all[i].text + "'");
                }
            }
            Assert.IsEmpty(offenders, "text elements still parsing escape sequences: " + string.Join(", ", offenders));
        }
    }
}
