using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// `uap_search` (design note 2026-09-10 section 4). The live cases use
    /// the `adb` asset provider and the `scene` provider the verification
    /// spike measured; the provider vocabulary and schema are pure.
    /// </summary>
    [TestFixture]
    public class UapSearchToolTests
    {
        private const string Folder = "Assets/UapSearchToolTests";
        private UapSearchTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapSearchTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapSearchToolTest"))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            if (AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.DeleteAsset(Folder);
            }
        }

        private static JsonNode Input(string query, string[] providers = null, int? maxResults = null)
        {
            JsonNode node = JsonNode.NewObject();
            if (query != null)
            {
                node.Set("query", query);
            }
            if (providers != null)
            {
                JsonNode arr = JsonNode.NewArray();
                foreach (string p in providers)
                {
                    arr.Add(p);
                }
                node.Set("providers", arr);
            }
            if (maxResults.HasValue)
            {
                node.Set("maxResults", maxResults.Value);
            }
            return node;
        }

        private JsonNode Execute(JsonNode input)
        {
            return JsonParser.Parse(_tool.Execute(input)[0]["text"].AsString());
        }

        // -- pure -----------------------------------------------------------------

        [Test]
        public void Metadata_ReadOnlyCoreTool_SchemaRequiresQuery()
        {
            Assert.AreEqual("uap_search", _tool.Name);
            Assert.AreEqual("core", _tool.Module);
            Assert.IsTrue(_tool.ReadOnly);
            Assert.IsFalse(_tool.Undoable);
            JsonNode schema = _tool.InputSchema;
            Assert.AreEqual("query", schema["required"][0].AsString());
            Assert.IsFalse(schema["additionalProperties"].AsBool(true));
            Assert.AreEqual("asset", schema["properties"]["providers"]["items"]["enum"][0].AsString());
            Assert.AreEqual("scene", schema["properties"]["providers"]["items"]["enum"][1].AsString());
        }

        [Test]
        public void ResolveProviders_DefaultBoth_Dedupes_RejectsUnknown()
        {
            var both = UapSearchTool.ResolveProviders(null);
            Assert.AreEqual(new[] { "asset", "scene" }, both.ToArray());
            Assert.AreEqual(new[] { "asset", "scene" }, UapSearchTool.ResolveProviders(JsonNode.NewArray()).ToArray());
            var dup = UapSearchTool.ResolveProviders(JsonNode.NewArray().Add("scene").Add("scene"));
            Assert.AreEqual(new[] { "scene" }, dup.ToArray());
            Assert.Throws<ArgumentException>(() => UapSearchTool.ResolveProviders(JsonNode.NewArray().Add("adb")));
            Assert.Throws<ArgumentException>(() => UapSearchTool.ResolveProviders(JsonNode.NewArray().Add(7)));
        }

        [Test]
        public void ToUnityProviderId_AssetMapsToAdb()
        {
            Assert.AreEqual("adb", UapSearchTool.ToUnityProviderId("asset"));
            Assert.AreEqual("scene", UapSearchTool.ToUnityProviderId("scene"));
        }

        [Test]
        public void Execute_MissingOrBlankQuery_Throws()
        {
            Assert.Throws<ArgumentException>(() => _tool.Execute(Input(null)));
            Assert.Throws<ArgumentException>(() => _tool.Execute(Input("   ")));
        }

        // -- live -----------------------------------------------------------------

        [Test]
        public void Execute_SceneProvider_FindsLightByTypeQuery_WithHierarchyPath()
        {
            var parent = new GameObject("UapSearchToolTestParent");
            var child = new GameObject("UapSearchToolTestLight");
            child.transform.SetParent(parent.transform);
            child.AddComponent<Light>();

            JsonNode result = Execute(Input("t:Light UapSearchToolTestLight", new[] { "scene" }));

            Assert.AreEqual("scene", result["providers"][0].AsString());
            Assert.GreaterOrEqual(result["totalMatches"].AsInt(), 1);
            bool found = false;
            foreach (JsonNode item in result["items"].Items)
            {
                Assert.AreEqual("scene", item["provider"].AsString());
                if (item["path"].AsString() == "UapSearchToolTestParent/UapSearchToolTestLight")
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, JsonWriter.Write(result));
            Assert.IsFalse(result["truncated"].AsBool(true));

            // Freshness across calls: an object created AFTER a query must
            // be visible to the next one (the stale-cache failure this
            // tool's shared context exists to prevent).
            var later = new GameObject("UapSearchToolTestLater");
            later.AddComponent<Camera>();
            JsonNode second = Execute(Input("t:Camera UapSearchToolTestLater", new[] { "scene" }));
            bool foundLater = false;
            foreach (JsonNode item in second["items"].Items)
            {
                if (item["path"].AsString() == "UapSearchToolTestLater")
                {
                    foundLater = true;
                }
            }
            Assert.IsTrue(foundLater, "object created after a previous query was not found: " + JsonWriter.Write(second));
        }

        [Test]
        public void Execute_AssetProvider_FindsFreshMaterial_WithAssetPath()
        {
            AssetDatabase.CreateFolder("Assets", "UapSearchToolTests");
            string path = Folder + "/UapSearchToolTestMat.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.SaveAssets();

            JsonNode result = Execute(Input("t:Material UapSearchToolTestMat", new[] { "asset" }));

            Assert.GreaterOrEqual(result["totalMatches"].AsInt(), 1);
            bool found = false;
            foreach (JsonNode item in result["items"].Items)
            {
                Assert.AreEqual("asset", item["provider"].AsString());
                if (item["path"].AsString() == path)
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, "fresh asset must be found via the adb provider: " + JsonWriter.Write(result));
        }

        [Test]
        public void Execute_MaxResults_TruncatesAndSaysSo()
        {
            for (int i = 0; i < 3; i++)
            {
                new GameObject("UapSearchToolTestMany" + i).AddComponent<Light>();
            }
            JsonNode result = Execute(Input("UapSearchToolTestMany", new[] { "scene" }, 2));
            Assert.AreEqual(2, result["items"].Count);
            Assert.GreaterOrEqual(result["totalMatches"].AsInt(), 3);
            Assert.IsTrue(result["truncated"].AsBool(false));
        }

        [Test]
        public void Execute_NoMatches_EmptyItems_NoError()
        {
            JsonNode result = Execute(Input("UapSearchToolTestNothingLikeThisExists", new[] { "asset", "scene" }));
            Assert.AreEqual(0, result["items"].Count);
            Assert.AreEqual(0, result["totalMatches"].AsInt());
            Assert.IsFalse(result["truncated"].AsBool(true));
            Assert.AreEqual(2, result["providers"].Count);
        }
    }
}
