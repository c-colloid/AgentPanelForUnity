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
        /// the script validation gate's commit tool). Equivalent to
        /// <see cref="CreateDefault(bool)"/> with uloopDetected:false.
        /// </summary>
        public static ToolRegistry CreateDefault()
        {
            return CreateDefault(false);
        }

        /// <summary>
        /// Builds the standard registry, skipping any tool whose name
        /// <see cref="UloopCapabilityMatrix.IsCoveredByUloop"/> reports as
        /// already covered by uLoop when <paramref name="uloopDetected"/>
        /// is true (design section 7.2: "do not even register" -- not just
        /// filter from tools/list). The 5a core set declares no overlap
        /// today, so this is a no-op in production until a later phase
        /// adds a tool uLoop also covers.
        /// </summary>
        public static ToolRegistry CreateDefault(bool uloopDetected)
        {
            var registry = new ToolRegistry();
            RegisterUnlessCovered(registry, new UapPingTool(), uloopDetected);
            // 2026-09-09: result retrieval for a call that outlived its
            // HTTP wait while running (design note
            // 2026-09-09-jobs-and-destructive-confirm section 1). Runs
            // off the main thread, so it answers while a job blocks it.
            RegisterUnlessCovered(registry, new UapJobStatusTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapSceneCreateObjectTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapSceneDestroyObjectTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapSceneReparentTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapSceneRenameTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapScenePlaceAssetTool(), uloopDetected);
            // 2026-09-17: there was no way to save a scene, so agents ran
            // "File/Save" through uap_editor_execute_menu -- a native modal
            // dialog on an untitled scene, 288 s of blocked Editor. Design
            // note docs/design-notes/2026-09-17-modal-menu-and-base64-scan.md.
            RegisterUnlessCovered(registry, new UapSceneSaveTool(), uloopDetected);
            // The other refused File menu item ("File/Open Scene" asks for
            // the file in the same kind of dialog); same note, section 1.6.
            RegisterUnlessCovered(registry, new UapSceneOpenTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapComponentAddTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapComponentRemoveTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapPropertySetTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapTransformSetTool(), uloopDetected);
            // 2026-09-15: the anchor-relative half of the same job.
            // uap_transform_set writes localPosition/rotation/scale on any
            // object, RectTransform included, and never touches the fields
            // a uGUI element is actually laid out by -- which cost up to
            // five uap_property_set calls plus anchor math done by hand.
            // Design note docs/design-notes/2026-09-15-rect-transform-layout-tool.md.
            RegisterUnlessCovered(registry, new UapRectTransformSetTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapComponentListTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapObjectInspectTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapQueryComponentTypesTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapQueryHierarchyTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapAssetCreateTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapAssetDeleteTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapAssetFindTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapSearchTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapScriptsCommitTool(), uloopDetected);
            // Phase 5b stream A -- "editor" module (design section 3c T2 / 1.2).
            // NOTE: the lightmap/Bakery bake tools that used to live in this
            // module moved to Agent Panel Pro (2026-09-11 core/pro split) --
            // they now arrive, when Pro is installed, through the
            // IUapToolProvider discovery pass below.
            RegisterUnlessCovered(registry, new UapEditorScreenshotTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapEditorExecuteMenuTool(), uloopDetected);
            // 2026-09-14: the other half of execute_menu -- a great many
            // third-party menu items take no argument and act on whatever
            // Selection holds (NDMF / Modular Avatar "Manual bake avatar",
            // the VRChat SDK build panel, UniVRM export, Bakery's
            // selected-scope bake), and the panel could previously only
            // read the selection, never set it. Design note
            // docs/design-notes/2026-09-14-selection-set-tool.md.
            RegisterUnlessCovered(registry, new UapEditorSelectTool(), uloopDetected);
            // 2026-09-07 -- "markers" module (design note section 1.3.3):
            // Scene-view 3D markers the agent points with; default ON.
            RegisterUnlessCovered(registry, new UapMarkerAddTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapMarkerListTool(), uloopDetected);
            RegisterUnlessCovered(registry, new UapMarkerClearTool(), uloopDetected);
            // 2026-09-17 -- sketch strokes the user draws in the Scene view
            // (design note docs/design-notes/2026-09-17-scene-sketch-strokes.md);
            // read-only listing, same module.
            RegisterUnlessCovered(registry, new UapStrokeListTool(), uloopDetected);
            // Phase 5b streams "prefab"/"anim" and Phase 5c "ui" moved to
            // Agent Panel Pro in full (2026-09-11 core/pro split, design
            // note docs/design-notes/2026-09-11-core-pro-split.md) -- see
            // the IUapToolProvider discovery pass below.
            RegisterProviderTools(registry, DiscoverProviders(), uloopDetected);
            return registry;
        }

        /// <summary>
        /// Pure overload of the provider-discovery pass: registers every
        /// tool the given providers yield, through the same
        /// RegisterUnlessCovered path (duplicate names rejected the same
        /// way as a built-in collision) -- lets a test verify registration
        /// without touching UnityEditor.TypeCache.
        /// </summary>
        public static void RegisterProviderTools(ToolRegistry registry, IEnumerable<IUapToolProvider> providers, bool uloopDetected)
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
                    RegisterUnlessCovered(registry, tool, uloopDetected);
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

        private static void RegisterUnlessCovered(ToolRegistry registry, IUapTool tool, bool uloopDetected)
        {
            if (uloopDetected && UloopCapabilityMatrix.IsCoveredByUloop(tool.Name))
            {
                return;
            }
            registry.Register(tool);
        }
    }
}
