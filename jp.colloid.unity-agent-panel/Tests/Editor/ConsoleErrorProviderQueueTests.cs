using System;
using System.Threading;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards the async-apply fix for the self-amplifying error flood
    /// (2026-08-02 design note section 1): OnLogMessage must never touch
    /// _entries/Changed synchronously -- it only enqueues under a lock --
    /// and the drain-on-next-tick (PumpQueuedLogEntries) must coalesce a
    /// whole batch into at most one Changed raise and refuse to recurse
    /// into itself while already applying. Exercises the exact private
    /// methods the real Application.logMessageReceived callback and
    /// EditorApplication.update tick call, via the *ForTests seams.
    /// </summary>
    [TestFixture]
    public class ConsoleErrorProviderQueueTests
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
        public void EnqueueLogMessage_DoesNotTouchEntriesOrRaiseChanged_UntilPumped()
        {
            bool changedRaised = false;
            Action handler = delegate { changedRaised = true; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.EnqueueLogMessageForTests(
                    "NullReferenceException: boom", "at X:1", LogType.Exception);

                Assert.AreEqual(0, ConsoleErrorProvider.Count,
                    "the callback must not apply to _entries synchronously");
                Assert.IsFalse(changedRaised,
                    "the callback must not raise Changed synchronously");
                Assert.AreEqual(1, ConsoleErrorProvider.QueuedLogEntryCountForTests);
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }
        }

        [Test]
        public void PumpQueuedLogEntries_AppliesQueuedEntry_AndRaisesChangedOnce()
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests("boom", "at X:1", LogType.Error);

            int changedCount = 0;
            Action handler = delegate { changedCount++; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            Assert.AreEqual(1, ConsoleErrorProvider.Count);
            Assert.AreEqual(1, changedCount);
            Assert.AreEqual(0, ConsoleErrorProvider.QueuedLogEntryCountForTests,
                "the queue must be fully drained after a pump");
        }

        [Test]
        public void PumpQueuedLogEntries_EmptyQueue_DoesNotRaiseChanged()
        {
            int changedCount = 0;
            Action handler = delegate { changedCount++; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }
            Assert.AreEqual(0, changedCount);
        }

        [Test]
        public void PumpQueuedLogEntries_IgnoresNonErrorLogTypes()
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests("just info", null, LogType.Log);
            ConsoleErrorProvider.EnqueueLogMessageForTests("just a warning", null, LogType.Warning);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            Assert.AreEqual(0, ConsoleErrorProvider.Count);
            Assert.AreEqual(0, ConsoleErrorProvider.QueuedLogEntryCountForTests);
        }

        [Test]
        public void PumpQueuedLogEntries_CoalescesDistinctEntriesIntoOneChangedRaise()
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests("a", null, LogType.Error);
            ConsoleErrorProvider.EnqueueLogMessageForTests("b", null, LogType.Error);
            ConsoleErrorProvider.EnqueueLogMessageForTests("c", null, LogType.Error);

            int changedCount = 0;
            Action handler = delegate { changedCount++; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            Assert.AreEqual(3, ConsoleErrorProvider.Count);
            Assert.AreEqual(1, changedCount,
                "a whole tick's worth of new distinct entries must coalesce"
                + " into a single Changed raise, not one per entry");
        }

        [Test]
        public void PumpQueuedLogEntries_RepeatedMessageInSameBatch_CoalescesToOneEntry()
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests("spammy", null, LogType.Exception);
            ConsoleErrorProvider.EnqueueLogMessageForTests("spammy", null, LogType.Exception);
            ConsoleErrorProvider.EnqueueLogMessageForTests("spammy", null, LogType.Exception);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            Assert.AreEqual(1, ConsoleErrorProvider.Count);
            Assert.AreEqual(3, ConsoleErrorProvider.Snapshot()[0].Occurrences);
        }

        [Test]
        public void PumpQueuedLogEntries_RepeatOnlyBatch_DoesNotRaiseChanged()
        {
            // First occurrence establishes the entry (via its own pump);
            // a second pump whose batch contains ONLY a repeat of an
            // already-known message must not raise Changed again.
            ConsoleErrorProvider.EnqueueLogMessageForTests("known", null, LogType.Error);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();

            ConsoleErrorProvider.EnqueueLogMessageForTests("known", null, LogType.Error);
            int changedCount = 0;
            Action handler = delegate { changedCount++; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            Assert.AreEqual(0, changedCount,
                "a repeat-only batch must bump Occurrences without"
                + " signalling a visible-set change");
            Assert.AreEqual(2, ConsoleErrorProvider.Snapshot()[0].Occurrences);
        }

        [Test]
        public void PumpQueuedLogEntries_ReentrantCallDuringApply_IsNoOpAndDeferred()
        {
            // This is the exact shape of the fixed defect: something that
            // runs as part of applying (a Changed subscriber) triggers
            // another attempt to pump/apply DURING the first apply. It
            // must be a pure no-op, not a recursive re-entry -- the
            // captured stack in the design note is exactly this loop with
            // the old synchronous RaiseChanged() call.
            ConsoleErrorProvider.EnqueueLogMessageForTests("first", null, LogType.Error);

            int reentrantDelta = -1;
            Action handler = delegate
            {
                ConsoleErrorProvider.EnqueueLogMessageForTests("second", null, LogType.Error);
                int before = ConsoleErrorProvider.Count;
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
                reentrantDelta = ConsoleErrorProvider.Count - before;
            };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            Assert.AreEqual(0, reentrantDelta,
                "a reentrant pump call while already applying must apply nothing");
            Assert.AreEqual(1, ConsoleErrorProvider.Count,
                "only the entry from the outer apply must be visible so far");
            Assert.AreEqual(1, ConsoleErrorProvider.QueuedLogEntryCountForTests,
                "the entry enqueued during the reentrant attempt must survive,"
                + " deferred to the next tick");

            // Next tick picks up what the reentrant attempt deferred.
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            Assert.AreEqual(2, ConsoleErrorProvider.Count);
        }

        [Test]
        public void EnqueueLogMessage_ConcurrentFromMultipleThreads_NoEntryLost()
        {
            // Real, dedicated OS threads (not Task.Run/the ThreadPool) so
            // Join() gives an unambiguous "every thread's loop body fully
            // executed" guarantee before checking the queue.
            //
            // threadCount*perThread is deliberately kept UNDER
            // ConsoleErrorProvider's own MaxEntries (100) FIFO cap on the
            // APPLIED _entries list. A first version of this test used 200
            // and saw exactly 100 survive a pump -- that looked exactly
            // like a lost-entry bug but was actually MaxEntries correctly
            // evicting the oldest of 200 genuinely-all-enqueued entries;
            // nothing was lost at the queue level (the primary assertion
            // below, on the UNCAPPED raw queue, is what actually pins
            // "concurrent enqueue never drops an entry" -- the final Count
            // check just confirms the pump drains everything through when
            // the count is within the cap).
            const int threadCount = 8;
            const int perThread = 10;
            var threads = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                threads[t] = new Thread(delegate ()
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        ConsoleErrorProvider.EnqueueLogMessageForTests(
                            "thread-" + threadId + "-error-" + i, null, LogType.Error);
                    }
                });
            }
            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Start();
            }
            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Join();
            }

            Assert.AreEqual(threadCount * perThread,
                ConsoleErrorProvider.QueuedLogEntryCountForTests,
                "the locked queue must not drop entries enqueued concurrently"
                + " from multiple threads, mirroring logMessageReceived"
                + " firing off the main thread");

            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            Assert.AreEqual(threadCount * perThread, ConsoleErrorProvider.Count);
        }

        /// <summary>
        /// HUB-7: an error raised on a BACKGROUND thread must be captured.
        /// The provider used to subscribe Application.logMessageReceived,
        /// which Unity only fires for main-thread logs, so a Task/worker
        /// exception never reached the error chip or the compile digest at
        /// all. This drives a real Debug.LogError from a real background
        /// thread through the live subscription -- the one thing the
        /// *ForTests seams above deliberately cannot prove.
        /// </summary>
        [Test]
        public void BackgroundThreadError_IsCaptured_ThroughTheThreadedSubscription()
        {
            const string message = "UapHub7BackgroundThreadError";
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var worker = new Thread(delegate () { Debug.LogError(message); });
                worker.Start();
                Assert.IsTrue(worker.Join(5000), "the logging thread must finish");

                // The threaded callback enqueues off-thread; the apply is
                // deferred to the main-thread pump, same as production.
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            bool found = false;
            foreach (ConsoleErrorProvider.Entry entry in ConsoleErrorProvider.Snapshot())
            {
                if (entry.Message != null && entry.Message.Contains(message))
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found,
                "a background-thread Debug.LogError must reach the provider"
                + " -- logMessageReceived (non-threaded) silently dropped it");
        }
    }
}
