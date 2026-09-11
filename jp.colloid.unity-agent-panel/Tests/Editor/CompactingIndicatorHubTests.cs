using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The "compacting" indicator (design note
    /// docs/design-notes/2026-09-10-compacting-indicator.md) through a real
    /// AgentClient into AgentHub's own handlers (the CompactionHubTests
    /// pattern): system/status "compacting" turns AgentHub.IsCompacting on,
    /// the compact_boundary / the turn's result / a stall / an explicit
    /// null status turn it off ("requesting" does NOT), and the panel's own
    /// "/compact" send turns it on before the CLI says anything. Plus
    /// StatusBarView's pure gate.
    /// </summary>
    public class CompactingIndicatorHubTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;

        [SetUp]
        public void SetUp()
        {
            AgentHub.ResetForTests();
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
            AgentHub.WireClientForTests(_client);
            AgentHub.WireTurnStalledForTests(_client);
            AgentHub.SetClientForTests(_client);
        }

        [TearDown]
        public void TearDown()
        {
            AgentHub.SetClientForTests(null);
            _client.Dispose();
            AgentHub.ResetForTests();
        }

        private void StartReadyClient()
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            _fake.ScriptLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\"}");
            PumpAll();
        }

        private void Replay(params string[] lines)
        {
            _fake.ScriptLines(lines);
            PumpAll();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private static string StatusLine(string status)
        {
            string value = status == null ? "null" : "\"" + status + "\"";
            return "{\"type\":\"system\",\"subtype\":\"status\",\"status\":" + value + ",\"session_id\":\"s1\"}";
        }

        private static string BoundaryLine(string trigger)
        {
            return "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"session_id\":\"s1\","
                + "\"compact_metadata\":{\"trigger\":\"" + trigger + "\",\"pre_tokens\":84213}}";
        }

        private const string ResultLine =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"s1\","
            + "\"result\":\"ok\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,"
            + "\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}";

        // -- The wire signal ----------------------------------------------------

        [Test]
        public void StatusCompacting_TurnsTheIndicatorOn_AndRaisesChanged()
        {
            StartReadyClient();
            Assert.IsFalse(AgentHub.IsCompacting, "precondition");
            int changed = 0;
            System.Action onChanged = delegate { changed++; };
            AgentHub.Changed += onChanged;
            try
            {
                Replay(StatusLine("compacting"));
                Assert.IsTrue(AgentHub.IsCompacting);
                Assert.AreEqual(1, changed, "the view must be told to redraw");

                // "requesting" fires before EVERY API call; a repeat of the
                // current state must not cost a redraw each time.
                Replay(StatusLine("compacting"));
                Assert.AreEqual(1, changed, "no change, no redraw");
            }
            finally
            {
                AgentHub.Changed -= onChanged;
            }
        }

        [Test]
        public void CompactBoundary_EndsTheIndicator()
        {
            StartReadyClient();
            Replay(StatusLine("compacting"));
            Assert.IsTrue(AgentHub.IsCompacting, "precondition");

            Replay(BoundaryLine("auto"));
            Assert.IsFalse(AgentHub.IsCompacting, "the boundary is the end of the compaction");
            Assert.IsTrue(AgentHub.ContextUnknownAfterCompaction,
                "the boundary's own bookkeeping is untouched");
        }

        [Test]
        public void NullStatus_EndsTheIndicator()
        {
            StartReadyClient();
            Replay(StatusLine("compacting"));
            Replay(StatusLine(null));
            Assert.IsFalse(AgentHub.IsCompacting, "an explicit status clear ends it");
        }

        [Test]
        public void RequestingStatus_DoesNotEndTheIndicator()
        {
            // "requesting" precedes every API call -- plausibly the
            // summarization call that IS the compaction. Letting it clear
            // the flag would blank the indicator during the very wait it
            // exists for. Both orders must survive.
            StartReadyClient();
            Replay(StatusLine("compacting"), StatusLine("requesting"));
            Assert.IsTrue(AgentHub.IsCompacting, "'requesting' after 'compacting' is the summarization call");
        }

        [Test]
        public void RequestingStatus_DoesNotEndTheSendSideHint()
        {
            StartReadyClient();
            Assert.IsTrue(AgentHub.SendUserMessage("/compact", "/compact", null), "precondition");
            Replay(StatusLine("requesting"));
            Assert.IsTrue(AgentHub.IsCompacting, "the send-side hint must survive the CLI's pre-call status");
            Replay(BoundaryLine("manual"));
            Assert.IsFalse(AgentHub.IsCompacting);
        }

        [Test]
        public void TurnResult_EndsTheIndicator_EvenWithoutABoundary()
        {
            StartReadyClient();
            Assert.IsTrue(AgentHub.SendUserMessage("hello", "hello", null), "precondition");
            Replay(StatusLine("compacting"));
            Assert.IsTrue(AgentHub.IsCompacting, "precondition");

            Replay(ResultLine);
            Assert.IsFalse(AgentHub.IsCompacting, "the turn is over; nothing can still be compacting");
        }

        [Test]
        public void StalledTurn_EndsTheIndicator()
        {
            StartReadyClient();
            Assert.IsTrue(AgentHub.SendUserMessage("hello", "hello", null), "precondition");
            Replay(StatusLine("compacting"));
            Assert.IsTrue(AgentHub.IsCompacting, "precondition");

            _client.SilenceTimeoutSeconds = 0.001;
            System.Threading.Thread.Sleep(15);
            _client.Pump();
            Assert.IsFalse(_client.TurnActive, "precondition: the backstop closed the turn");
            Assert.IsFalse(AgentHub.IsCompacting, "a force-closed turn must not leave the spinner up");
        }

        // -- The panel's own /compact ------------------------------------------

        [Test]
        public void SendingCompact_TurnsTheIndicatorOn_BeforeTheCliSaysAnything()
        {
            StartReadyClient();
            Assert.IsTrue(AgentHub.SendUserMessage("/compact", "/compact", null), "precondition");
            Assert.IsTrue(AgentHub.IsCompacting,
                "the user is waiting on a compaction from the moment the command is sent");

            // The CLI's own result closes it (a manual /compact turn IS the compaction).
            Replay(BoundaryLine("manual"), ResultLine);
            Assert.IsFalse(AgentHub.IsCompacting);
        }

        [Test]
        public void SendingCompactWithArgs_AlsoCounts_ButProseDoesNot()
        {
            StartReadyClient();
            Assert.IsTrue(AgentHub.SendUserMessage("/compact keep the file list", "/compact keep the file list", null));
            Assert.IsTrue(AgentHub.IsCompacting);
            Replay(ResultLine);
            Assert.IsFalse(AgentHub.IsCompacting);

            Assert.IsTrue(AgentHub.SendUserMessage("please /compact this", "please /compact this", null));
            Assert.IsFalse(AgentHub.IsCompacting, "a slash in prose is not a command (SlashCommandCatalog grammar)");
            Replay(ResultLine);

            Assert.IsTrue(AgentHub.SendUserMessage("/compactor design", "/compactor design", null));
            Assert.IsFalse(AgentHub.IsCompacting, "a different command sharing the prefix is not /compact");
        }

        // -- Lifecycle resets -----------------------------------------------------

        [Test]
        public void ResetForTests_ClearsTheIndicator()
        {
            StartReadyClient();
            Replay(StatusLine("compacting"));
            Assert.IsTrue(AgentHub.IsCompacting, "precondition");
            AgentHub.ResetForTests();
            Assert.IsFalse(AgentHub.IsCompacting);
        }

        // -- StatusBarView's pure gate ------------------------------------------

        [Test]
        public void StatusBar_ShowsCompacting_OnlyInsideATurn()
        {
            Assert.IsTrue(StatusBarView.ShowCompactingState(true, AgentClientState.Streaming));
            Assert.IsTrue(StatusBarView.ShowCompactingState(true, AgentClientState.ToolRunning));
            Assert.IsFalse(StatusBarView.ShowCompactingState(true, AgentClientState.Ready));
            Assert.IsFalse(StatusBarView.ShowCompactingState(true, AgentClientState.WaitingPermission));
            Assert.IsFalse(StatusBarView.ShowCompactingState(true, AgentClientState.Errored));
            Assert.IsFalse(StatusBarView.ShowCompactingState(true, AgentClientState.Starting));
            Assert.IsFalse(StatusBarView.ShowCompactingState(false, AgentClientState.Streaming));
        }
    }
}
