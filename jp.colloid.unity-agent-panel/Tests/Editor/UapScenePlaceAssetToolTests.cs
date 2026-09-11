using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real prefab placement via uap_scene_place_asset, including the
    /// non-GameObject refusal and the Undo revert path. NOTE:
    /// PrefabUtility.SaveAsPrefabAsset renames the saved root GameObject to
    /// match the prefab asset's file name, so the source object and the
    /// prefab file share the name "Placeable" here to avoid confusion.
    /// </summary>
    [TestFixture]
    public class UapScenePlaceAssetToolTests
    {
        private const string TestFolder = "Assets/UapScenePlaceAssetToolTests_Scratch";
        private const string PrefabPath = TestFolder + "/Placeable.prefab";
        private const string PlacedObjectName = "Placeable";

        private UapScenePlaceAssetTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapScenePlaceAssetTool();
            AssetDatabase.CreateFolder("Assets", "UapScenePlaceAssetToolTests_Scratch");
            var source = new GameObject(PlacedObjectName);
            PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
            Object.DestroyImmediate(source);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
            {
                if (go != null && go.name.StartsWith("Placeable"))
                {
                    Object.DestroyImmediate(go);
                }
            }
            if (AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.DeleteAsset(TestFolder);
            }
        }

        private static JsonNode Input(string assetPath, string parentPath = null)
        {
            JsonNode node = JsonNode.NewObject().Set("assetPath", assetPath);
            if (parentPath != null)
            {
                node.Set("parentPath", parentPath);
            }
            return node;
        }

        [Test]
        public void Execute_InstantiatesThePrefab_KeepingThePrefabConnection()
        {
            _tool.Execute(Input(PrefabPath));

            GameObject instance = GameObject.Find(PlacedObjectName);
            Assert.IsNotNull(instance);
            Assert.AreEqual(PrefabAssetType.Regular, PrefabUtility.GetPrefabAssetType(instance));
        }

        [Test]
        public void Execute_MissingAssetPath_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Execute_UnknownAssetPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input(TestFolder + "/DoesNotExist12345.prefab"));
            });
        }

        [Test]
        public void Execute_AssetIsNotAGameObject_Throws()
        {
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, TestFolder + "/NotPlaceable.mat");

            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input(TestFolder + "/NotPlaceable.mat"));
            });
        }

        [Test]
        public void Execute_WithParentPath_ParentsUnderExistingObject()
        {
            var parent = new GameObject("PlaceableParent");

            _tool.Execute(Input(PrefabPath, "PlaceableParent"));

            GameObject instance = GameObject.Find(PlacedObjectName);
            Assert.IsNotNull(instance);
            Assert.AreSame(parent.transform, instance.transform.parent);
        }

        [Test]
        public void Execute_ThenUndo_RemovesThePlacedInstance()
        {
            Undo.IncrementCurrentGroup();

            _tool.Execute(Input(PrefabPath));
            Assert.IsNotNull(GameObject.Find(PlacedObjectName));

            Undo.PerformUndo();

            Assert.IsNull(GameObject.Find(PlacedObjectName), "Undo should remove the placed instance.");
        }
    }
}
