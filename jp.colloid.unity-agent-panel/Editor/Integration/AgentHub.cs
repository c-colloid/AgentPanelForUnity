using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.Ops.Profiles;
using Colloid.AgentPanel.Ops.UnityPlugin;
using UnityEngine;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Static composition root: owns the single AgentClient, translates its
    /// protocol events into ChatSession/ChatMessage mutations and notifies
    /// the UI through Changed. Every member must be called from the main
    /// thread; AgentClient events already arrive there because the pump
    /// runs on EditorApplication.update.
    /// </summary>
    public static class AgentHub
    {
        private const int MaxConsecutiveRestarts = 3;
        private const int SummaryMaxChars = 160;
        private const string LogPrefix = "[AgentPanel] ";
        /// <summary>Ring buffer cap for the Settings Diagnostics stderr tail.</summary>
        private const int MaxStderrLines = 500;
        /// <summary>
        /// Short exit grace for the beforeAssemblyReload path (D4). 150 ms
        /// (was 500, 2026-09-06): this wait sits synchronously inside every
        /// domain reload, and the session is resumed with --resume
        /// regardless of whether the old process managed to exit on its
        /// own -- the tree kill that follows is what actually ends it. A
        /// CLI mid-tool rarely exits within any grace, so the longer wait
        /// mostly bought reload latency, not cleanliness. The interrupt
        /// line is still written first, so a CLI that IS idle gets its
        /// EOF and exits cleanly within this window.
        /// </summary>
        private const int ReloadStopGraceMillis = 150;
        /// <summary>
        /// Give up waiting for a sendable client after this long before
        /// dropping a queued auto-continuation (mirrors CompileGate.
        /// DrainTimeoutSeconds -- this is a "nice to have" resume, not a
        /// user-typed message, so a dropped continuation is logged rather
        /// than retried forever).
        /// </summary>
        private const double AutoContinueDrainTimeoutSeconds = 60.0;

        private static AgentClient _client;
        private static SessionCacheFile _sessionCache;
        /// <summary>
        /// Bumped every time the client is (re)started or torn down.
        /// Deferred callbacks capture it and bail out when it changed, so a
        /// stale delayCall can never tear down a freshly started client or
        /// resurrect a session the user discarded via StartFresh.
        /// </summary>
        private static int _clientEpoch;
        private static ChatSession _session;
        private static ZombieReaper _reaper;

        /// <summary>
        /// Process name recorded with the zombie record for the CURRENT
        /// spawn ("claude", or the ACP command's file name) so the reap
        /// check compares against what was actually launched.
        /// </summary>
        private static string _reaperProcessName = ZombieReaper.DefaultProcessName;

        /// <summary>The backend the current settings select (design note 2026-09-10-acp-backends.md section 2).</summary>
        public static AgentBackend CurrentBackend
        {
            get { return PanelStateStore.instance.Settings.agentBackend; }
        }

        /// <summary>
        /// A custom ACP agent's self-reported name (agentInfo.name from
        /// its initialize response, surfaced through system/init's
        /// version string as "name version"), or null before it has
        /// introduced itself / for non-custom backends. L10n.AgentName's
        /// fallback for the "{agent}" placeholder.
        /// </summary>
        public static string CurrentAcpAgentName { get; private set; }

        /// <summary>Pure: "qwen-code 1.2.0" -> "qwen-code"; null/blank -> null.</summary>
        internal static string ExtractAgentProductName(string versionString)
        {
            if (string.IsNullOrEmpty(versionString))
            {
                return null;
            }
            string trimmed = versionString.Trim();
            int space = trimmed.IndexOf(' ');
            string name = space > 0 ? trimmed.Substring(0, space) : trimmed;
            // A bare version number (Claude Code reports "2.1.0") is not a name.
            return name.Length > 0 && !char.IsDigit(name[0]) ? name : null;
        }

        /// <summary>True when the selected backend is Claude Code (the only one with in-panel auth, session history and subagent model settings).</summary>
        public static bool IsClaudeBackend
        {
            get { return !AgentBackends.IsAcp(CurrentBackend); }
        }

        // -- In-panel install (design note 2026-09-10-in-panel-install-and-sign-in.md section 1) --

        /// <summary>True while a CliInstaller run is in flight.</summary>
        public static bool CliInstallRunning { get; private set; }

        /// <summary>UTC ticks when the in-flight install started (0 when none).</summary>
        public static long CliInstallStartedUtcTicks { get; private set; }

        /// <summary>The backend the in-flight/last install was for.</summary>
        public static AgentBackend CliInstallBackend { get; private set; }

        /// <summary>Outcome of the last install run this domain; null before any run or while one is in flight.</summary>
        public static CliInstallResult LastCliInstallResult { get; private set; }

        /// <summary>
        /// Starts the in-panel install for the CURRENT backend. Returns
        /// false when one is already running, when the backend has no
        /// known installer (AcpCustom), or when the plan cannot be built.
        /// On success the hub reconnects on its own (EnsureStarted), so the
        /// first-run card turns into the chat without another click.
        /// </summary>
        public static bool BeginCliInstall()
        {
            if (CliInstallRunning)
            {
                return false;
            }
            AgentBackend backend = CurrentBackend;
            CliInstallPlan plan = CliInstallPlan.Build(backend,
                Application.platform == RuntimePlatform.WindowsEditor);
            if (plan == null)
            {
                return false;
            }
            CliInstallRunning = true;
            CliInstallStartedUtcTicks = DateTime.UtcNow.Ticks;
            CliInstallBackend = backend;
            LastCliInstallResult = null;
            Log("Installing " + AgentBackends.DisplayName(backend) + ": " + plan.DisplayCommand);
            RaiseChanged();
            CliInstaller.Run(plan, CreateKiller(), OnCliInstallCompleted, Log);
            return true;
        }

        private static void OnCliInstallCompleted(CliInstallResult result)
        {
            CliInstallRunning = false;
            CliInstallStartedUtcTicks = 0;
            LastCliInstallResult = result;
            if (result != null && result.Success)
            {
                Log("Install finished for " + AgentBackends.DisplayName(result.Backend) + "; reconnecting.");
                LastError = null;
                // A fresh spawn: the probe re-runs inside StartClient and
                // finds the binary the installer just wrote.
                if (CurrentBackend == result.Backend)
                {
                    RecoverConnection();
                    RaiseChanged();
                    return;
                }
            }
            else if (result != null)
            {
                Log("Install failed (" + result.Failure + ", exit " + result.ExitCode + "): " + result.LastLine);
            }
            RaiseChanged();
        }

        // -- ACP sign-in (design note 2026-09-10-in-panel-install-and-sign-in.md section 2) --

        /// <summary>
        /// True from the moment the ACP agent asked for sign-in (the bridge
        /// sent `authenticate`, the agent is opening its browser flow)
        /// until that round trip settles or the client leaves Starting.
        /// </summary>
        public static bool AcpSignInPending { get; private set; }

        /// <summary>Display name of the sign-in method the agent is using ("Login with Google", ...).</summary>
        public static string AcpSignInMethodName { get; private set; }

        /// <summary>
        /// A sign-in URL the agent printed to stderr while the sign-in was
        /// pending (agents fall back to printing it when they cannot open
        /// a browser themselves), or null.
        /// </summary>
        public static string AcpSignInUrl { get; private set; }

        /// <summary>The bridge's own error text when the last sign-in attempt failed; null otherwise.</summary>
        public static string AcpSignInError { get; private set; }

        /// <summary>
        /// The ACP auth method that actually authenticated THIS connection
        /// (design note 2026-09-10-acp-auth-guidance-and-method-display.md
        /// section 4). Both stay null when the agent opened the session
        /// without an `authenticate` round trip -- it was already signed in,
        /// and the method is simply not knowable from here -- which the
        /// Account card reports as a saved sign-in. Reset per spawn by
        /// TearDownClient.
        /// </summary>
        public static string AcpAuthMethodId { get; private set; }

        /// <summary>Display name of <see cref="AcpAuthMethodId"/> (falls back to the id when the agent gave no name).</summary>
        public static string AcpAuthMethodName { get; private set; }

        /// <summary>The method the bridge is trying right now, promoted to AcpAuthMethodId/Name once its round trip succeeds.</summary>
        private static string _acpAuthMethodIdInFlight;
        private static string _acpAuthMethodNameInFlight;

        /// <summary>
        /// Pure: the first http(s) URL in a stderr line, or null. Used
        /// only while AcpSignInPending so an unrelated URL in a log line
        /// never becomes a "sign-in link".
        /// </summary>
        internal static string ExtractFirstUrl(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }
            int start = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                start = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            }
            if (start < 0)
            {
                return null;
            }
            int end = start;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '"' && line[end] != '\''
                && line[end] != '<' && line[end] != '>')
            {
                end++;
            }
            string url = line.Substring(start, end - start).TrimEnd('.', ',', ')', ']');
            return url.Length > 8 ? url : null;
        }

        private static void OnAcpAuthenticationStarted(string methodId, string methodName)
        {
            // Arbitrary thread (the bridge raises from the reader thread):
            // set flags, defer the UI work to the main thread.
            string name = AcpProtocolBridge.DescribeAuthMethod(methodId, methodName);
            AuthCli.EnqueueCallback(delegate
            {
                AcpSignInPending = true;
                AcpSignInMethodName = name;
                _acpAuthMethodIdInFlight = methodId;
                _acpAuthMethodNameInFlight = name;
                AcpSignInUrl = null;
                AcpSignInError = null;
                AppendSystemNote(L10n.F(L10n.S.HubAcpSignInStartedNoteFmt,
                    AgentBackends.DisplayName(CurrentBackend), name), false);
                RaiseChanged();
            });
        }

        /// <summary>
        /// Set SYNCHRONOUSLY on the bridge's thread when the sign-in round
        /// trip fails, before the process is torn down: the death is
        /// observed by AgentClient's pump, which can run in the same editor
        /// frame before AuthCli's callback pump delivers the deferred
        /// AcpSignInError above. OnProcessDied consults this flag, not the
        /// main-thread property, so the no-reconnect decision cannot lose
        /// that race.
        /// </summary>
        private static volatile bool _acpSignInFailedThisProcess;

        /// <summary>True once the current process reached Ready (the handshake completed).</summary>
        private static bool _reachedReadyThisProcess;

        private static void OnAcpAuthenticationFinished(bool success, string error)
        {
            if (!success)
            {
                _acpSignInFailedThisProcess = true;
            }
            AuthCli.EnqueueCallback(delegate
            {
                AcpSignInPending = false;
                AcpSignInError = success ? null : (error ?? string.Empty);
                if (success)
                {
                    AcpAuthMethodId = _acpAuthMethodIdInFlight;
                    AcpAuthMethodName = _acpAuthMethodNameInFlight;
                    AppendAcpApiKeyAuthNoteIfNeeded();
                }
                else
                {
                    AppendSystemNote(L10n.F(L10n.S.HubAcpSignInFailedNoteFmt,
                        AgentBackends.DisplayName(CurrentBackend), error ?? string.Empty), true);
                }
                RaiseChanged();
            });
        }

        /// <summary>
        /// The ACP twin of <see cref="AppendApiKeyAuthNoteIfNeeded"/>
        /// (design note 2026-09-10-acp-auth-guidance-and-method-display.md
        /// section 4): once per spawn, when the method that authenticated
        /// is a key/gateway one, say in the transcript that usage is billed
        /// to that key rather than to a subscription. The Account card shows
        /// the method for as long as the connection lasts; this is the
        /// one-shot heads-up for whoever is only watching the chat.
        /// </summary>
        private static void AppendAcpApiKeyAuthNoteIfNeeded()
        {
            if (_apiKeyAuthNoted
                || !AcpProtocolBridge.IsApiKeyAuthMethod(AcpAuthMethodId, AcpAuthMethodName))
            {
                return;
            }
            _apiKeyAuthNoted = true;
            string text = L10n.F(L10n.S.HubAcpApiKeyAuthNoteFmt,
                AgentBackends.DisplayName(CurrentBackend),
                AcpProtocolBridge.DescribeAuthMethod(AcpAuthMethodId, AcpAuthMethodName));
            Log(text);
            AppendSystemNote(text, false);
            SessionCache.Save(Session, _lastModelUsage);
        }

        /// <summary>
        /// The "set this environment variable instead" sentence for a
        /// backend whose CLI documents an API key path, else empty. The
        /// panel stores no keys itself -- it only says where the CLI looks
        /// for one.
        /// </summary>
        internal static string ComposeAcpApiKeyGuidance(AgentBackend backend)
        {
            string hint = AgentBackends.ApiKeyHint(backend);
            return hint.Length == 0
                ? string.Empty
                : L10n.F(L10n.S.HubAcpApiKeyGuidanceFmt, AgentBackends.DisplayName(backend), hint);
        }

        /// <summary>Pure: <paramref name="second"/> appended to <paramref name="first"/> as a further sentence; either half may be empty.</summary>
        internal static string AppendSentence(string first, string second)
        {
            if (string.IsNullOrEmpty(first))
            {
                return second ?? string.Empty;
            }
            if (string.IsNullOrEmpty(second))
            {
                return first;
            }
            return first + " " + second;
        }

        private static void ResetAcpSignInState()
        {
            AcpSignInPending = false;
            AcpSignInMethodName = null;
            AcpSignInUrl = null;
        }

        private static void AppendSystemNote(string text, bool warning)
        {
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            note.Add(ChatMessageBlock.MakeSystemNote(text, warning));
            Session.AddMessage(note);
        }
        /// <summary>HUB-9: one inert-gate warning per spawn, not per tool call.</summary>
        private static bool _scriptGateInertWarned;
        /// <summary>
        /// One API-key-auth note per spawn (docs/design-notes/2026-09-10-
        /// claude-api-key-auth-passthrough.md), reset alongside
        /// _scriptGateInertWarned by TearDownClient -- see
        /// AppendApiKeyAuthNoteIfNeeded.
        /// </summary>
        private static bool _apiKeyAuthNoted;
        private static bool _resumedMidTurn;
        private static int _consecutiveDeaths;
        private static ControlRequestMessage _pendingPermission;
        /// <summary>
        /// Design note 2026-09-10 section 3: the tool name most recently
        /// consumed by <see cref="ConsumeReloadDroppedPermission"/> from
        /// SessionStateBridge.ReloadDroppedPermissionTool, held here just
        /// long enough for <see cref="TryAutoContinueInterruptedTurn"/> --
        /// called one step later in the SAME ReloadLifecycle.
        /// EnsureStartupReconciled tick -- to fold into the interrupted-
        /// turn continuation message. Null when nothing was consumed (the
        /// "no sentence" case, distinct from empty string only in that
        /// ComposeInterruptedContinuationMessage treats both the same via
        /// string.IsNullOrEmpty). Cleared once the continuation that used
        /// it is composed, and by <see cref="ResetForTests"/>; never
        /// SessionState-backed itself, since both the write and the read
        /// live within one domain's single update tick.
        /// </summary>
        private static string _lastReloadDroppedPermissionTool;
        private static ChatMessage _streamingAssistant;
        /// <summary>
        /// Per-model usage from the most recently completed turn's
        /// result.modelUsage (StatusBarView's context meter / usage
        /// popover source). Snapshot-per-turn, NOT accumulated across turns
        /// -- contextWindow/costUSD describe the model's current state, not
        /// a running total (see the design note section 2). Cleared on
        /// StartFresh/SwitchToSession (a different session's numbers must
        /// never linger); left as-is across a same-session Reconnect since
        /// it still describes the same conversation.
        /// </summary>
        /// <summary>
        /// The most recently completed turn's true end-of-turn context size
        /// (UsageInfo.LastIterationContextTokens), or -1 when no turn has
        /// completed this session or the CLI sent no iterations. Kept
        /// alongside <see cref="_lastModelUsage"/> because the meter needs
        /// BOTH: this for the numerator (context) and modelUsage for the
        /// denominator (that model's context window).
        ///
        /// Deliberately NOT restored from the session cache the way
        /// _lastModelUsage is: the cache stores per-model billing totals,
        /// which is precisely the quantity this field exists to stop
        /// standing in for context. A restored session therefore falls back
        /// one rung until its first completed turn, which is honest.
        /// </summary>
        private static long _lastContextTokens = -1;

        private static Dictionary<string, ModelUsage> _lastModelUsage =
            new Dictionary<string, ModelUsage>();
        /// <summary>
        /// Trigger ("manual" | "auto") of a system/compact_boundary seen
        /// since the last completed turn, or null. Consumed by
        /// OnTurnCompleted to decide whether that turn's context reading
        /// can be trusted (design note 2026-09-07-slash-commands-and-
        /// compaction.md section 2.2): a MANUAL compaction's own result
        /// describes the summarization call, which read the whole
        /// pre-compaction context, so its LastIterationContextTokens is
        /// the number the compaction just made obsolete.
        /// </summary>
        private static string _pendingCompactTrigger;
        /// <summary>
        /// True from a compact_boundary until the first completed turn
        /// whose context reading post-dates the compaction. While set, the
        /// status bar's meter shows "compacted" instead of a number
        /// (StatusBarView.RefreshContextMeter): the old billing-sum
        /// fallback would otherwise re-read the just-discarded context as
        /// ~100% right after the user asked for room.
        /// </summary>
        private static bool _contextUnknownAfterCompaction;
        /// <summary>
        /// True while the CLI is summarizing the conversation (design note
        /// docs/design-notes/2026-09-10-compacting-indicator.md). Set by
        /// system/status "compacting" (and, as a hint that needs no CLI
        /// support, by the panel's own send of "/compact"); cleared by the
        /// compact_boundary that ends the compaction, by an explicit
        /// status clear (null), and by every way a turn can end (result,
        /// stall, crash, teardown). While set, the status bar reads "Compacting
        /// context..." and the chat shows a spinner row: a compaction is
        /// one long API call with NO stream_event traffic, so without this
        /// the panel looked frozen for its whole duration. Not persisted
        /// across a domain reload (same honest fallback as the meter).
        /// </summary>
        private static bool _compacting;
        /// <summary>
        /// Live, in-flight reading of the context window taken from the
        /// most recent top-level assistant message of the CURRENT turn
        /// (four-field sum of its usage: input + output + cache_read +
        /// cache_creation, which is exactly what that API call's context
        /// held), or -1 while no turn is in flight / none has reported
        /// yet. Exists because <see cref="_lastContextTokens"/> is only
        /// refreshed by the result line, so a long tool-heavy turn left
        /// the status-bar meter frozen for minutes at the PREVIOUS turn's
        /// number (design note 2026-09-09-live-usage-during-turn.md).
        /// Superseded, and cleared, by the authoritative result in
        /// OnTurnCompleted; also cleared wherever an open turn is
        /// abandoned (AbortOpenTurn) so a dead turn's number never
        /// lingers.
        /// </summary>
        private static long _liveContextTokens = -1;
        /// <summary>
        /// Per-message usage of the current turn's top-level assistant
        /// messages, keyed by message id. A dictionary rather than a
        /// running sum because the CLI emits one assistant line PER
        /// CONTENT BLOCK, each repeating the same message id and the same
        /// usage -- summing naively double-counts a text+tool_use
        /// message. Summed (input + output only, matching FormatUsage) by
        /// <see cref="InFlightTurnTokens"/>; cleared with
        /// <see cref="_liveContextTokens"/>.
        /// </summary>
        private static readonly Dictionary<string, UsageInfo> _liveTurnUsage =
            new Dictionary<string, UsageInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ToolCallRecord> _openToolCalls =
            new Dictionary<string, ToolCallRecord>();
        /// <summary>
        /// Bare registry names (IUapTool.Name, e.g. "asset_create") of every
        /// non-undoable UapOps tool the CURRENT turn has run so far (design
        /// section 8.2 B2(c)/8.5 criterion 2). Populated in
        /// OnToolUseStarted, drained into a system-note warning and cleared
        /// by OnTurnCompleted; also cleared on any non-clean turn exit
        /// (TearDownClient) so a crash never attaches a stale warning to a
        /// LATER, unrelated turn.
        /// </summary>
        private static readonly HashSet<string> _currentTurnNonUndoableTools =
            new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// Phase 5c L3 item 3 (auto-continue after compile): true once,
        /// during the CURRENT turn, a uap_scripts_commit tool_result
        /// reported (AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles)
        /// that it actually moved staged files into Assets/. This is the
        /// ONLY signal HandleAutoContinueArming trusts to attribute an
        /// upcoming domain reload to the agent's own work -- see
        /// TryTrackScriptsCommitAttribution's doc comment for why a plain
        /// "was uap_scripts_commit called" check is not enough. Copied into
        /// SessionStateBridge.AutoContinuePendingAttribution (which is what
        /// survives the reload the commit is expected to cause) and reset
        /// every turn by HandleAutoContinueArming; also cleared by
        /// TearDownClient on a non-clean turn exit, mirroring
        /// _currentTurnNonUndoableTools immediately above.
        /// </summary>
        private static bool _currentTurnScriptsCommitAttributable;
        /// <summary>
        /// Message queued by TryAutoContinueAfterCompile, waiting for the
        /// just-respawned client to reach a sendable state (mirrors
        /// CompileGate's own "queue, then drain on EditorApplication.update"
        /// idiom, but scoped to this ONE message -- see
        /// TrySendPendingAutoContinueMessage's doc comment for why this
        /// does not simply reuse CompileGate.SendOrQueue). Null when
        /// nothing is pending.
        /// </summary>
        private static string _pendingAutoContinueMessage;
        /// <summary>
        /// True while <see cref="_pendingAutoContinueMessage"/> is an
        /// INTERRUPTED-turn continuation (TryAutoContinueInterruptedTurn)
        /// rather than the compile one: selects the transcript note, and
        /// on a confirmed send clears <see cref="_resumedMidTurn"/> so the
        /// ResumeBanner nudge does not keep offering a Continue button for
        /// a turn that was just continued automatically.
        /// </summary>
        private static bool _pendingAutoContinueIsInterruption;

        /// <summary>
        /// True only for the duration of an automatic continuation's
        /// SendUserMessage call: the wire message goes out, the turn
        /// bookkeeping runs, but no user bubble is added to the transcript
        /// (design note 2026-09-10-auto-approve-all-tools-and-lean-auto-
        /// continue section 3). Never true across a tick.
        /// </summary>
        private static bool _suppressTranscriptBubble;
        /// <summary>True while <see cref="OnAutoContinueSendTick"/> is subscribed to EditorApplication.update.</summary>
        private static bool _autoContinueHooked;
        /// <summary>EditorApplication.timeSinceStartup when the current wait for a sendable client began.</summary>
        private static double _autoContinueWaitStartedAt;
        /// <summary>
        /// Open subagent spawns keyed by the spawning Agent/Task tool_use id
        /// (Phase 4 design note section 3). Populated in OnToolUseStarted
        /// when that tool_use is top-level; removed once its tool_result
        /// arrives (OnToolResultReceived) or the turn ends while it is still
        /// running (FinalizeStreamingMessage marks it "stopped").
        /// </summary>
        private static readonly Dictionary<string, SubagentRecord> _openSubagents =
            new Dictionary<string, SubagentRecord>();
        /// <summary>
        /// task_started's task_id -&gt; tool_use_id, needed because
        /// task_updated only ever carries task_id (no tool_use_id) --
        /// see docs/design-notes/2026-07-31-subagent-display.md section 3.
        /// </summary>
        private static readonly Dictionary<string, string> _taskIdToToolUseId =
            new Dictionary<string, string>();
        /// <summary>
        /// Diagnostics stderr tail (Settings view). Deliberately NOT
        /// cleared on client restart/teardown -- a crash loop's stderr is
        /// exactly what the tail exists to show, and it is in-memory only
        /// (never persisted), so it naturally resets on domain reload /
        /// editor restart like every other static field here.
        /// </summary>
        private static readonly List<string> _stderrLog = new List<string>();
        /// <summary>
        /// The next-spawn-only settings fields as of the last successful
        /// client.Start() (SettingsView's "Reconnect now" hint). Null
        /// before any successful spawn this editor session.
        /// </summary>
        private static PanelSettings _lastSpawnedSettingsSnapshot;
        /// <summary>
        /// The custom-instructions sidecar text as of the last successful
        /// client.Start() (SettingsView's "Reconnect now" hint via
        /// SettingsChangeDetector.RequiresReconnect's 4-arg overload). Lives
        /// outside PanelSettings -- see CustomInstructionsFile -- so it is
        /// NOT part of _lastSpawnedSettingsSnapshot above. Null before any
        /// successful spawn this editor session.
        /// </summary>
        private static string _lastSpawnedCustomInstructions;
        /// <summary>
        /// Settings auto-apply state (docs/design-notes/2026-08-01-settings-
        /// auto-apply.md). True from the moment RequestAutoApplyReconnect
        /// sees a reconnect-worthy change until it is either applied
        /// (Reconnect() called) or invalidated (settings reverted,
        /// disconnected). AutoApplySettingsPolicy is the pure decision the
        /// per-frame tick and OnTurnCompleted's hook both defer to.
        /// </summary>
        private static bool _autoApplyPending;
        /// <summary>
        /// True once the debounce window has elapsed while the client was
        /// busy (a turn running or a permission pending): the per-frame tick
        /// stops polling at that point -- OnTurnCompleted alone drives the
        /// rest, per the design note's "apply on TurnCompleted once no
        /// permission is left pending" rule. Also what flips SettingsView's
        /// pending pill to its "apply after turn ends" text
        /// (IsAutoApplyDeferred).
        /// </summary>
        private static bool _autoApplyDeferred;
        /// <summary>EditorApplication.timeSinceStartup at the most recent reconnect-worthy edit.</summary>
        private static double _autoApplyLastEditTime;
        /// <summary>Whether the per-frame debounce tick is currently hooked onto EditorApplication.update.</summary>
        private static bool _autoApplyTickHooked;
        /// <summary>
        /// True from the moment an auto-apply Reconnect() is actually issued
        /// until the respawned client reaches Ready or Errored --
        /// StatusBarView's transient "Applying settings..." text (design
        /// note section 3).
        /// </summary>
        private static bool _autoApplyInFlight;
        /// <summary>
        /// The most recently received system/init message from ANY
        /// connection this editor session, retained across client
        /// teardown/recreate (Reconnect, auto-restart after ProcessDied,
        /// SwitchToSession, StartFresh -- see docs/design-notes/
        /// 2026-08-01-init-message-retention.md). Null only before the
        /// very first system/init this editor session has ever seen. Never
        /// explicitly cleared: unlike _lastModelUsage (session-content
        /// specific), the CLI version/session metadata this carries
        /// describes the installed CLI binary, not the current
        /// conversation, so a stale value from an ended session is still
        /// the best available answer to "what CLI version is running".
        /// </summary>
        private static SystemInitMessage _lastKnownInitMessage;
        /// <summary>See <see cref="CurrentAuthStatus"/>'s doc comment.</summary>
        private static AuthStatus _authStatus;
        /// <summary>Guards <see cref="RefreshAuthStatus"/> against overlapping "auth status --json" spawns.</summary>
        private static bool _authStatusQueryInFlight;
        /// <summary>
        /// Bumped every time an "auth status --json" query is started, by
        /// EITHER RefreshAuthStatus or OnLoginSessionExited. Each query's
        /// callback captures the token it was started with and only applies
        /// its result when that token still equals the current value --
        /// otherwise a newer query has since started and this result is
        /// stale. This is what lets OnLoginSessionExited always fire its own
        /// query (the design's "re-query regardless" rule) without racing a
        /// concurrent RefreshAuthStatus() call: whichever query started LAST
        /// wins, no matter which one's subprocess happens to finish first.
        /// </summary>
        private static int _authStatusQueryToken;
        /// <summary>See <see cref="CurrentLoginSession"/>'s doc comment.</summary>
        private static AuthLoginSession _loginSession;

        /// <summary>Raised after any observable mutation (session, state, errors).</summary>
        public static event Action Changed;

        /// <summary>The single client, or null before EnsureStarted / after Shutdown.</summary>
        public static AgentClient Client
        {
            get { return _client; }
        }

        /// <summary>
        /// The last system/init this editor session has actually received,
        /// from ANY connection (see the backing field's doc comment).
        /// SettingsView's About row falls back to this while the CURRENT
        /// client is connected (or connecting) but has not yet processed
        /// its OWN system/init line -- Client.InitMessage can legitimately
        /// still be null right after Ready, since Ready is reachable from
        /// the "initialize" control_response alone (see
        /// AgentClientStateTests
        /// .InitializeControlResponse_AloneAlsoTransitionsToReady) --
        /// rather than flashing "not connected" for a panel that, in fact,
        /// is connected. See docs/design-notes/2026-08-01-init-message-
        /// retention.md.
        /// </summary>
        public static SystemInitMessage LastKnownInitMessage
        {
            get { return _lastKnownInitMessage; }
        }

        /// <summary>
        /// Last CLI version string seen on ANY connection this editor
        /// session, surviving domain reloads through SessionStateBridge
        /// (unlike <see cref="LastKnownInitMessage"/>, whose static storage
        /// dies with the domain while a resumed connection may never re-emit
        /// system/init). Null when no init has ever arrived this editor
        /// session.
        /// </summary>
        public static string LastKnownCliVersion
        {
            get
            {
                if (_lastKnownInitMessage != null
                    && !string.IsNullOrEmpty(_lastKnownInitMessage.ClaudeCodeVersion))
                {
                    return _lastKnownInitMessage.ClaudeCodeVersion;
                }
                string persisted = SessionStateBridge.LastCliVersion;
                return string.IsNullOrEmpty(persisted) ? null : persisted;
            }
        }

        /// <summary>
        /// The transcript cache file (UserSettings/AgentPanel/
        /// SessionCache.json). Transcripts persist here as plain JSON, NOT
        /// inside the PanelStateStore asset -- UnityYAML corrupts assets
        /// containing raw message text (see SessionCacheFile).
        /// </summary>
        private static SessionCacheFile SessionCache
        {
            get
            {
                if (_sessionCache == null)
                {
                    _sessionCache = SessionCacheFile.CreateDefault(GetProjectRoot(), Log);
                }
                return _sessionCache;
            }
        }

        /// <summary>The active session display cache (never null).</summary>
        public static ChatSession Session
        {
            get
            {
                if (_session == null)
                {
                    // The per-model snapshot is restored WITH the session.
                    // It used to live only in the plain static above, so
                    // every editor start rendered restored token totals
                    // next to an empty usage popover and a dead context
                    // meter until the first turn completed (measured
                    // 2026-08-05, planted cache + fresh process: FormatUsage
                    // '19.1k tok', LastModelUsage 0 entries). Restoring from
                    // History was never affected -- SwitchToSession rebuilds
                    // the snapshot from the transcript -- which is why the
                    // asymmetry read as 'tokens gone at first open'.
                    Dictionary<string, ModelUsage> restoredUsage;
                    ChatSession loaded = SessionCache.Load(out restoredUsage);
                    _session = loaded ?? new ChatSession();
                    if (loaded != null && restoredUsage.Count > 0)
                    {
                        _lastModelUsage = restoredUsage;
                    }
                    else if (loaded != null)
                    {
                        // Cache has turns but no per-model key: a cache
                        // written before v0.20.1 persisted the breakdown.
                        // The user's own observation forced this path into
                        // existence -- switching to another History entry
                        // and back DID show the breakdown, because
                        // SwitchToSession rebuilds it from the transcript.
                        // The data and the rebuild code both already
                        // existed; only the boot path declined to use them,
                        // on a "not worth a file scan" judgment the
                        // workaround's mere discovery refuted. The scan
                        // runs only when the breakdown is missing, and the
                        // save below makes the next boot take the fast
                        // path, so steady state pays nothing.
                        TryBackfillModelUsageFromTranscript(loaded);
                    }
                }
                return _session;
            }
        }

        /// <summary>True when the last reload interrupted a running turn (resume nudge).</summary>
        public static bool ResumedMidTurn
        {
            get { return _resumedMidTurn; }
        }

        /// <summary>Human-readable startup failure (CLI not found, spawn error), or null.</summary>
        public static string LastError { get; private set; }

        /// <summary>
        /// The Unity project root (== the CLI's --cwd / WorkingDirectory).
        /// Exposed so HistoryView feeds SessionIndex.Refresh the EXACT same
        /// string StartClient passes as WorkingDirectory -- SessionIndex's
        /// cwd-to-directory-name transform is a plain character substitution
        /// with no normalization, so any drift here (different casing,
        /// trailing separator) would make History resolve the WRONG
        /// "~/.claude/projects/<...>/" directory.
        /// </summary>
        public static string ProjectRoot
        {
            get { return GetProjectRoot(); }
        }

        /// <summary>The unanswered can_use_tool request, or null (Phase 2 card input).</summary>
        public static ControlRequestMessage PendingPermission
        {
            get { return _pendingPermission; }
        }

        /// <summary>
        /// Design note 2026-09-10 section 1: number of can_use_tool
        /// requests waiting in the client's FIFO behind
        /// <see cref="PendingPermission"/> (excluding it). Zero when no
        /// request is pending or no client is up. The single-active model
        /// (HUB-4/CORE-6) is unchanged: this only reports the depth so the
        /// card can say "N more waiting" instead of surprising the user
        /// with a new card after every answer.
        /// </summary>
        public static int PendingPermissionQueueDepth
        {
            get
            {
                if (_pendingPermission == null || _client == null)
                {
                    return 0;
                }
                return _client.QueuedPermissionCount;
            }
        }

        /// <summary>
        /// True while a settings change is coalescing/waiting on the current
        /// turn before AgentHub applies it automatically (docs/design-notes/
        /// 2026-08-01-settings-auto-apply.md) -- SettingsView's pending-pill
        /// "apply after turn ends" variant. False once applied or when
        /// nothing is pending.
        /// </summary>
        public static bool IsAutoApplyDeferred
        {
            get { return _autoApplyPending && _autoApplyDeferred; }
        }

        /// <summary>
        /// True while an auto-apply Reconnect() is in flight --
        /// StatusBarView's transient "Applying settings..." text.
        /// </summary>
        public static bool IsAutoApplyInFlight
        {
            get { return _autoApplyInFlight; }
        }

        /// <summary>
        /// Recent CLI stderr lines (oldest first, capped at MaxStderrLines),
        /// surviving client restarts within this editor session. Read by
        /// SettingsView's Diagnostics tab; never written to disk.
        /// </summary>
        public static IReadOnlyList<string> StderrTail
        {
            get { return _stderrLog; }
        }

        /// <summary>Clears the Diagnostics stderr tail (Settings view "Clear" button).</summary>
        public static void ClearStderrTail()
        {
            _stderrLog.Clear();
            RaiseChanged();
        }

        /// <summary>
        /// Settings snapshot as of the last successful spawn, for
        /// SettingsChangeDetector.RequiresReconnect. ONLY the next-spawn-only
        /// fields are populated on the returned object -- every other field
        /// is left at the PanelSettings default and must not be read for
        /// any other purpose. Null before any successful spawn this editor
        /// session (StartFresh/EnsureStarted have not yet connected).
        /// </summary>
        public static PanelSettings LastSpawnedSettingsSnapshot
        {
            get { return _lastSpawnedSettingsSnapshot; }
        }

        /// <summary>
        /// Custom-instructions sidecar text as of the last successful spawn
        /// (see the field's doc comment). Null before any successful spawn
        /// this editor session.
        /// </summary>
        public static string LastSpawnedCustomInstructions
        {
            get { return _lastSpawnedCustomInstructions; }
        }

        /// <summary>
        /// Per-model usage from the most recently completed turn (see the
        /// field doc comment). Never null; empty before any turn completes
        /// this session.
        /// </summary>
        public static IReadOnlyDictionary<string, ModelUsage> LastModelUsage
        {
            get { return _lastModelUsage; }
        }

        /// <summary>See <see cref="_lastContextTokens"/>. -1 means "not observed".</summary>
        public static long LastContextTokens
        {
            get { return _lastContextTokens; }
        }

        /// <summary>See <see cref="_compacting"/>.</summary>
        public static bool IsCompacting
        {
            get { return _compacting; }
        }

        /// <summary>See <see cref="_contextUnknownAfterCompaction"/>.</summary>
        public static bool ContextUnknownAfterCompaction
        {
            get { return _contextUnknownAfterCompaction; }
        }

        /// <summary>See <see cref="_liveContextTokens"/>. -1 means "no in-flight reading".</summary>
        public static long LiveContextTokens
        {
            get { return _liveContextTokens; }
        }

        /// <summary>
        /// The context reading the meter should show RIGHT NOW: the live
        /// in-flight one when the current turn has already reported, else
        /// the last completed turn's. A live reading is always at least as
        /// fresh as the completed one, and after a compaction it is the
        /// first trustworthy post-compaction number (an auto compaction
        /// happens mid-turn; the very next assistant message ran on the
        /// compacted context).
        /// </summary>
        public static long CurrentContextTokens
        {
            get { return _liveContextTokens >= 0 ? _liveContextTokens : _lastContextTokens; }
        }

        /// <summary>
        /// input + output tokens the current turn's top-level assistant
        /// messages have reported so far (0 when idle). Added on top of the
        /// session's completed-turn totals by the status bar so the token
        /// counter moves during a turn instead of only at its end; the
        /// result line then folds the same numbers into the session and
        /// this drops back to 0.
        /// </summary>
        public static long InFlightTurnTokens
        {
            get
            {
                long total = 0;
                foreach (KeyValuePair<string, UsageInfo> pair in _liveTurnUsage)
                {
                    total += pair.Value.InputTokens + pair.Value.OutputTokens;
                }
                return total;
            }
        }

        /// <summary>
        /// Records one top-level assistant message's usage as the turn's
        /// live reading. Skipped entirely while a MANUAL compaction is
        /// pending: that turn's assistant message is the summarization
        /// call over the OLD context, the exact number the boundary just
        /// invalidated (design note 2026-09-07 section 2.2). An AUTO
        /// compaction's following messages ran on the new context and are
        /// taken as-is.
        /// </summary>
        private static void RecordLiveTurnUsage(AssistantMessage message)
        {
            if (message == null || message.Usage == null)
            {
                return;
            }
            if (string.Equals(_pendingCompactTrigger, SystemCompactBoundaryMessage.TriggerManual,
                    StringComparison.Ordinal))
            {
                return;
            }
            UsageInfo usage = message.Usage;
            long context = usage.InputTokens + usage.OutputTokens
                + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
            if (context > 0)
            {
                _liveContextTokens = context;
            }
            string key = string.IsNullOrEmpty(message.MessageId)
                ? "#" + _liveTurnUsage.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : message.MessageId;
            _liveTurnUsage[key] = usage;
        }

        /// <summary>Drops the in-flight reading: the turn ended (result) or was abandoned.</summary>
        private static void ClearLiveTurnUsage()
        {
            _liveContextTokens = -1;
            _liveTurnUsage.Clear();
        }

        /// <summary>
        /// The slash commands the composer offers right now: the cached
        /// CLI catalog (PanelSettings.slashCommandCatalog, refreshed on
        /// every live connection) with /compact and /clear guaranteed at
        /// the front (SlashCommandCatalog.WithBuiltins). Never null.
        /// </summary>
        public static List<SlashCommandEntry> SlashCommands
        {
            get
            {
                return SlashCommandCatalog.WithBuiltins(
                    PanelStateStore.instance.Settings.slashCommandCatalog,
                    L10n.S.SlashCompactDescription, L10n.S.SlashClearDescription);
            }
        }

        // -- Auth (docs/design-notes/2026-08-02-auth-in-panel.md) ------------------

        /// <summary>
        /// Cached "auth status --json" result, or null before the first
        /// RefreshAuthStatus() call completes this editor session.
        /// SettingsView's Account card and the chat setup card both render
        /// this SAME cache rather than each independently spawning
        /// "auth status --json".
        /// </summary>
        public static AuthStatus CurrentAuthStatus
        {
            get { return _authStatus; }
        }

        /// <summary>
        /// The in-flight "auth login" session, or null. UI code renders
        /// this object's CURRENT state (OAuthUrl/IsWaitingForCode/HasExited)
        /// on every AgentHub.Changed tick rather than owning any of the
        /// process lifecycle itself -- the session lives here (not in any
        /// view), so it survives a Settings view rebuild without leaking.
        /// </summary>
        public static AuthLoginSession CurrentLoginSession
        {
            get { return _loginSession; }
        }

        /// <summary>
        /// Re-queries "auth status --json" against the CLI path the current
        /// cliManualPath setting resolves to, then refreshes
        /// <see cref="CurrentAuthStatus"/> and raises Changed. No-op while a
        /// query is already in flight -- guards against spawning
        /// overlapping subprocesses when this is called from every Settings
        /// view activation.
        /// </summary>
        public static void RefreshAuthStatus()
        {
            if (_authStatusQueryInFlight)
            {
                return;
            }
            string cliPath = ResolveAuthCliPath();
            if (cliPath == null)
            {
                _authStatus = AuthStatus.Unavailable();
                RaiseChanged();
                return;
            }
            _authStatusQueryInFlight = true;
            int token = ++_authStatusQueryToken;
            AuthCli.QueryStatus(cliPath, delegate(AuthStatus status)
            {
                ApplyAuthStatusQueryResult(token, status, null);
            });
        }

        /// <summary>
        /// Shared token-checked apply for both RefreshAuthStatus and
        /// OnLoginSessionExited's "auth status --json" callbacks -- see
        /// <see cref="_authStatusQueryToken"/>'s doc comment for why this
        /// guard exists. A superseded (stale) result is dropped without
        /// touching <see cref="_authStatusQueryInFlight"/> or
        /// <see cref="_authStatus"/> at all: the newer query that superseded
        /// it owns both from here on. <paramref name="onFresh"/> (optional)
        /// runs only when the result was NOT stale, after _authStatus/
        /// _authStatusQueryInFlight are updated but before RaiseChanged --
        /// OnLoginSessionExited uses it to conditionally call
        /// RecoverConnection().
        /// </summary>
        private static void ApplyAuthStatusQueryResult(int token, AuthStatus status, Action<AuthStatus> onFresh)
        {
            if (token != _authStatusQueryToken)
            {
                return;
            }
            _authStatusQueryInFlight = false;
            _authStatus = status;
            if (onFresh != null)
            {
                onFresh(status);
            }
            RaiseChanged();
        }

        /// <summary>
        /// Starts an interactive "auth login" flow, or returns the ALREADY
        /// in-flight session unchanged if one exists -- both the Settings
        /// Account card's Login button and the chat setup card's Login
        /// button call this, and the second caller must never spawn a
        /// second "auth login" process. Returns null when the CLI path
        /// cannot be resolved at all.
        /// </summary>
        public static AuthLoginSession BeginLogin()
        {
            if (_loginSession != null && !_loginSession.HasExited)
            {
                return _loginSession;
            }
            string cliPath = ResolveAuthCliPath();
            if (cliPath == null)
            {
                return null;
            }
            AuthLoginSession session = AuthLoginSession.Begin(cliPath, CreateKiller(), Log);
            if (session == null)
            {
                return null;
            }
            _loginSession = session;
            session.UrlAvailable += delegate(string url) { RaiseChanged(); };
            session.WaitingForCode += delegate { RaiseChanged(); };
            session.Exited += delegate(int exitCode) { OnLoginSessionExited(session); };
            RaiseChanged();
            return session;
        }

        /// <summary>
        /// Writes the pasted authorization code to the in-flight login
        /// session's stdin. No-op (returns false) when no login is
        /// currently in flight.
        /// </summary>
        public static bool SubmitLoginCode(string code)
        {
            return _loginSession != null && _loginSession.SubmitCode(code);
        }

        /// <summary>
        /// Cancels the in-flight login session, if any. Its Exited event
        /// (fired once the process actually dies) is what clears
        /// <see cref="CurrentLoginSession"/> and re-queries status -- see
        /// <see cref="OnLoginSessionExited"/>.
        /// </summary>
        public static void CancelLogin()
        {
            if (_loginSession != null)
            {
                _loginSession.Cancel();
            }
        }

        /// <summary>
        /// A login process exited, for ANY exit code -- the exit code
        /// itself is never trusted as a success/failure signal (see
        /// AuthCli's design note "unverified fact" section). Re-queries
        /// status regardless, and only once that query confirms LoggedIn
        /// does it run <see cref="RecoverConnection"/> -- the same recipe
        /// the chat setup card's "Check again" button already uses -- so
        /// the panel becomes usable again without the user restarting
        /// anything.
        /// </summary>
        private static void OnLoginSessionExited(AuthLoginSession session)
        {
            // Shared with AgentHub.TerminateLoginSessionIfAny (quit-time
            // teardown) -- both paths must dispose the session (release its
            // Process object and stdin/stdout pipe handles), not just null
            // out the reference, or every login attempt (successful,
            // cancelled, or a quit mid-flow) leaks a Process/SafeHandle.
            DisposeAndClearLoginSession(session);
            string cliPath = ResolveAuthCliPath();
            if (cliPath == null)
            {
                // No newer query can possibly be in flight for a stale
                // token to race against here, but bump it anyway so any
                // OLDER RefreshAuthStatus() query still out there is
                // superseded by this authoritative (if CLI-path-less)
                // result rather than overwriting it afterward.
                _authStatusQueryToken++;
                _authStatusQueryInFlight = false;
                _authStatus = AuthStatus.Unavailable();
                RaiseChanged();
                return;
            }
            int token = ++_authStatusQueryToken;
            _authStatusQueryInFlight = true;
            AuthCli.QueryStatus(cliPath, delegate(AuthStatus status)
            {
                ApplyAuthStatusQueryResult(token, status, delegate(AuthStatus s)
                {
                    if (s.IsAvailable && s.LoggedIn)
                    {
                        RecoverConnection();
                    }
                });
            });
        }

        /// <summary>
        /// Tears down and restarts the CLI client from scratch (resuming
        /// the cached session id, same as EnsureStarted). Shared recipe for
        /// "something external fixed the auth/CLI problem, try again" --
        /// used by the chat setup card's "Check again" button and,
        /// automatically, once an in-panel login flow reports LoggedIn.
        /// </summary>
        public static void RecoverConnection()
        {
            Shutdown();
            EnsureStarted();
            // UXO-1: the setup card's "Check again" must also re-query auth
            // -- after the user logs in EXTERNALLY (a terminal `/login`),
            // a cached IsAvailable && !LoggedIn status would otherwise pin
            // the not-logged-in card forever. Guarded/async inside; raises
            // Changed on completion so the card re-resolves.
            RefreshAuthStatus();
        }

        /// <summary>
        /// Logs out: tears down the resident client FIRST (a session
        /// running against about-to-be-invalidated credentials is not
        /// something to leave alive), then runs "auth logout", then
        /// refreshes the cached status. <paramref name="onComplete"/>
        /// (optional) runs after the status refresh with whether the
        /// logout command itself reported success.
        /// </summary>
        public static void Logout(Action<bool> onComplete)
        {
            string cliPath = ResolveAuthCliPath();
            if (cliPath == null)
            {
                if (onComplete != null)
                {
                    onComplete(false);
                }
                return;
            }
            Shutdown();
            AuthCli.Logout(cliPath, delegate(bool success)
            {
                RefreshAuthStatus();
                if (onComplete != null)
                {
                    onComplete(success);
                }
            });
        }

        /// <summary>
        /// The same cliManualPath-aware path resolution StartClient uses,
        /// exposed standalone for the auth flows -- these must work even
        /// when the live client itself failed to spawn or is not connected
        /// (e.g. the CliNotFound/NotLoggedIn setup cards).
        /// </summary>
        private static string ResolveAuthCliPath()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            if (AgentBackends.IsAcp(settings.agentBackend))
            {
                // `claude auth ...` is meaningless for an ACP backend; the
                // Account card hides itself and the first-run login card
                // never shows (ChatView.ResolveFirstRunMode).
                return null;
            }
            ICliPathProbe probe = Application.platform == RuntimePlatform.WindowsEditor
                ? (ICliPathProbe)new WindowsCliPathProbe(settings.cliManualPath)
                : new UnixCliPathProbe(settings.cliManualPath);
            return probe.Resolve();
        }

        // -- Test-only seam (Colloid.AgentPanel.Editor.Tests via
        // InternalsVisibleTo, see AssemblyInfo.cs -- same precedent as
        // AgentPanelWindow.SetActiveView) --------------------------------------

        /// <summary>
        /// Wires this Hub's OWN production event handlers onto an
        /// externally supplied AgentClient (typically backed by
        /// FakeCliProcess) WITHOUT spawning a real process or touching
        /// <see cref="Client"/>. Lets SubagentGroupingTests (and the
        /// thinking-display hub-path tests) replay real captured fixture
        /// lines through the exact same
        /// OnToolUseStarted/OnAssistantMessageCompleted/OnTaskEventReceived/
        /// OnToolResultReceived/OnTurnCompleted/OnThinkingDelta/
        /// OnThinkingTokensReceived mutation logic production code runs,
        /// then inspect <see cref="Session"/> directly. Callers MUST call
        /// <see cref="ResetForTests"/> first and must never call any other
        /// AgentHub lifecycle method (StartClient/EnsureStarted/
        /// TearDownClient/...) around this -- it deliberately bypasses the
        /// whole real-client spawn/teardown lifecycle.
        /// </summary>
        internal static void WireClientForTests(AgentClient client)
        {
            client.AssistantMessageCompleted += OnAssistantMessageCompleted;
            client.ToolUseStarted += OnToolUseStarted;
            client.ToolResultReceived += OnToolResultReceived;
            client.TaskEventReceived += OnTaskEventReceived;
            client.TurnCompleted += OnTurnCompleted;
            client.ThinkingDelta += OnThinkingDelta;
            client.ThinkingTokensReceived += OnThinkingTokensReceived;
            client.CompactBoundaryReceived += OnCompactBoundaryReceived;
            client.StatusReceived += OnStatusReceived;
            client.PermissionRequested += OnPermissionRequested;
        }

        /// <summary>
        /// Test-only, separate from <see cref="WireClientForTests"/> ON
        /// PURPOSE: StateChanged is NOT part of that seam's fixed event set
        /// (existing fixtures built around it must keep their exact
        /// coverage), so a test that specifically needs to exercise
        /// AgentHub's OnStateChanged reaction (e.g. the mid-turn
        /// process-death -&gt; UapTurnScope release regression) opts in with
        /// this call as well.
        /// </summary>
        internal static void WireStateChangedForTests(AgentClient client)
        {
            client.StateChanged += OnStateChanged;
        }

        /// <summary>
        /// Test-only, same rationale as <see cref="WireStateChangedForTests"/>
        /// immediately above: ProcessDied is likewise not part of
        /// WireClientForTests' fixed event set, so a test that needs to
        /// exercise AgentHub's OWN OnProcessDied reaction (crash-loop
        /// bookkeeping, the defect 5 stranded-continuation-flag regression)
        /// opts in with this call. A real crash still needs no live
        /// process: FakeCliProcess.FailWrites + a send raises this exact
        /// event, same precedent as AgentHubTurnScopeReleaseTests.
        /// ProcessDeathMidTurn_ClosesTheOpenTurnScope.
        /// </summary>
        internal static void WireProcessDiedForTests(AgentClient client)
        {
            client.ProcessDied += OnProcessDied;
        }

        /// <summary>
        /// Test-only, same rationale as <see cref="WireStateChangedForTests"/>
        /// above: TurnStalled is likewise not part of WireClientForTests'
        /// fixed event set, so a test that needs to exercise AgentHub's OWN
        /// OnTurnStalled reaction (HUB-1's AbortOpenTurn -- scope release,
        /// stale warning/continuation-flag clearing) opts in with this
        /// call. A real stall needs no waiting: shrink
        /// AgentClient.SilenceTimeoutSeconds and pump.
        /// </summary>
        internal static void WireTurnStalledForTests(AgentClient client)
        {
            client.TurnStalled += OnTurnStalled;
        }

        /// <summary>
        /// Test-only: directly sets the crash-loop counter OnProcessDied
        /// increments and TrySendPendingAutoContinueMessage's defect-4 guard
        /// reads, without needing to replay MaxConsecutiveRestarts+1 real
        /// crash cycles (each of which would also need a successful
        /// reconnect in between to even be possible, since OnStateChanged
        /// only resets this on Starting-&gt;Ready). Callers MUST NOT rely on
        /// this to also reset PanelStateStore or client wiring. ResetForTests
        /// DOES also reset this field back to 0 (added alongside this
        /// method) -- see its own doc comment for why leaving it elevated
        /// would otherwise leak into every OTHER test in the same EditMode
        /// run.
        /// </summary>
        internal static void SetConsecutiveDeathsForTests(int value)
        {
            _consecutiveDeaths = value;
        }

        /// <summary>
        /// True while the crash-loop guard has given up restarting: the
        /// OnProcessDied handler stops auto-reconnecting once
        /// _consecutiveDeaths exceeds MaxConsecutiveRestarts and posts its
        /// "connection suspended" note. Every drain/retry path that might
        /// call EnsureStarted on a dead client MUST consult this first --
        /// TrySendPendingAutoContinueMessage (defect 4) and CompileGate's
        /// pending-send drain (HUB-2) both do -- or it would respawn a real
        /// claude process every update tick, fighting the very suspension
        /// the panel just told the user about. Cleared by a successful
        /// manual reconnect (OnStateChanged resets the counter on
        /// Starting-&gt;Ready).
        /// </summary>
        internal static bool IsCrashLoopSuspended
        {
            get { return _consecutiveDeaths > MaxConsecutiveRestarts; }
        }

        /// <summary>
        /// Test-only: resets the static session/tracking state to a clean
        /// slate without touching disk or any process (see
        /// WireClientForTests). Does NOT reset _client/_reaper/etc. --
        /// tests using this seam never spawn a real client in the first
        /// place.
        /// </summary>
        internal static void ResetForTests()
        {
            _session = new ChatSession();
            _streamingAssistant = null;
            _pendingPermission = null;
            _openToolCalls.Clear();
            _openSubagents.Clear();
            _taskIdToToolUseId.Clear();
            _currentTurnNonUndoableTools.Clear();
            _currentTurnScriptsCommitAttributable = false;
            _pendingAutoContinueMessage = null;
            _pendingAutoContinueIsInterruption = false;
            UnhookAutoContinueSendTick();
            ClearAutoContinuePendingState();
            // Added alongside SetConsecutiveDeathsForTests (defect 4 fix,
            // 2026-08-04): without this, a test that pushes the crash-loop
            // counter past MaxConsecutiveRestarts would leave it elevated
            // for every OTHER test in the same EditMode run (AgentHub
            // statics are shared across the whole run, same caveat as
            // SetClientForTests' own doc comment), silently making an
            // unrelated later test's TrySendPendingAutoContinueMessage call
            // hit the crash-loop-suspended branch instead of the one it
            // meant to exercise.
            _consecutiveDeaths = 0;
            _lastDeathUtcTicks = 0;
            // Same shared-statics rationale as _consecutiveDeaths above:
            // the transcript-backfill tests populate this, and a later test
            // reading LastModelUsage must see its own state, not theirs.
            _lastModelUsage = new Dictionary<string, ModelUsage>();
            _lastContextTokens = -1;
            ClearLiveTurnUsage();
            _pendingCompactTrigger = null;
            _contextUnknownAfterCompaction = false;
            _compacting = false;
            _lastReloadDroppedPermissionTool = null;
        }

        /// <summary>Test-only: true when an auto-continuation message is queued waiting for a sendable client (Phase 5c L3 item 3).</summary>
        internal static bool HasPendingAutoContinueMessageForTests
        {
            get { return !string.IsNullOrEmpty(_pendingAutoContinueMessage); }
        }

        /// <summary>
        /// Test-only: assigns AgentHub's own private static <see cref="_client"/>
        /// seam, distinct from WireClientForTests (which only subscribes this
        /// Hub's event handlers onto an externally-owned AgentClient and
        /// deliberately never touches <see cref="Client"/>). Lets tests for
        /// client-STATE-gated Hub methods (e.g. <see cref="SwitchSessionModel"/>,
        /// which short-circuits on `_client == null`) exercise the branch
        /// that actually calls into the client, instead of only the trivial
        /// no-op-without-a-connection branch. Callers MUST pass null in
        /// their teardown to restore the "no connection" invariant every
        /// other test fixture relies on (AgentHub statics are shared across
        /// the whole EditMode run).
        /// </summary>
        internal static void SetClientForTests(AgentClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Test-only: assigns AgentHub's private static
        /// <see cref="_loginSession"/> seam directly, without going through
        /// BeginLogin (which resolves a REAL cli path and would spawn an
        /// actual "auth login" process -- forbidden from a plain unit test).
        /// Pair with an AuthLoginSession created via
        /// AuthLoginSession.CreateForTests() (no backing process) and
        /// <see cref="TerminateLoginSessionForTests"/> below. Callers MUST
        /// pass null in their teardown, same rule as SetClientForTests.
        /// </summary>
        internal static void SetLoginSessionForTests(AuthLoginSession session)
        {
            _loginSession = session;
        }

        /// <summary>
        /// Test-only: runs the exact quit-time login-session teardown path
        /// (<see cref="Shutdown"/> uses the same private
        /// TerminateLoginSessionIfAny) without the rest of Shutdown's work
        /// (TearDownClient, SessionCache.Save) -- see
        /// AgentHubAuthTests.TerminateLoginSessionForTests_* for the
        /// regression this guards (ReloadLifecycle.OnWantsToQuit/OnQuitting
        /// previously never tore down an in-flight login at all).
        /// </summary>
        internal static void TerminateLoginSessionForTests()
        {
            TerminateLoginSessionIfAny();
        }

        /// <summary>
        /// Test-only: bumps and returns <see cref="_authStatusQueryToken"/>,
        /// simulating "a new auth status query just started" without
        /// actually spawning one -- lets tests reproduce the exact
        /// RefreshAuthStatus-vs-OnLoginSessionExited interleaving the token
        /// guard exists for (see <see cref="_authStatusQueryToken"/>'s doc
        /// comment) using two independently-bumped tokens.
        /// </summary>
        internal static int BumpAuthStatusQueryTokenForTests()
        {
            return ++_authStatusQueryToken;
        }

        /// <summary>
        /// Test-only: runs a query callback through the exact same
        /// staleness-checked apply path RefreshAuthStatus/
        /// OnLoginSessionExited use, given a token captured from
        /// <see cref="BumpAuthStatusQueryTokenForTests"/>.
        /// </summary>
        internal static void ApplyAuthStatusQueryResultForTests(int token, AuthStatus status)
        {
            ApplyAuthStatusQueryResult(token, status, null);
        }

        /// <summary>Test-only: resets the auth-status query state to a clean slate.</summary>
        internal static void ResetAuthStatusForTests()
        {
            _authStatus = null;
            _authStatusQueryInFlight = false;
            _authStatusQueryToken = 0;
        }

        /// <summary>
        /// Test-only: true when taskId still has a live _taskIdToToolUseId
        /// entry. Lets tests verify the map is pruned as subagents complete
        /// (OnToolResultReceived) or are demoted at teardown
        /// (DemoteOpenRecordsAndClear), instead of only growing for the
        /// life of the client connection.
        /// </summary>
        internal static bool HasTaskIdMappingForTests(string taskId)
        {
            return !string.IsNullOrEmpty(taskId) && _taskIdToToolUseId.ContainsKey(taskId);
        }

        /// <summary>
        /// Test-only: runs the exact TearDownClient path Shutdown/
        /// ShutdownForReload use (status demotion + dictionary clears),
        /// WITHOUT touching SessionCache/disk (TearDownClient itself never
        /// saves; only its Shutdown/ShutdownForReload callers do) and
        /// without a real _client (safe to call after WireClientForTests,
        /// where _client is never assigned). Verifies the fix for the
        /// "domain reload while a tool/subagent is running" defect.
        /// </summary>
        internal static void TearDownClientForTests()
        {
            TearDownClient(false);
        }

        // -- Lifecycle ---------------------------------------------------------

        /// <summary>
        /// Starts the CLI when it is not already running. Resumes the cached
        /// session when a session id is known. Safe to call repeatedly.
        ///
        /// Defect 2 fix (2026-08-04): every time this actually proceeds to
        /// (re)start the client -- never on the trivial "already running"
        /// fast-path return above -- it also clears the auto-continue
        /// attribution ticket (SessionStateBridge.AutoContinuePendingAttribution/
        /// AutoContinuePendingWasContinuation). This is the "EnsureStarted
        /// -- the path a user takes by simply typing a message -- does not
        /// clear it either" half of the fix: a ticket armed by
        /// HandleAutoContinueArming is only ever meant to survive until the
        /// ONE domain reload it was armed for. If the CLI instead dies for
        /// an unrelated reason (a crash loop, an auth failure) BEFORE that
        /// reload ever happens, and the user simply keeps using the panel
        /// (CompileGate's drain tick and every ordinary SendUserMessage call
        /// both route through here whenever the client is not currently
        /// alive), the world has moved on -- any later, unrelated reload
        /// must not resume the agent based on a promise made before this
        /// restart. Deliberately does NOT call ClearAutoContinuePendingState
        /// (which also clears AutoContinueTurnIsContinuation): THIS flag
        /// tracks "the turn currently in flight is itself a continuation"
        /// and legitimately needs to survive a reload that interrupts that
        /// SAME turn (ResumedMidTurn) -- RestoreAfterReload calls
        /// EnsureStarted as part of resuming that exact turn, and wiping the
        /// flag here would silently defeat guardrail 1 (a continuation
        /// resumed after being interrupted would no longer report itself as
        /// one, letting its own eventual completion arm a second,
        /// unbounded continuation). Clearing only the pending-ATTRIBUTION
        /// ticket, never the in-flight-continuation flag, is what keeps
        /// both invariants intact at once.
        /// </summary>
        public static void EnsureStarted()
        {
            // Ordering hardening (2026-09-06): the post-reload reconciliation
            // must run BEFORE this method touches anything. The window's
            // deferred boot call (AgentPanelWindow.StartHubDeferred) can
            // reach here ahead of ReloadLifecycle's first update tick;
            // without this, the StartClient below would rewrite
            // SessionStateBridge.TurnRunning (AbortOpenTurn, no client yet)
            // and the ticket clears below would spend the auto-continue
            // attribution -- both before RestoreAfterReload and
            // TryAutoContinueAfterCompile read them. Idempotent and
            // re-entrant (RestoreAfterReload calls back into this method;
            // the inner call is a no-op).
            ReloadLifecycle.EnsureStartupReconciled();
            if (_client != null
                && _client.State != AgentClientState.NotStarted
                && _client.State != AgentClientState.Errored)
            {
                return;
            }
            SessionStateBridge.AutoContinuePendingAttribution = false;
            SessionStateBridge.AutoContinuePendingWasContinuation = false;
            SessionStateBridge.AutoContinuePendingArmedAtUtcTicks = 0;
            StartClient(ResolveEnsureStartedResumeId(Session.sessionId, SessionStateBridge.CurrentSessionId));
        }

        /// <summary>
        /// Pure decision behind EnsureStarted's StartClient(resumeId)
        /// argument, extracted for testability the same way ResolveSpawnModel
        /// covers StartClient's --model decision (StartClient itself is
        /// private and resolves a real CLI executable path, so it cannot be
        /// exercised end-to-end here). Prefers `sessionSessionId`
        /// (ChatSession.sessionId), falling back to
        /// `sessionStateBridgeCurrentSessionId` (SessionStateBridge.
        /// CurrentSessionId, which survives a domain reload) when that is
        /// empty. CRITICALLY, the result is normalized to null when still
        /// empty afterward: both source properties default to
        /// string.Empty, never null (ChatSession.sessionId's field
        /// initializer; SessionStateBridge.CurrentSessionId's getter), so a
        /// genuinely fresh/never-resumed spawn must not pass "" through to
        /// StartClient -- ResolveSpawnModel's `resumeSessionId == null`
        /// check (the only thing distinguishing "fresh" from "resume")
        /// would otherwise misclassify it as a resume and silently drop
        /// --model, ignoring the persisted default model (regression fixed
        /// 2026-08-01; every other StartClient caller -- Reconnect, the
        /// OnProcessDied restart path -- already normalized this way
        /// inline; EnsureStarted was the one holdout).
        /// </summary>
        internal static string ResolveEnsureStartedResumeId(
            string sessionSessionId, string sessionStateBridgeCurrentSessionId)
        {
            string resumeId = sessionSessionId;
            if (string.IsNullOrEmpty(resumeId))
            {
                resumeId = sessionStateBridgeCurrentSessionId;
            }
            return string.IsNullOrEmpty(resumeId) ? null : resumeId;
        }

        /// <summary>
        /// Tears down and respawns the CLI client with the SAME session id
        /// (unlike StartFresh, which discards it), picking up any settings
        /// changes that only apply at spawn time (manual CLI path,
        /// permission mode, allowed/disallowedTools,
        /// dangerouslySkipPermissions -- see SettingsView / the Phase 3
        /// settings-propagation design note). No-op-safe to call at any
        /// time; if nothing is running it just starts fresh with the
        /// cached session id, same as EnsureStarted.
        /// </summary>
        public static void Reconnect()
        {
            string resumeId = Session.sessionId;
            TearDownClient(false);
            ClearAutoContinuePendingState();
            StartClient(string.IsNullOrEmpty(resumeId) ? null : resumeId);
        }

        /// <summary>Discards the cached session and starts a brand-new one.</summary>
        public static void StartFresh()
        {
            TearDownClient(true);
            _session = new ChatSession();
            // HUB-3: reset BEFORE the save. The old order persisted the
            // brand-new session's cache still carrying the PREVIOUS
            // conversation's per-model usage, so a restart right after
            // "New chat" restored the fresh session showing the old
            // session's token numbers.
            _lastModelUsage = new Dictionary<string, ModelUsage>();
            _lastContextTokens = -1;
            ClearLiveTurnUsage();
            _pendingCompactTrigger = null;
            _contextUnknownAfterCompaction = false;
            _compacting = false;
            SessionCache.Save(_session, _lastModelUsage);
            SessionStateBridge.CurrentSessionId = string.Empty;
            SessionStateBridge.HandoverPendingFrom = -1;
            _resumedMidTurn = false;
            ClearAutoContinuePendingState();
            StartClient(null);
        }

        /// <summary>
        /// Switches to a DIFFERENT existing session (History view), unlike
        /// StartFresh (discards to a brand-new id) and EnsureStarted/
        /// RestoreAfterReload (reconnects to the SAME Session.sessionId).
        /// Tears down the current process, replaces the display cache with
        /// restoredMessages (typically TranscriptLoader.Load(entry.FilePath)
        /// -- this method does not read the filesystem itself, see
        /// docs/design-notes/2026-07-31-history-restore-flow-and-model-picker.md
        /// section 1.1) and reconnects with --resume sessionId. Callers are
        /// responsible for confirming the switch with the user and for the
        /// double-resume guard (skip the call entirely when sessionId
        /// already equals Session.sessionId) -- this method always performs
        /// the kill+respawn unconditionally, matching every other AgentHub
        /// lifecycle method's "call it, it just runs" contract.
        /// </summary>
        public static void SwitchToSession(string sessionId, List<ChatMessage> restoredMessages,
            string titleHint = null)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }
            SwitchToSession(sessionId, restoredMessages, titleHint, null);
        }

        /// <summary>
        /// SwitchToSession with the transcript's reconstructed usage, so the
        /// status bar's token total and the "Usage this session" popover
        /// survive a history restore instead of resetting to "0 tok" /
        /// "No completed turn yet"
        /// (docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
        /// section 1). A null <paramref name="usage"/> keeps the pre-0.15
        /// behaviour of starting the counters at zero, which is still right
        /// for callers that have no transcript to read.
        /// </summary>
        public static void SwitchToSession(string sessionId, List<ChatMessage> restoredMessages,
            string titleHint, TranscriptUsage usage)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }
            TearDownClient(true);
            var session = new ChatSession { sessionId = sessionId };
            if (restoredMessages != null)
            {
                session.messages = restoredMessages;
            }
            ApplyRestoredUsage(session, usage);
            // A different session's compaction state must not linger any
            // more than its usage numbers do.
            _pendingCompactTrigger = null;
            _contextUnknownAfterCompaction = false;
            _compacting = false;
            string title = titleHint;
            if (string.IsNullOrEmpty(title))
            {
                title = FirstUserPlainText(session.messages);
            }
            if (!string.IsNullOrEmpty(title))
            {
                session.EnsureTitleFrom(title);
            }
            _session = session;
            // Seeded from the transcript rather than blanked: this is the
            // restored session's OWN last turn, so it is the correct value
            // for a field documented as "a different session's numbers must
            // never linger". Empty (the pre-0.15 blank) when no usage was
            // supplied or the transcript had no assistant usage at all.
            // HUB-3: seeded BEFORE the save -- the old order persisted the
            // switched-to session's cache still paired with the PREVIOUS
            // session's usage, exactly the lingering that comment forbids.
            _lastModelUsage = usage != null
                ? new Dictionary<string, ModelUsage>(usage.LastTurnModelUsage, StringComparer.Ordinal)
                : new Dictionary<string, ModelUsage>();
            SessionCache.Save(_session, _lastModelUsage);
            SessionStateBridge.CurrentSessionId = sessionId;
            _resumedMidTurn = false;
            ClearAutoContinuePendingState();
            StartClient(sessionId);
        }

        /// <summary>
        /// Copies reconstructed transcript usage onto a freshly built
        /// session. Cost is deliberately left at 0: no cost field exists
        /// anywhere in the on-disk transcript (measured), so any value here
        /// would be invented.
        /// </summary>
        /// <summary>
        /// Boot-time discovery half of the backfill: resolves the CLI
        /// transcript for <paramref name="loaded"/> the same way History
        /// does (SessionIndex's cwd transform + sessionId.jsonl) and hands
        /// it to <see cref="BackfillModelUsageFromTranscript"/>. Skips
        /// quietly when the session has no id, no completed work, or no
        /// transcript on disk -- a fresh project must not pay anything.
        /// </summary>
        private static void TryBackfillModelUsageFromTranscript(ChatSession loaded)
        {
            if (string.IsNullOrEmpty(loaded.sessionId)
                || (loaded.completedTurns <= 0
                    && loaded.totalInputTokens + loaded.totalOutputTokens <= 0))
            {
                return;
            }
            string dirName = SessionIndex.TransformCwdToProjectDirName(GetProjectRoot());
            string transcriptPath = Path.Combine(
                SessionIndex.DefaultProjectsRoot(), dirName, loaded.sessionId + ".jsonl");
            BackfillModelUsageFromTranscript(loaded, transcriptPath);
        }

        /// <summary>
        /// Rebuilds the per-model usage snapshot from a transcript exactly
        /// the way SwitchToSession does, then persists it so the NEXT boot
        /// restores from the cache without touching the transcript again.
        /// Internal (not private) purely as the EditMode test seam: the
        /// discovery half above depends on the real project root, which a
        /// test cannot control, while this half takes an explicit path.
        /// Returns whether a non-empty snapshot was applied.
        /// </summary>
        internal static bool BackfillModelUsageFromTranscript(ChatSession loaded, string transcriptPath)
        {
            if (!File.Exists(transcriptPath))
            {
                return false;
            }
            TranscriptUsage usage;
            TranscriptLoader.Load(transcriptPath, out usage, 1);
            if (usage.LastTurnModelUsage == null || usage.LastTurnModelUsage.Count == 0)
            {
                return false;
            }
            _lastModelUsage = new Dictionary<string, ModelUsage>(
                usage.LastTurnModelUsage, StringComparer.Ordinal);
            SessionCache.Save(loaded, _lastModelUsage);
            return true;
        }

        private static void ApplyRestoredUsage(ChatSession session, TranscriptUsage usage)
        {
            if (session == null || usage == null)
            {
                return;
            }
            session.totalInputTokens = usage.TotalInputTokens;
            session.totalOutputTokens = usage.TotalOutputTokens;
            session.totalCacheReadInputTokens = usage.TotalCacheReadInputTokens;
            session.totalCacheCreationInputTokens = usage.TotalCacheCreationInputTokens;
            session.completedTurns = usage.UserPromptCount;
            if (!string.IsNullOrEmpty(usage.LastTimestampIso))
            {
                session.lastActivityTimestamp = usage.LastTimestampIso;
            }
        }

        /// <summary>
        /// Persists PanelSettings.model -- the "default model for NEW
        /// sessions" (docs/design-notes/2026-08-01-model-settings-rework.md
        /// section 4.1, reversing v0.8.0's shared-source-of-truth design).
        /// The Settings "Default model" dropdown's sole entry point. Never
        /// touches the live client: a running session keeps whatever model
        /// it is already on (the header picker, <see cref="SwitchSessionModel"/>,
        /// is the ONLY way to change that) -- this only affects the
        /// `--model` argument AgentHub.StartClient passes on the NEXT fresh
        /// spawn (ResumeSessionId == null). `model` may be string.Empty --
        /// the Settings dropdown's "(Default)" sentinel
        /// (SettingsView.FormatModelChoiceValue), meaning "the next new
        /// session should not pass --model at all, let the CLI pick its own
        /// default" -- so persistence always runs regardless. Deliberately
        /// does NOT call RequestAutoApplyReconnect or touch
        /// _lastSpawnedSettingsSnapshot/_lastModelUsage: SettingsChangeDetector
        /// no longer compares `model` at all (see its class doc comment),
        /// so there is nothing pending to signal and no live session state
        /// this could invalidate.
        /// </summary>
        public static void SetDefaultModel(string model)
        {
            string normalized = model ?? string.Empty;
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.model = normalized;
            PanelStateStore.instance.SaveNow();
            RaiseChanged();
        }

        /// <summary>
        /// Switches the CURRENTLY RUNNING session's model live via the
        /// set_model control_request -- the header model picker's sole
        /// entry point (docs/design-notes/2026-08-01-model-settings-rework.md
        /// section 4.1). Deliberately does NOT touch
        /// PanelSettings.model/persistence at all: a one-off session switch
        /// must never silently rewrite what future NEW sessions start with
        /// (that is <see cref="SetDefaultModel"/>'s job, the Settings
        /// dropdown's entry point). No-op when `model` is empty (the header
        /// menu never offers an empty ModelOption in the first place, R07
        /// section 3) or when no client is connected enough to accept a
        /// live switch. Clears _lastModelUsage the same way
        /// StartFresh/SwitchToSession already do -- a stale modelUsage
        /// snapshot keyed to the OLD model would otherwise make the ctx
        /// meter fall back to its largest-token heuristic (possibly picking
        /// a helper model) until the next turn completes under the new
        /// model (see docs/design-notes/2026-07-31-live-model-tracking.md).
        /// </summary>
        public static void SwitchSessionModel(string model)
        {
            if (string.IsNullOrEmpty(model)
                || _client == null
                || _client.State == AgentClientState.NotStarted
                || _client.State == AgentClientState.Errored)
            {
                return;
            }
            _client.SetModel(model);
            _lastModelUsage = new Dictionary<string, ModelUsage>();
            _lastContextTokens = -1;
            ClearLiveTurnUsage();
            RaiseChanged();
        }

        /// <summary>
        /// Reflects a PanelSettings.uapOpsModules edit (Settings module
        /// toggle row) into the ALREADY-RUNNING UapOpsServer immediately --
        /// for TOOL AVAILABILITY, a module list change is explicitly NOT
        /// next-spawn-only (see that field's doc comment). Live tools/list
        /// calls pick this up on their own; when a CLI is connected this
        /// also nudges it via mcp_reconnect (design section 7.1/8.3: safer
        /// than relying on tools/list_changed, which is not trusted) so an
        /// already-open session's tool catalog updates without a full
        /// respawn. No-op (still updates the server) when nothing is
        /// connected -- the next spawn reads PanelStateStore directly
        /// regardless.
        ///
        /// This method deliberately does NOT also call
        /// RequestAutoApplyReconnect -- that half of the fix (converging the
        /// Stream C1 steering text, which the tool catalog above has no
        /// bearing on) lives in each SettingsView module-toggle handler
        /// instead, called alongside this method exactly the way every
        /// other reconnect-relevant Settings field already pairs
        /// SaveNow()/RequestAutoApplyReconnect(). Keeping it there (rather
        /// than folding it in here) mirrors OnUapOpsGateEnabledChanged's
        /// existing shape and keeps this method's own contract unchanged
        /// for any other caller.
        /// </summary>
        public static void ApplyUapOpsModulesChanged()
        {
            UapOpsServer.SetEnabledModules(PanelStateStore.instance.Settings.uapOpsModules);
            if (_client != null
                && _client.State != AgentClientState.NotStarted
                && _client.State != AgentClientState.Errored
                && UapOpsServer.IsRunning)
            {
                _client.SendMcpReconnect(UapOpsMcpConfig.ServerName);
            }
        }

        // -- Auto-approve level (UapAutoApproveLevel's own doc comment: the
        // CLI's own permission modes were measured and cannot fix permission
        // fatigue on a long task, so the panel decides per request instead)
        // ------------------------------------------------------------------

        /// <summary>
        /// Header/Settings UI entry point: call this immediately after
        /// writing a new value to PanelSettings' auto-approve level field
        /// (raising OR lowering it -- lowering simply never finds anything
        /// to resolve here, since <see cref="TryAutoApproveUapOpsTool"/>
        /// already ran the OLD, more permissive level against any request
        /// that arrived before the change). This is the actual point of the
        /// feature (UapAutoApproveLevel's doc comment): a user raises the
        /// level from the header PRECISELY because a permission card is
        /// already on screen blocking a long task, and the raise itself
        /// should resolve that card -- not merely change policy for the
        /// NEXT request while leaving the current one for the user to also
        /// click through by hand.
        ///
        /// Re-evaluates <see cref="_pendingPermission"/> (if any) against
        /// the CURRENT (just-changed) level via the exact same
        /// AutoApprovePolicy.ShouldAutoApprove path a fresh can_use_tool
        /// request goes through -- <see cref="TryAutoApproveUapOpsTool"/> is
        /// shared by both callers on purpose, so there is exactly one place
        /// that sends an auto-approve control_response, not two independently
        /// maintained copies. When that shared path answers the request, this
        /// method additionally clears <see cref="_pendingPermission"/> and
        /// raises <see cref="Changed"/> -- clearing the pending slot is a
        /// SEPARATE state transition (the card must disappear) from the
        /// approval itself, mirroring <see cref="RespondToPendingPermission"/>'s
        /// shape.
        ///
        /// No-op, leaving the card exactly as it was, when: nothing is
        /// pending (including the case where the user's own click already
        /// answered it in the meantime -- <see cref="_pendingPermission"/> is
        /// cleared the instant that happens, so a stray second call here,
        /// e.g. from rapidly toggling the level back and forth, can never
        /// answer the same request twice); the pending request is not a
        /// can_use_tool request; or the new level still does not cover it
        /// (e.g. it is a non-UapOps tool like Bash/Write -- AutoApprovePolicy
        /// never approves those at ANY level, or a mutating tool that still
        /// exceeds even the new level).
        ///
        /// Deliberately does NOT call RequestAutoApplyReconnect and must
        /// never be made to: the auto-approve level is read LIVE at
        /// decision time by <see cref="TryAutoApproveUapOpsTool"/> (like
        /// uapScriptGateEnabled's can_use_tool half, and
        /// autoApproveReadOnlyOps before it), never a spawn argument, so
        /// SettingsChangeDetector.RequiresReconnect must never be extended
        /// to compare it. That matters here specifically because
        /// AutoApplySettingsPolicy.Evaluate deliberately DEFERS a reconnect-
        /// requiring change while a permission is pending -- exactly the
        /// moment this method runs -- so routing the level through that
        /// machinery would make raising it to unblock a stuck card queue a
        /// reconnect instead of ever resolving anything live.
        /// </summary>
        public static void ApplyAutoApproveLevelChanged()
        {
            if (_pendingPermission == null)
            {
                return;
            }
            ControlRequestMessage request = _pendingPermission;
            if (!TryAutoApproveUapOpsTool(request))
            {
                return;
            }
            _pendingPermission = null;
            RaiseChanged();
        }

        /// <summary>Stops the CLI and persists the session cache.</summary>
        public static void Shutdown()
        {
            TerminateLoginSessionIfAny();
            TearDownClient(true);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        /// <summary>
        /// Kills and disposes any in-flight "auth login" child process
        /// out-of-band, without waiting for its own Exited event. Only
        /// AuthLoginSession's OWN beforeAssemblyReload hook (see its class
        /// doc comment) tears it down for a domain reload -- neither that
        /// hook nor OnLoginSessionExited's normal Exited-driven cleanup ever
        /// fires for EditorApplication.wantsToQuit/quitting, since a plain
        /// editor quit triggers no domain reload at all. Called from
        /// Shutdown() (ReloadLifecycle's quit handlers, plus Logout()) so an
        /// in-flight login can never outlive the editor process.
        /// </summary>
        private static void TerminateLoginSessionIfAny()
        {
            AuthLoginSession session = _loginSession;
            if (session == null)
            {
                return;
            }
            DisposeAndClearLoginSession(session);
        }

        /// <summary>
        /// Shared teardown for an AuthLoginSession, whether it exited on its
        /// own (OnLoginSessionExited) or is being killed out-of-band
        /// (TerminateLoginSessionIfAny): clears the reference (guarded by
        /// ReferenceEquals so a stale callback for an already-superseded
        /// session can never null out a NEWER one), best-effort kills the
        /// process tree, and disposes it (idempotent either way -- Cancel/
        /// Dispose on an already-exited process are safe no-ops per
        /// AuthLoginSession's own contract).
        /// </summary>
        private static void DisposeAndClearLoginSession(AuthLoginSession session)
        {
            if (ReferenceEquals(_loginSession, session))
            {
                _loginSession = null;
            }
            session.Cancel();
            session.Dispose();
        }

        /// <summary>
        /// Shutdown variant for beforeAssemblyReload: identical, but with a
        /// much smaller process-exit grace so the teardown fits the ~3 s
        /// domain-reload budget. The CLI rarely exits within any grace while
        /// a tool is running, and the session is resumed via --resume anyway,
        /// so waiting the full interactive grace only stalls every compile.
        /// </summary>
        /// <summary>
        /// HUB-10: the most OnWantsToQuit may do -- refresh the orphan
        /// reaper's record of the live CLI process WITHOUT killing it.
        /// wantsToQuit fires while the quit can still be vetoed (another
        /// package, or Cancel on the unsaved-scenes dialog), and the old
        /// Shutdown() there disconnected the panel for good in an editor
        /// that then kept running. Re-recording keeps the leak guarantee
        /// this is really about: if the process does outlive the editor,
        /// the next start's ReapOrphansNow finds and kills it. Silent and
        /// idempotent -- nothing to record when no client is up.
        /// </summary>
        internal static void RefreshProcessRecordForQuit()
        {
            if (_client == null || _reaper == null)
            {
                return;
            }
            int pid = SessionStateBridge.Pid;
            long startTicks = SessionStateBridge.ProcessStartTicks;
            if (pid <= 0)
            {
                return;
            }
            _reaper.Record(pid, startTicks, _reaperProcessName);
        }

        internal static void ShutdownForReload()
        {
            // clearZombieRecord: FALSE (2026-09-06). The 500 ms grace plus
            // tree kill is best-effort; deleting the PID record here
            // asserted "dead" without observing it, so a CLI that survived
            // (kill denied, taskkill missing, a race with its own exit
            // handling) kept running the old turn -- writing files and the
            // shared session transcript -- alongside the --resume spawn,
            // with nothing left that could ever find it. Keeping the record
            // lets ReloadLifecycle's post-reload ReapOrphansNow verify by
            // PID + start time + process name: a dead PID just clears the
            // record, a survivor is killed before the resume. Editor quit
            // (Shutdown) still clears: there is no post-quit tick to verify
            // in, and the next editor start reaps whatever it finds.
            TearDownClient(false, ReloadStopGraceMillis);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        /// <summary>
        /// Kills a recorded orphan CLI process without starting a client.
        /// Called by ReloadLifecycle on the first update tick after a domain
        /// reload so a crashed editor session cannot leak a claude process
        /// even when the CLI path probe later fails in StartClient.
        /// </summary>
        internal static void ReapOrphansNow()
        {
            var reaper = new ZombieReaper(
                Path.Combine(GetProjectRoot(), "UserSettings", "AgentPanel", "cli-process.json"),
                CreateKiller(), Log);
            reaper.ReapOrphans();
        }

        /// <summary>
        /// Called by ReloadLifecycle, but ONLY when SessionStateBridge.
        /// ClientWasRunning was true before the reload: re-reads the
        /// SessionState flags saved before the reload and reconnects with
        /// --resume. The transcript cache comes back through the Session
        /// getter (SessionCacheFile.Load), so the UI can repaint before
        /// the process is up.
        ///
        /// Defect 2 fix (2026-08-04): this method used to ALSO call
        /// <see cref="TryAutoContinueAfterCompile"/> right here, documented
        /// at the time as unconditional -- "it is what CONSUMES the
        /// SessionStateBridge tickets HandleAutoContinueArming wrote, so it
        /// must run on every reload, not just the ones this feature
        /// actually acts on". That claim was aspirational, not true: this
        /// method ITSELF only runs from inside ReloadLifecycle.
        /// OnFirstUpdate's `if (wasRunning)` branch, so bundling the ticket
        /// consumption in here meant it silently inherited that same gate.
        /// Any reload where the client was null or already Errored BEFORE
        /// the reload (a CLI crash-loop, an auth failure that never got
        /// past NotStarted) left ClientWasRunning false, so RestoreAfterReload
        /// -- and the ticket-consuming call bundled inside it -- never ran
        /// at all, and whatever ticket HandleAutoContinueArming had armed
        /// just sat in SessionState for the rest of the editor session,
        /// waiting to mislead the NEXT reload that finally did have
        /// ClientWasRunning true. ReloadLifecycle.OnFirstUpdate now calls
        /// AgentHub.TryAutoContinueAfterCompile() itself, unconditionally,
        /// BEFORE even checking wasRunning -- see that method's own doc
        /// comment for the full reasoning.
        /// </summary>
        internal static void RestoreAfterReload()
        {
            _resumedMidTurn = SessionStateBridge.TurnRunning;
            SessionStateBridge.TurnRunning = false;
            EnsureStarted();
        }

        /// <summary>
        /// Phase 5c L3 item 3 (auto-continue after compile) -- design
        /// section 3 item 3 / 8.3. The ONLY call site of
        /// AutoContinueAfterCompilePolicy.ShouldAutoContinue. Called by
        /// ReloadLifecycle.OnFirstUpdate UNCONDITIONALLY, before it even
        /// checks whether a client was running (defect 2 fix, 2026-08-04 --
        /// see RestoreAfterReload's own doc comment for why bundling this
        /// call inside that gated method was itself the bug). ALWAYS
        /// consumes -- reads, then clears -- the SessionState values
        /// HandleAutoContinueArming wrote before the reload, even when the
        /// feature is off or nothing was attributable, so a LATER,
        /// unrelated reload (a package resolve, the user hand-editing a
        /// script) can never read a stale ticket left over from an earlier
        /// turn.
        ///
        /// Defect 1 fix (2026-08-04): also folds AutoContinueAfterCompilePolicy.
        /// TicketIsFresh into the attributability check. A bare "was the
        /// last turn's commit attributable" bool has no notion of HOW LONG
        /// AGO that was, which let a confirmed scenario through: a commit's
        /// OWN resulting compile can fail (no domain reload follows a
        /// failed compile at all), leaving the ticket armed with nothing to
        /// consume it, until an entirely unrelated LATER reload (the user
        /// hand-fixing the same error in their own IDE, hours later) reads
        /// a still-true ticket and wrongly resumes the agent. See
        /// TicketIsFresh's own doc comment for why a reload-count-based
        /// check cannot catch this and wall-clock elapsed time can.
        ///
        /// compileSucceeded/errorDigest come from the HUB-8 carry-over
        /// pair (SessionStateBridge.LastCompileHadErrors/
        /// LastCompileErrorDigest) that ReloadLifecycle.OnBeforeAssemblyReload
        /// captured in the PREVIOUS domain, falling back to a fresh
        /// ConsoleErrorProvider query when nothing was carried. That order
        /// matters: the provider's entries are statics that do not survive
        /// a reload, and compiler errors are delivered to the OLD domain,
        /// so the post-reload query alone reported "succeeded" for every
        /// reload -- including the partial-failure case where some
        /// assemblies compiled and reloaded while others were still broken.
        /// This method used to document that blind spot as harmless
        /// ("Unity does not reload at all when a compile has errors, so
        /// reaching this method already implies success"); the partial case
        /// is the counterexample, and reporting an unobserved result to the
        /// model is exactly the defect class this project treats as
        /// headline. See AutoContinueAfterCompilePolicy.ComposeContinuationMessage's
        /// doc comment for the FAILED branch's wire text.
        ///
        /// Defect 3 fix (2026-08-04): no longer appends the "panel is
        /// sending the next message" system note itself. That note used to
        /// be written HERE, the instant this method decided to continue,
        /// before the client was even known to be sendable -- see
        /// TrySendPendingAutoContinueMessage's doc comment for where it
        /// moved to and why.
        /// </summary>
        internal static void TryAutoContinueAfterCompile(bool clientWasRunning)
        {
            bool attributable = SessionStateBridge.AutoContinuePendingAttribution;
            bool wasContinuation = SessionStateBridge.AutoContinuePendingWasContinuation;
            long armedAtUtcTicks = SessionStateBridge.AutoContinuePendingArmedAtUtcTicks;
            SessionStateBridge.AutoContinuePendingAttribution = false;
            SessionStateBridge.AutoContinuePendingWasContinuation = false;
            SessionStateBridge.AutoContinuePendingArmedAtUtcTicks = 0;

            // CONSUMING the ticket is unconditional -- that is the whole point
            // of moving this call out from behind the wasRunning gate, and a
            // ticket left armed is a ticket that misleads some later reload.
            // ACTING on it is not. If the client was not running when the
            // domain went down, there was no interrupted turn to resume, and
            // "continue the task" would be sent into a CLI this method had to
            // spawn for the purpose -- an unattended action on behalf of a
            // session that had already ended. The ticket is spent either way;
            // only the send is gated.
            if (!clientWasRunning)
            {
                return;
            }

            bool enabled = PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile;
            bool fresh = AutoContinueAfterCompilePolicy.TicketIsFresh(armedAtUtcTicks, DateTime.UtcNow.Ticks);
            if (!AutoContinueAfterCompilePolicy.ShouldAutoContinue(enabled, attributable && fresh, wasContinuation))
            {
                return;
            }

            // VISIBLE (non-ignored) errors only, matching FormatDigest's own
            // basis (2026-08-13 error-chip-ignore design note). Raw Count
            // here would resurrect exactly what the ignore feature silences:
            // an SDK that re-logs its known noise during this very reload
            // would flip the continuation message to "compile failed" over
            // errors the user marked do-not-fix -- worse, with a digest
            // that filters them out, the model would be told "failed" with
            // an EMPTY error list to fix.
            // HUB-8: prefer the snapshot ReloadLifecycle.OnBeforeAssemblyReload
            // took in the PREVIOUS domain, where the compiler errors
            // actually landed. Reading only the post-reload provider (whose
            // statics start empty) reported "SUCCEEDED" for every reload
            // that followed a partial failure -- telling the model a result
            // nothing had observed, this project's headline defect class.
            // The live provider stays the fallback for reloads that carried
            // nothing (a manual reload, an older session's leftovers).
            bool carriedHadErrors = SessionStateBridge.LastCompileHadErrors;
            string carriedDigest = SessionStateBridge.LastCompileErrorDigest;
            SessionStateBridge.ClearLastCompileResult();

            bool compileSucceeded;
            string errorDigest;
            if (carriedHadErrors)
            {
                compileSucceeded = false;
                errorDigest = !string.IsNullOrEmpty(carriedDigest)
                    ? carriedDigest
                    : ConsoleErrorProvider.FormatDigest();
            }
            else
            {
                compileSucceeded = ConsoleErrorProvider.VisibleCount == 0;
                errorDigest = compileSucceeded ? null : ConsoleErrorProvider.FormatDigest();
            }
            string message = AutoContinueAfterCompilePolicy.ComposeContinuationMessage(compileSucceeded, errorDigest);
            QueueAutoContinueMessage(message);
        }

        /// <summary>
        /// Design note 2026-09-10 section 3: reads and clears
        /// SessionStateBridge.ReloadDroppedPermissionTool -- the display
        /// name ReloadLifecycle.CaptureReloadDroppedPermissionTool saved
        /// before the reload discarded a still-pending can_use_tool
        /// request. Always stashes the value into
        /// <see cref="_lastReloadDroppedPermissionTool"/> for
        /// <see cref="TryAutoContinueInterruptedTurn"/> to fold into the
        /// continuation message one step later in the same
        /// ReloadLifecycle.EnsureStartupReconciled tick. When
        /// <paramref name="announce"/> is true and a tool name was
        /// captured, also appends a system note (HubReloadDroppedPermissionFmt)
        /// to the transcript, mirroring the streak-exhausted note pattern
        /// this file already uses elsewhere. ReloadLifecycle calls this
        /// with announce:false on the NOT-wasRunning path (no turn was
        /// interrupted, so nothing to tell the model or the user) purely
        /// to clear the SessionState key -- otherwise a stale name would
        /// leak into whatever the NEXT reload captures nothing for.
        /// </summary>
        /// <returns>The captured tool name, or an empty string when none was pending.</returns>
        internal static string ConsumeReloadDroppedPermission(bool announce)
        {
            string tool = SessionStateBridge.ReloadDroppedPermissionTool;
            SessionStateBridge.ReloadDroppedPermissionTool = string.Empty;
            _lastReloadDroppedPermissionTool = tool;
            if (announce && !string.IsNullOrEmpty(tool))
            {
                var note = new ChatMessage
                {
                    role = ChatMessage.RoleSystem,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
                note.Add(ChatMessageBlock.MakeSystemNote(
                    L10n.F(L10n.S.HubReloadDroppedPermissionFmt, tool), true));
                Session.AddMessage(note);
                SessionCache.Save(Session, _lastModelUsage);
                RaiseChanged();
            }
            return tool;
        }

        /// <summary>
        /// PanelSettings.autoContinueInterruptedTurn (design note 2026-09-06
        /// section 5-1): called by ReloadLifecycle right after
        /// <see cref="RestoreAfterReload"/> set <see cref="ResumedMidTurn"/>
        /// and BEFORE <see cref="StartAutoContinueDrain"/>. When the reload
        /// interrupted an open turn and the user opted in, queues the same
        /// continuation the ResumeBanner's Continue button sends -- through
        /// the existing single-slot auto-continue queue, so it inherits the
        /// sendability wait, the 60 s abandon path with its retraction
        /// note, and the crash-loop guard. The decision itself is
        /// AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted
        /// (pure, table-tested); this method only gathers the live inputs,
        /// carries the compile-error digest across (HUB-8 pair, consumed
        /// here when TryAutoContinueAfterCompile left it), bumps the
        /// in-a-row streak, and says why when the streak cap blocks it.
        /// Never sends synchronously: same HUB-6 rationale as
        /// <see cref="QueueAutoContinueMessage"/>.
        /// </summary>
        internal static void TryAutoContinueInterruptedTurn()
        {
            bool enabled = PanelStateStore.instance.Settings.autoContinueInterruptedTurn;
            bool otherPending = !string.IsNullOrEmpty(_pendingAutoContinueMessage);
            // No in-a-row cap any more (design note 2026-09-10-auto-approve-
            // all-tools-and-lean-auto-continue section 3): the crash-loop
            // suspension and the user's Interrupt are the only brakes.
            if (!AutoContinueAfterCompilePolicy.ShouldAutoContinueInterrupted(
                    enabled, _resumedMidTurn, IsCrashLoopSuspended, otherPending))
            {
                return;
            }

            // Compile-error carry-over: TryAutoContinueAfterCompile consumes
            // the HUB-8 pair only when IT acts; otherwise it is still here
            // and describes the domain that just went down. Fall back to
            // the live provider (visible errors only, FormatDigest's basis).
            string digest = null;
            if (SessionStateBridge.LastCompileHadErrors)
            {
                digest = !string.IsNullOrEmpty(SessionStateBridge.LastCompileErrorDigest)
                    ? SessionStateBridge.LastCompileErrorDigest
                    : ConsoleErrorProvider.FormatDigest();
            }
            else if (ConsoleErrorProvider.VisibleCount > 0)
            {
                digest = ConsoleErrorProvider.FormatDigest();
            }
            SessionStateBridge.ClearLastCompileResult();

            _pendingAutoContinueIsInterruption = true;
            // Design note 2026-09-10 section 3: fold in the tool name
            // ConsumeReloadDroppedPermission stashed one step earlier in
            // this same tick, then clear it -- the continuation being
            // composed right now is what consumes it.
            string pendingPermissionTool = _lastReloadDroppedPermissionTool;
            _lastReloadDroppedPermissionTool = null;
            QueueAutoContinueMessage(
                AutoContinueAfterCompilePolicy.ComposeInterruptedContinuationMessage(digest, pendingPermissionTool));
        }

        /// <summary>
        /// Queues <paramref name="message"/> for
        /// <see cref="TrySendPendingAutoContinueMessage"/> and hooks the
        /// drain tick. Deliberately does NOT attempt a synchronous send
        /// (HUB-6): TryAutoContinueAfterCompile runs from
        /// ReloadLifecycle.OnFirstUpdate BEFORE
        /// <see cref="RestoreAfterReload"/>, and an immediate send from
        /// here would EnsureStarted() a client and open a new turn ahead of
        /// the resume -- clobbering the SessionStateBridge.TurnRunning flag
        /// RestoreAfterReload reads to detect a mid-turn resume, and
        /// spawning the CLI before the transcript/session restore had run.
        /// Queue-and-hook means the actual send happens on the first
        /// EditorApplication.update tick, by which point OnFirstUpdate has
        /// completed the whole restore sequence.
        /// </summary>
        private static void QueueAutoContinueMessage(string message)
        {
            _pendingAutoContinueMessage = message;
            if (_autoContinueHooked)
            {
                return;
            }
            _autoContinueHooked = true;
            _autoContinueWaitStartedAt = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update += OnAutoContinueSendTick;
        }


        /// <summary>
        /// HUB-6: releases whatever <see cref="TryAutoContinueAfterCompile"/>
        /// queued. Called by ReloadLifecycle.OnFirstUpdate AFTER
        /// <see cref="RestoreAfterReload"/>, which is the entire point of
        /// the split: the queue step must not send, because sending
        /// EnsureStarted()s a client and opens a new turn -- ahead of the
        /// resume, that clobbers the SessionStateBridge.TurnRunning flag
        /// RestoreAfterReload reads to detect a mid-turn resume, and
        /// spawns the CLI before the session/transcript restore has run.
        /// Safe and silent when nothing is queued.
        /// </summary>
        internal static void StartAutoContinueDrain()
        {
            if (string.IsNullOrEmpty(_pendingAutoContinueMessage))
            {
                return;
            }
            if (TrySendPendingAutoContinueMessage())
            {
                UnhookAutoContinueSendTick();
            }
        }

        /// <summary>
        /// Attempts the queued auto-continuation send once. Returns true
        /// (and clears the queue) only on a CONFIRMED wire write -- mirrors
        /// SendUserMessage's own all-or-nothing contract. Marks
        /// SessionStateBridge.AutoContinueTurnIsContinuation immediately
        /// before attempting the send, and rolls it back to false on
        /// failure, so a failed attempt can never leave a LATER, genuine
        /// turn wrongly tagged as a continuation.
        ///
        /// Deliberately does NOT reuse CompileGate's shared queue: that
        /// queue can already hold messages queued from BEFORE this reload
        /// (a user follow-up typed while the previous turn was busy/
        /// compiling, persisted via SessionStateBridge.PendingSendsJson).
        /// A single ambient "the next thing CompileGate sends is mine" flag
        /// would misattribute whichever message happens to drain first
        /// whenever that pre-existing queue is non-empty. Owning a
        /// dedicated, single-message queue removes the ambiguity entirely:
        /// this method is the only thing that ever sends
        /// _pendingAutoContinueMessage, and it marks the continuation flag
        /// in the same call that confirms the send, with no shared state
        /// in between.
        ///
        /// Defect 3 fix (2026-08-04): AppendAutoContinueResumingNoteIfNeeded
        /// -- the "panel is sending the next message" guardrail-3 note --
        /// is now called from HERE, immediately before the actual send
        /// attempt below, instead of unconditionally back in
        /// TryAutoContinueAfterCompile the moment it merely DECIDED to
        /// continue. For the common case (client already sendable) this
        /// method runs synchronously from QueueAutoContinueMessage's first
        /// attempt, so the note and the send happen atomically, in the same
        /// call, with no window for them to diverge. For the queued case
        /// (client still Starting) the note now waits until a send is
        /// actually about to be attempted, and AbandonPendingAutoContinueMessage
        /// posts a retraction if that attempt never pans out -- see its own
        /// doc comment.
        ///
        /// Defect 4 fix (2026-08-04): checks the crash-loop guard BEFORE
        /// ever touching the client. OnProcessDied stops restarting once
        /// _consecutiveDeaths exceeds MaxConsecutiveRestarts and posts its
        /// own "connection suspended" note -- without this check, this
        /// method would call EnsureStarted() on every single
        /// OnAutoContinueSendTick tick for the whole 60-second drain
        /// window, respawning a REAL claude process each time and fighting
        /// the very suspension the panel just told the user about.
        /// </summary>
        private static bool TrySendPendingAutoContinueMessage()
        {
            if (string.IsNullOrEmpty(_pendingAutoContinueMessage))
            {
                return false;
            }
            if (UnityEditor.EditorApplication.isCompiling)
            {
                return false;
            }
            if (IsCrashLoopSuspended)
            {
                AbandonPendingAutoContinueMessage("the CLI connection is suspended after repeated crashes");
                return false;
            }
            if (_client == null
                || _client.State == AgentClientState.NotStarted
                || _client.State == AgentClientState.Errored)
            {
                EnsureStarted();
            }
            bool sendable = _client != null
                && (_client.State == AgentClientState.Ready
                    || _client.State == AgentClientState.Streaming
                    || _client.State == AgentClientState.ToolRunning);
            if (!sendable)
            {
                return false;
            }
            AppendAutoContinueResumingNoteIfNeeded();
            string message = _pendingAutoContinueMessage;
            SessionStateBridge.AutoContinueTurnIsContinuation = true;
            bool sent;
            _suppressTranscriptBubble = true;
            try
            {
                sent = SendUserMessage(message, message, null);
            }
            finally
            {
                _suppressTranscriptBubble = false;
            }
            if (!sent)
            {
                SessionStateBridge.AutoContinueTurnIsContinuation = false;
                return false;
            }
            _pendingAutoContinueMessage = null;
            SessionStateBridge.AutoContinuePendingSendUnconfirmed = false;
            if (_pendingAutoContinueIsInterruption)
            {
                // The interrupted turn has been continued: the ResumeBanner
                // must stop offering a Continue button for it.
                _pendingAutoContinueIsInterruption = false;
                _resumedMidTurn = false;
                RaiseChanged();
            }
            return true;
        }

        /// <summary>
        /// Appends guardrail 3's "the panel is sending the next message
        /// automatically" note exactly once per queued continuation --
        /// idempotent via SessionStateBridge.AutoContinuePendingSendUnconfirmed,
        /// which doubles as "has this note already been shown for the
        /// currently pending message" (see that field's own doc comment).
        /// Without the guard, a continuation that loses the sendability
        /// race on one tick and succeeds on a LATER tick would otherwise
        /// show this note twice.
        /// </summary>
        private static void AppendAutoContinueResumingNoteIfNeeded()
        {
            if (SessionStateBridge.AutoContinuePendingSendUnconfirmed)
            {
                return;
            }
            SessionStateBridge.AutoContinuePendingSendUnconfirmed = true;
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            // Localized, like every other transcript note this class adds
            // (HubScriptGateAutoDeniedFmt, HubTurnNonUndoableWarningFmt).
            // The CONTINUATION's own text (composed by
            // AutoContinueAfterCompilePolicy.ComposeContinuationMessage) is
            // deliberately not -- that one is model-facing wire content,
            // same as the UapOps steering section.
            note.Add(ChatMessageBlock.MakeSystemNote(_pendingAutoContinueIsInterruption
                ? L10n.S.HubAutoContinueInterruptedResuming
                : L10n.S.HubAutoContinueResuming));
            Session.AddMessage(note);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        /// <summary>
        /// HUB-9: appends the "the gate cannot block anything" warning to
        /// the transcript, once per spawn (_scriptGateInertWarned is reset
        /// by TearDownClient along with the rest of the per-spawn state),
        /// and logs it. Same MakeSystemNote(warning) shape the other hub
        /// notes use.
        /// </summary>
        private static void AppendScriptGateInertWarning()
        {
            if (_scriptGateInertWarned)
            {
                return;
            }
            _scriptGateInertWarned = true;
            Log(L10n.S.HubScriptGateInertWarning);
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            note.Add(ChatMessageBlock.MakeSystemNote(L10n.S.HubScriptGateInertWarning, true));
            Session.AddMessage(note);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        /// <summary>
        /// Drops the queued auto-continuation message and, ONLY when
        /// AppendAutoContinueResumingNoteIfNeeded had already announced it
        /// to the transcript, appends a loud retraction -- defect 3/4 fix
        /// (2026-08-04). Before this existed, the two ways a queued
        /// continuation could die both left the persisted transcript
        /// asserting as settled fact that a turn had been sent when the
        /// CLI never received it: the 60-second drain timeout
        /// (OnAutoContinueSendTick) used to only Debug.Log the give-up,
        /// never touch the saved Session, and a SECOND domain reload wiped
        /// AgentHub's plain _pendingAutoContinueMessage static (statics do
        /// not survive a reload) before the first reload's queued send ever
        /// got a sendable client. The second case is handled by
        /// <see cref="ReconcileInterruptedAutoContinueSend"/> calling this
        /// same method on the NEXT startup, using
        /// SessionStateBridge.AutoContinuePendingSendUnconfirmed (which DOES
        /// survive the reload) to know a retraction is owed.
        ///
        /// <paramref name="reasonForLog"/> is both Console diagnostic text
        /// and the {0} slot of the transcript retraction, so it has to read
        /// as an explanation to a person, not only to a log reader.
        /// </summary>
        private static void AbandonPendingAutoContinueMessage(string reasonForLog)
        {
            bool alreadyAnnounced = SessionStateBridge.AutoContinuePendingSendUnconfirmed;
            _pendingAutoContinueMessage = null;
            _pendingAutoContinueIsInterruption = false;
            SessionStateBridge.AutoContinuePendingSendUnconfirmed = false;
            UnhookAutoContinueSendTick();
            Log("Auto-continue: " + reasonForLog + "; dropping the queued continuation turn.");
            if (!alreadyAnnounced)
            {
                // Nothing was ever shown promising a send -- there is
                // nothing to retract.
                return;
            }
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            note.Add(ChatMessageBlock.MakeError(
                L10n.F(L10n.S.HubAutoContinueSendAbandonedFmt, reasonForLog)));
            Session.AddMessage(note);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        /// <summary>
        /// Called by ReloadLifecycle.OnFirstUpdate on EVERY startup,
        /// unconditionally, alongside TryAutoContinueAfterCompile -- defect
        /// 3 fix (2026-08-04) for the "a second domain reload wipes the
        /// plain _pendingAutoContinueMessage static" half of that defect.
        /// SessionStateBridge.AutoContinuePendingSendUnconfirmed is the one
        /// piece of this feature's bookkeeping that DOES survive a reload
        /// (SessionState-backed); if it is still true here, the PREVIOUS
        /// domain's AppendAutoContinueResumingNoteIfNeeded announced a send
        /// that the domain reload interrupted before AbandonPendingAutoContinueMessage
        /// or a confirmed SendUserMessage ever got to resolve it one way or
        /// the other. Reuses AbandonPendingAutoContinueMessage to post the
        /// same retraction note this feature's other give-up path uses --
        /// _pendingAutoContinueMessage/_autoContinueHooked are already at
        /// their fresh-domain defaults here, so those two lines are no-ops,
        /// leaving only the flag clear and the retraction note.
        /// </summary>
        internal static void ReconcileInterruptedAutoContinueSend()
        {
            if (!SessionStateBridge.AutoContinuePendingSendUnconfirmed)
            {
                return;
            }
            AbandonPendingAutoContinueMessage("a domain reload interrupted the send before it could be confirmed");
        }

        private static void OnAutoContinueSendTick()
        {
            if (TrySendPendingAutoContinueMessage())
            {
                UnhookAutoContinueSendTick();
                return;
            }
            if (string.IsNullOrEmpty(_pendingAutoContinueMessage))
            {
                // Nothing left to wait for (a caller other than this tick
                // consumed or cleared it -- defensive, not expected today).
                UnhookAutoContinueSendTick();
                return;
            }
            // HUB-5: a compile the send is BLOCKED on must not consume the
            // drain budget -- re-base the window while it runs, the same
            // way CompileGate.OnUpdate has always reset its own wait start.
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            bool isCompiling = UnityEditor.EditorApplication.isCompiling;
            if (isCompiling)
            {
                _autoContinueWaitStartedAt = now;
                return;
            }
            if (AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout(
                now, _autoContinueWaitStartedAt, isCompiling, AutoContinueDrainTimeoutSeconds))
            {
                AbandonPendingAutoContinueMessage(
                    "the client never became sendable within " + (int)AutoContinueDrainTimeoutSeconds + "s");
            }
        }

        private static void UnhookAutoContinueSendTick()
        {
            if (!_autoContinueHooked)
            {
                return;
            }
            _autoContinueHooked = false;
            UnityEditor.EditorApplication.update -= OnAutoContinueSendTick;
        }

        /// <summary>
        /// Discards any pending auto-continue state (Phase 5c L3 item 3)
        /// for a conversation that is about to be replaced entirely
        /// (StartFresh/SwitchToSession) or explicitly reconnected
        /// (Reconnect) -- called from those three methods only, NEVER from
        /// TearDownClient itself. TearDownClient underlies
        /// ShutdownForReload, which runs on EVERY domain reload including
        /// the exact one this feature reacts to; clearing these tickets
        /// there would erase them moments before the one reload they exist
        /// to survive. A plain Reconnect/StartFresh/SwitchToSession, by
        /// contrast, is an explicit signal that whatever was about to
        /// happen is no longer being waited for.
        ///
        /// Also resets the arm timestamp and the send-unconfirmed flag
        /// added by the 2026-08-04 defect 1/3 fixes -- both are only ever
        /// meaningful alongside AutoContinuePendingAttribution, so leaving
        /// either behind here would be inert but confusing to a future
        /// reader. No retraction note is posted for a dangling
        /// send-unconfirmed flag here (unlike AbandonPendingAutoContinueMessage):
        /// all three callers already replace or reconnect the ENTIRE
        /// session/client, which is either about to discard this transcript
        /// outright (StartFresh/SwitchToSession) or is itself the user's own
        /// deliberate, visible action (Reconnect) -- not a silent failure
        /// that needs explaining after the fact.
        /// </summary>
        private static void ClearAutoContinuePendingState()
        {
            SessionStateBridge.AutoContinueTurnIsContinuation = false;
            SessionStateBridge.AutoContinuePendingAttribution = false;
            SessionStateBridge.AutoContinuePendingWasContinuation = false;
            SessionStateBridge.AutoContinuePendingArmedAtUtcTicks = 0;
            SessionStateBridge.AutoContinuePendingSendUnconfirmed = false;
        }

        // -- User actions --------------------------------------------------------

        /// <summary>
        /// Sends the text to the CLI and, only when the wire write actually
        /// happened, appends the user message to the session. Returns false
        /// without touching the transcript when the client is missing or in
        /// a non-sendable state (Starting/WaitingPermission/Errored) -- the
        /// caller (CompileGate) queues the text and retries, so a message
        /// can never render as delivered while the CLI never received it.
        /// UI code must go through CompileGate.SendOrQueue.
        /// </summary>
        public static bool SendUserText(string text)
        {
            return SendUserMessage(text, null, null);
        }

        /// <summary>
        /// Structured variant: wireText goes to the CLI unchanged (the
        /// full ContextBlockFormatter.Compose form when context chips were
        /// attached), while the transcript stores displayText (the user's
        /// own words) plus one contextAttachment block per attachment --
        /// wire and display are decoupled (the raw delimited section never
        /// renders in the UI). displayText null/empty falls back to
        /// wireText; attachments may be null. UI code must still go
        /// through CompileGate.SendOrQueue, never call this directly --
        /// this method returns false instead of queueing during a compile.
        /// </summary>
        public static bool SendUserMessage(string wireText, string displayText,
            IList<ContextAttachment> attachments)
        {
            return SendUserMessage(wireText, displayText, attachments, null);
        }

        /// <summary>
        /// As the 3-arg overload, plus image attachments (design note
        /// 2026-09-07 section 2.3.4): each file is read and base64-encoded
        /// at the moment of sending (the queue never holds bytes) and goes
        /// out as an image content block after the text. A file that has
        /// vanished (Library cleanup, manual delete) is skipped and named
        /// in a note appended to the wire text, so the model knows an
        /// image was meant to be there. The transcript gets one Image block
        /// per image that was actually sent.
        /// </summary>
        public static bool SendUserMessage(string wireText, string displayText,
            IList<ContextAttachment> attachments, IList<ImageAttachment> images)
        {
            bool hasImages = images != null && images.Count > 0;
            if (string.IsNullOrEmpty(wireText) && !hasImages)
            {
                return false;
            }
            EnsureStarted();
            if (_client == null)
            {
                return false;
            }
            if (_client.State != AgentClientState.Ready
                && _client.State != AgentClientState.Streaming
                && _client.State != AgentClientState.ToolRunning)
            {
                return false;
            }
            var blocks = new List<OutboundMessages.ImageBlock>();
            var sentImages = new List<ImageAttachment>();
            var missing = new List<string>();
            if (hasImages)
            {
                for (int i = 0; i < images.Count; i++)
                {
                    ImageAttachment image = images[i];
                    if (image == null || string.IsNullOrEmpty(image.path))
                    {
                        continue;
                    }
                    byte[] bytes;
                    try
                    {
                        bytes = System.IO.File.Exists(image.path) ? System.IO.File.ReadAllBytes(image.path) : null;
                    }
                    catch (Exception)
                    {
                        bytes = null;
                    }
                    if (bytes == null || bytes.Length == 0)
                    {
                        missing.Add(string.IsNullOrEmpty(image.sourceName) ? image.path : image.sourceName);
                        continue;
                    }
                    blocks.Add(new OutboundMessages.ImageBlock
                    {
                        MediaType = string.IsNullOrEmpty(image.mediaType) ? ImageAttachmentPolicy.PngMediaType : image.mediaType,
                        Base64Data = Convert.ToBase64String(bytes)
                    });
                    sentImages.Add(image);
                }
            }
            string wire = wireText ?? string.Empty;
            if (missing.Count > 0)
            {
                wire = (wire.Length > 0 ? wire + "\n\n" : string.Empty)
                    + "(An attached image could not be sent because its file is gone: "
                    + string.Join(", ", missing.ToArray()) + ")";
            }
            if (wire.Length == 0 && blocks.Count == 0)
            {
                return false;
            }
            int handoverFrom = SessionStateBridge.HandoverPendingFrom;
            if (handoverFrom >= 0)
            {
                // First message to an agent that took over mid-conversation:
                // prepend the transcript so far (the bubble shows only the
                // user's text; Session.messages does not yet hold it).
                string handover = ConversationHandover.Build(Session.messages,
                    AgentBackends.DisplayName((AgentBackend)handoverFrom),
                    AgentBackends.DisplayName(CurrentBackend));
                wire = ConversationHandover.Compose(handover, wire);
                SessionStateBridge.HandoverPendingFrom = -1;
            }
            if (!_client.SendUserContent(wire, blocks))
            {
                // Refused or the write failed (death path already ran).
                return false;
            }
            // Phase 5a design section 1.3/8.2: one Undo group + one
            // Refresh-suppression window per EXECUTING turn. Idempotent --
            // a mid-turn follow-up send (queued text, "continue") is a
            // no-op here because the scope is already open; only
            // OnTurnCompleted closes it.
            UapTurnScope.BeginIfNeeded();
            // A "/compact" the panel itself sent is a compaction the user
            // is now waiting on: show the indicator from the send, not from
            // the CLI's status line (which older CLIs never emit and which
            // arrives only after the command is parsed). The boundary or
            // the turn's result ends it either way.
            string sentCommand;
            string sentArgs;
            if (SlashCommandCatalog.TryParse(wire, out sentCommand, out sentArgs)
                && SlashCommandCatalog.IsCompact(sentCommand))
            {
                _compacting = true;
            }
            string shown = string.IsNullOrEmpty(displayText) ? wire : displayText;
            // Design note 2026-09-10-auto-approve-all-tools-and-lean-auto-
            // continue section 3: an automatic continuation is announced
            // by its one-line system note (AppendAutoContinueResumingNoteIfNeeded)
            // and gets NO user bubble -- the bubble repeated a message the
            // user did not write and the note already covered.
            if (!_suppressTranscriptBubble)
            {
                var message = new ChatMessage
                {
                    role = ChatMessage.RoleUser,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    turnId = _client.CurrentTurnId,
                    delivered = true
                };
                if (!string.IsNullOrEmpty(shown))
                {
                    message.Add(ChatMessageBlock.MakeText(shown));
                }
                for (int i = 0; i < sentImages.Count; i++)
                {
                    message.Add(ChatMessageBlock.MakeImage(sentImages[i].path, sentImages[i].sourceName));
                }
                if (attachments != null)
                {
                    for (int i = 0; i < attachments.Count; i++)
                    {
                        ContextAttachment attachment = attachments[i];
                        if (attachment != null && !string.IsNullOrEmpty(attachment.payload))
                        {
                            message.Add(ChatMessageBlock.MakeContextAttachment(
                                attachment.title, attachment.payload));
                        }
                    }
                }
                Session.EnsureTitleFrom(shown);
                Session.AddMessage(message);
            }
            _streamingAssistant = null;
            SessionStateBridge.TurnRunning = _client.TurnActive;
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Answers the pending can_use_tool prompt (PermissionCard input).
        /// The caller must pass the request id its UI was built from: a
        /// decision is only applied when it matches the currently pending
        /// request, so a deferred callback (GenericMenu "Always for this
        /// session...") can never answer a LATER request with a decision --
        /// or updatedPermissions -- captured for an earlier one. Sends the
        /// control_response through the client, then -- for a DENY only
        /// (2026-08-02 design note section 2) -- collapses the card into a
        /// compact permanent transcript row ("Denied Bash" style). An ALLOW
        /// no longer gets a transcript note at all: the tool-use card
        /// already shows the call, its input and its result, and the note
        /// was measured to be 32-of-36 messages of pure noise in one
        /// working session ("<Tool> approved", nothing else). The caller
        /// may still pass a custom row text to override the default DENY
        /// wording; it is ignored on ALLOW. No-op when nothing is pending.
        ///
        /// Returns TRUE only when the decision was applied to the live
        /// pending request (the control_response was submitted); FALSE on
        /// every drop path (nothing pending, no client, stale/mismatched
        /// request id). UICODE-6: this return value is the ONE staleness
        /// authority -- PermissionCard's Always-allow menu gates its
        /// DURABLE panel-side rule persistence
        /// (PersistAcceptedRuleIfApplicable) on it, so a menu callback
        /// firing after the request moved on can neither answer the wire
        /// NOR permanently widen PanelSettings.allowedTools.
        /// </summary>
        public static bool RespondToPendingPermission(string requestId,
            PermissionDecision decision, string transcriptNote = null)
        {
            if (decision == null || _pendingPermission == null || _client == null)
            {
                return false;
            }
            if (string.IsNullOrEmpty(requestId)
                || requestId != _pendingPermission.RequestId)
            {
                Log("Dropped permission decision for request '" + (requestId ?? "<null>")
                    + "': the pending request is now '" + _pendingPermission.RequestId
                    + "' (stale UI callback).");
                return false;
            }
            ControlRequestMessage request = _pendingPermission;
            _pendingPermission = null;
            _client.RespondToPermission(request.RequestId, decision);

            // Skip the transcript row when the write killed the process --
            // the death path already adds its own error note and the tool
            // never ran, so "Denied ..." would be misleading (it wasn't a
            // deliberate denial, the process is gone). ALLOW never wrote a
            // row here in the first place (see doc comment above).
            if (!decision.Allow && _client != null && _client.State != AgentClientState.Errored)
            {
                var note = new ChatMessage
                {
                    role = ChatMessage.RoleSystem,
                    timestamp = DateTime.UtcNow.ToString("o")
                };
                note.Add(ChatMessageBlock.MakeSystemNote(
                    string.IsNullOrEmpty(transcriptNote)
                        ? DescribeDeniedPermission(request)
                        : transcriptNote));
                Session.AddMessage(note);
                SessionCache.Save(Session, _lastModelUsage);
            }
            // Re-arm the queued-send drain: text typed while the card was
            // up ("deny + type an alternative") is queued by CompileGate,
            // whose update hook gives up after 60 s. Answering the prompt
            // is what makes the client sendable again, so kick the drain
            // here or the queued message sits invisibly until the next
            // manual send or domain reload.
            CompileGate.DrainPending();
            RaiseChanged();
            return true;
        }

        /// <summary>Default transcript row text for a DENY decision.</summary>
        private static string DescribeDeniedPermission(ControlRequestMessage request)
        {
            string name = L10n.S.HubPermissionDefaultToolName;
            CanUseToolRequest tool = request.CanUseTool;
            if (tool != null)
            {
                name = !string.IsNullOrEmpty(tool.DisplayName) ? tool.DisplayName
                    : (!string.IsNullOrEmpty(tool.ToolName) ? tool.ToolName : name);
            }
            return L10n.F(L10n.S.HubPermissionDeniedFmt, name);
        }

        // -- Settings auto-apply (docs/design-notes/2026-08-01-settings-auto-apply.md) --

        /// <summary>
        /// Entry point for the settings auto-apply feature: call this from
        /// every SettingsView change-commit point (text field blur/commit,
        /// toggle/list change, custom-instructions edit). Coalesces rapid
        /// successive edits behind a debounce window
        /// (AutoApplySettingsPolicy.DebounceSeconds), then applies via the
        /// existing same-session <see cref="Reconnect"/> path -- immediately
        /// when the client is idle, or deferred until the running turn's
        /// TurnCompleted (with no permission left pending) otherwise. No-op
        /// when nothing is currently connected: EnsureStarted/a manual
        /// Reconnect will read the current settings on the next spawn
        /// regardless, so there is nothing to coalesce or defer. Also no-op
        /// when the current settings do not actually require a reconnect
        /// (SettingsChangeDetector), e.g. a permission-mode or Ctrl+Enter
        /// change, which already applies immediately/at next session on its
        /// own.
        /// </summary>
        public static void RequestAutoApplyReconnect()
        {
            if (!IsAutoApplyEligibleClientState() || !ComputeAutoApplyReconnectNeeded())
            {
                CancelAutoApply();
                return;
            }
            _autoApplyLastEditTime = UnityEditor.EditorApplication.timeSinceStartup;
            _autoApplyPending = true;
            _autoApplyDeferred = false;
            HookAutoApplyTick();
            RaiseChanged();
        }

        /// <summary>
        /// True when the client is connected enough for auto-apply to act on
        /// (Ready/Streaming/ToolRunning/WaitingPermission) -- NotStarted,
        /// Starting (a spawn/reconnect is already underway) and Errored all
        /// count as "not connected" here, matching the design note's "not
        /// connected (not spawned) -&gt; do nothing" rule.
        /// </summary>
        private static bool IsAutoApplyEligibleClientState()
        {
            return _client != null
                && _client.State != AgentClientState.NotStarted
                && _client.State != AgentClientState.Starting
                && _client.State != AgentClientState.Errored;
        }

        /// <summary>
        /// Recomputes whether the CURRENT settings/custom-instructions still
        /// differ from what the live client was spawned with -- the same
        /// comparison SettingsView's "Reconnect now" hint uses, read fresh
        /// each time rather than trusting a stale reconnectNeeded captured
        /// at edit time (a user could revert a field before the debounce
        /// elapses).
        /// </summary>
        private static bool ComputeAutoApplyReconnectNeeded()
        {
            string currentCustomInstructions =
                CustomInstructionsFile.CreateDefault(GetProjectRoot(), Log).Load();
            return SettingsChangeDetector.RequiresReconnect(
                _lastSpawnedSettingsSnapshot, PanelStateStore.instance.Settings,
                _lastSpawnedCustomInstructions, currentCustomInstructions);
        }

        /// <summary>Clears all pending/deferred auto-apply state and unhooks the debounce tick. Never touches _autoApplyInFlight.</summary>
        private static void CancelAutoApply()
        {
            _autoApplyPending = false;
            _autoApplyDeferred = false;
            UnhookAutoApplyTick();
        }

        private static void HookAutoApplyTick()
        {
            if (_autoApplyTickHooked)
            {
                return;
            }
            _autoApplyTickHooked = true;
            UnityEditor.EditorApplication.update += OnAutoApplyTick;
        }

        private static void UnhookAutoApplyTick()
        {
            if (!_autoApplyTickHooked)
            {
                return;
            }
            _autoApplyTickHooked = false;
            UnityEditor.EditorApplication.update -= OnAutoApplyTick;
        }

        /// <summary>
        /// Per-frame debounce poll (unhooked as soon as a decision other
        /// than DoNothing is reached -- once deferred, OnTurnCompleted
        /// alone drives the rest; once applied, there is nothing left to
        /// poll). Never runs after CancelAutoApply/UnhookAutoApplyTick.
        /// </summary>
        private static void OnAutoApplyTick()
        {
            if (!_autoApplyPending || _autoApplyDeferred)
            {
                UnhookAutoApplyTick();
                return;
            }
            TryAdvanceAutoApply();
        }

        /// <summary>
        /// Re-evaluates AutoApplySettingsPolicy against the CURRENT state and
        /// acts on the result. Called from the per-frame tick while
        /// debouncing, and from OnTurnCompleted once a change was deferred
        /// (that call re-checks busy/idle fresh -- a queued mid-turn send
        /// can keep the client busy, in which case this simply stays
        /// deferred for the next TurnCompleted).
        /// </summary>
        private static void TryAdvanceAutoApply()
        {
            bool isConnected = IsAutoApplyEligibleClientState();
            bool reconnectNeeded = isConnected && ComputeAutoApplyReconnectNeeded();
            if (!isConnected || !reconnectNeeded)
            {
                CancelAutoApply();
                return;
            }
            double secondsSinceLastEdit =
                UnityEditor.EditorApplication.timeSinceStartup - _autoApplyLastEditTime;
            bool turnRunning = _client != null && _client.TurnActive;
            bool pendingPermission = _pendingPermission != null;
            AutoApplyDecision decision = AutoApplySettingsPolicy.Evaluate(
                reconnectNeeded, isConnected, turnRunning, pendingPermission, secondsSinceLastEdit);
            switch (decision)
            {
                case AutoApplyDecision.ApplyNow:
                    UnhookAutoApplyTick();
                    _autoApplyPending = false;
                    _autoApplyDeferred = false;
                    ApplyAutoReconnect();
                    break;
                case AutoApplyDecision.DeferToTurnEnd:
                    // No more per-frame polling needed: OnTurnCompleted
                    // drives the rest. RaiseChanged so the pending pill can
                    // flip to its "apply after turn ends" text right away.
                    _autoApplyDeferred = true;
                    UnhookAutoApplyTick();
                    RaiseChanged();
                    break;
                case AutoApplyDecision.DoNothing:
                default:
                    // Still inside the debounce window: keep polling.
                    break;
            }
        }

        /// <summary>
        /// Applies a coalesced settings change via the existing same-session
        /// Reconnect() path. The actual Reconnect() call is deferred one
        /// tick via EditorApplication.delayCall -- exactly the pattern
        /// OnProcessDied's restart already uses -- because
        /// TryAdvanceAutoApply can itself be invoked from OnTurnCompleted,
        /// which runs synchronously on the CURRENT client's own Pump() call
        /// stack (EditorUpdatePump.OnEditorUpdate). Calling Reconnect()
        /// (which tears down and Disposes that same client) from inside
        /// that stack would be reentrant; deferring to the next update tick
        /// runs it top-level instead, once Pump() has fully returned.
        /// </summary>
        private static void ApplyAutoReconnect()
        {
            _autoApplyInFlight = true;
            RaiseChanged();
            int scheduledEpoch = _clientEpoch;
            UnityEditor.EditorApplication.delayCall += delegate
            {
                // Staleness guard: if anything else already tore down or
                // replaced the client in the meantime (manual Reconnect,
                // StartFresh, SwitchToSession, Shutdown, a crash restart),
                // this auto-apply is obsolete -- running it now would kill
                // whatever fresh client/session that other path already set
                // up. Whichever flow superseded this one will settle
                // _autoApplyInFlight through its own Ready/Errored
                // transition.
                if (scheduledEpoch != _clientEpoch)
                {
                    return;
                }
                Reconnect();
            };
        }

        // -- Client wiring --------------------------------------------------------

        private static bool StartClient(string resumeSessionId)
        {
            TearDownClient(false);
            LastError = null;

            PanelSettings settings = PanelStateStore.instance.Settings;
            string projectRoot = GetProjectRoot();
            IProcessKiller killer = CreateKiller();

            AgentBackend backend = settings.agentBackend;
            bool isAcp = AgentBackends.IsAcp(backend);
            if (resumeSessionId != null && !ResumeAllowedForBackend(Session.agentBackend, backend))
            {
                // Design note 2026-09-10-backend-switch-session.md: the
                // session id was issued by ANOTHER agent. Handing it to
                // this one either fails outright (Claude Code exits on an
                // unknown --resume id, which re-entered the reconnect loop
                // with the same id every time) or is silently replaced by
                // a new session. Start the new agent on a fresh session
                // instead; the transcript stays on screen and the old id
                // stays in the cache so switching back can still resume.
                var switchNote = new ChatMessage { role = ChatMessage.RoleSystem };
                switchNote.Add(ChatMessageBlock.MakeSystemNote(L10n.F(
                    L10n.S.HubSessionNotResumedAcrossBackendsNoteFmt,
                    AgentBackends.DisplayName((AgentBackend)Session.agentBackend),
                    AgentBackends.DisplayName(backend))));
                Session.AddMessage(switchNote);
                // The next user message carries the conversation over
                // (ConversationHandover) so the new agent can continue it.
                SessionStateBridge.HandoverPendingFrom = Session.agentBackend;
                resumeSessionId = null;
            }
            else if (resumeSessionId != null)
            {
                // Resuming the owning agent's own session: nothing to hand over.
                SessionStateBridge.HandoverPendingFrom = -1;
            }
            // A new process may be a different agent: forget the old name
            // until the new one introduces itself.
            CurrentAcpAgentName = null;
            string acpCommand = isAcp ? AgentBackends.EffectiveCommand(backend, settings.acpCommand) : null;
            ICliPathProbe probe = CreateCliPathProbe(settings);
            string cliPath = probe.Resolve();
            if (cliPath == null)
            {
                LastError = isAcp
                    ? L10n.F(L10n.S.HubAcpCommandNotFoundErrorFmt, AgentBackends.DisplayName(backend),
                        acpCommand, string.Join("; ", probe.DescribeCandidates()))
                    : L10n.F(L10n.S.HubCliNotFoundErrorFmt,
                        string.Join("; ", probe.DescribeCandidates()));
                Log(LastError);
                RaiseChanged();
                return false;
            }

            _reaper = new ZombieReaper(
                Path.Combine(projectRoot, "UserSettings", "AgentPanel", "cli-process.json"),
                killer, Log);
            _reaper.ReapOrphans();
            _reaperProcessName = ResolveReaperProcessName(isAcp, cliPath);

            // Design note 2026-09-10-acp-backends.md section 3: an ACP
            // backend gets the bridge transport, which ignores the Claude
            // flag string AgentClient builds and reads this spec instead.
            // The spec is filled in below (MCP endpoint, prompt) BEFORE
            // client.Start -- the bridge only reads it at Start.
            AcpLaunchSpec acpSpec = null;
            ICliTransport transport;
            if (isAcp)
            {
                acpSpec = new AcpLaunchSpec
                {
                    Backend = backend,
                    Arguments = AgentBackends.EffectiveArguments(backend, settings.acpCommand, settings.acpArguments),
                    ResumeSessionId = resumeSessionId,
                    Model = ResolveSpawnModel(resumeSessionId, settings.model),
                    PermissionMode = settings.permissionMode,
                    AuthMethodId = settings.acpAuthMethod,
                    ClientVersion = PackageVersionForClientInfo()
                };
                var acpTransport = new AcpBridgeTransport(acpSpec, killer, Log);
                acpTransport.AuthenticationStarted += OnAcpAuthenticationStarted;
                acpTransport.AuthenticationFinished += OnAcpAuthenticationFinished;
                transport = acpTransport;
            }
            else
            {
                transport = new ClaudeCliProcess(killer, Log);
            }
            ResetAcpSignInState();
            AcpSignInError = null;
            AcpAuthMethodId = null;
            AcpAuthMethodName = null;
            _acpAuthMethodIdInFlight = null;
            _acpAuthMethodNameInFlight = null;
            _acpSignInFailedThisProcess = false;
            _reachedReadyThisProcess = false;
            var client = new AgentClient(transport, Log);
            client.StateChanged += OnStateChanged;
            client.TextDelta += OnTextDelta;
            client.ThinkingDelta += OnThinkingDelta;
            client.AssistantMessageCompleted += OnAssistantMessageCompleted;
            client.ToolUseStarted += OnToolUseStarted;
            client.ToolResultReceived += OnToolResultReceived;
            client.TaskEventReceived += OnTaskEventReceived;
            client.ThinkingTokensReceived += OnThinkingTokensReceived;
            client.CompactBoundaryReceived += OnCompactBoundaryReceived;
            client.StatusReceived += OnStatusReceived;
            client.PermissionRequested += OnPermissionRequested;
            client.ControlRequestResolved += OnControlRequestResolved;
            client.TurnCompleted += OnTurnCompleted;
            client.TurnStalled += OnTurnStalled;
            client.SessionIdChanged += OnSessionIdChanged;
            client.InitMessageReceived += OnInitMessageReceived;
            client.ProcessDied += OnProcessDied;
            client.StderrLine += OnStderrLine;

            string customInstructions = CustomInstructionsFile.CreateDefault(projectRoot, Log).Load();
            // NOT an AgentClientOptions field (see AgentDefinitionFileWriter's
            // doc comment for why a --agents CLI argument was dropped in
            // favor of these files: R07 section 10 found --agents silently
            // ignored whenever --resume is also passed, which is always).
            // Unlike customInstructions above, this is NOT simply
            // "next-spawn-only": Sync() runs before every spawn, resumed or
            // new, but R07 section 10.8 ("capture15") found the CLI
            // snapshots `.claude/agents/*.md` at SESSION CREATION, so
            // rewriting the files immediately before a --resume spawn
            // (resumeSessionId non-null) has NO effect on that already-
            // existing session -- only a spawn with resumeSessionId == null
            // (a genuinely new session) actually picks up whatever Sync()
            // just wrote. SettingsChangeDetector deliberately does not
            // treat agentModelOverrides as reconnect-pending for this
            // reason (see its class doc comment).
            if (!isAcp)
            {
                AgentDefinitionFileWriter.CreateDefault(projectRoot, Log).Sync(settings.agentModelOverrides);
            }

            // UapOps (Phase 5a, design section 1.1/8.1): listen BEFORE
            // spawn -- the Unity-side server must already be accepting
            // connections when the CLI process starts, or MCP_CONNECT_
            // TIMEOUT_MS (docs/research/08-mcp-transport.md section 4.2,
            // default 5s) can lock the server into "failed" for the whole
            // process lifetime (section 3.2/3.3's measured asymmetry).
            // Master toggle OFF stops the resident HttpListener outright --
            // an explicitly disabled feature must not leave a loopback
            // socket open.
            string mcpConfigValue = null;
            if (settings.uapOpsEnabled)
            {
                UapOpsServer.EnsureStarted(Log);
                UapOpsServer.SetEnabledModules(settings.uapOpsModules);
                if (isAcp)
                {
                    // ACP agents take the MCP endpoint in session/new, not
                    // as a --mcp-config file; the Bearer token travels in
                    // the JSON-RPC params over the child's private stdin
                    // (never the command line), so SEC-2 holds here too.
                    acpSpec.McpServerName = UapOpsMcpConfig.ServerName;
                    acpSpec.McpUrl = "http://127.0.0.1:" + UapOpsServer.Port + "/mcp";
                    acpSpec.McpBearerToken = UapOpsServer.Token;
                }
                else
                {
                    mcpConfigValue = ComputeMcpConfigValue(
                        true, projectRoot, UapOpsServer.Port, UapOpsServer.Token, Log);
                }
            }
            else
            {
                UapOpsServer.Stop();
            }

            // Script validation gate hook (design section 8.7): generate/
            // refresh the PreToolUse hook + `--settings` JSON BEFORE spawn,
            // Windows-only for v1 (ShouldInstallScriptGateHook). Install
            // failure (logged inside EnsureInstalled) degrades to omitting
            // `--settings` for this spawn rather than failing the whole
            // spawn -- the can_use_tool layer (TryAutoDenyForScriptGate)
            // still applies regardless.
            string settingsFilePath = null;
            if (!isAcp && ShouldInstallScriptGateHook(settings.uapScriptGateEnabled, Application.platform))
            {
                settingsFilePath = GateHookInstaller.EnsureInstalled(projectRoot, Log);
            }
            // HUB-9: say so when the gate is on but both enforcement layers
            // are absent -- believing in a protection that is not running is
            // worse than knowing it is off.
            if (ScriptGateEffectivelyInert(settings.uapScriptGateEnabled,
                    settings.dangerouslySkipPermissions, Application.platform))
            {
                AppendScriptGateInertWarning();
            }

            // Extension Profiles (design section 3b/8.2 B3, C3): composed
            // fresh on every spawn -- the underlying detection is cached
            // per domain-load (ExtensionProfileDetectionCache), but the
            // master toggle/approved-hash list can change between spawns
            // without a domain reload, so this call itself is never cached.
            string profilesSection = ExtensionProfileService.ComposeAppendSection(
                projectRoot, settings.extensionProfilesEnabled, settings.approvedProfileHashes, Log);
            // Stream C1 (2026-08-02 design note section 3): steer toward
            // the typed uap_* tools instead of the general-purpose uloop
            // escape hatch, injected between the cost-policy line and the
            // profiles section. Pure/module-list-driven -- see
            // ComposeUapOpsSteeringSection's own doc comment.
            string uapOpsSteeringSection = ComposeUapOpsSteeringSection(
                settings.uapOpsEnabled, settings.uapOpsModules);
            // Stream C (docs/design-notes/2026-09-10-unity-official-plugin-
            // integration.md section 3): when Unity's official plugin is
            // installed in the CLI's user config, steer toward its /unity:*
            // skills, keep Editor control on uap_*, and flag Unity 6-only
            // guidance on older editors. Static detection only -- there is
            // no system/init yet at spawn time (section 1.1).
            string unityPluginSteeringSection = ComposeUnityPluginSteeringSection(
                UnityPluginProbe.ReadStatic(true), settings.unityPluginSteeringEnabled,
                settings.uapOpsEnabled, Application.unityVersion);

            string appendSystemPrompt = ComposeAppendSystemPrompt(
                customInstructions, settings.subagentCostPolicy,
                uapOpsSteeringSection, unityPluginSteeringSection, profilesSection);
            if (isAcp)
            {
                // ACP has no system-prompt flag: the bridge prepends this
                // to the first prompt of a new session instead. It must
                // NOT also ride AgentClientOptions -- the bridge ignores
                // the flag string, and CORE-7's command-line-length guard
                // would refuse a long instructions file for nothing.
                acpSpec.SystemPrompt = appendSystemPrompt;
                appendSystemPrompt = null;
            }

            try
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = cliPath,
                    WorkingDirectory = projectRoot,
                    ResumeSessionId = resumeSessionId,
                    Model = ResolveSpawnModel(resumeSessionId, settings.model),
                    PermissionMode = settings.permissionMode,
                    AllowedTools = isAcp ? null : settings.allowedTools,
                    DisallowedTools = isAcp ? null : settings.disallowedTools,
                    DangerouslySkipPermissions = !isAcp && settings.dangerouslySkipPermissions,
                    AppendSystemPrompt = appendSystemPrompt,
                    ThinkingDisplaySummarized = !isAcp && settings.showThinking,
                    SubagentModel = isAcp ? null : settings.subagentModel,
                    ClaudeAuth = isAcp ? ClaudeAuthMode.Auto : settings.claudeAuth,
                    McpConfigJson = mcpConfigValue,
                    SettingsFilePath = settingsFilePath,
                    InitializeTimeoutSeconds = isAcp ? AcpInitializeTimeoutSeconds : 0
                });
            }
            catch (Exception ex)
            {
                LastError = isAcp
                    ? L10n.F(L10n.S.HubAcpStartFailedFmt, AgentBackends.DisplayName(backend), ex.Message)
                    : L10n.F(L10n.S.HubCliStartFailedFmt, ex.Message);
                Log(LastError);
                client.Dispose();
                RaiseChanged();
                return false;
            }

            _client = client;
            _lastSpawnedSettingsSnapshot = CloneNextSpawnOnlyFields(settings);
            _lastSpawnedCustomInstructions = customInstructions;
            _reaper.Record(transport.ProcessId, transport.ProcessStartTimeUtcTicks, _reaperProcessName);
            SessionStateBridge.Pid = transport.ProcessId;
            SessionStateBridge.ProcessStartTicks = transport.ProcessStartTimeUtcTicks;
            EditorUpdatePump.Attach(client);
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Pure decision behind the fresh-vs-resume --model argument
        /// (docs/design-notes/2026-08-01-model-settings-rework.md section
        /// 4.1): a fresh spawn (resumeSessionId == null) passes the
        /// persisted default model; a --resume spawn passes null
        /// (AgentClientOptions.Model omits --model entirely when null/
        /// empty) so the CLI keeps the session's own model instead (R07
        /// section 3: an explicit --model always overrides it, even on
        /// resume -- passing the Settings default on every reconnect would
        /// silently drag a running session back to it, clobbering the
        /// header picker's live SwitchSessionModel switch). Internal test
        /// seam (AgentHubStartClientArgTests) so this decision is
        /// unit-tested without spawning a real CLI process.
        /// </summary>
        internal static string ResolveSpawnModel(string resumeSessionId, string defaultModel)
        {
            return resumeSessionId == null ? defaultModel : null;
        }

        /// <summary>
        /// Pure: whether a process death should schedule the bounded
        /// auto-reconnect. A death that followed a failed ACP sign-in
        /// (<paramref name="acpSignInError"/> non-null) must not -- the
        /// retry would fail identically until the user signs in.
        /// </summary>
        /// <summary>
        /// Pure: a cached session may be resumed only by the backend that
        /// issued it. -1 (unknown, pre-field caches) is allowed through --
        /// an ACP agent replaces an unknown id with a new session by
        /// itself, and a Claude cache written before the field existed was
        /// necessarily Claude's.
        /// </summary>
        internal static bool ResumeAllowedForBackend(int sessionBackend, AgentBackend target)
        {
            return sessionBackend < 0 || sessionBackend == (int)target;
        }

        /// <summary>
        /// Grace after a death within which a Starting->Ready transition
        /// does NOT clear the crash counter: a process that reaches Ready
        /// and dies seconds later, over and over, used to reset the
        /// counter on every round and reconnect forever. A completed turn
        /// (OnTurnCompleted) always clears it.
        /// </summary>
        internal const long CrashCounterResetGraceTicks = 60L * TimeSpan.TicksPerSecond;

        private static long _lastDeathUtcTicks;

        /// <summary>Pure decision behind the Ready-transition reset of the crash counter.</summary>
        internal static bool ShouldResetCrashCounterOnReady(long lastDeathUtcTicks, long nowUtcTicks)
        {
            return lastDeathUtcTicks <= 0 || nowUtcTicks - lastDeathUtcTicks >= CrashCounterResetGraceTicks;
        }

        internal static bool ShouldAutoReconnectAfterDeath(string acpSignInError)
        {
            return ShouldAutoReconnectAfterDeath(acpSignInError, false, true);
        }

        /// <summary>
        /// Full rule: no retry after a failed ACP sign-in, and no retry when
        /// an ACP agent died before its handshake ever completed (Grok
        /// Build exits with AuthorizationRequired instead of answering
        /// session/new when it is not signed in; a config typo behaves the
        /// same) -- an identical respawn cannot change either outcome.
        /// Claude Code keeps the bounded retry: its early deaths are the
        /// transient kind the crash-loop guard was written for.
        /// </summary>
        internal static bool ShouldAutoReconnectAfterDeath(string acpSignInError, bool isAcpBackend, bool reachedReady)
        {
            if (acpSignInError != null)
            {
                return false;
            }
            if (isAcpBackend && !reachedReady)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Initialize handshake budget for ACP backends: the agent may be
        /// waiting on a browser sign-in inside it (design note
        /// 2026-09-10-in-panel-install-and-sign-in.md section 2), which the
        /// Claude Code default of 60 s would cut off mid-flow.
        /// </summary>
        public const double AcpInitializeTimeoutSeconds = 600.0;

        /// <summary>
        /// The path probe for the selected backend: the platform Claude
        /// probe, or AcpCommandProbe over the effective ACP command
        /// (design note 2026-09-10-acp-backends.md section 3.4). Shared by
        /// StartClient and SettingsView so "Re-detect" and the spawn agree.
        /// </summary>
        public static ICliPathProbe CreateCliPathProbe(PanelSettings settings)
        {
            if (AgentBackends.IsAcp(settings.agentBackend))
            {
                return new AcpCommandProbe(
                    AgentBackends.EffectiveCommand(settings.agentBackend, settings.acpCommand));
            }
            return Application.platform == RuntimePlatform.WindowsEditor
                ? (ICliPathProbe)new WindowsCliPathProbe(settings.cliManualPath)
                : new UnixCliPathProbe(settings.cliManualPath);
        }

        /// <summary>
        /// Pure: the process name ZombieReaper must see for the spawned
        /// executable -- "claude" for Claude Code (the Windows probe traces
        /// the npm shim to node's claude.exe; the reaper contract predates
        /// this), the executable's own base name for an ACP command. A
        /// `.cmd` shim launches through cmd.exe, whose name will not match;
        /// the reaper then refuses to kill, which is the safe outcome.
        /// </summary>
        internal static string ResolveReaperProcessName(bool isAcp, string executablePath)
        {
            if (!isAcp)
            {
                return ZombieReaper.DefaultProcessName;
            }
            try
            {
                string name = Path.GetFileNameWithoutExtension(executablePath);
                return string.IsNullOrEmpty(name) ? ZombieReaper.DefaultProcessName : name;
            }
            catch (Exception)
            {
                return ZombieReaper.DefaultProcessName;
            }
        }

        private static string PackageVersionForClientInfo()
        {
            try
            {
                UnityEditor.PackageManager.PackageInfo info =
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AgentHub).Assembly);
                return info != null ? info.version : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// The decision behind AgentClientOptions.McpConfigJson (Phase 5a,
        /// arg-plumbing test seam mirroring ResolveSpawnModel/
        /// ComposeAppendSystemPrompt above): when
        /// <paramref name="uapOpsEnabled"/> is false, returns null so
        /// `--mcp-config`/`--strict-mcp-config` are omitted entirely
        /// (AgentClient.BuildArguments). When true, writes the config to
        /// a FILE (UapOpsMcpConfig.EnsureConfigFileWritten) and returns its
        /// absolute path -- SEC-2: the payload carries the UapOps Bearer
        /// token, and an inline `--mcp-config` argument would publish it on
        /// the world-readable process command line. Only when the file
        /// cannot be written does it fall back to the inline JSON (logged
        /// inside the writer; a broken UserSettings directory should
        /// degrade to the old exposure, not silently disable UapOps --
        /// forcing that failure requires local file control, i.e. an
        /// attacker who already has user rights). This method never
        /// starts/stops the server (StartClient does that before calling
        /// here), so a test can call it without a real HttpListener.
        /// </summary>
        internal static string ComputeMcpConfigValue(
            bool uapOpsEnabled, string projectRoot, int port, string token, Action<string> log)
        {
            if (!uapOpsEnabled)
            {
                return null;
            }
            string path = UapOpsMcpConfig.EnsureConfigFileWritten(projectRoot, port, token, log);
            return path != null ? path : UapOpsMcpConfig.BuildConfigJson(port, token);
        }

        /// <summary>
        /// Pure decision behind whether StartClient runs
        /// GateHookInstaller.EnsureInstalled and passes
        /// AgentClientOptions.SettingsFilePath at all (design section 8.7
        /// point 5: "non-Windows is v1 can_use_tool-only") -- unit-tested
        /// seam mirroring ResolveSpawnModel/ComputeMcpConfigValue above. The
        /// installer itself (file IO) is never called when this returns
        /// false: an explicitly-disabled gate must not leave the generated
        /// files around influencing a later manual `--settings` use, and a
        /// non-Windows spawn has nothing Windows-specific
        /// (`powershell -File`) to install in the first place.
        /// </summary>
        internal static bool ShouldInstallScriptGateHook(bool uapScriptGateEnabled, RuntimePlatform platform)
        {
            return uapScriptGateEnabled && platform == RuntimePlatform.WindowsEditor;
        }

        /// <summary>
        /// HUB-9: the combination where the script validation gate is ON
        /// and yet stops NOTHING. Two independent layers enforce it -- the
        /// generated PreToolUse hook (Windows only,
        /// <see cref="ShouldInstallScriptGateHook"/>) and the can_use_tool
        /// pre-filter (<see cref="TryAutoDenyForScriptGate"/>) -- and
        /// dangerouslySkipPermissions removes the second one by construction,
        /// because the CLI stops asking permission at all. Outside Windows
        /// that leaves zero layers: Assets/**/*.cs writes go straight
        /// through while the settings UI still says the gate is on. The
        /// gate's behavior is unchanged by this; what changes is that the
        /// hole is now stated out loud (spawn log + transcript note +
        /// Settings banner) instead of being silently believed in.
        /// </summary>
        internal static bool ScriptGateEffectivelyInert(
            bool uapScriptGateEnabled, bool dangerouslySkipPermissions, RuntimePlatform platform)
        {
            return uapScriptGateEnabled
                && dangerouslySkipPermissions
                && platform != RuntimePlatform.WindowsEditor;
        }

        /// <summary>
        /// Exact guidance line injected for
        /// <see cref="SubagentCostPolicy.HaikuForSimpleTasks"/> (docs/design-
        /// notes/2026-08-02-subagent-model-precedence.md section 3.2). Kept
        /// as a named constant (rather than inlined into
        /// <see cref="ComposeAppendSystemPrompt"/>) so a test can assert the
        /// exact wording without duplicating it.
        /// </summary>
        // Wording history: the first draft ("When you spawn subagents...,
        // pass model: \"haiku\" for simple mechanical subtasks...") was
        // live-tested 2026-08-02 and IGNORED twice in a row by a sonnet
        // parent, including on a literal file-listing task (usageKeys stayed
        // sonnet-only while the args provably carried the line). The
        // rule-formatted imperative below is the strengthened second
        // iteration; keep the MUST + explicit parameter shape if this is
        // ever reworded.
        internal const string HaikuForSimpleTasksInstructionLine =
            "IMPORTANT - subagent model selection rule: whenever you call the Agent (Task) tool"
            + " for a simple mechanical subtask (searching, file listing, bulk renaming, log"
            + " scanning, or similar read-and-report work), you MUST include model: \"haiku\" in"
            + " the tool input. Omit the model parameter only when the subtask genuinely needs"
            + " deeper reasoning, so it inherits the session model.";

        /// <summary>
        /// Pure composition of the --append-system-prompt payload (docs/
        /// design-notes/2026-08-02-subagent-model-precedence.md section
        /// 3.2): the custom-instructions sidecar text
        /// (CustomInstructionsFile) plus, ONLY when <paramref name="policy"/>
        /// is HaikuForSimpleTasks, <see cref="HaikuForSimpleTasksInstructionLine"/>
        /// joined after it with a blank line. AgentDecides (the default)
        /// injects nothing -- the result is exactly
        /// <paramref name="customInstructions"/>, unchanged, so an
        /// AgentDecides panel behaves identically to every version before
        /// this field existed. Null <paramref name="customInstructions"/> is
        /// treated as empty. Internal test seam (no CLI/PanelStateStore
        /// dependency) so both branches -- with and without custom
        /// instructions present -- are unit tested without spawning a real
        /// CLI process.
        /// </summary>
        internal static string ComposeAppendSystemPrompt(string customInstructions, SubagentCostPolicy policy)
        {
            string trimmedCustom = customInstructions ?? string.Empty;
            if (policy != SubagentCostPolicy.HaikuForSimpleTasks)
            {
                return trimmedCustom;
            }
            return trimmedCustom.Length == 0
                ? HaikuForSimpleTasksInstructionLine
                : trimmedCustom + "\n\n" + HaikuForSimpleTasksInstructionLine;
        }

        /// <summary>
        /// Overload additionally appending the Extension Profiles section
        /// (docs/design-notes/2026-08-01-phase5-unity-ops-design.md section
        /// 3b, C3: "extend the existing ComposeAppendSystemPrompt
        /// composition ... with a profiles section appended AFTER the
        /// cost-policy line"). <paramref name="profilesSection"/> is a
        /// pre-composed string (Colloid.AgentPanel.Ops.Profiles.
        /// ExtensionProfileService.ComposeAppendSection) rather than
        /// computed here, keeping this method itself free of any disk/
        /// TypeCache dependency -- exactly like <paramref name="customInstructions"/>
        /// is a pre-loaded string rather than a file path. Ordering is
        /// therefore fixed as: custom instructions, then (conditionally)
        /// the cost-policy line, then (conditionally) the profiles section
        /// -- each additional part joined with a blank line only when the
        /// accumulated body so far is non-empty, so an all-empty input
        /// still yields the shared empty-string result the 2-arg overload
        /// already guarantees.
        /// </summary>
        internal static string ComposeAppendSystemPrompt(string customInstructions, SubagentCostPolicy policy,
            string profilesSection)
        {
            string body = ComposeAppendSystemPrompt(customInstructions, policy);
            return AppendSection(body, profilesSection);
        }

        /// <summary>
        /// Overload additionally appending the UapOps tool-steering section
        /// (Stream C1, 2026-08-02 design note section 3: "ship the L2
        /// steering snippet ... injected next to the cost-policy line")
        /// BETWEEN the cost-policy line and the Extension Profiles section.
        /// Full production ordering: custom instructions, then
        /// (conditionally) the cost-policy line, then (conditionally) the
        /// UapOps steering section, then (conditionally) the profiles
        /// section. <paramref name="uapOpsSteeringSection"/> is a
        /// pre-composed string (<see cref="ComposeUapOpsSteeringSection"/>)
        /// exactly like <paramref name="profilesSection"/> is, keeping this
        /// method itself free of any PanelSettings/module-list dependency.
        /// The 3-arg overload above is UNCHANGED (still just custom +
        /// cost-policy + profiles) -- this is a new overload, not a
        /// modification, so every existing 2-/3-arg caller and test keeps
        /// its exact prior behavior.
        /// </summary>
        internal static string ComposeAppendSystemPrompt(string customInstructions, SubagentCostPolicy policy,
            string uapOpsSteeringSection, string profilesSection)
        {
            string body = ComposeAppendSystemPrompt(customInstructions, policy);
            body = AppendSection(body, uapOpsSteeringSection);
            return AppendSection(body, profilesSection);
        }

        /// <summary>Joins one already-composed section onto body with a blank line, unless section is empty.</summary>
        /// <summary>
        /// Overload additionally appending the Unity official plugin
        /// steering section (docs/design-notes/2026-09-10-unity-official-
        /// plugin-integration.md section 3.2) BETWEEN the UapOps steering
        /// section and the Extension Profiles section. Full production
        /// ordering: custom instructions, cost-policy line, UapOps
        /// steering, Unity plugin steering, profiles -- each joined with a
        /// blank line only when non-empty. The 4-arg overload above is
        /// UNCHANGED, same flavor as every earlier addition.
        /// </summary>
        internal static string ComposeAppendSystemPrompt(string customInstructions, SubagentCostPolicy policy,
            string uapOpsSteeringSection, string unityPluginSteeringSection, string profilesSection)
        {
            string body = ComposeAppendSystemPrompt(customInstructions, policy);
            body = AppendSection(body, uapOpsSteeringSection);
            body = AppendSection(body, unityPluginSteeringSection);
            return AppendSection(body, profilesSection);
        }

        /// <summary>Convenience over the pure overload: "installed" is the detector's EnabledNotLoaded or Loaded.</summary>
        internal static string ComposeUnityPluginSteeringSection(UnityPluginStatus status, bool steeringEnabled,
            bool uapOpsEnabled, string unityVersion)
        {
            bool installed = status != null
                && (status.State == UnityPluginState.EnabledNotLoaded || status.State == UnityPluginState.Loaded);
            return ComposeUnityPluginSteeringSection(installed, steeringEnabled, uapOpsEnabled, unityVersion);
        }

        /// <summary>
        /// Pure composition of the Unity official plugin steering section
        /// (design note section 3.2). Empty unless the plugin is installed
        /// AND the Settings toggle is on. Three lines at most: the routing
        /// line always; the "keep Editor control on uap_*" line only when
        /// UapOps is enabled (the plugin's unity-cli skill otherwise tells
        /// Claude to curl-install the `unity` CLI and drive the Editor
        /// through it, which the panel already does better and with Undo);
        /// the Unity 6 caveat only when the running editor is older than
        /// 6000 (or its version does not parse -- err toward the caveat).
        /// </summary>
        internal static string ComposeUnityPluginSteeringSection(bool pluginInstalled, bool steeringEnabled,
            bool uapOpsEnabled, string unityVersion)
        {
            if (!pluginInstalled || !steeringEnabled)
            {
                return string.Empty;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append(UnityPluginSteeringRoutingLine);
            if (uapOpsEnabled)
            {
                sb.Append('\n').Append(UnityPluginSteeringEditorControlLine);
                sb.Append('\n').Append(UnityPluginSteeringSearchLine);
            }
            if (!UnityVersionParser.IsUnity6OrNewer(unityVersion))
            {
                sb.Append('\n').Append(string.Format(UnityPluginSteeringUnity6CaveatFmt,
                    string.IsNullOrEmpty(unityVersion) ? "an unknown version" : unityVersion));
            }
            return sb.ToString();
        }

        internal const string UnityPluginSteeringRoutingLine =
            "Unity's official Claude Code plugin is installed, so skills named /unity:* are available."
            + " Route UI work through /unity:ui, package selection through /unity:unity-package-management,"
            + " and asset or scene lookups through /unity:generate-editor-search-query.";

        internal const string UnityPluginSteeringEditorControlLine =
            "Editor control is already provided by the uap_* tools of the \"unity-ops\" MCP server."
            + " Do NOT install or invoke the `unity` CLI, `unity mcp`, `unity command eval`, or the"
            + " com.unity.pipeline package for Editor control, even when /unity:unity-cli suggests it.";

        /// <summary>Stream D: the official search skill wants to open the Search window; the panel can run the query instead.</summary>
        internal const string UnityPluginSteeringSearchLine =
            "When /unity:generate-editor-search-query produces a Unity Search query, run it with the"
            + " uap_search tool (providers \"asset\" and/or \"scene\") and use the returned paths instead of"
            + " opening the Search window.";

        /// <summary>{0} = Application.unityVersion.</summary>
        internal const string UnityPluginSteeringUnity6CaveatFmt =
            "This project runs Unity {0}. The plugin's skills target Unity 6+; skip guidance that requires"
            + " Unity 6 (UI Toolkit runtime data binding, Render Graph, com.unity.pipeline) and prefer the"
            + " 2022 LTS equivalents.";

        private static string AppendSection(string body, string section)
        {
            string trimmed = section ?? string.Empty;
            if (trimmed.Length == 0)
            {
                return body;
            }
            return body.Length == 0 ? trimmed : body + "\n\n" + trimmed;
        }

        /// <summary>
        /// Family label for one UapOps tool module (design section 3's own
        /// family list and ordering: scene/component/property/asset,
        /// prefab overrides, anim/animator/material, screenshot/menu).
        /// Used only by <see cref="ComposeUapOpsSteeringSection"/> so the
        /// injected text can list ONLY the families whose module is
        /// actually enabled.
        /// </summary>
        private static readonly string[][] UapOpsModuleFamilies =
        {
            new[] { "core", "scene objects/components/properties/assets/search" },
            new[] { "prefab", "prefab overrides (including revert)" },
            new[] { "anim", "animation clips/animator/material" },
            new[] { "editor", "screenshots, menu execution and asynchronous lightmap bakes (Unity or Bakery)" },
            new[] { "markers", "Scene-view 3D markers (uap_marker_add/list/clear) to point at places and objects" }
        };

        /// <summary>
        /// Pure composition of the L2 steering snippet (2026-08-02 design
        /// note section 3, measured motivation: a live session ran
        /// "PowerShell x24" through uloop for animation work while the anim
        /// module -- and its typed tools -- were enabled and registered the
        /// whole time; nothing ever told the agent the typed tools existed
        /// or were preferred, and MCP tools are behind ToolSearch so the
        /// cheap familiar path won). Empty when UapOps itself is off
        /// (steering toward tools that do not exist would be pointless) or
        /// when every module happens to be disabled (nothing to name).
        /// Never mentions a family whose module is not in
        /// <paramref name="enabledModules"/> -- a disabled anim module, for
        /// example, is never advertised, so the text can never promise a
        /// tool the agent will then find missing. Never forbids uloop; it
        /// is named explicitly as the sanctioned, slower fallback -- see
        /// the design note's "Deliberately NOT done" closing line.
        ///
        /// 2026-09-08 addendum, right after the family-list line, gated on
        /// the "core" module: a real transcript from that same day shows
        /// the agent editing a Camera transform, a TextMeshPro text and an
        /// object's rotation through `uloop execute-dynamic-code` instead
        /// of uap_property_set/uap_transform_set even with core enabled.
        /// Three causes, each addressed by one added line: (1) uap_* are
        /// MCP tools of server "unity-ops" DEFERRED until ToolSearch loads
        /// them, and nothing said so; (2) uap_property_set only writes
        /// LOCAL transform values and needed two calls for
        /// position+rotation while uap_transform_set (also new) does both,
        /// local or world, in one; (3) a "still running on the main
        /// thread"/"was never started" failure has two different correct
        /// reactions and the agent had no way to tell them apart, so it
        /// fell back to dynamic code instead of retrying correctly.
        /// </summary>
        internal static string ComposeUapOpsSteeringSection(bool uapOpsEnabled,
            IEnumerable<string> enabledModules)
        {
            if (!uapOpsEnabled)
            {
                return string.Empty;
            }
            var enabledSet = new HashSet<string>(StringComparer.Ordinal);
            if (enabledModules != null)
            {
                foreach (string module in enabledModules)
                {
                    if (!string.IsNullOrEmpty(module))
                    {
                        enabledSet.Add(module);
                    }
                }
            }
            var families = new List<string>();
            for (int i = 0; i < UapOpsModuleFamilies.Length; i++)
            {
                if (enabledSet.Contains(UapOpsModuleFamilies[i][0]))
                {
                    families.Add(UapOpsModuleFamilies[i][1]);
                }
            }
            if (families.Count == 0)
            {
                return string.Empty;
            }
            return "Unity edits should go through the uap_* tools when one fits: "
                + string.Join(", ", families.ToArray()) + ".\n"
                + (enabledSet.Contains("core")
                    ? "The uap_* tools are MCP tools of server \"unity-ops\" and are loaded on demand --"
                      + " before first use load them with ToolSearch, e.g."
                      + " select:mcp__unity-ops__uap_property_set,mcp__unity-ops__uap_transform_set,mcp__unity-ops__uap_object_inspect;"
                      + " a single ToolSearch can load several at once.\n"
                      + "For position/rotation/scale of any object (cameras included, world or local"
                      + " space) use uap_transform_set; for text, font, size, color of TextMeshPro, for"
                      + " Camera field of view / clear flags / background, and for any other component"
                      + " field use uap_property_set (its description lists the common property paths)"
                      + " -- never write dynamic code or a script for these edits.\n"
                      + "If a uap_* call fails with \"still running on the Unity main thread\", the"
                      + " action DID start and will finish on its own: do not re-issue it and do not"
                      + " poll Editor.log; the message names a job id -- call uap_job_status with that"
                      + " job_id (wait_ms up to 10000) until its state is succeeded or failed; it answers"
                      + " even while the Editor is blocked and returns the call's own result (uap_ping"
                      + " only tells you the Editor is free again). If it fails with \"was never"
                      + " started\", nothing ran; wait for uap_ping to answer, then issue it again.\n"
                      + "uap_asset_delete" + (enabledSet.Contains("prefab") ? " and uap_prefab_apply_overrides are" : " is")
                      + " destructive: a call without confirm:true is refused and only reports what would"
                      + " change; pass dry_run:true to preview, then confirm:true to apply.\n"
                    : string.Empty)
                + "uloop or raw dynamic code is for what those tools cannot express"
                + " -- it works, but is the slower, confirmation-heavy path.\n"
                + "Never run Lightmapping.Bake() (synchronous) through uloop or dynamic code: it"
                + " blocks the Unity main thread for the whole bake, the Editor shows \"Hold on\","
                + " and every tool call stalls until it ends. Use uap_lightmap_bake (start, then poll"
                + " status) when that tool is available; its start runs a memory preflight and, by"
                + " default, fixes what it can (Auto Generate off, Max Lightmap Size clamp, per-object"
                + " Scale In Lightmap on the renderers that dominate the texel count via action"
                + " object_scales, resolution only as a last resort, unused assets unloaded) so \"Material render job skipped - out of system memory\""
                + " does not silently bake materials black -- when it refuses with HIGH risk, run"
                + " action preflight, apply its recommendations (texture Max Size, Contribute GI on"
                + " fewer objects, free RAM) and start again rather than passing force. Before tuning"
                + " lightmap settings, textures or Contribute GI, read uap_lightmap_bake action guide"
                + " (the optimization playbook: what costs memory vs time, and which serialized"
                + " properties to change) instead of guessing. When the project has Bakery GPU Lightmapper,"
                + " use uap_bakery_bake instead (get_settings/set_settings for bounces, samples,"
                + " texelsPerUnit, renderMode, renderDirMode; start with scope full/selected/probes;"
                + " never click through Bakery's window).\n"
                + (enabledSet.Contains("markers")
                    ? "When you need to show the user WHERE something is in the scene, add a Scene-view"
                      + " marker with uap_marker_add and refer to it as [n] in your reply instead of"
                      + " describing coordinates; clear your markers with uap_marker_clear when done."
                      + " uap_editor_screenshot lists every marker's pixel position; use capture:\"window\""
                      + " when you need the Scene view exactly as the user sees it (labels, gizmos)."
                      + " A \"Scene marker P<n>\" block in the user's message is a pin the user dropped"
                      + " in the Scene view to mean \"here\"; it is also listed by uap_marker_list as user-pin.\n"
                    : string.Empty)
                + "uap_ping is the authoritative liveness check for the Unity Editor:"
                + " a uloop focus-window reply of \"No running Unity process found\" is a"
                + " detection failure whenever uap_ping still answers, never a reason to"
                + " relaunch the Editor.";
        }

        /// <summary>
        /// HUB-1: the ONE place a non-clean turn exit is cleaned up. Every
        /// terminal path that is not OnTurnCompleted's clean ResultMessage
        /// -- the silence backstop (OnTurnStalled), a crash
        /// (OnStateChanged's Errored branch), a process death
        /// (OnProcessDied) and any teardown (TearDownClient) -- calls this
        /// instead of hand-picking clears. The project's own defect history
        /// (defects 4/5/6, and the stalled-turn scope leak this method was
        /// extracted to fix) is a list of "a new terminal path forgot ONE
        /// of these clears"; centralizing removes that class of bug.
        ///
        /// What it does, all idempotent (safe when several terminal paths
        /// fire for one exit, e.g. ProcessDied AND the Errored transition):
        /// - <see cref="FinalizeStreamingMessage"/>: stops the streaming
        ///   spinner and demotes open tool/subagent records so nothing
        ///   renders as "running" forever.
        /// - drops the pending permission card -- no answer can ever be
        ///   delivered for a turn that will not continue.
        /// - <see cref="UapTurnScope.EndIfActive"/>: collapses the turn's
        ///   Undo group and, critically, releases the AssetDatabase
        ///   auto-refresh suppression (leaving it held was the original
        ///   HUB-1 symptom: a stalled turn disabled auto-refresh for the
        ///   rest of the editor session).
        /// - clears the per-turn non-undoable warning set and the
        ///   scripts-commit attribution flag, so an aborted turn's state
        ///   can never attach to a LATER, unrelated turn.
        /// - mirrors SessionStateBridge.TurnRunning from the client.
        ///
        /// <paramref name="clearContinuationFlag"/>: stall/errored/died
        /// pass true -- their turn will never reach OnTurnCompleted, so a
        /// stranded AutoContinueTurnIsContinuation=true would make the next
        /// genuinely human-prompted turn refuse to arm (defect 5's exact
        /// mechanics). Teardown passes FALSE: TearDownClient underlies
        /// ShutdownForReload, which runs on the very domain reload that
        /// flag exists to survive (see ClearAutoContinuePendingState's doc
        /// comment for why reload must preserve it).
        /// </summary>
        internal static void AbortOpenTurn(bool clearContinuationFlag)
        {
            FinalizeStreamingMessage();
            ClearLiveTurnUsage();
            // A compaction that never reached its boundary ends with the
            // turn it was part of; the indicator must not outlive it.
            _compacting = false;
            _pendingPermission = null;
            UapTurnScope.EndIfActive();
            _currentTurnNonUndoableTools.Clear();
            _currentTurnScriptsCommitAttributable = false;
            if (clearContinuationFlag)
            {
                SessionStateBridge.AutoContinueTurnIsContinuation = false;
            }
            SessionStateBridge.TurnRunning = _client != null && _client.TurnActive;
        }

        private static void TearDownClient(bool clearZombieRecord,
            int stopGraceMillis = AgentClient.DefaultStopGraceMillis)
        {
            // HUB-9: the inert-gate warning is per SPAWN -- the next
            // StartClient re-evaluates and may need to warn again (settings
            // can change between spawns).
            _scriptGateInertWarned = false;
            _apiKeyAuthNoted = false;
            AcpAuthMethodId = null;
            AcpAuthMethodName = null;
            _acpAuthMethodIdInFlight = null;
            _acpAuthMethodNameInFlight = null;
            _clientEpoch++;
            // Closes a turn scope left open by any NON-clean exit from an
            // executing turn (design section 1.3/8.2). Kept BEFORE the
            // client dispose below (AbortOpenTurn runs again after it, but
            // the scope release must not depend on the dispose succeeding)
            // -- no-op when no scope is open, and idempotent with the
            // AbortOpenTurn call at the tail of this method.
            UapTurnScope.EndIfActive();
            EditorUpdatePump.Detach();
            if (_client != null)
            {
                AgentClient client = _client;
                _client = null;
                try
                {
                    // Explicit Stop first so the caller-chosen grace applies
                    // (Dispose alone would use the default); Dispose is then
                    // a no-op stop plus transport cleanup.
                    client.Stop(stopGraceMillis);
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    Log("Client dispose failed: " + ex.Message);
                }
            }
            if (clearZombieRecord && _reaper != null)
            {
                _reaper.ClearRecord();
            }
            SessionStateBridge.ClearProcessRecord();
            // Any pending/deferred auto-apply becomes moot the moment ANY
            // teardown runs: Reconnect/StartFresh/SwitchToSession/Shutdown
            // either respawn with the CURRENT settings already (nothing left
            // to coalesce) or tear down for good (nothing left to defer
            // against). Deliberately does not touch _autoApplyInFlight --
            // ApplyAutoReconnect sets that BEFORE calling Reconnect(), which
            // itself tears down through here, and it must survive until the
            // new client reaches Ready/Errored.
            CancelAutoApply();
            // HUB-1: the shared non-clean-exit cleanup (streaming finalize +
            // open-record demotion, pending permission, turn scope, warning
            // set, attribution flag, TurnRunning=false via the nulled
            // client). clearContinuationFlag: FALSE -- this method underlies
            // ShutdownForReload, which runs on the exact reload
            // SessionStateBridge.AutoContinueTurnIsContinuation exists to
            // survive (see ClearAutoContinuePendingState's doc comment for
            // why THAT clearing lives in Reconnect/StartFresh/
            // SwitchToSession instead, never here). Upgrade vs the old
            // inline block: _streamingAssistant's blocks are now marked
            // not-streaming (FinalizeStreamingMessage) instead of merely
            // dropping the reference with the session's blocks left
            // spinning.
            AbortOpenTurn(clearContinuationFlag: false);
            _taskIdToToolUseId.Clear();
        }

        // -- AgentClient event handlers (main thread) ------------------------------

        /// <summary>
        /// UXO-7: when the current client entered Starting (UTC ticks), or
        /// 0 while not Starting. StatusBarView reads it to escalate the
        /// "Connecting..." text after the UX spec's 10-second threshold --
        /// a hung connect and a normal one were indistinguishable before.
        /// </summary>
        public static long StartingSinceUtcTicks { get; private set; }

        private static void OnStateChanged(AgentClientState from, AgentClientState to)
        {
            StartingSinceUtcTicks = to == AgentClientState.Starting ? DateTime.UtcNow.Ticks : 0;
            if (to != AgentClientState.Starting)
            {
                // Sign-in either succeeded (Ready) or the process is gone
                // (Errored): the "look at your browser" state is over.
                ResetAcpSignInState();
            }
            if (to == AgentClientState.Ready)
            {
                _reachedReadyThisProcess = true;
            }
            if (to == AgentClientState.Ready && from == AgentClientState.Starting
                && ShouldResetCrashCounterOnReady(_lastDeathUtcTicks, DateTime.UtcNow.Ticks))
            {
                // Successful (re)connect resets the crash-loop guard --
                // unless it follows a death by seconds, in which case the
                // process has to prove itself first (a completed turn).
                _consecutiveDeaths = 0;
            }
            if (to == AgentClientState.Ready || to == AgentClientState.Errored)
            {
                // Whatever auto-apply Reconnect() was in flight (if any) has
                // now settled one way or the other -- clears the status
                // bar's transient "Applying settings..." text.
                _autoApplyInFlight = false;
            }
            if (to == AgentClientState.Errored)
            {
                // Nothing left to defer against: a crashed/suspended client
                // will never fire another TurnCompleted. The next real spawn
                // (manual Reconnect/EnsureStarted) reads current settings
                // fresh regardless.
                CancelAutoApply();
                // HUB-1: the shared non-clean-exit cleanup. A crash never
                // raises TurnCompleted, so everything a turn holds open must
                // be released RIGHT HERE rather than waiting for whatever
                // teardown eventually follows -- a crash with no immediate
                // reconnect/teardown (the crash-loop suspended branch)
                // would otherwise leave AssetDatabase auto-refresh
                // disallowed and stale per-turn warning state for however
                // long the user takes to notice (design section 1.3/8.2).
                // clearContinuationFlag: TRUE -- defect 5's fix: a
                // continuation turn that crashes here never reaches
                // HandleAutoContinueArming, and a stranded
                // AutoContinueTurnIsContinuation=true would make the next
                // genuinely human-prompted turn silently refuse to arm.
                // Kept in BOTH this handler and OnProcessDied deliberately:
                // some Errored transitions never raise ProcessDied at all
                // (e.g. StartClient failing to spawn), and vice versa the
                // death handler runs even when this state transition is
                // skipped -- AbortOpenTurn is idempotent, so double
                // cleanup is free.
                AbortOpenTurn(clearContinuationFlag: true);
            }
            if (_client != null)
            {
                SessionStateBridge.TurnRunning = _client.TurnActive;
            }
            if (to == AgentClientState.Ready
                || to == AgentClientState.Streaming
                || to == AgentClientState.ToolRunning)
            {
                // Any transition back to a sendable state re-arms the
                // CompileGate drain (no-op when nothing is queued). Covers
                // every path out of WaitingPermission/Starting/Errored after
                // the gate's 60 s wait already gave up.
                CompileGate.DrainPending();
            }
            RaiseChanged();
        }

        private static void OnTextDelta(string text)
        {
            ChatMessageBlock block = EnsureStreamingBlock(ChatBlockKind.Text);
            block.text += text;
            RaiseChanged();
        }

        private static void OnThinkingDelta(string thinking)
        {
            ChatMessageBlock block = EnsureStreamingBlock(ChatBlockKind.Thinking);
            block.text += thinking;
            RaiseChanged();
        }

        /// <summary>
        /// system/thinking_tokens (design note 2026-08-01-thinking-content-
        /// loss.md section 5 point 1): the CLI's only live signal about an
        /// in-progress thinking block, since the block's own text is always
        /// empty on the wire. EstimatedTokens is already cumulative, so it
        /// simply overwrites the block's running estimate rather than
        /// accumulating -- mirrors OnThinkingDelta's block-creation
        /// behavior (creates the block on first signal if none is open
        /// yet).
        /// </summary>
        private static void OnThinkingTokensReceived(SystemThinkingTokensMessage message)
        {
            ChatMessageBlock block = EnsureStreamingBlock(ChatBlockKind.Thinking);
            block.thinkingTokens = message.EstimatedTokens;
            RaiseChanged();
        }

        private static void OnAssistantMessageCompleted(AssistantMessage message)
        {
            SubagentRecord subagent = null;
            if (!string.IsNullOrEmpty(message.ParentToolUseId))
            {
                _openSubagents.TryGetValue(message.ParentToolUseId, out subagent);
            }
            if (subagent != null)
            {
                OnSubagentAssistantMessageCompleted(subagent, message);
                return;
            }
            // parentToolUseId == null (ordinary top-level turn), OR it is
            // non-null but unknown to _openSubagents: acceptance criteria 6
            // fallback -- render at the top level exactly as before.

            // Live status-bar numbers for this turn (context meter + token
            // counter) -- main chain only, matching what result.usage sums.
            RecordLiveTurnUsage(message);

            ChatMessage target = EnsureStreamingAssistant();

            // Capture the streaming tail's accumulated Thinking block(s)
            // (text + token estimate) BEFORE RemoveTrailingStreamingBlocks
            // discards them -- design note 2026-08-01-thinking-content-
            // loss.md sections 2 and 5 point 4. Today the finalized
            // thinking content is always empty on the wire, so without this
            // the token estimate (and, forward-compat, any future
            // accumulated text) would simply vanish at finalization instead
            // of surviving as the "Thought (~N tokens)" indicator.
            List<ThinkingTail> pendingThinking = ExtractTrailingThinkingTails(target);
            int pendingThinkingIndex = 0;

            // Replace the provisional streaming tail with authoritative text.
            RemoveTrailingStreamingBlocks(target);

            for (int i = 0; i < message.Content.Length; i++)
            {
                ContentBlock block = message.Content[i];
                switch (block.Type)
                {
                    case ContentBlockType.Text:
                        if (!string.IsNullOrEmpty(block.Text))
                        {
                            target.Add(ChatMessageBlock.MakeText(block.Text));
                        }
                        break;
                    case ContentBlockType.Thinking:
                        AppendFinalizedThinking(target, block.Thinking, pendingThinking,
                            ref pendingThinkingIndex);
                        break;
                    case ContentBlockType.RedactedThinking:
                        // Arrives complete (spec: encrypted data only, no
                        // delta stream) -- appended directly, independent
                        // of the pendingThinking tail correlation above
                        // (design note 2026-08-01-thinking-content-loss.md
                        // section 6).
                        target.Add(ChatMessageBlock.MakeRedactedThinking());
                        break;
                    default:
                        // tool_use blocks become cards via OnToolUseStarted;
                        // unknown blocks are skipped.
                        break;
                }
            }
            // Forward-compat fallback (design note section 2): a streamed
            // Thinking tail that never matched a finalized Thinking content
            // block (count mismatch) still keeps its estimate/text --
            // folded onto the last Thinking block already added, or
            // appended as a new one.
            MergeLeftoverThinkingTails(target, pendingThinking, pendingThinkingIndex);

            if (!string.IsNullOrEmpty(message.Error) || message.IsSynthetic)
            {
                string detail = message.Error ?? L10n.S.HubSyntheticResponse;
                target.Add(ChatMessageBlock.MakeError(L10n.F(L10n.S.HubCliErrorFmt, detail)));
            }
            RaiseChanged();
        }

        /// <summary>
        /// Routes a parent-tagged assistant message into the subagent's
        /// nested transcript instead of the top-level one. Subagent content
        /// never streams (R02c): only finalized text/thinking ever arrive,
        /// so there is no provisional streaming tail to remove here (unlike
        /// the top-level path).
        /// </summary>
        private static void OnSubagentAssistantMessageCompleted(SubagentRecord subagent,
            AssistantMessage message)
        {
            for (int i = 0; i < message.Content.Length; i++)
            {
                ContentBlock block = message.Content[i];
                switch (block.Type)
                {
                    case ContentBlockType.Text:
                        if (!string.IsNullOrEmpty(block.Text))
                        {
                            subagent.AddBlock(ChatMessageBlock.MakeText(block.Text));
                        }
                        break;
                    case ContentBlockType.Thinking:
                        if (!string.IsNullOrEmpty(block.Thinking))
                        {
                            subagent.AddBlock(ChatMessageBlock.MakeThinking(block.Thinking));
                        }
                        break;
                    case ContentBlockType.RedactedThinking:
                        subagent.AddBlock(ChatMessageBlock.MakeRedactedThinking());
                        break;
                    default:
                        // tool_use blocks become nested cards via
                        // OnToolUseStarted; unknown blocks are skipped.
                        break;
                }
            }
            if (!string.IsNullOrEmpty(message.Error) || message.IsSynthetic)
            {
                string detail = message.Error ?? L10n.S.HubSyntheticResponse;
                subagent.AddBlock(ChatMessageBlock.MakeError(L10n.F(L10n.S.HubCliErrorFmt, detail)));
            }
            RaiseChanged();
        }

        private static void OnToolUseStarted(ContentBlock block, string parentToolUseId)
        {
            if (string.IsNullOrEmpty(block.Id) || _openToolCalls.ContainsKey(block.Id))
            {
                return;
            }
            ToolCallRecord record = BuildToolCallRecord(block);
            _openToolCalls[block.Id] = record;
            TrackNonUndoableToolIfNeeded(block.Name);

            SubagentRecord parentSubagent = null;
            if (!string.IsNullOrEmpty(parentToolUseId))
            {
                _openSubagents.TryGetValue(parentToolUseId, out parentSubagent);
            }

            if (parentSubagent != null)
            {
                // Nested tool call inside a running subagent. Depth-1 cutoff
                // (design note section 3): even an Agent/Task-shaped nested
                // call renders as a plain card here, never a new
                // SubagentRecord -- R02c only ever observed spawnDepth 1.
                parentSubagent.AddBlock(ChatMessageBlock.MakeToolCall(record));
                RaiseChanged();
                return;
            }

            // Top-level tool_use, OR a non-null parentToolUseId unknown to
            // _openSubagents (acceptance criteria 6 fallback): render as an
            // ordinary top-level card. Only a genuinely top-level spawn
            // (parentToolUseId == null) ever starts tracking a new subagent.
            if (string.IsNullOrEmpty(parentToolUseId) && IsSubagentSpawnTool(block.Name, block.Input))
            {
                var subagent = new SubagentRecord
                {
                    toolUseId = block.Id,
                    subagentType = block.Input["subagent_type"].AsString(string.Empty),
                    description = block.Input["description"].AsString(string.Empty)
                };
                record.subagent = subagent;
                _openSubagents[block.Id] = subagent;
            }
            EnsureStreamingAssistant().Add(ChatMessageBlock.MakeToolCall(record));
            RaiseChanged();
        }

        /// <summary>
        /// Design section 8.2 B2(c)/8.5 criterion 2: records the tool's bare
        /// registry name into <see cref="_currentTurnNonUndoableTools"/>
        /// when <paramref name="wireToolName"/> resolves to a registered
        /// UapOps tool whose Undoable is false. No-op for every other tool
        /// (non-UapOps, or UapOps but Undoable) -- OnTurnCompleted only
        /// warns when this set is non-empty.
        ///
        /// READ-ONLY tools are excluded even though they are also
        /// Undoable == false: they change nothing, so there is nothing an
        /// undo would have to recover. Warning about them is both wrong and
        /// noisy -- live 2026-08-02, a turn that only listed the hierarchy
        /// and queried component types ended with "this turn ran operations
        /// Ctrl+Z cannot undo: uap_query_component_types,
        /// uap_query_hierarchy", which is exactly the transcript noise the
        /// v0.14.0 work set out to remove (see
        /// docs/design-notes/2026-08-02-noise-permissions-and-uloop-
        /// preference.md section 2).
        /// </summary>
        private static void TrackNonUndoableToolIfNeeded(string wireToolName)
        {
            IUapTool tool = UapOpsServer.FindByWireName(wireToolName);
            if (tool != null && !tool.Undoable && !tool.ReadOnly)
            {
                _currentTurnNonUndoableTools.Add(tool.Name);
            }
        }

        /// <summary>
        /// Phase 5c L3 item 3's attribution signal (design section 3 item
        /// 3 / 8.3): records that THIS turn may cause an attributable
        /// reload when <paramref name="wireToolName"/> resolves to
        /// UapScriptsCommitTool AND its tool_result reports moved files
        /// (AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles). Reuses
        /// the SAME wire-name resolution TrackNonUndoableToolIfNeeded just
        /// above already uses (UapOpsServer.FindByWireName) rather than
        /// inventing a second tool-name-matching path -- this is the "use
        /// that signal rather than a heuristic" instruction from the design
        /// applied literally.
        ///
        /// A merely-called-but-unsuccessful commit (block.IsError, or a
        /// result text starting with "Nothing staged" / "Compilation
        /// failed") must NOT set the flag -- neither outcome touches
        /// Assets/, so neither can be what a later reload is attributable
        /// to. Never CLEARS an already-true flag mid-turn: a turn could in
        /// principle call uap_scripts_commit more than once (e.g. a
        /// no-op commit after a real one), and a later call reporting
        /// "nothing to move" must not erase an earlier call's genuine
        /// success.
        /// </summary>
        private static void TryTrackScriptsCommitAttribution(string wireToolName, ContentBlock block)
        {
            if (block.IsError)
            {
                return;
            }
            IUapTool tool = UapOpsServer.FindByWireName(wireToolName);
            if (tool == null
                || !string.Equals(tool.Name, AutoContinueAfterCompilePolicy.ScriptsCommitToolName, StringComparison.Ordinal))
            {
                return;
            }
            string text = AutoContinueAfterCompilePolicy.ExtractTextContent(block.ResultContent);
            if (AutoContinueAfterCompilePolicy.ScriptCommitMovedFiles(text))
            {
                _currentTurnScriptsCommitAttributable = true;
            }
        }

        private static ToolCallRecord BuildToolCallRecord(ContentBlock block)
        {
            string inputJson = block.Input != null && !block.Input.IsNull
                ? JsonWriter.Write(block.Input)
                : string.Empty;
            return new ToolCallRecord
            {
                toolUseId = block.Id,
                toolName = block.Name ?? string.Empty,
                inputJson = inputJson,
                inputSummary = Truncate(inputJson, SummaryMaxChars),
                status = ToolCallStatus.Running,
                startedAtUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        /// <summary>
        /// True when a tool_use is a subagent spawn: the spawn tool's WIRE
        /// name is "Agent" (system/init's tools[] lists it as "Task" --
        /// R02c section 1), matched defensively by name OR by the presence
        /// of a "subagent_type" input key so a future rename cannot silently
        /// stop the panel from recognizing it.
        /// </summary>
        private static bool IsSubagentSpawnTool(string toolName, JsonNode input)
        {
            if (string.Equals(toolName, "Agent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "Task", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return input != null && input.IsObject && input.HasKey("subagent_type");
        }

        private static void OnToolResultReceived(ContentBlock block, string parentToolUseId)
        {
            if (string.IsNullOrEmpty(block.ToolUseId))
            {
                return;
            }
            ToolCallRecord record;
            if (!_openToolCalls.TryGetValue(block.ToolUseId, out record))
            {
                return;
            }
            _openToolCalls.Remove(block.ToolUseId);
            string summary = block.ResultContent != null && block.ResultContent.IsString
                ? Truncate(block.ResultContent.AsString(string.Empty), SummaryMaxChars)
                : string.Empty;
            record.Complete(block.IsError, summary, DateTime.UtcNow.Ticks);
            TryTrackScriptsCommitAttribution(record.toolName, block);
            if (record.subagent != null)
            {
                // The top-level tool_result is the final authority on this
                // subagent's outcome (design note section 3), overwriting
                // whatever task_updated/task_notification last reported.
                _openSubagents.Remove(record.subagent.toolUseId);
                record.subagent.status = block.IsError ? "failed" : "completed";
                // The taskId->toolUseId entry (if task_started ever arrived
                // for this spawn) would otherwise never be removed: this is
                // the normal-completion closure path, distinct from the
                // interrupted-turn path in RemoveOpenSubagentTracking below,
                // and both must prune the same map or it grows one entry per
                // subagent for the life of the client connection.
                if (!string.IsNullOrEmpty(record.subagent.taskId))
                {
                    _taskIdToToolUseId.Remove(record.subagent.taskId);
                }
            }
            RaiseChanged();
        }

        /// <summary>Routes system/task_started|task_progress|task_updated|task_notification.</summary>
        private static void OnTaskEventReceived(SystemTaskEventMessage message)
        {
            switch (message.Subtype)
            {
                case "task_started":
                    HandleTaskStarted(message);
                    break;
                case "task_progress":
                    HandleTaskProgress(message);
                    break;
                case "task_updated":
                    HandleTaskUpdated(message);
                    break;
                case "task_notification":
                    HandleTaskNotification(message);
                    break;
                default:
                    // Unknown task_* subtype: StreamJsonMessage.FromNode only
                    // constructs this type for the four known subtypes, but
                    // stay forward-compatible anyway (ignored, not an error).
                    break;
            }
        }

        private static void HandleTaskStarted(SystemTaskEventMessage message)
        {
            if (!string.IsNullOrEmpty(message.TaskId) && !string.IsNullOrEmpty(message.ToolUseId))
            {
                _taskIdToToolUseId[message.TaskId] = message.ToolUseId;
            }
            SubagentRecord subagent = ResolveSubagent(message.TaskId, message.ToolUseId);
            if (subagent == null)
            {
                return;
            }
            if (string.IsNullOrEmpty(subagent.taskId))
            {
                subagent.taskId = message.TaskId ?? string.Empty;
            }
            if (string.IsNullOrEmpty(subagent.subagentType))
            {
                subagent.subagentType = message.SubagentType ?? string.Empty;
            }
            if (string.IsNullOrEmpty(subagent.description))
            {
                subagent.description = message.Description ?? string.Empty;
            }
            RaiseChanged();
        }

        private static void HandleTaskProgress(SystemTaskEventMessage message)
        {
            SubagentRecord subagent = ResolveSubagent(message.TaskId, message.ToolUseId);
            if (subagent == null)
            {
                return;
            }
            subagent.progressLine = message.Description ?? string.Empty;
            if (!string.IsNullOrEmpty(message.LastToolName))
            {
                subagent.lastToolName = message.LastToolName;
            }
            subagent.totalTokens = message.TotalTokens;
            subagent.toolUses = message.ToolUses;
            subagent.durationMs = message.DurationMs;
            RaiseChanged();
        }

        private static void HandleTaskUpdated(SystemTaskEventMessage message)
        {
            if (string.IsNullOrEmpty(message.PatchStatus))
            {
                return;
            }
            SubagentRecord subagent = ResolveSubagent(message.TaskId, message.ToolUseId);
            if (subagent == null)
            {
                return;
            }
            subagent.status = message.PatchStatus;
            RaiseChanged();
        }

        private static void HandleTaskNotification(SystemTaskEventMessage message)
        {
            SubagentRecord subagent = ResolveSubagent(message.TaskId, message.ToolUseId);
            if (subagent == null)
            {
                return;
            }
            subagent.summaryMarkdown = message.SummaryMarkdown ?? string.Empty;
            if (!string.IsNullOrEmpty(message.Status))
            {
                subagent.status = message.Status;
            }
            if (message.TotalTokens > 0)
            {
                subagent.totalTokens = message.TotalTokens;
            }
            if (message.ToolUses > 0)
            {
                subagent.toolUses = message.ToolUses;
            }
            if (message.DurationMs > 0)
            {
                subagent.durationMs = message.DurationMs;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Resolves an open SubagentRecord by tool_use_id, falling back to
        /// the task_id-&gt;tool_use_id map (task_updated only ever carries
        /// task_id). Returns null when neither resolves to a currently open
        /// subagent (already closed, or an id this session never saw).
        /// </summary>
        private static SubagentRecord ResolveSubagent(string taskId, string toolUseId)
        {
            string resolvedToolUseId = toolUseId;
            if (string.IsNullOrEmpty(resolvedToolUseId) && !string.IsNullOrEmpty(taskId))
            {
                _taskIdToToolUseId.TryGetValue(taskId, out resolvedToolUseId);
            }
            if (string.IsNullOrEmpty(resolvedToolUseId))
            {
                return null;
            }
            SubagentRecord subagent;
            _openSubagents.TryGetValue(resolvedToolUseId, out subagent);
            return subagent;
        }

        private static void OnPermissionRequested(ControlRequestMessage request)
        {
            // ORDERING IS LOAD-BEARING -- DO NOT REORDER, and do not let a
            // future "simplify" pass swap these two. TryAutoDenyForScriptGate
            // MUST get first refusal on EVERY request, no matter how
            // permissive the user's chosen UapAutoApproveLevel is (even
            // AllUnityOps). The script gate's whole design assumes its
            // can_use_tool pre-filter always gets a chance to run before
            // anything else can answer the request; running the auto-approve
            // check first would let a future change to AutoApprovePolicy (or
            // to the gate's own tool-name set) silently create a path where a
            // gated Write/Edit/MultiEdit or a Bash script-redirection reaches
            // "allow" without ever being checked against the gate at all. In
            // the CURRENT tool-name namespace this ordering happens to be
            // unobservable from the outside (Write/Edit/MultiEdit/Bash never
            // resolve via UapOpsServer.FindByWireName, so
            // TryAutoApproveUapOpsTool always returns false for them
            // regardless of order) -- but that is an accident of today's
            // naming, not a guarantee, so the order is fixed here rather than
            // relied upon implicitly.
            if (TryAutoDenyForScriptGate(request))
            {
                return;
            }
            if (TryAutoApproveUapOpsTool(request))
            {
                return;
            }
            // HUB-4: this single slot is safe ONLY because AgentClient
            // serializes permission requests for us (CORE-6): every
            // can_use_tool is queued there and exactly one is promoted --
            // and raised here -- at a time, with the next promoted only
            // after RespondToPermission/turn-end resolves the live one. The
            // hub therefore never holds two at once and the original
            // "second request overwrites the first, first is never
            // answered, CLI waits forever" failure cannot occur. If that
            // client-side invariant is ever weakened, this overwrite is
            // where it resurfaces, so it is asserted loudly rather than
            // assumed: dropping a request silently is precisely the bug.
            if (_pendingPermission != null && _pendingPermission.RequestId != request.RequestId)
            {
                Log("Permission request '" + request.RequestId + "' arrived while '"
                    + _pendingPermission.RequestId + "' was still pending -- the client's FIFO"
                    + " should have held it back. Answering the older card is no longer possible.");
            }
            _pendingPermission = request;
            if (PanelStateStore.instance.Settings.permissionBeep && !IsPanelFocused())
            {
                UnityEditor.EditorApplication.Beep();
            }
            RaiseChanged();
        }

        /// <summary>
        /// 2026-08-03: auto-approves a can_use_tool request per
        /// AutoApprovePolicy.ShouldAutoApprove against the user's current
        /// UapAutoApproveLevel (PanelSettings.autoApproveLevel), WITHOUT
        /// ever setting <see cref="_pendingPermission"/> -- so no card shows
        /// for it at all, mirroring <see cref="TryAutoDenyForScriptGate"/>'s
        /// shape. Supersedes the v0.14.0 read-only-only
        /// TryAutoApproveReadOnlyUapOpsTool (gated on the old
        /// autoApproveReadOnlyOps bool): UapAutoApproveLevel.ReadOnly
        /// reproduces that exact behaviour, and Undoable/AllUnityOps widen it
        /// per AutoApprovePolicy's own rules.
        ///
        /// Shared by two callers: <see cref="OnPermissionRequested"/> (a
        /// FRESH request, before <see cref="_pendingPermission"/> is ever
        /// set) and <see cref="ApplyAutoApproveLevelChanged"/> (an ALREADY-
        /// pending request being re-evaluated after the user raises the
        /// level) -- both must send an auto-approve control_response the
        /// exact same way, so that logic lives here once rather than being
        /// duplicated.
        ///
        /// Resolution mirrors the permission card's own undo-badge lookup
        /// (UapOpsServer.FindByWireName): a null resolution means "not a
        /// UapOps tool" (isUapOpsTool:false), which AutoApprovePolicy always
        /// refuses at every level below AllTools -- Bash/Write/Edit/
        /// MultiEdit and any other CLI-native tool only auto-approve at
        /// UapAutoApproveLevel.AllTools (design note 2026-09-10-auto-
        /// approve-all-tools), and a requires_user_interaction request
        /// never does (see AutoApprovePolicy.ShouldAutoApprove's doc).
        /// Never adds a transcript note (an ALLOW never does -- see
        /// RespondToPendingPermission). Safe by construction: ReadOnly/
        /// Undoable are metadata this codebase owns, not model input, and
        /// ReadOnly is pinned by UapReadOnlyToolMetadataTests to the exact
        /// non-mutating set.
        /// </summary>
        private static bool TryAutoApproveUapOpsTool(ControlRequestMessage request)
        {
            if (_client == null)
            {
                return false;
            }
            CanUseToolRequest tool = request.CanUseTool;
            if (tool == null)
            {
                return false;
            }
            IUapTool resolved = UapOpsServer.FindByWireName(tool.ToolName);
            bool isUapOpsTool = resolved != null;
            bool readOnly = isUapOpsTool && resolved.ReadOnly;
            bool undoable = isUapOpsTool && resolved.Undoable;
            // 5-argument overload (design note 2026-09-10-auto-approve-all-
            // tools): AllTools approves CLI-native tools too, but a
            // requires_user_interaction request (AskUserQuestion) is a
            // question for the human and never auto-answers at any level.
            if (!AutoApprovePolicy.ShouldAutoApprove(
                GetAutoApproveLevel(), isUapOpsTool, readOnly, undoable, tool.RequiresUserInteraction))
            {
                return false;
            }
            _client.RespondToPermission(request.RequestId, PermissionDecision.AllowTool());
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Single read point for PanelSettings' auto-approve level (the
        /// UapAutoApproveLevel-valued field that replaced the v0.14.0
        /// autoApproveReadOnlyOps bool -- UapAutoApproveLevel.ReadOnly is
        /// exactly that bool's old "true" behaviour). Kept as one call
        /// rather than inlined at each read site so the field itself only
        /// needs to be named in one place. Purely live -- read fresh on
        /// every can_use_tool and on every ApplyAutoApproveLevelChanged
        /// call, never cached, exactly like autoApproveReadOnlyOps before it.
        /// </summary>
        private static UapAutoApproveLevel GetAutoApproveLevel()
        {
            return PanelStateStore.instance.Settings.autoApproveLevel;
        }

        /// <summary>
        /// The script validation gate's can_use_tool pre-filter (design
        /// section 7.4/8.2 B1, extended per section 8.3's "mechanism, not
        /// instruction" principle to cover the CLI's Bash tool too -- a
        /// shell redirection/heredoc can otherwise create or overwrite an
        /// Assets/**/*.cs or *.asmdef file with zero involvement from
        /// Write/Edit/MultiEdit): when enabled and the request is EITHER
        /// (a) the CLI's own Write/Edit/MultiEdit targeting a `.cs`/
        /// `.asmdef` file under Assets/ OUTSIDE the staging folder, OR (b)
        /// a Bash command whose shell redirection (`&gt;`, `&gt;&gt;`, `tee`)
        /// writes to such a path, answers DENY immediately with the pinned
        /// instructional message and returns true WITHOUT ever setting
        /// <see cref="_pendingPermission"/> -- so no permission card shows
        /// for it at all. Staging-path writes (and every other tool/
        /// command) fall through to the normal card flow.
        /// </summary>
        private static bool TryAutoDenyForScriptGate(ControlRequestMessage request)
        {
            if (_client == null || !PanelStateStore.instance.Settings.uapScriptGateEnabled)
            {
                return false;
            }
            CanUseToolRequest tool = request.CanUseTool;
            if (tool == null)
            {
                return false;
            }
            string offendingPath;
            string denyMessage = ScriptGate.DenyMessage;
            if (ScriptGate.IsGatedToolName(tool.ToolName))
            {
                offendingPath = tool.Input != null ? tool.Input["file_path"].AsString(null) : null;
                if (ScriptGate.IsProtectedGateFile(offendingPath))
                {
                    // Defense in depth (SEC-1): a Write/Edit to the gate's
                    // own config files gets a "don't touch this" denial, not
                    // the staging-redirect message.
                    denyMessage = ScriptGate.ProtectedGateFileDenyMessage;
                }
                else if (!ScriptGate.ShouldAutoDeny(tool.ToolName, offendingPath))
                {
                    return false;
                }
            }
            else if (string.Equals(tool.ToolName, "Bash", StringComparison.Ordinal))
            {
                string command = tool.Input != null ? tool.Input["command"].AsString(null) : null;
                if (!ScriptGate.TryFindGatedBashTarget(command, out offendingPath))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
            _client.RespondToPermission(request.RequestId, PermissionDecision.DenyTool(denyMessage));
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            note.Add(ChatMessageBlock.MakeSystemNote(
                L10n.F(L10n.S.HubScriptGateAutoDeniedFmt, offendingPath ?? string.Empty)));
            Session.AddMessage(note);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// system/compact_boundary (design note 2026-09-07-slash-commands-
        /// and-compaction.md section 2): the CLI just replaced the
        /// conversation so far with a summary, either because the user
        /// sent /compact or on its own because the window filled up. The
        /// transcript gets a localized note at the boundary (the model's
        /// memory of everything above it is now a summary -- the user
        /// should see where that line is), the streaming bubble, if any,
        /// is closed so text after the boundary starts a new one, and the
        /// context meter switches to "compacted" until a trustworthy
        /// post-compaction reading arrives (<see cref="ApplyCompactionToContextReading"/>).
        /// Open tool-call records are NOT demoted here (unlike
        /// FinalizeStreamingMessage): a compaction happens between API
        /// calls, never while a tool is mid-flight, and the turn is still
        /// running -- its own result closes it.
        /// </summary>
        private static void OnCompactBoundaryReceived(SystemCompactBoundaryMessage message)
        {
            if (message == null)
            {
                return;
            }
            if (_streamingAssistant != null)
            {
                for (int i = 0; i < _streamingAssistant.blocks.Count; i++)
                {
                    _streamingAssistant.blocks[i].streaming = false;
                }
                _streamingAssistant = null;
            }
            _pendingCompactTrigger = message.IsManual
                ? SystemCompactBoundaryMessage.TriggerManual
                : SystemCompactBoundaryMessage.TriggerAuto;
            // The boundary is the end of the compaction the status signal
            // (or the panel's own /compact send) announced.
            _compacting = false;
            _lastContextTokens = -1;
            // The live reading is the pre-compaction context too: drop it
            // (the in-flight token COUNT stays -- those tokens were spent)
            // so the meter reads "compacted" until the next assistant
            // message measures the new window.
            _liveContextTokens = -1;
            _contextUnknownAfterCompaction = true;
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o"),
                turnId = _client != null ? _client.CurrentTurnId : 0
            };
            note.Add(ChatMessageBlock.MakeSystemNote(
                CompactionNote.Describe(message.Trigger, message.PreTokens)));
            Session.AddMessage(note);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        /// <summary>
        /// system/status (design note 2026-09-10-compacting-indicator.md):
        /// "compacting" turns the indicator on; an explicit clear (null /
        /// absent status) turns it off. Any OTHER value is ignored on
        /// purpose: the CLI emits "requesting" right before each API call
        /// -- including, plausibly, the summarization call that IS the
        /// compaction -- so letting it clear the flag would blank the
        /// indicator during the very wait it exists for. The boundary and
        /// the turn's end are the real "off" signals. Only a real change
        /// redraws: "requesting" arrives once per API call and must not
        /// cost a Changed each time.
        /// </summary>
        private static void OnStatusReceived(SystemStatusMessage message)
        {
            if (message == null)
            {
                return;
            }
            bool compacting;
            if (message.IsCompacting)
            {
                compacting = true;
            }
            else if (string.IsNullOrEmpty(message.Status))
            {
                compacting = false;
            }
            else
            {
                return;
            }
            if (compacting == _compacting)
            {
                return;
            }
            _compacting = compacting;
            RaiseChanged();
        }

        /// <summary>
        /// Runs once per completed turn, right after <see cref="_lastContextTokens"/>
        /// was taken from the result. Decides whether that reading
        /// post-dates any compaction seen since the previous result:
        /// - no compaction: the reading stands; the "compacted" state (if
        ///   a manual compaction set it one turn ago) is over.
        /// - AUTO compaction mid-turn: the turn went on after the boundary
        ///   and its last iteration ran against the compacted context, so
        ///   the reading is exactly the post-compaction size -- trust it.
        /// - MANUAL /compact: the turn WAS the compaction; its last
        ///   iteration is the summarization call that read the whole old
        ///   context. Discard the reading and keep "compacted" until the
        ///   next turn measures the real, smaller window.
        /// </summary>
        private static void ApplyCompactionToContextReading()
        {
            string trigger = _pendingCompactTrigger;
            _pendingCompactTrigger = null;
            if (string.Equals(trigger, SystemCompactBoundaryMessage.TriggerManual, StringComparison.Ordinal))
            {
                _lastContextTokens = -1;
                _contextUnknownAfterCompaction = true;
                return;
            }
            _contextUnknownAfterCompaction = false;
        }

        private static void OnTurnCompleted(ResultMessage result)
        {
            FinalizeStreamingMessage();
            _pendingPermission = null;
            // A manual /compact's own result is what ends its compaction
            // when the CLI sends no boundary (older CLI, or a compaction
            // that failed); the indicator ends with the turn either way.
            _compacting = false;
            // Closes whatever UapTurnScope.BeginIfNeeded opened in
            // SendUserMessage -- collapses the turn's Undo group and
            // performs the turn's single batched AssetDatabase refresh
            // (design section 1.3/7.4/8.2). No-op when nothing was opened
            // (e.g. a turn that never sent through SendUserMessage).
            UapTurnScope.EndIfActive();

            long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
            if (result.Usage != null)
            {
                input = result.Usage.InputTokens;
                output = result.Usage.OutputTokens;
                cacheRead = result.Usage.CacheReadInputTokens;
                cacheCreate = result.Usage.CacheCreationInputTokens;
            }
            Session.AccumulateTurn(input, output, cacheRead, cacheCreate, result.TotalCostUsd);
            if (!result.IsError)
            {
                // A completed turn proves the process is healthy: the
                // crash counter starts over (see ShouldResetCrashCounterOnReady).
                _consecutiveDeaths = 0;
                _lastDeathUtcTicks = 0;
            }
            _lastModelUsage = result.ModelUsage ?? new Dictionary<string, ModelUsage>();
            _lastContextTokens = result.Usage != null
                ? result.Usage.LastIterationContextTokens : -1;
            // The authoritative numbers are in; the provisional in-flight
            // ones (already folded into Session above via result.usage)
            // must not be added on top a second time.
            ClearLiveTurnUsage();
            ApplyCompactionToContextReading();
            // Scene-per-session recording for History grouping. Done here,
            // per completed turn, because the CLI transcript carries no
            // scene information at all -- this is the only place the panel
            // can capture it, and only for sessions that run from now on.
            SessionMetaStoreAccess.RecordActiveScene(Session.sessionId);

            if (result.IsError && !string.IsNullOrEmpty(result.ResultText))
            {
                var note = new ChatMessage { role = ChatMessage.RoleSystem };
                note.Add(ChatMessageBlock.MakeError(result.ResultText));
                Session.AddMessage(note);
            }
            AppendNonUndoableTurnWarningIfAny();
            HandleAutoContinueArming();

            _resumedMidTurn = false;
            // With queued mid-turn sends one result does not mean idle:
            // mirror the client's open-turn state instead of forcing false.
            SessionStateBridge.TurnRunning = _client != null && _client.TurnActive;
            if (PanelStateStore.instance.Settings.turnCompleteBeep && !IsPanelFocused())
            {
                UnityEditor.EditorApplication.Beep();
            }
            SessionCache.Save(Session, _lastModelUsage);
            if (_autoApplyPending && _autoApplyDeferred)
            {
                // Re-checks busy/idle fresh: a queued mid-turn send can
                // already have re-opened TurnActive above, in which case
                // this simply stays deferred for the NEXT TurnCompleted.
                TryAdvanceAutoApply();
            }
            RaiseChanged();
        }

        /// <summary>
        /// Design section 8.2 B2(c)/8.5 criterion 2: if the just-completed
        /// turn ran any non-undoable UapOps tool (tracked by
        /// <see cref="TrackNonUndoableToolIfNeeded"/>), appends a system
        /// note transcript warning naming them, then clears the set for the
        /// next turn. No-op (and no empty note) when the turn ran none.
        /// </summary>
        private static void AppendNonUndoableTurnWarningIfAny()
        {
            if (_currentTurnNonUndoableTools.Count == 0)
            {
                return;
            }
            var names = new List<string>(_currentTurnNonUndoableTools);
            _currentTurnNonUndoableTools.Clear();
            names.Sort(StringComparer.Ordinal);
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            note.Add(ChatMessageBlock.MakeSystemNote(
                L10n.F(L10n.S.HubTurnNonUndoableWarningFmt, string.Join(", ", names.ToArray()))));
            Session.AddMessage(note);
        }

        /// <summary>
        /// Phase 5c L3 item 3 (auto-continue after compile) -- design
        /// section 3 item 3 / 8.3. Runs at the end of EVERY completed turn,
        /// whether or not PanelSettings.uapOpsAutoContinueAfterCompile is
        /// even on: see AutoContinueAfterCompilePolicy.
        /// L10n's HubAutoContinuePendingWillContinueOff for why the pending-
        /// reload announcement itself is not gated on the setting, only its
        /// wording is (section 8.3's "never a silent background reload" is
        /// a general safety property, not something this feature should
        /// withhold from a user who left auto-continue off).
        ///
        /// ORDER matters here: the live SessionStateBridge.
        /// AutoContinueTurnIsContinuation flag (whether THIS just-completed
        /// turn was itself sent by TryAutoContinueAfterCompile) is read and
        /// reset FIRST, unconditionally, every turn -- exactly like
        /// _currentTurnScriptsCommitAttributable below -- so a stale true
        /// left over from an earlier continuation can never bleed into a
        /// later, unrelated turn's bookkeeping. Note that this is not the
        /// ONLY place the flag is ever cleared any more: OnStateChanged
        /// (Errored) and OnProcessDied ALSO clear it, for a continuation
        /// turn that crashes instead of ever reaching OnTurnCompleted --
        /// see defect 5's fix on those two handlers.
        /// </summary>
        private static void HandleAutoContinueArming()
        {
            bool thisTurnWasContinuation = SessionStateBridge.AutoContinueTurnIsContinuation;
            SessionStateBridge.AutoContinueTurnIsContinuation = false;

            bool attributable = _currentTurnScriptsCommitAttributable;
            _currentTurnScriptsCommitAttributable = false;
            // Written UNCONDITIONALLY (true or false) so a later,
            // non-attributable turn correctly overwrites -- and thereby
            // clears -- any stale true a MUCH earlier turn might have
            // armed and never had consumed (e.g. because, unexpectedly,
            // the anticipated reload never actually happened).
            SessionStateBridge.AutoContinuePendingAttribution = attributable;
            SessionStateBridge.AutoContinuePendingWasContinuation = thisTurnWasContinuation;
            // Defect 1 fix (2026-08-04): stamped unconditionally too, right
            // alongside the two fields above, so TryAutoContinueAfterCompile
            // can tell a reload that legitimately follows THIS arm apart
            // from an unrelated LATER one -- see AutoContinueAfterCompilePolicy.
            // TicketIsFresh's doc comment for the concrete failure (a
            // commit whose own compile fails causes NO domain reload at
            // all, so the ticket sits armed with nothing to distinguish it
            // from fresh) this exists to close.
            SessionStateBridge.AutoContinuePendingArmedAtUtcTicks = DateTime.UtcNow.Ticks;

            if (!attributable)
            {
                return;
            }
            bool enabled = PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile;
            // Defect 6 fix (2026-08-04): DescribeOutcome replaces a single
            // `willAutoContinue ? ... : ...` ternary that folded two
            // independent negative causes -- the setting being off, and
            // guardrail 1 blocking a continuation from continuing itself --
            // onto the SAME "off in Settings" wording (see
            // AutoContinueAfterCompilePolicy.AutoContinueSkipReason's own
            // doc comment; ContinuationTurnItselfCommitsScripts_
            // DoesNotArmASecondContinuation is the regression test that
            // caught this live, with the setting ON).
            AutoContinueAfterCompilePolicy.AutoContinueSkipReason outcome =
                AutoContinueAfterCompilePolicy.DescribeOutcome(enabled, attributable, thisTurnWasContinuation);
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            // Shown whether or not auto-continue will fire: a reload that is
            // about to kill the CLI process is worth announcing on its own,
            // so a user with the setting off still learns why the panel is
            // about to look disconnected. Only the wording differs.
            //
            // The AlreadyContinuedThisCycle branch below reuses
            // HubAutoContinuePendingOff as a KNOWN-WRONG placeholder: it
            // says "off in Settings" even though DescribeOutcome only
            // returns this value when enabled is true. This stream does
            // not own the L10n catalog and must not add a new entry to it
            // -- see this stream's final report for the exact dedicated
            // string (HubAutoContinuePendingAlreadyContinued) the L10n
            // owner should add, after which this branch becomes a one-line
            // swap; the DescribeOutcome classification itself is already
            // correct and covered by AutoContinueAfterCompilePolicyTests
            // independently of which string ships here.
            string noteText;
            switch (outcome)
            {
                case AutoContinueAfterCompilePolicy.AutoContinueSkipReason.WillContinue:
                    noteText = L10n.S.HubAutoContinuePendingWillContinue;
                    break;
                case AutoContinueAfterCompilePolicy.AutoContinueSkipReason.DisabledInSettings:
                    noteText = L10n.S.HubAutoContinuePendingOff;
                    break;
                default:
                    // The once-per-turn guardrail. This used to render
                    // HubAutoContinuePendingOff too, which sent the user off
                    // to change a setting that was already correct.
                    noteText = L10n.S.HubAutoContinuePendingAlreadyContinued;
                    break;
            }
            note.Add(ChatMessageBlock.MakeSystemNote(noteText));
            Session.AddMessage(note);
        }

        private static void OnTurnStalled()
        {
            // HUB-1's original symptom lived here: the silence backstop
            // FORCES the turn closed client-side (TurnTracker.EndTurn runs
            // before TurnStalled is raised), so OnTurnCompleted will never
            // fire for it -- yet this handler only finalized the streaming
            // message. Everything else the turn held open leaked until the
            // next teardown: the UapTurnScope kept AssetDatabase
            // auto-refresh DISALLOWED (and the Undo group open) for the
            // rest of the editor session, the stalled turn's non-undoable
            // warning set attached itself to the next unrelated turn, a
            // pending permission card stayed up unanswerable, and a
            // stalled continuation turn stranded
            // AutoContinueTurnIsContinuation=true (defect 5's mechanics,
            // via a path defect 5 never covered).
            AbortOpenTurn(clearContinuationFlag: true);
            var note = new ChatMessage { role = ChatMessage.RoleSystem };
            note.Add(ChatMessageBlock.MakeSystemNote(L10n.S.HubTurnStalledNote));
            Session.AddMessage(note);
            SessionCache.Save(Session, _lastModelUsage);
            RaiseChanged();
        }

        private static void OnStderrLine(string line)
        {
            if (AcpSignInPending && AcpSignInUrl == null)
            {
                string url = ExtractFirstUrl(line);
                if (url != null)
                {
                    AcpSignInUrl = url;
                    AppendSystemNote(L10n.F(L10n.S.HubAcpSignInUrlNoteFmt, url), false);
                }
            }
            _stderrLog.Add(line);
            if (_stderrLog.Count > MaxStderrLines)
            {
                _stderrLog.RemoveAt(0);
            }
            RaiseChanged();
        }

        /// <summary>
        /// Relays a resolved set_model control_request into Changed so the
        /// header model picker/status bar repaint within one refresh tick
        /// of the response instead of waiting for some unrelated later
        /// event (TurnCompleted, StateChanged, ...) to happen to mark the
        /// UI dirty. SwitchSessionModel's own immediate RaiseChanged() only
        /// covers the fact that a switch was requested -- the
        /// LIVE AgentClient.CurrentModel value is not known until this
        /// control_response actually arrives (see
        /// docs/design-notes/2026-07-31-live-model-tracking.md and
        /// docs/design-notes/2026-07-31-model-picker-refresh-gap.md).
        /// Raised for both success and failure so a failed switch also
        /// repaints (clearing any transient "switching..." affordance a
        /// future UI adds) even though CurrentModel itself only changes on
        /// success. Other control_request kinds (interrupt,
        /// set_permission_mode) already have their own RaiseChanged calls
        /// at the point they mutate observable state, so this stays scoped
        /// to set_model per the reported defect.
        /// </summary>
        private static void OnControlRequestResolved(string kind, bool success, string error)
        {
            if (string.Equals(kind, "set_model", StringComparison.Ordinal))
            {
                RaiseChanged();
            }
            if (success && string.Equals(kind, "initialize", StringComparison.Ordinal))
            {
                // The rich commands[] catalog lives on the initialize
                // response, which can land before OR after system/init
                // depending on the spawn (resume vs fresh); refreshing on
                // both keeps whichever arrives last authoritative.
                RefreshSlashCommandCatalogCache();
            }
        }

        private static void OnSessionIdChanged(string sessionId)
        {
            Session.sessionId = sessionId;
            Session.agentBackend = (int)CurrentBackend;
            SessionStateBridge.CurrentSessionId = sessionId;
            RaiseChanged();
        }

        /// <summary>
        /// Retains the CLI version/session metadata as soon as ANY
        /// connection actually produces it, independent of any state
        /// transition (see AgentClient.InitMessageReceived's doc comment
        /// for why this cannot just be read off StateChanged/
        /// SessionIdChanged instead) -- the fix for the "About shows CLI
        /// version: not connected while Ready" defect (docs/design-notes/
        /// 2026-08-01-init-message-retention.md). RaiseChanged() here is
        /// what lets SettingsView's About row repaint at the exact moment
        /// this becomes available, even on the (same-state, no
        /// StateChanged) path where Ready was already reached from the
        /// "initialize" control_response alone.
        /// </summary>
        private static void OnInitMessageReceived(SystemInitMessage message)
        {
            _lastKnownInitMessage = message;
            CurrentAcpAgentName = CurrentBackend == AgentBackend.AcpCustom && message != null
                ? ExtractAgentProductName(message.ClaudeCodeVersion)
                : null;
            // The CLI version also survives DOMAIN RELOADS via
            // SessionStateBridge: a resumed connection can sit in Ready
            // without ever re-emitting system/init (live-observed
            // 2026-08-01), and the in-memory message is lost with the old
            // domain -- without this, About shows "not connected" after
            // every reload until the next init.
            if (message != null && !string.IsNullOrEmpty(message.ClaudeCodeVersion))
            {
                SessionStateBridge.LastCliVersion = message.ClaudeCodeVersion;
            }
            RefreshModelCatalogCache();
            RefreshAgentTypeCatalogCache(message);
            RefreshSlashCommandCatalogCache();
            SendMcpReconnectIfUapOpsFailed(message);
            AppendApiKeyAuthNoteIfNeeded(message);
            RaiseChanged();
        }

        /// <summary>
        /// Visibility, not blocking (docs/design-notes/2026-09-10-claude-
        /// api-key-auth-passthrough.md): once this session's system/init
        /// reports an apiKeySource other than "none"/empty -- meaning
        /// ANTHROPIC_API_KEY (or an apiKeyHelper) is actually in effect and
        /// usage is billed to that key rather than a subscription -- say so
        /// once, in the transcript, the same "once per spawn" shape as
        /// AppendScriptGateInertWarning. The Account card
        /// (SettingsView.RefreshAccountSection) shows the equivalent note
        /// for as long as the condition holds; this is the one-shot
        /// heads-up for whoever is only watching the chat.
        /// </summary>
        private static void AppendApiKeyAuthNoteIfNeeded(SystemInitMessage message)
        {
            if (_apiKeyAuthNoted || !IsClaudeBackend
                || message == null || string.IsNullOrEmpty(message.ApiKeySource)
                || string.Equals(message.ApiKeySource, "none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _apiKeyAuthNoted = true;
            string text = L10n.F(L10n.S.HubApiKeyAuthNoteFmt, message.ApiKeySource);
            Log(text);
            AppendSystemNote(text, false);
            SessionCache.Save(Session, _lastModelUsage);
        }

        /// <summary>
        /// The mcp_reconnect safety net (design section 1.1/8.5 criterion
        /// #6, docs/research/08-mcp-transport.md section 3.4): if this
        /// system/init reports the UapOps server as "failed" -- the
        /// listen-before-spawn race lost anyway, e.g. a --resume spawned
        /// while the editor's own UapOps HttpListener had not started
        /// listening yet -- and the Unity-side server IS now up, ask the
        /// CLI to retry the connection without a process restart. A no-op
        /// when the server was never reported at all (uapOpsEnabled was
        /// off at spawn time, or this is not even a system/init this
        /// method should react to) or when it is already connected/
        /// pending. Deliberately does not loop/retry beyond this single
        /// send per system/init -- R08 measured mcp_reconnect as a one-shot
        /// fix, and system/init itself can resend on later turns (section
        /// 3.1), giving this another chance if the first attempt lost a
        /// second race.
        /// </summary>
        private static void SendMcpReconnectIfUapOpsFailed(SystemInitMessage message)
        {
            if (_client == null || !ShouldSendUapOpsMcpReconnect(message, UapOpsServer.IsRunning))
            {
                return;
            }
            _client.SendMcpReconnect(UapOpsMcpConfig.ServerName);
        }

        /// <summary>
        /// Pure decision behind <see cref="SendMcpReconnectIfUapOpsFailed"/>,
        /// extracted for testability the same way ResolveSpawnModel covers
        /// StartClient's --model decision (that method itself needs a real
        /// AgentClient/UapOpsServer, so this is what
        /// AgentHubMcpReconnectTests exercises directly): true only when
        /// <paramref name="message"/> reports the UapOps server
        /// (UapOpsMcpConfig.ServerName) as "failed" AND the Unity-side
        /// server is actually running right now -- sending mcp_reconnect
        /// while it is still down would just fail again for no reason.
        /// </summary>
        internal static bool ShouldSendUapOpsMcpReconnect(SystemInitMessage message, bool uapOpsServerRunning)
        {
            if (message == null || message.McpServers == null || !uapOpsServerRunning)
            {
                return false;
            }
            for (int i = 0; i < message.McpServers.Length; i++)
            {
                McpServerStatus status = message.McpServers[i];
                if (status != null
                    && string.Equals(status.Name, UapOpsMcpConfig.ServerName, StringComparison.Ordinal)
                    && string.Equals(status.Status, "failed", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Caches the CURRENT client's model catalog (docs/design-notes/
        /// 2026-08-01-model-settings.md section 1) into PanelSettings.
        /// modelCatalog so the Settings "Default model" dropdown keeps
        /// working across an editor restart, before this session's own
        /// connection has re-supplied the live list. Reads
        /// _client.InitializeResponse (the "initialize" control_response,
        /// NOT this SystemInitMessage -- system/init itself carries no
        /// models[] field) rather than the message parameter; safe to call
        /// from OnInitMessageReceived because R02b's captured wire order
        /// has the "initialize" control_response arriving BEFORE
        /// system/init on every observed connection, so InitializeResponse
        /// is already populated by the time this runs. A no-op when
        /// nothing has been received yet, or when the current models[]
        /// list is empty (never clobbers a previously cached non-empty
        /// list with nothing).
        /// </summary>
        private static void RefreshModelCatalogCache()
        {
            if (_client == null || _client.InitializeResponse == null)
            {
                return;
            }
            List<Colloid.AgentPanel.UI.HeaderView.ModelOption> options =
                Colloid.AgentPanel.UI.HeaderView.ParseModels(_client.InitializeResponse.Response["models"]);
            if (options.Count == 0)
            {
                return;
            }
            var catalog = new List<ModelCatalogEntry>(options.Count);
            for (int i = 0; i < options.Count; i++)
            {
                catalog.Add(new ModelCatalogEntry
                {
                    value = options[i].Value ?? string.Empty,
                    displayName = options[i].DisplayName ?? string.Empty,
                    resolvedModel = options[i].ResolvedModel ?? string.Empty,
                    description = options[i].Description ?? string.Empty
                });
            }
            PanelStateStore.instance.Settings.modelCatalog = catalog;
            PanelStateStore.instance.SaveNow();
        }

        /// <summary>
        /// Caches the CURRENT connection's self-reported subagent type
        /// names (system/init.agents[], docs/design-notes/2026-08-01-
        /// model-settings-rework.md section 4.2) into PanelSettings.
        /// agentTypeCatalog, feeding the per-type override rows' agent-name
        /// PopupField (SettingsView) across an editor restart the same way
        /// RefreshModelCatalogCache does for modelCatalog. Tolerant: a null
        /// message or an empty/malformed agents[] array (SystemInitMessage.
        /// Agents is never itself null -- JsonNode.AsStringArray always
        /// returns at least an empty array) is a no-op that never clobbers
        /// a previously cached non-empty list with nothing.
        /// </summary>
        private static void RefreshAgentTypeCatalogCache(SystemInitMessage message)
        {
            if (message == null || message.Agents == null || message.Agents.Length == 0)
            {
                return;
            }
            PanelStateStore.instance.Settings.agentTypeCatalog = new List<string>(message.Agents);
            PanelStateStore.instance.SaveNow();
        }

        /// <summary>
        /// Caches the CURRENT connection's slash-command catalog into
        /// PanelSettings.slashCommandCatalog (design note 2026-09-07-slash-
        /// commands-and-compaction.md section 1.2): the initialize
        /// response's commands[] (name/description/argumentHint) merged
        /// with system/init's names-only slash_commands[]
        /// (SlashCommandCatalog.Merge). Called from both arrival points.
        /// Tolerant like the other two catalog refreshes: nothing known
        /// yet (no client, neither message) is a no-op that never clobbers
        /// a previously cached non-empty list with nothing.
        /// </summary>
        private static void RefreshSlashCommandCatalogCache()
        {
            if (_client == null)
            {
                return;
            }
            List<SlashCommandEntry> rich = null;
            if (_client.InitializeResponse != null && _client.InitializeResponse.Response != null)
            {
                rich = SlashCommandCatalog.ParseInitializeCommands(
                    _client.InitializeResponse.Response["commands"]);
            }
            string[] names = _client.InitMessage != null ? _client.InitMessage.SlashCommands : null;
            List<SlashCommandEntry> merged = SlashCommandCatalog.Merge(rich, names);
            if (merged.Count == 0)
            {
                return;
            }
            PanelStateStore.instance.Settings.slashCommandCatalog = merged;
            PanelStateStore.instance.SaveNow();
        }

        private static void OnProcessDied(string reason)
        {
            // HUB-1: the shared non-clean-exit cleanup. The client already
            // discarded its pending can_use_tool request
            // (HandleProcessDeath), so no answer can ever be delivered for
            // it -- clearing hides the permission card immediately,
            // otherwise it would stay visible (and silently no-op on
            // Allow) for a process that no longer exists, indefinitely so
            // in the crash-loop branch below where no restart ever runs.
            // clearContinuationFlag: TRUE (defect 5) -- kept in BOTH this
            // handler and OnStateChanged's Errored branch deliberately:
            // this fires for every process death regardless of exactly how
            // AgentClient reached Errored, while OnStateChanged also
            // covers Errored transitions that never raise ProcessDied at
            // all (e.g. StartClient failing before a process ever spawns).
            AbortOpenTurn(clearContinuationFlag: true);
            bool isAcpBackend = AgentBackends.IsAcp(CurrentBackend);
            if (!ShouldAutoReconnectAfterDeath(
                    _acpSignInFailedThisProcess ? (AcpSignInError ?? string.Empty) : AcpSignInError,
                    isAcpBackend, _reachedReadyThisProcess))
            {
                // Design note 2026-09-10-in-panel-install-and-sign-in.md
                // section 2.4: the process died because sign-in failed.
                // Respawning would ask the same agent the same question and
                // fail the same way (live report: four identical rounds),
                // so stop here with the next step spelled out. Not counted
                // as a crash: the user's Sign in / Reconnect starts fresh.
                var signInNote = new ChatMessage { role = ChatMessage.RoleSystem };
                string login = AgentBackends.LoginCommand(CurrentBackend);
                string loginOrName = string.IsNullOrEmpty(login) ? AgentBackends.DisplayName(CurrentBackend) : login;
                bool signInFailed = _acpSignInFailedThisProcess || AcpSignInError != null;
                // Sign-in failed means every method the agent offers was
                // tried and rejected -- for Gemini CLI that ends at
                // `gemini-api-key` with no GEMINI_API_KEY set -- so the note
                // has to name the variable and where it goes, not just the
                // login command (design note 2026-09-10-acp-auth-guidance-
                // and-method-display.md section 3).
                string signInText = signInFailed
                    ? AppendSentence(
                        L10n.F(L10n.S.HubAcpSignInRequiredNoteFmt, AgentBackends.DisplayName(CurrentBackend), loginOrName),
                        ComposeAcpApiKeyGuidance(CurrentBackend))
                    : L10n.F(L10n.S.HubAcpHandshakeDeathNoteFmt, AgentBackends.DisplayName(CurrentBackend), reason, loginOrName);
                signInNote.Add(ChatMessageBlock.MakeSystemNote(signInText, warning: true));
                Session.AddMessage(signInNote);
                RaiseChanged();
                return;
            }
            _consecutiveDeaths++;
            _lastDeathUtcTicks = DateTime.UtcNow.Ticks;
            var note = new ChatMessage { role = ChatMessage.RoleSystem };
            if (_consecutiveDeaths <= MaxConsecutiveRestarts)
            {
                // warning:true (design note 2026-08-14-ui-polish-audit.md
                // contract item 2): this is the process-exited/reconnecting
                // note -- something actually went wrong, unlike every other
                // MakeSystemNote call site in this file, which reports
                // routine bookkeeping.
                note.Add(ChatMessageBlock.MakeSystemNote(
                    L10n.F(L10n.S.HubProcessDiedReconnectingFmt, reason), warning: true));
                Session.AddMessage(note);
                RaiseChanged();
                string resumeId = Session.sessionId;
                int scheduledEpoch = _clientEpoch;
                UnityEditor.EditorApplication.delayCall += delegate
                {
                    // Staleness guard: if anything restarted or discarded
                    // the client in the meantime (CompileGate drain,
                    // StartFresh, Shutdown), this reconnect is obsolete --
                    // running it would kill the fresh client and possibly
                    // resume an abandoned session id.
                    if (scheduledEpoch != _clientEpoch)
                    {
                        return;
                    }
                    if (_client != null && _client.State != AgentClientState.Errored)
                    {
                        return;
                    }
                    StartClient(string.IsNullOrEmpty(resumeId) ? null : resumeId);
                };
            }
            else
            {
                note.Add(ChatMessageBlock.MakeError(
                    L10n.F(L10n.S.HubProcessDiedSuspendedFmt, reason)));
                Session.AddMessage(note);
                RaiseChanged();
            }
        }

        // -- Session mutation helpers ----------------------------------------------

        private static ChatMessage EnsureStreamingAssistant()
        {
            if (_streamingAssistant == null)
            {
                _streamingAssistant = new ChatMessage
                {
                    role = ChatMessage.RoleAssistant,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    turnId = _client != null ? _client.CurrentTurnId : 0
                };
                Session.AddMessage(_streamingAssistant);
            }
            return _streamingAssistant;
        }

        private static ChatMessageBlock EnsureStreamingBlock(ChatBlockKind kind)
        {
            ChatMessage message = EnsureStreamingAssistant();
            ChatMessageBlock last = message.LastBlock();
            if (last != null && last.kind == kind && last.streaming)
            {
                return last;
            }
            ChatMessageBlock block = kind == ChatBlockKind.Thinking
                ? ChatMessageBlock.MakeThinking(string.Empty, true)
                : ChatMessageBlock.MakeText(string.Empty, true);
            message.Add(block);
            return block;
        }

        private static void RemoveTrailingStreamingBlocks(ChatMessage message)
        {
            while (message.blocks.Count > 0)
            {
                ChatMessageBlock last = message.blocks[message.blocks.Count - 1];
                if (!last.streaming)
                {
                    break;
                }
                message.blocks.RemoveAt(message.blocks.Count - 1);
            }
        }

        /// <summary>Snapshot of one streamed Thinking block's accumulated text
        /// and token estimate, captured before it gets discarded by
        /// RemoveTrailingStreamingBlocks (design note 2026-08-01-thinking-
        /// content-loss.md sections 2 and 5 point 4).</summary>
        private struct ThinkingTail
        {
            public string Text;
            public long Tokens;
        }

        /// <summary>
        /// Collects (text, thinkingTokens) from every TRAILING streaming
        /// Thinking block on <paramref name="message"/> -- the exact same
        /// "trailing streaming run" RemoveTrailingStreamingBlocks is about
        /// to discard -- in the order they appear, so
        /// AppendFinalizedThinking can correlate them 1:1 with the
        /// finalized message's own Thinking content blocks (design note
        /// section 2: multiple thinking blocks in one turn are correlated
        /// in appearance order).
        /// </summary>
        private static List<ThinkingTail> ExtractTrailingThinkingTails(ChatMessage message)
        {
            var result = new List<ThinkingTail>();
            int i = message.blocks.Count - 1;
            while (i >= 0 && message.blocks[i].streaming)
            {
                if (message.blocks[i].kind == ChatBlockKind.Thinking)
                {
                    result.Insert(0, new ThinkingTail
                    {
                        Text = message.blocks[i].text,
                        Tokens = message.blocks[i].thinkingTokens
                    });
                }
                i--;
            }
            return result;
        }

        /// <summary>
        /// Adds one finalized Thinking block, merging in the next
        /// unconsumed pending tail (if any): finalized non-empty text
        /// always wins (the current CLI never sends this, but a future one
        /// might -- design note section 2), and when the finalized text is
        /// empty the pending tail's own accumulated text is kept instead.
        /// The tail's token estimate always carries over either way. A
        /// block with neither text nor a token estimate had no real
        /// thinking activity and is dropped, matching the pre-existing
        /// "skip empty thinking" behavior.
        /// </summary>
        private static void AppendFinalizedThinking(ChatMessage target, string finalizedText,
            List<ThinkingTail> pendingThinking, ref int pendingThinkingIndex)
        {
            string text = finalizedText ?? string.Empty;
            long tokens = 0;
            if (pendingThinkingIndex < pendingThinking.Count)
            {
                ThinkingTail tail = pendingThinking[pendingThinkingIndex];
                pendingThinkingIndex++;
                tokens = tail.Tokens;
                if (string.IsNullOrEmpty(text))
                {
                    text = tail.Text ?? string.Empty;
                }
            }
            if (string.IsNullOrEmpty(text) && tokens <= 0)
            {
                return;
            }
            ChatMessageBlock finalized = ChatMessageBlock.MakeThinking(text);
            finalized.thinkingTokens = tokens;
            target.Add(finalized);
        }

        /// <summary>
        /// Fallback for a pending-tail/finalized-block count mismatch
        /// (design note section 2): folds any streamed Thinking tails that
        /// never matched a finalized Thinking content block onto the LAST
        /// Thinking block already added to <paramref name="target"/> (if
        /// any), or appends a new one. In every real capture to date there
        /// is at most one Thinking block per assistant-message completion,
        /// so this only matters if a future CLI batches several into one
        /// message.
        /// </summary>
        private static void MergeLeftoverThinkingTails(ChatMessage target,
            List<ThinkingTail> pendingThinking, int consumedCount)
        {
            if (consumedCount >= pendingThinking.Count)
            {
                return;
            }
            string leftoverText = string.Empty;
            long leftoverTokens = 0;
            for (int i = consumedCount; i < pendingThinking.Count; i++)
            {
                if (!string.IsNullOrEmpty(pendingThinking[i].Text))
                {
                    leftoverText += pendingThinking[i].Text;
                }
                if (pendingThinking[i].Tokens > leftoverTokens)
                {
                    leftoverTokens = pendingThinking[i].Tokens;
                }
            }
            if (string.IsNullOrEmpty(leftoverText) && leftoverTokens <= 0)
            {
                return;
            }
            ChatMessageBlock lastThinking = null;
            for (int i = target.blocks.Count - 1; i >= 0; i--)
            {
                if (target.blocks[i].kind == ChatBlockKind.Thinking)
                {
                    lastThinking = target.blocks[i];
                    break;
                }
            }
            if (lastThinking != null)
            {
                lastThinking.text += leftoverText;
                if (leftoverTokens > lastThinking.thinkingTokens)
                {
                    lastThinking.thinkingTokens = leftoverTokens;
                }
                return;
            }
            ChatMessageBlock added = ChatMessageBlock.MakeThinking(leftoverText);
            added.thinkingTokens = leftoverTokens;
            target.Add(added);
        }

        private static void FinalizeStreamingMessage()
        {
            if (_streamingAssistant != null)
            {
                for (int i = 0; i < _streamingAssistant.blocks.Count; i++)
                {
                    _streamingAssistant.blocks[i].streaming = false;
                }
                _streamingAssistant = null;
            }
            DemoteOpenRecordsAndClear();
        }

        /// <summary>
        /// Demotes any still-"running"/Running ToolCallRecord/SubagentRecord
        /// left in _openToolCalls/_openSubagents to a terminal status, prunes
        /// their _taskIdToToolUseId entries, then clears both dictionaries.
        /// Shared by every path that can end a turn or a client connection
        /// while a tool call or subagent is still mid-flight:
        /// - FinalizeStreamingMessage (interrupted turn / process death /
        ///   the silence backstop -- design note section 3)
        /// - TearDownClient (Shutdown/ShutdownForReload/Reconnect/StartFresh/
        ///   SwitchToSession/a fresh StartClient tearing down the previous
        ///   client): without this, a domain reload or editor quit mid-tool
        ///   would persist a SessionCache entry whose status is permanently
        ///   stuck at "running"/Running (SubagentCard/ToolActivityCard both
        ///   render that as a spinner forever, since the new process after
        ///   --resume never emits a tool_result/task_notification for the
        ///   old tool_use_id).
        /// Not called from OnToolResultReceived's own per-subagent removal
        /// (normal completion): that path already knows the exact outcome
        /// (completed/failed) and prunes its own single taskId entry there.
        /// </summary>
        private static void DemoteOpenRecordsAndClear()
        {
            foreach (KeyValuePair<string, ToolCallRecord> pair in _openToolCalls)
            {
                if (pair.Value.status == ToolCallStatus.Running)
                {
                    pair.Value.status = ToolCallStatus.Pending;
                }
            }
            _openToolCalls.Clear();
            foreach (KeyValuePair<string, SubagentRecord> pair in _openSubagents)
            {
                if (pair.Value.status == "running")
                {
                    pair.Value.status = "stopped";
                }
                if (!string.IsNullOrEmpty(pair.Value.taskId))
                {
                    _taskIdToToolUseId.Remove(pair.Value.taskId);
                }
            }
            _openSubagents.Clear();
        }

        // -- Small helpers ------------------------------------------------------------

        private static IProcessKiller CreateKiller()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return new WindowsProcessKiller(Log);
            }
            return new BasicUnixKiller();
        }

        /// <summary>
        /// See LastSpawnedSettingsSnapshot's doc comment: only the fields
        /// SettingsChangeDetector compares are copied (plus model/
        /// agentModelOverrides, kept for other call sites even though the
        /// detector itself no longer reads them -- see its class doc
        /// comment for why both were removed from RequiresReconnect).
        /// uapOpsModules (2026-08-02 review fix, Stream C1 regression) is
        /// deep-copied into its own List&lt;string&gt; instance for the same
        /// reason CloneAgentModelOverrides deep-clones its entries below: the
        /// Settings UI's module toggle handlers mutate PanelStateStore.
        /// instance.Settings.uapOpsModules IN PLACE (Add/Remove), so sharing
        /// the same list reference here would let a later toggle silently
        /// mutate this snapshot too, permanently hiding the very drift
        /// SettingsChangeDetector.RequiresReconnect now exists to catch.
        ///
        /// extensionProfilesEnabled / approvedProfileHashes (2026-08-03 fix)
        /// are copied for the OPPOSITE failure: they were missing entirely,
        /// so every snapshot carried PanelSettings' own field-initializer
        /// values (extensionProfilesEnabled == true, an empty hash list)
        /// instead of what StartClient actually spawned with (it reads both
        /// straight off `settings` for ExtensionProfileService.
        /// ComposeAppendSection). Because RequiresReconnect DOES compare
        /// both, the snapshot disagreed with reality the moment a user
        /// turned profiles off or approved a single project-local profile
        /// -- and stayed disagreeing forever, since reconnecting rebuilds
        /// the snapshot with the same wrong defaults. That is a permanent
        /// false "settings changed, reconnect to apply" state (and, via
        /// ComputeAutoApplyReconnectNeeded, an auto-apply that can never
        /// settle). approvedProfileHashes is deep-copied for the same
        /// in-place-mutation reason as the lists above: the Settings
        /// approve/revoke handlers edit that list directly.
        ///
        /// Internal (rather than private) purely as a unit-test seam --
        /// CloneNextSpawnOnlyFieldsTests exercises it directly instead of
        /// through a real CLI spawn.
        /// </summary>
        internal static PanelSettings CloneNextSpawnOnlyFields(PanelSettings source)
        {
            return new PanelSettings
            {
                cliManualPath = source.cliManualPath ?? string.Empty,
                agentBackend = source.agentBackend,
                acpCommand = source.acpCommand ?? string.Empty,
                acpArguments = source.acpArguments ?? string.Empty,
                acpAuthMethod = source.acpAuthMethod ?? string.Empty,
                model = source.model ?? string.Empty,
                subagentModel = source.subagentModel ?? string.Empty,
                claudeAuth = source.claudeAuth,
                subagentCostPolicy = source.subagentCostPolicy,
                dangerouslySkipPermissions = source.dangerouslySkipPermissions,
                showThinking = source.showThinking,
                uapOpsEnabled = source.uapOpsEnabled,
                uapScriptGateEnabled = source.uapScriptGateEnabled,
                allowedTools = source.allowedTools != null
                    ? new List<string>(source.allowedTools) : new List<string>(),
                disallowedTools = source.disallowedTools != null
                    ? new List<string>(source.disallowedTools) : new List<string>(),
                uapOpsModules = source.uapOpsModules != null
                    ? new List<string>(source.uapOpsModules) : new List<string>(),
                extensionProfilesEnabled = source.extensionProfilesEnabled,
                unityPluginSteeringEnabled = source.unityPluginSteeringEnabled,
                approvedProfileHashes = source.approvedProfileHashes != null
                    ? new List<string>(source.approvedProfileHashes) : new List<string>(),
                agentModelOverrides = CloneAgentModelOverrides(source.agentModelOverrides)
            };
        }

        /// <summary>
        /// Deep-clones each entry (not just the list) -- AgentModelOverride
        /// is a mutable reference type the Settings UI edits in place
        /// (agentName/modelAlias fields, like QuickActionStore's rows), so
        /// a shallow List&lt;AgentModelOverride&gt; copy sharing the SAME
        /// objects would let a later live edit also mutate this snapshot,
        /// permanently hiding a real pending change from
        /// SettingsChangeDetector.RequiresReconnect.
        /// </summary>
        private static List<AgentModelOverride> CloneAgentModelOverrides(List<AgentModelOverride> source)
        {
            var result = new List<AgentModelOverride>();
            if (source == null)
            {
                return result;
            }
            for (int i = 0; i < source.Count; i++)
            {
                AgentModelOverride entry = source[i];
                result.Add(new AgentModelOverride
                {
                    agentName = entry != null ? (entry.agentName ?? string.Empty) : string.Empty,
                    modelAlias = entry != null ? (entry.modelAlias ?? string.Empty) : string.Empty
                });
            }
            return result;
        }

        /// <summary>First user message's PlainText(), or empty (SwitchToSession title fallback).</summary>
        private static string FirstUserPlainText(List<ChatMessage> messages)
        {
            if (messages == null)
            {
                return string.Empty;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] != null && messages[i].role == ChatMessage.RoleUser)
                {
                    string text = messages[i].PlainText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }
            return string.Empty;
        }

        private static string GetProjectRoot()
        {
            // Application.dataPath == <project>/Assets
            return Path.GetDirectoryName(Application.dataPath);
        }

        /// <summary>
        /// True when an AgentPanelWindow currently has OS input focus --
        /// the gate for both notification beeps (design note 2026-08-01
        /// #4: beeps only fire while the panel is NOT the focused window,
        /// so a user actively looking at the panel is never startled by
        /// its own activity).
        /// </summary>
        private static bool IsPanelFocused()
        {
            return UnityEditor.EditorWindow.focusedWindow is Colloid.AgentPanel.UI.AgentPanelWindow;
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, maxChars - 3) + "...";
        }

        private static void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>
        /// The panel's single prefixed log sink. Internal rather than
        /// private so sibling Integration helpers that own their own files
        /// (SessionMetaStoreAccess) log through the same prefix instead of
        /// calling Debug.Log directly and producing lines the user cannot
        /// attribute to this package.
        /// </summary>
        internal static void Log(string message)
        {
            Debug.Log(LogPrefix + message);
        }

        /// <summary>
        /// Minimal non-Windows kill fallback until a real Unix process-group
        /// killer lands (ARCHITECTURE.md D1 seam). Kills only the root
        /// process; children exit when their stdio pipes close.
        /// </summary>
        private sealed class BasicUnixKiller : IProcessKiller
        {
            public bool KillTree(int pid)
            {
                try
                {
                    using (var process = System.Diagnostics.Process.GetProcessById(pid))
                    {
                        process.Kill();
                    }
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
