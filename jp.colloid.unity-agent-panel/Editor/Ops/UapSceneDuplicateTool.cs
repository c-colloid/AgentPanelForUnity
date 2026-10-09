using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Duplicates a scene GameObject (with its whole subtree) one or more
    /// times, optionally under another parent and with a per-copy local
    /// position offset. Undoable; prefab-stage guarded. A prefab instance
    /// root is re-instantiated from its prefab asset (keeping the prefab
    /// link) but its property overrides are not copied.
    /// </summary>
    public sealed class UapSceneDuplicateTool : IUapTool
    {
        private const int MaxCount = 100;

        public string Name
        {
            get { return "uap_scene_duplicate"; }
        }

        public string Description
        {
            get
            {
                return "Duplicates a scene GameObject (and its children) 'count' times, optionally under"
                    + " another parent and with a per-copy local position offset (offset x copy index)."
                    + " Copies get unique sibling names. A prefab instance root is re-instantiated from its"
                    + " prefab (link kept) but property overrides are NOT copied. Returns each copy's"
                    + " index-suffixed path and instanceId.";
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
                            .Set("description", "Hierarchy path of the GameObject to duplicate (Name[i] / #instanceId forms accepted)."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("count", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "How many copies to make, 1.." + MaxCount + " (default 1)."))
                        .Set("name", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Base name for the copies. Default: the source name. Names are made unique among siblings."))
                        .Set("parentPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the parent for the copies. Omit to keep the source's parent."))
                        .Set("offset", JsonNode.NewObject()
                            .Set("type", "object")
                            .Set("description", "Local position offset per copy; copy N is moved by offset x N from the source's local position.")
                            .Set("properties", JsonNode.NewObject()
                                .Set("x", JsonNode.NewObject().Set("type", "number"))
                                .Set("y", JsonNode.NewObject().Set("type", "number"))
                                .Set("z", JsonNode.NewObject().Set("type", "number"))))
                        .Set("select", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true selects the new copies in the editor (default false).")))
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
            int count = input["count"].AsInt(1);
            if (count < 1 || count > MaxCount)
            {
                throw new ArgumentException("'count' must be between 1 and " + MaxCount + ".");
            }
            string sceneQuery = input["scene"].AsString(null);
            string parentPath = input["parentPath"].AsString(null);
            string baseName = input["name"].AsString(null);

            string error;
            Scene scene = UapAddressing.ResolveTargetScene(sceneQuery, out error);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
            GameObject source = UapAddressing.ResolveInScene(scene, path, out error);
            if (source == null)
            {
                throw new InvalidOperationException(error);
            }
            if (!UapPrefabStageGuard.Check(source, out error))
            {
                throw new InvalidOperationException(error);
            }

            Transform parent = source.transform.parent;
            if (!string.IsNullOrEmpty(parentPath))
            {
                GameObject parentGo = UapAddressing.ResolveInScene(scene, parentPath, out error);
                if (parentGo == null)
                {
                    throw new InvalidOperationException(error);
                }
                if (!UapPrefabStageGuard.Check(parentGo, out error))
                {
                    throw new InvalidOperationException(error);
                }
                parent = parentGo.transform;
            }
            else if (parent == null)
            {
                // A prefab stage keeps only its single root: a second root
                // copy would be discarded on save.
                PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && source.scene == stage.scene)
                {
                    parent = stage.prefabContentsRoot.transform;
                }
            }

            Vector3 offset = Vector3.zero;
            JsonNode offsetNode = input["offset"];
            if (offsetNode.IsObject)
            {
                offset = new Vector3((float)offsetNode["x"].AsDouble(0.0), (float)offsetNode["y"].AsDouble(0.0),
                    (float)offsetNode["z"].AsDouble(0.0));
            }

            Transform sourceTransform = source.transform;
            Vector3 localPosition = sourceTransform.localPosition;
            Quaternion localRotation = sourceTransform.localRotation;
            Vector3 localScale = sourceTransform.localScale;
            string nameToUse = string.IsNullOrEmpty(baseName) ? source.name : baseName;

            GameObject prefabAsset = null;
            bool prefabInstanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(source);
            if (prefabInstanceRoot)
            {
                prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(source);
                if (prefabAsset == null)
                {
                    prefabInstanceRoot = false;
                }
            }

            var created = new GameObject[count];
            for (int i = 0; i < count; i++)
            {
                GameObject clone;
                if (prefabInstanceRoot)
                {
                    clone = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, source.scene);
                }
                else
                {
                    clone = UnityEngine.Object.Instantiate(source);
                }
                Undo.RegisterCreatedObjectUndo(clone, "Duplicate " + source.name);
                if (clone.scene != source.scene)
                {
                    SceneManager.MoveGameObjectToScene(clone, source.scene);
                }
                clone.transform.SetParent(parent, false);
                clone.name = "UapDuplicateTemp";
                clone.name = GameObjectUtility.GetUniqueNameForSibling(parent, nameToUse);
                clone.transform.localPosition = localPosition + offset * (i + 1);
                clone.transform.localRotation = localRotation;
                clone.transform.localScale = localScale;
                created[i] = clone;
            }
            EditorSceneManager.MarkSceneDirty(source.scene);

            if (input["select"].AsBool(false))
            {
                var selection = new UnityEngine.Object[count];
                for (int i = 0; i < count; i++)
                {
                    selection[i] = created[i];
                }
                Selection.objects = selection;
            }

            JsonNode items = JsonNode.NewArray();
            for (int i = 0; i < count; i++)
            {
                items.Add(JsonNode.NewObject()
                    .Set("path", UapAddressing.DescribeHierarchyPath(created[i].transform))
                    .Set("instanceId", UnityObjectId.TextOf(created[i])));
            }
            JsonNode result = JsonNode.NewObject()
                .Set("source", UapAddressing.DescribeHierarchyPath(source.transform))
                .Set("count", count)
                .Set("created", items);
            if (prefabInstanceRoot)
            {
                result.Set("note", "The source is a prefab instance: copies were re-instantiated from the prefab"
                    + " asset; property overrides on the source were not copied.");
            }
            return UapToolResults.Text(JsonWriter.Write(result));
        }
    }
}
