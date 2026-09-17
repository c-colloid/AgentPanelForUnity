using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops.Markers;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Lists the sketch strokes the user drew in the Scene view (design
    /// note 2026-09-17-scene-sketch-strokes.md section 5) as structured
    /// JSON: id, S number, mode, point count, length, closed flag, bounds,
    /// the objects a surface stroke was drawn on or the plane a plane
    /// stroke lies in, and the samples themselves (position, normal, hit
    /// object index). ReadOnly: it only reads the store, so it may be
    /// auto-approved like uap_marker_list (pinned by
    /// UapReadOnlyToolMetadataTests).
    /// </summary>
    public sealed class UapStrokeListTool : IUapTool
    {
        /// <summary>Default sample cap per stroke in a listing; longer strokes are decimated evenly.</summary>
        public const int DefaultMaxPoints = 64;

        public string Name
        {
            get { return "uap_stroke_list"; }
        }

        public string Description
        {
            get
            {
                return "Lists the sketch strokes the user drew in the Unity Scene view (plane or surface"
                    + " mode) as JSON: world-space points with normals and the objects they were drawn on,"
                    + " length, closed flag, bounds and the sketch plane. Use id to get one stroke with"
                    + " every point. Read-only.";
            }
        }

        public string Module
        {
            get { return "markers"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("id", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "List just this stroke, with every point (the id from the chip or a previous listing)."))
                        .Set("max_points", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Samples per stroke in the listing; longer strokes are decimated evenly and flagged"
                                + " decimated:true. Default " + DefaultMaxPoints + "; 0 = every point.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            int id = input["id"].AsInt(0);
            int maxPoints = input["max_points"].AsInt(id > 0 ? 0 : DefaultMaxPoints);
            SceneStroke[] strokes = SceneStrokeStore.Snapshot();
            if (id > 0)
            {
                SceneStroke one = SceneStrokeStore.Find(id);
                if (one == null)
                {
                    return UapToolResults.Text("No stroke #" + id + " is shown in the Scene view.");
                }
                strokes = new[] { one };
            }
            if (strokes.Length == 0)
            {
                return UapToolResults.Text("No sketch strokes are shown in the Scene view.");
            }
            return UapToolResults.Text(JsonWriter.Write(Describe(strokes, maxPoints)));
        }

        /// <summary>Pure listing shape (design note section 5); public so tests can pin it without the store.</summary>
        public static JsonNode Describe(IList<SceneStroke> strokes, int maxPoints)
        {
            JsonNode list = JsonNode.NewArray();
            for (int i = 0; i < strokes.Count; i++)
            {
                list.Add(Describe(strokes[i], maxPoints));
            }
            return JsonNode.NewObject().Set("count", strokes.Count).Set("strokes", list);
        }

        public static JsonNode Describe(SceneStroke stroke, int maxPoints)
        {
            Bounds bounds = stroke.Bounds;
            JsonNode node = JsonNode.NewObject()
                .Set("id", stroke.Id)
                .Set("number", stroke.DisplayNumber)
                .Set("mode", SceneStroke.ModeName(stroke.Mode))
                .Set("pointCount", stroke.Points.Count)
                .Set("length", Round(stroke.Length))
                .Set("closed", stroke.Closed)
                .Set("bounds", JsonNode.NewObject().Set("min", Vec(bounds.min)).Set("max", Vec(bounds.max)));
            if (stroke.Mode == SceneStrokeMode.Plane)
            {
                node.Set("plane", JsonNode.NewObject().Set("origin", Vec(stroke.PlaneOrigin)).Set("normal", Vec(stroke.PlaneNormal)));
            }
            else
            {
                JsonNode objects = JsonNode.NewArray();
                for (int i = 0; i < stroke.HitPaths.Count; i++)
                {
                    objects.Add(stroke.HitPaths[i]);
                }
                node.Set("objects", objects);
            }
            List<int> shown = maxPoints > 0
                ? SceneStrokeGeometry.DecimateIndices(stroke.Points.Count, maxPoints)
                : SceneStrokeGeometry.DecimateIndices(stroke.Points.Count, stroke.Points.Count);
            JsonNode samples = JsonNode.NewArray();
            for (int i = 0; i < shown.Count; i++)
            {
                int index = shown[i];
                JsonNode sample = JsonNode.NewObject().Set("p", Vec(stroke.Points[index]));
                if (stroke.Mode == SceneStrokeMode.Surface)
                {
                    if (index < stroke.Normals.Count)
                    {
                        sample.Set("n", Vec(stroke.Normals[index]));
                    }
                    if (index < stroke.HitIndices.Count && stroke.HitIndices[index] >= 0)
                    {
                        sample.Set("obj", stroke.HitIndices[index]);
                    }
                }
                samples.Add(sample);
            }
            node.Set("samples", samples);
            if (shown.Count < stroke.Points.Count)
            {
                node.Set("decimated", true);
            }
            return node;
        }

        private static JsonNode Vec(Vector3 v)
        {
            return JsonNode.NewArray().Add(Round(v.x)).Add(Round(v.y)).Add(Round(v.z));
        }

        private static double Round(float value)
        {
            return System.Math.Round((double)value, 4);
        }
    }
}
