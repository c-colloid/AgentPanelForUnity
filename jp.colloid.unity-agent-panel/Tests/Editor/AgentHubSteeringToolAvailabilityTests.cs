using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// A steering sentence that names a tool needs the tool REGISTERED, not
    /// only its module enabled (docs/design-notes/2026-10-02-session-context-
    /// cost.md): the "prefab" module is on by default, but its tools ship in
    /// Agent Panel Pro, so with Core alone the section must not mention
    /// prefab overrides, uap_prefab_apply_overrides, the anim / patching
    /// families or the lightmap clause. With Pro the text is unchanged.
    /// </summary>
    [TestFixture]
    public class AgentHubSteeringToolAvailabilityTests
    {
        private static readonly string[] DefaultModules = { "core", "prefab", "editor", "markers", "web", "code" };
        private static readonly string[] AllModules =
            { "core", "prefab", "editor", "markers", "web", "code", "anim", "patching" };

        /// <summary>Core's own tools only: the real default registry minus everything another assembly (Agent Panel Pro) contributed.</summary>
        private static ToolRegistry CoreOnlyRegistry()
        {
            ToolRegistry full = ToolRegistry.CreateDefault();
            var core = new ToolRegistry();
            foreach (string name in full.AllNames())
            {
                IUapTool tool = full.Find(name);
                if (tool.GetType().Assembly == typeof(ToolRegistry).Assembly)
                {
                    core.Register(tool);
                }
            }
            return core;
        }

        /// <summary>Core plus stand-ins for the Pro tools the steering text names.</summary>
        internal static ToolRegistry CoreAndProRegistry()
        {
            ToolRegistry registry = CoreOnlyRegistry();
            registry.Register(new StubUapTool { Name = "uap_prefab_apply_overrides", Module = "prefab" });
            registry.Register(new StubUapTool { Name = "uap_animator_edit", Module = "anim" });
            registry.Register(new StubUapTool { Name = "uap_hot_reload", Module = "patching" });
            registry.Register(new StubUapTool { Name = "uap_pause_point", Module = "patching" });
            return registry;
        }

        private static string Steering(string[] modules, ToolRegistry registry, bool lightmapTools, bool compact = false)
        {
            return AgentHub.ComposeUapOpsSteeringSection(true, modules, lightmapTools, false, compact,
                UapPlaybookCatalog.CreateBuiltIn(), registry, false);
        }

        [Test]
        public void CoreOnly_DefaultModules_NamesNoProTool()
        {
            string text = Steering(DefaultModules, CoreOnlyRegistry(), false);

            StringAssert.DoesNotContain("prefab", text);
            StringAssert.DoesNotContain("uap_prefab_apply_overrides", text);
            StringAssert.DoesNotContain("lightmap", text);
            StringAssert.DoesNotContain("Bakery", text);
            // The destructive sentence still names Core's own tool, in the singular.
            StringAssert.Contains("uap_asset_delete is destructive", text);
            // The editor family keeps its facts and just loses the lightmap clause.
            StringAssert.Contains("Mouse.current), project facts such as Active Input Handling and installed"
                + " packages (uap_project_info), Scene-view 3D markers", text);
        }

        [Test]
        public void CoreOnly_EvenWithProModulesSwitchedOn_NamesNoProFamily()
        {
            string text = Steering(AllModules, CoreOnlyRegistry(), false);

            StringAssert.DoesNotContain("animation clips", text);
            StringAssert.DoesNotContain("uap_hot_reload", text);
            StringAssert.DoesNotContain("uap_pause_point", text);
            StringAssert.DoesNotContain("prefab overrides", text);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoreOnly_EveryToolNamedInTheText_IsRegistered(bool compact)
        {
            ToolRegistry registry = CoreOnlyRegistry();
            string text = Steering(AllModules, registry, false, compact);

            var allowed = new HashSet<string> { UapToolGateway.ToolsToolName, UapToolGateway.CallToolName };
            foreach (Match match in Regex.Matches(text, "uap_[a-z0-9_]+"))
            {
                Assert.IsTrue(registry.Find(match.Value) != null || allowed.Contains(match.Value),
                    "steering text names '" + match.Value + "', which Core alone does not register");
            }
        }

        [Test]
        public void CoreAndPro_KeepsTheProFamiliesAndTheDestructivePair()
        {
            string text = Steering(AllModules, CoreAndProRegistry(), true);

            StringAssert.Contains("prefab overrides (including revert)", text);
            StringAssert.Contains("animation clips/animator/material", text);
            StringAssert.Contains("hot-reloading changed static method bodies", text);
            StringAssert.Contains("uap_asset_delete and uap_prefab_apply_overrides are destructive", text);
            // The editor family is word-for-word what it was before the gating.
            StringAssert.Contains("uap_game_mouse for game code that reads Mouse.current),"
                + " project facts such as Active Input Handling and installed packages (uap_project_info) and"
                + " asynchronous lightmap bakes (Unity or Bakery)", text);
            StringAssert.Contains("uap_lightmap_bake", text);
        }

        [Test]
        public void ModuleOff_StillWinsOverARegisteredTool()
        {
            // The tool being registered is necessary, not sufficient: a module
            // the user switched off is still never advertised.
            string text = Steering(new[] { "core", "editor" }, CoreAndProRegistry(), false);

            StringAssert.DoesNotContain("prefab overrides", text);
            StringAssert.DoesNotContain("uap_prefab_apply_overrides", text);
            StringAssert.DoesNotContain("animation clips", text);
            StringAssert.DoesNotContain("uap_hot_reload", text);
        }
    }
}
