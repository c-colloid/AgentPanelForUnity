using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Per-tool pure metadata seams for the Phase 5b stream A "editor"
    /// module (uap_editor_screenshot / uap_editor_execute_menu /
    /// uap_editor_select) -- no
    /// HTTP/editor-mutation involved, just Name/Module/Undoable/InputSchema
    /// shape (mirrors UapCoreToolsMetadataTests/UapPrefabToolsMetadataTests).
    ///
    /// uap_lightmap_bake/uap_bakery_bake moved to Agent Panel Pro
    /// (2026-09-11 core/pro split, design note docs/design-notes/
    /// 2026-09-11-core-pro-split.md); their cases moved with them to
    /// UapEditorToolsProMetadataTests in that package's test assembly.
    /// </summary>
    [TestFixture]
    public class UapEditorToolsMetadataTests
    {
        private static ToolRegistry Registry()
        {
            return ToolRegistry.CreateDefault();
        }

        [TestCase("uap_editor_screenshot")]
        [TestCase("uap_editor_execute_menu")]
        [TestCase("uap_editor_select")]
        public void Tool_IsNotUndoable(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            Assert.IsNotNull(tool, toolName + " must be registered.");
            Assert.IsFalse(tool.Undoable, toolName + ".Undoable");
        }

        [TestCase("uap_editor_screenshot")]
        [TestCase("uap_editor_execute_menu")]
        [TestCase("uap_editor_select")]
        public void Tool_IsInEditorModule(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            Assert.IsNotNull(tool);
            Assert.AreEqual("editor", tool.Module);
        }

        [TestCase("uap_editor_screenshot")]
        [TestCase("uap_editor_execute_menu")]
        [TestCase("uap_editor_select")]
        public void Tool_InputSchema_IsAnObjectSchema(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            var schema = tool.InputSchema;
            Assert.IsTrue(schema.IsObject);
            Assert.AreEqual("object", schema["type"].AsString());
            Assert.IsTrue(schema["properties"].IsObject);
        }

        [TestCase("uap_editor_screenshot")]
        [TestCase("uap_editor_execute_menu")]
        [TestCase("uap_editor_select")]
        public void Tool_Description_IsNonEmpty(string toolName)
        {
            IUapTool tool = Registry().Find(toolName);
            Assert.IsFalse(string.IsNullOrEmpty(tool.Description));
        }

        [Test]
        public void EditorExecuteMenu_TakesMenuPathAndAction_MenuPathRequiredOnlyForRun()
        {
            // 2026-09-08: action "status" (design note
            // 2026-09-08-menu-timeout-and-tool-steering section 2) needs no
            // menuPath, so the schema no longer lists it under "required";
            // the "run" default still enforces it at Execute time.
            JsonNode schema = Registry().Find("uap_editor_execute_menu").InputSchema;
            Assert.IsTrue(schema["properties"]["menuPath"].IsObject);
            Assert.IsTrue(schema["properties"]["action"].IsObject);
            Assert.Throws<System.ArgumentException>(delegate
            {
                Registry().Find("uap_editor_execute_menu").Execute(JsonNode.NewObject());
            });
        }

        [Test]
        public void EditorScreenshot_HasNoRequiredFields()
        {
            var schema = Registry().Find("uap_editor_screenshot").InputSchema;
            CollectionAssert.IsEmpty(schema["required"].AsStringArray());
        }
    }
}
