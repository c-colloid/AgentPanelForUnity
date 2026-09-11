using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.Ops.Markers;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_marker_add / uap_marker_list / uap_marker_clear against the real
    /// SceneMarkerStore (reset per test) and a real scene object for the
    /// target-follow path -- the same contract the 2026-09-07 end-to-end
    /// spike exercised through the MCP handler. Metadata is pinned here
    /// too (module "markers", list read-only, add/clear not).
    /// </summary>
    [TestFixture]
    public class UapMarkerToolsTests
    {
        private const string TargetName = "UapMarkerToolsTest_Target";
        private GameObject _target;

        [SetUp]
        public void SetUp()
        {
            SceneMarkerStore.ResetForTests();
            _target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _target.name = TargetName;
            _target.transform.position = new Vector3(2f, 0f, 3f);
        }

        [TearDown]
        public void TearDown()
        {
            SceneMarkerStore.ResetForTests();
            if (_target != null)
            {
                UnityEngine.Object.DestroyImmediate(_target);
            }
        }

        private static JsonNode Vec(double x, double y, double z)
        {
            return JsonNode.NewArray().Add(x).Add(y).Add(z);
        }

        private static string Text(JsonNode result)
        {
            return result[0]["text"].AsString(string.Empty);
        }

        [Test]
        public void Metadata_ModuleAndFlags()
        {
            ToolRegistry registry = ToolRegistry.CreateDefault(false);
            IUapTool add = registry.Find("uap_marker_add");
            IUapTool list = registry.Find("uap_marker_list");
            IUapTool clear = registry.Find("uap_marker_clear");
            Assert.IsNotNull(add); Assert.IsNotNull(list); Assert.IsNotNull(clear);
            Assert.AreEqual("markers", add.Module);
            Assert.AreEqual("markers", list.Module);
            Assert.AreEqual("markers", clear.Module);
            Assert.IsFalse(add.ReadOnly); Assert.IsTrue(list.ReadOnly); Assert.IsFalse(clear.ReadOnly);
            Assert.IsFalse(add.Undoable); Assert.IsFalse(list.Undoable); Assert.IsFalse(clear.Undoable);
            foreach (IUapTool tool in new[] { add, list, clear })
            {
                Assert.AreEqual("object", tool.InputSchema["type"].AsString());
                Assert.IsTrue(tool.InputSchema["properties"].IsObject);
                Assert.IsFalse(string.IsNullOrEmpty(tool.Description));
            }
        }

        [Test]
        public void Add_Point_ReturnsIdAndStores()
        {
            JsonNode result = new UapMarkerAddTool().Execute(JsonNode.NewObject()
                .Set("position", Vec(1, 2, 3)).Set("label", "  Spawn\npoint ").Set("color", "cyan"));

            StringAssert.StartsWith("{\"id\":1}", Text(result));
            Assert.AreEqual(1, SceneMarkerStore.Count);
            SceneMarker marker = SceneMarkerStore.Find(1);
            Assert.AreEqual(new Vector3(1, 2, 3), marker.Position);
            Assert.AreEqual("Spawn point", marker.Label, "label is normalized on the way in");
            Assert.AreEqual(SceneMarkerOrigin.Agent, marker.Origin);
        }

        [Test]
        public void Add_Target_ResolvesToFullPath_AndFollowsTheObject()
        {
            JsonNode result = new UapMarkerAddTool().Execute(JsonNode.NewObject()
                .Set("kind", "box").Set("target", TargetName));

            StringAssert.Contains("following " + TargetName, Text(result));
            SceneMarker marker = SceneMarkerStore.Find(1);
            Vector3 shown;
            Assert.IsTrue(SceneMarkerRenderer.TryResolvePosition(marker, out shown));
            Assert.AreEqual(new Vector3(2f, 0f, 3f), shown);

            _target.transform.position = new Vector3(5f, 0f, 3f);
            Assert.IsTrue(SceneMarkerRenderer.TryResolvePosition(marker, out shown));
            Assert.AreEqual(new Vector3(5f, 0f, 3f), shown, "the marker follows the object's bounds centre");
        }

        [Test]
        public void Add_Arrow_RequiresTo()
        {
            var ex = Assert.Throws<ArgumentException>(() => new UapMarkerAddTool().Execute(
                JsonNode.NewObject().Set("kind", "arrow").Set("position", Vec(0, 0, 0))));
            StringAssert.Contains("\"to\"", ex.Message);
            Assert.AreEqual(0, SceneMarkerStore.Count);
        }

        [Test]
        public void Add_RejectsBadInputs_WithArgumentException()
        {
            var tool = new UapMarkerAddTool();
            Assert.Throws<ArgumentException>(() => tool.Execute(JsonNode.NewObject()), "position or target");
            Assert.Throws<ArgumentException>(() => tool.Execute(JsonNode.NewObject().Set("kind", "sphere").Set("position", Vec(0, 0, 0))));
            Assert.Throws<ArgumentException>(() => tool.Execute(JsonNode.NewObject().Set("position", JsonNode.NewArray().Add(1.0).Add(2.0))));
            Assert.Throws<ArgumentException>(() => tool.Execute(JsonNode.NewObject().Set("position", Vec(0, 0, 0)).Set("color", "no-such-colour")));
            Assert.Throws<ArgumentException>(() => tool.Execute(JsonNode.NewObject().Set("position", Vec(0, 0, 0)).Set("size", -1)));
            Assert.AreEqual(0, SceneMarkerStore.Count, "a rejected call must not leave a marker behind");
        }

        [Test]
        public void Add_UnknownTarget_IsInvalidOperation()
        {
            Assert.Throws<InvalidOperationException>(() => new UapMarkerAddTool().Execute(
                JsonNode.NewObject().Set("target", "UapMarkerToolsTest_DoesNotExist")));
        }

        [Test]
        public void Add_PastTheCap_WarnsAndEvicts()
        {
            var tool = new UapMarkerAddTool();
            for (int i = 0; i < SceneMarkerStore.MaxMarkers; i++)
            {
                tool.Execute(JsonNode.NewObject().Set("position", Vec(i, 0, 0)));
            }
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("position", Vec(99, 0, 0)));

            StringAssert.Contains("cap", Text(result));
            Assert.AreEqual(SceneMarkerStore.MaxMarkers, SceneMarkerStore.Count);
            Assert.IsNull(SceneMarkerStore.Find(1), "the oldest agent marker was evicted");
        }

        [Test]
        public void List_EmptyAndPopulated()
        {
            StringAssert.Contains("No markers", Text(new UapMarkerListTool().Execute(JsonNode.NewObject())));
            new UapMarkerAddTool().Execute(JsonNode.NewObject().Set("position", Vec(1, 2, 3)).Set("label", "A"));
            new UapMarkerAddTool().Execute(JsonNode.NewObject().Set("kind", "arrow").Set("position", Vec(0, 0, 0)).Set("to", Vec(0, 1, 0)).Set("ttl_seconds", 30));

            string text = Text(new UapMarkerListTool().Execute(JsonNode.NewObject()));

            StringAssert.Contains("markers (2)", text);
            StringAssert.Contains("#1 point agent \"A\" at (1.00, 2.00, 3.00)", text);
            StringAssert.Contains("#2 arrow agent", text);
            StringAssert.Contains("-> (0.00, 1.00, 0.00)", text);
            StringAssert.Contains("ttl=30s", text);
        }

        [Test]
        public void Clear_ById_AgentOnly_AndAll()
        {
            var add = new UapMarkerAddTool();
            add.Execute(JsonNode.NewObject().Set("position", Vec(0, 0, 0)));
            add.Execute(JsonNode.NewObject().Set("position", Vec(1, 0, 0)));
            SceneMarkerStore.Add(new SceneMarker { Origin = SceneMarkerOrigin.User, Label = "pin" });
            var clear = new UapMarkerClearTool();

            StringAssert.Contains("Removed marker #1", Text(clear.Execute(JsonNode.NewObject().Set("id", 1))));
            StringAssert.Contains("No marker #1", Text(clear.Execute(JsonNode.NewObject().Set("id", 1))));
            StringAssert.Contains("Removed 1 agent marker", Text(clear.Execute(JsonNode.NewObject())));
            Assert.AreEqual(1, SceneMarkerStore.Count, "the user's pin survives a default clear");
            StringAssert.Contains("including user pins", Text(clear.Execute(JsonNode.NewObject().Set("all", true))));
            Assert.AreEqual(0, SceneMarkerStore.Count);
        }
    }
}
