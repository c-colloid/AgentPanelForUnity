using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Saves a loaded scene without ever opening a dialog (design note
    /// 2026-09-17-modal-menu-and-base64-scan section 1.2). The panel had no
    /// save tool at all, so an agent finishing scene work reached for
    /// uap_editor_execute_menu "File/Save" -- which, on a scene that was
    /// never saved, opens the native Save Scene dialog and blocks the
    /// Editor until a person closes it (288 s in the live session that
    /// prompted this). EditorSceneManager.SaveScene with an explicit path
    /// never asks; with NO path on an untitled scene it opens that same
    /// dialog, which is why 'path' is demanded up front in that case
    /// instead of being passed through.
    ///
    /// Not undoable (a file write). Refuses to write over a different
    /// existing scene file unless overwrite:true -- "save as" onto another
    /// scene is the one way this tool could destroy work.
    /// </summary>
    public sealed class UapSceneSaveTool : IUapTool
    {
        public const string SceneExtension = ".unity";

        public string Name
        {
            get { return "uap_scene_save"; }
        }

        public string Description
        {
            get
            {
                return "Saves a loaded scene to disk without any dialog. A scene that already has a file is"
                    + " saved in place; an UNTITLED (never saved) scene needs 'path' (\"Assets/.../Name.unity\")."
                    + " Use this instead of uap_editor_execute_menu \"File/Save\" / \"File/Save As...\", which are"
                    + " refused because they can open a modal dialog. Not undoable.";
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
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Name or path of a loaded scene. Omit to save the active scene."))
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Where to save, under Assets/ and ending in .unity. Required for an"
                                + " untitled scene; for a scene that has a file it means \"save as\". Missing"
                                + " folders are created."))
                        .Set("saveAsCopy", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "With 'path': write a copy there and leave the open scene pointing at"
                                + " its current file (and still dirty). Default false."))
                        .Set("overwrite", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Allow 'path' to replace a DIFFERENT existing scene file. Default false.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            Scene scene = ResolveScene(input["scene"].AsString(null));
            string requested = input["path"].AsString(null);
            bool saveAsCopy = input["saveAsCopy"].AsBool(false);
            bool overwrite = input["overwrite"].AsBool(false);

            string error;
            string target = ResolveTargetPath(scene.path, requested, saveAsCopy, out error);
            if (target == null)
            {
                throw new ArgumentException(error);
            }
            bool sameFile = string.Equals(target, scene.path, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && !overwrite && File.Exists(target))
            {
                throw new InvalidOperationException("Refused: '" + target + "' already exists and is a different"
                    + " scene file. Nothing was saved. Pick another path, or pass overwrite:true to replace it.");
            }
            string folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            if (!EditorSceneManager.SaveScene(scene, target, saveAsCopy))
            {
                throw new InvalidOperationException("Unity could not save scene '" + DisplayName(scene) + "' to '"
                    + target + "' (see the Console for the reason).");
            }
            return UapToolResults.Text(saveAsCopy
                ? "Saved a copy of scene '" + DisplayName(scene) + "' to " + target + "."
                : "Saved scene '" + DisplayName(scene) + "' to " + target + ".");
        }

        /// <summary>
        /// The asset path the save goes to, or null with
        /// <paramref name="error"/>. Pure (no Unity API) so every refusal is
        /// pinned without touching a scene.
        /// </summary>
        public static string ResolveTargetPath(string currentScenePath, string requestedPath, bool saveAsCopy,
            out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(requestedPath))
            {
                if (string.IsNullOrEmpty(currentScenePath))
                {
                    error = "The scene is untitled (never saved), so 'path' is required, e.g."
                        + " \"Assets/Scenes/Main.unity\". Nothing was saved.";
                    return null;
                }
                if (saveAsCopy)
                {
                    error = "saveAsCopy:true needs 'path' (where the copy goes). Nothing was saved.";
                    return null;
                }
                return currentScenePath;
            }
            string normalized = UapAssetPath.NormalizeUnderAssets(requestedPath, out error);
            if (normalized == null)
            {
                return null;
            }
            if (!normalized.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + SceneExtension, StringComparison.OrdinalIgnoreCase))
            {
                error = "'path' must name a scene file ending in " + SceneExtension + " (got '" + requestedPath
                    + "'). Nothing was saved.";
                return null;
            }
            return normalized;
        }

        private static Scene ResolveScene(string sceneQuery)
        {
            if (string.IsNullOrEmpty(sceneQuery))
            {
                // Deliberately the active scene even while a prefab stage is
                // open: a stage's scene cannot be saved through SaveScene.
                return SceneManager.GetActiveScene();
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene candidate = SceneManager.GetSceneAt(i);
                if (string.Equals(candidate.name, sceneQuery, StringComparison.Ordinal)
                    || string.Equals(candidate.path, sceneQuery, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            throw new InvalidOperationException("Scene not found (not currently loaded): " + sceneQuery);
        }

        private static string DisplayName(Scene scene)
        {
            return string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
        }
    }
}
