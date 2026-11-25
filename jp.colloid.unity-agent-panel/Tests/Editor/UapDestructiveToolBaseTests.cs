using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The confirm / dry_run gate itself (design note 2026-09-09-jobs-and-
    /// destructive-confirm section 2), exercised through a pure stub so
    /// the three-way dispatch and its wording are pinned without an
    /// AssetDatabase; plus the pinned set of destructive tools in the
    /// real registry, so a new Undo-proof tool cannot ship outside the
    /// gate unnoticed.
    /// </summary>
    [TestFixture]
    public class UapDestructiveToolBaseTests
    {
        private sealed class StubDestructive : UapDestructiveToolBase
        {
            public int PreviewCalls;
            public int ApplyCalls;

            public override string Name { get { return "stub_destroy"; } }
            public override string Description { get { return "stub"; } }
            public override string Module { get { return "core"; } }
            public override bool Undoable { get { return false; } }

            protected override JsonNode BuildInputSchema()
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("path", JsonNode.NewObject().Set("type", "string")))
                    .Set("required", JsonNode.NewArray().Add("path"))
                    .Set("additionalProperties", false);
            }

            public override string Preview(JsonNode input)
            {
                PreviewCalls++;
                return "Would remove '" + input["path"].AsString("?") + "'.";
            }

            protected override JsonNode Apply(JsonNode input)
            {
                ApplyCalls++;
                return UapToolResultsForTests.Text("Removed.");
            }
        }

        /// <summary>UapToolResults is internal to the Editor assembly; a local copy keeps the stub self-contained.</summary>
        private static class UapToolResultsForTests
        {
            public static JsonNode Text(string text)
            {
                return JsonNode.NewArray().Add(JsonNode.NewObject().Set("type", "text").Set("text", text));
            }
        }

        [Test]
        public void NoConfirm_Refuses_WithPreview_AndNeverApplies()
        {
            var tool = new StubDestructive();
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", "Assets/X.mat"));
            });
            StringAssert.Contains("stub_destroy is destructive and requires confirm:true", ex.Message);
            StringAssert.Contains("Would remove 'Assets/X.mat'", ex.Message);
            StringAssert.Contains("Nothing was changed", ex.Message);
            Assert.AreEqual(1, tool.PreviewCalls);
            Assert.AreEqual(0, tool.ApplyCalls);
        }

        [Test]
        public void ConfirmFalse_IsTheSameAsAbsent()
        {
            var tool = new StubDestructive();
            Assert.Throws<InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", "p").Set("confirm", false));
            });
            Assert.AreEqual(0, tool.ApplyCalls);
        }

        [Test]
        public void ConfirmTrue_Applies_WithoutPreviewing()
        {
            var tool = new StubDestructive();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("path", "p").Set("confirm", true));
            Assert.AreEqual("Removed.", result[0]["text"].AsString());
            Assert.AreEqual(1, tool.ApplyCalls);
            Assert.AreEqual(0, tool.PreviewCalls);
        }

        [Test]
        public void DryRun_Previews_AndWinsOverConfirm()
        {
            var tool = new StubDestructive();
            JsonNode result = tool.Execute(JsonNode.NewObject()
                .Set("path", "p").Set("confirm", true).Set("dry_run", true));
            string text = result[0]["text"].AsString();
            StringAssert.StartsWith("DRY RUN -- nothing was changed.", text);
            StringAssert.Contains("Would remove 'p'", text);
            StringAssert.Contains("confirm:true", text);
            Assert.AreEqual(0, tool.ApplyCalls);
        }

        [Test]
        public void NullInput_IsTreatedAsEmpty_AndRefused()
        {
            var tool = new StubDestructive();
            Assert.Throws<InvalidOperationException>(delegate { tool.Execute(null); });
            Assert.AreEqual(0, tool.ApplyCalls);
        }

        [Test]
        public void InputSchema_CarriesConfirmAndDryRun_NextToTheToolsOwnProperties()
        {
            JsonNode schema = new StubDestructive().InputSchema;
            JsonNode properties = schema["properties"];
            Assert.IsTrue(properties.HasKey("path"));
            Assert.AreEqual("boolean", properties["confirm"]["type"].AsString());
            Assert.AreEqual("boolean", properties["dry_run"]["type"].AsString());
            CollectionAssert.AreEqual(new[] { "path" }, schema["required"].AsStringArray(),
                "confirm must not be schema-required: the refusal IS the preview path");
            Assert.IsFalse(schema["additionalProperties"].AsBool(true));
        }

        [Test]
        public void AugmentSchema_ToleratesASchemaWithoutProperties()
        {
            JsonNode schema = UapDestructiveToolBase.AugmentSchema(JsonNode.NewObject().Set("type", "object"));
            Assert.IsTrue(schema["properties"]["confirm"].IsObject);
        }

        [Test]
        public void ReadOnly_IsAlwaysFalse()
        {
            Assert.IsFalse(new StubDestructive().ReadOnly);
        }

        /// <summary>
        /// The pinned destructive set. Adding an Undo-proof or
        /// shared-asset-writing tool means adding it here AND deriving it
        /// from UapDestructiveToolBase.
        /// </summary>
        [Test]
        public void Registry_DestructiveTools_MatchThePinnedSet()
        {
            // Core's own destructive tools only: since the 2026-09-11
            // core/pro split, uap_prefab_apply_overrides lives in Agent
            // Panel Pro (pinned by Pro's test assembly), and CI runs this
            // suite both with and without Pro installed.
            var expected = new HashSet<string> { "uap_asset_delete" };
            // uap_execute_code (docs/design-notes/2026-09-28-uloop-phase3.md)
            // is a DELIBERATE exception, pinned separately: it implements
            // IUapDestructiveTool directly (not via UapDestructiveToolBase)
            // because whether confirm:true is required depends on a
            // per-call static risk scan, not "this tool is always
            // destructive" -- see its own class doc comment for why the
            // base class's always-refuse-without-confirm dispatch does not
            // fit. Identified here by IUapNeverAutoApproveTool (the marker
            // this deliberate exception also carries) rather than by name,
            // so this test still catches an ALWAYS-destructive tool that
            // copies uap_execute_code's shape to dodge the base-class check
            // without opting into the same never-auto-approved posture.
            var expectedConditional = new HashSet<string> { "uap_execute_code" };
            ToolRegistry registry = ToolRegistry.CreateDefault();
            var actual = new HashSet<string>();
            var actualConditional = new HashSet<string>();
            foreach (IUapTool tool in registry.ListEnabled(AllModules(registry)))
            {
                if (tool.GetType().Assembly != typeof(ToolRegistry).Assembly)
                {
                    continue;
                }
                if (!(tool is IUapDestructiveTool))
                {
                    continue;
                }
                if (tool is IUapNeverAutoApproveTool)
                {
                    actualConditional.Add(tool.Name);
                    Assert.IsTrue(tool.InputSchema["properties"].HasKey("confirm"), tool.Name + " must advertise confirm");
                    continue;
                }
                actual.Add(tool.Name);
                Assert.IsInstanceOf<UapDestructiveToolBase>(tool, tool.Name + " must use the shared gate");
                Assert.IsTrue(tool.InputSchema["properties"].HasKey("confirm"), tool.Name + " must advertise confirm");
                Assert.IsTrue(tool.InputSchema["properties"].HasKey("dry_run"), tool.Name + " must advertise dry_run");
            }
            CollectionAssert.AreEquivalent(expected, actual);
            CollectionAssert.AreEquivalent(expectedConditional, actualConditional);
        }

        private static List<string> AllModules(ToolRegistry registry)
        {
            // "code" added 2026-09-28 (docs/design-notes/2026-09-28-uloop-
            // phase3.md) so Registry_DestructiveTools_MatchThePinnedSet
            // above actually enumerates uap_execute_code instead of
            // silently skipping the module that introduced this test's
            // first IUapDestructiveTool exception.
            return new List<string> { "core", "prefab", "editor", "anim", "ui", "markers", "code" };
        }
    }
}
