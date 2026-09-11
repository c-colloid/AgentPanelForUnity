using System;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// SessionIndex: cwd -&gt; project directory transform, *.jsonl
    /// enumeration against a fake directory tree, mtime ordering and lazy
    /// preview extraction. See
    /// docs/design-notes/2026-07-31-session-history-restore.md for the
    /// evidence behind the transform rule.
    /// </summary>
    public class SessionIndexTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "AgentPanelSessionIndexTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        // ---------------------------------------------------------------
        // cwd -> project directory name transform (real-machine evidence)
        // ---------------------------------------------------------------

        [TestCase(
            "C:\\Unity\\UnityProjects\\DevelopmentProject",
            "C--Unity-UnityProjects-DevelopmentProject")]
        [TestCase(
            "C:\\Users\\colloid\\Dev\\UnityAgentPanel",
            "C--Users-colloid-Dev-UnityAgentPanel")]
        [TestCase(
            "C:\\Unity\\UnityProjects\\AITemp",
            "C--Unity-UnityProjects-AITemp")]
        public void TransformCwdToProjectDirName_MatchesRealObservedDirectories(
            string cwd, string expected)
        {
            // These three expected values were read directly off
            // "%USERPROFILE%\.claude\projects\" on the dev machine (real
            // CLI-created directories), not guessed from the doc prose.
            Assert.AreEqual(expected, SessionIndex.TransformCwdToProjectDirName(cwd));
        }

        [Test]
        public void TransformCwdToProjectDirName_ReplacesEveryNonAsciiAlnumCharacter_NoCollapsing()
        {
            Assert.AreEqual("a-b--c", SessionIndex.TransformCwdToProjectDirName("a/b_-c"));
            Assert.AreEqual("A1-2b", SessionIndex.TransformCwdToProjectDirName("A1.2b"));
        }

        [Test]
        public void TransformCwdToProjectDirName_NonAsciiLettersAreReplacedNotKept()
        {
            // Deliberate: char.IsLetterOrDigit would keep Unicode letters,
            // but this project chose ASCII-only [a-zA-Z0-9] (design note
            // section 1) since that could not be confirmed or refuted
            // against a real non-ASCII path on this machine.
            string cwd = "C:\\Unity\\" + "\u65E5\u672C\u8A9E" + "\\Project"; // ".../<nihongo>/Project"
            string result = SessionIndex.TransformCwdToProjectDirName(cwd);
            Assert.AreEqual("C--Unity-----Project", result);
            StringAssert.DoesNotContain("\u65E5", result);
        }

        [Test]
        public void TransformCwdToProjectDirName_EmptyOrNull_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, SessionIndex.TransformCwdToProjectDirName(string.Empty));
            Assert.AreEqual(string.Empty, SessionIndex.TransformCwdToProjectDirName(null));
        }

        // ---------------------------------------------------------------
        // Refresh: missing directory / enumeration / mtime ordering
        // ---------------------------------------------------------------

        [Test]
        public void Refresh_MissingProjectDirectory_ReturnsEmptyEntriesWithoutThrowing()
        {
            var index = new SessionIndex(_root);
            Assert.DoesNotThrow(delegate { index.Refresh("C:\\Some\\Project\\NeverOpened"); });
            Assert.IsEmpty(index.Entries);
        }

        [Test]
        public void Refresh_MissingRootItself_ReturnsEmptyEntriesWithoutThrowing()
        {
            var index = new SessionIndex(Path.Combine(_root, "does-not-exist-at-all"));
            Assert.DoesNotThrow(delegate { index.Refresh("C:\\Whatever"); });
            Assert.IsEmpty(index.Entries);
        }

        [Test]
        public void Refresh_ListsJsonlFilesOnly_SortedNewestFirst()
        {
            string cwd = "C:\\Unity\\UnityProjects\\DevelopmentProject";
            string dirName = SessionIndex.TransformCwdToProjectDirName(cwd);
            string sessionDir = Path.Combine(_root, dirName);
            Directory.CreateDirectory(sessionDir);

            string oldFile = WriteSessionFile(sessionDir, "11111111-1111-1111-1111-111111111111.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"old\"}]}}");
            Touch(oldFile, DateTime.UtcNow.AddMinutes(-10));

            string midFile = WriteSessionFile(sessionDir, "22222222-2222-2222-2222-222222222222.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"mid\"}]}}");
            Touch(midFile, DateTime.UtcNow.AddMinutes(-5));

            string newFile = WriteSessionFile(sessionDir, "33333333-3333-3333-3333-333333333333.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"new\"}]}}");
            Touch(newFile, DateTime.UtcNow);

            // Non-jsonl noise in the same directory must be ignored.
            File.WriteAllText(Path.Combine(sessionDir, "notes.txt"), "not a session");
            Directory.CreateDirectory(Path.Combine(sessionDir, "subdir"));

            var index = new SessionIndex(_root);
            index.Refresh(cwd);

            Assert.AreEqual(3, index.Entries.Count);
            Assert.AreEqual("33333333-3333-3333-3333-333333333333", index.Entries[0].SessionId);
            Assert.AreEqual("22222222-2222-2222-2222-222222222222", index.Entries[1].SessionId);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111", index.Entries[2].SessionId);
            Assert.Greater(index.Entries[0].SizeBytes, 0);
        }

        [Test]
        public void Refresh_CalledTwice_ReplacesEntriesRatherThanAccumulating()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jsonl", "{}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual(1, index.Entries.Count);

            WriteSessionFile(sessionDir, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.jsonl", "{}");
            index.Refresh(cwd);
            Assert.AreEqual(2, index.Entries.Count, "second Refresh must not keep stacking old entries");
        }

        [Test]
        public void Refresh_EmptyJsonlFile_IsListedWithEmptyPreview()
        {
            // A zero-byte file is a normal (if unusual) state -- e.g. the
            // CLI created the session but exited before writing a single
            // line. It must still be listed, just with no preview text.
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "cccccccc-cccc-cccc-cccc-cccccccccccc.jsonl"), string.Empty);

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual(1, index.Entries.Count);
            Assert.AreEqual(0, index.Entries[0].SizeBytes);
            Assert.AreEqual(string.Empty, index.Entries[0].FirstUserTextPreview);
        }

        // ---------------------------------------------------------------
        // Lazy preview extraction
        // ---------------------------------------------------------------

        [Test]
        public void FirstUserTextPreview_SkipsMetaLinesAndNonUserLines_TruncatesAt80Chars()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            string longText = new string('a', 120);
            WriteSessionFile(sessionDir, "dddddddd-dddd-dddd-dddd-dddddddddddd.jsonl",
                "{\"type\":\"queue-operation\"}",
                "{\"isMeta\":true,\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"skill injected, must be skipped\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"not a user line\"}]}}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\""
                    + longText + "\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual(1, index.Entries.Count);
            string preview = index.Entries[0].FirstUserTextPreview;
            Assert.AreEqual(80, preview.Length);
            Assert.IsTrue(preview.EndsWith("..."), "over-length preview must end with an ellipsis");
        }

        [Test]
        public void FirstUserTextPreview_TakesFirstLineOnly()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"line one\\nline two\\nline three\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual("line one", index.Entries[0].FirstUserTextPreview);
        }

        [Test]
        public void FirstUserTextPreview_NoUserLine_ReturnsEmptyString()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "ffffffff-ffff-ffff-ffff-ffffffffffff.jsonl",
                "{\"type\":\"queue-operation\"}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual(string.Empty, index.Entries[0].FirstUserTextPreview);
        }

        [Test]
        public void FirstUserTextPreview_GarbageFile_ReturnsEmptyStringNeverThrows()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllBytes(
                Path.Combine(sessionDir, "00000000-0000-0000-0000-000000000000.jsonl"),
                new byte[] { 0xFF, 0xFE, 0x00, 0x13, 0x37 });

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            string preview = null;
            Assert.DoesNotThrow(delegate { preview = index.Entries[0].FirstUserTextPreview; });
            Assert.AreEqual(string.Empty, preview);
        }

        [Test]
        public void FirstUserTextPreview_IsComputedLazilyAndCached()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            string filePath = WriteSessionFile(sessionDir, "12121212-1212-1212-1212-121212121212.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"first\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            SessionIndexEntry entry = index.Entries[0];
            Assert.AreEqual("first", entry.FirstUserTextPreview);

            // Mutating the file after the first access must not change the
            // cached preview -- it is computed once per entry instance.
            File.WriteAllText(filePath,
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"changed\"}]}}");
            Assert.AreEqual("first", entry.FirstUserTextPreview);
        }

        // ---------------------------------------------------------------
        // Lazy AiTitle / Cwd extraction (single-scan with FirstUserTextPreview)
        // ---------------------------------------------------------------

        [Test]
        public void TitleCwdAndPreview_AllExtractedFromOneFile()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "a0000000-0000-0000-0000-000000000001.jsonl",
                "{\"type\":\"session-start\",\"cwd\":\"C:\\\\Some\\\\Working\\\\Dir\"}",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Fix the login bug\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"please fix the login bug\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual(1, index.Entries.Count);
            SessionIndexEntry entry = index.Entries[0];
            Assert.AreEqual("Fix the login bug", entry.AiTitle);
            Assert.AreEqual("C:\\Some\\Working\\Dir", entry.Cwd);
            Assert.AreEqual("please fix the login bug", entry.FirstUserTextPreview);
        }

        [Test]
        public void AiTitle_NoAiTitleLine_ReturnsEmptyButPreviewStillWorks()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "a0000000-0000-0000-0000-000000000002.jsonl",
                "{\"type\":\"session-start\",\"cwd\":\"C:\\\\Proj\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"no title here\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            SessionIndexEntry entry = index.Entries[0];
            Assert.AreEqual(string.Empty, entry.AiTitle);
            Assert.AreEqual("C:\\Proj", entry.Cwd);
            Assert.AreEqual("no title here", entry.FirstUserTextPreview);
        }

        [Test]
        public void AiTitleCwdPreview_UnreadableOrMissingFile_ReturnAllEmptyWithoutThrowing()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllBytes(
                Path.Combine(sessionDir, "a0000000-0000-0000-0000-000000000003.jsonl"),
                new byte[] { 0xFF, 0xFE, 0x00, 0x13, 0x37 });

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            SessionIndexEntry entry = index.Entries[0];
            Assert.DoesNotThrow(delegate
            {
                Assert.AreEqual(string.Empty, entry.AiTitle);
                Assert.AreEqual(string.Empty, entry.Cwd);
                Assert.AreEqual(string.Empty, entry.FirstUserTextPreview);
            });
        }

        [Test]
        public void AiTitleCwdPreview_MalformedLineBeforeGoodLines_IsSkipped()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "a0000000-0000-0000-0000-000000000004.jsonl",
                "{ this is not valid json",
                "{\"type\":\"session-start\",\"cwd\":\"C:\\\\Good\\\\Dir\"}",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Good Title\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"good preview text\"}]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            SessionIndexEntry entry = index.Entries[0];
            Assert.AreEqual("Good Title", entry.AiTitle);
            Assert.AreEqual("C:\\Good\\Dir", entry.Cwd);
            Assert.AreEqual("good preview text", entry.FirstUserTextPreview);
        }

        [Test]
        public void AiTitle_PastScanLineCap_ReturnsEmpty()
        {
            // The ai-title line sits at line index 500 (0-based), one past
            // the last line the scan is allowed to look at (PreviewScanLineCap
            // = 500 lines, indices 0..499), so it must never be found.
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);

            var lines = new string[501];
            for (int i = 0; i < 500; i++)
            {
                lines[i] = "{\"type\":\"filler\"}";
            }
            lines[500] = "{\"type\":\"ai-title\",\"aiTitle\":\"Too Late\"}";
            WriteSessionFile(sessionDir, "a0000000-0000-0000-0000-000000000005.jsonl", lines);

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            SessionIndexEntry entry = index.Entries[0];
            Assert.AreEqual(string.Empty, entry.AiTitle);
        }

        // ---------------------------------------------------------------
        // Small helpers
        // ---------------------------------------------------------------

        private static string WriteSessionFile(string dir, string fileName, params string[] lines)
        {
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
            return path;
        }

        private static void Touch(string path, DateTime lastWriteUtc)
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }

        // -- UXIA-5: ModelName from the shared scan -----------------------

        [Test]
        public void ModelName_CapturesFirstRealAssistantModel_SkippingSynthetic()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "abababab-abab-abab-abab-abababababab.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
                "{\"type\":\"assistant\",\"message\":{\"model\":\"<synthetic>\",\"content\":[]}}",
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5\",\"content\":[]}}",
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-sonnet-5\",\"content\":[]}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            // A synthetic model (the CLI's local no-API turns) is not "the
            // model this session ran on"; the first REAL one is -- and a
            // later switch must not overwrite the latched answer.
            Assert.AreEqual("claude-opus-5", index.Entries[0].ModelName);
        }

        [Test]
        public void ModelName_NoAssistantLine_ReturnsEmptyString()
        {
            string cwd = "C:\\Proj";
            string sessionDir = Path.Combine(_root, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(sessionDir);
            WriteSessionFile(sessionDir, "cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd.jsonl",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}");

            var index = new SessionIndex(_root);
            index.Refresh(cwd);
            Assert.AreEqual(string.Empty, index.Entries[0].ModelName);
        }

    }
}
