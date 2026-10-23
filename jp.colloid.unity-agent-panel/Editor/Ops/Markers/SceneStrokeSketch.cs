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
    /// sections 3 and 4; shapes, navigation and regions in
    /// docs/design-notes/2026-10-09-sketch-shapes-navigation-regions.md):
    /// while <see cref="Armed"/>, the left mouse button draws one stroke
    /// in a Scene view -- on the sketch plane in
    /// <see cref="SceneStrokeMode.Plane"/> mode, on whatever mesh surface
    /// is under the cursor in <see cref="SceneStrokeMode.Surface"/> mode --
    /// with the current <see cref="Shape"/>: a freehand drag, a straight
    /// line, a click-by-click polyline, or a corner-to-corner rectangle or
    /// ellipse. The stroke is simplified (freehand only) on completion,
    /// stored in <see cref="SceneStrokeStore"/> and announced through
    /// <see cref="StrokePlaced"/> with a ready-made context-chip payload.
    ///
    /// Plane mode owns the depth controls (Ctrl+wheel / [ ] / F / X Y Z C)
    /// and hands the current plane to <see cref="SceneStrokeDepthGuide"/>
    /// for the on-screen depth cues. The plain wheel, the right and middle
    /// buttons and Alt are left to the Scene view, so the view can be
    /// zoomed, panned and orbited while a polyline is being placed
    /// (decision T2). Escape cancels the stroke in progress, or leaves the
    /// mode when nothing is being drawn. The Scene-view toolbar
    /// (<see cref="SceneAgentToolsOverlay"/>) and the context bar's sketch
    /// menu both drive <see cref="Armed"/> / <see cref="Mode"/> /
    /// <see cref="Shape"/>.
    /// </summary>
    public static class SceneStrokeSketch
    {
        /// <summary>Samples closer than this on screen are skipped while dragging.</summary>
        public const float MinSampleGuiDistance = 2f;
        /// <summary>Clicking within this many GUI points of a polyline's first vertex closes it.</summary>
        public const float CloseGuiDistance = 10f;
        /// <summary>Depth step per wheel notch / bracket key, as a fraction of the current depth.</summary>
        public const float DepthStepFraction = 0.05f;
        /// <summary>Lower bound on one depth step (world units).</summary>
        public const float DepthStepMin = 0.05f;
        /// <summary>Points shown in the chip payload before "uap_stroke_list returns all".</summary>
        public const int PayloadPointCount = 24;
        /// <summary>Surface-mode shapes are draped point by point; edges are subdivided to about this many pieces around the outline.</summary>
        public const int DrapeSegments = 64;

        private static bool _armed;
        private static bool _installed;
        private static SceneStrokeMode _mode = SceneStrokeMode.Surface;
        private static SceneStrokeShape _shape = SceneStrokeShape.Freehand;
        private static SceneStrokePlaneAxis _axis = SceneStrokePlaneAxis.CameraFacing;
        private static Vector3 _anchor;
        private static Vector3 _planeNormal = Vector3.back;
        private static bool _drawing;
        private static Vector2 _lastGui;
        private static Vector2 _firstGui;
        private static readonly List<Vector3> _rawPoints = new List<Vector3>();
        private static readonly List<Vector3> _rawNormals = new List<Vector3>();
        private static readonly List<string> _rawHits = new List<string>();
        // The frame a rectangle / ellipse / surface-mode shape is built in
        // (the sketch plane, or the tangent plane of the first surface hit).
        private static Vector3 _shapeOrigin;
        private static Vector3 _shapeNormal = Vector3.up;
        private static Vector3 _shapeRight = Vector3.right;
        private static Vector3 _shapeUp = Vector3.forward;
        /// <summary>Polyline: the rubber-band end under the cursor (null when off the plane / surface).</summary>
        private static Vector3? _previewPoint;

        /// <summary>Raised when <see cref="Armed"/>, <see cref="Mode"/> or <see cref="Shape"/> changes (toolbar and panel mirror it).</summary>
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

        /// <summary>
        /// The tool the next stroke is drawn with (decision T1). Kept
        /// across arm / disarm and both modes. Changing it discards a
        /// stroke in progress.
        /// </summary>
        public static SceneStrokeShape Shape
        {
            get { return _shape; }
            set
            {
                if (_shape == value)
                {
                    return;
                }
                CancelDrawing();
                _shape = value;
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

        /// <summary>True between the first and the last input of a stroke (a drag, or a polyline's clicks).</summary>
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
            if (e.type == EventType.KeyDown && HandleKey(e, sceneView))
            {
                if (_armed)
                {
                    UpdateHover(e.mousePosition, sceneView);
                }
                e.Use();
                return;
            }
            if (e.type == EventType.ScrollWheel)
            {
                // Ctrl (Cmd) + wheel steps the plane's depth; the plain
                // wheel stays the Scene view's zoom (decision T2).
                if (_mode == SceneStrokeMode.Plane && !_drawing && (e.control || e.command))
                {
                    StepDepth(e.delta.y > 0f, sceneView.camera);
                    UpdateHover(e.mousePosition, sceneView);
                    e.Use();
                }
                return;
            }
            switch (e.type)
            {
                case EventType.MouseMove:
                    UpdateHover(e.mousePosition, sceneView);
                    if (_drawing && _shape == SceneStrokeShape.Polyline)
                    {
                        _previewPoint = ResolveShapePoint(e.mousePosition, true);
                    }
                    sceneView.Repaint();
                    return;
                case EventType.MouseLeaveWindow:
                    HoverPoint = null;
                    HoverSurfacePoint = null;
                    _previewPoint = null;
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
                    if (_shape == SceneStrokeShape.Polyline)
                    {
                        PolylineClick(e, sceneView);
                    }
                    else
                    {
                        BeginStroke(e.mousePosition, sceneView);
                    }
                    e.Use();
                    return;
                case EventType.MouseDrag:
                    if (!_drawing || _shape == SceneStrokeShape.Polyline)
                    {
                        UpdateHover(e.mousePosition, sceneView);
                        if (_drawing)
                        {
                            _previewPoint = ResolveShapePoint(e.mousePosition, true);
                        }
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
                    if (_drawing && e.button == 0 && _shape != SceneStrokeShape.Polyline)
                    {
                        AddSample(e.mousePosition, e.shift, sceneView);
                        EndStroke(false);
                        e.Use();
                    }
                    return;
            }
        }

        /// <summary>Keys of both modes; true when the key was consumed (the caller refreshes the hover and uses the event).</summary>
        private static bool HandleKey(Event e, SceneView sceneView)
        {
            if (e.keyCode == KeyCode.Escape)
            {
                if (_drawing)
                {
                    CancelDrawing();
                    SceneView.RepaintAll();
                }
                else
                {
                    Armed = false;
                }
                return true;
            }
            if (_drawing && _shape == SceneStrokeShape.Polyline)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        EndStroke(false);
                        return true;
                    case KeyCode.Backspace:
                    case KeyCode.Delete:
                        RemoveLastVertex();
                        return true;
                }
            }
            if (_drawing)
            {
                return false;
            }
            int shapeKey = ShapeKeyIndex(e.keyCode);
            if (shapeKey >= 0)
            {
                Shape = (SceneStrokeShape)shapeKey;
                return true;
            }
            if (e.keyCode == KeyCode.N)
            {
                NameNewest();
                return true;
            }
            if (_mode != SceneStrokeMode.Plane)
            {
                return false;
            }
            switch (e.keyCode)
            {
                case KeyCode.LeftBracket:
                    StepDepth(false, sceneView.camera);
                    return true;
                case KeyCode.RightBracket:
                    StepDepth(true, sceneView.camera);
                    return true;
                case KeyCode.F:
                    SnapDepthToSurface(e.mousePosition);
                    return true;
                case KeyCode.X:
                    SetPlaneAxis(SceneStrokePlaneAxis.X, sceneView);
                    return true;
                case KeyCode.Y:
                    SetPlaneAxis(SceneStrokePlaneAxis.Y, sceneView);
                    return true;
                case KeyCode.Z:
                    SetPlaneAxis(SceneStrokePlaneAxis.Z, sceneView);
                    return true;
                case KeyCode.C:
                    SetPlaneAxis(SceneStrokePlaneAxis.CameraFacing, sceneView);
                    return true;
            }
            return false;
        }

        /// <summary>Pure: the shape index for the 1..5 keys (main row or keypad), -1 for any other key.</summary>
        public static int ShapeKeyIndex(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Alpha1: case KeyCode.Keypad1: return (int)SceneStrokeShape.Freehand;
                case KeyCode.Alpha2: case KeyCode.Keypad2: return (int)SceneStrokeShape.Line;
                case KeyCode.Alpha3: case KeyCode.Keypad3: return (int)SceneStrokeShape.Polyline;
                case KeyCode.Alpha4: case KeyCode.Keypad4: return (int)SceneStrokeShape.Rectangle;
                case KeyCode.Alpha5: case KeyCode.Keypad5: return (int)SceneStrokeShape.Ellipse;
            }
            return -1;
        }

        /// <summary>N: opens the name window for the newest stroke (no-op with none).</summary>
        private static void NameNewest()
        {
            SceneStroke[] strokes = SceneStrokeStore.Snapshot();
            if (strokes.Length > 0)
            {
                SceneStrokeNameWindow.Open(strokes[strokes.Length - 1].Id);
            }
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
            return TryPlanePoint(CurrentPlane, ray, out point);
        }

        private static bool TryPlanePoint(Plane plane, Ray ray, out Vector3 point)
        {
            float enter;
            point = Vector3.zero;
            if (!plane.Raycast(ray, out enter) || enter <= 0f)
            {
                return false;
            }
            point = ray.GetPoint(enter);
            return true;
        }

        /// <summary>
        /// The sample under the cursor for the current mode: the plane
        /// point in plane mode, the surface hit in surface mode. For a
        /// shape in progress in surface mode, a miss falls back to the
        /// shape's own tangent plane when <paramref name="allowShapePlane"/>
        /// (so a rectangle can be dragged past the edge of the mesh).
        /// </summary>
        private static Vector3? ResolveShapePoint(Vector2 guiPoint, bool allowShapePlane)
        {
            Vector3 point;
            Vector3 normal;
            string hit;
            if (TryResolveSample(guiPoint, allowShapePlane, out point, out normal, out hit))
            {
                return point;
            }
            return null;
        }

        private static bool TryResolveSample(Vector2 guiPoint, bool allowShapePlane,
            out Vector3 point, out Vector3 normal, out string hit)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            if (_mode == SceneStrokeMode.Plane)
            {
                normal = _planeNormal;
                hit = string.Empty;
                return TryPlanePoint(ray, out point);
            }
            if (SceneMarkerPin.TryRaycastSurface(ray, SceneMarkerPin.CollectRenderers(), out point, out normal, out hit))
            {
                return true;
            }
            if (allowShapePlane && _drawing && _rawPoints.Count > 0
                && TryPlanePoint(new Plane(_shapeNormal, _shapeOrigin), ray, out point))
            {
                normal = _shapeNormal;
                hit = string.Empty;
                return true;
            }
            return false;
        }

        // -- Drag shapes (freehand, line, rectangle, ellipse) -----------------------------------

        private static void BeginStroke(Vector2 guiPoint, SceneView sceneView)
        {
            CancelDrawing();
            if (_mode == SceneStrokeMode.Plane && _axis == SceneStrokePlaneAxis.CameraFacing)
            {
                // The plane faces the camera as it is NOW; it stays put for
                // the whole stroke even if the view moves (decision S3).
                RefreshPlaneNormal(sceneView);
            }
            Vector3 point;
            Vector3 normal;
            string hit;
            if (!TryResolveSample(guiPoint, false, out point, out normal, out hit))
            {
                // Surface mode only draws on surfaces (decision S5): a
                // press off every mesh starts nothing.
                return;
            }
            _drawing = true;
            _firstGui = guiPoint;
            _lastGui = guiPoint;
            SetShapeFrame(point, normal, sceneView);
            _rawPoints.Add(point);
            _rawNormals.Add(normal);
            _rawHits.Add(hit ?? string.Empty);
            HoverPoint = point;
        }

        /// <summary>The frame shapes are built in: the sketch plane (plane mode) or the first hit's tangent plane (surface mode), right along the screen.</summary>
        private static void SetShapeFrame(Vector3 origin, Vector3 normal, SceneView sceneView)
        {
            _shapeOrigin = origin;
            _shapeNormal = _mode == SceneStrokeMode.Plane ? _planeNormal : (normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up);
            Vector3 preferredRight = sceneView != null && sceneView.camera != null ? sceneView.camera.transform.right : Vector3.right;
            SceneStrokeGeometry.PlaneBasis(_shapeNormal, preferredRight, out _shapeRight, out _shapeUp);
        }

        private static void AddSample(Vector2 guiPoint, bool shift, SceneView sceneView)
        {
            if (!_drawing)
            {
                return;
            }
            bool straight = shift || _shape == SceneStrokeShape.Line;
            if (_shape == SceneStrokeShape.Rectangle || _shape == SceneStrokeShape.Ellipse)
            {
                UpdateDragShape(guiPoint, shift);
                return;
            }
            if (_rawPoints.Count > 0 && Vector2.Distance(guiPoint, _lastGui) < MinSampleGuiDistance && !straight)
            {
                return;
            }
            Vector3 point;
            Vector3 normal;
            string hit;
            if (!TryResolveSample(guiPoint, false, out point, out normal, out hit))
            {
                return;
            }
            if (straight && _rawPoints.Count >= 2)
            {
                // A straight line keeps the first sample and the cursor only.
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

        /// <summary>Rectangle / ellipse: regenerates the outline from the press point and the cursor's point on the shape plane.</summary>
        private static void UpdateDragShape(Vector2 guiPoint, bool uniform)
        {
            Vector3 corner;
            if (!TryPlanePoint(new Plane(_shapeNormal, _shapeOrigin), HandleUtility.GUIPointToWorldRay(guiPoint), out corner))
            {
                return;
            }
            List<Vector3> outline = _shape == SceneStrokeShape.Rectangle
                ? SceneStrokeGeometry.RectanglePoints(_shapeOrigin, corner, _shapeRight, _shapeUp, uniform)
                : SceneStrokeGeometry.EllipsePoints(_shapeOrigin, corner, _shapeRight, _shapeUp, uniform, SceneStrokeGeometry.EllipseSegments);
            _rawPoints.Clear();
            _rawNormals.Clear();
            _rawHits.Clear();
            for (int i = 0; i < outline.Count; i++)
            {
                _rawPoints.Add(outline[i]);
                _rawNormals.Add(_shapeNormal);
                _rawHits.Add(string.Empty);
            }
            _lastGui = guiPoint;
            HoverPoint = corner;
        }

        // -- Polyline --------------------------------------------------------------------------

        private static void PolylineClick(Event e, SceneView sceneView)
        {
            if (!_drawing)
            {
                BeginStroke(e.mousePosition, sceneView);
                _previewPoint = null;
                return;
            }
            if (e.clickCount >= 2)
            {
                // The first click of the pair already placed its vertex.
                EndStroke(false);
                return;
            }
            if (_rawPoints.Count >= 3 && Vector2.Distance(e.mousePosition, _firstGui) <= CloseGuiDistance)
            {
                EndStroke(true);
                return;
            }
            Vector3 point;
            Vector3 normal;
            string hit;
            if (!TryResolveSample(e.mousePosition, true, out point, out normal, out hit))
            {
                return;
            }
            _rawPoints.Add(point);
            _rawNormals.Add(normal);
            _rawHits.Add(hit ?? string.Empty);
            _lastGui = e.mousePosition;
        }

        private static void RemoveLastVertex()
        {
            if (_rawPoints.Count <= 1)
            {
                CancelDrawing();
            }
            else
            {
                _rawPoints.RemoveAt(_rawPoints.Count - 1);
                _rawNormals.RemoveAt(_rawNormals.Count - 1);
                _rawHits.RemoveAt(_rawHits.Count - 1);
            }
            SceneView.RepaintAll();
        }

        // -- Completion ---------------------------------------------------------------------

        private static void EndStroke(bool closedExplicitly)
        {
            _drawing = false;
            _previewPoint = null;
            SceneStrokeShape shape = _shape;
            var points = new List<Vector3>(_rawPoints);
            var normals = new List<Vector3>(_rawNormals);
            var hits = new List<string>(_rawHits);
            _rawPoints.Clear();
            _rawNormals.Clear();
            _rawHits.Clear();
            bool closed = closedExplicitly || shape == SceneStrokeShape.Rectangle || shape == SceneStrokeShape.Ellipse;
            if (_mode == SceneStrokeMode.Surface && shape != SceneStrokeShape.Freehand && shape != SceneStrokeShape.Line)
            {
                DrapeOntoSurface(points, normals, hits, closed);
            }
            SceneStroke stroke = BuildStroke(_mode, shape, points, normals, hits, _anchor, _planeNormal, closed);
            if (stroke == null)
            {
                SceneView.RepaintAll();
                return;
            }
            // The payload names the id and number the store is about to
            // assign, so the persisted Note (written by Add) is final.
            stroke.Number = SceneStrokeStore.NextNumber(SceneStrokeStore.Snapshot());
            stroke.Color = SceneStroke.PaletteColor(stroke.Number);
            stroke.Note = FormatPayload(stroke.Number, stroke, SceneStrokeStore.NextId);
            SceneStrokeStore.Add(stroke);
            Action<SceneStroke, string> handler = StrokePlaced;
            if (handler != null)
            {
                handler(stroke, stroke.Note);
            }
        }

        /// <summary>
        /// Surface mode, shapes: subdivides the outline and drops every
        /// point onto the mesh along the shape plane's normal (a ray from
        /// above the plane), recording the hit normal and object; a point
        /// over nothing keeps its plane position (decision T1).
        /// </summary>
        private static void DrapeOntoSurface(List<Vector3> points, List<Vector3> normals, List<string> hits, bool closed)
        {
            if (points.Count < 2)
            {
                return;
            }
            float perimeter = SceneStrokeGeometry.Length(points) + (closed ? Vector3.Distance(points[points.Count - 1], points[0]) : 0f);
            List<Vector3> dense = SceneStrokeGeometry.Subdivide(points, closed, perimeter / DrapeSegments, SceneStroke.MaxPoints);
            Bounds bounds = SceneStrokeGeometry.Bounds(dense);
            float lift = Mathf.Max(1f, bounds.size.magnitude);
            IList<Renderer> renderers = SceneMarkerPin.CollectRenderers();
            points.Clear();
            normals.Clear();
            hits.Clear();
            for (int i = 0; i < dense.Count; i++)
            {
                Vector3 p = dense[i];
                Vector3 hitPoint;
                Vector3 hitNormal;
                string hitPath;
                if (SceneMarkerPin.TryRaycastSurface(new Ray(p + _shapeNormal * lift, -_shapeNormal), renderers,
                    out hitPoint, out hitNormal, out hitPath))
                {
                    points.Add(hitPoint);
                    normals.Add(hitNormal);
                    hits.Add(hitPath ?? string.Empty);
                }
                else
                {
                    points.Add(p);
                    normals.Add(_shapeNormal);
                    hits.Add(string.Empty);
                }
            }
        }

        private static void CancelDrawing()
        {
            _drawing = false;
            _previewPoint = null;
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

        /// <summary>Freehand build with the closed flag from the geometry (the pre-shape signature).</summary>
        public static SceneStroke BuildStroke(SceneStrokeMode mode, IList<Vector3> points, IList<Vector3> normals,
            IList<string> hits, Vector3 planeOrigin, Vector3 planeNormal)
        {
            return BuildStroke(mode, SceneStrokeShape.Freehand, points, normals, hits, planeOrigin, planeNormal, false);
        }

        /// <summary>
        /// Turns raw samples into a stored stroke (decision S6): a freehand
        /// stroke goes through Douglas-Peucker at
        /// <see cref="SceneStrokeGeometry.ToleranceFor"/> of the length;
        /// every shape is then decimated uniformly down to
        /// <see cref="SceneStroke.MaxPoints"/> (shapes keep their vertices
        /// as placed -- a rectangle stays four corners, decision T1);
        /// per-point normals and hit paths follow the kept indices;
        /// distinct hit paths become <see cref="SceneStroke.HitPaths"/>.
        /// Null for fewer than two distinct points. Plane strokes record
        /// the plane and carry no per-point normals or hits. The stroke is
        /// closed when <paramref name="closed"/> says so or its ends meet
        /// (<see cref="SceneStrokeGeometry.IsClosed"/>).
        /// </summary>
        public static SceneStroke BuildStroke(SceneStrokeMode mode, SceneStrokeShape shape, IList<Vector3> points,
            IList<Vector3> normals, IList<string> hits, Vector3 planeOrigin, Vector3 planeNormal, bool closed)
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
            List<int> keep;
            if (shape == SceneStrokeShape.Freehand)
            {
                keep = SceneStrokeGeometry.SimplifyIndices(points, SceneStrokeGeometry.ToleranceFor(length));
            }
            else
            {
                keep = new List<int>(points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    keep.Add(i);
                }
            }
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
            var stroke = new SceneStroke { Mode = mode, Shape = shape };
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
            stroke.Closed = (closed && stroke.Points.Count >= 3) || SceneStrokeGeometry.IsClosed(stroke.Points);
            if (stroke.Closed && stroke.Points.Count >= 3 && shape != SceneStrokeShape.Freehand
                && Vector3.Distance(stroke.Points[0], stroke.Points[stroke.Points.Count - 1]) < 1e-5f)
            {
                // A polyline closed on its first vertex: drop the duplicate
                // end so the outline has no zero-length closing edge.
                stroke.Points.RemoveAt(stroke.Points.Count - 1);
                if (stroke.Normals.Count > stroke.Points.Count)
                {
                    stroke.Normals.RemoveAt(stroke.Normals.Count - 1);
                }
                if (stroke.HitIndices.Count > stroke.Points.Count)
                {
                    stroke.HitIndices.RemoveAt(stroke.HitIndices.Count - 1);
                }
            }
            return stroke;
        }

        /// <summary>The chip payload (context block WITHOUT wire delimiters).</summary>
        public static string FormatPayload(int number, SceneStroke stroke, int id)
        {
            var sb = new StringBuilder(320);
            sb.Append(L10n.F(L10n.S.CtxStrokePayloadHeaderFmt, number));
            if (!string.IsNullOrEmpty(stroke.Name))
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadNameFmt, stroke.Name));
            }
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadModeFmt, SceneStroke.ModeName(stroke.Mode)));
            if (stroke.Shape != SceneStrokeShape.Freehand)
            {
                sb.Append(", ").Append(L10n.F(L10n.S.CtxStrokePayloadShapeFmt, SceneStroke.ShapeName(stroke.Shape)));
            }
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadStatsFmt,
                stroke.Points.Count, stroke.Length.ToString("F2", CultureInfo.InvariantCulture),
                stroke.Closed ? L10n.S.CtxStrokePayloadClosedYes : L10n.S.CtxStrokePayloadClosedNo));
            if (stroke.IsRegion)
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxStrokePayloadAreaFmt,
                    SceneStrokeGeometry.PolygonArea(stroke.Points).ToString("F2", CultureInfo.InvariantCulture), id));
            }
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

        /// <summary>The user-facing name of a shape (toolbar dropdown, menu, readout).</summary>
        public static string ShapeLabel(SceneStrokeShape shape)
        {
            switch (shape)
            {
                case SceneStrokeShape.Line: return L10n.S.CtxSketchShapeLine;
                case SceneStrokeShape.Polyline: return L10n.S.CtxSketchShapePolyline;
                case SceneStrokeShape.Rectangle: return L10n.S.CtxSketchShapeRectangle;
                case SceneStrokeShape.Ellipse: return L10n.S.CtxSketchShapeEllipse;
                default: return L10n.S.CtxSketchShapeFreehand;
            }
        }

        /// <summary>The key-hint line under the Scene-view readout for the current state.</summary>
        public static string KeyHints()
        {
            if (_drawing && _shape == SceneStrokeShape.Polyline)
            {
                return L10n.S.CtxSketchPolylineHints;
            }
            return _mode == SceneStrokeMode.Plane ? L10n.S.CtxSketchKeyHints : L10n.S.CtxSketchSurfaceKeyHints;
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
            else
            {
                SceneStrokeDepthGuide.DrawReadout(sceneView.camera,
                    L10n.F(L10n.S.CtxSketchSurfaceReadoutFmt, ShapeLabel(_shape)) + "\n" + KeyHints());
            }
            Color ink = new Color(0.2f, 0.9f, 1f);
            bool closedPreview = _shape == SceneStrokeShape.Rectangle || _shape == SceneStrokeShape.Ellipse;
            if (_rawPoints.Count >= 2)
            {
                Handles.color = ink;
                Handles.DrawAAPolyLine(4f, _rawPoints.ToArray());
                if (closedPreview)
                {
                    Handles.DrawAAPolyLine(4f, _rawPoints[_rawPoints.Count - 1], _rawPoints[0]);
                }
            }
            if (_drawing && _shape == SceneStrokeShape.Polyline && _rawPoints.Count >= 1)
            {
                Handles.color = new Color(ink.r, ink.g, ink.b, 0.9f);
                Vector3 first = _rawPoints[0];
                Vector3 facing = sceneView.camera != null ? -sceneView.camera.transform.forward : Vector3.up;
                Handles.DrawWireDisc(first, facing, HandleUtility.GetHandleSize(first) * 0.08f);
                if (_previewPoint.HasValue)
                {
                    Handles.color = new Color(ink.r, ink.g, ink.b, 0.6f);
                    Handles.DrawDottedLine(_rawPoints[_rawPoints.Count - 1], _previewPoint.Value, 4f);
                }
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
            _shape = SceneStrokeShape.Freehand;
            _axis = SceneStrokePlaneAxis.CameraFacing;
            _anchor = Vector3.zero;
            _planeNormal = Vector3.back;
            CancelDrawing();
            HoverPoint = null;
            HoverSurfacePoint = null;
        }
    }
}
