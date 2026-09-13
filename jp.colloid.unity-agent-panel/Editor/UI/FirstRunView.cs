using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PanelSettings = Colloid.AgentPanel.Model.PanelSettings;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The two setup cards that replace the chat until the CLI is usable:
    /// "CLI not found" (with the in-panel installer, design note
    /// docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md
    /// section 1) and "not logged in" (Claude Code's `auth login`, or an ACP
    /// agent's own login command -- design note 2026-09-13-acp-feature-
    /// parity.md section 2). Backend-aware: title,
    /// body, install command and the path field's target setting follow
    /// PanelSettings.agentBackend (design note 2026-09-10-acp-backends.md
    /// section 4).
    /// </summary>
    public sealed class FirstRunView
    {
        public enum Mode
        {
            Hidden,
            CliNotFound,
            NotLoggedIn
        }

        private readonly VisualElement _root;
        private readonly VisualElement _cliCard;
        private readonly VisualElement _loginCard;
        private readonly TextField _pathField;
        private readonly Label _probeDetail;
        private readonly Label _cliTitle;
        private readonly Label _cliBody;
        private readonly Button _installButton;
        private readonly Button _nodeButton;
        private readonly Label _installStatus;
        private readonly Foldout _manualFoldout;
        private readonly VisualElement _manualCommandHost;
        private readonly Label _acpLoginHint;
        private readonly Label _loginTitle;
        private readonly Label _loginLead;
        private readonly Label _loginBody2;
        private readonly Label _loginAltBody;
        private readonly VisualElement _loginAltCommandHost;
        private AgentBackend _loginCardBackend = AgentBackend.ClaudeCode;
        private bool _loginCardBuiltOnce;
        private IVisualElementScheduledItem _installTick;
        private Mode _mode = Mode.Hidden;
        private AgentBackend _cardBackend = AgentBackend.ClaudeCode;
        private bool _cardBuiltOnce;

        public VisualElement Root
        {
            get { return _root; }
        }

        public Mode CurrentMode
        {
            get { return _mode; }
        }

        public FirstRunView()
        {
            _root = new VisualElement();
            _root.AddToClassList("uap-firstrun");
            _root.style.display = DisplayStyle.None;

            // -- Card A: CLI not found ---------------------------------------
            _cliCard = new VisualElement();
            _cliCard.AddToClassList("uap-card");

            _cliTitle = MakeTitle(L10n.S.FirstRunCliNotFoundTitle);
            _cliCard.Add(_cliTitle);
            _cliBody = MakeBody(L10n.S.FirstRunCliNotFoundBody);
            _cliCard.Add(_cliBody);

            // Primary path: one click runs the backend's installer; the
            // command it runs is shown in the confirmation dialog and in
            // the manual foldout below.
            var installRow = new VisualElement();
            installRow.AddToClassList("uap-card-row");
            _installButton = new Button(OnInstallClicked) { text = string.Empty };
            _installButton.AddToClassList("uap-card-btn");
            _installButton.AddToClassList("uap-card-btn--primary");
            installRow.Add(_installButton);
            _nodeButton = new Button(OnGetNodeClicked) { text = L10n.S.InstallOpenNodeButton };
            _nodeButton.AddToClassList("uap-card-btn");
            _nodeButton.style.display = DisplayStyle.None;
            installRow.Add(_nodeButton);
            _cliCard.Add(installRow);

            _installStatus = MakeBody(string.Empty);
            _installStatus.style.display = DisplayStyle.None;
            _cliCard.Add(_installStatus);

            _manualFoldout = new Foldout { text = L10n.S.InstallManualFoldout, value = false };
            _manualFoldout.AddToClassList("uap-card-foldout");
            _manualFoldout.Add(MakeBody(L10n.S.InstallManualBody));
            _manualCommandHost = new VisualElement();
            _manualFoldout.Add(_manualCommandHost);
            _cliCard.Add(_manualFoldout);

            _acpLoginHint = MakeBody(string.Empty);
            _acpLoginHint.style.display = DisplayStyle.None;
            _cliCard.Add(_acpLoginHint);

            var pathRow = new VisualElement();
            pathRow.AddToClassList("uap-card-row");
            _pathField = new TextField();
            _pathField.AddToClassList("uap-card-path");
            _pathField.SetValueWithoutNotify(CurrentCommandSetting());
            pathRow.Add(_pathField);
            var browse = new Button(OnBrowseClicked) { text = L10n.S.FirstRunBrowseButton };
            browse.AddToClassList("uap-card-btn");
            browse.style.marginRight = 0f;
            pathRow.Add(browse);
            _cliCard.Add(pathRow);

            var actionRow = new VisualElement();
            actionRow.AddToClassList("uap-card-row");
            var redetect = new Button(OnRedetectClicked) { text = L10n.S.FirstRunRedetectButton };
            redetect.AddToClassList("uap-card-btn");
            actionRow.Add(redetect);
            _cliCard.Add(actionRow);

            _probeDetail = new Label(string.Empty);
            _probeDetail.AddToClassList("uap-card-detail");
            _probeDetail.enableRichText = false;
            _cliCard.Add(_probeDetail);
            _root.Add(_cliCard);

            // -- Card B: not logged in (Claude Code, or an ACP agent with a
            // login command the panel can run) ------------------------------
            _loginCard = new VisualElement();
            _loginCard.AddToClassList("uap-card");

            // UXO-4 (SR section 4.1): the card LEADS with the in-panel
            // login -- one primary button whose lead text says exactly what
            // pressing it does (opens the Settings Account card's login
            // flow; the panel reconnects itself on success). The terminal
            // "claude" + "/login" walkthrough that used to open the card is
            // real but secondary, so it lives in a collapsed foldout with
            // its Check-again button; a first-run user should not have to
            // read shell instructions to find the one-click path.
            _loginTitle = MakeTitle(L10n.S.FirstRunLoginTitle);
            _loginCard.Add(_loginTitle);
            _loginLead = MakeBody(L10n.S.FirstRunLoginLead);
            _loginCard.Add(_loginLead);
            _loginBody2 = MakeBody(L10n.S.FirstRunLoginBody2);
            _loginCard.Add(_loginBody2);

            var loginRow = new VisualElement();
            loginRow.AddToClassList("uap-card-row");
            // In-panel login (docs/design-notes/2026-08-02-auth-in-panel.md
            // work item D): starts the SAME AgentHub.BeginLogin() flow the
            // Settings Account card drives, then jumps there -- this card
            // never duplicates the login flow UI itself.
            var login = new Button(OnLoginClicked) { text = L10n.S.FirstRunLoginButton };
            login.AddToClassList("uap-card-btn");
            login.AddToClassList("uap-card-btn--primary");
            loginRow.Add(login);
            _loginCard.Add(loginRow);

            var alt = new Foldout { text = L10n.S.FirstRunLoginAltFoldout, value = false };
            alt.AddToClassList("uap-card-foldout");
            _loginAltBody = MakeBody(L10n.S.FirstRunLoginAltBody);
            alt.Add(_loginAltBody);
            _loginAltCommandHost = new VisualElement();
            _loginAltCommandHost.Add(MessageBlockFactory.CreateCodeBlock("claude\n/login", "shell"));
            alt.Add(_loginAltCommandHost);
            var altRow = new VisualElement();
            altRow.AddToClassList("uap-card-row");
            var recheck = new Button(OnRecheckClicked) { text = L10n.S.FirstRunCheckAgainButton };
            recheck.AddToClassList("uap-card-btn");
            altRow.Add(recheck);
            alt.Add(altRow);
            _loginCard.Add(alt);
            _root.Add(_loginCard);
        }

        public void SetMode(Mode mode, string probeDetail)
        {
            if (mode == Mode.CliNotFound)
            {
                _probeDetail.text = probeDetail ?? string.Empty;
                ApplyCardBackend(AgentHub.CurrentBackend);
                RefreshInstallState();
            }
            if (mode == Mode.NotLoggedIn)
            {
                ApplyLoginCardBackend(AgentHub.CurrentBackend);
            }
            if (_mode == mode)
            {
                return;
            }
            _mode = mode;
            _root.style.display = mode == Mode.Hidden ? DisplayStyle.None : DisplayStyle.Flex;
            _cliCard.style.display = mode == Mode.CliNotFound
                ? DisplayStyle.Flex : DisplayStyle.None;
            _loginCard.style.display = mode == Mode.NotLoggedIn
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (mode == Mode.CliNotFound)
            {
                _pathField.SetValueWithoutNotify(CurrentCommandSetting());
            }
            if (mode != Mode.CliNotFound)
            {
                StopInstallTick();
            }
        }

        /// <summary>The setting the card's path field edits for the selected backend.</summary>
        private static string CurrentCommandSetting()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            return (AgentBackends.IsAcp(settings.agentBackend)
                ? settings.acpCommand : settings.cliManualPath) ?? string.Empty;
        }

        private static bool IsWindows()
        {
            return Application.platform == RuntimePlatform.WindowsEditor;
        }

        private void ApplyCardBackend(AgentBackend backend)
        {
            if (backend == _cardBackend && _cardBuiltOnce)
            {
                return;
            }
            _cardBackend = backend;
            _cardBuiltOnce = true;
            string name = AgentBackends.DisplayName(backend);
            string command = CliInstallPlan.DisplayCommandFor(backend, IsWindows());
            _manualCommandHost.Clear();
            bool installable = command.Length > 0;
            if (installable)
            {
                _manualCommandHost.Add(MessageBlockFactory.CreateCodeBlock(command, "shell"));
            }
            _manualFoldout.style.display = installable ? DisplayStyle.Flex : DisplayStyle.None;
            _installButton.text = L10n.F(L10n.S.InstallButtonFmt, name);
            _installButton.style.display = installable ? DisplayStyle.Flex : DisplayStyle.None;

            if (!AgentBackends.IsAcp(backend))
            {
                _cliTitle.text = L10n.S.FirstRunCliNotFoundTitle;
                _cliBody.text = L10n.S.FirstRunCliNotFoundBody;
                _acpLoginHint.style.display = DisplayStyle.None;
                return;
            }
            _cliTitle.text = L10n.F(L10n.S.FirstRunAcpNotFoundTitleFmt, name);
            _cliBody.text = L10n.S.FirstRunAcpNotFoundBody;
            string authHint = L10n.AcpAuthHint(backend, L10n.S.FirstRunAcpLoginHintFmt);
            _acpLoginHint.text = authHint;
            _acpLoginHint.tooltip = L10n.AcpAuthTooltip(backend, L10n.S.FirstRunAcpLoginHintFmt);
            _acpLoginHint.style.display = string.IsNullOrEmpty(authHint)
                ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// The login card's copy for the selected backend (design note
        /// 2026-09-13-acp-feature-parity.md section 2): Claude Code's
        /// original text, or "Sign in to {agent}" around the agent's own
        /// login command -- which the primary button runs in the panel and
        /// the foldout shows for a terminal.
        /// </summary>
        private void ApplyLoginCardBackend(AgentBackend backend)
        {
            if (backend == _loginCardBackend && _loginCardBuiltOnce)
            {
                return;
            }
            _loginCardBackend = backend;
            _loginCardBuiltOnce = true;
            _loginAltCommandHost.Clear();
            if (!AgentBackends.IsAcp(backend))
            {
                _loginTitle.text = L10n.S.FirstRunLoginTitle;
                _loginLead.text = L10n.S.FirstRunLoginLead;
                _loginBody2.text = L10n.S.FirstRunLoginBody2;
                _loginBody2.style.display = DisplayStyle.Flex;
                _loginAltBody.text = L10n.S.FirstRunLoginAltBody;
                _loginAltCommandHost.Add(MessageBlockFactory.CreateCodeBlock("claude\n/login", "shell"));
                return;
            }
            string name = AgentBackends.DisplayName(backend);
            string command = AgentBackends.LoginExecutable(backend);
            string arguments = AgentBackends.LoginArguments(backend);
            string commandLine = arguments.Length == 0 ? command : command + " " + arguments;
            _loginTitle.text = L10n.F(L10n.S.FirstRunAcpLoginTitleFmt, name);
            _loginLead.text = L10n.F(L10n.S.FirstRunAcpLoginLeadFmt, name, commandLine);
            string authHint = L10n.AcpAuthSummary(backend);
            _loginBody2.text = authHint;
            _loginBody2.style.display = authHint.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _loginAltBody.text = L10n.S.FirstRunAcpLoginAltBody;
            _loginAltCommandHost.Add(MessageBlockFactory.CreateCodeBlock(commandLine, "shell"));
        }

        // -- Install state ------------------------------------------------------

        private void RefreshInstallState()
        {
            bool running = AgentHub.CliInstallRunning;
            CliInstallResult last = AgentHub.LastCliInstallResult;
            string name = AgentBackends.DisplayName(_cardBackend);
            _installButton.SetEnabled(!running);
            string text = DescribeInstallState(running, last, name,
                AgentHub.CliInstallStartedUtcTicks, System.DateTime.UtcNow.Ticks);
            _installStatus.text = text ?? string.Empty;
            _installStatus.style.display = text == null ? DisplayStyle.None : DisplayStyle.Flex;
            _nodeButton.style.display = !running && last != null
                && last.Failure == CliInstallFailureKind.NodeMissing
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (running)
            {
                StartInstallTick();
            }
            else
            {
                StopInstallTick();
            }
        }

        /// <summary>
        /// Pure: the status line under the Install button (null = hidden).
        /// Shared with SettingsView so both surfaces say the same thing.
        /// </summary>
        internal static string DescribeInstallState(bool running, CliInstallResult last, string backendName,
            long startedUtcTicks, long nowUtcTicks)
        {
            if (running)
            {
                long elapsed = startedUtcTicks > 0 ? (nowUtcTicks - startedUtcTicks) / System.TimeSpan.TicksPerSecond : 0;
                if (elapsed < 0)
                {
                    elapsed = 0;
                }
                return L10n.F(L10n.S.InstallRunningFmt, backendName, elapsed);
            }
            if (last == null)
            {
                return null;
            }
            if (last.Success)
            {
                return L10n.F(L10n.S.InstallDoneFmt, backendName);
            }
            switch (last.Failure)
            {
                case CliInstallFailureKind.NodeMissing:
                    return L10n.S.InstallNodeMissing;
                case CliInstallFailureKind.ShellMissing:
                    return L10n.S.InstallShellMissing;
                case CliInstallFailureKind.TimedOut:
                    return L10n.S.InstallTimedOut;
                default:
                    return L10n.F(L10n.S.InstallFailedFmt, last.ExitCode, last.LastLine ?? string.Empty);
            }
        }

        private void StartInstallTick()
        {
            if (_installTick == null)
            {
                _installTick = _root.schedule.Execute(RefreshInstallState).Every(500);
            }
            else
            {
                _installTick.Resume();
            }
        }

        private void StopInstallTick()
        {
            if (_installTick != null)
            {
                _installTick.Pause();
            }
        }

        // -- Actions ------------------------------------------------------------

        private void OnInstallClicked()
        {
            AgentBackend backend = AgentHub.CurrentBackend;
            string command = CliInstallPlan.DisplayCommandFor(backend, IsWindows());
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
            RefreshInstallState();
        }

        private static void OnGetNodeClicked()
        {
            Application.OpenURL(CliInstallPlan.NodeDownloadUrl);
        }

        private void OnBrowseClicked()
        {
            string picked = EditorUtility.OpenFilePanel(L10n.S.FirstRunBrowseDialogTitle,
                string.Empty, string.Empty);
            if (!string.IsNullOrEmpty(picked))
            {
                _pathField.value = picked;
            }
        }

        private void OnRedetectClicked()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            string value = (_pathField.value ?? string.Empty).Trim();
            if (AgentBackends.IsAcp(settings.agentBackend))
            {
                settings.acpCommand = value;
            }
            else
            {
                settings.cliManualPath = value;
            }
            PanelStateStore.instance.SaveNow();
            // EnsureStarted re-runs the path probe and raises Changed either
            // way, so the surrounding ChatView refreshes automatically.
            AgentHub.EnsureStarted();
        }

        private static void OnRecheckClicked()
        {
            // Tear the client down and reconnect (resumes the cached
            // session id); a successful login shows up as a working init.
            AgentHub.RecoverConnection();
        }

        private static void OnLoginClicked()
        {
            AgentHub.BeginLogin();
            // ShowSettingsAccount (not plain ShowSettings) also scrolls the
            // Settings view to the Account card -- see its doc comment.
            AgentPanelWindow.ShowSettingsAccount();
        }

        // -- Small builders --------------------------------------------------------

        private static Label MakeTitle(string text)
        {
            var label = new Label(text);
            label.AddToClassList("uap-card-title");
            return label;
        }

        private static Label MakeBody(string text)
        {
            var label = new Label(text);
            label.AddToClassList("uap-card-body");
            label.enableRichText = false;
            return label;
        }
    }
}
