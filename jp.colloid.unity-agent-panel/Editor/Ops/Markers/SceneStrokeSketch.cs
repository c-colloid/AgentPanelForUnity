using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using L10n = Colloid.AgentPanel.UI.L10n;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>Which plane the plane-mode sketch lies in (design note 2026-09-17, decision S3).</summary>
    public enum SceneStrokePlaneAxis
    {
        /// <summary>Through the anchor, facing the Scene camera (re-taken from the camera at every stroke start).</summary>
        CameraFacing,
        /// <summary>World YZ plane through the anchor (normal +X).</summary>
        X,
        /// <summary>World XZ plane through the anchor (normal +Y).</summary>
        Y,
        /// <summary>World XY plane through the anchor (normal +Z).</summary>
        Z
    }

    /// <summary>
    /// Sketch input (docs/design-notes/2026-09-17-scene-sketch-strokes.md
    /// sections 3 and 4): while <see cref="Armed"/>, a left drag in a Scene
    /// view draws one stroke -- on the sketch plane in
    /// <see cref="SceneStrokeMode.Plane"/> mode, on whatever mesh surface
    /// is under the cursor in <see cref="SceneStrokeMode.Surface"/> mode --
    /// which is simplified on release, stored in
    /// <see cref="SceneStrokeStore"/> and announced through
    /// <see cref="StrokePlaced"/> with a ready-made context-chip payload.
    /// Plane mode also owns the depth controls (wheel / [ ] / F / X Y Z C)
    /// and hands the current plane to <see cref="SceneStrokeDepthGuide"/>
    /// for the on-screen depth cues. Escape leaves the mode. The
    /// Scene-view toolbar (<see cref="SceneAgentToolsOverlay"/>) and the
    /// context bar's sketch menu both drive <see cref="Armed"/> /
    /// <see cref="Mode"/>.
    /// </summary>
    public static class SceneStrokeSketch
    {
        /// <summary>Samples closer than this on screen are skipped while dragging.</summary>
        public const float MinSampleGuiDistance = 2f;
        /// <summary>Depth step per wheel notch / bracket key, as a fraction of the current depth.</summary>
        public const float DepthStepFraction = 0.05f;
        /// <summary>Lower bound on one depth step (world units).</summary>
        public const float DepthStepMin = 0.05f;
        /// <summary>Points shown in the chip payload before "uap_stroke_list returns all".</summary>
        public const int PayloadPointCount = 24;

        private static bool _armed;
        private static bool _installed;
        private static SceneStrokeMode _mode = SceneStrokeMode.Surface;
        private static SceneStrokePlaneAxis _axis = SceneStrokePlaneAxis.CameraFacing;
        private static Vector3 _anchor;
        private static Vector3 _planeNormal = Vector3.back;
        private static bool _drawing;
        private static Vector2 _lastGui;
        private static readonly List<Vector3> _rawPoints = new List<Vector3>();
        private static readonly List<Vector3> _rawNormals = new List<Vector3>();
        private static readonly List<string> _rawHits = new List<string>();

        /// <summary>Raised when <see cref="Armed"/> or <see cref="Mode"/> changes (toolbar and panel mirror it).</summary>
        public static event Action ArmedChanged;

        /// <summary>Raised after a stroke was stored: the stroke (with its id) and the chip payload text.</summary>
        public static event Action<SceneStroke, string> StrokePlaced;

        /// <summary>The would-be sample under the cursor while armed (null when outside or, in surface mode, off any surface).</summary>
        public static Vector3? HoverPoint;
        /// <summary>Plane mode: the surface the view ray hits behind/before the plane point (null when it hits nothing).</summary>
        public static Vector3? HoverSurfacePoint;

        /// <summary>True while a Scene-view drag draws a stroke.</summary>
        public static bool Armed
        {
            get { return _armed; }
            set
            {
                if (_armed == value)
                {
                    return;
                }
                _armed = value;
                if (!_armed)
                {
                    CancelDrawing();
                    HoverPoint = null;
                    HoverSurfacePoint = null;
                }
                else
                {
                    // One marking tool at a time: the pin and the sketch
                    // share the Scene-view click, so arming one disarms
                    // the other (the toolbar shows them as one tool set).
                    SceneMarkerPin.Armed = false;
                    InitializePlaneIfNeeded();
                }
                RaiseArmedChanged();
            }
        }

        /// <summary>Plane or surface. Changing it while armed keeps the mode armed (an in-progress stroke is discarded).</summary>
        public static SceneStrokeMode Mode
        {
            get { return _mode; }
            set
            {
                if (_mode == value)
                {
                    return;
                }
                CancelDrawing();
                _mode = value;
                if (_armed)
                {
                    InitializePlaneIfNeeded();
                }
                RaiseArmedChanged();
            }
        }

        /// <summary>One call for the toolbar / menu: sets the mode and arms.</summary>
        public static void Arm(SceneStrokeMode mode)
        {
            if (_armed && _mode == mode)
            {
                return;
            }
            CancelDrawing();
            _mode = mode;
            _armed = true;
            SceneMarkerPin.Armed = false;
            InitializePlaneIfNeeded();
            RaiseArmedChanged();
        }

        /// <summary>True while armed in the given mode (the toolbar toggles read this).</summary>
        public static bool IsArmedIn(SceneStrokeMode mode)
        {
            return _armed && _mode == mode;
        }

        public static SceneStrokePlaneAxis PlaneAxis
        {
            get { return _axis; }
        }

        /// <summary>Plane mode: the point the sketch plane passes through.</summary>
        public static Vector3 Anchor
        {
            get { return _anchor; }
        }

        /// <summary>Plane mode: the sketch plane's unit normal.</summary>
        public static Vector3 PlaneNormal
        {
            get { return _planeNormal; }
        }

        public static Plane CurrentPlane
        {
            get { return new Plane(_planeNormal, _anchor); }
        }

        /// <summary>True between MouseDown and MouseUp of a stroke.</summary>
        public static bool IsDrawing
        {
            get { return _drawing; }
        }

        /// <summary>The raw samples of the stroke being drawn (read-only view for the repaint).</summary>
        public static IList<Vector3> RawPoints
        {
            get { return _rawPoints; }
        }

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
        }

        // -- Plane management -------------------------------------------------------------

        /// <summary>
        /// Picks the anchor when plane mode is armed: the newest user pin,
        /// else the selection's bounds centre, else the Scene view's pivot
        /// (design note section 4). The plane faces the camera.
        /// </summary>
        private static void InitializePlaneIfNeeded()
        {
            if (_mode != SceneStrokeMode.Plane)
            {
                return;
            }
            _anchor = PickInitialAnchor(SceneView.lastActiveSceneView);
            _axis = SceneStrokePlaneAxis.CameraFacing;
            RefreshPlaneNormal(SceneView.lastActiveSceneView);
        }

        internal static Vector3 PickInitialAnchor(SceneView view)
        {
            SceneMarker[] markers = SceneMarkerStore.Snapshot();
            for (int i = markers.Length - 1; i >= 0; i--)
            {
                Vector3 position;
                if (markers[i].Origin == SceneMarkerOrigin.User && SceneMarkerRenderer.TryResolvePosition(markers[i], out position))
                {
                    return position;
                }
            }
            GameObject selected = Selection.activeGameObject;
            if (selected != null)
            {
                var renderer = selected.GetComponent<Renderer>();
                return renderer != null ? renderer.bounds.center : selected.transform.position;
            }
            return view != null ? view.pivot : Vector3.zero;
        }

        /// <summary>Recomputes the plane normal for the current axis; camera-facing takes the camera's view direction.</summary>
        private static void RefreshPlaneNormal(SceneView view)
        {
            switch (_axis)
            {
                case SceneStrokePlaneAxis.X: _planeNormal = Vector3.right; return;
                case SceneStrokePlaneAxis.Y: _planeNormal = Vector3.up; return;
                case SceneStrokePlaneAxis.Z: _planeNormal = Vector3.forward; return;
            }
            Camera camera = view != null ? view.camera : null;
            _planeNormal = camera != null ? -camera.transform.forward : Vector3.back;
        }

        /// <summary>Sets the plane axis (X/Y/Z keys, C for camera-facing) and repaints.</summary>
        public static void SetPlaneAxis(SceneStrokePlaneAxis axis, SceneView view)
        {
            _axis = axis;
            RefreshPlaneNormal(view);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Moves the anchor one step away from (<paramref name="farther"/>)
        /// or towards the camera. A camera-facing plane moves along the
        /// view direction. An axis plane ALWAYS moves along its own normal
        /// (the only move that changes where it cuts), in whichever sense
        /// points away from the camera; seen edge-on, "farther" is the
        /// normal's positive side. (Reported 2026-09-17: a Z plane viewed
        /// from above did not move on the wheel, because the step used
        /// to fall back to the view direction.)
        /// </summary>
        public static void StepDepth(bool farther, Camera camera)
        {
            if (camera == null)
            {
                return;
            }
            Vector3 forward = camera.transform.forward;
            float step = StepFor(camera);
            Vector3 direction = StepDirection(_axis, _planeNormal, forward);
            _anchor += direction * (farther ? step : -step);
            SceneView.RepaintAll();
        }

        /// <summary>One depth step for the current anchor: 5% of its distance along the view axis, at least <see cref="DepthStepMin"/>.</summary>
        private static float StepFor(Camera camera)
        {
            float depth = Mathf.Max(0f, Vector3.Dot(_anchor - camera.transform.position, camera.transform.forward));
            return Mathf.Max(DepthStepMin, depth * DepthStepFraction);
        }

        /// <summary>Pure: the unit direction a "farther" step moves the anchor (see <see cref="StepDepth"/>).</summary>
        public static Vector3 StepDirection(SceneStrokePlaneAxis axis, Vector3 planeNormal, Vector3 viewForward)
        {
            if (axis == SceneStrokePlaneAxis.CameraFacing || planeNormal.sqrMagnitude < 1e-8f)
            {
                return viewForward;
            }
            Vector3 n = planeNormal.normalized;
            float along = Vector3.Dot(n, viewForward);
            return along < 0f ? -n : n;
        }

        /// <summary>Depth of the anchor along the camera's view axis (the number the depth label shows).</summary>
        public static float DepthFrom(Camera camera)
        {
            if (camera == null)
            {
                return 0f;
            }
            return Vector3.Dot(_anchor - camera.transform.position, camera.transform.forward);
        }

        // -- Scene GUI -------------------------------------------------------------------

        private static void OnSceneGui(SceneView sceneView)
        {
            if (!_armed)
            {
                return;
            }
            Event e = Event.current;
            if (e == null)
            {
                return;
            }
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Armed = false;
                e.Use();
                return;
            }
            if (_mode == SceneStrokeMode.Plane && HandlePlaneControls(e, sceneView))
            {
                return;
            }
            switch (e.type)
            {
                case EventType.MouseMove:
                    UpdateHover(e.mousePosition, sceneView);
                    sceneView.Repaint();
                    return;
                case EventType.MouseLeaveWindow:
                    HoverPoint = null;
                    HoverSurfacePoint = null;
                    sceneView.Repaint();
                    return;
                case EventType.Repaint:
                    Draw(sceneView);
                    return;
                case EventType.MouseDown:
                    if (e.button != 0 || e.alt)
                    {
                        return;
                    }
                    BeginStroke(e.mousePosition, sceneView);
                    e.Use();
                    return;
                case EventType.MouseDrag:
                    if (!_drawing)
                    {
                        UpdateHover(e.mousePosition, sceneView);
                        sceneView.Repaint();
                        return;
                    }
                    if (e.button == 0)
                    {
                        AddSample(e.mousePosition, e.shift, sceneView);
                        e.Use();
                    }
                    sceneView.Repaint();
                    return;
                case EventType.MouseUp:
                    if (_drawing && e.button == 0)
                    {
                        AddSample(e.mousePosition, e.shift, sceneView);
                        EndStroke(sceneView);
                        e.Use();
                    }
                    return;
            }
        }

        /// <summary>Wheel and keys of plane mode; true when the event was consumed.</summary>
        private static bool HandlePlaneControls(Event e, SceneView sceneView)
        {
            if (_drawing)
            {
                return false;
            }
            if (e.type == EventType.ScrollWheel)
            {
                StepDepth(e.delta.y > 0f, sceneView.camera);
                UpdateHover(e.mousePosition, sceneView);
                e.Use();
                return true;
            }
            if (e.type != EventType.KeyDown)
            {
                return false;
            }
            switch (e.keyCode)
            {
                case KeyCode.LeftBracket:
                    StepDepth(false, sceneView.camera);
                    break;
                case KeyCode.RightBracket:
                    StepDepth(true, sceneView.camera);
                    break;
                case KeyCode.F:
                    SnapDepthToSurface(e.mousePosition);
                    break;
                case KeyCode.X:
                    SetPlaneAxis(SceneStrokePlaneAxis.X, sceneView);
                    break;
                case KeyCode.Y:
                    SetPlaneAxis(SceneStrokePlaneAxis.Y, sceneView);
                    break;
                case KeyCode.Z:
                    SetPlaneAxis(SceneStrokePlaneAxis.Z, sceneView);
                    break;
                case KeyCode.C:
                    SetPlaneAxis(SceneStrokePlaneAxis.CameraFacing, sceneView);
                    break;
                default:
                    return false;
            }
            UpdateHover(e.mousePosition, sceneView);
            e.Use();
            return true;
        }

        /// <summary>F: puts the plane through the surface under the cursor (no-op when the ray hits nothing).</summary>
        private static void SnapDepthToSurface(Vector2 guiPoint)
        {
            Vector3 point;
            Vector3 normal;
            string hit;
            if (SceneMarkerPin.TryRaycastSurface(HandleUtility.GUIPointToWorldRay(guiPoint),
                SceneMarkerPin.CollectRenderers(), out point, out normal, out hit))
            {
                _anchor = point;
                SceneView.RepaintAll();
            }
        }

        private static void UpdateHover(Vector2 guiPoint, SceneView sceneView)
        {
            Vector3 point;
            Vector3 normal;
            string hit;
            Ray ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            if (_mode == SceneStrokeMode.Plane)
            {
                HoverPoint = TryPlanePoint(ray, out point) ? point : (Vector3?)null;
                HoverSurfacePoint = SceneMarkerPin.TryRaycastSurface(ray, SceneMarkerPin.CollectRenderers(),
                    out point, out normal, out hit) ? point : (Vector3?)null;
                return;
            }
            HoverSurfacePoint = null;
            HoverPoint = SceneMarkerPin.TryRaycastSurface(ray, SceneMarkerPin.CollectRenderers(),
                out point, out normal, out hit) ? point : (Vector3?)null;
        }

        private static bool TryPlanePoint(Ray ray, out Vector3 point)
        {
            float enter;
            point = Vector3.zero;
            if (!CurrentPlane.Raycast(ray, out enter) || enter <= 0f)
            {
                return false;
            }
            point = ray.GetPoint(enter);
            return true;
        }

        private static void BeginStroke(Vector2 guiPoint, SceneView sceneView)
        {
            CancelDrawing();
            if (_mode == SceneStrokeMode.Plane && _axis == SceneStrokePlaneAxis.CameraFacing)
            {
                // The plane faces the camera as it is NOW; it stays put for
                // the whole stroke even if the view moves (decision S3).
                RefreshPlaneNormal(sceneView);
            }
            _drawing = true;
            _lastGui = guiPoint - new Vector2(1000f, 1000f);
            AddSample(guiPoint, false, sceneView);
        }

        private static void AddSample(Vector2 guiPoint, bool straight, SceneView sceneView)
        {
            if (!_drawing)
            {
                return;
            }
            if (_rawPoints.Count > 0 && Vector2.Distance(guiPoint, _lastGui) < MinSampleGuiDistance && !straight)
            {
                return;
            }
            Ray ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            Vector3 point;
            Vector3 normal;
            string hit;
            if (_mode == SceneStrokeMode.Plane)
            {
                if (!TryPlanePoint(ray, out point))
                {
                    return;
                }
                normal = _planeNormal;
                hit = string.Empty;
            }
            else if (!SceneMarkerPin.TryRaycastSurface(ray, SceneMarkerPin.CollectRenderers(), out point, out normal, out hit))
            {
                // Surface mode only draws on surfaces (decision S5).
                return;
            }
            if (straight && _rawPoints.Count >= 2)
            {
                // Shift: a straight line from the first sample to the cursor.
                _rawPoints.RemoveRange(1, _rawPoints.Count - 1);
                _rawNormals.RemoveRange(1, _rawNormals.Count - 1);
                _rawHits.RemoveRange(1, _rawHits.Count - 1);
            }
            _rawPoints.Add(point);
            _rawNormals.Add(normal);
            _rawHits.Add(hit ?? string.Empty);
            _lastGui = guiPoint;
            HoverPoint = point;
        }

        private static void EndStroke(SceneView sceneView)
        {
            _drawing = false;
            SceneStroke stroke = BuildStroke(_mode, _rawPoints, _rawNormals, _rawHits, _anchor, _planeNormal);
            _rawPoints.Clear();
            _rawNormals.Clear();
            _rawHits.Clear();
            if (stroke == null)
            {
                SceneView.RepaintAll();
                return;
            }
            // The payload names the id and number the store is about to
            // assign, so the persisted Note (written by Add) is final.
            stroke.Number = SceneStrokeStore.NextNumber(SceneStrokeStore.Snapshot());
            stroke.Note = FormatPayload(stroke.Number, stroke, SceneStrokeStore.NextId);
            SceneStrokeStore.Add(stroke);
            Action<SceneStroke, string> handler = StrokePlaced;
            if (handler != null)
            {
                handler(stroke, stroke.Note);
            }
        }

        private static void CancelDrawing()
        {
            _drawing = false;
            _rawPoints.Clear();
            _rawNormals.Clear();
            _rawHits.Clear();
        }

        private static void RaiseArmedChanged()
        {
            Action handler = ArmedChanged;
            if (handler != null)
            {
                handler();
            }
            SceneView.RepaintAll();
        }

        // -- Pure pieces (EditMode-testable) ------------------------------------------------

        /// <summary>
        /// Turns raw samples into a stored stroke (decision S6): Douglas-
        /// Peucker at <see cref="SceneStrokeGeometry.ToleranceFor"/> of the
        /// length, then uniform decimation down to
        /// <see cref="SceneStroke.MaxPoints"/>; per-point normals and hit
        /// paths follow the kept indices; distinct hit paths become
        /// <see cref="SceneStroke.HitPaths"/>. Null for fewer than two
        /// distinct points. Plane strokes record the plane and carry no
        /// per-point normals or hits.
        /// </summary>
        public static SceneStroke BuildStroke(SceneStrokeMode mode, IList<Vector3> points, IList<Vector3> normals,
            IList<string> hits, Vector3 planeOrigin, Vector3 planeNormal)
        {
            if (points == null || points.Count < 2)
            {
                return null;
            }
            float length = SceneStrokeGeometry.Length(points);
            if (length <= 0f)
            {
                return null;
            }
            List<int> keep = SceneStrokeGeometry.SimplifyIndices(points, SceneStrokeGeometry.ToleranceFor(length));
            if (keep.Count > SceneStroke.MaxPoints)
            {
                List<int> thin = SceneStrokeGeometry.DecimateIndices(keep.Count, SceneStroke.MaxPoints);
                var reduced = new List<int>(thin.Count);
                for (int i = 0; i < thin.Count; i++)
                {
                    reduced.Add(keep[thin[i]]);
                }
                keep = reduced;
            }
            var stroke = new SceneStroke { Mode = mode };
            var pathIndex = new Dictionary<string, int>();
            for (int i = 0; i < keep.Count; i++)
            {
                int index = keep[i];
                stroke.Points.Add(points[index]);
                if (mode != SceneStrokeMode.Surface)
                {
                    continue;
                }
                stroke.Normals.Add(normals != null && index < normals.Count ? normals[index] : Vector3.up);
                string path = hits != null && index < hits.Count ? hits[index] : null;
                if (string.IsNullOrEmpty(path))
                {
                    stroke.HitIndices.Add(-1);
                    continue;
                }
                int hitIndex;
                if (!pathIndex.TryGetValue(path, out hitIndex))
                {
                    hitIndex = stroke.HitPaths.Count;
                    stroke.HitPaths.Add(path);
                    pathIndex[path] = hitIndex;
                }
                stroke.HitIndices.Add(hitIndex);
            }
            if (mode == SceneStrokeMode.Plane)
            {
                stroke.PlaneOrigin = planeOrigin;
                stroke.PlaneNormal = planeNormal.sqrMagnitude > 0.0001f ? planeNormal.normalized : Vector3.up;
            }
            stroke.Closed = SceneStrokeGeometry.IsClosed(stroke.Points);
            return stroke;
        }

        /// <summary>The chip payload (context block WITHOUT wire delimiters).</summary>
        public static string FormatPayload(int number, SceneStroke stroke, int id)
        {
            var sb = new StringBuilder(320);
            sb.Append(L10n.F(L10n.S.CtxStrokePayloadHeaderFmt, number));
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadModeFmt, SceneStroke.ModeName(stroke.Mode)));
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadStatsFmt,
                stroke.Points.Count, stroke.Length.ToString("F2", CultureInfo.InvariantCulture),
                stroke.Closed ? L10n.S.CtxStrokePayloadClosedYes : L10n.S.CtxStrokePayloadClosedNo));
            if (stroke.Mode == SceneStrokeMode.Plane)
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadPlaneFmt, Format(stroke.PlaneOrigin), Format(stroke.PlaneNormal)));
            }
            else if (stroke.HitPaths.Count > 0)
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadObjectsFmt, string.Join(", ", stroke.HitPaths.ToArray())));
            }
            Bounds bounds = stroke.Bounds;
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadBoundsFmt, Format(bounds.min), Format(bounds.max)));
            List<int> shown = SceneStrokeGeometry.DecimateIndices(stroke.Points.Count, PayloadPointCount);
            var pointText = new StringBuilder(shown.Count * 24);
            for (int i = 0; i < shown.Count; i++)
            {
                if (i > 0)
                {
                    pointText.Append(", ");
                }
                pointText.Append(Format(stroke.Points[shown[i]]));
            }
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadPointsFmt, pointText.ToString()));
            if (shown.Count < stroke.Points.Count)
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadMorePointsFmt, shown.Count, stroke.Points.Count, id));
            }
            else
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadAllPointsFmt, id));
            }
            return sb.ToString();
        }

        internal static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F2", CultureInfo.InvariantCulture) + ", "
                + v.y.ToString("F2", CultureInfo.InvariantCulture) + ", "
                + v.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
        }

        // -- Repaint -----------------------------------------------------------------------

        private static void Draw(SceneView sceneView)
        {
            Color previousColor = Handles.color;
            var previousZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            if (_mode == SceneStrokeMode.Plane)
            {
                SceneStrokeDepthGuide.Draw(sceneView, CurrentPlane, _anchor, _axis, HoverPoint, HoverSurfacePoint, _drawing);
            }
            Color ink = new Color(0.2f, 0.9f, 1f);
            if (_rawPoints.Count >= 2)
            {
                Handles.color = ink;
                Handles.DrawAAPolyLine(4f, _rawPoints.ToArray());
            }
            if (HoverPoint.HasValue)
            {
                Vector3 p = HoverPoint.Value;
                float size = HandleUtility.GetHandleSize(p) * 0.06f;
                Handles.color = new Color(ink.r, ink.g, ink.b, 0.9f);
                Vector3 facing = sceneView.camera != null ? -sceneView.camera.transform.forward : Vector3.up;
                Handles.DrawSolidDisc(p, facing, size);
            }
            Handles.zTest = previousZTest;
            Handles.color = previousColor;
        }

        /// <summary>Test seam: disarms and forgets the plane without raising events.</summary>
        internal static void ResetForTests()
        {
            _armed = false;
            _mode = SceneStrokeMode.Surface;
            _axis = SceneStrokePlaneAxis.CameraFacing;
            _anchor = Vector3.zero;
            _planeNormal = Vector3.back;
            CancelDrawing();
            HoverPoint = null;
            HoverSurfacePoint = null;
        }
    }
}
