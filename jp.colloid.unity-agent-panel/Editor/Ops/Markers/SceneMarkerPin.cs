using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using L10n = Colloid.AgentPanel.UI.L10n;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// User-to-agent half of the markers (design note 2026-09-07 section
    /// 1.3.4 / decision M3): while <see cref="Armed"/>, the next left click
    /// in a Scene view drops a user pin -- a magenta Point marker with
    /// origin User -- at the clicked surface (the nearer of Physics.Raycast
    /// and a triangle-level mesh raycast over the scene's renderers, then
    /// HandleUtility.PlaceObject, then 5 m along the ray) and raises <see cref="PinPlaced"/> with a ready-made
    /// context-chip payload (world position, hit object, nearest objects,
    /// Scene camera). Arming ends after one pin unless Shift is held;
    /// Escape disarms. The Scene-view toolbar toggle
    /// (<see cref="SceneMarkerPinOverlay"/>) and the context bar's pin
    /// button both drive <see cref="Armed"/>.
    ///
    /// The pin itself lives in <see cref="SceneMarkerStore"/> like any
    /// other marker (so uap_marker_list shows it as "user-pin" and a
    /// domain reload keeps it); only the chip is the panel's.
    /// </summary>
    public static class SceneMarkerPin
    {
        /// <summary>Fallback distance along the click ray when nothing is under the cursor.</summary>
        public const float FallbackDistance = 5f;
        /// <summary>How far along the click ray colliders and meshes are searched.</summary>
        public const float MaxPickDistance = 10000f;
        /// <summary>Surface normal at <see cref="HoverPoint"/> (Vector3.up when the point is a fallback).</summary>
        public static Vector3 HoverNormal = Vector3.up;
        /// <summary>
        /// Pin size as a fraction of HandleUtility.GetHandleSize (screen-
        /// constant): the needle head is this times the handle size, so a
        /// pin is a small dot at any zoom rather than a 0.3 m sphere.
        /// </summary>
        public const float PinScreenFraction = 0.10f;
        /// <summary>Stored Size for pins; the renderer treats user pins by screen size, so this only orders labels.</summary>
        public const float PinSize = 0.05f;
        /// <summary>The would-be drop point under the cursor while armed (null when the cursor is outside).</summary>
        public static Vector3? HoverPoint;
        public const int NearestCount = 3;

        private static bool _armed;
        private static bool _installed;

        /// <summary>Raised when <see cref="Armed"/> changes (toolbar toggle and panel button mirror it).</summary>
        public static event Action ArmedChanged;

        /// <summary>Raised after a pin was placed: the marker (with its id) and the chip payload text.</summary>
        public static event Action<SceneMarker, string> PinPlaced;

        /// <summary>True while the next Scene-view click places a pin.</summary>
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
                    HoverPoint = null;
                }
                Action handler = ArmedChanged;
                if (handler != null)
                {
                    handler();
                }
                SceneView.RepaintAll();
            }
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
            // Claim the default control so the click reaches us instead of
            // selecting whatever is under the cursor.
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Armed = false;
                e.Use();
                return;
            }
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                // Live preview: show where the pin would land before the click.
                Vector3 hover;
                Vector3 hoverNormal;
                string ignored;
                ResolveClickPoint(HandleUtility.GUIPointToWorldRay(e.mousePosition), e.mousePosition, CollectRenderers(),
                    out hover, out hoverNormal, out ignored);
                HoverPoint = hover;
                HoverNormal = hoverNormal;
                sceneView.Repaint();
                return;
            }
            if (e.type == EventType.MouseLeaveWindow)
            {
                HoverPoint = null;
                sceneView.Repaint();
                return;
            }
            if (e.type == EventType.Repaint)
            {
                DrawHoverPreview();
                return;
            }
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt)
            {
                return;
            }
            Vector3 position;
            Vector3 normal;
            string hit;
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            IList<Renderer> renderers = CollectRenderers();
            ResolveClickPoint(ray, e.mousePosition, renderers, out position, out normal, out hit);
            SceneMarker pin = CreatePinMarker(position);
            pin.Note = FormatPayload(SceneMarkerStore.NextPinNumber(SceneMarkerStore.Snapshot()), position, hit,
                DescribeNearest(position, renderers, NearestCount),
                sceneView.camera != null ? sceneView.camera.transform.position : Vector3.zero,
                sceneView.pivot);
            SceneMarkerStore.Add(pin);
            string payload = pin.Note;
            HoverPoint = null;
            if (!e.shift)
            {
                Armed = false;
            }
            e.Use();
            Action<SceneMarker, string> handler = PinPlaced;
            if (handler != null)
            {
                handler(pin, payload);
            }
        }

        /// <summary>Ghost pin at <see cref="HoverPoint"/>: a translucent disc on the surface plus a crosshair.</summary>
        private static void DrawHoverPreview()
        {
            if (!HoverPoint.HasValue)
            {
                return;
            }
            Vector3 p = HoverPoint.Value;
            Vector3 n = HoverNormal.sqrMagnitude > 0.001f ? HoverNormal.normalized : Vector3.up;
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(n, u);
            float size = HandleUtility.GetHandleSize(p) * PinScreenFraction;
            Color previous = Handles.color;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Color magenta;
            SceneMarker.TryParseColor("magenta", out magenta);
            Handles.color = new Color(magenta.r, magenta.g, magenta.b, 0.35f);
            Handles.DrawSolidDisc(p, n, size * 2.5f);
            Handles.color = magenta;
            Handles.DrawWireDisc(p, n, size * 2.5f);
            Handles.DrawLine(p - u * size * 4f, p + u * size * 4f);
            Handles.DrawLine(p - v * size * 4f, p + v * size * 4f);
            Handles.DrawLine(p, p + n * size * 6f);
            Handles.SphereHandleCap(0, p + n * size * 6f, Quaternion.identity, size * 2f, EventType.Repaint);
            Handles.color = previous;
        }

        /// <summary>
        /// Where a click lands: the nearest of the first collider along
        /// <paramref name="ray"/> and the nearest rendered mesh
        /// (<see cref="SceneMeshRaycaster"/>, so colliderless geometry is
        /// hit where it is drawn rather than falling through to the floor),
        /// else the geometry under the GUI point (HandleUtility.PlaceObject,
        /// needs a GUI context -- skipped when <paramref name="guiPoint"/>
        /// is null), else <see cref="FallbackDistance"/> along the ray.
        /// <paramref name="hit"/> describes which path won, for the payload.
        /// </summary>
        public static void ResolveClickPoint(Ray ray, Vector2? guiPoint, out Vector3 position, out string hit)
        {
            Vector3 normal;
            ResolveClickPoint(ray, guiPoint, CollectRenderers(), out position, out normal, out hit);
        }

        /// <summary>
        /// <see cref="ResolveClickPoint(Ray, Vector2?, out Vector3, out string)"/>
        /// over an explicit renderer list, also returning the surface normal
        /// (Vector3.up when nothing was hit) so the hover ghost can lie on
        /// the surface.
        /// </summary>
        public static void ResolveClickPoint(Ray ray, Vector2? guiPoint, IList<Renderer> renderers,
            out Vector3 position, out Vector3 normal, out string hit)
        {
            if (TryRaycastSurface(ray, renderers, out position, out normal, out hit))
            {
                return;
            }
            if (guiPoint.HasValue)
            {
                Vector3 placed;
                Vector3 placedNormal;
                if (HandleUtility.PlaceObject(guiPoint.Value, out placed, out placedNormal))
                {
                    position = placed;
                    normal = placedNormal;
                    hit = L10n.S.CtxPinHitSurface;
                    return;
                }
            }
            position = ray.origin + ray.direction * FallbackDistance;
            hit = L10n.F(L10n.S.CtxPinHitNothingFmt, FallbackDistance);
        }

        /// <summary>
        /// The surface part of <see cref="ResolveClickPoint(Ray, Vector2?, IList{Renderer}, out Vector3, out Vector3, out string)"/>:
        /// the nearer of the first collider along <paramref name="ray"/> and
        /// the nearest rendered mesh, with the hit object's hierarchy path.
        /// False (outputs zero / up / empty) when the ray hits nothing --
        /// no GUI or fallback placement here, so the sketch's surface mode
        /// (design note 2026-09-17-scene-sketch-strokes.md, decision S5)
        /// can drop samples that are not on a surface.
        /// </summary>
        public static bool TryRaycastSurface(Ray ray, IList<Renderer> renderers,
            out Vector3 position, out Vector3 normal, out string hit)
        {
            float bestDistance = MaxPickDistance;
            bool found = false;
            position = Vector3.zero;
            normal = Vector3.up;
            hit = string.Empty;
            RaycastHit raycastHit;
            if (Physics.Raycast(ray, out raycastHit, MaxPickDistance))
            {
                position = raycastHit.point;
                normal = raycastHit.normal;
                hit = UapAddressing.DescribeHierarchyPath(raycastHit.collider.transform);
                bestDistance = raycastHit.distance;
                found = true;
            }
            SceneMeshRaycaster.MeshHit meshHit;
            if (SceneMeshRaycaster.Raycast(ray, renderers, bestDistance, out meshHit))
            {
                position = meshHit.Point;
                normal = meshHit.Normal;
                hit = UapAddressing.DescribeHierarchyPath(meshHit.Transform);
                found = true;
            }
            return found;
        }

        private static Renderer[] _rendererCache;
        private static double _rendererCacheAt;

        /// <summary>Scene renderers, refreshed at most every quarter second (hover resolves on every mouse move).</summary>
        internal static IList<Renderer> CollectRenderers()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_rendererCache == null || now - _rendererCacheAt > 0.25)
            {
                _rendererCache = UnityObjectCompat.FindAll<Renderer>();
                _rendererCacheAt = now;
            }
            return _rendererCache;
        }

        /// <summary>The marker a pin becomes: magenta Point, origin User, fixed position.</summary>
        public static SceneMarker CreatePinMarker(Vector3 position)
        {
            Color magenta;
            SceneMarker.TryParseColor("magenta", out magenta);
            return new SceneMarker
            {
                Kind = SceneMarkerKind.Point,
                Position = position,
                Label = L10n.S.CtxPinMarkerLabel,
                Color = magenta,
                Size = PinSize,
                Origin = SceneMarkerOrigin.User
            };
        }

        /// <summary>Up to <paramref name="max"/> renderers by distance from <paramref name="position"/>, as "Name (1.2m)" items.</summary>
        public static string DescribeNearest(Vector3 position, IList<Renderer> renderers, int max)
        {
            if (renderers == null || renderers.Count == 0 || max <= 0)
            {
                return string.Empty;
            }
            var candidates = new List<KeyValuePair<float, string>>(renderers.Count);
            for (int i = 0; i < renderers.Count; i++)
            {
                if (renderers[i] == null)
                {
                    continue;
                }
                candidates.Add(new KeyValuePair<float, string>(
                    Vector3.Distance(position, renderers[i].bounds.center), renderers[i].gameObject.name));
            }
            candidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(max, candidates.Count); i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(candidates[i].Value).Append(" (")
                  .Append(candidates[i].Key.ToString("F1", CultureInfo.InvariantCulture)).Append("m)");
            }
            return sb.ToString();
        }

        /// <summary>The chip payload (context block WITHOUT wire delimiters).</summary>
        public static string FormatPayload(int id, Vector3 position, string hit, string nearest,
            Vector3 cameraPosition, Vector3 pivot)
        {
            var sb = new StringBuilder(160);
            sb.Append(L10n.F(L10n.S.CtxPinPayloadHeaderFmt, id));
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxPinPayloadPositionFmt, Format(position)));
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxPinPayloadHitFmt, hit ?? string.Empty));
            if (!string.IsNullOrEmpty(nearest))
            {
                sb.Append("\n  ").Append(L10n.F(L10n.S.CtxPinPayloadNearestFmt, nearest));
            }
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxPinPayloadCameraFmt, Format(cameraPosition), Format(pivot)));
            return sb.ToString();
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F2", CultureInfo.InvariantCulture) + ", "
                + v.y.ToString("F2", CultureInfo.InvariantCulture) + ", "
                + v.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Test seam: disarms without raising events.</summary>
        internal static void ResetForTests()
        {
            _armed = false;
            HoverPoint = null;
            HoverNormal = Vector3.up;
            _rendererCache = null;
        }
    }
}
