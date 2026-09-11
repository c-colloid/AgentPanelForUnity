using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Ops.Markers;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The two ways the panel takes a picture of an editor view (design
    /// note 2026-09-07 decisions M2/M2', shared by uap_editor_screenshot
    /// and, later, the composer's "attach the current view" button):
    ///
    ///   Camera  -- render a camera into an offscreen RenderTexture. Works
    ///              headless-ish (needs the view for its size only), shows
    ///              DrawMesh markers, never shows Handles/gizmos/labels, and
    ///              lights the scene differently from the on-screen view.
    ///   Window  -- read the Scene view's pixels as displayed
    ///              (InternalEditorUtility.ReadScreenPixel). Shows exactly
    ///              what the user sees, labels included; needs the view to
    ///              be on screen and to have been drawn at least once.
    ///
    /// Window capture reads at the Scene view's CAMERA AREA, not at
    /// EditorWindow.position: the two differ by the tab bar plus toolbar
    /// (47 px measured on 2022.3.22f1, and GUIUtility.GUIToScreenPoint
    /// inside duringSceneGui reports the window's top-left, tab included).
    /// The camera area is the window's rootVisualElement: its worldBound
    /// is the area's rect inside the container window (measured (0, 47,
    /// 1100, 694) for a 720-point-tall view), so the screen origin is
    /// GUIToScreenPoint(zero) + rootVisualElement.worldBound.position and
    /// the size is that worldBound's size. GUIToScreenPoint is only
    /// meaningful inside a GUI callback, so a tracker records the result on
    /// every Scene view repaint and the capture, which runs from the tool
    /// dispatcher outside any GUI event, uses the most recent record. A
    /// view that was never drawn (batch mode) has no record and the capture
    /// fails with a structured error.
    /// </summary>
    public static class UapScreenCapture
    {
        /// <summary>What the tracker last saw for one Scene view.</summary>
        public struct SceneViewGeometry
        {
            /// <summary>Screen position of the Scene view's camera area (points).</summary>
            public Vector2 ScreenOrigin;
            /// <summary>Camera area size (points), from rootVisualElement.worldBound.</summary>
            public int PointWidth;
            public int PointHeight;
            public float PixelsPerPoint;
            public double RecordedAt;
        }

        private static readonly Dictionary<int, SceneViewGeometry> _geometry = new Dictionary<int, SceneViewGeometry>();
        private static bool _installed;

        [InitializeOnLoadMethod]
        private static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            SceneView.duringSceneGui -= TrackGeometry;
            SceneView.duringSceneGui += TrackGeometry;
        }

        private static void TrackGeometry(SceneView sceneView)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || sceneView.camera == null)
            {
                return;
            }
            Rect area = sceneView.rootVisualElement != null
                ? sceneView.rootVisualElement.worldBound
                : new Rect(0f, 0f, sceneView.position.width, sceneView.position.height);
            if (float.IsNaN(area.width) || float.IsNaN(area.height))
            {
                return;
            }
            _geometry[sceneView.GetInstanceID()] = new SceneViewGeometry
            {
                ScreenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero) + area.position,
                PointWidth = Mathf.RoundToInt(area.width),
                PointHeight = Mathf.RoundToInt(area.height),
                PixelsPerPoint = EditorGUIUtility.pixelsPerPoint,
                RecordedAt = EditorApplication.timeSinceStartup
            };
        }

        /// <summary>The last recorded geometry for <paramref name="sceneView"/>, if it has been drawn on screen.</summary>
        public static bool TryGetGeometry(SceneView sceneView, out SceneViewGeometry geometry)
        {
            geometry = default(SceneViewGeometry);
            return sceneView != null && _geometry.TryGetValue(sceneView.GetInstanceID(), out geometry);
        }

        /// <summary>Test seam: records geometry as the tracker would (no GUI context needed).</summary>
        internal static void RecordGeometryForTests(int sceneViewInstanceId, SceneViewGeometry geometry)
        {
            _geometry[sceneViewInstanceId] = geometry;
        }

        // -- Whole-view capture (shared by uap_editor_screenshot and the composer) ------

        /// <summary>
        /// Captures an editor view to a PNG: resolves the view (an open Scene
        /// view / Game view + camera, with the same structured errors the
        /// tool reports), then either renders its camera offscreen or reads
        /// the Scene view's pixels. Returns false with a user-facing error.
        /// </summary>
        public static bool TryCaptureView(UapScreenshotView view, UapScreenshotCapture capture, string path,
            out int width, out int height, out string description, out string error)
        {
            width = 0;
            height = 0;
            description = null;
            error = null;
            if (capture == UapScreenshotCapture.Window && view != UapScreenshotView.Scene)
            {
                error = "capture:\"window\" reads the Scene view's pixels and needs view:\"scene\";"
                    + " the Game view has no window capture -- use capture:\"camera\" for it.";
                return false;
            }
            Camera camera;
            SceneView sceneView;
            if (!TryResolveView(view, out camera, out sceneView, out width, out height, out description, out error))
            {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (capture == UapScreenshotCapture.Window)
            {
                if (!TryCaptureSceneViewWindow(sceneView, path, out width, out height, out error))
                {
                    return false;
                }
                description += " as displayed (window capture)";
                return true;
            }
            CaptureCameraToPng(camera, width, height, path);
            return true;
        }

        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;
        private const int MinDimension = 32;
        private const int MaxDimension = 4096;

        /// <summary>
        /// The open view's camera and capture size. Checks that the view is
        /// open STRUCTURALLY first (batch mode and headless callers get a
        /// structured error, never a silent camera-only render).
        /// </summary>
        public static bool TryResolveView(UapScreenshotView view, out Camera camera, out SceneView sceneView,
            out int width, out int height, out string description, out string error)
        {
            camera = null;
            sceneView = null;
            width = 0;
            height = 0;
            description = null;
            error = null;
            if (view == UapScreenshotView.Scene)
            {
                sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null || sceneView.camera == null)
                {
                    error = "No Scene view is open (e.g. running in batch mode) -- open a Scene view in the editor and try again.";
                    return false;
                }
                camera = sceneView.camera;
                width = ClampDimension(sceneView.position.width, DefaultWidth);
                height = ClampDimension(sceneView.position.height, DefaultHeight);
                description = "Scene view";
                return true;
            }
            EditorWindow gameView = FindOpenGameView();
            if (gameView == null)
            {
                error = "No Game view is open (e.g. running in batch mode) -- open a Game view in the editor and try again.";
                return false;
            }
            camera = Camera.main;
            if (camera == null)
            {
                camera = UnityEngine.Object.FindObjectOfType<Camera>();
            }
            if (camera == null)
            {
                error = "A Game view is open, but no active Camera was found in the scene to render"
                    + " (no Camera.main and no other Camera).";
                return false;
            }
            width = ClampDimension(gameView.position.width, DefaultWidth);
            height = ClampDimension(gameView.position.height, DefaultHeight);
            description = "Game view (" + camera.name + ")";
            return true;
        }

        private static int ClampDimension(float raw, int fallback)
        {
            if (float.IsNaN(raw) || raw <= 0f)
            {
                return fallback;
            }
            int rounded = Mathf.RoundToInt(raw);
            return Mathf.Clamp(rounded, MinDimension, MaxDimension);
        }

        /// <summary>Finds an ALREADY-OPEN GameView without ever creating one (GetWindow would).</summary>
        public static EditorWindow FindOpenGameView()
        {
            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                return null;
            }
            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(gameViewType);
            return windows.Length > 0 ? (EditorWindow)windows[0] : null;
        }

        // -- Camera capture ------------------------------------------------------------

        /// <summary>Renders <paramref name="camera"/> at the given size into a PNG at <paramref name="path"/>.</summary>
        public static void CaptureCameraToPng(Camera camera, int width, int height, string path)
        {
            RenderTexture previousTargetTexture = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var rt = new RenderTexture(width, height, 24);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTargetTexture;
                RenderTexture.active = previousActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // -- Window capture ------------------------------------------------------------

        /// <summary>
        /// Reads the Scene view's displayed pixels into a PNG. Returns
        /// false with a structured <paramref name="error"/> when the view
        /// has never been drawn on screen (batch mode, or a view that is
        /// hidden behind another tab) or the screen read yields nothing.
        /// </summary>
        public static bool TryCaptureSceneViewWindow(SceneView sceneView, string path,
            out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;
            SceneViewGeometry geometry;
            if (!TryGetGeometry(sceneView, out geometry))
            {
                error = "The Scene view has not been drawn on screen yet (batch mode, minimized, or hidden"
                    + " behind another tab), so its pixels cannot be read -- use capture:\"camera\" instead.";
                return false;
            }
            if (geometry.PointWidth < 1 || geometry.PointHeight < 1)
            {
                error = "The Scene view's drawable area is empty.";
                return false;
            }
            // ReadScreenPixel takes position and size in the same (point)
            // units; on a HiDPI display the returned image is still one
            // sample per point, which is what the marker listing assumes.
            int pointWidth = geometry.PointWidth;
            int pointHeight = geometry.PointHeight;
            Color[] pixels;
            try
            {
                pixels = InternalEditorUtility.ReadScreenPixel(geometry.ScreenOrigin, pointWidth, pointHeight);
            }
            catch (Exception e)
            {
                error = "Reading the Scene view's pixels failed: " + e.Message;
                return false;
            }
            if (pixels == null || pixels.Length < pointWidth * pointHeight)
            {
                error = "Reading the Scene view's pixels returned no image (is the Editor window visible on screen?).";
                return false;
            }
            var tex = new Texture2D(pointWidth, pointHeight, TextureFormat.RGB24, false);
            try
            {
                tex.SetPixels(pixels);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            width = pointWidth;
            height = pointHeight;
            return true;
        }

        // -- Markers in view -----------------------------------------------------------

        /// <summary>
        /// Projects every marker through <paramref name="camera"/> (viewport
        /// space, so it holds for any capture size with the same aspect) for
        /// <see cref="UapEditorScreenshotPaths.FormatMarkersInView"/>.
        /// </summary>
        public static List<UapMarkerScreenPoint> ProjectMarkers(Camera camera)
        {
            SceneMarker[] markers = SceneMarkerStore.Snapshot();
            var points = new List<UapMarkerScreenPoint>(markers.Length);
            for (int i = 0; i < markers.Length; i++)
            {
                Vector3 world;
                bool resolved = SceneMarkerRenderer.TryResolvePosition(markers[i], out world);
                points.Add(new UapMarkerScreenPoint
                {
                    Id = markers[i].Id,
                    Label = markers[i].Label,
                    Resolved = resolved,
                    Viewport = resolved && camera != null ? camera.WorldToViewportPoint(world) : Vector3.zero
                });
            }
            return points;
        }
    }
}
