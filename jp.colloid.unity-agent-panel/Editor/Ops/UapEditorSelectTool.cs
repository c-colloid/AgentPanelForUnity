using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Sets the Editor's object selection (design note
    /// 2026-09-14-selection-set-tool): the missing half of
    /// <see cref="UapEditorExecuteMenuTool"/>. A large share of the menu
    /// items worth driving through uap_editor_execute_menu do not take a
    /// target argument at all -- they read Selection and act on whatever
    /// is there. NDMF's and Modular Avatar's "Manual bake avatar" are the
    /// motivating case (both call AvatarProcessor.CanProcessObject/
    /// ManualProcessAvatar on Selection.activeGameObject), but the same is
    /// true of the VRChat SDK build panel, UniVRM's exporters, Bakery's
    /// "selected" bake scope and RPG Maker Unite's editors.
    ///
    /// Until this tool existed the panel could only ever READ the
    /// selection (SelectionContextProvider, for the context bar) -- so
    /// "select the avatar, then bake it" was a step the agent had to ask a
    /// human to perform by hand, mid-task, with no way to verify they had.
    ///
    /// NOT ReadOnly, deliberately. It writes nothing to the project or the
    /// scene, but it changes what the NEXT tool call acts on, and it moves
    /// what the user sees in the Hierarchy/Inspector -- the same reasoning
    /// that keeps uap_marker_add out of the auto-approved set (see
    /// UapReadOnlyToolMetadataTests).
    /// </summary>
    public sealed class UapEditorSelectTool : IUapTool
    {
        /// <summary>Refuses absurd selections rather than freezing the Editor building them.</summary>
        public const int MaxObjects = 256;

        public string Name
        {
            get { return "uap_editor_select"; }
        }

        public string Description
        {
            get
            {
                return "Selects GameObjects or assets in the Editor (sets Selection), so a menu command that"
                    + " acts on 'the current selection' has the right target -- run this BEFORE"
                    + " uap_editor_execute_menu for any menu item that takes no argument of its own"
                    + " (NDMF / Modular Avatar \"Manual bake avatar\", the VRChat SDK build panel, UniVRM"
                    + " export, Bakery's selected-scope bake). Pass 'path' for one scene object, 'paths'"
                    + " for several, 'assetPath' for a project asset, or clear:true to deselect."
                    + " Changes no project or scene data; it does move the user's Hierarchy/Inspector"
                    + " focus. Returns what ended up selected.";
            }
        }

        public string Module
        {
            get { return "editor"; }
        }

        public bool Undoable
        {
            get { return false; }
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
                            .Set("description", "Hierarchy path of one scene GameObject to select. Mutually exclusive with 'paths', 'assetPath' and 'clear'."))
                        .Set("paths", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "string"))
                            .Set("description", "Hierarchy paths of several scene GameObjects to select at once (max "
                                + MaxObjects + "). Unity picks one of them as the active object, so use 'path' when a menu"
                                + " item acts on the ACTIVE object specifically. Mutually exclusive with 'path', 'assetPath' and 'clear'."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path, used with 'path'/'paths'. Omit to use the active scene (or the open prefab stage)."))
                        .Set("assetPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Project-relative path of an asset to select, e.g. 'Assets/Avatars/Body.prefab'. Mutually exclusive with 'path', 'paths' and 'clear'."))
                        .Set("clear", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Deselect everything. Mutually exclusive with the other three.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string hierarchyPath = input["path"].AsString(null);
            string assetPath = input["assetPath"].AsString(null);
            bool clear = input["clear"].AsBool(false);
            List<string> paths = ReadPaths(input["paths"]);

            int given = 0;
            if (!string.IsNullOrEmpty(hierarchyPath)) { given++; }
            if (paths.Count > 0) { given++; }
            if (!string.IsNullOrEmpty(assetPath)) { given++; }
            if (clear) { given++; }
            if (given != 1)
            {
                throw new ArgumentException("Specify exactly one of 'path', 'paths', 'assetPath' or clear:true.");
            }

            if (clear)
            {
                Selection.objects = new UnityEngine.Object[0];
                return Describe("Cleared the selection.");
            }

            string sceneQuery = input["scene"].AsString(null);
            var selected = new List<UnityEngine.Object>();

            if (!string.IsNullOrEmpty(assetPath))
            {
                string error;
                UnityEngine.Object asset = UapAddressing.ResolveAsset(assetPath, out error);
                if (asset == null)
                {
                    throw new InvalidOperationException(error);
                }
                selected.Add(asset);
            }
            else
            {
                if (!string.IsNullOrEmpty(hierarchyPath))
                {
                    paths = new List<string> { hierarchyPath };
                }
                if (paths.Count > MaxObjects)
                {
                    throw new ArgumentException("Too many paths (" + paths.Count + "); at most "
                        + MaxObjects + " objects can be selected in one call.");
                }
                foreach (string p in paths)
                {
                    string error;
                    GameObject go = UapAddressing.ResolveHierarchyPath(sceneQuery, p, out error);
                    if (go == null)
                    {
                        // Resolve every path before touching Selection, so a
                        // typo in the third path cannot leave the Editor on a
                        // half-applied selection the agent then bakes.
                        throw new InvalidOperationException(error);
                    }
                    selected.Add(go);
                }
            }

            if (selected.Count == 1)
            {
                // Selection.activeObject's setter makes that object the whole
                // selection AND the active one. Assigning Selection.objects
                // instead would leave which entry becomes active up to Unity,
                // and the active object is precisely what the menu items this
                // tool exists for (Manual bake avatar) read.
                Selection.activeObject = selected[0];
            }
            else
            {
                // For a genuine multi-selection the active entry is Unity's
                // choice; writing activeObject afterwards would collapse the
                // selection back to one object.
                Selection.objects = selected.ToArray();
            }
            return Describe("Selected " + DescribeSelection(selected) + ".");
        }

        private static List<string> ReadPaths(JsonNode node)
        {
            var result = new List<string>();
            foreach (JsonNode item in node.Items)
            {
                string value = item.AsString(null);
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        private static string DescribeSelection(List<UnityEngine.Object> selected)
        {
            if (selected.Count == 1)
            {
                return "'" + selected[0].name + "'";
            }
            return selected.Count + " objects, active '" + selected[0].name + "'";
        }

        private static JsonNode Describe(string message)
        {
            JsonNode names = JsonNode.NewArray();
            UnityEngine.Object[] objects = Selection.objects;
            if (objects != null)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i] != null)
                    {
                        names.Add(objects[i].name);
                    }
                }
            }
            JsonNode result = JsonNode.NewObject()
                .Set("message", message)
                .Set("selectionCount", Selection.objects == null ? 0 : Selection.objects.Length)
                .Set("active", Selection.activeObject != null ? Selection.activeObject.name : null)
                .Set("selected", names);
            return UapToolResults.Text(JsonWriter.Write(result));
        }
    }
}
