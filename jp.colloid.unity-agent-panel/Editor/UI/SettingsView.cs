using System.Collections.Generic;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.Ops.Profiles;
using Colloid.AgentPanel.Ops.UnityPlugin;
using UnityEditor;
// PackageManager: only for the uLoop install progress poll (the AddRequest
// UloopInstaller.Apply hands back and its StatusCode) -- nothing else in
// this view talks to Package Manager.
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
// UnityEngine.UIElements.PanelSettings (the UI Toolkit render-settings
// asset type) collides with this package's own
// Colloid.AgentPanel.Model.PanelSettings -- same collision documented in
// AgentPanelWindow.cs and resolved there and in ComposerView.cs with this
// alias.
using PanelSettings = Colloid.AgentPanel.Model.PanelSettings;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Settings view (ARCHITECTURE.md Phase 3 / R05 section 6.1): conversation
    /// defaults (permission mode, Ctrl+Enter, tool allow/deny lists, the
    /// dangerouslySkipPermissions danger zone) and model choice lead --
    /// see CreateGUI's section-order comment for why -- followed by
    /// appearance (font size, CJK UI font), CLI path/reconnect status, and a
    /// read-only stderr tail. Every field persists through
    /// PanelStateStore.SaveNow() immediately
    /// on change -- there is no separate "Apply"/"Save" button, matching
    /// every other settings surface already in this codebase (Ctrl+Enter,
    /// preferCjkUiFont). Which changes also take effect immediately versus
    /// only at the next CLI spawn is documented field-by-field below and in
    /// docs/design-notes/2026-07-31-settings-propagation.md.
    /// </summary>
    public sealed class SettingsView : IAgentPanelView
    {
        private VisualElement _root;
        /// <summary>
        /// 2026-09-05 UI redesign (settings, S2): the scrolling body. The
        /// root became a plain container so the reconnect-pending banner
        /// can sit ABOVE the scroll and stay visible whichever card the
        /// user is editing -- it used to live inside the CLI card, which
        /// is collapsed by default, so a Conversation change that needed a
        /// reconnect was announced somewhere the user could not see.
        /// </summary>
        private ScrollView _scroll;
        private VisualElement _reconnectBanner;
        private bool _active;

        private TextField _cliPathField;
        private Label _cliResolvedLabel;
        private PopupField<AgentBackend> _backendField;
        private VisualElement _claudeCliGroup;
        private VisualElement _acpCliGroup;
        private TextField _acpCommandField;
        private TextField _acpArgumentsField;
        private TextField _acpAuthMethodField;
        private Label _acpCommandHintLabel;
        private Label _acpLoginHintLabel;
        /// <summary>Under the subagent model fields: how an ACP agent receives them (design note 2026-09-13-acp-feature-parity.md section 3).</summary>
        private Label _subagentModelAcpHintLabel;
        private VisualElement _reconnectHintRow;
        private Label _reconnectHintLabel;
        private Label _reconnectPendingPillLabel;

        private PopupField<PermissionModeOption> _permissionModeField;
        private Toggle _ctrlEnterToggle;
        private TextField _allowedToolsField;
        private TextField _disallowedToolsField;
        private Toggle _dangerousToggle;

        private PopupField<string> _defaultModelField;
        private Label _defaultModelHintLabel;
        private PopupField<SubagentCostPolicy> _subagentCostPolicyField;
        private PopupField<string> _subagentModelField;
        private Label _subagentPrecedenceWarningLabel;
        private Foldout _agentOverridesFoldout;
        private VisualElement _agentOverridesHost;
        private Label _agentOverridesDuplicateWarningLabel;

        /// <summary>
        /// Snapshot of the BuildAgentTypeChoices() result as of the last
        /// RebuildAgentOverrideRows() call. OnHubChanged compares the
        /// freshly-computed choice list against this on every tick so it
        /// can call RebuildAgentOverrideRows() ONLY when the catalog
        /// actually changed (e.g. RefreshAgentTypeCatalogCache just
        /// populated PanelSettings.agentTypeCatalog from a live system/
        /// init) rather than on every AgentHub.Changed tick, which would
        /// otherwise blow away an in-progress edit (open PopupField,
        /// TextField focus) on unrelated events like streaming tokens --
        /// see RefreshModelSection's doc comment for the same rationale
        /// applied to the row list as a whole.
        /// </summary>
        private List<string> _lastAgentOverrideTypeChoices;

        private TextField _customInstructionsField;

        private Toggle _showThinkingToggle;
        private Toggle _subagentDefaultExpandedToggle;
        private Toggle _showCostUsdToggle;

        private VisualElement _quickActionsHost;

        private Toggle _permissionBeepToggle;
        private Toggle _turnCompleteBeepToggle;

        private PopupField<PanelLanguage> _languageField;
        private SliderInt _fontSizeSlider;
        private Label _fontSizeValueLabel;
        private Toggle _cjkToggle;
        private Label _cjkDiagnosticLabel;

        private Toggle _uapOpsEnabledToggle;
        private Label _autoApproveWarningLabel;
        private Label _gateWarningLabel;
        private Label _autoContinueWarningLabel;
        private Label _agentOverridesNewSessionHint;
        private Toggle _uapOpsCoreModuleToggle;
        private Toggle _uapOpsPrefabModuleToggle;
        private Toggle _uapOpsEditorModuleToggle;
        private Toggle _uapOpsAnimModuleToggle;
        private Toggle _uapOpsMarkersModuleToggle;
        private Toggle _uapOpsUiModuleToggle;
        private Toggle _uapOpsAuthoringModuleToggle;
        private Toggle _uapOpsAvatarModuleToggle;
        private Toggle _uapOpsBatchModuleToggle;
        private Toggle _uapOpsTestsModuleToggle;
        private Toggle _uapOpsFxModuleToggle;
        private Toggle _uapOpsMeshModuleToggle;
        // Modules whose toggle AddModuleHint disabled because no add-on
        // registered any tool for them (Agent Panel Pro absent). Consulted
        // by RefreshUapOpsStatus, which otherwise re-enabled every module
        // switch whenever the master switch was on -- including, since the
        // 2026-09-11 split, the Pro-only ones it had just been told to
        // grey out (2026-09-12 core-only wording note, section 3).
        private readonly HashSet<string> _uapOpsModulesWithoutTools = new HashSet<string>(System.StringComparer.Ordinal);
        private Toggle _uapOpsGateEnabledToggle;
        private Toggle _uapOpsAutoContinueToggle;
        private Toggle _autoContinueInterruptedToggle;
        private Label _autoContinueInterruptedWarningLabel;
        private Label _playModeReloadHintLabel;
        private PopupField<UapAutoApproveLevel> _autoApproveLevelField;
        private Label _uapOpsStatusLabel;

        private Toggle _extensionProfilesEnabledToggle;
        private VisualElement _extensionProfilesHost;

        // -- uLoop integration (Phase 5c, design section 2/2.5) -------------------

        private Label _uloopStatusLabel;
        private Button _uloopInstallButton;
        private VisualElement _uloopInstallConfirmCard;
        private Label _uloopInstallRouteLabel;
        private VisualElement _uloopCaveatsHost;
        private Foldout _uloopDiffFoldout;
        private TextField _uloopDiffField;
        private Button _uloopInstallApplyButton;
        private Button _uloopInstallCancelButton;
        private Label _uloopInstallResultLabel;

        /// <summary>
        /// The ONE live install progress line (design-notes/2026-08-12-
        /// uloop-install-progress.md section 2a): "Installing... {n}s" while
        /// a resolve is in flight, the async failure text when the polled
        /// AddRequest reports Failure, the stalled text when only an old
        /// in-flight flag remains. Hidden whenever UloopInstallProgress says
        /// Idle or Succeeded (the status label's "Installed" carries the
        /// success). Sits at section level, NOT inside the confirmation
        /// card, because the state it must survive -- the domain reload a
        /// successful resolve triggers -- rebuilds this view with the card
        /// closed and no plan; a card-internal label would be invisible for
        /// exactly the stretch this feature exists to cover.
        /// </summary>
        private Label _uloopInstallProgressLabel;

        /// <summary>
        /// The AddRequest returned by the last successful Apply this domain
        /// has seen (UloopInstallApplyResult.ClientAddRequest). Deliberately
        /// a plain field: it CANNOT survive the domain reload a successful
        /// resolve causes, and that is fine -- the SessionStateBridge flag
        /// pair covers the post-reload stretch, and UloopInstallProgress
        /// ranks a live request above the flag whenever both exist. Kept
        /// (not nulled) after a FailedAsync render so every later refresh
        /// re-derives the failure text from the request object itself
        /// instead of a cached string.
        /// </summary>
        private AddRequest _uloopLiveAddRequest;

        /// <summary>True while <see cref="OnUloopProgressTick"/> is subscribed to EditorApplication.update.</summary>
        private bool _uloopProgressTickHooked;

        /// <summary>
        /// EditorApplication.timeSinceStartup of the last tick that actually
        /// evaluated/rendered -- the ~2-per-second throttle. The editor
        /// ticks update far faster than once per 500ms, and re-writing a
        /// label (plus the detector's manifest scan feeding it) every tick
        /// would dirty layout continuously for a number that only changes
        /// once a second; 2Hz keeps the elapsed counter visibly live (the
        /// changing number IS the liveness proof) at a fraction of the work.
        /// </summary>
        private double _uloopProgressLastEvalAt;

        // -- UICODE-4: coalesced hub refresh --------------------------------

        /// <summary>Coalescing interval for AgentHub.Changed-driven
        /// refreshes; deltas arrive far faster than any label here needs
        /// to update, and two of the refreshes touch the disk.</summary>
        private const long HubRefreshIntervalMillis = 250;

        /// <summary>Floor between UloopDetector manifest scans on the
        /// coalesced tick (2Hz, matching _uloopProgressLastEvalAt).</summary>
        private const double UloopDetectMinIntervalSeconds = 0.5;

        private IVisualElementScheduledItem _hubRefreshLoop;
        private bool _hubDirty;

        /// <summary>Manual path RefreshCliStatus last resolved for; null =
        /// never resolved (the gate's "must resolve" sentinel).</summary>
        private string _cliStatusResolvedForManualPath;

        private double _uloopDetectLastEvalAt;

        /// <summary>
        /// The plan the currently-open confirmation card is showing --
        /// exactly what "Apply" acts on (never a freshly-recomputed plan,
        /// per UloopInstaller.Apply's own doc comment: a plan computed
        /// against a stale snapshot must not be silently re-validated
        /// against a since-changed disk state). Null whenever the card is
        /// closed.
        /// </summary>
        private UloopInstallPlan _pendingUloopPlan;

        private Label _uloopPresetAppliedLabel;
        private Label _uloopSnippetAppliedLabel;

        // -- Unity official plugin (design note 2026-09-10 section 2) -----------
        private Label _unityPluginStatusLabel;
        private Label _unityPluginProgressLabel;
        private Button _unityPluginInstallButton;
        private VisualElement _unityPluginInstallConfirmCard;
        private Button _unityPluginInstallApplyButton;
        private Toggle _unityPluginSteeringToggle;
        /// <summary>True from Apply until the worker's completion callback lands (lost on domain reload; SessionState carries the flag across).</summary>
        private bool _unityPluginInstallRunning;
        /// <summary>The last completion this view saw (null until one lands, and after a reload).</summary>
        private UnityPluginInstallResult _unityPluginLastResult;
        /// <summary>Elapsed-seconds counter while a run is in flight (500ms cadence, paused otherwise).</summary>
        private IVisualElementScheduledItem _unityPluginProgressTick;
        private double _unityPluginDetectLastEvalAt;
        private const double UnityPluginDetectMinIntervalSeconds = 0.5;

        private TextField _stderrField;

        private Label _cliVersionLabel;

        // -- Account (docs/design-notes/2026-08-02-auth-in-panel.md) --------------

        private VisualElement _accountSectionRoot;
        private Label _accountStatusLabel;
        private Label _accountEnvTokenNoteLabel;
        private Label _accountApiKeyAuthNoteLabel;
        private Label _accountAcpAuthMethodLabel;
        private PopupField<ClaudeAuthMode> _claudeAuthField;
        private Button _accountLoginButton;
        private Button _accountLogoutButton;
        private VisualElement _accountLoginSubCard;
        private Label _accountAcpHintLabel;
        private Button _accountAcpSignInButton;
        private Button _accountAcpLoginButton;
        private Button _accountAcpCancelLoginButton;
        private Button _accountAcpCopyUrlButton;
        private Label _accountAcpLoginOutputLabel;
        private TextField _accountAcpUrlField;
        private Button _accountAcpOpenBrowserButton;
        private Button _cliInstallButton;
        private Button _cliNodeButton;
        private Label _cliInstallStatusLabel;
        private IVisualElementScheduledItem _cliInstallTick;
        private bool _cliResolvedNow;
        private bool _cliInstallLastSeenRunning;
        private Label _accountLoginInstructionLabel;
        private TextField _accountLoginUrlField;
        private Button _accountOpenBrowserButton;
        private Button _accountCopyUrlButton;
        private TextField _accountCodeField;
        private Button _accountSubmitCodeButton;
        private Button _accountCancelLoginButton;
        private Label _accountLoginFailedLabel;

        // -- UXO-6 login-feedback tracking (see ResolveAccountLoginFeedback).
        // All view-local: AgentHub owns the session; this only remembers
        // enough across Changed ticks to say "verifying" while the code is
        // in flight and "failed" once the post-exit status query lands.
        private bool _loginCodeSubmitted;
        private bool _loginWasInFlight;
        private bool _loginAwaitingVerdict;
        private bool _suppressLoginVerdict;
        private AuthStatus _loginVerdictBaseline;
        private bool _loginFailedNoticeVisible;

        public string Title
        {
            get { return L10n.S.SettingsTitle; }
        }

        public VisualElement Root
        {
            get { return _root; }
        }

        // -- IAgentPanelView ------------------------------------------------------

        public VisualElement CreateGUI()
        {
            var root = new VisualElement();
            root.AddToClassList("uap-settings");
            _root = root;

            BuildReconnectBanner(root);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("uap-settings-scroll");
            root.Add(scroll);
            _scroll = scroll;

            var title = new Label(L10n.S.SettingsTitle);
            title.AddToClassList("uap-settings-title");
            title.enableRichText = false;
            scroll.Add(title);

            // Section order (2026-08-14 ui-polish audit item 8): Conversation
            // and Model lead -- they are what a user opening Settings is
            // almost always here to change (permission mode, tool lists,
            // which model runs). CLI moved DOWN out of the lead slot to sit
            // beside Appearance/Diagnostics near the bottom: it is
            // connection plumbing/troubleshooting, read rarely and mostly
            // when something is already wrong, which is exactly the company
            // the read-only stderr tail (Diagnostics) and the uLoop
            // installer keep. Every other section's relative order is
            // unchanged from before this pass.
            // 2026-09-05 UI redesign (settings, S1): four topic groups.
            // Conversation and Model keep the lead slots the 2026-08-14
            // pass gave them (SettingsViewSectionIconTests' card indices
            // still hold -- group labels are not cards). Everything a
            // user changes while WORKING sits in the first group; how the
            // panel looks and when it beeps in the second; the Unity-side
            // machinery in the third; plumbing, entitlement and identity
            // last.
            AddGroupLabel(scroll, L10n.S.SettingsGroupConversation, true);
            BuildConversationSection(scroll);
            BuildModelSection(scroll);
            BuildCustomInstructionsSection(scroll);
            BuildQuickActionsSection(scroll);

            AddGroupLabel(scroll, L10n.S.SettingsGroupDisplay, false);
            BuildDisplaySection(scroll);
            BuildAppearanceSection(scroll);
            BuildNotificationsSection(scroll);
            BuildConsoleErrorsSection(scroll);

            AddGroupLabel(scroll, L10n.S.SettingsGroupUnity, false);
            BuildUapOpsSection(scroll);
            BuildExtensionProfilesSection(scroll);
            BuildUloopSection(scroll);
            BuildUnityPluginSection(scroll);

            AddGroupLabel(scroll, L10n.S.SettingsGroupConnection, false);
            BuildCliSection(scroll);
            BuildDiagnosticsSection(scroll);
            BuildAccountSection(scroll);
            // 2026-09-15 user feedback: "Agent Panel Pro updates" sat in
            // the Unity group, which is where the Unity-side MACHINERY
            // lives (what the agent may touch in the editor, which SDKs it
            // knows about, which helper CLIs are installed). This card is
            // none of that: it takes the registry URL and product key a
            // purchase came with and writes them to the package manager's
            // credentials -- an entitlement, read at install/upgrade time,
            // exactly the "connection and account" subject the sign-in card
            // above it covers. It also went unfound where it was, which a
            // card a buyer has to reach ONCE, right after paying, cannot
            // afford. Sits after Account (same subject, in the order a new
            // buyer meets them) and before About.
            BuildProUpdatesSection(scroll);
            BuildAboutSection(scroll);

            // 2026-09-06 settings review: long tooltips get a visible ? mark
            // (HelpAffordance) so the third copy layer is discoverable.
            HelpAffordance.ApplyMarks(scroll);

            RefreshAll();
            return root;
        }

        /// <summary>
        /// One small secondary-toned heading between card groups. `first`
        /// drops the top margin so the first heading sits flush under the
        /// page title instead of opening a gap there.
        /// </summary>
        private static void AddGroupLabel(VisualElement parent, string text, bool first)
        {
            var label = new Label(text);
            label.AddToClassList("uap-settings-group");
            if (first)
            {
                label.AddToClassList("uap-settings-group--first");
            }
            label.enableRichText = false;
            parent.Add(label);
        }

        /// <summary>
        /// The reconnect-pending notice, above the scroll (S2). Hidden
        /// unless SettingsChangeDetector says a spawned-with setting has
        /// changed; then it shows the sentence, the pill (a second
        /// encoding of the same state, never colour alone) and the one
        /// action that resolves it. RefreshReconnectHint drives every
        /// field here exactly as it drove the old in-card row.
        /// </summary>
        private void BuildReconnectBanner(VisualElement parent)
        {
            _reconnectBanner = new VisualElement();
            _reconnectBanner.AddToClassList("uap-settings-banner");
            _reconnectBanner.style.display = DisplayStyle.None;

            _reconnectHintRow = new VisualElement();
            _reconnectHintRow.AddToClassList("uap-settings-hint-row");
            _reconnectHintRow.AddToClassList("uap-settings-banner-text");
            _reconnectHintLabel = new Label(string.Empty);
            _reconnectHintLabel.AddToClassList("uap-settings-hint");
            _reconnectHintLabel.AddToClassList("uap-settings-hint--pending");
            _reconnectHintLabel.enableRichText = false;
            _reconnectHintRow.Add(_reconnectHintLabel);
            _reconnectPendingPillLabel = new Label(L10n.S.SettingsReconnectPendingPill);
            _reconnectPendingPillLabel.AddToClassList("uap-pill");
            _reconnectPendingPillLabel.AddToClassList("uap-pill--warn");
            _reconnectPendingPillLabel.enableRichText = false;
            _reconnectHintRow.Add(_reconnectPendingPillLabel);
            _reconnectBanner.Add(_reconnectHintRow);

            var reconnect = new Button(OnReconnectClicked) { text = L10n.S.SettingsReconnectNowButton };
            reconnect.AddToClassList("uap-settings-btn");
            reconnect.AddToClassList("uap-settings-btn--primary");
            reconnect.AddToClassList("uap-settings-banner-btn");
            _reconnectBanner.Add(reconnect);

            parent.Add(_reconnectBanner);
        }

        public void OnActivate()
        {
            if (_active)
            {
                return;
            }
            _active = true;
            AgentHub.Changed += OnHubChanged;
            // UICODE-4: hub-driven refreshes coalesce on this loop (house
            // pattern shared with ChatView/HistoryView) instead of running
            // synchronously per Changed raise -- which fires per streaming
            // delta and was doing disk IO (CLI path stat + manifest scan)
            // on every one while the Settings tab was visible.
            _hubRefreshLoop = _root.schedule.Execute(RefreshIfHubDirty)
                .Every(HubRefreshIntervalMillis);
            RefreshAll();
        }

        public void OnDeactivate()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            AgentHub.Changed -= OnHubChanged;
            if (_hubRefreshLoop != null)
            {
                _hubRefreshLoop.Pause();
                _hubRefreshLoop = null;
            }
            // A deactivated (hidden) settings tab must not keep an
            // EditorApplication.update poll alive just to update a label
            // nobody can see. The SessionState in-flight pair keeps the
            // truth; OnActivate's RefreshAll -> RefreshUloopSection
            // re-evaluates and re-hooks if the install is still in flight.
            StopUnityPluginProgressTick();
            StopUloopProgressTick();
        }

        public void SerializeState()
        {
            // Every field writes through PanelStateStore.SaveNow() the
            // moment it changes (see each On*Changed handler below); there
            // is no additional transient UI state (scroll position aside,
            // which ScrollView itself does not expose for persistence and
            // which is not worth wiring for a view this short) to capture
            // here.
        }

        private void OnHubChanged()
        {
            // UICODE-4: mark only. AgentHub.Changed fires per streaming
            // delta; the actual refresh work (including the disk-touching
            // probes, both further gated below) runs at most once per
            // HubRefreshIntervalMillis on the coalescing loop.
            _hubDirty = true;
        }

        private void RefreshIfHubDirty()
        {
            if (!_hubDirty)
            {
                return;
            }
            _hubDirty = false;
            RefreshDiagnostics();
            RefreshCliStatusIfPathChanged();
            if (_cliInstallLastSeenRunning != AgentHub.CliInstallRunning)
            {
                // An install just started or finished: the resolve result
                // can change without the path setting changing.
                _cliInstallLastSeenRunning = AgentHub.CliInstallRunning;
                RefreshCliStatus();
            }
            else
            {
                RefreshCliInstallState();
            }
            RefreshReconnectHint();
            RefreshCliVersionLabel();
            RefreshModelSection();
            RefreshAgentOverrideRowsIfCatalogChanged();
            RefreshAccountSection();
            RefreshUapOpsStatus();
            RefreshUloopSectionThrottled();
            RefreshUnityPluginSectionThrottled();
        }

        /// <summary>
        /// The coalesced-tick guard in front of RefreshCliStatus's disk
        /// stat: the resolve result can only change when the manual path
        /// setting does (or on the explicit Re-detect/reconnect actions,
        /// which call RefreshCliStatus directly and unconditionally).
        /// </summary>
        private void RefreshCliStatusIfPathChanged()
        {
            string current = ProbeKey(PanelStateStore.instance.Settings);
            if (!ShouldResolveCliStatus(_cliStatusResolvedForManualPath, current))
            {
                return;
            }
            RefreshCliStatus();
        }

        /// <summary>
        /// Pure gate (UICODE-4, testable directly): resolve when nothing
        /// was ever resolved (null sentinel) or the manual path changed.
        /// </summary>
        internal static bool ShouldResolveCliStatus(
            string lastResolvedForPath, string currentManualPath)
        {
            return lastResolvedForPath == null
                || !string.Equals(lastResolvedForPath, currentManualPath ?? string.Empty,
                    System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The coalesced-tick guard in front of UloopDetector's
        /// manifest.json read: the manifest is effectively immutable during
        /// a turn, so the same 2Hz floor the install-progress tick already
        /// uses (_uloopProgressLastEvalAt precedent) bounds this scan too.
        /// Explicit actions (install click, RefreshAll) keep calling
        /// RefreshUloopSection directly.
        /// </summary>
        private void RefreshUloopSectionThrottled()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _uloopDetectLastEvalAt < UloopDetectMinIntervalSeconds)
            {
                return;
            }
            _uloopDetectLastEvalAt = now;
            RefreshUloopSection();
        }

        // -- (a) CLI ---------------------------------------------------------------

        private void BuildCliSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsSectionCli,
                "d_UnityEditor.ConsoleWindow", "$", "cli");

            PanelSettings settings = PanelStateStore.instance.Settings;

            // Backend picker (design note 2026-09-10-acp-backends.md section
            // 4): Claude Code keeps its executable-path field; every ACP
            // backend shows command/arguments/auth-method fields instead.
            var backendChoices = new List<AgentBackend>
            {
                AgentBackend.ClaudeCode,
                AgentBackend.GeminiCli,
                AgentBackend.CodexAcp,
                AgentBackend.GrokBuild,
                AgentBackend.AcpCustom
            };
            _backendField = new PopupField<AgentBackend>(L10n.S.SettingsBackendLabel,
                backendChoices, settings.agentBackend, FormatBackendOption, FormatBackendOption);
            _backendField.AddToClassList("uap-settings-field");
            _backendField.RegisterValueChangedCallback(OnBackendChanged);
            VisualElement backendScope = AddHintScope(section);
            backendScope.Add(_backendField);
            backendScope.tooltip = L10n.S.SettingsBackendTooltip;

            _claudeCliGroup = new VisualElement();
            section.Add(_claudeCliGroup);
            _cliPathField = new TextField(L10n.S.SettingsCliPathLabel);
            _cliPathField.AddToClassList("uap-settings-field");
            _cliPathField.SetValueWithoutNotify(settings.cliManualPath);
            _cliPathField.RegisterValueChangedCallback(OnCliPathChanged);
            VisualElement cliPathScope = AddHintScope(_claudeCliGroup);
            cliPathScope.Add(_cliPathField);
            AddHint(cliPathScope, L10n.S.SettingsCliPathHint, L10n.S.SettingsCliPathTooltip);

            _acpCliGroup = new VisualElement();
            section.Add(_acpCliGroup);
            _acpCommandField = new TextField(L10n.S.SettingsAcpCommandLabel);
            _acpCommandField.AddToClassList("uap-settings-field");
            _acpCommandField.SetValueWithoutNotify(settings.acpCommand ?? string.Empty);
            _acpCommandField.RegisterValueChangedCallback(OnAcpCommandChanged);
            VisualElement acpCommandScope = AddHintScope(_acpCliGroup);
            acpCommandScope.Add(_acpCommandField);
            _acpCommandHintLabel = AddHint(acpCommandScope, string.Empty);
            _acpArgumentsField = new TextField(L10n.S.SettingsAcpArgumentsLabel);
            _acpArgumentsField.AddToClassList("uap-settings-field");
            _acpArgumentsField.SetValueWithoutNotify(settings.acpArguments ?? string.Empty);
            _acpArgumentsField.RegisterValueChangedCallback(OnAcpArgumentsChanged);
            _acpCliGroup.Add(_acpArgumentsField);
            _acpAuthMethodField = new TextField(L10n.S.SettingsAcpAuthMethodLabel);
            _acpAuthMethodField.AddToClassList("uap-settings-field");
            _acpAuthMethodField.SetValueWithoutNotify(settings.acpAuthMethod ?? string.Empty);
            _acpAuthMethodField.RegisterValueChangedCallback(OnAcpAuthMethodChanged);
            VisualElement acpAuthScope = AddHintScope(_acpCliGroup);
            acpAuthScope.Add(_acpAuthMethodField);
            AddHint(acpAuthScope, L10n.S.SettingsAcpAuthMethodHint, L10n.S.SettingsAcpAuthMethodTooltip);
            _acpLoginHintLabel = AddHint(_acpCliGroup, string.Empty);
            _acpLoginHintLabel.AddToClassList("uap-settings-hint--pending");
            AddHint(_acpCliGroup, L10n.S.SettingsAcpLimitationsHint, L10n.S.SettingsAcpLimitationsTooltip);
            RefreshBackendGroups();

            VisualElement row = AddRow(section);
            var redetect = new Button(OnRedetectClicked) { text = L10n.S.SettingsRedetectButton };
            redetect.AddToClassList("uap-settings-btn");
            row.Add(redetect);
            var reconnect = new Button(OnReconnectClicked) { text = L10n.S.SettingsReconnectNowButton };
            reconnect.AddToClassList("uap-settings-btn");
            row.Add(reconnect);

            _cliResolvedLabel = new Label(string.Empty);
            _cliResolvedLabel.AddToClassList("uap-settings-hint");
            _cliResolvedLabel.AddToClassList("uap-settings-status");
            MessageBlockFactory.ApplyMonoFont(_cliResolvedLabel);
            _cliResolvedLabel.enableRichText = false;
            section.Add(_cliResolvedLabel);

            // In-panel install (design note 2026-09-10-in-panel-install-and-
            // sign-in.md section 1): shown only while the command does not
            // resolve; the same CliInstaller run the first-run card starts.
            VisualElement installRow = AddRow(section);
            _cliInstallButton = new Button(OnCliInstallClicked) { text = string.Empty };
            _cliInstallButton.AddToClassList("uap-settings-btn");
            _cliInstallButton.AddToClassList("uap-settings-btn--primary");
            installRow.Add(_cliInstallButton);
            _cliNodeButton = new Button(OnCliGetNodeClicked) { text = L10n.S.InstallOpenNodeButton };
            _cliNodeButton.AddToClassList("uap-settings-btn");
            _cliNodeButton.style.display = DisplayStyle.None;
            installRow.Add(_cliNodeButton);
            _cliInstallStatusLabel = AddHint(section, string.Empty);
            _cliInstallStatusLabel.AddToClassList("uap-settings-hint--pending");
            _cliInstallStatusLabel.style.display = DisplayStyle.None;
            // The reconnect-pending notice used to live here, inside a
            // card that is collapsed by default. It is now the banner
            // above the scroll (BuildReconnectBanner) so it is visible
            // from whichever card the change was made in.
        }

        private void OnCliPathChanged(ChangeEvent<string> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.cliManualPath = evt.newValue ?? string.Empty;
            PanelStateStore.instance.SaveNow();
            RefreshCliStatus();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnRedetectClicked()
        {
            RefreshCliStatus();
        }

        private static string FormatBackendOption(AgentBackend backend)
        {
            return backend == AgentBackend.AcpCustom
                ? L10n.S.SettingsBackendOptionCustom
                : AgentBackends.DisplayName(backend);
        }

        private void OnBackendChanged(ChangeEvent<AgentBackend> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.agentBackend = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshBackendGroups();
            RefreshCliStatus();
            RefreshReconnectHint();
            RefreshAccountSection();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnAcpCommandChanged(ChangeEvent<string> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.acpCommand = evt.newValue ?? string.Empty;
            PanelStateStore.instance.SaveNow();
            RefreshBackendGroups();
            RefreshCliStatus();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnAcpArgumentsChanged(ChangeEvent<string> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.acpArguments = evt.newValue ?? string.Empty;
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnAcpAuthMethodChanged(ChangeEvent<string> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.acpAuthMethod = evt.newValue ?? string.Empty;
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        /// <summary>Shows the field group of the selected backend and refreshes its default-command hint.</summary>
        private void RefreshBackendGroups()
        {
            if (_claudeCliGroup == null || _acpCliGroup == null)
            {
                return;
            }
            PanelSettings settings = PanelStateStore.instance.Settings;
            bool acp = AgentBackends.IsAcp(settings.agentBackend);
            _claudeCliGroup.style.display = acp ? DisplayStyle.None : DisplayStyle.Flex;
            _acpCliGroup.style.display = acp ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshSubagentModelAcpHint();
            if (!acp)
            {
                return;
            }
            _acpCommandHintLabel.text = BuildAcpCommandHint(settings.agentBackend);
            string authHint = L10n.AcpAuthHint(settings.agentBackend, L10n.S.SettingsAcpLoginHintFmt);
            _acpLoginHintLabel.text = authHint;
            // The label's own tooltip wins over any set on an ancestor, so
            // this stays scoped to the sign-in line it explains.
            _acpLoginHintLabel.tooltip =
                L10n.AcpAuthTooltip(settings.agentBackend, L10n.S.SettingsAcpLoginHintFmt);
            _acpLoginHintLabel.style.display = string.IsNullOrEmpty(authHint)
                ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// The subagent model fields apply to every backend, but an ACP
        /// agent receives them as instructions at the start of a new
        /// session rather than through Claude's env var / agent files --
        /// say so right under the field, only while such a backend is
        /// selected.
        /// </summary>
        private void RefreshSubagentModelAcpHint()
        {
            if (_subagentModelAcpHintLabel == null)
            {
                return;
            }
            PanelSettings settings = PanelStateStore.instance.Settings;
            bool acp = AgentBackends.IsAcp(settings.agentBackend);
            _subagentModelAcpHintLabel.style.display = acp ? DisplayStyle.Flex : DisplayStyle.None;
            if (acp)
            {
                _subagentModelAcpHintLabel.text = L10n.F(L10n.S.SettingsSubagentModelAcpHintFmt,
                    AgentBackends.DisplayName(settings.agentBackend));
            }
        }

        /// <summary>Pure: the hint under the ACP command field for a backend (tested directly).</summary>
        internal static string BuildAcpCommandHint(AgentBackend backend)
        {
            string command = AgentBackends.DefaultCommand(backend);
            if (string.IsNullOrEmpty(command))
            {
                return L10n.S.SettingsAcpCommandHintCustom;
            }
            string arguments = AgentBackends.DefaultArguments(backend);
            string launch = string.IsNullOrEmpty(arguments) ? command : command + " " + arguments;
            return L10n.F(L10n.S.SettingsAcpCommandHintFmt, launch);
        }

        private void OnReconnectClicked()
        {
            AgentHub.Reconnect();
            RefreshCliStatus();
            RefreshReconnectHint();
        }

        /// <summary>
        /// Builds the platform-appropriate path probe for the CURRENT
        /// manual-path field value (not necessarily the one the live client
        /// was spawned with), shared by <see cref="RefreshCliStatus"/> and
        /// the About row's binary-version cache lookup so both always agree
        /// on what "the resolved CLI path" means right now.
        /// </summary>
        private static ICliPathProbe CreateCliPathProbe()
        {
            return AgentHub.CreateCliPathProbe(PanelStateStore.instance.Settings);
        }

        /// <summary>
        /// The value whose change invalidates the resolved CLI status: the
        /// manual path for Claude Code, the backend plus effective command
        /// for an ACP agent (design note 2026-09-10-acp-backends.md section 4).
        /// </summary>
        internal static string ProbeKey(PanelSettings settings)
        {
            if (AgentBackends.IsAcp(settings.agentBackend))
            {
                return settings.agentBackend + "|"
                    + AgentBackends.EffectiveCommand(settings.agentBackend, settings.acpCommand);
            }
            return settings.cliManualPath ?? string.Empty;
        }

        /// <summary>
        /// Resolves the CLI path with the CURRENT manual-path field value
        /// so "Re-detect" always reflects what the NEXT spawn would resolve
        /// to, purely for diagnostics -- it never touches the live client.
        /// Also (re)triggers the protocol-independent binary version probe
        /// (docs/design-notes/2026-08-01-cli-binary-version-probe.md) for
        /// the resolved path; <see cref="CliVersionProbe.BeginProbe"/> is
        /// itself a no-op once that exact path has already been attempted,
        /// so calling this from every `AgentHub.Changed` refresh (this
        /// method's other call site, `OnHubChanged`) never spawns more than
        /// one `--version` subprocess per distinct resolved path.
        /// </summary>
        private void RefreshCliStatus()
        {
            if (_cliResolvedLabel == null)
            {
                return;
            }
            _cliStatusResolvedForManualPath = ProbeKey(PanelStateStore.instance.Settings);
            ICliPathProbe probe = CreateCliPathProbe();
            string resolved = probe.Resolve();
            _cliResolvedLabel.text = !string.IsNullOrEmpty(resolved)
                ? L10n.F(L10n.S.SettingsCliResolvedFmt, resolved)
                : L10n.F(L10n.S.SettingsCliNotFoundFmt, string.Join("; ", probe.DescribeCandidates()));
            if (!string.IsNullOrEmpty(resolved))
            {
                CliVersionProbe.BeginProbe(resolved, delegate(string version)
                {
                    OnCliBinaryVersionProbed(resolved, version);
                });
            }
            _cliResolvedNow = !string.IsNullOrEmpty(resolved);
            RefreshCliInstallState();
        }

        private void RefreshCliInstallState()
        {
            if (_cliInstallButton == null)
            {
                return;
            }
            AgentBackend backend = PanelStateStore.instance.Settings.agentBackend;
            string name = AgentBackends.DisplayName(backend);
            bool installable = CliInstallPlan.DisplayCommandFor(backend,
                Application.platform == RuntimePlatform.WindowsEditor).Length > 0;
            bool running = AgentHub.CliInstallRunning;
            CliInstallResult last = AgentHub.LastCliInstallResult;
            _cliInstallButton.text = L10n.F(L10n.S.InstallButtonFmt, name);
            _cliInstallButton.style.display = installable && (!_cliResolvedNow || running)
                ? DisplayStyle.Flex : DisplayStyle.None;
            _cliInstallButton.SetEnabled(!running);
            string text = FirstRunView.DescribeInstallState(running, last, name,
                AgentHub.CliInstallStartedUtcTicks, System.DateTime.UtcNow.Ticks);
            _cliInstallStatusLabel.text = text ?? string.Empty;
            _cliInstallStatusLabel.style.display = text == null ? DisplayStyle.None : DisplayStyle.Flex;
            _cliNodeButton.style.display = !running && last != null
                && last.Failure == CliInstallFailureKind.NodeMissing
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (running)
            {
                if (_cliInstallTick == null && _root != null)
                {
                    _cliInstallTick = _root.schedule.Execute(RefreshCliInstallState).Every(500);
                }
                else if (_cliInstallTick != null)
                {
                    _cliInstallTick.Resume();
                }
            }
            else if (_cliInstallTick != null)
            {
                _cliInstallTick.Pause();
            }
        }

        private void OnCliInstallClicked()
        {
            AgentBackend backend = PanelStateStore.instance.Settings.agentBackend;
            string command = CliInstallPlan.DisplayCommandFor(backend,
                Application.platform == RuntimePlatform.WindowsEditor);
            if (command.Length == 0)
            {
                return;
            }
            bool confirmed = EditorUtility.DisplayDialog(
                L10n.F(L10n.S.InstallConfirmTitleFmt, AgentBackends.DisplayName(backend)),
                L10n.F(L10n.S.InstallConfirmBodyFmt, command),
                L10n.S.InstallConfirmButton,
                L10n.S.InstallCancelButton);
            if (!confirmed)
            {
                return;
            }
            AgentHub.BeginCliInstall();
            RefreshCliInstallState();
        }

        private static void OnCliGetNodeClicked()
        {
            Application.OpenURL(CliInstallPlan.NodeDownloadUrl);
        }

        /// <summary>
        /// Caches a successful binary-version probe result (keyed by the
        /// exact path it was probed for) and refreshes the About row so a
        /// probe landing after the initial paint still updates the label.
        /// An empty <paramref name="version"/> (probe failed/timed out)
        /// intentionally leaves any previously cached value untouched
        /// rather than clobbering it with nothing.
        /// </summary>
        private void OnCliBinaryVersionProbed(string path, string version)
        {
            if (!string.IsNullOrEmpty(version))
            {
                SessionStateBridge.CliBinaryVersion = version;
                SessionStateBridge.CliBinaryVersionPath = path;
            }
            RefreshCliVersionLabel();
        }

        /// <summary>
        /// Shows/hides the "some changes need Reconnect now" hint by
        /// comparing the last-spawned snapshot against the current
        /// settings (SettingsChangeDetector) -- covers the manual CLI
        /// path, allowed/disallowedTools, dangerouslySkipPermissions and
        /// showThinking (docs/design-notes/2026-08-01-thinking-content-
        /// loss.md section 7), the fields that only take effect at the
        /// next CLI spawn.
        /// </summary>
        private void RefreshReconnectHint()
        {
            if (_reconnectHintLabel == null || _reconnectHintRow == null)
            {
                return;
            }
            // The Custom instructions field is not backed by PanelSettings
            // (see CustomInstructionsFile's doc comment), so it is compared
            // through the 4-arg overload; the live TextField value (when
            // built) is always the "current" side -- it is saved to disk
            // on every keystroke, but reading the disk copy again here
            // would be redundant.
            string currentCustomInstructions = _customInstructionsField != null
                ? _customInstructionsField.value
                : LoadCustomInstructions();
            bool pending = SettingsChangeDetector.RequiresReconnect(
                AgentHub.LastSpawnedSettingsSnapshot, PanelStateStore.instance.Settings,
                AgentHub.LastSpawnedCustomInstructions, currentCustomInstructions);
            // Settings auto-apply (docs/design-notes/2026-08-01-settings-
            // auto-apply.md): while a change is deferred waiting for the
            // current turn to end, the pill/hint switch to a variant that
            // says so instead of implying a manual reconnect is required.
            bool deferred = pending && AgentHub.IsAutoApplyDeferred;
            _reconnectHintLabel.text = pending
                ? (deferred ? L10n.S.SettingsReconnectPendingHintDeferred : L10n.S.SettingsReconnectPendingHint)
                : string.Empty;
            _reconnectHintRow.style.display = pending ? DisplayStyle.Flex : DisplayStyle.None;
            if (_reconnectBanner != null)
            {
                _reconnectBanner.style.display = pending ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_reconnectPendingPillLabel != null)
            {
                _reconnectPendingPillLabel.text = deferred
                    ? L10n.S.SettingsReconnectPendingPillDeferred
                    : L10n.S.SettingsReconnectPendingPill;
            }
        }

        // -- (b) Conversation --------------------------------------------------------

        private void BuildConversationSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionConversation,
                "d_Profiler.NetworkMessages", IconLoader.GlyphGear);

            var modeChoices = new List<PermissionModeOption>
            {
                PermissionModeOption.Default,
                PermissionModeOption.Plan,
                PermissionModeOption.AcceptEdits
            };
            PermissionModeOption current = PermissionModeMapping.FromCliValue(
                PanelStateStore.instance.Settings.permissionMode);
            _permissionModeField = new PopupField<PermissionModeOption>(L10n.S.SettingsPermissionModeLabel,
                modeChoices, current, PermissionModeLabels.Describe,
                PermissionModeLabels.Describe);
            _permissionModeField.AddToClassList("uap-settings-field");
            _permissionModeField.RegisterValueChangedCallback(OnPermissionModeChanged);
            // The CONTROL goes inside the scope, not just the hint label. For
            // these fields the inline text is now empty -- the apply-timing
            // sentence moved to the tooltip -- so a scope wrapping only the
            // label would be a zero-height element nobody can hover.
            VisualElement permissionModeScope = AddHintScope(section);
            permissionModeScope.Add(_permissionModeField);
            permissionModeScope.tooltip = L10n.A(L10n.S.SettingsPermissionModeTooltip);

            // UXIA-3/4: the auto-approve level is permission policy, so it
            // lives HERE, directly under the permission mode -- not at the
            // bottom of the UapOps module section where it was buried. The
            // danger-zone foldout follows so the three same-axis controls
            // (when to ask / what auto-approves / skip everything) sit
            // adjacent and their interplay is readable in one place.
            BuildAutoApproveLevelField(section);
            BuildDangerZone(section);

            _ctrlEnterToggle = new Toggle(L10n.S.SettingsCtrlEnterLabel);
            _ctrlEnterToggle.AddToClassList("uap-settings-field");
            _ctrlEnterToggle.AddToClassList("uap-switch");
            _ctrlEnterToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.ctrlEnterToSend);
            _ctrlEnterToggle.RegisterValueChangedCallback(OnCtrlEnterChanged);
            VisualElement ctrlEnterScope = AddHintScope(section);
            ctrlEnterScope.Add(_ctrlEnterToggle);
            AddHint(ctrlEnterScope, L10n.S.SettingsCtrlEnterHint, L10n.S.SettingsCtrlEnterTooltip);

            _allowedToolsField = new TextField(L10n.S.SettingsAllowedToolsLabel);
            _allowedToolsField.multiline = true;
            _allowedToolsField.AddToClassList("uap-settings-multiline");
            _allowedToolsField.SetValueWithoutNotify(
                JoinLines(PanelStateStore.instance.Settings.allowedTools));
            _allowedToolsField.RegisterValueChangedCallback(OnAllowedToolsChanged);
            VisualElement allowedToolsScope = AddHintScope(section);
            // Marks the start of the allow/deny tool-list block as its own
            // visual subgroup within the Conversation card (top border +
            // spacing) -- 2026-08-14 ui-polish audit item 8. Only the
            // FIRST element of the block (this one) carries the class; the
            // disallowed-tools scope right after it is part of the same
            // subgroup, not a second one.
            allowedToolsScope.AddToClassList("uap-settings-subgroup-start");
            allowedToolsScope.Add(_allowedToolsField);
            AddHint(allowedToolsScope, L10n.S.SettingsAllowedToolsHint,
                L10n.S.SettingsAllowedToolsTooltip);

            _disallowedToolsField = new TextField(L10n.S.SettingsDisallowedToolsLabel);
            _disallowedToolsField.multiline = true;
            _disallowedToolsField.AddToClassList("uap-settings-multiline");
            _disallowedToolsField.SetValueWithoutNotify(
                JoinLines(PanelStateStore.instance.Settings.disallowedTools));
            _disallowedToolsField.RegisterValueChangedCallback(OnDisallowedToolsChanged);
            VisualElement disallowedToolsScope = AddHintScope(section);
            disallowedToolsScope.Add(_disallowedToolsField);
            AddHint(disallowedToolsScope, L10n.S.SettingsDisallowedToolsHint,
                L10n.S.SettingsDisallowedToolsTooltip);

        }

        /// <summary>
        /// The auto-approve level field (UXIA-3/4), extracted so exactly
        /// ONE construction exists wherever the field lives. Everything
        /// about it is unchanged from its UapOps-section days: purely
        /// live, no reconnect (deliberately absent from
        /// SettingsChangeDetector.RequiresReconnect), the SAME
        /// AutoApproveLevelLabels.Ordered choices and Describe() formatter
        /// the header chip uses so the two surfaces can never word this
        /// setting differently, warning-styled help, and
        /// OnAutoApproveLevelChanged's escalation confirm (UXA-3).
        /// Internal so SettingsViewLogicTests can pin choices/formatter.
        /// </summary>
        internal PopupField<UapAutoApproveLevel> BuildAutoApproveLevelField(VisualElement parent)
        {
            var autoApproveLevelChoices = new List<UapAutoApproveLevel>(AutoApproveLevelLabels.Ordered);
            _autoApproveLevelField = new PopupField<UapAutoApproveLevel>(
                L10n.S.SettingsAutoApproveLabel, autoApproveLevelChoices,
                PanelStateStore.instance.Settings.autoApproveLevel,
                AutoApproveLevelLabels.Describe, AutoApproveLevelLabels.Describe);
            _autoApproveLevelField.AddToClassList("uap-settings-field");
            _autoApproveLevelField.RegisterValueChangedCallback(OnAutoApproveLevelChanged);
            VisualElement autoApproveScope = AddHintScope(parent);
            autoApproveScope.Add(_autoApproveLevelField);
            _autoApproveWarningLabel = AddWarning(autoApproveScope, L10n.S.SettingsAutoApproveHelp,
                L10n.S.SettingsAutoApproveTooltip);
            return _autoApproveLevelField;
        }

        private void BuildDangerZone(VisualElement section)
        {
            var foldout = new Foldout { text = L10n.S.SettingsDangerZoneTitle, value = false };
            foldout.AddToClassList("uap-settings-danger-foldout");
            // Warn icon stays visible in the header even while the Foldout
            // is collapsed, so "this is dangerous" does not require opening
            // it first to see (design-notes/2026-08-01-settings-visual-
            // refresh.md phase A).
            AddFoldoutHeaderIcon(foldout, "d_console.warnicon.sml", IconLoader.GlyphWarn);

            var warning = new HelpBox(L10n.A(L10n.S.SettingsDangerZoneWarning), HelpBoxMessageType.Warning);
            warning.AddToClassList("uap-settings-helpbox");
            foldout.Add(warning);

            _dangerousToggle = new Toggle(L10n.S.SettingsDangerZoneToggle);
            _dangerousToggle.AddToClassList("uap-settings-field");
            _dangerousToggle.AddToClassList("uap-switch");
            _dangerousToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.dangerouslySkipPermissions);
            _dangerousToggle.RegisterValueChangedCallback(OnDangerousToggleChanged);
            VisualElement dangerScope = AddHintScope(foldout);
            dangerScope.Add(_dangerousToggle);
            dangerScope.tooltip = L10n.S.SettingsDangerZoneTooltip;

            section.Add(foldout);
        }

        private void OnPermissionModeChanged(ChangeEvent<PermissionModeOption> evt)
        {
            string cliValue = PermissionModeMapping.ToCliValue(evt.newValue);
            PanelStateStore.instance.Settings.permissionMode = cliValue;
            PanelStateStore.instance.SaveNow();
            AgentClient client = AgentHub.Client;
            if (client != null
                && client.State != AgentClientState.NotStarted
                && client.State != AgentClientState.Errored)
            {
                client.SetPermissionMode(cliValue);
            }
        }

        private void OnCtrlEnterChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.ctrlEnterToSend = evt.newValue;
            PanelStateStore.instance.SaveNow();
        }

        private void OnAllowedToolsChanged(ChangeEvent<string> evt)
        {
            PanelStateStore.instance.Settings.allowedTools = SplitLines(evt.newValue);
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnDisallowedToolsChanged(ChangeEvent<string> evt)
        {
            PanelStateStore.instance.Settings.disallowedTools = SplitLines(evt.newValue);
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnDangerousToggleChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.dangerouslySkipPermissions = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        // -- (b2) Model (v0.8.0, docs/design-notes/2026-08-01-model-settings.md) --------

        private void BuildModelSection(VisualElement parent)
        {
            // Distinct icon name from BuildConversationSection's "d__Popup"
            // (both resolve to a real built-in texture in this editor
            // version, so sharing a name meant BOTH cards silently rendered
            // the identical icon -- confirmed by SettingsViewSectionIconTests).
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionModel,
                "d_Preset.Context", IconLoader.GlyphSpark);

            PanelSettings settings = PanelStateStore.instance.Settings;
            List<ModelCatalogEntry> catalog = ResolveCurrentModelCatalog();
            List<string> choices = BuildModelChoices(catalog, settings.model);
            _defaultModelField = new PopupField<string>(L10n.S.SettingsDefaultModelLabel,
                choices, ResolveInitialModelChoice(choices, settings.model),
                FormatModelChoiceValue, FormatModelChoiceValue);
            _defaultModelField.AddToClassList("uap-settings-field");
            _defaultModelField.RegisterValueChangedCallback(OnDefaultModelChanged);
            section.Add(_defaultModelField);

            _defaultModelHintLabel = new Label(string.Empty);
            _defaultModelHintLabel.AddToClassList("uap-settings-hint");
            _defaultModelHintLabel.enableRichText = false;
            _defaultModelHintLabel.style.whiteSpace = WhiteSpace.Normal;
            section.Add(_defaultModelHintLabel);

            BuildSubagentCostPolicyField(section);
            BuildAgentOverridesFoldout(section);

            RebuildAgentOverrideRows();
            RefreshModelSection();
        }

        // -- (b2a0) Subagent cost policy (v0.11.0, docs/design-notes/
        // 2026-08-02-subagent-model-precedence.md section 3.2): the PRIMARY
        // subagent-cost control, directly under Default model. Reconnect-
        // relevant (it is composed into the same append-system-prompt
        // payload as custom instructions), so it DOES call
        // RequestAutoApplyReconnect, same as BuildSubagentModelField below. --

        private void BuildSubagentCostPolicyField(VisualElement section)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            var choices = new List<SubagentCostPolicy>
            {
                SubagentCostPolicy.AgentDecides,
                SubagentCostPolicy.HaikuForSimpleTasks
            };
            _subagentCostPolicyField = new PopupField<SubagentCostPolicy>(
                L10n.S.SettingsSubagentCostPolicyLabel, choices, settings.subagentCostPolicy,
                FormatSubagentCostPolicyOption, FormatSubagentCostPolicyOption);
            _subagentCostPolicyField.AddToClassList("uap-settings-field");
            _subagentCostPolicyField.RegisterValueChangedCallback(OnSubagentCostPolicyChanged);
            VisualElement subagentCostPolicyScope = AddHintScope(section);
            subagentCostPolicyScope.Add(_subagentCostPolicyField);

            AddHint(subagentCostPolicyScope, L10n.S.SettingsSubagentCostPolicyHint,
                L10n.S.SettingsSubagentCostPolicyTooltip);
        }

        /// <summary>
        /// Localized label for the Subagent cost policy PopupField's two
        /// values -- a plain switch (unlike model dropdowns, no catalog
        /// lookup involved). The default arm covers HaikuForSimpleTasks
        /// explicitly and treats every other value (including AgentDecides
        /// and any future/out-of-range enum value) as AgentDecides, so an
        /// unexpected int never renders a blank label.
        /// </summary>
        public static string FormatSubagentCostPolicyOption(SubagentCostPolicy policy)
        {
            switch (policy)
            {
                case SubagentCostPolicy.HaikuForSimpleTasks:
                    return L10n.S.SettingsSubagentCostPolicyOptionHaikuForSimpleTasks;
                case SubagentCostPolicy.AgentDecides:
                default:
                    return L10n.S.SettingsSubagentCostPolicyOptionAgentDecides;
            }
        }

        /// <summary>
        /// Persists PanelSettings.subagentCostPolicy and requests an
        /// auto-apply reconnect: like customInstructions (and unlike
        /// agentModelOverrides/model), this field is composed into the
        /// spawn's --append-system-prompt payload
        /// (AgentHub.ComposeAppendSystemPrompt), which SettingsChangeDetector
        /// treats as reconnect-relevant.
        /// </summary>
        private void OnSubagentCostPolicyChanged(ChangeEvent<SubagentCostPolicy> evt)
        {
            PanelStateStore.instance.Settings.subagentCostPolicy = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        // -- (b2a) Subagent model (v0.9.0, docs/design-notes/2026-08-01-
        // model-settings-rework.md section 4.2; renamed/moved to a
        // FORCE-semantics control in the advanced foldout per v0.11.0,
        // docs/design-notes/2026-08-02-subagent-model-precedence.md section
        // 3.2): blanket CLAUDE_CODE_SUBAGENT_MODEL env var -- a hard cost
        // clamp that overrides BOTH the per-type table below AND the
        // agent's own per-call model choice (R07 section 12, P5), normally
        // left empty. Reconnect-relevant, so it DOES call
        // RequestAutoApplyReconnect. -----------------------------------

        private void BuildSubagentModelField(VisualElement section)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            List<ModelCatalogEntry> catalog = ResolveCurrentModelCatalog();
            List<string> choices = BuildModelChoices(catalog, settings.subagentModel);
            _subagentModelField = new PopupField<string>(L10n.S.SettingsSubagentModelLabel,
                choices, ResolveInitialModelChoice(choices, settings.subagentModel),
                FormatSubagentModelChoiceValue, FormatSubagentModelChoiceValue);
            _subagentModelField.AddToClassList("uap-settings-field");
            _subagentModelField.RegisterValueChangedCallback(OnSubagentModelChanged);
            VisualElement subagentModelScope = AddHintScope(section);
            subagentModelScope.Add(_subagentModelField);

            AddHint(subagentModelScope, L10n.S.SettingsSubagentModelHint,
                L10n.S.SettingsSubagentModelTooltip);
            // 2-arg AddHint on purpose: the 3-arg overload returns null for
            // an empty text (this label is filled in per backend) and would
            // replace the scope's SettingsSubagentModelTooltip; the label's
            // own tooltip wins over the scope's while hovering it.
            _subagentModelAcpHintLabel = AddHint(subagentModelScope, string.Empty);
            _subagentModelAcpHintLabel.tooltip = L10n.S.SettingsSubagentModelAcpTooltip;
            _subagentModelAcpHintLabel.style.display = DisplayStyle.None;
            RefreshSubagentModelAcpHint();

            _subagentPrecedenceWarningLabel = new Label(L10n.S.SettingsSubagentPrecedenceWarning);
            _subagentPrecedenceWarningLabel.AddToClassList("uap-settings-hint");
            _subagentPrecedenceWarningLabel.AddToClassList("uap-settings-hint--pending");
            _subagentPrecedenceWarningLabel.enableRichText = false;
            _subagentPrecedenceWarningLabel.style.whiteSpace = WhiteSpace.Normal;
            _subagentPrecedenceWarningLabel.style.display = DisplayStyle.None;
            section.Add(_subagentPrecedenceWarningLabel);
        }

        /// <summary>
        /// Persists PanelSettings.subagentModel and -- unlike
        /// OnDefaultModelChanged -- DOES request an auto-apply reconnect:
        /// the measured CLAUDE_CODE_SUBAGENT_MODEL env var is read fresh on
        /// every spawn including --resume (R07 section 11), so this field
        /// genuinely is reconnect-relevant
        /// (SettingsChangeDetector.RequiresReconnect) and reaches the
        /// CURRENT session within seconds via the existing settings
        /// auto-apply machinery, no new-session caveat needed.
        /// </summary>
        private void OnSubagentModelChanged(ChangeEvent<string> evt)
        {
            PanelStateStore.instance.Settings.subagentModel = evt.newValue ?? string.Empty;
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            RefreshSubagentPrecedenceWarning();
            AgentHub.RequestAutoApplyReconnect();
        }

        /// <summary>
        /// Shows/hides SettingsSubagentPrecedenceWarning: the measured env
        /// var beats `.claude/agents/*.md` overrides outright (R07 section
        /// 11, P3), so a non-empty subagentModel makes every per-type
        /// override row below inert while it stays set. Only counts rows
        /// that would actually materialize a file (IsCompleteAgentOverride)
        /// -- a blank placeholder row (just added, not yet filled in) is
        /// not itself in conflict with anything.
        /// </summary>
        private void RefreshSubagentPrecedenceWarning()
        {
            if (_subagentPrecedenceWarningLabel == null)
            {
                return;
            }
            bool conflict = HasSubagentModelPrecedenceConflict(
                PanelStateStore.instance.Settings.subagentModel, LoadAgentModelOverrides());
            _subagentPrecedenceWarningLabel.style.display =
                conflict ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // -- (b2b) Per-type overrides, demoted to an advanced foldout
        // (v0.9.0, docs/design-notes/2026-08-01-model-settings-rework.md
        // section 4.2) -----------------------------------------------------

        private void BuildAgentOverridesFoldout(VisualElement section)
        {
            _agentOverridesFoldout = new Foldout
            {
                text = L10n.S.SettingsAgentOverridesFoldoutTitle,
                value = false
            };
            _agentOverridesFoldout.AddToClassList("uap-settings-advanced-foldout");
            section.Add(_agentOverridesFoldout);

            // v0.11.0: the blanket "force" dropdown lives at the TOP of
            // this foldout, above the per-type table -- see
            // BuildSubagentModelField's doc comment for why it moved here.
            BuildSubagentModelField(_agentOverridesFoldout);

            AddHint(AddHintScope(_agentOverridesFoldout), L10n.S.SettingsAgentOverridesHint,
                L10n.S.SettingsAgentOverridesTooltip);

            _agentOverridesDuplicateWarningLabel = new Label(
                L10n.S.SettingsAgentOverrideDuplicateNameWarning);
            _agentOverridesDuplicateWarningLabel.AddToClassList("uap-settings-hint");
            // Reuses the existing warn-colored hint variant (pending-
            // reconnect hint / danger zone) rather than introducing a new
            // near-duplicate USS rule for "this is a warning".
            _agentOverridesDuplicateWarningLabel.AddToClassList("uap-settings-hint--pending");
            _agentOverridesDuplicateWarningLabel.enableRichText = false;
            _agentOverridesDuplicateWarningLabel.style.whiteSpace = WhiteSpace.Normal;
            _agentOverridesDuplicateWarningLabel.style.display = DisplayStyle.None;
            _agentOverridesFoldout.Add(_agentOverridesDuplicateWarningLabel);

            _agentOverridesHost = new VisualElement();
            _agentOverridesHost.AddToClassList("uap-settings-qa-list");
            _agentOverridesFoldout.Add(_agentOverridesHost);

            // Permanent (not pending-state-driven, unlike
            // _reconnectHintRow) note that this table -- unlike every
            // other Settings field -- is never fixed by reconnecting an
            // existing session; see AddAgentOverrideRow's doc comment and
            // docs/research/07-model-configuration.md section 10.8.
            _agentOverridesNewSessionHint = AddHint(_agentOverridesFoldout, L10n.S.SettingsAgentOverridesNewSessionHint);

            VisualElement addRow = AddRow(_agentOverridesFoldout);
            var add = new Button(OnAddAgentOverrideClicked) { text = L10n.S.SettingsAddAgentOverrideButton };
            add.AddToClassList("uap-settings-btn");
            addRow.Add(add);
        }

        /// <summary>
        /// Persist-only entry point (docs/design-notes/2026-08-01-model-
        /// settings-rework.md section 4.1, reversing v0.8.0's shared-
        /// source-of-truth design): delegates entirely to
        /// AgentHub.SetDefaultModel, which persists PanelSettings.model
        /// and NEVER touches the live client -- changing this dropdown must
        /// not switch the currently running session (use the header model
        /// picker, AgentHub.SwitchSessionModel, for that). Deliberately no
        /// RequestAutoApplyReconnect call either: SettingsChangeDetector no
        /// longer compares `model` at all (a resume can never re-apply it),
        /// so there is nothing reconnect-pending to signal. evt.newValue
        /// CAN legitimately be string.Empty here -- selecting this
        /// dropdown's own "(Default)" sentinel (BuildModelChoices' leading
        /// entry) -- meaning "the next new session should not pass --model
        /// at all"; SetDefaultModel persists that empty value like any
        /// other choice.
        /// </summary>
        private void OnDefaultModelChanged(ChangeEvent<string> evt)
        {
            AgentHub.SetDefaultModel(evt.newValue);
        }

        /// <summary>
        /// Re-reads the model catalog/current value and refreshes the
        /// Default model dropdown's choices, selection and hint text.
        /// Deliberately does NOT touch the agent-override rows (their
        /// PopupFields are rebuilt only on structural changes -- add/
        /// remove/OnActivate -- via RebuildAgentOverrideRows; rebuilding
        /// them on every AgentHub.Changed tick, like this method's own
        /// caller does, would fight the user's in-progress edits the same
        /// way the Quick Actions list already avoids doing).
        /// </summary>
        private void RefreshModelSection()
        {
            if (_defaultModelField == null)
            {
                return;
            }
            PanelSettings settings = PanelStateStore.instance.Settings;
            List<ModelCatalogEntry> catalog = ResolveCurrentModelCatalog();
            List<string> choices = BuildModelChoices(catalog, settings.model);
            _defaultModelField.choices = choices;
            string initial = ResolveInitialModelChoice(choices, settings.model);
            if (_defaultModelField.value != initial)
            {
                _defaultModelField.SetValueWithoutNotify(initial);
            }
            if (_defaultModelHintLabel != null)
            {
                _defaultModelHintLabel.text = ResolveDefaultModelHintText(catalog, settings.model);
            }

            if (_subagentCostPolicyField != null && _subagentCostPolicyField.value != settings.subagentCostPolicy)
            {
                _subagentCostPolicyField.SetValueWithoutNotify(settings.subagentCostPolicy);
            }

            if (_subagentModelField != null)
            {
                List<string> subagentChoices = BuildModelChoices(catalog, settings.subagentModel);
                _subagentModelField.choices = subagentChoices;
                string subagentInitial = ResolveInitialModelChoice(subagentChoices, settings.subagentModel);
                if (_subagentModelField.value != subagentInitial)
                {
                    _subagentModelField.SetValueWithoutNotify(subagentInitial);
                }
            }
            RefreshSubagentPrecedenceWarning();
        }

        private static List<AgentModelOverride> LoadAgentModelOverrides()
        {
            return PanelStateStore.instance.Settings.agentModelOverrides;
        }

        private void RebuildAgentOverrideRows()
        {
            _agentOverridesHost.Clear();
            List<AgentModelOverride> overrides = LoadAgentModelOverrides();
            List<string> typeChoices = BuildAgentTypeChoices(
                PanelStateStore.instance.Settings.agentTypeCatalog, ResolveCurrentLiveAgents(), overrides);
            for (int i = 0; i < overrides.Count; i++)
            {
                AddAgentOverrideRow(overrides, overrides[i], typeChoices);
            }
            _lastAgentOverrideTypeChoices = typeChoices;
            // With no rows there is nothing the "new sessions only" caveat
            // could apply to; an orphan hint under an empty list is noise.
            if (_agentOverridesNewSessionHint != null)
            {
                _agentOverridesNewSessionHint.style.display =
                    overrides.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            RefreshAgentOverridesDuplicateWarning();
            RefreshSubagentPrecedenceWarning();
        }

        /// <summary>
        /// OnHubChanged's catalog-aware counterpart to the unconditional
        /// RebuildAgentOverrideRows(): recomputes the same
        /// BuildAgentTypeChoices() union the rows were last built with and
        /// only rebuilds (destructive -- clears every row's VisualElement,
        /// see RebuildAgentOverrideRows) when it actually differs from
        /// _lastAgentOverrideTypeChoices. This is what lets an
        /// already-open Settings view pick up a live/cached agent-type
        /// catalog arriving after construction (RefreshAgentTypeCatalogCache
        /// -> AgentHub.Changed) without also rebuilding on every unrelated
        /// AgentHub.Changed tick (streaming tokens, tool events, ...),
        /// which would otherwise blow away in-progress edits the same way
        /// RefreshModelSection's doc comment describes.
        /// </summary>
        private void RefreshAgentOverrideRowsIfCatalogChanged()
        {
            if (_agentOverridesHost == null)
            {
                return;
            }
            List<AgentModelOverride> overrides = LoadAgentModelOverrides();
            List<string> currentChoices = BuildAgentTypeChoices(
                PanelStateStore.instance.Settings.agentTypeCatalog, ResolveCurrentLiveAgents(), overrides);
            if (AreStringListsEqual(currentChoices, _lastAgentOverrideTypeChoices))
            {
                return;
            }
            RebuildAgentOverrideRows();
        }

        /// <summary>
        /// internal (not private) so SettingsViewLogicTests can guard the
        /// exact comparison RefreshAgentOverrideRowsIfCatalogChanged uses to
        /// decide whether an already-open Settings view's agent-override
        /// rows need a destructive rebuild -- see that method's doc
        /// comment. SettingsView itself has no test-instantiation seam
        /// (BuildRoot's full VisualElement tree is never constructed in
        /// EditMode tests anywhere in this file's suite), so this pure
        /// order-and-value comparison is the testable surface for that fix.
        /// </summary>
        internal static bool AreStringListsEqual(List<string> a, List<string> b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            if (a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], System.StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>system/init.agents[] from the live connection, or null when not (yet) connected.</summary>
        private static string[] ResolveCurrentLiveAgents()
        {
            AgentClient client = AgentHub.Client;
            return client != null && client.InitMessage != null ? client.InitMessage.Agents : null;
        }

        /// <summary>
        /// Shows/hides SettingsAgentOverrideDuplicateNameWarning based on
        /// HasDuplicateAgentOverrideNames. AgentDefinitionFileWriter.Sync
        /// writes one `.claude/agents/&lt;name&gt;.md` file per distinct agent
        /// name in list order, so two rows sharing an agent name silently
        /// collapse to the LAST row's model with no error surfaced anywhere
        /// (R07 section 10) -- this warning is the only place that fact
        /// becomes visible to the user.
        /// </summary>
        private void RefreshAgentOverridesDuplicateWarning()
        {
            if (_agentOverridesDuplicateWarningLabel == null)
            {
                return;
            }
            bool hasDuplicate = HasDuplicateAgentOverrideNames(LoadAgentModelOverrides());
            _agentOverridesDuplicateWarningLabel.style.display =
                hasDuplicate ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// One editable row. `overrides` is the SAME list instance shared
        /// by every row built in this pass (mirrors AddQuickActionRow);
        /// `entry` is edited (and removed) by reference, never by index.
        /// Deliberately does NOT call RefreshReconnectHint/AgentHub.
        /// RequestAutoApplyReconnect from any of these handlers (unlike
        /// every other next-spawn-only field in this file): docs/research/
        /// 07-model-configuration.md section 10.8 ("capture15") found the
        /// CLI snapshots `.claude/agents/*.md` at SESSION CREATION, so
        /// reconnecting the EXISTING session (same session id, `--resume`)
        /// can never pick up an agentModelOverrides edit no matter how the
        /// row changes -- only a session created after
        /// AgentDefinitionFileWriter.Sync has written the new table (i.e. a
        /// brand-new session from the header "+") sees it. Signaling
        /// "reconnect now would fix this" would therefore be actively
        /// wrong; BuildModelSection instead adds a permanent, always-shown
        /// hint under the row list pointing at starting a new session
        /// instead (SettingsAgentOverridesNewSessionHint).
        /// </summary>
        /// <summary>
        /// `typeChoices` is the RebuildAgentOverrideRows-computed union
        /// (BuildAgentTypeChoices) shared by every row in this pass -- see
        /// that method's doc comment. When empty (never connected this
        /// project AND no rows already have a name), the agent-name column
        /// falls back to the original free-text TextField so the row is
        /// never left with no way to enter a name at all.
        /// </summary>
        private void AddAgentOverrideRow(List<AgentModelOverride> overrides, AgentModelOverride entry,
            List<string> typeChoices)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-settings-model-row");

            if (typeChoices == null || typeChoices.Count == 0)
            {
                var nameField = new TextField();
                nameField.AddToClassList("uap-settings-model-name");
                nameField.tooltip = L10n.S.SettingsAgentOverrideNameTooltip;
                nameField.SetValueWithoutNotify(entry.agentName ?? string.Empty);
                nameField.RegisterValueChangedCallback(delegate(ChangeEvent<string> evt)
                {
                    entry.agentName = (evt.newValue ?? string.Empty).Trim();
                    PanelStateStore.instance.SaveNow();
                    RefreshAgentOverridesDuplicateWarning();
                    RefreshSubagentPrecedenceWarning();
                });
                row.Add(nameField);
            }
            else
            {
                var namePopupChoices = new List<string> { string.Empty };
                namePopupChoices.AddRange(typeChoices);
                var namePopup = new PopupField<string>(namePopupChoices,
                    ResolveInitialModelChoice(namePopupChoices, entry.agentName),
                    FormatAgentTypeChoiceValue, FormatAgentTypeChoiceValue);
                namePopup.AddToClassList("uap-settings-model-name");
                namePopup.tooltip = L10n.S.SettingsAgentOverrideNameTooltip;
                namePopup.RegisterValueChangedCallback(delegate(ChangeEvent<string> evt)
                {
                    entry.agentName = evt.newValue ?? string.Empty;
                    PanelStateStore.instance.SaveNow();
                    RefreshAgentOverridesDuplicateWarning();
                    RefreshSubagentPrecedenceWarning();
                });
                row.Add(namePopup);
            }

            List<ModelCatalogEntry> catalog = ResolveCurrentModelCatalog();
            List<string> choices = BuildModelChoices(catalog, entry.modelAlias);
            var modelField = new PopupField<string>(choices,
                ResolveInitialModelChoice(choices, entry.modelAlias),
                FormatModelChoiceValue, FormatModelChoiceValue);
            modelField.AddToClassList("uap-settings-model-picker");
            modelField.tooltip = L10n.S.SettingsAgentOverrideModelTooltip;
            modelField.RegisterValueChangedCallback(delegate(ChangeEvent<string> evt)
            {
                entry.modelAlias = evt.newValue ?? string.Empty;
                PanelStateStore.instance.SaveNow();
                RefreshSubagentPrecedenceWarning();
            });
            row.Add(modelField);

            var remove = new Button(delegate
            {
                overrides.Remove(entry);
                PanelStateStore.instance.SaveNow();
                RebuildAgentOverrideRows();
            });
            remove.text = L10n.S.SettingsAgentOverrideRemoveButton;
            remove.AddToClassList("uap-settings-btn");
            remove.AddToClassList("uap-settings-model-remove");
            row.Add(remove);

            _agentOverridesHost.Add(row);
        }

        private void OnAddAgentOverrideClicked()
        {
            // No reconnect hint/auto-apply here either -- see
            // AddAgentOverrideRow's doc comment; this field never needs
            // either now.
            List<AgentModelOverride> overrides = LoadAgentModelOverrides();
            overrides.Add(new AgentModelOverride());
            PanelStateStore.instance.SaveNow();
            RebuildAgentOverrideRows();
        }

        /// <summary>
        /// Prefers the CURRENT client's live models[] (immediately accurate
        /// once connected -- HeaderView's own model picker reads the exact
        /// same InitializeResponse, R02b) and falls back to the persisted
        /// PanelSettings.modelCatalog (populated by AgentHub.
        /// RefreshModelCatalogCache on any past connection this project has
        /// made) when not connected or before the live list is available.
        /// Never returns null.
        /// </summary>
        private static List<ModelCatalogEntry> ResolveCurrentModelCatalog()
        {
            AgentClient client = AgentHub.Client;
            if (client != null && client.InitializeResponse != null)
            {
                List<HeaderView.ModelOption> live =
                    HeaderView.ParseModels(client.InitializeResponse.Response["models"]);
                if (live.Count > 0)
                {
                    var converted = new List<ModelCatalogEntry>(live.Count);
                    for (int i = 0; i < live.Count; i++)
                    {
                        converted.Add(new ModelCatalogEntry
                        {
                            value = live[i].Value ?? string.Empty,
                            displayName = live[i].DisplayName ?? string.Empty,
                            resolvedModel = live[i].ResolvedModel ?? string.Empty,
                            description = live[i].Description ?? string.Empty
                        });
                    }
                    return converted;
                }
            }
            List<ModelCatalogEntry> cached = PanelStateStore.instance.Settings.modelCatalog;
            return cached ?? new List<ModelCatalogEntry>();
        }

        /// <summary>
        /// The catalog's server/plan-side "Default (recommended)" entry's
        /// wire value (docs/research/07-model-configuration.md section 13,
        /// verified against Tests/Editor/Fixtures/out_bidi.jsonl) --
        /// distinct from the Settings dropdowns' own empty-string sentinel
        /// (BuildModelChoices' leading choice, meaning "omit --model
        /// entirely"). Selecting THIS value passes the literal string
        /// "default" as --model.
        /// </summary>
        public const string DefaultCatalogEntryValue = "default";

        /// <summary>
        /// Reads the catalog fresh on every call (rather than closing over
        /// a snapshot) so a PopupField's format callback never goes stale
        /// after PanelSettings.modelCatalog is wholesale-replaced by
        /// AgentHub.RefreshModelCatalogCache.
        /// </summary>
        private static string FormatModelChoiceValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return L10n.S.SettingsModelChoiceDefaultLabel;
            }
            return FormatModelValueDisplay(value);
        }

        /// <summary>
        /// Formatter for the Subagent model PopupField: same catalog
        /// lookup as FormatModelChoiceValue, but the empty sentinel reads
        /// "(same as default)" (SettingsSubagentModelSameAsDefaultLabel)
        /// rather than "(Default)" -- this field's empty value means
        /// "inherit the Default model above", a distinct concept from the
        /// Default model dropdown's own "(Default)" (= "let the CLI pick").
        /// </summary>
        private static string FormatSubagentModelChoiceValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return L10n.S.SettingsSubagentModelSameAsDefaultLabel;
            }
            return FormatModelValueDisplay(value);
        }

        /// <summary>
        /// Shared non-empty-value formatter for every model PopupField in
        /// this view: the server/plan-defined "default" catalog entry
        /// (v0.11.0, docs/design-notes/2026-08-02-subagent-model-precedence.md
        /// section 3.1) renders through FormatDefaultModelEntryLabel so its
        /// TRUE resolved target is visible ("Default (recommended: <short>)");
        /// every other value keeps its plain catalog displayName.
        /// </summary>
        private static string FormatModelValueDisplay(string value)
        {
            if (string.Equals(value, DefaultCatalogEntryValue, System.StringComparison.Ordinal))
            {
                return FormatDefaultModelEntryLabel(LookupModelCatalogEntry(value));
            }
            return LookupModelDisplayName(value);
        }

        /// <summary>
        /// Reads the catalog fresh on every call (see FormatModelChoiceValue's
        /// original doc comment on why) and returns the matching
        /// displayName, or `value` itself when not found.
        /// </summary>
        private static string LookupModelDisplayName(string value)
        {
            ModelCatalogEntry candidate = LookupModelCatalogEntry(value);
            if (candidate == null)
            {
                return value;
            }
            return string.IsNullOrEmpty(candidate.displayName) ? value : candidate.displayName;
        }

        /// <summary>
        /// Reads the catalog fresh on every call (see FormatModelChoiceValue's
        /// doc comment) and returns the matching ModelCatalogEntry, or null
        /// when `value` is not present. Impure wrapper around the pure
        /// <see cref="FindCatalogEntry"/> so every call site sharing the
        /// same "read the live/cached catalog" concern goes through one
        /// place.
        /// </summary>
        private static ModelCatalogEntry LookupModelCatalogEntry(string value)
        {
            return FindCatalogEntry(ResolveCurrentModelCatalog(), value);
        }

        /// <summary>
        /// Pure catalog lookup by value (ordinal), given an explicit list --
        /// unlike LookupModelCatalogEntry, takes no dependency on
        /// AgentHub/PanelStateStore, so it is unit tested directly.
        /// Null-safe; returns null when not found or `catalog` is null.
        /// </summary>
        public static ModelCatalogEntry FindCatalogEntry(List<ModelCatalogEntry> catalog, string value)
        {
            if (catalog == null)
            {
                return null;
            }
            for (int i = 0; i < catalog.Count; i++)
            {
                ModelCatalogEntry candidate = catalog[i];
                if (candidate != null && string.Equals(candidate.value, value, System.StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Pure label for the catalog's "default" entry (v0.11.0, docs/
        /// design-notes/2026-08-02-subagent-model-precedence.md section
        /// 3.1, work item C -- answering the 2026-08-02 user feedback
        /// "changing the Default model dropdown doesn't change what
        /// 'Default (recommended)' displays as"): docs/research/
        /// 07-model-configuration.md section 13 found nothing client-side
        /// can retarget that entry, so this only makes its TRUE resolved
        /// target visible. Renders "Default (recommended: &lt;short&gt;)"
        /// via <see cref="ShortenResolvedModel"/> when `entry.resolvedModel`
        /// is present; falls back to `entry.displayName` (or the literal
        /// catalog value, for a null entry / a cache written before this
        /// field existed) otherwise -- so an old cache without
        /// resolvedModel/description never crashes or mislabels, it just
        /// shows what it always showed.
        /// </summary>
        public static string FormatDefaultModelEntryLabel(ModelCatalogEntry entry)
        {
            string shortLabel = ShortenResolvedModel(entry != null ? entry.resolvedModel : null);
            if (!string.IsNullOrEmpty(shortLabel))
            {
                return L10n.F(L10n.S.SettingsDefaultModelChoiceResolvedFmt, shortLabel);
            }
            if (entry != null && !string.IsNullOrEmpty(entry.displayName))
            {
                return entry.displayName;
            }
            return DefaultCatalogEntryValue;
        }

        /// <summary>
        /// Pure string-transform seam (v0.11.0, work item C/E): compacts a
        /// resolved model id like "claude-opus-5[1m]" into "opus-5 (1m)" --
        /// strips a leading "claude-" prefix, then pulls a trailing
        /// "[...]" bracket suffix (if any) out into a parenthesized suffix
        /// on the end. No brackets present (e.g. "claude-sonnet-5") yields
        /// just the de-prefixed id ("sonnet-5"); null/empty input returns
        /// null so callers (FormatDefaultModelEntryLabel) know to fall back
        /// to the untouched displayName instead of showing an empty label.
        /// </summary>
        public static string ShortenResolvedModel(string resolvedModel)
        {
            if (string.IsNullOrEmpty(resolvedModel))
            {
                return null;
            }
            const string claudePrefix = "claude-";
            string body = resolvedModel.StartsWith(claudePrefix, System.StringComparison.Ordinal)
                ? resolvedModel.Substring(claudePrefix.Length)
                : resolvedModel;

            int bracketStart = body.IndexOf('[');
            if (bracketStart < 0)
            {
                return body;
            }
            int bracketEnd = body.IndexOf(']', bracketStart);
            if (bracketEnd <= bracketStart)
            {
                return body;
            }
            string suffix = body.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            string baseName = body.Substring(0, bracketStart) + body.Substring(bracketEnd + 1);
            return suffix.Length == 0 ? baseName : baseName + " (" + suffix + ")";
        }

        /// <summary>
        /// Pure hint-text decision for the Default model dropdown (v0.11.0,
        /// work item C): an empty catalog keeps the existing "connect once"
        /// hint; otherwise, when the CURRENTLY SELECTED entry carries a
        /// non-empty description (only populated from a live connection or
        /// a v0.11.0+ cache -- see ModelCatalogEntry's doc comment), that
        /// description IS the hint (the most specific, freshest truth
        /// available); falling back to the generic
        /// SettingsDefaultModelHint otherwise (no matching entry, or one
        /// with no description -- including every entry cached before this
        /// field existed).
        /// </summary>
        public static string ResolveDefaultModelHintText(List<ModelCatalogEntry> catalog, string selectedValue)
        {
            if (catalog == null || catalog.Count == 0)
            {
                return L10n.S.SettingsDefaultModelHintNoCatalog;
            }
            ModelCatalogEntry entry = FindCatalogEntry(catalog, selectedValue);
            if (entry != null && !string.IsNullOrEmpty(entry.description))
            {
                return entry.description;
            }
            return L10n.S.SettingsDefaultModelHint;
        }

        /// <summary>Formatter for the per-type override row's agent-name PopupField: empty shows a "pick one" placeholder, otherwise the raw agent name (names ARE the display text -- no catalog lookup).</summary>
        private static string FormatAgentTypeChoiceValue(string value)
        {
            return string.IsNullOrEmpty(value) ? L10n.S.SettingsAgentOverrideNamePlaceholder : value;
        }

        // -- (c1) Custom instructions --------------------------------------------------

        private void BuildCustomInstructionsSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionCustomInstructions,
                "d_TextAsset Icon", IconLoader.GlyphFile);
            AddHint(AddHintScope(section), L10n.A(L10n.S.SettingsCustomInstructionsHint),
                L10n.A(L10n.S.SettingsCustomInstructionsTooltip));

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("uap-settings-custom-scroll");

            _customInstructionsField = new TextField();
            _customInstructionsField.multiline = true;
            _customInstructionsField.AddToClassList("uap-settings-custom-field");
            _customInstructionsField.SetValueWithoutNotify(LoadCustomInstructions());
            _customInstructionsField.RegisterValueChangedCallback(OnCustomInstructionsChanged);
            scroll.Add(_customInstructionsField);
            section.Add(scroll);
        }

        /// <summary>
        /// Loads the custom-instructions sidecar text (never PanelSettings/
        /// UnityYAML -- see CustomInstructionsFile's doc comment).
        /// </summary>
        private static string LoadCustomInstructions()
        {
            return CustomInstructionsFile.CreateDefault(AgentHub.ProjectRoot).Load();
        }

        private void OnCustomInstructionsChanged(ChangeEvent<string> evt)
        {
            CustomInstructionsFile.CreateDefault(AgentHub.ProjectRoot).Save(evt.newValue ?? string.Empty);
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        // -- (c2) Display ---------------------------------------------------------------

        private void BuildDisplaySection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionDisplay,
                "d_UnityEditor.GameView", IconLoader.GlyphBullet);

            _showThinkingToggle = new Toggle(L10n.S.SettingsShowThinkingLabel);
            _showThinkingToggle.AddToClassList("uap-settings-field");
            _showThinkingToggle.AddToClassList("uap-switch");
            _showThinkingToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.showThinking);
            _showThinkingToggle.RegisterValueChangedCallback(OnShowThinkingChanged);
            VisualElement showThinkingScope = AddHintScope(section);
            showThinkingScope.Add(_showThinkingToggle);
            // 2026-09-06 settings review: the label is self-explanatory; the
            // CLI-version detail lives in the tooltip (and its ? mark) only.
            showThinkingScope.tooltip = L10n.S.SettingsShowThinkingTooltip;

            _subagentDefaultExpandedToggle = new Toggle(L10n.S.SettingsExpandSubagentLabel);
            _subagentDefaultExpandedToggle.AddToClassList("uap-settings-field");
            // Follows showThinkingScope's hint immediately above -- a new
            // topic starting right after another field's hint, per the
            // 2026-08-14 ui-polish audit item 8.
            _subagentDefaultExpandedToggle.AddToClassList("uap-settings-field--group-start");
            _subagentDefaultExpandedToggle.AddToClassList("uap-switch");
            _subagentDefaultExpandedToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.subagentDefaultExpanded);
            _subagentDefaultExpandedToggle.RegisterValueChangedCallback(OnSubagentDefaultExpandedChanged);
            section.Add(_subagentDefaultExpandedToggle);
            AddHint(section, L10n.S.SettingsExpandSubagentHint);

            _showCostUsdToggle = new Toggle(L10n.S.SettingsShowCostLabel);
            _showCostUsdToggle.AddToClassList("uap-settings-field");
            // Same rule as _subagentDefaultExpandedToggle above: follows
            // that field's own hint immediately above.
            _showCostUsdToggle.AddToClassList("uap-settings-field--group-start");
            _showCostUsdToggle.AddToClassList("uap-switch");
            _showCostUsdToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.showCostUsd);
            _showCostUsdToggle.RegisterValueChangedCallback(OnShowCostUsdChanged);
            section.Add(_showCostUsdToggle);
            AddHint(section, L10n.S.SettingsShowCostHint);
        }

        private void OnShowThinkingChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.showThinking = evt.newValue;
            PanelStateStore.instance.SaveNow();
            // Forces every cached MessageListController row to rebuild on
            // the next Refresh (see the field's doc comment) so the change
            // is visible on the CURRENT transcript, not just new messages.
            // This covers VISIBILITY only and applies immediately; the
            // `--thinking-display summarized` spawn argument that makes
            // NEW thinking blocks carry real text still needs a reconnect
            // (docs/design-notes/2026-08-01-thinking-content-loss.md
            // section 7), hence the same reconnect-hint/auto-apply calls
            // every other next-spawn-only field's handler makes.
            MessageBlockFactory.SettingsGeneration++;
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnSubagentDefaultExpandedChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.subagentDefaultExpanded = evt.newValue;
            PanelStateStore.instance.SaveNow();
        }

        private void OnShowCostUsdChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.showCostUsd = evt.newValue;
            PanelStateStore.instance.SaveNow();
        }

        // -- (c3) Quick actions -----------------------------------------------------------

        private void BuildQuickActionsSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionQuickActions,
                "d_Favorite", "+");
            AddHint(AddHintScope(section), L10n.S.SettingsQuickActionsHint,
                L10n.S.SettingsQuickActionsTooltip);

            _quickActionsHost = new VisualElement();
            _quickActionsHost.AddToClassList("uap-settings-qa-list");
            section.Add(_quickActionsHost);

            VisualElement addRow = AddRow(section);
            var add = new Button(OnAddQuickActionClicked) { text = L10n.S.SettingsAddQuickActionButton };
            add.AddToClassList("uap-settings-btn");
            addRow.Add(add);

            RebuildQuickActionRows();
        }

        private static List<QuickAction> LoadQuickActions()
        {
            return QuickActionStore.CreateDefault(AgentHub.ProjectRoot).Load();
        }

        private static void SaveQuickActions(List<QuickAction> actions)
        {
            QuickActionStore.CreateDefault(AgentHub.ProjectRoot).Save(actions);
        }

        private void RebuildQuickActionRows()
        {
            _quickActionsHost.Clear();
            List<QuickAction> actions = LoadQuickActions();
            for (int i = 0; i < actions.Count; i++)
            {
                AddQuickActionRow(actions, actions[i]);
            }
        }

        /// <summary>
        /// One editable row. `actions` is the SAME list instance shared by
        /// every row built in this pass (loaded once in
        /// RebuildQuickActionRows), so every field's change handler saves
        /// the whole list back with everyone else's edits intact -- not a
        /// stale reload-from-disk snapshot. `action` is removed by
        /// reference (QuickAction has no value equality), never by index,
        /// so a remove is unambiguous even if two rows share the same text.
        /// </summary>
        private void AddQuickActionRow(List<QuickAction> actions, QuickAction action)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-settings-qa-row");

            var labelField = new TextField();
            labelField.AddToClassList("uap-settings-qa-label");
            labelField.tooltip = L10n.S.SettingsQuickActionLabelTooltip;
            labelField.SetValueWithoutNotify(action.label ?? string.Empty);
            labelField.RegisterValueChangedCallback(delegate(ChangeEvent<string> evt)
            {
                action.label = evt.newValue ?? string.Empty;
                SaveQuickActions(actions);
            });
            row.Add(labelField);

            var promptField = new TextField();
            promptField.multiline = true;
            promptField.AddToClassList("uap-settings-qa-prompt");
            promptField.tooltip = L10n.S.SettingsQuickActionPromptTooltip;
            promptField.SetValueWithoutNotify(action.prompt ?? string.Empty);
            promptField.RegisterValueChangedCallback(delegate(ChangeEvent<string> evt)
            {
                action.prompt = evt.newValue ?? string.Empty;
                SaveQuickActions(actions);
            });
            row.Add(promptField);

            var remove = new Button(delegate
            {
                actions.Remove(action);
                SaveQuickActions(actions);
                RebuildQuickActionRows();
            });
            remove.text = L10n.S.SettingsQuickActionRemoveButton;
            remove.AddToClassList("uap-settings-btn");
            remove.AddToClassList("uap-settings-qa-remove");
            row.Add(remove);

            _quickActionsHost.Add(row);
        }

        private void OnAddQuickActionClicked()
        {
            List<QuickAction> actions = LoadQuickActions();
            actions.Add(new QuickAction { label = L10n.S.SettingsQuickActionNewDefaultLabel, prompt = string.Empty });
            SaveQuickActions(actions);
            RebuildQuickActionRows();
        }

        // -- (c4) Notifications -----------------------------------------------------------

        private void BuildNotificationsSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionNotifications,
                "d_AudioSource Icon", IconLoader.GlyphBullet);
            AddHint(section, L10n.S.SettingsNotificationsHint);

            _permissionBeepToggle = new Toggle(L10n.S.SettingsPermissionBeepLabel);
            _permissionBeepToggle.AddToClassList("uap-settings-field");
            // First field right after the section's own intro hint -- same
            // "new topic after a hint" spacing need as Display's toggles
            // above (2026-08-14 ui-polish audit item 8).
            _permissionBeepToggle.AddToClassList("uap-settings-field--group-start");
            _permissionBeepToggle.AddToClassList("uap-switch");
            _permissionBeepToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.permissionBeep);
            _permissionBeepToggle.RegisterValueChangedCallback(OnPermissionBeepChanged);
            section.Add(_permissionBeepToggle);

            _turnCompleteBeepToggle = new Toggle(L10n.S.SettingsTurnCompleteBeepLabel);
            _turnCompleteBeepToggle.AddToClassList("uap-settings-field");
            _turnCompleteBeepToggle.AddToClassList("uap-switch");
            _turnCompleteBeepToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.turnCompleteBeep);
            _turnCompleteBeepToggle.RegisterValueChangedCallback(OnTurnCompleteBeepChanged);
            section.Add(_turnCompleteBeepToggle);
        }

        private void OnPermissionBeepChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.permissionBeep = evt.newValue;
            PanelStateStore.instance.SaveNow();
        }

        private void OnTurnCompleteBeepChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.turnCompleteBeep = evt.newValue;
            PanelStateStore.instance.SaveNow();
        }

        // -- (c5) Console errors (docs/design-notes/2026-08-13-error-chip-
        // ignore.md): management UI for the chip's persistent ignore ------

        private VisualElement _ignoredErrorsHost;
        private VisualElement _ignoredErrorsClearRow;
        private TextField _ignoredErrorPatternsField;

        private void BuildConsoleErrorsSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionConsoleErrors,
                "d_console.erroricon.sml", IconLoader.GlyphWarn);
            AddHint(AddHintScope(section), L10n.S.SettingsConsoleErrorsHint,
                L10n.S.SettingsConsoleErrorsTooltip);

            _ignoredErrorPatternsField = new TextField(L10n.S.SettingsIgnoredErrorPatternsLabel);
            _ignoredErrorPatternsField.multiline = true;
            _ignoredErrorPatternsField.AddToClassList("uap-settings-field");
            // First field right after the section's own intro hint -- same
            // spot as Notifications' permissionBeepToggle (2026-08-14
            // ui-polish audit item 8).
            _ignoredErrorPatternsField.AddToClassList("uap-settings-field--group-start");
            _ignoredErrorPatternsField.AddToClassList("uap-settings-errignore-patterns");
            _ignoredErrorPatternsField.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.ignoredConsoleErrorPatterns ?? string.Empty);
            _ignoredErrorPatternsField.RegisterValueChangedCallback(OnIgnoredErrorPatternsChanged);
            VisualElement patternsScope = AddHintScope(section);
            patternsScope.Add(_ignoredErrorPatternsField);
            AddHint(patternsScope, L10n.S.SettingsIgnoredErrorPatternsHint,
                L10n.S.SettingsIgnoredErrorPatternsTooltip);

            _ignoredErrorsHost = new VisualElement();
            _ignoredErrorsHost.AddToClassList("uap-settings-errignore-list");
            section.Add(_ignoredErrorsHost);

            _ignoredErrorsClearRow = AddRow(section);
            var clearAll = new Button(OnClearIgnoredErrorsClicked)
            {
                text = L10n.S.SettingsIgnoredErrorsClearAllButton
            };
            clearAll.AddToClassList("uap-settings-btn");
            _ignoredErrorsClearRow.Add(clearAll);

            RebuildIgnoredErrorRows();
        }

        private void OnIgnoredErrorPatternsChanged(ChangeEvent<string> evt)
        {
            PanelStateStore.instance.Settings.ignoredConsoleErrorPatterns = evt.newValue ?? string.Empty;
            PanelStateStore.instance.SaveNow();
            // The chip lives in another view and refreshes only off
            // ConsoleErrorProvider.Changed -- without this poke a pattern
            // edit would not move the chip until the next real error event.
            ConsoleErrorProvider.NotifyIgnoreStoreChanged();
        }

        /// <summary>
        /// One row per permanently ignored message. The list AND its
        /// clear-all row hide entirely while empty (design note: "hide the
        /// list when there are no ignored messages"). Row shape mirrors the
        /// quick-action rows: shrinking ellipsized text plus a trailing
        /// remove button in ONE straight column (2026-08-03 list-row-
        /// control-alignment rule).
        /// </summary>
        private void RebuildIgnoredErrorRows()
        {
            _ignoredErrorsHost.Clear();
            List<string> ignored = PanelStateStore.instance.Settings.ignoredConsoleErrors;
            bool any = ignored != null && ignored.Count > 0;
            _ignoredErrorsHost.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            _ignoredErrorsClearRow.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            if (!any)
            {
                return;
            }
            for (int i = 0; i < ignored.Count; i++)
            {
                AddIgnoredErrorRow(ignored, ignored[i]);
            }
        }

        private void AddIgnoredErrorRow(List<string> ignored, string message)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-settings-errignore-row");

            // Console error text is arbitrary (and can be agent-induced),
            // so it goes through the same sanitize chokepoint as every
            // other untrusted string and never renders rich text.
            var text = new Label(IconLoader.SanitizeForDisplay(message));
            text.enableRichText = false;
            text.AddToClassList("uap-settings-errignore-msg");
            text.tooltip = IconLoader.SanitizeForDisplay(message);
            row.Add(text);

            var remove = new Button(delegate
            {
                ignored.Remove(message);
                PanelStateStore.instance.SaveNow();
                ConsoleErrorProvider.NotifyIgnoreStoreChanged();
                RebuildIgnoredErrorRows();
            });
            remove.text = L10n.S.SettingsIgnoredErrorRemoveButton;
            remove.AddToClassList("uap-settings-btn");
            remove.AddToClassList("uap-settings-errignore-remove");
            row.Add(remove);

            _ignoredErrorsHost.Add(row);
        }

        private void OnClearIgnoredErrorsClicked()
        {
            List<string> ignored = PanelStateStore.instance.Settings.ignoredConsoleErrors;
            if (ignored == null || ignored.Count == 0)
            {
                return;
            }
            ignored.Clear();
            PanelStateStore.instance.SaveNow();
            ConsoleErrorProvider.NotifyIgnoreStoreChanged();
            RebuildIgnoredErrorRows();
        }

        // -- (b2) UapOps (Phase 5a, docs/design-notes/2026-08-01-phase5-
        // unity-ops-design.md section 1/8.6) -------------------------------

        private void BuildUapOpsSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsSectionUapOps,
                "d_UnityEditor.SceneHierarchyWindow", IconLoader.GlyphGear, "uapops");
            AddHint(AddHintScope(section), L10n.A(L10n.S.SettingsUapOpsHint), L10n.A(L10n.S.SettingsUapOpsTooltip));

            _uapOpsEnabledToggle = new Toggle(L10n.S.SettingsUapOpsEnabledLabel);
            _uapOpsEnabledToggle.AddToClassList("uap-settings-field");
            _uapOpsEnabledToggle.AddToClassList("uap-switch");
            _uapOpsEnabledToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsEnabled);
            _uapOpsEnabledToggle.RegisterValueChangedCallback(OnUapOpsEnabledChanged);
            VisualElement uapOpsEnabledScope = AddHintScope(section);
            uapOpsEnabledScope.Add(_uapOpsEnabledToggle);
            uapOpsEnabledScope.tooltip = L10n.S.SettingsUapOpsEnabledTooltip;

            // Module toggle rows (design section 7.1): real toggles over
            // PanelSettings.uapOpsModules. Phase 5a shipped "core"; Phase 5b
            // stream A adds "prefab" (section 7.3) and "editor" (section 3c
            // T2/1.2), both default ON alongside "core".
            _uapOpsCoreModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleCoreLabel);
            _uapOpsCoreModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsCoreModuleToggle.AddToClassList("uap-switch");
            _uapOpsCoreModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsCoreModuleToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsModules.Contains("core"));
            _uapOpsCoreModuleToggle.RegisterValueChangedCallback(OnUapOpsCoreModuleToggleChanged);
            section.Add(_uapOpsCoreModuleToggle);
            AddHint(section, L10n.S.SettingsUapOpsModuleCoreHint).AddToClassList("uap-settings-hint--child");

            _uapOpsPrefabModuleToggle = new Toggle(L10n.S.SettingsUapOpsModulePrefabLabel);
            _uapOpsPrefabModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsPrefabModuleToggle.AddToClassList("uap-switch");
            _uapOpsPrefabModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsPrefabModuleToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsModules.Contains("prefab"));
            _uapOpsPrefabModuleToggle.RegisterValueChangedCallback(OnUapOpsPrefabModuleToggleChanged);
            section.Add(_uapOpsPrefabModuleToggle);
            AddModuleHint(section, _uapOpsPrefabModuleToggle, "prefab", L10n.S.SettingsUapOpsModulePrefabHint);

            _uapOpsEditorModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleEditorLabel);
            _uapOpsEditorModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsEditorModuleToggle.AddToClassList("uap-switch");
            _uapOpsEditorModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsEditorModuleToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsModules.Contains("editor"));
            _uapOpsEditorModuleToggle.RegisterValueChangedCallback(OnUapOpsEditorModuleToggleChanged);
            section.Add(_uapOpsEditorModuleToggle);
            // "editor" keeps uap_editor_screenshot/uap_editor_execute_menu/
            // uap_editor_select in Core even with Pro absent (only the
            // lightmap/Bakery bake tools moved out), so this toggle is never disabled -- always show the
            // ordinary hint, never the Pro-absent one.
            AddHint(section, L10n.S.SettingsUapOpsModuleEditorHint).AddToClassList("uap-settings-hint--child");

            // 2026-09-07: Scene-view 3D markers (default ON, generation 2).
            _uapOpsMarkersModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleMarkersLabel);
            _uapOpsMarkersModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsMarkersModuleToggle.AddToClassList("uap-switch");
            _uapOpsMarkersModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsMarkersModuleToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsModules.Contains("markers"));
            _uapOpsMarkersModuleToggle.RegisterValueChangedCallback(OnUapOpsMarkersModuleToggleChanged);
            section.Add(_uapOpsMarkersModuleToggle);
            AddHint(section, L10n.S.SettingsUapOpsModuleMarkersHint).AddToClassList("uap-settings-hint--child");

            // Phase 5b stream B adds "anim" (design section 1.2/8.8) --
            // default OFF, unlike core/prefab/editor above, so its toggle
            // simply reflects the (initially absent) module list entry.
            _uapOpsAnimModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleAnimLabel);
            _uapOpsAnimModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsAnimModuleToggle.AddToClassList("uap-switch");
            _uapOpsAnimModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsAnimModuleToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsModules.Contains("anim"));
            _uapOpsAnimModuleToggle.RegisterValueChangedCallback(OnUapOpsAnimModuleToggleChanged);
            section.Add(_uapOpsAnimModuleToggle);
            AddModuleHint(section, _uapOpsAnimModuleToggle, "anim", L10n.S.SettingsUapOpsModuleAnimHint);

            // Phase 5c "ui" module (UI Toolkit window automation), default
            // OFF like anim. The module existed at the ToolRegistry /
            // PanelSettings.uapOpsModules level since Phase 5c but never had
            // a switch here (the 2026-09-11 core/pro split note recorded
            // that gap and left it for a later task); USER-GUIDE section 12
            // has described this row all along. Since the split its tools
            // ship in Agent Panel Pro, so with Pro absent it is disabled
            // with the same explanatory hint as prefab/anim.
            _uapOpsUiModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleUiLabel);
            _uapOpsUiModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsUiModuleToggle.AddToClassList("uap-switch");
            _uapOpsUiModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsUiModuleToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapOpsModules.Contains("ui"));
            _uapOpsUiModuleToggle.RegisterValueChangedCallback(OnUapOpsUiModuleToggleChanged);
            section.Add(_uapOpsUiModuleToggle);
            AddModuleHint(section, _uapOpsUiModuleToggle, "ui", L10n.S.SettingsUapOpsModuleUiHint);

            // 2026-09-15 "authoring" module (Extension Profile scaffolding
            // and validation), default OFF like anim/ui. Its tools ship in
            // Agent Panel Pro, so with Pro absent the row is disabled with
            // the same explanatory hint.
            _uapOpsAuthoringModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleAuthoringLabel);
            _uapOpsAuthoringModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsAuthoringModuleToggle.AddToClassList("uap-switch");
            _uapOpsAuthoringModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsAuthoringModuleToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsModules.Contains("authoring"));
            _uapOpsAuthoringModuleToggle.RegisterValueChangedCallback(OnUapOpsAuthoringModuleToggleChanged);
            section.Add(_uapOpsAuthoringModuleToggle);
            AddModuleHint(section, _uapOpsAuthoringModuleToggle, "authoring",
                L10n.S.SettingsUapOpsModuleAuthoringHint);

            // 2026-09-15 "avatar" module (avatar measurement and the VRChat
            // Performance Rank), default OFF and Pro-provided like the two
            // rows above it.
            _uapOpsAvatarModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleAvatarLabel);
            _uapOpsAvatarModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsAvatarModuleToggle.AddToClassList("uap-switch");
            _uapOpsAvatarModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsAvatarModuleToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsModules.Contains("avatar"));
            _uapOpsAvatarModuleToggle.RegisterValueChangedCallback(OnUapOpsAvatarModuleToggleChanged);
            section.Add(_uapOpsAvatarModuleToggle);
            AddModuleHint(section, _uapOpsAvatarModuleToggle, "avatar",
                L10n.S.SettingsUapOpsModuleAvatarHint);

            // 2026-09-15 "batch" module (many operations behind one
            // permission card), default OFF and Pro-provided like the rows
            // above it. Deliberately opt-in: it changes what one approval
            // covers.
            _uapOpsBatchModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleBatchLabel);
            _uapOpsBatchModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsBatchModuleToggle.AddToClassList("uap-switch");
            _uapOpsBatchModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsBatchModuleToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsModules.Contains("batch"));
            _uapOpsBatchModuleToggle.RegisterValueChangedCallback(OnUapOpsBatchModuleToggleChanged);
            section.Add(_uapOpsBatchModuleToggle);
            AddModuleHint(section, _uapOpsBatchModuleToggle, "batch",
                L10n.S.SettingsUapOpsModuleBatchHint);

            // 2026-09-15 "tests" module (runs the project's own EditMode
            // tests), default OFF and Pro-provided like the rows above it.
            // Opt-in for a reason of its own: a test is project code, and
            // running it can write assets or dirty the open scene.
            _uapOpsTestsModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleTestsLabel);
            _uapOpsTestsModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsTestsModuleToggle.AddToClassList("uap-switch");
            _uapOpsTestsModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsTestsModuleToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsModules.Contains("tests"));
            _uapOpsTestsModuleToggle.RegisterValueChangedCallback(OnUapOpsTestsModuleToggleChanged);
            section.Add(_uapOpsTestsModuleToggle);
            AddTestsModuleHint(section, _uapOpsTestsModuleToggle,
                L10n.S.SettingsUapOpsModuleTestsHint);

            // 2026-09-15 "fx" module (Particle System module editing),
            // default OFF and Pro-provided like the rows above it. Opt-in
            // because it writes scene objects, not because it is dangerous
            // to read.
            _uapOpsFxModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleFxLabel);
            _uapOpsFxModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsFxModuleToggle.AddToClassList("uap-switch");
            _uapOpsFxModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsFxModuleToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsModules.Contains("fx"));
            _uapOpsFxModuleToggle.RegisterValueChangedCallback(OnUapOpsFxModuleToggleChanged);
            section.Add(_uapOpsFxModuleToggle);
            AddModuleHint(section, _uapOpsFxModuleToggle, "fx",
                L10n.S.SettingsUapOpsModuleFxHint);

            // 2026-09-15 "mesh" module (Mesh generation from numbers),
            // default OFF and Pro-provided like the rows above it. Opt-in
            // because it writes assets and scene objects; its own row
            // rather than a rider on "editor" so the row's one line says
            // exactly what it lets the agent make.
            _uapOpsMeshModuleToggle = new Toggle(L10n.S.SettingsUapOpsModuleMeshLabel);
            _uapOpsMeshModuleToggle.AddToClassList("uap-settings-field");
            _uapOpsMeshModuleToggle.AddToClassList("uap-switch");
            _uapOpsMeshModuleToggle.AddToClassList("uap-settings-field--child");
            _uapOpsMeshModuleToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsModules.Contains("mesh"));
            _uapOpsMeshModuleToggle.RegisterValueChangedCallback(OnUapOpsMeshModuleToggleChanged);
            section.Add(_uapOpsMeshModuleToggle);
            AddModuleHint(section, _uapOpsMeshModuleToggle, "mesh",
                L10n.S.SettingsUapOpsModuleMeshHint);

            // Script validation gate (design section 7.4/8.2 B1). Warning-
            // styled (AddWarning, not AddHint) per design-notes/2026-08-04-
            // settings-annotation-load.md section 4: one of the three
            // settings whose annotation explains what ENABLING it does, so
            // the short line must stay visible on the card without
            // hovering -- "a warning that only reads on hover does not
            // function as a warning" (same section). The fuller detail is
            // still one hover away via the 3-arg overload's tooltip.
            _uapOpsGateEnabledToggle = new Toggle(L10n.S.SettingsUapOpsGateEnabledLabel);
            _uapOpsGateEnabledToggle.AddToClassList("uap-settings-field");
            _uapOpsGateEnabledToggle.AddToClassList("uap-switch");
            _uapOpsGateEnabledToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.uapScriptGateEnabled);
            _uapOpsGateEnabledToggle.RegisterValueChangedCallback(OnUapOpsGateEnabledChanged);
            VisualElement gateEnabledScope = AddHintScope(section);
            gateEnabledScope.Add(_uapOpsGateEnabledToggle);
            _gateWarningLabel = AddWarning(gateEnabledScope, L10n.S.SettingsUapOpsGateEnabledHint,
                L10n.S.SettingsUapOpsGateEnabledTooltip);
            AddHint(AddHintScope(section),
                L10n.F(L10n.S.SettingsUapOpsStagingFolderHintFmt, ScriptGate.StagingFolder),
                L10n.S.SettingsUapOpsStagingFolderTooltip);

            // Auto-continue after compile (Phase 5c L3(3), design section
            // 3/8.5): a plain bool toggle added directly to this
            // column-hosted `section`, same shape as every other toggle in
            // this card -- no flex-shrink/min-width risk (LAYOUT RULE only
            // applies to a BaseField placed in a flex ROW alongside
            // siblings; this one has none). Deliberately does NOT call
            // RefreshReconnectHint/AgentHub.RequestAutoApplyReconnect --
            // PanelSettings.uapOpsAutoContinueAfterCompile's own doc
            // comment is explicit that this field is read live at the
            // moment a reload completes, never baked into a spawn
            // argument, and is deliberately absent from
            // SettingsChangeDetector.RequiresReconnect. Warning-styled for
            // the same "must stay readable without hovering" reason as
            // the script-validation gate above.
            _uapOpsAutoContinueToggle = new Toggle(L10n.S.SettingsAutoContinueLabel);
            _uapOpsAutoContinueToggle.AddToClassList("uap-settings-field");
            _uapOpsAutoContinueToggle.AddToClassList("uap-switch");
            _uapOpsAutoContinueToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile);
            _uapOpsAutoContinueToggle.RegisterValueChangedCallback(OnUapOpsAutoContinueToggleChanged);
            VisualElement autoContinueScope = AddHintScope(section);
            autoContinueScope.Add(_uapOpsAutoContinueToggle);
            _autoContinueWarningLabel = AddWarning(autoContinueScope, L10n.S.SettingsAutoContinueHelp,
                L10n.S.SettingsAutoContinueTooltip);

            // Interrupted-turn auto-continue (design note 2026-09-06 section
            // 5-1): same shape and same live-read contract as the compile
            // toggle above -- PanelSettings.autoContinueInterruptedTurn is
            // read by ReloadLifecycle when a reload completes, never baked
            // into a spawn, so no reconnect hint either.
            _autoContinueInterruptedToggle = new Toggle(L10n.S.SettingsAutoContinueInterruptedLabel);
            _autoContinueInterruptedToggle.AddToClassList("uap-settings-field");
            _autoContinueInterruptedToggle.AddToClassList("uap-switch");
            _autoContinueInterruptedToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.autoContinueInterruptedTurn);
            _autoContinueInterruptedToggle.RegisterValueChangedCallback(OnAutoContinueInterruptedToggleChanged);
            VisualElement autoContinueInterruptedScope = AddHintScope(section);
            autoContinueInterruptedScope.Add(_autoContinueInterruptedToggle);
            _autoContinueInterruptedWarningLabel = AddWarning(autoContinueInterruptedScope,
                L10n.S.SettingsAutoContinueInterruptedHelp,
                L10n.S.SettingsAutoContinueInterruptedTooltip);

            // Play Mode reload advisor (design note 2026-09-10 section 4):
            // whether Play cuts a turn short at all is entirely a PROJECT
            // setting (EditorSettings.enterPlayModeOptions*) this package
            // does not own and, per that note, must not change on the
            // user's behalf -- so this is a read-only hint plus a link to
            // where the user can change it themselves, not a toggle.
            // PlayModeReloadAdvisor.ReloadsOnPlayNow() is re-read whenever
            // RefreshWarningTones runs (see that method) so the hint stays
            // correct if the user edits Project Settings while the panel
            // is open.
            VisualElement playModeReloadScope = AddHintScope(section);
            _playModeReloadHintLabel = AddHint(playModeReloadScope,
                PlayModeReloadAdvisor.ReloadsOnPlayNow()
                    ? L10n.S.SettingsPlayModeReloadOnHint
                    : L10n.S.SettingsPlayModeReloadOffHint,
                L10n.S.SettingsPlayModeReloadTooltip);
            var openProjectSettings = new Button(OnOpenPlayModeProjectSettingsClicked)
            {
                text = L10n.S.SettingsPlayModeOpenProjectSettingsButton
            };
            openProjectSettings.AddToClassList("uap-settings-link-btn");
            playModeReloadScope.Add(openProjectSettings);

            // UXIA-3/4: the auto-approve level itself moved next to the
            // permission mode (BuildConversationSection ->
            // BuildAutoApproveLevelField) -- it is permission policy, not a
            // UapOps module toggle, and buried at the bottom of this
            // section nobody found it. Only this cross-reference hint
            // remains so a user who looks for it HERE (its old home, and
            // the section whose tools it governs) is pointed to the field.
            VisualElement autoApproveMovedScope = AddHintScope(section);
            AddHint(autoApproveMovedScope, L10n.S.SettingsAutoApproveMovedHint,
                L10n.S.SettingsAutoApproveMovedTooltip);

            _uapOpsStatusLabel = new Label(string.Empty);
            _uapOpsStatusLabel.AddToClassList("uap-settings-hint");
            _uapOpsStatusLabel.AddToClassList("uap-settings-status");
            _uapOpsStatusLabel.enableRichText = false;
            section.Add(_uapOpsStatusLabel);

            RefreshUapOpsStatus();
        }

        /// <summary>
        /// Module toggle rows whose tools can live entirely in an absent
        /// add-on package (2026-09-11 core/pro split, design note
        /// docs/design-notes/2026-09-11-core-pro-split.md "seam 4"):
        /// when <see cref="UapOpsServer.Registry"/> currently has zero
        /// tools registered for <paramref name="module"/> (Agent Panel Pro
        /// not installed), the toggle is disabled, its hint keeps the
        /// normal "what these tools do" sentence and appends that the
        /// add-on is required (<see cref="ResolveModuleHint"/>), and the
        /// row carries a tooltip saying what Pro is and how the toggle
        /// comes back (2026-09-12 core-only wording note: the previous
        /// "Provided by Agent Panel Pro (not installed)." told a reader who
        /// had only ever seen the Core package neither what the module did
        /// nor what "Pro" was). A fresh domain reload after installing Pro
        /// re-evaluates this the next time the Settings view is built.
        /// </summary>
        private void AddModuleHint(VisualElement section, Toggle moduleToggle, string module, string normalHint)
        {
            bool hasTools = UapOpsServer.Registry.HasToolsInModule(module);
            if (!hasTools)
            {
                _uapOpsModulesWithoutTools.Add(module);
                moduleToggle.SetEnabled(false);
                moduleToggle.tooltip = L10n.S.SettingsUapOpsProAbsentTooltip;
            }
            else
            {
                _uapOpsModulesWithoutTools.Remove(module);
            }
            Label hint = AddHint(section, ResolveModuleHint(hasTools, normalHint));
            hint.AddToClassList("uap-settings-hint--child");
            if (!hasTools)
            {
                hint.tooltip = L10n.S.SettingsUapOpsProAbsentTooltip;
            }
        }

        /// <summary>
        /// The "tests" row, whose disabled state has TWO possible reasons
        /// and only one of them is "buy Pro". uap_test_run lives in an
        /// assembly constrained to com.unity.test-framework, so in a
        /// project that has Pro but not that package the tool is compiled
        /// out and the row greys out for a reason the generic Pro sentence
        /// would state wrongly. "prefab" is Pro's too and is registered
        /// unconditionally, so it answers "is Pro here at all" without this
        /// row having to guess.
        /// </summary>
        private void AddTestsModuleHint(VisualElement section, Toggle moduleToggle, string normalHint)
        {
            bool hasTools = UapOpsServer.Registry.HasToolsInModule("tests");
            bool proPresent = UapOpsServer.Registry.HasToolsInModule("prefab");
            if (hasTools)
            {
                _uapOpsModulesWithoutTools.Remove("tests");
            }
            else
            {
                _uapOpsModulesWithoutTools.Add("tests");
                moduleToggle.SetEnabled(false);
                moduleToggle.tooltip = proPresent
                    ? L10n.S.SettingsUapOpsTestFrameworkAbsentTooltip
                    : L10n.S.SettingsUapOpsProAbsentTooltip;
            }
            Label hint = AddHint(section, ResolveTestsModuleHint(hasTools, proPresent, normalHint));
            hint.AddToClassList("uap-settings-hint--child");
            if (!hasTools)
            {
                hint.tooltip = moduleToggle.tooltip;
            }
        }

        /// <summary>
        /// Pure text rule behind <see cref="AddTestsModuleHint"/>: the
        /// module's own hint, then the sentence naming whichever thing is
        /// actually missing -- the Test Framework package when Pro is
        /// already installed, Pro itself otherwise.
        /// </summary>
        internal static string ResolveTestsModuleHint(bool hasTools, bool proPresent, string normalHint)
        {
            if (hasTools)
            {
                return normalHint;
            }
            if (!proPresent)
            {
                return ResolveModuleHint(false, normalHint);
            }
            return L10n.F(L10n.S.SettingsUapOpsTestFrameworkAbsentHintFmt, normalHint ?? string.Empty).Trim();
        }

        /// <summary>
        /// Pure text rule behind <see cref="AddModuleHint"/>: the module's
        /// own hint when its tools are registered, otherwise that same
        /// hint followed by the Pro-required sentence -- never the Pro
        /// sentence alone, so the reader still learns what the disabled
        /// toggle would enable.
        /// </summary>
        internal static string ResolveModuleHint(bool hasTools, string normalHint)
        {
            if (hasTools)
            {
                return normalHint;
            }
            return L10n.F(L10n.S.SettingsUapOpsProAbsentHintFmt, normalHint ?? string.Empty).Trim();
        }

        /// <summary>
        /// A module switch is interactive only while the master switch is
        /// on AND some package registered tools for it; see
        /// <see cref="ResolveModuleToggleEnabled"/> for the pure rule.
        /// </summary>
        private bool ModuleToggleEnabled(bool masterEnabled, string module)
        {
            return ResolveModuleToggleEnabled(masterEnabled, !_uapOpsModulesWithoutTools.Contains(module));
        }

        internal static bool ResolveModuleToggleEnabled(bool masterEnabled, bool moduleHasTools)
        {
            return masterEnabled && moduleHasTools;
        }

        private void OnUapOpsEnabledChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.uapOpsEnabled = evt.newValue;
            PanelStateStore.instance.SaveNow();
            AgentHub.RequestAutoApplyReconnect();
            RefreshUapOpsStatus();
        }

        // Each handler below calls BOTH AgentHub.ApplyUapOpsModulesChanged
        // (instant live tool-catalog update via mcp_reconnect) AND
        // RefreshReconnectHint/AgentHub.RequestAutoApplyReconnect (2026-08-02
        // review fix, Stream C1 regression: uapOpsModules now also drives
        // the --append-system-prompt steering text, which has no live
        // update path -- see PanelSettings.uapOpsModules' doc comment).
        // Dropping either call reintroduces a real bug: dropping
        // ApplyUapOpsModulesChanged would delay the ACTUAL tool catalog
        // change until the next reconnect; dropping the reconnect calls
        // would leave the steering text silently stale for the rest of the
        // session even though the tool catalog already moved on.

        private void OnUapOpsCoreModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("core"))
                {
                    modules.Add("core");
                }
            }
            else
            {
                modules.Remove("core");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsPrefabModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("prefab"))
                {
                    modules.Add("prefab");
                }
            }
            else
            {
                modules.Remove("prefab");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsEditorModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("editor"))
                {
                    modules.Add("editor");
                }
            }
            else
            {
                modules.Remove("editor");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsAnimModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("anim"))
                {
                    modules.Add("anim");
                }
            }
            else
            {
                modules.Remove("anim");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsUiModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("ui"))
                {
                    modules.Add("ui");
                }
            }
            else
            {
                modules.Remove("ui");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsBatchModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("batch"))
                {
                    modules.Add("batch");
                }
            }
            else
            {
                modules.Remove("batch");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsTestsModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("tests"))
                {
                    modules.Add("tests");
                }
            }
            else
            {
                modules.Remove("tests");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsFxModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("fx"))
                {
                    modules.Add("fx");
                }
            }
            else
            {
                modules.Remove("fx");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsMeshModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("mesh"))
                {
                    modules.Add("mesh");
                }
            }
            else
            {
                modules.Remove("mesh");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsAvatarModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("avatar"))
                {
                    modules.Add("avatar");
                }
            }
            else
            {
                modules.Remove("avatar");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsAuthoringModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("authoring"))
                {
                    modules.Add("authoring");
                }
            }
            else
            {
                modules.Remove("authoring");
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsMarkersModuleToggleChanged(ChangeEvent<bool> evt)
        {
            List<string> modules = PanelStateStore.instance.Settings.uapOpsModules;
            if (evt.newValue)
            {
                if (!modules.Contains("markers"))
                {
                    modules.Add("markers");
                }
            }
            else
            {
                modules.Remove("markers");
                // Switching the module off also takes the agent's markers
                // off the screen; the user's own pins stay.
                Colloid.AgentPanel.Ops.Markers.SceneMarkerStore.Clear(false);
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.ApplyUapOpsModulesChanged();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsGateEnabledChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.uapScriptGateEnabled = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshWarningTones();
            // Design section 8.7: unlike before that revision, this toggle
            // now ALSO controls a next-spawn-only argument (`--settings
            // <path>`, the PreToolUse hook) -- see PanelSettings.
            // uapScriptGateEnabled's doc comment. The can_use_tool half of
            // the gate stays live either way, but the hook half needs the
            // same auto-apply-reconnect nudge uapOpsEnabled already gets
            // above, or a toggle flip would silently do nothing until a
            // manual reconnect.
            AgentHub.RequestAutoApplyReconnect();
        }

        private void OnUapOpsAutoContinueToggleChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.uapOpsAutoContinueAfterCompile = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshWarningTones();
            // No RefreshReconnectHint/RequestAutoApplyReconnect call here --
            // see the field's build-site comment in BuildUapOpsSection.
        }

        private void OnAutoContinueInterruptedToggleChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.autoContinueInterruptedTurn = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshWarningTones();
        }

        /// <summary>
        /// Persists PanelSettings.autoApproveLevel and immediately
        /// re-evaluates any pending permission card against the new level
        /// (AgentHub.ApplyAutoApproveLevelChanged): the whole point of
        /// raising this setting is to unblock a card that is ALREADY on
        /// screen, not merely to change policy for the next request (see
        /// that method's own doc comment). Deliberately does NOT call
        /// RequestAutoApplyReconnect/RefreshReconnectHint -- this field is
        /// read LIVE at decision time (AutoApprovePolicy.ShouldAutoApprove
        /// via AgentHub.TryAutoApproveUapOpsTool), never a spawn argument,
        /// same as permissionBeep and the superseded autoApproveReadOnlyOps
        /// toggle before it. Safe to call ApplyAutoApproveLevelChanged even
        /// when the level was LOWERED or nothing is pending -- it is then a
        /// no-op, per that method's doc comment.
        /// </summary>
        private void OnAutoApproveLevelChanged(ChangeEvent<UapAutoApproveLevel> evt)
        {
            // UXA-3: the SECOND frictionless path to AllUnityOps (the header
            // menu is the first) -- both gate through the same shared
            // confirmation, BEFORE the assignment, because
            // ApplyAutoApproveLevelChanged immediately auto-answers a
            // permission request already waiting on screen. On cancel the
            // dropdown -- which already shows the new value -- reverts via
            // SetValueWithoutNotify (a plain value set would re-enter this
            // handler).
            if (!AutoApproveLevelLabels.ConfirmEscalationIfNeeded(
                    PanelStateStore.instance.Settings.autoApproveLevel, evt.newValue))
            {
                _autoApproveLevelField.SetValueWithoutNotify(evt.previousValue);
                return;
            }
            PanelStateStore.instance.Settings.autoApproveLevel = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshWarningTones();
            AgentHub.ApplyAutoApproveLevelChanged();
        }

        /// <summary>
        /// Status line precedence: the master toggle's OWN current setting
        /// wins over whatever UapOpsServer happens to be doing right now --
        /// disabling the toggle must read "Disabled" immediately, even
        /// though the server itself only actually stops at the next
        /// reconnect (matching every other next-spawn-only field's
        /// "Reconnect now" pending-hint pattern rather than claiming a live
        /// effect that has not happened yet).
        /// </summary>
        private void RefreshUapOpsStatus()
        {
            if (_uapOpsStatusLabel == null)
            {
                return;
            }
            RefreshWarningTones();
            bool enabledSetting = PanelStateStore.instance.Settings.uapOpsEnabled;
            // The module switches are children of the master switch:
            // greyed while it is off, so the hierarchy reads without a hint.
            // A module whose tools are not installed (AddModuleHint) stays
            // greyed regardless -- this refresh used to re-enable it.
            _uapOpsCoreModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "core"));
            _uapOpsPrefabModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "prefab"));
            _uapOpsEditorModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "editor"));
            _uapOpsMarkersModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "markers"));
            _uapOpsAnimModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "anim"));
            _uapOpsUiModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "ui"));
            _uapOpsAuthoringModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "authoring"));
            _uapOpsAvatarModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "avatar"));
            _uapOpsBatchModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "batch"));
            _uapOpsTestsModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "tests"));
            _uapOpsFxModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "fx"));
            _uapOpsMeshModuleToggle?.SetEnabled(ModuleToggleEnabled(enabledSetting, "mesh"));
            _uapOpsStatusLabel.text = !enabledSetting
                ? L10n.S.SettingsUapOpsStatusDisabled
                : (UapOpsServer.IsRunning
                    ? L10n.F(L10n.S.SettingsUapOpsStatusRunningFmt,
                        UapOpsServer.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : L10n.S.SettingsUapOpsStatusStopped);

            // Keeps this dropdown's displayed value in sync with the header
            // control -- both surfaces write the SAME PanelSettings.
            // autoApproveLevel field via AgentHub.ApplyAutoApproveLevelChanged,
            // so a level raised from the header (e.g. to unblock a pending
            // permission card) must be reflected here too on the next
            // OnHubChanged tick, exactly like _subagentCostPolicyField's
            // sync in RefreshModelSection above. SetValueWithoutNotify avoids
            // re-entering OnAutoApproveLevelChanged (which would otherwise
            // re-persist the same value and needlessly re-run
            // ApplyAutoApproveLevelChanged on every hub tick).
            UapAutoApproveLevel currentLevel = PanelStateStore.instance.Settings.autoApproveLevel;
            if (_autoApproveLevelField != null && _autoApproveLevelField.value != currentLevel)
            {
                _autoApproveLevelField.SetValueWithoutNotify(currentLevel);
            }
        }

        // -- (b2) Extension profiles (design section 3b/C4) ------------------------

        /// <summary>
        /// "Extension profiles" card (design section 3b/C4): a master
        /// toggle over the whole feature, plus one row per DETECTED profile
        /// (bundled or user) showing its trust status and, for a user
        /// profile, an approve/revoke affordance. Non-detected profiles are
        /// not listed at all -- there is nothing actionable about an SDK
        /// that is not even present in this project.
        /// </summary>
        private void BuildExtensionProfilesSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsSectionExtensionProfiles,
                "d_ScriptableObject Icon", IconLoader.GlyphFile, "profiles");
            AddHint(AddHintScope(section), L10n.A(L10n.S.SettingsExtensionProfilesHint),
                L10n.A(L10n.S.SettingsExtensionProfilesTooltip));

            _extensionProfilesEnabledToggle = new Toggle(L10n.S.SettingsExtensionProfilesEnabledLabel);
            _extensionProfilesEnabledToggle.AddToClassList("uap-settings-field");
            _extensionProfilesEnabledToggle.AddToClassList("uap-switch");
            _extensionProfilesEnabledToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.extensionProfilesEnabled);
            _extensionProfilesEnabledToggle.RegisterValueChangedCallback(OnExtensionProfilesEnabledChanged);
            VisualElement extensionProfilesEnabledScope = AddHintScope(section);
            extensionProfilesEnabledScope.Add(_extensionProfilesEnabledToggle);
            extensionProfilesEnabledScope.tooltip = L10n.S.SettingsExtensionProfilesEnabledTooltip;

            _extensionProfilesHost = new VisualElement();
            _extensionProfilesHost.AddToClassList("uap-settings-qa-list");
            section.Add(_extensionProfilesHost);

            RefreshExtensionProfilesSection();
        }

        private void OnExtensionProfilesEnabledChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.extensionProfilesEnabled = evt.newValue;
            PanelStateStore.instance.SaveNow();
            AgentHub.RequestAutoApplyReconnect();
        }

        /// <summary>
        /// Rebuilds the detected-profile row list from
        /// <see cref="ExtensionProfileService.BuildStatuses"/> -- the same
        /// source of truth AgentHub's --append-system-prompt composition
        /// uses, so this list can never show a profile as "approved" that
        /// is not actually being injected (or vice versa). Called from
        /// RefreshAll() (view activation) and after every approve/revoke
        /// action; deliberately NOT wired into OnHubChanged, which fires on
        /// every streaming token and would otherwise rebuild this
        /// (file-reading) list constantly and blow away an in-progress
        /// dialog interaction, mirroring RefreshModelSection's rationale
        /// for the agent-override rows.
        /// </summary>
        private void RefreshExtensionProfilesSection()
        {
            if (_extensionProfilesHost == null)
            {
                return;
            }
            _extensionProfilesHost.Clear();
            List<ExtensionProfileStatus> statuses = ExtensionProfileService.BuildStatuses(
                AgentHub.ProjectRoot, PanelStateStore.instance.Settings.approvedProfileHashes);
            bool anyDetected = false;
            bool anyBundled = false;
            for (int i = 0; i < statuses.Count; i++)
            {
                anyBundled |= statuses[i].IsBundled;
                if (!statuses[i].Detected)
                {
                    continue;
                }
                anyDetected = true;
                AddExtensionProfileRow(statuses[i]);
            }
            if (!anyDetected)
            {
                // 2026-09-12 core-only wording: "nothing detected" is only
                // an honest summary when there was something to detect
                // WITH. The Core package ships no bundled profile of its
                // own (2026-09-11 core/pro split), so with no add-on
                // installed the empty list is explained by what is missing
                // -- the bundled profiles -- not by the project's contents.
                if (anyBundled)
                {
                    AddHint(_extensionProfilesHost, L10n.S.SettingsExtensionProfilesEmptyHint);
                }
                else
                {
                    AddHint(_extensionProfilesHost, L10n.S.SettingsExtensionProfilesNoBundledHint)
                        .tooltip = L10n.S.SettingsExtensionProfilesNoBundledTooltip;
                }
            }
            AddExtensionProfileGapAffordance(statuses);
        }

        /// <summary>
        /// Names the installed packages no profile covers, and hands the
        /// user a ready-made request to have one drafted.
        ///
        /// Shown only where the authoring tools exist to act on it (the
        /// "authoring" module ships in Agent Panel Pro), because a list of
        /// gaps with no way to close them is just a complaint. The request
        /// goes to the clipboard rather than straight into the composer:
        /// the user pastes it into a chat when they are ready, which keeps
        /// this card from reaching across the panel and overwriting
        /// whatever they were in the middle of typing.
        /// </summary>
        private void AddExtensionProfileGapAffordance(List<ExtensionProfileStatus> statuses)
        {
            if (!UapOpsServer.Registry.HasToolsInModule("authoring"))
            {
                return;
            }

            var profiles = new List<ExtensionProfile>();
            for (int i = 0; i < statuses.Count; i++)
            {
                if (statuses[i].Profile != null)
                {
                    profiles.Add(statuses[i].Profile);
                }
            }

            int total;
            List<string> uncovered = ExtensionProfileGapFinder.FindUncovered(
                ExtensionProfileDetectionCache.InstalledPackageIds(AgentHub.ProjectRoot),
                profiles, ExtensionProfileGapFinder.DefaultMaxResults, out total);
            if (uncovered.Count == 0)
            {
                return;
            }

            string joined = string.Join(", ", uncovered.ToArray());
            string hintText = total > uncovered.Count
                ? L10n.F(L10n.S.SettingsExtensionProfilesGapsMoreFmt, joined, total - uncovered.Count)
                : L10n.F(L10n.S.SettingsExtensionProfilesGapsFmt, joined);
            AddHint(_extensionProfilesHost, hintText).tooltip =
                L10n.S.SettingsExtensionProfilesGapsTooltip;

            VisualElement row = AddRow(_extensionProfilesHost);
            var status = new Label(string.Empty);
            status.AddToClassList("uap-settings-hint");
            var copy = new Button(delegate
            {
                EditorGUIUtility.systemCopyBuffer =
                    L10n.F(L10n.S.SettingsExtensionProfilesGapsRequestFmt, joined);
                status.text = L10n.S.SettingsExtensionProfilesGapsCopied;
            })
            {
                text = L10n.S.SettingsExtensionProfilesGapsButton,
            };
            copy.AddToClassList("uap-settings-btn");
            copy.tooltip = L10n.S.SettingsExtensionProfilesGapsTooltip;
            row.Add(copy);
            row.Add(status);
        }

        private void AddExtensionProfileRow(ExtensionProfileStatus status)
        {
            VisualElement row = AddRow(_extensionProfilesHost);

            // Deliberately NOT uap-settings-qa-label (the Quick Actions
            // column: a fixed 110px non-shrinking box sized for short
            // user-authored names). An SDK display name is longer than
            // that, so it used to be clipped while the status text wrapped
            // onto its own baseline -- design note 2026-08-02-composer-
            // newline-and-profile-row.md section 2.
            var nameLabel = new Label(IconLoader.SanitizeForDisplay(status.Profile.DisplayName));
            nameLabel.AddToClassList("uap-settings-profile-name");
            nameLabel.enableRichText = false;
            row.Add(nameLabel);

            string statusText = status.IsBundled
                ? L10n.S.SettingsExtensionProfilesStatusBundled
                : (status.Trusted
                    ? L10n.S.SettingsExtensionProfilesStatusApproved
                    : L10n.S.SettingsExtensionProfilesStatusPending);
            // Inline status chip: the hint style is a BLOCK style
            // (white-space: normal + vertical margins) and wrapped/
            // misaligned inside this row.
            var statusLabel = new Label(statusText);
            statusLabel.AddToClassList("uap-settings-profile-status");
            statusLabel.enableRichText = false;
            row.Add(statusLabel);

            if (status.IsBundled)
            {
                return;
            }
            if (status.Trusted)
            {
                var revoke = new Button(() => OnRevokeExtensionProfileClicked(status))
                    { text = L10n.S.SettingsExtensionProfilesRevokeButton };
                revoke.AddToClassList("uap-settings-btn");
                row.Add(revoke);
            }
            else
            {
                var approve = new Button(() => OnApproveExtensionProfileClicked(status))
                    { text = L10n.S.SettingsExtensionProfilesApproveButton };
                approve.AddToClassList("uap-settings-btn");
                approve.AddToClassList("uap-settings-btn--primary");
                row.Add(approve);
            }
        }

        /// <summary>
        /// Approval flow (design section 8.2 B3 / C4): shows the FULL
        /// instruction text that would be injected -- never a summary --
        /// before recording approval, so the user is looking at exactly
        /// what AgentHub will append to --append-system-prompt while this
        /// profile stays detected with this exact content. Approving pins
        /// the machine-bound approval TOKEN for status.ContentHashHex
        /// (computed from the file's raw bytes at the last
        /// RefreshExtensionProfilesSection call; OPS-11) into
        /// PanelSettings.approvedProfileHashes; any subsequent edit to the
        /// file changes its hash -- and therefore its token -- and this row
        /// reverts to "Pending" on the next refresh
        /// (ExtensionProfileTrust.IsTrusted).
        /// </summary>
        private void OnApproveExtensionProfileClicked(ExtensionProfileStatus status)
        {
            string instructionText = status.Profile.InstructionLines != null
                ? string.Join("\n", status.Profile.InstructionLines.ToArray())
                : string.Empty;
            bool confirmed = EditorUtility.DisplayDialog(
                L10n.F(L10n.S.SettingsExtensionProfilesApproveDialogTitleFmt, status.Profile.DisplayName),
                L10n.F(L10n.S.SettingsExtensionProfilesApproveDialogBodyFmt, instructionText),
                L10n.S.SettingsExtensionProfilesApproveDialogConfirmButton,
                L10n.S.SettingsExtensionProfilesApproveDialogCancelButton);
            if (!confirmed || string.IsNullOrEmpty(status.ContentHashHex))
            {
                return;
            }
            // OPS-11: what gets stored is the machine-bound token, not the
            // raw content hash -- a State.asset shipped inside a malicious
            // project cannot carry a token this machine's salt would mint.
            string token = ExtensionProfileTrust.ComputeApprovalToken(
                MachineApprovalSalt.Get(), status.ContentHashHex);
            List<string> hashes = PanelStateStore.instance.Settings.approvedProfileHashes;
            if (!hashes.Contains(token))
            {
                hashes.Add(token);
            }
            PanelStateStore.instance.SaveNow();
            AgentHub.RequestAutoApplyReconnect();
            RefreshExtensionProfilesSection();
        }

        private void OnRevokeExtensionProfileClicked(ExtensionProfileStatus status)
        {
            if (string.IsNullOrEmpty(status.ContentHashHex))
            {
                return;
            }
            List<string> hashes = PanelStateStore.instance.Settings.approvedProfileHashes;
            hashes.Remove(ExtensionProfileTrust.ComputeApprovalToken(
                MachineApprovalSalt.Get(), status.ContentHashHex));
            // Also drop a legacy raw-hash entry for this profile (pre-OPS-11
            // stores) so revoking cleans the old form out too.
            hashes.Remove(status.ContentHashHex);
            PanelStateStore.instance.SaveNow();
            AgentHub.RequestAutoApplyReconnect();
            RefreshExtensionProfilesSection();
        }

        // -- (b3) uLoop integration (Phase 5c, design section 2/2.5) ---------------
        //
        // Three independent affordances, per the design doc:
        //   1. Status (installed/not) + a one-click install that NEVER edits
        //      manifest.json without an explicit confirmation card showing the
        //      exact diff first -- this package's standing rule (ARCHITECTURE
        //      risk #12) is that the panel does not edit manifest.json, and
        //      this button is a deliberate, user-triggered exception that is
        //      only defensible because the diff is shown, never applied blind.
        //   2. An allowedTools/disallowedTools preset that allows `uloop *`
        //      while shadowing the three subcommands that have frozen this
        //      editor before (`update`/`sync`/`launch`) and deliberately
        //      leaving `execute-dynamic-code` out of BOTH lists so it always
        //      falls through to a permission card.
        //   3. A custom-instructions snippet insert (idempotent, never
        //      clobbers what the user wrote).

        /// <summary>
        /// Builds the "uLoop integration" card: status line, the (state-
        /// dependent) install button plus its confirmation sub-card, and the
        /// two preset buttons with their help text and one-shot "applied"
        /// confirmation labels.
        /// </summary>
        // -- Agent Panel Pro updates (design note 2026-09-12-pro-update-
        // delivery.md section 3.3): the purchaser pastes the product key
        // once; ProRegistryAccess writes Unity's .upmconfig.toml and the
        // project's manifest.json, and the Package Manager takes it from
        // there. The key is never kept by the panel -- the field is cleared
        // after a successful save. ------------------------------------------

        private TextField _proRegistryUrlField;
        private TextField _proKeyField;
        private Button _proApplyButton;
        private Button _proVccButton;
        private Label _proStatusLabel;

        private void BuildProUpdatesSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsSectionProUpdates,
                "d_Package Manager", IconLoader.GlyphOpenWindow, "pro");
            AddHint(AddHintScope(section), L10n.A(L10n.S.SettingsProUpdatesHint),
                L10n.A(L10n.S.SettingsProUpdatesTooltip));

            PanelSettings settings = PanelStateStore.instance.Settings;
            _proRegistryUrlField = new TextField(L10n.S.SettingsProUpdatesUrlLabel);
            _proRegistryUrlField.AddToClassList("uap-settings-field");
            _proRegistryUrlField.SetValueWithoutNotify(EffectiveProRegistryUrl(settings));
            _proRegistryUrlField.RegisterValueChangedCallback(OnProRegistryUrlChanged);
            section.Add(_proRegistryUrlField);

            _proKeyField = new TextField(L10n.S.SettingsProUpdatesKeyLabel);
            _proKeyField.AddToClassList("uap-settings-field");
            _proKeyField.isPasswordField = true;
            section.Add(_proKeyField);

            VisualElement row = AddRow(section);
            _proApplyButton = new Button(OnProApplyClicked) { text = L10n.S.SettingsProUpdatesApplyButton };
            _proApplyButton.AddToClassList("uap-settings-btn");
            _proApplyButton.AddToClassList("uap-settings-btn--primary");
            row.Add(_proApplyButton);

            // Phase 2 (design note section 3.3 item 2): the same key, handed
            // to VCC / ALCOM as the repository's Authorization header.
            _proVccButton = new Button(OnProVccClicked) { text = L10n.S.SettingsProUpdatesVccButton };
            _proVccButton.AddToClassList("uap-settings-btn");
            _proVccButton.tooltip = L10n.S.SettingsProUpdatesVccTooltip;
            row.Add(_proVccButton);

            _proStatusLabel = new Label(string.Empty);
            _proStatusLabel.AddToClassList("uap-settings-hint");
            _proStatusLabel.AddToClassList("uap-settings-status");
            _proStatusLabel.enableRichText = false;
            _proStatusLabel.style.whiteSpace = WhiteSpace.Normal;
            _proStatusLabel.style.display = DisplayStyle.None;
            section.Add(_proStatusLabel);
        }

        /// <summary>Settings value when set, else the built-in default.</summary>
        internal static string EffectiveProRegistryUrl(PanelSettings settings)
        {
            string url = settings != null ? settings.proRegistryUrl : null;
            return string.IsNullOrEmpty(url) ? ProRegistryAccess.DefaultRegistryUrl : url;
        }

        private void OnProRegistryUrlChanged(ChangeEvent<string> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            string value = (evt.newValue ?? string.Empty).Trim();
            settings.proRegistryUrl = value == ProRegistryAccess.DefaultRegistryUrl ? string.Empty : value;
            PanelStateStore.instance.SaveNow();
        }

        private void OnProApplyClicked()
        {
            ProRegistryApplyResult result = ProRegistryAccess.Apply(
                _proRegistryUrlField.value, _proKeyField.value);
            _proStatusLabel.text = DescribeProApplyResult(result);
            _proStatusLabel.EnableInClassList("uap-settings-hint--error", !result.Success);
            _proStatusLabel.style.display = DisplayStyle.Flex;
            if (result.Success)
            {
                _proKeyField.SetValueWithoutNotify(string.Empty);
            }
        }

        private void OnProVccClicked()
        {
            string registryUrl = _proRegistryUrlField.value;
            string listingUrl = ProRegistryAccess.VpmListingUrl(registryUrl);
            string token = listingUrl == null
                ? null
                : ProRegistryAccess.ResolveTokenForVcc(_proKeyField.value, registryUrl,
                    System.IO.File.ReadAllText, ProRegistryAccess.UpmConfigPath());
            string status = DescribeProVccAttempt(listingUrl, token);
            bool ok = listingUrl != null && token != null;
            if (ok)
            {
                Application.OpenURL(ProRegistryAccess.BuildVccDeepLink(listingUrl, token));
            }
            _proStatusLabel.text = status;
            _proStatusLabel.EnableInClassList("uap-settings-hint--error", !ok);
            _proStatusLabel.style.display = DisplayStyle.Flex;
        }

        /// <summary>Pure wording rule for the VCC / ALCOM button's status line.</summary>
        internal static string DescribeProVccAttempt(string listingUrl, string token)
        {
            if (listingUrl == null)
            {
                return L10n.S.SettingsProUpdatesStatusErrorUrl;
            }
            if (token == null)
            {
                return L10n.S.SettingsProUpdatesStatusErrorVccNoKey;
            }
            return L10n.F(L10n.S.SettingsProUpdatesStatusVccOpenedFmt, listingUrl);
        }

        /// <summary>Pure wording rule for the status line under the Save key button.</summary>
        internal static string DescribeProApplyResult(ProRegistryApplyResult result)
        {
            if (result.Success)
            {
                return L10n.F(L10n.S.SettingsProUpdatesStatusAppliedFmt, result.UpmConfigPath);
            }
            switch (result.Error)
            {
                case ProRegistryApplyError.EmptyKey:
                    return L10n.S.SettingsProUpdatesStatusErrorEmptyKey;
                case ProRegistryApplyError.InvalidUrl:
                    return L10n.S.SettingsProUpdatesStatusErrorUrl;
                case ProRegistryApplyError.ForeignRegistry:
                    return L10n.F(L10n.S.SettingsProUpdatesStatusErrorForeignFmt, result.Detail,
                        ProRegistryAccess.ProPackageId);
                case ProRegistryApplyError.ManifestUnreadable:
                    return L10n.F(L10n.S.SettingsProUpdatesStatusErrorManifestFmt, result.Detail);
                default:
                    return L10n.F(L10n.S.SettingsProUpdatesStatusErrorWriteFmt, result.Detail);
            }
        }

        private void BuildUloopSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsUloopSectionTitle,
                "d_Package Manager", IconLoader.GlyphOpenWindow, "uloop");

            _uloopStatusLabel = new Label(string.Empty);
            _uloopStatusLabel.AddToClassList("uap-settings-hint");
            _uloopStatusLabel.AddToClassList("uap-settings-status");
            _uloopStatusLabel.enableRichText = false;
            section.Add(_uloopStatusLabel);

            // Live install progress line, right under the status it will
            // eventually flip (see the field's own doc comment for why it
            // lives at section level rather than inside the confirm card).
            // uap-settings-hint--pending (warn color, bold) fits all three
            // texts it can show -- Installing/FailedAsync/Stalled are each
            // "attention, something is in flight or went wrong", the same
            // register as the pending-reconnect pill this class already
            // uses that style for.
            _uloopInstallProgressLabel = new Label(string.Empty);
            _uloopInstallProgressLabel.AddToClassList("uap-settings-hint");
            _uloopInstallProgressLabel.AddToClassList("uap-settings-hint--pending");
            _uloopInstallProgressLabel.enableRichText = false;
            _uloopInstallProgressLabel.style.whiteSpace = WhiteSpace.Normal;
            _uloopInstallProgressLabel.style.display = DisplayStyle.None;
            section.Add(_uloopInstallProgressLabel);

            VisualElement installRow = AddRow(section);
            _uloopInstallButton = new Button(OnUloopInstallButtonClicked)
                { text = L10n.S.SettingsUloopInstallButton };
            _uloopInstallButton.AddToClassList("uap-settings-btn");
            _uloopInstallButton.AddToClassList("uap-settings-btn--primary");
            installRow.Add(_uloopInstallButton);

            BuildUloopInstallConfirmCard(section);

            VisualElement presetScope = AddHintScope(section);
            VisualElement presetRow = AddRow(presetScope);
            var presetButton = new Button(OnUloopPresetButtonClicked)
                { text = L10n.S.SettingsUloopPresetButton };
            presetButton.AddToClassList("uap-settings-btn");
            presetRow.Add(presetButton);
            AddHint(presetScope, L10n.S.SettingsUloopPresetHelp, L10n.S.SettingsUloopPresetTooltip);
            _uloopPresetAppliedLabel = new Label(L10n.S.SettingsUloopPresetApplied);
            _uloopPresetAppliedLabel.AddToClassList("uap-settings-hint");
            _uloopPresetAppliedLabel.enableRichText = false;
            _uloopPresetAppliedLabel.style.display = DisplayStyle.None;
            section.Add(_uloopPresetAppliedLabel);

            VisualElement snippetScope = AddHintScope(section);
            VisualElement snippetRow = AddRow(snippetScope);
            var snippetButton = new Button(OnUloopSnippetButtonClicked)
                { text = L10n.S.SettingsUloopSnippetButton };
            snippetButton.AddToClassList("uap-settings-btn");
            snippetRow.Add(snippetButton);
            AddHint(snippetScope, L10n.S.SettingsUloopSnippetHelp, L10n.S.SettingsUloopSnippetTooltip);
            _uloopSnippetAppliedLabel = new Label(L10n.S.SettingsUloopSnippetApplied);
            _uloopSnippetAppliedLabel.AddToClassList("uap-settings-hint");
            _uloopSnippetAppliedLabel.enableRichText = false;
            _uloopSnippetAppliedLabel.style.display = DisplayStyle.None;
            section.Add(_uloopSnippetAppliedLabel);

            RefreshUloopSection();
        }

        /// <summary>
        /// Inline install confirmation sub-card, built once and hidden until
        /// "Install uLoop" is pressed -- the same shape as BuildAccountLoginSubCard's
        /// inline sub-step pattern (see that method's doc comment), reused
        /// here for the same reason: an in-progress, temporary flow should
        /// read as a distinct sub-step inside the card rather than a modal
        /// dialog. Shows the route explanation, EVERY caveat (mapped through
        /// DescribeUloopCaveat, so even an unrecognized future caveat code
        /// still renders honestly instead of an empty row), and the full
        /// before/after manifest diff (BuildManifestDiffText) -- never a
        /// summary, per the task's "the diff is the whole point of this UI".
        /// The Apply button is disabled outright whenever the pending plan's
        /// CanProceed() is false (a blocking caveat present), so a blocked
        /// install can be reviewed but never actually applied from here.
        ///
        /// 2026-08-12 slimming (design note of that date, section 1's
        /// measurement: 222px card, of which a 27px two-path route sentence
        /// and a 124px always-open raw JSON diff): the route line is now one
        /// short pathless sentence with the two full paths on its tooltip,
        /// and the diff sits collapsed inside a Foldout -- still the full
        /// diff, one click away, because 207 chars of JSON is wrong for a
        /// hover tooltip (not selectable, not mono-spaced) but also wrong to
        /// force onto every confirmation. Caveats deliberately stay as
        /// always-visible labels: "a warning that only reads on hover does
        /// not function as a warning" (2026-08-04 annotation note section 4).
        /// </summary>
        private void BuildUloopInstallConfirmCard(VisualElement section)
        {
            _uloopInstallConfirmCard = new VisualElement();
            _uloopInstallConfirmCard.AddToClassList("uap-settings-login-subcard");
            _uloopInstallConfirmCard.style.display = DisplayStyle.None;

            var titleLabel = new Label(L10n.S.SettingsUloopInstallConfirmTitle);
            titleLabel.AddToClassList("uap-settings-hint");
            titleLabel.enableRichText = false;
            titleLabel.style.whiteSpace = WhiteSpace.Normal;
            _uloopInstallConfirmCard.Add(titleLabel);

            _uloopInstallRouteLabel = new Label(string.Empty);
            _uloopInstallRouteLabel.AddToClassList("uap-settings-hint");
            _uloopInstallRouteLabel.enableRichText = false;
            _uloopInstallRouteLabel.style.whiteSpace = WhiteSpace.Normal;
            _uloopInstallConfirmCard.Add(_uloopInstallRouteLabel);

            _uloopCaveatsHost = new VisualElement();
            _uloopInstallConfirmCard.Add(_uloopCaveatsHost);

            // Collapsed-by-default Foldout around the diff (2026-08-12
            // slimming -- see this method's doc comment). Plain Foldout, no
            // USS class: the default header/indent styling is fine here and
            // adding a class invites the silent flex drift the AddHintScope
            // doc comment warns about. PopulateUloopInstallConfirmCard
            // re-collapses it for every new plan, so one install's opened
            // diff never leaves the NEXT plan's diff pre-expanded (an open
            // diff should always be a deliberate click on the plan it shows).
            _uloopDiffFoldout = new Foldout
                { text = L10n.S.SettingsUloopInstallDiffFoldout, value = false };

            // Reuses the diagnostics tail's exact scroll+field pair
            // (uap-settings-diag-scroll/-field): a bordered, sunken,
            // mono-font, bounded-height text box is exactly what a
            // technical file diff needs, and no new USS class is required.
            var diffScroll = new ScrollView(ScrollViewMode.Vertical);
            diffScroll.AddToClassList("uap-settings-diag-scroll");
            _uloopDiffField = new TextField();
            _uloopDiffField.multiline = true;
            _uloopDiffField.isReadOnly = true;
            _uloopDiffField.AddToClassList("uap-settings-diag-field");
            MessageBlockFactory.ApplyMonoFont(_uloopDiffField);
            diffScroll.Add(_uloopDiffField);
            _uloopDiffFoldout.Add(diffScroll);
            _uloopInstallConfirmCard.Add(_uloopDiffFoldout);

            VisualElement actionRow = AddRow(_uloopInstallConfirmCard);
            _uloopInstallApplyButton = new Button(OnUloopInstallApplyClicked)
                { text = L10n.S.SettingsUloopInstallApply };
            _uloopInstallApplyButton.AddToClassList("uap-settings-btn");
            _uloopInstallApplyButton.AddToClassList("uap-settings-btn--primary");
            actionRow.Add(_uloopInstallApplyButton);
            _uloopInstallCancelButton = new Button(OnUloopInstallCancelClicked)
                { text = L10n.S.SettingsUloopInstallCancel };
            _uloopInstallCancelButton.AddToClassList("uap-settings-btn");
            actionRow.Add(_uloopInstallCancelButton);

            _uloopInstallResultLabel = new Label(string.Empty);
            _uloopInstallResultLabel.AddToClassList("uap-settings-hint");
            _uloopInstallResultLabel.enableRichText = false;
            _uloopInstallResultLabel.style.whiteSpace = WhiteSpace.Normal;
            _uloopInstallResultLabel.style.display = DisplayStyle.None;
            _uloopInstallConfirmCard.Add(_uloopInstallResultLabel);

            section.Add(_uloopInstallConfirmCard);
        }

        /// <summary>
        /// Computes the plan (UloopInstaller.Plan is pure inspection -- it
        /// never writes anything, see its own doc comment) and opens the
        /// confirmation card populated from it. Pressing "Install uLoop"
        /// again while the card is already open recomputes a fresh plan --
        /// the card always reflects the CURRENT disk state, never a stale
        /// one left over from an earlier open.
        /// </summary>
        private void OnUloopInstallButtonClicked()
        {
            _pendingUloopPlan = UloopInstaller.Plan(AgentHub.ProjectRoot);
            PopulateUloopInstallConfirmCard(_pendingUloopPlan);
            _uloopInstallConfirmCard.style.display = DisplayStyle.Flex;
        }

        private void PopulateUloopInstallConfirmCard(UloopInstallPlan plan)
        {
            _uloopCaveatsHost.Clear();
            _uloopInstallResultLabel.style.display = DisplayStyle.None;
            _uloopInstallApplyButton.SetEnabled(plan != null && plan.CanProceed());
            // Every new plan starts with its diff collapsed -- an expanded
            // diff must always be the result of a deliberate click on THIS
            // plan, never inherited from whatever the user opened last time.
            _uloopDiffFoldout.value = false;

            if (plan == null || plan.AlreadyInstalled)
            {
                // Defensive only -- RefreshUloopSection hides the Install
                // button once installed, so this branch is reachable only
                // via a race (installed by something else between the
                // button becoming visible and this click resolving). Shows
                // something honest rather than an empty/confusing diff.
                _uloopInstallRouteLabel.style.display = DisplayStyle.Flex;
                _uloopInstallRouteLabel.text = L10n.S.SettingsUloopStatusInstalled;
                _uloopInstallRouteLabel.tooltip = string.Empty;
                _uloopDiffFoldout.style.display = DisplayStyle.None;
                _uloopDiffField.SetValueWithoutNotify(string.Empty);
                return;
            }

            // A plan that could not choose a route (Method == None: malformed
            // manifest, missing manifest, or a scoped-registry blocker --
            // three of the four caveat codes) leaves ManifestPath and
            // BackupPath empty. The route sentence and the (empty) diff
            // foldout would sit meaninglessly above the real blocker text.
            // Show the blocker alone.
            if (plan.Method == UloopInstallMethod.None)
            {
                _uloopInstallRouteLabel.style.display = DisplayStyle.None;
                _uloopInstallRouteLabel.tooltip = string.Empty;
                _uloopDiffFoldout.style.display = DisplayStyle.None;
                _uloopDiffField.SetValueWithoutNotify(string.Empty);
            }
            else
            {
                _uloopInstallRouteLabel.style.display = DisplayStyle.Flex;
                _uloopDiffFoldout.style.display = DisplayStyle.Flex;
                // One pathless sentence inline; the two full paths ride the
                // route label's OWN tooltip (2026-08-12 slimming). The
                // tooltip goes on this label, not the whole card: a tooltip
                // set on a shared container is delivered to every child
                // without one of its own (2026-08-04 note section 3), which
                // would make hovering the caveats or the Cancel button
                // answer a question about file paths nobody asked there.
                _uloopInstallRouteLabel.text = L10n.S.SettingsUloopInstallViaManifest;
                _uloopInstallRouteLabel.tooltip = L10n.F(
                    L10n.S.SettingsUloopInstallPathsTooltipFmt,
                    plan.ManifestPath, plan.BackupPath);
            }

            for (int i = 0; i < plan.Caveats.Count; i++)
            {
                UloopInstallCaveat caveat = plan.Caveats[i];
                var caveatLabel = new Label(DescribeUloopCaveat(caveat));
                caveatLabel.AddToClassList("uap-settings-hint");
                caveatLabel.enableRichText = false;
                caveatLabel.style.whiteSpace = WhiteSpace.Normal;
                if (caveat != null && caveat.Blocking)
                {
                    // Reuses the existing warn-colored hint variant (danger
                    // zone / pending-reconnect) rather than a new near-
                    // duplicate USS rule for "this is a blocker".
                    caveatLabel.AddToClassList("uap-settings-hint--pending");
                }
                _uloopCaveatsHost.Add(caveatLabel);
            }

            _uloopDiffField.SetValueWithoutNotify(
                IconLoader.StripVariationSelectors(BuildManifestDiffText(plan.ManifestBefore, plan.ManifestAfter)));
        }

        /// <summary>
        /// Applies the pending plan (never a freshly recomputed one -- see
        /// _pendingUloopPlan's own doc comment) and reports the outcome.
        /// On success the confirmation card closes outright and the
        /// section-level progress line takes over: the plan is consumed, the
        /// dispatched AddRequest is stashed for polling, and the
        /// SessionState in-flight pair is armed so the domain reload a
        /// successful resolve triggers cannot silently revert the section
        /// (2026-08-12 design note 2a) -- the old static
        /// SettingsUloopInstallStarted sentence this handler used to show is
        /// exactly what the live elapsed counter replaces (note 2b). On
        /// SYNCHRONOUS failure NOTHING was changed (UloopInstaller.Apply's
        /// own contract: any backup taken is restored), so the card stays
        /// open with the failure text and Apply deliberately left enabled
        /// for a retry -- except the two restore-failed codes, see below.
        /// </summary>
        private void OnUloopInstallApplyClicked()
        {
            if (_pendingUloopPlan == null || !_pendingUloopPlan.CanProceed())
            {
                return;
            }
            string backupPath = _pendingUloopPlan.BackupPath;
            UloopInstallApplyResult result = UloopInstaller.Apply(_pendingUloopPlan, UnityEngine.Debug.LogError);

            // Two of the failure codes mean the manifest write failed AND the
            // restore that should have undone it also failed, which is the
            // one and only case where the file on disk is no longer
            // known-good. Both of them used to render the same "Nothing was
            // changed (any backup taken was restored)" line as every other
            // failure -- telling the user nothing happened at precisely the
            // moment that is least true, and never mentioning the backup that
            // is their only way back. Retry is also withheld on those codes:
            // re-running against a manifest in an unknown state is how a
            // recoverable mess becomes an unrecoverable one.
            bool manifestStateUnknown =
                result.FailureCode == UloopInstaller.ApplyFailureManifestWriteFailedRestoreFailed
                || result.FailureCode == UloopInstaller.ApplyFailureClientAddThrewRestoreFailed;
            if (result.Success)
            {
                // From here the truth about the install lives in the
                // dispatched request (and, across the reload it will cause,
                // in the SessionState pair). Arm both BEFORE the refresh
                // below so the very first evaluation already renders
                // "Installing... 0s" from a real signal.
                _uloopLiveAddRequest = result.ClientAddRequest;
                SessionStateBridge.UloopInstallInFlight = true;
                SessionStateBridge.UloopInstallStartedAtUtcTicks = System.DateTime.UtcNow.Ticks;
                HideUloopInstallConfirmCard();
            }
            else if (manifestStateUnknown)
            {
                _uloopInstallResultLabel.text = L10n.F(
                    L10n.S.SettingsUloopInstallFailedManifestUnknownFmt, backupPath, result.FailureDetail);
                _uloopInstallResultLabel.style.display = DisplayStyle.Flex;
                _uloopInstallApplyButton.SetEnabled(false);
            }
            else
            {
                _uloopInstallResultLabel.text = L10n.S.SettingsUloopInstallFailed;
                _uloopInstallResultLabel.style.display = DisplayStyle.Flex;
            }
            RefreshUloopSection();
        }

        private void OnUloopInstallCancelClicked()
        {
            HideUloopInstallConfirmCard();
        }

        private void HideUloopInstallConfirmCard()
        {
            if (_uloopInstallConfirmCard == null)
            {
                return;
            }
            _uloopInstallConfirmCard.style.display = DisplayStyle.None;
            _pendingUloopPlan = null;
        }

        /// <summary>
        /// Re-detects uLoop (a plain manifest.json string scan -- UloopDetector.
        /// DetectInProject -- cheap enough to run on every OnHubChanged tick,
        /// the same way RefreshCliStatus already re-probes the CLI path on
        /// every tick) and shows/hides the Install button accordingly. Once
        /// installed, the confirmation card (if a stale one were somehow
        /// still open) is force-closed -- there is nothing left to install.
        ///
        /// Also runs the install progress machine on every call (2026-08-12
        /// design note 2a): this is the method that runs right after a
        /// domain reload (via BuildUloopSection / RefreshAll), so evaluating
        /// here is exactly what lets a reload-interrupted install re-enter
        /// Installing (fresh SessionState flag) or Stalled (old flag)
        /// instead of silently reverting to "Not installed" -- the reported
        /// defect this whole feature exists to close.
        /// </summary>
        private void RefreshUloopSection()
        {
            if (_uloopStatusLabel == null)
            {
                return;
            }
            bool installed = UloopDetector.DetectInProject(AgentHub.ProjectRoot);
            _uloopStatusLabel.text = installed
                ? L10n.S.SettingsUloopStatusInstalled
                : L10n.S.SettingsUloopStatusMissing;
            _uloopInstallButton.style.display = installed ? DisplayStyle.None : DisplayStyle.Flex;
            if (installed)
            {
                HideUloopInstallConfirmCard();
            }
            UpdateUloopInstallProgress(installed);
        }

        /// <summary>
        /// Gathers the real signals (live AddRequest flags, detector
        /// verdict, SessionState in-flight pair), lets the pure machine
        /// (UloopInstallProgress.Evaluate -- table-tested in
        /// UloopInstallProgressTests) decide the state, and renders it onto
        /// the single progress label. This method contains NO precedence
        /// logic of its own on purpose: every "which signal wins" decision
        /// lives in the pure class where EditMode tests can pin it.
        /// </summary>
        private void UpdateUloopInstallProgress(bool installed)
        {
            AddRequest request = _uloopLiveAddRequest;
            bool hasLive = request != null;
            bool completed = hasLive && request.IsCompleted;
            bool failed = completed && request.Status == StatusCode.Failure;
            bool flagSet = SessionStateBridge.UloopInstallInFlight;
            long startedTicks = SessionStateBridge.UloopInstallStartedAtUtcTicks;
            double elapsedSeconds = 0.0;
            if (startedTicks > 0)
            {
                elapsedSeconds = (System.DateTime.UtcNow.Ticks - startedTicks)
                    / (double)System.TimeSpan.TicksPerSecond;
                if (elapsedSeconds < 0.0)
                {
                    // A clock that moved backwards must read as "fresh", not
                    // as a negative age -- never worse than Installing.
                    elapsedSeconds = 0.0;
                }
            }

            UloopInstallProgressResult progress = UloopInstallProgress.Evaluate(
                hasLive, completed, failed, installed, flagSet,
                elapsedSeconds, UloopInstallProgress.StaleThresholdSeconds);

            if (progress.ShouldClearFlag)
            {
                SessionStateBridge.UloopInstallInFlight = false;
                SessionStateBridge.UloopInstallStartedAtUtcTicks = 0;
            }

            string text;
            switch (progress.State)
            {
                case UloopInstallProgressState.Installing:
                    // The elapsed whole-seconds number changing is the
                    // entire liveness proof (design note 2a's rejected
                    // alternative: Client.Add exposes no percentage, so a
                    // progress bar would be fabricated -- the honest maximum
                    // is a truthful counter).
                    text = L10n.F(L10n.S.SettingsUloopInstallingFmt,
                        ((long)elapsedSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case UloopInstallProgressState.FailedAsync:
                    // Null-guarded defensively: nothing in the AddRequest
                    // API contract promises Error is non-null whenever
                    // Status is Failure, and this failure path has NOT been
                    // exercised against a real registry outage yet (the
                    // PM's live verification covers it) -- fall back to the
                    // status code string rather than rendering "could not
                    // resolve: " with a hole in it.
                    string message = request != null && request.Error != null
                        ? request.Error.message
                        : null;
                    if (string.IsNullOrEmpty(message))
                    {
                        message = request != null
                            ? request.Status.ToString()
                            : StatusCode.Failure.ToString();
                    }
                    text = L10n.F(L10n.S.SettingsUloopInstallAsyncFailedFmt, message);
                    break;
                case UloopInstallProgressState.Stalled:
                    text = L10n.S.SettingsUloopInstallStalled;
                    break;
                default:
                    // Idle and Succeeded both render nothing here: Idle has
                    // nothing to say, and Succeeded is already carried by
                    // the status label flipping to "Installed" right above.
                    text = string.Empty;
                    break;
            }

            if (text.Length == 0)
            {
                _uloopInstallProgressLabel.style.display = DisplayStyle.None;
            }
            else
            {
                _uloopInstallProgressLabel.style.display = DisplayStyle.Flex;
                if (!string.Equals(_uloopInstallProgressLabel.text, text, System.StringComparison.Ordinal))
                {
                    _uloopInstallProgressLabel.text = text;
                }
            }

            // A second Apply mid-resolve would race the one in flight (two
            // Client.Add dispatches for the same id), so the Install button
            // sleeps while Installing -- and only while Installing: after
            // FailedAsync the button comes back so the user can build a
            // FRESH plan if they prefer the panel over Package Manager
            // (never the stale one -- the card was closed and the plan
            // nulled the moment Apply succeeded).
            _uloopInstallButton.SetEnabled(progress.State != UloopInstallProgressState.Installing);

            if (progress.State == UloopInstallProgressState.Installing)
            {
                StartUloopProgressTick();
            }
            else
            {
                StopUloopProgressTick();
            }
        }

        /// <summary>
        /// Hooks <see cref="OnUloopProgressTick"/> onto EditorApplication.
        /// update -- once (idempotent via _uloopProgressTickHooked), same
        /// hook-once/unhook-on-exit discipline as AgentHub's auto-continue
        /// tick. Hooked whenever an evaluation lands on Installing; unhooked
        /// the moment one lands anywhere else (and on OnDeactivate, so a
        /// hidden settings tab does not keep polling -- OnActivate's
        /// RefreshAll re-evaluates and re-hooks if the install is still in
        /// flight).
        /// </summary>
        private void StartUloopProgressTick()
        {
            if (_uloopProgressTickHooked)
            {
                return;
            }
            _uloopProgressTickHooked = true;
            _uloopProgressLastEvalAt = 0.0;
            EditorApplication.update += OnUloopProgressTick;
        }

        private void StopUloopProgressTick()
        {
            if (!_uloopProgressTickHooked)
            {
                return;
            }
            _uloopProgressTickHooked = false;
            EditorApplication.update -= OnUloopProgressTick;
        }

        /// <summary>
        /// One poll step, throttled to ~2 evaluations per second (see
        /// _uloopProgressLastEvalAt's doc comment for why every editor tick
        /// would be wasteful). Delegates to RefreshUloopSection rather than
        /// evaluating the machine directly so the tick that notices the
        /// terminal state also flips the status label / Install button in
        /// the same pass -- one render path for all callers, no
        /// tick-only shortcut that could drift from it.
        /// </summary>
        private void OnUloopProgressTick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _uloopProgressLastEvalAt < 0.5)
            {
                return;
            }
            _uloopProgressLastEvalAt = now;
            RefreshUloopSection();
        }

        /// <summary>
        /// Applies the allowedTools/disallowedTools halves of the uLoop
        /// preset (see BuildUloopDisallowedPatterns' doc comment for why the
        /// exclusions live in disallowedTools rather than narrowing the
        /// allow pattern itself) and refreshes both the Conversation
        /// section's TextFields and the reconnect-pending state --
        /// allowedTools/disallowedTools are next-spawn-only
        /// (SettingsChangeDetector), exactly like a manual edit of either
        /// field already is.
        /// </summary>
        private void OnUloopPresetButtonClicked()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            // Allowlist of specific safe subcommands, NOT the broad
            // Bash(uloop *) -- see UloopAllowedSubcommands.
            //
            // The strip MUST happen before the merge and cannot be skipped.
            // MergeToolListAdditions only ever appends, so on a machine where
            // an EARLIER version of this same button already wrote
            // "Bash(uloop *)", adding the eight narrow patterns next to it
            // changes nothing at all: the wildcard still grants everything,
            // including `uloop execute-dynamic-code`, which is measured
            // auto-allowed under it (design note section 9.2) and which this
            // button's own help text promises will always ask. The users who
            // pressed the old button are exactly the uLoop users, so without
            // this line the rewrite would protect only the people who never
            // needed it.
            settings.allowedTools = MergeToolListAdditions(
                StripBroadUloopAllowEntries(settings.allowedTools),
                BuildUloopAllowedPatterns());
            settings.disallowedTools = MergeToolListAdditions(settings.disallowedTools,
                BuildUloopDisallowedPatterns());
            PanelStateStore.instance.SaveNow();
            if (_allowedToolsField != null)
            {
                _allowedToolsField.SetValueWithoutNotify(JoinLines(settings.allowedTools));
            }
            if (_disallowedToolsField != null)
            {
                _disallowedToolsField.SetValueWithoutNotify(JoinLines(settings.disallowedTools));
            }
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
            if (_uloopPresetAppliedLabel != null)
            {
                _uloopPresetAppliedLabel.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>
        /// Appends the L2 usage-norm snippet to the custom-instructions
        /// sidecar (AppendUloopGuidanceSnippet handles the idempotency/
        /// no-clobber contract). Reconnect-relevant like any other
        /// custom-instructions edit.
        /// </summary>
        private void OnUloopSnippetButtonClicked()
        {
            string current = LoadCustomInstructions();
            string updated = AppendUloopGuidanceSnippet(current);
            if (!string.Equals(updated, current, System.StringComparison.Ordinal))
            {
                CustomInstructionsFile.CreateDefault(AgentHub.ProjectRoot).Save(updated);
                if (_customInstructionsField != null)
                {
                    _customInstructionsField.SetValueWithoutNotify(updated);
                }
                RefreshReconnectHint();
                AgentHub.RequestAutoApplyReconnect();
            }
            if (_uloopSnippetAppliedLabel != null)
            {
                _uloopSnippetAppliedLabel.style.display = DisplayStyle.Flex;
            }
        }

        // -- uLoop pure helpers (EditMode tested via SettingsViewLogicTests) -------

        /// <summary>
        /// Maps a UloopInstallPlan caveat's stable Code to localized display
        /// text (task item 2's caveat table). Falls back to the RAW CODE
        /// itself for any code this switch does not recognize -- "an
        /// UNKNOWN code must still render something honest rather than an
        /// empty row" -- rather than an empty string, so a future
        /// UloopInstaller caveat this UI has not been updated for yet stays
        /// visible (just untranslated) instead of silently vanishing from
        /// the confirmation card. Appends caveat.Detail in parentheses when
        /// present (a path, an existing registry URL, a parse error) -- the
        /// localized sentence alone is intentionally generic per L10n
        /// string, and the detail is exactly the context that makes an
        /// otherwise-identical caveat distinguishable.
        /// </summary>
        public static string DescribeUloopCaveat(UloopInstallCaveat caveat)
        {
            if (caveat == null)
            {
                return string.Empty;
            }
            string text;
            switch (caveat.Code)
            {
                case UloopInstaller.CaveatCodeVccProject:
                    text = L10n.S.SettingsUloopCaveatVcc;
                    break;
                case UloopInstaller.CaveatCodeOffline:
                    text = L10n.S.SettingsUloopCaveatOffline;
                    break;
                case UloopInstaller.CaveatCodeManifestUnreadable:
                    text = L10n.S.SettingsUloopCaveatManifestUnreadable;
                    break;
                case UloopInstaller.CaveatCodeScopedRegistryConflict:
                    text = L10n.S.SettingsUloopCaveatRegistryConflict;
                    break;
                default:
                    text = caveat.Code ?? string.Empty;
                    break;
            }
            return string.IsNullOrEmpty(caveat.Detail) ? text : text + " (" + caveat.Detail + ")";
        }

        /// <summary>
        /// The subcommands the preset allows, one explicit pattern each.
        ///
        /// An earlier shape wrote a single broad "Bash(uloop *)" instead --
        /// allow everything, then shadow the dangerous subcommands back out
        /// via disallowedTools. MEASURED against a live CLI 2026-08-04
        /// (scratchpad/toolpattern/probe2.js, both shapes, same six commands,
        /// two identical runs; design note section 9.2):
        ///
        ///                        broad+exclude    allowlist
        ///   uloop compile ......  auto-allowed    auto-allowed
        ///   uloop update .......  CLI-denied      CLI-denied
        ///   uloop update --yes .  CLI-denied      CLI-denied
        ///   uloop launch .......  CLI-denied      CLI-denied
        ///   execute-dynamic-code  AUTO-ALLOWED    asks
        ///
        /// The exclusions work: a bare, argument-less "uloop update" -- the
        /// exact form of the real freeze incident -- is refused under both
        /// shapes. That was the risk this change was made to avoid, and it
        /// turned out not to exist.
        ///
        /// What the measurement DID find is on the last row. The broad
        /// pattern silently auto-allowed `uloop execute-dynamic-code`, which
        /// compiles and runs arbitrary C# inside the editor, and which design
        /// section 8.3 requires to ALWAYS prompt. Nothing in the old shape
        /// was wrong about update/sync/launch; it was wrong about everything
        /// it had never thought to enumerate. That is the failure mode of
        /// deny-listing in general -- it can only exclude the dangers someone
        /// already listed -- and it is why this stays an allowlist even
        /// though the specific fear that prompted the rewrite was unfounded.
        ///
        /// The list is deliberately short: read-only or clearly-scoped
        /// commands only. Anything absent still works -- it just asks first.
        /// </summary>
        private static readonly string[] UloopAllowedSubcommands =
        {
            "compile", "get-logs", "list", "run-tests",
        };

        /// <summary>
        /// Pure: the exact allowedTools entries the preset adds -- one
        /// exact-match form and one wildcard-suffix form per safe subcommand,
        /// for the same reason BuildUloopDisallowedPatterns ships both
        /// (the wildcard grammar is unmeasured, so neither form is assumed to
        /// subsume the other). Here the redundancy only buys convenience: a
        /// form that fails to match costs a permission card, nothing worse.
        /// </summary>
        /// <summary>
        /// Pure: <paramref name="existing"/> with every allowedTools entry
        /// removed that grants uloop BROADLY -- `Bash(uloop *)` and its
        /// spacing/casing variants. Everything else is left untouched,
        /// including the preset's own narrow entries and any unrelated tool.
        ///
        /// This exists because the preset's safety is subtractive on upgrade,
        /// not just additive. A narrow allowlist sitting beside a surviving
        /// wildcard is not an allowlist. Only entries that are a wildcard
        /// over the whole `uloop` command are removed -- a user's deliberate
        /// `Bash(uloop update)` (should they have added one by hand) is
        /// theirs to keep, and disallowedTools shadows it anyway.
        /// </summary>
        public static List<string> StripBroadUloopAllowEntries(List<string> existing)
        {
            var result = new List<string>();
            if (existing == null)
            {
                return result;
            }
            for (int i = 0; i < existing.Count; i++)
            {
                if (!IsBroadUloopAllowEntry(existing[i]))
                {
                    result.Add(existing[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// True for `Bash(uloop *)` and the shapes a hand-edit plausibly
        /// produces: extra or missing spaces, several asterisks, different
        /// casing. Deliberately conservative about what counts as "broad" --
        /// everything between `uloop` and the closing paren must be nothing
        /// but whitespace and asterisks, so `Bash(uloop compile *)` is NOT
        /// stripped.
        /// </summary>
        private static bool IsBroadUloopAllowEntry(string entry)
        {
            if (string.IsNullOrEmpty(entry))
            {
                return false;
            }
            string trimmed = entry.Trim();
            const string prefix = "Bash(uloop";
            if (!trimmed.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
                || !trimmed.EndsWith(")", System.StringComparison.Ordinal))
            {
                return false;
            }
            string middle = trimmed.Substring(prefix.Length, trimmed.Length - prefix.Length - 1);
            bool sawStar = false;
            for (int i = 0; i < middle.Length; i++)
            {
                char c = middle[i];
                if (c == '*')
                {
                    sawStar = true;
                    continue;
                }
                if (!char.IsWhiteSpace(c))
                {
                    return false;
                }
            }
            return sawStar;
        }

        public static List<string> BuildUloopAllowedPatterns()
        {
            var result = new List<string>(UloopAllowedSubcommands.Length * 2);
            for (int i = 0; i < UloopAllowedSubcommands.Length; i++)
            {
                string sub = UloopAllowedSubcommands[i];
                result.Add("Bash(uloop " + sub + ")");
                result.Add("Bash(uloop " + sub + " *)");
            }
            return result;
        }

        /// <summary>
        /// Subcommands that have frozen this editor before (this project's
        /// real incident, referenced by SettingsUloopPresetHelp) and must
        /// never be auto-allowed. They are already absent from
        /// UloopAllowedSubcommands, so listing them here is the second of two
        /// independent barriers, not the only one.
        /// `execute-dynamic-code` is deliberately NOT in this list -- see
        /// BuildUloopDisallowedPatterns' doc comment for why it needs to
        /// stay off of BOTH lists instead of being added here.
        /// </summary>
        private static readonly string[] UloopBlockedSubcommands = { "update", "sync", "launch" };

        /// <summary>
        /// Pure: the exact disallowedTools entries the preset adds for
        /// UloopBlockedSubcommands.
        ///
        /// Since the allowlist above never names these subcommands, they
        /// would already fall through to a permission card without this list
        /// at all. It is kept anyway because a card is not a block: the user
        /// still has to notice and refuse one, at the end of a long unattended
        /// run, for the one command that froze this editor. Belt and braces.
        ///
        /// AgentClient.BuildArguments (read before choosing this shape, per
        /// the task instruction) treats --allowedTools/--disallowedTools as
        /// two entirely independent CLI argument lists -- AppendToolList is
        /// called once per list with no cross-list logic at all. There is no
        /// exclusion mechanism IN THIS REPO that makes a disallowedTools
        /// entry "win" over an allowedTools entry; that precedence is the
        /// CLI's OWN permission engine, already relied on elsewhere in this
        /// project's design (docs/design-notes/2026-08-01-phase5-unity-ops-
        /// design.md section 8.7's measured finding "deny beats permission-
        /// mode" -- deny beating a mere allow rule, which this preset relies
        /// on, is the strictly weaker and therefore implied claim). The
        /// 2026-08-04 measurement confirmed that engine refuses these
        /// outright ("Permission to use Bash with command uloop update has
        /// been denied"), including under the old broad-allow shape, so
        /// disallowedTools is the right home for the exclusion.
        ///
        /// Two forms per subcommand, not one. When this was written the
        /// wildcard grammar was unmeasured and the redundancy was insurance
        /// against the BARE, no-argument form -- the exact shape of the real
        /// incident, a lone "uloop update" with nothing after it -- slipping
        /// through. The 2026-08-04 measurement (see UloopAllowedSubcommands)
        /// showed both forms are in fact refused, so the redundancy is no
        /// longer load-bearing. It costs six short strings and is kept:
        /// <list type="bullet">
        /// <item>"Bash(uloop update)" -- an exact-match form, covering the
        /// agent invoking the bare command with no trailing arguments at
        /// all.</item>
        /// <item>"Bash(uloop update *)" -- the wildcard-suffix shape,
        /// covering any invocation WITH trailing arguments/flags.</item>
        /// </list>
        /// `execute-dynamic-code` is intentionally ABSENT from this list AND
        /// from the allowlist -- keeping it out of both is the only way to
        /// make it always ASK, and the 2026-08-04 probe confirmed it does
        /// (it produced a can_use_tool round trip under the allowlist shape,
        /// and ran unattended under the old broad one).
        /// The task requires it to always ASK, not to be blocked outright --
        /// and this codebase's allowedTools/disallowedTools machinery has no
        /// third "ask" list (AgentClientOptions only has AllowedTools/
        /// DisallowedTools; see AgentClient.BuildArguments). The only way to
        /// make a command "always ask" with just those two lists is to keep
        /// it out of BOTH: an entry absent from both always falls through to
        /// whatever the live permission mode does for an unlisted Bash
        /// command, which is a can_use_tool round trip (a card) in every
        /// mode this panel exposes today (default/plan/acceptEdits) --
        /// docs/design-notes/2026-08-03-auto-approve-levels.md section 1
        /// measured that acceptEdits only skips that round trip for
        /// Write/Edit-family tools, never Bash, so this holds even there.
        /// Putting execute-dynamic-code in disallowedTools would make it a
        /// hard, silent block instead of a card -- the wrong behaviour for
        /// this one entry.
        /// </summary>
        public static List<string> BuildUloopDisallowedPatterns()
        {
            var result = new List<string>(UloopBlockedSubcommands.Length * 2);
            for (int i = 0; i < UloopBlockedSubcommands.Length; i++)
            {
                string sub = UloopBlockedSubcommands[i];
                result.Add("Bash(uloop " + sub + ")");
                result.Add("Bash(uloop " + sub + " *)");
            }
            return result;
        }

        /// <summary>
        /// Pure, idempotent merge: appends every entry of `toAdd` that is
        /// not already present (ordinal, exact string match) in `existing`,
        /// preserving `existing`'s current order/entries untouched and never
        /// mutating the input list. Shared by the allowedTools and
        /// disallowedTools halves of the uLoop preset (and reusable by any
        /// future one-click preset) so pressing the button any number of
        /// times can never duplicate an entry -- the task's explicit
        /// "idempotent: pressing twice must not duplicate entries"
        /// requirement.
        /// </summary>
        public static List<string> MergeToolListAdditions(List<string> existing, IList<string> toAdd)
        {
            var result = existing != null ? new List<string>(existing) : new List<string>();
            if (toAdd == null)
            {
                return result;
            }
            for (int i = 0; i < toAdd.Count; i++)
            {
                string candidate = toAdd[i];
                if (!string.IsNullOrEmpty(candidate) && !result.Contains(candidate))
                {
                    result.Add(candidate);
                }
            }
            return result;
        }

        /// <summary>
        /// The exact literal text the "Insert guidance snippet" button
        /// appends to the custom-instructions sidecar (task item 4 / design
        /// section 2 item 3: prefer this panel's own Unity tools; `uloop
        /// execute-dynamic-code` only for what they cannot express; writing
        /// and compiling code is the last resort). Deliberately plain ASCII
        /// English, NOT routed through L10n -- like AgentHub.
        /// ComposeUapOpsSteeringSection's own steering text (the closest
        /// sibling to this one), this string is CONSUMED BY THE MODEL via
        /// --append-system-prompt, never displayed as UI chrome, so it is
        /// not a candidate for the L10n treatment SettingsUloopSnippetHelp
        /// (the BUTTON's own human-facing description) already has.
        /// </summary>
        public const string UloopGuidanceSnippet =
            "Prefer this panel's own Unity tools (uap_*) for Unity edits."
            + " Reach for `uloop execute-dynamic-code` only for what those"
            + " tools cannot express. Writing and compiling new C# code is"
            + " the last resort.";

        /// <summary>
        /// Pure, idempotent append: returns `existingText` completely
        /// unchanged when it already contains UloopGuidanceSnippet verbatim
        /// (ordinal substring search) -- pressing the button twice, or
        /// reopening a project whose custom instructions already carry it
        /// from a prior session, must never duplicate it, and must never
        /// touch (let alone clobber) any other text the user wrote.
        /// Otherwise appends the snippet after exactly one blank line,
        /// trimming trailing whitespace off `existingText` first so the
        /// separator is always exactly one blank line regardless of how the
        /// user's existing text happens to end (matches AgentHub.
        /// ComposeAppendSystemPrompt's own blank-line join convention
        /// between composed sections). A null/empty `existingText` yields
        /// the snippet alone, with no leading blank line.
        /// </summary>
        public static string AppendUloopGuidanceSnippet(string existingText)
        {
            string current = existingText ?? string.Empty;
            if (current.IndexOf(UloopGuidanceSnippet, System.StringComparison.Ordinal) >= 0)
            {
                return current;
            }
            string trimmed = current.TrimEnd();
            return trimmed.Length == 0 ? UloopGuidanceSnippet : trimmed + "\n\n" + UloopGuidanceSnippet;
        }

        /// <summary>
        /// Pure line-level diff between a manifest's before/after text (task
        /// item 2: "the diff is the whole point of this UI ... do not
        /// collapse or truncate it into a summary"). Rendered with unified-
        /// diff-style leading markers ("  " unchanged, "- " removed, "+ "
        /// added) rather than an English "Before"/"After" heading,
        /// deliberately -- every other visible string in this file is
        /// localized (L10n.S.*), no localized "Before"/"After" label exists
        /// to reuse, and the task explicitly forbids adding new L10n
        /// strings, while a bare +/- marker reads the same regardless of UI
        /// language (the same convention unified diff/`git diff` output
        /// already uses).
        ///
        /// Exploits the fact that UloopInstaller.BuildManifestAfterText only
        /// ever INSERTS content (a brand-new "scopedRegistries" array, or
        /// one new scope string appended into an existing array) and never
        /// deletes or reorders anything already there: finding the longest
        /// common line PREFIX and the longest common line SUFFIX (not
        /// overlapping the prefix) between the two texts isolates the
        /// changed region without needing a general (and here unnecessary)
        /// LCS diff algorithm. Everything strictly between prefix and suffix
        /// in `before` renders as removed and everything strictly between
        /// them in `after` renders as added; for every diff this class
        /// actually produces the "removed" slice is typically empty or a
        /// single line (nothing is ever deleted outright -- at most a line
        /// gains a trailing comma when a sibling is inserted after it), so
        /// in practice this reads as a tight, minimal diff around the one
        /// inserted stanza/line.
        ///
        /// Degrades honestly, never incorrectly, when the ORIGINAL
        /// manifest.json was not already in the exact 2-space-indent style
        /// PrettyPrintManifest always re-emits (UloopInstaller's own doc
        /// comment: re-indentation, key order and newline style are not
        /// preserved) -- the common prefix/suffix can then be short or even
        /// empty, so the diff shows most or all of the file as removed and
        /// re-added. That is not a bug in this function: it is an accurate
        /// reflection of the fact that the whole file's formatting is
        /// genuinely about to be rewritten, which is exactly the kind of
        /// thing this confirmation UI exists to surface rather than hide.
        /// Nothing is ever truncated either way -- every line of both
        /// `before` and `after` appears exactly once.
        /// </summary>
        public static string BuildManifestDiffText(string before, string after)
        {
            string[] beforeLines = SplitManifestTextIntoLines(before);
            string[] afterLines = SplitManifestTextIntoLines(after);

            int prefixLen = 0;
            int maxPrefix = System.Math.Min(beforeLines.Length, afterLines.Length);
            while (prefixLen < maxPrefix
                && string.Equals(beforeLines[prefixLen], afterLines[prefixLen], System.StringComparison.Ordinal))
            {
                prefixLen++;
            }

            int suffixLen = 0;
            int maxSuffix = System.Math.Min(beforeLines.Length - prefixLen, afterLines.Length - prefixLen);
            while (suffixLen < maxSuffix
                && string.Equals(
                    beforeLines[beforeLines.Length - 1 - suffixLen],
                    afterLines[afterLines.Length - 1 - suffixLen],
                    System.StringComparison.Ordinal))
            {
                suffixLen++;
            }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < prefixLen; i++)
            {
                AppendDiffLine(sb, "  ", beforeLines[i]);
            }
            int beforeMiddleEnd = beforeLines.Length - suffixLen;
            for (int i = prefixLen; i < beforeMiddleEnd; i++)
            {
                AppendDiffLine(sb, "- ", beforeLines[i]);
            }
            int afterMiddleEnd = afterLines.Length - suffixLen;
            for (int i = prefixLen; i < afterMiddleEnd; i++)
            {
                AppendDiffLine(sb, "+ ", afterLines[i]);
            }
            for (int i = beforeMiddleEnd; i < beforeLines.Length; i++)
            {
                AppendDiffLine(sb, "  ", beforeLines[i]);
            }
            return sb.ToString();
        }

        private static void AppendDiffLine(System.Text.StringBuilder sb, string marker, string line)
        {
            sb.Append(marker).Append(line).Append('\n');
        }

        /// <summary>
        /// Splits on both "\n" and "\r\n" (SplitLines' own convention
        /// elsewhere in this file), then drops exactly one trailing empty
        /// element when the text ends with a newline -- PrettyPrintManifest
        /// (UloopInstaller) always ends its output with a single trailing
        /// "\n", and without this, a manifest text ending in a newline would
        /// otherwise contribute a spurious blank context line at the very
        /// end of every diff this class ever renders. Null/empty input
        /// yields an empty array, never null.
        /// </summary>
        private static string[] SplitManifestTextIntoLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new string[0];
            }
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
            {
                var trimmed = new string[lines.Length - 1];
                for (int i = 0; i < trimmed.Length; i++)
                {
                    trimmed[i] = lines[i];
                }
                return trimmed;
            }
            return lines;
        }

        // -- (c) Appearance ----------------------------------------------------------

        private void BuildAppearanceSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionAppearance,
                "d_Font Icon", "Aa");

            // Language dropdown (docs/design-notes/2026-08-01-i18n.md #4):
            // this is the ONLY production call site that resolves
            // PanelSettings.language into L10n's current catalog in this
            // stage -- see L10n's class doc comment on why nothing resolves
            // it automatically yet. New field, so (unlike the rest of this
            // not-yet-swept view) it already reads through L10n.S rather
            // than a hardcoded literal.
            var languageChoices = new List<PanelLanguage>
            {
                PanelLanguage.Auto,
                PanelLanguage.English,
                PanelLanguage.Japanese
            };
            _languageField = new PopupField<PanelLanguage>(L10n.S.SettingsLanguageLabel,
                languageChoices, PanelStateStore.instance.Settings.language,
                FormatLanguageOption, FormatLanguageOption);
            _languageField.AddToClassList("uap-settings-field");
            _languageField.RegisterValueChangedCallback(OnLanguageChanged);
            VisualElement languageScope = AddHintScope(section);
            languageScope.Add(_languageField);
            languageScope.tooltip = L10n.S.SettingsLanguageTooltip;

            // AddRow's parent is the scope, not the section: the tooltip has
            // to sit on something that actually contains the slider, and the
            // inline hint here is now empty.
            VisualElement fontSizeScope = AddHintScope(section);
            VisualElement fontRow = AddRow(fontSizeScope);
            _fontSizeSlider = new SliderInt(L10n.S.SettingsFontSizeLabel,
                PanelSettings.MinFontSizePx, PanelSettings.MaxFontSizePx);
            _fontSizeSlider.AddToClassList("uap-settings-slider");
            _fontSizeSlider.SetValueWithoutNotify(PanelStateStore.instance.Settings.fontSizePx);
            _fontSizeSlider.RegisterValueChangedCallback(OnFontSizeChanged);
            fontRow.Add(_fontSizeSlider);
            _fontSizeValueLabel = new Label(FormatFontSize(PanelStateStore.instance.Settings.fontSizePx));
            _fontSizeValueLabel.AddToClassList("uap-settings-slider-value");
            fontRow.Add(_fontSizeValueLabel);
            fontSizeScope.tooltip = L10n.S.SettingsFontSizeTooltip;

            _cjkToggle = new Toggle(L10n.S.SettingsCjkToggleLabel);
            _cjkToggle.AddToClassList("uap-settings-field");
            _cjkToggle.AddToClassList("uap-switch");
            _cjkToggle.SetValueWithoutNotify(PanelStateStore.instance.Settings.preferCjkUiFont);
            _cjkToggle.RegisterValueChangedCallback(OnCjkToggleChanged);
            section.Add(_cjkToggle);

            _cjkDiagnosticLabel = new Label(string.Empty);
            _cjkDiagnosticLabel.AddToClassList("uap-settings-hint");
            _cjkDiagnosticLabel.AddToClassList("uap-settings-status");
            _cjkDiagnosticLabel.enableRichText = false;
            section.Add(_cjkDiagnosticLabel);
        }

        /// <summary>
        /// "English"/"Japanese" are shown as fixed endonyms regardless of
        /// the currently active UI language (so the option stays
        /// recognizable to a reader of either language, the same way most
        /// OS language pickers work) -- only "Auto" is translated through
        /// the active catalog. UiStringsJa.JapaneseLanguageDisplayName is
        /// the one non-ASCII literal source GlyphAuditTests' source scan
        /// excludes; "English" is plain ASCII and safe anywhere.
        /// </summary>
        private static string FormatLanguageOption(PanelLanguage language)
        {
            switch (language)
            {
                case PanelLanguage.English:
                    return "English";
                case PanelLanguage.Japanese:
                    return UiStringsJa.JapaneseLanguageDisplayName;
                case PanelLanguage.Auto:
                default:
                    return L10n.S.SettingsLanguageOptionAuto;
            }
        }

        private void OnLanguageChanged(ChangeEvent<PanelLanguage> evt)
        {
            PanelStateStore.instance.Settings.language = evt.newValue;
            PanelStateStore.instance.SaveNow();
            // Fires L10n.LanguageChanged when the resolved language
            // actually changes, which AgentPanelWindow has subscribed to
            // (once, statically) to rebuild every open panel window/
            // floating permission window -- see that subscription's doc
            // comment for why the rebuild itself is deferred via
            // EditorApplication.delayCall rather than happening
            // synchronously inside this ChangeEvent callback.
            L10n.ApplyFromSettings(evt.newValue);
        }

        private void OnFontSizeChanged(ChangeEvent<int> evt)
        {
            int clamped = PanelSettings.ClampFontSize(evt.newValue);
            PanelStateStore.instance.Settings.fontSizePx = clamped;
            PanelStateStore.instance.SaveNow();
            _fontSizeValueLabel.text = FormatFontSize(clamped);
            AgentPanelWindow.ReapplyContentRootStyling();
        }

        private void OnCjkToggleChanged(ChangeEvent<bool> evt)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.preferCjkUiFont = evt.newValue;
            // Mirrors EnsureCjkUiFontDefault: an explicit user choice must
            // stick, so OnEnable's one-time system-language default may
            // never overwrite it again.
            settings.cjkUiFontDecided = true;
            PanelStateStore.instance.SaveNow();
            AgentPanelWindow.ReapplyContentRootStyling();
            RefreshCjkDiagnostic();
        }

        private void RefreshCjkDiagnostic()
        {
            if (_cjkDiagnosticLabel == null)
            {
                return;
            }
            string source = FontLoader.JapaneseUiFontSource;
            _cjkDiagnosticLabel.text = string.IsNullOrEmpty(source)
                ? L10n.S.SettingsCjkDiagnosticNone
                : L10n.F(FontLoader.JapaneseUiFontFromFontFix
                        ? L10n.S.SettingsCjkDiagnosticViaFontFixFmt
                        : L10n.S.SettingsCjkDiagnosticDetectedFmt,
                    DescribeFontSource(source));
        }

        private static string FormatFontSize(int px)
        {
            return L10n.F(L10n.S.SettingsFontSizeValueFmt,
                px.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // -- (d) Diagnostics -----------------------------------------------------------

        private void BuildDiagnosticsSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsSectionDiagnostics,
                "d_UnityEditor.DebugInspectorWindow", "(i)", "diagnostics");
            AddHint(section, L10n.S.SettingsDiagnosticsHint);

            VisualElement row = AddRow(section);
            var copy = new Button(OnCopyStderrClicked) { text = L10n.S.SettingsCopyButton };
            copy.AddToClassList("uap-settings-btn");
            row.Add(copy);
            var clear = new Button(OnClearStderrClicked) { text = L10n.S.SettingsClearButton };
            clear.AddToClassList("uap-settings-btn");
            row.Add(clear);

            var stderrScroll = new ScrollView(ScrollViewMode.Vertical);
            stderrScroll.AddToClassList("uap-settings-diag-scroll");

            _stderrField = new TextField();
            _stderrField.multiline = true;
            _stderrField.isReadOnly = true;
            _stderrField.AddToClassList("uap-settings-diag-field");
            MessageBlockFactory.ApplyMonoFont(_stderrField);
            stderrScroll.Add(_stderrField);
            section.Add(stderrScroll);
        }

        private void OnCopyStderrClicked()
        {
            EditorGUIUtility.systemCopyBuffer = string.Join("\n", AgentHub.StderrTail);
        }

        private void OnClearStderrClicked()
        {
            AgentHub.ClearStderrTail();
        }

        private void RefreshDiagnostics()
        {
            if (_stderrField == null)
            {
                return;
            }
            IReadOnlyList<string> lines = AgentHub.StderrTail;
            _stderrField.SetValueWithoutNotify(lines.Count == 0
                ? L10n.S.SettingsDiagnosticsEmpty
                : IconLoader.StripVariationSelectors(string.Join("\n", lines)));
        }

        // -- (d2) Account (docs/design-notes/2026-08-02-auth-in-panel.md) ---------

        private void BuildAccountSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionAccount,
                "d_CloudConnect", "@");
            _accountSectionRoot = section;

            _accountStatusLabel = new Label(string.Empty);
            _accountStatusLabel.AddToClassList("uap-settings-hint");
            _accountStatusLabel.enableRichText = false;
            _accountStatusLabel.style.whiteSpace = WhiteSpace.Normal;
            section.Add(_accountStatusLabel);

            // Live E2E (2026-08-02) found the editor environment can carry
            // CLAUDE_CODE_OAUTH_TOKEN, making "auth status" report
            // authMethod "oauth_token" with no email/plan detail -- and
            // making login/logout only affect the stored credentials, not
            // the env-token auth actually in use. Be honest about it.
            _accountEnvTokenNoteLabel = new Label(L10n.S.SettingsAccountEnvTokenNote);
            _accountEnvTokenNoteLabel.AddToClassList("uap-settings-hint");
            _accountEnvTokenNoteLabel.AddToClassList("uap-settings-hint--pending");
            _accountEnvTokenNoteLabel.enableRichText = false;
            _accountEnvTokenNoteLabel.style.whiteSpace = WhiteSpace.Normal;
            _accountEnvTokenNoteLabel.style.display = DisplayStyle.None;
            section.Add(_accountEnvTokenNoteLabel);

            // Visibility, not blocking (docs/design-notes/2026-09-10-claude-
            // api-key-auth-passthrough.md #3): shown whenever the CLI's
            // system/init reports API-key auth is actually in effect, for
            // as long as that holds (unlike AppendApiKeyAuthNoteIfNeeded's
            // one-shot transcript note).
            _accountApiKeyAuthNoteLabel = new Label(string.Empty);
            _accountApiKeyAuthNoteLabel.AddToClassList("uap-settings-hint");
            _accountApiKeyAuthNoteLabel.AddToClassList("uap-settings-hint--pending");
            _accountApiKeyAuthNoteLabel.enableRichText = false;
            _accountApiKeyAuthNoteLabel.style.whiteSpace = WhiteSpace.Normal;
            _accountApiKeyAuthNoteLabel.style.display = DisplayStyle.None;
            section.Add(_accountApiKeyAuthNoteLabel);

            // ACP counterpart (design note 2026-09-10-acp-auth-guidance-and-
            // method-display.md #4): which of the agent's own sign-in
            // methods actually authenticated this connection, shown for as
            // long as the connection lasts.
            _accountAcpAuthMethodLabel = new Label(string.Empty);
            _accountAcpAuthMethodLabel.AddToClassList("uap-settings-hint");
            _accountAcpAuthMethodLabel.enableRichText = false;
            _accountAcpAuthMethodLabel.style.whiteSpace = WhiteSpace.Normal;
            _accountAcpAuthMethodLabel.style.display = DisplayStyle.None;
            section.Add(_accountAcpAuthMethodLabel);

            BuildClaudeAuthField(section);

            VisualElement actionRow = AddRow(section);
            _accountLoginButton = new Button(OnAccountLoginClicked) { text = L10n.S.SettingsAccountLoginButton };
            _accountLoginButton.AddToClassList("uap-settings-btn");
            actionRow.Add(_accountLoginButton);
            _accountLogoutButton = new Button(OnAccountLogoutClicked) { text = L10n.S.SettingsAccountLogoutButton };
            _accountLogoutButton.AddToClassList("uap-settings-btn");
            actionRow.Add(_accountLogoutButton);

            BuildAccountLoginSubCard(section);

            // ACP variant (design note 2026-09-10-in-panel-install-and-sign-in.md
            // section 2): the agent signs in through its own browser flow;
            // this card reports the state and offers the one button that
            // (re)starts it, plus the URL when the agent printed one.
            _accountAcpHintLabel = AddHint(section, L10n.S.SettingsAccountAcpHint);
            _accountAcpHintLabel.style.display = DisplayStyle.None;
            VisualElement acpRow = AddRow(section);
            // In-panel sign-in (design note 2026-09-13-acp-feature-parity.md
            // section 2): runs the agent's own login command; only for
            // backends that have one (AgentBackends.HasInPanelLogin).
            _accountAcpLoginButton = new Button(OnAccountAcpLoginClicked)
                { text = L10n.S.SettingsAccountAcpLoginButton };
            _accountAcpLoginButton.AddToClassList("uap-settings-btn");
            _accountAcpLoginButton.AddToClassList("uap-settings-btn--primary");
            _accountAcpLoginButton.style.display = DisplayStyle.None;
            acpRow.Add(_accountAcpLoginButton);
            _accountAcpSignInButton = new Button(OnAccountAcpSignInClicked)
                { text = L10n.S.SettingsAccountAcpSignInButton };
            _accountAcpSignInButton.AddToClassList("uap-settings-btn");
            _accountAcpSignInButton.style.display = DisplayStyle.None;
            acpRow.Add(_accountAcpSignInButton);
            _accountAcpCancelLoginButton = new Button(OnAccountAcpCancelLoginClicked)
                { text = L10n.S.SettingsAccountCancelLoginButton };
            _accountAcpCancelLoginButton.AddToClassList("uap-settings-btn");
            _accountAcpCancelLoginButton.style.display = DisplayStyle.None;
            acpRow.Add(_accountAcpCancelLoginButton);
            _accountAcpOpenBrowserButton = new Button(OnAccountAcpOpenBrowserClicked)
                { text = L10n.S.SettingsAccountOpenBrowserButton };
            _accountAcpOpenBrowserButton.AddToClassList("uap-settings-btn");
            _accountAcpOpenBrowserButton.style.display = DisplayStyle.None;
            acpRow.Add(_accountAcpOpenBrowserButton);
            _accountAcpCopyUrlButton = new Button(OnAccountAcpCopyUrlClicked)
                { text = L10n.S.SettingsAccountCopyButton };
            _accountAcpCopyUrlButton.AddToClassList("uap-settings-btn");
            _accountAcpCopyUrlButton.style.display = DisplayStyle.None;
            acpRow.Add(_accountAcpCopyUrlButton);
            _accountAcpUrlField = new TextField(L10n.S.SettingsAccountLoginUrlLabel);
            _accountAcpUrlField.isReadOnly = true;
            _accountAcpUrlField.AddToClassList("uap-settings-field");
            _accountAcpUrlField.style.display = DisplayStyle.None;
            section.Add(_accountAcpUrlField);
            // What the login command last printed (a device code, "logged
            // in", an error) -- the exit code alone says nothing.
            _accountAcpLoginOutputLabel = new Label(string.Empty);
            _accountAcpLoginOutputLabel.AddToClassList("uap-settings-hint");
            _accountAcpLoginOutputLabel.enableRichText = false;
            _accountAcpLoginOutputLabel.style.whiteSpace = WhiteSpace.Normal;
            _accountAcpLoginOutputLabel.style.display = DisplayStyle.None;
            MessageBlockFactory.ApplyMonoFont(_accountAcpLoginOutputLabel);
            section.Add(_accountAcpLoginOutputLabel);

            // UXO-6: the one place a failed login attempt becomes VISIBLE.
            // Before this, a wrong/expired code meant the sub-card simply
            // vanished and the status line went back to "Not logged in" --
            // indistinguishable from never having tried.
            _accountLoginFailedLabel = new Label(L10n.S.SettingsAccountLoginFailedNotice);
            _accountLoginFailedLabel.AddToClassList("uap-settings-hint");
            _accountLoginFailedLabel.AddToClassList("uap-settings-hint--error");
            _accountLoginFailedLabel.enableRichText = false;
            _accountLoginFailedLabel.style.whiteSpace = WhiteSpace.Normal;
            _accountLoginFailedLabel.style.display = DisplayStyle.None;
            section.Add(_accountLoginFailedLabel);

            RefreshAccountSection();
        }

        // -- API key auth passthrough (v0.40.0, docs/design-notes/2026-09-
        // 10-claude-api-key-auth-passthrough.md): PanelSettings.claudeAuth.
        // Claude Code only -- ANTHROPIC_API_KEY has no meaning for an ACP
        // agent, so this control is hidden in RefreshAccountSection's acp
        // branch exactly like the login/logout buttons above. Next-spawn-
        // only (SettingsChangeDetector), same shape as
        // BuildSubagentCostPolicyField. --------------------------------

        private void BuildClaudeAuthField(VisualElement section)
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            var choices = new List<ClaudeAuthMode>
            {
                ClaudeAuthMode.Auto,
                ClaudeAuthMode.SubscriptionOnly
            };
            _claudeAuthField = new PopupField<ClaudeAuthMode>(
                L10n.S.SettingsClaudeAuthLabel, choices, settings.claudeAuth,
                FormatClaudeAuthOption, FormatClaudeAuthOption);
            _claudeAuthField.AddToClassList("uap-settings-field");
            _claudeAuthField.RegisterValueChangedCallback(OnClaudeAuthChanged);
            VisualElement claudeAuthScope = AddHintScope(section);
            claudeAuthScope.Add(_claudeAuthField);

            AddHint(claudeAuthScope, L10n.S.SettingsClaudeAuthHint, L10n.S.SettingsClaudeAuthTooltip);
        }

        /// <summary>
        /// Localized label for the API key authentication PopupField's two
        /// values, same shape as FormatSubagentCostPolicyOption -- any
        /// unexpected value (including a future one) falls back to Auto so
        /// an out-of-range enum never renders a blank label.
        /// </summary>
        public static string FormatClaudeAuthOption(ClaudeAuthMode mode)
        {
            switch (mode)
            {
                case ClaudeAuthMode.SubscriptionOnly:
                    return L10n.S.SettingsClaudeAuthOptionSubscriptionOnly;
                case ClaudeAuthMode.Auto:
                default:
                    return L10n.S.SettingsClaudeAuthOptionAuto;
            }
        }

        /// <summary>
        /// Persists PanelSettings.claudeAuth and requests an auto-apply
        /// reconnect: ClaudeCliProcess reads it fresh on every process
        /// start, including a --resume spawn, exactly like subagentModel.
        /// </summary>
        private void OnClaudeAuthChanged(ChangeEvent<ClaudeAuthMode> evt)
        {
            PanelStateStore.instance.Settings.claudeAuth = evt.newValue;
            PanelStateStore.instance.SaveNow();
            RefreshReconnectHint();
            AgentHub.RequestAutoApplyReconnect();
        }

        /// <summary>
        /// Inline login sub-card (design note section 2.2): instruction
        /// text, the OAuth URL as a read-only selectable field plus "Open
        /// browser"/"Copy" (the CLI already tries to open the browser
        /// itself -- these are the fallback it suggests in its own stdout),
        /// a code field + Submit, and Cancel. Driven entirely by
        /// AgentHub.CurrentLoginSession's live state via
        /// RefreshAccountSection -- this method only builds the (initially
        /// hidden) shell.
        /// </summary>
        private void BuildAccountLoginSubCard(VisualElement section)
        {
            _accountLoginSubCard = new VisualElement();
            _accountLoginSubCard.AddToClassList("uap-settings-login-subcard");
            _accountLoginSubCard.style.display = DisplayStyle.None;

            _accountLoginInstructionLabel = new Label(string.Empty);
            _accountLoginInstructionLabel.AddToClassList("uap-settings-hint");
            _accountLoginInstructionLabel.enableRichText = false;
            _accountLoginInstructionLabel.style.whiteSpace = WhiteSpace.Normal;
            _accountLoginSubCard.Add(_accountLoginInstructionLabel);

            _accountLoginUrlField = new TextField(L10n.S.SettingsAccountLoginUrlLabel);
            _accountLoginUrlField.isReadOnly = true;
            _accountLoginUrlField.AddToClassList("uap-settings-field");
            _accountLoginSubCard.Add(_accountLoginUrlField);

            VisualElement urlRow = AddRow(_accountLoginSubCard);
            _accountOpenBrowserButton = new Button(OnAccountOpenBrowserClicked)
                { text = L10n.S.SettingsAccountOpenBrowserButton };
            _accountOpenBrowserButton.AddToClassList("uap-settings-btn");
            urlRow.Add(_accountOpenBrowserButton);
            _accountCopyUrlButton = new Button(OnAccountCopyUrlClicked) { text = L10n.S.SettingsAccountCopyButton };
            _accountCopyUrlButton.AddToClassList("uap-settings-btn");
            urlRow.Add(_accountCopyUrlButton);

            _accountCodeField = new TextField(L10n.S.SettingsAccountCodeLabel);
            _accountCodeField.AddToClassList("uap-settings-field");
            _accountLoginSubCard.Add(_accountCodeField);

            VisualElement codeRow = AddRow(_accountLoginSubCard);
            _accountSubmitCodeButton = new Button(OnAccountSubmitCodeClicked)
                { text = L10n.S.SettingsAccountSubmitCodeButton };
            _accountSubmitCodeButton.AddToClassList("uap-settings-btn");
            _accountSubmitCodeButton.AddToClassList("uap-settings-btn--primary");
            codeRow.Add(_accountSubmitCodeButton);
            _accountCancelLoginButton = new Button(OnAccountCancelLoginClicked)
                { text = L10n.S.SettingsAccountCancelLoginButton };
            _accountCancelLoginButton.AddToClassList("uap-settings-btn");
            codeRow.Add(_accountCancelLoginButton);

            section.Add(_accountLoginSubCard);
        }

        private void OnAccountLoginClicked()
        {
            _loginFailedNoticeVisible = false;
            _loginCodeSubmitted = false;
            _suppressLoginVerdict = false;
            AgentHub.BeginLogin();
            RefreshAccountSection();
        }

        private void OnAccountLogoutClicked()
        {
            bool confirmed = EditorUtility.DisplayDialog(
                L10n.S.SettingsAccountLogoutConfirmTitle,
                L10n.S.SettingsAccountLogoutConfirmBody,
                L10n.S.SettingsAccountLogoutConfirmButton,
                L10n.S.SettingsAccountLogoutCancelButton);
            if (!confirmed)
            {
                return;
            }
            AgentHub.Logout(null);
        }

        private void OnAccountOpenBrowserClicked()
        {
            AuthLoginSession session = AgentHub.CurrentLoginSession;
            if (session != null && !string.IsNullOrEmpty(session.OAuthUrl))
            {
                Application.OpenURL(session.OAuthUrl);
            }
        }

        private void OnAccountCopyUrlClicked()
        {
            AuthLoginSession session = AgentHub.CurrentLoginSession;
            if (session != null && !string.IsNullOrEmpty(session.OAuthUrl))
            {
                EditorGUIUtility.systemCopyBuffer = session.OAuthUrl;
            }
        }

        private void OnAccountSubmitCodeClicked()
        {
            if (_accountCodeField == null)
            {
                return;
            }
            if (AgentHub.SubmitLoginCode(_accountCodeField.value))
            {
                _accountCodeField.SetValueWithoutNotify(string.Empty);
                // UXO-6: flip the sub-card into "verifying" immediately --
                // the process gives no per-code acknowledgement, so this
                // flag is the only feedback between paste and exit.
                _loginCodeSubmitted = true;
                RefreshAccountSection();
            }
        }

        private void OnAccountCancelLoginClicked()
        {
            // A deliberate cancel must not read as "login failed" when the
            // process exit + status re-query come back around.
            _suppressLoginVerdict = true;
            AgentHub.CancelLogin();
        }

        /// <summary>
        /// Pure re-render from AgentHub.CurrentAuthStatus/CurrentLoginSession
        /// -- never spawns a subprocess itself (that is RefreshAll's job via
        /// AgentHub.RefreshAuthStatus). Safe to call from OnHubChanged on
        /// every tick, and is exactly what lets a rebuilt SettingsView
        /// resume showing an in-flight login (started before the rebuild)
        /// without creating or leaking a second process -- the session
        /// object itself lives in AgentHub, not here.
        /// </summary>
        private static void SetDisplay(VisualElement element, bool visible)
        {
            if (element != null)
            {
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void RefreshAccountSectionAcp()
        {
            AgentBackend backend = AgentHub.CurrentBackend;
            string name = AgentBackends.DisplayName(backend);
            AgentClient client = AgentHub.Client;
            bool connected = client != null
                && client.State != AgentClientState.NotStarted
                && client.State != AgentClientState.Starting
                && client.State != AgentClientState.Errored;
            // The in-panel login command, when one is running (design note
            // 2026-09-13-acp-feature-parity.md section 2), owns the status
            // line and the URL; otherwise the bridge's own sign-in state
            // does, as before.
            AuthLoginSession login = AgentHub.CurrentLoginSession;
            bool loginRunning = login != null && !login.HasExited;
            string commandLine = AgentHub.AcpLoginCommandLine;
            bool loginAvailable = commandLine.Length > 0;
            string url = loginRunning ? login.OAuthUrl : AgentHub.AcpSignInUrl;
            if (loginRunning)
            {
                _accountStatusLabel.text = L10n.F(L10n.S.SettingsAccountAcpLoginRunningFmt, name, commandLine);
            }
            else if (AgentHub.AcpSignInPending)
            {
                _accountStatusLabel.text = L10n.F(L10n.S.SettingsAccountAcpSignInPendingFmt, name);
            }
            else if (!string.IsNullOrEmpty(AgentHub.AcpLoginError))
            {
                _accountStatusLabel.text = AgentHub.AcpLoginError;
            }
            else if (!string.IsNullOrEmpty(AgentHub.AcpSignInError))
            {
                _accountStatusLabel.text = L10n.F(L10n.S.HubAcpSignInFailedNoteFmt, name, AgentHub.AcpSignInError);
            }
            else if (connected)
            {
                _accountStatusLabel.text = L10n.F(L10n.S.SettingsAccountAcpConnectedFmt, name);
            }
            else
            {
                _accountStatusLabel.text = L10n.S.SettingsAccountAcpNotConnected;
            }
            _accountAcpHintLabel.text = loginAvailable
                ? L10n.F(L10n.S.SettingsAccountAcpLoginHintFmt, commandLine)
                : L10n.S.SettingsAccountAcpHint;
            bool hasUrl = (loginRunning || AgentHub.AcpSignInPending) && !string.IsNullOrEmpty(url);
            SetDisplay(_accountAcpOpenBrowserButton, hasUrl);
            SetDisplay(_accountAcpCopyUrlButton, hasUrl);
            SetDisplay(_accountAcpUrlField, hasUrl);
            if (hasUrl)
            {
                _accountAcpUrlField.SetValueWithoutNotify(url);
            }
            SetDisplay(_accountAcpLoginButton, loginAvailable);
            _accountAcpLoginButton.SetEnabled(!loginRunning && !AgentHub.AcpSignInPending);
            SetDisplay(_accountAcpCancelLoginButton, loginRunning);
            string lastLine = loginRunning ? (login.LatestOutputLine ?? string.Empty) : string.Empty;
            SetDisplay(_accountAcpLoginOutputLabel, lastLine.Length > 0);
            if (lastLine.Length > 0)
            {
                _accountAcpLoginOutputLabel.text = IconLoader.SanitizeForDisplay(lastLine);
            }
            _accountAcpSignInButton.SetEnabled(!AgentHub.AcpSignInPending && !loginRunning);

            // Which method got us in, and -- when it is a key/gateway one --
            // that the bill lands on that key rather than a subscription.
            bool showMethod = connected && !AgentHub.AcpSignInPending;
            SetDisplay(_accountAcpAuthMethodLabel, showMethod);
            string methodName = AcpProtocolBridge.DescribeAuthMethod(
                AgentHub.AcpAuthMethodId, AgentHub.AcpAuthMethodName);
            if (showMethod)
            {
                _accountAcpAuthMethodLabel.text = FormatAcpAuthMethodLine(
                    AgentHub.AcpAuthMethodId, AgentHub.AcpAuthMethodName);
            }
            bool apiKeyMethod = showMethod
                && AcpProtocolBridge.IsApiKeyAuthMethod(AgentHub.AcpAuthMethodId, AgentHub.AcpAuthMethodName);
            SetDisplay(_accountApiKeyAuthNoteLabel, apiKeyMethod);
            if (apiKeyMethod)
            {
                _accountApiKeyAuthNoteLabel.text =
                    L10n.F(L10n.S.SettingsAccountAcpApiKeyAuthNoteFmt, methodName);
            }
        }

        /// <summary>
        /// Pure: the Account card's sign-in-method line for an ACP backend.
        /// No id and no name means the agent opened the session without an
        /// `authenticate` round trip -- it was already signed in, and the
        /// method is not knowable from here -- so say that neutrally rather
        /// than naming a method the panel is guessing at.
        /// </summary>
        internal static string FormatAcpAuthMethodLine(string methodId, string methodName)
        {
            string described = AcpProtocolBridge.DescribeAuthMethod(methodId, methodName);
            return described.Length == 0
                ? L10n.S.SettingsAccountAcpAuthMethodStored
                : L10n.F(L10n.S.SettingsAccountAcpAuthMethodFmt, described);
        }

        private void OnAccountAcpSignInClicked()
        {
            // A fresh process: the bridge re-runs the handshake and asks
            // the agent to authenticate again when it still needs to.
            AgentHub.RecoverConnection();
            RefreshAccountSection();
        }

        /// <summary>The URL to open/copy: the in-panel login command's when one runs, else the bridge's.</summary>
        private static string CurrentAcpSignInUrl()
        {
            AuthLoginSession login = AgentHub.CurrentLoginSession;
            if (login != null && !login.HasExited && !string.IsNullOrEmpty(login.OAuthUrl))
            {
                return login.OAuthUrl;
            }
            return AgentHub.AcpSignInUrl;
        }

        private static void OnAccountAcpOpenBrowserClicked()
        {
            string url = CurrentAcpSignInUrl();
            if (!string.IsNullOrEmpty(url))
            {
                Application.OpenURL(url);
            }
        }

        private static void OnAccountAcpCopyUrlClicked()
        {
            string url = CurrentAcpSignInUrl();
            if (!string.IsNullOrEmpty(url))
            {
                EditorGUIUtility.systemCopyBuffer = url;
            }
        }

        private void OnAccountAcpLoginClicked()
        {
            AgentHub.BeginAcpLogin();
            RefreshAccountSection();
        }

        private void OnAccountAcpCancelLoginClicked()
        {
            AgentHub.CancelLogin();
            RefreshAccountSection();
        }

        private void RefreshAccountSection()
        {
            if (_accountStatusLabel == null)
            {
                return;
            }
            // The Account card is Claude Code's `claude auth` surface; an
            // ACP agent signs in through its own CLI (design note
            // 2026-09-10-acp-backends.md section 4).
            bool acp = !AgentHub.IsClaudeBackend;
            SetDisplay(_accountAcpHintLabel, acp);
            SetDisplay(_accountAcpSignInButton, acp);
            if (acp)
            {
                // RefreshAccountSectionAcp owns _accountApiKeyAuthNoteLabel
                // here: with an ACP agent it carries the ACP method's
                // billing note instead of Claude's apiKeySource one.
                RefreshAccountSectionAcp();
                SetDisplay(_accountLoginButton, false);
                SetDisplay(_accountLogoutButton, false);
                SetDisplay(_accountLoginSubCard, false);
                SetDisplay(_accountEnvTokenNoteLabel, false);
                SetDisplay(_claudeAuthField, false);
                SetDisplay(_accountLoginFailedLabel, false);
                return;
            }
            SetDisplay(_accountAcpOpenBrowserButton, false);
            SetDisplay(_accountAcpCopyUrlButton, false);
            SetDisplay(_accountAcpLoginButton, false);
            SetDisplay(_accountAcpCancelLoginButton, false);
            SetDisplay(_accountAcpLoginOutputLabel, false);
            SetDisplay(_accountAcpUrlField, false);
            SetDisplay(_accountAcpAuthMethodLabel, false);
            SetDisplay(_claudeAuthField, true);
            if (_claudeAuthField != null)
            {
                PanelSettings settings = PanelStateStore.instance.Settings;
                if (_claudeAuthField.value != settings.claudeAuth)
                {
                    _claudeAuthField.SetValueWithoutNotify(settings.claudeAuth);
                }
            }
            RefreshApiKeyAuthNote();
            AuthStatus status = AgentHub.CurrentAuthStatus;
            AuthLoginSession session = AgentHub.CurrentLoginSession;
            bool loginInFlight = session != null && !session.HasExited;

            // UXO-6 verdict plumbing. AgentHub clears CurrentLoginSession the
            // moment the process exits and re-queries auth status, so
            // "did my code work" is only answerable when the NEXT AuthStatus
            // instance arrives: remember the instance visible at exit time
            // and judge on the first tick that shows a different one.
            if (_loginWasInFlight && !loginInFlight)
            {
                _loginCodeSubmitted = false;
                if (_suppressLoginVerdict)
                {
                    _suppressLoginVerdict = false;
                }
                else
                {
                    _loginAwaitingVerdict = true;
                    _loginVerdictBaseline = status;
                }
            }
            _loginWasInFlight = loginInFlight;
            if (_loginAwaitingVerdict && !ReferenceEquals(status, _loginVerdictBaseline))
            {
                _loginAwaitingVerdict = false;
                _loginVerdictBaseline = null;
                _loginFailedNoticeVisible =
                    !(status != null && status.IsAvailable && status.LoggedIn);
            }
            if (status != null && status.IsAvailable && status.LoggedIn)
            {
                _loginFailedNoticeVisible = false;
            }
            if (_accountLoginFailedLabel != null)
            {
                _accountLoginFailedLabel.style.display =
                    _loginFailedNoticeVisible && !loginInFlight
                        ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (status == null)
            {
                _accountStatusLabel.text = L10n.S.SettingsAccountCheckingStatus;
            }
            else if (!status.IsAvailable)
            {
                _accountStatusLabel.text = L10n.S.SettingsAccountUnavailable;
            }
            else if (status.LoggedIn)
            {
                _accountStatusLabel.text = BuildLoggedInStatusText(status);
            }
            else
            {
                _accountStatusLabel.text = L10n.S.SettingsAccountNotLoggedIn;
            }

            if (_accountEnvTokenNoteLabel != null)
            {
                _accountEnvTokenNoteLabel.style.display =
                    IsEnvTokenAuth(status) ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // Login stays visible even while logged in -- design note
            // section 2.2 mandates it double as a "re-login" affordance
            // (AgentHub.BeginLogin() never checks CurrentAuthStatus, so
            // starting a fresh login while already logged in is safe).
            // Logout is the one exclusively gated on being logged in.
            bool loggedIn = status != null && status.IsAvailable && status.LoggedIn;
            _accountLoginButton.style.display =
                !loginInFlight ? DisplayStyle.Flex : DisplayStyle.None;
            // Same action, honest label: a login while logged in switches accounts.
            _accountLoginButton.text = loggedIn
                ? L10n.S.SettingsAccountSwitchButton
                : L10n.S.SettingsAccountLoginButton;
            _accountLogoutButton.style.display =
                !loginInFlight && loggedIn ? DisplayStyle.Flex : DisplayStyle.None;

            _accountLoginSubCard.style.display = loginInFlight ? DisplayStyle.Flex : DisplayStyle.None;
            if (!loginInFlight)
            {
                return;
            }
            string url = session.OAuthUrl ?? string.Empty;
            _accountLoginUrlField.SetValueWithoutNotify(url);
            bool hasUrl = url.Length > 0;
            _accountOpenBrowserButton.SetEnabled(hasUrl);
            _accountCopyUrlButton.SetEnabled(hasUrl);
            AccountLoginFeedback feedback = ResolveAccountLoginFeedback(
                loginInFlight, session.IsWaitingForCode, _loginCodeSubmitted);
            _accountLoginInstructionLabel.text =
                feedback == AccountLoginFeedback.Verifying
                    ? L10n.S.SettingsAccountLoginVerifying
                    : feedback == AccountLoginFeedback.WaitingForCode
                        ? L10n.S.SettingsAccountLoginWaitingForCode
                        : L10n.S.SettingsAccountLoginStarting;
            bool canType = feedback == AccountLoginFeedback.WaitingForCode;
            _accountCodeField.SetEnabled(canType);
            _accountSubmitCodeButton.SetEnabled(canType);
        }

        /// <summary>
        /// UXO-6 (SR section 4.3): the in-flight login sub-card's state.
        /// Before this, submitting a code changed NOTHING on screen (the
        /// process gives no acknowledgement until it exits), and a rejected
        /// code ended as a silently vanished card. Pure so the truth table
        /// is testable without an auth process: in flight, a submitted code
        /// means Verifying (field+button lock until the process exits) and
        /// otherwise the CLI's waiting/starting phases map straight through.
        /// The post-exit verdict (Failed notice) is deliberately NOT decided
        /// here -- it needs the next AuthStatus instance, which
        /// RefreshAccountSection tracks by reference (see its comment).
        /// </summary>
        internal enum AccountLoginFeedback
        {
            Idle,
            Starting,
            WaitingForCode,
            Verifying
        }

        internal static AccountLoginFeedback ResolveAccountLoginFeedback(
            bool loginInFlight, bool waitingForCode, bool codeSubmitted)
        {
            if (!loginInFlight)
            {
                return AccountLoginFeedback.Idle;
            }
            if (codeSubmitted)
            {
                return AccountLoginFeedback.Verifying;
            }
            return waitingForCode
                ? AccountLoginFeedback.WaitingForCode
                : AccountLoginFeedback.Starting;
        }

        /// <summary>
        /// Logged-in status line. Environment-token auth ("oauth_token",
        /// e.g. CLAUDE_CODE_OAUTH_TOKEN in the editor's environment) carries
        /// no email/subscription detail in `auth status --json` (measured
        /// live 2026-08-02), so the two-placeholder format would render as
        /// "Logged in as  (unknown plan)." -- fall back to the plain
        /// no-detail line whenever the email is missing.
        /// </summary>
        internal static string BuildLoggedInStatusText(AuthStatus status)
        {
            if (string.IsNullOrEmpty(status.Email))
            {
                return L10n.S.SettingsAccountLoggedInNoDetail;
            }
            string subscription = string.IsNullOrEmpty(status.SubscriptionType)
                ? L10n.S.SettingsAccountSubscriptionUnknown
                : status.SubscriptionType;
            return L10n.F(L10n.S.SettingsAccountLoggedInFmt, status.Email, subscription);
        }

        /// <summary>
        /// True when the CLI reports the session is authenticated via an
        /// environment token rather than the stored credentials -- the one
        /// case where the Account card's login/logout buttons change state
        /// the running auth does not actually depend on.
        /// </summary>
        internal static bool IsEnvTokenAuth(AuthStatus status)
        {
            return status != null && status.IsAvailable && status.LoggedIn
                && string.Equals(status.AuthMethod, "oauth_token", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the CURRENT session's system/init reported an
        /// apiKeySource other than "none"/empty -- ANTHROPIC_API_KEY (or an
        /// apiKeyHelper) is actually authenticating this session, so usage
        /// is billed to that key rather than a subscription (docs/design-
        /// notes/2026-09-10-claude-api-key-auth-passthrough.md #3).
        /// </summary>
        internal static bool IsApiKeyAuth(SystemInitMessage message)
        {
            return message != null && !string.IsNullOrEmpty(message.ApiKeySource)
                && !string.Equals(message.ApiKeySource, "none", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Shows/hides and fills _accountApiKeyAuthNoteLabel from AgentHub.LastKnownInitMessage.</summary>
        private void RefreshApiKeyAuthNote()
        {
            if (_accountApiKeyAuthNoteLabel == null)
            {
                return;
            }
            SystemInitMessage message = AgentHub.LastKnownInitMessage;
            bool apiKeyAuth = IsApiKeyAuth(message);
            _accountApiKeyAuthNoteLabel.style.display = apiKeyAuth ? DisplayStyle.Flex : DisplayStyle.None;
            if (apiKeyAuth)
            {
                _accountApiKeyAuthNoteLabel.text =
                    L10n.F(L10n.S.SettingsAccountApiKeyAuthNoteFmt, message.ApiKeySource);
            }
        }

        /// <summary>
        /// Scrolls this view's ScrollView so the Account card (and its
        /// freshly-started login sub-card, when one is in flight) is
        /// actually on screen -- AgentPanelWindow.ShowSettingsAccount's sole
        /// purpose (FirstRunView's chat-view "Log in" button jumps here per
        /// design note section 2.2). BuildAccountSection registers Account
        /// 9th of 10 sections, so without this the user would otherwise land
        /// at the very top of a long scroll with no visible feedback that
        /// anything happened. Deferred one scheduler tick: ScrollView.
        /// ScrollTo needs the target's layout already resolved, which is not
        /// guaranteed the same frame a Chat -&gt; Settings view switch just
        /// flipped this root's display from None to Flex.
        /// </summary>
        public void ScrollToAccountSection()
        {
            ScrollView scroll = _scroll;
            if (scroll == null || _accountSectionRoot == null)
            {
                return;
            }
            // Explicit zero-arg lambda (not a parameterless "delegate { }"
            // literal): IVisualElementScheduler.Execute is overloaded on
            // Action vs Action<TimerState>, and a bare "delegate { }" can
            // implicitly match either -- CS0121 ambiguous call -- while a
            // "() => ..." lambda's explicit empty parameter list only
            // matches the Action overload.
            scroll.schedule.Execute(() => scroll.ScrollTo(_accountSectionRoot)).ExecuteLater(0);
        }

        // -- (e) About ------------------------------------------------------------------

        // The PUBLIC repository (the Core mirror; see the public-mirror section of docs/RELEASING.md).
        // The development monorepo is private, so a link there is a 404 for
        // every user who installed the package from the mirror -- which is
        // every user but the author (2026-09-12 core-only wording note, P0).
        internal const string GitHubRepoUrl = "https://github.com/c-colloid/AgentPanelForUnity";

        private void BuildAboutSection(VisualElement parent)
        {
            VisualElement section = AddSection(parent, L10n.S.SettingsSectionAbout,
                "d_console.infoicon.sml", "(?)");

            // Neutral-colored pills (not a status color) for the two
            // version numbers, and Package-Manager-style suppressed links
            // for the two actions below -- design-notes/2026-08-01-
            // settings-visual-refresh.md section 1 / R06 section 4.5.
            VisualElement versionRow = AddRow(section);
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AgentPanelWindow).Assembly);
            var packageLabel = new Label(L10n.F(L10n.S.SettingsPackageVersionFmt,
                info != null ? info.version : L10n.S.SettingsPackageVersionUnknown));
            packageLabel.AddToClassList("uap-pill");
            packageLabel.AddToClassList("uap-pill--neutral");
            packageLabel.AddToClassList("uap-settings-version-pill");
            packageLabel.enableRichText = false;
            versionRow.Add(packageLabel);

            _cliVersionLabel = new Label(string.Empty);
            _cliVersionLabel.AddToClassList("uap-pill");
            _cliVersionLabel.AddToClassList("uap-pill--neutral");
            _cliVersionLabel.AddToClassList("uap-settings-version-pill");
            _cliVersionLabel.enableRichText = false;
            versionRow.Add(_cliVersionLabel);

            VisualElement row = AddRow(section);
            var changelog = new Button(OnOpenChangelogClicked) { text = L10n.S.SettingsOpenChangelogButton };
            changelog.AddToClassList("uap-settings-link-btn");
            row.Add(changelog);
            var github = new Button(OnOpenGitHubClicked) { text = L10n.S.SettingsOpenGitHubButton };
            github.AddToClassList("uap-settings-link-btn");
            row.Add(github);

            RefreshCliVersionLabel();
        }

        /// <summary>
        /// CLI version precedence (docs/design-notes/2026-08-01-cli-binary-
        /// version-probe.md, superseding docs/design-notes/2026-08-01-init-
        /// message-retention.md's fix, which turned out to only cover the
        /// case where `system/init` eventually arrives): the CLI emits
        /// `system/init` only once a connection's FIRST user message has
        /// actually been processed, so an idle spawned/resumed connection
        /// with no message sent yet sits Ready forever with `InitMessage ==
        /// null` -- retention alone cannot fix that, because nothing was
        /// ever received to retain. This method now falls back, in order:
        /// 1. The live SystemInitMessage
        /// (`client.InitMessage.ClaudeCodeVersion`) -- this connection's
        /// own, most authoritative value.
        /// 2. `AgentHub.LastKnownCliVersion` -- the last `system/init` ANY
        /// connection this editor session has produced, while the CURRENT
        /// client is connected or connecting (not NotStarted/Errored) but
        /// has not processed its own `system/init` yet.
        /// 3. The protocol-independent binary probe
        /// (`SessionStateBridge.CliBinaryVersion`, only trusted when its
        /// recorded path still matches what the CLI path field resolves to
        /// right now) -- read straight from the resolved executable, so it
        /// is available even when neither of the above ever will be for
        /// this connection.
        /// "not connected" is shown only once none of the three yield a
        /// value, which in practice now means "no CLI path resolves at
        /// all" (a resolved path always at least attempts the binary
        /// probe -- see <see cref="RefreshCliStatus"/>), or a probe that
        /// has not landed yet on this exact refresh (self-corrects within
        /// a frame or two once the async probe's callback fires).
        /// Precedence and format-string selection are pure
        /// (<see cref="ResolveCliVersionDisplay"/>) so both are unit
        /// tested without a live AgentClient/CLI process.
        /// </summary>
        private void RefreshCliVersionLabel()
        {
            if (_cliVersionLabel == null)
            {
                return;
            }
            AgentClient client = AgentHub.Client;
            string liveVersion = client != null && client.InitMessage != null
                ? client.InitMessage.ClaudeCodeVersion : null;
            bool connectedOrConnecting = client != null
                && client.State != AgentClientState.NotStarted
                && client.State != AgentClientState.Errored;
            // Survives domain reloads via SessionState -- a resumed
            // connection may never re-emit system/init, and the static
            // LastKnownInitMessage dies with the old domain.
            string persistedInitVersion = connectedOrConnecting ? AgentHub.LastKnownCliVersion : null;
            string binaryProbeVersion = GetCachedBinaryVersionForCurrentCliPath();

            bool fromBinaryProbeOnly;
            string cliVersion = ResolveCliVersionDisplay(
                liveVersion, persistedInitVersion, binaryProbeVersion, out fromBinaryProbeOnly);

            _cliVersionLabel.text = string.IsNullOrEmpty(cliVersion)
                ? L10n.F(L10n.S.SettingsCliVersionFmt, L10n.S.SettingsCliVersionNotConnected)
                : L10n.F(fromBinaryProbeOnly ? L10n.S.SettingsCliVersionUnconfirmedFmt : L10n.S.SettingsCliVersionFmt,
                    cliVersion);
        }

        /// <summary>
        /// Returns the cached binary-probe version ONLY when it was probed
        /// for the EXACT path the CLI path field resolves to right now --
        /// a changed path (or one that no longer resolves) must never show
        /// a stale previous binary's version. Null when nothing resolves or
        /// nothing has been cached for the current path yet.
        /// </summary>
        private static string GetCachedBinaryVersionForCurrentCliPath()
        {
            string resolved = CreateCliPathProbe().Resolve();
            if (string.IsNullOrEmpty(resolved) || SessionStateBridge.CliBinaryVersionPath != resolved)
            {
                return null;
            }
            string cached = SessionStateBridge.CliBinaryVersion;
            return string.IsNullOrEmpty(cached) ? null : cached;
        }

        /// <summary>
        /// Pure precedence resolution for the About row's CLI version text:
        /// live &gt; persisted-init &gt; binary-probe &gt; none. Returns
        /// null (not the "not connected" L10n text) when nothing is known,
        /// leaving that substitution to the caller so this method itself
        /// carries no L10n dependency.
        /// <paramref name="fromBinaryProbeOnly"/> is true only when the
        /// binary-probe tier is what won, telling the caller to use the
        /// "not yet connected" format variant instead of the live/persisted
        /// one.
        /// </summary>
        public static string ResolveCliVersionDisplay(string liveVersion, string persistedInitVersion,
            string binaryProbeVersion, out bool fromBinaryProbeOnly)
        {
            if (!string.IsNullOrEmpty(liveVersion))
            {
                fromBinaryProbeOnly = false;
                return liveVersion;
            }
            if (!string.IsNullOrEmpty(persistedInitVersion))
            {
                fromBinaryProbeOnly = false;
                return persistedInitVersion;
            }
            if (!string.IsNullOrEmpty(binaryProbeVersion))
            {
                fromBinaryProbeOnly = true;
                return binaryProbeVersion;
            }
            fromBinaryProbeOnly = false;
            return null;
        }

        private void OnOpenChangelogClicked()
        {
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AgentPanelWindow).Assembly);
            if (info == null || string.IsNullOrEmpty(info.resolvedPath))
            {
                return;
            }
            string path = System.IO.Path.Combine(info.resolvedPath, "CHANGELOG.md");
            if (System.IO.File.Exists(path))
            {
                EditorUtility.OpenWithDefaultApp(path);
            }
        }

        private void OnOpenGitHubClicked()
        {
            Application.OpenURL(GitHubRepoUrl);
        }

        /// <summary>
        /// Design note 2026-09-10 section 4: the ONLY change this feature
        /// makes on the user's behalf is opening the settings page --
        /// never the setting itself (see PlayModeReloadAdvisor's doc
        /// comment for why).
        /// </summary>
        private void OnOpenPlayModeProjectSettingsClicked()
        {
            SettingsService.OpenProjectSettings("Project/Editor");
        }

        // -- Shared layout helpers -----------------------------------------------------

        private void RefreshAll()
        {
            RefreshWarningTones();
            RefreshCliStatus();
            RefreshDiagnostics();
            RefreshCjkDiagnostic();
            RefreshReconnectHint();
            RefreshCliVersionLabel();
            RefreshModelSection();
            // Spawns "auth status --json" (guarded against overlapping
            // queries by AgentHub itself) -- only from here, NOT from
            // OnHubChanged, which fires far too often (every streaming
            // token) to spawn a subprocess on. RefreshAccountSection below
            // is the cheap, spawn-free re-render OnHubChanged calls instead.
            AgentHub.RefreshAuthStatus();
            RefreshAccountSection();
            RefreshUapOpsStatus();
            RefreshExtensionProfilesSection();
            RefreshUloopSection();
        }

        /// <summary>
        /// Builds one card (design-notes/2026-08-01-settings-visual-refresh.md
        /// phase A): a header strip (icon + title) over a body container,
        /// and returns the BODY so every existing call site keeps appending
        /// its fields exactly as before. `iconName` is resolved through
        /// IconLoader (graceful fallback to `fallbackGlyph` on a version
        /// where the built-in name does not resolve -- see IconLoader's own
        /// doc comment); `fallbackGlyph` must be plain ASCII or an existing
        /// IconLoader.Glyph* constant (GlyphAuditTests guards this).
        /// </summary>
        private static VisualElement AddSection(VisualElement parent, string titleText,
            string iconName, string fallbackGlyph)
        {
            var card = new VisualElement();
            card.AddToClassList("uap-settings-card");

            var header = new VisualElement();
            header.AddToClassList("uap-settings-card-header");
            header.Add(IconLoader.CreateIcon(iconName, fallbackGlyph,
                "uap-settings-card-icon", "uap-settings-card-icon-glyph"));
            var title = new Label(titleText);
            title.AddToClassList("uap-settings-card-title");
            title.enableRichText = false;
            header.Add(title);
            card.Add(header);

            var body = new VisualElement();
            body.AddToClassList("uap-settings-card-body");
            card.Add(body);

            parent.Add(card);
            return body;
        }

        /// <summary>
        /// UXIA-2: SessionState key for one collapsible section's
        /// open/closed state. Pure so the round-trip is testable; the
        /// HistoryView view-preference keys are the naming precedent
        /// (survives domain reloads, resets with the editor session --
        /// exactly the lifetime a disclosure preference wants).
        /// </summary>
        internal static string SectionDisclosureKey(string sectionId)
        {
            return "Colloid.AgentPanel.Settings.SectionOpen." + sectionId;
        }

        // -- Unity official plugin section (design note 2026-09-10 section 2) ---

        /// <summary>
        /// Placed right after the uLoop section: both are "install an
        /// external tool" cards. Collapsed default shows three lines --
        /// status, the install button row (with the progress line hidden
        /// until a run starts), and the details foldout -- plus the Stream C
        /// steering toggle. Long text (license, uninstall command) lives
        /// in the foldout; the two warnings that change what the user is
        /// agreeing to (user scope, Unity 6+) stay on the confirm card
        /// itself (2026-08-12 note section 2b: a warning you must hover to
        /// read is not a warning).
        /// </summary>
        private void BuildUnityPluginSection(VisualElement parent)
        {
            VisualElement section = AddCollapsibleSection(parent, L10n.S.SettingsUnityPluginSectionTitle,
                "d_Package Manager", IconLoader.GlyphOpenWindow, "unity-plugin");

            _unityPluginStatusLabel = new Label(string.Empty);
            _unityPluginStatusLabel.AddToClassList("uap-settings-hint");
            _unityPluginStatusLabel.AddToClassList("uap-settings-status");
            _unityPluginStatusLabel.enableRichText = false;
            _unityPluginStatusLabel.style.whiteSpace = WhiteSpace.Normal;
            section.Add(_unityPluginStatusLabel);

            _unityPluginProgressLabel = new Label(string.Empty);
            _unityPluginProgressLabel.AddToClassList("uap-settings-hint");
            _unityPluginProgressLabel.AddToClassList("uap-settings-hint--pending");
            _unityPluginProgressLabel.enableRichText = false;
            _unityPluginProgressLabel.style.whiteSpace = WhiteSpace.Normal;
            _unityPluginProgressLabel.style.display = DisplayStyle.None;
            section.Add(_unityPluginProgressLabel);

            VisualElement installRow = AddRow(section);
            _unityPluginInstallButton = new Button(OnUnityPluginInstallButtonClicked)
                { text = L10n.S.SettingsUnityPluginInstallButton };
            _unityPluginInstallButton.AddToClassList("uap-settings-btn");
            _unityPluginInstallButton.AddToClassList("uap-settings-btn--primary");
            installRow.Add(_unityPluginInstallButton);

            BuildUnityPluginInstallConfirmCard(section);

            var details = new Foldout { text = L10n.S.SettingsUnityPluginDetailsFoldout, value = false };
            var detailsBody = new Label(L10n.S.SettingsUnityPluginDetailsBody);
            detailsBody.AddToClassList("uap-settings-hint");
            detailsBody.enableRichText = false;
            detailsBody.style.whiteSpace = WhiteSpace.Normal;
            details.Add(detailsBody);
            var repoField = new TextField(L10n.S.SettingsUnityPluginRepoLabel);
            repoField.AddToClassList("uap-settings-field");
            repoField.isReadOnly = true;
            repoField.SetValueWithoutNotify("https://github.com/" + UnityPluginIdentity.MarketplaceSource);
            details.Add(repoField);
            section.Add(details);

            _unityPluginSteeringToggle = new Toggle(L10n.S.SettingsUnityPluginSteeringLabel);
            _unityPluginSteeringToggle.AddToClassList("uap-settings-field");
            _unityPluginSteeringToggle.AddToClassList("uap-switch");
            _unityPluginSteeringToggle.SetValueWithoutNotify(
                PanelStateStore.instance.Settings.unityPluginSteeringEnabled);
            _unityPluginSteeringToggle.RegisterValueChangedCallback(OnUnityPluginSteeringChanged);
            VisualElement steeringScope = AddHintScope(section);
            steeringScope.Add(_unityPluginSteeringToggle);
            steeringScope.tooltip = L10n.S.SettingsUnityPluginSteeringTooltip;

            RefreshUnityPluginSection();
        }

        private void BuildUnityPluginInstallConfirmCard(VisualElement section)
        {
            _unityPluginInstallConfirmCard = new VisualElement();
            _unityPluginInstallConfirmCard.AddToClassList("uap-settings-login-subcard");
            _unityPluginInstallConfirmCard.style.display = DisplayStyle.None;

            string[] lines =
            {
                L10n.S.SettingsUnityPluginInstallConfirmTitle,
                L10n.S.SettingsUnityPluginCaveatScope,
                L10n.S.SettingsUnityPluginCaveatUnity6
            };
            for (int i = 0; i < lines.Length; i++)
            {
                var label = new Label(lines[i]);
                label.AddToClassList("uap-settings-hint");
                label.enableRichText = false;
                label.style.whiteSpace = WhiteSpace.Normal;
                _unityPluginInstallConfirmCard.Add(label);
            }

            VisualElement actionRow = AddRow(_unityPluginInstallConfirmCard);
            _unityPluginInstallApplyButton = new Button(OnUnityPluginInstallApplyClicked)
                { text = L10n.S.SettingsUnityPluginInstallApply };
            _unityPluginInstallApplyButton.AddToClassList("uap-settings-btn");
            _unityPluginInstallApplyButton.AddToClassList("uap-settings-btn--primary");
            actionRow.Add(_unityPluginInstallApplyButton);
            var cancel = new Button(HideUnityPluginInstallConfirmCard)
                { text = L10n.S.SettingsUnityPluginInstallCancel };
            cancel.AddToClassList("uap-settings-btn");
            actionRow.Add(cancel);

            section.Add(_unityPluginInstallConfirmCard);
        }

        private void OnUnityPluginInstallButtonClicked()
        {
            _unityPluginLastResult = null;
            _unityPluginInstallConfirmCard.style.display = DisplayStyle.Flex;
        }

        private void HideUnityPluginInstallConfirmCard()
        {
            if (_unityPluginInstallConfirmCard != null)
            {
                _unityPluginInstallConfirmCard.style.display = DisplayStyle.None;
            }
        }

        private void OnUnityPluginInstallApplyClicked()
        {
            if (_unityPluginInstallRunning)
            {
                return;
            }
            string cliPath = CreateCliPathProbe().Resolve();
            if (string.IsNullOrEmpty(cliPath))
            {
                RefreshUnityPluginSection();
                return;
            }
            // Arm the reload-surviving pair BEFORE the worker starts so the
            // very first refresh already renders "Installing... 0s" from a
            // real signal, and a reload during the clone re-enters Installing.
            SessionStateBridge.UnityPluginInstallInFlight = true;
            SessionStateBridge.UnityPluginInstallStartedAtUtcTicks = System.DateTime.UtcNow.Ticks;
            _unityPluginInstallRunning = true;
            _unityPluginLastResult = null;
            HideUnityPluginInstallConfirmCard();
            StartUnityPluginProgressTick();
            RefreshUnityPluginSection();
            UnityPluginInstaller.Run(cliPath, OnUnityPluginInstallCompleted);
        }

        private void OnUnityPluginInstallCompleted(UnityPluginInstallResult result)
        {
            _unityPluginInstallRunning = false;
            _unityPluginLastResult = result;
            if (result != null && !result.Success)
            {
                UnityEngine.Debug.LogWarning("[AgentPanel] Unity plugin install failed at " + result.Stage
                    + " (exit " + result.ExitCode + "): " + result.LastLine);
            }
            StopUnityPluginProgressTick();
            RefreshUnityPluginSection();
        }

        private void OnUnityPluginSteeringChanged(ChangeEvent<bool> evt)
        {
            PanelStateStore.instance.Settings.unityPluginSteeringEnabled = evt.newValue;
            PanelStateStore.instance.SaveNow();
            AgentHub.RequestAutoApplyReconnect();
        }

        private void StartUnityPluginProgressTick()
        {
            if (_unityPluginProgressTick == null && _root != null)
            {
                _unityPluginProgressTick = _root.schedule.Execute(RefreshUnityPluginSection).Every(500);
            }
            else if (_unityPluginProgressTick != null)
            {
                _unityPluginProgressTick.Resume();
            }
        }

        private void StopUnityPluginProgressTick()
        {
            if (_unityPluginProgressTick != null)
            {
                _unityPluginProgressTick.Pause();
            }
        }

        /// <summary>Hub-tick entry: the two file reads are cheap, but not free at every hub change; 2Hz like the uLoop scan.</summary>
        private void RefreshUnityPluginSectionThrottled()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _unityPluginDetectLastEvalAt < UnityPluginDetectMinIntervalSeconds)
            {
                return;
            }
            _unityPluginDetectLastEvalAt = now;
            RefreshUnityPluginSection();
        }

        /// <summary>
        /// Renders the section from two pure decisions: the detector's
        /// state (UnityPluginDetector.Resolve, fed the static files plus the
        /// hub's last system/init) and the progress machine
        /// (UnityPluginInstallProgress.Evaluate). No precedence logic of its
        /// own -- both tables are EditMode-tested where they live.
        /// </summary>
        private void RefreshUnityPluginSection()
        {
            if (_unityPluginStatusLabel == null)
            {
                return;
            }
            bool cliAvailable = !string.IsNullOrEmpty(CreateCliPathProbe().Resolve());
            UnityPluginStatus status = UnityPluginProbe.Read(cliAvailable, AgentHub.LastKnownInitMessage);
            bool installedNow = status.State == UnityPluginState.EnabledNotLoaded
                || status.State == UnityPluginState.Loaded;

            _unityPluginStatusLabel.text = DescribeUnityPluginStatus(status);
            bool showInstall = status.State == UnityPluginState.NotInstalled && !_unityPluginInstallRunning;
            _unityPluginInstallButton.style.display = showInstall ? DisplayStyle.Flex : DisplayStyle.None;
            _unityPluginInstallButton.SetEnabled(cliAvailable);
            if (!showInstall)
            {
                HideUnityPluginInstallConfirmCard();
            }

            // A reload flips the SessionState flag on but leaves the
            // worker flag off and the tick unarmed: re-arm the counter so
            // "Installing... Ns" keeps advancing until the machine settles.
            bool flagSet = SessionStateBridge.UnityPluginInstallInFlight;
            long startedTicks = SessionStateBridge.UnityPluginInstallStartedAtUtcTicks;
            double elapsedSeconds = 0.0;
            if (startedTicks > 0)
            {
                elapsedSeconds = (System.DateTime.UtcNow.Ticks - startedTicks)
                    / (double)System.TimeSpan.TicksPerSecond;
                if (elapsedSeconds < 0.0)
                {
                    elapsedSeconds = 0.0;
                }
            }
            UnityPluginInstallProgressResult progress = UnityPluginInstallProgress.Evaluate(
                _unityPluginInstallRunning,
                _unityPluginLastResult != null,
                _unityPluginLastResult != null && _unityPluginLastResult.Success,
                installedNow, flagSet, elapsedSeconds, UnityPluginInstallProgress.StaleThresholdSeconds);
            if (progress.ShouldClearFlag)
            {
                SessionStateBridge.UnityPluginInstallInFlight = false;
                SessionStateBridge.UnityPluginInstallStartedAtUtcTicks = 0;
            }
            if (progress.State == UnityPluginInstallProgressState.Installing && !_unityPluginInstallRunning)
            {
                StartUnityPluginProgressTick();
            }
            else if (progress.State != UnityPluginInstallProgressState.Installing)
            {
                StopUnityPluginProgressTick();
            }

            string text = null;
            switch (progress.State)
            {
                case UnityPluginInstallProgressState.Installing:
                    text = L10n.F(L10n.S.SettingsUnityPluginInstallingFmt, (int)elapsedSeconds);
                    break;
                case UnityPluginInstallProgressState.Done:
                    text = L10n.S.SettingsUnityPluginInstalled;
                    break;
                case UnityPluginInstallProgressState.Failed:
                    text = DescribeUnityPluginFailure(_unityPluginLastResult);
                    break;
                case UnityPluginInstallProgressState.Stalled:
                    text = L10n.S.SettingsUnityPluginInstallStalled;
                    break;
            }
            _unityPluginProgressLabel.text = text ?? string.Empty;
            _unityPluginProgressLabel.style.display = text == null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Pure: state -> status line (design note section 1.2 table). Internal so SettingsViewLogicTests can pin every row.</summary>
        internal static string DescribeUnityPluginStatus(UnityPluginStatus status)
        {
            if (status == null)
            {
                return string.Empty;
            }
            string version = string.IsNullOrEmpty(status.Version) ? "?" : status.Version;
            switch (status.State)
            {
                case UnityPluginState.CliUnavailable:
                    return L10n.S.SettingsUnityPluginStatusCliUnavailable;
                case UnityPluginState.Disabled:
                    return L10n.S.SettingsUnityPluginStatusDisabled;
                case UnityPluginState.EnabledNotLoaded:
                    return status.SessionObserved
                        ? L10n.F(L10n.S.SettingsUnityPluginStatusEnabledNotLoadedFmt, version)
                        : L10n.F(L10n.S.SettingsUnityPluginStatusInstalledFmt, version);
                case UnityPluginState.Loaded:
                    return L10n.F(L10n.S.SettingsUnityPluginStatusLoadedFmt, version);
                case UnityPluginState.LoadError:
                    return L10n.F(L10n.S.SettingsUnityPluginStatusLoadErrorFmt, status.ErrorMessage ?? string.Empty);
                default:
                    return L10n.S.SettingsUnityPluginStatusNotInstalled;
            }
        }

        /// <summary>Pure: failed result -> one line naming the stage, exit code and the CLI's last line.</summary>
        internal static string DescribeUnityPluginFailure(UnityPluginInstallResult result)
        {
            if (result == null)
            {
                return string.Empty;
            }
            string stage = result.Stage == UnityPluginInstallStage.MarketplaceAdd
                ? L10n.S.SettingsUnityPluginStageMarketplace
                : L10n.S.SettingsUnityPluginStageInstall;
            return L10n.F(L10n.S.SettingsUnityPluginInstallFailedFmt, stage, result.ExitCode,
                result.LastLine ?? string.Empty).TrimEnd();
        }

        /// <summary>
        /// UXIA-2 (SR section 5.2): the collapsed-by-default variant of
        /// AddSection for ADVANCED sections. Fifteen always-expanded cards
        /// gave connection plumbing (CLI, Diagnostics) and rarely-touched
        /// machinery (UapOps modules, Extension Profiles, uLoop) the same
        /// visual weight as the everyday knobs; these now render as a
        /// Foldout-headed card, collapsed until opened, remembering their
        /// state per editor session. Same generalization the danger-zone
        /// and agent-overrides foldouts already proved out; the section
        /// icon survives via AddFoldoutHeaderIcon. Returns the body
        /// container, exactly like AddSection, so the Build*Section
        /// callers only swap the one call.
        /// </summary>
        private static VisualElement AddCollapsibleSection(VisualElement parent, string titleText,
            string iconName, string fallbackGlyph, string sectionId)
        {
            var card = new VisualElement();
            card.AddToClassList("uap-settings-card");
            card.AddToClassList("uap-settings-card--collapsible");

            string stateKey = SectionDisclosureKey(sectionId);
            var foldout = new Foldout
            {
                text = titleText,
                value = SessionState.GetBool(stateKey, false)
            };
            foldout.AddToClassList("uap-settings-section-foldout");
            AddFoldoutHeaderIcon(foldout, iconName, fallbackGlyph);
            foldout.RegisterValueChangedCallback(delegate(ChangeEvent<bool> evt)
            {
                // Toggles INSIDE the section body bubble ChangeEvent<bool>
                // through this same handler; only the foldout's own header
                // toggle may write the disclosure state.
                if (ReferenceEquals(evt.target, foldout))
                {
                    SessionState.SetBool(stateKey, evt.newValue);
                }
            });
            card.Add(foldout);

            var body = new VisualElement();
            body.AddToClassList("uap-settings-card-body");
            foldout.Add(body);

            parent.Add(card);
            return body;
        }

        /// <summary>
        /// Prepends a small icon to a Foldout's header row, right before its
        /// label, so the state the Foldout hides (e.g. "this is dangerous")
        /// still reads with the Foldout collapsed. Foldout's own API only
        /// takes a plain string for `text` (R06 section 3.5), so the icon
        /// has to be spliced into the internal Toggle it renders with --
        /// found defensively by class name rather than assumed index order,
        /// since BaseField-derived controls are not guaranteed to lay out
        /// label-before-input for every control type (Toggle shows
        /// checkmark-then-label, unlike most other fields).
        /// </summary>
        private static void AddFoldoutHeaderIcon(Foldout foldout, string iconName, string fallbackGlyph)
        {
            Toggle header = foldout.Q<Toggle>(className: "unity-foldout__toggle");
            if (header == null)
            {
                return;
            }
            VisualElement icon = IconLoader.CreateIcon(iconName, fallbackGlyph,
                "uap-settings-danger-icon", "uap-settings-danger-icon-glyph");
            Label label = header.Q<Label>(className: "unity-toggle__label");
            if (label != null && label.parent != null)
            {
                label.parent.Insert(label.parent.IndexOf(label), icon);
            }
            else
            {
                header.Insert(0, icon);
            }
        }

        private static VisualElement AddRow(VisualElement section)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-settings-row");
            section.Add(row);
            return row;
        }

        /// <summary>
        /// internal (not private) so SettingsViewLogicTests can exercise
        /// both AddHint overloads and AddWarning directly -- same
        /// rationale as AreStringListsEqual above: SettingsView has no
        /// test-instantiation seam for the full BuildRoot tree, so these
        /// pure DOM-construction helpers are the testable surface.
        /// </summary>
        internal static Label AddHint(VisualElement parent, string text)
        {
            var hint = new Label(text);
            hint.AddToClassList("uap-settings-hint");
            hint.enableRichText = false;
            hint.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(hint);
            return hint;
        }

        /// <summary>
        /// Overload that also sets a hover tooltip on `parent`, the
        /// mechanism docs/design-notes/2026-08-04-settings-annotation-
        /// load.md was written to justify (section 3): tooltip set on a
        /// parent is delivered to a hovered child that has no tooltip of
        /// its own, and a hovered child's own tooltip wins if both are
        /// set -- so ONE tooltip on a field's dedicated container (see
        /// AddHintScope below) covers hovering either the field's own
        /// label or its control, without needing to set it twice. The
        /// note's section 4 verdict was "inline stays one short line, the
        /// long explanation moves to the tooltip" -- so `text` here is
        /// expected to be the SHORT inline line and `tooltip` the fuller
        /// text that used to sit inline before this pass.
        ///
        /// If `text` is null/empty but `tooltip` is not, no Label is
        /// added at all -- a control whose explanation is entirely
        /// hover-only must not leave an empty line eating the vertical
        /// space this overload exists to reclaim. The 2-arg overload
        /// above is untouched (always adds its Label, tooltip or not) so
        /// every pre-existing call site keeps behaving exactly as before.
        ///
        /// IMPORTANT: `parent` must be scoped to exactly the one field
        /// this call describes (see AddHintScope). Passing a
        /// multi-field container (a whole `.uap-settings-card-body`, an
        /// entire Foldout) works for the FIRST hint set on it, but every
        /// later call on the same `parent` overwrites the property --
        /// only the last one survives -- and every other field sharing
        /// that parent with no tooltip of its own then shows the WRONG
        /// (most-recently-set) tooltip on hover, per the bubbling rule
        /// above. This bit twice while wiring this file's own call sites
        /// (Unity Operations alone has five tooltip-bearing hints sharing
        /// one card body) before AddHintScope was introduced to close it.
        /// </summary>
        internal static Label AddHint(VisualElement parent, string text, string tooltip)
        {
            Label label = null;
            if (!string.IsNullOrEmpty(text))
            {
                label = AddHint(parent, text);
            }
            if (!string.IsNullOrEmpty(tooltip))
            {
                parent.tooltip = tooltip;
            }
            return label;
        }

        /// <summary>
        /// Sibling of AddHint for the three settings design-notes/2026-08-
        /// 04-settings-annotation-load.md section 4 explicitly keeps OFF
        /// the inline-to-tooltip move: the script-validation gate, the
        /// auto-approve level, and auto-continue-after-compile. All three
        /// are warnings about what ENABLING the setting does, and "a
        /// warning that only reads on hover does not function as a
        /// warning" (same section) -- so the short line stays permanently
        /// visible on the card (uap-settings-hint--warning, AgentPanel.uss),
        /// while the longer detail that used to sit inline is still one
        /// hover away via the 3-arg overload below, same as an ordinary
        /// hint. Never leaves the label out for an empty `text` (unlike
        /// AddHint's 3-arg overload) -- an invisible warning is a
        /// contradiction in terms, so this helper does not offer that option.
        /// </summary>
        internal static Label AddWarning(VisualElement parent, string text)
        {
            var warning = new Label(text);
            warning.AddToClassList("uap-settings-hint");
            warning.AddToClassList("uap-settings-hint--warning");
            warning.enableRichText = false;
            warning.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(warning);
            return warning;
        }

        /// <summary>3-arg AddWarning: same tooltip mechanics as AddHint's 3-arg overload above (including the AddHintScope caveat) applied to the always-visible warning label instead of a plain hint.</summary>
        internal static Label AddWarning(VisualElement parent, string text, string tooltip)
        {
            Label warning = AddWarning(parent, text);
            if (!string.IsNullOrEmpty(tooltip))
            {
                parent.tooltip = tooltip;
            }
            return warning;
        }

        // -- Value-driven warning tone (2026-09-06 settings review) -------------
        //
        // Amber used to mark all three "what enabling this does" lines
        // permanently -- including on the SAFE value, where a warning colour
        // next to the recommended default reads as "something is wrong".
        // The line stays visible either way (a warning that only reads on
        // hover is not a warning); only its tone now follows the value:
        // amber when the chosen value is the risky one, plain otherwise.

        internal static bool IsRiskyAutoApproveLevel(UapAutoApproveLevel level)
        {
            return level != UapAutoApproveLevel.Ask && level != UapAutoApproveLevel.ReadOnly;
        }

        internal static bool IsRiskyScriptGateSetting(bool gateEnabled)
        {
            return !gateEnabled;
        }

        internal static bool IsRiskyAutoContinueSetting(bool autoContinueEnabled)
        {
            return autoContinueEnabled;
        }

        internal static void SetWarningTone(Label label, bool warn)
        {
            if (label != null)
            {
                label.EnableInClassList("uap-settings-hint--warning", warn);
            }
        }

        private void RefreshWarningTones()
        {
            Colloid.AgentPanel.Model.PanelSettings settings = PanelStateStore.instance.Settings;
            SetWarningTone(_autoApproveWarningLabel, IsRiskyAutoApproveLevel(settings.autoApproveLevel));
            SetWarningTone(_gateWarningLabel, IsRiskyScriptGateSetting(settings.uapScriptGateEnabled));
            SetWarningTone(_autoContinueWarningLabel, IsRiskyAutoContinueSetting(settings.uapOpsAutoContinueAfterCompile));
            SetWarningTone(_autoContinueInterruptedWarningLabel, IsRiskyAutoContinueSetting(settings.autoContinueInterruptedTurn));
            // Design note 2026-09-10 section 4: EditorSettings.
            // enterPlayModeOptions* is a PROJECT setting the user may
            // change (Project Settings > Editor) while this panel stays
            // open, so the hint is re-read here alongside the other
            // value-driven labels above rather than only once at build time.
            if (_playModeReloadHintLabel != null)
            {
                _playModeReloadHintLabel.text = PlayModeReloadAdvisor.ReloadsOnPlayNow()
                    ? L10n.S.SettingsPlayModeReloadOnHint
                    : L10n.S.SettingsPlayModeReloadOffHint;
            }
        }

        /// <summary>
        /// The CJK font diagnostic showed FontLoader's internal source tag
        /// ("osasset:Noto Sans CJK JP"); the user only needs the font name.
        /// Pure: everything after the last ':' of a "tag:name" string, the
        /// string itself otherwise.
        /// </summary>
        internal static string DescribeFontSource(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }
            int colon = source.LastIndexOf(':');
            return colon >= 0 && colon < source.Length - 1 ? source.Substring(colon + 1) : source;
        }

        /// <summary>
        /// Returns a new, unstyled VisualElement appended to `parent` --
        /// the "field's row/container" AddHint/AddWarning's 3-arg
        /// overloads need so a field's tooltip does not leak onto
        /// unrelated siblings sharing the same multi-field card (see
        /// AddHint's 3-arg doc comment for the bug this closes). Every
        /// call site that wires a tooltip adds its control (if any) and
        /// its hint/warning label into this scope INSTEAD OF the outer
        /// `section`/foldout, so the scope becomes the smallest container
        /// that holds exactly that one field.
        ///
        /// Deliberately carries no CSS class: .uap-settings-card-body
        /// (the `section` AddSection returns) sets no flex-direction or
        /// align-items override of its own (AgentPanel.uss), so nesting
        /// an unstyled VisualElement one level deeper between it and a
        /// field changes nothing about that field's width or spacing --
        /// both levels fall back to the same UI Toolkit defaults. Adding
        /// a class here (even an empty one, for a future style hook)
        /// would risk exactly the kind of silent layout drift this
        /// project's memory of repeated flex-grow/shrink regressions
        /// warns about, for no benefit this pass needs.
        /// </summary>
        private static VisualElement AddHintScope(VisualElement parent)
        {
            var scope = new VisualElement();
            parent.Add(scope);
            return scope;
        }

        // -- Pure helpers (EditMode tested via SettingsViewLogicTests) ------------------

        /// <summary>One string per non-empty, trimmed line; empty/null input yields an empty list.</summary>
        public static List<string> SplitLines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
            return result;
        }

        /// <summary>Inverse of SplitLines for populating the multiline field; null list yields empty string.</summary>
        public static string JoinLines(List<string> items)
        {
            return items == null ? string.Empty : string.Join("\n", items);
        }

        // -- Model section pure helpers (v0.8.0, EditMode tested via
        // SettingsViewLogicTests) ---------------------------------------------------

        /// <summary>
        /// Choice list for a model PopupField: always starts with the empty
        /// ("(Default)") sentinel, then every distinct catalog value in
        /// order, then `currentValue` appended at the end if it is
        /// non-empty and not already present (so a stale/manual value never
        /// silently vanishes from the dropdown it is currently set to).
        /// Never empty -- PopupField requires its initial value to be a
        /// member of `choices`, and the empty sentinel guarantees that even
        /// when the catalog itself is empty and currentValue is empty too.
        /// </summary>
        public static List<string> BuildModelChoices(List<ModelCatalogEntry> catalog, string currentValue)
        {
            var result = new List<string> { string.Empty };
            if (catalog != null)
            {
                for (int i = 0; i < catalog.Count; i++)
                {
                    ModelCatalogEntry entry = catalog[i];
                    string value = entry != null ? entry.value : null;
                    if (!string.IsNullOrEmpty(value) && !result.Contains(value))
                    {
                        result.Add(value);
                    }
                }
            }
            if (!string.IsNullOrEmpty(currentValue) && !result.Contains(currentValue))
            {
                result.Add(currentValue);
            }
            return result;
        }

        /// <summary>currentValue when present in choices, else choices[0] (always the empty sentinel per BuildModelChoices). Null-safe.</summary>
        public static string ResolveInitialModelChoice(List<string> choices, string currentValue)
        {
            if (choices == null || choices.Count == 0)
            {
                return string.Empty;
            }
            string want = currentValue ?? string.Empty;
            for (int i = 0; i < choices.Count; i++)
            {
                if (string.Equals(choices[i], want, System.StringComparison.Ordinal))
                {
                    return choices[i];
                }
            }
            return choices[0];
        }

        /// <summary>
        /// True when both fields of an override row are filled in --
        /// AgentDefinitionFileWriter.Sync skips any entry missing EITHER
        /// AgentName or ModelAlias when materializing `.claude/agents/
        /// &lt;name&gt;.md` files, so only a row that satisfies this actually
        /// changes what gets spawned. No longer used to gate a reconnect
        /// hint/auto-apply request (docs/research/07-model-configuration.md
        /// section 10.8, "capture15": reconnecting an existing session can
        /// never apply an agentModelOverrides change regardless of row
        /// completeness, so AddAgentOverrideRow's field handlers stopped
        /// calling AgentHub.RequestAutoApplyReconnect entirely) -- kept as
        /// the pure "does this row actually do anything" definition for
        /// SettingsViewLogicTests and any future UI that wants it (e.g. a
        /// per-row "inactive" indicator).
        /// </summary>
        public static bool IsCompleteAgentOverride(AgentModelOverride entry)
        {
            return entry != null
                && !string.IsNullOrEmpty(entry.agentName)
                && !string.IsNullOrEmpty(entry.modelAlias);
        }

        /// <summary>
        /// True when two or more entries share the same non-empty, trimmed
        /// agentName (ordinal comparison). AgentDefinitionFileWriter.Sync
        /// writes one file per distinct agentName in list order, so a
        /// duplicate name silently collapses to the LAST matching entry's
        /// model (both entries target the same file path) -- there is no
        /// error from the write itself, so the panel is the only place
        /// that can ever surface it. This only decides whether to show that
        /// warning; it never changes which entry wins.
        /// </summary>
        public static bool HasDuplicateAgentOverrideNames(List<AgentModelOverride> overrides)
        {
            if (overrides == null || overrides.Count < 2)
            {
                return false;
            }
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < overrides.Count; i++)
            {
                AgentModelOverride entry = overrides[i];
                string name = entry != null ? (entry.agentName ?? string.Empty).Trim() : string.Empty;
                if (name.Length == 0)
                {
                    continue;
                }
                if (!seen.Add(name))
                {
                    return true;
                }
            }
            return false;
        }

        // -- v0.9.0 additions (docs/design-notes/2026-08-01-model-settings-
        // rework.md section 4.2) -- agent-name choice union and the
        // subagent-model/per-type precedence warning predicate, both EditMode
        // tested via SettingsViewLogicTests. -------------------------------

        /// <summary>
        /// Union of agent type names from three sources, deduplicated
        /// (ordinal) while preserving first-seen order: `cachedCatalog`
        /// (PanelSettings.agentTypeCatalog, refreshed from system/init.
        /// agents[] on any past connection), then `liveAgents` (the SAME
        /// array from the CURRENT connection, if any -- may legitimately
        /// list a name not yet cached), then every existing override
        /// entry's agentName (so a stale/manual entry the CLI no longer
        /// reports stays selectable, per docs/design-notes/2026-08-01-
        /// model-settings-rework.md work item C). Null lists are treated
        /// as empty; null/empty names are never included. Never itself
        /// includes the empty-sentinel choice -- callers needing a "please
        /// select" option (BuildAgentOverrideRow's PopupField) prepend it.
        /// </summary>
        public static List<string> BuildAgentTypeChoices(List<string> cachedCatalog, IList<string> liveAgents,
            List<AgentModelOverride> overrides)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            AppendDistinctNonEmpty(result, seen, cachedCatalog);
            AppendDistinctNonEmpty(result, seen, liveAgents);
            if (overrides != null)
            {
                for (int i = 0; i < overrides.Count; i++)
                {
                    AgentModelOverride entry = overrides[i];
                    string name = entry != null ? entry.agentName : null;
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                    {
                        result.Add(name);
                    }
                }
            }
            return result;
        }

        private static void AppendDistinctNonEmpty(List<string> result, HashSet<string> seen, IList<string> source)
        {
            if (source == null)
            {
                return;
            }
            for (int i = 0; i < source.Count; i++)
            {
                string value = source[i];
                if (!string.IsNullOrEmpty(value) && seen.Add(value))
                {
                    result.Add(value);
                }
            }
        }

        /// <summary>
        /// True when a blanket subagentModel is set AND at least one
        /// per-type override row would actually materialize a
        /// `.claude/agents/&lt;name&gt;.md` file (IsCompleteAgentOverride) --
        /// the precondition for showing SettingsSubagentPrecedenceWarning
        /// (R07 section 11, P3: the measured CLAUDE_CODE_SUBAGENT_MODEL env
        /// var beats those files outright, so any complete row is
        /// currently inert while subagentModel stays non-empty). A blank
        /// placeholder row (just added via the "+" button, not yet filled
        /// in) never trips this on its own.
        /// </summary>
        public static bool HasSubagentModelPrecedenceConflict(string subagentModel,
            List<AgentModelOverride> overrides)
        {
            if (string.IsNullOrEmpty(subagentModel) || overrides == null)
            {
                return false;
            }
            for (int i = 0; i < overrides.Count; i++)
            {
                if (IsCompleteAgentOverride(overrides[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
