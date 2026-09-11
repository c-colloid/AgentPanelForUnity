using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// Ray-vs-mesh picking for the user pin (design note 2026-09-07
    /// section 4.14): a pin must land on what the user SEES, and most
    /// editor geometry has no collider, so Physics.Raycast alone only ever
    /// finds the floor. This walks the scene's renderers, prefilters by
    /// bounds and intersects the ray with the actual triangles through the
    /// editor's own HandleUtility.IntersectRayMesh (internal, reached by
    /// reflection -- the routine the Scene view uses for placement).
    /// Skinned meshes are baked first so a posed character is hit where it
    /// is drawn. When the internal method is missing (a future editor
    /// renames it) <see cref="Available"/> is false and callers fall back
    /// to the collider/plane paths as before.
    /// </summary>
    public static class SceneMeshRaycaster
    {
        /// <summary>One mesh hit: world point/normal, distance along the ray and the renderer's transform.</summary>
        public struct MeshHit
        {
            public Vector3 Point;
            public Vector3 Normal;
            public float Distance;
            public Transform Transform;
        }

        private static MethodInfo _intersect;
        private static bool _resolved;
        private static Mesh _bakeScratch;

        /// <summary>True when the editor exposes IntersectRayMesh (Unity 2019+ so far).</summary>
        public static bool Available
        {
            get { return Resolve() != null; }
        }

        private static MethodInfo Resolve()
        {
            if (_resolved)
            {
                return _intersect;
            }
            _resolved = true;
            try
            {
                _intersect = typeof(HandleUtility).GetMethod("IntersectRayMesh",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() }, null);
            }
            catch (Exception)
            {
                _intersect = null;
            }
            return _intersect;
        }

        /// <summary>Ray against one mesh under <paramref name="matrix"/>. False when unavailable or missed.</summary>
        public static bool TryIntersect(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            MethodInfo method = Resolve();
            if (method == null || mesh == null)
            {
                return false;
            }
            var args = new object[] { ray, mesh, matrix, null };
            try
            {
                if (!(bool)method.Invoke(null, args))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            hit = (RaycastHit)args[3];
            return true;
        }

        /// <summary>
        /// Nearest mesh hit among <paramref name="renderers"/> within
        /// <paramref name="maxDistance"/>. Disabled renderers, inactive
        /// objects, hierarchy-hidden objects and anything hidden by the
        /// Scene-view visibility toggle are skipped -- the pin goes on
        /// what is drawn.
        /// </summary>
        public static bool Raycast(Ray ray, IList<Renderer> renderers, float maxDistance, out MeshHit best)
        {
            best = default(MeshHit);
            best.Distance = maxDistance;
            bool found = false;
            if (renderers == null || !Available)
            {
                return false;
            }
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsPickable(renderer))
                {
                    continue;
                }
                float boundsDistance;
                if (!renderer.bounds.IntersectRay(ray, out boundsDistance) || boundsDistance > best.Distance)
                {
                    continue;
                }
                Mesh mesh;
                Matrix4x4 matrix;
                if (!TryGetMesh(renderer, out mesh, out matrix))
                {
                    continue;
                }
                RaycastHit hit;
                if (!TryIntersect(ray, mesh, matrix, out hit) || hit.distance >= best.Distance)
                {
                    continue;
                }
                best.Point = hit.point;
                best.Normal = hit.normal;
                best.Distance = hit.distance;
                best.Transform = renderer.transform;
                found = true;
            }
            return found;
        }

        private static bool IsPickable(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled)
            {
                return false;
            }
            GameObject go = renderer.gameObject;
            if (!go.activeInHierarchy || (go.hideFlags & HideFlags.HideInHierarchy) != 0)
            {
                return false;
            }
            try
            {
                if (SceneVisibilityManager.instance != null && SceneVisibilityManager.instance.IsHidden(go))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // Visibility manager is editor state that can be absent in odd contexts; treat as visible.
            }
            return true;
        }

        private static bool TryGetMesh(Renderer renderer, out Mesh mesh, out Matrix4x4 matrix)
        {
            mesh = null;
            matrix = Matrix4x4.identity;
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                if (skinned.sharedMesh == null)
                {
                    return false;
                }
                if (_bakeScratch == null)
                {
                    _bakeScratch = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }
                skinned.BakeMesh(_bakeScratch, true);
                mesh = _bakeScratch;
                Transform t = skinned.transform;
                matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                return true;
            }
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return false;
            }
            mesh = filter.sharedMesh;
            matrix = renderer.transform.localToWorldMatrix;
            return true;
        }
    }
}
