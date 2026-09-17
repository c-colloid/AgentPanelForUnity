using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guard, updated for the v0.9.0 default/in-use split (docs/
    /// design-notes/2026-08-01-model-settings-rework.md section 4.1):
    /// AgentHub.SwitchModel was replaced by AgentHub.SetDefaultModel
    /// (persist-only, never touches the live client -- see its doc
    /// comment) and AgentHub.SwitchSessionModel (live-only, never
    /// persists). This file originally guarded a defect where SwitchModel
    /// used to `return` immediately on an empty/null model, before
    /// touching PanelSettings.model at all -- that same guard now applies
    /// to SetDefaultModel, the Settings "Default model" dropdown's sole
    /// entry point.
    ///
    /// No client is wired for SetDefaultModel's tests (AgentHub's private
    /// static `_client` field stays null, exactly as it does at the start
    /// of any editor session before a connection is made), which is fine
    /// since SetDefaultModel never touches the client anyway.
    /// SwitchSessionModel's tests are split into its genuine no-op paths
    /// (no client / empty model, below) and its actual live-switch path,
    /// which requires a connected client and therefore uses
    /// AgentHub.SetClientForTests (see that method's doc comment for why
    /// WireClientForTests alone is not enough: it only subscribes event
    /// handlers, never assigns AgentHub's own `_client`, so
    /// SwitchSessionModel's `_client == null` guard would always short-
    /// circuit the switch before ever reaching AgentClient.SetModel).
    /// </summary>
    [TestFixture]
    public class AgentHubSwitchModelTests
    {
        private string _originalModel;

        [SetUp]
        public void SetUp()
        {
            _originalModel = PanelStateStore.instance.Settings.model;
        }

        [TearDown]
        public void TearDown()
        {
            PanelStateStore.instance.Settings.model = _originalModel;
            PanelStateStore.instance.SaveNow();
            AgentHub.SetClientForTests(null);
            AgentHub.ResetPendingSessionModelForTests();
        }

        // -- Helpers (2026-09-17 note: the handshake gate) -----------------------

        private static void PumpAll(AgentClient client)
        {
            while (client.Pump(50, 50.0) > 0)
            {
            }
        }

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

        /// <summary>Feeds the captured initialize control_response (models[] list) so InitializeResponse is set.</summary>
        private static void CompleteHandshake(FakeCliProcess fake, AgentClient client)
        {
            fake.ScriptLine(FixtureLine("out_bidi.jsonl", "\"request_id\":\"req_1\""));
            PumpAll(client);
            Assert.IsNotNull(client.InitializeResponse, "fixture must resolve the initialize request");
        }

        private static int CountSetModelLines(FakeCliProcess fake, string model)
        {
            int n = 0;
            foreach (string line in fake.WrittenLines)
            {
                if (line.Contains("\"set_model\"") && line.Contains("\"" + model + "\""))
                {
                    n++;
                }
            }
            return n;
        }

        private static string LastRequestId(FakeCliProcess fake)
        {
            return JsonParser.Parse(fake.WrittenLines[fake.WrittenLines.Count - 1])["request_id"].AsString();
        }

        private static ChatMessageBlock LastSystemNote()
        {
            var messages = AgentHub.Session.messages;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                for (int b = messages[i].blocks.Count - 1; b >= 0; b--)
                {
                    if (messages[i].blocks[b].kind == ChatBlockKind.SystemNote)
                    {
                        return messages[i].blocks[b];
                    }
                }
            }
            return null;
        }

        [Test]
        public void SetDefaultModel_EmptyString_PersistsTheDefaultSentinel()
        {
            PanelStateStore.instance.Settings.model = "haiku";
            PanelStateStore.instance.SaveNow();

            AgentHub.SetDefaultModel(string.Empty);

            Assert.AreEqual(string.Empty, PanelStateStore.instance.Settings.model,
                "Selecting the \"(Default)\" sentinel must persist as an empty "
                + "model value, not silently no-op and leave the previous "
                + "explicit model (\"haiku\") in place.");
        }

        [Test]
        public void SetDefaultModel_Null_NormalizesToEmptyStringAndPersists()
        {
            PanelStateStore.instance.Settings.model = "sonnet";
            PanelStateStore.instance.SaveNow();

            AgentHub.SetDefaultModel(null);

            Assert.AreEqual(string.Empty, PanelStateStore.instance.Settings.model,
                "A null model (same sentinel value as empty string from the "
                + "dropdown's perspective) must also persist, never throw and "
                + "never leave the previous explicit model in place.");
        }

        [Test]
        public void SetDefaultModel_ExplicitValue_StillPersistsAsBefore()
        {
            PanelStateStore.instance.Settings.model = string.Empty;
            PanelStateStore.instance.SaveNow();

            AgentHub.SetDefaultModel("opus");

            Assert.AreEqual("opus", PanelStateStore.instance.Settings.model,
                "Switching the default to an explicit alias must keep working "
                + "exactly as SwitchModel's persistence half did before the split.");
        }

        // -- SwitchSessionModel (docs/design-notes/2026-08-01-model-settings-
        // rework.md section 4.1): the header picker's live-only entry point.
        // Must NEVER write PanelSettings.model. --------------------------------

        [Test]
        public void SwitchSessionModel_NoClientWired_NeverPersistsPanelSettingsModel()
        {
            PanelStateStore.instance.Settings.model = "sonnet";
            PanelStateStore.instance.SaveNow();

            // No client is wired in this test fixture, so this exercises
            // the no-op-without-a-connected-client branch only -- see
            // SwitchSessionModel_LiveClient_CallsSetModelButNeverPersists
            // below for the actual live-switch branch.
            AgentHub.SwitchSessionModel("haiku");

            Assert.AreEqual("sonnet", PanelStateStore.instance.Settings.model,
                "SwitchSessionModel must never write PanelSettings.model -- "
                + "that is SetDefaultModel's sole job. A live session switch "
                + "must not silently rewrite what future NEW sessions start with.");
        }

        [Test]
        public void SwitchSessionModel_EmptyOrNull_NoOp_DoesNotThrow()
        {
            Assert.DoesNotThrow(delegate { AgentHub.SwitchSessionModel(string.Empty); });
            Assert.DoesNotThrow(delegate { AgentHub.SwitchSessionModel(null); });
        }

        /// <summary>
        /// Guards the actual live-switch branch (AgentHubSwitchModelTests
        /// review finding, 2026-08-01): the two tests above only ever hit
        /// SwitchSessionModel's `_client == null` early-return, which can
        /// never violate the "never persists" invariant regardless of
        /// correctness (there is no code path from that branch to
        /// PanelSettings.model either way). Wires a real AgentClient
        /// (FakeCliProcess-backed, no process spawn) via
        /// AgentHub.SetClientForTests, starts it (State becomes Starting,
        /// which is neither NotStarted nor Errored -- the only two states
        /// SwitchSessionModel bails out on), then asserts BOTH that the
        /// live client actually received the set_model control_request
        /// (proving this test exercises the real branch, not another
        /// no-op) AND that PanelSettings.model still never gets written.
        /// </summary>
        [Test]
        public void SwitchSessionModel_LiveClient_CallsSetModelButNeverPersists()
        {
            PanelStateStore.instance.Settings.model = "sonnet";
            PanelStateStore.instance.SaveNow();

            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = "C:/fake/claude.exe",
                    WorkingDirectory = "C:/fake/project"
                });
                // 2026-09-17: the live branch only writes once the
                // initialize handshake has answered (see the held-switch
                // tests below); complete it first.
                CompleteHandshake(fake, client);
                AgentHub.SetClientForTests(client);

                AgentHub.SwitchSessionModel("haiku");

                bool sawSetModel = false;
                foreach (string line in fake.WrittenLines)
                {
                    if (line.Contains("\"set_model\"") && line.Contains("\"haiku\""))
                    {
                        sawSetModel = true;
                        break;
                    }
                }
                Assert.IsTrue(sawSetModel,
                    "SwitchSessionModel with a connected client must write a "
                    + "set_model control_request to the transport -- otherwise "
                    + "this test is not actually exercising the live-switch "
                    + "branch (see AgentHub.SwitchSessionModel's `_client == "
                    + "null` guard).");
                Assert.AreEqual("sonnet", PanelStateStore.instance.Settings.model,
                    "Even when a live switch actually happens, "
                    + "PanelSettings.model (the default for NEW sessions) "
                    + "must stay untouched -- that is SetDefaultModel's sole job.");
            }
        }

        // ------------------------------------------------------------------
        // docs/design-notes/2026-09-17-model-switch-before-init.md: a pick
        // made in the seconds after "+" (or a reconnect), before the
        // initialize handshake answers, used to race the handshake and be
        // dropped without a trace. It is now held and sent when the
        // handshake resolves, and every outcome writes a transcript note.
        // ------------------------------------------------------------------

        [Test]
        public void SwitchSessionModel_BeforeHandshake_IsHeld_AndSentWhenInitializeResolves()
        {
            AgentHub.ResetForTests();
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = "C:/fake/claude.exe",
                    WorkingDirectory = "C:/fake/project"
                });
                AgentHub.SetClientForTests(client);
                AgentHub.WireControlRequestResolvedForTests(client);

                AgentHub.SwitchSessionModel("fable");

                Assert.AreEqual(0, CountSetModelLines(fake, "fable"),
                    "nothing may be written before the handshake answers");
                Assert.AreEqual("fable", AgentHub.PendingSessionModel);
                ChatMessageBlock queued = LastSystemNote();
                Assert.IsNotNull(queued, "the hold is announced in the transcript");
                StringAssert.Contains("fable", queued.text);

                CompleteHandshake(fake, client);

                Assert.AreEqual(1, CountSetModelLines(fake, "fable"),
                    "the held switch is sent exactly once when initialize resolves");
                Assert.IsNull(AgentHub.PendingSessionModel);

                string requestId = LastRequestId(fake);
                fake.ScriptLine("{\"type\":\"control_response\",\"response\":"
                    + "{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\","
                    + "\"response\":{}}}");
                PumpAll(client);

                ChatMessageBlock done = LastSystemNote();
                Assert.IsNotNull(done);
                Assert.AreNotSame(queued, done, "success writes its own confirmation note");
                StringAssert.Contains("fable", done.text);
                Assert.IsFalse(done.warning, "a successful switch is not a warning");
            }
        }

        [Test]
        public void SetModelFailure_AppendsAWarningNote_AndKeepsTheOldModel()
        {
            AgentHub.ResetForTests();
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = "C:/fake/claude.exe",
                    WorkingDirectory = "C:/fake/project"
                });
                CompleteHandshake(fake, client);
                AgentHub.SetClientForTests(client);
                AgentHub.WireControlRequestResolvedForTests(client);
                string before = client.CurrentModel;

                AgentHub.SwitchSessionModel("haiku");
                string requestId = LastRequestId(fake);
                fake.ScriptLine("{\"type\":\"control_response\",\"response\":"
                    + "{\"subtype\":\"error\",\"request_id\":\"" + requestId + "\","
                    + "\"error\":\"model not available\"}}");
                PumpAll(client);

                ChatMessageBlock note = LastSystemNote();
                Assert.IsNotNull(note, "a failed switch must say so in the transcript");
                Assert.IsTrue(note.warning);
                StringAssert.Contains("haiku", note.text);
                StringAssert.Contains("model not available", note.text);
                Assert.AreEqual(before, client.CurrentModel);
            }
        }

        [Test]
        public void HeldSwitch_DiesWithTheClient()
        {
            AgentHub.ResetForTests();
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = "C:/fake/claude.exe",
                    WorkingDirectory = "C:/fake/project"
                });
                AgentHub.SetClientForTests(client);
                AgentHub.SwitchSessionModel("fable");
                Assert.AreEqual("fable", AgentHub.PendingSessionModel);

                AgentHub.TearDownClientForTests();

                Assert.IsNull(AgentHub.PendingSessionModel,
                    "a switch held for a handshake that will never finish must not leak into the next spawn");
            }
        }
    }
}
