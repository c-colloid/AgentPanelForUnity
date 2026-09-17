using System;
using System.Collections.Generic;
using System.Globalization;
using L10n = Colloid.AgentPanel.UI.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// The plane-mode depth cues (docs/design-notes/2026-09-17-scene-
    /// sketch-strokes.md, decision S4). Three things tell the user where
    /// the sketch plane sits among the scene's geometry:
    ///
    ///   * the <b>section contour</b>: the plane's intersection with every
    ///     visible mesh whose bounds it crosses, drawn as lines on the
    ///     objects' surfaces (zTest Always, so a contour on a far wall
    ///     still reads through nearer objects);
    ///   * a <b>plane patch</b>: a screen-constant grid around the cursor's
    ///     plane point, depth-tested, so geometry in front of the plane
    ///     hides it and geometry behind shows through;
    ///   * the <b>depth readout</b>: the anchor's distance from the camera,
    ///     the plane axis, and -- when the view ray also hits a surface --
    ///     how far that surface is in front of or behind the plane point,
    ///     with a tie line between the two.
    ///
    /// The contour is the expensive part: it is recomputed only when the
    /// plane changes (or the scene's renderer set does), under a triangle
    /// budget, and cached as a flat segment list.
    /// </summary>
    public static class SceneStrokeDepthGuide
    {
        /// <summary>Triangles sectioned per recompute before the contour is cut short (large scenes).</summary>
        public const int TriangleBudget = 300000;
        /// <summary>Recompute the contour at least this often (seconds) so moved objects catch up.</summary>
        public const double RefreshInterval = 1.0;
        /// <summary>Grid cells per side of the plane patch.</summary>
        public const int PatchCells = 6;

        private static readonly List<Vector3> _segments = new List<Vector3>();
        private static Vector3[] _segmentArray = new Vector3[0];
        private static Plane _cachedPlane = new Plane(Vector3.up, float.NaN);
        private static int _cachedRendererCount = -1;
        private static double _cachedAt = -1;
        private static bool _partial;
        private static GUIStyle _labelStyle;

        /// <summary>True when the last contour recompute hit the triangle budget (the readout says "partial").</summary>
        public static bool Partial
        {
            get { return _partial; }
        }

        /// <summary>Number of contour segments in the cache (for tests / diagnostics).</summary>
        public static int SegmentCount
        {
            get { return _segments.Count / 2; }
        }

        /// <summary>Draws every cue for the current repaint. Caller sets/restores Handles state.</summary>
        public static void Draw(SceneView sceneView, Plane plane, Vector3 anchor, SceneStrokePlaneAxis axis,
            Vector3? hoverPoint, Vector3? hoverSurfacePoint, bool drawing)
        {
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null)
            {
                return;
            }
            RefreshContour(plane, SceneMarkerPin.CollectRenderers());
            Color contour = new Color(0.2f, 0.9f, 1f, 0.9f);
            if (_segmentArray.Length >= 2)
            {
                Handles.zTest = CompareFunction.Always;
                Handles.color = contour;
                Handles.DrawLines(_segmentArray);
            }
            Vector3 patchCentre = hoverPoint.HasValue ? hoverPoint.Value : anchor;
            DrawPlanePatch(patchCentre, plane.normal, contour);
            if (hoverPoint.HasValue && hoverSurfacePoint.HasValue)
            {
                Handles.zTest = CompareFunction.Always;
                Handles.color = new Color(1f, 0.85f, 0.1f, 0.9f);
                Handles.DrawDottedLine(hoverPoint.Value, hoverSurfacePoint.Value, 4f);
            }
            if (!drawing)
            {
                DrawReadout(camera, plane, anchor, axis, hoverPoint, hoverSurfacePoint);
            }
        }

        /// <summary>
        /// Recomputes the contour when the plane moved, the renderer set
        /// changed, or <see cref="RefreshInterval"/> elapsed. Public so a
        /// test can drive it with a known renderer list.
        /// </summary>
        public static void RefreshContour(Plane plane, IList<Renderer> renderers)
        {
            int count = renderers != null ? renderers.Count : 0;
            double now = EditorApplication.timeSinceStartup;
            bool samePlane = !float.IsNaN(_cachedPlane.distance)
                && Mathf.Abs(_cachedPlane.distance - plane.distance) < 1e-5f
                && Vector3.Dot(_cachedPlane.normal, plane.normal) > 0.99999f;
            if (samePlane && count == _cachedRendererCount && now - _cachedAt < RefreshInterval)
            {
                return;
            }
            _cachedPlane = plane;
            _cachedRendererCount = count;
            _cachedAt = now;
            _segments.Clear();
            _partial = false;
            int budget = TriangleBudget;
            for (int i = 0; i < count && budget > 0; i++)
            {
                Renderer renderer = renderers[i];
                if (!SceneMeshRaycaster.IsPickable(renderer) || !SceneStrokeGeometry.BoundsCrossesPlane(renderer.bounds, plane))
                {
                    continue;
                }
                Mesh mesh;
                Matrix4x4 matrix;
                if (!SceneMeshRaycaster.TryGetMesh(renderer, out mesh, out matrix))
                {
                    continue;
                }
                Vector3[] vertices;
                int[] triangles;
                try
                {
                    vertices = mesh.vertices;
                    triangles = mesh.triangles;
                }
                catch (Exception)
                {
                    // A non-readable mesh in an odd context: skip it rather than break the repaint.
                    continue;
                }
                int triangleCount = triangles.Length / 3;
                if (triangleCount > budget)
                {
                    _partial = true;
                    triangleCount = budget;
                    var cut = new int[triangleCount * 3];
                    Array.Copy(triangles, cut, cut.Length);
                    triangles = cut;
                }
                budget -= triangleCount;
                SceneStrokeGeometry.SectionTriangles(vertices, triangles, matrix, plane, _segments);
            }
            if (budget <= 0)
            {
                _partial = true;
            }
            _segmentArray = _segments.ToArray();
        }

        /// <summary>Screen-constant grid patch in the plane around <paramref name="centre"/>, depth-tested.</summary>
        private static void DrawPlanePatch(Vector3 centre, Vector3 normal, Color color)
        {
            Vector3 n = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(n, u);
            float half = HandleUtility.GetHandleSize(centre) * 1.2f;
            float cell = half * 2f / PatchCells;
            Handles.zTest = CompareFunction.LessEqual;
            Handles.color = new Color(color.r, color.g, color.b, 0.35f);
            for (int i = 0; i <= PatchCells; i++)
            {
                float offset = -half + cell * i;
                Handles.DrawLine(centre + u * offset - v * half, centre + u * offset + v * half);
                Handles.DrawLine(centre - u * half + v * offset, centre + u * half + v * offset);
            }
            Handles.zTest = CompareFunction.Always;
            Handles.color = color;
            Handles.DrawWireDisc(centre, n, half * 0.08f);
        }

        private static void DrawReadout(Camera camera, Plane plane, Vector3 anchor, SceneStrokePlaneAxis axis,
            Vector3? hoverPoint, Vector3? hoverSurfacePoint)
        {
            EnsureLabelStyle();
            float depth = Vector3.Dot(anchor - camera.transform.position, camera.transform.forward);
            string axisName;
            switch (axis)
            {
                case SceneStrokePlaneAxis.X: axisName = "X"; break;
                case SceneStrokePlaneAxis.Y: axisName = "Y"; break;
                case SceneStrokePlaneAxis.Z: axisName = "Z"; break;
                default: axisName = L10n.S.CtxSketchAxisCamera; break;
            }
            string line1 = L10n.F(L10n.S.CtxSketchDepthReadoutFmt, depth.ToString("F2", CultureInfo.InvariantCulture), axisName)
                + (_partial ? " " + L10n.S.CtxSketchContourPartial : string.Empty);
            string line2 = null;
            if (hoverPoint.HasValue && hoverSurfacePoint.HasValue)
            {
                float delta = Vector3.Dot(hoverSurfacePoint.Value - hoverPoint.Value, camera.transform.forward);
                string amount = Mathf.Abs(delta).ToString("F2", CultureInfo.InvariantCulture);
                line2 = delta >= 0f
                    ? L10n.F(L10n.S.CtxSketchSurfaceBehindFmt, amount)
                    : L10n.F(L10n.S.CtxSketchSurfaceInFrontFmt, amount);
            }
            string text = line2 != null ? line1 + "\n" + line2 + "\n" + L10n.S.CtxSketchKeyHints : line1 + "\n" + L10n.S.CtxSketchKeyHints;
            Vector2 gui = hoverPoint.HasValue
                ? HandleUtility.WorldToGUIPoint(hoverPoint.Value) + new Vector2(18f, 18f)
                : new Vector2(12f, 12f);
            Handles.BeginGUI();
            var content = new GUIContent(text);
            Vector2 size = _labelStyle.CalcSize(content);
            GUI.Label(new Rect(gui.x, gui.y, size.x, size.y), content, _labelStyle);
            Handles.EndGUI();
        }

        private static void EnsureLabelStyle()
        {
            if (_labelStyle != null)
            {
                return;
            }
            _labelStyle = new GUIStyle
            {
                fontSize = 11,
                padding = new RectOffset(6, 6, 3, 3),
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false
            };
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.normal.background = Texture2D.grayTexture;
        }

        /// <summary>Test seam: drops the contour cache.</summary>
        internal static void ResetForTests()
        {
            _segments.Clear();
            _segmentArray = new Vector3[0];
            _cachedPlane = new Plane(Vector3.up, float.NaN);
            _cachedRendererCount = -1;
            _cachedAt = -1;
            _partial = false;
        }
    }
}
