using System;
using System.Threading;
using System.Threading.Tasks;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Main-thread marshal seam tests (design section 1: "HttpListener
    /// threads must never touch Unity APIs directly ... the HTTP response
    /// waits (with timeout) for the main-thread result"). Simulates a
    /// worker thread calling Execute() while the "main thread" (this test
    /// method) controls exactly when Pump() runs -- proving the tool body
    /// never runs on the calling/worker thread and never runs before Pump()
    /// is called.
    /// </summary>
    [TestFixture]
    public class UapMainThreadDispatcherTests
    {
        [Test]
        public void Execute_DoesNotRunTheTool_UntilPumpIsCalled()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var tool = new StubUapTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));

            // Give the background call a moment to enqueue; it must NOT
            // have executed the tool yet -- nothing has pumped.
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000),
                "Expected the work item to be enqueued before any Pump() call.");
            Assert.AreEqual(0, tool.ExecuteCallCount);
            Assert.IsFalse(call.IsCompleted);

            dispatcher.Pump();

            Assert.IsTrue(call.Wait(2000), "Execute() should unblock once Pump() ran the item.");
            Assert.AreEqual(1, tool.ExecuteCallCount);
            Assert.AreEqual(0, dispatcher.PendingCount);
        }

        [Test]
        public void Execute_RunsTheToolOnThePumpingThread_NotTheCallingThread()
        {
            var dispatcher = new UapMainThreadDispatcher();
            int executingThreadId = -1;
            var tool = new StubUapTool
            {
                ExecuteImpl = delegate(JsonNode input)
                {
                    executingThreadId = Thread.CurrentThread.ManagedThreadId;
                    return JsonNode.NewArray();
                }
            };
            int pumpingThreadId = Thread.CurrentThread.ManagedThreadId;
            int backgroundCallerThreadId = -1;

            Task<JsonNode> call = Task.Run(() =>
            {
                backgroundCallerThreadId = Thread.CurrentThread.ManagedThreadId;
                return dispatcher.Execute(tool, JsonNode.NewObject());
            });
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));

            dispatcher.Pump(); // Runs on THIS (the test/"main") thread.
            call.Wait(2000);

            Assert.AreEqual(pumpingThreadId, executingThreadId,
                "The tool body must run on whichever thread called Pump(), not the background caller's thread.");
            Assert.AreNotEqual(backgroundCallerThreadId, executingThreadId,
                "Execute() was called from a background Task; the tool body must never run on that thread directly.");
        }

        [Test]
        public void Execute_ReturnsTheToolsResult()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var expected = JsonNode.NewArray().Add("marker");
            var tool = new StubUapTool { ExecuteImpl = delegate { return expected; } };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));
            dispatcher.Pump();

            JsonNode result = call.Result;
            Assert.AreEqual("marker", result[0].AsString());
        }

        [Test]
        public void Execute_ToolThrows_ExceptionPropagatesToTheCaller()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var tool = new StubUapTool
            {
                ExecuteImpl = delegate { throw new InvalidOperationException("boom"); }
            };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));
            dispatcher.Pump();

            AggregateException aggregate = Assert.Throws<AggregateException>(delegate
            {
                JsonNode unused = call.Result;
            });
            Assert.IsInstanceOf<InvalidOperationException>(aggregate.InnerException);
            Assert.AreEqual("boom", aggregate.InnerException.Message);
        }

        [Test]
        public void Execute_TimesOut_WhenPumpNeverRuns()
        {
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 50 };
            var tool = new StubUapTool();

            Assert.Throws<TimeoutException>(delegate { dispatcher.Execute(tool, JsonNode.NewObject()); });
        }

        [Test]
        public void Pump_WithNothingQueued_IsANoOp()
        {
            var dispatcher = new UapMainThreadDispatcher();
            Assert.DoesNotThrow(delegate { dispatcher.Pump(); });
        }

        [Test]
        public void Execute_NullTool_Throws()
        {
            var dispatcher = new UapMainThreadDispatcher();
            Assert.Throws<ArgumentNullException>(delegate { dispatcher.Execute(null, JsonNode.NewObject()); });
        }

        [Test]
        public void Pump_RunsMultipleQueuedItems_InOneCall()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var toolA = new StubUapTool();
            var toolB = new StubUapTool();

            Task<JsonNode> callA = Task.Run(() => dispatcher.Execute(toolA, JsonNode.NewObject()));
            Task<JsonNode> callB = Task.Run(() => dispatcher.Execute(toolB, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 2, 2000));

            dispatcher.Pump();

            Assert.IsTrue(Task.WaitAll(new Task[] { callA, callB }, 2000));
            Assert.AreEqual(1, toolA.ExecuteCallCount);
            Assert.AreEqual(1, toolB.ExecuteCallCount);
        }

        /// <summary>
        /// Regression for the "UapOpsServer.Stop() leaves a queued
        /// tools/call blocked for up to 15s" defect: CancelAll must fail
        /// any item CURRENTLY QUEUED (not yet picked up by Pump) right
        /// away, instead of leaving its Execute() caller to hit
        /// DefaultTimeoutMillis with nothing left to ever run it.
        /// </summary>
        [Test]
        public void CancelAll_UnblocksExecute_WithoutWaitingForTheTimeout()
        {
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 15000 };
            var tool = new StubUapTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            dispatcher.CancelAll("UapOps server is stopping.");

            InvalidOperationException thrown = null;
            try
            {
                JsonNode unused = call.Result;
            }
            catch (AggregateException aggregate)
            {
                thrown = aggregate.InnerException as InvalidOperationException;
            }
            stopwatch.Stop();

            Assert.IsNotNull(thrown, "CancelAll must fail the queued item instead of leaving it to time out.");
            Assert.AreEqual("UapOps server is stopping.", thrown.Message);
            Assert.Less(stopwatch.ElapsedMilliseconds, 5000,
                "CancelAll must unblock the caller immediately, not wait out DefaultTimeoutMillis.");
            Assert.AreEqual(0, tool.ExecuteCallCount, "a cancelled item must never actually run the tool.");
            Assert.AreEqual(0, dispatcher.PendingCount);
        }

        [Test]
        public void CancelAll_WithNothingQueued_IsANoOp()
        {
            var dispatcher = new UapMainThreadDispatcher();
            Assert.DoesNotThrow(delegate { dispatcher.CancelAll("reason"); });
        }

        private static bool SpinWaitUntil(Func<bool> condition, int timeoutMillis)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMillis)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(2);
            }
            return condition();
        }
    }
}
