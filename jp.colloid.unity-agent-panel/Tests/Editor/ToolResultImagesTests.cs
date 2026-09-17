using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design note 2026-09-12-tool-result-image-preview.md: the pure
    /// extractor (both embedded wire shapes, text-named paths, the cap and
    /// the dedupe rule), the session-cache round trip of the recorded
    /// paths, and the history restore of a tool_result that carried a
    /// picture. No Unity objects: the saver and the path resolver are
    /// fakes, so every branch runs without a store or an editor.
    /// </summary>
    [TestFixture]
    public class ToolResultImagesTests
    {
        private readonly List<KeyValuePair<byte[], string>> _saved = new List<KeyValuePair<byte[], string>>();
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _saved.Clear();
            _dir = Path.Combine(Path.GetTempPath(), "uap-toolimg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            TranscriptLoader.ImageSaver = null;
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private string FakeSave(byte[] bytes, string mediaType)
        {
            _saved.Add(new KeyValuePair<byte[], string>(bytes, mediaType));
            return "/store/" + _saved.Count + (mediaType == "image/jpeg" ? ".jpg" : ".png");
        }

        private static string ExistsOnly(string candidate)
        {
            return File.Exists(candidate) ? candidate : null;
        }

        private static string Base64(string ascii)
        {
            return Convert.ToBase64String(Encoding.ASCII.GetBytes(ascii));
        }

        private static JsonNode ApiImage(string mediaType, string data)
        {
            return JsonNode.NewObject().Set("type", "image").Set("source", JsonNode.NewObject()
                .Set("type", "base64").Set("media_type", mediaType).Set("data", data));
        }

        private static JsonNode McpImage(string mimeType, string data)
        {
            return JsonNode.NewObject().Set("type", "image").Set("data", data).Set("mimeType", mimeType);
        }

        private static JsonNode Text(string text)
        {
            return JsonNode.NewObject().Set("type", "text").Set("text", text);
        }

        // -- Embedded blocks ---------------------------------------------------------

        [Test]
        public void Resolve_ApiShapedBlock_SavesBytesWithMediaType()
        {
            JsonNode content = JsonNode.NewArray().Add(Text("Captured")).Add(ApiImage("image/png", Base64("PNGDATA")));

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(new[] { "/store/1.png" }, paths);
            Assert.AreEqual(1, _saved.Count);
            Assert.AreEqual("PNGDATA", Encoding.ASCII.GetString(_saved[0].Key));
            Assert.AreEqual("image/png", _saved[0].Value);
        }

        [Test]
        public void Resolve_McpShapedBlock_NormalizesJpgMimeType()
        {
            JsonNode content = JsonNode.NewArray().Add(McpImage("image/jpg", Base64("JPEGDATA")));

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(new[] { "/store/1.jpg" }, paths);
            Assert.AreEqual("image/jpeg", _saved[0].Value);
        }

        [Test]
        public void Resolve_SkipsUndecodableBlocksAndKeepsTheRest()
        {
            JsonNode content = JsonNode.NewArray()
                .Add(ApiImage("image/gif", Base64("GIF")))          // unsupported media type
                .Add(ApiImage("image/png", "%%%not-base64%%%"))     // invalid payload
                .Add(ApiImage("image/png", string.Empty))            // empty payload
                .Add(JsonNode.NewObject().Set("type", "image").Set("source", JsonNode.NewObject()
                    .Set("type", "url").Set("url", "https://x/y.png"))) // url source
                .Add(ApiImage("image/png", Base64("GOOD")));

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(new[] { "/store/1.png" }, paths);
            Assert.AreEqual("GOOD", Encoding.ASCII.GetString(_saved[0].Key));
        }

        [Test]
        public void Resolve_CapsAtMaxImagesPerResult()
        {
            JsonNode content = JsonNode.NewArray();
            for (int i = 0; i < ToolResultImages.MaxImagesPerResult + 3; i++)
            {
                content.Add(ApiImage("image/png", Base64("IMG" + i)));
            }

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(ToolResultImages.MaxImagesPerResult, paths.Count);
            Assert.AreEqual(ToolResultImages.MaxImagesPerResult, _saved.Count, "no decode past the cap");
        }

        [Test]
        public void Resolve_SaverThrowing_SkipsThatImageOnly()
        {
            int calls = 0;
            Func<byte[], string, string> flaky = (bytes, media) =>
            {
                calls++;
                if (calls == 1) throw new IOException("disk full");
                return "/store/ok.png";
            };
            JsonNode content = JsonNode.NewArray()
                .Add(ApiImage("image/png", Base64("A")))
                .Add(ApiImage("image/png", Base64("B")));

            List<string> paths = ToolResultImages.Resolve(content, flaky, ExistsOnly);

            Assert.AreEqual(new[] { "/store/ok.png" }, paths);
        }

        [Test]
        public void Resolve_NullSaver_IgnoresEmbeddedButStillScansText()
        {
            string file = Path.Combine(_dir, "shot.png").Replace('\\', '/');
            File.WriteAllBytes(file, new byte[] { 1 });
            JsonNode content = JsonNode.NewArray()
                .Add(Text("Captured Game view to " + file + " (640x480)."))
                .Add(ApiImage("image/png", Base64("X")));

            List<string> paths = ToolResultImages.Resolve(content, null, ExistsOnly);

            Assert.AreEqual(new[] { file }, paths);
        }

        [Test]
        public void Resolve_NullOrMissingContent_IsEmpty()
        {
            Assert.IsEmpty(ToolResultImages.Resolve(null, FakeSave, ExistsOnly));
            Assert.IsEmpty(ToolResultImages.Resolve(JsonNode.NewObject()["absent"], FakeSave, ExistsOnly));
            Assert.IsEmpty(ToolResultImages.Resolve(JsonNode.Of("plain text without a path"), FakeSave, ExistsOnly));
        }

        // -- Paths named in the text ----------------------------------------------------

        [Test]
        public void Resolve_StringContentNamingAnExistingFile_ReturnsIt()
        {
            string file = Path.Combine(_dir, "game_20260912.png").Replace('\\', '/');
            File.WriteAllBytes(file, new byte[] { 1 });
            JsonNode content = JsonNode.Of("Captured Game view (Camera.main) to " + file + " (1920x1080).\nNote: offscreen render.");

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(new[] { file }, paths);
            Assert.IsEmpty(_saved, "a path is referenced, never copied into the store");
        }

        [Test]
        public void Resolve_PathThatDoesNotExist_IsSkipped()
        {
            JsonNode content = JsonNode.Of("Wrote /nowhere/" + Guid.NewGuid().ToString("N") + ".png");

            Assert.IsEmpty(ToolResultImages.Resolve(content, FakeSave, ExistsOnly));
        }

        [Test]
        public void Resolve_EmbeddedImagePresent_SkipsThePathTheTextNames()
        {
            // uap_editor_screenshot return_image:true names the file AND
            // embeds it; the card must show the picture once.
            string file = Path.Combine(_dir, "scene.png").Replace('\\', '/');
            File.WriteAllBytes(file, new byte[] { 1 });
            JsonNode content = JsonNode.NewArray()
                .Add(Text("Captured Scene view to " + file + " (800x600)."))
                .Add(ApiImage("image/png", Base64("SHOT")));

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(new[] { "/store/1.png" }, paths);
        }

        [Test]
        public void Resolve_PathWithSpaces_FoundByTheLenientPass()
        {
            string spaced = Path.Combine(_dir, "My Project");
            Directory.CreateDirectory(spaced);
            string file = Path.Combine(spaced, "shot.png").Replace('\\', '/');
            File.WriteAllBytes(file, new byte[] { 1 });
            JsonNode content = JsonNode.Of("Saved to " + file + " just now.");

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, ExistsOnly);

            Assert.AreEqual(new[] { file }, paths);
        }

        [Test]
        public void Resolve_RelativeCandidate_GoesThroughTheResolver()
        {
            string absolute = Path.Combine(_dir, "gen.jpg").Replace('\\', '/');
            File.WriteAllBytes(absolute, new byte[] { 1 });
            Func<string, string> projectRelative = c => c.StartsWith("Assets/", StringComparison.Ordinal)
                ? absolute : null;
            JsonNode content = JsonNode.Of("Generated Assets/Textures/gen.jpg (512x512)");

            List<string> paths = ToolResultImages.Resolve(content, FakeSave, projectRelative);

            Assert.AreEqual(new[] { absolute }, paths);
        }

        [Test]
        public void FindImagePaths_StrictShapes()
        {
            List<string> found = ToolResultImages.FindImagePaths(
                "win C:\\proj\\Temp\\a.PNG, unix /home/u/b.jpeg (1x1). quoted \"/q/c.jpg\" rel Assets/d.png"
                + " not-image /e/f.gif partial /g/h.png.bak dup /home/u/b.jpeg");

            Assert.AreEqual(new[] { "C:/proj/Temp/a.PNG", "/home/u/b.jpeg", "/q/c.jpg", "Assets/d.png" }, found);
        }

        /// <summary>
        /// The 2026-09-17 freeze: Codex handed uap_editor_screenshot's MCP
        /// result over as ONE JSON string, base64 picture included. Base64
        /// has a '/' every ~64 characters and no stop character, so the
        /// whole-text regex walked from every '/' to the end of the blob --
        /// 431 s on the main thread for 498 KB. The budget here is three
        /// orders of magnitude under that and still generous for a slow CI
        /// host; a return to the quadratic scan cannot pass it.
        /// </summary>
        [Test]
        public void FindImagePaths_Base64BlobInText_StaysLinear_AndStillFindsThePath()
        {
            var blob = new System.Text.StringBuilder(520000);
            var random = new System.Random(20260917);
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            for (int i = 0; i < 500000; i++)
            {
                blob.Append(alphabet[random.Next(alphabet.Length)]);
            }
            string text = "{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Captured Scene view to"
                + " C:/proj/Temp/UapOpsScreenshots/shot.png (826x430).\"},{\"type\":\"image\",\"data\":\""
                + blob + "\",\"mimeType\":\"image/png\"}]}}";

            var watch = System.Diagnostics.Stopwatch.StartNew();
            List<string> strict = ToolResultImages.FindImagePaths(text, false);
            List<string> lenient = ToolResultImages.FindImagePaths(text, true);
            watch.Stop();

            Assert.AreEqual(new[] { "C:/proj/Temp/UapOpsScreenshots/shot.png" }, strict);
            Assert.Contains("C:/proj/Temp/UapOpsScreenshots/shot.png", lenient);
            Assert.Less(watch.ElapsedMilliseconds, 2000,
                "path scan over a base64-bearing result must stay linear");
        }

        [Test]
        public void FindImagePaths_PathAfterTheBlob_IsFoundToo()
        {
            string text = new string('/', 100000) + " then /home/u/late.jpg";

            Assert.AreEqual(new[] { "/home/u/late.jpg" }, ToolResultImages.FindImagePaths(text, false));
        }

        [Test]
        public void FindImagePaths_LenientPass_KeepsBothPathsOfOneLine()
        {
            List<string> found = ToolResultImages.FindImagePaths(
                "wrote C:/My Project/a.png and D:/Other Dir/b.jpeg today", true);

            Assert.AreEqual(new[] { "C:/My Project/a.png", "D:/Other Dir/b.jpeg" }, found);
        }

        [Test]
        public void FindImagePaths_EmptyText_IsEmpty()
        {
            Assert.IsEmpty(ToolResultImages.FindImagePaths(null));
            Assert.IsEmpty(ToolResultImages.FindImagePaths(string.Empty));
            Assert.IsEmpty(ToolResultImages.FindImagePaths("no paths here"));
        }

        [TestCase("image/png", "image/png")]
        [TestCase("IMAGE/PNG", "image/png")]
        [TestCase("image/jpeg", "image/jpeg")]
        [TestCase("image/jpg", "image/jpeg")]
        [TestCase("image/webp", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void NormalizeMediaType(string input, string expected)
        {
            Assert.AreEqual(expected, ToolResultImages.NormalizeMediaType(input));
        }

        // -- Session cache round trip ----------------------------------------------------

        [Test]
        public void SessionCache_RoundTripsResultImagePaths_AndOmitsTheKeyWhenEmpty()
        {
            string path = Path.Combine(_dir, "SessionCache.json");
            var cache = new SessionCacheFile(path, delegate { });
            var session = new ChatSession();
            var assistant = new ChatMessage { role = ChatMessage.RoleAssistant, turnId = 1 };
            var withImages = new ToolCallRecord
            {
                toolUseId = "toolu_img",
                toolName = "mcp__uap__uap_editor_screenshot",
                status = ToolCallStatus.Succeeded,
                resultSummary = "Captured"
            };
            withImages.resultImagePaths.Add("/store/a.png");
            withImages.resultImagePaths.Add("C:/proj/Temp/b.jpg");
            assistant.Add(ChatMessageBlock.MakeToolCall(withImages));
            assistant.Add(ChatMessageBlock.MakeToolCall(new ToolCallRecord
            {
                toolUseId = "toolu_text",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded
            }));
            session.AddMessage(assistant);

            cache.Save(session);
            string json = File.ReadAllText(path);
            Assert.AreEqual(1, CountOccurrences(json, "\"resultImages\""),
                "only the record with pictures writes the additive key");

            ChatSession loaded = cache.Load();
            ToolCallRecord first = loaded.messages[0].blocks[0].toolCall;
            ToolCallRecord second = loaded.messages[0].blocks[1].toolCall;
            Assert.AreEqual(new[] { "/store/a.png", "C:/proj/Temp/b.jpg" }, first.resultImagePaths);
            Assert.IsTrue(first.HasResultImages);
            Assert.IsNotNull(second.resultImagePaths);
            Assert.IsFalse(second.HasResultImages);
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        // -- History restore ---------------------------------------------------------------

        [Test]
        public void TranscriptLoader_RestoresToolResultImagesThroughTheSaver()
        {
            TranscriptLoader.ImageSaver = FakeSave;
            string jsonl = Path.Combine(_dir, "t.jsonl");
            string toolUse = JsonWriter.Write(JsonNode.NewObject().Set("type", "assistant").Set("uuid", "a1")
                .Set("timestamp", "2026-09-12T00:00:00Z")
                .Set("message", JsonNode.NewObject().Set("role", "assistant").Set("content", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("type", "tool_use").Set("id", "toolu_1").Set("name", "Read")
                        .Set("input", JsonNode.NewObject().Set("file_path", "/p/shot.png"))))));
            string toolResult = JsonWriter.Write(JsonNode.NewObject().Set("type", "user").Set("uuid", "u1")
                .Set("timestamp", "2026-09-12T00:00:01Z")
                .Set("message", JsonNode.NewObject().Set("role", "user").Set("content", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("type", "tool_result").Set("tool_use_id", "toolu_1")
                        .Set("content", JsonNode.NewArray().Add(ApiImage("image/png", Base64("SHOT"))))))));
            File.WriteAllText(jsonl, toolUse + "\n" + toolResult + "\n", new UTF8Encoding(false));

            List<ChatMessage> messages = TranscriptLoader.Load(jsonl);

            ToolCallRecord record = FindToolCall(messages, "toolu_1");
            Assert.IsNotNull(record);
            Assert.AreEqual(ToolCallStatus.Succeeded, record.status);
            Assert.AreEqual(new[] { "/store/1.png" }, record.resultImagePaths);
            Assert.AreEqual("SHOT", Encoding.ASCII.GetString(_saved[0].Key));
        }

        [Test]
        public void TranscriptLoader_WithoutSaver_LeavesAnEmptyListNotNull()
        {
            TranscriptLoader.ImageSaver = null;
            string jsonl = Path.Combine(_dir, "t.jsonl");
            string toolUse = JsonWriter.Write(JsonNode.NewObject().Set("type", "assistant").Set("uuid", "a1")
                .Set("timestamp", "2026-09-12T00:00:00Z")
                .Set("message", JsonNode.NewObject().Set("role", "assistant").Set("content", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("type", "tool_use").Set("id", "toolu_1").Set("name", "Read")
                        .Set("input", JsonNode.NewObject())))));
            string toolResult = JsonWriter.Write(JsonNode.NewObject().Set("type", "user").Set("uuid", "u1")
                .Set("timestamp", "2026-09-12T00:00:01Z")
                .Set("message", JsonNode.NewObject().Set("role", "user").Set("content", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("type", "tool_result").Set("tool_use_id", "toolu_1")
                        .Set("content", JsonNode.NewArray().Add(ApiImage("image/png", Base64("SHOT"))))))));
            File.WriteAllText(jsonl, toolUse + "\n" + toolResult + "\n", new UTF8Encoding(false));

            ToolCallRecord record = FindToolCall(TranscriptLoader.Load(jsonl), "toolu_1");

            Assert.IsNotNull(record);
            Assert.IsNotNull(record.resultImagePaths);
            Assert.IsFalse(record.HasResultImages);
        }

        private static ToolCallRecord FindToolCall(List<ChatMessage> messages, string toolUseId)
        {
            foreach (ChatMessage message in messages)
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
    }
}
