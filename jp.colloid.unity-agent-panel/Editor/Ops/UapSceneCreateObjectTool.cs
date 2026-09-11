using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Creates a new GameObject (empty or a primitive) in a scene (design
    /// section 1.2, R09 section 1.1: ObjectFactory over `new GameObject()`
    /// so Undo/Preset defaults are automatic). Undoable; prefab-stage
    /// guarded.
    /// </summary>
    public sealed class UapSceneCreateObjectTool : IUapTool
    {
        public string Name
        {
            get { return "uap_scene_create_object"; }
        }

        public string Description
        {
            get
            {
                return "Creates a new GameObject (empty, or a primitive like Cube/Sphere) in a scene,"
                    + " optionally parented and positioned. No script/compile required.";
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
                        .Set("name", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Name for the new GameObject (required)."))
                        .Set("primitiveType", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Cube/Sphere/Capsule/Cylinder/Plane/Quad. Omit for an empty GameObject."))
                        .Set("parentPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the parent, e.g. 'Root/Container'. Omit for a scene root object."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("position", Vec3Schema("Local position (defaults to 0,0,0)."))
                        .Set("rotation", Vec3Schema("Local Euler rotation in degrees (defaults to 0,0,0)."))
                        .Set("scale", Vec3Schema("Local scale (defaults to 1,1,1)."))
                    )
                    .Set("required", JsonNode.NewArray().Add("name"))
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
            string name = input["name"].AsString(null);
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("'name' is required.");
            }
            string sceneQuery = input["scene"].AsString(null);
            string parentPath = input["parentPath"].AsString(null);

            string error;
            Scene scene = UapAddressing.ResolveTargetScene(sceneQuery, out error);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
            if (!UapPrefabStageGuard.CheckScene(scene, out error))
            {
                throw new InvalidOperationException(error);
            }

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parent = UapAddressing.ResolveInScene(scene, parentPath, out error);
                if (parent == null)
                {
                    throw new InvalidOperationException(error);
                }
            }
            // OPS-8: with a prefab stage open and parentPath omitted, the
            // new object would become a SECOND ROOT of the stage's virtual
            // scene -- and a prefab stage saves only its single prefab
            // root, so the object silently vanished on save. Default the
            // parent to the stage's prefab contents root instead; an
            // explicit parentPath still wins.
            bool parentedToStageRoot = false;
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (parent == null && stage != null && stage.scene == scene)
            {
                parent = stage.prefabContentsRoot;
                parentedToStageRoot = true;
            }

            string primitiveTypeName = input["primitiveType"].AsString(null);
            GameObject go;
            if (!string.IsNullOrEmpty(primitiveTypeName))
            {
                PrimitiveType primitiveType;
                if (!TryParsePrimitiveType(primitiveTypeName, out primitiveType))
                {
                    throw new ArgumentException("Unknown primitiveType: " + primitiveTypeName
                        + " (expected Cube/Sphere/Capsule/Cylinder/Plane/Quad).");
                }
                go = ObjectFactory.CreatePrimitive(primitiveType);
                go.name = name;
            }
            else
            {
                go = ObjectFactory.CreateGameObject(name);
            }

            // Parent FIRST when there is one: SetParent already carries the
            // object into the parent's scene, and (OPS-8) a prefab stage's
            // preview scene is exactly the case where reparenting is the
            // reliable way in.
            if (parent != null)
            {
                go.transform.SetParent(parent.transform, false);
            }
            else if (go.scene != scene)
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            ApplyTransform(go.transform, input["position"], input["rotation"], input["scale"]);

            EditorSceneManager.MarkSceneDirty(go.scene);

            return UapToolResults.Text("Created '" + name + "' at "
                + UapAddressing.DescribeHierarchyPath(go.transform) + " in scene '" + scene.name + "'."
                + (parentedToStageRoot
                    ? " (Prefab stage open and no parentPath given, so it was parented under the"
                        + " prefab root -- a second stage root would be discarded on save.)"
                    : string.Empty));
        }

        private static bool TryParsePrimitiveType(string name, out PrimitiveType result)
        {
            foreach (PrimitiveType value in (PrimitiveType[])Enum.GetValues(typeof(PrimitiveType)))
            {
                if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    result = value;
                    return true;
                }
            }
            result = default(PrimitiveType);
            return false;
        }

        private static void ApplyTransform(Transform t, JsonNode position, JsonNode rotation, JsonNode scale)
        {
            if (position != null && position.IsObject)
            {
                t.localPosition = ReadVector3(position, Vector3.zero);
            }
            if (rotation != null && rotation.IsObject)
            {
                t.localEulerAngles = ReadVector3(rotation, Vector3.zero);
            }
            if (scale != null && scale.IsObject)
            {
                t.localScale = ReadVector3(scale, Vector3.one);
            }
        }

        private static Vector3 ReadVector3(JsonNode node, Vector3 fallback)
        {
            return new Vector3(
                (float)node["x"].AsDouble(fallback.x),
                (float)node["y"].AsDouble(fallback.y),
                (float)node["z"].AsDouble(fallback.z));
        }
    }
}
