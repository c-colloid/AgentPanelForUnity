using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// Draws <see cref="SceneMarkerStore"/> into the Scene view twice
    /// (design note 2026-09-07 decision M1, verified in section 1.4/1.5):
    ///
    ///   * Handles in <c>SceneView.duringSceneGui</c> -- sphere / arrow /
    ///     wire box / the numbered label. What the user sees; also what a
    ///     window capture sees. Never reaches an offscreen camera render.
    ///   * <c>Graphics.DrawMesh</c> queued in <c>Camera.onPreCull</c>
    ///     (Built-in RP) / <c>RenderPipelineManager.beginCameraRendering</c>
    ///     (SRP) for SceneView cameras only -- an unlit sphere / box /
    ///     arrow shaft that lands in every render of that camera, so the
    ///     agent's own uap_editor_screenshot (camera mode) shows it too.
    ///
    /// Nothing here touches the scene: no GameObject, no dirty flag, no
    /// Undo. The material is HideAndDontSave and rebuilt after a reload.
    /// </summary>
    public static class SceneMarkerRenderer
    {
        private const float LabelMinDx = 140f;
        private const float LabelMinDy = 18f;

        private static bool _installed;
        private static Mesh _sphere;
        private static Mesh _cube;
        private static Material _material;
        private static MaterialPropertyBlock _propertyBlock;
        private static GUIStyle _labelStyle;

        /// <summary>Resolved world position per marker for the current repaint (targets re-resolved each time).</summary>
        private static readonly Dictionary<int, Vector3> _resolved = new Dictionary<int, Vector3>();

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
            Camera.onPreCull -= OnPreCull;
            Camera.onPreCull += OnPreCull;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseResources;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseResources;
        }

        /// <summary>
        /// World position a marker draws at: the target's renderer bounds
        /// centre (or its transform) while the target resolves, otherwise
        /// the fixed position. Public so tools can report the same point
        /// the user sees. Returns false when a target is set but no longer
        /// resolves (the marker draws nowhere and list reports it).
        /// </summary>
        public static bool TryResolvePosition(SceneMarker marker, out Vector3 position)
        {
            position = marker.Position;
            if (!marker.HasTarget)
            {
                return true;
            }
            string error;
            GameObject target = UapAddressing.ResolveHierarchyPath(null, marker.Target, out error);
            if (target == null)
            {
                return false;
            }
            var renderer = target.GetComponent<Renderer>();
            position = renderer != null ? renderer.bounds.center : target.transform.position;
            return true;
        }

        // -- Handles pass (on screen) ---------------------------------------------------

        private static void OnSceneGui(SceneView sceneView)
        {
            SceneMarker[] markers = SceneMarkerStore.Snapshot();
            if (markers.Length == 0)
            {
                return;
            }
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            EnsureLabelStyle();
            _resolved.Clear();
            var labelPoints = new List<Vector2>(markers.Length);
            var labelWorld = new List<Vector3>(markers.Length);
            var labelMarkers = new List<SceneMarker>(markers.Length);

            CompareFunction previousZTest = Handles.zTest;
            Color previousColor = Handles.color;
            Handles.zTest = CompareFunction.Always;
            for (int i = 0; i < markers.Length; i++)
            {
                SceneMarker marker = markers[i];
                Vector3 position;
                if (!TryResolvePosition(marker, out position))
                {
                    continue;
                }
                _resolved[marker.Id] = position;
                Handles.color = marker.Color;
                if (marker.Origin == SceneMarkerOrigin.User)
                {
                    // A pin: needle + head at a screen-constant size, so it
                    // never dwarfs the thing it points at.
                    float pin = HandleUtility.GetHandleSize(position) * SceneMarkerPin.PinScreenFraction;
                    Handles.DrawLine(position, position + Vector3.up * pin * 6f);
                    Handles.SphereHandleCap(0, position + Vector3.up * pin * 6f, Quaternion.identity, pin * 2f, EventType.Repaint);
                    Handles.DrawWireDisc(position, Vector3.up, pin * 1.5f);
                    Vector3 pinLabelAnchor = position + Vector3.up * pin * 8f;
                    labelWorld.Add(pinLabelAnchor);
                    labelPoints.Add(HandleUtility.WorldToGUIPoint(pinLabelAnchor));
                    labelMarkers.Add(marker);
                    continue;
                }
                switch (marker.Kind)
                {
                    case SceneMarkerKind.Point:
                        Handles.SphereHandleCap(0, position, Quaternion.identity, marker.Size * 1.1f, EventType.Repaint);
                        break;
                    case SceneMarkerKind.Box:
                        Handles.DrawWireCube(position, Vector3.one * marker.Size * 1.15f);
                        break;
                    case SceneMarkerKind.Arrow:
                        Vector3 delta = marker.To - position;
                        if (delta.sqrMagnitude > 0.0001f)
                        {
                            Handles.ArrowHandleCap(0, position, Quaternion.LookRotation(delta), delta.magnitude, EventType.Repaint);
                        }
                        break;
                }
                Vector3 labelAnchor = position + Vector3.up * (marker.Size + 0.25f);
                labelWorld.Add(labelAnchor);
                labelPoints.Add(HandleUtility.WorldToGUIPoint(labelAnchor));
                labelMarkers.Add(marker);
            }
            Handles.zTest = previousZTest;
            Handles.color = previousColor;

            // Labels last, nudged apart so two markers at the same place
            // (a pin dropped on an agent marker, verified overlap in the
            // 2026-09-07 note section 1.5) both stay readable.
            float[] offsets = ComputeLabelOffsets(labelPoints, LabelMinDx, LabelMinDy);
            Handles.BeginGUI();
            for (int i = 0; i < labelMarkers.Count; i++)
            {
                Vector2 gui = labelPoints[i];
                gui.y += offsets[i];
                var content = new GUIContent(" " + labelMarkers[i].DisplayLabel + " ");
                Vector2 size = _labelStyle.CalcSize(content);
                GUI.Label(new Rect(gui.x - size.x * 0.5f, gui.y - size.y, size.x, size.y), content, _labelStyle);
            }
            Handles.EndGUI();
        }

        /// <summary>
        /// Pure label de-overlap: for labels whose GUI anchors are within
        /// <paramref name="minDx"/> horizontally and <paramref name="minDy"/>
        /// vertically of an EARLIER label (after that label's own offset),
        /// returns a downward y offset that stacks them <paramref name="minDy"/>
        /// apart. Earlier labels (older markers) keep their place. Never
        /// null; zero for every label that does not collide.
        /// </summary>
        public static float[] ComputeLabelOffsets(IList<Vector2> anchors, float minDx, float minDy)
        {
            var offsets = new float[anchors == null ? 0 : anchors.Count];
            if (anchors == null)
            {
                return offsets;
            }
            for (int i = 1; i < anchors.Count; i++)
            {
                bool moved = true;
                int guard = 0;
                while (moved && guard++ < anchors.Count * 4)
                {
                    moved = false;
                    float y = anchors[i].y + offsets[i];
                    for (int j = 0; j < i; j++)
                    {
                        float otherY = anchors[j].y + offsets[j];
                        if (Mathf.Abs(anchors[i].x - anchors[j].x) < minDx && Mathf.Abs(y - otherY) < minDy)
                        {
                            offsets[i] = otherY + minDy - anchors[i].y;
                            moved = true;
                            break;
                        }
                    }
                }
            }
            return offsets;
        }

        private static void EnsureLabelStyle()
        {
            if (_labelStyle != null)
            {
                return;
            }
            _labelStyle = new GUIStyle
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(6, 6, 3, 3),
                alignment = TextAnchor.MiddleLeft
            };
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.normal.background = Texture2D.grayTexture;
        }

        // -- DrawMesh pass (every render of a SceneView camera) ---------------------------

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            OnPreCull(camera);
        }

        private static void OnPreCull(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.SceneView)
            {
                return;
            }
            SceneMarker[] markers = SceneMarkerStore.Snapshot();
            if (markers.Length == 0)
            {
                return;
            }
            if (!EnsureResources())
            {
                return;
            }
            for (int i = 0; i < markers.Length; i++)
            {
                SceneMarker marker = markers[i];
                Vector3 position;
                if (!TryResolvePosition(marker, out position))
                {
                    continue;
                }
                _propertyBlock.SetColor("_Color", marker.Color);
                if (marker.Origin == SceneMarkerOrigin.User)
                {
                    // Screen-constant pin for the camera capture: head sphere
                    // plus a thin needle, sized from the camera distance.
                    float pin = PinWorldSize(camera, position);
                    Graphics.DrawMesh(_sphere, Matrix4x4.TRS(position + Vector3.up * pin * 6f, Quaternion.identity, Vector3.one * pin * 2f),
                        _material, 0, camera, 0, _propertyBlock);
                    Graphics.DrawMesh(_cube, Matrix4x4.TRS(position + Vector3.up * pin * 3f, Quaternion.identity, new Vector3(pin * 0.3f, pin * 6f, pin * 0.3f)),
                        _material, 0, camera, 0, _propertyBlock);
                    continue;
                }
                switch (marker.Kind)
                {
                    case SceneMarkerKind.Point:
                        Graphics.DrawMesh(_sphere, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * marker.Size),
                            _material, 0, camera, 0, _propertyBlock);
                        break;
                    case SceneMarkerKind.Box:
                        Graphics.DrawMesh(_cube, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * marker.Size),
                            _material, 0, camera, 0, _propertyBlock);
                        break;
                    case SceneMarkerKind.Arrow:
                        Vector3 delta = marker.To - position;
                        float length = delta.magnitude;
                        if (length > 0.01f)
                        {
                            Graphics.DrawMesh(_cube,
                                Matrix4x4.TRS(position + delta * 0.5f, Quaternion.LookRotation(delta), new Vector3(0.06f, 0.06f, length)),
                                _material, 0, camera, 0, _propertyBlock);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Same visual size HandleUtility.GetHandleSize gives on screen,
        /// computed from the camera outside a GUI context (the DrawMesh
        /// pass): distance along the view axis times the vertical FOV
        /// factor, times the pin fraction.
        /// </summary>
        public static float PinWorldSize(Camera camera, Vector3 position)
        {
            if (camera == null)
            {
                return SceneMarkerPin.PinSize;
            }
            Transform t = camera.transform;
            float depth = Mathf.Max(0.01f, Vector3.Dot(position - t.position, t.forward));
            float handle = camera.orthographic
                ? camera.orthographicSize * 2f / 80f * 8f
                : depth * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f / 80f * 8f;
            return handle * SceneMarkerPin.PinScreenFraction;
        }

        private static bool EnsureResources()
        {
            if (_material != null && _sphere != null && _cube != null)
            {
                return true;
            }
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                return false;
            }
            _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _propertyBlock = new MaterialPropertyBlock();
            return _sphere != null && _cube != null;
        }

        private static void ReleaseResources()
        {
            if (_material != null)
            {
                UnityEngine.Object.DestroyImmediate(_material);
                _material = null;
            }
        }
    }
}
