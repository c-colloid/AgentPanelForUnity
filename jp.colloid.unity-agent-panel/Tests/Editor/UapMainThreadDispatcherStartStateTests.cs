using System;
using System.Threading;
using System.Threading.Tasks;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guards for the timeout OUTCOME words (design note
    /// 2026-09-08-menu-timeout-and-tool-steering section 2). Measured
    /// motivation: a live session got the same "timed out waiting for the
    /// Unity main thread" text both for a scene-build menu that HAD started
    /// (and took 9 minutes) and for one the reload-busy Editor never picked
    /// up; the agent could not tell them apart and polled Editor.log for a
    /// marker that in the second case never came. The dispatcher now
    /// settles the meaning with one Interlocked exchange and says which it
    /// was; the steering prompt quotes these exact phrases back.
    /// </summary>
    [TestFixture]
    public class UapMainThreadDispatcherStartStateTests
    {
        /// <summary>A plain IUapTool whose Execute blocks until the test releases it -- the 9-minute menu.</summary>
        private sealed class BlockingUapTool : IUapTool
        {
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
            public int ExecuteCallCount;
            public int CompletedCount;

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
                Interlocked.Increment(ref CompletedCount);
                return JsonNode.NewObject().Set("ok", true);
            }
        }

        [Test]
        public void SyncTool_NeverPumped_MessageSaysNeverStarted_AndALaterPumpDoesNotRunIt()
        {
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 50 };
            var tool = new StubUapTool();

            TimeoutException ex = Assert.Throws<TimeoutException>(
                delegate { dispatcher.Execute(tool, JsonNode.NewObject()); });

            StringAssert.Contains("was never started", ex.Message);
            StringAssert.Contains("NOTHING ran", ex.Message);
            StringAssert.DoesNotContain("still running", ex.Message);

            // The Editor comes back and pumps: the dropped item must be
            // discarded, never executed behind the caller's back.
            dispatcher.Pump();
            Assert.AreEqual(0, tool.ExecuteCallCount, "a dropped item must never run after its caller was told nothing ran");
            Assert.AreEqual(0, dispatcher.PendingCount);
        }

        [Test]
        public void SyncTool_StartedBeforeTheTimeout_MessageSaysStillRunning_AndItFinishesOnItsOwn()
        {
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 100 };
            var tool = new BlockingUapTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            Assert.IsTrue(SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000));

            // "Main thread" picks the item up and gets stuck inside the tool
            // (Pump blocks for as long as Execute does, exactly like a
            // synchronous menu item blocks the Editor loop).
            Thread pumpThread = new Thread(dispatcher.Pump);
            pumpThread.Start();
            Assert.IsTrue(SpinWaitUntil(() => tool.ExecuteCallCount == 1, 2000), "the pump must have entered the tool");

            AggregateException aggregate = Assert.Throws<AggregateException>(delegate { call.Wait(5000); });
            var timeout = aggregate.InnerException as TimeoutException;
            Assert.IsNotNull(timeout, "expected the waiter to time out while the tool is running");
            StringAssert.Contains("is still running on the Unity main thread", timeout.Message);
            StringAssert.Contains("Do not re-issue it", timeout.Message);
            StringAssert.Contains("uap_ping", timeout.Message);
            StringAssert.DoesNotContain("never started", timeout.Message);

            tool.Gate.Set();
            Assert.IsTrue(pumpThread.Join(5000), "the main thread must be released once the tool returns");
            Assert.AreEqual(1, tool.CompletedCount, "a started tool runs to completion; it was never cancelled");
        }

        [Test]
        public void DescribeTimeout_ThreeOutcomes_HaveDistinctPinnedPhrases()
        {
            string never = UapMainThreadDispatcher.DescribeTimeout("t", 15000, UapMainThreadDispatcher.TimeoutOutcome.NeverStarted);
            string running = UapMainThreadDispatcher.DescribeTimeout("t", 15000, UapMainThreadDispatcher.TimeoutOutcome.StillRunning);
            string abandoned = UapMainThreadDispatcher.DescribeTimeout("t", 15000, UapMainThreadDispatcher.TimeoutOutcome.Abandoned);

            StringAssert.Contains("was never started", never);
            StringAssert.Contains("15 s", never);
            StringAssert.Contains("is still running on the Unity main thread", running);
            StringAssert.Contains("abandoned", abandoned);
            StringAssert.DoesNotContain("still running", abandoned);
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
