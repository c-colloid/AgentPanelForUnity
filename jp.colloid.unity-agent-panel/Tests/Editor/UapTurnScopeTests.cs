using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapTurnScope tests (design section 1.3/7.4/8.2: one Undo group + one
    /// refresh-suppression window per turn). Deliberately narrow: this
    /// class touches the REAL, process-global AssetDatabase auto-refresh
    /// counter, so every test begins and ends its OWN scope in strict
    /// try/finally pairs -- an unbalanced test here would leave
    /// AssetDatabase.AllowAutoRefresh permanently suppressed for the rest
    /// of this editor session's test run (see RefreshSuppressionCounterTests
    /// for the exhaustive pure-counter coverage of the reentrant logic
    /// itself).
    /// </summary>
    [TestFixture]
    public class UapTurnScopeTests
    {
        [TearDown]
        public void TearDown()
        {
            // Safety net: if an assertion failure above left the scope
            // open, close it now rather than leaking suppressed
            // auto-refresh into every later test in the run.
            if (UapTurnScope.IsActive)
            {
                UapTurnScope.EndIfActive();
            }
        }

        [Test]
        public void BeginIfNeeded_ThenEndIfActive_TogglesIsActive()
        {
            Assert.IsFalse(UapTurnScope.IsActive);
            UapTurnScope.BeginIfNeeded();
            try
            {
                Assert.IsTrue(UapTurnScope.IsActive);
            }
            finally
            {
                UapTurnScope.EndIfActive();
            }
            Assert.IsFalse(UapTurnScope.IsActive);
        }

        [Test]
        public void BeginIfNeeded_CalledTwice_StaysActiveOnce_NoThrow()
        {
            UapTurnScope.BeginIfNeeded();
            try
            {
                Assert.DoesNotThrow(delegate { UapTurnScope.BeginIfNeeded(); });
                Assert.IsTrue(UapTurnScope.IsActive);
            }
            finally
            {
                UapTurnScope.EndIfActive();
            }
        }

        [Test]
        public void EndIfActive_WithoutBegin_IsANoOp()
        {
            Assert.DoesNotThrow(delegate { UapTurnScope.EndIfActive(); });
            Assert.IsFalse(UapTurnScope.IsActive);
        }

        [Test]
        public void EndIfActive_CalledTwice_SecondCallIsANoOp()
        {
            UapTurnScope.BeginIfNeeded();
            UapTurnScope.EndIfActive();
            Assert.DoesNotThrow(delegate { UapTurnScope.EndIfActive(); });
            Assert.IsFalse(UapTurnScope.IsActive);
        }

        [Test]
        public void FullCycle_LeavesAutoRefreshExplicitlyReenabled()
        {
            // AssetDatabase does not expose a "is auto-refresh currently
            // disallowed" getter, so this asserts the observable contract
            // instead: AllowAutoRefresh + Refresh must both be callable
            // without throwing immediately after EndIfActive, and the
            // scope must report inactive.
            UapTurnScope.BeginIfNeeded();
            UapTurnScope.EndIfActive();
            Assert.IsFalse(UapTurnScope.IsActive);
            Assert.DoesNotThrow(delegate { AssetDatabase.Refresh(); });
        }
    }
}
