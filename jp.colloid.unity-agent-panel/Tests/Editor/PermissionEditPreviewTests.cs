using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Line-classification tests for the Edit/MultiEdit approval diff
    /// (UXA-2). Pure logic, no VisualElements -- PermissionCardTests pins
    /// the rendered structure separately.
    /// </summary>
    [TestFixture]
    public class PermissionEditPreviewTests
    {
        private static JsonNode Parse(string json)
        {
            JsonNode node;
            string error;
            Assert.IsTrue(JsonParser.TryParse(json, out node, out error), error);
            return node;
        }

        private static string Render(List<PermissionEditPreview.DiffLine> lines)
        {
            var sb = new System.Text.StringBuilder();
            foreach (PermissionEditPreview.DiffLine line in lines)
            {
                if (sb.Length > 0)
                {
                    sb.Append('|');
                }
                switch (line.Kind)
                {
                    case PermissionEditPreview.LineKind.Remove: sb.Append('-'); break;
                    case PermissionEditPreview.LineKind.Add: sb.Append('+'); break;
                    case PermissionEditPreview.LineKind.Gap: sb.Append('~'); break;
                    case PermissionEditPreview.LineKind.Separator: sb.Append('='); break;
                    default: sb.Append(' '); break;
                }
                sb.Append(line.Text);
            }
            return sb.ToString();
        }

        [Test]
        public void IsEditTool_MatchesEditAndMultiEditOnly()
        {
            Assert.IsTrue(PermissionEditPreview.IsEditTool("Edit"));
            Assert.IsTrue(PermissionEditPreview.IsEditTool("MultiEdit"));
            Assert.IsTrue(PermissionEditPreview.IsEditTool("edit"), "describer table is case-insensitive");
            Assert.IsFalse(PermissionEditPreview.IsEditTool("Write"));
            Assert.IsFalse(PermissionEditPreview.IsEditTool("NotebookEdit"));
            Assert.IsFalse(PermissionEditPreview.IsEditTool(null));
        }

        [Test]
        public void SingleEdit_CommonHeadAndChange_ClassifiesContextRemoveAdd()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"a\\nb\",\"new_string\":\"a\\nc\"}"));

            Assert.AreEqual(" a|-b|+c", Render(lines));
        }

        [Test]
        public void SingleEdit_CommonTail_BecomesTrailingContext()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"x\\nend\",\"new_string\":\"y\\nend\"}"));

            Assert.AreEqual("-x|+y| end", Render(lines));
        }

        [Test]
        public void SingleEdit_PureInsertion_HasNoRemoveLines()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"a\\nz\",\"new_string\":\"a\\nnew\\nz\"}"));

            Assert.AreEqual(" a|+new| z", Render(lines));
        }

        [Test]
        public void SingleEdit_LongCommonHead_IsElidedToContextLinesNearestTheChange()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"l1\\nl2\\nl3\\nl4\\nOLD\",\"new_string\":\"l1\\nl2\\nl3\\nl4\\nNEW\"}"));

            Assert.AreEqual("~...| l3| l4|-OLD|+NEW", Render(lines),
                "context beyond ContextLines must collapse into a gap marker, keeping the nearest lines");
        }

        [Test]
        public void SingleEdit_LongCommonTail_IsElidedAfterContextLines()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"OLD\\nl1\\nl2\\nl3\\nl4\",\"new_string\":\"NEW\\nl1\\nl2\\nl3\\nl4\"}"));

            Assert.AreEqual("-OLD|+NEW| l1| l2|~...", Render(lines));
        }

        [Test]
        public void SingleEdit_CrlfOldVsLfNew_DiffsCleanly()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"a\\r\\nb\",\"new_string\":\"a\\nc\"}"));

            Assert.AreEqual(" a|-b|+c", Render(lines),
                "an invisible trailing CR must never turn a context line into remove+add");
        }

        [Test]
        public void MultiEdit_ConcatenatesEachEditWithASeparator()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"edits\":["
                    + "{\"old_string\":\"a\",\"new_string\":\"b\"},"
                    + "{\"old_string\":\"c\",\"new_string\":\"d\"}]}"));

            Assert.AreEqual("-a|+b|=|-c|+d", Render(lines));
        }

        [Test]
        public void MissingEditShape_ReturnsEmpty_SoTheCallerFallsBack()
        {
            Assert.AreEqual(0, PermissionEditPreview.BuildEditDiff(
                Parse("{\"file_path\":\"a.cs\"}")).Count);
            Assert.AreEqual(0, PermissionEditPreview.BuildEditDiff(
                Parse("{\"edits\":[{\"note\":\"no strings\"}]}")).Count);
            Assert.AreEqual(0, PermissionEditPreview.BuildEditDiff(null).Count);
            Assert.AreEqual(0, PermissionEditPreview.BuildEditDiff(JsonNode.Null).Count);
        }

        [Test]
        public void OldStringOnly_StillDiffs_AsAPureRemoval()
        {
            List<PermissionEditPreview.DiffLine> lines = PermissionEditPreview.BuildEditDiff(
                Parse("{\"old_string\":\"gone\",\"new_string\":\"\"}"));

            // new_string \"\" splits to one empty line; the empty line is
            // common with nothing, so the change reads remove + add-empty.
            Assert.AreEqual("-gone|+", Render(lines));
        }
    }
}
