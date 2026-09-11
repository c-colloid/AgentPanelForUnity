using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Places a project asset (a prefab, or any GameObject-typed asset such
    /// as an imported model) into a scene (design section 1.2:
    /// "scene_place_asset" -- R09 section 2.1: PrefabUtility.InstantiatePrefab
    /// keeps the prefab connection, so it must be used instead of
    /// Object.Instantiate whenever the asset IS a prefab asset). Undoable
    /// via RegisterCreatedObjectUndo; prefab-stage guarded.
    /// </summary>
    public sealed class UapScenePlaceAssetTool : IUapTool
    {
        public string Name
        {
            get { return "uap_scene_place_asset"; }
        }

        public string Description
        {
            get
            {
                return "Places a prefab (or other GameObject-typed asset, e.g. an imported model) into"
                    + " a scene, optionally parented and positioned. Prefabs keep their prefab connection.";
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
                        .Set("assetPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Project-relative path to a prefab/GameObject asset, e.g. 'Assets/Prefabs/Enemy.prefab'."))
                        .Set("parentPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the parent, e.g. 'Root/Container'. Omit for a scene root object."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("position", Vec3Schema("Local position (defaults to the prefab's own local position).")))
                    .Set("required", JsonNode.NewArray().Add("assetPath"))
                    .Set("additionalProperties", false);
            }
        }

        private static JsonNode Vec3Schema(string description)
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("description", description)
                .Set("properties", JsonNode.NewObject()
                    .Set("x", JsonNode.NewObject().Set("type", "number"))
                    .Set("y", JsonNode.NewObject().Set("type", "number"))
                    .Set("z", JsonNode.NewObject().Set("type", "number")));
        }

        public JsonNode Execute(JsonNode input)
        {
            string assetPath = input["assetPath"].AsString(null);
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new ArgumentException("'assetPath' is required.");
            }

            string error;
            UnityEngine.Object asset = UapAddressing.ResolveAsset(assetPath, out error);
            if (asset == null)
            {
                throw new InvalidOperationException(error);
            }
            GameObject source = asset as GameObject;
            if (source == null)
            {
                throw new InvalidOperationException("Asset at '" + assetPath
                    + "' is not a placeable GameObject (found " + asset.GetType().Name + ").");
            }

            string sceneQuery = input["scene"].AsString(null);
            Scene scene = UapAddressing.ResolveTargetScene(sceneQuery, out error);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
            if (!UapPrefabStageGuard.CheckScene(scene, out error))
            {
                throw new InvalidOperationException(error);
            }

            string parentPath = input["parentPath"].AsString(null);
            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parent = UapAddressing.ResolveInScene(scene, parentPath, out error);
                if (parent == null)
                {
                    throw new InvalidOperationException(error);
                }
            }

            GameObject instance;
            if (PrefabUtility.IsPartOfPrefabAsset(source))
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
            }
            else
            {
                instance = UnityEngine.Object.Instantiate(source);
                if (instance.scene != scene)
                {
                    SceneManager.MoveGameObjectToScene(instance, scene);
                }
            }
            Undo.RegisterCreatedObjectUndo(instance, "Place " + source.name);

            if (parent != null)
            {
                instance.transform.SetParent(parent.transform, false);
            }
            JsonNode position = input["position"];
            if (position != null && position.IsObject)
            {
                instance.transform.localPosition = new Vector3(
                    (float)position["x"].AsDouble(instance.transform.localPosition.x),
                    (float)position["y"].AsDouble(instance.transform.localPosition.y),
                    (float)position["z"].AsDouble(instance.transform.localPosition.z));
            }

            EditorSceneManager.MarkSceneDirty(instance.scene);

            return UapToolResults.Text("Placed '" + assetPath + "' as "
                + UapAddressing.DescribeHierarchyPath(instance.transform) + " in scene '" + scene.name + "'.");
        }
    }
}
