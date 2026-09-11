using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// P1 of the 2026-09-07 design note: the pure policy, the wire shape
    /// (text + image content blocks), the queue round trip, the store's
    /// hash naming and retention, and the encoder against real textures
    /// (CPU resample, PNG/JPEG choice, refusals) in a scratch store.
    /// </summary>
    [TestFixture]
    public class ImageAttachmentTests
    {
        private string _scratch;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "uap-attach-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
            ImageAttachmentStore.SetRootForTests(Path.Combine(_scratch, "store"));
        }

        [TearDown]
        public void TearDown()
        {
            ImageAttachmentStore.SetRootForTests(null);
            try { Directory.Delete(_scratch, true); } catch (Exception) { }
        }

        // -- Policy -------------------------------------------------------------------

        [TestCase("a.png", true)]
        [TestCase("A.JPG", true)]
        [TestCase("x.jpeg", true)]
        [TestCase("x.gif", false)]
        [TestCase("x.webp", false)]
        [TestCase("noext", false)]
        [TestCase("", false)]
        public void IsSupportedFile(string path, bool expected)
        {
            Assert.AreEqual(expected, ImageAttachmentPolicy.IsSupportedFile(path));
        }

        [Test]
        public void FitToLongEdge_CapsAt1568_KeepsAspect_LeavesSmallAlone()
        {
            int w, h;
            ImageAttachmentPolicy.FitToLongEdge(2560, 1440, out w, out h);
            Assert.AreEqual(1568, w);
            Assert.AreEqual(882, h);
            ImageAttachmentPolicy.FitToLongEdge(800, 3136, out w, out h);
            Assert.AreEqual(400, w);
            Assert.AreEqual(1568, h);
            ImageAttachmentPolicy.FitToLongEdge(1568, 100, out w, out h);
            Assert.AreEqual(1568, w);
            Assert.AreEqual(100, h);
        }

        [Test]
        public void Thresholds_JpegFallback_HardCap_StoreName()
        {
            Assert.IsFalse(ImageAttachmentPolicy.ShouldFallBackToJpeg(ImageAttachmentPolicy.JpegFallbackBytes));
            Assert.IsTrue(ImageAttachmentPolicy.ShouldFallBackToJpeg(ImageAttachmentPolicy.JpegFallbackBytes + 1));
            Assert.IsFalse(ImageAttachmentPolicy.IsTooLarge(ImageAttachmentPolicy.MaxEncodedBytes));
            Assert.IsTrue(ImageAttachmentPolicy.IsTooLarge(ImageAttachmentPolicy.MaxEncodedBytes + 1));
            Assert.AreEqual("abcd.png", ImageAttachmentPolicy.BuildStoreFileName("ABCD", "image/png"));
            Assert.AreEqual("abcd.jpg", ImageAttachmentPolicy.BuildStoreFileName("abcd", "image/jpeg"));
            Assert.AreEqual(4, ImageAttachmentPolicy.MaxPerMessage);
            Assert.AreEqual(1843, ImageAttachmentPolicy.EstimateTokens(1568, 882));
        }

        // -- Wire ---------------------------------------------------------------------

        [Test]
        public void UserContent_TextThenImages_SingleLine()
        {
            string line = OutboundMessages.UserContent("hello", new List<OutboundMessages.ImageBlock>
            {
                new OutboundMessages.ImageBlock { MediaType = "image/png", Base64Data = "AAAA" }
            });

            Assert.IsFalse(line.Contains("\n"), "one stdin line");
            JsonNode node = JsonParser.Parse(line);
            JsonNode content = node["message"]["content"];
            Assert.AreEqual(2, content.Count);
            Assert.AreEqual("text", content[0]["type"].AsString());
            Assert.AreEqual("hello", content[0]["text"].AsString());
            Assert.AreEqual("image", content[1]["type"].AsString());
            Assert.AreEqual("base64", content[1]["source"]["type"].AsString());
            Assert.AreEqual("image/png", content[1]["source"]["media_type"].AsString());
            Assert.AreEqual("AAAA", content[1]["source"]["data"].AsString());
        }

        [Test]
        public void UserContent_ImageOnly_OmitsTextBlock_AndUserTextIsUnchanged()
        {
            JsonNode node = JsonParser.Parse(OutboundMessages.UserContent(string.Empty,
                new[] { new OutboundMessages.ImageBlock { MediaType = "image/jpeg", Base64Data = "BB" } }));
            Assert.AreEqual(1, node["message"]["content"].Count);
            Assert.AreEqual("image", node["message"]["content"][0]["type"].AsString());

            JsonNode text = JsonParser.Parse(OutboundMessages.UserText("hi"));
            Assert.AreEqual(1, text["message"]["content"].Count);
            Assert.AreEqual("hi", text["message"]["content"][0]["text"].AsString());
        }

        [Test]
        public void UserContent_NothingToSend_Throws()
        {
            Assert.Throws<ArgumentException>(() => OutboundMessages.UserContent(string.Empty, null));
            Assert.Throws<ArgumentException>(() => OutboundMessages.UserContent("x",
                new[] { new OutboundMessages.ImageBlock { MediaType = "", Base64Data = "AA" } }));
        }

        // -- Model / queue ------------------------------------------------------------

        [Test]
        public void ImageAttachment_JsonRoundTrip_AndRejectsPathless()
        {
            var image = new ImageAttachment { path = "/tmp/a.png", mediaType = "image/jpeg", width = 10, height = 20, bytes = 12345, sourceName = "shot.png" };
            ImageAttachment back = ImageAttachment.FromJson(image.ToJson());
            Assert.AreEqual("/tmp/a.png", back.path);
            Assert.AreEqual("image/jpeg", back.mediaType);
            Assert.AreEqual(10, back.width);
            Assert.AreEqual(20, back.height);
            Assert.AreEqual(12345L, back.bytes);
            Assert.AreEqual("shot.png", back.sourceName);
            Assert.IsNull(ImageAttachment.FromJson(JsonNode.NewObject().Set("mediaType", "image/png")));
        }

        [Test]
        public void CompileGateQueue_RoundTripsImages_NextToAttachments()
        {
            var entry = new CompileGateQueue.Entry
            {
                WireText = "look",
                DisplayText = "look",
                Attachments = new List<ContextAttachment> { new ContextAttachment("t", "p") },
                Images = new List<ImageAttachment> { new ImageAttachment { path = "/x/y.png", sourceName = "y.png", width = 4, height = 4, bytes = 9 } }
            };
            string discard;
            List<CompileGateQueue.Entry> back = CompileGateQueue.Deserialize(CompileGateQueue.Serialize(new[] { entry }), out discard);

            Assert.IsNull(discard);
            Assert.AreEqual(1, back.Count);
            Assert.AreEqual(1, back[0].Attachments.Count);
            Assert.AreEqual(1, back[0].Images.Count);
            Assert.AreEqual("/x/y.png", back[0].Images[0].path);
            Assert.AreEqual("y.png", back[0].Images[0].sourceName);
        }

        [Test]
        public void CompileGateQueue_EntryWithoutImages_HasNullImages()
        {
            string discard;
            var back = CompileGateQueue.Deserialize(CompileGateQueue.Serialize(new[] { new CompileGateQueue.Entry { WireText = "w" } }), out discard);
            Assert.IsNull(back[0].Images);
        }

        [Test]
        public void ChatMessageBlock_MakeImage_KindPathCaption()
        {
            ChatMessageBlock block = ChatMessageBlock.MakeImage("/p.png", "p.png");
            Assert.AreEqual(ChatBlockKind.Image, block.kind);
            Assert.AreEqual("/p.png", block.text);
            Assert.AreEqual("p.png", block.title);
        }

        // -- Store --------------------------------------------------------------------

        [Test]
        public void Store_SavesUnderContentHash_Dedupes_AndCleansOldFiles()
        {
            byte[] bytes = { 1, 2, 3, 4 };
            string first = ImageAttachmentStore.Save(bytes, "image/png");
            string second = ImageAttachmentStore.Save(bytes, "image/png");

            Assert.AreEqual(first, second);
            StringAssert.EndsWith(ImageAttachmentStore.Sha1Hex(bytes) + ".png", first);
            Assert.IsTrue(File.Exists(first));
            Assert.AreEqual(1, Directory.GetFiles(ImageAttachmentStore.Root).Length);

            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-ImageAttachmentStore.RetentionDays - 1));
            Assert.AreEqual(1, ImageAttachmentStore.Cleanup(DateTime.UtcNow));
            Assert.IsFalse(File.Exists(first));
        }

        // -- Encoder ------------------------------------------------------------------

        private static Texture2D MakeTexture(int width, int height, Color32 fill)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill;
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        [Test]
        public void Encoder_SmallTexture_StaysPng_AtNativeSize()
        {
            Texture2D texture = MakeTexture(64, 32, new Color32(255, 0, 0, 255));
            try
            {
                ImageAttachment image;
                string error;
                Assert.IsTrue(ImageAttachmentEncoder.TryImportTexture(texture, "red.png", out image, out error), error);
                Assert.AreEqual(64, image.width);
                Assert.AreEqual(32, image.height);
                Assert.AreEqual("image/png", image.mediaType);
                Assert.AreEqual("red.png", image.sourceName);
                Assert.IsTrue(File.Exists(image.path));
                Assert.AreEqual(new FileInfo(image.path).Length, image.bytes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void Encoder_LargeTexture_IsDownscaledToTheLongEdge()
        {
            Texture2D texture = MakeTexture(3136, 1568, new Color32(0, 200, 40, 255));
            try
            {
                ImageAttachment image;
                string error;
                Assert.IsTrue(ImageAttachmentEncoder.TryImportTexture(texture, "big", out image, out error), error);
                Assert.AreEqual(1568, image.width);
                Assert.AreEqual(784, image.height);
                Texture2D check = new Texture2D(2, 2);
                Assert.IsTrue(ImageConversion.LoadImage(check, File.ReadAllBytes(image.path)));
                Assert.AreEqual(1568, check.width);
                Color32 px = check.GetPixels32()[0];
                Assert.AreEqual(0, px.r); Assert.AreEqual(200, px.g); Assert.AreEqual(40, px.b);
                UnityEngine.Object.DestroyImmediate(check);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void Encoder_ImportFile_RoundTripsAPngFromDisk_AndRefusesOthers()
        {
            Texture2D texture = MakeTexture(8, 8, new Color32(10, 20, 30, 255));
            string png = Path.Combine(_scratch, "src.PNG");
            File.WriteAllBytes(png, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllText(Path.Combine(_scratch, "notes.txt"), "x");
            File.WriteAllBytes(Path.Combine(_scratch, "broken.png"), new byte[] { 1, 2, 3 });

            ImageAttachment image;
            string error;
            Assert.IsTrue(ImageAttachmentEncoder.TryImportFile(png, out image, out error), error);
            Assert.AreEqual("src.PNG", image.sourceName);
            Assert.AreEqual(8, image.width);

            Assert.IsFalse(ImageAttachmentEncoder.TryImportFile(Path.Combine(_scratch, "notes.txt"), out image, out error));
            StringAssert.Contains("PNG", error);
            Assert.IsFalse(ImageAttachmentEncoder.TryImportFile(Path.Combine(_scratch, "missing.png"), out image, out error));
            Assert.IsFalse(ImageAttachmentEncoder.TryImportFile(Path.Combine(_scratch, "broken.png"), out image, out error));
        }

        [Test]
        public void Encoder_Resize_BilinearKeepsSolidColour()
        {
            Texture2D texture = MakeTexture(20, 10, new Color32(100, 150, 200, 255));
            Texture2D small = ImageAttachmentEncoder.Resize(texture, 7, 3);
            try
            {
                Assert.AreEqual(7, small.width);
                Assert.AreEqual(3, small.height);
                foreach (Color32 px in small.GetPixels32())
                {
                    Assert.AreEqual(100, px.r); Assert.AreEqual(150, px.g); Assert.AreEqual(200, px.b);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(small);
            }
        }
    }
}
