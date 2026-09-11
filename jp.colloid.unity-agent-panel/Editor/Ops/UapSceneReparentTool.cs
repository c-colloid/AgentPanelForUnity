using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Re-parents an existing GameObject under a new parent, or to the
    /// scene root when 'newParentPath' is omitted (design section 1.2:
    /// "scene_reparent/rename/destroy" -- Undo.SetTransformParent, R09
    /// section 1.2 -- the only Undo.SetTransformParent overload confirmed
    /// to exist takes no worldPositionStays argument, so
    /// worldPositionStays:false is applied as a second, still-undoable
    /// step that restores the pre-reparent local transform). Undoable;
    /// prefab-stage guarded. Refuses when the new parent is the target
    /// itself or a descendant of it (a cycle).
    /// </summary>
    public sealed class UapSceneReparentTool : IUapTool
    {
        public string Name
        {
            get { return "uap_scene_reparent"; }
        }

        public string Description
        {
            get
            {
                return "Re-parents a GameObject under a new parent, or to the scene root when"
                    + " 'newParentPath' is omitted/empty.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return true; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the GameObject to re-parent."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("newParentPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the new parent. Omit or empty for the scene root."))
                        .Set("worldPositionStays", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Keep world position/rotation/scale unchanged (default true).")))
                    .Set("required", JsonNode.NewArray().Add("path"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string path = input["path"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            string sceneQuery = input["scene"].AsString(null);
            string error;
            GameObject go = UapAddressing.ResolveHierarchyPath(sceneQuery, path, out error);
            if (go == null)
            {
                throw new InvalidOperationException(error);
            }
            if (!UapPrefabStageGuard.Check(go, out error))
            {
                throw new InvalidOperationException(error);
            }

            string newParentPath = input["newParentPath"].AsString(null);
            GameObject newParent = null;
            if (!string.IsNullOrEmpty(newParentPath))
            {
                Scene scene = go.scene;
                newParent = UapAddressing.ResolveInScene(scene, newParentPath, out error);
                if (newParent == null)
                {
                    throw new InvalidOperationException(error);
                }
                if (IsSameOrDescendant(newParent.transform, go.transform))
                {
                    throw new InvalidOperationException("Cannot re-parent '" + path + "' under '" + newParentPath
                        + "': the new parent is the target itself or one of its descendants (would create a cycle).");
                }
            }

            bool worldPositionStays = input["worldPositionStays"].AsBool(true);
            Vector3 localPosition = go.transform.localPosition;
            Quaternion localRotation = go.transform.localRotation;
            Vector3 localScale = go.transform.localScale;

            Transform newParentTransform = newParent != null ? newParent.transform : null;
            Undo.SetTransformParent(go.transform, newParentTransform, "Reparent " + go.name);

            if (!worldPositionStays)
            {
                Undo.RecordObject(go.transform, "Reparent " + go.name);
                go.transform.localPosition = localPosition;
                go.transform.localRotation = localRotation;
                go.transform.localScale = localScale;
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

            return UapToolResults.Text("Re-parented '" + path + "' under "
                + (newParent != null ? "'" + newParentPath + "'" : "the scene root") + ".");
        }

        /// <summary>True when <paramref name="candidate"/> IS <paramref name="target"/>, or one of its ancestors is.</summary>
        private static bool IsSameOrDescendant(Transform candidate, Transform target)
        {
            Transform t = candidate;
            while (t != null)
            {
                if (t == target)
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }
    }
}
