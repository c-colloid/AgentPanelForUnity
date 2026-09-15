using System.Collections.Generic;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pins IUapTool.ReadOnly (2026-08-02 design note section 2 B2) to the
    /// EXACT set the design named -- query_hierarchy, query_component_types,
    /// component_list, object_inspect, asset_find, prefab_get_overrides,
    /// editor_screenshot -- and guards the safety invariant that makes
    /// AgentHub's auto-approve safe by construction: no tool that mutates
    /// anything may ever be flagged ReadOnly. A future tool addition/
    /// mislabel that widens the auto-approved set will fail this fixture,
    /// not silently start skipping permission cards for a write.
    /// </summary>
    [TestFixture]
    public class UapReadOnlyToolMetadataTests
    {
        private static readonly HashSet<string> ExpectedReadOnlyNames = new HashSet<string>
        {
            // Moved here from the mutating set in v0.16.0. It echoes a
            // string and touches nothing; it had been misclassified since
            // the ReadOnly flag was introduced (v0.14.0) after this tool
            // shipped (v0.12.0). Caught by counting the real registry while
            // verifying the new auto-approve levels, which sort tools by
            // exactly these flags -- a tool that changes nothing was
            // landing in the tier for work Undo cannot take back.
            "uap_ping",
            // 2026-09-09: reads the job ledger off the main thread, changes nothing.
            "uap_job_status",
            "uap_query_hierarchy",
            "uap_query_component_types",
            "uap_component_list",
            "uap_object_inspect",
            "uap_asset_find",
            "uap_search",
            "uap_editor_screenshot",
            // 2026-09-07 markers module: list only reads the marker store.
            "uap_marker_list",
        };

        /// <summary>
        /// Tools whose Execute is known (by reading each implementation) to
        /// mutate the project/scene/asset database or write any file --
        /// i.e. every registered tool that is NOT in ExpectedReadOnlyNames.
        /// Listed explicitly (rather than derived as "everything else") so
        /// this test fails loudly, naming the offender, if a new tool is
        /// ever added to the registry without ALSO being added to exactly
        /// one of these two sets.
        /// </summary>
        private static readonly HashSet<string> ExpectedMutatingNames = new HashSet<string>
        {
            "uap_scene_create_object",
            "uap_scene_destroy_object",
            "uap_scene_reparent",
            "uap_scene_rename",
            "uap_scene_place_asset",
            "uap_component_add",
            "uap_component_remove",
            "uap_property_set",
            "uap_transform_set",
            // 2026-09-15: writes the RectTransform layout fields
            // (anchors, pivot, anchoredPosition, sizeDelta, offsets).
            "uap_rect_transform_set",
            "uap_asset_create",
            "uap_asset_delete",
            "uap_scripts_commit",
            "uap_editor_execute_menu",
            // 2026-09-14: writes no project or scene data, but it decides
            // what the NEXT call acts on -- auto-approving it would let an
            // agent silently retarget a destructive uap_editor_execute_menu
            // that the user approved while looking at a different object --
            // and it moves the user's Hierarchy/Inspector focus. Same
            // reasoning as the marker tools below.
            "uap_editor_select",
            // 2026-09-07 markers module: add/clear change what is on the
            // user's screen (not the project) -- still a side effect the
            // permission card must ask about, so never auto-approved.
            "uap_marker_add",
            "uap_marker_clear",
        };

        private static ToolRegistry CreateFullRegistry()
        {
            // uloopDetected:false -- the ReadOnly/mutating classification is
            // metadata on the tool objects themselves, independent of which
            // subset uLoop coverage happens to filter out at registration.
            return ToolRegistry.CreateDefault(false);
        }

        /// <summary>
        /// The registry's tools that this package defines. Since the
        /// 2026-09-11 core/pro split, CreateDefault also registers whatever
        /// an installed add-on's IUapToolProvider yields (Agent Panel Pro's
        /// prefab/anim/ui/lightmap tools), and CI runs this suite both with
        /// and without Pro -- so Core's pinned sets cover Core's tools only,
        /// and Pro's own test assembly pins Pro's.
        /// </summary>
        private static List<IUapTool> CoreTools(ToolRegistry registry)
        {
            List<IUapTool> tools = registry.ListEnabled(AllModules(registry));
            tools.RemoveAll(t => t.GetType().Assembly != typeof(ToolRegistry).Assembly);
            return tools;
        }

        [Test]
        public void EveryRegisteredTool_IsAccountedForInExactlyOneSet()
        {
            ToolRegistry registry = CreateFullRegistry();
            Assert.AreEqual(ExpectedReadOnlyNames.Count + ExpectedMutatingNames.Count, CoreTools(registry).Count,
                "a Core tool was registered that this test's two expected-name sets do not (yet) account for"
                + " -- classify it as read-only or mutating and add it to the matching set above.");
        }

        [Test]
        public void ReadOnlyTools_MatchTheExactPinnedSet()
        {
            ToolRegistry registry = CreateFullRegistry();
            List<IUapTool> readOnlyTools = CoreTools(registry);
            readOnlyTools.RemoveAll(t => !t.ReadOnly);

            var actualNames = new HashSet<string>();
            foreach (IUapTool tool in readOnlyTools)
            {
                actualNames.Add(tool.Name);
            }
            CollectionAssert.AreEquivalent(ExpectedReadOnlyNames, actualNames,
                "the ReadOnly=true tool set drifted from the pinned design list");
        }

        [Test]
        public void MutatingTools_AreAllFlaggedNotReadOnly()
        {
            ToolRegistry registry = CreateFullRegistry();
            foreach (string name in ExpectedMutatingNames)
            {
                IUapTool tool = registry.Find(name);
                Assert.IsNotNull(tool, "expected tool not found in the registry: " + name);
                Assert.IsFalse(tool.ReadOnly,
                    "'" + name + "' mutates the project and must NEVER be flagged ReadOnly"
                    + " -- doing so would auto-approve it without a permission card.");
            }
        }

        [Test]
        public void PinnedReadOnlyTools_AreAllFlaggedReadOnly()
        {
            ToolRegistry registry = CreateFullRegistry();
            foreach (string name in ExpectedReadOnlyNames)
            {
                IUapTool tool = registry.Find(name);
                Assert.IsNotNull(tool, "expected tool not found in the registry: " + name);
                Assert.IsTrue(tool.ReadOnly, "'" + name + "' is expected to be ReadOnly=true");
            }
        }

        /// <summary>Every module any tool in the default registry declares, so ListEnabled returns everything.</summary>
        private static List<string> AllModules(ToolRegistry registry)
        {
            return new List<string> { "core", "prefab", "editor", "anim", "ui", "markers" };
        }
    }
}
