using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_editor_execute_menu action "status" (design note
    /// 2026-09-08-menu-timeout-and-tool-steering section 2): after a
    /// long menu item outlives the dispatcher's wait, the agent needs a
    /// main-thread answer to "did it run and finish" that is not grepping
    /// Editor.log. Same [MenuItem] test-double approach as
    /// UapEditorExecuteMenuToolTests.
    /// </summary>
    [TestFixture]
    public class UapEditorExecuteMenuStatusTests
    {
        private const string MenuPath = "Tools/UapOpsTests/NoOpForExecuteMenuStatusTests";

        [MenuItem(MenuPath)]
        private static void NoOp()
        {
        }

        [SetUp]
        public void SetUp()
        {
            UapEditorExecuteMenuTool.ClearRunLog();
        }

        [TearDown]
        public void TearDown()
        {
            UapEditorExecuteMenuTool.ClearRunLog();
        }

        private static string StatusText(JsonNode result)
        {
            return result[0]["text"].AsString(string.Empty);
        }

        [Test]
        public void Status_WithNoRuns_ReportsAnEmptyListAndSaysWhy()
        {
            JsonNode result = new UapEditorExecuteMenuTool().Execute(
                JsonNode.NewObject().Set("action", "status"));

            string text = StatusText(result);
            StringAssert.Contains("\"runs\":[]", text);
            StringAssert.Contains("domain reload", text, "an empty record must explain that a reload clears it");
        }

        [Test]
        public void Run_ThenStatus_ListsTheRunAsDoneWithFoundAndDuration()
        {
            var tool = new UapEditorExecuteMenuTool();
            JsonNode runResult = tool.Execute(JsonNode.NewObject().Set("menuPath", MenuPath));
            StringAssert.Contains("\"found\":true", runResult[0]["text"].AsString(string.Empty));
            StringAssert.Contains("durationSeconds", runResult[0]["text"].AsString(string.Empty));

            string text = StatusText(tool.Execute(JsonNode.NewObject().Set("action", "status")));

            StringAssert.Contains(MenuPath, text);
            StringAssert.Contains("\"state\":\"done\"", text);
            StringAssert.Contains("\"found\":true", text);
            StringAssert.Contains("finishedUtc", text);
        }

        private const string MissingMenuPath = "Tools/UapOpsTests/DoesNotExist12345";

        /// <summary>ExecuteMenuItem logs a Console Error for an unresolved path (production behavior); the runner fails on unexpected Errors, so whitelist it.</summary>
        private static void ExpectMissingMenuError()
        {
            LogAssert.Expect(LogType.Error,
                "ExecuteMenuItem failed because there is no menu named '" + MissingMenuPath + "'");
        }

        [Test]
        public void Status_WithMenuPathFilter_OnlyListsMatchingRuns()
        {
            var tool = new UapEditorExecuteMenuTool();
            tool.Execute(JsonNode.NewObject().Set("menuPath", MenuPath));
            ExpectMissingMenuError();
            tool.Execute(JsonNode.NewObject().Set("menuPath", MissingMenuPath));

            string filtered = StatusText(tool.Execute(
                JsonNode.NewObject().Set("action", "status").Set("menuPath", MenuPath)));

            StringAssert.Contains(MenuPath, filtered);
            StringAssert.DoesNotContain("DoesNotExist12345", filtered);
        }

        [Test]
        public void Run_UnknownMenu_IsRecordedAsDoneButNotFound()
        {
            var tool = new UapEditorExecuteMenuTool();
            ExpectMissingMenuError();
            tool.Execute(JsonNode.NewObject().Set("menuPath", MissingMenuPath));

            string text = StatusText(tool.Execute(JsonNode.NewObject().Set("action", "status")));

            StringAssert.Contains("\"found\":false", text);
            StringAssert.Contains("\"state\":\"done\"", text);
        }

        [Test]
        public void UnknownAction_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new UapEditorExecuteMenuTool().Execute(
                    JsonNode.NewObject().Set("action", "poke").Set("menuPath", MenuPath));
            });
        }
    }
}
