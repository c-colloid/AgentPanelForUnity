using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_compile (docs/design-notes/2026-09-22-uloop-phase1.md section
    /// 2.3). Most of the decision table also runs in the license-free smoke
    /// gate via UapCompilePolicy; what needs the Editor here is the same
    /// thing UapPlayModeToolTests exists for -- Execute must DEFER the real
    /// CompilationPipeline.RequestScriptCompilation() call instead of
    /// running it inline, and this fixture proves that by swapping the
    /// scheduler for a recorder (a real test run must never actually
    /// recompile the project or enter Play Mode).
    ///
    /// CompilationPipeline events themselves are driven through the
    /// internal Notify*ForTests seams rather than a real compile, so the
    /// state-machine plumbing (Requested -&gt; Compiling -&gt; Succeeded/
    /// Failed, and the "never adopt a run we didn't see start" guard) is
    /// exercised deterministically in batch mode too.
    /// </summary>
    [TestFixture]
    public class UapCompileToolTests
    {
        private Action<Action> _originalScheduler;
        private List<Action> _scheduled;

        [SetUp]
        public void SetUp()
        {
            _originalScheduler = UapCompileTool.Scheduler;
            _scheduled = new List<Action>();
            UapCompileTool.Scheduler = _scheduled.Add;
            UapCompileResultStore.ResetForTests();
            UapCompileTool.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            UapCompileTool.Scheduler = _originalScheduler;
            UapCompileTool.ResetForTests();
            UapCompileResultStore.ResetForTests();
        }

        // -- Execute: request/status wiring ---------------------------------

        [Test]
        public void Execute_NoAction_IsRequest()
        {
            RequireIdleEditor();

            JsonNode reply = new UapCompileTool().Execute(JsonNode.NewObject());

            Assert.AreEqual(1, _scheduled.Count,
                "an argument-less call must default to request, not status");
            string text = reply[0]["text"].AsString();
            StringAssert.Contains("\"state\":\"requested\"", text);
            StringAssert.Contains("\"requestId\":", text);
        }

        [Test]
        public void Execute_Request_DefersTheCompileAndReturnsARequestId()
        {
            RequireIdleEditor();

            JsonNode reply = new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "request"));

            Assert.AreEqual(1, _scheduled.Count,
                "the compile request must be scheduled after the reply, never run inside Execute");
            string text = reply[0]["text"].AsString();
            StringAssert.Contains("uap_ping", text);
            JsonNode parsed = JsonParser.Parse(text);
            Assert.IsNotEmpty(parsed["requestId"].AsString());
            Assert.AreEqual("requested", parsed["state"].AsString());
        }

        [Test]
        public void Execute_Request_Twice_SecondCallRefusesAlreadyRequested()
        {
            RequireIdleEditor();

            new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "request"));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "request"));
            });
            StringAssert.Contains("already requested", error.Message);
            Assert.AreEqual(1, _scheduled.Count, "the refused second call must not schedule a second compile");
        }

        [Test]
        public void Execute_Status_WithNoPriorRequest_ReportsIdleAndLiveFlags()
        {
            JsonNode reply = new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "status"));

            Assert.AreEqual(0, _scheduled.Count);
            string text = reply[0]["text"].AsString();
            StringAssert.Contains("\"state\":\"idle\"", text);
            StringAssert.Contains("\"scriptCompilationFailed\":", text);
            StringAssert.Contains("\"isCompilingNow\":", text);
        }

        [Test]
        public void Execute_UnknownAction_IsRefusedByName()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(delegate
            {
                new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "compile"));
            });
            StringAssert.Contains("request, status", error.Message);
        }

        [Test]
        public void Execute_Request_Clean_IsCarriedThroughToTheReply()
        {
            RequireIdleEditor();

            JsonNode reply = new UapCompileTool().Execute(
                JsonNode.NewObject().Set("action", "request").Set("clean", true));

            StringAssert.Contains("\"clean\":true", reply[0]["text"].AsString());
        }

        // -- CompilationPipeline plumbing (driven via test seams) -----------

        [Test]
        public void CompileRun_ErrorsPresent_EndsInFailedWithCounts()
        {
            RequireIdleEditor();
            new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "request"));

            UapCompileTool.NotifyCompilationStartedForTests();
            UapCompileTool.NotifyAssemblyCompiledForTests(new[]
            {
                new CompilerMessage { file = "Assets/Foo.cs", line = 3, column = 5, message = "CS1002", type = CompilerMessageType.Error },
                new CompilerMessage { file = "Assets/Foo.cs", line = 9, column = 1, message = "unused var", type = CompilerMessageType.Warning },
            });
            UapCompileTool.NotifyCompilationFinishedForTests();

            JsonNode reply = new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "status"));
            JsonNode parsed = JsonParser.Parse(reply[0]["text"].AsString());
            Assert.AreEqual("failed", parsed["state"].AsString());
            Assert.AreEqual(1, parsed["errorCount"].AsInt());
            Assert.AreEqual(1, parsed["warningCount"].AsInt());
            Assert.AreEqual(2, parsed["messages"].Count);
            Assert.IsFalse(UapCompileTool.SubscribedForTests, "a terminal result must unsubscribe");
        }

        [Test]
        public void CompileRun_NoErrors_EndsInSucceeded()
        {
            RequireIdleEditor();
            new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "request"));

            UapCompileTool.NotifyCompilationStartedForTests();
            UapCompileTool.NotifyAssemblyCompiledForTests(new[]
            {
                new CompilerMessage { file = "Assets/Foo.cs", line = 1, column = 1, message = "info only", type = CompilerMessageType.Info },
            });
            UapCompileTool.NotifyCompilationFinishedForTests();

            JsonNode reply = new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "status"));
            JsonNode parsed = JsonParser.Parse(reply[0]["text"].AsString());
            Assert.AreEqual("succeeded", parsed["state"].AsString());
            Assert.AreEqual(0, parsed["errorCount"].AsInt());
            Assert.AreEqual(1, parsed["infoCount"].AsInt());
        }

        [Test]
        public void CompilationFinished_WithoutHavingSeenItStart_DoesNotPublishAndUnsubscribes()
        {
            RequireIdleEditor();
            new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "request"));

            // NotifyCompilationStartedForTests is skipped on purpose: this
            // simulates a compilationFinished for a run this tool never
            // claimed as its own (e.g. it started before a late re-subscribe).
            UapCompileTool.NotifyCompilationFinishedForTests();
            // Checked before action:status: status deliberately re-arms the
            // subscription while a request is still pending (a domain reload
            // drops it), so asserting afterwards would test that instead.
            Assert.IsFalse(UapCompileTool.SubscribedForTests);

            JsonNode reply = new UapCompileTool().Execute(JsonNode.NewObject().Set("action", "status"));
            JsonNode parsed = JsonParser.Parse(reply[0]["text"].AsString());
            Assert.AreEqual("requested", parsed["state"].AsString(),
                "an unclaimed compilationFinished must not overwrite the pending record");
        }

        // -- UapCompilePolicy: pure decision table --------------------------

        [Test]
        public void DecideRequest_PlayModeActive_Refuses()
        {
            string message;
            UapCompileRequestOutcome outcome = UapCompilePolicy.DecideRequest(
                UapCompileState.Idle, true, false, out message);
            Assert.AreEqual(UapCompileRequestOutcome.RefusePlayMode, outcome);
            StringAssert.Contains("Play Mode", message);
        }

        [Test]
        public void DecideRequest_CompilingLive_Refuses()
        {
            string message;
            UapCompileRequestOutcome outcome = UapCompilePolicy.DecideRequest(
                UapCompileState.Idle, false, true, out message);
            Assert.AreEqual(UapCompileRequestOutcome.RefuseAlreadyCompiling, outcome);
        }

        [Test]
        public void DecideRequest_PersistedRequestedButNotYetLive_Refuses()
        {
            string message;
            UapCompileRequestOutcome outcome = UapCompilePolicy.DecideRequest(
                UapCompileState.Requested, false, false, out message);
            Assert.AreEqual(UapCompileRequestOutcome.RefuseAlreadyRequested, outcome);
        }

        [Test]
        public void DescribeStatusMessage_OrphanedCompilingRecord_DoesNotClaimACompileIsRunning()
        {
            var orphaned = UapCompileResult.Idle();
            orphaned.State = UapCompileState.Compiling;
            StringAssert.Contains("not compiling now", UapCompilePolicy.DescribeStatusMessage(orphaned, false, false));
            Assert.AreEqual("Compiling now.", UapCompilePolicy.DescribeStatusMessage(orphaned, true, false));
        }

        [Test]
        public void DecideRequest_StaleRequestedRecord_SelfHealsToProceed()
        {
            // A request that never produced compilation events (nothing to
            // recompile, or an asmdef setup error) must not block every later
            // request for the rest of the session.
            string message;
            Assert.AreEqual(UapCompileRequestOutcome.RefuseAlreadyRequested, UapCompilePolicy.DecideRequest(
                UapCompileState.Requested, false, false, UapCompilePolicy.StaleRequestSeconds - 1.0, out message));
            Assert.AreEqual(UapCompileRequestOutcome.Proceed, UapCompilePolicy.DecideRequest(
                UapCompileState.Requested, false, false, UapCompilePolicy.StaleRequestSeconds + 1.0, out message));
            Assert.AreEqual(UapCompileRequestOutcome.RefuseAlreadyCompiling, UapCompilePolicy.DecideRequest(
                UapCompileState.Requested, false, true, UapCompilePolicy.StaleRequestSeconds + 1.0, out message),
                "a live compile still wins over staleness");
        }

        [Test]
        public void DecideRequest_OrphanedCompilingStateButNotLive_SelfHealsToProceed()
        {
            // A persisted "Compiling" with compilingLive:false means Unity
            // is not actually running anything right now (e.g. an editor
            // restart orphaned the record) -- must not block forever.
            string message;
            UapCompileRequestOutcome outcome = UapCompilePolicy.DecideRequest(
                UapCompileState.Compiling, false, false, out message);
            Assert.AreEqual(UapCompileRequestOutcome.Proceed, outcome);
        }

        [Test]
        public void DecideRequest_Idle_Succeeded_Failed_AllProceed()
        {
            string message;
            Assert.AreEqual(UapCompileRequestOutcome.Proceed,
                UapCompilePolicy.DecideRequest(UapCompileState.Idle, false, false, out message));
            Assert.AreEqual(UapCompileRequestOutcome.Proceed,
                UapCompilePolicy.DecideRequest(UapCompileState.Succeeded, false, false, out message));
            Assert.AreEqual(UapCompileRequestOutcome.Proceed,
                UapCompilePolicy.DecideRequest(UapCompileState.Failed, false, false, out message));
        }

        [Test]
        public void ParseAction_EmptyIsRequest_UnknownThrows()
        {
            Assert.AreEqual(UapCompileAction.Request, UapCompilePolicy.ParseAction(null));
            Assert.AreEqual(UapCompileAction.Request, UapCompilePolicy.ParseAction(string.Empty));
            Assert.AreEqual(UapCompileAction.Request, UapCompilePolicy.ParseAction("REQUEST"));
            Assert.AreEqual(UapCompileAction.Status, UapCompilePolicy.ParseAction("status"));
            Assert.Throws<ArgumentException>(delegate { UapCompilePolicy.ParseAction("go"); });
        }

        [Test]
        public void StateName_RoundTripsThroughTryParse()
        {
            foreach (UapCompileState state in new[]
            {
                UapCompileState.Idle, UapCompileState.Requested, UapCompileState.Compiling,
                UapCompileState.Succeeded, UapCompileState.Failed,
            })
            {
                UapCompileState parsed;
                Assert.IsTrue(UapCompilePolicy.TryParseStateName(UapCompilePolicy.StateName(state), out parsed));
                Assert.AreEqual(state, parsed);
            }
        }

        [Test]
        public void TryParseStateName_Garbage_FallsBackToIdle()
        {
            UapCompileState parsed;
            Assert.IsFalse(UapCompilePolicy.TryParseStateName("not-a-state", out parsed));
            Assert.AreEqual(UapCompileState.Idle, parsed);
        }

        [Test]
        public void MessageTypeName_RoundTripsThroughTryParse()
        {
            foreach (UapCompileMessageType type in new[]
            {
                UapCompileMessageType.Error, UapCompileMessageType.Warning, UapCompileMessageType.Info,
            })
            {
                UapCompileMessageType parsed;
                Assert.IsTrue(UapCompilePolicy.TryParseMessageTypeName(UapCompilePolicy.MessageTypeName(type), out parsed));
                Assert.AreEqual(type, parsed);
            }
        }

        [Test]
        public void FormatMessageLine_WithAndWithoutFile()
        {
            var withFile = new UapCompileMessage { File = "Assets/Foo.cs", Line = 3, Column = 5, Message = "boom", Type = UapCompileMessageType.Error };
            Assert.AreEqual("error: Assets/Foo.cs(3,5): boom", UapCompilePolicy.FormatMessageLine(withFile));

            var noFile = new UapCompileMessage { Message = "boom", Type = UapCompileMessageType.Warning };
            Assert.AreEqual("warning: boom", UapCompilePolicy.FormatMessageLine(noFile));
        }

        [Test]
        public void Summarize_FormatsCounts()
        {
            Assert.AreEqual("0 error(s), 0 warning(s)", UapCompilePolicy.Summarize(0, 0));
            Assert.AreEqual("2 error(s), 1 warning(s)", UapCompilePolicy.Summarize(2, 1));
        }

        [Test]
        public void Cap_UnderLimit_KeepsEverythingNoOverflow()
        {
            var all = new List<UapCompileMessage> { new UapCompileMessage(), new UapCompileMessage() };
            int overflow;
            List<UapCompileMessage> kept = UapCompilePolicy.Cap(all, 5, out overflow);
            Assert.AreEqual(2, kept.Count);
            Assert.AreEqual(0, overflow);
        }

        [Test]
        public void Cap_OverLimit_TruncatesAndReportsOverflow()
        {
            var all = new List<UapCompileMessage>();
            for (int i = 0; i < 10; i++)
            {
                all.Add(new UapCompileMessage { Line = i });
            }
            int overflow;
            List<UapCompileMessage> kept = UapCompilePolicy.Cap(all, 3, out overflow);
            Assert.AreEqual(3, kept.Count);
            Assert.AreEqual(7, overflow);
            Assert.AreEqual(0, kept[0].Line, "Cap must keep the FIRST entries, not the last");
        }

        [Test]
        public void Cap_NullOrNegativeMax_NeverThrows()
        {
            int overflow;
            Assert.AreEqual(0, UapCompilePolicy.Cap(null, 5, out overflow).Count);
            Assert.AreEqual(0, overflow);
            List<UapCompileMessage> kept = UapCompilePolicy.Cap(new List<UapCompileMessage> { new UapCompileMessage() }, -1, out overflow);
            Assert.AreEqual(0, kept.Count);
            Assert.AreEqual(1, overflow);
        }

        [Test]
        public void FormatUtcTicks_ZeroIsEmpty_PositiveIsIso8601()
        {
            Assert.AreEqual(string.Empty, UapCompilePolicy.FormatUtcTicks(0));
            Assert.AreEqual(string.Empty, UapCompilePolicy.FormatUtcTicks(-5));
            string formatted = UapCompilePolicy.FormatUtcTicks(DateTime.UtcNow.Ticks);
            StringAssert.Contains("T", formatted);
        }

        [Test]
        public void ToJson_FromJson_RoundTrips()
        {
            var result = new UapCompileResult
            {
                State = UapCompileState.Failed,
                RequestId = "abc123",
                Clean = true,
                StartedAtUtcTicks = 100L,
                FinishedAtUtcTicks = 200L,
                ErrorCount = 1,
                WarningCount = 2,
                InfoCount = 3,
                OverflowCount = 4,
                Messages = new List<UapCompileMessage>
                {
                    new UapCompileMessage { File = "A.cs", Line = 1, Column = 2, Message = "m", Type = UapCompileMessageType.Error },
                },
            };

            UapCompileResult round = UapCompilePolicy.FromJson(UapCompilePolicy.ToJson(result));

            Assert.AreEqual(result.State, round.State);
            Assert.AreEqual(result.RequestId, round.RequestId);
            Assert.AreEqual(result.Clean, round.Clean);
            Assert.AreEqual(result.StartedAtUtcTicks, round.StartedAtUtcTicks);
            Assert.AreEqual(result.FinishedAtUtcTicks, round.FinishedAtUtcTicks);
            Assert.AreEqual(result.ErrorCount, round.ErrorCount);
            Assert.AreEqual(result.WarningCount, round.WarningCount);
            Assert.AreEqual(result.InfoCount, round.InfoCount);
            Assert.AreEqual(result.OverflowCount, round.OverflowCount);
            Assert.AreEqual(1, round.Messages.Count);
            Assert.AreEqual("A.cs", round.Messages[0].File);
            Assert.AreEqual(UapCompileMessageType.Error, round.Messages[0].Type);
        }

        [Test]
        public void FromJson_Garbled_DegradesToIdleRatherThanThrowing()
        {
            UapCompileResult result = UapCompilePolicy.FromJson(null);
            Assert.AreEqual(UapCompileState.Idle, result.State);

            result = UapCompilePolicy.FromJson(JsonNode.Of("not an object"));
            Assert.AreEqual(UapCompileState.Idle, result.State);

            result = UapCompilePolicy.FromJson(JsonNode.NewObject().Set("state", "sideways"));
            Assert.AreEqual(UapCompileState.Idle, result.State);
        }

        // -- UapCompileResultStore -------------------------------------------

        [Test]
        public void ResultStore_LoadWithNothingSaved_IsIdle()
        {
            UapCompileResult result = UapCompileResultStore.Load();
            Assert.AreEqual(UapCompileState.Idle, result.State);
            Assert.AreEqual(string.Empty, result.RequestId);
        }

        [Test]
        public void ResultStore_SaveThenLoad_RoundTrips()
        {
            var result = new UapCompileResult
            {
                State = UapCompileState.Succeeded,
                RequestId = "r1",
                Clean = false,
                StartedAtUtcTicks = 1L,
                FinishedAtUtcTicks = 2L,
            };
            UapCompileResultStore.Save(result);

            UapCompileResult loaded = UapCompileResultStore.Load();
            Assert.AreEqual(UapCompileState.Succeeded, loaded.State);
            Assert.AreEqual("r1", loaded.RequestId);
        }

        /// <summary>
        /// The Execute(request) cases read the REAL Editor state; a suite
        /// started while Play Mode or a compile is running would exercise a
        /// different branch (the refusal path) than the one under test, so
        /// say so rather than fail on it -- same idiom as
        /// UapPlayModeToolTests.RequireEditMode.
        /// </summary>
        private static void RequireIdleEditor()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling)
            {
                Assert.Ignore("Needs an idle Editor (not playing, not compiling).");
            }
        }
    }
}
