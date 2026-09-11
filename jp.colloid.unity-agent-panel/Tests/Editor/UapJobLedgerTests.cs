using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure ledger/job tests (design note 2026-09-09-jobs-and-destructive-
    /// confirm section 1): id issuance, registration idempotence, the
    /// "never evict a running job" cap rule, and the state words
    /// uap_job_status reports.
    /// </summary>
    [TestFixture]
    public class UapJobLedgerTests
    {
        [Test]
        public void NewId_IsUniquePerCall_AndPrefixed()
        {
            var ledger = new UapJobLedger();
            string a = ledger.NewId();
            string b = ledger.NewId();
            StringAssert.StartsWith("job-", a);
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void NewId_DiffersAcrossLedgerInstances()
        {
            // A domain reload makes a fresh ledger; an id from before the
            // reload must never resolve to a new job with the same serial.
            string a = new UapJobLedger().NewId();
            string b = new UapJobLedger().NewId();
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void Register_ThenFind_ReturnsTheJob_AndListIsNewestFirst()
        {
            var ledger = new UapJobLedger();
            var first = new UapJob(ledger.NewId(), "t1");
            var second = new UapJob(ledger.NewId(), "t2");
            ledger.Register(first);
            ledger.Register(second);

            Assert.AreSame(first, ledger.Find(first.Id));
            Assert.AreSame(second, ledger.Find(second.Id));
            Assert.IsNull(ledger.Find("job-nope-1"));
            Assert.IsNull(ledger.Find(null));
            var list = ledger.List();
            Assert.AreEqual(2, list.Count);
            Assert.AreSame(second, list[0]);
            Assert.AreSame(first, list[1]);
        }

        [Test]
        public void Register_SameJobTwice_IsANoOp()
        {
            var ledger = new UapJobLedger();
            var job = new UapJob(ledger.NewId(), "t");
            ledger.Register(job);
            ledger.Register(job);
            Assert.AreEqual(1, ledger.Count);
        }

        [Test]
        public void Register_OverCapacity_EvictsOldestCompleted_NeverARunningJob()
        {
            var ledger = new UapJobLedger();
            var running = new UapJob(ledger.NewId(), "running");
            running.MarkStarted();
            ledger.Register(running);
            for (int i = 0; i < UapJobLedger.Capacity; i++)
            {
                var done = new UapJob(ledger.NewId(), "done" + i);
                done.Complete(JsonNode.NewArray(), null);
                ledger.Register(done);
            }

            Assert.AreEqual(UapJobLedger.Capacity, ledger.Count);
            Assert.AreSame(running, ledger.Find(running.Id), "the running job (oldest) must survive eviction");
            Assert.AreSame(running, ledger.List()[ledger.List().Count - 1], "it stays the oldest entry");
        }

        [Test]
        public void Register_AllRunning_GrowsPastCapacityRatherThanForgettingALiveJob()
        {
            var ledger = new UapJobLedger();
            for (int i = 0; i < UapJobLedger.Capacity + 3; i++)
            {
                var job = new UapJob(ledger.NewId(), "t" + i);
                job.MarkStarted();
                ledger.Register(job);
            }
            Assert.AreEqual(UapJobLedger.Capacity + 3, ledger.Count);
        }

        [Test]
        public void Job_StateWords_FollowTheLifecycle()
        {
            var job = new UapJob("job-x-1", "tool");
            Assert.AreEqual(UapJobState.Queued, job.State);
            Assert.IsNull(job.StartedUtc);
            Assert.IsNull(job.Result);

            job.MarkStarted();
            Assert.AreEqual(UapJobState.Running, job.State);
            Assert.IsNotNull(job.StartedUtc);
            Assert.IsFalse(job.WaitForCompletion(0));

            JsonNode content = JsonNode.NewArray().Add("done");
            job.Complete(content, null);
            Assert.AreEqual(UapJobState.Succeeded, job.State);
            Assert.AreSame(content, job.Result);
            Assert.IsNull(job.Error);
            Assert.IsNotNull(job.FinishedUtc);
            Assert.IsTrue(job.WaitForCompletion(0));
        }

        [Test]
        public void Job_CompleteWithError_IsFailed_AndFirstCompletionWins()
        {
            var job = new UapJob("job-x-2", "tool");
            job.MarkStarted();
            job.Complete(null, new InvalidOperationException("boom"));
            Assert.AreEqual(UapJobState.Failed, job.State);
            Assert.AreEqual("boom", job.Error);

            job.Complete(JsonNode.NewArray(), null);
            Assert.AreEqual(UapJobState.Failed, job.State, "a second Complete must not overwrite the first");
        }

        [Test]
        public void Job_CompleteWithoutStart_StampsStartToo()
        {
            var job = new UapJob("job-x-3", "tool");
            job.Complete(JsonNode.NewArray(), null);
            Assert.AreEqual(UapJobState.Succeeded, job.State);
            Assert.IsNotNull(job.StartedUtc);
        }

        [Test]
        public void Job_EmptyId_Throws()
        {
            Assert.Throws<ArgumentException>(delegate { new UapJob(string.Empty, "t"); });
        }

        [Test]
        public void Describe_Succeeded_CarriesResult_Running_CarriesNote()
        {
            var job = new UapJob("job-d-1", "tool");
            job.MarkStarted();
            JsonNode running = UapJobStatusTool.Describe(job, true);
            Assert.AreEqual("running", running["state"].AsString());
            Assert.IsTrue(running.HasKey("note"));
            Assert.IsFalse(running.HasKey("result"));

            job.Complete(JsonNode.NewArray().Add(JsonNode.NewObject().Set("type", "text").Set("text", "hi")), null);
            JsonNode done = UapJobStatusTool.Describe(job, true);
            Assert.AreEqual("succeeded", done["state"].AsString());
            Assert.AreEqual("hi", done["result"][0]["text"].AsString());
            Assert.IsTrue(done.HasKey("finishedUtc"));

            JsonNode listed = UapJobStatusTool.Describe(job, false);
            Assert.IsFalse(listed.HasKey("result"), "the list form omits results to stay small");
        }
    }
}
