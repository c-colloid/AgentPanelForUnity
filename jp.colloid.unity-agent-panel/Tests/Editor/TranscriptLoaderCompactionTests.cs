using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// History restore across a compaction (design note docs/design-notes/
    /// 2026-09-07-slash-commands-and-compaction.md section 2.4): the
    /// on-disk compact_boundary line becomes the same note the live path
    /// writes, the CLI-authored isCompactSummary user line never becomes
    /// a user bubble (nor a History preview), and assistant text after
    /// the boundary starts a fresh bubble.
    /// </summary>
    public class TranscriptLoaderCompactionTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelTranscriptLoaderCompactionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            // The seam Integration installs at editor load; pinned here so
            // the test does not depend on that having run first.
            TranscriptLoader.CompactionDescriber = CompactionNote.Describe;
        }

        [TearDown]
        public void TearDown()
        {
            TranscriptLoader.CompactionDescriber = CompactionNote.Describe;
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private string WriteFile(params string[] lines)
        {
            string path = Path.Combine(_dir, "session.jsonl");
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
            return path;
        }

        private const string UserLine =
            "{\"type\":\"user\",\"timestamp\":\"2026-09-07T10:00:00.000Z\",\"message\":{\"role\":\"user\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"rename the player script\"}]}}";

        private const string AssistantBefore =
            "{\"type\":\"assistant\",\"timestamp\":\"2026-09-07T10:00:01.000Z\",\"message\":{\"id\":\"m1\","
            + "\"model\":\"claude-sonnet-5\",\"content\":[{\"type\":\"text\",\"text\":\"before\"}],"
            + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

        private const string BoundaryLine =
            "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"content\":\"Conversation compacted\","
            + "\"isMeta\":false,\"timestamp\":\"2026-09-07T10:00:02.000Z\","
            + "\"compactMetadata\":{\"trigger\":\"manual\",\"preTokens\":84213}}";

        private const string CompactSummaryLine =
            "{\"type\":\"user\",\"isCompactSummary\":true,\"timestamp\":\"2026-09-07T10:00:03.000Z\","
            + "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":"
            + "\"This session is being continued from a previous conversation that ran out of context.\"}]}}";

        private const string AssistantAfter =
            "{\"type\":\"assistant\",\"timestamp\":\"2026-09-07T10:00:04.000Z\",\"message\":{\"id\":\"m2\","
            + "\"model\":\"claude-sonnet-5\",\"content\":[{\"type\":\"text\",\"text\":\"after\"}],"
            + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}";

        [Test]
        public void Load_CompactBoundary_BecomesTheLiveNote_SummaryLineVanishes_BubbleSplits()
        {
            List<ChatMessage> messages = TranscriptLoader.Load(
                WriteFile(UserLine, AssistantBefore, BoundaryLine, CompactSummaryLine, AssistantAfter));

            Assert.AreEqual(4, messages.Count,
                "user, assistant(before), compaction note, assistant(after) -- no summary bubble");
            Assert.AreEqual(ChatMessage.RoleUser, messages[0].role);
            Assert.AreEqual(ChatMessage.RoleAssistant, messages[1].role);
            Assert.AreEqual("before", messages[1].blocks[0].text);

            ChatMessage note = messages[2];
            Assert.AreEqual(ChatMessage.RoleSystem, note.role);
            Assert.AreEqual(ChatBlockKind.SystemNote, note.blocks[0].kind);
            Assert.AreEqual(CompactionNote.Describe("manual", 84213), note.blocks[0].text,
                "restore must word the boundary exactly like the live path");
            Assert.AreEqual("2026-09-07T10:00:02.000Z", note.timestamp);

            Assert.AreEqual(ChatMessage.RoleAssistant, messages[3].role);
            Assert.AreEqual("after", messages[3].blocks[0].text,
                "assistant text after the boundary must not merge into the pre-boundary bubble");

            foreach (ChatMessage message in messages)
            {
                if (message.role == ChatMessage.RoleUser)
                {
                    StringAssert.DoesNotContain("continued from a previous conversation", message.blocks[0].text);
                }
            }
        }

        [Test]
        public void Load_CompactSummary_DoesNotCountAsAPrompt()
        {
            TranscriptUsage usage;
            TranscriptLoader.Load(WriteFile(UserLine, BoundaryLine, CompactSummaryLine, AssistantAfter), out usage);
            Assert.AreEqual(1, usage.UserPromptCount, "the CLI-authored summary is not a user prompt");
        }

        [Test]
        public void Load_BoundaryWithoutMetadata_StillNotes()
        {
            List<ChatMessage> messages = TranscriptLoader.Load(WriteFile(
                UserLine, "{\"type\":\"system\",\"subtype\":\"compact_boundary\"}", AssistantAfter));
            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual(ChatMessage.RoleSystem, messages[1].role);
            Assert.AreEqual(CompactionNote.Describe(string.Empty, -1), messages[1].blocks[0].text);
        }

        [Test]
        public void Load_WithoutADescriber_FallsBackToPlainEnglish()
        {
            TranscriptLoader.CompactionDescriber = null;
            List<ChatMessage> messages = TranscriptLoader.Load(WriteFile(UserLine, BoundaryLine, AssistantAfter));
            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual("Conversation compacted (84.2k tokens before).", messages[1].blocks[0].text);
        }

        [Test]
        public void Load_OtherSystemSubtypes_StillIgnored()
        {
            List<ChatMessage> messages = TranscriptLoader.Load(WriteFile(
                UserLine, "{\"type\":\"system\",\"subtype\":\"status\",\"content\":\"x\"}", AssistantAfter));
            Assert.AreEqual(2, messages.Count);
        }

        [Test]
        public void SessionIndex_Preview_SkipsTheCompactSummary()
        {
            string root = Path.Combine(_dir, "projects");
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee.jsonl"),
                string.Join("\n", new[] { BoundaryLine, CompactSummaryLine, UserLine }),
                new UTF8Encoding(false));

            var index = new SessionIndex(root);
            index.Refresh(cwd);
            Assert.AreEqual(1, index.Entries.Count);
            Assert.AreEqual("rename the player script", index.Entries[0].FirstUserTextPreview,
                "the summary the CLI wrote must never become a session's preview");
        }
    }
}
