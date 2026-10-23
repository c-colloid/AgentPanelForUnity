using System;
using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>How a stroke was drawn (design note 2026-09-17-scene-sketch-strokes.md section 2).</summary>
    public enum SceneStrokeMode
    {
        /// <summary>Drawn on a fixed sketch plane (depth chosen first).</summary>
        Plane,
        /// <summary>Drawn on whatever mesh surface was under the cursor (depth from raycast).</summary>
        Surface
    }

    /// <summary>
    /// How the points of a stroke were produced (design note
    /// 2026-10-09-sketch-shapes-navigation-regions.md, decision T1). The
    /// points are always a plain polyline; the shape tells the agent that
    /// a rectangle's four points are corners, an ellipse's samples lie on
    /// a curve, and so on.
    /// </summary>
    public enum SceneStrokeShape
    {
        /// <summary>A left drag, sampled as the mouse moves.</summary>
        Freehand,
        /// <summary>Two points: press and release.</summary>
        Line,
        /// <summary>Click-placed vertices; closed by clicking the first one again.</summary>
        Polyline,
        /// <summary>Four corners from a corner-to-corner drag (always closed).</summary>
        Rectangle,
        /// <summary>Sampled ellipse inscribed in a corner-to-corner drag (always closed).</summary>
        Ellipse
    }

    /// <summary>
    /// One user-drawn stroke in the Scene view (docs/design-notes/
    /// 2026-09-17-scene-sketch-strokes.md, decision S1): a world-space
    /// polyline plus, per point, the surface normal and the object it was
    /// drawn on (surface mode), or the plane it lies in (plane mode). Pure
    /// data, never a scene object; lives in <see cref="SceneStrokeStore"/>
    /// and round-trips through the package's own JSON for the SessionState
    /// sidecar that carries strokes across a domain reload.
    /// </summary>
    public sealed class SceneStroke
    {
        /// <summary>Hard cap on stored points per stroke (decision S6).</summary>
        public const int MaxPoints = 256;

        /// <summary>1-based, unique for the editor session; what uap_stroke_list reports as id.</summary>
        public int Id;
        /// <summary>The "S&lt;n&gt;" the user sees: lowest free number, reused after removal (like pin numbers).</summary>
        public int Number;
        public SceneStrokeMode Mode = SceneStrokeMode.Surface;
        /// <summary>Which tool produced the points (2026-10-09 note, T1).</summary>
        public SceneStrokeShape Shape = SceneStrokeShape.Freehand;
        /// <summary>
        /// Optional user-given name ("plaza", "spawn area"), shown in the
        /// Scene-view label and the chip and reported by uap_stroke_list
        /// (2026-10-09 note, decision T4). Empty when unnamed.
        /// </summary>
        public string Name = string.Empty;
        /// <summary>World-space points, in drawing order.</summary>
        public List<Vector3> Points = new List<Vector3>();
        /// <summary>Surface mode: normal per point (same count as Points). Plane mode: empty (see PlaneNormal).</summary>
        public List<Vector3> Normals = new List<Vector3>();
        /// <summary>Surface mode: index into <see cref="HitPaths"/> per point (-1 = none). Plane mode: empty.</summary>
        public List<int> HitIndices = new List<int>();
        /// <summary>Distinct hierarchy paths of the objects the stroke was drawn on, in first-hit order.</summary>
        public List<string> HitPaths = new List<string>();
        /// <summary>Plane mode: a point on the sketch plane.</summary>
        public Vector3 PlaneOrigin;
        /// <summary>Plane mode: the sketch plane's unit normal. Surface mode: unused.</summary>
        public Vector3 PlaneNormal = Vector3.up;
        /// <summary>True when the stroke ends where it started (a loop).</summary>
        public bool Closed;
        public Color Color = new Color(1f, 0.85f, 0.1f);
        /// <summary>EditorApplication.timeSinceStartup at creation.</summary>
        public double CreatedAt;
        /// <summary>Context-chip payload captured at completion, so the chip can be rebuilt after a reload.</summary>
        public string Note = string.Empty;

        /// <summary>"S3" -- the label the user sees and the chip carries.</summary>
        public string DisplayNumber
        {
            get { return "S" + (Number > 0 ? Number : Id).ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>"S3 plaza" when named, else "S3" -- the Scene-view label.</summary>
        public string DisplayLabel
        {
            get { return string.IsNullOrEmpty(Name) ? DisplayNumber : DisplayNumber + " " + Name; }
        }

        /// <summary>True for a closed polyline with at least three points: the stroke bounds an area (a region).</summary>
        public bool IsRegion
        {
            get { return Closed && Points.Count >= 3; }
        }

        /// <summary>Total polyline length in world units.</summary>
        public float Length
        {
            get { return SceneStrokeGeometry.Length(Points); }
        }

        /// <summary>Axis-aligned world bounds of the points (zero-size when empty).</summary>
        public Bounds Bounds
        {
            get { return SceneStrokeGeometry.Bounds(Points); }
        }

        /// <summary>Hierarchy path the point at <paramref name="index"/> was drawn on, or empty.</summary>
        public string HitPathAt(int index)
        {
            if (index < 0 || index >= HitIndices.Count)
            {
                return string.Empty;
            }
            int hit = HitIndices[index];
            return hit >= 0 && hit < HitPaths.Count ? HitPaths[hit] : string.Empty;
        }

        /// <summary>Parses a "mode" argument (case-insensitive). False for unknown values.</summary>
        public static bool TryParseMode(string text, out SceneStrokeMode mode)
        {
            mode = SceneStrokeMode.Surface;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            switch (text.Trim().ToLowerInvariant())
            {
                case "plane": mode = SceneStrokeMode.Plane; return true;
                case "surface": mode = SceneStrokeMode.Surface; return true;
            }
            return false;
        }

        public static string ModeName(SceneStrokeMode mode)
        {
            return mode == SceneStrokeMode.Plane ? "plane" : "surface";
        }

        /// <summary>The wire name of a shape ("freehand", "line", "polyline", "rectangle", "ellipse").</summary>
        public static string ShapeName(SceneStrokeShape shape)
        {
            switch (shape)
            {
                case SceneStrokeShape.Line: return "line";
                case SceneStrokeShape.Polyline: return "polyline";
                case SceneStrokeShape.Rectangle: return "rectangle";
                case SceneStrokeShape.Ellipse: return "ellipse";
                default: return "freehand";
            }
        }

        /// <summary>Number of shapes in <see cref="SceneStrokeShape"/> (the 1..N shape keys).</summary>
        public const int ShapeCount = 5;

        /// <summary>
        /// The ink for the stroke with the given S number: eight distinct
        /// hues cycled by number, so neighbouring regions of an area split
        /// read apart (2026-10-09 note, decision T5). Number 1 keeps the
        /// yellow every stroke used to have.
        /// </summary>
        public static Color PaletteColor(int number)
        {
            Color[] palette = Palette;
            int index = number <= 0 ? 0 : (number - 1) % palette.Length;
            return palette[index];
        }

        private static readonly Color[] Palette =
        {
            new Color(1f, 0.85f, 0.1f),
            new Color(0.35f, 0.85f, 1f),
            new Color(0.55f, 1f, 0.45f),
            new Color(1f, 0.5f, 0.35f),
            new Color(0.85f, 0.55f, 1f),
            new Color(1f, 0.65f, 0.85f),
            new Color(0.45f, 1f, 0.85f),
            new Color(1f, 0.95f, 0.6f)
        };

        // -- JSON (SessionState sidecar) ----------------------------------------------

        public JsonNode ToJson()
        {
            JsonNode node = JsonNode.NewObject()
                .Set("id", Id)
                .Set("number", Number)
                .Set("mode", (int)Mode)
                .Set("shape", (int)Shape)
                .Set("name", Name ?? string.Empty)
                .Set("p", FlattenVectors(Points))
                .Set("n", FlattenVectors(Normals))
                .Set("hi", FlattenInts(HitIndices))
                .Set("hp", FlattenStrings(HitPaths))
                .Set("ox", PlaneOrigin.x).Set("oy", PlaneOrigin.y).Set("oz", PlaneOrigin.z)
                .Set("nx", PlaneNormal.x).Set("ny", PlaneNormal.y).Set("nz", PlaneNormal.z)
                .Set("closed", Closed)
                .Set("color", "#" + ColorUtility.ToHtmlStringRGBA(Color))
                .Set("createdAt", CreatedAt)
                .Set("note", Note ?? string.Empty);
            return node;
        }

        /// <summary>Inverse of <see cref="ToJson"/>; null for a node that is not an object or has no points.</summary>
        public static SceneStroke FromJson(JsonNode node)
        {
            if (node == null || !node.IsObject)
            {
                return null;
            }
            Color color;
            if (!ColorUtility.TryParseHtmlString(node["color"].AsString("#FFD91AFF"), out color))
            {
                color = new Color(1f, 0.85f, 0.1f);
            }
            var stroke = new SceneStroke
            {
                Id = node["id"].AsInt(0),
                Number = node["number"].AsInt(0),
                Mode = node["mode"].AsInt(1) == 0 ? SceneStrokeMode.Plane : SceneStrokeMode.Surface,
                Shape = ParseShape(node["shape"].AsInt(0)),
                Name = node["name"].AsString(string.Empty),
                Points = UnflattenVectors(node["p"]),
                Normals = UnflattenVectors(node["n"]),
                HitIndices = UnflattenInts(node["hi"]),
                HitPaths = UnflattenStrings(node["hp"]),
                PlaneOrigin = new Vector3((float)node["ox"].AsDouble(0), (float)node["oy"].AsDouble(0), (float)node["oz"].AsDouble(0)),
                PlaneNormal = new Vector3((float)node["nx"].AsDouble(0), (float)node["ny"].AsDouble(1), (float)node["nz"].AsDouble(0)),
                Closed = node["closed"].AsBool(false),
                Color = color,
                CreatedAt = node["createdAt"].AsDouble(0),
                Note = node["note"].AsString(string.Empty)
            };
            return stroke.Points.Count == 0 ? null : stroke;
        }

        private static SceneStrokeShape ParseShape(int value)
        {
            return value >= 0 && value < ShapeCount ? (SceneStrokeShape)value : SceneStrokeShape.Freehand;
        }

        private static JsonNode FlattenVectors(IList<Vector3> vectors)
        {
            JsonNode array = JsonNode.NewArray();
            if (vectors != null)
            {
                for (int i = 0; i < vectors.Count; i++)
                {
                    array.Add(vectors[i].x).Add(vectors[i].y).Add(vectors[i].z);
                }
            }
            return array;
        }

        private static List<Vector3> UnflattenVectors(JsonNode array)
        {
            var result = new List<Vector3>();
            if (array == null || !array.IsArray)
            {
                return result;
            }
            for (int i = 0; i + 2 < array.Count; i += 3)
            {
                result.Add(new Vector3((float)array[i].AsDouble(0), (float)array[i + 1].AsDouble(0), (float)array[i + 2].AsDouble(0)));
            }
            return result;
        }

        private static JsonNode FlattenInts(IList<int> values)
        {
            JsonNode array = JsonNode.NewArray();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    array.Add(values[i]);
                }
            }
            return array;
        }

        private static List<int> UnflattenInts(JsonNode array)
        {
            var result = new List<int>();
            if (array == null || !array.IsArray)
            {
                return result;
            }
            for (int i = 0; i < array.Count; i++)
            {
                result.Add(array[i].AsInt(-1));
            }
            return result;
        }

        private static JsonNode FlattenStrings(IList<string> values)
        {
            JsonNode array = JsonNode.NewArray();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    array.Add(values[i] ?? string.Empty);
                }
            }
            return array;
        }

        private static List<string> UnflattenStrings(JsonNode array)
        {
            var result = new List<string>();
            if (array == null || !array.IsArray)
            {
                return result;
            }
            for (int i = 0; i < array.Count; i++)
            {
                result.Add(array[i].AsString(string.Empty));
            }
            return result;
        }
    }
}
