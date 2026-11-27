using System;
using Colloid.AgentPanel.Core.Client;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// AgentClient.Attach / Detach (CLI relay, design note docs/design-
    /// notes/2026-11-25-cli-relay.md): a client that picks up a CLI which
    /// is already running writes nothing, rebuilds its state from the
    /// lines the relay replays, and a client that lets go of the CLI
    /// neither interrupts nor stops it.
    /// </summary>
    [TestFixture]
    public class AgentClientAttachTests
    {
        private const string InitLine =
            "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\"}";
        private const string InitializeResponse =
            "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"req_1\","
            + "\"response\":{\"commands\":[],\"models\":[],\"account\":{}}}}";
        private const string ToolUseLine =
            "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]},"
            + "\"parent_tool_use_id\":null,\"session_id\":\"s1\"}";
        private const string ResultLine =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"s1\","
            + "\"result\":\"done\",\"num_turns\":1,\"duration_ms\":1,\"total_cost_usd\":0}";

        private FakeCliProcess _fake;
        private AgentClient _client;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private void Attach(int openTurns)
        {
            _client.Attach(new AgentClientAttachOptions
            {
                InitializeRequestId = "req_1",
                TurnId = 7,
                OpenTurns = openTurns,
                RequestCounterSeed = 10000
            });
        }

        [Test]
        public void Attach_WritesNothing_AndDoesNotStartTheTransport()
        {
            Attach(1);

            Assert.AreEqual(0, _fake.StartCallCount, "the CLI is already running: nothing to spawn");
            Assert.AreEqual(0, _fake.WrittenLines.Count, "the handshake was done by the previous client");
            Assert.AreEqual(AgentClientState.Starting, _client.State);
            Assert.AreEqual("req_1", _client.InitializeRequestId);
        }

        [Test]
        public void Attach_MidTurn_ReplayedHandshake_LeadsToStreaming_InTheSameTurn()
        {
            Attach(1);
            _fake.ScriptLine(InitLine);
            _fake.ScriptLine(InitializeResponse);
            PumpAll();

            Assert.AreEqual(AgentClientState.Streaming, _client.State, "a turn is open: not Ready");
            Assert.IsTrue(_client.TurnActive);
            Assert.AreEqual(7, _client.CurrentTurnId);
            Assert.AreEqual("s1", _client.SessionId);
            Assert.IsNotNull(_client.InitializeResponse, "the replayed handshake response must be adopted");
        }

        [Test]
        public void Attach_Idle_ReplayedHandshake_LeadsToReady()
        {
            Attach(0);
            _fake.ScriptLine(InitLine);
            _fake.ScriptLine(InitializeResponse);
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsFalse(_client.TurnActive);
        }

        [Test]
        public void Attach_MidTurn_ReplayedLinesRunTheTurnToItsEnd()
        {
            int completed = 0;
            _client.TurnCompleted += delegate { completed++; };
            Attach(1);
            _fake.ScriptLine(InitLine);
            _fake.ScriptLine(InitializeResponse);
            _fake.ScriptLine(ToolUseLine);
            PumpAll();
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);

            _fake.ScriptLine(ResultLine);
            PumpAll();
            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.AreEqual(1, completed);
            Assert.IsFalse(_client.TurnActive);
        }

        [Test]
        public void SeedCounter_MakesNewRequestIdsStartAboveTheSeed()
        {
            var requests = new PendingRequestMap();
            requests.SeedCounter(10000);

            Assert.AreEqual("int_10001", requests.NextRequestId("int"),
                "ids must not repeat the ones the previous client used");
        }

        [Test]
        public void Detach_OnAPlainTransport_IsAnOrdinaryStop()
        {
            _client.Start(new AgentClientOptions { CliPath = "C:/fake/claude.exe", WorkingDirectory = "C:/fake" });

            Assert.IsFalse(_client.Detach(100), "a transport that cannot detach is stopped instead");
            Assert.IsTrue(_fake.StopCalled);
            Assert.AreEqual(AgentClientState.NotStarted, _client.State);
        }

        [Test]
        public void Detach_BeforeAnythingStarted_DoesNotThrow()
        {
            Assert.DoesNotThrow(delegate { _client.Detach(100); });
            Assert.AreEqual(AgentClientState.NotStarted, _client.State);
        }
    }
}
