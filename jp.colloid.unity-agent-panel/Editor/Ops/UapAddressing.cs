using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The ONE target-addressing helper every core write/read tool goes
    /// through (design section 1.2/8.5: "hierarchy path + optional scene,
    /// asset path for assets"). Scene-object addressing is
    /// prefab-stage-aware by construction: when no explicit "scene" is
    /// given and a prefab stage is open, resolution targets the stage's
    /// virtual scene rather than silently falling through to whatever
    /// scene happens to be "active" (R09 section 9.2's documented trap).
    /// </summary>
    public static class UapAddressing
    {
        /// <summary>
        /// Pure: splits a hierarchy path like "Parent/Child/Grandchild" into
        /// non-empty segments. Leading/trailing/duplicate slashes collapse
        /// away; null/empty input yields an empty array (never throws).
        /// </summary>
        public static string[] SplitHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Array.Empty<string>();
            }
            string[] raw = path.Split('/');
            var result = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (!string.IsNullOrEmpty(raw[i]))
                {
                    result.Add(raw[i]);
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// Pure: which loaded scene (by index into the parallel
        /// names/paths arrays) a "scene" tool argument refers to.
        /// Empty/null <paramref name="sceneQuery"/> resolves to
        /// <paramref name="defaultIndex"/> (the caller's prefab-stage-aware
        /// default -- see <see cref="ResolveTargetScene"/>). Returns -1
        /// when a non-empty query matches nothing.
        /// </summary>
        public static int ResolveSceneIndex(string sceneQuery, string[] sceneNames, string[] scenePaths,
            int defaultIndex)
        {
            if (string.IsNullOrEmpty(sceneQuery))
            {
                return defaultIndex;
            }
            if (sceneNames != null)
            {
                for (int i = 0; i < sceneNames.Length; i++)
                {
                    if (string.Equals(sceneNames[i], sceneQuery, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }
            if (scenePaths != null)
            {
                for (int i = 0; i < scenePaths.Length; i++)
                {
                    if (string.Equals(scenePaths[i], sceneQuery, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// Resolves which Scene a tool call targets. Explicit
        /// <paramref name="sceneQuery"/> (matched against every loaded
        /// scene's name, then path) always wins. Omitted: the open prefab
        /// stage's virtual scene when one is open, else the active scene.
        /// </summary>
        public static Scene ResolveTargetScene(string sceneQuery, out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(sceneQuery))
            {
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    Scene candidate = EditorSceneManager.GetSceneAt(i);
                    if (string.Equals(candidate.name, sceneQuery, StringComparison.Ordinal)
                        || string.Equals(candidate.path, sceneQuery, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
                error = "Scene not found (not currently loaded): " + sceneQuery;
                return default(Scene);
            }
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null ? stage.scene : EditorSceneManager.GetActiveScene();
        }

        /// <summary>
        /// Walks a hierarchy path inside <paramref name="scene"/>, matching
        /// each segment against sibling names (first exact match wins when
        /// several siblings share a name -- Unity itself allows duplicate
        /// sibling names, see R09 section 1.4). Returns null with a
        /// descriptive <paramref name="error"/> when the scene has not
        /// finished loading, the path is empty, or any segment is missing.
        /// </summary>
        public static GameObject ResolveInScene(Scene scene, string path, out string error)
        {
            error = null;
            if (!scene.IsValid())
            {
                error = "The target scene is not valid (not loaded).";
                return null;
            }
            string[] segments = SplitHierarchyPath(path);
            if (segments.Length == 0)
            {
                error = "'path' must name at least one GameObject.";
                return null;
            }
            GameObject[] roots = scene.GetRootGameObjects();
            Transform current = null;
            for (int s = 0; s < segments.Length; s++)
            {
                Transform found = null;
                if (s == 0)
                {
                    for (int r = 0; r < roots.Length; r++)
                    {
                        if (roots[r].name == segments[0])
                        {
                            found = roots[r].transform;
                            break;
                        }
                    }
                }
                else
                {
                    for (int c = 0; c < current.childCount; c++)
                    {
                        if (current.GetChild(c).name == segments[s])
                        {
                            found = current.GetChild(c);
                            break;
                        }
                    }
                }
                if (found == null)
                {
                    error = "GameObject not found: no '" + segments[s] + "' under '"
                        + string.Join("/", segments, 0, s) + "' (full path: " + path + ").";
                    return null;
                }
                current = found;
            }
            return current.gameObject;
        }

        /// <summary>Convenience: resolves the target scene, then the path inside it. See the two-step overloads for the individual pieces.</summary>
        public static GameObject ResolveHierarchyPath(string sceneQuery, string path, out string error)
        {
            Scene scene = ResolveTargetScene(sceneQuery, out error);
            if (error != null)
            {
                return null;
            }
            return ResolveInScene(scene, path, out error);
        }

        /// <summary>Full slash-joined hierarchy path from the scene root down to <paramref name="transform"/>, for tool result text.</summary>
        public static string DescribeHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }
            var segments = new List<string>();
            Transform t = transform;
            while (t != null)
            {
                segments.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", segments.ToArray());
        }

        /// <summary>
        /// Loads the asset at <paramref name="assetPath"/> (project-relative,
        /// e.g. "Assets/Materials/Foo.mat"). Returns null with a descriptive
        /// error when the path is empty or nothing exists there.
        /// </summary>
        public static UnityEngine.Object ResolveAsset(string assetPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(assetPath))
            {
                error = "'assetPath' must not be empty.";
                return null;
            }
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                error = "Asset not found: " + assetPath;
            }
            return asset;
        }
    }
}
