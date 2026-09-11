using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_editor_screenshot's "no view is open" error path -- the ONE
    /// behavior a headless EditMode test run can exercise for real, since
    /// batch mode (and the test runner in general) never has a SceneView or
    /// GameView open (see the design kickoff's "unit tests cover the error
    /// path ... only" scoping for this tool, and the class doc comment on
    /// UapEditorScreenshotTool for why the "is a view open" check runs
    /// BEFORE any camera lookup).
    /// </summary>
    [TestFixture]
    public class UapEditorScreenshotToolTests
    {
        [Test]
        public void Execute_DefaultView_NoGameViewOpen_ThrowsStructuredError()
        {
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapEditorScreenshotTool().Execute(JsonNode.NewObject());
            });
            StringAssert.Contains("Game view", ex.Message);
        }

        [Test]
        public void Execute_SceneView_NoSceneViewOpen_ThrowsStructuredError()
        {
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapEditorScreenshotTool().Execute(JsonNode.NewObject().Set("view", "scene"));
            });
            StringAssert.Contains("Scene view", ex.Message);
        }

        [Test]
        public void Execute_UnknownView_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new UapEditorScreenshotTool().Execute(JsonNode.NewObject().Set("view", "not_a_view"));
            });
        }
    }

    /// <summary>Pure target-selection/output-path seams (no UnityEditor window/camera APIs) -- see UapEditorScreenshotPaths's class doc comment.</summary>
    [TestFixture]
    public class UapEditorScreenshotPathsTests
    {
        [Test]
        public void TryParseView_Empty_DefaultsToGame()
        {
            UapScreenshotView view;
            string error;
            Assert.IsTrue(UapEditorScreenshotPaths.TryParseView(null, out view, out error));
            Assert.AreEqual(UapScreenshotView.Game, view);
            Assert.IsNull(error);
        }

        [Test]
        public void TryParseView_Scene_CaseInsensitive()
        {
            UapScreenshotView view;
            string error;
            Assert.IsTrue(UapEditorScreenshotPaths.TryParseView("SCENE", out view, out error));
            Assert.AreEqual(UapScreenshotView.Scene, view);
        }

        [Test]
        public void TryParseView_Invalid_ReturnsFalseWithError()
        {
            UapScreenshotView view;
            string error;
            Assert.IsFalse(UapEditorScreenshotPaths.TryParseView("bogus", out view, out error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void BuildOutputPath_IncludesViewTagAndTimestamp_UnderTempFolder()
        {
            var timestamp = new DateTime(2026, 8, 2, 10, 30, 45, 123, DateTimeKind.Utc);
            string path = UapEditorScreenshotPaths.BuildOutputPath(timestamp, UapScreenshotView.Scene, "C:/Proj");

            Assert.AreEqual("C:/Proj/Temp/UapOpsScreenshots/uap_scene_20260802_103045_123.png", path);
        }

        [Test]
        public void BuildOutputPath_DifferentViews_ProduceDifferentFileNames()
        {
            var timestamp = new DateTime(2026, 8, 2, 10, 30, 45, 123, DateTimeKind.Utc);
            string scenePath = UapEditorScreenshotPaths.BuildOutputPath(timestamp, UapScreenshotView.Scene, "C:/Proj");
            string gamePath = UapEditorScreenshotPaths.BuildOutputPath(timestamp, UapScreenshotView.Game, "C:/Proj");
            Assert.AreNotEqual(scenePath, gamePath);
        }

        [Test]
        public void BuildOutputPath_EmptyProjectRoot_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                UapEditorScreenshotPaths.BuildOutputPath(DateTime.UtcNow, UapScreenshotView.Game, string.Empty);
            });
        }
    }
}
