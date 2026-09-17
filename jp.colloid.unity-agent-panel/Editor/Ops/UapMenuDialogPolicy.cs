using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Which built-in menu items uap_editor_execute_menu refuses because
    /// they open a NATIVE MODAL dialog (design note
    /// 2026-09-17-modal-menu-and-base64-scan section 1). Such a dialog runs
    /// its own Win32 message loop inside ExecuteMenuItem: the Editor loop
    /// does not tick once until a person closes it (measured: 0 ticks in
    /// 110 s), so every later uap_* call dies "never started", the panel
    /// itself stops answering permission requests, and nothing the agent
    /// can call is able to dismiss the dialog. A live Codex session sat
    /// behind "File/Save" on an untitled scene for 288 s.
    ///
    /// The list is what was MEASURED to open a dialog on 2022.3.22f1
    /// (Windows), not a guess at everything that might: Build Settings,
    /// Project Settings, Preferences and File/New Scene open ordinary
    /// EditorWindows and stay allowed. Third-party menus cannot be listed;
    /// those are covered after the fact by <see cref="UapNativeModalProbe"/>
    /// naming the dialog in the timeout text. Pure and Unity-free so the
    /// table is pinned by plain tests.
    /// </summary>
    public static class UapMenuDialogPolicy
    {
        /// <summary>
        /// The refusal text for <paramref name="menuPath"/>, or null when
        /// the menu may run. <paramref name="anyOpenSceneUntitled"/>: some
        /// loaded scene has never been saved (empty path) -- the one case
        /// in which File/Save asks where to put it.
        /// </summary>
        public static string Refusal(string menuPath, bool anyOpenSceneUntitled)
        {
            string key = Normalize(menuPath);
            switch (key)
            {
                case "file/save":
                    return anyOpenSceneUntitled
                        ? Refuse(menuPath, "the open scene is untitled, so Unity asks where to save it",
                            "Call uap_scene_save with 'path' (e.g. \"Assets/Scenes/Main.unity\") instead.")
                        : null;
                case "file/save as":
                    return Refuse(menuPath, "it always asks for a file name",
                        "Call uap_scene_save with 'path' instead (saveAsCopy:true keeps the open scene where it is).");
                case "file/open scene":
                    return Refuse(menuPath, "it asks which scene file to open",
                        "Call uap_scene_open with 'path' (\"Assets/.../Name.unity\"; find it with uap_asset_find) instead.");
                case "file/build and run":
                    return Refuse(menuPath, "it asks for a build location",
                        "Ask the user to start the build, or use a build script with an explicit output path.");
                case "file/exit":
                    return Refuse(menuPath, "it closes the Editor (and asks about unsaved scenes first)",
                        "Ask the user to quit Unity themselves.");
                case "assets/import new asset":
                    return Refuse(menuPath, "it asks which file to import",
                        "Copy the file under Assets/ and run \"Assets/Refresh\" instead.");
                case "assets/import package/custom package":
                    return Refuse(menuPath, "it asks which .unitypackage to import",
                        "Ask the user to import the package themselves.");
                default:
                    return null;
            }
        }

        /// <summary>
        /// Lower-case, '\' to '/', surrounding whitespace and a trailing
        /// "..." dropped -- the agent types "File/Save As" as
        /// often as "File/Save As...", and both resolve to the same item
        /// only when spelled exactly, but both must be refused.
        /// </summary>
        internal static string Normalize(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
            {
                return string.Empty;
            }
            string key = menuPath.Trim().Replace('\\', '/');
            while (key.EndsWith("...", StringComparison.Ordinal))
            {
                key = key.Substring(0, key.Length - 3).TrimEnd();
            }
            return key.ToLowerInvariant();
        }

        private static string Refuse(string menuPath, string why, string instead)
        {
            return "Refused: menu '" + menuPath + "' opens a native modal dialog (" + why + "). A modal dialog"
                + " blocks the Unity main thread until a person closes it -- no uap_* tool could run, or close"
                + " it, in the meantime. Nothing was executed. " + instead;
        }
    }
}
