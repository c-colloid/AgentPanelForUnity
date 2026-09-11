using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Per-tool pure metadata seams (task line: "schema shapes, undoable
    /// flags") for every Phase 5a core tool -- no HTTP/editor-mutation
    /// involved, just Name/Module/Undoable/InputSchema shape.
    /// </summary>
    [TestFixture]
    public class UapCoreToolsMetadataTests
    {
        private static ToolRegistry Registry()
        {
            return ToolRegistry.CreateDefault();
        }

        [TestCase("uap_ping", false)]
        [TestCase("uap_job_status", false)]
        [TestCase("uap_scene_create_object", true)]
        [TestCase("uap_scene_destroy_object", true)]
        [TestCase("uap_scene_reparent", true)]
        [TestCase("uap_scene_rename", true)]
        [TestCase("uap_scene_place_asset", true)]
        [TestCase("uap_component_add", true)]
        [TestCase("uap_component_remove", true)]
        [TestCase("uap_property_set", true)]
        [TestCase("uap_transform_set", true)]
        [TestCase("uap_component_list", false)]
        [TestCase("uap_object_inspect", false)]
        [TestCase("uap_query_component_types", false)]
        [TestCase("uap_query_hierarchy", false)]
        [TestCase("uap_asset_create", false)]
        [TestCase("uap_asset_delete", false)]
        [TestCase("uap_asset_find", false)]
        [TestCase("uap_search", false)]
        [TestCase("uap_scripts_commit", false)]
        public void Tool_HasExpectedUndoableFlag(string toolName, bool expectedUndoable)
        {
            IUapTool tool = Registry().Find(toolName);
            Assert.IsNotNull(tool, toolName + " must be registered.");
            Assert.AreEqual(expectedUndoable, tool.Undoable, toolName + ".Undoable");
        }

        [TestCase("uap_job_status")]
        [TestCase("uap_scene_create_object")]
        [TestCase("uap_scene_destroy_object")]
        [TestCase("uap_scene_reparent")]
        [TestCase("uap_scene_rename")]
        [TestCase("uap_scene_place_asset")]
        [TestCase("uap_component_add")]
        [TestCase("uap_component_remove")]
        [TestCase("uap_property_set")]
        [TestCase("uap_transform_set")]
        [TestCase("uap_component_list")]
        [TestCase("uap_object_inspect")]
        [TestCase("uap_query_component_types")]
        [TestCase("uap_query_hierarchy")]
        [TestCase("uap_asset_create")]
        [TestCase("uap_asset_delete")]
        [TestCase("uap_asset_find")]
        [TestCase("uap_search")]
        [TestCase("uap_scripts_commit")]
        public void Tool_IsInCoreModule(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            Assert.IsNotNull(tool);
            Assert.AreEqual("core", tool.Module);
        }

        [TestCase("uap_scene_create_object")]
        [TestCase("uap_scene_destroy_object")]
        [TestCase("uap_scene_reparent")]
        [TestCase("uap_scene_rename")]
        [TestCase("uap_scene_place_asset")]
        [TestCase("uap_component_add")]
        [TestCase("uap_component_remove")]
        [TestCase("uap_property_set")]
        [TestCase("uap_transform_set")]
        [TestCase("uap_component_list")]
        [TestCase("uap_object_inspect")]
        [TestCase("uap_query_component_types")]
        [TestCase("uap_query_hierarchy")]
        [TestCase("uap_asset_create")]
        [TestCase("uap_asset_delete")]
        [TestCase("uap_asset_find")]
        [TestCase("uap_search")]
        [TestCase("uap_scripts_commit")]
        public void Tool_InputSchema_IsAnObjectSchema(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            var schema = tool.InputSchema;
            Assert.IsTrue(schema.IsObject);
            Assert.AreEqual("object", schema["type"].AsString());
            Assert.IsTrue(schema["properties"].IsObject);
        }

        [TestCase("uap_scene_create_object")]
        [TestCase("uap_scene_destroy_object")]
        [TestCase("uap_scene_reparent")]
        [TestCase("uap_scene_rename")]
        [TestCase("uap_scene_place_asset")]
        [TestCase("uap_component_add")]
        [TestCase("uap_component_remove")]
        [TestCase("uap_property_set")]
        [TestCase("uap_transform_set")]
        [TestCase("uap_object_inspect")]
        [TestCase("uap_query_hierarchy")]
        [TestCase("uap_asset_create")]
        [TestCase("uap_asset_delete")]
        [TestCase("uap_asset_find")]
        [TestCase("uap_search")]
        public void Tool_Description_IsNonEmpty(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            Assert.IsFalse(string.IsNullOrEmpty(tool.Description));
        }

        [Test]
        public void ComponentAdd_RequiresPathAndComponentType()
        {
            var required = Registry().Find("uap_component_add").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "path");
            CollectionAssert.Contains(ToStringArray(required), "componentType");
        }

        [Test]
        public void SceneCreateObject_RequiresName()
        {
            var required = Registry().Find("uap_scene_create_object").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "name");
        }

        [Test]
        public void PropertySet_RequiresPropertyPath()
        {
            var required = Registry().Find("uap_property_set").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "propertyPath");
        }

        [Test]
        public void TransformSet_RequiresPath()
        {
            var required = Registry().Find("uap_transform_set").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "path");
        }

        [Test]
        public void AssetCreate_RequiresAssetTypeAndPath()
        {
            var required = Registry().Find("uap_asset_create").InputSchema["required"];
            var arr = ToStringArray(required);
            CollectionAssert.Contains(arr, "assetType");
            CollectionAssert.Contains(arr, "path");
        }

        [Test]
        public void SceneDestroyObject_RequiresPath()
        {
            var required = Registry().Find("uap_scene_destroy_object").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "path");
        }

        [Test]
        public void ComponentRemove_RequiresPathAndComponentType()
        {
            var required = Registry().Find("uap_component_remove").InputSchema["required"];
            var arr = ToStringArray(required);
            CollectionAssert.Contains(arr, "path");
            CollectionAssert.Contains(arr, "componentType");
        }

        [Test]
        public void SceneReparent_RequiresPath()
        {
            var required = Registry().Find("uap_scene_reparent").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "path");
        }

        [Test]
        public void SceneRename_RequiresPathAndNewName()
        {
            var required = Registry().Find("uap_scene_rename").InputSchema["required"];
            var arr = ToStringArray(required);
            CollectionAssert.Contains(arr, "path");
            CollectionAssert.Contains(arr, "newName");
        }

        [Test]
        public void ScenePlaceAsset_RequiresAssetPath()
        {
            var required = Registry().Find("uap_scene_place_asset").InputSchema["required"];
            CollectionAssert.Contains(ToStringArray(required), "assetPath");
        }

        [Test]
        public void QueryHierarchy_HasNoRequiredFields()
        {
            var schema = Registry().Find("uap_query_hierarchy").InputSchema;
            CollectionAssert.IsEmpty(ToStringArray(schema["required"]));
        }

        private static string[] ToStringArray(Colloid.AgentPanel.Core.Json.JsonNode arrayNode)
        {
            return arrayNode.AsStringArray();
        }
    }
}
