using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// MODEL-13: ChatSession.AccumulateTurn's cost bookkeeping.
    /// result.total_cost_usd is the CLI process's OWN running total, and a
    /// --resume spawns a fresh process whose total restarts from zero --
    /// the old plain overwrite made the session's displayed cost go
    /// BACKWARDS on resume. The rule is monotonic non-decreasing, with the
    /// documented trade that a resumed session under-counts until the new
    /// process passes the old high-water mark.
    /// </summary>
    [TestFixture]
    public class ChatSessionTests
    {
        private static ChatSession Turn(ChatSession session, double reportedTotal)
        {
            session.AccumulateTurn(10, 20, 0, 0, reportedTotal);
            return session;
        }

        [Test]
        public void AccumulateTurn_IncreasingTotals_TrackTheLatest()
        {
            var session = new ChatSession();
            Turn(session, 0.05);
            Turn(session, 0.12);
            Assert.AreEqual(0.12, session.totalCostUsd, 1e-9);
        }

        [Test]
        public void AccumulateTurn_SmallerTotalAfterResume_NeverShrinksTheDisplay()
        {
            var session = new ChatSession();
            Turn(session, 0.12);
            // A --resume'd fresh CLI process reports ITS OWN total: small again.
            Turn(session, 0.03);
            Assert.AreEqual(0.12, session.totalCostUsd, 1e-9,
                "the session total is monotonic -- a resumed process's smaller running total must not roll it back");
        }

        [Test]
        public void AccumulateTurn_ZeroReports_KeepZero_SubscriptionAuth()
        {
            var session = new ChatSession();
            Turn(session, 0.0);
            Turn(session, 0.0);
            Assert.AreEqual(0.0, session.totalCostUsd, 1e-9,
                "subscription auth always reports 0 -- it must stay 0, not become garbage");
        }

        [Test]
        public void AccumulateTurn_TokenTotals_StillSum()
        {
            var session = new ChatSession();
            session.AccumulateTurn(10, 20, 5, 3, 0.0);
            session.AccumulateTurn(1, 2, 1, 1, 0.0);
            Assert.AreEqual(11, session.totalInputTokens);
            Assert.AreEqual(22, session.totalOutputTokens);
            Assert.AreEqual(6, session.totalCacheReadInputTokens);
            Assert.AreEqual(4, session.totalCacheCreationInputTokens);
            Assert.AreEqual(2, session.completedTurns);
        }
    }
}
