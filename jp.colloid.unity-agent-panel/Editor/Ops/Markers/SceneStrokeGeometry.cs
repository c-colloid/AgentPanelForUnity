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
    }
}
