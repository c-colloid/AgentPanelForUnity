using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PanelSettings = Colloid.AgentPanel.Model.PanelSettings;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Multiline composer with keyboard send (ARCHITECTURE.md D6 / R05
    /// section 2.5):
    /// - Enter sends, Shift+Enter inserts a newline. With
    ///   PanelSettings.ctrlEnterToSend (the IME escape hatch, risk 11)
    ///   Enter always inserts a newline and Ctrl/Cmd+Enter sends.
    /// - KeyDownEvent is captured at TrickleDown and both the keycode and
    ///   the paired character event are stopped + prevented on send. The
    ///   keycode event DECIDES and the character event obeys that stored
    ///   decision (see <see cref="ResolveEnterAction"/>) -- deriving it
    ///   twice used to eat the newline whenever the character event
    ///   arrived without the Shift flag (design note 2026-08-02-composer-
    ///   newline-and-profile-row.md).
    /// - Esc interrupts the running turn.
    /// - The send button swaps to Stop only while a turn is active AND the
    ///   field is empty; as soon as the field has text it reads Send again,
    ///   even mid-turn, so a mouse click sends (steers) instead of
    ///   interrupting (design note 2026-08-03-subagent-ux-and-midturn-
    ///   input.md section 3; decision + rejected alternatives recorded on
    ///   <see cref="PrimaryAction"/>). Esc always interrupts regardless of
    ///   field content -- this only affects the ONE button's meaning.
    /// - Sends go through CompileGate.SendOrQueue so messages typed during
    ///   a compile are queued instead of hitting a dying process.
    /// - The draft is mirrored to SessionStateBridge.InputDraft so it
    ///   survives domain reloads.
    /// - Slash commands (design note 2026-09-07-slash-commands-and-
    ///   compaction.md section 1): while the field holds "/" plus a
    ///   command prefix, a suggestion popup lists the CLI's catalog
    ///   (AgentHub.SlashCommands); Up/Down move, Tab or Enter complete,
    ///   Esc closes. A sent "/name args" goes to the CLI verbatim -- no
    ///   context chips or images ride along, they stay pending for the
    ///   next real message -- except "/clear", which the panel handles
    ///   itself as New chat (AgentHub.StartFresh).
    /// </summary>
    public sealed class ComposerView
    {
        private readonly VisualElement _root;
        private readonly TextField _field;
        private readonly Label _placeholder;
        private readonly Label _hint;
        private readonly Button _quickActionsButton;
        /// <summary>"+" menu: image file / Scene view / Game view / Scene view as displayed (design note 2026-09-07 section 2.3.2).</summary>
        private readonly Button _attachButton;
        /// <summary>Pending image thumbnails above the field; consumed by the next send.</summary>
        private readonly VisualElement _attachStrip;
        private readonly System.Collections.Generic.List<ImageAttachment> _images =
            new System.Collections.Generic.List<ImageAttachment>();
        private readonly Button _action;

        private bool _inputEnabled = true;
        private bool _turnActive;
        private bool _pendingPermissionHint;
        private bool _stopStyleApplied;
        private ContextBarView _contextBar;

        // -- Slash-command popup state (see the class remarks) --
        private readonly VisualElement _slashPopup;
        private List<SlashCommandEntry> _slashMatches = new List<SlashCommandEntry>();
        private string _slashPrefix = string.Empty;
        private int _slashSelected = -1;
        private bool _slashPopupVisible;
        /// <summary>
        /// Set by a Tab keycode event the popup consumed: Unity follows it
        /// with a character event carrying '\t' (keyCode None) that the
        /// TextField would otherwise insert into the just-completed text.
        /// Consumed by the very next KeyDownEvent, whatever it is.
        /// </summary>
        private bool _swallowNextTabChar;

        public VisualElement Root
        {
            get { return _root; }
        }

        public ComposerView()
        {
            _root = new VisualElement();
            _root.AddToClassList("uap-composer");

            _attachStrip = new VisualElement();
            _attachStrip.AddToClassList("uap-attach-strip");
            _attachStrip.style.display = DisplayStyle.None;
            _root.Add(_attachStrip);
            RestorePendingImages();

            _slashPopup = new VisualElement();
            _slashPopup.AddToClassList("uap-slash-popup");
            _slashPopup.style.display = DisplayStyle.None;
            _root.Add(_slashPopup);

            var fieldWrap = new VisualElement();
            fieldWrap.AddToClassList("uap-composer-fieldwrap");

            _field = new TextField();
            _field.multiline = true;
            _field.AddToClassList("uap-composer-field");
            _field.SetValueWithoutNotify(SessionStateBridge.InputDraft);
            _field.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _field.RegisterValueChangedCallback(OnValueChanged);
            _field.RegisterCallback<FocusInEvent>(delegate { UpdatePlaceholder(); });
            _field.RegisterCallback<FocusOutEvent>(delegate { UpdatePlaceholder(); });
            fieldWrap.Add(_field);

            _placeholder = new Label(string.Empty);
            _placeholder.AddToClassList("uap-composer-placeholder");
            _placeholder.pickingMode = PickingMode.Ignore;
            fieldWrap.Add(_placeholder);
            _root.Add(fieldWrap);

            var row = new VisualElement();
            row.AddToClassList("uap-composer-row");

            _hint = new Label(string.Empty);
            _hint.AddToClassList("uap-composer-hint");
            row.Add(_hint);

            var actionsRow = new VisualElement();
            actionsRow.AddToClassList("uap-composer-actions");

            // Quick-action menu (design note 2026-08-01 #3b): lists the
            // user's Settings-configured QuickAction labels and inserts the
            // selected one's prompt into the field -- never auto-sends, so
            // a misclick never fires an unreviewed message.
            _attachButton = new Button(OnAttachClicked);
            _attachButton.text = L10n.S.ComposerAttachButton;
            _attachButton.tooltip = L10n.S.ComposerAttachTooltip;
            _attachButton.AddToClassList("uap-quickactions-btn");
            _attachButton.AddToClassList("uap-attach-btn");
            actionsRow.Add(_attachButton);

            _quickActionsButton = new Button(OnQuickActionsClicked);
            _quickActionsButton.text = L10n.S.ComposerQuickButton;
            _quickActionsButton.tooltip = L10n.S.ComposerQuickTooltip;
            _quickActionsButton.AddToClassList("uap-quickactions-btn");
            actionsRow.Add(_quickActionsButton);

            _action = new Button(OnActionClicked);
            _action.text = L10n.S.ComposerSendButton;
            _action.AddToClassList("uap-send-btn");
            actionsRow.Add(_action);
            row.Add(actionsRow);
            _root.Add(row);

            UpdatePlaceholder();
            RefreshSlashPopup();
            UpdateHint();
        }

        // -- Public API -----------------------------------------------------------

        /// <summary>Re-reads client/settings state and updates enable/labels.</summary>
        public void Refresh(AgentClient client, bool inputEnabled)
        {
            _inputEnabled = inputEnabled;
            _turnActive = client != null && client.TurnActive;

            _field.SetEnabled(inputEnabled);
            _quickActionsButton.SetEnabled(inputEnabled);
            _attachButton.SetEnabled(inputEnabled);
            UpdateActionButton();
            UpdatePlaceholder();
            if (!inputEnabled && _slashPopupVisible)
            {
                HideSlashPopup();
            }
            UpdateHint();
        }

        /// <summary>
        /// Wires the context bar whose chips are consumed on send: the
        /// outgoing WIRE text becomes user text + delimited context blocks
        /// (ContextBlockFormatter.Compose) while the transcript keeps the
        /// typed text plus structured attachment chips, still routed
        /// through CompileGate so queued sends keep their context.
        /// </summary>
        public void SetContextSource(ContextBarView contextBar)
        {
            _contextBar = contextBar;
        }

        /// <summary>Puts suggestion text into the field and focuses it (no auto-send).</summary>
        public void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            string current = _field.value ?? string.Empty;
            _field.value = current.Length == 0 ? text : current + "\n" + text;
            FocusField();
        }

        /// <summary>Persists the draft (called from SerializeState).</summary>
        public void SaveDraft()
        {
            SessionStateBridge.InputDraft = _field.value ?? string.Empty;
        }

        public void FocusField()
        {
            _field.schedule.Execute(() => _field.Focus()).StartingIn(1);
        }

        // -- Events ------------------------------------------------------------------

        /// <summary>UICODE-7 test seams: drive OnKeyDown with a synthetic
        /// event (panel-less SendEvent is a no-op in EditMode tests) and
        /// observe the keycode->char bridge state. Test-only.</summary>
        internal void SimulateKeyDownForTests(KeyDownEvent evt)
        {
            OnKeyDown(evt);
        }

        internal EnterAction PendingEnterActionForTests
        {
            get { return _pendingEnterAction; }
        }

        /// <summary>Test seam: sets the field text through the normal change path (popup refresh included).</summary>
        internal void SetFieldTextForTests(string text)
        {
            SetFieldText(text);
        }

        internal string FieldTextForTests
        {
            get { return _field.value ?? string.Empty; }
        }

        internal bool SlashPopupVisibleForTests
        {
            get { return _slashPopupVisible; }
        }

        internal IList<SlashCommandEntry> SlashMatchesForTests
        {
            get { return _slashMatches; }
        }

        internal int SlashSelectedForTests
        {
            get { return _slashSelected; }
        }

        internal string HintTextForTests
        {
            get { return _hint.text; }
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_swallowNextTabChar)
            {
                _swallowNextTabChar = false;
                if (evt.character == '\t' && evt.keyCode == KeyCode.None)
                {
                    evt.StopPropagation();
                    evt.PreventDefault();
                    return;
                }
            }
            if (_slashPopupVisible && TryHandleSlashPopupKey(evt))
            {
                return;
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                AgentClient client = AgentHub.Client;
                if (client != null && client.TurnActive)
                {
                    client.Interrupt();
                    evt.StopPropagation();
                }
                return;
            }

            if (ClipboardImageReader.IsPasteChord(evt.keyCode, evt.ctrlKey || evt.commandKey, evt.shiftKey, evt.altKey))
            {
                // Image paste: only when the clipboard has no text (text
                // paste stays the TextField's own). Swallow the chord when
                // an image was taken so the field does not also paste.
                _pendingEnterAction = EnterAction.Unknown;
                if (TryPasteClipboardImage(false))
                {
                    evt.StopPropagation();
                    evt.PreventDefault();
                }
                return;
            }

            bool isEnterKey = evt.keyCode == KeyCode.Return
                || evt.keyCode == KeyCode.KeypadEnter;
            bool isEnterChar = !isEnterKey && (evt.character == '\n' || evt.character == '\r');
            if (!isEnterKey && !isEnterChar)
            {
                // UICODE-7: any other key breaks the keycode->char bridge.
                // _pendingEnterAction exists ONLY to hand one keycode
                // Enter's decision to its immediately-following character
                // event; when that pair never completes (some IME/platform
                // paths suppress the char event after PreventDefault), a
                // stale action would otherwise sit here until the NEXT
                // orphan Enter character -- an IME newline commit, say --
                // and replay the old Send/Newline decision on it.
                _pendingEnterAction = EnterAction.Unknown;
                return;
            }

            PanelSettings settings = PanelStateStore.instance.Settings;
            EnterAction action = ResolveEnterAction(
                settings.ctrlEnterToSend,
                evt.ctrlKey || evt.commandKey,
                evt.shiftKey,
                isEnterKey,
                _pendingEnterAction,
                IsImeCompositionActive());
            _pendingEnterAction = isEnterKey ? action : EnterAction.Unknown;

            if (action == EnterAction.Send && _slashPopupVisible)
            {
                SlashCommandEntry selected = SelectedSlashEntry();
                if (SlashCommandCatalog.ShouldCompleteOnEnter(_slashPrefix, selected))
                {
                    // Enter on a highlighted suggestion COMPLETES it; the
                    // next Enter sends. The paired character event must
                    // neither send nor insert a newline into the completed
                    // text, so it inherits Ignore through the pending
                    // protocol (same shape as the IME-confirm case below).
                    _pendingEnterAction = isEnterKey ? EnterAction.Ignore : EnterAction.Unknown;
                    evt.StopPropagation();
                    evt.PreventDefault();
                    AcceptSlashEntry(selected);
                    return;
                }
            }

            if (action == EnterAction.Ignore)
            {
                // UXO-2: a composition-confirm Enter. Swallow the pair the
                // same way every other Enter is swallowed (the editing
                // engine must not double-handle it), but neither send nor
                // insert -- the IME's commit reaches the field through the
                // text engine, not through this event.
                evt.StopPropagation();
                evt.PreventDefault();
                return;
            }

            if (action == EnterAction.Newline)
            {
                // Insert the line break OURSELVES; never fall through to
                // UI Toolkit's default handling. Measured live 2026-08-02
                // (design note section 1b): in a multiline UITK TextField
                // the editing engine treats Shift+Enter as "commit and
                // leave" -- it calls MoveFocusToCompositeRoot, so focus
                // jumps from the inner TextElement to the outer TextField,
                // the caret disappears and NO newline is inserted (the
                // event trace showed the focus hop with no ChangeEvent).
                // Only the keycode event inserts; the paired character
                // event is swallowed below so the break is not doubled.
                evt.StopPropagation();
                evt.PreventDefault();
                if (isEnterKey)
                {
                    InsertNewlineAtCaret();
                }
                return;
            }

            // Swallow both the keycode event and the paired character
            // event so no stray newline lands in the field.
            evt.StopPropagation();
            evt.PreventDefault();
            if (isEnterKey)
            {
                TrySend();
            }
        }

        /// <summary>
        /// Replaces the current selection (or inserts at the caret) with a
        /// line break and leaves the caret after it. Done by hand because
        /// UI Toolkit's own multiline handling maps Shift+Enter to "end
        /// editing", not "new line" -- see the call site.
        /// <see cref="ComputeNewlineInsertion"/> is the pure part.
        /// </summary>
        private void InsertNewlineAtCaret()
        {
            string text = _field.value ?? string.Empty;
            int caret;
            string next = ComputeNewlineInsertion(
                text, _field.cursorIndex, _field.selectIndex, out caret);
            _field.value = next;
            // Both indices must move, or the next keystroke would replace
            // the range between a stale selection anchor and the caret.
            _field.SelectRange(caret, caret);
        }

        /// <summary>
        /// Pure text edit behind <see cref="InsertNewlineAtCaret"/>:
        /// replaces [min(cursor,select), max(cursor,select)) with "\n" and
        /// reports where the caret lands. Indices are clamped, so a stale
        /// or out-of-range caret (possible right after the draft is
        /// restored across a domain reload) degrades to an append instead
        /// of throwing.
        /// </summary>
        internal static string ComputeNewlineInsertion(
            string text, int cursorIndex, int selectIndex, out int newCaretIndex)
        {
            text = text ?? string.Empty;
            int start = cursorIndex < selectIndex ? cursorIndex : selectIndex;
            int end = cursorIndex < selectIndex ? selectIndex : cursorIndex;
            start = Mathf.Clamp(start, 0, text.Length);
            end = Mathf.Clamp(end, start, text.Length);
            newCaretIndex = start + 1;
            return text.Substring(0, start) + "\n" + text.Substring(end);
        }

        /// <summary>What an Enter keypress should do.</summary>
        internal enum EnterAction
        {
            /// <summary>No decision carried over from a keycode event.</summary>
            Unknown = 0,
            /// <summary>Insert a line break (let the field's default handler run).</summary>
            Newline,
            /// <summary>Send the message (and swallow the paired character event).</summary>
            Send,
            /// <summary>
            /// UXO-2: an Enter that arrived while an IME composition was
            /// active -- the user is CONFIRMING the composition, not
            /// sending and not asking for a line break. Swallow the event
            /// pair entirely: no send (the mis-send this exists to stop --
            /// the UX spec calls it the most important detail for Japanese
            /// users), and no inserted newline either (the caret sits
            /// inside an uncommitted composition; the IME's own commit
            /// lands through the text engine, not through this event).
            /// </summary>
            Ignore
        }

        /// <summary>
        /// Decision carried from an Enter KEYCODE event to its paired
        /// CHARACTER event. Unity delivers Enter as two KeyDownEvents (one
        /// with keyCode == Return and no character, one with
        /// character == '\n' and keyCode == None); the second does not
        /// reliably repeat the first's modifier flags, so recomputing the
        /// decision from `evt.shiftKey` there used to flip Shift+Enter to
        /// "send", swallow the character event via PreventDefault, and --
        /// since only the keycode branch calls TrySend -- silently drop the
        /// newline without sending anything (the reported bug).
        /// </summary>
        private EnterAction _pendingEnterAction = EnterAction.Unknown;

        /// <summary>
        /// Pure decision seam (unit-tested without a window): the keycode
        /// event derives the action from the settings + modifiers; the
        /// paired character event REUSES that decision when one is pending
        /// and only falls back to deriving its own when there is none (a
        /// character event with no preceding keycode event, e.g. some IME
        /// paths).
        ///
        /// UXO-2: <paramref name="imeComposing"/> (default false keeps
        /// every pre-existing call/pin byte-identical) short-circuits to
        /// <see cref="EnterAction.Ignore"/> on BOTH event shapes -- a
        /// composition-confirm Enter must neither send nor insert. The
        /// pending protocol keeps the pair consistent even when the
        /// composition ends between the two events: the keycode event's
        /// Ignore is stored and the paired character event obeys it. The
        /// pending decision wins over a live composing=true on the char
        /// event deliberately -- a keycode event that already decided
        /// Send/Newline while NOT composing must not have its paired char
        /// event re-classified by a composition that started in between.
        /// </summary>
        internal static EnterAction ResolveEnterAction(
            bool ctrlEnterToSend, bool ctrlOrCmd, bool shift,
            bool isKeyCodeEvent, EnterAction pending,
            bool imeComposing = false)
        {
            if (!isKeyCodeEvent && pending != EnterAction.Unknown)
            {
                return pending;
            }
            if (imeComposing)
            {
                return EnterAction.Ignore;
            }
            bool send = ctrlEnterToSend ? ctrlOrCmd : !shift;
            return send ? EnterAction.Send : EnterAction.Newline;
        }

        /// <summary>
        /// UXO-2: best-effort probe for an ACTIVE IME composition, read at
        /// Enter-decision time. The UX spec (docs/research/05-ux-spec.md)
        /// calls the composition-confirm guard "the most important detail
        /// for Japanese users", but Unity 2022.3's UI Toolkit exposes no
        /// isComposing on KeyDownEvent/TextField -- the only editor-side
        /// signal is UnityEngine.Input.compositionString, whose timing
        /// relative to the confirm event is platform-dependent and
        /// unmeasured (docs/research/03-unity-integration.md even claims
        /// the confirm produces no event at all in UITK 2022.3; risk 11
        /// mandates real-IME verification either way). This guard is
        /// therefore FAIL-SAFE by construction: it can only ever suppress
        /// an Enter while composition text is actually present -- when the
        /// signal is empty, cleared-before-the-event, or throwing
        /// (batchmode/CI), it returns false and behavior is byte-identical
        /// to before UXO-2. The ctrlEnterToSend escape hatch stays the
        /// documented fallback for machines where the signal never fires.
        /// Virtual-into-static via a seam is deliberately avoided: tests
        /// pin the decision through ResolveEnterAction's imeComposing
        /// parameter instead of faking this probe.
        /// </summary>
        internal static bool IsImeCompositionActive()
        {
            try
            {
                string composition = UnityEngine.Input.compositionString;
                return !string.IsNullOrEmpty(composition);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // -- Primary action (Send/Stop) ---------------------------------------------

        /// <summary>
        /// What the single always-visible action button does right now
        /// (design note 2026-08-03-subagent-ux-and-midturn-input.md
        /// section 3).
        ///
        /// MEASURED, twice, against the real CLI: a message written mid-
        /// turn is accepted and folded into the running turn -- one
        /// result, no queue needed (section 3.1). The panel's own send
        /// path already has no turn-state gate (TrySend below never
        /// checked it), so pressing Enter mid-turn already worked. The
        /// reported bug -- "let me type instead of stopping to resend" --
        /// turned out to be that the only MOUSE-reachable control ignored
        /// this entirely: it swapped to Stop while a turn ran and stayed
        /// Stop no matter what was typed, so the keyboard and mouse paths
        /// disagreed with each other. That disagreement, not a missing
        /// queue, was the actual defect.
        ///
        /// CHOSEN: keep exactly one button, in exactly one screen
        /// position, and let its meaning track the field the same way a
        /// human reading the row would -- if something is typed, the
        /// obvious click sends it (mid-turn sends steer, per the
        /// measurement above); if the field is empty, the obvious click
        /// can only mean "stop this". This is precisely the field-content
        /// split the task brief itself points at ("an empty field
        /// mid-turn arguably still wants Stop"), pushed into
        /// <see cref="ResolvePrimaryAction"/> so it is pinned by a test
        /// instead of living only in this comment.
        ///
        /// REJECTED -- a second, permanently visible Stop button next to
        /// Send: two fixed buttons plus the existing Quick button is three
        /// fixed-width controls competing with the hint label for a
        /// ~300px composer row (already tight in Japanese -- see the
        /// composer strings in UiStringsJa.cs), and it reintroduces a
        /// worse version of the exact trap this task removes: two
        /// adjacent, similarly-styled buttons a few pixels apart, one
        /// that sends and one that throws away the running turn, is a
        /// bigger mis-click hazard than one button whose meaning tracks
        /// what is plainly visible in the field beside it.
        ///
        /// REJECTED -- a modifier-key chord (e.g. Ctrl+Click to force-stop
        /// with text present): not discoverable, and not purely
        /// mouse-driven either (it still leans on a keyboard modifier).
        /// This composer already has exactly one modifier convention
        /// (Shift+Enter / Ctrl+Enter, see the class doc above), adopted
        /// only because it works around a UI Toolkit quirk -- adding a
        /// second, unrelated modifier to the same row multiplies what a
        /// user has to remember for no gain over "look at the button".
        ///
        /// THE SAFETY VALVE for the one case this narrows -- a user who
        /// has drafted text but wants to hard-stop via mouse without
        /// sending or clearing it first: Esc still interrupts
        /// unconditionally regardless of field content (OnKeyDown's
        /// Escape branch is untouched by this change), and the composer
        /// already advertises exactly that -- ComposerHintEscToStop
        /// ("Esc to stop") is shown for the entire duration of a turn,
        /// field content or not. Mouse-Stop was always the secondary
        /// affordance for that reason; this change only narrows the one
        /// case where it used to disagree with what pressing Enter
        /// already did.
        /// </summary>
        internal enum PrimaryAction
        {
            /// <summary>Route the click through <see cref="TrySend"/>.</summary>
            Send,
            /// <summary>Route the click through <see cref="AgentClient.Interrupt"/>.</summary>
            Stop
        }

        /// <summary>
        /// Pure decision seam (unit-tested without a window, same pattern
        /// as <see cref="ResolveEnterAction"/>): Stop only when a turn is
        /// running AND the field is empty; Send in every other case,
        /// including mid-turn with text -- the exact case that used to be
        /// trapped behind a button that only knew how to interrupt. See
        /// <see cref="PrimaryAction"/> for the full justification against
        /// both the "Stop must stay reachable" and "Send must not be a
        /// trap" constraints.
        /// </summary>
        internal static PrimaryAction ResolvePrimaryAction(bool turnActive, bool fieldHasText)
        {
            if (turnActive && !fieldHasText)
            {
                return PrimaryAction.Stop;
            }
            return PrimaryAction.Send;
        }

        private void OnValueChanged(ChangeEvent<string> evt)
        {
            HandleFieldTextChanged(evt.newValue);
        }

        /// <summary>
        /// Programmatic text replacement that behaves exactly like typing:
        /// sets the value WITHOUT the ChangeEvent (SendEvent is a no-op on
        /// a panel-less field, so the event path cannot be relied on) and
        /// runs the same follow-up the change callback would have.
        /// </summary>
        private void SetFieldText(string text)
        {
            text = text ?? string.Empty;
            _field.SetValueWithoutNotify(text);
            HandleFieldTextChanged(text);
        }

        /// <summary>Everything that must track the field's text, whichever way it changed.</summary>
        private void HandleFieldTextChanged(string newValue)
        {
            SessionStateBridge.InputDraft = newValue ?? string.Empty;
            UpdatePlaceholder();
            RefreshSlashPopup();
            UpdateHint();
            // The hub-driven Refresh() path (ChatView's ~60ms coalesced
            // loop) is NOT enough on its own here: it only re-runs when
            // AgentHub fires Changed, and a running subagent can go quiet
            // for seconds between task_progress events (design note
            // 2026-08-03 section 2.2 measured 2.4-4.4s gaps). Without this
            // call the button could keep reading Stop for several seconds
            // after the user had already typed a message meant to send.
            UpdateActionButton();
        }

        /// <summary>
        /// Builds the quick-action GenericMenu from the current
        /// QuickActionStore contents (Settings-only editing, no live
        /// composer editing in v1). A '/' in a label would otherwise start
        /// a GenericMenu submenu -- flattened the same way HeaderView's
        /// model picker already handles server-controlled '/' in model
        /// descriptions.
        /// </summary>
        private void OnQuickActionsClicked()
        {
            List<QuickAction> actions =
                QuickActionStore.CreateDefault(AgentHub.ProjectRoot).Load();
            var menu = new GenericMenu();
            if (actions.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent(L10n.S.ComposerNoQuickActionsMenuItem));
            }
            else
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    QuickAction action = actions[i];
                    if (action == null || string.IsNullOrEmpty(action.prompt))
                    {
                        continue;
                    }
                    string label = string.IsNullOrEmpty(action.label) ? L10n.S.ComposerQuickActionUntitled : action.label;
                    label = label.Replace('/', '-');
                    string prompt = action.prompt;
                    menu.AddItem(new GUIContent(label), false, delegate { InsertText(prompt); });
                }
            }
            menu.DropDown(_quickActionsButton.worldBound);
        }

        private void OnActionClicked()
        {
            if (ResolvePrimaryAction(_turnActive, FieldHasText()) == PrimaryAction.Stop)
            {
                AgentClient client = AgentHub.Client;
                if (client != null)
                {
                    client.Interrupt();
                }
                return;
            }
            TrySend();
        }

        private void TrySend()
        {
            if (!_inputEnabled)
            {
                return;
            }
            string text = (_field.value ?? string.Empty).Trim();
            if (text.Length == 0 && _images.Count == 0)
            {
                return;
            }
            string commandName;
            string commandArgs;
            if (SlashCommandCatalog.TryParse(text, out commandName, out commandArgs))
            {
                SendSlashCommand(text, commandName);
                return;
            }
            string wireText = text;
            System.Collections.Generic.List<ContextAttachment> attachments = null;
            if (_contextBar != null)
            {
                // Chips are consumed exactly once, at the moment the text
                // leaves the composer. The WIRE text carries the delimited
                // context section for the CLI; the structured attachments
                // ride along so the transcript renders compact chips
                // instead of the raw delimiter block -- both survive the
                // CompileGate queue across a domain reload.
                attachments = _contextBar.ConsumeAttachments();
                if (attachments != null && attachments.Count > 0)
                {
                    var payloads = new System.Collections.Generic.List<string>(
                        attachments.Count);
                    for (int i = 0; i < attachments.Count; i++)
                    {
                        payloads.Add(attachments[i].payload);
                    }
                    wireText = ContextBlockFormatter.Compose(text, payloads);
                }
            }
            // Images ride along as content blocks; an image-only message
            // shows a placeholder line in the transcript so the bubble is
            // never empty text.
            System.Collections.Generic.List<ImageAttachment> images = ConsumeImages();
            string displayText = text.Length > 0 ? text
                : (images != null && images.Count > 0 ? L10n.S.ComposerImageOnlyDisplay : text);
            CompileGate.SendOrQueue(wireText, displayText, attachments, images);
            // The field just went from "has text" back to empty. If a
            // turn is still running (the mid-turn send case this whole
            // change is for), the button must revert to Stop now that
            // there is nothing left to send -- SetValueWithoutNotify does
            // not raise OnValueChanged, so nothing else would catch this
            // transition until the next hub-driven Refresh(); the shared
            // tail re-syncs it explicitly.
            ClearFieldAfterSend();
        }

        /// <summary>Trimmed-empty check shared by <see cref="OnActionClicked"/> and
        /// <see cref="UpdateActionButton"/> -- matches the same trim <see cref="TrySend"/>
        /// applies before deciding whether there is anything to send.</summary>
        private bool FieldHasText()
        {
            return !string.IsNullOrEmpty((_field.value ?? string.Empty).Trim()) || _images.Count > 0;
        }

        // -- Slash commands (design note 2026-09-07-slash-commands-and-compaction.md section 1) --

        /// <summary>
        /// A typed "/name args" leaves the composer as-is: the CLI's
        /// stream-json input treats such a user message as the slash
        /// command (Agent SDK "Slash commands"), so the wire text IS the
        /// typed text. Context chips and pending images are deliberately
        /// NOT consumed -- appending a delimited context block or an image
        /// to "/compact" would turn the command into prose; they stay
        /// pending for the next real message. "/clear" never reaches the
        /// CLI: the CLI's own /clear starts a new session id underneath a
        /// panel still showing the old one, so it maps to the panel's New
        /// chat (AgentHub.StartFresh) instead.
        /// </summary>
        private void SendSlashCommand(string text, string commandName)
        {
            HideSlashPopup();
            ClearFieldAfterSend();
            if (SlashCommandCatalog.IsClear(commandName))
            {
                AgentHub.StartFresh();
                return;
            }
            CompileGate.SendOrQueue(text, text, null, null);
        }

        /// <summary>Shared tail of every send: empties the field and re-syncs the widgets that watch it.</summary>
        private void ClearFieldAfterSend()
        {
            _field.SetValueWithoutNotify(string.Empty);
            SessionStateBridge.InputDraft = string.Empty;
            UpdatePlaceholder();
            RefreshSlashPopup();
            UpdateHint();
            UpdateActionButton();
            FocusField();
        }

        /// <summary>
        /// Re-derives the popup from the field text: open (and filtered)
        /// while the WHOLE text is "/" plus a command prefix
        /// (SlashCommandCatalog.TryGetTypedPrefix), closed otherwise. The
        /// highlighted row survives re-filtering when its command is still
        /// listed; else it snaps to the first row.
        /// </summary>
        private void RefreshSlashPopup()
        {
            string prefix;
            string text = _field.value ?? string.Empty;
            if (!_inputEnabled || !SlashCommandCatalog.TryGetTypedPrefix(text, out prefix))
            {
                HideSlashPopup();
                return;
            }
            string previouslySelected = SelectedSlashEntry() != null ? SelectedSlashEntry().name : null;
            _slashPrefix = prefix;
            _slashMatches = SlashCommandCatalog.Filter(AgentHub.SlashCommands, prefix);
            _slashSelected = _slashMatches.Count > 0 ? 0 : -1;
            if (previouslySelected != null)
            {
                for (int i = 0; i < _slashMatches.Count; i++)
                {
                    if (string.Equals(_slashMatches[i].name, previouslySelected, System.StringComparison.OrdinalIgnoreCase))
                    {
                        _slashSelected = i;
                        break;
                    }
                }
            }
            BuildSlashRows();
            _slashPopup.style.display = DisplayStyle.Flex;
            _slashPopupVisible = true;
        }

        private void HideSlashPopup()
        {
            if (!_slashPopupVisible && _slashMatches.Count == 0)
            {
                return;
            }
            _slashPopupVisible = false;
            _slashMatches = new List<SlashCommandEntry>();
            _slashSelected = -1;
            _slashPrefix = string.Empty;
            _slashPopup.Clear();
            _slashPopup.style.display = DisplayStyle.None;
        }

        private void BuildSlashRows()
        {
            _slashPopup.Clear();
            if (_slashMatches.Count == 0)
            {
                var empty = new Label(L10n.S.ComposerSlashNoMatch);
                empty.AddToClassList("uap-slash-empty");
                _slashPopup.Add(empty);
                return;
            }
            for (int i = 0; i < _slashMatches.Count; i++)
            {
                SlashCommandEntry entry = _slashMatches[i];
                int index = i;
                var row = new VisualElement();
                row.AddToClassList("uap-slash-row");
                if (i == _slashSelected)
                {
                    row.AddToClassList("uap-slash-row--selected");
                }
                var name = new Label("/" + entry.name);
                name.AddToClassList("uap-slash-name");
                row.Add(name);
                if (!string.IsNullOrEmpty(entry.argumentHint))
                {
                    var arg = new Label(entry.argumentHint);
                    arg.AddToClassList("uap-slash-arg");
                    row.Add(arg);
                }
                if (!string.IsNullOrEmpty(entry.description))
                {
                    var desc = new Label(entry.description);
                    desc.AddToClassList("uap-slash-desc");
                    desc.tooltip = entry.description;
                    row.Add(desc);
                }
                row.RegisterCallback<ClickEvent>(delegate
                {
                    if (index < _slashMatches.Count)
                    {
                        AcceptSlashEntry(_slashMatches[index]);
                    }
                });
                _slashPopup.Add(row);
            }
        }

        private SlashCommandEntry SelectedSlashEntry()
        {
            if (_slashSelected < 0 || _slashSelected >= _slashMatches.Count)
            {
                return null;
            }
            return _slashMatches[_slashSelected];
        }

        private void MoveSlashSelection(int delta)
        {
            if (_slashMatches.Count == 0)
            {
                return;
            }
            int next = _slashSelected + delta;
            if (next < 0)
            {
                next = _slashMatches.Count - 1;
            }
            else if (next >= _slashMatches.Count)
            {
                next = 0;
            }
            _slashSelected = next;
            BuildSlashRows();
        }

        /// <summary>
        /// Puts "/name " into the field (SlashCommandCatalog.CompleteText):
        /// the trailing space leaves prefix mode, so the popup closes
        /// through the ordinary value-changed path, and the caret lands
        /// after it ready for arguments.
        /// </summary>
        private void AcceptSlashEntry(SlashCommandEntry entry)
        {
            string completed = SlashCommandCatalog.CompleteText(entry);
            if (completed.Length == 0)
            {
                return;
            }
            SetFieldText(completed);
            // Both indices move, as in InsertNewlineAtCaret. Skipped when
            // the field is not attached to a panel (EditMode tests).
            if (_field.panel != null)
            {
                _field.SelectRange(completed.Length, completed.Length);
            }
            FocusField();
        }

        /// <summary>
        /// Keys the open popup owns. Returns true when the event was
        /// consumed (swallowed) so OnKeyDown's ordinary paths do not also
        /// see it. Every consumed key also breaks the Enter keycode->char
        /// bridge, same as any other non-Enter key.
        /// </summary>
        private bool TryHandleSlashPopupKey(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                HideSlashPopup();
                UpdateHint();
            }
            else if (evt.keyCode == KeyCode.UpArrow)
            {
                MoveSlashSelection(-1);
            }
            else if (evt.keyCode == KeyCode.DownArrow)
            {
                MoveSlashSelection(1);
            }
            else if (evt.keyCode == KeyCode.Tab)
            {
                SlashCommandEntry selected = SelectedSlashEntry();
                if (selected == null)
                {
                    return false;
                }
                _swallowNextTabChar = true;
                AcceptSlashEntry(selected);
            }
            else
            {
                return false;
            }
            _pendingEnterAction = EnterAction.Unknown;
            evt.StopPropagation();
            evt.PreventDefault();
            return true;
        }

        // -- Image attachments (design note 2026-09-07 section 2.3) ---------------------

        /// <summary>Pending images, oldest first (read-only view for tests/callers).</summary>
        public System.Collections.Generic.IList<ImageAttachment> PendingImages
        {
            get { return _images.AsReadOnly(); }
        }

        /// <summary>
        /// Adds an encoded attachment to the strip. Refused past
        /// ImageAttachmentPolicy.MaxPerMessage (the hint says so). Returns
        /// false when refused.
        /// </summary>
        public bool AddImage(ImageAttachment image)
        {
            if (image == null || string.IsNullOrEmpty(image.path))
            {
                return false;
            }
            if (_images.Count >= ImageAttachmentPolicy.MaxPerMessage)
            {
                ShowAttachError(L10n.F(L10n.S.AttachImageTooManyFmt, ImageAttachmentPolicy.MaxPerMessage));
                return false;
            }
            _images.Add(image);
            PersistPendingImages();
            RebuildAttachStrip();
            UpdateActionButton();
            return true;
        }

        /// <summary>Encodes and attaches image files (drops, the file picker).</summary>
        public void AddImageFiles(System.Collections.Generic.IList<string> paths)
        {
            if (paths == null)
            {
                return;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                ImageAttachment image;
                string error;
                if (ImageAttachmentEncoder.TryImportFile(paths[i], out image, out error))
                {
                    if (!AddImage(image))
                    {
                        break;
                    }
                }
                else
                {
                    ShowAttachError(error);
                }
            }
        }

        /// <summary>Takes the pending images off the strip (the send consumed them). Null when none.</summary>
        public System.Collections.Generic.List<ImageAttachment> ConsumeImages()
        {
            if (_images.Count == 0)
            {
                return null;
            }
            var taken = new System.Collections.Generic.List<ImageAttachment>(_images);
            _images.Clear();
            PersistPendingImages();
            RebuildAttachStrip();
            return taken;
        }

        private void RemoveImage(ImageAttachment image)
        {
            if (_images.Remove(image))
            {
                PersistPendingImages();
                RebuildAttachStrip();
                UpdateActionButton();
            }
        }

        private void RebuildAttachStrip()
        {
            _attachStrip.Clear();
            _attachStrip.style.display = _images.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            for (int i = 0; i < _images.Count; i++)
            {
                ImageAttachment image = _images[i];
                var item = new VisualElement();
                item.AddToClassList("uap-attach-thumb");
                Texture2D texture = ImageThumbnailCache.Get(image.path);
                if (texture != null)
                {
                    var thumb = new Image { image = texture, scaleMode = ScaleMode.ScaleToFit };
                    thumb.AddToClassList("uap-attach-thumb-image");
                    item.Add(thumb);
                }
                var caption = new Label(string.IsNullOrEmpty(image.sourceName) ? L10n.S.ChatImageDefaultCaption : image.sourceName);
                caption.enableRichText = false;
                caption.AddToClassList("uap-attach-thumb-caption");
                item.tooltip = caption.text + " (" + image.width + "\u00D7" + image.height + ", " + (image.bytes / 1024) + " KB)";
                item.Add(caption);
                var remove = new Button(delegate { RemoveImage(image); });
                remove.text = IconLoader.GlyphX;
                remove.tooltip = L10n.S.AttachImageRemoveTooltip;
                remove.AddToClassList("uap-ctx-chip-x");
                item.Add(remove);
                _attachStrip.Add(item);
            }
        }

        private void PersistPendingImages()
        {
            if (_images.Count == 0)
            {
                SessionStateBridge.ComposerImagesJson = string.Empty;
                return;
            }
            JsonNode array = JsonNode.NewArray();
            for (int i = 0; i < _images.Count; i++)
            {
                array.Add(_images[i].ToJson());
            }
            SessionStateBridge.ComposerImagesJson = JsonWriter.Write(array);
        }

        private void RestorePendingImages()
        {
            string json = SessionStateBridge.ComposerImagesJson;
            if (string.IsNullOrEmpty(json))
            {
                return;
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(json, out node, out error) || !node.IsArray)
            {
                SessionStateBridge.ComposerImagesJson = string.Empty;
                return;
            }
            foreach (JsonNode item in node.Items)
            {
                ImageAttachment image = ImageAttachment.FromJson(item);
                if (image != null && System.IO.File.Exists(image.path) && _images.Count < ImageAttachmentPolicy.MaxPerMessage)
                {
                    _images.Add(image);
                }
            }
            RebuildAttachStrip();
        }

        private void ShowAttachError(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            Debug.LogWarning("[AgentPanel] " + message);
            EditorUtility.DisplayDialog(L10n.S.AttachImageErrorTitle, message, L10n.S.AttachImageErrorOk);
        }

        private void OnAttachClicked()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L10n.S.AttachMenuImageFile), false, OnAttachImageFile);
            menu.AddItem(new GUIContent(L10n.S.AttachMenuSceneView), false, delegate { AttachView(UapScreenshotView.Scene, UapScreenshotCapture.Camera); });
            menu.AddItem(new GUIContent(L10n.S.AttachMenuGameView), false, delegate { AttachView(UapScreenshotView.Game, UapScreenshotCapture.Camera); });
            menu.AddItem(new GUIContent(L10n.S.AttachMenuSceneViewWindow), false, delegate { AttachView(UapScreenshotView.Scene, UapScreenshotCapture.Window); });
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L10n.S.AttachMenuClipboard), false, delegate { TryPasteClipboardImage(true); });
            menu.DropDown(_attachButton.worldBound);
        }

        /// <summary>
        /// Attaches the clipboard image (or copied image files). Returns
        /// true when something was attached. <paramref name="explicitAction"/>
        /// (the menu item) reports an empty clipboard or a failure in a
        /// dialog; the Ctrl/Cmd+V path stays silent on empty so a plain
        /// paste never nags, and only logs a failure.
        /// </summary>
        internal bool TryPasteClipboardImage(bool explicitAction)
        {
            if (!explicitAction && !ClipboardImageReader.ShouldTryImage(EditorGUIUtility.systemCopyBuffer))
            {
                return false;
            }
            ClipboardImageResult read = ClipboardImageReader.Read();
            if (read.HasImage)
            {
                int before = _images.Count;
                AddImageFiles(new[] { read.ImagePath });
                try { System.IO.File.Delete(read.ImagePath); } catch (System.Exception) { }
                return _images.Count > before;
            }
            if (read.FilePaths.Count > 0)
            {
                System.Collections.Generic.List<string> files = ClipboardImageReader.FilterImageFiles(read.FilePaths);
                if (files.Count > 0)
                {
                    int before = _images.Count;
                    AddImageFiles(files);
                    return _images.Count > before;
                }
            }
            if (!string.IsNullOrEmpty(read.Error))
            {
                string message = L10n.F(L10n.S.AttachClipboardFailedFmt, read.Error);
                if (explicitAction)
                {
                    ShowAttachError(message);
                }
                else
                {
                    Debug.LogWarning("[AgentPanel] " + message);
                }
            }
            else if (explicitAction)
            {
                ShowAttachError(L10n.S.AttachClipboardEmpty);
            }
            return false;
        }

        private void OnAttachImageFile()
        {
            string path = EditorUtility.OpenFilePanelWithFilters(L10n.S.AttachMenuImageFile, string.Empty,
                new[] { "Image", "png,jpg,jpeg" });
            if (!string.IsNullOrEmpty(path))
            {
                AddImageFiles(new[] { path });
            }
        }

        private void AttachView(UapScreenshotView view, UapScreenshotCapture capture)
        {
            ImageAttachment image;
            string error;
            if (ImageAttachmentEncoder.TryCaptureView(view, capture, out image, out error))
            {
                AddImage(image);
            }
            else
            {
                ShowAttachError(error);
            }
        }

        // -- Presentation ---------------------------------------------------------------

        /// <summary>
        /// Applies <see cref="ResolvePrimaryAction"/> to the button's
        /// label, stop-style class and enabled state. Called from
        /// <see cref="Refresh"/> (turn/input state changes, driven by
        /// AgentHub.Changed on ChatView's ~60ms coalesced loop),
        /// <see cref="OnValueChanged"/> (every keystroke) and
        /// <see cref="TrySend"/> (the field clearing itself without a
        /// keystroke) -- see each call site for why the hub-driven path
        /// alone is not sufficient.
        /// </summary>
        private void UpdateActionButton()
        {
            bool isStop = ResolvePrimaryAction(_turnActive, FieldHasText()) == PrimaryAction.Stop;
            _action.text = isStop ? L10n.S.ComposerStopButton : L10n.S.ComposerSendButton;
            // UXO-8: a per-state tooltip so the button's CURRENT meaning is
            // inspectable, not inferred from a two-character label.
            _action.tooltip = isStop ? L10n.S.ComposerStopTooltip : L10n.S.ComposerSendTooltip;
            _action.SetEnabled(isStop || _inputEnabled);
            if (isStop != _stopStyleApplied)
            {
                _stopStyleApplied = isStop;
                _action.EnableInClassList("uap-send-btn--stop", isStop);
            }
        }

        private void UpdatePlaceholder()
        {
            bool show = string.IsNullOrEmpty(_field.value) && _inputEnabled;
            _placeholder.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
            {
                if (_pendingPermissionHint)
                {
                    // UXA-4: while a permission request is pending, the
                    // composer doubles as the deny-reason entry (spec
                    // section 3.7) -- announce it where the eye already is.
                    _placeholder.text = L10n.S.ComposerPlaceholderPermissionPending;
                    return;
                }
                PanelSettings settings = PanelStateStore.instance.Settings;
                _placeholder.text = L10n.A(settings.ctrlEnterToSend
                    ? L10n.S.ComposerPlaceholderCtrlEnter
                    : L10n.S.ComposerPlaceholderEnter);
            }
        }

        /// <summary>
        /// UXA-4: flips the empty-field placeholder to the deny-reason
        /// hint while a can_use_tool request is pending. ChatView drives
        /// this from its refresh, so the hint tracks the pending request's
        /// lifetime, not any composer-local guess.
        /// </summary>
        public void SetPendingPermissionHint(bool pending)
        {
            if (_pendingPermissionHint == pending)
            {
                return;
            }
            _pendingPermissionHint = pending;
            UpdatePlaceholder();
        }

        /// <summary>
        /// UXA-4: consume-on-read of the composer text for a deny message.
        /// Clears the field on a non-blank read so the same words cannot
        /// ALSO be sent as a chat message by a later Enter -- one entry,
        /// one use. Blank text is returned as-is (nothing to clear).
        /// </summary>
        internal string ConsumeTextForDeny()
        {
            string text = (_field.value ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                _field.value = string.Empty;
            }
            return text;
        }

        internal string PlaceholderTextForTests
        {
            get { return _placeholder.text; }
        }

        private void UpdateHint()
        {
            if (_slashPopupVisible)
            {
                _hint.text = L10n.S.ComposerSlashHint;
                return;
            }
            if (EditorApplication.isCompiling)
            {
                _hint.text = L10n.S.ComposerHintCompiling;
                return;
            }
            int pending = CompileGate.PendingCount;
            if (pending > 0)
            {
                _hint.text = L10n.F(L10n.S.ComposerHintQueuedFmt, pending);
                return;
            }
            _hint.text = DescribeActionHint(_turnActive, FieldHasText());
        }

        /// <summary>
        /// UXO-8: the primary button flips Send&lt;-&gt;Stop with the
        /// field's emptiness while a turn runs (ResolvePrimaryAction --
        /// deliberate, test-pinned, kept). The flip itself was silent: the
        /// hint under the field now narrates the CURRENT meaning, so
        /// mid-turn typing that turns Stop into Send is announced ("Enter
        /// appends to the running turn - Esc stops") instead of only the
        /// button label changing in the user's peripheral vision. Pure for
        /// the four-way table test.
        /// </summary>
        internal static string DescribeActionHint(bool turnActive, bool fieldHasText)
        {
            if (!turnActive)
            {
                return string.Empty;
            }
            return fieldHasText
                ? L10n.S.ComposerHintTurnSendAndStop
                : L10n.S.ComposerHintEscToStop;
        }
    }
}
