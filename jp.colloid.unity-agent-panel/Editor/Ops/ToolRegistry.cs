using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// In-process catalog of every IUapTool this editor session knows
    /// about, plus the module enable/disable filter tools/list applies
    /// (design section 7.1 -- "tools/list returns only enabled modules").
    /// Pure C#, no HTTP/Unity dependency: registration order is preserved
    /// so tools/list output is stable across calls.
    /// </summary>
    public sealed class ToolRegistry
    {
        private readonly List<IUapTool> _tools = new List<IUapTool>();
        private readonly Dictionary<string, IUapTool> _byName =
            new Dictionary<string, IUapTool>(StringComparer.Ordinal);

        /// <summary>Registers a tool. Throws on a null tool, an empty name, or a name already registered.</summary>
        public void Register(IUapTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException("tool");
            }
            if (string.IsNullOrEmpty(tool.Name))
            {
                throw new ArgumentException("Tool.Name must be non-empty.", "tool");
            }
            if (_byName.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException("A tool named '" + tool.Name + "' is already registered.");
            }
            _byName[tool.Name] = tool;
            _tools.Add(tool);
        }

        /// <summary>Looks up a tool by its exact wire name, or null when absent.</summary>
        public IUapTool Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            IUapTool tool;
            return _byName.TryGetValue(name, out tool) ? tool : null;
        }

        /// <summary>
        /// The module of the tool registered under <paramref name="name"/>,
        /// or null when no such tool is registered. The lookup
        /// <see cref="UapPlaybookCatalog.AvailabilityFor"/> needs to decide
        /// whether a playbook's required tools are reachable.
        /// </summary>
        public string ModuleOf(string name)
        {
            IUapTool tool = Find(name);
            return tool != null ? tool.Module : null;
        }

        /// <summary>Total registered tool count, regardless of module enablement (test/diagnostics seam).</summary>
        public int Count
        {
            get { return _tools.Count; }
        }

        /// <summary>
        /// Tools whose Module is present in <paramref name="enabledModules"/>,
        /// in registration order. A null/empty collection yields an empty
        /// list (nothing enabled), matching the "explicit opt-in" module
        /// model -- there is no implicit "everything" wildcard.
        /// </summary>
        public List<IUapTool> ListEnabled(IEnumerable<string> enabledModules)
        {
            var enabledSet = new HashSet<string>(StringComparer.Ordinal);
            if (enabledModules != null)
            {
                foreach (string module in enabledModules)
                {
                    if (!string.IsNullOrEmpty(module))
                    {
                        enabledSet.Add(module);
                    }
                }
            }
            var result = new List<IUapTool>();
            for (int i = 0; i < _tools.Count; i++)
            {
                if (enabledSet.Contains(_tools[i].Module))
                {
                    result.Add(_tools[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Every registered tool's wire name, in registration order and
        /// regardless of module enablement. Unlike
        /// <see cref="ListEnabled"/> this needs no module list, which is
        /// what a caller checking "does a tool by this name exist" wants
        /// -- uap_profile_validate reads it to reject an Extension Profile
        /// instruction line that names a tool nothing answers to.
        /// </summary>
        public List<string> AllNames()
        {
            var result = new List<string>(_tools.Count);
            for (int i = 0; i < _tools.Count; i++)
            {
                result.Add(_tools[i].Name);
            }
            return result;
        }

        /// <summary>
        /// Builds the standard registry (uap_ping plus the Phase 5a
        /// "core" module tools: scene/component/property/asset/query and
        /// the script validation gate's commit tool, and everything added
        /// since). Every tool is always registered: whether uLoop is in the
        /// project does not change the set (the capability matrix that used
        /// to skip uLoop-covered tools was empty for good and is gone,
        /// docs/design-notes/2026-10-02-uloop-card-final.md).
        /// </summary>
        public static ToolRegistry CreateDefault()
        {
            var registry = new ToolRegistry();
            registry.Register(new UapPingTool());
            // 2026-09-09: result retrieval for a call that outlived its
            // HTTP wait while running (design note
            // 2026-09-09-jobs-and-destructive-confirm section 1). Runs
            // off the main thread, so it answers while a job blocks it.
            registry.Register(new UapJobStatusTool());
            registry.Register(new UapSceneCreateObjectTool());
            registry.Register(new UapSceneDestroyObjectTool());
            registry.Register(new UapSceneReparentTool());
            registry.Register(new UapSceneRenameTool());
            // 2026-09-30: no duplicate tool existed (agents recreated objects by hand).
            registry.Register(new UapSceneDuplicateTool());
            registry.Register(new UapScenePlaceAssetTool());
            // 2026-09-17: there was no way to save a scene, so agents ran
            // "File/Save" through uap_editor_execute_menu -- a native modal
            // dialog on an untitled scene, 288 s of blocked Editor. Design
            // note docs/design-notes/2026-09-17-modal-menu-and-base64-scan.md.
            registry.Register(new UapSceneSaveTool());
            // The other refused File menu item ("File/Open Scene" asks for
            // the file in the same kind of dialog); same note, section 1.6.
            registry.Register(new UapSceneOpenTool());
            registry.Register(new UapComponentAddTool());
            registry.Register(new UapComponentRemoveTool());
            registry.Register(new UapPropertySetTool());
            registry.Register(new UapTransformSetTool());
            // 2026-09-15: the anchor-relative half of the same job.
            // uap_transform_set writes localPosition/rotation/scale on any
            // object, RectTransform included, and never touches the fields
            // a uGUI element is actually laid out by -- which cost up to
            // five uap_property_set calls plus anchor math done by hand.
            // Design note docs/design-notes/2026-09-15-rect-transform-layout-tool.md.
            registry.Register(new UapRectTransformSetTool());
            registry.Register(new UapComponentListTool());
            registry.Register(new UapObjectInspectTool());
            // 2026-09-23 (uLoop Phase 2, docs/design-notes/2026-09-23-uloop-phase2.md
            // section 3.2): uap_game_watch samples a component's field/property on
            // a timer and returns the time series -- the Harmony-free half of
            // uLoop's watch. Same module and addressing as uap_object_inspect.
            registry.Register(new UapGameWatchTool());
            registry.Register(new UapQueryComponentTypesTool());
            registry.Register(new UapQueryHierarchyTool());
            registry.Register(new UapAssetCreateTool());
            registry.Register(new UapAssetDeleteTool());
            registry.Register(new UapAssetFindTool());
            registry.Register(new UapSearchTool());
            registry.Register(new UapScriptsCommitTool());
            // 2026-09-22 (uLoop Phase 1, design note docs/design-notes/
            // 2026-09-22-uloop-phase1.md): uap_compile recompiles the project
            // on demand and reports errors/warnings -- uap_scripts_commit only
            // validates what the agent itself staged -- and uap_help returns a
            // tool's full description and schema by name, which is how an
            // agent on the compact tool list (section 2.8) reads the detail of
            // a tool it found through uap_tools.
            registry.Register(new UapCompileTool());
            registry.Register(new UapHelpTool());
            // Phase 5b stream A -- "editor" module (design section 3c T2 / 1.2).
            // NOTE: the lightmap/Bakery bake tools that used to live in this
            // module moved to Agent Panel Pro (2026-09-11 core/pro split) --
            // they now arrive, when Pro is installed, through the
            // IUapToolProvider discovery pass below.
            registry.Register(new UapEditorScreenshotTool());
            registry.Register(new UapEditorExecuteMenuTool());
            // 2026-09-14: the other half of execute_menu -- a great many
            // third-party menu items take no argument and act on whatever
            // Selection holds (NDMF / Modular Avatar "Manual bake avatar",
            // the VRChat SDK build panel, UniVRM export, Bakery's
            // selected-scope bake), and the panel could previously only
            // read the selection, never set it. Design note
            // docs/design-notes/2026-09-14-selection-set-tool.md.
            registry.Register(new UapEditorSelectTool());
            // 2026-09-21 -- the two capabilities that made a project install
            // uLoop for nothing else (design note docs/design-notes/
            // 2026-09-21-play-mode-and-console-log-tools.md): running the
            // scene, and reading what Unity said about it. The typed
            // versions carry the permission card, the job ledger and the
            // panel's own console plumbing, which `uloop control-play-mode`
            // / `uloop get-logs` over Bash cannot (2026-09-21 note section
            // 9.4 item 4).
            registry.Register(new UapPlayModeTool());
            registry.Register(new UapConsoleLogsTool());
            // 2026-09-21, same note's "cheap four": clearing the Console
            // before a run, and setting the Game View resolution a
            // screenshot is taken at (design note docs/design-notes/
            // 2026-09-21-console-clear-and-game-view-size.md).
            registry.Register(new UapConsoleClearTool());
            registry.Register(new UapGameViewSizeTool());
            // 2026-09-22 -- the last capability that made a project install
            // uLoop for nothing else: driving the running game (design note
            // docs/design-notes/2026-09-22-game-input-tools.md). uGUI
            // pointer events through the EventSystem, and keyboard keys
            // through the Input System; both by reflection, since neither
            // package is guaranteed to be installed.
            registry.Register(new UapGamePointerTool());
            registry.Register(new UapGameKeyTool());
            // 2026-09-22, same Phase 1 note: Input System Mouse.current for game
            // code that reads the device directly (dry_run:true is a Camera.main
            // raycast that needs neither Play Mode nor the Input System), the
            // project facts that decide which of these tools can work, and
            // bringing the Editor to the OS foreground when an unfocused Editor
            // stalls Play Mode.
            registry.Register(new UapGameMouseTool());
            registry.Register(new UapProjectInfoTool());
            registry.Register(new UapEditorFocusTool());
            // 2026-09-23, uLoop Phase 2 (docs/design-notes/2026-09-23-uloop-phase2.md
            // sections 3.1 and 3.4): record the Game/Scene view to a video file in
            // the background, and record a Play Mode keyboard/mouse interaction
            // once to replay it later through the same QueueStateEvent paths as
            // uap_game_key / uap_game_mouse.
            registry.Register(new UapRecordVideoTool());
            registry.Register(new UapInputRecordTool());
            registry.Register(new UapInputReplayTool());
            // 2026-09-07 -- "markers" module (design note section 1.3.3):
            // Scene-view 3D markers the agent points with; default ON.
            registry.Register(new UapMarkerAddTool());
            registry.Register(new UapMarkerListTool());
            registry.Register(new UapMarkerClearTool());
            // 2026-09-17 -- sketch strokes the user draws in the Scene view
            // (design note docs/design-notes/2026-09-17-scene-sketch-strokes.md);
            // read-only listing, same module.
            registry.Register(new UapStrokeListTool());
            // 2026-09-17 -- "web" module: fetch a URL for the agent (images
            // inline, PDFs / files saved, HTML as text), since every agent's
            // own web fetch flattens to text. Runs off the main thread.
            // Design note docs/design-notes/2026-09-17-web-fetch-tool.md.
            registry.Register(new UapWebFetchTool());
            // Same note, section 8: a search for agents without one of their
            // own; needs the user's provider API key (Settings > Web fetch),
            // otherwise it answers with where to set it.
            registry.Register(new UapWebSearchTool());
            // 2026-09-28 -- new "code" module (docs/design-notes/2026-09-28-uloop-phase3.md,
            // roadmap docs/design-notes/2026-09-22-uloop-full-native-roadmap.md section 4):
            // uLoop's execute-dynamic-code, native and safer.
            registry.Register(new UapExecuteCodeTool());
            // Phase 5b streams "prefab"/"anim" and Phase 5c "ui" moved to
            // Agent Panel Pro in full (2026-09-11 core/pro split, design
            // note docs/design-notes/2026-09-11-core-pro-split.md) -- see
            // the IUapToolProvider discovery pass below.
            RegisterProviderTools(registry, DiscoverProviders());
            return registry;
        }

        /// <summary>
        /// Pure overload of the provider-discovery pass: registers every
        /// tool the given providers yield through <see cref="Register"/>
        /// (duplicate names rejected the same way as a built-in collision) -- lets a test verify registration
        /// without touching UnityEditor.TypeCache.
        /// </summary>
        public static void RegisterProviderTools(ToolRegistry registry, IEnumerable<IUapToolProvider> providers)
        {
            if (registry == null || providers == null)
            {
                return;
            }
            foreach (IUapToolProvider provider in providers)
            {
                if (provider == null)
                {
                    continue;
                }
                IEnumerable<IUapTool> tools;
                try
                {
                    tools = provider.CreateTools();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[ToolRegistry] Tool provider '" + provider.GetType().FullName
                        + "' threw while creating tools; skipped: " + ex);
                    continue;
                }
                if (tools == null)
                {
                    continue;
                }
                foreach (IUapTool tool in tools)
                {
                    if (tool == null)
                    {
                        continue;
                    }
                    registry.Register(tool);
                }
            }
        }

        /// <summary>
        /// Every non-abstract IUapToolProvider with a public parameterless
        /// constructor across loaded assemblies, sorted by full type name
        /// for deterministic registration order. A provider that fails to
        /// construct is logged and skipped, never fatal.
        /// </summary>
        private static List<IUapToolProvider> DiscoverProviders()
        {
            var result = new List<IUapToolProvider>();
            List<Type> types;
            try
            {
                // Only PUBLIC classes are providers. Test stubs are private nested
                // classes inside test fixtures (e.g. ToolRegistryTests.
                // ThrowingToolProvider) and live in the same domain as this
                // code under the EditMode runner -- without this filter
                // TypeCache hands them to production CreateDefault, and a
                // stub that throws on purpose logs an error into every
                // unrelated test that touches UapOpsServer.Registry.
                types = TypeCache.GetTypesDerivedFrom<IUapToolProvider>()
                    .Where(t => !t.IsAbstract && !t.IsInterface && (t.IsPublic || t.IsNestedPublic)
                        && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogError("[ToolRegistry] Failed to enumerate IUapToolProvider types via TypeCache: " + ex);
                return result;
            }
            foreach (Type type in types)
            {
                try
                {
                    result.Add((IUapToolProvider)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    Debug.LogError("[ToolRegistry] Failed to instantiate tool provider '" + type.FullName + "'; skipped: " + ex);
                }
            }
            return result;
        }

        /// <summary>
        /// Module ids that have at least one tool registered right now
        /// (design note 2026-09-11-core-pro-split.md "seam 4"): lets
        /// SettingsView tell a module toggle apart from a module whose
        /// tools are provided entirely by an absent add-on package
        /// (Agent Panel Pro not installed).
        /// </summary>
        public bool HasToolsInModule(string module)
        {
            if (string.IsNullOrEmpty(module))
            {
                return false;
            }
            for (int i = 0; i < _tools.Count; i++)
            {
                if (string.Equals(_tools[i].Module, module, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
