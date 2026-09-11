using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real scene object destruction via uap_scene_destroy_object, including the Undo revert path.</summary>
    [TestFixture]
    public class UapSceneDestroyObjectToolTests
    {
        private UapSceneDestroyObjectTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapSceneDestroyObjectTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapDestroyTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static JsonNode Input(string path)
        {
            return JsonNode.NewObject().Set("path", path);
        }

        [Test]
        public void Execute_DestroysTheGameObject()
        {
            new GameObject("UapDestroyTestSimple");

            _tool.Execute(Input("UapDestroyTestSimple"));

            Assert.IsNull(GameObject.Find("UapDestroyTestSimple"));
        }

        [Test]
        public void Execute_MissingPath_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("NoSuchObjectAtAll12345"));
            });
        }

        [Test]
        public void Execute_PrefabAssetPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("Assets/Somewhere/Foo.prefab"));
            });
        }

        [Test]
        public void Execute_ThenUndo_RestoresTheDestroyedObject()
        {
            new GameObject("UapDestroyTestUndo");
            Undo.IncrementCurrentGroup();

            _tool.Execute(Input("UapDestroyTestUndo"));
            Assert.IsNull(GameObject.Find("UapDestroyTestUndo"));

            Undo.PerformUndo();

            Assert.IsNotNull(GameObject.Find("UapDestroyTestUndo"), "Undo should restore the destroyed GameObject.");
        }

        [Test]
        public void Execute_ReturnsTextContentDescribingTheDestroyedPath()
        {
            new GameObject("UapDestroyTestResult");

            JsonNode result = _tool.Execute(Input("UapDestroyTestResult"));

            Assert.IsTrue(result.IsArray);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("UapDestroyTestResult", text);
        }
    }
}
