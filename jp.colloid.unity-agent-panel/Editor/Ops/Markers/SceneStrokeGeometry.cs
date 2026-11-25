using System;
using System.Collections.Generic;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// Pure geometry behind the sketch strokes (design note 2026-09-17-
    /// scene-sketch-strokes.md sections 2 and 3): polyline length, bounds,
    /// the closed-loop test, Douglas-Peucker simplification, uniform
    /// decimation, and the plane-vs-triangle-mesh section that draws the
    /// depth contour in plane mode. No Unity editor state -- EditMode
    /// tests pin these directly.
    /// </summary>
    public static class SceneStrokeGeometry
    {
        /// <summary>Simplification tolerance as a fraction of the stroke length (decision S6).</summary>
        public const float SimplifyFraction = 0.005f;
        /// <summary>Lower bound on the simplification tolerance (world units).</summary>
        public const float SimplifyMinTolerance = 0.001f;
        /// <summary>A stroke is closed when its ends are within this fraction of its length of each other.</summary>
        public const float ClosedFraction = 0.02f;
        /// <summary>Lower bound on the closed-loop distance (world units), for tiny strokes.</summary>
        public const float ClosedMinDistance = 0.01f;

        /// <summary>Sum of segment lengths; 0 for fewer than two points.</summary>
        public static float Length(IList<Vector3> points)
        {
            if (points == null || points.Count < 2)
            {
                return 0f;
            }
            float length = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                length += Vector3.Distance(points[i - 1], points[i]);
            }
            return length;
        }

        /// <summary>Axis-aligned bounds of the points; a zero-size bounds at the origin when empty.</summary>
        public static Bounds Bounds(IList<Vector3> points)
        {
            if (points == null || points.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++)
            {
                bounds.Encapsulate(points[i]);
            }
            return bounds;
        }

        /// <summary>
        /// True when the polyline has at least three points and its last
        /// point lies within max(<see cref="ClosedFraction"/> x length,
        /// <see cref="ClosedMinDistance"/>) of its first.
        /// </summary>
        public static bool IsClosed(IList<Vector3> points)
        {
            if (points == null || points.Count < 3)
            {
                return false;
            }
            float threshold = Mathf.Max(ClosedMinDistance, Length(points) * ClosedFraction);
            return Vector3.Distance(points[0], points[points.Count - 1]) <= threshold;
        }

        /// <summary>The tolerance <see cref="Simplify"/> uses for a stroke of the given length.</summary>
        public static float ToleranceFor(float length)
        {
            return Mathf.Max(SimplifyMinTolerance, length * SimplifyFraction);
        }

        /// <summary>
        /// Douglas-Peucker: the indices (ascending) of the points to keep
        /// so that no dropped point is farther than <paramref name="tolerance"/>
        /// from the simplified polyline. First and last are always kept.
        /// Iterative (explicit stack) so a long stroke cannot overflow.
        /// </summary>
        public static List<int> SimplifyIndices(IList<Vector3> points, float tolerance)
        {
            var keep = new List<int>();
            if (points == null || points.Count == 0)
            {
                return keep;
            }
            if (points.Count < 3 || tolerance <= 0f)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    keep.Add(i);
                }
                return keep;
            }
            var flags = new bool[points.Count];
            flags[0] = true;
            flags[points.Count - 1] = true;
            var stack = new Stack<KeyValuePair<int, int>>();
            stack.Push(new KeyValuePair<int, int>(0, points.Count - 1));
            float toleranceSq = tolerance * tolerance;
            while (stack.Count > 0)
            {
                KeyValuePair<int, int> range = stack.Pop();
                int first = range.Key;
                int last = range.Value;
                if (last - first < 2)
                {
                    continue;
                }
                float bestSq = -1f;
                int bestIndex = -1;
                for (int i = first + 1; i < last; i++)
                {
                    float dSq = DistanceToSegmentSq(points[i], points[first], points[last]);
                    if (dSq > bestSq)
                    {
                        bestSq = dSq;
                        bestIndex = i;
                    }
                }
                if (bestIndex >= 0 && bestSq > toleranceSq)
                {
                    flags[bestIndex] = true;
                    stack.Push(new KeyValuePair<int, int>(first, bestIndex));
                    stack.Push(new KeyValuePair<int, int>(bestIndex, last));
                }
            }
            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i])
                {
                    keep.Add(i);
                }
            }
            return keep;
        }

        /// <summary>Convenience over <see cref="SimplifyIndices"/>: the kept points themselves.</summary>
        public static List<Vector3> Simplify(IList<Vector3> points, float tolerance)
        {
            List<int> keep = SimplifyIndices(points, tolerance);
            var result = new List<Vector3>(keep.Count);
            for (int i = 0; i < keep.Count; i++)
            {
                result.Add(points[keep[i]]);
            }
            return result;
        }

        /// <summary>
        /// Indices of at most <paramref name="max"/> points spread evenly
        /// over [0, count), always including the first and the last. Every
        /// index when count &lt;= max. Empty for max &lt; 1 or count 0.
        /// </summary>
        public static List<int> DecimateIndices(int count, int max)
        {
            var result = new List<int>();
            if (count <= 0 || max < 1)
            {
                return result;
            }
            if (count <= max)
            {
                for (int i = 0; i < count; i++)
                {
                    result.Add(i);
                }
                return result;
            }
            if (max == 1)
            {
                result.Add(0);
                return result;
            }
            for (int i = 0; i < max; i++)
            {
                int index = (int)Math.Round((double)i * (count - 1) / (max - 1));
                if (result.Count == 0 || result[result.Count - 1] != index)
                {
                    result.Add(index);
                }
            }
            return result;
        }

        /// <summary>Squared distance from <paramref name="p"/> to segment ab.</summary>
        public static float DistanceToSegmentSq(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abSq = ab.sqrMagnitude;
            if (abSq < 1e-12f)
            {
                return (p - a).sqrMagnitude;
            }
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSq);
            return (p - (a + ab * t)).sqrMagnitude;
        }

        /// <summary>
        /// Intersects a plane with a triangle mesh (decision S4): for every
        /// triangle the plane cuts, appends the two world-space endpoints of
        /// the cut segment to <paramref name="segments"/> (pairs, so the
        /// list feeds Handles.DrawLines / a Lines mesh directly). Triangles
        /// with a vertex exactly on the plane contribute a segment only when
        /// the other two straddle it; a triangle lying in the plane adds
        /// nothing (its edges belong to its neighbours). Returns how many
        /// segments were added.
        /// </summary>
        public static int SectionTriangles(Vector3[] vertices, int[] triangles, Matrix4x4 localToWorld,
            Plane plane, List<Vector3> segments)
        {
            if (vertices == null || triangles == null || segments == null)
            {
                return 0;
            }
            int added = 0;
            int vertexCount = vertices.Length;
            var world = new Vector3[3];
            var dist = new float[3];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t];
                int i1 = triangles[t + 1];
                int i2 = triangles[t + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
                {
                    continue;
                }
                world[0] = localToWorld.MultiplyPoint3x4(vertices[i0]);
                world[1] = localToWorld.MultiplyPoint3x4(vertices[i1]);
                world[2] = localToWorld.MultiplyPoint3x4(vertices[i2]);
                dist[0] = plane.GetDistanceToPoint(world[0]);
                dist[1] = plane.GetDistanceToPoint(world[1]);
                dist[2] = plane.GetDistanceToPoint(world[2]);
                bool anyPositive = dist[0] > 0f || dist[1] > 0f || dist[2] > 0f;
                bool anyNegative = dist[0] < 0f || dist[1] < 0f || dist[2] < 0f;
                if (!anyPositive || !anyNegative)
                {
                    continue;
                }
                int found = 0;
                Vector3 first = Vector3.zero;
                Vector3 second = Vector3.zero;
                for (int e = 0; e < 3 && found < 2; e++)
                {
                    int a = e;
                    int b = (e + 1) % 3;
                    float da = dist[a];
                    float db = dist[b];
                    Vector3 hit;
                    if (da == 0f)
                    {
                        hit = world[a];
                    }
                    else if ((da > 0f) == (db > 0f) || db == 0f)
                    {
                        // Same side, or the crossing is at b (picked up by the next edge).
                        continue;
                    }
                    else
                    {
                        float s = da / (da - db);
                        hit = world[a] + (world[b] - world[a]) * s;
                    }
                    if (found == 0)
                    {
                        first = hit;
                    }
                    else if ((hit - first).sqrMagnitude > 1e-12f)
                    {
                        second = hit;
                    }
                    else
                    {
                        continue;
                    }
                    found++;
                }
                if (found == 2)
                {
                    segments.Add(first);
                    segments.Add(second);
                    added++;
                }
            }
            return added;
        }

        /// <summary>True when <paramref name="bounds"/> reaches the plane (corners on both sides, or touching).</summary>
        public static bool BoundsCrossesPlane(Bounds bounds, Plane plane)
        {
            Vector3 c = bounds.center;
            Vector3 e = bounds.extents;
            float r = Mathf.Abs(plane.normal.x) * e.x + Mathf.Abs(plane.normal.y) * e.y + Mathf.Abs(plane.normal.z) * e.z;
            return Mathf.Abs(plane.GetDistanceToPoint(c)) <= r;
        }

        // -- Shapes and regions (design note 2026-10-09-sketch-shapes-navigation-regions.md) --

        /// <summary>Samples around an ellipse (decision T1).</summary>
        public const int EllipseSegments = 48;

        /// <summary>
        /// An orthonormal in-plane basis for a plane with the given normal:
        /// <paramref name="right"/> is the projection of
        /// <paramref name="preferredRight"/> into the plane (the camera's
        /// right for a camera-facing plane, so a dragged rectangle's edges
        /// run along the screen), falling back to a world axis when that
        /// is degenerate; <paramref name="up"/> completes the frame.
        /// </summary>
        public static void PlaneBasis(Vector3 normal, Vector3 preferredRight, out Vector3 right, out Vector3 up)
        {
            Vector3 n = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up;
            right = preferredRight - n * Vector3.Dot(preferredRight, n);
            if (right.sqrMagnitude < 1e-6f)
            {
                right = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right);
            }
            right.Normalize();
            up = Vector3.Cross(n, right).normalized;
        }

        /// <summary>
        /// Newell's normal of a (roughly planar) polygon: the direction of
        /// its signed area vector. Zero for fewer than three points or a
        /// degenerate polygon. Independent of the starting vertex.
        /// </summary>
        public static Vector3 NewellNormal(IList<Vector3> points)
        {
            if (points == null || points.Count < 3)
            {
                return Vector3.zero;
            }
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[(i + 1) % points.Count];
                sum += Vector3.Cross(a, b);
            }
            return sum.sqrMagnitude > 1e-12f ? sum.normalized : Vector3.zero;
        }

        /// <summary>Area of a planar polygon in world units squared (half the length of its area vector).</summary>
        public static float PolygonArea(IList<Vector3> points)
        {
            if (points == null || points.Count < 3)
            {
                return 0f;
            }
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < points.Count; i++)
            {
                sum += Vector3.Cross(points[i], points[(i + 1) % points.Count]);
            }
            return sum.magnitude * 0.5f;
        }

        /// <summary>Mean of the points (what the agent gets as a region's centre; the vertex centroid, not the area centroid).</summary>
        public static Vector3 Centroid(IList<Vector3> points)
        {
            if (points == null || points.Count == 0)
            {
                return Vector3.zero;
            }
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < points.Count; i++)
            {
                sum += points[i];
            }
            return sum / points.Count;
        }

        /// <summary>
        /// The four corners of the rectangle spanned by two points of a
        /// plane, in the plane's basis: a, (b along right), b, (a along
        /// right); counter-clockwise or clockwise depending on the drag.
        /// With <paramref name="square"/> the second corner is clamped so
        /// both edges share the shorter extent (Shift).
        /// </summary>
        public static List<Vector3> RectanglePoints(Vector3 a, Vector3 b, Vector3 right, Vector3 up, bool square)
        {
            Vector3 d = b - a;
            float dx = Vector3.Dot(d, right);
            float dy = Vector3.Dot(d, up);
            if (square)
            {
                float side = Mathf.Min(Mathf.Abs(dx), Mathf.Abs(dy));
                dx = Mathf.Sign(dx) * side;
                dy = Mathf.Sign(dy) * side;
            }
            return new List<Vector3>
            {
                a,
                a + right * dx,
                a + right * dx + up * dy,
                a + up * dy
            };
        }

        /// <summary>
        /// <see cref="EllipseSegments"/> points on the ellipse inscribed in
        /// the rectangle spanned by two points of a plane (a circle with
        /// <paramref name="circle"/>). Not repeated at the end: the stroke
        /// is marked closed instead.
        /// </summary>
        public static List<Vector3> EllipsePoints(Vector3 a, Vector3 b, Vector3 right, Vector3 up, bool circle, int segments)
        {
            Vector3 d = b - a;
            float dx = Vector3.Dot(d, right);
            float dy = Vector3.Dot(d, up);
            if (circle)
            {
                float side = Mathf.Min(Mathf.Abs(dx), Mathf.Abs(dy));
                dx = Mathf.Sign(dx) * side;
                dy = Mathf.Sign(dy) * side;
            }
            Vector3 centre = a + right * (dx * 0.5f) + up * (dy * 0.5f);
            float rx = Mathf.Abs(dx) * 0.5f;
            float ry = Mathf.Abs(dy) * 0.5f;
            int n = Mathf.Max(3, segments);
            var points = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n * Mathf.PI * 2f;
                points.Add(centre + right * (Mathf.Cos(t) * rx) + up * (Mathf.Sin(t) * ry));
            }
            return points;
        }

        /// <summary>
        /// Inserts points along each segment (closing segment included
        /// when <paramref name="closed"/>) so that no segment is longer
        /// than <paramref name="maxStep"/>, keeping the originals; the
        /// surface-mode shapes are draped onto the mesh point by point and
        /// need the extra samples to follow it. Never exceeds
        /// <paramref name="maxPoints"/> in total (the step is widened to
        /// fit). Fewer than two points come back unchanged.
        /// </summary>
        public static List<Vector3> Subdivide(IList<Vector3> points, bool closed, float maxStep, int maxPoints)
        {
            var result = new List<Vector3>();
            if (points == null)
            {
                return result;
            }
            if (points.Count < 2)
            {
                result.AddRange(points);
                return result;
            }
            int segments = points.Count - 1 + (closed ? 1 : 0);
            float total = Length(points) + (closed ? Vector3.Distance(points[points.Count - 1], points[0]) : 0f);
            float step = Mathf.Max(maxStep, 1e-4f);
            if (maxPoints > points.Count)
            {
                int spare = maxPoints - points.Count;
                step = Mathf.Max(step, total / Mathf.Max(1, spare));
            }
            for (int s = 0; s < segments; s++)
            {
                Vector3 a = points[s];
                Vector3 b = points[(s + 1) % points.Count];
                result.Add(a);
                float len = Vector3.Distance(a, b);
                int pieces = Mathf.Max(1, Mathf.CeilToInt(len / step - 1e-4f));
                for (int k = 1; k < pieces; k++)
                {
                    result.Add(Vector3.Lerp(a, b, (float)k / pieces));
                }
            }
            if (!closed)
            {
                result.Add(points[points.Count - 1]);
            }
            return result;
        }

        /// <summary>Projects points into the 2D frame (origin, right, up).</summary>
        public static List<Vector2> Project(IList<Vector3> points, Vector3 origin, Vector3 right, Vector3 up)
        {
            var result = new List<Vector2>(points != null ? points.Count : 0);
            if (points == null)
            {
                return result;
            }
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 d = points[i] - origin;
                result.Add(new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, up)));
            }
            return result;
        }

        /// <summary>Even-odd point-in-polygon test (works for concave and self-touching outlines).</summary>
        public static bool PointInPolygon(IList<Vector2> polygon, Vector2 point)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }
            bool inside = false;
            int count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > point.y) != (b.y > point.y))
                {
                    float x = (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
                    if (point.x < x)
                    {
                        inside = !inside;
                    }
                }
            }
            return inside;
        }

        /// <summary>Signed 2D area (positive counter-clockwise).</summary>
        public static float SignedArea(IList<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return 0f;
            }
            float sum = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                sum += a.x * b.y - b.x * a.y;
            }
            return sum * 0.5f;
        }

        /// <summary>
        /// Ear-clipping triangulation of a simple polygon (concave allowed,
        /// no holes): triples of indices into <paramref name="polygon"/>.
        /// Consecutive duplicate vertices are tolerated. A self-intersecting
        /// outline still yields a triangle list (the fill may overlap), never
        /// an exception. Empty for fewer than three vertices.
        /// </summary>
        public static List<int> Triangulate(IList<Vector2> polygon)
        {
            var triangles = new List<int>();
            if (polygon == null || polygon.Count < 3)
            {
                return triangles;
            }
            var indices = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                if (i > 0 && (polygon[i] - polygon[indices[indices.Count - 1]]).sqrMagnitude < 1e-12f)
                {
                    continue;
                }
                indices.Add(i);
            }
            if (indices.Count >= 2 && (polygon[indices[0]] - polygon[indices[indices.Count - 1]]).sqrMagnitude < 1e-12f)
            {
                indices.RemoveAt(indices.Count - 1);
            }
            if (indices.Count < 3)
            {
                return triangles;
            }
            bool ccw = SignedArea(Pick(polygon, indices)) >= 0f;
            int guard = 0;
            while (indices.Count > 3 && guard++ < 4 * polygon.Count * polygon.Count)
            {
                bool clipped = false;
                for (int i = 0; i < indices.Count; i++)
                {
                    int i0 = indices[(i + indices.Count - 1) % indices.Count];
                    int i1 = indices[i];
                    int i2 = indices[(i + 1) % indices.Count];
                    Vector2 a = polygon[i0];
                    Vector2 b = polygon[i1];
                    Vector2 c = polygon[i2];
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (ccw ? cross <= 1e-9f : cross >= -1e-9f)
                    {
                        continue; // reflex or degenerate corner
                    }
                    bool empty = true;
                    for (int k = 0; k < indices.Count && empty; k++)
                    {
                        int ik = indices[k];
                        if (ik == i0 || ik == i1 || ik == i2)
                        {
                            continue;
                        }
                        if (InTriangle(polygon[ik], a, b, c))
                        {
                            empty = false;
                        }
                    }
                    if (!empty)
                    {
                        continue;
                    }
                    triangles.Add(i0);
                    triangles.Add(i1);
                    triangles.Add(i2);
                    indices.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                {
                    // No ear found (self-intersecting input): clip the first
                    // corner anyway so the loop terminates with some fill.
                    triangles.Add(indices[indices.Count - 1]);
                    triangles.Add(indices[0]);
                    triangles.Add(indices[1]);
                    indices.RemoveAt(0);
                }
            }
            if (indices.Count == 3)
            {
                triangles.Add(indices[0]);
                triangles.Add(indices[1]);
                triangles.Add(indices[2]);
            }
            return triangles;
        }

        private static List<Vector2> Pick(IList<Vector2> polygon, List<int> indices)
        {
            var result = new List<Vector2>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                result.Add(polygon[indices[i]]);
            }
            return result;
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool hasNeg = d1 < -1e-9f || d2 < -1e-9f || d3 < -1e-9f;
            bool hasPos = d1 > 1e-9f || d2 > 1e-9f || d3 > 1e-9f;
            return !(hasNeg && hasPos);
        }
    }
}
