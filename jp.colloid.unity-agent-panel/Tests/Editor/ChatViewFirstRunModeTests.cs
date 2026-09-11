using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UXO-1: a not-logged-in FIRST run used to look fully usable -- the
    /// CLI spawns fine without login, the transcript is empty, so
    /// ResolveFirstRunMode returned Hidden and the user's first send
    /// bounced off authentication_failed before the login card could
    /// appear. The mode decision now also reads the boot-time
    /// `auth status` cache and is fully parameterized (no statics), so the
    /// whole matrix is pinned here without spawning any subprocess (the
    /// project's auth-safety rule: tests never call RefreshAuthStatus/
    /// BeginLogin directly).
    /// </summary>
    [TestFixture]
    public class ChatViewFirstRunModeTests
    {
        private static ChatSession EmptySession()
        {
            return new ChatSession();
        }

        private static ChatSession SessionWithAuthError()
        {
            var session = new ChatSession();
            var message = new ChatMessage { role = ChatMessage.RoleSystem };
            message.Add(ChatMessageBlock.MakeError(
                "CLI error: authentication_failed. Please run /login"));
            session.AddMessage(message);
            return session;
        }

        private static AuthStatus LoggedOut()
        {
            return new AuthStatus { IsAvailable = true, LoggedIn = false };
        }

        private static AuthStatus LoggedIn()
        {
            return new AuthStatus { IsAvailable = true, LoggedIn = true, Email = "user@example.com" };
        }

        [Test]
        public void FreshBoot_AuthQueryNotCompletedYet_StaysHidden()
        {
            // null = the boot-time query has not returned (worst case a
            // 10 s timeout). Flashing the login card on every healthy boot
            // would be worse than the original defect.
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, EmptySession(), null));
        }

        [Test]
        public void FreshBoot_CliUnavailableStatus_StaysHidden()
        {
            // IsAvailable == false means the auth PROBE could not run
            // (spawn failure/timeout) -- that is CliNotFound territory via
            // LastError, not evidence the user is logged out.
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, EmptySession(),
                    AuthStatus.Unavailable()));
        }

        [Test]
        public void FreshBoot_NotLoggedIn_ShowsTheLoginCard_BeforeAnySendBounces()
        {
            // THE UXO-1 pin: available + not logged in -> proactive card,
            // empty transcript and all.
            Assert.AreEqual(FirstRunView.Mode.NotLoggedIn,
                ChatView.ResolveFirstRunMode(null, null, EmptySession(), LoggedOut()));
        }

        [Test]
        public void FreshBoot_LoggedIn_StaysHidden()
        {
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, EmptySession(), LoggedIn()));
        }

        [Test]
        public void EnvTokenAuth_LoggedInWithEmptyEmail_StaysHidden()
        {
            // ANTHROPIC_AUTH_TOKEN-style auth reports LoggedIn with an
            // empty Email -- the gate must not require an email address.
            var envToken = new AuthStatus
            {
                IsAvailable = true,
                LoggedIn = true,
                AuthMethod = "oauth_token"
            };
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, EmptySession(), envToken));
        }

        [Test]
        public void CliNotFound_WinsOverEverything()
        {
            Assert.AreEqual(FirstRunView.Mode.CliNotFound,
                ChatView.ResolveFirstRunMode("probe failed: no claude on PATH",
                    null, SessionWithAuthError(), LoggedOut()));
        }

        [Test]
        public void RecentTranscriptAuthError_Wins_EvenWhenTheCachedStatusSaysLoggedIn()
        {
            // An env token revoked mid-session produces auth-error blocks
            // while the cached status still says LoggedIn -- the live
            // evidence in the transcript must win over the stale cache.
            Assert.AreEqual(FirstRunView.Mode.NotLoggedIn,
                ChatView.ResolveFirstRunMode(null, null, SessionWithAuthError(), LoggedIn()));
        }

        [Test]
        public void RecentTranscriptAuthError_StillWorks_WithNoAuthStatusAtAll()
        {
            // The pre-UXO-1 reactive path stays intact for sessions where
            // the boot probe never completed.
            Assert.AreEqual(FirstRunView.Mode.NotLoggedIn,
                ChatView.ResolveFirstRunMode(null, null, SessionWithAuthError(), null));
        }

        // -- UXO-4: the login card leads with ONE primary action ---------

        /// <summary>
        /// Every setup card keeps exactly one uap-card-btn--primary: the
        /// login card's used to sit BELOW a terminal walkthrough, next to a
        /// same-weight Check-again. The terminal path survives, demoted
        /// into a collapsed foldout.
        /// </summary>
        [Test]
        public void SetupCards_EachHaveExactlyOnePrimaryButton()
        {
            var view = new FirstRunView();
            System.Collections.Generic.List<VisualElement> cards =
                view.Root.Query(className: "uap-card").ToList();
            Assert.AreEqual(2, cards.Count, "CLI card + login card");
            foreach (VisualElement card in cards)
            {
                Assert.AreEqual(1,
                    card.Query<Button>(className: "uap-card-btn--primary").ToList().Count,
                    "one obvious next action per card");
            }
        }

        [Test]
        public void LoginCard_TerminalPath_LivesInACollapsedFoldout_WithCheckAgain()
        {
            var view = new FirstRunView();
            // Two demoted terminal paths exist now (design note
            // 2026-09-10-in-panel-install-and-sign-in.md): the CLI card's
            // "install manually" foldout and the login card's /login one.
            // Both stay collapsed; Check again lives in exactly one of them.
            System.Collections.Generic.List<Foldout> foldouts =
                view.Root.Query<Foldout>(className: "uap-card-foldout").ToList();
            Assert.IsTrue(foldouts.Count >= 1, "the terminal path must still exist, demoted");
            int checkAgainCount = 0;
            foreach (Foldout foldout in foldouts)
            {
                Assert.IsFalse(foldout.value, "collapsed until asked for");
                checkAgainCount += foldout.Query<Button>().Where(
                    delegate(Button b) { return b.text == L10n.S.FirstRunCheckAgainButton; })
                    .ToList().Count;
            }
            Assert.AreEqual(1, checkAgainCount, "Check again belongs to the terminal path now");
        }

    }
}
