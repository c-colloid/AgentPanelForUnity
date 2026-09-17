using System;
using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Executes an arbitrary Unity Editor menu command by its exact path
    /// (design section 3c "T2: menu command execution" -- the single most
    /// universal lever for third-party extensions/SDKs that expose no other
    /// stable API surface: nearly every one registers at least one
    /// [MenuItem]). EditorApplication.ExecuteMenuItem reports whether the
    /// path resolved to a real, currently-valid menu item.
    ///
    /// Deliberately NOT scoped to a target and Undoable=false: a menu
    /// command can do absolutely anything a human clicking the same menu
    /// entry could -- open windows, trigger a script compile, save the
    /// project, run a destructive/irreversible action. Callers should
    /// follow up with a query tool (or uap_editor_screenshot) to verify the
    /// actual effect rather than trusting "found":true alone.
    ///
    /// action "status" (design note 2026-09-08-menu-timeout-and-tool-steering
    /// section 2): a menu item runs synchronously on the main thread, so a
    /// long one (a live session's scene build took 9 minutes) outlives the
    /// dispatcher's wait and the caller only ever sees a timeout. The run
    /// log below is written on the main thread BEFORE and AFTER
    /// ExecuteMenuItem, so once the Editor answers again the agent can ask
    /// "did it run, did it finish, how long did it take" instead of grepping
    /// Editor.log for a marker of its own. The log is a plain static: a
    /// domain reload (which a menu can itself trigger) empties it, and
    /// status says so rather than reporting "no runs" as if nothing happened.
    /// </summary>
    public sealed class UapEditorExecuteMenuTool : IUapTool
    {
        public const string ActionRun = "run";
        public const string ActionStatus = "status";

        /// <summary>How many past runs status reports (newest first).</summary>
        public const int RunLogCapacity = 10;

        /// <summary>One ExecuteMenuItem invocation as seen by action "status".</summary>
        public sealed class MenuRun
        {
            public string MenuPath;
            public DateTime StartedUtc;
            /// <summary>Null until ExecuteMenuItem returns (or throws).</summary>
            public DateTime? FinishedUtc;
            public bool Found;
            public string Error;
        }

        private static readonly List<MenuRun> RunLog = new List<MenuRun>();

        /// <summary>Test seam: forget every recorded run (a domain reload does the same).</summary>
        public static void ClearRunLog()
        {
            RunLog.Clear();
        }

        public string Name
        {
            get { return "uap_editor_execute_menu"; }
        }

        public string Description
        {
            get
            {
                return "Executes a Unity Editor menu command by its exact menu path (e.g."
                    + " \"GameObject/3D Object/Cube\") -- can trigger ANYTHING that menu item does,"
                    + " including compiles, saves, windows, or destructive/irreversible actions. Menu items"
                    + " that open a native modal dialog (File/Save on an untitled scene, File/Save As...,"
                    + " File/Open Scene, Assets/Import New Asset..., ...) are REFUSED: such a dialog blocks the"
                    + " Editor until a person closes it; use uap_scene_save / uap_scene_open instead. Not"
                    + " undoable and not scoped to a target; verify the result with a query tool or"
                    + " uap_editor_screenshot afterward. A menu item runs synchronously on the Editor"
                    + " main thread: if this call fails with 'still running on the Unity main thread',"
                    + " the menu DID start and will finish on its own -- do not re-issue it, poll uap_ping"
                    + " until it answers, then call action \"status\" (lists the last runs with"
                    + " found/finished/duration). If it fails with 'was never started', nothing ran.";
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
                        .Set("action", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "\"run\" (default) executes 'menuPath'; \"status\" lists the last"
                                + " runs recorded in this Editor session (menuPath, found, startedUtc,"
                                + " finishedUtc, durationSeconds) -- use it after a 'still running' timeout"
                                + " once uap_ping answers again. The record does not survive a domain reload."))
                        .Set("menuPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Exact menu path, e.g. \"Assets/Refresh\" or \"GameObject/3D Object/Cube\"."
                                + " Required for \"run\"; for \"status\" it filters the listed runs when given.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string action = input["action"].AsString(ActionRun);
            string menuPath = input["menuPath"].AsString(null);
            if (action == ActionStatus)
            {
                return Status(menuPath);
            }
            if (action != ActionRun)
            {
                throw new ArgumentException("Unknown action '" + action + "'; use \"run\" or \"status\".");
            }
            if (string.IsNullOrEmpty(menuPath))
            {
                throw new ArgumentException("'menuPath' is required.");
            }
            // Before the run is recorded: a refused menu never started, and
            // "status" must not list it (design note
            // 2026-09-17-modal-menu-and-base64-scan section 1).
            string refusal = UapMenuDialogPolicy.Refusal(menuPath, AnyOpenSceneUntitled());
            if (refusal != null)
            {
                throw new InvalidOperationException(refusal);
            }

            var run = new MenuRun { MenuPath = menuPath, StartedUtc = DateTime.UtcNow };
            Record(run);
            try
            {
                // ExecuteMenuItem answers false for an unknown path AND
                // logs a Console Error about it. The agent already gets
                // found:false, so when Unity can tell us up front that the
                // item does not exist, skip the call and keep the Console
                // clean (design note 2026-09-17-tool-caused-console-errors.md).
                bool exists;
                run.Found = (!TryMenuItemExists(menuPath, out exists) || exists)
                    && EditorApplication.ExecuteMenuItem(menuPath);
            }
            catch (Exception ex)
            {
                run.Error = ex.GetType().Name + ": " + ex.Message;
                throw;
            }
            finally
            {
                run.FinishedUtc = DateTime.UtcNow;
            }
            JsonNode result = JsonNode.NewObject()
                .Set("menuPath", menuPath)
                .Set("found", run.Found)
                .Set("durationSeconds", Seconds(run));
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static System.Reflection.MethodInfo _menuItemExists;
        private static bool _menuItemExistsResolved;

        /// <summary>
        /// Asks UnityEditor.Menu.MenuItemExists (internal) whether a menu
        /// path resolves. False when the method is not there in this Unity
        /// version or throws -- the caller then simply runs the item as
        /// before.
        /// </summary>
        internal static bool TryMenuItemExists(string menuPath, out bool exists)
        {
            exists = false;
            if (!_menuItemExistsResolved)
            {
                _menuItemExistsResolved = true;
                _menuItemExists = typeof(Menu).GetMethod("MenuItemExists",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
            }
            if (_menuItemExists == null)
            {
                return false;
            }
            try
            {
                exists = (bool)_menuItemExists.Invoke(null, new object[] { menuPath });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A loaded scene that was never saved: the case in which File/Save asks for a file name.</summary>
        private static bool AnyOpenSceneUntitled()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (string.IsNullOrEmpty(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).path))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Record(MenuRun run)
        {
            RunLog.Insert(0, run);
            while (RunLog.Count > RunLogCapacity)
            {
                RunLog.RemoveAt(RunLog.Count - 1);
            }
        }

        private static JsonNode Status(string menuPathFilter)
        {
            JsonNode runs = JsonNode.NewArray();
            for (int i = 0; i < RunLog.Count; i++)
            {
                MenuRun run = RunLog[i];
                if (!string.IsNullOrEmpty(menuPathFilter)
                    && !string.Equals(run.MenuPath, menuPathFilter, StringComparison.Ordinal))
                {
                    continue;
                }
                runs.Add(Describe(run));
            }
            JsonNode body = JsonNode.NewObject()
                .Set("runs", runs)
                .Set("note", RunLog.Count == 0
                    ? "No menu runs are recorded in this Editor session. The record is cleared by a domain"
                      + " reload, so a menu that triggered a recompile (or ran before one) leaves no entry;"
                      + " verify its effect with a query tool or uap_editor_screenshot instead."
                    : "Newest first. A run without finishedUtc did not return before this Editor session's"
                      + " record was read.");
            return UapToolResults.Text(JsonWriter.Write(body));
        }

        private static JsonNode Describe(MenuRun run)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("menuPath", run.MenuPath)
                .Set("state", run.FinishedUtc == null ? "running" : (run.Error != null ? "failed" : "done"))
                .Set("found", run.Found)
                .Set("startedUtc", run.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
            if (run.FinishedUtc != null)
            {
                node.Set("finishedUtc", run.FinishedUtc.Value.ToString("o", CultureInfo.InvariantCulture))
                    .Set("durationSeconds", Seconds(run));
            }
            if (run.Error != null)
            {
                node.Set("error", run.Error);
            }
            return node;
        }

        private static double Seconds(MenuRun run)
        {
            DateTime end = run.FinishedUtc ?? DateTime.UtcNow;
            return Math.Round((end - run.StartedUtc).TotalSeconds, 3);
        }
    }
}
