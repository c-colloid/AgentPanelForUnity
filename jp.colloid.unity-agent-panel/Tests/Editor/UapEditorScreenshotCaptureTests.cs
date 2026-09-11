using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// P4 of the 2026-09-07 design note: the capture argument, the
    /// capture-tagged file name, the pure "Markers in view" listing, and the
    /// two structured refusals a headless run can exercise (window capture
    /// of the Game view; window capture of a Scene view that was never
    /// drawn on screen).
    /// </summary>
    [TestFixture]
    public class UapEditorScreenshotCaptureTests
    {
        [Test]
        public void TryParseCapture_DefaultsToCamera_AcceptsWindowCaseInsensitive_RejectsOthers()
        {
            UapScreenshotCapture capture;
            string error;
            Assert.IsTrue(UapEditorScreenshotPaths.TryParseCapture(null, out capture, out error));
            Assert.AreEqual(UapScreenshotCapture.Camera, capture);
            Assert.IsTrue(UapEditorScreenshotPaths.TryParseCapture(" Window ", out capture, out error));
            Assert.AreEqual(UapScreenshotCapture.Window, capture);
            Assert.IsFalse(UapEditorScreenshotPaths.TryParseCapture("screen", out capture, out error));
            StringAssert.Contains("capture", error);
        }

        [Test]
        public void BuildOutputPath_WindowCapture_IsTaggedAndDistinctFromCamera()
        {
            var timestamp = new DateTime(2026, 9, 7, 1, 2, 3, 4, DateTimeKind.Utc);
            string camera = UapEditorScreenshotPaths.BuildOutputPath(timestamp, UapScreenshotView.Scene, UapScreenshotCapture.Camera, "C:/Proj");
            string window = UapEditorScreenshotPaths.BuildOutputPath(timestamp, UapScreenshotView.Scene, UapScreenshotCapture.Window, "C:/Proj");
            StringAssert.Contains("uap_scene_window_20260907_010203_004.png", window);
            Assert.AreEqual(UapEditorScreenshotPaths.BuildOutputPath(timestamp, UapScreenshotView.Scene, "C:/Proj"), camera,
                "the 3-arg overload is the camera capture");
            Assert.AreNotEqual(camera, window);
        }

        [Test]
        public void FormatMarkersInView_PixelCoordinates_OffScreen_NotDrawn_AndNullWhenEmpty()
        {
            Assert.IsNull(UapEditorScreenshotPaths.FormatMarkersInView(null, 100, 50));
            Assert.IsNull(UapEditorScreenshotPaths.FormatMarkersInView(new List<UapMarkerScreenPoint>(), 100, 50));
            var points = new List<UapMarkerScreenPoint>
            {
                new UapMarkerScreenPoint { Id = 1, Label = "top-left", Resolved = true, Viewport = new Vector3(0f, 1f, 5f) },
                new UapMarkerScreenPoint { Id = 2, Label = "centre", Resolved = true, Viewport = new Vector3(0.5f, 0.5f, 5f) },
                new UapMarkerScreenPoint { Id = 3, Label = "behind", Resolved = true, Viewport = new Vector3(0.5f, 0.5f, -1f) },
                new UapMarkerScreenPoint { Id = 4, Label = "outside", Resolved = true, Viewport = new Vector3(1.2f, 0.5f, 5f) },
                new UapMarkerScreenPoint { Id = 5, Label = "gone", Resolved = false }
            };

            string text = UapEditorScreenshotPaths.FormatMarkersInView(points, 200, 100);

            StringAssert.Contains("Markers in view (image 200x100):", text);
            StringAssert.Contains("#1 \"top-left\" at pixel (0, 0)", text, "viewport y is up, image y is down");
            StringAssert.Contains("#2 \"centre\" at pixel (100, 50)", text);
            StringAssert.Contains("#3 \"behind\" off-screen", text);
            StringAssert.Contains("#4 \"outside\" off-screen", text);
            StringAssert.Contains("#5 \"gone\" not drawn", text);
        }

        [Test]
        public void Execute_WindowCaptureOfGameView_IsAnArgumentError()
        {
            var ex = Assert.Throws<ArgumentException>(() => new UapEditorScreenshotTool().Execute(
                JsonNode.NewObject().Set("view", "game").Set("capture", "window")));
            StringAssert.Contains("view:\"scene\"", ex.Message);
        }

        [Test]
        public void Execute_UnknownCapture_Throws()
        {
            Assert.Throws<ArgumentException>(() => new UapEditorScreenshotTool().Execute(
                JsonNode.NewObject().Set("view", "scene").Set("capture", "print")));
        }

        [Test]
        public void TryCaptureSceneViewWindow_NeverDrawnView_IsAStructuredError()
        {
            int width, height;
            string error;
            Assert.IsFalse(UapScreenCapture.TryCaptureSceneViewWindow(null, "unused.png", out width, out height, out error));
            StringAssert.Contains("not been drawn", error);
            StringAssert.Contains("capture:\"camera\"", error);
        }

        [Test]
        public void ToolResults_AddImage_AppendsAnMcpImageBlock_AndIgnoresEmpty()
        {
            JsonNode content = UapToolResults.Text("hello");
            UapToolResults.AddImage(content, new byte[] { 1, 2, 3 }, "image/png");
            UapToolResults.AddImage(content, new byte[0], "image/png");
            UapToolResults.AddImage(content, null, "image/jpeg");

            Assert.AreEqual(2, content.Count);
            Assert.AreEqual("image", content[1]["type"].AsString());
            Assert.AreEqual(Convert.ToBase64String(new byte[] { 1, 2, 3 }), content[1]["data"].AsString());
            Assert.AreEqual("image/png", content[1]["mimeType"].AsString());
            JsonNode fresh = UapToolResults.AddImage(null, new byte[] { 9 }, null);
            Assert.AreEqual("image/png", fresh[0]["mimeType"].AsString(), "null mime defaults to PNG");
        }

        [Test]
        public void ScreenshotSchema_HasReturnImageBoolean()
        {
            JsonNode schema = new UapEditorScreenshotTool().InputSchema;
            Assert.AreEqual("boolean", schema["properties"]["return_image"]["type"].AsString());
        }

        [Test]
        public void ScreenshotSchema_ListsCaptureEnum()
        {
            JsonNode schema = new UapEditorScreenshotTool().InputSchema;
            CollectionAssert.AreEqual(new[] { "camera", "window" }, schema["properties"]["capture"]["enum"].AsStringArray());
        }
    }
}
