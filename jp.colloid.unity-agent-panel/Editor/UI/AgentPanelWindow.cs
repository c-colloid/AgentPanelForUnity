using System;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The dockable Agent Panel window (ARCHITECTURE.md D6). Owns the
    /// three-region root (Header / ViewContainer / StatusBar) loaded from
    /// AgentPanel.uxml, applies the uap-theme-dark/light class from
    /// EditorGUIUtility.isProSkin, hosts the ChatView through
    /// IAgentPanelView, and drives header/status/title refreshes from
    /// AgentHub.Changed on a coalescing schedule. The titleContent doubles
    /// as the background-attention badge (warn icon while a permission
    /// request waits; R05 section 3.7).
    /// </summary>
    public sealed class AgentPanelWindow : EditorWindow
    {
        private const string UiFolder = "Packages/jp.colloid.unity-agent-panel/Editor/UI/Uss/";
        private const long RefreshIntervalMillis = 80;

        private static string WindowTitle
        {
            get { return L10n.S.HubWindowTitle; }
        }

        private ChatView _chatView;
        private VisualElement _chatRoot;
        private SettingsView _settingsView;
        private VisualElement _settingsRoot;
        private HistoryView _historyView;
        private VisualElement _historyRoot;
        private HeaderView _header;
        private StatusBarView _statusBar;
        private IVisualElementScheduledItem _refreshLoop;
        private bool _dirty;
        private bool _built;
        private bool _badgeShown;

        /// <summary>
        /// Which ViewContainer view is currently visible. Chat by default;
        /// switching never restarts the CLI client or rebuilds the
        /// transcript -- see
        /// docs/design-notes/2026-07-31-view-container-switching.md.
        /// </summary>
        private PanelViewKind _activeView = PanelViewKind.Chat;

        /// <summary>
        /// A view switch requested while this window's CreateGUI has not
        /// finished building yet (Open() returning a just-created
        /// GetWindow&lt;T&gt; instance does not guarantee CreateGUI already ran --
        /// see docs/design-notes/2026-07-31-show-settings-timing.md). Applied
        /// once CreateGUI reaches _built = true instead of being silently
        /// dropped (the reported "ShowSettings() did nothing" defect).
        /// </summary>
        private PanelViewKind? _pendingView;

        /// <summary>
        /// A SettingsView.Show(tab, cardId) requested (ShowSettings(tab,
        /// cardId) / ShowSettingsAccount) before this window's CreateGUI
        /// has finished building yet --
        /// same rationale/timing as <see cref="_pendingView"/>. Applied
        /// once CreateGUI reaches _built = true, right after _pendingView
        /// itself is applied.
        /// </summary>
        private SettingsTab? _pendingSettingsTab;
        private string _pendingSettingsCard;

        /// <summary>
        /// Back-to-chat affordance living in the ViewContainer region
        /// (shown whenever Settings or History is active). Originally paired
        /// with an interim gear button before HeaderView grew its own
        /// Settings/History navigation (this stage); trimmed down to just
        /// the back button per the plan recorded in
        /// docs/design-notes/2026-07-31-view-container-switching.md.
        /// </summary>
        private VisualElement _viewStrip;
        private Button _backButton;

        /// <summary>Opens (or focuses) the panel.</summary>
        public static AgentPanelWindow Open()
        {
            var window = GetWindow<AgentPanelWindow>(WindowTitle);
            window.Show();
            return window;
        }

        /// <summary>
        /// Subscribes once (per domain load) to L10n.LanguageChanged so a
        /// real language switch (SettingsView's dropdown) rebuilds every
        /// open panel window without the caller needing to know this window
        /// type exists. A static constructor runs lazily on first touch of
        /// this type -- guaranteed before SettingsView could ever raise the
        /// event, since raising it requires PanelStateStore/L10n plumbing
        /// that only a live panel (i.e. this type) exercises.
        /// </summary>
        static AgentPanelWindow()
        {
            L10n.LanguageChanged += OnLanguageChangedGlobal;
        }

        private static void OnLanguageChangedGlobal()
        {
            // Deferred: this can fire synchronously from inside a
            // ChangeEvent callback on a control that lives in the very
            // VisualElement tree RebuildAllOpenPanels is about to tear down
            // and rebuild (SettingsView's language dropdown) -- mutating
            // that tree from inside its own event dispatch is the same
            // class of hazard StartHubDeferred already sidesteps below for
            // CreateGUI's own AgentHub.EnsureStarted() call.
            EditorApplication.delayCall += RebuildAllOpenPanels;
        }

        /// <summary>
        /// Rebuilds every currently open AgentPanelWindow/PermissionWindow
        /// from scratch via their own (re-entry-safe) CreateGUI -- the
        /// "find and reuse the existing full-rebuild path" mechanism a
        /// language switch needs so it is visible immediately without
        /// requiring the user to close and reopen the panel. CreateGUI
        /// preserves _activeView across a re-entrant rebuild (see
        /// AgentPanelWindowViewStateTests), so this does not disturb which
        /// view (Chat/Settings/History) is currently on screen.
        /// </summary>
        internal static void RebuildAllOpenPanels()
        {
            AgentPanelWindow[] panels = Resources.FindObjectsOfTypeAll<AgentPanelWindow>();
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null)
                {
                    panels[i].CreateGUI();
                }
            }
            PermissionWindow[] permWindows = Resources.FindObjectsOfTypeAll<PermissionWindow>();
            for (int i = 0; i < permWindows.Length; i++)
            {
                if (permWindows[i] != null)
                {
                    permWindows[i].CreateGUI();
                }
            }
        }

        // -- Unity lifecycle ---------------------------------------------------

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            minSize = new Vector2(300f, 200f);
        }

        public void CreateGUI()
        {
            // Boot-time language application: PanelSettings.language is
            // persisted, but only the Settings dropdown used to call
            // ApplyFromSettings -- so every domain reload / editor start
            // rendered English until the user visited Settings
            // (live-diagnosed 2026-08-01, setting=Auto effective=English).
            // Applying here, before any label is constructed, makes THIS
            // build use the right catalog. LanguageChanged only fires when
            // the effective language actually changes (the first build
            // after a reload), costing at most one extra deferred rebuild.
            L10n.ApplyFromSettings(
                Colloid.AgentPanel.Model.PanelStateStore.instance.Settings.language);

            VisualElement root = rootVisualElement;

            // Re-entry guard: Unity can invoke CreateGUI a second time on
            // the SAME window instance without an intervening OnDisable/
            // OnEnable (observed defect). Without this, the previous
            // ChatView/SettingsView/HistoryView instances and their event
            // subscriptions (AgentHub.Changed, EditorUpdatePump.RepaintRequested,
            // the scheduled refresh loop) would leak permanently and a
            // second CloneTree would append onto an already-populated root.
            // Tear down exactly like OnDisable does, then fall through to
            // the normal (re)build below.
            if (_built)
            {
                TeardownLiveState();
                // _activeView is deliberately NOT reset here: the build
                // below applies visibility FROM _activeView, so the user's
                // current view survives any rebuild (teardown path or the
                // OnDisable/OnEnable path where this branch is skipped
                // entirely because _built is already false).
            }
            // Unconditional, not just BuildSkeleton's not-found fallback:
            // a re-entrant CreateGUI must never let a second CloneTree (or
            // fallback build) append onto whatever the first pass already
            // populated this root with.
            root.Clear();
            // NOT root.styleSheets.Clear(): that wholesale-clears every
            // stylesheet on this root, including Unity's OWN implicitly
            // attached editor default stylesheet (DefaultCommonDark/
            // Light_inter.uss) -- which this window never added and does
            // not own. That default sheet is the only source of
            // `.unity-scroll-view__content-container { flex-shrink: 0; }`;
            // without it every ScrollView's content-container falls back to
            // UI Toolkit's initial flex-shrink:1, and Yoga has no
            // min-content floor, so content taller/wider than the viewport
            // gets flex-CRUSHED to viewport size instead of overflowing/
            // scrolling -- text still paints at full glyph size against a
            // ~1-4px box, producing the Settings/History/markdown-table
            // overlap-garbage defect. Remove only the 4 sheets THIS window
            // adds (by identity, re-resolved from the same paths
            // LoadStyleSheets uses) so a re-entrant CreateGUI still tears
            // its own sheets down cleanly without touching anything it does
            // not own. See
            // docs/design-notes/2026-07-31-settings-layout-freeze-investigation.md
            // and docs/design-notes/2026-07-31-scroll-container-shrink-fix.md.
            RemovePackageStyleSheets(root);

            ApplyThemeAndStyles(root);
            // Narrow-width mode (docs/design-notes/2026-09-05-ui-redesign.md
            // section 2.5): the root measures itself and toggles
            // `uap-narrow` -- USS has no container queries, so this is the
            // same root-class mechanism the theme/font-scale classes use.
            // Registering the same method group twice is a no-op in UI
            // Toolkit, and TeardownLiveState unregisters it anyway.
            root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            ApplyWidthClass(root, root.resolvedStyle.width);

            VisualElement header;
            VisualElement viewContainer;
            VisualElement statusBar;
            BuildSkeleton(root, out header, out viewContainer, out statusBar);

            _header = new HeaderView();
            header.Add(_header.Root);

            _statusBar = new StatusBarView();
            statusBar.Add(_statusBar.Root);

            BuildViewSwitchStrip(viewContainer);

            _chatView = new ChatView();
            _chatRoot = _chatView.CreateGUI();
            viewContainer.Add(_chatRoot);
            // Font-scale anchor: the chat root only -- NOT the window root
            // -- so the slider never reflows Settings/History or the window
            // chrome (see ApplyFontScale's doc comment and
            // docs/design-notes/2026-08-01-fontscale-scope.md).
            ApplyFontScale(_chatRoot);

            _settingsView = new SettingsView();
            _settingsRoot = _settingsView.CreateGUI();
            viewContainer.Add(_settingsRoot);

            _historyView = new HistoryView();
            _historyRoot = _historyView.CreateGUI();
            viewContainer.Add(_historyRoot);

            // Build-time visibility must honor _activeView: it can be
            // non-Chat when CreateGUI runs re-entrantly (or in any flow
            // where a switch landed before this rebuild). Hardcoding
            // chat-visible here desyncs the displays from _activeView, and
            // SetActiveView's same-view early return then locks the panel
            // out of every later switch to that view (live-diagnosed
            // 2026-08-01: strip showed "< Chat" while the chat view stayed
            // on screen and ShowSettings() no-oped forever).
            SetViewVisible(PanelViewKind.Chat, _activeView == PanelViewKind.Chat);
            SetViewVisible(PanelViewKind.Settings, _activeView == PanelViewKind.Settings);
            SetViewVisible(PanelViewKind.History, _activeView == PanelViewKind.History);
            ActivateView(_activeView);

            UpdateViewSwitchStrip();

            AgentHub.Changed += OnHubChanged;
            EditorUpdatePump.RepaintRequested += Repaint;
            _refreshLoop = root.schedule.Execute(RefreshIfDirty).Every(RefreshIntervalMillis);
            _dirty = true;
            _built = true;
            RefreshIfDirty();

            // Apply a view switch that was requested before this build
            // finished (ShowSettings/ShowChat/ShowHistory called against a
            // window whose CreateGUI had not run yet) now that _built is
            // true and SetActiveView can act on it. Must run AFTER _built
            // is set above -- SetActiveView no-ops while !_built.
            if (_pendingView.HasValue)
            {
                PanelViewKind pending = _pendingView.Value;
                _pendingView = null;
                SetActiveView(pending);
            }

            // Same deferred-application rationale as _pendingView above,
            // for a ShowSettingsAccount() scroll requested before this
            // build finished. Must run AFTER _pendingView is applied so the
            // Settings root is already visible when the scroll executes.
            if (_pendingSettingsTab.HasValue)
            {
                SettingsTab tab = _pendingSettingsTab.Value;
                string cardId = _pendingSettingsCard;
                _pendingSettingsTab = null;
                _pendingSettingsCard = null;
                if (_settingsView != null)
                {
                    _settingsView.Show(tab, cardId);
                }
            }

            // Every text element this build made, the first refresh's rows
            // included: paths and code keep their backslashes (TextEscapes
            // doc comment). Subtrees grown later sweep themselves.
            TextEscapes.Disable(root);

            // Spawn/reconnect outside of GUI construction so a slow probe
            // never blocks the window from appearing.
            EditorApplication.delayCall += StartHubDeferred;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= StartHubDeferred;
            if (!_built)
            {
                return;
            }
            TeardownLiveState();
        }

        /// <summary>
        /// The one teardown for live window state, shared by OnDisable and
        /// the re-entrant CreateGUI guard. The re-entry path used to skip
        /// SerializeState, so a rebuild (language switch) dropped the live
        /// scroll offset and composer draft on the floor before the new
        /// ChatView restored from SessionStateBridge.
        /// </summary>
        private void TeardownLiveState()
        {
            _built = false;
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            AgentHub.Changed -= OnHubChanged;
            EditorUpdatePump.RepaintRequested -= Repaint;
            if (_refreshLoop != null)
            {
                _refreshLoop.Pause();
                _refreshLoop = null;
            }
            // ChatView holds the only state worth serializing (scroll
            // offset, composer draft) regardless of which view is on
            // screen when the window closes -- it was never rebuilt by a
            // view switch, so its live fields are always current.
            // SerializeState runs BEFORE DeactivateView, same as the
            // original OnDisable ordering.
            if (_chatView != null)
            {
                _chatView.SerializeState();
            }
            // Only the CURRENTLY ACTIVE view still has live subscriptions:
            // SetActiveView already called OnDeactivate on whichever view
            // was switched away from, so calling it twice here would be a
            // harmless but pointless no-op for that one -- deactivate
            // whichever view is active now.
            DeactivateView(_activeView);
        }

        // -- Skeleton -----------------------------------------------------------

        /// <summary>
        /// Theme class (uap-theme-dark/light from isProSkin) + the three
        /// stylesheets + the Japanese UI font (when enabled). Shared with
        /// every other window that hosts panel UI (PermissionWindow) so
        /// tokens -- and now the body font -- resolve identically
        /// everywhere.
        /// </summary>
        internal static void ApplyThemeAndStyles(VisualElement root)
        {
            root.AddToClassList(EditorGUIUtility.isProSkin ? "uap-theme-dark" : "uap-theme-light");
            IconLoader.ApplyAgentAccent(root);
            LoadStyleSheets(root);
            ApplyCjkUiFont(root);
            // Font scale is deliberately NOT applied here: unlike the CJK
            // font and the theme/stylesheets (which are meant to affect the
            // WHOLE window, chrome included), the font-size slider must only
            // affect conversation content -- see ApplyFontScale's doc
            // comment and docs/design-notes/2026-08-01-fontscale-scope.md.
            // Callers apply it separately to whichever element is that
            // window's conversation-content root.
        }

        /// <summary>
        /// Patchy-bold/faint Japanese glyph fix (live feedback): when
        /// PanelSettings.preferCjkUiFont is on and
        /// FontLoader.JapaneseUiFontAsset (TextCore DynamicOS FontAsset)
        /// resolves on this machine, assign it at this window's
        /// CONTENT ROOT via an inherited unityFontDefinition rather than
        /// visiting every label individually -- -unity-font-definition is
        /// an inherited USS property, so one assignment here reaches every
        /// body-text element below it (chat message list, composer
        /// TextField, context bar, permission card, status bar, header
        /// title, first-run/empty views) without per-glyph OS fallback
        /// ever triggering for Japanese text.
        ///
        /// Mono text is NOT affected: MessageBlockFactory.ApplyMonoFont
        /// assigns RobotoMono as an INLINE style directly on the leaf
        /// label/TextField (CodeBlockElement, ToolActivityCard preview,
        /// attachment payload, permission preview). An inline style always
        /// wins over an inherited value in the USS cascade regardless of
        /// which assignment runs first or last, so mono code surfaces keep
        /// rendering in RobotoMono whether this method runs before or
        /// after ApplyMonoFont. Rich-text bold-tag emphasis on CJK is
        /// untouched by design (root cause class 2, out of scope here).
        ///
        /// Nothing is assigned when the setting is off or nothing resolved
        /// (FontLoader.ApplyJapaneseUi no-ops), so a machine without any
        /// matching OS font renders exactly as before this feature.
        ///
        /// Called every time this window's content root is (re)built --
        /// i.e. on open and reopen -- exactly like the uap-theme-dark/light
        /// class immediately above it, so a theme switch is picked up the
        /// same way the theme class already is in this codebase (there is
        /// no separate dynamic isProSkin-changed listener for either).
        /// </summary>
        private static void ApplyCjkUiFont(VisualElement root)
        {
            // Fully qualified: UnityEngine.UIElements.PanelSettings (the
            // UI Toolkit render-settings asset type) is in scope here via
            // the file's `using UnityEngine.UIElements;` and collides with
            // this package's own Colloid.AgentPanel.Model.PanelSettings.
            Colloid.AgentPanel.Model.PanelStateStore store =
                Colloid.AgentPanel.Model.PanelStateStore.instance;
            Colloid.AgentPanel.Model.PanelSettings settings =
                store != null ? store.Settings : null;
            // Settings can be null when the singleton has not finished its
            // first OnEnable yet (observed via external dynamic-code
            // access); treat that as "use the computed default".
            bool prefer = settings != null
                ? settings.preferCjkUiFont
                : Colloid.AgentPanel.Model.PanelSettings
                    .ComputeDefaultPreferCjkUiFont(Application.systemLanguage);
            if (!prefer)
            {
                return;
            }
            FontLoader.ApplyJapaneseUi(root);
        }

        // FontScale.uss still loads last (kept unchanged; harmless either
        // way now, see below). Historically this mattered because
        // `.uap-fontscale-N` and the active `uap-theme-dark`/`light` class
        // both redefined the same --uap-font-size-* custom properties on
        // the SAME element (the window root) at equal specificity, and USS
        // breaks that kind of tie by load order (docs/design-notes/
        // 2026-07-31-font-scale-cascade-fix.md: the prior !important-based
        // version was inert whenever a theme class was present, which is
        // always). Since docs/design-notes/2026-08-01-fontscale-scope.md,
        // `.uap-fontscale-N` is applied only to each window's conversation-
        // content root (AgentPanelWindow._chatRoot / PermissionWindow
        // .ContentRoot), never to the window root the theme class lives on
        // -- there is no longer a same-element tie to break there, so this
        // ordering is no longer load-bearing for that interaction, but it
        // is left as-is because there is no reason to change it and doing
        // so would be an unrelated, unverified risk.
        //
        // This is the COMPLETE set of stylesheets this window itself adds
        // to a root. CreateGUI's re-entry teardown (RemovePackageStyleSheets)
        // removes exactly these four, by identity, and nothing else -- see
        // that method's comment and
        // docs/design-notes/2026-07-31-scroll-container-shrink-fix.md for
        // why a wholesale root.styleSheets.Clear() is never used here.
        private static readonly string[] PackageStyleSheetFiles =
            { "AgentPanel.uss", "ThemeDark.uss", "ThemeLight.uss", "FontScale.uss" };

        private static void LoadStyleSheets(VisualElement root)
        {
            for (int i = 0; i < PackageStyleSheetFiles.Length; i++)
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UiFolder + PackageStyleSheetFiles[i]);
                if (sheet == null)
                {
                    Debug.LogWarning("[AgentPanel] Missing stylesheet: " + UiFolder + PackageStyleSheetFiles[i]);
                    continue;
                }
                // Idempotent: RemovePackageStyleSheets already strips a
                // prior instance of this exact asset on re-entry, but
                // guarding here too keeps this method safe to call on its
                // own (e.g. from a future caller that does not go through
                // CreateGUI's teardown) without ever producing a duplicate
                // entry in the set.
                if (!root.styleSheets.Contains(sheet))
                {
                    root.styleSheets.Add(sheet);
                }
            }
        }

        /// <summary>
        /// Removes exactly the 4 stylesheets THIS window adds via
        /// LoadStyleSheets -- by re-resolving the same asset paths, which
        /// Unity's AssetDatabase guarantees resolve to the SAME StyleSheet
        /// instance within a domain, so `styleSheets.Remove` matches by
        /// identity against whatever LoadStyleSheets added on a prior
        /// CreateGUI pass.
        ///
        /// Deliberately NOT `root.styleSheets.Clear()`: on an EditorWindow
        /// root, Clear() also removes Unity's own implicitly attached
        /// editor default stylesheet (DefaultCommonDark_inter.uss /
        /// DefaultCommonLight_inter.uss), which this window never added and
        /// must not touch. That default sheet is where
        /// `.unity-scroll-view__content-container { flex-shrink: 0; }`
        /// lives -- lose it and every ScrollView's content-container falls
        /// back to UI Toolkit's initial flex-shrink:1, so content taller/
        /// wider than the viewport gets flex-crushed to viewport size
        /// instead of scrolling (Yoga implements no min-content floor),
        /// producing the Settings/History/markdown-table overlap-garbage
        /// defect. See
        /// docs/design-notes/2026-07-31-settings-layout-freeze-investigation.md
        /// (root-cause proof) and
        /// docs/design-notes/2026-07-31-scroll-container-shrink-fix.md
        /// (this fix's options/decision).
        /// </summary>
        private static void RemovePackageStyleSheets(VisualElement root)
        {
            for (int i = 0; i < PackageStyleSheetFiles.Length; i++)
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UiFolder + PackageStyleSheetFiles[i]);
                if (sheet != null && root.styleSheets.Contains(sheet))
                {
                    root.styleSheets.Remove(sheet);
                }
            }
        }

        /// <summary>
        /// Instantiates AgentPanel.uxml and resolves the three named
        /// regions; falls back to code-built regions when the asset has
        /// not been imported yet (fresh package drop).
        /// </summary>
        private static void BuildSkeleton(VisualElement root, out VisualElement header,
            out VisualElement viewContainer, out VisualElement statusBar)
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiFolder + "AgentPanel.uxml");
            if (tree != null)
            {
                tree.CloneTree(root);
                header = root.Q<VisualElement>("uap-header");
                viewContainer = root.Q<VisualElement>("uap-view-container");
                statusBar = root.Q<VisualElement>("uap-status-bar");
                if (header != null && viewContainer != null && statusBar != null)
                {
                    return;
                }
                root.Clear();
            }
            else
            {
                Debug.LogWarning("[AgentPanel] Missing layout asset: "
                    + UiFolder + "AgentPanel.uxml (using code-built fallback).");
            }

            var fallbackRoot = new VisualElement();
            fallbackRoot.AddToClassList("uap-root");
            header = new VisualElement { name = "uap-header" };
            header.AddToClassList("uap-header");
            viewContainer = new VisualElement { name = "uap-view-container" };
            viewContainer.AddToClassList("uap-view-container");
            statusBar = new VisualElement { name = "uap-status-bar" };
            statusBar.AddToClassList("uap-status-bar");
            fallbackRoot.Add(header);
            fallbackRoot.Add(viewContainer);
            fallbackRoot.Add(statusBar);
            root.Add(fallbackRoot);
        }

        // -- Refresh -----------------------------------------------------------------

        private void OnHubChanged()
        {
            _dirty = true;
        }

        private void RefreshIfDirty()
        {
            if (!_dirty || !_built)
            {
                return;
            }
            _dirty = false;
            // The agent accent (spark colour) follows the selected backend.
            IconLoader.ApplyAgentAccent(rootVisualElement);
            _header.Refresh();
            _statusBar.Refresh();

            AgentClient client = AgentHub.Client;
            bool waiting = client != null
                && client.State == AgentClientState.WaitingPermission;
            // Edge-triggered on the SAME "just started waiting" transition
            // the badge itself detects below. UXA-7: this used to force
            // SetActiveView(Chat), interrupting whatever the user was
            // editing in Settings/History; the self-driving floating
            // PermissionWindow keeps the request reachable without the
            // yank (see PermissionArrivalPolicy). autoOpened: true keeps
            // the window's keyboard Y/N unarmed, the existing guard for a
            // window the user did not ask for.
            if (waiting && !_badgeShown
                && PermissionArrivalPolicy.Decide(_activeView, PermissionWindow.IsOpen)
                    == PermissionArrivalPolicy.Arrival.OpenFloatingWindow)
            {
                PermissionWindow.OpenForPending(true);
            }
            UpdateTitleBadge(waiting);
        }

        /// <summary>
        /// Attention badge hook: while a can_use_tool prompt is waiting the
        /// tab shows a warning icon so a hidden panel still signals the
        /// pending question (R05 section 3.7). Phase 2 extends this with
        /// the completed-turn (orange) state.
        /// </summary>
        private void UpdateTitleBadge(bool waiting)
        {
            if (waiting == _badgeShown)
            {
                return;
            }
            _badgeShown = waiting;
            if (waiting)
            {
                Texture2D icon = IconLoader.Find("d_console.warnicon.sml");
                titleContent = new GUIContent(WindowTitle, icon,
                    L10n.A(L10n.S.HubPermissionPendingTooltip));
            }
            else
            {
                titleContent = new GUIContent(WindowTitle);
            }
        }

        private static void StartHubDeferred()
        {
            AgentHub.EnsureStarted();
            // UXO-1: one-shot boot-time auth probe. Before this, nothing
            // queried `auth status` until the user opened Settings, so a
            // not-logged-in first run showed a fully usable composer and the
            // user's FIRST send bounced off authentication_failed --
            // ChatView.ResolveFirstRunMode could only react to that error
            // block after the fact. The query is cheap and safe here:
            // AgentHub self-guards overlapping spawns, runs it off-thread,
            // and raises Changed on completion, which ChatView already
            // coalesces into a refresh. Deliberately NOT inside
            // EnsureStarted: that runs on hot paths (CompileGate drain
            // ticks, every send with a dead client), and a boot probe
            // belongs to the one deferred boot call.
            AgentHub.RefreshAuthStatus();
        }

        // -- View switching (ARCHITECTURE.md Phase 3 / R05 section 6.1) --------------

        /// <summary>
        /// Public entry point for HeaderView's settings gear (and any other
        /// future caller). Focuses/opens the panel and switches to Settings.
        /// </summary>
        public static void ShowSettings()
        {
            RequestView(PanelViewKind.Settings);
        }

        /// <summary>
        /// Opens/focuses the panel, switches to Settings AND lands on the
        /// Agent card -- FirstRunView's chat-view "Log in" setup card uses
        /// this (docs/design-notes/2026-08-02-auth-in-panel.md section 2.2)
        /// instead of plain ShowSettings() so the just-started login
        /// sub-card (OAuth URL, code field) is not silently off-screen.
        /// </summary>
        public static void ShowSettingsAccount()
        {
            ShowSettings(SettingsTab.Connection, SettingsView.AgentCardId);
        }

        /// <summary>
        /// Opens Settings on one tab and, given a card id, opens and scrolls
        /// to that card (docs/design-notes/2026-09-17-settings-redesign-
        /// plan.md, D9). The plain ShowSettings() reopens whichever tab the
        /// user left, which SettingsView remembers per editor session.
        /// </summary>
        public static void ShowSettings(SettingsTab tab, string cardId = null)
        {
            AgentPanelWindow window = Open();
            window.RequestActiveView(PanelViewKind.Settings);
            window.RequestShowSettingsCard(tab, cardId);
        }

        /// <summary>
        /// Applies a SettingsView.Show(tab, cardId) immediately when this
        /// window has already finished building; otherwise queues it for
        /// CreateGUI to apply once the skeleton exists, mirroring
        /// <see cref="RequestActiveView"/>. Internal so EditMode tests can
        /// drive it without GetWindow/Show.
        /// </summary>
        /// <summary>
        /// The Settings view, for EditMode tests that drive it directly
        /// (a ScriptableObject window that is never shown has no panel, so
        /// a TextField's value change raises no ChangeEvent there).
        /// </summary>
        internal SettingsView SettingsViewForTests
        {
            get { return _settingsView; }
        }

        internal void RequestShowSettingsCard(SettingsTab tab, string cardId)
        {
            if (!_built)
            {
                _pendingSettingsTab = tab;
                _pendingSettingsCard = cardId;
                return;
            }
            if (_settingsView != null)
            {
                _settingsView.Show(tab, cardId);
            }
        }

        /// <summary>Switches the currently open panel back to Chat, if one is open.</summary>
        public static void ShowChat()
        {
            RequestView(PanelViewKind.Chat);
        }

        /// <summary>Opens (or focuses) the panel and switches to History.</summary>
        public static void ShowHistory()
        {
            RequestView(PanelViewKind.History);
        }

        /// <summary>
        /// Opens/focuses the panel and switches to the given view -- the
        /// shared body of ShowSettings/ShowChat/ShowHistory. Routed through
        /// RequestActiveView (not SetActiveView directly) because Open()'s
        /// GetWindow&lt;AgentPanelWindow&gt; call does not guarantee CreateGUI has
        /// already run on a just-created window instance (see
        /// docs/design-notes/2026-07-31-show-settings-timing.md); calling
        /// SetActiveView directly here would silently no-op in that case,
        /// which is the exact "ShowSettings() did nothing" defect this fixes.
        /// </summary>
        private static void RequestView(PanelViewKind kind)
        {
            AgentPanelWindow window = Open();
            window.RequestActiveView(kind);
        }

        /// <summary>
        /// Applies the view switch immediately when this window has already
        /// finished building; otherwise queues it for CreateGUI to apply
        /// once the skeleton (and _built) exist. Internal (not private) so
        /// EditMode tests can drive the exact same path ShowSettings/
        /// ShowChat/ShowHistory use without going through GetWindow/Show,
        /// which this codebase's tests deliberately avoid (window-manager
        /// side effects) -- see AgentPanelWindowTests.
        /// </summary>
        internal void RequestActiveView(PanelViewKind kind)
        {
            if (!_built)
            {
                _pendingView = kind;
                return;
            }
            SetActiveView(kind);
        }

        internal void SetActiveView(PanelViewKind kind)
        {
            if (_activeView == kind || !_built)
            {
                return;
            }
            DeactivateView(_activeView);
            SetViewVisible(_activeView, false);
            _activeView = kind;
            SetViewVisible(_activeView, true);
            ActivateView(_activeView);
            UpdateViewSwitchStrip();
            _dirty = true;
            RefreshIfDirty();
        }

        private VisualElement RootForView(PanelViewKind kind)
        {
            switch (kind)
            {
                case PanelViewKind.Settings:
                    return _settingsRoot;
                case PanelViewKind.History:
                    return _historyRoot;
                case PanelViewKind.Chat:
                default:
                    return _chatRoot;
            }
        }

        private void SetViewVisible(PanelViewKind kind, bool visible)
        {
            VisualElement root = RootForView(kind);
            if (root != null)
            {
                root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void ActivateView(PanelViewKind kind)
        {
            if (kind == PanelViewKind.Chat && _chatView != null)
            {
                _chatView.OnActivate();
            }
            else if (kind == PanelViewKind.Settings && _settingsView != null)
            {
                _settingsView.OnActivate();
            }
            else if (kind == PanelViewKind.History && _historyView != null)
            {
                _historyView.OnActivate();
            }
        }

        private void DeactivateView(PanelViewKind kind)
        {
            if (kind == PanelViewKind.Chat && _chatView != null)
            {
                _chatView.OnDeactivate();
            }
            else if (kind == PanelViewKind.Settings && _settingsView != null)
            {
                _settingsView.OnDeactivate();
            }
            else if (kind == PanelViewKind.History && _historyView != null)
            {
                _historyView.OnDeactivate();
            }
        }

        private void BuildViewSwitchStrip(VisualElement viewContainer)
        {
            _viewStrip = new VisualElement();
            _viewStrip.AddToClassList("uap-viewstrip");
            _backButton = MakeStripButton(null, "<", L10n.S.NavBackButtonLabel,
                L10n.S.NavBackButtonTooltip,
                delegate { SetActiveView(PanelViewKind.Chat); });
            _viewStrip.Add(_backButton);
            viewContainer.Add(_viewStrip);
        }

        private void UpdateViewSwitchStrip()
        {
            // The whole strip (not just the button) hides on Chat now that
            // it holds only the back affordance -- HeaderView's own
            // history/settings buttons cover Chat-side navigation, so an
            // empty bordered band above the transcript would just be dead
            // space (see the class-level comment on _backButton).
            bool onChat = _activeView == PanelViewKind.Chat;
            _viewStrip.style.display = onChat ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static Button MakeStripButton(string iconName, string fallbackGlyph,
            string label, string tooltip, Action onClick)
        {
            var button = new Button(onClick);
            button.AddToClassList("uap-viewstrip-btn");
            button.tooltip = tooltip;
            button.Add(IconLoader.CreateIcon(iconName, fallbackGlyph,
                "uap-viewstrip-icon", "uap-viewstrip-glyph"));
            var text = new Label(label);
            text.AddToClassList("uap-viewstrip-label");
            text.enableRichText = false;
            button.Add(text);
            return button;
        }

        // -- Shared window styling helpers (also used by PermissionWindow) ----------

        /// <summary>
        /// Reapplies the CJK font (window-wide) and the font-size class
        /// (conversation-content-only) to every currently open
        /// AgentPanelWindow/PermissionWindow. Called by SettingsView right
        /// after the user flips the CJK toggle or moves the font-size
        /// slider so the change is visible immediately, without waiting for
        /// the next window open/reopen.
        ///
        /// The two fonts intentionally target different elements: CJK still
        /// goes on the WHOLE window (rootVisualElement) like the theme
        /// class, but font-scale goes on each window's own
        /// conversation-content root (AgentPanelWindow._chatRoot /
        /// PermissionWindow.ContentRoot) so the slider never reflows
        /// Settings/History or the window chrome -- see ApplyFontScale's
        /// doc comment and docs/design-notes/2026-08-01-fontscale-scope.md
        /// (live feedback: dragging the slider used to rescale the entire
        /// window, shifting the Settings layout under the cursor and making
        /// continuous dragging impossible).
        /// </summary>
        internal static void ReapplyContentRootStyling()
        {
            AgentPanelWindow[] panels = Resources.FindObjectsOfTypeAll<AgentPanelWindow>();
            for (int i = 0; i < panels.Length; i++)
            {
                AgentPanelWindow panel = panels[i];
                if (panel != null && panel.rootVisualElement != null)
                {
                    ApplyCjkUiFont(panel.rootVisualElement);
                    if (panel._chatRoot != null)
                    {
                        ApplyFontScale(panel._chatRoot);
                    }
                }
            }
            PermissionWindow[] permWindows = Resources.FindObjectsOfTypeAll<PermissionWindow>();
            for (int i = 0; i < permWindows.Length; i++)
            {
                PermissionWindow permWindow = permWindows[i];
                if (permWindow != null && permWindow.rootVisualElement != null)
                {
                    ApplyCjkUiFont(permWindow.rootVisualElement);
                    VisualElement content = permWindow.ContentRoot;
                    if (content != null)
                    {
                        ApplyFontScale(content);
                    }
                }
            }
        }

        /// <summary>
        /// Marks every text element in every open AgentPanelWindow /
        /// PermissionWindow for repaint. Used by FontLoader after the CJK
        /// FontAsset was repaired IN PLACE (same instance, new atlas
        /// material): the material swap is what makes UI Toolkit
        /// regenerate a label's cached text mesh, but only on its next
        /// repaint -- and a label whose last draw threw from the
        /// destroyed atlas is no longer dirty, so nothing else would
        /// repaint it. Repairs are rare; the tree walk is affordable.
        /// </summary>
        internal static void MarkAllTextDirty()
        {
            AgentPanelWindow[] panels = Resources.FindObjectsOfTypeAll<AgentPanelWindow>();
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null)
                {
                    MarkTextDirty(panels[i].rootVisualElement);
                }
            }
            PermissionWindow[] permWindows = Resources.FindObjectsOfTypeAll<PermissionWindow>();
            for (int i = 0; i < permWindows.Length; i++)
            {
                if (permWindows[i] != null)
                {
                    MarkTextDirty(permWindows[i].rootVisualElement);
                }
            }
        }

        private static void MarkTextDirty(VisualElement root)
        {
            if (root == null)
            {
                return;
            }
            try
            {
                root.Query<TextElement>().ForEach(element => element.MarkDirtyRepaint());
            }
            catch (System.Exception)
            {
                // One window's tree must not stop the sweep.
            }
        }

        /// <summary>
        /// Assigns the `uap-fontscale-N` class matching PanelSettings
        /// .fontSizePx (clamped) to the given CONVERSATION-CONTENT root --
        /// never the window root (see
        /// docs/design-notes/2026-08-01-fontscale-scope.md: applying it at
        /// the window root used to rescale Settings/History and the window
        /// chrome too, which broke slider dragging by shifting the very
        /// control the cursor was on). See
        /// docs/design-notes/2026-07-31-font-size-application.md for why
        /// this overrides the --uap-font-size-* CUSTOM PROPERTIES every
        /// text class already reads via var() rather than assigning an
        /// inline style.fontSize (which would not cascade, since every text
        /// class sets its own font-size explicitly) -- the custom-property
        /// override still works from a descendant root exactly as it did
        /// from the window root, since FontScale.uss's selectors are plain
        /// class selectors with no dependency on which element carries the
        /// class, only that it is an ancestor of the text elements that
        /// should scale.
        /// </summary>
        internal static void ApplyFontScale(VisualElement root)
        {
            for (int px = Colloid.AgentPanel.Model.PanelSettings.MinFontSizePx;
                px <= Colloid.AgentPanel.Model.PanelSettings.MaxFontSizePx; px++)
            {
                root.RemoveFromClassList(FontScaleClassName(px));
            }
            Colloid.AgentPanel.Model.PanelStateStore store =
                Colloid.AgentPanel.Model.PanelStateStore.instance;
            Colloid.AgentPanel.Model.PanelSettings settings =
                store != null ? store.Settings : null;
            int fontSizePx = settings != null
                ? settings.fontSizePx
                : Colloid.AgentPanel.Model.PanelSettings.DefaultFontSizePx;
            fontSizePx = Colloid.AgentPanel.Model.PanelSettings.ClampFontSize(fontSizePx);
            root.AddToClassList(FontScaleClassName(fontSizePx));
        }

        private static string FontScaleClassName(int px)
        {
            return "uap-fontscale-" + px.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // -- Narrow-width mode (2026-09-05 UI redesign, D5) --------------------------

        /// <summary>
        /// Below this root width the panel folds its secondary chrome
        /// (status-bar model name / separators / usage figures, the
        /// composer hint) and caps the header chips -- see the
        /// `.uap-narrow` block at the end of AgentPanel.uss. 340px is the
        /// "narrow" line 05-ux-spec.md sections 2.2 and 2.6 already drew.
        /// </summary>
        internal const float NarrowWidthThresholdPx = 340f;

        internal const string NarrowClassName = "uap-narrow";

        /// <summary>
        /// Pure decision: is a root of this width narrow? A width that is
        /// not a real measurement yet (NaN before the first layout, 0 or
        /// negative from a just-docked or collapsed host) is NOT narrow --
        /// the first GeometryChangedEvent after docking can report 0, and
        /// folding the chrome on a phantom measurement would flash it away
        /// for one frame on every open.
        /// </summary>
        internal static bool ResolveIsNarrow(float width, float thresholdPx)
        {
            if (float.IsNaN(width) || width <= 0f)
            {
                return false;
            }
            return width < thresholdPx;
        }

        internal static void ApplyWidthClass(VisualElement root, float width)
        {
            root.EnableInClassList(NarrowClassName, ResolveIsNarrow(width, NarrowWidthThresholdPx));
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            ApplyWidthClass(rootVisualElement, evt.newRect.width);
        }
    }
}
