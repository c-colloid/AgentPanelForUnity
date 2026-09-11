using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real uap_editor_execute_menu tests against a purpose-built [MenuItem]
    /// test double (rather than a real editor menu command) so "found":true
    /// is verified by an actual side effect without ever risking a real,
    /// possibly slow/flaky/destructive menu action running under the
    /// EditMode test runner.
    /// </summary>
    [TestFixture]
    public class UapEditorExecuteMenuToolTests
    {
        private const string MenuPath = "Tools/UapOpsTests/NoOpForExecuteMenuToolTests";

        internal static int InvocationCount;

        [MenuItem(MenuPath)]
        private static void NoOp()
        {
            InvocationCount++;
        }

        [SetUp]
        public void SetUp()
        {
            InvocationCount = 0;
        }

        [Test]
        public void Execute_KnownMenuPath_InvokesItAndReportsFound()
        {
            JsonNode result = new UapEditorExecuteMenuTool().Execute(
                JsonNode.NewObject().Set("menuPath", MenuPath));

            Assert.AreEqual(1, InvocationCount);
            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.IsTrue(parsed["found"].AsBool());
            Assert.AreEqual(MenuPath, parsed["menuPath"].AsString());
        }

        [Test]
        public void Execute_UnknownMenuPath_ReportsNotFound_DoesNotThrow()
        {
            // EditorApplication.ExecuteMenuItem itself logs a Unity Console
            // Error for an unresolved path (production behavior, not a bug)
            // -- the EditMode test runner otherwise fails the test on any
            // unexpected Error-level log, so the expected one must be
            // whitelisted here.
            LogAssert.Expect(LogType.Error,
                "ExecuteMenuItem failed because there is no menu named 'Tools/UapOpsTests/DoesNotExist12345'");

            JsonNode result = new UapEditorExecuteMenuTool().Execute(
                JsonNode.NewObject().Set("menuPath", "Tools/UapOpsTests/DoesNotExist12345"));

            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.IsFalse(parsed["found"].AsBool());
        }

        [Test]
        public void Execute_MissingMenuPath_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                new UapEditorExecuteMenuTool().Execute(JsonNode.NewObject());
            });
        }
    }
}
