using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The menu items uap_editor_execute_menu refuses (design note
    /// 2026-09-17-modal-menu-and-base64-scan section 1): the table itself,
    /// and the tool refusing BEFORE ExecuteMenuItem -- the only place a
    /// refusal is worth anything, since a test that let "File/Save" through
    /// on an untitled scene would hang the runner behind the dialog.
    /// </summary>
    [TestFixture]
    public class UapMenuDialogPolicyTests
    {
        [TestCase("File/Save As...")]
        [TestCase("File/Save As")]
        [TestCase("file/save as ...")]
        [TestCase(" File\\Save As... ")]
        [TestCase("File/Open Scene")]
        [TestCase("File/Build And Run")]
        [TestCase("File/Exit")]
        [TestCase("Assets/Import New Asset...")]
        [TestCase("Assets/Import Package/Custom Package...")]
        public void Refusal_AlwaysModalItems_AreRefusedWhateverTheSceneState(string menuPath)
        {
            Assert.IsNotNull(UapMenuDialogPolicy.Refusal(menuPath, false));
            Assert.IsNotNull(UapMenuDialogPolicy.Refusal(menuPath, true));
        }

        [Test]
        public void Refusal_FileSave_OnlyWhileASceneIsUntitled_AndNamesTheReplacement()
        {
            Assert.IsNull(UapMenuDialogPolicy.Refusal("File/Save", false));

            string refusal = UapMenuDialogPolicy.Refusal("File/Save", true);

            StringAssert.StartsWith("Refused:", refusal);
            StringAssert.Contains("uap_scene_save", refusal);
            StringAssert.Contains("Nothing was executed", refusal);
        }

        // Measured on 2022.3.22f1 to open ordinary EditorWindows (or
        // nothing), not a native dialog: these must keep working.
        [TestCase("File/Build Settings...")]
        [TestCase("Edit/Project Settings...")]
        [TestCase("Edit/Preferences...")]
        [TestCase("File/New Scene")]
        [TestCase("File/Save Project")]
        [TestCase("Assets/Export Package...")]
        [TestCase("Assets/Refresh")]
        [TestCase("GameObject/3D Object/Cube")]
        [TestCase("Tools/Some Vendor/File/Save As...")]
        [TestCase("")]
        [TestCase(null)]
        public void Refusal_NonModalItems_AreAllowed(string menuPath)
        {
            Assert.IsNull(UapMenuDialogPolicy.Refusal(menuPath, true));
        }

        // "File/Save As..." rather than "File/Save": it is refused whatever
        // scene the test runner happens to have open, and creating an
        // untitled scene additively is itself an error under the runner.
        [Test]
        public void Tool_ModalMenu_IsRefusedBeforeItRuns_AndLeavesNoRunRecord()
        {
            UapEditorExecuteMenuTool.ClearRunLog();
            var tool = new UapEditorExecuteMenuTool();

            var ex = Assert.Throws<System.InvalidOperationException>(
                () => tool.Execute(JsonNode.NewObject().Set("menuPath", "File/Save As...")));

            StringAssert.Contains("uap_scene_save", ex.Message);
            JsonNode status = JsonParser.Parse(
                tool.Execute(JsonNode.NewObject().Set("action", "status"))[0]["text"].AsString());
            Assert.AreEqual(0, status["runs"].Count, "a refused menu never started");
        }

        [Test]
        public void Tool_Description_TellsTheAgentAboutTheRefusalAndTheSaveTool()
        {
            string description = new UapEditorExecuteMenuTool().Description;

            StringAssert.Contains("REFUSED", description);
            StringAssert.Contains("uap_scene_save", description);
        }
    }
}
