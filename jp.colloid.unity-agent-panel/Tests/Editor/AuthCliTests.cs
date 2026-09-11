using System;
using Colloid.AgentPanel.Core.Process;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Fixture-only regression guards for AuthCli/AuthLoginSession (docs/
    /// design-notes/2026-08-02-auth-in-panel.md): AuthStatus JSON parsing,
    /// the OAuth-URL and promptless-code-prompt pure seams (pinned to the
    /// exact captured "auth login" stdout, including its non-ASCII ellipsis
    /// and its non-newline-terminated final line -- see
    /// Fixtures/auth_login_stdout.txt), and AuthLoginSession's argument
    /// guards. No test here spawns the real "claude" binary except the one
    /// UAP_LIVE_CLI-gated status round trip at the bottom (read-only "auth
    /// status --json"), per the project's auth-safety rule.
    /// </summary>
    public class AuthCliTests
    {
        // -- AuthStatus / ParseStatusJson -----------------------------------------

        [Test]
        public void ParseStatusJson_LoggedIn_ParsesAllFields()
        {
            string json = "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\","
                + "\"email\":\"user@example.com\",\"orgId\":\"org_123\",\"orgName\":\"Acme\","
                + "\"subscriptionType\":\"max\"}";
            AuthStatus status = AuthCli.ParseStatusJson(json);
            Assert.IsTrue(status.IsAvailable);
            Assert.IsTrue(status.LoggedIn);
            Assert.AreEqual("user@example.com", status.Email);
            Assert.AreEqual("max", status.SubscriptionType);
            Assert.AreEqual("claude.ai", status.AuthMethod);
        }

        [Test]
        public void ParseStatusJson_LoggedOut_ParsesFalseWithEmptyOptionalFields()
        {
            string json = "{\"loggedIn\":false,\"authMethod\":\"none\",\"apiProvider\":\"firstParty\"}";
            AuthStatus status = AuthCli.ParseStatusJson(json);
            Assert.IsTrue(status.IsAvailable);
            Assert.IsFalse(status.LoggedIn);
            Assert.AreEqual(string.Empty, status.Email);
            Assert.AreEqual(string.Empty, status.SubscriptionType);
            Assert.AreEqual("none", status.AuthMethod);
        }

        [Test]
        public void ParseStatusJson_MalformedJson_ReturnsUnavailable()
        {
            AuthStatus status = AuthCli.ParseStatusJson("{not valid json");
            Assert.IsFalse(status.IsAvailable);
            Assert.IsFalse(status.LoggedIn);
        }

        [Test]
        public void ParseStatusJson_EmptyOrNull_ReturnsUnavailable()
        {
            Assert.IsFalse(AuthCli.ParseStatusJson(string.Empty).IsAvailable);
            Assert.IsFalse(AuthCli.ParseStatusJson(null).IsAvailable);
        }

        [Test]
        public void ParseStatusJson_NonObjectJson_ReturnsUnavailable()
        {
            Assert.IsFalse(AuthCli.ParseStatusJson("[1,2,3]").IsAvailable);
            Assert.IsFalse(AuthCli.ParseStatusJson("\"just a string\"").IsAvailable);
            Assert.IsFalse(AuthCli.ParseStatusJson("42").IsAvailable);
        }

        [Test]
        public void ParseStatusJson_ExtraUnknownFields_AreIgnoredWithoutFailing()
        {
            string json = "{\"loggedIn\":true,\"email\":\"a@b.com\",\"subscriptionType\":\"pro\","
                + "\"authMethod\":\"claude.ai\",\"somethingFutureField\":{\"nested\":true,\"list\":[1,2]}}";
            AuthStatus status = AuthCli.ParseStatusJson(json);
            Assert.IsTrue(status.IsAvailable);
            Assert.IsTrue(status.LoggedIn);
            Assert.AreEqual("a@b.com", status.Email);
            Assert.AreEqual("pro", status.SubscriptionType);
        }

        [Test]
        public void ParseStatusJson_SurroundingWhitespaceAndTrailingNewline_IsTolerated()
        {
            // RunOneShot appends "\n" per OutputDataReceived line, so a
            // real capture always carries a trailing newline.
            string json = "  {\"loggedIn\":false,\"authMethod\":\"none\"}  \n";
            AuthStatus status = AuthCli.ParseStatusJson(json);
            Assert.IsTrue(status.IsAvailable);
            Assert.IsFalse(status.LoggedIn);
        }

        // -- ExtractOAuthUrl (pure seam, pinned to the captured fixture) ----------

        [Test]
        public void ExtractOAuthUrl_CapturedLoginStdout_FindsExactUrl()
        {
            string stdout = FixtureLoader.ReadAllText("auth_login_stdout.txt");
            string url = AuthCli.ExtractOAuthUrl(stdout);
            Assert.IsNotNull(url);
            StringAssert.StartsWith("https://claude.com/cai/oauth/authorize?code=true", url);
            StringAssert.Contains("state=B7O-ypAiJfiIo5pswCbLhM0xyj2kI8ucLHSb-zkhyxQ", url);
            // The match must stop at the newline, never bleeding into the
            // following "Paste code here..." prompt line.
            StringAssert.DoesNotContain("Paste", url);
            StringAssert.DoesNotContain("\n", url);
        }

        [Test]
        public void ExtractOAuthUrl_NoUrlPresent_ReturnsNull()
        {
            Assert.IsNull(AuthCli.ExtractOAuthUrl("Opening browser to sign in...\nno link in this output"));
        }

        [Test]
        public void ExtractOAuthUrl_NullOrEmpty_ReturnsNull()
        {
            Assert.IsNull(AuthCli.ExtractOAuthUrl(null));
            Assert.IsNull(AuthCli.ExtractOAuthUrl(string.Empty));
        }

        // -- EndsWithCodePrompt (promptless-tail detection) -----------------------

        [Test]
        public void EndsWithCodePrompt_CapturedLoginStdout_DetectsPromptDespiteNoTrailingNewline()
        {
            string stdout = FixtureLoader.ReadAllText("auth_login_stdout.txt");
            Assert.IsTrue(AuthCli.EndsWithCodePrompt(stdout));
        }

        [Test]
        public void EndsWithCodePrompt_ExactTailAlone_IsDetected()
        {
            Assert.IsTrue(AuthCli.EndsWithCodePrompt(AuthCli.WaitingForCodePromptTail));
        }

        [Test]
        public void EndsWithCodePrompt_PartialPromptText_IsNotDetected()
        {
            Assert.IsFalse(AuthCli.EndsWithCodePrompt("Paste code here if prompted"));
            Assert.IsFalse(AuthCli.EndsWithCodePrompt("Paste code here if prompted >"));
        }

        [Test]
        public void EndsWithCodePrompt_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(AuthCli.EndsWithCodePrompt(null));
            Assert.IsFalse(AuthCli.EndsWithCodePrompt(string.Empty));
        }

        // -- AuthLoginSession argument guards (pure logic, no process spawn) ------

        [Test]
        public void Begin_EmptyCliPath_ReturnsNull()
        {
            Assert.IsNull(AuthLoginSession.Begin(string.Empty, new FakeKiller()));
        }

        [Test]
        public void Begin_NullCliPath_ReturnsNull()
        {
            Assert.IsNull(AuthLoginSession.Begin(null, new FakeKiller()));
        }

        [Test]
        public void Begin_NullKiller_ReturnsNull()
        {
            Assert.IsNull(AuthLoginSession.Begin("claude", null));
        }

        /// <summary>
        /// A path that cannot possibly resolve to a real executable drives
        /// Process.Start() into its exception path -- exercising
        /// AuthLoginSession's spawn-failure branch (Exited(-1) deferred
        /// through AuthCli's pump) without needing any real CLI binary.
        /// </summary>
        [Test]
        public void Begin_NonExistentExecutable_EventuallyExitsWithNegativeOneOnDrain()
        {
            AuthLoginSession session = AuthLoginSession.Begin(
                "C:\\this\\path\\does\\not\\exist\\uap-fake-claude.exe", new FakeKiller());
            Assert.IsNotNull(session);

            int? exitCode = null;
            session.Exited += delegate(int code) { exitCode = code; };

            // The failure is deferred through the same pump the success
            // path uses (see AuthLoginSession.Start's doc comment) --
            // it must NOT already have fired before the subscription above.
            Assert.IsFalse(session.HasExited,
                "spawn failure must not raise Exited synchronously inside Begin(), "
                + "or a caller subscribing right after Begin() returns would miss it");

            AuthCli.DrainPendingForTests();

            Assert.IsTrue(session.HasExited);
            Assert.AreEqual(-1, session.ExitCode);
            Assert.IsTrue(exitCode.HasValue);
            Assert.AreEqual(-1, exitCode.Value);

            session.Dispose();
        }

        [Test]
        public void SubmitCode_BeforeAnyProcessRunning_ReturnsFalse()
        {
            // A session whose spawn already failed has _running == false;
            // SubmitCode must refuse rather than throw.
            AuthLoginSession session = AuthLoginSession.Begin(
                "C:\\this\\path\\does\\not\\exist\\uap-fake-claude.exe", new FakeKiller());
            Assert.IsNotNull(session);
            AuthCli.DrainPendingForTests();
            Assert.IsFalse(session.SubmitCode("ABC123"));
            session.Dispose();
        }

        [Test]
        public void SubmitCode_BlankCode_ReturnsFalseWithoutTouchingStdin()
        {
            AuthLoginSession session = AuthLoginSession.Begin(
                "C:\\this\\path\\does\\not\\exist\\uap-fake-claude.exe", new FakeKiller());
            Assert.IsNotNull(session);
            Assert.IsFalse(session.SubmitCode(string.Empty));
            Assert.IsFalse(session.SubmitCode("   "));
            Assert.IsFalse(session.SubmitCode(null));
            session.Dispose();
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            AuthLoginSession session = AuthLoginSession.Begin(
                "C:\\this\\path\\does\\not\\exist\\uap-fake-claude.exe", new FakeKiller());
            Assert.IsNotNull(session);
            session.Dispose();
            Assert.DoesNotThrow(delegate { session.Dispose(); });
        }

        // -- AuthLoginSession state machine (pure logic, no process) --------------
        // docs/design-notes/2026-08-02-auth-in-panel.md section 4's fourth
        // regression guard, distinct from the ExtractOAuthUrl/
        // EndsWithCodePrompt tests above: those exercise the two static
        // helpers directly against one whole string, never AuthLoginSession's
        // own OnOutputChunk buffering (accumulate-then-rescan) that actually
        // raises UrlAvailable/WaitingForCode in production against streamed,
        // arbitrarily-split chunks.

        [Test]
        public void FeedOutputChunk_StreamedFixtureAcrossMultipleChunks_RaisesUrlAvailableThenWaitingForCode()
        {
            AuthLoginSession session = AuthLoginSession.CreateForTests();
            int urlRaisedCount = 0;
            string raisedUrl = null;
            int waitingRaisedCount = 0;
            session.UrlAvailable += delegate(string url)
            {
                urlRaisedCount++;
                raisedUrl = url;
            };
            session.WaitingForCode += delegate { waitingRaisedCount++; };

            string stdout = FixtureLoader.ReadAllText("auth_login_stdout.txt");
            string[] lines = stdout.Split('\n');
            Assert.GreaterOrEqual(lines.Length, 3, "fixture must have its 3 captured lines.");

            // Feed one captured line per chunk (the CLI's real per-print
            // stdout flushes), across THREE separate calls, proving
            // OnOutputChunk accumulates across calls rather than only ever
            // inspecting the newest chunk in isolation.
            session.FeedOutputChunkForTests(lines[0] + "\n");
            AuthCli.DrainPendingForTests();
            Assert.AreEqual(0, urlRaisedCount, "line 1 alone carries no URL.");
            Assert.AreEqual(0, waitingRaisedCount);

            session.FeedOutputChunkForTests(lines[1] + "\n");
            AuthCli.DrainPendingForTests();
            Assert.AreEqual(1, urlRaisedCount);
            StringAssert.StartsWith("https://claude.com/cai/oauth/authorize?code=true", raisedUrl);
            StringAssert.Contains("state=B7O-ypAiJfiIo5pswCbLhM0xyj2kI8ucLHSb-zkhyxQ", raisedUrl);
            StringAssert.DoesNotContain("\n", raisedUrl);
            Assert.AreEqual(0, waitingRaisedCount, "the prompt tail has not arrived yet.");

            session.FeedOutputChunkForTests(lines[2]);
            AuthCli.DrainPendingForTests();
            Assert.AreEqual(1, waitingRaisedCount);
            Assert.IsTrue(session.IsWaitingForCode);
            Assert.AreEqual(1, urlRaisedCount, "must not re-raise UrlAvailable on a later chunk.");
            Assert.AreEqual(raisedUrl, session.OAuthUrl);
        }

        [Test]
        public void FeedOutputChunk_UrlNotYetPresent_DoesNotRaiseAcrossPartialPrefixChunk()
        {
            AuthLoginSession session = AuthLoginSession.CreateForTests();
            string raisedUrl = null;
            session.UrlAvailable += delegate(string url) { raisedUrl = url; };

            // Split BEFORE the "https://" literal itself even begins, so
            // the accumulated buffer after chunk 1 contains no candidate
            // match at all -- proving detection genuinely waits for the
            // token to exist in the accumulated buffer rather than firing
            // on unrelated text.
            session.FeedOutputChunkForTests("Opening browser to sign in...\nIf the browser didn't open, visit: ");
            AuthCli.DrainPendingForTests();
            Assert.IsNull(raisedUrl, "no \"https://\" token exists yet in the buffer.");

            session.FeedOutputChunkForTests("https://claude.com/cai/oauth/authorize?code=true&state=abc\n");
            AuthCli.DrainPendingForTests();
            Assert.AreEqual("https://claude.com/cai/oauth/authorize?code=true&state=abc", raisedUrl);
        }

        [Test]
        public void FeedOutputChunk_WaitingForCode_NotRaisedUntilExactTailArrives()
        {
            AuthLoginSession session = AuthLoginSession.CreateForTests();
            int waitingRaisedCount = 0;
            session.WaitingForCode += delegate { waitingRaisedCount++; };

            // A partial prefix of the prompt tail must not match.
            session.FeedOutputChunkForTests("Paste code here if prompted");
            AuthCli.DrainPendingForTests();
            Assert.AreEqual(0, waitingRaisedCount);
            Assert.IsFalse(session.IsWaitingForCode);

            // Completing it to the exact (space-terminated, no newline) tail
            // must raise it, exactly once even across subsequent re-scans.
            session.FeedOutputChunkForTests(" > ");
            AuthCli.DrainPendingForTests();
            Assert.AreEqual(1, waitingRaisedCount);
            Assert.IsTrue(session.IsWaitingForCode);

            session.FeedOutputChunkForTests(string.Empty);
            AuthCli.DrainPendingForTests();
            Assert.AreEqual(1, waitingRaisedCount, "must not re-raise on a later re-scan.");
        }

        private sealed class FakeKiller : IProcessKiller
        {
            public bool KillTree(int pid)
            {
                return true;
            }
        }

        // -- Live (gated) ----------------------------------------------------------

        /// <summary>
        /// Read-only round trip against the real, already-authenticated CLI
        /// (docs/design-notes/2026-08-02-auth-in-panel.md section 3):
        /// "auth status --json" only, never login/logout. Guarded the same
        /// way LiveCliIntegrationTests is: [Category("LiveCli")] plus the
        /// UAP_LIVE_CLI=1 environment gate. Drains AuthCli's callback queue
        /// manually (DrainPendingForTests) rather than depending on
        /// EditorApplication.update, which does not tick while this
        /// synchronous [Test] method is still running -- see that method's
        /// doc comment.
        /// </summary>
        [Test]
        [Category("LiveCli")]
        public void QueryStatus_LiveRoundTrip_ReturnsAvailableParsedResult()
        {
            if (Environment.GetEnvironmentVariable("UAP_LIVE_CLI") != "1")
            {
                Assert.Ignore("Live CLI test skipped (set UAP_LIVE_CLI=1 to enable).");
            }

            string cliPath = new WindowsCliPathProbe(null).Resolve();
            Assert.IsNotNull(cliPath, "WindowsCliPathProbe could not resolve claude.exe.");

            AuthStatus result = null;
            bool done = false;
            AuthCli.QueryStatus(cliPath, delegate(AuthStatus status)
            {
                result = status;
                done = true;
            });

            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done && DateTime.UtcNow < deadline)
            {
                AuthCli.DrainPendingForTests();
                if (!done)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            Assert.IsTrue(done, "AuthCli.QueryStatus callback never fired within 15s.");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsAvailable,
                "auth status --json should be available for a real, resolvable CLI.");
        }
    }
}
