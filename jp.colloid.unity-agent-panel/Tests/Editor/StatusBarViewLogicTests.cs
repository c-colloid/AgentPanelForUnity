using System.Collections.Generic;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using Colloid.AgentPanel.Core.Client;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guards for StatusBarView's context meter:
    /// SelectPrimaryModelUsage (which result.modelUsage entry the meter
    /// describes when a turn reports more than one model),
    /// ComputeUsedTokens and ComputeContextPercent (including the "no
    /// data" vs "genuine 0%" distinction). See
    /// docs/design-notes/2026-07-31-history-restore-flow-and-model-picker.md
    /// section 2 for the rationale.
    /// </summary>
    public class StatusBarViewLogicTests
    {
        /// <summary>
        /// FormatUsage assertions are written against the ENGLISH catalog.
        /// Window-building fixtures elsewhere in the suite legitimately run
        /// CreateGUI, which now applies the persisted language setting
        /// (Auto resolves to the OS language -- Japanese on the maintainer's
        /// machine), so this fixture must pin its language explicitly per
        /// the i18n determinism rule (docs/design-notes/2026-08-01-i18n.md).
        /// </summary>
        [SetUp]
        public void PinEnglishCatalog()
        {
            L10n.OverrideForTests(PanelLanguage.English);
        }

        [TearDown]
        public void UnpinCatalog()
        {
            L10n.OverrideForTests(null);
        }

        private static ModelUsage Usage(long input, long output, long cacheRead,
            long cacheCreate, long contextWindow, double costUsd = 0)
        {
            return new ModelUsage
            {
                InputTokens = input,
                OutputTokens = output,
                CacheReadInputTokens = cacheRead,
                CacheCreationInputTokens = cacheCreate,
                ContextWindow = contextWindow,
                CostUsd = costUsd
            };
        }

        // -- SelectPrimaryModelUsage ------------------------------------------------

        [Test]
        public void SelectPrimaryModelUsage_NullDictionary_ReturnsNull()
        {
            Assert.IsNull(StatusBarView.SelectPrimaryModelUsage(null, "claude-opus-4-8[1m]"));
        }

        [Test]
        public void SelectPrimaryModelUsage_EmptyDictionary_ReturnsNull()
        {
            var empty = new Dictionary<string, ModelUsage>();
            Assert.IsNull(StatusBarView.SelectPrimaryModelUsage(empty, "claude-opus-4-8[1m]"));
        }

        [Test]
        public void SelectPrimaryModelUsage_ExactKeyMatch_IsPreferred_OverLargerEntry()
        {
            // Real shape (success_bidi_inbound.jsonl fixture): the small
            // helper model (haiku, title generation) can appear alongside
            // the main conversation model. An exact match on the CURRENT
            // resolved model must win even when the other entry has more
            // tokens.
            var dict = new Dictionary<string, ModelUsage>
            {
                { "claude-haiku-4-5-20251001", Usage(558, 14, 0, 0, 200000) },
                { "claude-opus-5[1m]", Usage(4, 102, 49893, 5899, 1000000) }
            };
            ModelUsage result = StatusBarView.SelectPrimaryModelUsage(dict, "claude-haiku-4-5-20251001");
            Assert.AreEqual(200000, result.ContextWindow);
        }

        [Test]
        public void SelectPrimaryModelUsage_NoExactMatch_PicksLargestTokenEntry()
        {
            var dict = new Dictionary<string, ModelUsage>
            {
                { "claude-haiku-4-5-20251001", Usage(558, 14, 0, 0, 200000) },
                { "claude-opus-5[1m]", Usage(4, 102, 49893, 5899, 1000000) }
            };
            // currentResolvedModel does not match either key (e.g. not yet
            // connected, or a model name the CLI resolved differently).
            ModelUsage result = StatusBarView.SelectPrimaryModelUsage(dict, "claude-sonnet-5");
            Assert.AreEqual(1000000, result.ContextWindow, "opus has 106 tokens vs haiku's 572");
        }

        [Test]
        public void SelectPrimaryModelUsage_NullOrEmptyCurrentModel_FallsBackToLargestEntry()
        {
            var dict = new Dictionary<string, ModelUsage>
            {
                { "claude-haiku-4-5-20251001", Usage(558, 14, 0, 0, 200000) },
                { "claude-opus-5[1m]", Usage(4, 102, 49893, 5899, 1000000) }
            };
            Assert.AreEqual(1000000, StatusBarView.SelectPrimaryModelUsage(dict, null).ContextWindow);
            Assert.AreEqual(1000000, StatusBarView.SelectPrimaryModelUsage(dict, string.Empty).ContextWindow);
        }

        // -- ComputeUsedTokens --------------------------------------------------------

        [Test]
        public void ComputeUsedTokens_NullUsage_ReturnsZero()
        {
            Assert.AreEqual(0, StatusBarView.ComputeUsedTokens(null));
        }

        [Test]
        public void ComputeUsedTokens_SumsAllFourFields()
        {
            ModelUsage usage = Usage(input: 4, output: 102, cacheRead: 49893, cacheCreate: 5899,
                contextWindow: 1000000);
            Assert.AreEqual(4 + 102 + 49893 + 5899, StatusBarView.ComputeUsedTokens(usage));
        }

        // -- ComputeContextPercent -----------------------------------------------------

        [Test]
        public void ComputeContextPercent_NullUsage_ReturnsZero()
        {
            Assert.AreEqual(0, StatusBarView.ComputeContextPercent(null));
        }

        [Test]
        public void ComputeContextPercent_ZeroContextWindow_ReturnsZero_DivisionGuard()
        {
            ModelUsage usage = Usage(100, 100, 0, 0, contextWindow: 0);
            Assert.AreEqual(0, StatusBarView.ComputeContextPercent(usage));
        }

        [Test]
        public void ComputeContextPercent_NegativeContextWindow_ReturnsZero()
        {
            ModelUsage usage = Usage(100, 100, 0, 0, contextWindow: -1);
            Assert.AreEqual(0, StatusBarView.ComputeContextPercent(usage));
        }

        [Test]
        public void ComputeContextPercent_TypicalUsage_RoundsToNearestPercent()
        {
            // (4 + 102 + 49893 + 5899) / 1000000 = 5.5898% -> 6
            ModelUsage usage = Usage(4, 102, 49893, 5899, 1000000);
            Assert.AreEqual(6, StatusBarView.ComputeContextPercent(usage));
        }

        [Test]
        public void ComputeContextPercent_ExactlyFull_Returns100()
        {
            ModelUsage usage = Usage(500000, 500000, 0, 0, 1000000);
            Assert.AreEqual(100, StatusBarView.ComputeContextPercent(usage));
        }

        [Test]
        public void ComputeContextPercent_OverCapacity_ClampsTo100()
        {
            // Cache tokens can legitimately push "used" above contextWindow
            // in edge cases (e.g. right before a CLI auto-compact); the
            // meter must never show more than 100%.
            ModelUsage usage = Usage(900000, 900000, 0, 0, 1000000);
            Assert.AreEqual(100, StatusBarView.ComputeContextPercent(usage));
        }

        [Test]
        public void ComputeContextPercent_Zero_WhenNoUsageYet()
        {
            ModelUsage usage = Usage(0, 0, 0, 0, 200000);
            Assert.AreEqual(0, StatusBarView.ComputeContextPercent(usage));
        }

        // -- FormatUsage (docs/design-notes/2026-08-01-settings-enrichment.md #2:
        // PanelSettings.showCostUsd) ---------------------------------------------------

        private static ChatSession SessionWithUsage(long input, long output, double costUsd)
        {
            var session = new ChatSession();
            session.AccumulateTurn(input, output, 0, 0, costUsd);
            return session;
        }

        [Test]
        public void FormatUsage_NullSession_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, StatusBarView.FormatUsage(null, true));
            Assert.AreEqual(string.Empty, StatusBarView.FormatUsage(null, false));
        }

        [Test]
        public void FormatUsage_NoTokensYet_ReturnsZeroTokRegardlessOfShowCost()
        {
            ChatSession session = SessionWithUsage(0, 0, 0);
            Assert.AreEqual("0 tok", StatusBarView.FormatUsage(session, true));
            Assert.AreEqual("0 tok", StatusBarView.FormatUsage(session, false));
        }

        [Test]
        public void FormatUsage_ShowCostOn_NonzeroCost_AppendsCostSuffix()
        {
            ChatSession session = SessionWithUsage(1000, 700, 0.02);
            string result = StatusBarView.FormatUsage(session, true);
            StringAssert.Contains("1.7k tok", result);
            StringAssert.Contains("$0.02", result);
        }

        [Test]
        public void FormatUsage_ShowCostOff_OmitsCostSuffix_EvenWithNonzeroCost()
        {
            ChatSession session = SessionWithUsage(1000, 700, 0.02);
            string result = StatusBarView.FormatUsage(session, false);
            Assert.AreEqual("1.7k tok", result);
            StringAssert.DoesNotContain("$", result);
        }

        [Test]
        public void FormatUsage_ShowCostOn_ZeroCost_OmitsCostSuffix()
        {
            // Subscription auth typically reports no meaningful cost (R05):
            // a zero cost never shows a "$0.00" suffix even with the
            // toggle on.
            ChatSession session = SessionWithUsage(1000, 700, 0);
            string result = StatusBarView.FormatUsage(session, true);
            Assert.AreEqual("1.7k tok", result);
        }

        [Test]
        public void FormatUsage_InFlightTokens_AddedToCompletedTotal()
        {
            // Mid-turn the counter climbs with the turn's own assistant
            // messages (design note 2026-09-09-live-usage-during-turn.md)
            // while the cost suffix stays the completed-turn figure.
            ChatSession session = SessionWithUsage(1000, 700, 0.02);
            Assert.AreEqual("2.0k tok - $0.02", StatusBarView.FormatUsage(session, true, 300));
            Assert.AreEqual("1.7k tok", StatusBarView.FormatUsage(session, false, 0));
            Assert.AreEqual("1.7k tok", StatusBarView.FormatUsage(session, false, -5),
                "a negative in-flight count is treated as none");
        }

        [Test]
        public void FormatUsage_InFlightTokens_OnFreshSession_ShowsThemAlone()
        {
            ChatSession session = SessionWithUsage(0, 0, 0);
            Assert.AreEqual("0 tok", StatusBarView.FormatUsage(session, true, 0));
            Assert.AreEqual("42 tok", StatusBarView.FormatUsage(session, true, 42));
        }

        // -- UXO-7: slow-connect escalation ------------------------------

        /// <summary>
        /// Escalates only while genuinely Starting AND past the 10s
        /// threshold; a negative elapsed (the sentinel for "no start tick
        /// recorded") must fail toward the calm text.
        /// </summary>
        [TestCase(AgentClientState.Starting, 10000L, true)]
        [TestCase(AgentClientState.Starting, 10001L, true)]
        [TestCase(AgentClientState.Starting, 9999L, false)]
        [TestCase(AgentClientState.Starting, -1L, false)]
        [TestCase(AgentClientState.Ready, 60000L, false)]
        [TestCase(AgentClientState.Streaming, 60000L, false)]
        [TestCase(AgentClientState.Errored, 60000L, false)]
        [TestCase(AgentClientState.NotStarted, 60000L, false)]
        public void ShouldShowSlowConnect_Table(AgentClientState state, long elapsedMs, bool expected)
        {
            Assert.AreEqual(expected, StatusBarView.ShouldShowSlowConnect(
                state, elapsedMs, StatusBarView.SlowConnectThresholdMs));
        }


        // -- The meter measures CONTEXT, not cumulative billing -----------
        //
        // Every number below is read from a real captured result payload,
        // not invented. The defect: result.modelUsage is a billing sum over
        // every API call the turn made (each re-reading the same context
        // from cache) plus every subagent's tokens, and the meter divided
        // THAT by the context window.

        private static ResultMessage ParseResult(string fixture)
        {
            foreach (string line in FixtureLoader.ReadLines(fixture))
            {
                if (line.IndexOf("\"type\":\"result\"", System.StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                var msg = StreamJsonMessage.ParseLine(line, delegate(string m) { }) as ResultMessage;
                if (msg != null)
                {
                    return msg;
                }
            }
            return null;
        }

        /// <summary>
        /// The measured spread on the ONE capture that contains a subagent:
        /// modelUsage says 97,886, the enclosing usage says 56,449, and the
        /// context actually was 28,299. The meter must report the last one.
        /// </summary>
        [Test]
        public void SubagentCapture_ContextIsTheLastIteration_NotTheBillingTotal()
        {
            ResultMessage result = ParseResult("task_subagent_inbound.jsonl");
            Assert.IsNotNull(result, "the capture must contain a result line");
            Assert.AreEqual(28299L, result.Usage.LastIterationContextTokens);

            ModelUsage opus = result.ModelUsage["claude-opus-5[1m]"];
            Assert.AreEqual(97886L, StatusBarView.ComputeUsedTokens(opus),
                "billing total, subagent folded in -- 3.46x the real context");

            Assert.AreEqual(28299L,
                StatusBarView.ResolveContextTokens(result.Usage.LastIterationContextTokens, opus));
            Assert.AreEqual(3, StatusBarView.ComputeContextPercent(
                StatusBarView.ResolveContextTokens(result.Usage.LastIterationContextTokens, opus), opus),
                "3% of the 1M window, not the 10% the billing total produced");
        }

        /// <summary>
        /// The overstatement is not a subagent-only problem: a plain turn
        /// re-reads its context once per iteration, so billing is ~2x
        /// context even with no fan-out at all. All four subagent-free
        /// captures show it.
        /// </summary>
        [TestCase("success_bidi_inbound.jsonl", 27962L, 55898L)]
        [TestCase("permission_inbound.jsonl", 28161L, 56190L)]
        [TestCase("askuser_inbound.jsonl", 27999L, 55978L)]
        [TestCase("askuser3_inbound.jsonl", 28029L, 56006L)]
        public void PlainTurns_BillingIsAboutTwiceTheContext(
            string fixture, long expectedContext, long expectedBilling)
        {
            ResultMessage result = ParseResult(fixture);
            Assert.IsNotNull(result, fixture + " must contain a result line");
            Assert.AreEqual(expectedContext, result.Usage.LastIterationContextTokens);
            Assert.AreEqual(expectedBilling,
                StatusBarView.ComputeUsedTokens(result.ModelUsage["claude-opus-5[1m]"]));
        }

        [Test]
        public void ResolveContextTokens_NoIterations_FallsBackToTheBillingTotal()
        {
            var usage = new ModelUsage { InputTokens = 10, OutputTokens = 20, ContextWindow = 1000 };
            // -1 is the "absent" marker; 0 is a genuinely empty context and
            // must NOT trigger the fallback.
            Assert.AreEqual(30L, StatusBarView.ResolveContextTokens(-1, usage));
            Assert.AreEqual(0L, StatusBarView.ResolveContextTokens(0, usage));
        }

        [Test]
        public void ResolveContextTokens_NoIterationsAndNoUsage_IsZero_NeverThrows()
        {
            Assert.AreEqual(0L, StatusBarView.ResolveContextTokens(-1, null));
        }

        // -- SelectPrimaryModelUsage: the suffix gap ----------------------

        /// <summary>
        /// The keys carry a context-window suffix; CurrentModel may not.
        /// Before canonicalModel matching, that miss fell through to
        /// "largest bucket wins" -- which under a subagent fan-out on its
        /// own model selects the FAN-OUT's window, so the meter would
        /// measure against a window the user is not conversing in.
        /// </summary>
        [Test]
        public void SelectPrimaryModelUsage_SuffixMismatch_MatchesOnCanonicalModel()
        {
            var usage = new Dictionary<string, ModelUsage>
            {
                { "claude-haiku-4-5-20251001", new ModelUsage {
                    InputTokens = 900000, CanonicalModel = "claude-haiku-4-5", ContextWindow = 200000 } },
                { "claude-opus-5[1m]", new ModelUsage {
                    InputTokens = 10, CanonicalModel = "claude-opus-5", ContextWindow = 1000000 } }
            };

            ModelUsage picked = StatusBarView.SelectPrimaryModelUsage(usage, "claude-opus-5");

            Assert.AreEqual(1000000L, picked.ContextWindow,
                "the running model's window, not the biggest bucket's");
        }

        [Test]
        public void SelectPrimaryModelUsage_ExactKeyStillWins_OverCanonical()
        {
            var usage = new Dictionary<string, ModelUsage>
            {
                { "claude-opus-5[1m]", new ModelUsage {
                    CanonicalModel = "claude-opus-5", ContextWindow = 1000000 } },
                { "claude-opus-5", new ModelUsage {
                    CanonicalModel = "claude-opus-5", ContextWindow = 500000 } }
            };

            Assert.AreEqual(500000L,
                StatusBarView.SelectPrimaryModelUsage(usage, "claude-opus-5").ContextWindow);
        }

        [Test]
        public void CanonicalModel_IsParsedFromTheRealCapture()
        {
            ResultMessage result = ParseResult("task_subagent_inbound.jsonl");
            Assert.AreEqual("claude-opus-5", result.ModelUsage["claude-opus-5[1m]"].CanonicalModel);
            Assert.AreEqual("claude-haiku-4-5",
                result.ModelUsage["claude-haiku-4-5-20251001"].CanonicalModel);
        }


        // -- Held model switch on the chip (2026-09-17 model-switch-before-init note) --

        [Test]
        public void ResolveModelName_HeldSwitch_OutranksTheDefaultFallback()
        {
            string original = PanelStateStore.instance.Settings.model;
            try
            {
                PanelStateStore.instance.Settings.model = "claude-opus-5";
                Assert.AreEqual("fable-5-1",
                    StatusBarView.ResolveModelName(null, "claude-fable-5-1"),
                    "the model the user just picked shows while the handshake is pending");
                Assert.AreEqual("opus-5", StatusBarView.ResolveModelName(null, null),
                    "no held switch: the default fallback as before");
                Assert.AreEqual("opus-5", StatusBarView.ResolveModelName(null, string.Empty));
            }
            finally
            {
                PanelStateStore.instance.Settings.model = original;
            }
        }
    }
}
