using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// One-call position/rotation/scale read-or-write for any GameObject,
    /// by hierarchy path, in either local or world space (2026-09-08
    /// design note, measured motivation: a live session edited a Camera's
    /// transform, a TextMeshPro text and an object's rotation through
    /// `uloop execute-dynamic-code` instead of uap_property_set -- one
    /// reason was that uap_property_set only ever writes LOCAL transform
    /// values and needs two separate calls for position+rotation, while
    /// the agent wanted world-space `transform.position` in one shot).
    /// Omitting position/rotation/scale entirely turns this into a pure
    /// read of the CURRENT values including world space, which
    /// uap_object_inspect cannot report. Undoable; prefab-stage guarded,
    /// same as uap_property_set.
    /// </summary>
    public sealed class UapTransformSetTool : IUapTool
    {
        public string Name
        {
            get { return "uap_transform_set"; }
        }

        public string Description
        {
            get
            {
                return "Sets (or reads) a GameObject's position / rotation / scale in local or world"
                    + " space in one call -- Transform, RectTransform, Camera, lights, any object --"
                    + " by hierarchy path. Omit all three to just read the current values (including"
                    + " world space).";
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
                            .Set("description", "Hierarchy path of the target GameObject."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("space", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("local").Add("world"))
                            .Set("description", "Space for 'position' and 'rotation' (default 'local'). 'scale' is always local -- Unity has no meaningful world scale setter."))
                        .Set("position", Vec3Schema("New position, in the space named by 'space'. Partial -- give only the axes you want to change; the rest keep their current value."))
                        .Set("rotation", Vec3Schema("New rotation as Euler degrees, in the space named by 'space'. Partial, same merge rule as 'position'."))
                        .Set("scale", Vec3Schema("New LOCAL scale. Partial, same merge rule as 'position'.")))
                    .Set("required", JsonNode.NewArray().Add("path"))
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
                    .Set("z", JsonNode.NewObject().Set("type", "number")))
                .Set("additionalProperties", false);
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

            string space = input["space"].AsString("local");
            bool worldSpace;
            if (string.Equals(space, "local", StringComparison.OrdinalIgnoreCase))
            {
                worldSpace = false;
            }
            else if (string.Equals(space, "world", StringComparison.OrdinalIgnoreCase))
            {
                worldSpace = true;
            }
            else
            {
                throw new ArgumentException("Unknown space: " + space + " (expected 'local' or 'world').");
            }

            Transform t = go.transform;
            JsonNode positionNode = input["position"];
            JsonNode rotationNode = input["rotation"];
            JsonNode scaleNode = input["scale"];
            bool hasPosition = positionNode != null && positionNode.IsObject;
            bool hasRotation = rotationNode != null && rotationNode.IsObject;
            bool hasScale = scaleNode != null && scaleNode.IsObject;

            if (hasPosition || hasRotation || hasScale)
            {
                Undo.RecordObject(t, "uap_transform_set");

                if (hasPosition)
                {
                    Vector3 current = worldSpace ? t.position : t.localPosition;
                    Vector3 merged = MergeVector3(positionNode, current);
                    if (worldSpace)
                    {
                        t.position = merged;
                    }
                    else
                    {
                        t.localPosition = merged;
                    }
                }
                if (hasRotation)
                {
                    Vector3 current = worldSpace ? t.eulerAngles : t.localEulerAngles;
                    Vector3 merged = MergeVector3(rotationNode, current);
                    if (worldSpace)
                    {
                        t.eulerAngles = merged;
                    }
                    else
                    {
                        t.localEulerAngles = merged;
                    }
                }
                if (hasScale)
                {
                    t.localScale = MergeVector3(scaleNode, t.localScale);
                }

                EditorSceneManager.MarkSceneDirty(go.scene);
            }

            JsonNode result = JsonNode.NewObject()
                .Set("path", path)
                .Set("space", worldSpace ? "world" : "local")
                .Set("local", JsonNode.NewObject()
                    .Set("position", Vector3ToNode(t.localPosition))
                    .Set("rotation", Vector3ToNode(t.localEulerAngles))
                    .Set("scale", Vector3ToNode(t.localScale)))
                .Set("world", JsonNode.NewObject()
                    .Set("position", Vector3ToNode(t.position))
                    .Set("rotation", Vector3ToNode(t.eulerAngles)));

            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static Vector3 MergeVector3(JsonNode node, Vector3 current)
        {
            return new Vector3(
                (float)node["x"].AsDouble(current.x),
                (float)node["y"].AsDouble(current.y),
                (float)node["z"].AsDouble(current.z));
        }

        private static JsonNode Vector3ToNode(Vector3 v)
        {
            return JsonNode.NewObject()
                .Set("x", (double)v.x)
                .Set("y", (double)v.y)
                .Set("z", (double)v.z);
        }
    }
}
