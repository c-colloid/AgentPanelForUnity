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
        /// Pure: parses one path segment of the form "Name[2]" (0-based
        /// index among siblings that share that exact name). False when the
        /// segment has no trailing "[digits]" suffix or the name part would
        /// be empty. Callers must try the LITERAL segment first -- an object
        /// really named "Foo[1]" still resolves as itself.
        /// </summary>
        public static bool TryParseIndexedSegment(string segment, out string name, out int index)
        {
            name = null;
            index = 0;
            if (string.IsNullOrEmpty(segment) || segment.Length < 4 || segment[segment.Length - 1] != ']')
            {
                return false;
            }
            int open = segment.LastIndexOf('[');
            if (open < 1)
            {
                return false;
            }
            string digits = segment.Substring(open + 1, segment.Length - open - 2);
            if (digits.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < '0' || digits[i] > '9')
                {
                    return false;
                }
            }
            int parsed;
            if (!int.TryParse(digits, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }
            name = segment.Substring(0, open);
            index = parsed;
            return true;
        }

        /// <summary>
        /// Pure: whole-path instance-id form "#12345". False for anything
        /// else (including "#" followed by non-digits, which stays an
        /// ordinary object name).
        /// </summary>
        public static bool TryParseInstanceIdPath(string path, out int instanceId)
        {
            instanceId = 0;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            string trimmed = path.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '#')
            {
                return false;
            }
            return int.TryParse(trimmed.Substring(1), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out instanceId);
        }

        private static Transform FindSibling(IList<Transform> siblings, string segment)
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].name == segment)
                {
                    return siblings[i];
                }
            }
            string name;
            int index;
            if (TryParseIndexedSegment(segment, out name, out index))
            {
                int seen = 0;
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (siblings[i].name == name)
                    {
                        if (seen == index)
                        {
                            return siblings[i];
                        }
                        seen++;
                    }
                }
            }
            return null;
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
        /// sibling names, see R09 section 1.4; "Name[2]" picks the 3rd
        /// same-named sibling, and a whole path "#12345" is an instance id). Returns null with a
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
            int instanceId;
            if (TryParseInstanceIdPath(path, out instanceId))
            {
                GameObject byId = UnityObjectId.FromText(instanceId.ToString()) as GameObject;
                if (byId == null)
                {
                    error = "No GameObject with instance id " + instanceId + " (it may have been destroyed).";
                    return null;
                }
                return byId;
            }
            string[] segments = SplitHierarchyPath(path);
            if (segments.Length == 0)
            {
                error = "'path' must name at least one GameObject.";
                return null;
            }
            GameObject[] roots = scene.GetRootGameObjects();
            var rootTransforms = new List<Transform>(roots.Length);
            for (int r = 0; r < roots.Length; r++)
            {
                rootTransforms.Add(roots[r].transform);
            }
            Transform current = null;
            for (int s = 0; s < segments.Length; s++)
            {
                Transform found;
                if (s == 0)
                {
                    found = FindSibling(rootTransforms, segments[0]);
                }
                else
                {
                    var children = new List<Transform>(current.childCount);
                    for (int c = 0; c < current.childCount; c++)
                    {
                        children.Add(current.GetChild(c));
                    }
                    found = FindSibling(children, segments[s]);
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

        /// <summary>
        /// Full slash-joined hierarchy path from the scene root down to
        /// <paramref name="transform"/>, for tool result text. A segment that
        /// has a same-named sibling is emitted as "Name[i]" (0-based among
        /// those siblings) so the path resolves back to exactly this object.
        /// </summary>
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
                segments.Insert(0, DescribeSegment(t));
                t = t.parent;
            }
            return string.Join("/", segments.ToArray());
        }

        /// <summary>The path segment for one transform: its name, or "Name[i]" when a sibling shares the name.</summary>
        public static string DescribeSegment(Transform t)
        {
            if (t == null)
            {
                return string.Empty;
            }
            int count = 0;
            int index = 0;
            if (t.parent != null)
            {
                Transform parent = t.parent;
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling.name == t.name)
                    {
                        if (sibling == t)
                        {
                            index = count;
                        }
                        count++;
                    }
                }
            }
            else
            {
                Scene scene = t.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded)
                {
                    GameObject[] roots = scene.GetRootGameObjects();
                    for (int i = 0; i < roots.Length; i++)
                    {
                        if (roots[i].name == t.name)
                        {
                            if (roots[i].transform == t)
                            {
                                index = count;
                            }
                            count++;
                        }
                    }
                }
            }
            return count > 1 ? t.name + "[" + index + "]" : t.name;
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
