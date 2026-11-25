using System.Collections.Generic;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// What a CLOSED stroke means as a region (docs/design-notes/
    /// 2026-10-09-sketch-shapes-navigation-regions.md, decisions T3 / T4):
    /// the plane it lies in (its sketch plane for a plane stroke, the
    /// Newell plane through its centroid for a surface stroke), its area,
    /// and the scene objects inside it -- the "which objects are in area
    /// A" an area split of an existing world needs. The frame and the
    /// containment test are pure (EditMode-tested); only
    /// <see cref="CollectContents"/> walks the scene.
    /// </summary>
    public static class SceneStrokeRegion
    {
        /// <summary>Default cap on the object paths a listing names per region.</summary>
        public const int DefaultMaxObjects = 32;

        /// <summary>An in-plane 2D frame: origin plus orthonormal right / up / normal.</summary>
        public struct Frame
        {
            public Vector3 Origin;
            public Vector3 Right;
            public Vector3 Up;
            public Vector3 Normal;

            public Vector2 Project(Vector3 point)
            {
                Vector3 d = point - Origin;
                return new Vector2(Vector3.Dot(d, Right), Vector3.Dot(d, Up));
            }
        }

        /// <summary>
        /// The frame a stroke's outline is measured in: a plane stroke uses
        /// its sketch plane; a surface stroke uses the Newell normal of its
        /// points (falling back to the mean of its sample normals, then to
        /// world up) through its centroid.
        /// </summary>
        public static Frame FrameFor(SceneStroke stroke)
        {
            var frame = new Frame();
            if (stroke == null)
            {
                frame.Normal = Vector3.up;
                SceneStrokeGeometry.PlaneBasis(frame.Normal, Vector3.right, out frame.Right, out frame.Up);
                return frame;
            }
            frame.Origin = SceneStrokeGeometry.Centroid(stroke.Points);
            if (stroke.Mode == SceneStrokeMode.Plane && stroke.PlaneNormal.sqrMagnitude > 1e-8f)
            {
                frame.Normal = stroke.PlaneNormal.normalized;
            }
            else
            {
                Vector3 normal = SceneStrokeGeometry.NewellNormal(stroke.Points);
                if (normal.sqrMagnitude < 0.5f)
                {
                    normal = Vector3.zero;
                    for (int i = 0; i < stroke.Normals.Count; i++)
                    {
                        normal += stroke.Normals[i];
                    }
                    normal = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up;
                }
                frame.Normal = normal;
            }
            SceneStrokeGeometry.PlaneBasis(frame.Normal, Vector3.right, out frame.Right, out frame.Up);
            return frame;
        }

        /// <summary>The outline in the frame's 2D coordinates.</summary>
        public static List<Vector2> Outline(SceneStroke stroke, Frame frame)
        {
            var outline = new List<Vector2>(stroke.Points.Count);
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                outline.Add(frame.Project(stroke.Points[i]));
            }
            return outline;
        }

        /// <summary>
        /// Pure containment rule (decision T4): a candidate with bounds
        /// <paramref name="centre"/> / <paramref name="extents"/> is inside
        /// when its centre projects into the outline AND it is not a
        /// "floor" -- an object whose projected footprint covers the whole
        /// outline (the ground an area is drawn on would otherwise be
        /// inside every area). <paramref name="outlineBounds"/> is the
        /// outline's 2D min/max.
        /// </summary>
        public static bool Contains(IList<Vector2> outline, Rect outlineBounds, Frame frame, Vector3 centre, Vector3 extents)
        {
            Vector2 c = frame.Project(centre);
            if (!SceneStrokeGeometry.PointInPolygon(outline, c))
            {
                return false;
            }
            float rx = Mathf.Abs(Vector3.Dot(frame.Right, new Vector3(extents.x, 0f, 0f)))
                + Mathf.Abs(Vector3.Dot(frame.Right, new Vector3(0f, extents.y, 0f)))
                + Mathf.Abs(Vector3.Dot(frame.Right, new Vector3(0f, 0f, extents.z)));
            float ry = Mathf.Abs(Vector3.Dot(frame.Up, new Vector3(extents.x, 0f, 0f)))
                + Mathf.Abs(Vector3.Dot(frame.Up, new Vector3(0f, extents.y, 0f)))
                + Mathf.Abs(Vector3.Dot(frame.Up, new Vector3(0f, 0f, extents.z)));
            bool coversOutline = c.x - rx <= outlineBounds.xMin && c.x + rx >= outlineBounds.xMax
                && c.y - ry <= outlineBounds.yMin && c.y + ry >= outlineBounds.yMax;
            return !coversOutline;
        }

        /// <summary>2D min/max of an outline.</summary>
        public static Rect OutlineBounds(IList<Vector2> outline)
        {
            if (outline == null || outline.Count == 0)
            {
                return new Rect();
            }
            Vector2 min = outline[0];
            Vector2 max = outline[0];
            for (int i = 1; i < outline.Count; i++)
            {
                min = Vector2.Min(min, outline[i]);
                max = Vector2.Max(max, outline[i]);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>
        /// Hierarchy paths of the visible scene objects (renderers in the
        /// current stage) inside the region, in hierarchy order, at most
        /// <paramref name="max"/> of them; <paramref name="total"/> is the
        /// full count. The objects the stroke was drawn ON are excluded
        /// (they are the ground, not the contents). Empty for a stroke
        /// that is not a region.
        /// </summary>
        public static List<string> CollectContents(SceneStroke stroke, int max, out int total)
        {
            total = 0;
            var result = new List<string>();
            if (stroke == null || !stroke.IsRegion)
            {
                return result;
            }
            Frame frame = FrameFor(stroke);
            List<Vector2> outline = Outline(stroke, frame);
            Rect bounds = OutlineBounds(outline);
            var ground = new HashSet<string>(stroke.HitPaths);
            var seen = new HashSet<Transform>();
            Renderer[] renderers = UnityObjectCompat.FindAll<Renderer>();
            var inside = new List<string>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!SceneMeshRaycaster.IsPickable(renderer) || !seen.Add(renderer.transform))
                {
                    continue;
                }
                Bounds b = renderer.bounds;
                if (!Contains(outline, bounds, frame, b.center, b.extents))
                {
                    continue;
                }
                string path = PathOf(renderer);
                if (ground.Contains(path))
                {
                    continue;
                }
                inside.Add(path);
            }
            inside.Sort(string.CompareOrdinal);
            total = inside.Count;
            for (int i = 0; i < inside.Count && i < max; i++)
            {
                result.Add(inside[i]);
            }
            return result;
        }

        private static string PathOf(Renderer renderer)
        {
            return renderer != null ? UapAddressing.DescribeHierarchyPath(renderer.transform) : string.Empty;
        }

        /// <summary>
        /// Triangle list (indices into the stroke's points) that fills the
        /// region, from ear clipping in the region's frame; empty when the
        /// stroke is not a region.
        /// </summary>
        public static List<int> FillTriangles(SceneStroke stroke)
        {
            if (stroke == null || !stroke.IsRegion)
            {
                return new List<int>();
            }
            Frame frame = FrameFor(stroke);
            return SceneStrokeGeometry.Triangulate(Outline(stroke, frame));
        }
    }
}
