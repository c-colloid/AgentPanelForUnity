using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Per-tool pure metadata seams for the new "code" module (docs/design-
    /// notes/2026-09-28-uloop-phase3.md: uap_execute_code, uLoop's
    /// execute-dynamic-code, native and safer) -- no HTTP/editor-mutation
    /// involved, just Name/Module/Undoable/ReadOnly/InputSchema shape
    /// (mirrors UapCoreToolsMetadataTests/UapEditorToolsMetadataTests).
    /// Assumes ToolRegistry.CreateDefault() registers uap_execute_code
    /// (see this task's `registry_lines` integration snippet) -- until that
    /// line lands, Find(...) returns null and these fail loudly rather than
    /// silently, which is the intended signal that the snippet still needs
    /// applying.
    /// </summary>
    [TestFixture]
    public class UapCodeToolsMetadataTests
    {
        private static ToolRegistry Registry()
        {
            return ToolRegistry.CreateDefault();
        }

        private static IUapTool Tool()
        {
            IUapTool tool = Registry().Find(UapExecuteCodeTool.ToolName);
            Assert.IsNotNull(tool, UapExecuteCodeTool.ToolName + " must be registered (see registry_lines snippet).");
            return tool;
        }

        [Test]
        public void Tool_IsInCodeModule()
        {
            Assert.AreEqual("code", Tool().Module);
        }

        [Test]
        public void Tool_IsNotReadOnly()
        {
            // Never ReadOnly, at any action -- IUapTool.ReadOnly is per
            // tool, not per argument (same rule UapCompileTool/UapPlayModeTool
            // pin for the identical reason).
            Assert.IsFalse(Tool().ReadOnly);
        }

        [Test]
        public void Tool_IsNotStaticallyUndoable()
        {
            // Conservative false: whether a RUN is undoable depends on
            // whether the snippet itself calls Undo.RecordObject, which a
            // static per-tool flag cannot express -- the reply reports it
            // per call instead (see the class doc comment).
            Assert.IsFalse(Tool().Undoable);
        }

        [Test]
        public void Tool_ImplementsIUapPollableTool()
        {
            Assert.IsInstanceOf<IUapPollableTool>(Tool());
        }

        [Test]
        public void Tool_ImplementsIUapDestructiveTool()
        {
            Assert.IsInstanceOf<IUapDestructiveTool>(Tool());
        }

        [Test]
        public void Tool_ImplementsIUapNeverAutoApproveTool()
        {
            Assert.IsInstanceOf<IUapNeverAutoApproveTool>(Tool());
        }

        [Test]
        public void Tool_ImplementsIUapAbandonableTool()
        {
            Assert.IsInstanceOf<IUapAbandonableTool>(Tool());
        }

        [Test]
        public void Tool_DescriptionMentionsPreferTypedTools()
        {
            // Roadmap section 4.2 item 4 (2026-08-02 measurement: agents
            // otherwise route everything through dynamic code).
            StringAssert.Contains("PREFER THE TYPED", Tool().Description);
        }

        [Test]
        public void Tool_InputSchema_DeclaresActionEnum()
        {
            Colloid.AgentPanel.Core.Json.JsonNode schema = Tool().InputSchema;
            Colloid.AgentPanel.Core.Json.JsonNode actionEnum = schema["properties"]["action"]["enum"];
            Assert.AreEqual(3, actionEnum.Count);
        }

        [Test]
        public void Tool_InputSchema_RejectsAdditionalProperties()
        {
            Assert.IsFalse(Tool().InputSchema["additionalProperties"].AsBool(true));
        }
    }
}
