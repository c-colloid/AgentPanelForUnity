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
            var expected = new HashSet<string> { "uap_asset_delete", "uap_prefab_apply_overrides" };
            ToolRegistry registry = ToolRegistry.CreateDefault(false);
            var actual = new HashSet<string>();
            foreach (IUapTool tool in registry.ListEnabled(AllModules(registry)))
            {
                if (tool is IUapDestructiveTool)
                {
                    actual.Add(tool.Name);
                    Assert.IsInstanceOf<UapDestructiveToolBase>(tool, tool.Name + " must use the shared gate");
                    Assert.IsTrue(tool.InputSchema["properties"].HasKey("confirm"), tool.Name + " must advertise confirm");
                    Assert.IsTrue(tool.InputSchema["properties"].HasKey("dry_run"), tool.Name + " must advertise dry_run");
                }
            }
            CollectionAssert.AreEquivalent(expected, actual);
        }

        private static List<string> AllModules(ToolRegistry registry)
        {
            return new List<string> { "core", "prefab", "editor", "anim", "ui", "markers" };
        }
    }
}
