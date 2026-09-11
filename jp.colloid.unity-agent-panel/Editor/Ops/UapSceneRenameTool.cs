using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Renames an existing GameObject (design section 1.2: "scene_reparent/
    /// rename/destroy"). Undoable via a plain Undo.RecordObject snapshot
    /// (GameObject.name is a regular serialized field, not a Transform
    /// hierarchy relationship, so no dedicated Undo API is needed).
    /// Prefab-stage guarded.
    /// </summary>
    public sealed class UapSceneRenameTool : IUapTool
    {
        public string Name
        {
            get { return "uap_scene_rename"; }
        }

        public string Description
        {
            get { return "Renames a GameObject in a scene."; }
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
                            .Set("description", "Hierarchy path of the GameObject to rename."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("newName", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "New name for the GameObject (must be non-empty).")))
                    .Set("required", JsonNode.NewArray().Add("path").Add("newName"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string path = input["path"].AsString(null);
            string newName = input["newName"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            if (string.IsNullOrEmpty(newName))
            {
                throw new ArgumentException("'newName' must be non-empty.");
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

            string oldName = go.name;
            Undo.RecordObject(go, "Rename " + oldName);
            go.name = newName;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

            return UapToolResults.Text("Renamed '" + oldName + "' to '" + newName + "'.");
        }
    }
}
