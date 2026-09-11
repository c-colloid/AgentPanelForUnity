using Colloid.AgentPanel.Core.Client;
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
    }
}
