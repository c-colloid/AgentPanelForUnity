using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guards for the AgentHub side of docs/design-notes/
    /// 2026-08-02-auth-in-panel.md's auth-in-panel feature -- specifically
    /// two review findings that live in AgentHub.cs's login-session
    /// orchestration:
    ///
    /// 1. A stale "auth status --json" query result must never overwrite a
    ///    fresher one that started later but happened to resolve first
    ///    (OnLoginSessionExited unconditionally re-queried status without
    ///    checking whether a RefreshAuthStatus() call was already in
    ///    flight, so whichever subprocess happened to finish LAST won,
    ///    regardless of which one started last).
    /// 2. An in-flight "auth login" child process/Process object must be
    ///    torn down (Cancel + Dispose) whenever AgentHub tears the panel
    ///    down for good (editor quit via ReloadLifecycle.OnWantsToQuit/
    ///    OnQuitting -&gt; AgentHub.Shutdown), not just on its own Exited
    ///    event -- previously only AuthLoginSession's own
    ///    beforeAssemblyReload hook (domain reload) or a normal process
    ///    exit ever disposed it, and neither of those fires for a plain
    ///    editor quit.
    ///
    /// Neither test spawns a real CLI process: AgentHub.BeginLogin()/
    /// RefreshAuthStatus()/OnLoginSessionExited() all resolve a REAL
    /// cliManualPath-based path and would spawn an actual subprocess if
    /// called directly (forbidden for a plain unit test per the project's
    /// auth-safety rule) -- these tests instead drive the pure token-guard
    /// logic and the login-session teardown logic through dedicated
    /// test-only seams (AgentHub.BumpAuthStatusQueryTokenForTests /
    /// ApplyAuthStatusQueryResultForTests / SetLoginSessionForTests /
    /// TerminateLoginSessionForTests), paired with
    /// AuthLoginSession.CreateForTests() (no backing process at all).
    /// </summary>
    [TestFixture]
    public class AgentHubAuthTests
    {
        [TearDown]
        public void TearDown()
        {
            AgentHub.SetLoginSessionForTests(null);
            AgentHub.ResetAuthStatusForTests();
        }

        // -- Finding: stale query result overwriting a fresher one ---------------

        [Test]
        public void ApplyAuthStatusQueryResult_MatchingToken_AppliesStatus()
        {
            AgentHub.ResetAuthStatusForTests();
            int token = AgentHub.BumpAuthStatusQueryTokenForTests();

            var status = new AuthStatus { IsAvailable = true, LoggedIn = true, Email = "a@b.com" };
            AgentHub.ApplyAuthStatusQueryResultForTests(token, status);

            Assert.AreSame(status, AgentHub.CurrentAuthStatus);
        }

        [Test]
        public void ApplyAuthStatusQueryResult_StaleToken_IsDroppedWithoutTouchingCurrentStatus()
        {
            AgentHub.ResetAuthStatusForTests();
            int staleToken = AgentHub.BumpAuthStatusQueryTokenForTests();
            AgentHub.BumpAuthStatusQueryTokenForTests(); // a newer query starts; staleToken is now superseded

            var staleStatus = new AuthStatus { IsAvailable = true, LoggedIn = false };
            AgentHub.ApplyAuthStatusQueryResultForTests(staleToken, staleStatus);

            Assert.IsNull(AgentHub.CurrentAuthStatus,
                "a superseded query's result must be dropped entirely, not applied.");
        }

        /// <summary>
        /// The exact race from the review finding: RefreshAuthStatus() is
        /// already in flight (older token) when a login exits and
        /// OnLoginSessionExited fires its own query (newer token). Whichever
        /// subprocess happens to finish LAST must never win -- only the
        /// query that started LAST (the login's, which is authoritative per
        /// the design's "always re-query after login exits" rule) may ever
        /// end up as CurrentAuthStatus, regardless of completion order.
        /// </summary>
        [Test]
        public void ApplyAuthStatusQueryResult_OlderQueryResolvesAfterNewerOne_DoesNotOverwriteFreshResult()
        {
            AgentHub.ResetAuthStatusForTests();
            int olderToken = AgentHub.BumpAuthStatusQueryTokenForTests(); // RefreshAuthStatus(), started first
            int newerToken = AgentHub.BumpAuthStatusQueryTokenForTests(); // OnLoginSessionExited's re-query, started second

            var freshLoggedInStatus = new AuthStatus { IsAvailable = true, LoggedIn = true, Email = "fresh@example.com" };
            var staleLoggedOutStatus = new AuthStatus { IsAvailable = true, LoggedIn = false };

            // The newer (login) query's subprocess happens to finish FIRST.
            AgentHub.ApplyAuthStatusQueryResultForTests(newerToken, freshLoggedInStatus);
            Assert.AreSame(freshLoggedInStatus, AgentHub.CurrentAuthStatus);

            // The older (pre-login) query's subprocess finishes LATER, with
            // a stale logged-out snapshot. Before the fix this unconditionally
            // overwrote _authStatus; now it must be dropped as stale.
            AgentHub.ApplyAuthStatusQueryResultForTests(olderToken, staleLoggedOutStatus);

            Assert.AreSame(freshLoggedInStatus, AgentHub.CurrentAuthStatus,
                "the stale pre-login RefreshAuthStatus() result must not overwrite "
                + "the fresh post-login status OnLoginSessionExited already applied.");
        }

        // -- Finding: in-flight login session never torn down on quit ------------

        [Test]
        public void TerminateLoginSessionForTests_InFlightSession_DisposesAndClearsCurrentLoginSession()
        {
            AuthLoginSession session = AuthLoginSession.CreateForTests();
            AgentHub.SetLoginSessionForTests(session);
            Assert.AreSame(session, AgentHub.CurrentLoginSession);

            AgentHub.TerminateLoginSessionForTests();

            Assert.IsNull(AgentHub.CurrentLoginSession,
                "the quit-time teardown must clear AgentHub's login-session reference.");
            Assert.IsTrue(session.WasDisposedForTests,
                "and it must actually Dispose() the session (releasing its Process/pipe "
                + "handles), not just null out the reference -- this is the exact "
                + "leak ReloadLifecycle.OnWantsToQuit/OnQuitting previously had, since "
                + "neither of those ever touched AgentHub.CurrentLoginSession at all.");
        }

        [Test]
        public void TerminateLoginSessionForTests_NoSessionInFlight_IsANoOp()
        {
            AgentHub.SetLoginSessionForTests(null);
            Assert.DoesNotThrow(delegate { AgentHub.TerminateLoginSessionForTests(); });
            Assert.IsNull(AgentHub.CurrentLoginSession);
        }
    }
}
