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
    /// P6 of the 2026-09-07 design note: history restore turns the JSONL's
    /// user image blocks back into Image blocks through the injected
    /// saver, keeps text order, and skips anything it cannot decode.
    /// </summary>
    [TestFixture]
    public class TranscriptLoaderImageTests
    {
        private string _dir;
        private readonly List<KeyValuePair<byte[], string>> _saved = new List<KeyValuePair<byte[], string>>();

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uap-transcript-img-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _saved.Clear();
            TranscriptLoader.ImageSaver = (bytes, media) =>
            {
                _saved.Add(new KeyValuePair<byte[], string>(bytes, media));
                return "/store/" + _saved.Count + (media == "image/jpeg" ? ".jpg" : ".png");
            };
        }

        [TearDown]
        public void TearDown()
        {
            TranscriptLoader.ImageSaver = null;
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private string Write(string line)
        {
            string path = Path.Combine(_dir, "t.jsonl");
            File.WriteAllText(path, line + "\n", new UTF8Encoding(false));
            return path;
        }

        private static string UserLine(params JsonNode[] blocks)
        {
            JsonNode content = JsonNode.NewArray();
            foreach (JsonNode b in blocks) content.Add(b);
            return JsonWriter.Write(JsonNode.NewObject().Set("type", "user").Set("uuid", "u1").Set("timestamp", "2026-09-07T00:00:00Z")
                .Set("message", JsonNode.NewObject().Set("role", "user").Set("content", content)));
        }

        private static JsonNode Text(string t)
        {
            return JsonNode.NewObject().Set("type", "text").Set("text", t);
        }

        private static JsonNode Image(string base64, string media = "image/png")
        {
            return JsonNode.NewObject().Set("type", "image").Set("source",
                JsonNode.NewObject().Set("type", "base64").Set("media_type", media).Set("data", base64));
        }

        [Test]
        public void UserImageBlocks_BecomeImageBlocks_AfterTheText()
        {
            string b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
            List<ChatMessage> messages = TranscriptLoader.Load(Write(UserLine(Text("look"), Image(b64), Image(b64, "image/jpeg"))));

            Assert.AreEqual(1, messages.Count);
            ChatMessage user = messages[0];
            Assert.AreEqual(ChatMessage.RoleUser, user.role);
            Assert.AreEqual(3, user.blocks.Count);
            Assert.AreEqual(ChatBlockKind.Text, user.blocks[0].kind);
            Assert.AreEqual(ChatBlockKind.Image, user.blocks[1].kind);
            Assert.AreEqual("/store/1.png", user.blocks[1].text);
            Assert.AreEqual("/store/2.jpg", user.blocks[2].text);
            Assert.AreEqual(2, _saved.Count);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, _saved[0].Key);
            Assert.AreEqual("image/jpeg", _saved[1].Value);
        }

        [Test]
        public void ImageOnlyMessage_StillCreatesTheUserBubble()
        {
            List<ChatMessage> messages = TranscriptLoader.Load(Write(UserLine(Image(Convert.ToBase64String(new byte[] { 9 })))));
            Assert.AreEqual(1, messages.Count);
            Assert.AreEqual(1, messages[0].blocks.Count);
            Assert.AreEqual(ChatBlockKind.Image, messages[0].blocks[0].kind);
        }

        [Test]
        public void UndecodableOrForeignImages_AreSkipped_NotFatal()
        {
            JsonNode urlImage = JsonNode.NewObject().Set("type", "image").Set("source",
                JsonNode.NewObject().Set("type", "url").Set("url", "https://x/y.png"));
            List<ChatMessage> messages = TranscriptLoader.Load(Write(UserLine(Text("t"), Image("not base64!!"), urlImage, Image(""))));

            Assert.AreEqual(1, messages.Count);
            Assert.AreEqual(1, messages[0].blocks.Count, "only the text survives");
            Assert.AreEqual(0, _saved.Count);
        }

        [Test]
        public void NoSaver_SkipsImagesSilently()
        {
            TranscriptLoader.ImageSaver = null;
            List<ChatMessage> messages = TranscriptLoader.Load(Write(UserLine(Text("t"), Image(Convert.ToBase64String(new byte[] { 1 })))));
            Assert.AreEqual(1, messages[0].blocks.Count);
        }

        [Test]
        public void SaverThrowing_SkipsThatImage()
        {
            TranscriptLoader.ImageSaver = (b, m) => { throw new IOException("disk full"); };
            List<ChatMessage> messages = TranscriptLoader.Load(Write(UserLine(Text("t"), Image(Convert.ToBase64String(new byte[] { 1 })))));
            Assert.AreEqual(1, messages[0].blocks.Count);
        }
    }
}
