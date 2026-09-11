using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Adds a component to an existing GameObject, resolved by short or
    /// fully-qualified type name (design section 1.2/3b). Uses the plain
    /// runtime GameObject.AddComponent(Type) plus an explicit
    /// Undo.RegisterCreatedObjectUndo -- R09 confirms RegisterCreatedObjectUndo
    /// works for any UnityEngine.Object, components included, so this is a
    /// single well-known-correct Undo path regardless of which
    /// ObjectFactory overloads a given Unity minor version happens to
    /// expose.
    /// </summary>
    public sealed class UapComponentAddTool : IUapTool
    {
        public string Name
        {
            get { return "uap_component_add"; }
        }

        public string Description
        {
            get
            {
                return "Adds a component (built-in or any already-compiled custom type) to a GameObject"
                    + " by name -- use uap_query_component_types first if the exact type name is unknown.";
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
                            .Set("description", "Hierarchy path of the target GameObject, e.g. 'Root/Enemy'."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("componentType", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Short name (e.g. 'BoxCollider') or fully-qualified type name.")))
                    .Set("required", JsonNode.NewArray().Add("path").Add("componentType"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string path = input["path"].AsString(null);
            string componentTypeName = input["componentType"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            if (string.IsNullOrEmpty(componentTypeName))
            {
                throw new ArgumentException("'componentType' is required.");
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

            Type type = UapComponentTypeResolver.ResolveComponentType(componentTypeName, out error);
            if (type == null)
            {
                throw new InvalidOperationException(error);
            }
            // OPS-5 (pre-check half): the resolver's TypeCache sweep also
            // matches abstract bases (Collider, Renderer...), which
            // AddComponent cannot instantiate. Say so up front instead of
            // letting the null-return path below report it less precisely.
            // NOTE (measured in CI): this catches types abstract in C#
            // (custom abstract MonoBehaviours). Several Unity built-ins the
            // engine REFUSES as "abstract" -- UnityEngine.Collider,
            // Renderer -- are declared concrete in the C# API, so they fall
            // through to the null-return guard below instead. Both paths
            // refuse; only the wording differs.
            if (type.IsAbstract)
            {
                throw new InvalidOperationException("'" + componentTypeName + "' resolves to " + type.FullName
                    + ", which is abstract and cannot be added directly -- pick a concrete component type"
                    + " (e.g. BoxCollider rather than Collider); uap_query_component_types lists them.");
            }

            // OPS-5: AddComponent returns null instead of throwing when
            // Unity refuses the add ([DisallowMultipleComponent] duplicate,
            // unsatisfiable [RequireComponent], ...). The old code passed
            // that null to RegisterCreatedObjectUndo and reported a false
            // "Added ..." success.
            Component added = go.AddComponent(type);
            if (added == null)
            {
                throw new InvalidOperationException("Unity did not add " + type.FullName + " to '" + path
                    + "' -- most often a [DisallowMultipleComponent] duplicate, an unsatisfiable"
                    + " [RequireComponent], or a type Unity treats as abstract natively even though it"
                    + " is concrete in C# (UnityEngine.Collider, UnityEngine.Renderer, ...); see the"
                    + " Console for Unity's own reason. No changes were made.");
            }
            Undo.RegisterCreatedObjectUndo(added, "Add Component " + type.Name);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

            return UapToolResults.Text("Added " + type.FullName + " to " + path + ".");
        }
    }
}
