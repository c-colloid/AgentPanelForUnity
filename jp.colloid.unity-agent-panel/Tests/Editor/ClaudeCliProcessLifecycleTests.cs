using Colloid.AgentPanel.Core.Process;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// CORE-3/4/5/10: the parts of the transport lifecycle hardening that
    /// are testable without spawning a real process (project rule: no real
    /// subprocesses from unit tests). The concurrency itself
    /// (Start-vs-instant-exit under _lifecycleLock, the bounded post-exit
    /// output drain) is validated by the UAP_LIVE_CLI-gated live suite and
    /// by construction -- these tests pin the pure decision, the bounded
    /// constants' contract, and the idempotence Dispose/CloseStdin rely on.
    /// </summary>
    [TestFixture]
    public class ClaudeCliProcessLifecycleTests
    {
        private sealed class FakeKiller : IProcessKiller
        {
            public int KillTreeCalls;

            public bool KillTree(int pid)
            {
                KillTreeCalls++;
                return true;
            }
        }

        /// <summary>CORE-4's one decision: a process observed as already exited must never be marked running.</summary>
        [Test]
        public void ShouldMarkRunning_TracksHasExited()
        {
            Assert.IsTrue(ClaudeCliProcess.ShouldMarkRunning(false));
            Assert.IsFalse(ClaudeCliProcess.ShouldMarkRunning(true));
        }

        /// <summary>CORE-3/5: the post-exit flush waits are finite by contract -- an unbounded wait is exactly the bug they replace.</summary>
        [Test]
        public void OutputFlushBounds_AreFiniteAndSane()
        {
            Assert.Greater(OneShotCli.OutputFlushMillis, 0);
            Assert.LessOrEqual(OneShotCli.OutputFlushMillis, 5000);
            Assert.Greater(ClaudeCliProcess.OutputFlushMillis, 0);
            Assert.LessOrEqual(ClaudeCliProcess.OutputFlushMillis, 5000);
        }

        /// <summary>CORE-10: Dispose now closes stdin first, so CloseStdin must stay idempotent under every ordering.</summary>
        [Test]
        public void CloseStdin_BeforeStartAndRepeated_NeverThrows()
        {
            var transport = new ClaudeCliProcess(new FakeKiller());
            transport.CloseStdin();
            transport.CloseStdin();
            transport.Dispose();
        }

        [Test]
        public void Dispose_NeverStarted_AndDouble_NeverThrows()
        {
            var killer = new FakeKiller();
            var transport = new ClaudeCliProcess(killer);
            transport.Dispose();
            transport.Dispose();
            Assert.AreEqual(0, killer.KillTreeCalls,
                "a never-started transport has nothing to kill");
        }

        [Test]
        public void KillAndStop_NeverStarted_AreNoOps()
        {
            var killer = new FakeKiller();
            var transport = new ClaudeCliProcess(killer);
            transport.Kill();
            transport.Stop("ignored", 100);
            Assert.AreEqual(0, killer.KillTreeCalls);
            Assert.IsFalse(transport.IsRunning);
        }
    }
}
