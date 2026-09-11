using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Destroys an existing GameObject in a scene (design section 1.2:
    /// "scene_reparent/rename/destroy" -- Undo.DestroyObjectImmediate).
    /// Undoable; prefab-stage guarded. Refuses a prefab ASSET path (e.g.
    /// "Assets/Foo.prefab") with a structured error pointing at
    /// uap_asset_delete instead -- this tool only ever destroys a resolved
    /// SCENE instance.
    /// </summary>
    public sealed class UapSceneDestroyObjectTool : IUapTool
    {
        public string Name
        {
            get { return "uap_scene_destroy_object"; }
        }

        public string Description
        {
            get
            {
                return "Destroys a GameObject in a scene. For a prefab ASSET file (not a scene instance),"
                    + " use uap_asset_delete instead.";
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
                            .Set("description", "Hierarchy path of the GameObject to destroy, e.g. 'Root/Enemy'."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage).")))
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
            if (LooksLikePrefabAssetPath(path))
            {
                throw new InvalidOperationException("'path' looks like a prefab ASSET path ('" + path
                    + "'), not a scene hierarchy path. Use uap_asset_delete to delete a prefab asset instead.");
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

            string description = UapAddressing.DescribeHierarchyPath(go.transform);
            UnityEngine.SceneManagement.Scene scene = go.scene;

            Undo.DestroyObjectImmediate(go);
            // OPS-6: Unity can refuse a destroy without throwing (a Console
            // error only) -- the same defense-in-depth UapComponentRemoveTool
            // has, so a refused destroy never reports "Destroyed ...".
            // A destroyed UnityEngine.Object compares equal to null.
            if (go != null)
            {
                throw new InvalidOperationException("Unity did not destroy '" + description
                    + "' -- see the Console for Unity's own reason. No changes were made.");
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            return UapToolResults.Text("Destroyed '" + description + "'.");
        }

        private static bool LooksLikePrefabAssetPath(string path)
        {
            return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }
    }
}
