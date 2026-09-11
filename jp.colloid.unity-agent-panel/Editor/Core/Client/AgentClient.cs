using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Core.Client
{
    /// <summary>Options for AgentClient.Start (ARCHITECTURE.md D2).</summary>
    public sealed class AgentClientOptions
    {
        /// <summary>Full path of the claude executable. Required.</summary>
        public string CliPath;
        /// <summary>Working directory (Unity project root). Required.</summary>
        public string WorkingDirectory;
        /// <summary>Session id for --resume. Null/empty = fresh session.</summary>
        public string ResumeSessionId;
        /// <summary>Model alias or full name for --model. Null/empty = CLI default.</summary>
        public string Model;
        /// <summary>--permission-mode value (default/plan/acceptEdits/...). Null/empty = omit.</summary>
        public string PermissionMode;
        /// <summary>
        /// --allowedTools static list (Settings UI, applied at next spawn
        /// only -- there is no live control_request for this in the D2
        /// protocol). Null/empty omits the flag entirely, leaving the
        /// spawned argument string byte-identical to before this option
        /// existed.
        /// </summary>
        public List<string> AllowedTools;
        /// <summary>--disallowedTools static list. Same next-spawn-only caveat as AllowedTools.</summary>
        public List<string> DisallowedTools;
        /// <summary>
        /// --dangerously-skip-permissions (ARCHITECTURE.md D3: default OFF,
        /// explicit opt-in only). Next-spawn-only, like the tool lists.
        /// </summary>
        public bool DangerouslySkipPermissions;
        /// <summary>
        /// --append-system-prompt text (Settings UI "Custom instructions",
        /// docs/design-notes/2026-08-01-settings-enrichment.md #1). The
        /// text itself lives in a plain-text sidecar file
        /// (Model.CustomInstructionsFile), never PanelSettings/UnityYAML --
        /// it can be code-shaped (brace-heavy). Null/empty omits the flag
        /// entirely, leaving the spawned argument string byte-identical to
        /// before this option existed. Next-spawn-only, like the tool
        /// lists.
        /// </summary>
        public string AppendSystemPrompt;
        /// <summary>
        /// Passes the (hidden, but publicly usable) `--thinking-display
        /// summarized` CLI argument, which is the only verified way to make
        /// the headless stream-json protocol emit non-empty thinking text
        /// (default off, thinking_delta carries only estimated_tokens; see
        /// docs/design-notes/2026-08-01-thinking-content-loss.md section
        /// 4c). Wired 1:1 from the Settings "Show thinking blocks" toggle
        /// (PanelSettings.showThinking) -- there is no separate control.
        /// Default false omits the flag entirely, leaving the spawned
        /// argument string byte-identical to before this option existed.
        /// Next-spawn-only, like the tool lists.
        /// </summary>
        public bool ThinkingDisplaySummarized;
        /// <summary>
        /// Blanket subagent model alias (PanelSettings.subagentModel,
        /// docs/design-notes/2026-08-01-model-settings-rework.md section
        /// 4.2), applied as the CLAUDE_CODE_SUBAGENT_MODEL environment
        /// variable on the spawned process -- NOT a CLI argument, so it is
        /// plumbed straight through to ICliTransport.Start rather than
        /// BuildArguments below (docs/research/07-model-configuration.md
        /// section 11: env var, not --agents/--model). Null/empty leaves
        /// the variable completely untouched on the child environment
        /// (ClaudeCliProcess.ComputeSubagentModelEnvEntries never clears an
        /// inherited value); unlike every next-spawn-only option above,
        /// this ALSO takes effect on a --resume spawn (measured: the CLI
        /// reads the CURRENT env value at process start, not a
        /// snapshot-at-session-creation like .claude/agents/*.md).
        /// </summary>
        public string SubagentModel;

        /// <summary>
        /// PanelSettings.claudeAuth (docs/design-notes/2026-09-10-claude-
        /// api-key-auth-passthrough.md): whether ClaudeCliProcess should
        /// remove ANTHROPIC_API_KEY from the spawned CLI's environment.
        /// ClaudeAuthMode.Auto (default) leaves it untouched -- the CLI
        /// picks auth exactly as it would in a terminal. Plumbed straight
        /// through to ICliTransport.Start like SubagentModel above;
        /// non-Claude-Code transports ignore it (see ICliTransport.Start's
        /// doc comment).
        /// </summary>
        public ClaudeAuthMode ClaudeAuth = ClaudeAuthMode.Auto;
        // NOTE: subagent name -> model alias overrides (Settings "Model"
        // section detail table) are NOT a spawn argument here. R07 section
        // 10 found that a `--agents '<json>'` override is silently ignored
        // by the CLI whenever it is combined with --resume (is_error:false,
        // exitCode:0, no error surfaced anywhere -- the override just never
        // takes effect) -- and the panel ALWAYS spawns with --resume once a
        // session exists, so that flag was dead weight in real use. The
        // working, --resume-proof mechanism is instead a set of
        // `<project>/.claude/agents/<AgentName>.md` files the CLI rereads
        // from disk on every process start regardless of --resume; see
        // Colloid.AgentPanel.Model.AgentDefinitionFileWriter (Unity-side,
        // written by AgentHub.StartClient just before this Start() call) --
        // never routed through AgentClientOptions/BuildArguments.

        /// <summary>
        /// The raw `--mcp-config` value (docs/research/08-mcp-transport.md
        /// section 1.2: the CLI auto-detects a JSON FILE PATH vs an inline
        /// JSON string and connects identically either way). Normally the
        /// ABSOLUTE path of the config file written by
        /// Colloid.AgentPanel.Ops.UapOpsMcpConfig.EnsureConfigFileWritten --
        /// SEC-2: the payload carries the UapOps server's Bearer token, so
        /// it must not ride the world-readable process command line; the
        /// inline JSON shape remains only as AgentHub.ComputeMcpConfigValue's
        /// fallback when the file cannot be written. (UapOpsServer is not
        /// referenced from this Core.Client assembly layer to keep it
        /// transport-agnostic; AgentHub wires the two together.) Null/empty
        /// omits BOTH `--mcp-config` and `--strict-mcp-config` entirely,
        /// leaving the spawned argument string byte-identical to before this
        /// option existed -- the PanelSettings.uapOpsEnabled master toggle's
        /// OFF state. `--strict-mcp-config` is always paired with a
        /// non-empty value (docs/research/08-mcp-transport.md section 1.2:
        /// without it, the user's own global/.mcp.json MCP servers would
        /// silently merge in).
        /// </summary>
        public string McpConfigJson;

        /// <summary>
        /// Absolute path of the script validation gate's generated
        /// `--settings` JSON (Colloid.AgentPanel.Ops.GateHookInstaller,
        /// design section 8.7): wires a PreToolUse hook that fires even
        /// under `--permission-mode acceptEdits`, where the can_use_tool
        /// pre-filter alone was measured NOT to (section 8.7's live-E2E
        /// finding). Null/empty omits `--settings` entirely, leaving the
        /// spawned argument string byte-identical to before this option
        /// existed -- PanelSettings.uapScriptGateEnabled OFF, or a
        /// non-Windows platform (v1 is Windows-only; AgentHub is
        /// responsible for both gates, not this option). MUST be an
        /// absolute path: the CLI resolves `--settings` relative to its
        /// OWN cwd, not the panel's (design section 8.7's measured
        /// contract).
        /// </summary>
        public string SettingsFilePath;
        /// <summary>
        /// `--plugin-dir` paths, one flag per entry (docs/design-notes/
        /// 2026-09-10-unity-official-plugin-integration.md section 1.5).
        /// Production leaves this null today: a marketplace-installed
        /// plugin loads under plain `-p` without it (measured 2026-09-10),
        /// and the flag exists here for the day `--bare` becomes the `-p`
        /// default. Null/empty omits the flag entirely. Next-spawn-only.
        /// </summary>
        public List<string> PluginDirs;

        /// <summary>
        /// How long Start waits for the initialize handshake before
        /// declaring the process dead (design note
        /// docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md
        /// section 2). 0 or negative = PendingRequestMap.DefaultTimeoutSeconds
        /// (60 s, the Claude Code value). An ACP backend that has to sign
        /// the user in through a browser first needs minutes, not seconds.
        /// </summary>
        public double InitializeTimeoutSeconds;
    }

    /// <summary>
    /// The user's answer to a can_use_tool permission request. Built by the
    /// permission card UI and translated into a control_response by
    /// AgentClient.RespondToPermission.
    /// </summary>
    public sealed class PermissionDecision
    {
        public bool Allow { get; private set; }
        /// <summary>allow: the (possibly edited) tool input to run with. Optional.</summary>
        public JsonNode UpdatedInput { get; private set; }
        /// <summary>allow: permission rules to persist ("always allow" scopes). Optional.</summary>
        public JsonNode UpdatedPermissions { get; private set; }
        /// <summary>deny: explanation shown to the model.</summary>
        public string DenyMessage { get; private set; }
        /// <summary>deny: also abort the whole turn.</summary>
        public bool DenyInterrupt { get; private set; }

        private PermissionDecision()
        {
        }

        public static PermissionDecision AllowTool(JsonNode updatedInput = null,
            JsonNode updatedPermissions = null)
        {
            return new PermissionDecision
            {
                Allow = true,
                UpdatedInput = updatedInput,
                UpdatedPermissions = updatedPermissions
            };
        }

        public static PermissionDecision DenyTool(string message, bool interrupt = false)
        {
            return new PermissionDecision
            {
                Allow = false,
                DenyMessage = message ?? string.Empty,
                DenyInterrupt = interrupt
            };
        }
    }

    /// <summary>
    /// Core state machine driving one resident bidirectional stream-json CLI
    /// process (ARCHITECTURE.md 3.4, D2). Unity-independent.
    ///
    /// Threading model: the transport fills LineChannels from reader
    /// threads; the owner calls Pump() from the main thread (via
    /// EditorUpdatePump). ALL events fire synchronously on the thread that
    /// calls Pump()/SendUserText()/etc. -- consumers never need to marshal.
    ///
    /// Robustness contract:
    /// - Process death is detected via the transport Exited flag and failed
    ///   stdin writes; both funnel into Errored + ProcessDied.
    /// - control_responses may arrive before or after related events;
    ///   correlation goes through PendingRequestMap only.
    /// - Unknown inbound messages are ignored; dropped lines never stop the
    ///   pump (StreamJsonMessage.ParseLine returns null, already logged).
    /// </summary>
    public sealed class AgentClient : IDisposable
    {
        private readonly ICliTransport _transport;
        private readonly Action<string> _logger;
        private readonly TurnTracker _turns = new TurnTracker();
        private readonly PendingRequestMap _requests = new PendingRequestMap();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<string> _lineBuffer = new List<string>(32);

        private volatile bool _transportExited;
        private bool _deathHandled;
        private bool _stopRequested;
        private string _pendingPermissionRequestId;
        private ControlRequestMessage _pendingPermissionRequest;

        /// <summary>
        /// CORE-6: can_use_tool requests beyond the single active one wait
        /// here in arrival order. The CLI can issue permissions for
        /// parallel tool_use blocks concurrently; the old unconditional
        /// overwrite evicted the first request, which then could never be
        /// answered (RespondToPermission id mismatch) and the CLI waited
        /// on it until the silence backstop. The active pending pair above
        /// stays the ONLY thing consumers see -- the upper layers'
        /// single-active-permission model is unchanged.
        /// </summary>
        private readonly Queue<ControlRequestMessage> _permissionQueue =
            new Queue<ControlRequestMessage>();

        /// <summary>Re-entrancy guard: a synchronous auto-approve answers
        /// from INSIDE the PermissionRequested raise; the outer promote
        /// loop keeps draining, so the inner call must not recurse.</summary>
        private bool _promotingPermissions;
        /// <summary>
        /// Resolved model committed by the most recent SUCCESSFUL SetModel
        /// call, or null when no live switch has landed since the last
        /// Start() (see docs/design-notes/2026-07-31-live-model-tracking.md).
        /// Read through CurrentModel, never directly.
        /// </summary>
        private string _liveModel;

        public AgentClient(ICliTransport transport, Action<string> logger = null)
        {
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }
            _transport = transport;
            _logger = logger;
            _transport.Exited += OnTransportExited;
        }

        // -- Observable state -------------------------------------------------

        public AgentClientState State { get; private set; }

        /// <summary>Session id from system/init (used for --resume).</summary>
        public string SessionId { get; private set; }

        /// <summary>The system/init message, once received.</summary>
        public SystemInitMessage InitMessage { get; private set; }

        /// <summary>The initialize control_response (models/commands/account/pid).</summary>
        public ControlResponseMessage InitializeResponse { get; private set; }

        /// <summary>
        /// The model actually in effect for the NEXT turn: the resolved
        /// model committed by the most recent successful SetModel call,
        /// falling back to InitMessage.Model when no live switch has landed
        /// yet this connection. HeaderView/StatusBarView must read this --
        /// not InitMessage.Model -- for "the current model" (see
        /// docs/design-notes/2026-07-31-live-model-tracking.md).
        /// </summary>
        public string CurrentModel
        {
            get
            {
                if (!string.IsNullOrEmpty(_liveModel))
                {
                    return _liveModel;
                }
                return InitMessage != null ? InitMessage.Model : null;
            }
        }

        /// <summary>Id of the current (or last) turn.</summary>
        public int CurrentTurnId
        {
            get { return _turns.CurrentTurnId; }
        }

        /// <summary>True while a turn is running (between send and result).</summary>
        public bool TurnActive
        {
            get { return _turns.TurnActive; }
        }

        /// <summary>request_id of the can_use_tool prompt awaiting an answer, or null.</summary>
        public string PendingPermissionRequestId
        {
            get { return _pendingPermissionRequestId; }
        }

        /// <summary>
        /// Design note 2026-09-10 section 1: how many can_use_tool
        /// requests wait in the CORE-6 FIFO BEHIND the active one (the
        /// active request is not counted). Main thread only, like every
        /// other accessor here. Zero whenever nothing is queued, including
        /// while no request is active at all.
        /// </summary>
        public int QueuedPermissionCount
        {
            get { return _permissionQueue.Count; }
        }

        /// <summary>
        /// Silence backstop timeout in seconds (default 600). Exposed as a
        /// test seam so the backstop paths can be exercised without waiting
        /// ten minutes of wall-clock time.
        /// </summary>
        public double SilenceTimeoutSeconds
        {
            get { return _turns.SilenceTimeoutSeconds; }
            set { _turns.SilenceTimeoutSeconds = value; }
        }

        /// <summary>The transport (PID/start-time access for zombie bookkeeping).</summary>
        public ICliTransport Transport
        {
            get { return _transport; }
        }

        // -- Events (all raised on the pump/caller thread) --------------------

        public event Action<AgentClientState, AgentClientState> StateChanged;
        public event Action<string> TextDelta;
        public event Action<string> ThinkingDelta;
        public event Action<AssistantMessage> AssistantMessageCompleted;
        /// <summary>Args: the tool_use block, and its parent_tool_use_id (null at the
        /// top level; non-null when this tool_use was made BY a running subagent).</summary>
        public event Action<ContentBlock, string> ToolUseStarted;
        /// <summary>Args: the tool_result block, and its parent_tool_use_id (see ToolUseStarted).</summary>
        public event Action<ContentBlock, string> ToolResultReceived;
        /// <summary>system/task_started|task_progress|task_updated|task_notification (Phase 4).</summary>
        public event Action<SystemTaskEventMessage> TaskEventReceived;
        /// <summary>system/thinking_tokens -- running thinking-token estimate
        /// while a thinking block streams (design note 2026-08-01-thinking-
        /// content-loss.md section 5).</summary>
        public event Action<SystemThinkingTokensMessage> ThinkingTokensReceived;
        /// <summary>system/compact_boundary -- the CLI compacted the
        /// conversation (manual "/compact" or automatic when the window
        /// filled up). Raised mid-turn; the turn's own result still
        /// follows. See docs/design-notes/2026-09-07-slash-commands-and-
        /// compaction.md section 2.</summary>
        public event Action<SystemCompactBoundaryMessage> CompactBoundaryReceived;
        /// <summary>system/status -- the CLI's coarse activity signal.
        /// "compacting" is raised for the whole summarization call that
        /// precedes a compact_boundary (design note
        /// docs/design-notes/2026-09-10-compacting-indicator.md).</summary>
        public event Action<SystemStatusMessage> StatusReceived;
        public event Action<ControlRequestMessage> PermissionRequested;
        public event Action<ResultMessage> TurnCompleted;
        /// <summary>Silence backstop fired: the turn was force-closed without a result.</summary>
        public event Action TurnStalled;
        public event Action<string> SessionIdChanged;
        /// <summary>
        /// Fired every time HandleSystemInit populates <see cref="InitMessage"/>,
        /// UNCONDITIONALLY -- independent of any State transition and of
        /// whether SessionId actually changed. Needed because the real
        /// captured CLI wire order delivers the "initialize"
        /// control_response BEFORE system/init (docs/research/
        /// 02b-authenticated-captures.md), so Starting -&gt; Ready can already
        /// happen from the control_response alone (see
        /// AgentClientStateTests
        /// .InitializeControlResponse_AloneAlsoTransitionsToReady) before
        /// InitMessage is ever set. When that happens, HandleSystemInit's
        /// own "if (State == Starting) SetState(Ready)" is a same-state
        /// no-op that raises no StateChanged, and SessionIdChanged only
        /// fires when the new session id differs from the client's current
        /// one (never true resuming the SAME session on an instance that
        /// already knows it) -- so neither existing event reliably signals
        /// "InitMessage just became available". AgentHub subscribes to
        /// retain the CLI version across that gap (docs/design-notes/
        /// 2026-08-01-init-message-retention.md).
        /// </summary>
        public event Action<SystemInitMessage> InitMessageReceived;
        /// <summary>Process died unexpectedly; argument is a short reason string.</summary>
        public event Action<string> ProcessDied;
        /// <summary>Every raw stdout line, before parsing (log view).</summary>
        public event Action<string> RawLineForLog;
        /// <summary>
        /// Every raw stderr line from the CLI process, in addition to the
        /// existing Log("[cli stderr] ...") call -- this is the feed the
        /// Settings Diagnostics tail consumes (AgentHub keeps a small ring
        /// buffer across client restarts; AgentClient itself buffers
        /// nothing).
        /// </summary>
        public event Action<string> StderrLine;
        /// <summary>
        /// Fired whenever an outbound control_request this client tracked
        /// (set_model, set_permission_mode, interrupt; NOT the
        /// permission-prompt control_response -- see PermissionRequested)
        /// resolves against a matching control_response. Args: kind (the
        /// subtype passed to PendingRequestMap.Track, e.g. "set_model"),
        /// success, error (null when success). Unsolicited/unmatched
        /// control_responses (late permission-decision echoes, per 02b
        /// section 3) never reach this event -- only requests this client
        /// itself is tracking do. Additive surface for LiveCli tests that
        /// need to observe set_model/set_permission_mode round trips
        /// without depending on internal request-id generation.
        /// </summary>
        public event Action<string, bool, string> ControlRequestResolved;

        // -- Lifecycle ---------------------------------------------------------

        /// <summary>
        /// Spawns the CLI with the exact D2 argument list and sends the
        /// initialize handshake. Valid from NotStarted or Errored.
        /// </summary>
        public void Start(AgentClientOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            if (string.IsNullOrEmpty(options.CliPath))
            {
                throw new ArgumentException("options.CliPath must be set.", "options");
            }
            if (State != AgentClientState.NotStarted && State != AgentClientState.Errored)
            {
                throw new InvalidOperationException(
                    "AgentClient.Start is only valid from NotStarted/Errored (was " + State + ").");
            }

            _transportExited = false;
            _deathHandled = false;
            _stopRequested = false;
            _pendingPermissionRequestId = null;
            _pendingPermissionRequest = null;
            _permissionQueue.Clear();
            _liveModel = null;
            _turns.Reset();
            _requests.Clear();

            SetState(AgentClientState.Starting);
            string arguments = BuildArguments(options);
            // CORE-7: a bloated command line (huge custom-instructions
            // sidecar via --append-system-prompt, oversized inline MCP
            // config) blows Windows' 32767-char CreateProcess cap and fails
            // opaquely. Refuse BEFORE spawning, through the same
            // ProcessDied surface the hub already renders.
            if (CommandLineLimitApplies() && CommandLineWouldExceedLimit(options.CliPath, arguments))
            {
                HandleProcessDeath("command line too long: "
                    + (options.CliPath.Length + 1 + arguments.Length) + " chars (limit ~"
                    + WindowsCommandLineSafeLimit + "). Shorten the custom instructions"
                    + " file or the MCP config.");
                return;
            }
            _transport.Start(options.CliPath, arguments, options.WorkingDirectory,
                options.SubagentModel, options.ClaudeAuth);

            string requestId = _requests.NextRequestId("req");
            _requests.Track(requestId, "initialize", NowSeconds(),
                options.InitializeTimeoutSeconds > 0
                    ? options.InitializeTimeoutSeconds
                    : PendingRequestMap.DefaultTimeoutSeconds,
                OnInitializeResolved);
            if (!_transport.WriteLine(OutboundMessages.Initialize(requestId)))
            {
                HandleProcessDeath("initialize write failed");
            }
        }

        /// <summary>
        /// Drains up to maxLines stdout lines (or maxMillis) and dispatches
        /// them; then handles process death, request timeouts and the
        /// silence backstop. Returns the number of lines processed so the
        /// caller can decide whether to repaint. Never throws on bad input
        /// lines.
        /// </summary>
        public int Pump(int maxLines = 20, double maxMillis = 2.0)
        {
            _lineBuffer.Clear();
            int count = _transport.Output.TryDequeueBatch(_lineBuffer, maxLines, maxMillis);
            for (int i = 0; i < count; i++)
            {
                // Per-line isolation: parsing never throws, but the events
                // raised by DispatchLine run subscriber (UI) code. One
                // faulty handler must not discard the rest of the batch --
                // dropping a buffered result line would wedge the turn until
                // the 10-minute silence backstop.
                try
                {
                    DispatchLine(_lineBuffer[i]);
                }
                catch (Exception ex)
                {
                    Log("Event subscriber threw while dispatching a line: " + ex);
                }
            }
            _lineBuffer.Clear();

            // Drain stderr into the logger so the channel stays bounded
            // (a dedicated log view consumes RawLineForLog/stderr in Phase 3).
            string errorLine;
            int errorBudget = 50;
            while (errorBudget-- > 0 && _transport.ErrorOutput.TryDequeue(out errorLine))
            {
                Log("[cli stderr] " + errorLine);
                Raise(StderrLine, errorLine);
            }

            // Death check AFTER draining: lines already produced before the
            // exit still get delivered (the final result often races exit).
            // Only flip to Errored once the buffer is empty.
            if (_transportExited && !_deathHandled && _transport.Output.Count == 0)
            {
                HandleProcessDeath("process exited");
            }

            double now = NowSeconds();
            List<PendingRequestMap.PendingRequest> expired = _requests.CollectTimedOut(now);
            for (int i = 0; i < expired.Count; i++)
            {
                Log("control_request timed out: " + expired[i].Kind
                    + " (" + expired[i].RequestId + ")");
            }

            // Silence backstop suspension: while a can_use_tool prompt is
            // pending the CLI is blocked on the USER's answer and emits
            // nothing (02b section 1) -- that is think-time, not silence,
            // and the UX spec (R05 3.7) says the permission wait has no
            // timeout. Refresh the activity clock every pump so the
            // backstop only starts counting again after the prompt is
            // answered (or cleared by result/death/stop).
            if (_pendingPermissionRequestId != null)
            {
                _turns.MarkActivity(now);
            }

            if (_turns.IsSilenceExceeded(now))
            {
                Log("Silence backstop: no result after "
                    + (int)_turns.SilenceTimeoutSeconds + "s; forcing turn closed.");
                _turns.EndTurn();
                if (State == AgentClientState.Streaming
                    || State == AgentClientState.ToolRunning
                    || State == AgentClientState.WaitingPermission)
                {
                    SetState(AgentClientState.Ready);
                }
                Raise(TurnStalled);
            }
            return count;
        }

        // -- Commands ----------------------------------------------------------

        /// <summary>
        /// Sends a user text message. While idle (Ready) this opens a new
        /// turn as before. While a turn is already running (Streaming/
        /// ToolRunning) and has not been interrupted, this is a mid-turn
        /// STEERING send: TurnTracker.BeginTurn folds it into the turn
        /// already open instead of counting a second one, because the CLI
        /// itself folds it and still emits exactly one result for the whole
        /// thing (measured; see TurnTracker's class remarks and
        /// docs/research/02-claude-cli-protocol.md section 4.1). Returns
        /// true only when the line was actually written to the CLI; false
        /// when the client is not in a sendable state (Starting/
        /// WaitingPermission/Errored/NotStarted) or the write failed.
        /// Callers (AgentHub / CompileGate) must queue or retain the text on
        /// false -- never display it as delivered.
        /// </summary>
        public bool SendUserText(string text)
        {
            return SendUserContent(text, null);
        }

        /// <summary>
        /// As <see cref="SendUserText"/>, with image content blocks after
        /// the text (design note 2026-09-07 decision I1). The text may be
        /// empty when at least one image is given.
        /// </summary>
        public bool SendUserContent(string text, IList<OutboundMessages.ImageBlock> images)
        {
            bool hasImages = images != null && images.Count > 0;
            if (string.IsNullOrEmpty(text) && !hasImages)
            {
                return false;
            }
            if (State != AgentClientState.Ready
                && State != AgentClientState.Streaming
                && State != AgentClientState.ToolRunning)
            {
                Log("SendUserText refused in state " + State + " (caller must queue).");
                return false;
            }
            _turns.BeginTurn(NowSeconds());
            if (!_transport.WriteLine(OutboundMessages.UserContent(text ?? string.Empty, images)))
            {
                HandleProcessDeath("user message write failed");
                return false;
            }
            if (State == AgentClientState.Ready)
            {
                SetState(AgentClientState.Streaming);
            }
            return true;
        }

        /// <summary>Interrupts the running turn (stop button / Esc).</summary>
        public void Interrupt()
        {
            if (!_turns.TurnActive)
            {
                return;
            }
            string requestId = _requests.NextRequestId("int");
            _requests.Track(requestId, "interrupt", NowSeconds());
            _turns.MarkInterrupted();
            if (!_transport.WriteLine(OutboundMessages.Interrupt(requestId)))
            {
                HandleProcessDeath("interrupt write failed");
            }
        }

        /// <summary>Answers the pending can_use_tool prompt.</summary>
        public void RespondToPermission(string requestId, PermissionDecision decision)
        {
            if (string.IsNullOrEmpty(requestId) || decision == null)
            {
                return;
            }
            // Every captured/documented allow payload carries updatedInput
            // (02b section 2, R02 section 5.3). When the caller did not edit
            // the input, echo the original input from the pending request so
            // the wire shape always matches the observed one.
            JsonNode updatedInput = decision.UpdatedInput;
            if (decision.Allow && updatedInput == null
                && _pendingPermissionRequest != null
                && _pendingPermissionRequest.RequestId == requestId
                && _pendingPermissionRequest.CanUseTool != null)
            {
                updatedInput = _pendingPermissionRequest.CanUseTool.Input;
            }
            string line = decision.Allow
                ? OutboundMessages.AllowToolUse(requestId, updatedInput,
                    decision.UpdatedPermissions)
                : OutboundMessages.DenyToolUse(requestId, decision.DenyMessage,
                    decision.DenyInterrupt);
            if (!_transport.WriteLine(line))
            {
                HandleProcessDeath("permission response write failed");
                return;
            }
            if (requestId == _pendingPermissionRequestId)
            {
                _pendingPermissionRequestId = null;
                _pendingPermissionRequest = null;
                if (State == AgentClientState.WaitingPermission)
                {
                    SetState(decision.Allow
                        ? AgentClientState.ToolRunning
                        : AgentClientState.Streaming);
                }
                // CORE-6: surface the next queued concurrent request (this
                // re-enters WaitingPermission when one exists).
                PromoteNextPermission();
            }
        }

        /// <summary>Switches permission mode on the running session.</summary>
        public void SetPermissionMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
            {
                return;
            }
            string requestId = _requests.NextRequestId("pm");
            _requests.Track(requestId, "set_permission_mode", NowSeconds());
            if (!_transport.WriteLine(OutboundMessages.SetPermissionMode(requestId, mode)))
            {
                HandleProcessDeath("set_permission_mode write failed");
            }
        }

        /// <summary>
        /// Switches the model on the running session. The resolved name
        /// committed to CurrentModel on success is looked up from the
        /// SAME InitializeResponse.Response["models"] value->resolvedModel
        /// mapping HeaderView.ParseModels uses (no unverified assumption
        /// about the set_model control_response payload shape -- see
        /// docs/design-notes/2026-07-31-live-model-tracking.md); an
        /// unmatched value (manual/unlisted model id) falls back to itself.
        /// </summary>
        public void SetModel(string model)
        {
            if (string.IsNullOrEmpty(model))
            {
                return;
            }
            string requestId = _requests.NextRequestId("sm");
            string resolvedGuess = ResolveModelValue(model);
            _requests.Track(requestId, "set_model", NowSeconds(),
                PendingRequestMap.DefaultTimeoutSeconds,
                delegate(ControlResponseMessage response)
                {
                    if (response != null && response.Success)
                    {
                        _liveModel = resolvedGuess;
                    }
                });
            if (!_transport.WriteLine(OutboundMessages.SetModel(requestId, model)))
            {
                HandleProcessDeath("set_model write failed");
            }
        }

        /// <summary>
        /// Resolves a --model/set_model value (e.g. "haiku") to its
        /// resolvedModel name (e.g. "claude-haiku-4-5-20251001") via the
        /// cached initialize response's models[] array; returns the input
        /// unchanged when InitializeResponse is missing or has no matching
        /// entry.
        /// </summary>
        private string ResolveModelValue(string modelValue)
        {
            if (InitializeResponse != null)
            {
                JsonNode models = InitializeResponse.Response["models"];
                if (models != null && models.IsArray)
                {
                    foreach (JsonNode item in models.Items)
                    {
                        if (!item.IsObject)
                        {
                            continue;
                        }
                        if (string.Equals(item["value"].AsString(string.Empty), modelValue,
                            StringComparison.Ordinal))
                        {
                            string resolved = item["resolvedModel"].AsString(string.Empty);
                            return string.IsNullOrEmpty(resolved) ? modelValue : resolved;
                        }
                    }
                }
            }
            return modelValue;
        }

        /// <summary>
        /// Sends the (undocumented but measured-working, docs/research/
        /// 08-mcp-transport.md section 3.4) `mcp_reconnect` control_request
        /// for the named MCP server. This is the safety net for the
        /// "connect failed at process start, never retried" asymmetry R08
        /// measured: a server that was down when this CLI process spawned
        /// (or resumed) never reconnects on its own, no matter how long the
        /// session runs, unless explicitly told to. No-op when
        /// <paramref name="serverName"/> is empty. Resolution fires through
        /// <see cref="ControlRequestResolved"/> with kind "mcp_reconnect"
        /// like every other tracked control_request -- there is no
        /// dedicated event, since the caller (AgentHub) only needs to know
        /// success/failure for logging, not a payload.
        /// </summary>
        public void SendMcpReconnect(string serverName)
        {
            if (string.IsNullOrEmpty(serverName))
            {
                return;
            }
            string requestId = _requests.NextRequestId("mcpr");
            _requests.Track(requestId, "mcp_reconnect", NowSeconds());
            if (!_transport.WriteLine(OutboundMessages.McpReconnect(requestId, serverName)))
            {
                HandleProcessDeath("mcp_reconnect write failed");
            }
        }

        /// <summary>Default grace (millis) for interactive Stop().</summary>
        public const int DefaultStopGraceMillis = 2000;

        /// <summary>
        /// Graceful stop: interrupt + stdin close + short wait, then tree
        /// kill (delegated to the transport). Ends in NotStarted.
        /// </summary>
        public void Stop()
        {
            Stop(DefaultStopGraceMillis);
        }

        /// <summary>
        /// Stop with a caller-chosen exit grace. The beforeAssemblyReload
        /// path passes a small value (the whole reload budget is ~3 s and
        /// the session is resumed via --resume anyway); interactive paths
        /// use DefaultStopGraceMillis.
        /// </summary>
        public void Stop(int graceMillis)
        {
            _stopRequested = true;
            if (State != AgentClientState.NotStarted)
            {
                string interruptLine = null;
                if (_turns.TurnActive)
                {
                    interruptLine = OutboundMessages.Interrupt(_requests.NextRequestId("int"));
                }
                _transport.Stop(interruptLine, graceMillis);
            }
            _turns.Reset();
            _requests.Clear();
            _pendingPermissionRequestId = null;
            _pendingPermissionRequest = null;
            _permissionQueue.Clear();
            SetState(AgentClientState.NotStarted);
        }

        public void Dispose()
        {
            Stop();
            _transport.Exited -= OnTransportExited;
            _transport.Dispose();
        }

        // -- Argument building (D2) --------------------------------------------

        /// <summary>
        /// CORE-7: CreateProcess on Windows caps the executable + argument
        /// string at 32767 chars; beyond it the spawn fails opaquely or
        /// truncates. 32000 leaves margin for quoting and the OS's own
        /// additions. Unix ARG_MAX is megabytes, so the limit is only
        /// enforced there (see Start).
        /// </summary>
        public const int WindowsCommandLineSafeLimit = 32000;

        /// <summary>
        /// Test seam for the platform gate on the CORE-7 guard: null (the
        /// default) means "apply on Windows only"; tests force true so the
        /// refuse-to-spawn path is exercisable on any CI OS.
        /// </summary>
        internal static bool? ForceCommandLineLimitCheckForTests;

        /// <summary>CORE-7's pure size predicate; platform-independent by design (the caller owns the platform gate).</summary>
        public static bool CommandLineWouldExceedLimit(string cliPath, string arguments)
        {
            int length = (cliPath != null ? cliPath.Length : 0) + 1
                + (arguments != null ? arguments.Length : 0);
            return length > WindowsCommandLineSafeLimit;
        }

        private static bool CommandLineLimitApplies()
        {
            if (ForceCommandLineLimitCheckForTests.HasValue)
            {
                return ForceCommandLineLimitCheckForTests.Value;
            }
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        /// <summary>Builds the exact CLI argument list per ARCHITECTURE.md D2.</summary>
        public static string BuildArguments(AgentClientOptions options)
        {
            var args = new StringBuilder();
            args.Append("-p --input-format stream-json --output-format stream-json --verbose");
            args.Append(" --include-partial-messages --replay-user-messages");
            args.Append(" --permission-prompt-tool stdio");
            if (!string.IsNullOrEmpty(options.ResumeSessionId))
            {
                args.Append(" --resume ").Append(QuoteArg(options.ResumeSessionId));
            }
            if (!string.IsNullOrEmpty(options.Model))
            {
                args.Append(" --model ").Append(QuoteArg(options.Model));
            }
            if (!string.IsNullOrEmpty(options.PermissionMode))
            {
                args.Append(" --permission-mode ").Append(QuoteArg(options.PermissionMode));
            }
            if (!string.IsNullOrEmpty(options.AppendSystemPrompt))
            {
                args.Append(" --append-system-prompt ").Append(QuoteArg(options.AppendSystemPrompt));
            }
            if (options.ThinkingDisplaySummarized)
            {
                args.Append(" --thinking-display summarized");
            }
            // NOTE: --dangerously-skip-permissions is a documented,
            // unambiguous single flag. The exact multi-value wire syntax
            // for --allowedTools/--disallowedTools has NOT been captured
            // against a live v2.1.218 process in this repo (unlike every
            // other flag here, which R02 verified empirically) -- the
            // "one value per argument after the flag" form below matches
            // publicly documented Claude Code CLI usage but should be
            // reverified against a real spawn before relying on it for
            // anything more permissive than an empty (omitted) list.
            if (options.DangerouslySkipPermissions)
            {
                args.Append(" --dangerously-skip-permissions");
            }
            AppendToolList(args, "--allowedTools", options.AllowedTools);
            AppendToolList(args, "--disallowedTools", options.DisallowedTools);
            if (!string.IsNullOrEmpty(options.McpConfigJson))
            {
                args.Append(" --mcp-config ").Append(QuoteArg(options.McpConfigJson));
                args.Append(" --strict-mcp-config");
            }
            if (!string.IsNullOrEmpty(options.SettingsFilePath))
            {
                args.Append(" --settings ").Append(QuoteArg(options.SettingsFilePath));
            }
            // One `--plugin-dir <path>` per entry (the CLI's documented form:
            // "Each flag takes one path. Repeat the flag for more paths").
            // Empty/omitted leaves the argument string byte-identical to
            // before this option existed.
            if (options.PluginDirs != null)
            {
                for (int i = 0; i < options.PluginDirs.Count; i++)
                {
                    string dir = options.PluginDirs[i];
                    if (string.IsNullOrEmpty(dir))
                    {
                        continue;
                    }
                    args.Append(" --plugin-dir ").Append(QuoteArg(dir));
                }
            }
            return args.ToString();
        }

        private static void AppendToolList(StringBuilder args, string flag, List<string> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return;
            }
            args.Append(' ').Append(flag);
            for (int i = 0; i < tools.Count; i++)
            {
                string tool = tools[i];
                if (string.IsNullOrEmpty(tool))
                {
                    continue;
                }
                args.Append(' ').Append(QuoteArg(tool));
            }
        }

        private static string QuoteArg(string value)
        {
            if (!NeedsQuoting(value))
            {
                return value;
            }
            // Win32/CRT command-line quoting (CommandLineToArgvW rules):
            // a backslash is only special when it immediately precedes a
            // double quote -- either an embedded one or the closing quote
            // this method appends. A naive ".Replace(\"\\\"\", ...)" (the
            // previous implementation) leaves a backslash run before the
            // closing quote un-doubled, so e.g. a custom-instructions
            // value ending in "\" produces an argument whose closing
            // quote is escaped away instead of terminated, corrupting
            // every argument after it on the spawned command line.
            var sb = new StringBuilder();
            sb.Append('"');
            int backslashRun = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    backslashRun++;
                    continue;
                }
                if (c == '"')
                {
                    // N backslashes immediately before an embedded quote
                    // must become 2N+1 backslashes (escape every
                    // backslash, then escape the quote itself).
                    sb.Append('\\', backslashRun * 2 + 1);
                    sb.Append('"');
                    backslashRun = 0;
                    continue;
                }
                if (backslashRun > 0)
                {
                    // Not followed by a quote -- these backslashes are
                    // literal and need no doubling.
                    sb.Append('\\', backslashRun);
                    backslashRun = 0;
                }
                sb.Append(c);
            }
            if (backslashRun > 0)
            {
                // Trailing run sits directly before the closing quote we
                // are about to append: N backslashes must become 2N so
                // the closing quote is parsed as a real terminator
                // (an odd count would escape it into a literal quote).
                sb.Append('\\', backslashRun * 2);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Whether <paramref name="value"/> must be wrapped in quotes to
        /// survive CommandLineToArgvW re-splitting. Triggers on any character
        /// that is an argument boundary or needs escaping: SPACE and TAB are
        /// both delimiters, a double quote needs escaping, and any other
        /// control character (&lt; 0x20 -- e.g. an embedded newline in a
        /// multiline --append-system-prompt value) is quoted defensively.
        /// TAB in particular is a real delimiter a CJK custom-instruction
        /// line can contain with no ASCII space, which the previous
        /// space/quote/newline-only guard let split into two arguments on the
        /// spawned command line (the second half then reinterpreted as a
        /// stray positional). Empty stays unquoted, matching prior behavior.
        /// </summary>
        internal static bool NeedsQuoting(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == ' ' || c < ' ')
                {
                    return true;
                }
            }
            return false;
        }

        // -- Inbound dispatch ---------------------------------------------------

        private void DispatchLine(string line)
        {
            // Diagnostics fan-out is isolated: this raise runs before
            // ParseLine, so a faulty subscriber that escaped here would hit
            // the pump's per-line catch and skip the line's parse and state
            // transition entirely -- a buffered result line lost that way
            // wedges the turn until the silence backstop.
            try
            {
                Raise(RawLineForLog, line);
            }
            catch (Exception ex)
            {
                Log("RawLineForLog subscriber threw: " + ex);
            }
            StreamJsonMessage message = StreamJsonMessage.ParseLine(line, _logger);
            if (message == null)
            {
                return;
            }
            _turns.MarkActivity(NowSeconds());

            switch (message.Type)
            {
                case InboundType.SystemInit:
                    HandleSystemInit((SystemInitMessage)message);
                    break;
                case InboundType.Assistant:
                    HandleAssistant((AssistantMessage)message);
                    break;
                case InboundType.User:
                    HandleUserEcho((UserEchoMessage)message);
                    break;
                case InboundType.Result:
                    HandleResult((ResultMessage)message);
                    break;
                case InboundType.StreamEvent:
                    HandleStreamEvent((StreamEventMessage)message);
                    break;
                case InboundType.SystemTaskEvent:
                    Raise(TaskEventReceived, (SystemTaskEventMessage)message);
                    break;
                case InboundType.SystemThinkingTokens:
                    Raise(ThinkingTokensReceived, (SystemThinkingTokensMessage)message);
                    break;
                case InboundType.SystemCompactBoundary:
                    Raise(CompactBoundaryReceived, (SystemCompactBoundaryMessage)message);
                    break;
                case InboundType.SystemStatus:
                    Raise(StatusReceived, (SystemStatusMessage)message);
                    break;
                case InboundType.ControlRequest:
                    HandleControlRequest((ControlRequestMessage)message);
                    break;
                case InboundType.ControlResponse:
                    HandleControlResponse((ControlResponseMessage)message);
                    break;
                case InboundType.System:
                case InboundType.Unknown:
                default:
                    // Forward compatibility: ignored by design.
                    break;
            }
        }

        private void HandleSystemInit(SystemInitMessage message)
        {
            InitMessage = message;
            // Unconditional -- see the event's own doc comment for why
            // StateChanged/SessionIdChanged cannot be relied on here.
            Raise(InitMessageReceived, message);
            if (!string.IsNullOrEmpty(message.SessionId) && message.SessionId != SessionId)
            {
                SessionId = message.SessionId;
                Raise(SessionIdChanged, SessionId);
            }
            if (State == AgentClientState.Starting)
            {
                SetState(AgentClientState.Ready);
            }
        }

        private void HandleAssistant(AssistantMessage message)
        {
            Raise(AssistantMessageCompleted, message);
            bool hasToolUse = false;
            for (int i = 0; i < message.Content.Length; i++)
            {
                if (message.Content[i].Type == ContentBlockType.ToolUse)
                {
                    hasToolUse = true;
                    Raise(ToolUseStarted, message.Content[i], message.ParentToolUseId);
                }
            }
            if (hasToolUse && State == AgentClientState.Streaming)
            {
                SetState(AgentClientState.ToolRunning);
            }
        }

        private void HandleUserEcho(UserEchoMessage message)
        {
            if (message.IsReplay)
            {
                _turns.MarkAck();
            }
            bool hadToolResult = false;
            for (int i = 0; i < message.Content.Length; i++)
            {
                if (message.Content[i].Type == ContentBlockType.ToolResult)
                {
                    hadToolResult = true;
                    Raise(ToolResultReceived, message.Content[i], message.ParentToolUseId);
                }
            }
            if (hadToolResult && State == AgentClientState.ToolRunning)
            {
                SetState(AgentClientState.Streaming);
            }
        }

        private void HandleResult(ResultMessage message)
        {
            _turns.EndTurn();
            // A permission prompt never straddles a result boundary.
            _pendingPermissionRequestId = null;
            _pendingPermissionRequest = null;
            _permissionQueue.Clear();
            if (!_turns.TurnActive)
            {
                // Last open turn closed: the conversation is idle. This is
                // also the ordinary outcome for a turn that had one or more
                // mid-turn STEERING sends folded into it (TurnTracker.
                // BeginTurn) -- the CLI folds those and still emits exactly
                // one result for the whole thing (measured; see
                // TurnTracker's class remarks / docs/research/
                // 02-claude-cli-protocol.md section 4.1), so this single
                // EndTurn already brings the counter to zero regardless of
                // how many user messages went into the turn.
                if (State == AgentClientState.Streaming
                    || State == AgentClientState.ToolRunning
                    || State == AgentClientState.WaitingPermission
                    || State == AgentClientState.Starting)
                {
                    SetState(AgentClientState.Ready);
                }
            }
            else
            {
                // A turn is still open: this only happens when a send was
                // written after Interrupt but before the interrupted turn's
                // own result arrived (TurnTracker.BeginTurn counts that as
                // a genuine second turn, since the interrupted one is being
                // torn down and still owes its own result). Ordinary
                // mid-turn steering sends never reach this branch -- they
                // were folded into the turn that just closed above.
                if (State == AgentClientState.ToolRunning
                    || State == AgentClientState.WaitingPermission)
                {
                    SetState(AgentClientState.Streaming);
                }
            }
            // Invariant: state transitions above happen BEFORE this raise, so
            // a throwing TurnCompleted subscriber (caught by the pump's
            // per-line isolation) can never leave the client stuck outside
            // Ready/Streaming after its turn already closed.
            Raise(TurnCompleted, message);
        }

        private void HandleStreamEvent(StreamEventMessage message)
        {
            // Residual events after an interrupt are dropped (turn-id fence).
            if (_turns.InterruptRequested)
            {
                return;
            }
            // Forward-compat guard (Phase 4 design note section 2c): R02c
            // observed subagent content NEVER streams -- it arrives only as
            // completed assistant/user lines carrying parent_tool_use_id.
            // If a future CLI version starts streaming subagent deltas too,
            // dropping them here (rather than raising TextDelta/ThinkingDelta,
            // which carry no parent info) keeps them out of the top-level
            // transcript buffer instead of corrupting it.
            if (!string.IsNullOrEmpty(message.ParentToolUseId))
            {
                return;
            }
            if (message.IsThinkingBlockStart())
            {
                // Ensures the streaming Thinking block exists immediately,
                // ahead of either a thinking_delta or a system/
                // thinking_tokens event (design note section 5 point 2).
                // Reuses the existing ThinkingDelta plumbing with an empty
                // string -- appending "" is a no-op on the accumulated
                // text, so this is purely a block-creation signal, not a
                // second event surface AgentHub has to subscribe to.
                Raise(ThinkingDelta, string.Empty);
                return;
            }
            string text;
            if (message.TryGetTextDelta(out text))
            {
                Raise(TextDelta, text);
                return;
            }
            string thinking;
            if (message.TryGetThinkingDelta(out thinking))
            {
                Raise(ThinkingDelta, thinking);
            }
        }

        private void HandleControlRequest(ControlRequestMessage message)
        {
            if (message.IsCanUseTool)
            {
                // CORE-6: always queue, promote only when no request is
                // active. A second concurrent can_use_tool used to
                // overwrite the pending pair, orphaning the first request
                // forever (the CLI kept waiting for its answer -> turn
                // wedged until the silence backstop).
                _permissionQueue.Enqueue(message);
                PromoteNextPermission();
            }
            else
            {
                // hook_callback / request_user_dialog / mcp_message: v1 logs and ignores.
                Log("Ignored control_request subtype '" + message.Subtype
                    + "' (" + message.RequestId + ").");
            }
        }

        /// <summary>
        /// Makes the queue head the active pending permission and raises
        /// PermissionRequested for it, repeating while a subscriber
        /// resolves synchronously (auto-approve answers from inside the
        /// raise). A flat while + re-entrancy guard, NOT recursion: N
        /// auto-approved queued requests drain in one loop with constant
        /// stack depth. No-op while a request is already active -- the
        /// resolution paths (RespondToPermission, result, death, stop)
        /// decide when the next one surfaces.
        /// </summary>
        private void PromoteNextPermission()
        {
            if (_promotingPermissions)
            {
                return;
            }
            _promotingPermissions = true;
            try
            {
                while (_pendingPermissionRequestId == null && _permissionQueue.Count > 0)
                {
                    ControlRequestMessage next = _permissionQueue.Dequeue();
                    _pendingPermissionRequestId = next.RequestId;
                    _pendingPermissionRequest = next;
                    if (State == AgentClientState.Streaming
                        || State == AgentClientState.ToolRunning
                        || State == AgentClientState.Ready)
                    {
                        SetState(AgentClientState.WaitingPermission);
                    }
                    Raise(PermissionRequested, next);
                }
            }
            finally
            {
                _promotingPermissions = false;
            }
        }

        private void HandleControlResponse(ControlResponseMessage message)
        {
            PendingRequestMap.PendingRequest request = _requests.TryResolve(message);
            if (request == null)
            {
                // 02b section 3: every control_response the panel writes to
                // stdin (permission allow/deny) is echoed back on stdout
                // with the CLI-generated UUID request_id. Unmatched inbound
                // control_responses are therefore normal traffic and MUST be
                // ignored silently (no console log pollution). The raw line
                // is still visible via RawLineForLog for diagnostics.
                return;
            }
            if (!message.Success)
            {
                Log("control_request '" + request.Kind + "' failed: "
                    + (message.Error ?? "unknown error"));
            }
            Raise(ControlRequestResolved, request.Kind, message.Success, message.Error);
        }

        private void OnInitializeResolved(ControlResponseMessage response)
        {
            if (response == null)
            {
                Log("initialize handshake timed out.");
                if (State == AgentClientState.Starting && !_stopRequested)
                {
                    // A CLI that never answers initialize (hung binary,
                    // unexpected version, stuck auth) must not wedge the
                    // panel in Starting forever: force the death path so
                    // AgentHub's bounded auto-reconnect and the error UI
                    // take over. Tear the transport down first -- the
                    // process is alive but useless.
                    _transport.Stop(null, 0);
                    HandleProcessDeath("initialize handshake timed out");
                }
                return;
            }
            InitializeResponse = response;
            if (State == AgentClientState.Starting)
            {
                SetState(AgentClientState.Ready);
            }
        }

        // -- Death handling -----------------------------------------------------

        private void OnTransportExited()
        {
            // Arbitrary thread: only set the flag; Pump() acts on it.
            _transportExited = true;
        }

        private void HandleProcessDeath(string reason)
        {
            if (_deathHandled)
            {
                return;
            }
            _deathHandled = true;
            if (_stopRequested || State == AgentClientState.NotStarted)
            {
                // Deliberate teardown: not an error.
                return;
            }
            _turns.Reset();
            _requests.Clear();
            _pendingPermissionRequestId = null;
            _pendingPermissionRequest = null;
            _permissionQueue.Clear();
            SetState(AgentClientState.Errored);
            Raise(ProcessDied, reason);
        }

        // -- Helpers -------------------------------------------------------------

        private void SetState(AgentClientState next)
        {
            AgentClientState previous = State;
            if (previous == next)
            {
                return;
            }
            if (!AgentClientStateTransitions.IsValid(previous, next))
            {
                Log("Unexpected state transition " + previous + " -> " + next
                    + " (applying anyway; the stream is authoritative).");
            }
            State = next;
            Action<AgentClientState, AgentClientState> handler = StateChanged;
            if (handler != null)
            {
                handler(previous, next);
            }
        }

        private double NowSeconds()
        {
            return _clock.Elapsed.TotalSeconds;
        }

        private void Raise(Action handler)
        {
            if (handler != null)
            {
                handler();
            }
        }

        private void Raise<T>(Action<T> handler, T argument)
        {
            if (handler != null)
            {
                handler(argument);
            }
        }

        private void Raise<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2)
        {
            if (handler != null)
            {
                handler(arg1, arg2);
            }
        }

        private void Raise<T1, T2, T3>(Action<T1, T2, T3> handler, T1 arg1, T2 arg2, T3 arg3)
        {
            if (handler != null)
            {
                handler(arg1, arg2, arg3);
            }
        }

        private void Log(string message)
        {
            if (_logger != null)
            {
                _logger(message);
            }
        }
    }
}
