using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// Bidirectional protocol translator between the panel's native wire
    /// dialect (Claude Code stream-json, the only thing AgentClient/AgentHub
    /// understand) and ACP (Agent Client Protocol: JSON-RPC 2.0 over stdio,
    /// spoken by Gemini CLI, codex-acp, Qwen Code, ...). Design note:
    /// docs/design-notes/2026-09-10-acp-backends.md section 3.
    ///
    /// Pure state machine, Unity-free, no I/O of its own: the two sinks
    /// given to the constructor receive the lines to write. AcpBridgeTransport
    /// owns the process and calls this under one lock from both the
    /// reader thread (<see cref="OnAgentLine"/>) and the main thread
    /// (<see cref="OnPanelLine"/>), so no method here is re-entrant across
    /// threads and none needs to be.
    ///
    /// Translation table (panel -> agent):
    /// - control_request/initialize  -> initialize, then session/load or
    ///   session/new (+authenticate on auth_required), then the Claude
    ///   initialize control_response and system/init are emitted.
    /// - user message                -> session/prompt (text + image blocks).
    ///   A prompt sent while one is running is QUEUED and sent after it
    ///   returns; the single Claude `result` is emitted only once the queue
    ///   drains, matching the CLI's own fold-steering-sends-into-one-turn
    ///   semantics AgentClient's TurnTracker relies on.
    /// - control_response (can_use_tool answer) -> session/request_permission
    ///   response (allow_once / allow_always / reject_once).
    /// - control_request/interrupt   -> session/cancel notification.
    /// - control_request/set_model   -> session/set_model.
    /// - control_request/set_permission_mode -> session/set_mode (when a
    ///   mode id matches), else an error control_response.
    ///
    /// Translation table (agent -> panel):
    /// - session/update agent_message_chunk  -> stream_event text_delta
    ///   (final text folded into an assistant message at the next tool call
    ///   or turn end).
    /// - session/update agent_thought_chunk  -> stream_event thinking start
    ///   + thinking_delta (folded into a thinking block likewise).
    /// - session/update tool_call            -> assistant message with one
    ///   tool_use block; tool_call_update reaching completed/failed -> user
    ///   message with one tool_result block.
    /// - session/request_permission          -> control_request can_use_tool.
    /// - session/prompt response             -> assistant (flush) + result.
    /// - session/update available_commands_update -> system/init re-emit
    ///   with slash_commands (the composer's "/" popup).
    /// - fs/* and terminal/* requests        -> JSON-RPC method-not-found
    ///   (the client never advertises those capabilities).
    /// </summary>
    public sealed class AcpProtocolBridge
    {
        /// <summary>ACP MAJOR protocol version this bridge speaks.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Prefix of the synthetic can_use_tool request ids the bridge hands the panel.</summary>
        public const string PermissionRequestIdPrefix = "acp-perm-";

        /// <summary>Delimiter text wrapped around the system prompt injected into the first prompt of a new session.</summary>
        public const string SystemPromptHeader =
            "[Standing instructions from Agent Panel for Unity -- follow them for this whole session]";
        public const string SystemPromptFooter = "[End of standing instructions]";

        private readonly AcpLaunchSpec _spec;
        private readonly string _workingDirectory;
        private readonly Action<string> _toAgent;
        private readonly Action<string> _toPanel;
        private readonly Action<string> _log;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _nextRpcId = 1;
        private readonly Dictionary<long, Action<AcpInbound>> _pendingRpc =
            new Dictionary<long, Action<AcpInbound>>();

        // -- Handshake state ----------------------------------------------------
        private string _initRequestId;
        private bool _initReplied;
        private bool _initEmitted;
        private bool _authAttempted;
        private readonly List<string> _authCandidates = new List<string>();
        private int _authCandidateIndex;
        private readonly List<string> _authFailures = new List<string>();
        private bool _loadingSession;
        private bool _sessionLoaded;
        private string _sessionId;
        private string _agentName = string.Empty;
        private string _agentVersion = string.Empty;
        private bool _capLoadSession;
        private bool _capPromptImage;
        private bool _capMcpHttp;
        private readonly List<string> _authMethodIds = new List<string>();
        private readonly List<string> _authMethodNames = new List<string>();
        private readonly List<KeyValuePair<string, string>> _modes =
            new List<KeyValuePair<string, string>>();
        private string _currentModeId;
        private readonly List<ModelEntry> _models = new List<ModelEntry>();
        private string _currentModelId;
        private readonly List<CommandEntry> _commands = new List<CommandEntry>();

        // -- Turn state -----------------------------------------------------------
        private bool _promptInFlight;
        private long _promptRpcId = -1;
        private readonly Queue<JsonNode> _queuedPrompts = new Queue<JsonNode>();
        private long _turnStartMillis;
        private bool _firstPromptSent;
        private bool _cancelRequested;
        private int _messageCounter;
        private int _blockIndex;
        private readonly StringBuilder _textBuffer = new StringBuilder();
        private readonly StringBuilder _thinkingBuffer = new StringBuilder();
        private bool _thinkingStarted;
        private readonly StringBuilder _turnText = new StringBuilder();
        private readonly Dictionary<string, ToolCallState> _toolCalls =
            new Dictionary<string, ToolCallState>(StringComparer.Ordinal);
        private readonly List<string> _toolCallOrder = new List<string>();
        private readonly Dictionary<string, PermissionState> _pendingPermissions =
            new Dictionary<string, PermissionState>(StringComparer.Ordinal);
        private int _permissionCounter;

        // -- Usage state (design note 2026-09-10-acp-usage-display.md) ------------
        /// <summary>Tokens the agent's last usage_update said are in context, or -1 when none arrived yet.</summary>
        private long _contextUsedTokens = -1;
        /// <summary>Context window size from the last usage_update (0 = unknown; the meter stays hidden).</summary>
        private long _contextWindowTokens;
        /// <summary>Per-model context window from the model catalog's `_meta.totalContextTokens` (Grok Build).</summary>
        private readonly Dictionary<string, long> _catalogContextWindows =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>Cumulative session cost from usage_update, only when the agent bills in USD.</summary>
        private double _cumulativeCostUsd;
        /// <summary>Per-turn totals folded from every PromptResponse of the current panel turn.</summary>
        private long _turnInputTokens;
        private long _turnOutputTokens;
        private long _turnCacheReadTokens;
        private long _turnCacheWriteTokens;
        private bool _turnUsageReported;
        /// <summary>Gemini's _meta.quota.model_usage rows: model -> [input, output].</summary>
        private readonly Dictionary<string, long[]> _turnModelUsage =
            new Dictionary<string, long[]>(StringComparer.Ordinal);

        private sealed class ModelEntry
        {
            public string Id;
            public string Name;
            public string Description;
        }

        private sealed class CommandEntry
        {
            public string Name;
            public string Description;
            public string Hint;
        }

        private sealed class ToolCallState
        {
            public string Id;
            public string Name;
            public string Title;
            public string Kind;
            public JsonNode Input;
            public bool Announced;
            public bool Completed;
            public readonly StringBuilder Output = new StringBuilder();
            /// <summary>Image content the tool returned, already in the
            /// API shape ({"type":"image","source":{"type":"base64",...}})
            /// the panel's stream reader expects.</summary>
            public readonly List<JsonNode> Images = new List<JsonNode>();
        }

        private sealed class PermissionState
        {
            public JsonNode RpcId;
            public string ToolCallId;
            public string AllowOnceId;
            public string AllowAlwaysId;
            public string RejectOnceId;
            public string RejectAlwaysId;
        }

        public AcpProtocolBridge(AcpLaunchSpec spec, string workingDirectory,
            Action<string> toAgent, Action<string> toPanel, Action<string> log = null)
        {
            if (spec == null)
            {
                throw new ArgumentNullException("spec");
            }
            if (toAgent == null)
            {
                throw new ArgumentNullException("toAgent");
            }
            if (toPanel == null)
            {
                throw new ArgumentNullException("toPanel");
            }
            _spec = spec;
            _workingDirectory = workingDirectory ?? string.Empty;
            _toAgent = toAgent;
            _toPanel = toPanel;
            _log = log;
        }

        // -- Observable (diagnostics/tests) ---------------------------------------

        public string SessionId
        {
            get { return _sessionId; }
        }

        public bool PromptInFlight
        {
            get { return _promptInFlight; }
        }

        public int QueuedPromptCount
        {
            get { return _queuedPrompts.Count; }
        }

        public string CurrentModelId
        {
            get { return _currentModelId; }
        }

        public string CurrentModeId
        {
            get { return _currentModeId; }
        }

        public string AgentName
        {
            get { return _agentName; }
        }

        // =====================================================================
        // Panel -> agent
        // =====================================================================

        /// <summary>
        /// Handles one Claude-shaped stdin line written by AgentClient.
        /// Never throws; unparseable input is logged and dropped.
        /// </summary>
        public void OnPanelLine(string line)
        {
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(line ?? string.Empty, out node, out error) || node == null || !node.IsObject)
            {
                Log("Dropped non-JSON line from the panel: " + error);
                return;
            }
            string type = node["type"].AsString(string.Empty);
            switch (type)
            {
                case "control_request":
                    HandlePanelControlRequest(node);
                    break;
                case "user":
                    HandlePanelUserMessage(node);
                    break;
                case "control_response":
                    HandlePanelPermissionAnswer(node);
                    break;
                default:
                    Log("Ignored panel line of type '" + type + "'.");
                    break;
            }
        }

        private void HandlePanelControlRequest(JsonNode node)
        {
            string requestId = node["request_id"].AsString(string.Empty);
            JsonNode request = node["request"];
            string subtype = request["subtype"].AsString(string.Empty);
            switch (subtype)
            {
                case "initialize":
                    BeginHandshake(requestId);
                    break;
                case "interrupt":
                    HandleInterrupt(requestId);
                    break;
                case "set_model":
                    HandleSetModel(requestId, request["model"].AsString(string.Empty));
                    break;
                case "set_permission_mode":
                    HandleSetPermissionMode(requestId, request["mode"].AsString(string.Empty));
                    break;
                default:
                    EmitControlError(requestId, "control_request '" + subtype
                        + "' is not supported by the ACP bridge.");
                    break;
            }
        }

        private void BeginHandshake(string requestId)
        {
            _initRequestId = requestId;
            JsonNode clientInfo = JsonNode.NewObject()
                .Set("name", _spec.ClientName ?? "unity-agent-panel")
                .Set("title", "Agent Panel for Unity")
                .Set("version", _spec.ClientVersion ?? string.Empty);
            JsonNode parameters = JsonNode.NewObject()
                .Set("protocolVersion", ProtocolVersion)
                .Set("clientCapabilities", JsonNode.NewObject()
                    .Set("fs", JsonNode.NewObject()
                        .Set("readTextFile", false)
                        .Set("writeTextFile", false))
                    .Set("terminal", false))
                .Set("clientInfo", clientInfo);
            SendRequest("initialize", parameters, OnInitializeResult);
        }

        private void OnInitializeResult(AcpInbound response)
        {
            if (response.IsError)
            {
                FailHandshake("initialize failed: " + response.ErrorMessage);
                return;
            }
            JsonNode result = response.Result;
            int agentVersion = result["protocolVersion"].AsInt(ProtocolVersion);
            if (agentVersion != ProtocolVersion)
            {
                Log("Agent answered protocolVersion " + agentVersion + " (bridge speaks "
                    + ProtocolVersion + "); continuing best-effort.");
            }
            JsonNode caps = result["agentCapabilities"];
            _capLoadSession = caps["loadSession"].AsBool(false);
            _capPromptImage = caps["promptCapabilities"]["image"].AsBool(false);
            _capMcpHttp = caps["mcpCapabilities"]["http"].AsBool(false);
            _agentName = result["agentInfo"]["name"].AsString(string.Empty) ?? string.Empty;
            _agentVersion = result["agentInfo"]["version"].AsString(string.Empty) ?? string.Empty;
            _authMethodIds.Clear();
            _authMethodNames.Clear();
            foreach (JsonNode method in result["authMethods"].Items)
            {
                string id = method["id"].AsString();
                if (!string.IsNullOrEmpty(id))
                {
                    _authMethodIds.Add(id);
                    _authMethodNames.Add(method["name"].AsString(id));
                }
            }
            OpenSession();
        }

        private void OpenSession()
        {
            bool load = !string.IsNullOrEmpty(_spec.ResumeSessionId) && _capLoadSession;
            JsonNode parameters = JsonNode.NewObject()
                .Set("cwd", _workingDirectory)
                .Set("mcpServers", BuildMcpServers());
            if (load)
            {
                parameters.Set("sessionId", _spec.ResumeSessionId);
                _loadingSession = true;
                SendRequest("session/load", parameters, delegate(AcpInbound response)
                {
                    _loadingSession = false;
                    if (response.IsError)
                    {
                        // A stale/unknown session id must not brick the
                        // panel: fall back to a fresh session, exactly what
                        // the Claude CLI does when --resume cannot find one.
                        Log("session/load failed (" + response.ErrorMessage + "); creating a new session.");
                        OpenNewSession();
                        return;
                    }
                    _sessionLoaded = true;
                    _firstPromptSent = true; // the loaded session already saw the standing instructions
                    OnSessionReady(_spec.ResumeSessionId, response.Result);
                });
                return;
            }
            OpenNewSession();
        }

        private void OpenNewSession()
        {
            JsonNode parameters = JsonNode.NewObject()
                .Set("cwd", _workingDirectory)
                .Set("mcpServers", BuildMcpServers());
            SendRequest("session/new", parameters, delegate(AcpInbound response)
            {
                if (response.IsError)
                {
                    if (!_authAttempted && _authMethodIds.Count > 0
                        && LooksLikeAuthRequired(response))
                    {
                        Authenticate();
                        return;
                    }
                    FailHandshake("session/new failed: " + response.ErrorMessage);
                    return;
                }
                string sessionId = response.Result["sessionId"].AsString();
                if (string.IsNullOrEmpty(sessionId))
                {
                    FailHandshake("session/new returned no sessionId.");
                    return;
                }
                OnSessionReady(sessionId, response.Result);
            });
        }

        /// <summary>
        /// ACP reserves -32000 for "authentication required"; some agents
        /// only say so in the message. Pure so a test can pin the heuristic.
        /// </summary>
        internal static bool LooksLikeAuthRequired(AcpInbound response)
        {
            if (response.ErrorCode == AcpJsonRpc.AuthRequired)
            {
                return true;
            }
            string message = response.ErrorMessage ?? string.Empty;
            return message.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// First call: ranks the agent's methods (design note
        /// 2026-09-10-in-panel-install-and-sign-in.md section 2.4) and
        /// tries the best one; every failure moves on to the next
        /// candidate, and only when all of them failed does the handshake
        /// fail. Live report: codex-acp advertises "API Key" FIRST, so the
        /// old take-the-first rule tried the one method that cannot work
        /// without an environment variable and never reached the ChatGPT
        /// browser login two entries down.
        /// </summary>
        private void Authenticate()
        {
            if (!_authAttempted)
            {
                _authAttempted = true;
                _authCandidates.Clear();
                _authCandidates.AddRange(RankAuthMethods(_spec.AuthMethodId, _authMethodIds, _authMethodNames));
                _authCandidateIndex = 0;
                _authFailures.Clear();
            }
            if (_authCandidateIndex >= _authCandidates.Count)
            {
                string summary = string.Join("; ", _authFailures.ToArray());
                Action<bool, string> finished = AuthenticationFinished;
                if (finished != null)
                {
                    finished(false, summary);
                }
                FailHandshake("sign-in failed for every method the agent offers (" + summary
                    + "). Sign in to the agent's own CLI once"
                    + (string.IsNullOrEmpty(AgentBackends.LoginCommand(_spec.Backend))
                        ? string.Empty
                        : " (" + AgentBackends.LoginCommand(_spec.Backend) + ")")
                    + ", then press Sign in or Reconnect (Settings > Account).");
                return;
            }
            string methodId = _authCandidates[_authCandidateIndex++];
            string methodName = methodId;
            int index = _authMethodIds.IndexOf(methodId);
            if (index >= 0 && index < _authMethodNames.Count)
            {
                methodName = _authMethodNames[index];
            }
            Log("Agent requires authentication; trying method '" + methodId + "'.");
            Action<string, string> startedHandler = AuthenticationStarted;
            if (startedHandler != null)
            {
                startedHandler(methodId, methodName);
            }
            SendRequest("authenticate", JsonNode.NewObject().Set("methodId", methodId),
                delegate(AcpInbound response)
                {
                    if (response.IsError)
                    {
                        Log("authenticate('" + methodId + "') failed: " + response.ErrorMessage);
                        _authFailures.Add(methodName + ": " + response.ErrorMessage);
                        Authenticate();
                        return;
                    }
                    Action<bool, string> finishedHandler = AuthenticationFinished;
                    if (finishedHandler != null)
                    {
                        finishedHandler(true, null);
                    }
                    OpenNewSession();
                });
        }

        /// <summary>
        /// Pure: the order in which to try the agent's auth methods. The
        /// user's explicit choice goes first; then browser/OAuth account
        /// logins (the subscription path this panel exists for); then
        /// device-code logins; then anything unclassified; and last the
        /// methods that need an environment variable or a gateway (API
        /// key, gateway, Vertex), which can only fail from inside the
        /// panel. Classification is by id and display name keywords, so
        /// it survives agents this code has never seen.
        /// </summary>
        internal static List<string> RankAuthMethods(string preferred, List<string> ids, List<string> names)
        {
            var result = new List<string>();
            if (ids == null)
            {
                return result;
            }
            var buckets = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                string name = names != null && i < names.Count ? names[i] : string.Empty;
                int rank = string.Equals(id, preferred, StringComparison.Ordinal) ? 0 : ClassifyAuthMethod(id, name);
                buckets.Add(new KeyValuePair<int, string>(rank, id));
            }
            // Stable by insertion order within a rank (List.Sort is not stable).
            for (int rank = 0; rank <= ApiKeyRank; rank++)
            {
                for (int i = 0; i < buckets.Count; i++)
                {
                    if (buckets[i].Key == rank && !result.Contains(buckets[i].Value))
                    {
                        result.Add(buckets[i].Value);
                    }
                }
            }
            return result;
        }

        /// <summary>The rank <see cref="ClassifyAuthMethod"/> gives key/gateway methods: tried last, and billed to the key rather than a subscription.</summary>
        internal const int ApiKeyRank = 4;

        /// <summary>
        /// Pure: true when the method authenticates with a key or gateway
        /// the panel does not supply (the class RankAuthMethods sorts last).
        /// The Account card and the transcript note use this to say that
        /// usage is billed to that key rather than to a subscription
        /// (design note 2026-09-10-acp-auth-guidance-and-method-display.md
        /// section 4).
        /// </summary>
        internal static bool IsApiKeyAuthMethod(string id, string name)
        {
            return ClassifyAuthMethod(id, name) == ApiKeyRank;
        }

        /// <summary>Pure: how the UI names an auth method -- its display name, else its bare id, else empty.</summary>
        internal static string DescribeAuthMethod(string id, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
            return id ?? string.Empty;
        }

        internal static int ClassifyAuthMethod(string id, string name)
        {
            string text = ((id ?? string.Empty) + " " + (name ?? string.Empty)).ToLowerInvariant();
            bool keyLike = text.Contains("api") && text.Contains("key")
                || text.Contains("apikey") || text.Contains("gateway") || text.Contains("vertex")
                || text.Contains("token");
            if (keyLike)
            {
                return ApiKeyRank;
            }
            if (text.Contains("device"))
            {
                return 2;
            }
            if (text.Contains("chatgpt") || text.Contains("chat-gpt") || text.Contains("oauth")
                || text.Contains("google") || text.Contains("login") || text.Contains("log in")
                || text.Contains("sign in") || text.Contains("browser") || text.Contains("personal")
                || text.Contains("account") || text.Contains("subscription"))
            {
                return 1;
            }
            return 3;
        }

        /// <summary>The configured method when the agent offers it, else the agent's first method.</summary>
        internal static string ResolveAuthMethodId(string preferred, List<string> offered)
        {
            List<string> ranked = RankAuthMethods(preferred, offered, null);
            return ranked.Count > 0 ? ranked[0] : (preferred ?? string.Empty);
        }

        private JsonNode BuildMcpServers()
        {
            JsonNode servers = JsonNode.NewArray();
            if (string.IsNullOrEmpty(_spec.McpServerName) || string.IsNullOrEmpty(_spec.McpUrl))
            {
                return servers;
            }
            if (!_capMcpHttp)
            {
                Log("Agent does not advertise mcpCapabilities.http; UapOps tools will be"
                    + " unavailable to it this session.");
                return servers;
            }
            JsonNode headers = JsonNode.NewArray();
            if (!string.IsNullOrEmpty(_spec.McpBearerToken))
            {
                headers.Add(JsonNode.NewObject()
                    .Set("name", "Authorization")
                    .Set("value", "Bearer " + _spec.McpBearerToken));
            }
            servers.Add(JsonNode.NewObject()
                .Set("type", "http")
                .Set("name", _spec.McpServerName)
                .Set("url", _spec.McpUrl)
                .Set("headers", headers));
            return servers;
        }

        private void OnSessionReady(string sessionId, JsonNode result)
        {
            _sessionId = sessionId;
            ReadModes(result["modes"]);
            ReadModels(result["models"]);
            if (result["configOptions"].IsArray)
            {
                ReadConfigOptions(result["configOptions"]);
            }
            // Model: the panel's default (fresh spawn) is applied through
            // set_model when the agent lists it; on a loaded session the
            // hub passes null so the session keeps its own model.
            if (!string.IsNullOrEmpty(_spec.Model) && _models.Count > 0
                && !string.Equals(_spec.Model, _currentModelId, StringComparison.Ordinal)
                && FindModel(_spec.Model) != null)
            {
                string modelId = FindModel(_spec.Model).Id;
                SendRequest("session/set_model",
                    JsonNode.NewObject().Set("sessionId", _sessionId).Set("modelId", modelId),
                    delegate(AcpInbound response)
                    {
                        if (!response.IsError)
                        {
                            _currentModelId = modelId;
                        }
                        else
                        {
                            Log("session/set_model at start failed: " + response.ErrorMessage);
                        }
                    });
            }
            string modeId = ResolveModeId(_spec.PermissionMode, _modes);
            if (modeId != null && !string.Equals(modeId, _currentModeId, StringComparison.Ordinal))
            {
                SendRequest("session/set_mode",
                    JsonNode.NewObject().Set("sessionId", _sessionId).Set("modeId", modeId),
                    delegate(AcpInbound response)
                    {
                        if (!response.IsError)
                        {
                            _currentModeId = modeId;
                        }
                    });
            }
            ReplyInitialize();
            EmitSystemInit();
        }

        private void ReadModes(JsonNode modes)
        {
            _modes.Clear();
            if (modes == null || !modes.IsObject)
            {
                return;
            }
            foreach (JsonNode mode in modes["availableModes"].Items)
            {
                string id = mode["id"].AsString();
                if (!string.IsNullOrEmpty(id))
                {
                    _modes.Add(new KeyValuePair<string, string>(id, mode["name"].AsString(id)));
                }
            }
            _currentModeId = modes["currentModeId"].AsString(_currentModeId);
        }

        private void ReadModels(JsonNode models)
        {
            _models.Clear();
            if (models == null || !models.IsObject)
            {
                return;
            }
            foreach (JsonNode model in models["availableModels"].Items)
            {
                string id = model["modelId"].AsString();
                if (!string.IsNullOrEmpty(id))
                {
                    _models.Add(new ModelEntry
                    {
                        Id = id,
                        Name = model["name"].AsString(id),
                        Description = model["description"].AsString(string.Empty)
                    });
                    long window = model["_meta"]["totalContextTokens"].AsLong(0);
                    if (window > 0)
                    {
                        _catalogContextWindows[id] = window;
                    }
                }
            }
            _currentModelId = models["currentModelId"].AsString(_currentModelId);
        }

        /// <summary>
        /// Newer agents expose modes/models as "configOptions" (category
        /// mode/model); mirror them into the same tables so both shapes
        /// drive the same header picker.
        /// </summary>
        private void ReadConfigOptions(JsonNode options)
        {
            foreach (JsonNode option in options.Items)
            {
                string category = option["category"].AsString(string.Empty);
                string id = option["id"].AsString(string.Empty);
                bool isMode = category == "mode" || id == "mode";
                bool isModel = category == "model" || id == "model";
                if (!isMode && !isModel)
                {
                    continue;
                }
                string current = option["currentValue"].AsString(null);
                if (isMode && _modes.Count == 0)
                {
                    foreach (JsonNode choice in option["options"].Items)
                    {
                        string value = choice["value"].AsString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            _modes.Add(new KeyValuePair<string, string>(value, choice["name"].AsString(value)));
                        }
                    }
                    if (current != null)
                    {
                        _currentModeId = current;
                    }
                }
                if (isModel && _models.Count == 0)
                {
                    foreach (JsonNode choice in option["options"].Items)
                    {
                        string value = choice["value"].AsString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            _models.Add(new ModelEntry
                            {
                                Id = value,
                                Name = choice["name"].AsString(value),
                                Description = choice["description"].AsString(string.Empty)
                            });
                        }
                    }
                    if (current != null)
                    {
                        _currentModelId = current;
                    }
                }
            }
        }

        private ModelEntry FindModel(string idOrName)
        {
            for (int i = 0; i < _models.Count; i++)
            {
                if (string.Equals(_models[i].Id, idOrName, StringComparison.Ordinal))
                {
                    return _models[i];
                }
            }
            for (int i = 0; i < _models.Count; i++)
            {
                if (string.Equals(_models[i].Name, idOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return _models[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Maps the panel's Claude-style permission mode to one of the
        /// agent's advertised session mode ids, by a small vocabulary of
        /// synonyms (Gemini CLI: default/auto-edit/yolo, codex-acp:
        /// read-only/auto/full-access, ...). Null when nothing matches --
        /// the caller then leaves the agent's own default alone. Pure.
        /// </summary>
        internal static string ResolveModeId(string permissionMode, List<KeyValuePair<string, string>> modes)
        {
            if (string.IsNullOrEmpty(permissionMode) || modes == null || modes.Count == 0)
            {
                return null;
            }
            string[] candidates;
            switch (permissionMode)
            {
                case "acceptEdits":
                    candidates = new[] { "acceptEdits", "accept-edits", "auto-edit", "autoEdit", "auto_edit", "auto" };
                    break;
                case "bypassPermissions":
                    candidates = new[] { "bypassPermissions", "yolo", "full-access", "full_access", "fullAccess", "auto" };
                    break;
                case "plan":
                    candidates = new[] { "plan", "read-only", "readOnly", "read_only" };
                    break;
                default:
                    candidates = new[] { "default", "ask", "approve", "read-only", "readOnly", "read_only" };
                    break;
            }
            for (int c = 0; c < candidates.Length; c++)
            {
                for (int m = 0; m < modes.Count; m++)
                {
                    if (string.Equals(modes[m].Key, candidates[c], StringComparison.OrdinalIgnoreCase))
                    {
                        return modes[m].Key;
                    }
                }
            }
            return null;
        }

        private void ReplyInitialize()
        {
            if (_initReplied || _initRequestId == null)
            {
                return;
            }
            _initReplied = true;
            JsonNode models = JsonNode.NewArray();
            for (int i = 0; i < _models.Count; i++)
            {
                models.Add(JsonNode.NewObject()
                    .Set("value", _models[i].Id)
                    .Set("displayName", _models[i].Name)
                    .Set("description", _models[i].Description)
                    .Set("resolvedModel", _models[i].Id));
            }
            JsonNode response = JsonNode.NewObject()
                .Set("commands", BuildCommandsJson())
                .Set("models", models)
                .Set("agents", JsonNode.NewArray())
                .Set("account", JsonNode.NewObject())
                .Set("acp_agent", JsonNode.NewObject()
                    .Set("name", _agentName)
                    .Set("version", _agentVersion));
            EmitControlSuccess(_initRequestId, response);
        }

        private JsonNode BuildCommandsJson()
        {
            JsonNode commands = JsonNode.NewArray();
            for (int i = 0; i < _commands.Count; i++)
            {
                commands.Add(JsonNode.NewObject()
                    .Set("name", _commands[i].Name)
                    .Set("description", _commands[i].Description ?? string.Empty)
                    .Set("argumentHint", _commands[i].Hint ?? string.Empty));
            }
            return commands;
        }

        private void EmitSystemInit()
        {
            if (string.IsNullOrEmpty(_sessionId))
            {
                return;
            }
            _initEmitted = true;
            JsonNode slash = JsonNode.NewArray();
            for (int i = 0; i < _commands.Count; i++)
            {
                slash.Add(_commands[i].Name);
            }
            JsonNode mcp = JsonNode.NewArray();
            if (!string.IsNullOrEmpty(_spec.McpServerName) && !string.IsNullOrEmpty(_spec.McpUrl))
            {
                mcp.Add(JsonNode.NewObject()
                    .Set("name", _spec.McpServerName)
                    .Set("status", _capMcpHttp ? "connected" : "failed"));
            }
            string version = string.IsNullOrEmpty(_agentName)
                ? _agentVersion
                : (_agentName + (string.IsNullOrEmpty(_agentVersion) ? string.Empty : " " + _agentVersion));
            JsonNode node = JsonNode.NewObject()
                .Set("type", "system")
                .Set("subtype", "init")
                .Set("session_id", _sessionId)
                .Set("cwd", _workingDirectory)
                .Set("model", _currentModelId ?? _spec.Model ?? string.Empty)
                .Set("permissionMode", _spec.PermissionMode ?? string.Empty)
                .Set("claude_code_version", version)
                .Set("apiKeySource", "acp")
                .Set("tools", JsonNode.NewArray())
                .Set("slash_commands", slash)
                .Set("agents", JsonNode.NewArray())
                .Set("mcp_servers", mcp)
                .Set("acp_backend", AgentBackends.DisplayName(_spec.Backend));
            EmitToPanel(node);
        }

        private void FailHandshake(string reason)
        {
            Log("ACP handshake failed: " + reason);
            if (!_initReplied && _initRequestId != null)
            {
                _initReplied = true;
                EmitControlError(_initRequestId, reason);
            }
            // Surface the reason in the transcript through the ordinary
            // error-result path (AgentHub renders is_error results as an
            // error block), then let the transport tear the process down.
            EmitResult(true, reason, "error_during_execution", "handshake_failed");
            Action<string> handler = HandshakeFailed;
            if (handler != null)
            {
                handler(reason);
            }
        }

        /// <summary>Raised (under the bridge lock) when the handshake cannot complete; the transport stops the process.</summary>
        public event Action<string> HandshakeFailed;

        /// <summary>
        /// Raised (under the bridge lock) when the agent reported that
        /// sign-in is required and the bridge sent `authenticate`. ACP
        /// agents drive their own sign-in from here -- Gemini CLI and
        /// codex-acp open the browser OAuth flow -- so the panel only has
        /// to tell the user to look at the browser. Args: method id, method
        /// display name.
        /// </summary>
        public event Action<string, string> AuthenticationStarted;

        /// <summary>Raised when the `authenticate` round trip settled. Args: success, error text (null on success).</summary>
        public event Action<bool, string> AuthenticationFinished;

        private void HandlePanelUserMessage(JsonNode node)
        {
            JsonNode content = node["message"]["content"];
            JsonNode prompt = JsonNode.NewArray();
            var textForEcho = new StringBuilder();
            if (content.IsString)
            {
                string text = content.AsString(string.Empty);
                prompt.Add(JsonNode.NewObject().Set("type", "text").Set("text", text));
                textForEcho.Append(text);
            }
            else
            {
                foreach (JsonNode block in content.Items)
                {
                    string blockType = block["type"].AsString(string.Empty);
                    if (blockType == "text")
                    {
                        string text = block["text"].AsString(string.Empty);
                        prompt.Add(JsonNode.NewObject().Set("type", "text").Set("text", text));
                        textForEcho.Append(text);
                    }
                    else if (blockType == "image")
                    {
                        if (!_capPromptImage)
                        {
                            Log("Agent does not accept image prompts; image block dropped.");
                            continue;
                        }
                        prompt.Add(JsonNode.NewObject()
                            .Set("type", "image")
                            .Set("mimeType", block["source"]["media_type"].AsString("image/png"))
                            .Set("data", block["source"]["data"].AsString(string.Empty)));
                    }
                }
            }
            if (prompt.Count == 0)
            {
                Log("Dropped a user message with no translatable content.");
                return;
            }
            if (!_firstPromptSent && !string.IsNullOrEmpty(_spec.SystemPrompt))
            {
                prompt = PrependSystemPrompt(prompt, _spec.SystemPrompt);
            }
            // Echo like `--replay-user-messages` does: TurnTracker.MarkAck
            // clears its awaiting-ack flag on it.
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "user")
                .Set("isReplay", true)
                .Set("session_id", _sessionId ?? string.Empty)
                .Set("message", JsonNode.NewObject()
                    .Set("role", "user")
                    .Set("content", JsonNode.NewArray().Add(JsonNode.NewObject()
                        .Set("type", "text").Set("text", textForEcho.ToString())))));
            if (_promptInFlight)
            {
                _queuedPrompts.Enqueue(prompt);
                return;
            }
            _turnStartMillis = _clock.ElapsedMilliseconds;
            _turnText.Length = 0;
            ResetTurnUsage();
            SendPrompt(prompt);
        }

        /// <summary>Pure: the injected standing-instructions block goes FIRST so the model reads it before the user's ask.</summary>
        internal static JsonNode PrependSystemPrompt(JsonNode prompt, string systemPrompt)
        {
            JsonNode result = JsonNode.NewArray();
            result.Add(JsonNode.NewObject().Set("type", "text").Set("text",
                SystemPromptHeader + "\n" + systemPrompt.Trim() + "\n" + SystemPromptFooter + "\n\n"));
            foreach (JsonNode block in prompt.Items)
            {
                result.Add(block);
            }
            return result;
        }

        private void SendPrompt(JsonNode prompt)
        {
            if (string.IsNullOrEmpty(_sessionId))
            {
                EmitResult(true, "No ACP session is open; the message was not sent.",
                    "error_during_execution", "no_session");
                return;
            }
            _firstPromptSent = true;
            _promptInFlight = true;
            _cancelRequested = false;
            _promptRpcId = SendRequest("session/prompt",
                JsonNode.NewObject().Set("sessionId", _sessionId).Set("prompt", prompt),
                OnPromptResult);
        }

        private void OnPromptResult(AcpInbound response)
        {
            _promptInFlight = false;
            _promptRpcId = -1;
            if (response.IsError)
            {
                _queuedPrompts.Clear();
                FlushAssistantText();
                CompleteOpenToolCalls("The agent ended the turn before this tool call reported a result.");
                EmitResult(true, response.ErrorMessage, "error_during_execution", null);
                return;
            }
            string stopReason = response.Result["stopReason"].AsString("end_turn");
            AddPromptUsage(response.Result);
            if (_queuedPrompts.Count > 0 && !_cancelRequested)
            {
                // Steering send folded into the same panel turn: keep the
                // turn open, deliver the next prompt, one result at the end.
                FlushAssistantText();
                SendPrompt(_queuedPrompts.Dequeue());
                return;
            }
            _queuedPrompts.Clear();
            FlushAssistantText();
            CompleteOpenToolCalls(stopReason == "cancelled"
                ? "Cancelled before the tool call reported a result."
                : "The agent ended the turn before this tool call reported a result.");
            bool isError = stopReason == "refusal";
            EmitResult(isError, isError ? "The agent refused to continue." : _turnText.ToString(),
                isError ? "error_during_execution" : "success", stopReason);
        }

        private void HandleInterrupt(string requestId)
        {
            _queuedPrompts.Clear();
            if (_promptInFlight && !string.IsNullOrEmpty(_sessionId))
            {
                _cancelRequested = true;
                CancelPendingPermissions();
                _toAgent(AcpJsonRpc.Notification("session/cancel",
                    JsonNode.NewObject().Set("sessionId", _sessionId)));
            }
            EmitControlSuccess(requestId, JsonNode.NewObject().Set("still_queued", JsonNode.NewArray()));
        }

        private void HandleSetModel(string requestId, string model)
        {
            if (string.IsNullOrEmpty(_sessionId))
            {
                EmitControlError(requestId, "no session");
                return;
            }
            ModelEntry entry = FindModel(model);
            string modelId = entry != null ? entry.Id : model;
            SendRequest("session/set_model",
                JsonNode.NewObject().Set("sessionId", _sessionId).Set("modelId", modelId),
                delegate(AcpInbound response)
                {
                    if (response.IsError)
                    {
                        EmitControlError(requestId, response.ErrorMessage);
                        return;
                    }
                    _currentModelId = modelId;
                    EmitControlSuccess(requestId, JsonNode.NewObject());
                });
        }

        private void HandleSetPermissionMode(string requestId, string mode)
        {
            string modeId = ResolveModeId(mode, _modes);
            if (modeId == null || string.IsNullOrEmpty(_sessionId))
            {
                EmitControlError(requestId, "the agent offers no session mode matching '" + mode + "'");
                return;
            }
            SendRequest("session/set_mode",
                JsonNode.NewObject().Set("sessionId", _sessionId).Set("modeId", modeId),
                delegate(AcpInbound response)
                {
                    if (response.IsError)
                    {
                        EmitControlError(requestId, response.ErrorMessage);
                        return;
                    }
                    _currentModeId = modeId;
                    EmitControlSuccess(requestId, JsonNode.NewObject());
                });
        }

        private void HandlePanelPermissionAnswer(JsonNode node)
        {
            JsonNode envelope = node["response"];
            string requestId = envelope["request_id"].AsString(string.Empty);
            PermissionState pending;
            if (!_pendingPermissions.TryGetValue(requestId, out pending))
            {
                Log("Permission answer for unknown request '" + requestId + "' ignored.");
                return;
            }
            _pendingPermissions.Remove(requestId);
            JsonNode inner = envelope["response"];
            bool allow = inner["behavior"].AsString(string.Empty) == "allow";
            bool always = inner.HasKey("updatedPermissions")
                && inner["updatedPermissions"].IsArray
                && inner["updatedPermissions"].Count > 0;
            bool interrupt = inner["interrupt"].AsBool(false);
            string optionId = ChooseOptionId(pending, allow, always);
            JsonNode outcome;
            if (optionId != null)
            {
                outcome = JsonNode.NewObject().Set("outcome", "selected").Set("optionId", optionId);
            }
            else
            {
                outcome = JsonNode.NewObject().Set("outcome", "cancelled");
            }
            _toAgent(AcpJsonRpc.Response(pending.RpcId, JsonNode.NewObject().Set("outcome", outcome)));
            if (!allow && interrupt && _promptInFlight && !string.IsNullOrEmpty(_sessionId))
            {
                _cancelRequested = true;
                _queuedPrompts.Clear();
                _toAgent(AcpJsonRpc.Notification("session/cancel",
                    JsonNode.NewObject().Set("sessionId", _sessionId)));
            }
        }

        private static string ChooseOptionId(PermissionState pending, bool allow, bool always)
        {
            if (allow)
            {
                if (always && pending.AllowAlwaysId != null)
                {
                    return pending.AllowAlwaysId;
                }
                return pending.AllowOnceId ?? pending.AllowAlwaysId;
            }
            return pending.RejectOnceId ?? pending.RejectAlwaysId;
        }

        private void CancelPendingPermissions()
        {
            if (_pendingPermissions.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, PermissionState> pair in _pendingPermissions)
            {
                _toAgent(AcpJsonRpc.Response(pair.Value.RpcId, JsonNode.NewObject()
                    .Set("outcome", JsonNode.NewObject().Set("outcome", "cancelled"))));
            }
            _pendingPermissions.Clear();
        }

        // =====================================================================
        // Agent -> panel
        // =====================================================================

        /// <summary>
        /// Handles one stdout line from the agent. Non-JSON-RPC lines are
        /// forwarded to the log only (banners). Never throws.
        /// </summary>
        public void OnAgentLine(string line)
        {
            AcpInbound inbound = AcpJsonRpc.TryParse(line);
            if (inbound == null)
            {
                if (!string.IsNullOrEmpty(line) && line.Trim().Length > 0)
                {
                    Log("[agent stdout] " + line);
                }
                return;
            }
            try
            {
                if (inbound.IsResponse)
                {
                    HandleAgentResponse(inbound);
                }
                else if (inbound.IsNotification)
                {
                    HandleAgentNotification(inbound);
                }
                else if (inbound.IsRequest)
                {
                    HandleAgentRequest(inbound);
                }
            }
            catch (Exception ex)
            {
                Log("ACP bridge failed to handle an agent line: " + ex);
            }
        }

        /// <summary>
        /// The agent process died: settle whatever the panel still waits on
        /// so AgentClient's death path finds a consistent stream (a turn
        /// without a result would otherwise sit until the silence backstop
        /// -- AgentClient itself flips to Errored on exit regardless).
        /// </summary>
        public void OnAgentExited()
        {
            _pendingRpc.Clear();
            _pendingPermissions.Clear();
            _queuedPrompts.Clear();
            if (_promptInFlight)
            {
                _promptInFlight = false;
                FlushAssistantText();
                CompleteOpenToolCalls("The agent process exited before this tool call reported a result.");
                EmitResult(true, "The agent process exited mid-turn.", "error_during_execution", "process_exited");
            }
        }

        private void HandleAgentResponse(AcpInbound inbound)
        {
            long id = inbound.NumericId;
            Action<AcpInbound> handler;
            if (id < 0 || !_pendingRpc.TryGetValue(id, out handler))
            {
                Log("Unmatched JSON-RPC response (id " + JsonWriter.Write(inbound.Id) + ") ignored.");
                return;
            }
            _pendingRpc.Remove(id);
            handler(inbound);
        }

        private void HandleAgentNotification(AcpInbound inbound)
        {
            if (inbound.Method != "session/update")
            {
                Log("Ignored ACP notification '" + inbound.Method + "'.");
                return;
            }
            JsonNode update = inbound.Params["update"];
            string kind = update["sessionUpdate"].AsString(string.Empty);
            // Grok Build stamps every notification's params._meta with
            // totalTokens, its running estimate of the context; replayed
            // history carries the historical value, so the last line of a
            // session/load replay leaves the current one behind.
            long metaTokens = inbound.Params["_meta"]["totalTokens"].AsLong(-1);
            if (metaTokens > 0)
            {
                _contextUsedTokens = metaTokens;
            }
            if (_loadingSession)
            {
                // session/load replays history; the panel restores its own
                // transcript cache, so the replay is dropped -- except the
                // command list, which is session metadata, not history.
                if (kind == "available_commands_update")
                {
                    ReadCommands(update["availableCommands"]);
                }
                else if (kind == "current_mode_update")
                {
                    _currentModeId = update["currentModeId"].AsString(_currentModeId);
                }
                else if (kind == "usage_update")
                {
                    // Agents send the resumed session's context state
                    // right after session/load; it is the meter's first
                    // reading, not history.
                    OnUsageUpdate(update);
                }
                return;
            }
            switch (kind)
            {
                case "usage_update":
                    OnUsageUpdate(update);
                    break;
                case "agent_message_chunk":
                    OnAgentMessageChunk(update["content"]);
                    break;
                case "agent_thought_chunk":
                    OnAgentThoughtChunk(update["content"]);
                    break;
                case "tool_call":
                    OnToolCall(update, true);
                    break;
                case "tool_call_update":
                    OnToolCall(update, false);
                    break;
                case "available_commands_update":
                    ReadCommands(update["availableCommands"]);
                    if (_initEmitted)
                    {
                        EmitSystemInit();
                    }
                    break;
                case "current_mode_update":
                    _currentModeId = update["currentModeId"].AsString(_currentModeId);
                    break;
                case "config_option_update":
                    ReadConfigOptions(update["configOptions"]);
                    break;
                case "user_message_chunk":
                case "plan":
                    // Plan entries have no Claude-side equivalent the hub
                    // renders; the agent's own text narrates them anyway.
                    break;
                default:
                    Log("Ignored session/update kind '" + kind + "'.");
                    break;
            }
        }

        private void ReadCommands(JsonNode commands)
        {
            _commands.Clear();
            foreach (JsonNode command in commands.Items)
            {
                string name = command["name"].AsString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                _commands.Add(new CommandEntry
                {
                    Name = name,
                    Description = command["description"].AsString(string.Empty),
                    Hint = command["input"]["hint"].AsString(string.Empty)
                });
            }
        }

        private void OnAgentMessageChunk(JsonNode content)
        {
            string text = ContentBlockText(content);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (_thinkingStarted && _thinkingBuffer.Length > 0 && _textBuffer.Length == 0)
            {
                // Thinking finished, text starts: keep them in separate
                // content blocks like the Claude stream does.
                _blockIndex++;
            }
            _textBuffer.Append(text);
            _turnText.Append(text);
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "stream_event")
                .Set("session_id", _sessionId ?? string.Empty)
                .Set("event", JsonNode.NewObject()
                    .Set("type", "content_block_delta")
                    .Set("index", _blockIndex)
                    .Set("delta", JsonNode.NewObject().Set("type", "text_delta").Set("text", text))));
        }

        private void OnAgentThoughtChunk(JsonNode content)
        {
            string text = ContentBlockText(content);
            if (text == null)
            {
                return;
            }
            if (_textBuffer.Length > 0)
            {
                // Thought after text (agents that reason between
                // paragraphs): fold what has streamed so far into its own
                // assistant message so the transcript keeps the real order
                // -- text, then this thinking block -- instead of one
                // message that puts every thought before every sentence.
                FlushAssistantText();
            }
            if (!_thinkingStarted)
            {
                _thinkingStarted = true;
                EmitToPanel(JsonNode.NewObject()
                    .Set("type", "stream_event")
                    .Set("session_id", _sessionId ?? string.Empty)
                    .Set("event", JsonNode.NewObject()
                        .Set("type", "content_block_start")
                        .Set("index", _blockIndex)
                        .Set("content_block", JsonNode.NewObject().Set("type", "thinking").Set("thinking", string.Empty))));
            }
            if (text.Length == 0)
            {
                return;
            }
            _thinkingBuffer.Append(text);
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "stream_event")
                .Set("session_id", _sessionId ?? string.Empty)
                .Set("event", JsonNode.NewObject()
                    .Set("type", "content_block_delta")
                    .Set("index", _blockIndex)
                    .Set("delta", JsonNode.NewObject().Set("type", "thinking_delta").Set("thinking", text))));
        }

        /// <summary>Text of an ACP ContentBlock (text; resource_link/resource summarized by uri).</summary>
        internal static string ContentBlockText(JsonNode content)
        {
            if (content == null || !content.IsObject)
            {
                return null;
            }
            switch (content["type"].AsString(string.Empty))
            {
                case "text":
                    return content["text"].AsString(string.Empty);
                case "resource_link":
                    return content["uri"].AsString(string.Empty);
                case "resource":
                    {
                        string text = content["resource"]["text"].AsString();
                        return text ?? content["resource"]["uri"].AsString(string.Empty);
                    }
                case "image":
                    return "[image]";
                case "audio":
                    return "[audio]";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Folds streamed text/thinking into one completed assistant
        /// message -- the same "deltas, then the full message" order the
        /// Claude stream has, which AgentHub.OnAssistantMessageCompleted
        /// reconciles against its streaming buffer.
        /// </summary>
        private void FlushAssistantText()
        {
            if (_textBuffer.Length == 0 && _thinkingBuffer.Length == 0)
            {
                _thinkingStarted = false;
                return;
            }
            JsonNode content = JsonNode.NewArray();
            if (_thinkingBuffer.Length > 0)
            {
                content.Add(JsonNode.NewObject().Set("type", "thinking").Set("thinking", _thinkingBuffer.ToString()));
            }
            if (_textBuffer.Length > 0)
            {
                content.Add(JsonNode.NewObject().Set("type", "text").Set("text", _textBuffer.ToString()));
            }
            EmitAssistant(content);
            _textBuffer.Length = 0;
            _thinkingBuffer.Length = 0;
            _thinkingStarted = false;
            _blockIndex = 0;
        }

        private void OnToolCall(JsonNode update, bool isCreate)
        {
            string toolCallId = update["toolCallId"].AsString();
            if (string.IsNullOrEmpty(toolCallId))
            {
                return;
            }
            ToolCallState state;
            if (!_toolCalls.TryGetValue(toolCallId, out state))
            {
                state = new ToolCallState { Id = toolCallId };
                _toolCalls[toolCallId] = state;
                _toolCallOrder.Add(toolCallId);
            }
            if (update.HasKey("title"))
            {
                state.Title = update["title"].AsString(state.Title);
            }
            if (update.HasKey("kind"))
            {
                state.Kind = update["kind"].AsString(state.Kind);
            }
            else if (string.IsNullOrEmpty(state.Kind))
            {
                state.Kind = GrokMetaKind(update);
            }
            if (update.HasKey("rawInput") && update["rawInput"].IsObject)
            {
                state.Input = update["rawInput"];
            }
            AppendToolOutput(state, update);
            if (!state.Announced)
            {
                AnnounceToolCall(state, update);
            }
            string status = update["status"].AsString(string.Empty);
            if (!state.Completed && (status == "completed" || status == "failed"))
            {
                CompleteToolCall(state, status == "failed", null);
            }
        }

        private void AnnounceToolCall(ToolCallState state, JsonNode update)
        {
            // Text that streamed before this tool call belongs BEFORE it in
            // the transcript, so it is folded now.
            FlushAssistantText();
            state.Announced = true;
            state.Name = MapToolName(state.Kind, state.Title, state.Input);
            JsonNode input = BuildToolInput(state, update);
            state.Input = input;
            EmitAssistant(JsonNode.NewArray().Add(JsonNode.NewObject()
                .Set("type", "tool_use")
                .Set("id", state.Id)
                .Set("name", state.Name)
                .Set("input", input)));
        }

        /// <summary>
        /// The Claude-side tool name the panel renders/policies on (design
        /// note 2026-09-17-acp-tool-name-mapping.md). UapOps calls map to
        /// the exact `mcp__unity-ops__uap_*` wire name so
        /// UapOpsServer.FindByWireName, auto-approve levels and the undo
        /// badge all keep working. That name is a POLICY input (it can
        /// auto-approve the call), so it is only ever read from a field
        /// that IS a tool id -- never searched for inside free text:
        /// - a shell call (`command`/`script` in the raw input) is never a
        ///   UapOps call, whatever its text mentions;
        /// - the title's leading token (Gemini: "uap_x (unity-ops MCP
        ///   Server)", Grok's later title "unity-ops__uap_x"), except for
        ///   kind "execute" whose title is the command line (Codex);
        /// - the raw input's `tool_name` / `tool` / `toolName` (Grok's
        ///   first frame is title "use_tool" + {tool_name, tool_input};
        ///   Codex sends {server, tool, arguments}); a `server` next to it
        ///   must be unity-ops.
        /// Another MCP server's call in Codex's {server, tool} shape maps to
        /// `mcp__server__tool` instead of "Bash". Everything else maps by
        /// ACP kind onto the closest Claude tool name (ToolCardDescriber
        /// reads `command`/`file_path`/`path` from the raw input when the
        /// agent provides them and falls back to the name otherwise); kind
        /// other/unknown shows the agent's own title when that cannot be
        /// mistaken for a Claude tool name. Pure.
        /// </summary>
        internal static string MapToolName(string kind, string title, JsonNode rawInput)
        {
            bool hasInput = rawInput != null && rawInput.IsObject;
            bool isShell = hasInput && (rawInput["command"].IsString || rawInput["script"].IsString);
            if (!isShell)
            {
                string uap = kind == "execute" ? null : ParseUapToolId(title, true);
                string server = hasInput ? rawInput["server"].AsString() : null;
                if (uap == null && hasInput && (server == null || server == UapServerName))
                {
                    uap = ParseUapToolId(rawInput["tool_name"].AsString(), false)
                        ?? ParseUapToolId(rawInput["tool"].AsString(), false)
                        ?? ParseUapToolId(rawInput["toolName"].AsString(), false);
                }
                if (uap != null)
                {
                    return "mcp__" + UapServerName + "__" + uap;
                }
                string tool = hasInput ? rawInput["tool"].AsString() : null;
                if (IsMcpIdSegment(server) && IsMcpIdSegment(tool))
                {
                    return "mcp__" + server + "__" + tool;
                }
            }
            switch (kind ?? string.Empty)
            {
                case "read":
                    return "Read";
                case "edit":
                    return "Edit";
                case "delete":
                    return "Delete";
                case "move":
                    return "Move";
                case "search":
                    return "Search";
                case "execute":
                    return "Bash";
                case "fetch":
                    return "WebFetch";
                case "think":
                    return "Think";
                default:
                    return AgentTitleAsToolName(title) ?? "Tool";
            }
        }

        /// <summary>
        /// Grok Build's first tool_call frame has no ACP `kind` (it arrives
        /// one update later, after the name is already announced) but
        /// carries `_meta["x.ai/tool"].kind`, which is the ACP kind for its
        /// file/shell tools ("execute", "read", ...) and the tool's own name
        /// otherwise ("use_tool"). Only the ACP values are taken.
        /// </summary>
        internal static string GrokMetaKind(JsonNode toolCall)
        {
            string kind = toolCall["_meta"]["x.ai/tool"]["kind"].AsString();
            switch (kind ?? string.Empty)
            {
                case "read":
                case "edit":
                case "delete":
                case "move":
                case "search":
                case "execute":
                case "fetch":
                case "think":
                    return kind;
                default:
                    return null;
            }
        }

        private const string UapServerName = "unity-ops";

        private static readonly string[] UapToolIdPrefixes =
        {
            "mcp__" + UapServerName + "__", // Claude wire name
            "mcp." + UapServerName + ".",   // Codex title
            UapServerName + "__",           // Grok / Codex function name
            string.Empty                    // bare id (Gemini)
        };

        /// <summary>
        /// `uap_<snake_case>` when the text IS a UapOps tool id, optionally
        /// qualified with the unity-ops server; null otherwise. With
        /// <paramref name="allowTrailingText"/> only the leading token (up
        /// to the first whitespace) has to be the id.
        /// </summary>
        internal static string ParseUapToolId(string text, bool allowTrailingText)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            string token = text.Trim();
            if (allowTrailingText)
            {
                for (int i = 0; i < token.Length; i++)
                {
                    if (char.IsWhiteSpace(token[i]))
                    {
                        token = token.Substring(0, i);
                        break;
                    }
                }
            }
            for (int p = 0; p < UapToolIdPrefixes.Length; p++)
            {
                if (token.StartsWith(UapToolIdPrefixes[p], StringComparison.Ordinal))
                {
                    token = token.Substring(UapToolIdPrefixes[p].Length);
                    break;
                }
            }
            if (token.Length <= 4 || !token.StartsWith("uap_", StringComparison.Ordinal))
            {
                return null;
            }
            for (int i = 0; i < token.Length; i++)
            {
                if (!IsIdentifierChar(token[i]))
                {
                    return null;
                }
            }
            return token;
        }

        private static bool IsMcpIdSegment(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("__", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsIdentifierChar(text[i]) && text[i] != '-' && text[i] != '.')
                {
                    return false;
                }
            }
            return true;
        }

        private const int AgentTitleMaxChars = 48;

        /// <summary>
        /// The agent's own title as the tool name for kind other/unknown
        /// ("search_tool", "X search:"), or null to keep "Tool". The panel
        /// special-cases Claude's tool names (Write, Bash, Task,
        /// AskUserQuestion, mcp__*), all of which are a single run of
        /// letters or start with "mcp__"; a title of that form is refused
        /// so an agent's title can never opt into that handling.
        /// </summary>
        internal static string AgentTitleAsToolName(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return null;
            }
            string name = title.Trim();
            int lineEnd = name.IndexOfAny(new[] { '\r', '\n' });
            if (lineEnd >= 0)
            {
                name = name.Substring(0, lineEnd).TrimEnd();
            }
            if (name.Length > AgentTitleMaxChars)
            {
                name = name.Substring(0, AgentTitleMaxChars - 3) + "...";
            }
            if (name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            bool lettersOnly = true;
            for (int i = 0; i < name.Length && lettersOnly; i++)
            {
                lettersOnly = (name[i] >= 'a' && name[i] <= 'z') || (name[i] >= 'A' && name[i] <= 'Z');
            }
            return lettersOnly ? null : name;
        }

        private static bool IsIdentifierChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }

        /// <summary>
        /// The tool's own arguments. Grok ({tool_name, tool_input}) and
        /// Codex ({server, tool, arguments}) wrap an MCP call's arguments
        /// in their dispatcher's envelope; for a call mapped to an MCP wire
        /// name the panel shows what Claude would: the arguments.
        /// </summary>
        internal static JsonNode McpArguments(string mappedName, JsonNode rawInput)
        {
            if (rawInput == null || !rawInput.IsObject || mappedName == null
                || !mappedName.StartsWith("mcp__", StringComparison.Ordinal))
            {
                return rawInput;
            }
            if (rawInput["tool_name"].IsString && rawInput["tool_input"].IsObject)
            {
                return rawInput["tool_input"];
            }
            if (rawInput["tool"].IsString && rawInput["arguments"].IsObject)
            {
                return rawInput["arguments"];
            }
            return rawInput;
        }

        private static JsonNode BuildToolInput(ToolCallState state, JsonNode update)
        {
            JsonNode input = JsonNode.NewObject();
            JsonNode arguments = McpArguments(state.Name, state.Input);
            if (arguments != null && arguments.IsObject)
            {
                foreach (KeyValuePair<string, JsonNode> pair in arguments.Properties)
                {
                    input.Set(pair.Key, pair.Value);
                }
            }
            if (!string.IsNullOrEmpty(state.Title) && !input.HasKey("title"))
            {
                input.Set("title", state.Title);
            }
            if (!string.IsNullOrEmpty(state.Kind) && !input.HasKey("kind"))
            {
                input.Set("kind", state.Kind);
            }
            JsonNode locations = update["locations"];
            if (locations.IsArray && locations.Count > 0 && !input.HasKey("file_path") && !input.HasKey("path"))
            {
                string path = locations[0]["path"].AsString();
                if (!string.IsNullOrEmpty(path))
                {
                    // Both spellings: ToolCardDescriber reads either, and the
                    // script validation gate's can_use_tool pre-filter
                    // (ScriptGate) keys on `file_path` for edit-kind tools.
                    input.Set("path", path);
                    input.Set("file_path", path);
                }
            }
            return input;
        }

        private static void AppendToolOutput(ToolCallState state, JsonNode update)
        {
            JsonNode content = update["content"];
            if (content.IsArray)
            {
                foreach (JsonNode item in content.Items)
                {
                    string type = item["type"].AsString(string.Empty);
                    string text = null;
                    if (type == "content")
                    {
                        if (TryAddImage(state, item["content"]))
                        {
                            continue;
                        }
                        text = ContentBlockText(item["content"]);
                    }
                    else if (type == "diff")
                    {
                        text = "diff " + item["path"].AsString(string.Empty) + "\n"
                            + (item["oldText"].IsNull ? string.Empty : "--- old\n" + item["oldText"].AsString(string.Empty) + "\n")
                            + "+++ new\n" + item["newText"].AsString(string.Empty);
                    }
                    else if (type == "terminal")
                    {
                        text = "[terminal " + item["terminalId"].AsString(string.Empty) + "]";
                    }
                    AppendOutputLine(state, text);
                }
            }
            if (state.Output.Length == 0 && state.Images.Count == 0
                && update.HasKey("rawOutput") && !update["rawOutput"].IsNull)
            {
                JsonNode raw = update["rawOutput"];
                if (AppendMcpResult(state, raw))
                {
                    return;
                }
                state.Output.Append(raw.IsString ? raw.AsString(string.Empty) : JsonWriter.Write(raw));
            }
        }

        /// <summary>
        /// Same values as ToolResultImages.MaxImagesPerResult / MaxBase64Chars
        /// (the reader that decodes these blocks). Repeated here because this
        /// file also compiles Unity-free without the Model layer
        /// (ci/SmokeTests); AcpProtocolBridgeTests pins the two pairs together.
        /// </summary>
        internal const int MaxToolResultImages = 4;
        internal const int MaxToolResultImageBase64Chars = 8 * 1024 * 1024;

        /// <summary>
        /// codex-acp reports an MCP tool's result only as rawOutput =
        /// {"result":{"content":[...MCP blocks...],...},"error":null}, with an
        /// empty `content`. MCP content blocks have the ACP ContentBlock
        /// shape, so they translate exactly like the `content` path --
        /// stringifying the object instead would carry a screenshot's base64
        /// as result text and leave the picture out of the tool card. False
        /// (nothing appended) for any other shape, or when the blocks yield
        /// nothing, so the caller's stringify fallback still applies.
        /// </summary>
        private static bool AppendMcpResult(ToolCallState state, JsonNode raw)
        {
            if (!raw.IsObject || !raw["result"].IsObject || !raw["result"]["content"].IsArray)
            {
                return false;
            }
            foreach (JsonNode block in raw["result"]["content"].Items)
            {
                if (!TryAddImage(state, block))
                {
                    AppendOutputLine(state, ContentBlockText(block));
                }
            }
            return state.Output.Length > 0 || state.Images.Count > 0;
        }

        /// <summary>
        /// Keeps an image block as a picture (not the "[image]" stand-in
        /// text) so the tool card can show it. False for a non-image block,
        /// and for a picture the panel would never decode (past the per-result
        /// count, or an oversized payload): carrying that base64 through the
        /// result buys nothing, and the stand-in text still says it was there.
        /// </summary>
        private static bool TryAddImage(ToolCallState state, JsonNode block)
        {
            JsonNode image = ImageBlockFromAcp(block);
            if (image == null
                || state.Images.Count >= MaxToolResultImages
                || image["source"]["data"].AsString(string.Empty).Length > MaxToolResultImageBase64Chars)
            {
                return false;
            }
            state.Images.Add(image);
            return true;
        }

        private static void AppendOutputLine(ToolCallState state, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (state.Output.Length > 0)
            {
                state.Output.Append('\n');
            }
            state.Output.Append(text);
        }

        /// <summary>
        /// An ACP image content block ({"type":"image","data":base64,
        /// "mimeType":...}) as the API-shaped block the panel's tool_result
        /// reader (ToolResultImages) decodes; null for anything else.
        /// </summary>
        internal static JsonNode ImageBlockFromAcp(JsonNode content)
        {
            if (content == null || !content.IsObject
                || content["type"].AsString(string.Empty) != "image")
            {
                return null;
            }
            string data = content["data"].AsString(null);
            if (string.IsNullOrEmpty(data))
            {
                return null;
            }
            return JsonNode.NewObject()
                .Set("type", "image")
                .Set("source", JsonNode.NewObject()
                    .Set("type", "base64")
                    .Set("media_type", content["mimeType"].AsString("image/png"))
                    .Set("data", data));
        }

        private void CompleteToolCall(ToolCallState state, bool isError, string fallbackText)
        {
            state.Completed = true;
            string text = state.Output.Length > 0 ? state.Output.ToString() : (fallbackText ?? string.Empty);
            // A plain string when the tool produced only text (byte-for-byte
            // what every reader expected before images); an array of blocks
            // once a picture is involved, the same shape the Claude stream
            // carries for Read on a PNG.
            JsonNode resultContent;
            if (state.Images.Count == 0)
            {
                resultContent = JsonNode.Of(text);
            }
            else
            {
                resultContent = JsonNode.NewArray();
                if (!string.IsNullOrEmpty(text))
                {
                    resultContent.Add(JsonNode.NewObject().Set("type", "text").Set("text", text));
                }
                for (int i = 0; i < state.Images.Count; i++)
                {
                    resultContent.Add(state.Images[i]);
                }
            }
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "user")
                .Set("session_id", _sessionId ?? string.Empty)
                .Set("message", JsonNode.NewObject()
                    .Set("role", "user")
                    .Set("content", JsonNode.NewArray().Add(JsonNode.NewObject()
                        .Set("type", "tool_result")
                        .Set("tool_use_id", state.Id)
                        .Set("content", resultContent)
                        .Set("is_error", isError)))));
        }

        private void CompleteOpenToolCalls(string reason)
        {
            for (int i = 0; i < _toolCallOrder.Count; i++)
            {
                ToolCallState state;
                if (_toolCalls.TryGetValue(_toolCallOrder[i], out state) && state.Announced && !state.Completed)
                {
                    CompleteToolCall(state, false, reason);
                }
            }
            _toolCalls.Clear();
            _toolCallOrder.Clear();
        }

        private void HandleAgentRequest(AcpInbound inbound)
        {
            switch (inbound.Method)
            {
                case "session/request_permission":
                    OnRequestPermission(inbound);
                    break;
                default:
                    // fs/read_text_file, fs/write_text_file, terminal/*:
                    // never advertised (clientCapabilities all false).
                    _toAgent(AcpJsonRpc.ErrorResponse(inbound.Id, AcpJsonRpc.MethodNotFound,
                        "Method not supported by this client: " + inbound.Method));
                    break;
            }
        }

        private void OnRequestPermission(AcpInbound inbound)
        {
            JsonNode toolCall = inbound.Params["toolCall"];
            string toolCallId = toolCall["toolCallId"].AsString(string.Empty);
            ToolCallState state = null;
            if (!string.IsNullOrEmpty(toolCallId))
            {
                if (!_toolCalls.TryGetValue(toolCallId, out state))
                {
                    state = new ToolCallState { Id = toolCallId };
                    _toolCalls[toolCallId] = state;
                    _toolCallOrder.Add(toolCallId);
                }
                if (toolCall.HasKey("title"))
                {
                    state.Title = toolCall["title"].AsString(state.Title);
                }
                if (toolCall.HasKey("kind"))
                {
                    state.Kind = toolCall["kind"].AsString(state.Kind);
                }
                if (toolCall["rawInput"].IsObject)
                {
                    state.Input = toolCall["rawInput"];
                }
                if (!state.Announced)
                {
                    AnnounceToolCall(state, toolCall);
                }
            }
            var pending = new PermissionState { RpcId = inbound.Id, ToolCallId = toolCallId };
            foreach (JsonNode option in inbound.Params["options"].Items)
            {
                string optionId = option["optionId"].AsString();
                if (string.IsNullOrEmpty(optionId))
                {
                    continue;
                }
                switch (option["kind"].AsString(string.Empty))
                {
                    case "allow_once":
                        if (pending.AllowOnceId == null) pending.AllowOnceId = optionId;
                        break;
                    case "allow_always":
                        if (pending.AllowAlwaysId == null) pending.AllowAlwaysId = optionId;
                        break;
                    case "reject_once":
                        if (pending.RejectOnceId == null) pending.RejectOnceId = optionId;
                        break;
                    case "reject_always":
                        if (pending.RejectAlwaysId == null) pending.RejectAlwaysId = optionId;
                        break;
                }
            }
            string requestId = PermissionRequestIdPrefix + (++_permissionCounter);
            _pendingPermissions[requestId] = pending;

            string toolName = state != null ? state.Name : MapToolName(toolCall["kind"].AsString(), toolCall["title"].AsString(), null);
            // An MCP wire name is its own display name: PermissionCard
            // formats it as "server: tool", where the agent's title would
            // be the raw "unity-ops__uap_ping" (Grok).
            string title = state != null && !string.IsNullOrEmpty(state.Title)
                && !toolName.StartsWith("mcp__", StringComparison.Ordinal) ? state.Title : toolName;
            JsonNode input = state != null && state.Input != null
                ? McpArguments(toolName, state.Input) : JsonNode.NewObject();
            JsonNode suggestions = JsonNode.NewArray();
            if (pending.AllowAlwaysId != null)
            {
                // Shape PermissionCard already understands (the CLI's own
                // addRules suggestion); accepting it comes back as
                // updatedPermissions, which selects allow_always above.
                suggestions.Add(JsonNode.NewObject()
                    .Set("type", "addRules")
                    .Set("rules", JsonNode.NewArray().Add(JsonNode.NewObject().Set("toolName", toolName)))
                    .Set("behavior", "allow")
                    .Set("destination", "session"));
            }
            JsonNode request = JsonNode.NewObject()
                .Set("subtype", "can_use_tool")
                .Set("tool_name", toolName)
                .Set("display_name", title)
                .Set("input", input)
                .Set("tool_use_id", toolCallId)
                .Set("permission_suggestions", suggestions);
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "control_request")
                .Set("request_id", requestId)
                .Set("request", request));
        }

        // =====================================================================
        // Emit helpers
        // =====================================================================

        // =====================================================================
        // Usage translation (design note 2026-09-10-acp-usage-display.md)
        // =====================================================================

        /// <summary>Tokens in context per the agent's last usage_update; -1 when none arrived. Test seam.</summary>
        internal long ContextUsedTokens { get { return _contextUsedTokens; } }

        /// <summary>Context window size per the agent's last usage_update, else the catalog's value for the current model; 0 when unknown. Test seam.</summary>
        internal long ContextWindowTokens { get { return EffectiveContextWindow(); } }

        /// <summary>
        /// usage_update.size wins (it describes the live window, e.g.
        /// after a model switch); otherwise the model catalog's
        /// `_meta.totalContextTokens` for the current model, which is how
        /// Grok Build publishes the window (its TUI reads the same key).
        /// </summary>
        private long EffectiveContextWindow()
        {
            if (_contextWindowTokens > 0)
            {
                return _contextWindowTokens;
            }
            long window;
            if (!string.IsNullOrEmpty(_currentModelId)
                && _catalogContextWindows.TryGetValue(_currentModelId, out window))
            {
                return window;
            }
            return 0;
        }

        /// <summary>
        /// session/update "usage_update": `used` tokens currently in
        /// context and `size` of the window, plus an optional cumulative
        /// `cost` (ACP RFD "Session Context Size and Cost"). codex-acp
        /// sends it on every Codex token-count event (used = the last
        /// model call's total tokens, size = the model's context window);
        /// Gemini CLI 0.59 does not send it at all. Either value is kept
        /// when the other is missing so a size-only update after
        /// session/new still arms the meter's denominator.
        /// </summary>
        private void OnUsageUpdate(JsonNode update)
        {
            long used = update["used"].AsLong(-1);
            long size = update["size"].AsLong(0);
            if (used >= 0)
            {
                _contextUsedTokens = used;
            }
            if (size > 0)
            {
                _contextWindowTokens = size;
            }
            JsonNode cost = update["cost"];
            if (cost.IsObject)
            {
                string currency = cost["currency"].AsString(string.Empty);
                double amount = cost["amount"].AsDouble(0.0);
                // The panel's cost field is dollars; another currency
                // would be shown with the wrong unit, so it is dropped.
                if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) && amount >= 0)
                {
                    _cumulativeCostUsd = amount;
                }
            }
        }

        private void ResetTurnUsage()
        {
            _turnInputTokens = 0;
            _turnOutputTokens = 0;
            _turnCacheReadTokens = 0;
            _turnCacheWriteTokens = 0;
            _turnUsageReported = false;
            _turnModelUsage.Clear();
        }

        /// <summary>
        /// Folds one PromptResponse's token counts into the panel turn
        /// (several prompts fold into one turn on a steering send).
        /// Sources, in order: the ACP `usage` object (inputTokens /
        /// outputTokens / cachedReadTokens / cachedWriteTokens --
        /// codex-acp fills it with the LAST model call's counts, so a
        /// Codex turn reports its final call, not the sum of all calls),
        /// then the `_meta.quota.token_count` extension both Gemini CLI
        /// (snake_case input_tokens/output_tokens, turn totals) and
        /// codex-acp (camelCase TokenCount) write, with Gemini's
        /// per-model `model_usage` rows kept for the usage popover.
        /// </summary>
        private void AddPromptUsage(JsonNode result)
        {
            if (result == null || !result.IsObject)
            {
                return;
            }
            JsonNode usage = result["usage"];
            if (usage.IsObject)
            {
                _turnInputTokens += usage["inputTokens"].AsLong();
                _turnOutputTokens += usage["outputTokens"].AsLong();
                _turnCacheReadTokens += usage["cachedReadTokens"].AsLong();
                _turnCacheWriteTokens += usage["cachedWriteTokens"].AsLong();
                _turnUsageReported = true;
            }
            JsonNode meta = result["_meta"];
            // Grok Build: _meta.totalTokens is the context after the turn,
            // _meta.usage the whole-prompt bill (inputTokens INCLUDING
            // cache reads, per its own doc comment), and the sibling
            // inputTokens/outputTokens/cachedReadTokens the last call only.
            long metaTotal = meta["totalTokens"].AsLong(-1);
            if (metaTotal > 0)
            {
                _contextUsedTokens = metaTotal;
            }
            if (!usage.IsObject && meta["usage"].IsObject)
            {
                JsonNode bill = meta["usage"];
                AddGrokUsageRow(bill, null);
                foreach (KeyValuePair<string, JsonNode> pair in bill["modelUsage"].Properties)
                {
                    if (pair.Value.IsObject)
                    {
                        AddGrokUsageRow(pair.Value, pair.Key);
                    }
                }
                double ticks = bill["costUsdTicks"].AsDouble(0.0);
                if (ticks > 0 && !bill["costIsPartial"].AsBool(false) && !bill["usageIsIncomplete"].AsBool(false))
                {
                    _cumulativeCostUsd += ticks / 1e10;
                }
                _turnUsageReported = true;
            }
            else if (!usage.IsObject && meta["inputTokens"].IsNumber)
            {
                _turnInputTokens += meta["inputTokens"].AsLong();
                _turnOutputTokens += meta["outputTokens"].AsLong();
                _turnCacheReadTokens += meta["cachedReadTokens"].AsLong();
                _turnUsageReported = true;
            }
            JsonNode quota = meta["quota"];
            if (!quota.IsObject)
            {
                return;
            }
            JsonNode count = quota["token_count"];
            if (!usage.IsObject && count.IsObject)
            {
                _turnInputTokens += ReadTokenField(count, "input_tokens", "inputTokens");
                _turnOutputTokens += ReadTokenField(count, "output_tokens", "outputTokens");
                _turnCacheReadTokens += ReadTokenField(count, "cached_input_tokens", "cachedInputTokens");
                _turnUsageReported = true;
            }
            foreach (JsonNode row in quota["model_usage"].Items)
            {
                if (!row.IsObject)
                {
                    continue;
                }
                string model = row["model"].AsString(string.Empty);
                JsonNode rowCount = row["token_count"];
                if (model.Length == 0 || !rowCount.IsObject)
                {
                    continue;
                }
                long[] totals;
                if (!_turnModelUsage.TryGetValue(model, out totals))
                {
                    totals = new long[2];
                    _turnModelUsage[model] = totals;
                }
                totals[0] += ReadTokenField(rowCount, "input_tokens", "inputTokens");
                totals[1] += ReadTokenField(rowCount, "output_tokens", "outputTokens");
            }
        }

        /// <summary>
        /// One Grok PromptUsage row (totals when <paramref name="model"/>
        /// is null, else a per-model row). Its inputTokens fold the cache
        /// reads in, so the panel's input (uncached) is the difference.
        /// </summary>
        private void AddGrokUsageRow(JsonNode row, string model)
        {
            long fullInput = row["inputTokens"].AsLong();
            long cacheRead = row["cachedReadTokens"].AsLong();
            long cacheWrite = row["cacheCreationTokens"].AsLong();
            long input = Math.Max(0, fullInput - cacheRead - cacheWrite);
            long output = row["outputTokens"].AsLong();
            if (model == null)
            {
                _turnInputTokens += input;
                _turnOutputTokens += output;
                _turnCacheReadTokens += cacheRead;
                _turnCacheWriteTokens += cacheWrite;
                return;
            }
            long[] totals;
            if (!_turnModelUsage.TryGetValue(model, out totals))
            {
                totals = new long[2];
                _turnModelUsage[model] = totals;
            }
            totals[0] += input;
            totals[1] += output;
        }

        private static long ReadTokenField(JsonNode node, string snakeName, string camelName)
        {
            long value = node[snakeName].AsLong(-1);
            return value >= 0 ? value : Math.Max(0, node[camelName].AsLong(0));
        }

        /// <summary>
        /// The usage object on every assistant line the bridge emits. The
        /// hub takes a live context reading from the four-field sum of
        /// the newest assistant message and adds input+output to the
        /// status bar's in-flight counter, so the context reading rides
        /// in cache_read_input_tokens alone: an ACP agent gives no
        /// billing split for it, and putting it under input_tokens would
        /// add the whole context to the token counter once per message.
        /// </summary>
        private JsonNode BuildAssistantUsage()
        {
            JsonNode usage = JsonNode.NewObject()
                .Set("input_tokens", 0)
                .Set("output_tokens", 0);
            if (_contextUsedTokens > 0)
            {
                usage.Set("cache_read_input_tokens", _contextUsedTokens);
            }
            return usage;
        }

        /// <summary>
        /// result.usage: the turn's counts plus, when a usage_update was
        /// seen, one `iterations` entry whose four-field sum is the
        /// context reading (UsageInfo.LastIterationContextTokens is what
        /// the hub's meter reads after a turn).
        /// </summary>
        private JsonNode BuildResultUsage()
        {
            JsonNode usage = JsonNode.NewObject()
                .Set("input_tokens", _turnInputTokens)
                .Set("output_tokens", _turnOutputTokens)
                .Set("cache_creation_input_tokens", _turnCacheWriteTokens)
                .Set("cache_read_input_tokens", _turnCacheReadTokens);
            if (_contextUsedTokens >= 0)
            {
                usage.Set("iterations", JsonNode.NewArray().Add(JsonNode.NewObject()
                    .Set("input_tokens", _contextUsedTokens)
                    .Set("output_tokens", 0)
                    .Set("cache_creation_input_tokens", 0)
                    .Set("cache_read_input_tokens", 0)));
            }
            return usage;
        }

        /// <summary>
        /// result.modelUsage: one row per model (Gemini's per-model rows
        /// when present, else the current model), the current model's row
        /// carrying contextWindow so StatusBarView.SelectPrimaryModelUsage
        /// finds the meter's denominator by exact model id. Empty when the
        /// agent reported nothing, which keeps the meter hidden exactly as
        /// before instead of showing 0%.
        /// </summary>
        private JsonNode BuildResultModelUsage()
        {
            JsonNode modelUsage = JsonNode.NewObject();
            long window = EffectiveContextWindow();
            if (!_turnUsageReported && window <= 0)
            {
                return modelUsage;
            }
            string current = string.IsNullOrEmpty(_currentModelId) ? "agent" : _currentModelId;
            if (_turnModelUsage.Count > 0)
            {
                bool currentSeen = false;
                foreach (KeyValuePair<string, long[]> pair in _turnModelUsage)
                {
                    bool isCurrent = string.Equals(pair.Key, current, StringComparison.Ordinal);
                    currentSeen |= isCurrent;
                    modelUsage.Set(pair.Key, ModelUsageRow(pair.Value[0], pair.Value[1], 0, 0,
                        isCurrent ? window : 0));
                }
                if (!currentSeen && window > 0)
                {
                    modelUsage.Set(current, ModelUsageRow(0, 0, 0, 0, window));
                }
                return modelUsage;
            }
            modelUsage.Set(current, ModelUsageRow(_turnInputTokens, _turnOutputTokens,
                _turnCacheReadTokens, _turnCacheWriteTokens, window));
            return modelUsage;
        }

        private static JsonNode ModelUsageRow(long input, long output, long cacheRead, long cacheWrite,
            long contextWindow)
        {
            return JsonNode.NewObject()
                .Set("inputTokens", input)
                .Set("outputTokens", output)
                .Set("cacheReadInputTokens", cacheRead)
                .Set("cacheCreationInputTokens", cacheWrite)
                .Set("webSearchRequests", 0)
                .Set("costUSD", 0.0)
                .Set("contextWindow", contextWindow)
                .Set("maxOutputTokens", 0);
        }

        private void EmitAssistant(JsonNode content)
        {
            _messageCounter++;
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "assistant")
                .Set("session_id", _sessionId ?? string.Empty)
                .Set("message", JsonNode.NewObject()
                    .Set("id", "acp-msg-" + _messageCounter)
                    .Set("role", "assistant")
                    .Set("model", _currentModelId ?? string.Empty)
                    .Set("content", content)
                    .Set("stop_reason", JsonNode.Null)
                    .Set("usage", BuildAssistantUsage())));
        }

        private void EmitResult(bool isError, string text, string subtype, string stopReason)
        {
            long duration = Math.Max(0, _clock.ElapsedMilliseconds - _turnStartMillis);
            JsonNode node = JsonNode.NewObject()
                .Set("type", "result")
                .Set("subtype", subtype ?? (isError ? "error_during_execution" : "success"))
                .Set("is_error", isError)
                .Set("result", text ?? string.Empty)
                .Set("session_id", _sessionId ?? string.Empty)
                .Set("duration_ms", duration)
                .Set("duration_api_ms", duration)
                .Set("num_turns", 1)
                .Set("total_cost_usd", _cumulativeCostUsd)
                .Set("usage", BuildResultUsage())
                .Set("modelUsage", BuildResultModelUsage());
            if (!string.IsNullOrEmpty(stopReason))
            {
                node.Set("stop_reason", stopReason);
            }
            EmitToPanel(node);
        }

        private void EmitControlSuccess(string requestId, JsonNode response)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "control_response")
                .Set("response", JsonNode.NewObject()
                    .Set("subtype", "success")
                    .Set("request_id", requestId)
                    .Set("response", response ?? JsonNode.NewObject())));
        }

        private void EmitControlError(string requestId, string error)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }
            EmitToPanel(JsonNode.NewObject()
                .Set("type", "control_response")
                .Set("response", JsonNode.NewObject()
                    .Set("subtype", "error")
                    .Set("request_id", requestId)
                    .Set("error", error ?? string.Empty)));
        }

        private void EmitToPanel(JsonNode node)
        {
            _toPanel(JsonWriter.Write(node));
        }

        private long SendRequest(string method, JsonNode parameters, Action<AcpInbound> onResponse)
        {
            long id = _nextRpcId++;
            if (onResponse == null)
            {
                onResponse = delegate(AcpInbound ignored) { };
            }
            _pendingRpc[id] = onResponse;
            _toAgent(AcpJsonRpc.Request(id, method, parameters));
            return id;
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }
    }
}
