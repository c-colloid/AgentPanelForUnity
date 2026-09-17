using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.Ops.Markers;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_stroke_list against the real SceneStrokeStore (reset per test):
    /// registration and metadata (module "markers", read-only), the empty
    /// text, the JSON listing shape from design note 2026-09-17-scene-
    /// sketch-strokes.md section 5, decimation with max_points, and the
    /// single-stroke id path.
    /// </summary>
    [TestFixture]
    public class UapStrokeListToolTests
    {
        [SetUp]
        public void SetUp()
        {
            SceneStrokeStore.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            SceneStrokeStore.ResetForTests();
        }

        private static string Text(JsonNode result)
        {
            return result[0]["text"].AsString(string.Empty);
        }

        private static SceneStroke Stroke(SceneStrokeMode mode, int pointCount)
        {
            var stroke = new SceneStroke { Mode = mode };
            for (int i = 0; i < pointCount; i++)
            {
                stroke.Points.Add(new Vector3(i, 0f, 0f));
                if (mode == SceneStrokeMode.Surface)
                {
                    stroke.Normals.Add(Vector3.up);
                    stroke.HitIndices.Add(0);
                }
            }
            if (mode == SceneStrokeMode.Surface)
            {
                stroke.HitPaths.Add("Env/Floor");
            }
            else
            {
                stroke.PlaneOrigin = new Vector3(0f, 1f, 0f);
                stroke.PlaneNormal = Vector3.up;
            }
            return stroke;
        }

        [Test]
        public void Registered_InMarkersModule_AndReadOnly()
        {
            ToolRegistry registry = ToolRegistry.CreateDefault(false);
            IUapTool tool = registry.Find("uap_stroke_list");
            Assert.IsNotNull(tool);
            Assert.AreEqual("markers", tool.Module);
            Assert.IsTrue(tool.ReadOnly);
            Assert.IsFalse(tool.Undoable);
            Assert.IsFalse(tool.InputSchema["additionalProperties"].AsBool(true));
        }

        [Test]
        public void Execute_Empty_SaysSo()
        {
            string text = Text(new UapStrokeListTool().Execute(JsonNode.NewObject()));
            StringAssert.Contains("No sketch strokes", text);
        }

        [Test]
        public void Execute_ListsSurfaceAndPlaneStrokes_AsJson()
        {
            SceneStrokeStore.Add(Stroke(SceneStrokeMode.Surface, 3));
            SceneStrokeStore.Add(Stroke(SceneStrokeMode.Plane, 2));
            JsonNode root = JsonParser.Parse(Text(new UapStrokeListTool().Execute(JsonNode.NewObject())));
            Assert.AreEqual(2, root["count"].AsInt());
            JsonNode surface = root["strokes"][0];
            Assert.AreEqual(1, surface["id"].AsInt());
            Assert.AreEqual("S1", surface["number"].AsString());
            Assert.AreEqual("surface", surface["mode"].AsString());
            Assert.AreEqual(3, surface["pointCount"].AsInt());
            Assert.AreEqual(2.0, surface["length"].AsDouble(), 1e-6);
            Assert.IsFalse(surface["closed"].AsBool(true));
            Assert.AreEqual("Env/Floor", surface["objects"][0].AsString());
            Assert.AreEqual(3, surface["samples"].Count);
            Assert.AreEqual(1.0, surface["samples"][1]["p"][0].AsDouble(), 1e-6);
            Assert.AreEqual(1.0, surface["samples"][1]["n"][1].AsDouble(), 1e-6);
            Assert.AreEqual(0, surface["samples"][1]["obj"].AsInt(-1));
            Assert.IsFalse(surface["decimated"].AsBool(false));
            Assert.AreEqual(2.0, surface["bounds"]["max"][0].AsDouble(), 1e-6);
            JsonNode plane = root["strokes"][1];
            Assert.AreEqual("plane", plane["mode"].AsString());
            Assert.AreEqual(1.0, plane["plane"]["origin"][1].AsDouble(), 1e-6);
            Assert.AreEqual(1.0, plane["plane"]["normal"][1].AsDouble(), 1e-6);
            Assert.IsFalse(plane["samples"][0]["n"].IsArray, "plane samples carry no per-point normal");
        }

        [Test]
        public void Execute_MaxPoints_Decimates_AndIdReturnsEveryPoint()
        {
            SceneStrokeStore.Add(Stroke(SceneStrokeMode.Surface, 100));
            var tool = new UapStrokeListTool();
            JsonNode listed = JsonParser.Parse(Text(tool.Execute(JsonNode.NewObject().Set("max_points", 10))));
            Assert.AreEqual(10, listed["strokes"][0]["samples"].Count);
            Assert.IsTrue(listed["strokes"][0]["decimated"].AsBool(false));
            Assert.AreEqual(99.0, listed["strokes"][0]["samples"][9]["p"][0].AsDouble(), 1e-6, "the last point is always kept");
            JsonNode defaulted = JsonParser.Parse(Text(tool.Execute(JsonNode.NewObject())));
            Assert.AreEqual(UapStrokeListTool.DefaultMaxPoints, defaulted["strokes"][0]["samples"].Count);
            JsonNode one = JsonParser.Parse(Text(tool.Execute(JsonNode.NewObject().Set("id", 1))));
            Assert.AreEqual(1, one["count"].AsInt());
            Assert.AreEqual(100, one["strokes"][0]["samples"].Count, "id lists every point");
            StringAssert.Contains("No stroke #7", Text(tool.Execute(JsonNode.NewObject().Set("id", 7))));
        }
    }
}
