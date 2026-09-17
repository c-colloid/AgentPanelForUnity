using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_scene_save (design note 2026-09-17-modal-menu-and-base64-scan
    /// section 1.2). The point of the tool is that it can never open the
    /// Save Scene dialog, so the refusals that guarantee it are pinned on
    /// the pure path resolver; the one real save goes through
    /// saveAsCopy:true so the scene the test runner holds open keeps its
    /// identity (no NewScene here either -- see FontLoaderTests for what
    /// that does to open panels).
    /// </summary>
    [TestFixture]
    public class UapSceneSaveToolTests
    {
        private const string TempFolder = "Assets/__UapSceneSaveToolTests";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ResolveTargetPath_UntitledSceneWithoutPath_IsRefused_NeverPassedThroughToUnity()
        {
            string error;

            Assert.IsNull(UapSceneSaveTool.ResolveTargetPath(string.Empty, null, false, out error));
            StringAssert.Contains("untitled", error);
            StringAssert.Contains("'path' is required", error);
        }

        [Test]
        public void ResolveTargetPath_TitledSceneWithoutPath_SavesInPlace()
        {
            string error;

            Assert.AreEqual("Assets/Scenes/Main.unity",
                UapSceneSaveTool.ResolveTargetPath("Assets/Scenes/Main.unity", null, false, out error));
            Assert.IsNull(error);
        }

        [Test]
        public void ResolveTargetPath_SaveAsCopyWithoutPath_IsRefused()
        {
            string error;

            Assert.IsNull(UapSceneSaveTool.ResolveTargetPath("Assets/Scenes/Main.unity", null, true, out error));
            StringAssert.Contains("saveAsCopy", error);
        }

        [TestCase("Assets/Scenes/New.unity", "Assets/Scenes/New.unity")]
        [TestCase("Assets\\Scenes\\New.UNITY", "Assets/Scenes/New.UNITY")]
        [TestCase("Assets/Scenes/../Other/New.unity", "Assets/Other/New.unity")]
        public void ResolveTargetPath_ExplicitPath_IsNormalized(string requested, string expected)
        {
            string error;

            Assert.AreEqual(expected, UapSceneSaveTool.ResolveTargetPath(string.Empty, requested, false, out error));
        }

        [TestCase("Assets/Scenes/New.prefab")]
        [TestCase("Assets/Scenes/New")]
        [TestCase("Assets/.unity")]
        [TestCase("Packages/x/New.unity")]
        [TestCase("Assets/../../Outside.unity")]
        [TestCase("C:/Temp/New.unity")]
        public void ResolveTargetPath_BadPath_IsRefused(string requested)
        {
            string error;

            Assert.IsNull(UapSceneSaveTool.ResolveTargetPath(string.Empty, requested, false, out error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void Execute_UntitledActiveSceneWithoutPath_ThrowsInsteadOfOpeningTheDialog()
        {
            Assume.That(SceneManager.GetActiveScene().path, Is.Empty,
                "needs the untitled scene the EditMode runner normally holds");

            var ex = Assert.Throws<ArgumentException>(
                () => new UapSceneSaveTool().Execute(JsonNode.NewObject()));

            StringAssert.Contains("'path' is required", ex.Message);
        }

        [Test]
        public void Execute_SaveAsCopy_WritesTheFile_CreatesFolders_AndLeavesTheOpenSceneAlone()
        {
            Scene active = SceneManager.GetActiveScene();
            string pathBefore = active.path;
            string target = TempFolder + "/Nested/Copy.unity";

            JsonNode result = new UapSceneSaveTool().Execute(JsonNode.NewObject()
                .Set("path", target).Set("saveAsCopy", true));

            Assert.IsTrue(File.Exists(target));
            StringAssert.Contains(target, result[0]["text"].AsString());
            Assert.AreEqual(pathBefore, SceneManager.GetActiveScene().path);
        }

        [Test]
        public void Execute_ExistingDifferentSceneFile_IsRefusedWithoutOverwrite_AndReplacedWithIt()
        {
            string target = TempFolder + "/Existing.unity";
            var tool = new UapSceneSaveTool();
            tool.Execute(JsonNode.NewObject().Set("path", target).Set("saveAsCopy", true));

            var ex = Assert.Throws<InvalidOperationException>(
                () => tool.Execute(JsonNode.NewObject().Set("path", target).Set("saveAsCopy", true)));
            StringAssert.Contains("overwrite:true", ex.Message);

            Assert.DoesNotThrow(() => tool.Execute(JsonNode.NewObject()
                .Set("path", target).Set("saveAsCopy", true).Set("overwrite", true)));
        }

        [Test]
        public void Execute_UnknownScene_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new UapSceneSaveTool().Execute(JsonNode.NewObject().Set("scene", "NoSuchScene_12345")));

            StringAssert.Contains("not currently loaded", ex.Message);
        }

        [Test]
        public void Metadata_IsACoreWriteToolThatIsNotUndoable_AndIsRegistered()
        {
            var tool = new UapSceneSaveTool();

            Assert.AreEqual("uap_scene_save", tool.Name);
            Assert.AreEqual("core", tool.Module);
            Assert.IsFalse(tool.Undoable);
            Assert.IsFalse(tool.ReadOnly);
            Assert.IsNotNull(ToolRegistry.CreateDefault(false).Find("uap_scene_save"));
        }
    }
}
