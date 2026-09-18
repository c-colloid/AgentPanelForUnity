using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// 20px status strip (R05 section 2.6): state dot + label, model name,
    /// a context-window usage meter (thin bar + percentage, warn color at
    /// 80%+), cumulative session tokens and cost (cost only when
    /// result.total_cost_usd was reported greater than zero -- on
    /// subscription auth the CLI reports no meaningful cost and the panel
    /// falls back to tokens only, ARCHITECTURE.md risk 16). Clicking the
    /// usage area opens a small per-model breakdown popover. See
    /// docs/design-notes/2026-07-31-history-restore-flow-and-model-picker.md
    /// section 2 for why the meter picks ONE entry out of
    /// result.modelUsage (a turn can report multiple models, e.g. a
    /// title-generation helper model alongside the main conversation
    /// model) and what "used tokens" means here.
    /// </summary>
    public sealed class StatusBarView
    {
        private readonly VisualElement _root;
        private readonly VisualElement _dot;
        private readonly Label _stateText;
        private readonly Button _slowReconnect;
        private readonly Label _model;
        private readonly VisualElement _ctxMeter;
        private readonly VisualElement _ctxFill;
        private readonly Label _ctxLabel;
        private readonly VisualElement _usageContainer;
        private readonly Label _usage;

        private const int WarnThresholdPercent = 80;

        private static readonly string[] DotClasses =
        {
            "uap-status-dot--idle",
            "uap-status-dot--busy",
            "uap-status-dot--warn",
            "uap-status-dot--error",
            "uap-status-dot--reconnecting"
        };

        public VisualElement Root
        {
            get { return _root; }
        }

        public StatusBarView()
        {
            _root = new VisualElement();
            _root.style.flexDirection = FlexDirection.Row;
            _root.style.alignItems = Align.Center;
            _root.style.flexGrow = 1f;

            _dot = new VisualElement();
            _dot.AddToClassList("uap-status-dot");
            _root.Add(_dot);

            _stateText = new Label(L10n.S.StatusDisconnected);
            _stateText.AddToClassList("uap-status-text");
            _root.Add(_stateText);

            // UXO-7: hidden except during a SLOW connect (spec section 3.3:
            // escalate after 10s with a recovery affordance). Same
            // EnsureStarted entry the header/banner Reconnect uses.
            _slowReconnect = new Button(OnSlowReconnectClicked)
                { text = L10n.S.BannerReconnectButton };
            _slowReconnect.AddToClassList("uap-status-slow-reconnect");
            _slowReconnect.style.display = DisplayStyle.None;
            _root.Add(_slowReconnect);
            // The 10s threshold needs wall-clock ticks, not events -- an
            // unchanged hub state never re-raises Changed. One coarse
            // repaint per second while attached; Refresh is idempotent and
            // cheap.
            _root.schedule.Execute(Refresh).Every(1000);

            _root.Add(MakeSeparator());

            _model = new Label("-");
            _model.AddToClassList("uap-status-model");
            _model.enableRichText = false;
            _root.Add(_model);

            _root.Add(MakeSeparator());

            _ctxMeter = new VisualElement();
            _ctxMeter.AddToClassList("uap-status-ctx-meter");
            _ctxFill = new VisualElement();
            _ctxFill.AddToClassList("uap-status-ctx-fill");
            _ctxMeter.Add(_ctxFill);
            _root.Add(_ctxMeter);

            _ctxLabel = new Label(string.Empty);
            _ctxLabel.AddToClassList("uap-status-ctx-label");
            _root.Add(_ctxLabel);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            _root.Add(spacer);

            _usageContainer = new VisualElement();
            _usageContainer.AddToClassList("uap-status-usage-container");
            _usageContainer.RegisterCallback<ClickEvent>(OnUsageClicked);
            _usage = new Label(string.Empty);
            _usage.AddToClassList("uap-status-usage");
            _usageContainer.Add(_usage);
            _root.Add(_usageContainer);
        }

        /// <summary>Re-reads AgentHub state and repaints the strip.</summary>
        public void Refresh()
        {
            AgentClient client = AgentHub.Client;
            ChatSession session = AgentHub.Session;

            string text;
            string dotClass;
            if (AgentHub.IsAutoApplyInFlight)
            {
                // Settings auto-apply (docs/design-notes/2026-08-01-settings-
                // auto-apply.md section 3): a transient override for the
                // whole teardown/respawn window, regardless of what the
                // client itself reports (null during the brief teardown
                // moment, then Starting once the new one spawns) -- clears
                // itself the instant the respawned client reaches Ready or
                // Errored (AgentHub.OnStateChanged).
                text = L10n.S.StatusApplyingSettings;
                dotClass = "uap-status-dot--busy";
            }
            else if (client == null)
            {
                if (!string.IsNullOrEmpty(AgentHub.LastError))
                {
                    text = L10n.S.StatusCliNotFound;
                    dotClass = "uap-status-dot--error";
                }
                else
                {
                    // client == null with no LastError is the panel BETWEEN
                    // connections (e.g. the brief teardown moment ahead of
                    // an OnProcessDied restart, or before EnsureStarted has
                    // fired at all) rather than a genuinely idle/neutral
                    // state -- a reconnect is expected, so the dot should
                    // read as such instead of falling through to no color
                    // at all (design note 2026-08-14-ui-polish-audit.md
                    // contract item 3).
                    text = L10n.S.StatusDisconnected;
                    dotClass = "uap-status-dot--reconnecting";
                }
            }
            else
            {
                if (ShowCompactingState(AgentHub.IsCompacting, client.State))
                {
                    // Design note 2026-09-10-compacting-indicator.md: a
                    // compaction is one long API call with no stream
                    // traffic, so "Responding..." over a silent transcript
                    // read as a hang. Busy dot: it IS work in progress.
                    text = L10n.S.StatusCompacting;
                    dotClass = "uap-status-dot--busy";
                }
                else
                {
                    switch (client.State)
                    {
                        case AgentClientState.Starting:
                            text = AgentHub.AcpSignInPending
                                ? L10n.S.StatusWaitingSignIn
                                : ShouldShowSlowConnect(client.State,
                                    ElapsedMsSince(AgentHub.StartingSinceUtcTicks), SlowConnectThresholdMs)
                                    ? L10n.S.StatusConnectingSlow
                                    : L10n.S.StatusConnecting;
                            dotClass = "uap-status-dot--busy";
                            break;
                        case AgentClientState.Ready:
                            text = L10n.S.StatusIdle;
                            dotClass = "uap-status-dot--idle";
                            break;
                        case AgentClientState.Streaming:
                            text = L10n.S.StatusResponding;
                            dotClass = "uap-status-dot--busy";
                            break;
                        case AgentClientState.ToolRunning:
                            text = L10n.S.StatusRunningTool;
                            dotClass = "uap-status-dot--busy";
                            break;
                        case AgentClientState.WaitingPermission:
                            text = L10n.S.StatusWaitingPermission;
                            dotClass = "uap-status-dot--warn";
                            break;
                        case AgentClientState.Errored:
                            text = L10n.S.StatusError;
                            dotClass = "uap-status-dot--error";
                            break;
                        default:
                            text = L10n.S.StatusDisconnected;
                            dotClass = null;
                            break;
                    }
                }
            }
            _stateText.text = text;
            ApplyDotClass(dotClass);
            // UXO-7: the reconnect affordance appears exactly when the text
            // escalated to slow-connect, and never otherwise.
            bool slow = client != null && !AgentHub.AcpSignInPending && ShouldShowSlowConnect(client.State,
                ElapsedMsSince(AgentHub.StartingSinceUtcTicks), SlowConnectThresholdMs);
            _slowReconnect.style.display = slow ? DisplayStyle.Flex : DisplayStyle.None;

            _model.text = ResolveModelName(client, AgentHub.PendingSessionModel);
            _usage.text = FormatUsage(session, PanelStateStore.instance.Settings.showCostUsd,
                AgentHub.InFlightTurnTokens);
            RefreshContextMeter(client);
        }

        // -- Slow-connect escalation (UXO-7, spec section 3.3) -------------

        /// <summary>10 seconds, straight from the UX spec's escalation rule.</summary>
        internal const long SlowConnectThresholdMs = 10000;

        /// <summary>
        /// UXO-7's pure decision: escalate only while genuinely Starting
        /// AND past the threshold. A zero/unset start (elapsed computed as
        /// negative) never escalates -- fail toward the calm text.
        /// </summary>
        internal static bool ShouldShowSlowConnect(AgentClientState state, long elapsedMs, long thresholdMs)
        {
            if (state != AgentClientState.Starting)
            {
                return false;
            }
            return elapsedMs >= thresholdMs;
        }

        /// <summary>
        /// Pure gate for the "Compacting context..." status text: only
        /// while AgentHub says a compaction is running AND the client is
        /// inside a turn (Streaming / ToolRunning). Any other state --
        /// Ready after a stall, WaitingPermission, Errored -- keeps its
        /// own text; the hub's flag is cleared on every turn end anyway,
        /// so this is a belt for the brief window between the two.
        /// </summary>
        internal static bool ShowCompactingState(bool compacting, AgentClientState state)
        {
            return compacting
                && (state == AgentClientState.Streaming || state == AgentClientState.ToolRunning);
        }

        private static long ElapsedMsSince(long utcTicks)
        {
            if (utcTicks <= 0)
            {
                return -1;
            }
            return (System.DateTime.UtcNow.Ticks - utcTicks) / System.TimeSpan.TicksPerMillisecond;
        }

        private void OnSlowReconnectClicked()
        {
            AgentHub.Reconnect();
        }

        // -- Context meter --------------------------------------------------------------

        private void RefreshContextMeter(AgentClient client)
        {
            string currentResolvedModel = client != null ? client.CurrentModel : null;
            ModelUsage usage = SelectPrimaryModelUsage(AgentHub.LastModelUsage, currentResolvedModel);
            // CurrentContextTokens, not LastContextTokens: mid-turn the
            // hub already holds a fresher reading from the latest
            // assistant message, so the meter moves WHILE a long turn
            // runs instead of only when its result lands (design note
            // 2026-09-09-live-usage-during-turn.md).
            long contextTokens = AgentHub.CurrentContextTokens;
            bool compacted = ShowCompactedState(
                AgentHub.ContextUnknownAfterCompaction, contextTokens);
            int percent = compacted ? 0 : ComputeContextPercent(
                ResolveContextTokens(contextTokens, usage), usage);
            bool hasData = usage != null && usage.ContextWindow > 0;

            _ctxMeter.style.display = hasData ? DisplayStyle.Flex : DisplayStyle.None;
            _ctxLabel.style.display = hasData ? DisplayStyle.Flex : DisplayStyle.None;
            if (!hasData)
            {
                return;
            }
            if (compacted)
            {
                // Design note 2026-09-07-slash-commands-and-compaction.md
                // section 2.2: right after a compaction the only number on
                // hand describes the context that was just thrown away
                // (the billing-sum fallback would read ~100%), so the
                // meter says "compacted" until the next completed turn
                // measures the real, smaller window.
                _ctxFill.style.width = new Length(0, LengthUnit.Percent);
                _ctxLabel.text = L10n.S.StatusContextCompactedLabel;
                _ctxFill.EnableInClassList("uap-status-ctx-fill--warn", false);
                _ctxLabel.EnableInClassList("uap-status-warn-text", false);
                _ctxMeter.tooltip = L10n.S.StatusContextCompactedTooltip;
                return;
            }
            _ctxFill.style.width = new Length(percent, LengthUnit.Percent);
            _ctxLabel.text = L10n.F(L10n.S.StatusContextPercentFmt, percent);
            bool warn = percent >= WarnThresholdPercent;
            _ctxFill.EnableInClassList("uap-status-ctx-fill--warn", warn);
            _ctxLabel.EnableInClassList("uap-status-warn-text", warn);
            _ctxMeter.tooltip = warn
                ? L10n.S.StatusContextWarnTooltip
                : string.Empty;
        }

        private void OnUsageClicked(ClickEvent evt)
        {
            Rect screenRect = GUIUtility.GUIToScreenRect(_usageContainer.worldBound);
            UsagePopover.ShowBelow(screenRect);
        }

        // -- Helpers ------------------------------------------------------------

        private void ApplyDotClass(string active)
        {
            for (int i = 0; i < DotClasses.Length; i++)
            {
                _dot.EnableInClassList(DotClasses[i], DotClasses[i] == active);
            }
        }

        /// <summary>
        /// Short display name for the current model: prefers the live
        /// resolved model (AgentClient.CurrentModel -- system/init's model,
        /// overridden by a successful live SetModel; e.g.
        /// "claude-opus-4-8[1m]"), falling back to the persisted
        /// PanelSettings.model (an alias, e.g. "sonnet") when not connected.
        /// Shared with HeaderView's model picker button label so the two
        /// surfaces never disagree on what "the current model" means (see
        /// docs/design-notes/2026-07-31-live-model-tracking.md).
        /// </summary>
        internal static string ResolveModelName(AgentClient client)
        {
            return ResolveModelName(client, null);
        }

        /// <summary>
        /// As above, with the switch AgentHub is holding until the
        /// initialize handshake answers (AgentHub.PendingSessionModel):
        /// it outranks the live model, which is the one being replaced,
        /// so the chip shows what the user just picked instead of
        /// flipping back to the old name until the switch lands.
        /// </summary>
        internal static string ResolveModelName(AgentClient client, string pendingSessionModel)
        {
            string model = !string.IsNullOrEmpty(pendingSessionModel)
                ? pendingSessionModel
                : (client != null ? client.CurrentModel : null);
            if (string.IsNullOrEmpty(model))
            {
                model = PanelStateStore.instance.Settings.model;
            }
            if (string.IsNullOrEmpty(model))
            {
                return "-";
            }
            // Short display name: strip the family prefix.
            if (model.StartsWith("claude-", System.StringComparison.Ordinal))
            {
                model = model.Substring(7);
            }
            return model;
        }

        /// <summary>
        /// Pure formatter (EditMode tested via StatusBarViewLogicTests):
        /// "1.7k tok" or, when showCostUsd is on AND a nonzero cost was
        /// reported, "1.7k tok - $0.02". showCostUsd off always omits the
        /// cost suffix regardless of session.totalCostUsd -- the design
        /// note's subscription-auth rationale (R05): the CLI-reported cost
        /// is not a meaningful number for subscription users.
        /// </summary>
        public static string FormatUsage(ChatSession session, bool showCostUsd)
        {
            return FormatUsage(session, showCostUsd, 0);
        }

        /// <summary>
        /// <see cref="FormatUsage(ChatSession, bool)"/> plus the tokens the
        /// CURRENT turn has reported so far (<see cref="AgentHub.InFlightTurnTokens"/>),
        /// so the counter climbs during a turn. The cost suffix stays the
        /// session's completed-turn figure: nothing on the wire prices a
        /// turn before its result. Negative in-flight counts are treated
        /// as 0.
        /// </summary>
        public static string FormatUsage(ChatSession session, bool showCostUsd, long inFlightTokens)
        {
            if (session == null)
            {
                return string.Empty;
            }
            long total = session.totalInputTokens + session.totalOutputTokens
                + (inFlightTokens > 0 ? inFlightTokens : 0);
            if (total <= 0)
            {
                return L10n.S.StatusUsageZeroTokens;
            }
            string tokens = L10n.F(L10n.S.StatusUsageTokensFmt, FormatTokens(total));
            if (showCostUsd && session.totalCostUsd > 0)
            {
                tokens = L10n.F(L10n.S.StatusUsageTokensWithCostFmt, FormatTokens(total),
                    session.totalCostUsd.ToString("0.00", CultureInfo.InvariantCulture));
            }
            return tokens;
        }

        private static string FormatTokens(long count)
        {
            return TokenCountFormat.Short(count);
        }

        private static VisualElement MakeSeparator()
        {
            var sep = new VisualElement();
            sep.AddToClassList("uap-status-sep");
            return sep;
        }

        // -- Pure helpers (EditMode tested via StatusBarViewLogicTests) ----------------

        /// <summary>
        /// Picks which result.modelUsage entry the context meter describes.
        /// A single turn's modelUsage can carry MULTIPLE models (e.g. a
        /// small helper model used for title generation alongside the main
        /// conversation model, observed in the real success_bidi_inbound.jsonl
        /// fixture) -- using the wrong one's contextWindow as the
        /// denominator produces a meaningless percentage. Preference order:
        /// 1. The entry whose key equals currentResolvedModel (system/init's
        ///    Model field) -- an exact match is always correct.
        /// 2. Otherwise, the entry with the largest ComputeUsedTokens total
        ///    (input+output+cache). NOT just input+output: a real capture
        ///    caught this the hard way -- a long-running main conversation
        ///    can have a TINY fresh input+output (e.g. 4 in / 102 out) once
        ///    most of its context is served from cache (49893 cache-read +
        ///    5899 cache-create tokens), while a small title-generation
        ///    helper model's one-shot summarization call has a much LARGER
        ///    raw input+output (558 in / 14 out) despite being the
        ///    throwaway helper. Comparing the full four-field total (the
        ///    same metric ComputeUsedTokens/the percentage itself uses)
        ///    correctly picks the main model in that exact case; comparing
        ///    input+output alone picked the helper instead
        ///    (StatusBarViewLogicTests caught this against the real
        ///    fixture numbers).
        /// Returns null when modelUsage is null/empty (never a zeroed
        /// ModelUsage -- callers must distinguish "no data" from "0%").
        /// </summary>
        public static ModelUsage SelectPrimaryModelUsage(
            IReadOnlyDictionary<string, ModelUsage> modelUsage, string currentResolvedModel)
        {
            if (modelUsage == null || modelUsage.Count == 0)
            {
                return null;
            }
            if (!string.IsNullOrEmpty(currentResolvedModel))
            {
                ModelUsage exact;
                if (modelUsage.TryGetValue(currentResolvedModel, out exact))
                {
                    return exact;
                }
                // Rule 2: the same model wearing a different suffix. The
                // keys carry the context-window suffix ("claude-opus-5[1m]")
                // while CurrentModel can be whatever the catalog's
                // resolvedModel said, so an exact-key miss does NOT mean
                // "not this model". Matching canonicalModel closes that gap
                // BEFORE the largest-bucket guess below, which is a real
                // hazard once subagents run on their own model: the biggest
                // bucket is then the fan-out's, and the meter would measure
                // against a context window the user is not conversing in.
                foreach (KeyValuePair<string, ModelUsage> pair in modelUsage)
                {
                    if (pair.Value != null
                        && !string.IsNullOrEmpty(pair.Value.CanonicalModel)
                        && currentResolvedModel.StartsWith(pair.Value.CanonicalModel,
                            System.StringComparison.Ordinal))
                    {
                        return pair.Value;
                    }
                }
            }
            ModelUsage best = null;
            long bestTokens = -1;
            foreach (KeyValuePair<string, ModelUsage> pair in modelUsage)
            {
                if (pair.Value == null)
                {
                    continue;
                }
                long tokens = ComputeUsedTokens(pair.Value);
                if (tokens > bestTokens)
                {
                    bestTokens = tokens;
                    best = pair.Value;
                }
            }
            return best;
        }

        /// <summary>
        /// "Used tokens" for the context meter: input + output + both cache
        /// fields, i.e. everything that counts against the model's context
        /// window on the NEXT turn. Returns 0 for a null usage.
        /// </summary>
        public static long ComputeUsedTokens(ModelUsage usage)
        {
            if (usage == null)
            {
                return 0;
            }
            return usage.InputTokens + usage.OutputTokens
                + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
        }

        /// <summary>
        /// How many tokens actually occupy the context window, given the
        /// turn's observed context size and the fallbacks.
        ///
        /// The meter used to answer this with ComputeUsedTokens over
        /// result.modelUsage -- but modelUsage is CUMULATIVE BILLING, not
        /// context: it sums every API call the turn made (each re-reading
        /// the same context from cache) and folds in every subagent's
        /// tokens too. Measured against the five real result captures, that
        /// reads ~56k on a plain turn and 97,886 with one subagent, for a
        /// context that never exceeded ~28k -- a 2x to 3.46x overstatement
        /// of the one number whose job is to warn about imminent
        /// compaction. Under a subagent fan-out it simply pins at 100%.
        ///
        /// Preference order, each rung strictly better than the next:
        /// 1. The turn's last iteration (<paramref name="lastContextTokens"/>,
        ///    from UsageInfo) -- the real context, main-chain only.
        /// 2. Nothing else on the wire measures context, so fall back to
        ///    the old billing sum rather than blanking a meter that has
        ///    always shown something. It over-reads; it does not under-read,
        ///    so it never hides an imminent compaction.
        /// Subagent tokens are correctly excluded by rung 1: a subagent
        /// starts with cache_read 0 and builds its own context, so its
        /// tokens never occupied this window (measured, subagent_sidechain).
        /// </summary>
        public static long ResolveContextTokens(long lastContextTokens, ModelUsage fallback)
        {
            if (lastContextTokens >= 0)
            {
                return lastContextTokens;
            }
            return ComputeUsedTokens(fallback);
        }

        /// <summary>
        /// Pure gate for the meter's "compacted" rendering: only while a
        /// compaction has invalidated the last reading AND no trustworthy
        /// post-compaction reading has arrived (lastContextTokens &lt; 0).
        /// A real reading always wins over the flag -- the flag exists to
        /// suppress the billing-sum fallback, never a measurement.
        /// </summary>
        internal static bool ShowCompactedState(bool contextUnknownAfterCompaction, long lastContextTokens)
        {
            return contextUnknownAfterCompaction && lastContextTokens < 0;
        }

        /// <summary>
        /// Context-window usage percentage, clamped to [0, 100]. Returns 0
        /// for a null usage OR a non-positive contextWindow (division
        /// guard) -- callers must check ContextWindow &gt; 0 separately to
        /// tell "no data" apart from a genuine 0%. The usage argument
        /// supplies only the DENOMINATOR (that model's context window);
        /// the numerator comes from <see cref="ResolveContextTokens"/>.
        /// </summary>
        public static int ComputeContextPercent(long usedTokens, ModelUsage usage)
        {
            if (usage == null || usage.ContextWindow <= 0)
            {
                return 0;
            }
            return ClampPercent(usedTokens, usage.ContextWindow);
        }

        /// <summary>
        /// Billing-total overload kept for the per-model popover, which
        /// legitimately describes what each model BILLED this turn rather
        /// than what occupies the conversation's window.
        /// </summary>
        public static int ComputeContextPercent(ModelUsage usage)
        {
            if (usage == null || usage.ContextWindow <= 0)
            {
                return 0;
            }
            long used = ComputeUsedTokens(usage);
            return ClampPercent(used, usage.ContextWindow);
        }

        private static int ClampPercent(long used, long contextWindow)
        {
            double percent = (used / (double)contextWindow) * 100.0;
            if (percent < 0)
            {
                return 0;
            }
            if (percent > 100)
            {
                return 100;
            }
            return (int)System.Math.Round(percent);
        }

        /// <summary>
        /// Small non-modal popover (ShowAsDropDown) with a per-model
        /// breakdown of AgentHub.LastModelUsage -- the usage area's click
        /// target (R05 section 2.6: "clicking opens a breakdown popover").
        /// </summary>
        private sealed class UsagePopover : EditorWindow
        {
            private static readonly Vector2 Size = new Vector2(260f, 0f);

            public static void ShowBelow(Rect screenRect)
            {
                var window = CreateInstance<UsagePopover>();
                window.BuildContent();
                Vector2 size = new Vector2(Size.x,
                    Mathf.Max(60f, window.ComputeContentHeight()));
                window.ShowAsDropDown(screenRect, size);
            }

            private float ComputeContentHeight()
            {
                IReadOnlyDictionary<string, ModelUsage> usage = AgentHub.LastModelUsage;
                int rowCount = usage != null ? usage.Count : 0;
                return 36f + Mathf.Max(1, rowCount) * 46f;
            }

            private void BuildContent()
            {
                VisualElement root = rootVisualElement;
                AgentPanelWindow.ApplyThemeAndStyles(root);
                root.AddToClassList("uap-usage-popover");

                var title = new Label(L10n.S.StatusUsagePopoverTitle);
                title.AddToClassList("uap-usage-popover-title");
                title.enableRichText = false;
                root.Add(title);

                IReadOnlyDictionary<string, ModelUsage> usage = AgentHub.LastModelUsage;
                if (usage == null || usage.Count == 0)
                {
                    // Two different truths share this empty state, and the
                    // message must match the one that is actually the case.
                    // "No completed turn yet" next to a status bar showing
                    // restored totals was a lie (2026-08-05 report: a
                    // 1-turn session restored from a pre-v0.20.1 cache has
                    // totals but no per-model breakdown to show).
                    ChatSession session = AgentHub.Session;
                    bool hasHistory = session != null
                        && (session.completedTurns > 0
                            || session.totalInputTokens + session.totalOutputTokens > 0);
                    var empty = new Label(hasHistory
                        ? L10n.S.StatusUsagePopoverBreakdownPending
                        : L10n.S.StatusUsagePopoverEmpty);
                    empty.AddToClassList("uap-usage-popover-empty");
                    empty.enableRichText = false;
                    empty.style.whiteSpace = WhiteSpace.Normal;
                    root.Add(empty);
                    return;
                }
                bool showCostUsd = PanelStateStore.instance.Settings.showCostUsd;
                foreach (KeyValuePair<string, ModelUsage> pair in usage)
                {
                    root.Add(BuildModelRow(pair.Key, pair.Value, showCostUsd));
                }
            }

            private static VisualElement BuildModelRow(string modelName, ModelUsage usage, bool showCostUsd)
            {
                var row = new VisualElement();
                row.AddToClassList("uap-usage-popover-row");

                var name = new Label(modelName ?? string.Empty);
                name.AddToClassList("uap-usage-popover-model");
                name.enableRichText = false;
                row.Add(name);

                string line = L10n.F(L10n.S.StatusUsagePopoverLineFmt,
                    usage.InputTokens.ToString(CultureInfo.InvariantCulture),
                    usage.OutputTokens.ToString(CultureInfo.InvariantCulture),
                    (usage.CacheReadInputTokens + usage.CacheCreationInputTokens)
                        .ToString(CultureInfo.InvariantCulture));
                if (showCostUsd && usage.CostUsd > 0)
                {
                    line = L10n.F(L10n.S.StatusUsagePopoverCostSuffixFmt, line,
                        usage.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture));
                }
                var detail = new Label(line);
                detail.AddToClassList("uap-usage-popover-detail");
                detail.enableRichText = false;
                row.Add(detail);

                if (usage.ContextWindow > 0)
                {
                    int percent = ComputeContextPercent(usage);
                    var ctx = new Label(L10n.F(L10n.S.StatusUsagePopoverContextFmt, percent,
                        FormatTokens(usage.ContextWindow)));
                    ctx.AddToClassList("uap-usage-popover-detail");
                    ctx.enableRichText = false;
                    row.Add(ctx);
                }
                TextEscapes.Disable(row);
                return row;
            }
        }
    }
}
