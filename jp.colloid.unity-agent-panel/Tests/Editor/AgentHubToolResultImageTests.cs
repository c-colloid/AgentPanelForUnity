using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Live-path half of design note 2026-09-12-tool-result-image-preview.md:
    /// a tool_result with an image block reaching AgentHub through the
    /// fake CLI lands the decoded file in the (scratch) attachment store
    /// and on the ToolCallRecord, an array-shaped result still gets its
    /// text summary, and the card renders the thumbnail strip outside the
    /// collapsed details. Same harness as AgentHubAutoContinueAfterCompileTests
    /// (WireClientForTests + FakeCliProcess, no process spawn).
    /// </summary>
    [TestFixture]
    public class AgentHubToolResultImageTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private string _scratch;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "uap-hub-toolimg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            ImageAttachmentStore.SetRootForTests(Path.Combine(_scratch, "store"));
            AgentHub.ResetForTests();
            ToolActivityCard.ResetExpandedStateForTests();
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
            AgentHub.WireClientForTests(_client);
            AgentHub.SetClientForTests(_client);
            _client.Start(new AgentClientOptions { CliPath = "C:/fake/claude.exe", WorkingDirectory = "C:/fake/project" });
            _fake.ScriptLine("{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\"}");
            PumpAll();
        }

        [TearDown]
        public void TearDown()
        {
            AgentHub.SetClientForTests(null);
            _client.Dispose();
            AgentHub.ResetForTests();
            ImageAttachmentStore.SetRootForTests(null);
            try { Directory.Delete(_scratch, true); } catch (Exception) { }
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private void SendToolUse(string toolUseId, string name)
        {
            _fake.ScriptLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\""
                + toolUseId + "\",\"name\":\"" + name + "\",\"input\":{}}]}}");
            PumpAll();
        }

        private void SendToolResultContent(string toolUseId, string contentJson)
        {
            _fake.ScriptLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\""
                + ",\"tool_use_id\":\"" + toolUseId + "\",\"content\":" + contentJson + ",\"is_error\":false}]}}");
            PumpAll();
        }

        private static ToolCallRecord FindRecord(string toolUseId)
        {
            foreach (ChatMessage message in AgentHub.Session.messages)
            {
                foreach (ChatMessageBlock block in message.blocks)
                {
                    if (block.toolCall != null && block.toolCall.toolUseId == toolUseId)
                    {
                        return block.toolCall;
                    }
                }
            }
            return null;
        }

        [Test]
        public void EmbeddedImageBlock_IsSavedToTheStoreAndRecorded()
        {
            SendToolUse("toolu_shot", "mcp__uap__uap_editor_screenshot");
            string data = Convert.ToBase64String(Encoding.ASCII.GetBytes("FAKE-PNG-BYTES"));
            SendToolResultContent("toolu_shot",
                "[{\"type\":\"text\",\"text\":\"Captured Game view to C:/nowhere/x.png (1x1).\"},"
                + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + data + "\"}}]");

            ToolCallRecord record = FindRecord("toolu_shot");
            Assert.IsNotNull(record);
            Assert.AreEqual(ToolCallStatus.Succeeded, record.status);
            Assert.AreEqual(1, record.resultImagePaths.Count);
            string saved = record.resultImagePaths[0];
            Assert.IsTrue(File.Exists(saved), "decoded file must exist: " + saved);
            StringAssert.StartsWith(ImageAttachmentStore.Root.Replace('\\', '/'), saved);
            Assert.AreEqual("FAKE-PNG-BYTES", Encoding.ASCII.GetString(File.ReadAllBytes(saved)));
            StringAssert.StartsWith("Captured Game view", record.resultSummary,
                "an array-shaped result keeps its first text block as the summary");
        }

        [Test]
        public void PathNamedInTextResult_IsRecordedWhenTheFileExists()
        {
            string file = Path.Combine(_scratch, "game_view.png").Replace('\\', '/');
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            SendToolUse("toolu_path", "mcp__uap__uap_editor_screenshot");
            SendToolResultContent("toolu_path", "\"Captured Game view (Camera.main) to " + file + " (1920x1080).\"");

            ToolCallRecord record = FindRecord("toolu_path");
            Assert.IsNotNull(record);
            Assert.AreEqual(new List<string> { file }, record.resultImagePaths);
            Assert.IsFalse(Directory.Exists(ImageAttachmentStore.Root), "a referenced file is never copied into the store");
        }

        [Test]
        public void TextOnlyResult_RecordsNoImages()
        {
            SendToolUse("toolu_text", "Bash");
            SendToolResultContent("toolu_text", "\"total 0\"");

            ToolCallRecord record = FindRecord("toolu_text");
            Assert.IsNotNull(record);
            Assert.IsFalse(record.HasResultImages);
            Assert.AreEqual("total 0", record.resultSummary);
        }

        [Test]
        public void Card_ShowsTheImageStripOutsideTheCollapsedDetails()
        {
            var record = new ToolCallRecord
            {
                toolUseId = "toolu_card",
                toolName = "Read",
                inputJson = "{\"file_path\":\"/p/missing.png\"}",
                status = ToolCallStatus.Succeeded
            };
            record.resultImagePaths.Add(Path.Combine(_scratch, "missing.png"));

            var card = new ToolActivityCard(record);

            VisualElement strip = card.Q<VisualElement>(className: "uap-toolcard-images");
            Assert.IsNotNull(strip, "strip is built for a record with result images");
            Assert.AreEqual(1, strip.childCount);
            Assert.AreNotEqual(DisplayStyle.None, strip.style.display.value, "strip is never hidden behind the chevron");
            Assert.IsFalse(card.IsExpandedForTests, "details stay collapsed; the picture does not depend on them");
            // A path whose file is gone renders the placeholder caption, not an empty box.
            Assert.IsNotNull(strip.Q<Label>(className: "uap-msg-image-missing"));
        }

        [Test]
        public void Card_WithoutImages_BuildsNoStrip()
        {
            var card = new ToolActivityCard(new ToolCallRecord
            {
                toolUseId = "toolu_plain",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded,
                resultSummary = "ok"
            });

            Assert.IsNull(card.Q<VisualElement>(className: "uap-toolcard-images"));
        }
    }
}
