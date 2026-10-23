using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>uap_property_set / uap_object_inspect with componentType "GameObject" (active, layer, tag, static flags).</summary>
    [TestFixture]
    public class UapPropertySetGameObjectTests
    {
        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("UapPropGoTest");
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                UnityEngine.Object.DestroyImmediate(_go);
            }
        }

        private static JsonNode Set(string propertyPath, JsonNode value)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("path", "UapPropGoTest")
                .Set("componentType", "gameobject")
                .Set("propertyPath", propertyPath);
            node.Set("value", value);
            return node;
        }

        [Test]
        public void Set_IsActiveFalse_DeactivatesTheObject()
        {
            new UapPropertySetTool().Execute(Set("m_IsActive", JsonNode.Of(false)));
            Assert.IsFalse(_go.activeSelf);
        }

        [Test]
        public void Set_LayerByName_AndByNumber()
        {
            new UapPropertySetTool().Execute(Set("m_Layer", JsonNode.Of("UI")));
            Assert.AreEqual(LayerMask.NameToLayer("UI"), _go.layer);
            new UapPropertySetTool().Execute(Set("m_Layer", JsonNode.Of(3)));
            Assert.AreEqual(3, _go.layer);
        }

        [Test]
        public void Set_UnknownLayerName_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new UapPropertySetTool().Execute(Set("m_Layer", JsonNode.Of("NoSuchLayerXyz")));
            });
        }

        [Test]
        public void Set_Tag_SetsTheTag()
        {
            new UapPropertySetTool().Execute(Set("m_TagString", JsonNode.Of("MainCamera")));
            Assert.AreEqual("MainCamera", _go.tag);
        }

        [Test]
        public void Set_StaticFlags_FromNames()
        {
            new UapPropertySetTool().Execute(Set("m_StaticEditorFlags",
                JsonNode.Of("BatchingStatic, ContributeGI")));
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(_go);
            Assert.AreEqual(StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI, flags);
        }

        [Test]
        public void Set_UnsupportedGameObjectPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                new UapPropertySetTool().Execute(Set("m_Name", JsonNode.Of("X")));
            });
        }

        [Test]
        public void Set_MaterialSavedPropertiesPath_ThrowsNamingMaterialSet()
        {
            const string folder = "Assets/UapPropGoTest_Scratch";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "UapPropGoTest_Scratch");
            }
            try
            {
                var shader = Shader.Find("Hidden/InternalErrorShader");
                if (shader == null)
                {
                    Assert.Ignore("No shader available to build a Material.");
                }
                string matPath = folder + "/Test.mat";
                AssetDatabase.CreateAsset(new Material(shader), matPath);
                var ex = Assert.Throws<ArgumentException>(delegate
                {
                    new UapPropertySetTool().Execute(JsonNode.NewObject()
                        .Set("assetPath", matPath)
                        .Set("propertyPath", "m_SavedProperties.m_Colors.Array.data[0].second")
                        .Set("value", JsonNode.Of(1)));
                });
                StringAssert.Contains("uap_material_set", ex.Message);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Inspect_GameObject_ListsTheFourProperties()
        {
            JsonNode result = new UapObjectInspectTool().Execute(JsonNode.NewObject()
                .Set("path", "UapPropGoTest").Set("componentType", "GameObject"));
            string text = JsonWriter.Write(result);
            foreach (string key in new[] { "m_IsActive", "m_Layer", "m_TagString", "m_StaticEditorFlags" })
            {
                StringAssert.Contains(key, text);
            }
        }
    }
}
