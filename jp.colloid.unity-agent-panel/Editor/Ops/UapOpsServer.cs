using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Static composition root for UapOps (Phase 5a, design section 1 /
    /// 8.6 ADR): owns the ToolRegistry, the per-editor-session bearer
    /// token, the enabled-module set and the resident HttpListener-backed
    /// MCP server. AgentHub.StartClient calls <see cref="EnsureStarted"/>
    /// BEFORE every CLI spawn when PanelSettings.uapOpsEnabled is on
    /// (listen-before-spawn, docs/research/08-mcp-transport.md section 5)
    /// and <see cref="Stop"/> when it is off. The server's lifetime is
    /// otherwise the EDITOR SESSION, not the CLI connection: reconnecting
    /// or restarting the CLI must never bounce the port/token, since the
    /// design's mcp_reconnect safety net (R08 section 3.4) assumes the
    /// Unity-side server keeps running independently of CLI churn. A
    /// domain reload resets all of these statics anyway, which is exactly
    /// the "server restarts with the panel" behavior the design calls for.
    /// </summary>
    public static class UapOpsServer
    {
        private static UapOpsHttpServer _server;
        private static UapMainThreadDispatcher _dispatcher;
        private static ToolRegistry _registry;
        private static UapPlaybookCatalog _playbooks;
        private static string _token;
        private static List<string> _enabledModules = new List<string> { "core" };
        private static bool _pumpHooked;

        /// <summary>True once Start() has succeeded and Stop() has not since been called.</summary>
        public static bool IsRunning
        {
            get { return _server != null && _server.IsListening; }
        }

        /// <summary>OS-assigned port, or 0 when not running.</summary>
        public static int Port
        {
            get { return _server != null ? _server.Port : 0; }
        }

        /// <summary>Current bearer token, or null before the first EnsureStarted() this editor session.</summary>
        public static string Token
        {
            get { return _token; }
        }

        /// <summary>The tool registry (lazily created so PanelSettings/SettingsView can query it -- e.g. for a future module list UI -- even before the server itself has started).</summary>
        public static ToolRegistry Registry
        {
            get
            {
                if (_registry == null)
                {
                    _registry = ToolRegistry.CreateDefault();
                }
                return _registry;
            }
        }

        /// <summary>
        /// The playbook catalog uap_help and the steering text read (design
        /// note docs/design-notes/2026-10-02-uap-help-playbooks.md): Core's
        /// built-ins plus whatever <see cref="IUapPlaybookProvider"/>s
        /// contribute. Lazy like <see cref="Registry"/>; a domain reload
        /// rebuilds it.
        /// </summary>
        public static UapPlaybookCatalog Playbooks
        {
            get
            {
                if (_playbooks == null)
                {
                    _playbooks = UapPlaybookCatalog.Create(UapPlaybookProviderDiscovery.Discover(),
                        delegate(string message) { Debug.LogError("[UapPlaybooks] " + message); });
                }
                return _playbooks;
            }
        }

        // SessionState (not a plain static) so the answer survives the domain
        // reloads Play Mode and compiles cause while the agent's connection
        // lives on; it is the same editor-session lifetime as the spawn.
        private const string CompactToolListSessionKey = "UnityAgentPanel.UapOps.CompactToolList";

        /// <summary>
        /// True when the agent connection AgentHub last spawned uses the
        /// compact tool list (uap_tools/uap_call). uap_help cannot see which
        /// endpoint called it, so it reads this to decide whether the
        /// compact-mode playbook (which needs the two gateway tools) is on
        /// offer. False until the first spawn.
        /// </summary>
        public static bool CompactToolList
        {
            get
            {
                try
                {
                    return SessionState.GetBool(CompactToolListSessionKey, false);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Records the resolved tool-list mode of the connection AgentHub is spawning (see <see cref="CompactToolList"/>).</summary>
        public static void SetCompactToolList(bool compact)
        {
            try
            {
                SessionState.SetBool(CompactToolListSessionKey, compact);
            }
            catch (Exception)
            {
                // SessionState can be unavailable during early editor start-up; the flag then stays false.
            }
        }

        /// <summary>
        /// Resolves a CLI wire tool_use/can_use_tool name (e.g.
        /// "mcp__unity-ops__asset_create") to the registered
        /// <see cref="IUapTool"/>, or null when the name is not one of
        /// ours (<see cref="UapOpsMcpConfig.StripToolNamePrefix"/> returns
        /// null) or is not currently registered. Pure lookup -- used by the
        /// permission card's "not undoable" badge and the turn-level
        /// non-undoable warning (design section 8.2 B2), never mutates
        /// anything.
        /// </summary>
        public static IUapTool FindByWireName(string wireToolName)
        {
            string bareName = UapOpsMcpConfig.StripToolNamePrefix(wireToolName);
            return bareName == null ? null : Registry.Find(bareName);
        }

        /// <summary>
        /// Like <see cref="FindByWireName"/>, but transparent to uap_call
        /// (design note docs/design-notes/2026-09-22-uloop-phase1.md
        /// section 2.8 item 4): a decision or display that depends on
        /// WHICH tool actually runs must resolve through this, not
        /// FindByWireName, once uap_call exists -- calling uap_call with
        /// name:"uap_scene_create_object" must auto-approve/badge/attribute
        /// exactly like a direct call to uap_scene_create_object, not like
        /// a call to an unregistered "uap_call". Every call site that used
        /// to read FindByWireName for that purpose (AgentHub's
        /// TrackNonUndoableToolIfNeeded / TryTrackScriptsCommitAttribution /
        /// TryAutoApproveUapOpsTool, PermissionCard's undo badge) now reads
        /// this instead.
        ///
        /// <paramref name="input"/> is the request's own arguments (e.g.
        /// CanUseToolRequest.Input / ContentBlock.Input) -- for a plain
        /// (non-uap_call) wire name it passes straight through untouched
        /// via <paramref name="effectiveInput"/>; for uap_call it is
        /// parsed the SAME way UapOpsRequestHandler.HandleToolsCall parses
        /// it (UapToolGateway.TryUnwrapCall), so a permission card and the
        /// tool that actually executes always agree on what "the
        /// arguments" are. A malformed uap_call (no 'name') resolves to
        /// null, exactly like an unknown tool name -- never throws.
        /// </summary>
        public static IUapTool ResolveEffectiveTool(string wireToolName, JsonNode input, out JsonNode effectiveInput)
        {
            effectiveInput = input;
            string bareName = UapOpsMcpConfig.StripToolNamePrefix(wireToolName);
            if (bareName == null)
            {
                return null;
            }
            if (string.Equals(bareName, UapToolGateway.CallToolName, StringComparison.Ordinal))
            {
                string innerName;
                JsonNode innerArguments;
                if (!UapToolGateway.TryUnwrapCall(input, out innerName, out innerArguments))
                {
                    return null;
                }
                effectiveInput = innerArguments;
                return Registry.Find(innerName);
            }
            return Registry.Find(bareName);
        }

        /// <summary>
        /// Sets which tool modules tools/list serves. Takes effect on the
        /// NEXT tools/list call -- no server restart needed (design section
        /// 7.1: module changes are meant to be cheap, reflected via
        /// mcp_reconnect rather than a full respawn).
        /// </summary>
        public static void SetEnabledModules(IEnumerable<string> modules)
        {
            _enabledModules = modules != null ? new List<string>(modules) : new List<string>();
        }

        /// <summary>
        /// A copy of the module list tools/list currently serves. A tool
        /// that dispatches to OTHER tools (uap_batch) has to check this:
        /// the module filter is applied when tools/list is built, so a tool
        /// reached by name through another tool would otherwise sidestep a
        /// module the user deliberately switched off. A copy, so a caller
        /// cannot edit the live list.
        /// </summary>
        public static List<string> EnabledModules()
        {
            return new List<string>(_enabledModules);
        }

        /// <summary>Starts the server if it is not already running. Safe to call repeatedly.</summary>
        public static void EnsureStarted(Action<string> logger = null)
        {
            if (IsRunning)
            {
                return;
            }
            if (_token == null)
            {
                _token = UapOpsAuth.GenerateToken();
            }
            _dispatcher = new UapMainThreadDispatcher();
            _dispatcher.StallHintProvider = ReadStallHint;
            // Console errors a tool call raises go back to the agent in the
            // result instead of onto the user's "fix these errors" chip
            // (design note 2026-09-17-tool-caused-console-errors.md).
            _dispatcher.BeginToolLogScope = Colloid.AgentPanel.Integration.ConsoleErrorProvider.BeginToolScope;
            _dispatcher.EndToolLogScope = Colloid.AgentPanel.Integration.ConsoleErrorProvider.EndToolScope;
            // uap_web_fetch runs on the HTTP worker and hops back through
            // this dispatcher only to downscale an image on the main thread
            // (design note 2026-09-17-web-fetch-tool.md section 6).
            UapWebFetchTool.MainThreadExecutor = _dispatcher;
            Colloid.AgentPanel.Model.PanelSettings webSettings = Colloid.AgentPanel.Model.PanelStateStore.instance.Settings;
            UapWebFetchTool.HostRules = new UapWebHostRules(webSettings.webFetchAllowedHosts, webSettings.webFetchBlockedHosts);
            UapWebSearchTool.Config = new UapWebSearchConfig(
                UapWebSearchConfig.ParseProvider(webSettings.webSearchProvider), webSettings.webSearchApiKey);
            var handler = new UapOpsRequestHandler(Registry, () => _token,
                () => _enabledModules, _dispatcher, null, ReadThrottleNotice);
            var server = new UapOpsHttpServer(handler, logger);
            server.Start();
            _server = server;
            HookPump();
            // uap_console_logs reads what this captures. Hooked HERE rather
            // than from [InitializeOnLoadMethod] so the panel adds no
            // always-on editor work of its own: capture runs only while an
            // agent session with UapOps enabled is live (design note
            // docs/design-notes/2026-09-21-play-mode-and-console-log-tools.md
            // section 3).
            UapConsoleLogBuffer.Install();
        }

        /// <summary>
        /// Stops the server (master toggle OFF, or the beforeAssemblyReload
        /// path). Safe to call repeatedly / when not running. Cancels any
        /// tools/call still QUEUED for the dispatcher before unhooking the
        /// pump -- once UnhookPump runs, nothing will ever call Pump()
        /// again, so a work item left in the queue would otherwise block
        /// its HTTP worker thread inside UapMainThreadDispatcher.Execute for
        /// up to DefaultTimeoutMillis with no way to ever complete.
        /// </summary>
        public static void Stop()
        {
            if (_dispatcher != null)
            {
                _dispatcher.CancelAll("UapOps server is stopping.");
            }
            UapWebFetchTool.MainThreadExecutor = null;
            UapConsoleLogBuffer.Uninstall();
            UnhookPump();
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
            _dispatcher = null;
        }

        private static void HookPump()
        {
            if (_pumpHooked)
            {
                return;
            }
            _pumpHooked = true;
            EditorApplication.update += OnUpdate;
        }

        private static void UnhookPump()
        {
            if (!_pumpHooked)
            {
                return;
            }
            _pumpHooked = false;
            EditorApplication.update -= OnUpdate;
        }

        private static void OnUpdate()
        {
            RefreshThrottleSnapshot();
            if (_dispatcher != null)
            {
                _dispatcher.Pump();
            }
        }

        // -- Editor-loop throttle notice (design note 2026-09-06 section 7) --
        // Live report: with Play Mode running and the Editor unfocused (Run
        // In Background off) the Editor loop ticks sporadically, a
        // uap_query_hierarchy call died as a bare "The operation timed
        // out." on the CLI side, and -- unlike uloop's tools -- nothing in
        // the uap_* responses said why. The main thread refreshes this
        // snapshot every tick; worker threads only ever read the volatile
        // string (Unity APIs are main-thread-only), and the dispatcher /
        // request handler splice it into timeouts and successful results.

        private static volatile string _throttleNotice;

        /// <summary>
        /// The condition text the pure decision maps to, exposed for tests
        /// and for the transcript: one sentence naming the cause and the
        /// fix, worded so the model acts on it (focus the window) rather
        /// than retrying blindly.
        /// </summary>
        public const string PlayModeUnfocusedNotice =
            "The Unity Editor is unfocused while Play Mode is running (Run In Background is off),"
            + " so the Editor loop is throttled and uap_* calls may be slow or time out."
            + " Focus the Unity Editor window (or enable Run In Background in Player Settings) and retry.";

        /// <summary>
        /// Pure: the notice for the given Editor state, or null when the
        /// main thread is expected to tick normally.
        /// </summary>
        internal static string ComputeThrottleNotice(bool isPlaying, bool editorFocused, bool runInBackground)
        {
            if (isPlaying && !editorFocused && !runInBackground)
            {
                return PlayModeUnfocusedNotice;
            }
            return null;
        }

        private static void RefreshThrottleSnapshot()
        {
            try
            {
                _throttleNotice = ComputeThrottleNotice(
                    EditorApplication.isPlaying,
                    UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                    Application.runInBackground);
            }
            catch (Exception)
            {
                // A snapshot is a courtesy; never let it break the pump.
            }
        }

        /// <summary>Worker-thread-safe read of the last main-thread snapshot (null = not throttled).</summary>
        internal static string ReadThrottleNotice()
        {
            return _throttleNotice;
        }

        /// <summary>
        /// What the dispatcher appends to a main-thread timeout: the
        /// throttle snapshot, else a native modal dialog owning the Editor
        /// right now (design note 2026-09-17-modal-menu-and-base64-scan
        /// section 1.3). Worker-thread-safe: the probe is plain user32.
        /// Only the dispatcher gets the dialog half -- a call that
        /// SUCCEEDED cannot have run behind a modal dialog.
        /// </summary>
        internal static string ReadStallHint()
        {
            return ReadThrottleNotice() ?? UapNativeModalProbe.Describe();
        }

        /// <summary>Test-only: resets every static back to a clean, never-started slate.</summary>
        internal static void ResetForTests()
        {
            Stop();
            _registry = null;
            _playbooks = null;
            SetCompactToolList(false);
            _token = null;
            _enabledModules = new List<string> { "core" };
        }
    }
}
