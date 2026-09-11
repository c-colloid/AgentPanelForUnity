using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Removes a component from an existing GameObject, resolved by short
    /// or fully-qualified type name plus an optional index for multiple
    /// components of the same type (design section 1.2: "component_add/
    /// remove" -- Undo.DestroyObjectImmediate). Undoable; prefab-stage
    /// guarded. Refuses to remove Transform (Unity forbids a GameObject
    /// without one).
    /// </summary>
    public sealed class UapComponentRemoveTool : IUapTool
    {
        public string Name
        {
            get { return "uap_component_remove"; }
        }

        public string Description
        {
            get
            {
                return "Removes a component from a GameObject by type name -- use uap_component_list"
                    + " first to find the type/index when several components of the same type exist."
                    + " Transform cannot be removed.";
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
                            .Set("description", "Short name (e.g. 'BoxCollider') or fully-qualified type name."))
                        .Set("componentIndex", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Which component when several of the same type exist (default 0). This is the PER-TYPE ordinal -- uap_component_list reports it as typeIndex; do not pass that tool's all-components index.")))
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
            if (typeof(Transform).IsAssignableFrom(type))
            {
                throw new InvalidOperationException("Cannot remove " + type.Name
                    + " -- every GameObject requires a Transform (or RectTransform).");
            }

            Component[] comps = go.GetComponents(type);
            int componentIndex = input["componentIndex"].AsInt(0);
            if (componentIndex < 0 || componentIndex >= comps.Length)
            {
                throw new InvalidOperationException("No component of type '" + componentTypeName
                    + "' at index " + componentIndex + " on '" + path + "' (found " + comps.Length + ").");
            }

            Component target = comps[componentIndex];

            string blocker = FindRequireComponentBlocker(go, target, type);
            if (blocker != null)
            {
                throw new InvalidOperationException("Cannot remove " + type.Name + " from " + path
                    + " -- " + blocker + " on the same GameObject requires it via [RequireComponent]."
                    + " Remove " + blocker + " first, or leave another " + type.Name
                    + "-compatible component in place.");
            }

            Undo.DestroyObjectImmediate(target);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

            if (target != null)
            {
                // Defense in depth: Unity refused the destroy without throwing
                // (the documented behavior for a blocked RequireComponent
                // dependency is a console error, not an exception). Report
                // failure instead of a false "Removed ..." success.
                throw new InvalidOperationException("Unity did not remove " + type.FullName + " from " + path
                    + " -- it may still be required by another component. No changes were made.");
            }

            return UapToolResults.Text("Removed " + type.FullName + " from " + path + ".");
        }

        /// <summary>
        /// Returns the type name of a sibling component that would be left
        /// without a [RequireComponent] dependency it declares if <paramref
        /// name="target"/> were removed, or null if removal is safe. A
        /// dependency is only a blocker when no OTHER component on the same
        /// GameObject still satisfies it (e.g. removing one of two
        /// BoxColliders is fine if something requires BoxCollider).
        /// </summary>
        private static string FindRequireComponentBlocker(GameObject go, Component target, Type targetType)
        {
            Component[] all = go.GetComponents<Component>();
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null || c == target)
                {
                    continue;
                }
                object[] attrs = c.GetType().GetCustomAttributes(typeof(RequireComponent), true);
                for (int a = 0; a < attrs.Length; a++)
                {
                    var req = (RequireComponent)attrs[a];
                    if (RequirementBlocksRemoval(req.m_Type0, targetType, go, target)
                        || RequirementBlocksRemoval(req.m_Type1, targetType, go, target)
                        || RequirementBlocksRemoval(req.m_Type2, targetType, go, target))
                    {
                        return c.GetType().Name;
                    }
                }
            }
            return null;
        }

        private static bool RequirementBlocksRemoval(Type requiredType, Type targetType, GameObject go, Component target)
        {
            if (requiredType == null || !requiredType.IsAssignableFrom(targetType))
            {
                return false;
            }
            Component[] candidates = go.GetComponents(requiredType);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && candidates[i] != target)
                {
                    // Another component still satisfies the requirement.
                    return false;
                }
            }
            return true;
        }
    }
}
