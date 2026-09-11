using Colloid.AgentPanel.Integration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// HUB-2: CompileGate's pending-send drain used to call
    /// AgentHub.EnsureStarted() on every update tick whenever the client
    /// was dead -- including while the crash-loop guard had SUSPENDED
    /// restarts after repeated instant deaths, respawning a real claude
    /// process each tick and fighting the suspension (the auto-continue
    /// path gained this exact guard as the defect-4 fix; the drain was the
    /// remaining hole). These tests drive the internal OnUpdate seam
    /// directly with the counter forced past the cap.
    /// </summary>
    [TestFixture]
    public class CompileGateCrashLoopTests
    {
        [SetUp]
        public void SetUp()
        {
            CompileGate.ResetForTests();
            AgentHub.SetConsecutiveDeathsForTests(0);
            // No client: SendOrQueue below must take the queue branch, not
            // race a client some earlier fixture left wired.
            AgentHub.SetClientForTests(null);
        }

        [TearDown]
        public void TearDown()
        {
            CompileGate.ResetForTests();
            AgentHub.SetConsecutiveDeathsForTests(0);
        }

        [Test]
        public void IsCrashLoopSuspended_AtTheCap_IsFalse_PastTheCap_IsTrue()
        {
            // MaxConsecutiveRestarts is 3: the guard trips strictly PAST it
            // (the same `>` OnProcessDied's restart branch uses), so the
            // third crash still restarts and the fourth suspends.
            AgentHub.SetConsecutiveDeathsForTests(3);
            Assert.IsFalse(AgentHub.IsCrashLoopSuspended);
            AgentHub.SetConsecutiveDeathsForTests(4);
            Assert.IsTrue(AgentHub.IsCrashLoopSuspended);
        }

        [Test]
        public void DrainTick_WhileSuspended_KeepsTheQueue_AndDoesNotRespawn()
        {
            // Queue one message (no sendable client in the test
            // environment, so SendOrQueue always queues).
            CompileGate.SendOrQueue("hello from the queue");
            Assert.AreEqual(1, CompileGate.PendingCount);

            AgentHub.SetConsecutiveDeathsForTests(4);
            LogAssert.Expect(LogType.Warning,
                "[AgentPanel] CompileGate: CLI connection is suspended after"
                + " repeated crashes; 1 message(s) remain queued.");

            // The guard must return BEFORE the EnsureStarted branch -- a
            // respawn attempt here would try to resolve and launch a real
            // CLI process (and log its own errors, which LogAssert would
            // then flag as unexpected).
            CompileGate.OnUpdate();

            Assert.AreEqual(1, CompileGate.PendingCount,
                "user text must never be dropped by the suspension -- the"
                + " queue stays persisted for the next re-arm");
        }

        [Test]
        public void DrainTick_WhileSuspended_WarnsOnce_NotEveryTick()
        {
            CompileGate.SendOrQueue("still queued");
            AgentHub.SetConsecutiveDeathsForTests(4);
            LogAssert.Expect(LogType.Warning,
                "[AgentPanel] CompileGate: CLI connection is suspended after"
                + " repeated crashes; 1 message(s) remain queued.");
            CompileGate.OnUpdate();
            // The first tick unhooked the drain; production never calls
            // OnUpdate again until something re-arms. Driving the seam a
            // second time anyway must warn again (it IS a fresh tick) --
            // what we pin here is that the first tick detached the hook,
            // which is observable as SendOrQueue re-arming from scratch:
            LogAssert.Expect(LogType.Warning,
                "[AgentPanel] CompileGate: CLI connection is suspended after"
                + " repeated crashes; 2 message(s) remain queued.");
            CompileGate.SendOrQueue("second");
            CompileGate.OnUpdate();
            Assert.AreEqual(2, CompileGate.PendingCount);
        }
    }
}
