using System.Collections.Generic;
using System.Threading;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards docs/design-notes/2026-09-17-tool-caused-console-errors.md:
    /// a Console error raised BY a UapOps tool call (measured: Unity's own
    /// "ExecuteMenuItem failed because there is no menu named ..." when an
    /// agent passed uap_editor_execute_menu a path that does not exist)
    /// must not become the user's "ask the agent to fix these" chip -- it
    /// goes back to the agent in the tool result instead -- while the same
    /// error outside a tool call, or from another thread, still does.
    /// </summary>
    [TestFixture]
    public class ConsoleErrorToolScopeTests
    {
        [SetUp]
        public void SetUp()
        {
            ConsoleErrorProvider.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            ConsoleErrorProvider.ResetForTests();
        }

        [Test]
        public void ErrorInsideToolScope_IsReturnedByEnd_AndNeverReachesTheChip()
        {
            ConsoleErrorProvider.BeginToolScope();
            ConsoleErrorProvider.EnqueueLogMessageForTests(
                "ExecuteMenuItem failed because there is no menu named 'GameObject/Duplicate'",
                "UnityEditor.EditorApplication:ExecuteMenuItem (string)", LogType.Error);
            string[] captured = ConsoleErrorProvider.EndToolScope();
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            Assert.AreEqual(1, captured.Length);
            StringAssert.Contains("GameObject/Duplicate", captured[0]);
            Assert.AreEqual(0, ConsoleErrorProvider.QueuedLogEntryCountForTests);
            Assert.AreEqual(0, ConsoleErrorProvider.Count);
        }

        [Test]
        public void ErrorAfterToolScopeClosed_ReachesTheChipAsBefore()
        {
            ConsoleErrorProvider.BeginToolScope();
            ConsoleErrorProvider.EndToolScope();
            ConsoleErrorProvider.EnqueueLogMessageForTests("NullReferenceException: boom", "at X:1", LogType.Exception);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            Assert.AreEqual(1, ConsoleErrorProvider.Count);
        }

        [Test]
        public void ErrorFromAnotherThread_DuringToolScope_StillReachesTheChip()
        {
            ConsoleErrorProvider.BeginToolScope();
            var worker = new Thread(delegate ()
            {
                ConsoleErrorProvider.EnqueueLogMessageForTests("Background importer failed", "at Y:2", LogType.Error);
            });
            worker.Start();
            worker.Join();
            string[] captured = ConsoleErrorProvider.EndToolScope();
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            Assert.AreEqual(0, captured.Length);
            Assert.AreEqual(1, ConsoleErrorProvider.Count);
        }

        [Test]
        public void ToolScope_IsBounded_Deduplicated_AndNestable()
        {
            ConsoleErrorProvider.BeginToolScope();
            ConsoleErrorProvider.BeginToolScope();
            for (int i = 0; i < ConsoleErrorProvider.MaxToolScopeErrors + 3; i++)
            {
                ConsoleErrorProvider.EnqueueLogMessageForTests("error " + i, null, LogType.Error);
                ConsoleErrorProvider.EnqueueLogMessageForTests("error " + i, null, LogType.Error);
            }
            string[] inner = ConsoleErrorProvider.EndToolScope();
            string[] outer = ConsoleErrorProvider.EndToolScope();

            Assert.AreEqual(0, inner.Length, "an inner close must not hand out the lines");
            Assert.AreEqual(ConsoleErrorProvider.MaxToolScopeErrors, outer.Length);
            Assert.AreEqual(0, ConsoleErrorProvider.QueuedLogEntryCountForTests);
        }

        [Test]
        public void UnbalancedEnd_IsHarmless()
        {
            Assert.AreEqual(0, ConsoleErrorProvider.EndToolScope().Length);
            ConsoleErrorProvider.EnqueueLogMessageForTests("still captured for the chip", null, LogType.Error);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            Assert.AreEqual(1, ConsoleErrorProvider.Count);
        }

        [Test]
        public void Dispatcher_AppendsScopedErrorsToTheToolResult_AndKeepsThemOffTheChip()
        {
            var dispatcher = new UapMainThreadDispatcher
            {
                BeginToolLogScope = ConsoleErrorProvider.BeginToolScope,
                EndToolLogScope = ConsoleErrorProvider.EndToolScope
            };
            var tool = new StubUapTool
            {
                ExecuteImpl = delegate (JsonNode input)
                {
                    LogAssert.Expect(LogType.Error, "tool-caused error for ConsoleErrorToolScopeTests");
                    Debug.LogError("tool-caused error for ConsoleErrorToolScopeTests");
                    return UapToolResults.Text("{\"found\":false}");
                }
            };

            JsonNode result = null;
            var caller = new Thread(delegate () { result = dispatcher.Execute(tool, JsonNode.NewObject()); });
            caller.Start();
            Assert.IsTrue(SpinWait.SpinUntil(delegate { return dispatcher.PendingCount == 1; }, 2000));
            dispatcher.Pump();
            Assert.IsTrue(caller.Join(2000));
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("{\"found\":false}", result[0]["text"].AsString());
            StringAssert.StartsWith(UapMainThreadDispatcher.ScopedErrorsHeading, result[1]["text"].AsString());
            StringAssert.Contains("tool-caused error for ConsoleErrorToolScopeTests", result[1]["text"].AsString());
            Assert.AreEqual(0, ConsoleErrorProvider.Count);
        }

        [Test]
        public void AppendScopedErrors_LeavesResultAlone_WhenNothingWasCaptured()
        {
            JsonNode result = UapToolResults.Text("ok");
            Assert.AreSame(result, UapMainThreadDispatcher.AppendScopedErrors(result, null));
            Assert.AreSame(result, UapMainThreadDispatcher.AppendScopedErrors(result, new List<string>()));
            Assert.AreEqual(1, result.Count);
            Assert.IsNull(UapMainThreadDispatcher.AppendScopedErrors(null, new List<string> { "x" }));
        }
    }
}
