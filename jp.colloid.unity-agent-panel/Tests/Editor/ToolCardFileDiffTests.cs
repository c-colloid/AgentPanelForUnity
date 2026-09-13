using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Line building for the tool card's file-change view (design note
    /// 2026-09-13-toolcard-vertex-limit.md section 4). Pure logic;
    /// ToolActivityCardLongTextTests pins the rendered structure.
    /// </summary>
    [TestFixture]
    public class ToolCardFileDiffTests
    {
        private static string Render(List<PermissionEditPreview.DiffLine> lines)
        {
            var sb = new System.Text.StringBuilder();
            foreach (PermissionEditPreview.DiffLine line in lines)
            {
                switch (line.Kind)
                {
                    case PermissionEditPreview.LineKind.Remove: sb.Append('-'); break;
                    case PermissionEditPreview.LineKind.Add: sb.Append('+'); break;
                    case PermissionEditPreview.LineKind.Gap: sb.Append('~'); break;
                    case PermissionEditPreview.LineKind.Separator: sb.Append('='); break;
                    default: sb.Append(' '); break;
                }
                sb.Append(line.Text).Append('|');
            }
            return sb.ToString();
        }

        [Test]
        public void IsFileChangeTool_WriteAndEditFamily_CaseInsensitive()
        {
            Assert.IsTrue(ToolCardFileDiff.IsFileChangeTool("Write"));
            Assert.IsTrue(ToolCardFileDiff.IsFileChangeTool("write"));
            Assert.IsTrue(ToolCardFileDiff.IsFileChangeTool("Edit"));
            Assert.IsTrue(ToolCardFileDiff.IsFileChangeTool("MultiEdit"));
            Assert.IsFalse(ToolCardFileDiff.IsFileChangeTool("Read"));
            Assert.IsFalse(ToolCardFileDiff.IsFileChangeTool("Bash"));
            Assert.IsFalse(ToolCardFileDiff.IsFileChangeTool(null));
        }

        [Test]
        public void Write_ContentBecomesAllAddedLines_TrailingNewlineDropped()
        {
            string json = "{\"file_path\":\"Assets/A.cs\",\"content\":\"one\\ntwo\\r\\nthree\\n\"}";
            Assert.AreEqual("+one|+two|+three|", Render(ToolCardFileDiff.BuildLines("Write", json)));
        }

        [Test]
        public void Write_EmptyContent_IsOneEmptyAddedLine()
        {
            string json = "{\"file_path\":\"Assets/A.cs\",\"content\":\"\"}";
            Assert.AreEqual("+|", Render(ToolCardFileDiff.BuildLines("Write", json)));
        }

        [Test]
        public void Write_WithoutContent_YieldsNothing_SoTheCallerFallsBack()
        {
            Assert.IsEmpty(ToolCardFileDiff.BuildLines("Write", "{\"file_path\":\"Assets/A.cs\"}"));
            Assert.IsEmpty(ToolCardFileDiff.BuildLines("Write", "not json"));
            Assert.IsEmpty(ToolCardFileDiff.BuildLines("Write", (string)null));
        }

        [Test]
        public void Edit_DelegatesToTheApprovalDiff()
        {
            string json = "{\"file_path\":\"Assets/A.cs\",\"old_string\":\"a\\nb\",\"new_string\":\"a\\nc\"}";
            Assert.AreEqual(" a|-b|+c|", Render(ToolCardFileDiff.BuildLines("Edit", json)));
        }

        [Test]
        public void NonFileTool_YieldsNothing()
        {
            Assert.IsEmpty(ToolCardFileDiff.BuildLines("Bash", "{\"command\":\"ls\"}"));
        }

        [Test]
        public void FilePathOf_PrefersFilePath_ThenPath()
        {
            JsonNode node;
            Assert.IsTrue(ToolCardFileDiff.TryParse("{\"file_path\":\"A\",\"path\":\"B\"}", out node));
            Assert.AreEqual("A", ToolCardFileDiff.FilePathOf(node));
            Assert.IsTrue(ToolCardFileDiff.TryParse("{\"path\":\"B\"}", out node));
            Assert.AreEqual("B", ToolCardFileDiff.FilePathOf(node));
            Assert.IsTrue(ToolCardFileDiff.TryParse("{}", out node));
            Assert.IsNull(ToolCardFileDiff.FilePathOf(node));
            Assert.IsNull(ToolCardFileDiff.FilePathOf(null));
        }

        [Test]
        public void CopyTextOf_IsTheRawWriteContentOnly()
        {
            JsonNode node;
            Assert.IsTrue(ToolCardFileDiff.TryParse("{\"content\":\"x\\ny\"}", out node));
            Assert.AreEqual("x\ny", ToolCardFileDiff.CopyTextOf("Write", node));
            Assert.IsNull(ToolCardFileDiff.CopyTextOf("Edit", node));
        }

        [Test]
        public void TryParse_RejectsNonObjects()
        {
            JsonNode node;
            Assert.IsFalse(ToolCardFileDiff.TryParse("[1]", out node));
            Assert.IsFalse(ToolCardFileDiff.TryParse("", out node));
        }
    }
}
