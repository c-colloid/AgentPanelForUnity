using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Opens a scene file without ever opening a dialog (design note
    /// 2026-09-17-modal-menu-and-base64-scan section 1.6) -- the
    /// counterpart of <see cref="UapSceneSaveTool"/> for the refused
    /// "File/Open Scene" menu, which asks for the file in a native modal
    /// dialog that blocks the Editor until a person closes it.
    ///
    /// EditorSceneManager.OpenScene never asks anything: in Single mode it
    /// silently DROPS every unsaved change in the scenes it replaces (the
    /// menu's "Scene(s) Have Been Modified" prompt lives in the menu
    /// handler, not in the API). That is the one way this tool could
    /// destroy work, so a Single open while a loaded scene is dirty is
    /// refused -- naming the dirty scenes -- unless discardUnsaved:true.
    /// Additive never unloads anything and needs no gate. Not undoable.
    /// </summary>
    public sealed class UapSceneOpenTool : IUapTool
    {
        public const string ModeSingle = "single";
        public const string ModeAdditive = "additive";

        public string Name
        {
            get { return "uap_scene_open"; }
        }

        public string Description
        {
            get
            {
                return "Opens a scene file (\"Assets/.../Name.unity\") without any dialog. mode \"single\" (default)"
                    + " replaces the loaded scenes and is REFUSED while one of them has unsaved changes -- save"
                    + " them with uap_scene_save first, or pass discardUnsaved:true to drop those changes;"
                    + " mode \"additive\" loads it next to them. Use this instead of uap_editor_execute_menu"
                    + " \"File/Open Scene\", which is refused because it opens a modal dialog. Not undoable;"
                    + " not available in Play Mode.";
            }
        }

        public string Module
        {
            get { return "core"; }
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
                            .Set("description", "Scene asset path ending in .unity, under Assets/ or Packages/"
                                + " (find one with uap_asset_find)."))
                        .Set("mode", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add(ModeSingle).Add(ModeAdditive))
                            .Set("description", "\"single\" (default) replaces every loaded scene; \"additive\" keeps them."))
                        .Set("discardUnsaved", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Only for mode \"single\": open even though loaded scenes have unsaved"
                                + " changes, LOSING those changes. Default false.")))
                    .Set("required", JsonNode.NewArray().Add("path"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string mode = input["mode"].AsString(ModeSingle);
            bool discardUnsaved = input["discardUnsaved"].AsBool(false);

            string error;
            string path = NormalizeScenePath(input["path"].AsString(null), out error);
            if (path == null)
            {
                throw new ArgumentException(error);
            }
            if (mode != ModeSingle && mode != ModeAdditive)
            {
                throw new ArgumentException("Unknown mode '" + mode + "'; use \"single\" or \"additive\".");
            }
            // A Packages/ path is virtual (the package may live outside the
            // project folder), so the file check alone would refuse it.
            if (!File.Exists(path) && AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(SceneAsset))
            {
                throw new InvalidOperationException("Scene file not found: " + path
                    + ". Nothing was opened (find scenes with uap_asset_find).");
            }
            string refusal = Refusal(mode, discardUnsaved, EditorApplication.isPlayingOrWillChangePlaymode,
                DirtyLoadedSceneNames());
            if (refusal != null)
            {
                throw new InvalidOperationException(refusal);
            }

            Scene opened;
            try
            {
                opened = EditorSceneManager.OpenScene(path,
                    mode == ModeAdditive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            }
            catch (Exception ex)
            {
                // E.g. additive while the active scene is untitled: Unity's
                // own sentence is the most useful thing to hand back.
                throw new InvalidOperationException("Unity could not open '" + path + "': " + ex.Message);
            }
            if (!opened.IsValid())
            {
                throw new InvalidOperationException("Unity could not open '" + path + "' (see the Console for the reason).");
            }
            return UapToolResults.Text("Opened scene '" + opened.name + "' (" + path + ", " + mode + "); "
                + SceneManager.sceneCount + " scene(s) loaded.");
        }

        /// <summary>
        /// Why this open must not run, or null. Pure (no Unity API) so the
        /// gate that protects unsaved work is pinned without opening a
        /// scene under the test runner.
        /// </summary>
        public static string Refusal(string mode, bool discardUnsaved, bool playing, IList<string> dirtySceneNames)
        {
            if (playing)
            {
                return "Refused: scenes cannot be opened through the Editor API in Play Mode. Nothing was opened."
                    + " Exit Play Mode first.";
            }
            if (mode == ModeSingle && !discardUnsaved && dirtySceneNames != null && dirtySceneNames.Count > 0)
            {
                return "Refused: opening in mode \"single\" unloads the current scene(s), and "
                    + string.Join(", ", new List<string>(dirtySceneNames).ToArray())
                    + " ha" + (dirtySceneNames.Count == 1 ? "s" : "ve") + " unsaved changes that would be lost."
                    + " Nothing was opened. Save with uap_scene_save first (an untitled scene needs 'path'),"
                    + " or re-issue with discardUnsaved:true to drop those changes, or use mode \"additive\".";
            }
            return null;
        }

        /// <summary>
        /// '\' to '/', dot segments collapsed, must end in .unity and sit
        /// under Assets/ or Packages/ (a scene in a package can be opened,
        /// unlike saved to). Null with <paramref name="error"/> otherwise.
        /// </summary>
        public static string NormalizeScenePath(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "'path' is required (a scene asset path such as \"Assets/Scenes/Main.unity\").";
                return null;
            }
            string normalized = UapAssetPath.CollapseDotSegments(path.Trim().Replace('\\', '/'));
            bool rooted = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            if (!rooted
                || !normalized.EndsWith(UapSceneSaveTool.SceneExtension, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + UapSceneSaveTool.SceneExtension, StringComparison.OrdinalIgnoreCase))
            {
                error = "'path' must be a scene asset path under Assets/ or Packages/ ending in "
                    + UapSceneSaveTool.SceneExtension + " (got '" + path + "'). Nothing was opened.";
                return null;
            }
            return normalized;
        }

        private static List<string> DirtyLoadedSceneNames()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    names.Add("'" + (string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name) + "'");
                }
            }
            return names;
        }
    }
}
