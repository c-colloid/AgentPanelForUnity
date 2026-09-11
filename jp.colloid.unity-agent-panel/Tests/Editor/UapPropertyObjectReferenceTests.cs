using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// OPS defects #1/#2/#3/#4 (measured against a real Unity 2022.3.22f1
    /// probe run 2026-09-08): ObjectReference wrote a silent null on a
    /// type-mismatched asset path and could never address a sub-asset
    /// (a Material inside a ScriptableObject, the TextMeshPro font-asset
    /// case); LayerMask threw "does not support propertyType LayerMask";
    /// enum matching only accepted the DISPLAY name, not the C# member
    /// name; and uap_object_inspect printed Vector/Color values rounded to
    /// 2 decimals and "<Generic>" for arrays with no children at all. Each
    /// probe here reproduces one of those defects directly against the
    /// fixed UapPropertyValueWriter / UapObjectInspectTool.
    /// </summary>
    [TestFixture]
    public class UapPropertyObjectReferenceTests
    {
        private const string TestFolder = "Assets/UapPropertyObjectReferenceTests_Scratch";

        private GameObject _go;
        private MeshRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.CreateFolder("Assets", "UapPropertyObjectReferenceTests_Scratch");
            }
            _go = new GameObject("UapPropertyObjectReferenceTestObject");
            _renderer = _go.AddComponent<MeshRenderer>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
            if (AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.DeleteAsset(TestFolder);
            }
        }

        private static Material CreateMaterial(string name)
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Hidden/InternalErrorShader");
            var material = new Material(shader);
            material.name = name;
            return material;
        }

        private SerializedProperty FirstMaterialElement()
        {
            var so = new SerializedObject(_renderer);
            SerializedProperty materials = so.FindProperty("m_Materials");
            materials.arraySize = 1;
            so.ApplyModifiedProperties();
            return new SerializedObject(_renderer).FindProperty("m_Materials.Array.data[0]");
        }

        // -- object reference: wrong-typed asset ---------------------------

        [Test]
        public void ObjectReference_WrongTypedAssetPath_ThrowsInsteadOfSilentNull()
        {
            var scriptableObject = ScriptableObject.CreateInstance<ScriptableObject>();
            string path = TestFolder + "/WrongType.asset";
            AssetDatabase.CreateAsset(scriptableObject, path);
            AssetDatabase.SaveAssets();

            SerializedProperty element = FirstMaterialElement();
            var ex = Assert.Throws<System.InvalidOperationException>(delegate
            {
                UapPropertyValueWriter.Write(element, JsonNode.Of(path), false);
            });
            StringAssert.Contains("ScriptableObject", ex.Message);
            StringAssert.Contains("Material", ex.Message);

            SerializedProperty reread = new SerializedObject(_renderer).FindProperty("m_Materials.Array.data[0]");
            Assert.IsNull(reread.objectReferenceValue, "a refused write must leave the field null, not silently 'succeed' with null");
        }

        // -- object reference: sub-asset addressing ------------------------

        [Test]
        public void ObjectReference_PathHashName_ResolvesTheNamedSubAsset()
        {
            var scriptableObject = ScriptableObject.CreateInstance<ScriptableObject>();
            string path = TestFolder + "/WithSubMaterial.asset";
            AssetDatabase.CreateAsset(scriptableObject, path);
            Material subMat = CreateMaterial("SubMat");
            AssetDatabase.AddObjectToAsset(subMat, scriptableObject);
            AssetDatabase.ImportAsset(path);
            AssetDatabase.SaveAssets();

            SerializedProperty element = FirstMaterialElement();
            UapPropertyValueWriter.Write(element, JsonNode.Of(path + "#SubMat"), false);
            element.serializedObject.ApplyModifiedProperties();

            Assert.IsNotNull(_renderer.sharedMaterials[0]);
            Assert.AreEqual("SubMat", _renderer.sharedMaterials[0].name);
        }

        [Test]
        public void ObjectReference_PathHashUnknownName_ThrowsListingAvailableSubAssets()
        {
            var scriptableObject = ScriptableObject.CreateInstance<ScriptableObject>();
            string path = TestFolder + "/WithSubMaterial2.asset";
            AssetDatabase.CreateAsset(scriptableObject, path);
            Material subMat = CreateMaterial("SubMat");
            AssetDatabase.AddObjectToAsset(subMat, scriptableObject);
            AssetDatabase.ImportAsset(path);
            AssetDatabase.SaveAssets();

            SerializedProperty element = FirstMaterialElement();
            var ex = Assert.Throws<System.InvalidOperationException>(delegate
            {
                UapPropertyValueWriter.Write(element, JsonNode.Of(path + "#NoSuchSubAsset"), false);
            });
            StringAssert.Contains("SubMat", ex.Message);
        }

        [Test]
        public void ObjectReference_PlainPath_UniqueCompatibleSubAsset_AutoPicked()
        {
            var scriptableObject = ScriptableObject.CreateInstance<ScriptableObject>();
            string path = TestFolder + "/AutoPick.asset";
            AssetDatabase.CreateAsset(scriptableObject, path);
            Material subMat = CreateMaterial("OnlyCompatible");
            AssetDatabase.AddObjectToAsset(subMat, scriptableObject);
            AssetDatabase.ImportAsset(path);
            AssetDatabase.SaveAssets();

            SerializedProperty element = FirstMaterialElement();
            // Plain path (no '#') -- the main asset (the ScriptableObject)
            // is NOT a Material, but exactly one sub-asset at this path is,
            // so it must be auto-picked (the TextMeshPro m_fontSharedMaterial
            // case, where callers pass the font asset path).
            UapPropertyValueWriter.Write(element, JsonNode.Of(path), false);
            element.serializedObject.ApplyModifiedProperties();

            Assert.IsNotNull(_renderer.sharedMaterials[0]);
            Assert.AreEqual("OnlyCompatible", _renderer.sharedMaterials[0].name);
        }

        [Test]
        public void ObjectReference_PlainPath_MultipleCompatibleSubAssets_ThrowsAmbiguous()
        {
            var scriptableObject = ScriptableObject.CreateInstance<ScriptableObject>();
            string path = TestFolder + "/Ambiguous.asset";
            AssetDatabase.CreateAsset(scriptableObject, path);
            Material matA = CreateMaterial("MatA");
            Material matB = CreateMaterial("MatB");
            AssetDatabase.AddObjectToAsset(matA, scriptableObject);
            AssetDatabase.AddObjectToAsset(matB, scriptableObject);
            AssetDatabase.ImportAsset(path);
            AssetDatabase.SaveAssets();

            SerializedProperty element = FirstMaterialElement();
            var ex = Assert.Throws<System.InvalidOperationException>(delegate
            {
                UapPropertyValueWriter.Write(element, JsonNode.Of(path), false);
            });
            StringAssert.Contains("#", ex.Message);
        }

        // -- LayerMask -------------------------------------------------------

        [Test]
        public void LayerMask_ByInteger_WritesTheRawBitmask()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = new SerializedObject(camera).FindProperty("m_CullingMask");
            Assert.AreEqual(SerializedPropertyType.LayerMask, prop.propertyType);

            UapPropertyValueWriter.Write(prop, JsonNode.Of(5L), false);
            prop.serializedObject.ApplyModifiedProperties();

            Assert.AreEqual(5, camera.cullingMask);
        }

        [Test]
        public void LayerMask_ByLayerName_SetsThatLayersBit()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = new SerializedObject(camera).FindProperty("m_CullingMask");

            UapPropertyValueWriter.Write(prop, JsonNode.Of("Default"), false);
            prop.serializedObject.ApplyModifiedProperties();

            int expected = 1 << LayerMask.NameToLayer("Default");
            Assert.AreEqual(expected, camera.cullingMask);
        }

        [Test]
        public void LayerMask_ByArrayOfNamesAndIntegers_OrsThemTogether()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = new SerializedObject(camera).FindProperty("m_CullingMask");

            JsonNode array = JsonNode.NewArray().Add("Default").Add(4L);
            UapPropertyValueWriter.Write(prop, array, false);
            prop.serializedObject.ApplyModifiedProperties();

            int expected = (1 << LayerMask.NameToLayer("Default")) | 4;
            Assert.AreEqual(expected, camera.cullingMask);
        }

        [Test]
        public void LayerMask_UnknownLayerName_Throws()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = new SerializedObject(camera).FindProperty("m_CullingMask");
            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(prop, JsonNode.Of("NoSuchLayer12345"), false);
            });
        }

        // -- enum C#-member-name matching -----------------------------------

        [Test]
        public void Enum_CSharpMemberName_MatchesDisplayNameEquivalent()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = new SerializedObject(camera).FindProperty("m_ClearFlags");
            Assert.AreEqual(SerializedPropertyType.Enum, prop.propertyType);

            // prop.enumNames reports the DISPLAY name "Solid Color"; the C#
            // enum member is CameraClearFlags.SolidColor. Both must match.
            UapPropertyValueWriter.Write(prop, JsonNode.Of("SolidColor"), false);
            prop.serializedObject.ApplyModifiedProperties();

            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
        }

        // -- inspect: full precision -----------------------------------------

        [Test]
        public void Inspect_Vector3_PrintsFullPrecision_NotRoundedToTwoDecimals()
        {
            _go.transform.position = new Vector3(1.5f, 0.005f, -2f);

            var tool = new UapObjectInspectTool();
            JsonNode result = tool.Execute(JsonNode.NewObject()
                .Set("path", "UapPropertyObjectReferenceTestObject")
                .Set("componentType", "Transform"));
            string text = result[0]["text"].AsString();

            StringAssert.DoesNotContain("0.01", text);
            StringAssert.Contains("0.005", text);
        }

        // -- inspect: array expansion -----------------------------------------

        [Test]
        public void Inspect_MaterialsArray_ListsArraySizeAndElementPaths()
        {
            FirstMaterialElement().serializedObject.ApplyModifiedProperties();

            var tool = new UapObjectInspectTool();
            JsonNode result = tool.Execute(JsonNode.NewObject()
                .Set("path", "UapPropertyObjectReferenceTestObject")
                .Set("componentType", "MeshRenderer"));
            string text = result[0]["text"].AsString();

            StringAssert.Contains("m_Materials.Array.size", text);
            StringAssert.Contains("m_Materials.Array.data[0]", text);
        }
    }
}
