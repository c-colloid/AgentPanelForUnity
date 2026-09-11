using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real scene object creation via uap_scene_create_object (task line: "EditMode tests may exercise real scene object creation/undo").</summary>
    [TestFixture]
    public class UapSceneCreateObjectToolTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private UapSceneCreateObjectTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapSceneCreateObjectTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapSceneToolTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
            _created.Clear();
        }

        private static JsonNode Input(string name, string primitiveType = null, string parentPath = null)
        {
            JsonNode node = JsonNode.NewObject().Set("name", name);
            if (primitiveType != null)
            {
                node.Set("primitiveType", primitiveType);
            }
            if (parentPath != null)
            {
                node.Set("parentPath", parentPath);
            }
            return node;
        }

        /// <summary>
        /// OPS-8: with a prefab stage open and parentPath omitted, the new
        /// object must be parented under the stage's prefab root -- a
        /// second stage ROOT is silently discarded when the stage saves,
        /// which turned "created successfully" into a vanished object.
        /// </summary>
        [Test]
        public void Execute_InPrefabStage_NoParentPath_ParentsUnderThePrefabRoot()
        {
            const string folder = "Assets/UapSceneCreateStage_Scratch";
            const string prefabPath = folder + "/StageRoot.prefab";
            AssetDatabase.CreateFolder("Assets", "UapSceneCreateStage_Scratch");
            var source = new GameObject("StageRoot");
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            Object.DestroyImmediate(source);
            try
            {
                PrefabStage stage = PrefabStageUtility.OpenPrefab(prefabPath);
                Assert.IsNotNull(stage, "prefab stage must open in EditMode");
                int rootCountBefore = stage.scene.rootCount;

                JsonNode result = _tool.Execute(Input("UapSceneToolTestStaged"));

                GameObject created = stage.prefabContentsRoot.transform
                    .Find("UapSceneToolTestStaged") != null
                    ? stage.prefabContentsRoot.transform.Find("UapSceneToolTestStaged").gameObject
                    : null;
                Assert.IsNotNull(created, "the new object must live UNDER the prefab root");
                Assert.AreEqual(rootCountBefore, stage.scene.rootCount,
                    "no second stage root may appear -- it would be discarded on save");
                StringAssert.Contains("prefab root", result[0]["text"].AsString());
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(folder);
            }
        }

        /// <summary>OPS-8's default must not override an explicit parent inside the stage.</summary>
        [Test]
        public void Execute_InPrefabStage_ExplicitParentPath_StillWins()
        {
            const string folder = "Assets/UapSceneCreateStage_Scratch";
            const string prefabPath = folder + "/StageRoot.prefab";
            AssetDatabase.CreateFolder("Assets", "UapSceneCreateStage_Scratch");
            var source = new GameObject("StageRoot");
            var child = new GameObject("ExistingChild");
            child.transform.SetParent(source.transform);
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            Object.DestroyImmediate(source);
            try
            {
                PrefabStage stage = PrefabStageUtility.OpenPrefab(prefabPath);
                Assert.IsNotNull(stage);

                _tool.Execute(Input("UapSceneToolTestStagedChild", null, "StageRoot/ExistingChild"));

                Transform existing = stage.prefabContentsRoot.transform.Find("ExistingChild");
                Assert.IsNotNull(existing.Find("UapSceneToolTestStagedChild"),
                    "the explicit parentPath must be honored inside the stage");
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Execute_EmptyGameObject_IsCreatedInActiveScene()
        {
            _tool.Execute(Input("UapSceneToolTestEmpty"));

            GameObject found = GameObject.Find("UapSceneToolTestEmpty");
            Assert.IsNotNull(found);
        }

        [Test]
        public void Execute_Primitive_CreatesCubeMesh()
        {
            _tool.Execute(Input("UapSceneToolTestCube", "Cube"));

            GameObject found = GameObject.Find("UapSceneToolTestCube");
            Assert.IsNotNull(found);
            Assert.IsNotNull(found.GetComponent<MeshFilter>(), "a Cube primitive must carry a MeshFilter");
        }

        [Test]
        public void Execute_UnknownPrimitiveType_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                _tool.Execute(Input("UapSceneToolTestBad", "NotAPrimitive"));
            });
        }

        [Test]
        public void Execute_MissingName_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Execute_WithParentPath_ParentsUnderExistingObject()
        {
            _tool.Execute(Input("UapSceneToolTestParent"));
            _tool.Execute(Input("UapSceneToolTestChild", parentPath: "UapSceneToolTestParent"));

            GameObject parent = GameObject.Find("UapSceneToolTestParent");
            GameObject child = GameObject.Find("UapSceneToolTestChild");
            Assert.IsNotNull(parent);
            Assert.IsNotNull(child);
            Assert.AreSame(parent.transform, child.transform.parent);
        }

        [Test]
        public void Execute_UnknownParentPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapSceneToolTestOrphan", parentPath: "NoSuchParentAtAll12345"));
            });
        }

        [Test]
        public void Execute_WithPosition_SetsLocalPosition()
        {
            JsonNode input = Input("UapSceneToolTestPositioned")
                .Set("position", JsonNode.NewObject().Set("x", 1.0).Set("y", 2.0).Set("z", 3.0));
            _tool.Execute(input);

            GameObject found = GameObject.Find("UapSceneToolTestPositioned");
            Assert.IsNotNull(found);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), found.transform.localPosition);
        }

        [Test]
        public void Execute_ReturnsTextContentDescribingTheCreatedPath()
        {
            JsonNode result = _tool.Execute(Input("UapSceneToolTestResult"));
            Assert.IsTrue(result.IsArray);
            Assert.AreEqual(1, result.Count);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("UapSceneToolTestResult", text);
        }
    }
}
