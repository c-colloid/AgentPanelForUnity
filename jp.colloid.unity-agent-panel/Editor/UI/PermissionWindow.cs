using System;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Floating NON-modal host for the permission card (hybrid card UX).
    /// Shown with ShowUtility -- never ShowModal/ShowModalUtility -- so the
    /// editor stays fully interactive while a can_use_tool request waits.
    ///
    /// Opens automatically (from ChatView) when a request arrives while the
    /// docked panel is too small (PermissionCardLayout.ShouldOpenWindow),
    /// and manually via the inline card's open-in-window button. It hosts
    /// the SAME PermissionCard component in HostKind.Window mode -- one
    /// implementation, two hosts -- and the single source of truth stays
    /// AgentHub.PendingPermission: answering here or inline resolves both
    /// (this window closes as soon as no request is pending, which also
    /// covers response, process death, interrupt and turn end).
    ///
    /// Lifecycle hard rules:
    /// - Exactly one instance ever (static reuse; reopening an existing
    ///   window only marks it dirty -- no focus stealing beyond the initial
    ///   ShowUtility).
    /// - EditorWindow instances persist across domain reload, but a pending
    ///   permission never does (the CLI process is killed before the
    ///   reload), so on load a delayCall sweep closes any surviving
    ///   PermissionWindow with no live pending request, silently.
    /// </summary>
    public sealed class PermissionWindow : EditorWindow
    {
        private const long RefreshIntervalMillis = 80;
        private static readonly Vector2 DefaultSize = new Vector2(420f, 360f);

        /// <summary>
        /// Width for an AskUserQuestion request (2026-08-05 stepper design
        /// note section 2c): option buttons are a label + description
        /// two-line stack, and narrower widths wrap the description to
        /// three lines and eat the card's height. Tool-variant requests
        /// keep the original 420x360. INITIAL size only -- the reuse path
        /// in OpenForPending never touches position/size of a window the
        /// user already has open (they may have placed and sized it
        /// deliberately).
        /// </summary>
        private const float QuestionVariantWidth = 560f;

        /// <summary>
        /// Content-driven question-variant height (2026-08-14 UI polish
        /// audit item 6): the flat 520pt default over-allocated for a
        /// short single question, so the height is now a pure function of
        /// the parsed question count -- see
        /// <see cref="ComputeQuestionVariantHeight"/>. Chrome that is not
        /// part of any question section: title bar margin, the summary row
        /// (icon + title), the Submit/Skip action row, and body padding.
        /// </summary>
        private const float QuestionChromeHeight = 170f;

        /// <summary>
        /// Height budgeted for the ONE currently visible question section
        /// (header/question text + option buttons). The stepper only ever
        /// shows one section at a time (2026-08-05 stepper design note,
        /// QuestionStepperModel), so this is a flat addend -- NOT
        /// multiplied by the question count.
        /// </summary>
        private const float QuestionSectionHeight = 175f;

        /// <summary>Added only when the tab strip itself renders (2+ questions).</summary>
        private const float QuestionTabStripHeight = 24f;

        /// <summary>
        /// Clamp floor/ceiling (2026-08-14 UI polish audit item 6) kept as
        /// a safety net even though the current formula's own outputs
        /// never reach either bound -- SourceScan_ComputeQuestionVariantHeight_ClampsToDesignedBounds
        /// (PermissionCardTests) pins their presence since no behavioral
        /// test can otherwise catch their removal.
        /// </summary>
        private const float QuestionHeightMin = 320f;

        private const float QuestionHeightMax = 520f;

        private static readonly Vector2 MinimumSize = new Vector2(320f, 240f);

        private static PermissionWindow _instance;

        private PermissionCard _card;
        private VisualElement _contentRoot;
        private IVisualElementScheduledItem _refreshLoop;
        private bool _dirty;
        private bool _built;

        /// <summary>
        /// The permission-preview content root (wraps the PermissionCard).
        /// This -- NOT rootVisualElement -- is what receives the
        /// `uap-fontscale-N` class (AgentPanelWindow.ReapplyContentRootStyling
        /// reads this so a live slider change reaches this window too): see
        /// docs/design-notes/2026-08-01-fontscale-scope.md. The window root
        /// still gets the theme/stylesheets/CJK font via
        /// AgentPanelWindow.ApplyThemeAndStyles, same as before.
        /// </summary>
        internal VisualElement ContentRoot
        {
            get { return _contentRoot; }
        }

        /// <summary>
        /// True when this window was opened by the size decision rather
        /// than a user click. An auto-opened window must never arm the
        /// card's Y/N hotkeys (the user may be mid-keystroke in the
        /// composer); focus is handed straight back to the window that had
        /// it. Not serialized on purpose: the window never survives a
        /// domain reload with a live request (see the orphan sweep).
        /// </summary>
        private bool _autoOpened;

        /// <summary>True while the floating window exists (open or opening).</summary>
        public static bool IsOpen
        {
            get { return _instance != null; }
        }

        /// <summary>
        /// Raised when the window opens or closes so the inline host can
        /// swap between the full card and the slim wait bar without polling.
        /// </summary>
        public static event Action StateChanged;

        // -- Static open/close ---------------------------------------------------

        /// <summary>
        /// Shows the currently pending permission request in the floating
        /// window. Reuses the single existing instance (without focusing
        /// it); no-op when nothing is pending.
        ///
        /// autoOpened = true is the ChatView size-decision path: utility
        /// windows take OS keyboard focus on ShowUtility, so the window
        /// gives focus straight back to whichever EditorWindow had it (the
        /// user may be typing an alternative in the composer -- their next
        /// 'y'/'n' keystroke must keep landing in that text field, never in
        /// this card). The card's self-focus is suppressed too, so the Y/N
        /// hotkeys only arm after the user explicitly interacts with this
        /// window. autoOpened = false (open-in-window button, [Show here]
        /// reversal) keeps the normal focus-follows-intent behavior.
        /// </summary>
        public static void OpenForPending(bool autoOpened)
        {
            if (AgentHub.PendingPermission == null)
            {
                return;
            }
            if (_instance != null)
            {
                // Reuse: refresh only. Focus stays wherever the user put it.
                _instance._dirty = true;
                return;
            }
            EditorWindow previousFocus = autoOpened ? focusedWindow : null;
            var window = CreateInstance<PermissionWindow>();
            window._autoOpened = autoOpened;
            window.ShowUtility();
            // Decide the initial size from the pending request ONCE, here:
            // question-variant requests get a content-driven size (see
            // ComputeInitialSize).
            window.CenterOnMainEditorWindow(
                ComputeInitialSize(AgentHub.PendingPermission));
            if (previousFocus != null)
            {
                previousFocus.Focus();
            }
            RaiseStateChanged();
        }

        /// <summary>Closes the window when it is open ([Show here] / teardown).</summary>
        public static void CloseIfOpen()
        {
            if (_instance != null)
            {
                _instance.Close();
            }
        }

        /// <summary>
        /// Domain-reload sweep: EditorWindow instances survive the reload
        /// but pending permissions never do (the CLI is killed before the
        /// reload), so any persisted PermissionWindow without a live
        /// pending request closes itself silently.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void CloseOrphansAfterReload()
        {
            EditorApplication.delayCall += delegate
            {
                if (AgentHub.PendingPermission != null)
                {
                    return;
                }
                PermissionWindow[] windows =
                    Resources.FindObjectsOfTypeAll<PermissionWindow>();
                for (int i = 0; i < windows.Length; i++)
                {
                    if (windows[i] != null)
                    {
                        windows[i].Close();
                    }
                }
            };
        }

        // -- Unity lifecycle -------------------------------------------------------

        private void OnEnable()
        {
            _instance = this;
            titleContent = new GUIContent(L10n.A(L10n.S.PermWindowTitle));
            minSize = MinimumSize;
        }

        public void CreateGUI()
        {
            // Re-entry guard (UICODE-1), mirroring AgentPanelWindow.CreateGUI:
            // RebuildAllOpenPanels re-invokes CreateGUI directly on every
            // open instance of THIS window type too (its doc comment always
            // claimed the path was "re-entry-safe" -- previously true only
            // for AgentPanelWindow). Without this block, a language switch
            // while a permission request was on screen double-subscribed
            // AgentHub.Changed (OnDisable removes only one occurrence),
            // stacked a second host+PermissionCard under the never-cleared
            // root (duplicate cards, the stale one frozen in the old
            // language), and leaked the first scheduled refresh loop.
            if (_built)
            {
                AgentHub.Changed -= OnHubChanged;
                if (_refreshLoop != null)
                {
                    _refreshLoop.Pause();
                    _refreshLoop = null;
                }
                _built = false;
            }

            // Same boot-time language application as AgentPanelWindow.CreateGUI:
            // a floating permission window re-created after a reload must not
            // build its labels from the default English catalog while the
            // user's setting says otherwise.
            L10n.ApplyFromSettings(PanelStateStore.instance.Settings.language);

            VisualElement root = rootVisualElement;
            // Drop the previous build's host+card outright (stylesheets are
            // handled by ApplyThemeAndStyles' own idempotent loader; the
            // root's other state is Unity's, not ours).
            root.Clear();
            AgentPanelWindow.ApplyThemeAndStyles(root);

            var host = new VisualElement();
            host.AddToClassList("uap-permwin-root");
            root.Add(host);
            _contentRoot = host;
            // Font-scale anchor: this content root only, never the window
            // root -- see ContentRoot's doc comment and
            // docs/design-notes/2026-08-01-fontscale-scope.md.
            AgentPanelWindow.ApplyFontScale(host);

            _card = new PermissionCard(PermissionCard.HostKind.Window);
            // Auto-opened windows never self-focus the card: the synchronous
            // refresh below would otherwise arm Y/N while the user is still
            // typing in the docked panel (whose focus guard cannot see THIS
            // panel's focusController).
            _card.SetAutoFocusEnabled(!_autoOpened);
            host.Add(_card.Root);
            TextEscapes.Disable(host);

            AgentHub.Changed += OnHubChanged;
            _refreshLoop = root.schedule.Execute(RefreshIfDirty).Every(RefreshIntervalMillis);
            _dirty = true;
            _built = true;
            // Populate synchronously only when there is something to show.
            // The no-pending case (a persisted window rebuilt right after a
            // domain reload) must NOT Close() from inside CreateGUI; the
            // scheduled loop / the reload sweep handles it a tick later.
            if (AgentHub.PendingPermission != null)
            {
                RefreshIfDirty();
            }
        }

        private void OnDisable()
        {
            if (!_built)
            {
                return;
            }
            _built = false;
            AgentHub.Changed -= OnHubChanged;
            if (_refreshLoop != null)
            {
                _refreshLoop.Pause();
                _refreshLoop = null;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
            RaiseStateChanged();
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
            var pending = AgentHub.PendingPermission;
            if (pending == null)
            {
                // Response sent (from either host), process death,
                // interrupt or turn end: this window has no reason to live.
                Close();
                return;
            }
            _card.Refresh(pending);
        }

        // -- Helpers ---------------------------------------------------------------------

        /// <summary>
        /// Picks the initial window size for the pending request: a
        /// content-driven question-variant size (see
        /// <see cref="ComputeQuestionVariantHeight"/>, 2026-08-14 UI polish
        /// audit item 6) when the request will render as the
        /// AskUserQuestion card, the classic tool-variant size otherwise.
        /// Static + internal so the decision is EditMode testable without
        /// opening a window.
        /// </summary>
        internal static Vector2 ComputeInitialSize(ControlRequestMessage pending)
        {
            if (!IsQuestionVariantRequest(pending))
            {
                return DefaultSize;
            }
            int questionCount = AskUserQuestionInput
                .FromInput(pending.CanUseTool.Input).Questions.Count;
            return new Vector2(QuestionVariantWidth,
                ComputeQuestionVariantHeight(questionCount));
        }

        /// <summary>
        /// Content-driven height for the AskUserQuestion variant
        /// (2026-08-14 UI polish audit item 6, replacing the flat 520pt
        /// default that over-allocated for a short single question):
        /// chrome + the one currently-visible question section, plus the
        /// tab strip only when there is one to show (2+ questions). NOT
        /// scaled by the actual question count beyond that -- the stepper
        /// (QuestionStepperModel) only ever displays one section at a
        /// time, so a 4-question request needs exactly the same room as a
        /// 2-question one. Clamped to [QuestionHeightMin,
        /// QuestionHeightMax] as a floor/ceiling safety net. Pure function
        /// of the question count so it is EditMode-testable without a live
        /// window (the "Auto-sizing... by measuring live layout" approach
        /// was explicitly rejected in the audit -- this static estimate
        /// stays).
        /// </summary>
        internal static float ComputeQuestionVariantHeight(int questionCount)
        {
            float height = QuestionChromeHeight + QuestionSectionHeight;
            if (questionCount >= 2)
            {
                height += QuestionTabStripHeight;
            }
            return Mathf.Clamp(height, QuestionHeightMin, QuestionHeightMax);
        }

        /// <summary>
        /// Mirrors PermissionCard.Build()'s variant decision (same inputs,
        /// same parser): requires_user_interaction AND at least one
        /// well-formed question. A requires_user_interaction request whose
        /// questions parse empty falls back to the tool variant in the
        /// card, so it must get the tool-variant size here too.
        /// </summary>
        internal static bool IsQuestionVariantRequest(ControlRequestMessage pending)
        {
            if (pending == null || pending.CanUseTool == null)
            {
                return false;
            }
            if (!pending.CanUseTool.RequiresUserInteraction)
            {
                return false;
            }
            return AskUserQuestionInput.FromInput(pending.CanUseTool.Input)
                .Questions.Count > 0;
        }

        private void CenterOnMainEditorWindow(Vector2 size)
        {
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float x = main.x + (main.width - size.x) * 0.5f;
            float y = main.y + (main.height - size.y) * 0.5f;
            position = new Rect(x, y, size.x, size.y);
        }

        private static void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
