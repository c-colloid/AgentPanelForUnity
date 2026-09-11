using System;
using System.Threading;
using System.Threading.Tasks;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The dispatcher half of the job mechanism (design note
    /// 2026-09-09-jobs-and-destructive-confirm section 1): a call whose
    /// waiter times out while the tool is ALREADY running is registered
    /// as a job, its id is quoted in the timeout message, and its result
    /// is retrievable through uap_job_status -- which runs OFF the main
    /// thread and therefore answers while the job still blocks it. Calls
    /// that finish in time, are never started, or are abandoned pollables
    /// leave no job behind.
    /// </summary>
    [TestFixture]
    public class UapMainThreadDispatcherJobTests
    {
        /// <summary>A plain IUapTool whose Execute blocks until the test releases it -- the 9-minute menu.</summary>
        private sealed class BlockingUapTool : IUapTool
        {
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
            public int ExecuteCallCount;
            public Exception ThrowOnRelease;

            public string Name { get { return "blocking_tool"; } }
            public string Description { get { return "blocks until released"; } }
            public string Module { get { return "core"; } }
            public bool Undoable { get { return false; } }
            public bool ReadOnly { get { return false; } }
            public JsonNode InputSchema { get { return JsonNode.NewObject().Set("type", "object"); } }

            public JsonNode Execute(JsonNode input)
            {
                Interlocked.Increment(ref ExecuteCallCount);
                Gate.Wait(10000);
                if (ThrowOnRelease != null)
                {
                    throw ThrowOnRelease;
                }
                return JsonNode.NewArray().Add(JsonNode.NewObject().Set("type", "text").Set("text", "long result"));
            }
        }

        private sealed class OffThreadStub : IUapOffThreadTool
        {
            public int ExecutingThreadId = -1;
            public string Name { get { return "off_thread_stub"; } }
            public string Description { get { return "runs on the caller"; } }
            public string Module { get { return "core"; } }
            public bool Undoable { get { return false; } }
            public bool ReadOnly { get { return true; } }
            public JsonNode InputSchema { get { return JsonNode.NewObject().Set("type", "object"); } }

            public JsonNode Execute(JsonNode input)
            {
                ExecutingThreadId = Thread.CurrentThread.ManagedThreadId;
                return JsonNode.NewArray().Add("off");
            }
        }

        private static string ExtractJobId(string message)
        {
            const string marker = "kept as job \"";
            int start = message.IndexOf(marker, StringComparison.Ordinal);
            Assert.Greater(start, -1, "the StillRunning message must name the job: " + message);
            start += marker.Length;
            int end = message.IndexOf('"', start);
            return message.Substring(start, end - start);
        }

        [Test]
        public void StillRunningTimeout_RegistersTheJob_AndItsResultArrivesWhenTheToolReturns()
        {
            var ledger = new UapJobLedger();
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 100, Jobs = ledger };
            var tool = new BlockingUapTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));
            var pumpThread = new Thread(dispatcher.Pump);
            pumpThread.Start();
            Assert.IsTrue(SpinWaitUntil(() => tool.ExecuteCallCount == 1, 2000));

            AggregateException aggregate = Assert.Throws<AggregateException>(delegate { call.Wait(5000); });
            var timeout = aggregate.InnerException as TimeoutException;
            Assert.IsNotNull(timeout);
            string jobId = ExtractJobId(timeout.Message);
            StringAssert.Contains("uap_job_status", timeout.Message);

            UapJob job = ledger.Find(jobId);
            Assert.IsNotNull(job, "a StillRunning timeout must register the job under the quoted id");
            Assert.AreEqual(UapJobState.Running, job.State);
            Assert.AreEqual("blocking_tool", job.ToolName);

            // uap_job_status answers while the "main thread" is still stuck in the tool.
            var status = new UapJobStatusTool(ledger);
            JsonNode runningReply = dispatcher.Execute(status, JsonNode.NewObject().Set("job_id", jobId));
            JsonNode runningBody;
            string parseError;
            Assert.IsTrue(JsonParser.TryParse(runningReply[0]["text"].AsString(), out runningBody, out parseError));
            Assert.AreEqual("running", runningBody["state"].AsString());

            tool.Gate.Set();
            Assert.IsTrue(pumpThread.Join(5000));

            JsonNode doneReply = dispatcher.Execute(status, JsonNode.NewObject().Set("job_id", jobId).Set("wait_ms", 1000));
            JsonNode doneBody;
            Assert.IsTrue(JsonParser.TryParse(doneReply[0]["text"].AsString(), out doneBody, out parseError));
            Assert.AreEqual("succeeded", doneBody["state"].AsString());
            Assert.AreEqual("long result", doneBody["result"][0]["text"].AsString());
        }

        [Test]
        public void StillRunningTimeout_ToolThenThrows_JobIsFailedWithTheMessage()
        {
            var ledger = new UapJobLedger();
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 100, Jobs = ledger };
            var tool = new BlockingUapTool { ThrowOnRelease = new InvalidOperationException("menu failed") };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));
            var pumpThread = new Thread(dispatcher.Pump);
            pumpThread.Start();
            Assert.IsTrue(SpinWaitUntil(() => tool.ExecuteCallCount == 1, 2000));
            AggregateException aggregate = Assert.Throws<AggregateException>(delegate { call.Wait(5000); });
            string jobId = ExtractJobId(aggregate.InnerException.Message);

            tool.Gate.Set();
            Assert.IsTrue(pumpThread.Join(5000));

            UapJob job = ledger.Find(jobId);
            Assert.AreEqual(UapJobState.Failed, job.State);
            Assert.AreEqual("menu failed", job.Error);
        }

        [Test]
        public void StillRunningTimeout_WaitMs_BlocksUntilTheJobCompletes()
        {
            var ledger = new UapJobLedger();
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 100, Jobs = ledger };
            var tool = new BlockingUapTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));
            var pumpThread = new Thread(dispatcher.Pump);
            pumpThread.Start();
            Assert.IsTrue(SpinWaitUntil(() => tool.ExecuteCallCount == 1, 2000));
            AggregateException aggregate = Assert.Throws<AggregateException>(delegate { call.Wait(5000); });
            string jobId = ExtractJobId(aggregate.InnerException.Message);

            // Release the tool shortly AFTER the status call starts waiting.
            var releaser = new Thread(delegate ()
            {
                Thread.Sleep(150);
                tool.Gate.Set();
            });
            releaser.Start();
            JsonNode reply = new UapJobStatusTool(ledger).Execute(
                JsonNode.NewObject().Set("job_id", jobId).Set("wait_ms", 3000));
            JsonNode body;
            string parseError;
            Assert.IsTrue(JsonParser.TryParse(reply[0]["text"].AsString(), out body, out parseError));
            Assert.AreEqual("succeeded", body["state"].AsString(), "wait_ms must hold the reply until completion");
            pumpThread.Join(5000);
            releaser.Join(5000);
        }

        [Test]
        public void CallThatFinishesInTime_LeavesNoJob()
        {
            var ledger = new UapJobLedger();
            var dispatcher = new UapMainThreadDispatcher { Jobs = ledger };
            var tool = new StubUapTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));
            dispatcher.Pump();
            Assert.IsTrue(call.Wait(2000));

            Assert.AreEqual(0, ledger.Count);
        }

        [Test]
        public void NeverStartedTimeout_LeavesNoJob_AndNamesNone()
        {
            var ledger = new UapJobLedger();
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 50, Jobs = ledger };

            TimeoutException ex = Assert.Throws<TimeoutException>(
                delegate { dispatcher.Execute(new StubUapTool(), JsonNode.NewObject()); });

            StringAssert.DoesNotContain("job_id", ex.Message);
            Assert.AreEqual(0, ledger.Count);
        }

        [Test]
        public void OffThreadTool_RunsOnTheCallingThread_WithoutAPump()
        {
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 50 };
            var tool = new OffThreadStub();

            JsonNode result = dispatcher.Execute(tool, JsonNode.NewObject());

            Assert.AreEqual("off", result[0].AsString());
            Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, tool.ExecutingThreadId);
            Assert.AreEqual(0, dispatcher.PendingCount, "an off-thread tool must never be queued for the main thread");
        }

        [Test]
        public void JobStatus_UnknownId_Throws_WithTheSessionScopeExplained()
        {
            var status = new UapJobStatusTool(new UapJobLedger());
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                status.Execute(JsonNode.NewObject().Set("job_id", "job-dead-1"));
            });
            StringAssert.Contains("domain reload", ex.Message);
        }

        [Test]
        public void JobStatus_NoJobId_ListsJobsNewestFirst()
        {
            var ledger = new UapJobLedger();
            var older = new UapJob(ledger.NewId(), "a");
            var newer = new UapJob(ledger.NewId(), "b");
            ledger.Register(older);
            ledger.Register(newer);

            JsonNode reply = new UapJobStatusTool(ledger).Execute(JsonNode.NewObject());
            JsonNode body;
            string parseError;
            Assert.IsTrue(JsonParser.TryParse(reply[0]["text"].AsString(), out body, out parseError));
            Assert.AreEqual(2, body["jobs"].Count);
            Assert.AreEqual(newer.Id, body["jobs"][0]["jobId"].AsString());
            Assert.AreEqual("b", body["jobs"][0]["tool"].AsString());
        }

        [Test]
        public void JobStatus_IsRegisteredInCore_ReadOnly_OffThread()
        {
            IUapTool tool = ToolRegistry.CreateDefault().Find(UapJobStatusTool.ToolName);
            Assert.IsNotNull(tool);
            Assert.IsInstanceOf<IUapOffThreadTool>(tool);
            Assert.IsTrue(tool.ReadOnly);
            Assert.IsFalse(tool.Undoable);
            Assert.AreEqual("core", tool.Module);
        }

        [Test]
        public void DescribeTimeout_StillRunningWithJobId_NamesTheJobAndTheStatusTool()
        {
            string text = UapMainThreadDispatcher.DescribeTimeout("t", 15000,
                UapMainThreadDispatcher.TimeoutOutcome.StillRunning, "job-ab12-3");
            StringAssert.Contains("is still running on the Unity main thread", text);
            StringAssert.Contains("job-ab12-3", text);
            StringAssert.Contains("uap_job_status", text);
            StringAssert.Contains("Do not re-issue it", text);
        }

        private static bool SpinWaitUntil(Func<bool> condition, int timeoutMillis)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMillis);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(5);
            }
            return condition();
        }
    }
}
