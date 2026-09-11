using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// AgentClient state machine tests driven through FakeCliProcess with
    /// real captured CLI lines from the fixtures (out_bidi.jsonl,
    /// success_bidi_inbound.jsonl, permission_inbound.jsonl). No live CLI
    /// and no process spawn is involved.
    /// </summary>
    [TestFixture]
    public class AgentClientStateTests
    {
        private const string ExpectedBaseArgs =
            "-p --input-format stream-json --output-format stream-json --verbose"
            + " --include-partial-messages --replay-user-messages"
            + " --permission-prompt-tool stdio";

        private FakeCliProcess _fake;
        private AgentClient _client;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeCliProcess();
            _log = new List<string>();
            _client = new AgentClient(_fake, _log.Add);
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void StartClient(AgentClientOptions options = null)
        {
            _client.Start(options ?? new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        /// <summary>Starts the client and feeds the captured system/init line.</summary>
        private void MakeReady()
        {
            StartClient();
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"subtype\":\"init\""));
            PumpAll();
            Assert.AreEqual(AgentClientState.Ready, _client.State);
        }

        /// <summary>First fixture line containing all the given substrings.</summary>
        private static string FixtureLine(string fixture, params string[] substrings)
        {
            foreach (string line in FixtureLoader.ReadLines(fixture))
            {
                bool all = true;
                foreach (string s in substrings)
                {
                    if (line.IndexOf(s, System.StringComparison.Ordinal) < 0)
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    return line;
                }
            }
            Assert.Fail("No line in " + fixture + " contains: " + string.Join(" + ", substrings));
            return null;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null
                && haystack.IndexOf(needle, System.StringComparison.Ordinal) >= 0;
        }

        // ------------------------------------------------------------------
        // Spawn + initialize handshake
        // ------------------------------------------------------------------

        [Test]
        public void Start_SpawnsWithExactArguments_AndSendsInitialize()
        {
            StartClient();

            Assert.AreEqual(AgentClientState.Starting, _client.State);
            Assert.AreEqual(1, _fake.StartCallCount);
            Assert.AreEqual("C:/fake/claude.exe", _fake.StartedExecutablePath);
            Assert.AreEqual("C:/fake/project", _fake.StartedWorkingDirectory);
            Assert.AreEqual(ExpectedBaseArgs, _fake.StartedArguments);

            Assert.AreEqual(1, _fake.WrittenLines.Count);
            Assert.IsTrue(Contains(_fake.WrittenLines[0], "\"subtype\":\"initialize\""));
            Assert.IsTrue(Contains(_fake.WrittenLines[0], "\"request_id\":\"req_1\""));
        }

        /// <summary>CORE-7: the pure size predicate behind the Windows 32767-char CreateProcess cap.</summary>
        [Test]
        public void CommandLineWouldExceedLimit_PureThresholds()
        {
            Assert.IsFalse(AgentClient.CommandLineWouldExceedLimit("claude", "-p --verbose"));
            Assert.IsFalse(AgentClient.CommandLineWouldExceedLimit(null, null));
            Assert.IsTrue(AgentClient.CommandLineWouldExceedLimit(
                "claude", new string('x', AgentClient.WindowsCommandLineSafeLimit)));
        }

        /// <summary>
        /// CORE-7: an over-limit command line must refuse BEFORE spawning
        /// (the spawn would fail opaquely or truncate), surfacing through
        /// the same ProcessDied path the hub already renders. The platform
        /// seam forces the Windows-only gate on so this runs on any CI OS.
        /// </summary>
        [Test]
        public void Start_CommandLineTooLong_RefusesToSpawn_WithExplicitError()
        {
            AgentClient.ForceCommandLineLimitCheckForTests = true;
            try
            {
                string reason = null;
                _client.ProcessDied += delegate (string r) { reason = r; };

                StartClient(new AgentClientOptions
                {
                    CliPath = "C:/fake/claude.exe",
                    WorkingDirectory = "C:/fake/project",
                    AppendSystemPrompt = new string('x', AgentClient.WindowsCommandLineSafeLimit)
                });

                Assert.AreEqual(0, _fake.StartCallCount, "the transport must never be spawned");
                Assert.AreEqual(AgentClientState.Errored, _client.State);
                Assert.IsTrue(Contains(reason, "command line too long"), "was: " + reason);
            }
            finally
            {
                AgentClient.ForceCommandLineLimitCheckForTests = null;
            }
        }

        /// <summary>CORE-7: with the platform gate forced OFF, the same oversized options spawn normally (Unix ARG_MAX is megabytes).</summary>
        [Test]
        public void Start_CommandLineTooLong_GateOff_SpawnsNormally()
        {
            AgentClient.ForceCommandLineLimitCheckForTests = false;
            try
            {
                StartClient(new AgentClientOptions
                {
                    CliPath = "C:/fake/claude.exe",
                    WorkingDirectory = "C:/fake/project",
                    AppendSystemPrompt = new string('x', AgentClient.WindowsCommandLineSafeLimit)
                });

                Assert.AreEqual(1, _fake.StartCallCount);
                Assert.AreEqual(AgentClientState.Starting, _client.State);
            }
            finally
            {
                AgentClient.ForceCommandLineLimitCheckForTests = null;
            }
        }

        [Test]
        public void BuildArguments_AppendsResumeModelAndPermissionMode()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                ResumeSessionId = "19fe4284-bb73-4dcb-841e-5e3ce932e834",
                Model = "sonnet",
                PermissionMode = "plan"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --resume 19fe4284-bb73-4dcb-841e-5e3ce932e834"
                + " --model sonnet --permission-mode plan", args);
        }

        [Test]
        public void SystemInit_TransitionsToReady_AndPublishesSessionId()
        {
            StartClient();
            string sessionId = null;
            _client.SessionIdChanged += delegate (string id) { sessionId = id; };

            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"subtype\":\"init\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.AreEqual("19fe4284-bb73-4dcb-841e-5e3ce932e834", sessionId);
            Assert.AreEqual(sessionId, _client.SessionId);
            Assert.IsNotNull(_client.InitMessage);
            Assert.AreEqual("2.1.218", _client.InitMessage.ClaudeCodeVersion);
        }

        [Test]
        public void InitializeControlResponse_AloneAlsoTransitionsToReady()
        {
            // The captured stream really does deliver the control_response
            // BEFORE system/init; either order must reach Ready.
            StartClient();
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"request_id\":\"req_1\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsNotNull(_client.InitializeResponse);
            Assert.IsTrue(_client.InitializeResponse.Success);
            Assert.IsTrue(_client.InitializeResponse.Response["models"].Count > 0);
        }

        /// <summary>
        /// Regression guard for the "About shows CLI version: not
        /// connected" defect (docs/design-notes/2026-08-01-init-message-
        /// retention.md). The test above proves Ready is reachable from the
        /// control_response ALONE, before InitMessage is ever set; this one
        /// covers what happens once system/init arrives AFTER that --
        /// HandleSystemInit's own "if (State == Starting) SetState(Ready)"
        /// is a same-state no-op by then (no StateChanged fires), so
        /// InitMessageReceived must be the signal a listener (AgentHub) can
        /// rely on instead.
        /// </summary>
        [Test]
        public void SystemInit_AfterControlResponseAlreadyReachedReady_StillRaisesInitMessageReceived()
        {
            StartClient();
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"request_id\":\"req_1\""));
            PumpAll();
            Assert.AreEqual(AgentClientState.Ready, _client.State,
                "sanity: the control_response alone must already have reached Ready.");
            Assert.IsNull(_client.InitMessage,
                "sanity: InitMessage must still be null before system/init arrives.");

            var received = new List<SystemInitMessage>();
            _client.InitMessageReceived += received.Add;

            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"subtype\":\"init\""));
            PumpAll();

            Assert.AreEqual(1, received.Count,
                "InitMessageReceived must fire exactly once for this system/init line, "
                + "even though State was already Ready.");
            Assert.AreEqual("2.1.218", received[0].ClaudeCodeVersion);
            Assert.IsNotNull(_client.InitMessage,
                "InitMessage must be populated once system/init is processed, regardless "
                + "of the order it arrived in relative to the initialize control_response.");
            Assert.AreEqual("2.1.218", _client.InitMessage.ClaudeCodeVersion);
            Assert.AreEqual(AgentClientState.Ready, _client.State,
                "State must remain Ready -- no spurious re-transition.");
        }

        // ------------------------------------------------------------------
        // Live model tracking (docs/design-notes/2026-07-31-live-model-tracking.md)
        // ------------------------------------------------------------------

        /// <summary>Starts the client and feeds BOTH the initialize control_response
        /// (populates InitializeResponse.Response["models"]) and system/init
        /// (populates InitMessage), matching the real capture's ordering
        /// (out_bidi.jsonl line 1 = control_response, line 2 = system/init).</summary>
        private void MakeReadyWithModelsList()
        {
            StartClient();
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"request_id\":\"req_1\""));
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"subtype\":\"init\""));
            PumpAll();
            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsNotNull(_client.InitializeResponse);
        }

        private static string ExtractRequestId(string outboundLine)
        {
            return JsonParser.Parse(outboundLine)["request_id"].AsString();
        }

        [Test]
        public void CurrentModel_MirrorsInitMessage_WhenNoLiveSwitchHasHappened()
        {
            MakeReadyWithModelsList();
            Assert.AreEqual(_client.InitMessage.Model, _client.CurrentModel);
        }

        [Test]
        public void SetModel_CommitsLiveModelOnSuccess_AndResolvesAliasViaModelsList()
        {
            MakeReadyWithModelsList();
            string originalModel = _client.CurrentModel;

            _client.SetModel("haiku");
            string sentLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(sentLine, "\"subtype\":\"set_model\""));
            Assert.IsTrue(Contains(sentLine, "\"model\":\"haiku\""));
            string requestId = ExtractRequestId(sentLine);

            // No commit yet: the control_response has not arrived.
            Assert.AreEqual(originalModel, _client.CurrentModel);

            string success = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\","
                + "\"response\":{}}}";
            _fake.ScriptLine(success);
            PumpAll();

            // "haiku" resolves to "claude-haiku-4-5-20251001" via the SAME
            // models[] the picker itself parses (out_bidi.jsonl fixture) --
            // not the raw alias the caller passed.
            Assert.AreEqual("claude-haiku-4-5-20251001", _client.CurrentModel);
            Assert.AreNotEqual(originalModel, _client.CurrentModel);
            // InitMessage.Model itself is untouched (additive property only).
            Assert.AreEqual(originalModel, _client.InitMessage.Model);
        }

        [Test]
        public void SetModel_DoesNotCommitLiveModelOnFailureOrTimeout()
        {
            MakeReadyWithModelsList();
            string originalModel = _client.CurrentModel;

            _client.SetModel("haiku");
            string sentLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            string requestId = ExtractRequestId(sentLine);

            string failure = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"error\",\"request_id\":\"" + requestId + "\","
                + "\"error\":\"model not available\"}}";
            _fake.ScriptLine(failure);
            PumpAll();

            Assert.AreEqual(originalModel, _client.CurrentModel);
        }

        /// <summary>
        /// Pins the event contract AgentHub.OnControlRequestResolved relies
        /// on to fix the "model picker label does not update immediately"
        /// defect (docs/design-notes/2026-07-31-model-picker-refresh-gap.md):
        /// ControlRequestResolved must fire exactly once, synchronously
        /// within the same Pump() call that delivers the set_model success
        /// control_response, with kind == "set_model" and success == true --
        /// AND CurrentModel must already reflect the new value by the time
        /// that event fires (not on some later tick), since AgentHub relays
        /// this single event straight into RaiseChanged() with no
        /// additional delay of its own.
        /// </summary>
        [Test]
        public void SetModel_Success_FiresControlRequestResolved_WithCurrentModelAlreadyUpdated()
        {
            MakeReadyWithModelsList();
            var resolvedKinds = new List<string>();
            var successes = new List<bool>();
            string currentModelAtResolveTime = null;
            _client.ControlRequestResolved += delegate(string kind, bool success, string error)
            {
                resolvedKinds.Add(kind);
                successes.Add(success);
                currentModelAtResolveTime = _client.CurrentModel;
            };

            _client.SetModel("haiku");
            string requestId = ExtractRequestId(_fake.WrittenLines[_fake.WrittenLines.Count - 1]);
            string success = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\","
                + "\"response\":{}}}";
            _fake.ScriptLine(success);
            PumpAll();

            CollectionAssert.AreEqual(new[] { "set_model" }, resolvedKinds,
                "ControlRequestResolved must fire exactly once for this set_model round trip.");
            CollectionAssert.AreEqual(new[] { true }, successes);
            Assert.AreEqual("claude-haiku-4-5-20251001", currentModelAtResolveTime,
                "CurrentModel must already be the NEW resolved model by the time "
                + "ControlRequestResolved fires -- AgentHub relays this event straight "
                + "into RaiseChanged() with no further delay.");
        }

        [Test]
        public void SetModel_Failure_StillFiresControlRequestResolved_SoUiCanRepaint()
        {
            MakeReadyWithModelsList();
            var resolvedKinds = new List<string>();
            var successes = new List<bool>();
            _client.ControlRequestResolved += delegate(string kind, bool success, string error)
            {
                resolvedKinds.Add(kind);
                successes.Add(success);
            };

            _client.SetModel("haiku");
            string requestId = ExtractRequestId(_fake.WrittenLines[_fake.WrittenLines.Count - 1]);
            string failure = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"error\",\"request_id\":\"" + requestId + "\","
                + "\"error\":\"model not available\"}}";
            _fake.ScriptLine(failure);
            PumpAll();

            CollectionAssert.AreEqual(new[] { "set_model" }, resolvedKinds,
                "A failed set_model must still resolve so AgentHub can repaint "
                + "(clearing any transient switching affordance) even though "
                + "CurrentModel itself does not change on failure.");
            CollectionAssert.AreEqual(new[] { false }, successes);
        }

        [Test]
        public void SetModel_UnmatchedValue_FallsBackToRawValueOnSuccess()
        {
            // A manually-typed/unlisted model id has no models[] entry --
            // CurrentModel should still update to SOMETHING (the raw value)
            // rather than silently keeping the old label forever.
            MakeReadyWithModelsList();

            _client.SetModel("some-custom-model-id");
            string sentLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            string requestId = ExtractRequestId(sentLine);

            string success = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\","
                + "\"response\":{}}}";
            _fake.ScriptLine(success);
            PumpAll();

            Assert.AreEqual("some-custom-model-id", _client.CurrentModel);
        }

        [Test]
        public void Start_ResetsLiveModel_SoAFreshConnectIsAuthoritativeAgain()
        {
            MakeReadyWithModelsList();
            _client.SetModel("haiku");
            string requestId = ExtractRequestId(_fake.WrittenLines[_fake.WrittenLines.Count - 1]);
            _fake.ScriptLine("{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\","
                + "\"response\":{}}}");
            PumpAll();
            Assert.AreEqual("claude-haiku-4-5-20251001", _client.CurrentModel);

            // Simulate a reconnect: Stop then Start again (like AgentHub.Reconnect).
            _client.Stop();
            StartClient();
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"subtype\":\"init\""));
            PumpAll();

            Assert.AreEqual(_client.InitMessage.Model, _client.CurrentModel);
            Assert.AreNotEqual("claude-haiku-4-5-20251001", _client.CurrentModel);
        }

        // ------------------------------------------------------------------
        // SendMcpReconnect (Phase 5a, docs/research/08-mcp-transport.md
        // section 3.4's measured-working mcp_reconnect control_request --
        // the UapOps reconnect safety net's unit-testable seam).
        // ------------------------------------------------------------------

        [Test]
        public void SendMcpReconnect_WritesTheControlRequest_WithTheGivenServerName()
        {
            MakeReadyWithModelsList();
            _client.SendMcpReconnect("unity-ops");

            string sentLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(sentLine, "\"subtype\":\"mcp_reconnect\""));
            Assert.IsTrue(Contains(sentLine, "\"serverName\":\"unity-ops\""));
        }

        [Test]
        public void SendMcpReconnect_EmptyServerName_IsANoOp_WritesNothing()
        {
            MakeReadyWithModelsList();
            int before = _fake.WrittenLines.Count;
            _client.SendMcpReconnect(string.Empty);
            Assert.AreEqual(before, _fake.WrittenLines.Count);
        }

        [Test]
        public void SendMcpReconnect_Success_FiresControlRequestResolved_WithKindMcpReconnect()
        {
            MakeReadyWithModelsList();
            var resolvedKinds = new List<string>();
            var successes = new List<bool>();
            _client.ControlRequestResolved += delegate(string kind, bool success, string error)
            {
                resolvedKinds.Add(kind);
                successes.Add(success);
            };

            _client.SendMcpReconnect("unity-ops");
            string requestId = ExtractRequestId(_fake.WrittenLines[_fake.WrittenLines.Count - 1]);
            string success = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\","
                + "\"response\":{}}}";
            _fake.ScriptLine(success);
            PumpAll();

            CollectionAssert.AreEqual(new[] { "mcp_reconnect" }, resolvedKinds);
            CollectionAssert.AreEqual(new[] { true }, successes);
        }

        [Test]
        public void SendMcpReconnect_WriteFails_HandlesProcessDeath()
        {
            MakeReadyWithModelsList();
            _fake.FailWrites = true;
            _client.SendMcpReconnect("unity-ops");
            Assert.AreEqual(AgentClientState.Errored, _client.State);
        }

        // ------------------------------------------------------------------
        // Turn flow: send -> stream -> result
        // ------------------------------------------------------------------

        [Test]
        public void SendUserText_EntersStreaming_AndWritesSerializedUserLine()
        {
            MakeReady();

            _client.SendUserText("hello world");

            Assert.AreEqual(AgentClientState.Streaming, _client.State);
            Assert.IsTrue(_client.TurnActive);
            string last = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(last, "\"type\":\"user\""));
            Assert.IsTrue(Contains(last, "hello world"));
        }

        [Test]
        public void StreamEventDeltas_RaiseTextDelta_InOrder()
        {
            MakeReady();
            _client.SendUserText("go");
            var deltas = new List<string>();
            _client.TextDelta += deltas.Add;

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "\"text_delta\",\"text\":\"I\""));
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "'ll run the command."));
            PumpAll();

            Assert.AreEqual("I'll run the command.", string.Join("", deltas));
        }

        [Test]
        public void Result_CompletesTurn_AndReturnsToReady()
        {
            MakeReady();
            _client.SendUserText("go");
            ResultMessage completed = null;
            _client.TurnCompleted += delegate (ResultMessage r) { completed = r; };

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsFalse(_client.TurnActive);
            Assert.IsNotNull(completed);
            Assert.IsFalse(completed.IsError);
            Assert.AreEqual("DONE", completed.ResultText);
        }

        [Test]
        public void FaultyRawLineSubscriber_DoesNotWedgeResultLine()
        {
            MakeReady();
            _client.SendUserText("go");
            _client.RawLineForLog += delegate (string line)
            {
                throw new System.InvalidOperationException("diagnostics tail broke");
            };
            ResultMessage completed = null;
            _client.TurnCompleted += delegate (ResultMessage r) { completed = r; };

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            PumpAll();

            // The raise is isolated INSIDE DispatchLine: the line still
            // reaches ParseLine and closes the turn. Before the fix the
            // exception escaped to the pump's per-line catch and the
            // buffered result line was skipped whole -- turn wedged until
            // the silence backstop.
            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsFalse(_client.TurnActive);
            Assert.IsNotNull(completed,
                "result must be processed even when a RawLineForLog subscriber throws");
            bool isolatedLog = false;
            for (int i = 0; i < _log.Count; i++)
            {
                if (Contains(_log[i], "RawLineForLog subscriber threw"))
                {
                    isolatedLog = true;
                }
            }
            Assert.IsTrue(isolatedLog,
                "the dedicated isolation catch (not the pump's last-resort catch) must log it");

            // The client is fully alive afterwards: a next send opens a
            // new turn as usual.
            _client.SendUserText("again");
            Assert.AreEqual(AgentClientState.Streaming, _client.State);
        }

        [Test]
        public void ThrowingTurnCompletedSubscriber_SeesStateAlreadyReady()
        {
            MakeReady();
            _client.SendUserText("go");
            AgentClientState? observedDuringRaise = null;
            _client.TurnCompleted += delegate (ResultMessage r)
            {
                observedDuringRaise = _client.State;
                throw new System.InvalidOperationException("UI handler broke");
            };

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            PumpAll();

            // State transitions run BEFORE the TurnCompleted raise, so even
            // a throwing subscriber (absorbed by the pump's per-line catch)
            // can never leave the client stuck outside Ready after its turn
            // closed.
            Assert.AreEqual(AgentClientState.Ready, observedDuringRaise);
            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsFalse(_client.TurnActive);
        }

        [Test]
        public void ErrorResult_FromAuthFailure_StillCompletesTurn()
        {
            MakeReady();
            _client.SendUserText("go");
            ResultMessage completed = null;
            _client.TurnCompleted += delegate (ResultMessage r) { completed = r; };

            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"type\":\"result\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsNotNull(completed);
            Assert.IsTrue(completed.IsError);
            Assert.AreEqual("api_error", completed.TerminalReason);
        }

        [Test]
        public void MidTurnSteeringSend_FoldsIntoOpenTurn_OneResultReturnsToReady()
        {
            // CORRECTED (2026-08-03): this test used to assert that two
            // user messages sent before any result requires TWO results to
            // close (modeled on a misreading of R02 4.1, which only ever
            // measured SEQUENTIAL sends). Two live probes against the real
            // CLI (docs/research/02-claude-cli-protocol.md section 4.1;
            // design note 2026-08-03-subagent-ux-and-midturn-input.md
            // section 3) show the opposite: a message written while a turn
            // is still running is STEERING, folded into that turn, and the
            // CLI emits exactly ONE result no matter how many user messages
            // went into it. The old assertion (still busy after the first
            // and only result) encoded the wrong assumption and, if it had
            // driven the production fix, would have left the panel stuck
            // "busy" forever after any mid-turn send -- worse than the bug
            // mid-turn input was meant to fix. This test now asserts the
            // measured behaviour: idle after the single result.
            MakeReady();
            Assert.IsTrue(_client.SendUserText("first"));
            int turnIdAfterFirst = _client.CurrentTurnId;
            Assert.IsTrue(_client.SendUserText("second (mid-turn steering)"));

            // Folded, not queued: still the SAME turn id, and still just
            // one open turn as far as the client is concerned.
            Assert.AreEqual(turnIdAfterFirst, _client.CurrentTurnId);
            Assert.IsTrue(_client.TurnActive);

            string resultLine = FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\"");
            _fake.ScriptLine(resultLine);
            PumpAll();

            // The single result the CLI actually sends closes the whole
            // merged turn: idle, not stuck busy.
            Assert.IsFalse(_client.TurnActive);
            Assert.AreEqual(AgentClientState.Ready, _client.State);
        }

        [Test]
        public void SequentialSends_EachStillOpenAndCloseItsOwnTurn()
        {
            // Guards against the mid-turn fold fix (BeginTurn now folds a
            // send into an already-open, non-interrupted turn) silently
            // changing the ORDINARY sequential case: send only after the
            // previous result has fully closed the turn. This is exactly
            // what R02 4.1 measured originally, and must behave identically
            // to before the correction -- a fresh counted turn every time.
            MakeReady();
            Assert.IsTrue(_client.SendUserText("first"));
            int firstTurnId = _client.CurrentTurnId;
            Assert.IsTrue(_client.TurnActive);

            string resultLine = FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\"");
            _fake.ScriptLine(resultLine);
            PumpAll();
            Assert.IsFalse(_client.TurnActive);
            Assert.AreEqual(AgentClientState.Ready, _client.State);

            Assert.IsTrue(_client.SendUserText("second (sequential)"));
            int secondTurnId = _client.CurrentTurnId;
            Assert.Greater(secondTurnId, firstTurnId);
            Assert.IsTrue(_client.TurnActive);

            _fake.ScriptLine(resultLine);
            PumpAll();
            Assert.IsFalse(_client.TurnActive);
            Assert.AreEqual(AgentClientState.Ready, _client.State);
        }

        [Test]
        public void SendUserText_ReturnsFalse_WhenNotSendable()
        {
            // Starting: refused, no user line written, no turn opened.
            StartClient();
            int linesBefore = _fake.WrittenLines.Count;
            Assert.IsFalse(_client.SendUserText("too early"));
            Assert.AreEqual(linesBefore, _fake.WrittenLines.Count);
            Assert.IsFalse(_client.TurnActive);

            // Ready: accepted.
            _fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"subtype\":\"init\""));
            PumpAll();
            Assert.IsTrue(_client.SendUserText("now it goes"));

            // WaitingPermission: refused again.
            _fake.ScriptLine(FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\""));
            PumpAll();
            Assert.AreEqual(AgentClientState.WaitingPermission, _client.State);
            linesBefore = _fake.WrittenLines.Count;
            Assert.IsFalse(_client.SendUserText("lost while waiting"));
            Assert.AreEqual(linesBefore, _fake.WrittenLines.Count);
        }

        // ------------------------------------------------------------------
        // Interrupt
        // ------------------------------------------------------------------

        [Test]
        public void Interrupt_WritesControlRequest_AndSuppressesResidualDeltas()
        {
            MakeReady();
            _client.SendUserText("go");
            var deltas = new List<string>();
            _client.TextDelta += deltas.Add;

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "\"text_delta\",\"text\":\"I\""));
            PumpAll();
            Assert.AreEqual(1, deltas.Count);

            _client.Interrupt();
            string last = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(last, "\"subtype\":\"interrupt\""));

            // Residual delta after the interrupt is fenced off.
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "'ll run the command."));
            PumpAll();
            Assert.AreEqual(1, deltas.Count);

            // The result still lands and closes the turn.
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            PumpAll();
            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsFalse(_client.TurnActive);
        }

        [Test]
        public void QueuedSendAfterInterrupt_DoesNotUnfenceResidualDeltas()
        {
            MakeReady();
            _client.SendUserText("first");
            var deltas = new List<string>();
            _client.TextDelta += deltas.Add;

            _client.Interrupt();

            // A send queued right after the interrupt must not lift the
            // fence for the interrupted turn's residue.
            Assert.IsTrue(_client.SendUserText("second (queued)"));
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "\"text_delta\",\"text\":\"I\""));
            PumpAll();
            Assert.AreEqual(0, deltas.Count);

            // The interrupted turn's result lifts the fence; the queued
            // turn's deltas then flow normally.
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "'ll run the command."));
            PumpAll();
            Assert.AreEqual(1, deltas.Count);
            Assert.IsTrue(_client.TurnActive);
        }

        // ------------------------------------------------------------------
        // Tool use flow
        // ------------------------------------------------------------------

        [Test]
        public void ToolUse_ThenToolResult_TogglesToolRunning()
        {
            MakeReady();
            _client.SendUserText("go");
            ContentBlock toolUse = null;
            ContentBlock toolResult = null;
            string toolUseParent = "unset";
            string toolResultParent = "unset";
            _client.ToolUseStarted += delegate (ContentBlock b, string parent) { toolUse = b; toolUseParent = parent; };
            _client.ToolResultReceived += delegate (ContentBlock b, string parent) { toolResult = b; toolResultParent = parent; };

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"assistant\"", "\"type\":\"tool_use\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            Assert.IsNotNull(toolUse);
            Assert.AreEqual("Bash", toolUse.Name);
            Assert.AreEqual("toolu_01Wjzti2JaoxPiuKKYHPjAn5", toolUse.Id);
            Assert.IsNull(toolUseParent, "top-level tool_use carries no parent_tool_use_id");

            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "\"type\":\"tool_result\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.Streaming, _client.State);
            Assert.IsNotNull(toolResult);
            Assert.AreEqual("toolu_01Wjzti2JaoxPiuKKYHPjAn5", toolResult.ToolUseId);
            Assert.IsNull(toolResultParent, "top-level tool_result carries no parent_tool_use_id");
            Assert.IsFalse(toolResult.IsError);
        }

        // ------------------------------------------------------------------
        // Permission routing
        // ------------------------------------------------------------------

        [Test]
        public void CanUseTool_RoutesToWaitingPermission_AllowReturnsToToolRunning()
        {
            MakeReady();
            _client.SendUserText("go");
            ControlRequestMessage request = null;
            _client.PermissionRequested += delegate (ControlRequestMessage r) { request = r; };

            _fake.ScriptLine(FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.WaitingPermission, _client.State);
            Assert.IsNotNull(request);
            Assert.IsTrue(request.IsCanUseTool);
            Assert.AreEqual("Write", request.CanUseTool.ToolName);
            Assert.AreEqual(request.RequestId, _client.PendingPermissionRequestId);

            _client.RespondToPermission(request.RequestId,
                PermissionDecision.AllowTool(request.CanUseTool.Input));

            string last = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(last, "\"behavior\":\"allow\""));
            Assert.IsTrue(Contains(last, request.RequestId));
            Assert.IsTrue(Contains(last, "panel_permission_test.txt"));
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            Assert.IsNull(_client.PendingPermissionRequestId);
        }

        // ------------------------------------------------------------------
        // CORE-6: concurrent can_use_tool requests queue FIFO behind the
        // single active one instead of evicting it.
        // ------------------------------------------------------------------

        [Test]
        public void ConcurrentCanUseTool_QueuesSecond_UntilFirstAnswered()
        {
            MakeReady();
            _client.SendUserText("go");
            var raised = new List<ControlRequestMessage>();
            _client.PermissionRequested += raised.Add;

            string template = FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\"");
            _fake.ScriptLine(template);
            PumpAll();
            Assert.AreEqual(1, raised.Count);
            string firstId = raised[0].RequestId;
            string secondId = firstId + "-second";
            _fake.ScriptLine(template.Replace(firstId, secondId));
            PumpAll();

            // The old unconditional overwrite would have evicted the first
            // request here, orphaning it forever.
            Assert.AreEqual(1, raised.Count, "the second concurrent request must queue, not raise yet");
            Assert.AreEqual(firstId, _client.PendingPermissionRequestId);

            _client.RespondToPermission(firstId,
                PermissionDecision.AllowTool(raised[0].CanUseTool.Input));

            Assert.AreEqual(2, raised.Count, "answering the first must surface the queued second");
            Assert.AreEqual(secondId, _client.PendingPermissionRequestId);
            Assert.AreEqual(AgentClientState.WaitingPermission, _client.State);

            _client.RespondToPermission(secondId,
                PermissionDecision.AllowTool(raised[1].CanUseTool.Input));

            Assert.IsNull(_client.PendingPermissionRequestId);
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            string lastLine = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            string prevLine = _fake.WrittenLines[_fake.WrittenLines.Count - 2];
            Assert.IsTrue(Contains(prevLine, firstId) && Contains(prevLine, "\"behavior\":\"allow\""));
            Assert.IsTrue(Contains(lastLine, secondId) && Contains(lastLine, "\"behavior\":\"allow\""));
        }

        [Test]
        public void Result_DiscardsQueuedPermissions_NothingPromotedAfterTheTurn()
        {
            MakeReady();
            _client.SendUserText("go");
            var raised = new List<ControlRequestMessage>();
            _client.PermissionRequested += raised.Add;

            string template = FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\"");
            _fake.ScriptLine(template);
            PumpAll();
            string firstId = raised[0].RequestId;
            _fake.ScriptLine(template.Replace(firstId, firstId + "-queued"));
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsNull(_client.PendingPermissionRequestId,
                "a permission prompt never straddles a result boundary");
            Assert.AreEqual(1, raised.Count,
                "the queued request died with its turn -- promoting it after the "
                + "result would prompt for a tool call that no longer exists");
        }

        [Test]
        public void SynchronousAutoApprove_DrainsTheWholeQueue_WithoutRecursion()
        {
            MakeReady();
            _client.SendUserText("go");
            ControlRequestMessage firstRaised = null;
            _client.PermissionRequested += delegate (ControlRequestMessage r)
            {
                if (firstRaised == null)
                {
                    firstRaised = r;
                }
            };

            string template = FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\"");
            _fake.ScriptLine(template);
            PumpAll();
            string baseId = firstRaised.RequestId;
            _fake.ScriptLine(template.Replace(baseId, baseId + "-q1"));
            _fake.ScriptLine(template.Replace(baseId, baseId + "-q2"));
            PumpAll();

            // Auto-approve answering synchronously from INSIDE the raise --
            // the AgentHub.TryAutoApprove shape. The flat promote loop must
            // drain both queued requests in one pass, no recursion.
            int autoApproved = 0;
            _client.PermissionRequested += delegate (ControlRequestMessage r)
            {
                autoApproved++;
                _client.RespondToPermission(r.RequestId,
                    PermissionDecision.AllowTool(r.CanUseTool.Input));
            };

            _client.RespondToPermission(baseId,
                PermissionDecision.AllowTool(firstRaised.CanUseTool.Input));

            Assert.AreEqual(2, autoApproved, "both queued requests must drain");
            Assert.IsNull(_client.PendingPermissionRequestId);
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
        }

        [Test]
        public void Allow_WithoutEditedInput_EchoesOriginalInputAsUpdatedInput()
        {
            // Every captured allow payload carries updatedInput (02b
            // section 2); a bare AllowTool() must substitute the original
            // request input instead of omitting the field.
            MakeReady();
            _client.SendUserText("go");
            ControlRequestMessage request = null;
            _client.PermissionRequested += delegate (ControlRequestMessage r) { request = r; };

            _fake.ScriptLine(FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\""));
            PumpAll();
            Assert.IsNotNull(request);

            _client.RespondToPermission(request.RequestId, PermissionDecision.AllowTool());

            string last = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(last, "\"behavior\":\"allow\""));
            Assert.IsTrue(Contains(last, "\"updatedInput\""));
            Assert.IsTrue(Contains(last, "panel_permission_test.txt"));
        }

        [Test]
        public void ControlResponseEchoBack_IsIgnoredSilently()
        {
            // 02b section 3: the CLI echoes back every control_response the
            // panel writes (permission allow/deny). The echo must not
            // produce log pollution or state changes.
            MakeReady();
            _client.SendUserText("go");
            ControlRequestMessage request = null;
            _client.PermissionRequested += delegate (ControlRequestMessage r) { request = r; };

            _fake.ScriptLine(FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\""));
            PumpAll();
            Assert.IsNotNull(request);
            _client.RespondToPermission(request.RequestId,
                PermissionDecision.AllowTool(request.CanUseTool.Input));
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);

            _log.Clear();
            _fake.ScriptLine(FixtureLine("permission_inbound.jsonl",
                "\"type\":\"control_response\"", "\"behavior\":\"allow\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            foreach (string entry in _log)
            {
                Assert.IsFalse(Contains(entry, "Unsolicited"),
                    "Echo-back produced log pollution: " + entry);
                Assert.IsFalse(Contains(entry, request.RequestId),
                    "Echo-back produced log pollution: " + entry);
            }
        }

        [Test]
        public void CanUseTool_Deny_ReturnsToStreaming()
        {
            MakeReady();
            _client.SendUserText("go");
            ControlRequestMessage request = null;
            _client.PermissionRequested += delegate (ControlRequestMessage r) { request = r; };

            _fake.ScriptLine(FixtureLine("permission_inbound.jsonl", "\"subtype\":\"can_use_tool\""));
            PumpAll();
            Assert.IsNotNull(request);

            _client.RespondToPermission(request.RequestId,
                PermissionDecision.DenyTool("use the scratchpad instead"));

            string last = _fake.WrittenLines[_fake.WrittenLines.Count - 1];
            Assert.IsTrue(Contains(last, "\"behavior\":\"deny\""));
            Assert.IsTrue(Contains(last, "use the scratchpad instead"));
            Assert.AreEqual(AgentClientState.Streaming, _client.State);
        }

        // ------------------------------------------------------------------
        // Process death
        // ------------------------------------------------------------------

        [Test]
        public void ProcessExit_TransitionsToErrored_AndRaisesProcessDied()
        {
            MakeReady();
            string reason = null;
            _client.ProcessDied += delegate (string r) { reason = r; };

            _fake.SimulateExit();
            _client.Pump();

            Assert.AreEqual(AgentClientState.Errored, _client.State);
            Assert.IsNotNull(reason);
        }

        [Test]
        public void ProcessExit_DeliversBufferedLinesBeforeErroring()
        {
            MakeReady();
            _client.SendUserText("go");
            ResultMessage completed = null;
            _client.TurnCompleted += delegate (ResultMessage r) { completed = r; };

            // The final result often races the exit notification; buffered
            // lines must still be dispatched before the death is surfaced.
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl",
                "\"type\":\"result\"", "\"result\":\"DONE\""));
            _fake.SimulateExit();
            PumpAll();
            _client.Pump();

            Assert.IsNotNull(completed);
            Assert.AreEqual(AgentClientState.Errored, _client.State);
        }

        [Test]
        public void FailedWrite_IsTreatedAsProcessDeath()
        {
            MakeReady();
            string reason = null;
            _client.ProcessDied += delegate (string r) { reason = r; };

            _fake.FailWrites = true;
            _client.SendUserText("this write will fail");

            Assert.AreEqual(AgentClientState.Errored, _client.State);
            Assert.IsNotNull(reason);
        }

        [Test]
        public void Stop_IsCleanShutdown_NotAnError()
        {
            MakeReady();
            _client.SendUserText("go");
            bool died = false;
            _client.ProcessDied += delegate { died = true; };

            _client.Stop();

            Assert.AreEqual(AgentClientState.NotStarted, _client.State);
            Assert.IsTrue(_fake.StopCalled);
            Assert.IsTrue(Contains(_fake.StopInterruptLine, "\"subtype\":\"interrupt\""));

            // A late exit notification after a deliberate stop stays silent.
            _fake.SimulateExit();
            _client.Pump();
            Assert.AreEqual(AgentClientState.NotStarted, _client.State);
            Assert.IsFalse(died);
        }

        // ------------------------------------------------------------------
        // Forward compatibility
        // ------------------------------------------------------------------

        [Test]
        public void UnknownAndSystemStatusLines_AreIgnoredWithoutStateChange()
        {
            MakeReady();

            // system/status and rate_limit_event are both real captured lines
            // the panel does not act on.
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "\"subtype\":\"status\""));
            _fake.ScriptLine(FixtureLine("success_bidi_inbound.jsonl", "\"type\":\"rate_limit_event\""));
            _fake.ScriptLine("not even json {{{");
            int processed = 0;
            while (true)
            {
                int step = _client.Pump(50, 50.0);
                if (step == 0)
                {
                    break;
                }
                processed += step;
            }

            Assert.AreEqual(3, processed);
            Assert.AreEqual(AgentClientState.Ready, _client.State);
        }

        // ------------------------------------------------------------------
        // TurnTracker / PendingRequestMap units (pure logic, no fixtures)
        // ------------------------------------------------------------------

        [Test]
        public void TurnTracker_SilenceBackstop_TripsOnlyWhileActive()
        {
            var turns = new TurnTracker();
            turns.SilenceTimeoutSeconds = 10;

            Assert.IsFalse(turns.IsSilenceExceeded(1000));
            turns.BeginTurn(0);
            Assert.IsFalse(turns.IsSilenceExceeded(9));
            Assert.IsTrue(turns.IsSilenceExceeded(10));
            turns.MarkActivity(10);
            Assert.IsFalse(turns.IsSilenceExceeded(19));
            turns.EndTurn();
            Assert.IsFalse(turns.IsSilenceExceeded(100000));
        }

        [Test]
        public void TurnTracker_TurnIdsIncrease_AndInterruptFlagResets()
        {
            var turns = new TurnTracker();
            int first = turns.BeginTurn(0);
            turns.MarkInterrupted();
            Assert.IsTrue(turns.InterruptRequested);
            turns.EndTurn();
            Assert.IsFalse(turns.InterruptRequested);
            int second = turns.BeginTurn(1);
            Assert.Greater(second, first);
            Assert.IsTrue(turns.AwaitingAck);
            turns.MarkAck();
            Assert.IsFalse(turns.AwaitingAck);
        }

        [Test]
        public void TurnTracker_MidTurnSteeringSend_DoesNotIncrementOpenTurnCount()
        {
            // Corrected accounting (docs/research/02-claude-cli-protocol.md
            // section 4.1): a second BeginTurn while a turn is open and NOT
            // interrupted is STEERING -- it must fold into the running turn
            // (same CurrentTurnId, OpenTurnCount unchanged) rather than
            // counting as a second turn the CLI will never send a second
            // result for.
            var turns = new TurnTracker();
            int first = turns.BeginTurn(0);
            Assert.AreEqual(1, turns.OpenTurnCount);

            int second = turns.BeginTurn(1);
            Assert.AreEqual(first, second);
            Assert.AreEqual(1, turns.OpenTurnCount);
            Assert.IsTrue(turns.TurnActive);

            // One EndTurn -- matching the one result the CLI actually sends
            // for the merged turn -- closes it completely.
            turns.EndTurn();
            Assert.AreEqual(0, turns.OpenTurnCount);
            Assert.IsFalse(turns.TurnActive);
        }

        [Test]
        public void TurnTracker_SendAfterInterrupt_StillOpensAGenuineSecondTurn()
        {
            // Distinguishes the fold above from the pre-existing, still
            // correct case: a send written after Interrupt but before the
            // interrupted turn's own result arrives is NOT steering -- the
            // interrupted turn is being torn down and still owes its own
            // result, so this really is a second turn and needs its own
            // EndTurn. This behaviour predates and is unchanged by the
            // mid-turn fold correction.
            var turns = new TurnTracker();
            int first = turns.BeginTurn(0);
            turns.MarkInterrupted();

            int second = turns.BeginTurn(1);
            Assert.Greater(second, first);
            Assert.AreEqual(2, turns.OpenTurnCount);

            // First EndTurn: the interrupted turn's own result. Still busy
            // (the second, genuinely distinct turn has not resulted yet),
            // and the fence lifts.
            turns.EndTurn();
            Assert.IsTrue(turns.TurnActive);
            Assert.IsFalse(turns.InterruptRequested);

            // Second EndTurn: the second turn's result. Now idle.
            turns.EndTurn();
            Assert.IsFalse(turns.TurnActive);
        }

        [Test]
        public void PendingRequestMap_TimesOutAndResolvesIndependently()
        {
            var map = new PendingRequestMap();
            ControlResponseMessage resolved = null;
            bool timedOut = false;

            string id1 = map.NextRequestId("req");
            string id2 = map.NextRequestId("int");
            Assert.AreNotEqual(id1, id2);

            map.Track(id1, "initialize", 0, 5,
                delegate (ControlResponseMessage r) { resolved = r; });
            map.Track(id2, "interrupt", 0, 100,
                delegate (ControlResponseMessage r) { timedOut = r == null; });

            var expired = map.CollectTimedOut(6);
            Assert.AreEqual(1, expired.Count);
            Assert.AreEqual(id1, expired[0].RequestId);
            Assert.IsNull(resolved);

            // Craft a response for id2 through the real parser.
            string line = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + id2 + "\","
                + "\"response\":{\"still_queued\":[]}}}";
            var msg = (ControlResponseMessage)StreamJsonMessage.ParseLine(line, null);
            var request = map.TryResolve(msg);
            Assert.IsNotNull(request);
            Assert.AreEqual("interrupt", request.Kind);
            Assert.IsFalse(timedOut);
            Assert.AreEqual(0, map.Count);

            // Duplicate response resolves nothing.
            Assert.IsNull(map.TryResolve(msg));
        }
    }
}
