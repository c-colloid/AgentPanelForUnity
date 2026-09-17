using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// HeaderView.ParseModels against the REAL initialize control_response
    /// captured in Tests/Editor/Fixtures/out_bidi.jsonl (R02 section 5.1;
    /// shape independently re-verified this round -- see
    /// docs/design-notes/2026-07-31-history-restore-flow-and-model-picker.md
    /// section 3).
    /// </summary>
    public class HeaderViewLogicTests
    {
        private static JsonNode LoadModelsNodeFromFixture()
        {
            List<string> lines = FixtureLoader.ReadLines("out_bidi.jsonl");
            for (int i = 0; i < lines.Count; i++)
            {
                var message = StreamJsonMessage.ParseLine(lines[i], null) as ControlResponseMessage;
                if (message == null)
                {
                    continue;
                }
                JsonNode models = message.Response["models"];
                if (models.IsArray)
                {
                    return models;
                }
            }
            Assert.Fail("out_bidi.jsonl has no control_response with a models[] array");
            return null;
        }

        [Test]
        public void ParseModels_NullNode_ReturnsEmptyList_NeverThrows()
        {
            Assert.IsNotNull(HeaderView.ParseModels(null));
            Assert.AreEqual(0, HeaderView.ParseModels(null).Count);
        }

        [Test]
        public void ParseModels_NonArrayNode_ReturnsEmptyList()
        {
            Assert.AreEqual(0, HeaderView.ParseModels(JsonNode.NewObject()).Count);
            Assert.AreEqual(0, HeaderView.ParseModels(JsonNode.Null).Count);
        }

        [Test]
        public void ParseModels_EntryMissingValue_IsSkipped()
        {
            var array = JsonNode.NewArray();
            array.Add(JsonNode.NewObject().Set("displayName", "No value field"));
            array.Add(JsonNode.NewObject().Set("value", "sonnet").Set("displayName", "Sonnet"));
            List<HeaderView.ModelOption> options = HeaderView.ParseModels(array);
            Assert.AreEqual(1, options.Count);
            Assert.AreEqual("sonnet", options[0].Value);
        }

        [Test]
        public void ParseModels_DisplayNameMissing_FallsBackToValue()
        {
            var array = JsonNode.NewArray();
            array.Add(JsonNode.NewObject().Set("value", "haiku"));
            List<HeaderView.ModelOption> options = HeaderView.ParseModels(array);
            Assert.AreEqual(1, options.Count);
            Assert.AreEqual("haiku", options[0].DisplayName);
        }

        // -- Real fixture (out_bidi.jsonl) -----------------------------------------------

        [Test]
        public void ParseModels_RealFixture_ReturnsFiveModelsInOrder()
        {
            List<HeaderView.ModelOption> options = HeaderView.ParseModels(LoadModelsNodeFromFixture());
            Assert.AreEqual(5, options.Count);
            CollectionAssert.AreEqual(
                new[] { "default", "opus[1m]", "claude-fable-5[1m]", "sonnet", "haiku" },
                options.ConvertAll(o => o.Value));
        }

        [Test]
        public void ParseModels_RealFixture_ResolvedModelAndDisplayNameMatchCapturedData()
        {
            List<HeaderView.ModelOption> options = HeaderView.ParseModels(LoadModelsNodeFromFixture());

            HeaderView.ModelOption sonnet = options.Find(o => o.Value == "sonnet");
            Assert.IsNotNull(sonnet);
            Assert.AreEqual("claude-sonnet-5", sonnet.ResolvedModel);
            Assert.AreEqual("Sonnet", sonnet.DisplayName);
            StringAssert.Contains("$3/$15 per Mtok", sonnet.Description);

            HeaderView.ModelOption haiku = options.Find(o => o.Value == "haiku");
            Assert.IsNotNull(haiku);
            Assert.AreEqual("claude-haiku-4-5-20251001", haiku.ResolvedModel);
            Assert.AreEqual("Haiku", haiku.DisplayName);

            HeaderView.ModelOption defaultOption = options.Find(o => o.Value == "default");
            Assert.IsNotNull(defaultOption);
            Assert.AreEqual("claude-opus-4-8[1m]", defaultOption.ResolvedModel);
            Assert.AreEqual("Default (recommended)", defaultOption.DisplayName);
        }

        // -- ApplyPanelDefaultSuffix (v0.11.0, docs/design-notes/2026-08-02-
        // subagent-model-precedence.md section 3.1, work item C): the model
        // menu's " (default)" suffix decision, independent of the isCurrent
        // checkmark decided inline in OnModelPickerClicked. --------------

        [Test]
        public void ApplyPanelDefaultSuffix_ValueMatchesPanelDefault_AppendsSuffix()
        {
            string result = HeaderView.ApplyPanelDefaultSuffix("Sonnet", "sonnet", "sonnet");
            Assert.AreEqual(
                Colloid.AgentPanel.UI.L10n.F(Colloid.AgentPanel.UI.L10n.S.HeaderModelOptionDefaultSuffixFmt, "Sonnet"),
                result);
        }

        [Test]
        public void ApplyPanelDefaultSuffix_ValueDoesNotMatchPanelDefault_ReturnsLabelUnchanged()
        {
            Assert.AreEqual("Haiku", HeaderView.ApplyPanelDefaultSuffix("Haiku", "haiku", "sonnet"));
        }

        [Test]
        public void ApplyPanelDefaultSuffix_PanelDefaultEmpty_NeverAppendsSuffix()
        {
            // No explicit default persisted (settings.model == "") never
            // matches any real catalog value (ParseModels always skips
            // empty "value" entries), so nothing gets the suffix.
            Assert.AreEqual("Sonnet", HeaderView.ApplyPanelDefaultSuffix("Sonnet", "sonnet", string.Empty));
            Assert.AreEqual("Sonnet", HeaderView.ApplyPanelDefaultSuffix("Sonnet", "sonnet", null));
        }

        [Test]
        public void ApplyPanelDefaultSuffix_IsIndependentOfCurrentSessionState()
        {
            // The suffix is decided purely from optionValue == panel
            // default -- it takes no isCurrent/session parameter at all,
            // so an option can carry it while a DIFFERENT option is the
            // one actually running (or before any session exists).
            string result = HeaderView.ApplyPanelDefaultSuffix("Opus", "opus", "opus");
            StringAssert.Contains("Opus", result);
            Assert.AreNotEqual("Opus", result);
        }
        // -- UICODE-10: GetModels memoized by InitializeResponse reference --

        private static AgentClient MakeInitializedClient(out FakeCliProcess fake)
        {
            fake = new FakeCliProcess();
            var client = new AgentClient(fake);
            client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            // The models catalog rides on the INITIALIZE CONTROL RESPONSE
            // (request_id req_1 -- the first request a fresh client sends),
            // not on the system/init line: scripting "subtype":"init" here
            // left InitializeResponse null and GetModels empty (measured in
            // CI). Same needle AgentClientStateTests uses to reach Ready.
            foreach (string line in FixtureLoader.ReadLines("out_bidi.jsonl"))
            {
                if (line.IndexOf("\"request_id\":\"req_1\"", System.StringComparison.Ordinal) >= 0)
                {
                    fake.ScriptLine(line);
                    break;
                }
            }
            while (client.Pump(50, 50.0) > 0)
            {
            }
            return client;
        }

        /// <summary>
        /// UICODE-10: RefreshModelPicker runs on every AgentHub.Changed
        /// (per streaming delta at the hottest) and used to re-run
        /// ParseModels -- a fresh List + option objects each time -- just
        /// to answer Count&gt;0. GetModels memoizes on the
        /// InitializeResponse REFERENCE, which is exact: the models node is
        /// immutable per response instance and a reconnect replaces the
        /// whole instance.
        /// </summary>
        [Test]
        public void GetModels_SameInitializeResponse_ReturnsTheSameListInstance()
        {
            FakeCliProcess fake;
            AgentClient client = MakeInitializedClient(out fake);
            try
            {
                var header = new HeaderView();
                List<HeaderView.ModelOption> first = header.GetModels(client);
                List<HeaderView.ModelOption> second = header.GetModels(client);
                Assert.Greater(first.Count, 0, "the init fixture carries models");
                Assert.AreSame(first, second, "same response reference -> no re-parse, no allocation");
            }
            finally
            {
                client.Dispose();
            }
        }

        // -- 2026-09-06 review fix 2: narrow-dock compact chip -------------------

        [Test]
        public void Header_BuildsTheCompactChip_NextToTheTwoItReplaces()
        {
            // USS decides which of the three is visible (uap-narrow on the
            // root); the view only guarantees all three exist, that the
            // compact chip carries the model name like the picker does, and
            // that it explains itself.
            var header = new HeaderView();

            Button compact = header.Root.Q<Button>(className: "uap-header-compact-btn");
            Button picker = header.Root.Q<Button>(className: "uap-header-model-btn");
            Button autoApprove = header.Root.Q<Button>(className: "uap-header-autoapprove-btn");
            Assert.IsNotNull(compact);
            Assert.IsNotNull(picker);
            Assert.IsNotNull(autoApprove);
            Assert.AreEqual(picker.text, compact.text, "the compact chip wears the model name");
            Assert.IsFalse(string.IsNullOrEmpty(compact.tooltip));
        }

        [Test]
        public void GetModels_DifferentClientsResponses_ReparsePerResponse_NullClientIsEmpty()
        {
            FakeCliProcess fakeA;
            FakeCliProcess fakeB;
            AgentClient clientA = MakeInitializedClient(out fakeA);
            AgentClient clientB = MakeInitializedClient(out fakeB);
            try
            {
                var header = new HeaderView();
                List<HeaderView.ModelOption> a = header.GetModels(clientA);
                List<HeaderView.ModelOption> b = header.GetModels(clientB);
                Assert.AreNotSame(a, b, "a different InitializeResponse instance must re-parse");

                Assert.AreEqual(0, header.GetModels(null).Count, "no client -> empty, never null");
            }
            finally
            {
                clientA.Dispose();
                clientB.Dispose();
            }
        }


        // -- UXO-5: picker state before/after init -----------------------

        [Test]
        public void ResolveModelPickerState_LiveOptions_UseLive_DisabledOnlyMidStream()
        {
            HeaderView.ModelPickerState idle = HeaderView.ResolveModelPickerState(true, true, false);
            Assert.AreEqual(HeaderView.ModelPickerSource.Live, idle.Source);
            Assert.IsTrue(idle.Enabled);

            HeaderView.ModelPickerState mid = HeaderView.ResolveModelPickerState(true, false, true);
            Assert.AreEqual(HeaderView.ModelPickerSource.Live, mid.Source);
            Assert.IsFalse(mid.Enabled, "a set_model mid-stream would race the turn");
        }

        /// <summary>
        /// The dead-button era this closes: no live options used to mean
        /// SetEnabled(false), so before the first initialize (or with the
        /// CLI down) the picker ignored every click with no explanation.
        /// </summary>
        [Test]
        public void ResolveModelPickerState_NoLiveOptions_CachedCatalog_EnabledCacheMenu()
        {
            HeaderView.ModelPickerState state = HeaderView.ResolveModelPickerState(false, true, false);
            Assert.AreEqual(HeaderView.ModelPickerSource.Cache, state.Source);
            Assert.IsTrue(state.Enabled);
        }

        [Test]
        public void ResolveModelPickerState_NothingAtAll_StillEnabled_SoTheClickExplains()
        {
            HeaderView.ModelPickerState state = HeaderView.ResolveModelPickerState(false, false, false);
            Assert.AreEqual(HeaderView.ModelPickerSource.Empty, state.Source);
            Assert.IsTrue(state.Enabled);
        }

        /// <summary>Streaming only matters to the LIVE source: with no live options there is no running turn a cache/default write could race.</summary>
        [Test]
        public void ResolveModelPickerState_StreamingFlag_DoesNotDisableTheCachePath()
        {
            Assert.IsTrue(HeaderView.ResolveModelPickerState(false, true, true).Enabled);
            Assert.IsTrue(HeaderView.ResolveModelPickerState(false, false, true).Enabled);
        }


        // -- Cached-catalog menu target (2026-09-17 model-switch-before-init note) --

        [Test]
        public void CachedMenu_TargetsTheSession_WheneverAClientCanTakeALiveSwitch()
        {
            // Spawned and waiting for the handshake: the seconds after "+".
            Assert.IsTrue(HeaderView.ResolveCachedMenuTargetsSession(true, AgentClientState.Starting));
            Assert.IsTrue(HeaderView.ResolveCachedMenuTargetsSession(true, AgentClientState.Ready));
            Assert.IsTrue(HeaderView.ResolveCachedMenuTargetsSession(true, AgentClientState.WaitingPermission));
        }

        [Test]
        public void CachedMenu_SetsTheDefault_WhenThereIsNoSessionToSwitch()
        {
            Assert.IsFalse(HeaderView.ResolveCachedMenuTargetsSession(false, AgentClientState.NotStarted));
            Assert.IsFalse(HeaderView.ResolveCachedMenuTargetsSession(true, AgentClientState.NotStarted));
            Assert.IsFalse(HeaderView.ResolveCachedMenuTargetsSession(true, AgentClientState.Errored));
        }
    }
}
