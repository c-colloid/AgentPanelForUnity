using System;
using System.Threading;
using System.Threading.Tasks;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>A pollable tool that reports "not done" a fixed number of ticks before completing -- simulates uap_scripts_commit's multi-tick AssemblyBuilder wait without touching the real compiler.</summary>
    internal sealed class StubPollableTool : IUapPollableTool
    {
        public string Name
        {
            get { return "stub_pollable"; }
        }

        public string Description
        {
            get { return "test stub"; }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get { return JsonNode.NewObject().Set("type", "object"); }
        }

        public int TicksUntilDone = 3;
        public int PollCallCount;
        public int ExecuteCallCount;

        private sealed class State
        {
            public int Remaining;
        }

        public JsonNode Execute(JsonNode input)
        {
            ExecuteCallCount++;
            return JsonNode.NewArray().Add("sync");
        }

        public bool Poll(JsonNode input, ref object state, out JsonNode result)
        {
            PollCallCount++;
            var s = state as State;
            if (s == null)
            {
                s = new State { Remaining = TicksUntilDone };
                state = s;
            }
            if (s.Remaining > 0)
            {
                s.Remaining--;
                result = null;
                return false;
            }
            result = JsonNode.NewArray().Add("done");
            return true;
        }
    }

    /// <summary>Extends UapMainThreadDispatcherTests' coverage to the IUapPollableTool path (design section 7.4/8.1: "polls to completion on the update pump WITHOUT blocking the editor").</summary>
    [TestFixture]
    public class UapMainThreadDispatcherPollingTests
    {
        [Test]
        public void PollableTool_DoesNotComplete_UntilEnoughPumpCallsRan()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var tool = new StubPollableTool { TicksUntilDone = 3 };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000);

            dispatcher.Pump();
            Assert.IsFalse(call.IsCompleted, "one Pump() call must not finish a 3-tick poll.");
            dispatcher.Pump();
            Assert.IsFalse(call.IsCompleted);
            dispatcher.Pump();
            Assert.IsFalse(call.IsCompleted);
            dispatcher.Pump();

            Assert.IsTrue(call.Wait(2000));
            Assert.AreEqual("done", call.Result[0].AsString());
            Assert.AreEqual(4, tool.PollCallCount);
            Assert.AreEqual(0, tool.ExecuteCallCount, "the dispatcher must never fall back to Execute() for a pollable tool.");
        }

        [Test]
        public void PollableTool_SinglePumpCall_NeverBlocksWaitingForCompletion()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var tool = new StubPollableTool { TicksUntilDone = 1000 };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            dispatcher.Pump();
            sw.Stop();

            Assert.Less(sw.ElapsedMilliseconds, 200, "Pump() must return promptly, never busy-wait for a whole compile.");
            Assert.IsFalse(call.IsCompleted);
            Assert.AreEqual(1, tool.PollCallCount);
        }

        [Test]
        public void PollableTool_ReEnqueuedItem_DoesNotStarveOtherQueuedWork()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var slow = new StubPollableTool { TicksUntilDone = 5 };
            var fast = new StubUapTool();

            Task<JsonNode> slowCall = Task.Run(() => dispatcher.Execute(slow, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000);
            dispatcher.Pump(); // slow re-enqueues itself once.

            Task<JsonNode> fastCall = Task.Run(() => dispatcher.Execute(fast, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 2, 2000);

            dispatcher.Pump();

            Assert.IsTrue(fastCall.Wait(2000), "a plain tool queued alongside an in-progress poll must still complete.");
            Assert.AreEqual(1, fast.ExecuteCallCount);
            Assert.IsFalse(slowCall.IsCompleted);
        }

        [Test]
        public void PollableTool_ThrowsDuringPoll_PropagatesToCaller()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var tool = new ThrowingPollableTool();

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000);
            dispatcher.Pump();

            AggregateException aggregate = Assert.Throws<AggregateException>(delegate
            {
                JsonNode unused = call.Result;
            });
            Assert.IsInstanceOf<InvalidOperationException>(aggregate.InnerException);
        }

        // ------------------------------------------------------------------
        // SEC-7: a pollable whose Execute waiter timed out is abandoned --
        // its terminal side effect must never run afterwards.
        // ------------------------------------------------------------------

        [Test]
        public void TimedOutPollable_IsAbandoned_TerminalSideEffectNeverRuns()
        {
            var dispatcher = new UapMainThreadDispatcher { DefaultTimeoutMillis = 50 };
            var tool = new SideEffectPollableTool { TicksUntilDone = 2 };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000);

            // Let the waiter time out BEFORE any Pump runs the item.
            AggregateException aggregate = Assert.Throws<AggregateException>(delegate
            {
                JsonNode unused = call.Result;
            });
            Assert.IsInstanceOf<TimeoutException>(aggregate.InnerException);

            // Pump well past the tool's tick budget: the cancelled item
            // must be discarded on first pickup -- no poll, no re-enqueue,
            // and above all no terminal commit.
            for (int i = 0; i < 6; i++)
            {
                dispatcher.Pump();
            }

            Assert.IsFalse(tool.Committed,
                "the terminal side effect ran after the caller was told the call failed");
            Assert.AreEqual(0, tool.PollCallCount, "a cancelled item must not be polled");
            Assert.AreEqual(0, dispatcher.PendingCount, "the cancelled item must be discarded, not requeued");
        }

        [Test]
        public void NonTimedOutPollable_StillCommitsItsTerminalSideEffect()
        {
            var dispatcher = new UapMainThreadDispatcher();
            var tool = new SideEffectPollableTool { TicksUntilDone = 2 };

            Task<JsonNode> call = Task.Run(() => dispatcher.Execute(tool, JsonNode.NewObject()));
            SpinWaitUntil(() => dispatcher.PendingCount == 1, 2000);
            dispatcher.Pump();
            dispatcher.Pump();
            dispatcher.Pump();

            Assert.IsTrue(call.Wait(2000));
            Assert.IsTrue(tool.Committed, "the ordinary completion path is unchanged");
        }

        /// <summary>Pollable whose TERMINAL tick flips a flag -- models
        /// uap_scripts_commit's FinishCommit moving files into Assets/.</summary>
        private sealed class SideEffectPollableTool : IUapPollableTool
        {
            public string Name { get { return "side_effect_pollable"; } }
            public string Description { get { return "test stub"; } }
            public string Module { get { return "core"; } }
            public bool Undoable { get { return false; } }
            public bool ReadOnly { get { return false; } }
            public JsonNode InputSchema { get { return JsonNode.NewObject().Set("type", "object"); } }

            public int TicksUntilDone = 2;
            public int PollCallCount;
            public bool Committed;

            private sealed class State
            {
                public int Remaining;
            }

            public JsonNode Execute(JsonNode input)
            {
                throw new InvalidOperationException("pollable path only");
            }

            public bool Poll(JsonNode input, ref object state, out JsonNode result)
            {
                PollCallCount++;
                var s = state as State;
                if (s == null)
                {
                    s = new State { Remaining = TicksUntilDone };
                    state = s;
                }
                if (s.Remaining > 0)
                {
                    s.Remaining--;
                    result = null;
                    return false;
                }
                Committed = true;
                result = JsonNode.NewArray().Add("committed");
                return true;
            }
        }

        private sealed class ThrowingPollableTool : IUapPollableTool
        {
            public string Name { get { return "throwing_pollable"; } }
            public string Description { get { return "test stub"; } }
            public string Module { get { return "core"; } }
            public bool Undoable { get { return false; } }
            public bool ReadOnly { get { return false; } }
            public JsonNode InputSchema { get { return JsonNode.NewObject().Set("type", "object"); } }

            public JsonNode Execute(JsonNode input)
            {
                throw new InvalidOperationException("boom");
            }

            public bool Poll(JsonNode input, ref object state, out JsonNode result)
            {
                result = null;
                throw new InvalidOperationException("boom");
            }
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
