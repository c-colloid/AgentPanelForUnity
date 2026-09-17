using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// Draws <see cref="SceneStrokeStore"/> into the Scene view twice, the
    /// way <see cref="SceneMarkerRenderer"/> draws markers (design note
    /// 2026-09-17-scene-sketch-strokes.md, decision S2):
    ///
    ///   * an anti-aliased polyline plus the "S&lt;n&gt;" label through
    ///     Handles in <c>SceneView.duringSceneGui</c> -- what the user sees
    ///     and what a window capture sees;
    ///   * a Lines-topology mesh through <c>Graphics.DrawMesh</c> for
    ///     SceneView cameras only, so the agent's own uap_editor_screenshot
    ///     (camera mode) shows the stroke too.
    ///
    /// Nothing here touches the scene. Meshes are cached per stroke id and
    /// rebuilt when the point count changes; everything is HideAndDontSave
    /// and released before an assembly reload.
    /// </summary>
    public static class SceneStrokeRenderer
    {
        private const float LineWidth = 4f;

        private static bool _installed;
        private static Material _material;
        private static MaterialPropertyBlock _propertyBlock;
        private static GUIStyle _labelStyle;
        private static readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();

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
            SceneStrokeStore.Changed -= PruneMeshes;
            SceneStrokeStore.Changed += PruneMeshes;
        }

        // -- Handles pass (on screen) ---------------------------------------------------

        private static void OnSceneGui(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            SceneStroke[] strokes = SceneStrokeStore.Snapshot();
            if (strokes.Length == 0)
            {
                return;
            }
            EnsureLabelStyle();
            CompareFunction previousZTest = Handles.zTest;
            Color previousColor = Handles.color;
            Handles.zTest = CompareFunction.Always;
            Vector3 facing = sceneView.camera != null ? -sceneView.camera.transform.forward : Vector3.up;
            var labelPoints = new List<Vector2>(strokes.Length);
            var labelTexts = new List<string>(strokes.Length);
            for (int i = 0; i < strokes.Length; i++)
            {
                SceneStroke stroke = strokes[i];
                if (stroke.Points.Count == 0)
                {
                    continue;
                }
                Handles.color = stroke.Color;
                if (stroke.Points.Count >= 2)
                {
                    Handles.DrawAAPolyLine(LineWidth, stroke.Points.ToArray());
                    if (stroke.Closed)
                    {
                        Handles.DrawAAPolyLine(LineWidth, stroke.Points[stroke.Points.Count - 1], stroke.Points[0]);
                    }
                }
                Vector3 start = stroke.Points[0];
                float dot = HandleUtility.GetHandleSize(start) * 0.05f;
                Handles.DrawSolidDisc(start, facing, dot);
                labelPoints.Add(HandleUtility.WorldToGUIPoint(start));
                labelTexts.Add(stroke.DisplayNumber);
            }
            Handles.zTest = previousZTest;
            Handles.color = previousColor;

            float[] offsets = SceneMarkerRenderer.ComputeLabelOffsets(labelPoints, 60f, 18f);
            Handles.BeginGUI();
            for (int i = 0; i < labelTexts.Count; i++)
            {
                Vector2 gui = labelPoints[i];
                gui.y += offsets[i];
                var content = new GUIContent(" " + labelTexts[i] + " ");
                Vector2 size = _labelStyle.CalcSize(content);
                GUI.Label(new Rect(gui.x + 8f, gui.y - size.y - 4f, size.x, size.y), content, _labelStyle);
            }
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
            SceneStroke[] strokes = SceneStrokeStore.Snapshot();
            if (strokes.Length == 0 || !EnsureResources())
            {
                return;
            }
            for (int i = 0; i < strokes.Length; i++)
            {
                Mesh mesh = MeshFor(strokes[i]);
                if (mesh == null)
                {
                    continue;
                }
                _propertyBlock.SetColor("_Color", strokes[i].Color);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, _material, 0, camera, 0, _propertyBlock);
            }
        }

        /// <summary>A Lines mesh of the stroke's segments (closing segment included), cached by id.</summary>
        private static Mesh MeshFor(SceneStroke stroke)
        {
            if (stroke.Points.Count < 2)
            {
                return null;
            }
            int segmentCount = stroke.Points.Count - 1 + (stroke.Closed ? 1 : 0);
            Mesh mesh;
            if (_meshes.TryGetValue(stroke.Id, out mesh) && mesh != null && mesh.vertexCount == segmentCount * 2)
            {
                return mesh;
            }
            if (mesh == null)
            {
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                _meshes[stroke.Id] = mesh;
            }
            var vertices = new Vector3[segmentCount * 2];
            var indices = new int[segmentCount * 2];
            for (int s = 0; s < segmentCount; s++)
            {
                int a = s;
                int b = (s + 1) % stroke.Points.Count;
                vertices[s * 2] = stroke.Points[a];
                vertices[s * 2 + 1] = stroke.Points[b];
                indices[s * 2] = s * 2;
                indices[s * 2 + 1] = s * 2 + 1;
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Drops cached meshes for strokes that are gone.</summary>
        private static void PruneMeshes()
        {
            if (_meshes.Count == 0)
            {
                return;
            }
            var gone = new List<int>();
            foreach (KeyValuePair<int, Mesh> entry in _meshes)
            {
                if (SceneStrokeStore.Find(entry.Key) == null)
                {
                    gone.Add(entry.Key);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                Mesh mesh = _meshes[gone[i]];
                _meshes.Remove(gone[i]);
                if (mesh != null)
                {
                    Object.DestroyImmediate(mesh);
                }
            }
        }

        private static bool EnsureResources()
        {
            if (_material != null)
            {
                return true;
            }
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                return false;
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _propertyBlock = new MaterialPropertyBlock();
            return true;
        }

        private static void ReleaseResources()
        {
            if (_material != null)
            {
                Object.DestroyImmediate(_material);
                _material = null;
            }
            foreach (KeyValuePair<int, Mesh> entry in _meshes)
            {
                if (entry.Value != null)
                {
                    Object.DestroyImmediate(entry.Value);
                }
            }
            _meshes.Clear();
        }
    }
}
