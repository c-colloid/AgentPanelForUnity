using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Header band (28px, R05 section 2.2): session title, model picker,
    /// history button, new-session, settings gear and a compact reconnect
    /// affordance shown only in the Errored state. All navigation goes
    /// through AgentPanelWindow's ShowChat/ShowSettings/ShowHistory (the
    /// single view-switch entry point AgentPanelWindow.cs already
    /// establishes -- see docs/design-notes/2026-07-31-view-container-switching.md)
    /// and the Errored-state reconnect calls the SAME AgentHub.EnsureStarted
    /// entry point ResumeBanner's own reconnect button uses, per the task's
    /// "keep them consistent through one AgentHub entry point" requirement.
    /// </summary>
    public sealed class HeaderView
    {
        private readonly VisualElement _root;
        private readonly Label _spark;
        private readonly Label _title;
        private readonly Button _modelPicker;
        private readonly Button _autoApprove;
        private readonly Button _compact;
        private readonly Button _historyButton;
        private readonly Button _newSession;
        private readonly Button _settings;
        private readonly Button _reconnect;

        public VisualElement Root
        {
            get { return _root; }
        }

        public HeaderView()
        {
            _root = new VisualElement();
            _root.style.flexDirection = FlexDirection.Row;
            _root.style.alignItems = Align.Center;
            _root.style.flexGrow = 1f;

            _spark = new Label(IconLoader.CurrentAgentGlyph);
            _spark.AddToClassList("uap-header-spark");
            _root.Add(_spark);

            _title = new Label(L10n.S.HeaderDefaultTitle);
            _title.AddToClassList("uap-header-title");
            _title.enableRichText = false;
            _root.Add(_title);

            _modelPicker = new Button(OnModelPickerClicked) { text = L10n.S.StatusModelUnknown };
            _modelPicker.AddToClassList("uap-header-model-btn");
            _modelPicker.tooltip = L10n.S.HeaderModelPickerTooltip;
            _root.Add(_modelPicker);

            // Auto-approve level lives in the HEADER, not only in Settings,
            // because of what it is for: the moment a user wants it is the
            // moment a permission card is blocking a long task, and making
            // them open Settings to find it is the same interruption they
            // are trying to escape. Keeping it permanently visible also
            // means how much the agent is allowed to do unattended is never
            // hidden state -- it is on screen next to the model.
            _autoApprove = new Button(OnAutoApproveClicked);
            _autoApprove.AddToClassList("uap-header-autoapprove-btn");
            _root.Add(_autoApprove);
            RefreshAutoApprove();

            // 2026-09-06 review fix 2: on a narrow dock (root `uap-narrow`)
            // USS hides the two chips above and shows this one instead. It
            // wears the model name (so the model stays visible where the
            // status bar has already folded it away) and its menu hands
            // off to the same model / auto-approve menus the two chips
            // open. Hidden by USS otherwise; nothing here is width-aware.
            _compact = new Button(OnCompactClicked) { text = L10n.S.StatusModelUnknown };
            _compact.AddToClassList("uap-header-compact-btn");
            _compact.tooltip = L10n.S.HeaderCompactTooltip;
            _root.Add(_compact);

            // Plain text (not MakeIconButton): no built-in Unity icon name
            // for "session history" has been verified against a live
            // editor in this session (unlike d_Toolbar Plus / d__Popup /
            // d_Refresh below, all named explicitly in R05 section 2.2/5.5)
            // -- a text label sidesteps the guess entirely rather than
            // risking a semantically-wrong icon silently resolving.
            _historyButton = new Button(OnHistoryClicked) { text = L10n.S.HeaderHistoryButton };
            _historyButton.AddToClassList("uap-header-history-btn");
            _historyButton.tooltip = L10n.S.HeaderHistoryTooltip;
            _root.Add(_historyButton);

            _newSession = MakeIconButton("d_Toolbar Plus", "+", L10n.S.HeaderNewSessionTooltip,
                OnNewSessionClicked);
            _root.Add(_newSession);

            _settings = MakeIconButton("d__Popup", IconLoader.GlyphGear,
                L10n.S.HeaderSettingsTooltip, OnSettingsClicked);
            _root.Add(_settings);

            _reconnect = MakeIconButton("d_Refresh", IconLoader.GlyphDownArrow,
                L10n.A(L10n.S.HeaderReconnectTooltip), OnReconnectClicked);
            _reconnect.AddToClassList("uap-header-btn--warn");
            _reconnect.style.display = DisplayStyle.None;
            _root.Add(_reconnect);
        }

        /// <summary>Re-reads AgentHub state and repaints the header.</summary>
        public void Refresh()
        {
            // The agent mark and name follow the selected backend.
            string glyph = IconLoader.CurrentAgentGlyph;
            if (_spark.text != glyph)
            {
                _spark.text = glyph;
            }
            string reconnectTooltip = L10n.A(L10n.S.HeaderReconnectTooltip);
            if (_reconnect.tooltip != reconnectTooltip)
            {
                _reconnect.tooltip = reconnectTooltip;
            }
            ChatSession session = AgentHub.Session;
            // session.title is auto-derived from the FIRST USER message
            // (not model output), but it is still free-form text that can
            // contain emoji the editor fonts have no glyph for -- sanitize
            // the same way every other render chokepoint does (see
            // IconLoader.SanitizeForDisplay's doc comment for the dropped
            // ranges and rationale).
            string title = session != null && !string.IsNullOrEmpty(session.title)
                ? IconLoader.SanitizeForDisplay(session.title)
                : L10n.S.HeaderDefaultTitle;
            if (_title.text != title)
            {
                _title.text = title;
            }

            AgentClient client = AgentHub.Client;
            bool busy = client != null && client.TurnActive;
            _newSession.SetEnabled(!busy);

            RefreshModelPicker(client);
            RefreshAutoApprove();

            bool errored = client != null && client.State == AgentClientState.Errored;
            _reconnect.style.display = errored ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // -- Auto-approve level ----------------------------------------------------

        /// <summary>
        /// Repaints the header's auto-approve control from the stored level.
        /// Also called from Refresh(), so a change made in the Settings view
        /// shows up here without the two surfaces having to know about each
        /// other.
        ///
        /// The button carries a class per level rather than one generic
        /// class: at the top level the agent is allowed to run Unity
        /// operations that Undo cannot take back, and that deserves to look
        /// different from "ask me every time" at a glance, not just read
        /// differently.
        /// </summary>
        private void RefreshAutoApprove()
        {
            UapAutoApproveLevel level = PanelStateStore.instance.Settings.autoApproveLevel;
            // Short form on the chip, full name in the tooltip: the header
            // is a ~300 px row shared with the title and four other
            // controls, and the full names ellipsize down to noise there
            // (measured: squeezed to the 46 px min-width).
            string shortLabel = AutoApproveLevelLabels.DescribeShort(level);
            if (_autoApprove.text != shortLabel)
            {
                _autoApprove.text = shortLabel;
            }
            _autoApprove.tooltip = L10n.F(L10n.S.HeaderAutoApproveTooltipFmt,
                AutoApproveLevelLabels.Describe(level));
            _autoApprove.EnableInClassList("uap-header-autoapprove-btn--relaxed",
                level == UapAutoApproveLevel.Undoable);
            _autoApprove.EnableInClassList("uap-header-autoapprove-btn--open",
                level == UapAutoApproveLevel.AllUnityOps || level == UapAutoApproveLevel.AllTools);
        }

        private void OnAutoApproveClicked()
        {
            UapAutoApproveLevel current = PanelStateStore.instance.Settings.autoApproveLevel;
            var menu = new GenericMenu();
            menu.AddDisabledItem(new GUIContent(L10n.S.AutoApproveMenuTitle));
            menu.AddSeparator(string.Empty);
            for (int i = 0; i < AutoApproveLevelLabels.Ordered.Length; i++)
            {
                UapAutoApproveLevel level = AutoApproveLevelLabels.Ordered[i];
                // UXA-3: set the dangerous top level visually apart from the
                // three ask/undo-safe ones (the chip already tiers via its
                // --open class; the menu had no tiering at all). GenericMenu
                // labels cannot carry styling, so the separator is the
                // strongest cue available here; the real friction is the
                // confirmation dialog inside ApplyAutoApproveLevel.
                if (level == UapAutoApproveLevel.AllUnityOps)
                {
                    menu.AddSeparator(string.Empty);
                }
                menu.AddItem(new GUIContent(AutoApproveLevelLabels.Describe(level)),
                    level == current, delegate { ApplyAutoApproveLevel(level); });
            }
            menu.ShowAsContext();
        }

        private void ApplyAutoApproveLevel(UapAutoApproveLevel level)
        {
            // Fully qualified: UnityEngine.UIElements.PanelSettings (the UI
            // Toolkit render-settings asset) is in scope here and collides
            // with this package's own Model.PanelSettings.
            Colloid.AgentPanel.Model.PanelSettings settings = PanelStateStore.instance.Settings;
            if (settings.autoApproveLevel == level)
            {
                return;
            }
            // UXA-3: the confirmation must run BEFORE the assignment --
            // ApplyAutoApproveLevelChanged below immediately auto-answers a
            // permission request already waiting on screen, and an approved
            // request cannot be un-answered.
            if (!AutoApproveLevelLabels.ConfirmEscalationIfNeeded(
                    settings.autoApproveLevel, level))
            {
                return;
            }
            settings.autoApproveLevel = level;
            PanelStateStore.instance.SaveNow();
            // Deliberately NOT RequestAutoApplyReconnect: this policy is
            // evaluated per permission request, so it needs no respawn --
            // and a reconnect would kill the very turn the user is trying to
            // unblock. AgentHub re-checks any request already waiting, so
            // raising the level clears the card that prompted the change
            // instead of only helping from the next one onward.
            AgentHub.ApplyAutoApproveLevelChanged();
            RefreshAutoApprove();
        }

        // -- Model picker ---------------------------------------------------------

        private void RefreshModelPicker(AgentClient client)
        {
            string label = StatusBarView.ResolveModelName(client, AgentHub.PendingSessionModel);
            if (_modelPicker.text != label)
            {
                _modelPicker.text = label;
            }
            if (_compact.text != label)
            {
                _compact.text = label;
            }
            bool hasOptions = GetModels(client).Count > 0;
            bool streaming = client != null && client.State == AgentClientState.Streaming;
            bool hasCachedCatalog = PanelStateStore.instance.Settings.modelCatalog.Count > 0;
            _modelPicker.SetEnabled(
                ResolveModelPickerState(hasOptions, hasCachedCatalog, streaming).Enabled);
        }

        // UICODE-10: memoized by the InitializeResponse REFERENCE. Refresh
        // runs on every AgentHub.Changed (per streaming delta at the
        // hottest), and re-running ParseModels allocated a fresh
        // List<ModelOption> + options each time just to answer Count>0 --
        // steady GC pressure for a value that is immutable per initialize
        // response. Reference identity is an exact key: the models node
        // never changes within one response object, and a reconnect
        // replaces the whole response instance.
        private ControlResponseMessage _cachedModelsResponse;
        private List<ModelOption> _cachedModels = new List<ModelOption>();

        internal List<ModelOption> GetModels(AgentClient client)
        {
            ControlResponseMessage response = client != null ? client.InitializeResponse : null;
            if (response == null)
            {
                _cachedModelsResponse = null;
                if (_cachedModels.Count > 0)
                {
                    _cachedModels = new List<ModelOption>();
                }
                return _cachedModels;
            }
            if (!ReferenceEquals(response, _cachedModelsResponse))
            {
                _cachedModelsResponse = response;
                _cachedModels = ParseModels(response.Response["models"]);
            }
            return _cachedModels;
        }

        /// <summary>Where the model menu's entries come from (UXO-5).</summary>
        internal enum ModelPickerSource
        {
            /// <summary>Live initialize-response options; selection switches the running session.</summary>
            Live,
            /// <summary>Persisted PanelSettings.modelCatalog; selection writes the panel default (next-spawn-only).</summary>
            Cache,
            /// <summary>No live options and no cached catalog; the menu explains instead of doing nothing.</summary>
            Empty
        }

        internal struct ModelPickerState
        {
            public bool Enabled;
            public ModelPickerSource Source;
        }

        /// <summary>
        /// UXO-5 (SR section 4.2): the header model picker used to be a
        /// dead button until the first initialize response arrived -- the
        /// panel's most-asked "why can't I pick a model?" moment. Pure
        /// decision: live options win (disabled only mid-stream, where a
        /// set_model would race the turn); otherwise the persisted catalog
        /// cache backs a default-model menu; otherwise the button stays
        /// ENABLED and the click explains ("choose after connecting")
        /// rather than swallowing the click.
        /// </summary>
        internal static ModelPickerState ResolveModelPickerState(
            bool hasLiveOptions, bool hasCachedCatalog, bool streaming)
        {
            var state = new ModelPickerState();
            if (hasLiveOptions)
            {
                state.Source = ModelPickerSource.Live;
                state.Enabled = !streaming;
                return state;
            }
            state.Source = hasCachedCatalog ? ModelPickerSource.Cache : ModelPickerSource.Empty;
            state.Enabled = true;
            return state;
        }

        private void OnModelPickerClicked()
        {
            ShowModelMenu(_modelPicker);
        }

        /// <summary>
        /// The narrow-dock chip's menu: one entry per folded chip, each
        /// opening the very menu that chip opens when it is visible.
        /// </summary>
        private void OnCompactClicked()
        {
            UapAutoApproveLevel level = PanelStateStore.instance.Settings.autoApproveLevel;
            var menu = new GenericMenu();
            // The wide-layout picker is DISABLED mid-stream (a set_model would
            // race the turn -- ResolveModelPickerState). ShowModelMenu re-checks
            // that and returns silently, so the race is already impossible
            // from here; what the compact chip owed the user was the same
            // visible "not now": a disabled entry instead of a dead click.
            AgentClient client = AgentHub.Client;
            bool streaming = client != null && client.State == AgentClientState.Streaming;
            bool modelEnabled = ResolveModelPickerState(GetModels(client).Count > 0,
                PanelStateStore.instance.Settings.modelCatalog.Count > 0, streaming).Enabled;
            var modelItem = new GUIContent(L10n.F(L10n.S.HeaderCompactModelItemFmt,
                _compact.text.Replace('/', '-')));
            if (modelEnabled)
            {
                menu.AddItem(modelItem, false, delegate { ShowModelMenu(_compact); });
            }
            else
            {
                menu.AddDisabledItem(modelItem);
            }
            menu.AddItem(new GUIContent(L10n.F(L10n.S.HeaderCompactAutoApproveItemFmt,
                AutoApproveLevelLabels.Describe(level).Replace('/', '-'))), false,
                OnAutoApproveClicked);
            menu.DropDown(_compact.worldBound);
        }

        private void ShowModelMenu(VisualElement anchor)
        {
            AgentClient client = AgentHub.Client;
            List<ModelOption> options = GetModels(client);
            bool streaming = client != null && client.State == AgentClientState.Streaming;
            List<ModelCatalogEntry> catalog = PanelStateStore.instance.Settings.modelCatalog;
            ModelPickerState state = ResolveModelPickerState(
                options.Count > 0, catalog.Count > 0, streaming);
            if (!state.Enabled)
            {
                return;
            }
            if (state.Source == ModelPickerSource.Empty)
            {
                var emptyMenu = new GenericMenu();
                emptyMenu.AddDisabledItem(
                    new GUIContent(L10n.S.HeaderModelPickerConnectFirst));
                emptyMenu.DropDown(anchor.worldBound);
                return;
            }
            if (state.Source == ModelPickerSource.Cache)
            {
                ShowCachedCatalogMenu(catalog, anchor);
                return;
            }
            string currentResolved = client != null ? client.CurrentModel : null;
            string currentSettingsValue = PanelStateStore.instance.Settings.model;

            var menu = new GenericMenu();
            for (int i = 0; i < options.Count; i++)
            {
                ModelOption option = options[i];
                bool isCurrent = !string.IsNullOrEmpty(currentResolved)
                    ? string.Equals(option.ResolvedModel, currentResolved, StringComparison.Ordinal)
                    : string.Equals(option.Value, currentSettingsValue, StringComparison.Ordinal);
                string label = string.IsNullOrEmpty(option.Description)
                    ? option.DisplayName
                    : L10n.F(L10n.S.HeaderModelOptionWithDescriptionFmt,
                        option.DisplayName, option.Description);
                // v0.11.0 (docs/design-notes/2026-08-02-subagent-model-
                // precedence.md section 3.1): "which one is my persisted
                // default" and "which one is running now" (isCurrent,
                // above) are independent facts -- a session switched via
                // this very picker can be running a model that is NOT the
                // panel default, and vice versa before any session exists.
                label = ApplyPanelDefaultSuffix(label, option.Value, currentSettingsValue);
                // GenericMenu treats '/' as a submenu separator; model
                // descriptions from the CLI never contain one in the
                // captured fixture, but guard anyway since this text is
                // effectively server-controlled.
                label = label.Replace('/', '-');
                menu.AddItem(new GUIContent(label), isCurrent, delegate
                {
                    AgentHub.SwitchSessionModel(option.Value);
                });
            }
            menu.DropDown(anchor.worldBound);
        }

        /// <summary>
        /// UXO-5: the offline model menu, built from the persisted catalog
        /// cache (PanelSettings.modelCatalog, refreshed on every live
        /// connection). No session exists to switch, so the checkmark marks
        /// the persisted panel default and a selection goes through
        /// AgentHub.SetDefaultModel -- the same next-spawn-only write the
        /// Settings dropdown uses -- NEVER SwitchSessionModel (which would
        /// silently no-op here). The default suffix is skipped: with no
        /// running model, "default" and "current" collapse into one fact
        /// and the checkmark already states it.
        /// </summary>
        private void ShowCachedCatalogMenu(List<ModelCatalogEntry> catalog, VisualElement anchor)
        {
            // 2026-09-17 note: this menu also serves the seconds between a
            // spawn ("+", reconnect) and the initialize handshake, when the
            // live options are not known yet. The user pressed the SESSION
            // picker, so with a live client the pick goes to the session
            // (held until the handshake answers), not to the next-spawn
            // default -- which used to be silently written instead, and the
            // running session kept its old model.
            AgentClient client = AgentHub.Client;
            bool targetsSession = ResolveCachedMenuTargetsSession(
                client != null, client != null ? client.State : AgentClientState.NotStarted);
            string currentSettingsValue = targetsSession && !string.IsNullOrEmpty(AgentHub.PendingSessionModel)
                ? AgentHub.PendingSessionModel
                : PanelStateStore.instance.Settings.model;
            var menu = new GenericMenu();
            for (int i = 0; i < catalog.Count; i++)
            {
                ModelCatalogEntry entry = catalog[i];
                if (entry == null || string.IsNullOrEmpty(entry.value))
                {
                    continue;
                }
                string display = string.IsNullOrEmpty(entry.displayName)
                    ? entry.value
                    : entry.displayName;
                string label = string.IsNullOrEmpty(entry.description)
                    ? display
                    : L10n.F(L10n.S.HeaderModelOptionWithDescriptionFmt,
                        display, entry.description);
                label = label.Replace('/', '-');
                bool isCurrent = string.Equals(
                    entry.value, currentSettingsValue, StringComparison.Ordinal);
                string value = entry.value;
                menu.AddItem(new GUIContent(label), isCurrent, delegate
                {
                    if (targetsSession)
                    {
                        AgentHub.SwitchSessionModel(value);
                    }
                    else
                    {
                        AgentHub.SetDefaultModel(value);
                    }
                });
            }
            menu.DropDown(anchor.worldBound);
        }

        /// <summary>
        /// Pure decision for the cached-catalog menu: a pick targets the
        /// running SESSION whenever a client exists that can still accept
        /// a live switch (spawned and not dead: every state but NotStarted
        /// and Errored, the same two SwitchSessionModel bails on); with no
        /// such client there is no session to switch and the pick sets the
        /// next-spawn default, as before.
        /// </summary>
        internal static bool ResolveCachedMenuTargetsSession(bool hasClient, AgentClientState state)
        {
            return hasClient
                && state != AgentClientState.NotStarted
                && state != AgentClientState.Errored;
        }

        /// <summary>
        /// Pure label-suffix decision for the model menu (v0.11.0, docs/
        /// design-notes/2026-08-02-subagent-model-precedence.md section
        /// 3.1, work item C): appends the localized " (default)" suffix
        /// when <paramref name="optionValue"/> equals the persisted panel
        /// default (<see cref="Model.PanelSettings.model"/>), independent
        /// of whether that option is also the CURRENTLY RUNNING session's
        /// model (isCurrent, decided separately in OnModelPickerClicked) --
        /// the two are different facts and this method only ever decides
        /// the former. An empty <paramref name="panelDefaultModelValue"/>
        /// (no explicit default persisted -- the CLI's own default applies)
        /// never matches any real catalog value, so no option gets the
        /// suffix in that case.
        /// </summary>
        internal static string ApplyPanelDefaultSuffix(string label, string optionValue,
            string panelDefaultModelValue)
        {
            if (!string.IsNullOrEmpty(panelDefaultModelValue)
                && string.Equals(optionValue, panelDefaultModelValue, StringComparison.Ordinal))
            {
                return L10n.F(L10n.S.HeaderModelOptionDefaultSuffixFmt, label);
            }
            return label;
        }

        /// <summary>One entry from the initialize response's models[] array.</summary>
        public sealed class ModelOption
        {
            /// <summary>The --model flag value / set_model argument (e.g. "sonnet").</summary>
            public string Value;
            /// <summary>The resolved model name (e.g. "claude-sonnet-5"), matches SystemInitMessage.Model.</summary>
            public string ResolvedModel;
            public string DisplayName;
            public string Description;
        }

        /// <summary>
        /// Pure parse of ControlResponseMessage.Response["models"] (the
        /// initialize handshake's model list, R02 section 5.1; shape
        /// verified against Tests/Editor/Fixtures/out_bidi.jsonl). Entries
        /// missing the required "value" field are skipped; every other
        /// field is optional-first (D8), matching the rest of the protocol
        /// layer's tolerance policy even though this parser lives in UI
        /// (the models array is UI-display-only, not a wire message type
        /// Core/Protocol needs to own).
        /// </summary>
        public static List<ModelOption> ParseModels(JsonNode modelsNode)
        {
            var result = new List<ModelOption>();
            if (modelsNode == null || !modelsNode.IsArray)
            {
                return result;
            }
            foreach (JsonNode item in modelsNode.Items)
            {
                if (!item.IsObject)
                {
                    continue;
                }
                string value = item["value"].AsString(string.Empty);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                result.Add(new ModelOption
                {
                    Value = value,
                    ResolvedModel = item["resolvedModel"].AsString(string.Empty),
                    DisplayName = item["displayName"].AsString(value),
                    Description = item["description"].AsString(string.Empty)
                });
            }
            return result;
        }

        // -- Navigation -------------------------------------------------------------

        private static void OnNewSessionClicked()
        {
            AgentHub.StartFresh();
            AgentPanelWindow.ShowChat();
        }

        private static void OnHistoryClicked()
        {
            AgentPanelWindow.ShowHistory();
        }

        private static void OnSettingsClicked()
        {
            AgentPanelWindow.ShowSettings();
        }

        private static void OnReconnectClicked()
        {
            // Same entry point as ResumeBanner's Errored-state Reconnect
            // button: restarts from Errored and resumes the cached session
            // id; AgentHub.Changed refreshes both surfaces.
            AgentHub.EnsureStarted();
        }

        private static Button MakeIconButton(string iconName, string fallbackText,
            string tooltip, System.Action onClick)
        {
            var button = onClick != null ? new Button(onClick) : new Button();
            button.AddToClassList("uap-header-btn");
            button.tooltip = tooltip;
            UnityEngine.Texture2D texture = IconLoader.Find(iconName);
            if (texture != null)
            {
                button.text = string.Empty;
                button.Add(new Image
                {
                    image = texture,
                    scaleMode = UnityEngine.ScaleMode.ScaleToFit
                });
            }
            else
            {
                button.text = fallbackText;
            }
            return button;
        }
    }
}
