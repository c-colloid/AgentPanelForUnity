using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Core's own playbooks (design note docs/design-notes/
    /// 2026-10-02-uap-help-playbooks.md), served by <c>uap_help topic:"&lt;id&gt;"</c>.
    /// Every tool and argument name in a body is checked against the real
    /// tool descriptions; a body is a numbered step list with the decision
    /// points and the failure handling that no single tool description can
    /// carry. ASCII only, English.
    ///
    /// The multi-step guidance that used to ride the always-loaded steering
    /// text (AgentHub.UapOpsModuleFamilies) lives here now; safety rules
    /// (execute_code is never auto-approved, prefer typed tools) stay in the
    /// steering text.
    /// </summary>
    public static class UapBuiltInPlaybooks
    {
        public const string VerifyGameplay = "verify-gameplay";
        public const string UiClick = "ui-click";
        public const string ScriptChange = "script-change";
        public const string ReproduceInput = "reproduce-input";
        public const string ExecuteCode = "execute-code";
        public const string CompactMode = "compact-mode";

        /// <summary>A fresh list each call, in the order the index names them.</summary>
        public static List<UapPlaybook> Create()
        {
            return new List<UapPlaybook>
            {
                new UapPlaybook(VerifyGameplay, "Verify a gameplay change in Play Mode",
                    "Confirm a gameplay change works by running the game and driving it yourself, not by asking the user to press Play.",
                    new[] { "uap_play_mode", "uap_console_logs", "uap_editor_screenshot", "uap_game_pointer" },
                    Lines(
                        "Goal: prove the change works by running it.",
                        "1. Call uap_project_info first. Active Input Handling must include the Input System for uap_game_key / uap_game_mouse (uap_game_pointer on uGUI works with either); with Run In Background off the Editor must keep OS focus.",
                        "2. Optional: uap_console_clear, so only this run's output is read.",
                        "3. uap_play_mode action:\"start\". It returns BEFORE the Editor has switched (domain reload): wait for uap_ping to answer, then uap_play_mode action:\"status\" until it reports playing. Refused while compiling: wait, retry once.",
                        "4. Optional evidence: uap_record_video action:\"start\" NOW, after Play Mode runs (a recording begun in Edit Mode is killed by the reload); end it with action:\"stop\".",
                        "5. Drive the game. uGUI: uap_game_pointer target:\"<hierarchy path>\" (playbook ui-click). Keys: uap_game_key action:\"press\" key:\"Space\" (action:\"down\" then \"up\" to hold). Game code reading Mouse.current: uap_game_mouse action:\"click\" x, y (dry_run:true shows what a point hits).",
                        "6. Observe. Values over time: uap_game_watch target, component, members:[...], duration, until. Messages: uap_console_logs types:[\"error\",\"exception\"] with since_id = the previous reply's lastId. Picture: uap_editor_screenshot view:\"game\" return_image:true.",
                        "7. Finish: stop any recording, uap_play_mode action:\"stop\", wait for uap_ping. Never leave the project in Play Mode.",
                        "No effect from input: the Editor lost OS focus -> uap_editor_focus, repeat. The game throws: read uap_console_logs, fix the script (playbook script-change), run once more; never repeat an unchanged failing run.")),

                new UapPlaybook(UiClick, "Find and press uGUI elements",
                    "Press a uGUI button, toggle or slider in the running game by its hierarchy path instead of guessing pixels.",
                    new[] { "uap_editor_screenshot", "uap_game_pointer", "uap_play_mode" },
                    Lines(
                        "Goal: press a uGUI widget (Button, Toggle, Slider, Dropdown, ...) by name.",
                        "1. The game must be in Play Mode (uap_play_mode action:\"start\", then wait for uap_ping) with an EventSystem and an active Canvas in the scene.",
                        "2. List the elements: uap_editor_screenshot ui_only:true returns just the list, no image. Use annotate_ui:true view:\"game\" return_image:true to also see the screen with labelled outlines. Raise maxElements when the reply says truncated.",
                        "3. Each element has label, path (hierarchy path), component type, interactable, visible text and a centre in pixels and 0..1. Choose by text/label and skip interactable:false.",
                        "4. Press it: uap_game_pointer target:\"<path>\" (action:\"click\" is the default). Long press: action:\"long_press\" duration:<seconds>. Drag or slider: action:\"drag\" target plus x, y as the destination, optionally speed and drop_target:\"<path>\".",
                        "5. Check the effect: uap_console_logs (since_id), uap_editor_screenshot view:\"game\" return_image:true, or uap_game_watch on the value that should change.",
                        "Coordinates are the fallback, only for things with no path: x/y in pixels from the TOP-left at the Game View's render size (uap_game_view_size sets it) or normalized:true with 0..1 fractions. Take a fresh list first if the UI changed.",
                        "No effect: a path click ignores raycast blockers, a coordinate click does not. Nothing in the list: the object is not uGUI (3D scene objects take uap_game_mouse instead).")),

                new UapPlaybook(ScriptChange, "Change C# and confirm it compiles",
                    "Edit scripts, get them compiled and read the errors without looping on the same failure.",
                    new[] { "uap_compile", "uap_scripts_commit", "uap_ping" },
                    Lines(
                        "Goal: change C# and know it compiled.",
                        "1. Scripts you write: put each file under UapStaging/ at the project root, mirroring its Assets/ sub-path (UapStaging/Editor/Foo.cs lands at Assets/Editor/Foo.cs), then call uap_scripts_commit. It compiles the staged files and moves them into Assets/ ONLY when they build; otherwise nothing lands and the errors come back verbatim. Do not compile or refresh yourself for staged scripts.",
                        "2. Files changed another way (the user, git, a tool): uap_compile (action:\"request\" is the default; clean:true only when incremental state itself looks wrong). It compiles AFTER the reply because a reload drops the connection: wait for uap_ping to answer, then uap_compile action:\"status\" for the result and the error list. Refused in Play Mode: uap_play_mode action:\"stop\" first.",
                        "3. Errors are file/line/column/message. Fix the FIRST one (later ones are often knock-on), then commit or compile again.",
                        "4. Never loop on the same error. The same message after two attempts means stop: re-read the file and the message, check usings, asmdef references and the Unity version's API, or ask the user.",
                        "5. A clean compile reloads the domain: static state and Play Mode are gone. Wait for uap_ping before the next uap_* call, then check runtime behaviour (playbook verify-gameplay) and uap_console_logs for errors from the new code.")),

                new UapPlaybook(ReproduceInput, "Record and replay keyboard/mouse input",
                    "Capture a Play Mode interaction once and replay it on demand as evidence or a regression check.",
                    new[] { "uap_input_record", "uap_input_replay", "uap_play_mode", "uap_ping" },
                    Lines(
                        "Goal: record an interaction once, replay it whenever needed. Needs Play Mode (not paused) and the Input System package (uap_project_info shows Active Input Handling).",
                        "1. uap_play_mode action:\"start\", wait for uap_ping, uap_play_mode action:\"status\" until playing.",
                        "2. uap_input_record action:\"start\" (optional path inside the project, max_seconds; default file under Library/UnityAgentPanel/InputRecordings/). One recording at a time, none during a replay.",
                        "3. Do the interaction: uap_game_key / uap_game_mouse calls; whatever they inject is captured.",
                        "4. uap_input_record action:\"stop\" returns the file path, event count and duration. status shows progress; a script compile or leaving Play Mode finalizes and writes the file first (stoppedBy assembly-reload / play-mode-exit).",
                        "5. Replay: be in Play Mode again, then uap_input_replay action:\"start\" path:\"<file>\" (speed 0.25..4, default 1). Poll action:\"status\" (appliedEventCount/eventCount) until done; action:\"stop\" aborts and releases every held key.",
                        "6. Judge the result as in verify-gameplay: uap_console_logs, uap_game_watch, uap_editor_screenshot.",
                        "Regression: after a code change (playbook script-change) restart Play Mode and replay the same file. A replay assumes the same starting state (scene, save data, random seed); if the game diverges, record again.",
                        "Failure: a replay refuses while a recording runs and vice versa; an unreadable file is reported with the reason, so re-record.")),

                new UapPlaybook(ExecuteCode, "Run a one-off C# snippet",
                    "Use uap_execute_code only when no typed uap_* tool can do the job; how to shape the snippet and read its reply.",
                    new[] { "uap_execute_code" },
                    Lines(
                        "Goal: run a one-off snippet only when NO typed tool fits. Scene, component, property and asset edits, transforms, screenshots, Console, Play Mode, input and compiling all have typed tools: use them.",
                        "Shape: 'code' is the BODY of object Run() { ... }: statements ending in return <value>; (no return gives null). Default usings: System, System.Linq, System.Collections.Generic, UnityEngine, UnityEditor; more via usings:[\"Some.Namespace\"]. Declare no class or Run method.",
                        "1. uap_execute_code code:\"...\" (action:\"run\" is the default). Compilation does not block the Editor; errors come back as line/column relative to YOUR code.",
                        "2. A risky API (process start, network, file/asset writes or deletes, reflection, endless loop) makes the first call only a PREVIEW (flags, why, code hash); nothing ran. If really intended, repeat the same code with confirm:true: that means you trust the code, the scan is no sandbox.",
                        "3. It is never auto-approved: the permission card shows the whole snippet, so keep it short.",
                        "4. The reply has the returned value, Debug.Log output and an Undo report: Undo exists only if your code calls Undo.RecordObject or similar; tell the user when it does not.",
                        "5. Each run loads an assembly that stays until a domain reload; the reply reports loadedSnippets/loadedBytes. Past the warning call action:\"reset\" with confirm:true (requests a script reload); action:\"status\" shows the cache without running.",
                        "Never: unbounded loops or sleeps (main thread), Lightmapping.Bake, writing .cs/.asmdef files (use UapStaging + uap_scripts_commit). Refused while compiling; Play Mode scene changes are lost on exit.")),

                new UapPlaybook(CompactMode, "Use the compact tool list",
                    "Find a tool with uap_tools, read its schema, and run it with uap_call when this connection lists only the core module.",
                    new[] { "uap_tools", "uap_call" },
                    Lines(
                        "This connection lists only the core module's tools directly; every other enabled module is reached through two gateway tools.",
                        "1. Find: uap_tools query:\"<keyword>\" (case-insensitive substring of name, module or one-line summary) and/or module:\"editor\". The reply has name, module, summary and readOnly, no schemas.",
                        "2. Read the exact arguments: uap_tools names:[\"uap_game_pointer\"], or uap_help names:[...] / name:\"...\". You get the full description, inputSchema, readOnly and undoable. An unknown name or a disabled module is found:false with a reason, not an error. Never guess arguments.",
                        "3. Run: uap_call name:\"uap_game_pointer\" arguments:{\"target\":\"Canvas/Panel/StartButton\"}. 'arguments' is that tool's own input object. It behaves exactly like a direct call: same permission card, module check and result.",
                        "4. Core tools are called directly, not through uap_call. When a playbook names a tool outside core, send that call through uap_call.",
                        "5. Unknown tool or disabled module: uap_tools with no query lists what is enabled. The two gateway tools cannot call each other.")),
            };
        }

        /// <summary>Joins steps with newlines (the body's only structure).</summary>
        private static string Lines(params string[] lines)
        {
            return string.Join("\n", lines);
        }
    }
}
