using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guard for docs/design-notes/2026-11-28-lazy-tool-card-
    /// details.md: what a collapsed card hides is not built until it is
    /// opened, and a transcript picture is not decoded by the code that
    /// builds its row. Both used to be paid for every card of every live
    /// row on each reload and each row rebuild (one row = one whole turn).
    /// </summary>
    public class LazyCardContentTests
    {
        private string _scratch;
        private bool _originalSubagentDefaultExpanded;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "uap-lazy-card-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            ToolActivityCard.ResetExpandedStateForTests();
            SubagentCard.ResetExpandedStateForTests();
            ExpandStateMemory.ResetForTests();
            DeferredThumbnails.ClearForTests();
            _originalSubagentDefaultExpanded = PanelStateStore.instance.Settings.subagentDefaultExpanded;
            PanelStateStore.instance.Settings.subagentDefaultExpanded = false;
        }

        [TearDown]
        public void TearDown()
        {
            DeferredThumbnails.TreatDetachedAsLiveForTests = false;
            DeferredThumbnails.ClearForTests();
            ToolActivityCard.ResetExpandedStateForTests();
            SubagentCard.ResetExpandedStateForTests();
            ExpandStateMemory.ResetForTests();
            PanelStateStore.instance.Settings.subagentDefaultExpanded = _originalSubagentDefaultExpanded;
            try { Directory.Delete(_scratch, true); } catch (Exception) { }
        }

        private static ToolCallRecord MakeTool(string id)
        {
            return new ToolCallRecord
            {
                toolUseId = id,
                toolName = "Bash",
                inputJson = "{\"command\":\"ls\"}",
                resultSummary = "ok",
                status = ToolCallStatus.Succeeded
            };
        }

        // -- Tool card ---------------------------------------------------------------

        [Test]
        public void ToolCard_Collapsed_BuildsNoDetails()
        {
            var card = new ToolActivityCard(MakeTool("toolu_lazy_1"));

            Assert.IsFalse(card.DetailsBuiltForTests);
            Assert.IsNull(card.Q<VisualElement>(className: "uap-toolcard-details"));
        }

        [Test]
        public void ToolCard_FirstExpand_BuildsDetails_AndCollapseKeepsThem()
        {
            var card = new ToolActivityCard(MakeTool("toolu_lazy_2"));

            card.ToggleExpandedForTests();
            VisualElement details = card.Q<VisualElement>(className: "uap-toolcard-details");
            Assert.IsNotNull(details);
            Assert.AreEqual(DisplayStyle.Flex, details.style.display.value);
            Assert.AreSame(details, card[card.childCount - 1], "details stay the card's last child");

            card.ToggleExpandedForTests();
            Assert.AreEqual(DisplayStyle.None, details.style.display.value);
            Assert.AreSame(details, card.Q<VisualElement>(className: "uap-toolcard-details"),
                "collapsing hides the pane, it is not built twice");
        }

        [Test]
        public void ToolCard_RememberedExpanded_IsBuiltOpen()
        {
            ToolCallRecord record = MakeTool("toolu_lazy_3");
            new ToolActivityCard(record).ToggleExpandedForTests();

            var rebuilt = new ToolActivityCard(record);

            Assert.IsTrue(rebuilt.IsExpandedForTests);
            Assert.IsTrue(rebuilt.DetailsBuiltForTests);
        }

        // -- Group row ---------------------------------------------------------------

        [Test]
        public void GroupRow_Closed_BuildsNoCards_OpenBuildsAll()
        {
            var records = new List<ToolCallRecord> { MakeTool("g_a"), MakeTool("g_b"), MakeTool("g_c") };

            VisualElement closed = ToolActivityCard.CreateGroupRow(records, "m/0");
            Assert.AreEqual(0, closed.Query<ToolActivityCard>().ToList().Count);

            ExpandStateMemory.Set("m/0", true);
            VisualElement open = ToolActivityCard.CreateGroupRow(records, "m/0");
            Assert.AreEqual(3, open.Query<ToolActivityCard>().ToList().Count);
        }

        // -- Subagent card -----------------------------------------------------------

        [Test]
        public void SubagentCard_Collapsed_LeavesDetailsEmpty_ExpandFillsThem()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_sub_lazy",
                toolName = "Agent",
                status = ToolCallStatus.Succeeded,
                subagent = new SubagentRecord
                {
                    toolUseId = "toolu_sub_lazy",
                    status = "completed",
                    subagentType = "general-purpose",
                    description = "desc",
                    summaryMarkdown = "done"
                }
            };

            var card = new SubagentCard(record);
            VisualElement details = card.Q<VisualElement>(className: "uap-subcard-details");
            Assert.IsNotNull(details);
            Assert.AreEqual(0, details.childCount);

            card.ToggleExpandedForTests();
            Assert.Greater(details.childCount, 0);
        }

        // -- Transcript pictures -----------------------------------------------------

        private string WritePng(string name)
        {
            var texture = new Texture2D(4, 3, TextureFormat.RGBA32, false);
            string path = Path.Combine(_scratch, name).Replace('\\', '/');
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            return path;
        }

        [Test]
        public void ImageBlock_IsBuiltWithoutDecoding_AndFilledByThePump()
        {
            string path = WritePng("shot.png");
            DeferredThumbnails.TreatDetachedAsLiveForTests = true;

            VisualElement block = MessageBlockFactory.CreateImageBlock(ChatMessageBlock.MakeImage(path, "shot.png"));

            Image image = block.Q<Image>(className: "uap-msg-image-thumb");
            Assert.IsNotNull(image, "an existing file gets its box at once");
            Assert.IsNull(image.image, "the row builder does not decode");
            Assert.IsNull(ImageThumbnailCache.Peek(path));
            Assert.AreEqual(1, DeferredThumbnails.PendingCount);

            DeferredThumbnails.PumpOnce(0);

            Assert.IsNotNull(image.image);
            Assert.AreEqual(0, DeferredThumbnails.PendingCount);
            StringAssert.Contains("4", block.Q<Label>(className: "uap-msg-image-caption").text);

            // Already decoded: the next block for the same file is filled at once.
            VisualElement again = MessageBlockFactory.CreateImageBlock(ChatMessageBlock.MakeImage(path, "shot.png"));
            Assert.IsNotNull(again.Q<Image>(className: "uap-msg-image-thumb").image);
            Assert.AreEqual(0, DeferredThumbnails.PendingCount);
            ImageThumbnailCache.Invalidate(path);
        }

        [Test]
        public void ImageBlock_ThrownAwayBeforeItsTurn_IsNeverDecoded()
        {
            string path = WritePng("gone.png");
            DeferredThumbnails.TreatDetachedAsLiveForTests = true;
            MessageBlockFactory.CreateImageBlock(ChatMessageBlock.MakeImage(path, "gone.png"));
            Assert.AreEqual(1, DeferredThumbnails.PendingCount);

            // No panel, as for a row that was rebuilt or released.
            DeferredThumbnails.TreatDetachedAsLiveForTests = false;
            DeferredThumbnails.PumpOnce(0);

            Assert.AreEqual(0, DeferredThumbnails.PendingCount);
            Assert.IsNull(ImageThumbnailCache.Peek(path));
        }

        [Test]
        public void Pump_DecodesOnePicturePerTick_WhenOverBudget()
        {
            DeferredThumbnails.TreatDetachedAsLiveForTests = true;
            string first = WritePng("a.png");
            string second = WritePng("b.png");
            MessageBlockFactory.CreateImageBlock(ChatMessageBlock.MakeImage(first, "a.png"));
            MessageBlockFactory.CreateImageBlock(ChatMessageBlock.MakeImage(second, "b.png"));

            DeferredThumbnails.PumpOnce(0);
            Assert.AreEqual(1, DeferredThumbnails.PendingCount, "a zero budget still decodes one, and only one");
            DeferredThumbnails.PumpOnce(0);
            Assert.AreEqual(0, DeferredThumbnails.PendingCount);
            ImageThumbnailCache.Invalidate(first);
            ImageThumbnailCache.Invalidate(second);
        }

        [Test]
        public void ImageBlock_MissingFile_ShowsThePlaceholder()
        {
            VisualElement block = MessageBlockFactory.CreateImageBlock(
                ChatMessageBlock.MakeImage(Path.Combine(_scratch, "nope.png"), "nope.png"));

            Assert.IsNotNull(block.Q<Label>(className: "uap-msg-image-missing"));
            Assert.IsNull(block.Q<Image>(className: "uap-msg-image-thumb"));
            Assert.AreEqual(0, DeferredThumbnails.PendingCount);
        }
    }
}
