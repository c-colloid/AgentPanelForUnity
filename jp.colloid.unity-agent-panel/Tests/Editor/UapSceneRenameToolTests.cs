using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real GameObject renaming via uap_scene_rename, including the empty-name refusal and the Undo revert path.</summary>
    [TestFixture]
    public class UapSceneRenameToolTests
    {
        private UapSceneRenameTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapSceneRenameTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapRenameTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static JsonNode Input(string path, string newName)
        {
            return JsonNode.NewObject().Set("path", path).Set("newName", newName);
        }

        [Test]
        public void Execute_RenamesTheGameObject()
        {
            var go = new GameObject("UapRenameTestOld");

            _tool.Execute(Input("UapRenameTestOld", "UapRenameTestNew"));

            Assert.AreEqual("UapRenameTestNew", go.name);
        }

        [Test]
        public void Execute_MissingPath_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("newName", "X"));
            });
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("NoSuchObjectAtAll12345", "UapRenameTestX"));
            });
        }

        [Test]
        public void Execute_EmptyNewName_Throws()
        {
            new GameObject("UapRenameTestEmptyTarget");

            Assert.Throws<System.ArgumentException>(delegate
            {
                _tool.Execute(Input("UapRenameTestEmptyTarget", string.Empty));
            });
        }

        [Test]
        public void Execute_ThenUndo_RestoresThePreviousName()
        {
            var go = new GameObject("UapRenameTestUndoOld");
            Undo.IncrementCurrentGroup();

            _tool.Execute(Input("UapRenameTestUndoOld", "UapRenameTestUndoNew"));
            Assert.AreEqual("UapRenameTestUndoNew", go.name);

            Undo.PerformUndo();

            Assert.AreEqual("UapRenameTestUndoOld", go.name, "Undo should restore the previous name.");
        }
    }
}
