using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_scene_open (design note 2026-09-17-modal-menu-and-base64-scan
    /// section 1.6). What matters is the gate in front of
    /// EditorSceneManager.OpenScene -- the API drops unsaved changes
    /// without asking -- so it is pinned on the pure functions. No test
    /// here actually opens a scene in Single mode: that would unload the
    /// scene the EditMode runner holds (and see FontLoaderTests for what a
    /// scene switch does to open panels); every Execute case below stops
    /// at a refusal before Unity is reached.
    /// </summary>
    [TestFixture]
    public class UapSceneOpenToolTests
    {
        [Test]
        public void Refusal_SingleWithDirtyScenes_IsRefused_NamingThemAndTheWaysOut()
        {
            string refusal = UapSceneOpenTool.Refusal(UapSceneOpenTool.ModeSingle, false, false,
                new[] { "'Untitled'", "'Level1'" });

            StringAssert.StartsWith("Refused:", refusal);
            StringAssert.Contains("'Untitled', 'Level1' have unsaved changes", refusal);
            StringAssert.Contains("Nothing was opened", refusal);
            StringAssert.Contains("uap_scene_save", refusal);
            StringAssert.Contains("discardUnsaved:true", refusal);
        }

        [Test]
        public void Refusal_SingleWithOneDirtyScene_UsesTheSingularVerb()
        {
            string refusal = UapSceneOpenTool.Refusal(UapSceneOpenTool.ModeSingle, false, false, new[] { "'Main'" });

            StringAssert.Contains("'Main' has unsaved changes", refusal);
        }

        [Test]
        public void Refusal_SingleWithDirtyScenes_PassesWithDiscardUnsaved()
        {
            Assert.IsNull(UapSceneOpenTool.Refusal(UapSceneOpenTool.ModeSingle, true, false, new[] { "'Main'" }));
        }

        [Test]
        public void Refusal_SingleWithNothingDirty_Passes()
        {
            Assert.IsNull(UapSceneOpenTool.Refusal(UapSceneOpenTool.ModeSingle, false, false, new string[0]));
            Assert.IsNull(UapSceneOpenTool.Refusal(UapSceneOpenTool.ModeSingle, false, false, null));
        }

        [Test]
        public void Refusal_Additive_NeverNeedsTheGate()
        {
            Assert.IsNull(UapSceneOpenTool.Refusal(UapSceneOpenTool.ModeAdditive, false, false, new[] { "'Main'" }));
        }

        [TestCase(UapSceneOpenTool.ModeSingle)]
        [TestCase(UapSceneOpenTool.ModeAdditive)]
        public void Refusal_PlayMode_IsRefusedInEitherMode_EvenWithDiscardUnsaved(string mode)
        {
            string refusal = UapSceneOpenTool.Refusal(mode, true, true, new string[0]);

            StringAssert.Contains("Play Mode", refusal);
        }

        [TestCase("Assets/Scenes/Main.unity", "Assets/Scenes/Main.unity")]
        [TestCase(" Assets\\Scenes\\Main.UNITY ", "Assets/Scenes/Main.UNITY")]
        [TestCase("Assets/A/../Scenes/Main.unity", "Assets/Scenes/Main.unity")]
        [TestCase("Packages/com.vendor.kit/Samples/Demo.unity", "Packages/com.vendor.kit/Samples/Demo.unity")]
        public void NormalizeScenePath_GoodPaths(string input, string expected)
        {
            string error;

            Assert.AreEqual(expected, UapSceneOpenTool.NormalizeScenePath(input, out error));
            Assert.IsNull(error);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/Scenes/Main.prefab")]
        [TestCase("Assets/.unity")]
        [TestCase("Assets/../../Outside.unity")]
        [TestCase("C:/Temp/Main.unity")]
        [TestCase("Library/Main.unity")]
        public void NormalizeScenePath_BadPaths_AreRefused(string input)
        {
            string error;

            Assert.IsNull(UapSceneOpenTool.NormalizeScenePath(input, out error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void Execute_MissingFile_Throws_BeforeTouchingTheLoadedScenes()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new UapSceneOpenTool().Execute(
                JsonNode.NewObject().Set("path", "Assets/__NoSuchFolder_12345/Nope.unity")));

            StringAssert.Contains("Scene file not found", ex.Message);
        }

        [Test]
        public void Execute_BadPathOrMode_Throws()
        {
            var tool = new UapSceneOpenTool();

            Assert.Throws<ArgumentException>(() => tool.Execute(JsonNode.NewObject()));
            Assert.Throws<ArgumentException>(() => tool.Execute(
                JsonNode.NewObject().Set("path", "Assets/x.unity").Set("mode", "replace")));
        }

        [Test]
        public void Metadata_IsACoreWriteToolThatIsNotUndoable_AndIsRegistered()
        {
            var tool = new UapSceneOpenTool();

            Assert.AreEqual("uap_scene_open", tool.Name);
            Assert.AreEqual("core", tool.Module);
            Assert.IsFalse(tool.Undoable);
            Assert.IsFalse(tool.ReadOnly);
            Assert.IsNotNull(ToolRegistry.CreateDefault(false).Find("uap_scene_open"));
        }

        [Test]
        public void MenuPolicy_FileOpenScene_NowPointsAtThisTool()
        {
            StringAssert.Contains("uap_scene_open", UapMenuDialogPolicy.Refusal("File/Open Scene", false));
        }
    }
}
