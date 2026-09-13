using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using Colloid.AgentPanel.UI.Markdown;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Structure of the tool card's details after the 65535-vertex fix
    /// (design note 2026-09-13-toolcard-vertex-limit.md): no single text
    /// element ever carries more than LongTextChunker.DefaultMaxChars, a
    /// hard-capped section announces the remainder and offers Copy, and a
    /// Write/Edit card renders a +/- file-change view instead of the raw
    /// JSON. Detached elements (no panel), same idiom as the other card
    /// suites.
    /// </summary>
    [TestFixture]
    public class ToolActivityCardLongTextTests
    {
        private static string Repeat(string unit, int count)
        {
            var sb = new System.Text.StringBuilder(unit.Length * count);
            for (int i = 0; i < count; i++)
            {
                sb.Append(unit);
            }
            return sb.ToString();
        }

        private static List<T> All<T>(VisualElement root, string className) where T : VisualElement
        {
            return root.Query<T>(className: className).ToList();
        }

        private static void AssertNoTextElementOverCap(VisualElement root)
        {
            List<TextElement> texts = root.Query<TextElement>().ToList();
            Assert.IsNotEmpty(texts);
            foreach (TextElement t in texts)
            {
                Assert.LessOrEqual((t.text ?? string.Empty).Length, LongTextChunker.DefaultMaxChars,
                    "one text element must stay under the per-element vertex ceiling");
            }
        }

        [Test]
        public void RawSection_ShortBody_IsOneLabel()
        {
            VisualElement section = ToolActivityCard.CreateSectionForTests("Input", "{\"a\":1}");
            Assert.AreEqual(1, All<Label>(section, "uap-toolcard-pre").Count);
            Assert.IsNull(section.Q<Button>(className: "uap-toolcard-copy"));
            Assert.IsNull(section.Q<Label>(className: "uap-toolcard-truncated"));
        }

        [Test]
        public void RawSection_LongBody_IsChunkedIntoSeveralLabels_LosingNothing()
        {
            string body = Repeat("0123456789abcdef\n", 3000); // 51,000 chars
            VisualElement section = ToolActivityCard.CreateSectionForTests("Input", body);
            List<Label> pres = All<Label>(section, "uap-toolcard-pre");
            Assert.Greater(pres.Count, 1);
            AssertNoTextElementOverCap(section);
            var sb = new System.Text.StringBuilder();
            foreach (Label pre in pres)
            {
                sb.Append(pre.text);
            }
            Assert.AreEqual(body, sb.ToString());
            // Seam classes: every chunk but the first continues, every
            // chunk but the last has more after it.
            Assert.IsFalse(pres[0].ClassListContains("uap-toolcard-pre--cont"));
            Assert.IsTrue(pres[pres.Count - 1].ClassListContains("uap-toolcard-pre--cont"));
            Assert.IsTrue(pres[0].ClassListContains("uap-toolcard-pre--more"));
            Assert.IsFalse(pres[pres.Count - 1].ClassListContains("uap-toolcard-pre--more"));
        }

        [Test]
        public void RawSection_OverHardCap_AnnouncesRemainderAndOffersCopy()
        {
            string body = Repeat("x", ToolActivityCard.SectionMaxChars + 12345);
            VisualElement section = ToolActivityCard.CreateSectionForTests("Result", body);
            Label footer = section.Q<Label>(className: "uap-toolcard-truncated");
            Assert.IsNotNull(footer);
            StringAssert.Contains("12345", footer.text);
            Assert.IsNotNull(section.Q<Button>(className: "uap-toolcard-copy"));
            AssertNoTextElementOverCap(section);
        }

        [Test]
        public void WriteCard_RendersFileChangeView_NotRawJson()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "w1",
                toolName = "Write",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"file_path\":\"Assets/Scripts/Player.cs\",\"content\":\"using UnityEngine;\\n\\npublic class Player {}\\n\"}",
                resultSummary = "File created successfully"
            };
            VisualElement details = ToolActivityCard.BuildDetailsForTests(record);
            Assert.IsNotNull(details.Q<VisualElement>(className: "uap-toolcard-diff"));
            Label path = details.Q<Label>(className: "uap-toolcard-path");
            Assert.IsNotNull(path);
            Assert.AreEqual("Assets/Scripts/Player.cs", path.text);
            List<Label> adds = All<Label>(details, "uap-perm-diff-add");
            Assert.AreEqual(3, adds.Count);
            Assert.AreEqual("+ using UnityEngine;", adds[0].text);
            Assert.AreEqual("+ ", adds[1].text);
            Assert.AreEqual("+ public class Player {}", adds[2].text);
            Assert.IsNotNull(details.Q<Button>(className: "uap-toolcard-copy"), "Write offers Copy of the raw content");
            // The raw JSON dump is replaced, the Result section stays.
            List<Label> pres = All<Label>(details, "uap-toolcard-pre");
            Assert.AreEqual(1, pres.Count);
            Assert.AreEqual("File created successfully", pres[0].text);
        }

        [Test]
        public void EditCard_RendersRemovedAndAddedLines_WithoutCopy()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "e1",
                toolName = "Edit",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"file_path\":\"Assets/A.cs\",\"old_string\":\"int a = 1;\",\"new_string\":\"int a = 2;\"}"
            };
            VisualElement details = ToolActivityCard.BuildDetailsForTests(record);
            Assert.AreEqual("- int a = 1;", details.Q<Label>(className: "uap-perm-diff-del").text);
            Assert.AreEqual("+ int a = 2;", details.Q<Label>(className: "uap-perm-diff-add").text);
            Assert.IsNull(details.Q<Button>(className: "uap-toolcard-copy"));
        }

        [Test]
        public void WriteCard_WithoutContent_FallsBackToRawInput()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "w2",
                toolName = "Write",
                status = ToolCallStatus.Failed,
                inputJson = "{\"file_path\":\"Assets/A.cs\"}"
            };
            VisualElement details = ToolActivityCard.BuildDetailsForTests(record);
            Assert.IsNull(details.Q<VisualElement>(className: "uap-toolcard-diff"));
            Assert.IsNotNull(details.Q<Label>(className: "uap-toolcard-pre"));
        }

        [Test]
        public void NonFileTool_KeepsRawInputSection()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "b1",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"command\":\"ls\"}"
            };
            VisualElement details = ToolActivityCard.BuildDetailsForTests(record);
            Assert.IsNull(details.Q<VisualElement>(className: "uap-toolcard-diff"));
            Assert.AreEqual("{\"command\":\"ls\"}", details.Q<Label>(className: "uap-toolcard-pre").text);
        }

        [Test]
        public void DiffLines_PastPreviewBudget_ShowAllButton_ThenHardCapFooter()
        {
            var lines = new List<PermissionEditPreview.DiffLine>();
            int total = ToolActivityCard.DiffHardMaxLines + 7;
            for (int i = 0; i < total; i++)
            {
                lines.Add(new PermissionEditPreview.DiffLine
                {
                    Kind = PermissionEditPreview.LineKind.Add,
                    Text = "line " + i
                });
            }
            var host = new VisualElement();
            ToolActivityCard.RenderDiffLines(host, lines, ToolActivityCard.DiffPreviewMaxLines);
            Assert.AreEqual(ToolActivityCard.DiffPreviewMaxLines, All<Label>(host, "uap-perm-diff-line").Count);
            Button showAll = host.Q<Button>(className: "uap-perm-diff-expand");
            Assert.IsNotNull(showAll);
            StringAssert.Contains(ToolActivityCard.DiffHardMaxLines.ToString(), showAll.text);

            ToolActivityCard.RenderDiffLines(host, lines, ToolActivityCard.DiffHardMaxLines);
            Assert.AreEqual(ToolActivityCard.DiffHardMaxLines, All<Label>(host, "uap-perm-diff-line").Count);
            Assert.IsNull(host.Q<Button>(className: "uap-perm-diff-expand"));
            Label footer = host.Q<Label>(className: "uap-toolcard-truncated");
            Assert.IsNotNull(footer);
            StringAssert.Contains("7", footer.text);
        }

        [Test]
        public void DiffLine_LongerThanCap_IsChunkedAcrossLabels()
        {
            var lines = new List<PermissionEditPreview.DiffLine>
            {
                new PermissionEditPreview.DiffLine
                {
                    Kind = PermissionEditPreview.LineKind.Add,
                    Text = Repeat("{\"k\":1},", 3000) // minified one-line file, 24,000 chars
                }
            };
            var host = new VisualElement();
            ToolActivityCard.RenderDiffLines(host, lines, ToolActivityCard.DiffPreviewMaxLines);
            List<Label> labels = All<Label>(host, "uap-perm-diff-add");
            Assert.Greater(labels.Count, 1);
            AssertNoTextElementOverCap(host);
            Assert.IsTrue(labels[0].text.StartsWith("+ "));
        }

        [Test]
        public void CodeBlock_LongBody_IsSplitAcrossSeveralReadOnlyFields()
        {
            string code = Repeat("Debug.Log(\"0123456789\");\n", 2000); // 50,000 chars
            var block = new CodeBlockElement(code, "csharp");
            List<TextField> fields = block.Query<TextField>(className: "uap-code-field").ToList();
            Assert.Greater(fields.Count, 1);
            var sb = new System.Text.StringBuilder();
            foreach (TextField f in fields)
            {
                Assert.IsTrue(f.isReadOnly);
                Assert.LessOrEqual(f.value.Length, LongTextChunker.DefaultMaxChars);
                sb.Append(f.value);
            }
            Assert.AreEqual(code.TrimEnd('\r', '\n'), sb.ToString());
        }

        [Test]
        public void CodeBlock_ShortBody_IsOneField()
        {
            var block = new CodeBlockElement("int x = 1;", "csharp");
            Assert.AreEqual(1, block.Query<TextField>(className: "uap-code-field").ToList().Count);
        }
    }
}
