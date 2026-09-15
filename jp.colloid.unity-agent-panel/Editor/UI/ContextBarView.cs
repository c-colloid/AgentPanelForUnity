using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops.Markers;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Slim chip strip between the message list and the composer
    /// (R05 section 4.1): an attach-selection button, a scene chip
    /// toggle, chips for objects dropped from Project/Hierarchy, and an
    /// automatic Console-error chip with a one-click "Ask Claude to fix"
    /// action. Chips are removable and serialize into the outgoing
    /// message as delimited context blocks (ContextBlockFormatter) when
    /// the composer sends. Refreshes are event-driven only
    /// (Selection.selectionChanged / ConsoleErrorProvider.Changed) --
    /// nothing here runs on a per-frame tick.
    /// </summary>
    public sealed class ContextBarView
    {
        private enum ChipKind
        {
            Selection,
            Scene,
            DroppedObject,
            /// <summary>A user pin (design note 2026-09-07 section 1.3.4); the X also removes the pin.</summary>
            Marker
        }

        private sealed class Chip
        {
            public ChipKind Kind;
            /// <summary>Attachment title shown in the transcript chip row.</summary>
            public string Title;
            /// <summary>Fixed context block captured at attach time.</summary>
            public string Payload;
            /// <summary>Late-bound payload (scene chip: fresh at send).</summary>
            public Func<string> PayloadProvider;
            /// <summary>Object to ping when the chip is clicked (optional).</summary>
            public UnityEngine.Object Target;
            public VisualElement Element;
            /// <summary>SceneMarkerStore id for Marker chips (0 otherwise).</summary>
            public int MarkerId;
        }

        private const int MaxChips = 8;
        private const int MaxChipLabelChars = 24;

        /// <summary>How long the post-ignore undo notice stays up (UXO-3).</summary>
        private const long IgnoreNoticeMillis = 10000;

        // Glyphs come from IconLoader -- the single guarded registry of
        // runtime-constructed UI glyphs (GlyphAuditTests).

        private readonly VisualElement _root;
        private readonly Button _attachButton;
        private readonly Button _fixButton;
        private readonly Button _sceneButton;
        private readonly VisualElement _chipHost;
        private readonly VisualElement _errorChip;
        private readonly Label _errorLabel;
        /// <summary>
        /// "n scene markers" chip (design note 2026-09-07 section 1.3.5):
        /// shown while the agent has markers on screen so the user always
        /// has a one-click way to take them off; the X clears the agent's
        /// markers only (the user's pins are theirs). Read-only view over
        /// SceneMarkerStore -- no state of its own.
        /// </summary>
        private readonly VisualElement _markersChip;
        private readonly Label _markersLabel;
        /// <summary>Panel-side pin button: the fallback/twin of the Scene-view toolbar toggle.</summary>
        private readonly Button _pinButton;
        private readonly VisualElement _ignoreNotice;
        private readonly Label _ignoreNoticeLabel;
        private IVisualElementScheduledItem _ignoreNoticeHide;
        private List<string> _lastIgnoredMessages;
        private readonly List<Chip> _chips = new List<Chip>();

        private bool _active;
        private bool _barEnabled = true;

        /// <summary>
        /// Content-keyed replacement for the old count-based
        /// _errorDismissedAtCount (docs/design-notes/2026-08-13-error-
        /// chip-ignore.md "Measured root cause": a count is a proxy that
        /// cannot express "this error does not need fixing" and gets
        /// un-dismissed by an unrelated count DROP, e.g. OnCompilationStarted
        /// removing superseded compiler entries). The fix button records the
        /// exact Message of every entry it just sent here -- never
        /// persisted, so a fix that fails and leaves the same error present
        /// after a reload shows the chip again (this set does not survive a
        /// domain reload; PanelSettings.ignoredConsoleErrors, the X
        /// button's store, is the persistent mechanism). Ordinal comparer
        /// to match ConsoleErrorIgnoreFilter's own comparison basis.
        /// </summary>
        private readonly HashSet<string> _acknowledgedErrorMessages =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// UXO-3: what the error chip's bare X means now -- "close for
        /// now", NOT the permanent PanelSettings ignore (that moved to the
        /// chip's explicit menu). Same transient, same-content-keyed
        /// semantics as _acknowledgedErrorMessages: never persisted, does
        /// not survive a domain reload, and a NEW error message shows the
        /// chip again immediately. Reflexively closing the chip can no
        /// longer permanently silence a real error.
        /// </summary>
        private readonly HashSet<string> _dismissedForNowErrorMessages =
            new HashSet<string>(StringComparer.Ordinal);

        public VisualElement Root
        {
            get { return _root; }
        }

        public ContextBarView()
        {
            _root = new VisualElement();
            _root.AddToClassList("uap-ctxbar");

            _attachButton = new Button(OnAttachSelectionClicked);
            _attachButton.text = L10n.S.CtxAttachSelectionButton;
            _attachButton.tooltip = L10n.S.CtxAttachSelectionTooltip;
            _attachButton.AddToClassList("uap-ctx-attach");
            _root.Add(_attachButton);

            _sceneButton = new Button(OnSceneToggleClicked);
            _sceneButton.text = L10n.S.CtxAttachSceneButton;
            _sceneButton.tooltip = L10n.S.CtxAttachSceneTooltip;
            _sceneButton.AddToClassList("uap-ctx-attach");
            _root.Add(_sceneButton);

            _pinButton = new Button(OnPinButtonClicked);
            _pinButton.tooltip = L10n.S.CtxPinTooltip;
            _pinButton.AddToClassList("uap-ctx-attach");
            _root.Add(_pinButton);
            UpdatePinButton();

            _chipHost = new VisualElement();
            _chipHost.AddToClassList("uap-ctx-chiphost");
            _root.Add(_chipHost);

            // Automatic Console-error chip (visible only while errors exist).
            _errorChip = new VisualElement();
            _errorChip.AddToClassList("uap-ctx-chip");
            _errorChip.AddToClassList("uap-ctx-chip--error");
            // --error, not --warn (2026-08-14 token-hygiene note): this
            // glyph rides an ERROR-severity chip and resolves to
            // --uap-status-error; it was the one warn-NAMED class in the
            // stylesheet whose color said error, and the name was the lie.
            _errorChip.Add(IconLoader.CreateIcon("d_console.erroricon.sml", IconLoader.GlyphWarn,
                "uap-ctx-chip-icon", "uap-ctx-chip-glyph--error"));
            _errorLabel = new Label(string.Empty);
            // Defense in depth: the text here is a trusted count string,
            // but every chip-strip Label stays plain (TextElement defaults
            // to enableRichText = TRUE).
            _errorLabel.enableRichText = false;
            _errorLabel.AddToClassList("uap-ctx-chip-label");
            _errorChip.Add(_errorLabel);
            _fixButton = new Button(OnFixErrorsClicked);
            _fixButton.text = L10n.A(L10n.S.CtxFixErrorsButton);
            _fixButton.AddToClassList("uap-ctx-fixbtn");
            _errorChip.Add(_fixButton);
            // UXO-3: two separated intents. The menu holds the PERMANENT
            // ignore (an explicit, named action); the bare X below is only
            // "close for now" and can no longer silently persist anything.
            var errorMenu = new Button(OnErrorChipMenuClicked);
            errorMenu.text = IconLoader.GlyphChevronDown;
            errorMenu.tooltip = L10n.S.CtxErrorMenuTooltip;
            errorMenu.AddToClassList("uap-ctx-chip-x");
            _errorChip.Add(errorMenu);
            Button errorClose = MakeRemoveButton(OnErrorChipDismissed);
            errorClose.tooltip = L10n.S.CtxErrorDismissForNowTooltip;
            _errorChip.Add(errorClose);
            _errorChip.style.display = DisplayStyle.None;
            _root.Add(_errorChip);

            _markersChip = new VisualElement();
            _markersChip.AddToClassList("uap-ctx-chip");
            _markersChip.Add(IconLoader.CreateIcon("d_Transform Icon", IconLoader.GlyphBullet,
                "uap-ctx-chip-icon", "uap-ctx-chip-glyph"));
            _markersLabel = new Label(string.Empty);
            _markersLabel.enableRichText = false;
            _markersLabel.AddToClassList("uap-ctx-chip-label");
            _markersChip.Add(_markersLabel);
            Button markersClear = MakeRemoveButton(OnMarkersChipClearClicked);
            markersClear.tooltip = L10n.S.CtxMarkersClearTooltip;
            _markersChip.Add(markersClear);
            _markersChip.style.display = DisplayStyle.None;
            _root.Add(_markersChip);

            // UXO-3: transient undo notice shown right after a permanent
            // ignore -- the one moment the user can reverse it in place
            // (the other path stays Settings' management list).
            _ignoreNotice = new VisualElement();
            _ignoreNotice.AddToClassList("uap-ctx-chip");
            _ignoreNoticeLabel = new Label(string.Empty);
            _ignoreNoticeLabel.enableRichText = false;
            _ignoreNoticeLabel.AddToClassList("uap-ctx-chip-label");
            _ignoreNotice.Add(_ignoreNoticeLabel);
            var undoButton = new Button(OnIgnoreUndoClicked);
            undoButton.text = L10n.S.CtxErrorIgnoreUndoButton;
            undoButton.AddToClassList("uap-ctx-fixbtn");
            _ignoreNotice.Add(undoButton);
            _ignoreNotice.Add(MakeRemoveButton(HideIgnoreNotice));
            _ignoreNotice.style.display = DisplayStyle.None;
            _root.Add(_ignoreNotice);

            UpdateAttachButton();
            UpdateSceneButton();
        }

        // -- Lifecycle (called by ChatView) -------------------------------------

        public void OnActivate()
        {
            if (_active)
            {
                return;
            }
            _active = true;
            Selection.selectionChanged += OnSelectionChanged;
            ConsoleErrorProvider.Changed += OnErrorsChanged;
            SceneMarkerStore.Changed += OnMarkersChanged;
            SceneMarkerPin.ArmedChanged += UpdatePinButton;
            UpdateAttachButton();
            UpdateErrorChip();
            OnMarkersChanged();
            UpdatePinButton();
        }

        public void OnDeactivate()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            Selection.selectionChanged -= OnSelectionChanged;
            ConsoleErrorProvider.Changed -= OnErrorsChanged;
            SceneMarkerStore.Changed -= OnMarkersChanged;
            SceneMarkerPin.ArmedChanged -= UpdatePinButton;
            // The undo notice is meaningful only in the moment it appeared;
            // a view switch ends that moment (and parks the timer).
            HideIgnoreNotice();
        }

        /// <summary>Mirrors the chat enable state (first-run cards disable it).</summary>
        public void Refresh(bool enabled)
        {
            // The agent name follows the selected backend (L10n.AgentName).
            string fixText = L10n.A(L10n.S.CtxFixErrorsButton);
            if (_fixButton.text != fixText)
            {
                _fixButton.text = fixText;
            }
            if (_barEnabled == enabled)
            {
                return;
            }
            _barEnabled = enabled;
            _root.SetEnabled(enabled);
        }

        // -- Composer integration ---------------------------------------------------

        /// <summary>
        /// Returns the context attachments (title + payload) for the
        /// outgoing message and clears the attached chips (they were
        /// consumed by this send). The composer builds the WIRE text from
        /// the payloads (ContextBlockFormatter.Compose) and stores these
        /// structured attachments for the transcript display. The error
        /// chip is NOT included -- it is its own one-click action.
        /// </summary>
        public List<ContextAttachment> ConsumeAttachments()
        {
            if (_chips.Count == 0)
            {
                return null;
            }
            var attachments = new List<ContextAttachment>(_chips.Count);
            for (int i = 0; i < _chips.Count; i++)
            {
                Chip chip = _chips[i];
                string payload = chip.PayloadProvider != null
                    ? chip.PayloadProvider() : chip.Payload;
                if (!string.IsNullOrEmpty(payload))
                {
                    attachments.Add(new ContextAttachment(chip.Title, payload));
                }
            }
            for (int i = _chips.Count - 1; i >= 0; i--)
            {
                RemoveChip(_chips[i]);
            }
            return attachments;
        }

        /// <summary>Chips for objects dropped onto the composer area.</summary>
        public void AddObjectChips(UnityEngine.Object[] objects)
        {
            if (objects == null)
            {
                return;
            }
            for (int i = 0; i < objects.Length; i++)
            {
                UnityEngine.Object obj = objects[i];
                if (obj == null || _chips.Count >= MaxChips)
                {
                    break;
                }
                string payload = SelectionContextProvider.DescribeObject(obj);
                if (string.IsNullOrEmpty(payload))
                {
                    continue;
                }
                AddChip(ChipKind.DroppedObject, ShortLabel(obj.name),
                    BuildObjectTitle(obj), payload, null, obj);
            }
        }

        // -- Button handlers --------------------------------------------------------

        private void OnAttachSelectionClicked()
        {
            if (_chips.Count >= MaxChips)
            {
                return;
            }
            string payload = SelectionContextProvider.BuildSummary();
            if (string.IsNullOrEmpty(payload))
            {
                return;
            }
            string label = SelectionContextProvider.BuildChipLabel();
            AddChip(ChipKind.Selection, label, L10n.F(L10n.S.CtxSelectionChipTitleFmt, label),
                payload, null, Selection.activeObject);
        }

        private void OnSceneToggleClicked()
        {
            Chip existing = FindChip(ChipKind.Scene);
            if (existing != null)
            {
                RemoveChip(existing);
                return;
            }
            if (_chips.Count >= MaxChips)
            {
                return;
            }
            // Late-bound payload: the scene may change between attach and
            // send, so the summary is computed when the message goes out.
            string sceneLabel = L10n.F(L10n.S.CtxSceneChipLabelFmt, ShortLabel(SceneContextProvider.ActiveSceneName));
            AddChip(ChipKind.Scene, sceneLabel, sceneLabel,
                null, SceneContextProvider.BuildSummary, null);
        }

        private void OnFixErrorsClicked()
        {
            // Send EXACTLY the set the chip is counting (docs/design-notes/
            // 2026-09-07-scene-markers-image-attachments-error-chip.md
            // section 3): visible minus acknowledged minus dismissed-for-now.
            // This used to be FormatDigest() over ALL visible entries, so
            // already-sent and X-closed errors went out again, the title
            // claimed the wrong count, and -- because the digest keeps the
            // OLDEST ten -- a new error behind ten acknowledged ones was
            // never sent while the chip kept saying "1 console error".
            List<ConsoleErrorProvider.Entry> sendable = SelectSendable(
                ConsoleErrorProvider.VisibleSnapshot(),
                _acknowledgedErrorMessages, _dismissedForNowErrorMessages);
            if (sendable.Count == 0)
            {
                return;
            }
            string digest = ConsoleErrorProvider.FormatDigest(sendable,
                ConsoleErrorProvider.DefaultDigestEntries);
            if (string.IsNullOrEmpty(digest))
            {
                return;
            }
            string prompt = L10n.S.CtxFixErrorsPrompt;
            int count = sendable.Count;
            string title = count == 1
                ? L10n.S.CtxConsoleErrorTitleSingle
                : L10n.F(L10n.S.CtxConsoleErrorTitlePluralFmt, count);
            // Wire text keeps the full delimited compose; the transcript
            // shows the prompt plus a collapsed attachment chip.
            CompileGate.SendOrQueue(
                ContextBlockFormatter.Compose(prompt, new List<string> { digest }),
                prompt,
                new List<ContextAttachment> { new ContextAttachment(title, digest) });
            AcknowledgeSentMessages(sendable, _acknowledgedErrorMessages);
            UpdateErrorChip();
        }

        /// <summary>
        /// The X button (UXO-3): hides every currently VISIBLE error for
        /// NOW -- transient, content-keyed, never persisted (see
        /// _dismissedForNowErrorMessages). It used to call the permanent
        /// PanelSettings ignore, which made a reflexive close -- rendered
        /// identically to the harmless attachment-chip X -- silence real
        /// errors forever with no visible undo. The permanent ignore is
        /// still available, as the explicit menu action below.
        /// </summary>
        private void OnPinButtonClicked()
        {
            SceneMarkerPin.Armed = !SceneMarkerPin.Armed;
        }

        private void UpdatePinButton()
        {
            bool armed = SceneMarkerPin.Armed;
            _pinButton.text = armed ? L10n.S.CtxPinButtonArmed : L10n.S.CtxPinButton;
            _pinButton.EnableInClassList("uap-ctx-attach--on", armed);
        }

        /// <summary>
        /// Keeps the pin chips in step with the store: a chip for every
        /// user pin that has none yet (dropped from the Scene-view overlay
        /// while the panel was hidden, or restored after a reload), and no
        /// chip for a pin that is gone. Payloads come from the marker's
        /// Note, captured at placement. Sending consumes the chips but
        /// leaves the pins on screen; the chip's X removes the pin too.
        /// </summary>
        private void OnMarkersChanged()
        {
            UpdateMarkersChip();
            SceneMarker[] markers = SceneMarkerStore.Snapshot();
            for (int i = _chips.Count - 1; i >= 0; i--)
            {
                Chip chip = _chips[i];
                if (chip.Kind == ChipKind.Marker && chip.MarkerId > 0 && SceneMarkerStore.Find(chip.MarkerId) == null)
                {
                    RemoveChip(chip);
                }
            }
            for (int i = 0; i < markers.Length; i++)
            {
                SceneMarker pin = markers[i];
                if (pin.Origin != SceneMarkerOrigin.User || pin.Id <= 0 || FindMarkerChip(pin.Id) != null)
                {
                    continue;
                }
                if (_chips.Count >= MaxChips)
                {
                    break;
                }
                int number = pin.PinNumber > 0 ? pin.PinNumber : pin.Id;
                AddChip(ChipKind.Marker, L10n.F(L10n.S.CtxPinChipLabelFmt, number),
                    L10n.F(L10n.S.CtxPinChipTitleFmt, number), pin.Note, null, null, pin.Id);
            }
        }

        private Chip FindMarkerChip(int markerId)
        {
            for (int i = 0; i < _chips.Count; i++)
            {
                if (_chips[i].Kind == ChipKind.Marker && _chips[i].MarkerId == markerId)
                {
                    return _chips[i];
                }
            }
            return null;
        }

        private void OnMarkersChipClearClicked()
        {
            // Everything, pins included: this chip is the user's one-click
            // way to get a clean Scene view, whatever put things there.
            SceneMarkerStore.Clear(true);
        }

        private void UpdateMarkersChip()
        {
            if (_markersChip.panel == null)
            {
                return;
            }
            int count = SceneMarkerStore.Count;
            _markersChip.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (count > 0)
            {
                _markersLabel.text = count == 1
                    ? L10n.S.CtxMarkersChipSingle : L10n.F(L10n.S.CtxMarkersChipPluralFmt, count);
            }
        }

        private void OnErrorChipDismissed()
        {
            DismissVisibleForNow(
                ConsoleErrorProvider.VisibleSnapshot(), _dismissedForNowErrorMessages);
            UpdateErrorChip();
        }

        /// <summary>
        /// UXO-3: the chip's menu -- home of the PERMANENT ignore, now an
        /// explicit named action instead of the bare X's hidden meaning.
        /// </summary>
        private void OnErrorChipMenuClicked()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L10n.S.CtxErrorIgnoreForeverMenuItem), false,
                OnErrorIgnoreForeverChosen);
            menu.ShowAsContext();
        }

        private void OnErrorIgnoreForeverChosen()
        {
            // The whole persistence step lives in the provider (2026-08-13
            // design note) so ConsoleErrorVisibilityTests can exercise the
            // REAL production path through the settings seam instead of a
            // copy of it. The explicit refresh below is belt-and-suspenders
            // -- IgnoreCurrentlyVisible raises Changed, which also lands in
            // OnErrorsChanged while this chip is attached.
            List<string> ignored = ConsoleErrorProvider.IgnoreCurrentlyVisible();
            UpdateErrorChip();
            if (ignored.Count > 0)
            {
                ShowIgnoreNotice(ignored);
            }
        }

        private void ShowIgnoreNotice(List<string> ignored)
        {
            _lastIgnoredMessages = ignored;
            _ignoreNoticeLabel.text = ignored.Count == 1
                ? L10n.S.CtxErrorIgnoredNoticeSingle
                : L10n.F(L10n.S.CtxErrorIgnoredNoticePluralFmt, ignored.Count);
            _ignoreNotice.style.display = DisplayStyle.Flex;
            // Ephemeral: quietly gone after a while (undo stays reachable
            // through Settings' management list); any newer notice cancels
            // the previous timer by replacing it.
            if (_ignoreNoticeHide != null)
            {
                _ignoreNoticeHide.Pause();
            }
            _ignoreNoticeHide = _root.schedule.Execute(HideIgnoreNotice)
                .StartingIn(IgnoreNoticeMillis);
        }

        private void HideIgnoreNotice()
        {
            _ignoreNotice.style.display = DisplayStyle.None;
            _lastIgnoredMessages = null;
            if (_ignoreNoticeHide != null)
            {
                _ignoreNoticeHide.Pause();
                _ignoreNoticeHide = null;
            }
        }

        private void OnIgnoreUndoClicked()
        {
            List<string> toRestore = _lastIgnoredMessages;
            HideIgnoreNotice();
            if (toRestore != null)
            {
                ConsoleErrorProvider.Unignore(toRestore);
                UpdateErrorChip();
            }
        }

        // -- Provider events (event-driven refresh only) ------------------------------

        private void OnSelectionChanged()
        {
            UpdateAttachButton();
        }

        private void OnErrorsChanged()
        {
            // Content-keyed visibility (CountUnacknowledgedVisible, called
            // from UpdateErrorChip) means there is nothing to recompute
            // here beyond a plain refresh -- unlike the old count-based
            // _errorDismissedAtCount, a changed entry set can only ever
            // shrink or grow the unacknowledged set correctly on its own;
            // it never needs an explicit "un-dismiss" step.
            UpdateErrorChip();
        }

        // -- Chip plumbing ---------------------------------------------------------------

        private void AddChip(ChipKind kind, string label, string title,
            string payload, Func<string> payloadProvider, UnityEngine.Object target, int markerId = 0)
        {
            var chip = new Chip
            {
                Kind = kind,
                Title = string.IsNullOrEmpty(title) ? label : title,
                Payload = payload,
                PayloadProvider = payloadProvider,
                Target = target,
                MarkerId = markerId
            };

            var element = new VisualElement();
            element.AddToClassList("uap-ctx-chip");
            element.Add(MakeChipIcon(kind, target));
            var text = new Label(string.IsNullOrEmpty(label) ? L10n.S.CtxDefaultChipLabel : label);
            // SECURITY: chip labels carry asset/GameObject names, which the
            // agent can create or rename (model-writable text). They never
            // pass through the InlineMarkupConverter chokepoint, so rich
            // text must stay OFF (TextElement defaults it to TRUE).
            text.enableRichText = false;
            text.AddToClassList("uap-ctx-chip-label");
            element.Add(text);
            Button remove = MakeRemoveButton(delegate
            {
                RemoveChip(chip);
                if (chip.Kind == ChipKind.Marker && chip.MarkerId > 0)
                {
                    SceneMarkerStore.Remove(chip.MarkerId);
                }
            });
            element.Add(remove);
            if (target != null)
            {
                element.RegisterCallback<ClickEvent>(delegate(ClickEvent evt)
                {
                    // The remove X is a child of this chip: its click
                    // bubbles through the chip root, and pinging the
                    // object while removing the chip would be a surprise.
                    var origin = evt.target as VisualElement;
                    if (origin != null && remove.Contains(origin))
                    {
                        return;
                    }
                    if (chip.Target != null)
                    {
                        EditorGUIUtility.PingObject(chip.Target);
                    }
                });
                element.tooltip = L10n.S.CtxPingTooltip;
            }

            chip.Element = element;
            _chips.Add(chip);
            _chipHost.Add(element);
            if (kind == ChipKind.Scene)
            {
                UpdateSceneButton();
            }
        }

        private void RemoveChip(Chip chip)
        {
            _chips.Remove(chip);
            if (chip.Element != null && chip.Element.parent != null)
            {
                chip.Element.parent.Remove(chip.Element);
            }
            if (chip.Kind == ChipKind.Scene)
            {
                UpdateSceneButton();
            }
        }

        private Chip FindChip(ChipKind kind)
        {
            for (int i = 0; i < _chips.Count; i++)
            {
                if (_chips[i].Kind == kind)
                {
                    return _chips[i];
                }
            }
            return null;
        }

        private VisualElement MakeChipIcon(ChipKind kind, UnityEngine.Object target)
        {
            // Object-derived chips use the object's own mini thumbnail
            // (accurate per asset type); the scene chip uses the built-in
            // scene icon. Everything falls back to a text glyph.
            if (target != null)
            {
                Texture2D thumb = AssetPreview.GetMiniThumbnail(target);
                if (thumb != null)
                {
                    var image = new Image { image = thumb, scaleMode = ScaleMode.ScaleToFit };
                    image.AddToClassList("uap-ctx-chip-icon");
                    return image;
                }
            }
            string iconName = kind == ChipKind.Scene
                ? "d_SceneAsset Icon" : kind == ChipKind.Marker ? "d_Transform Icon" : "d_GameObject Icon";
            return IconLoader.CreateIcon(iconName, IconLoader.GlyphBullet,
                "uap-ctx-chip-icon", "uap-ctx-chip-glyph");
        }

        private static Button MakeRemoveButton(Action onClick)
        {
            var button = new Button(onClick);
            button.text = IconLoader.GlyphX;
            button.tooltip = L10n.S.CtxRemoveTooltip;
            button.AddToClassList("uap-ctx-chip-x");
            return button;
        }

        // -- Presentation -------------------------------------------------------------------

        private void UpdateAttachButton()
        {
            _attachButton.SetEnabled(SelectionContextProvider.HasSelection);
        }

        private void UpdateSceneButton()
        {
            bool on = FindChip(ChipKind.Scene) != null;
            _sceneButton.EnableInClassList("uap-ctx-attach--on", on);
            _sceneButton.text = on ? L10n.S.CtxSceneAttachedButton : L10n.S.CtxAttachSceneButton;
        }

        private void UpdateErrorChip()
        {
            // Defensive, NOT the fix: the real fix for the flood this
            // guarded against is ConsoleErrorProvider deferring its
            // Changed raise to a main-thread EditorApplication.update tick
            // (2026-08-02 design note section 1) instead of calling it
            // synchronously from inside Application.logMessageReceived,
            // which could land here mid-repaint. This check instead
            // covers the ordinary, unrelated case of a queued Changed
            // notification reaching this handler after the chip's element
            // has been detached (window/view torn down between the event
            // being raised and this tick) -- same idiom as
            // StreamingLabelPump's "entry.Label.panel == null" check.
            if (_errorChip.panel == null)
            {
                return;
            }
            ConsoleErrorProvider.Entry[] visibleEntries = ConsoleErrorProvider.VisibleSnapshot();
            int count = CountUnacknowledgedVisible(visibleEntries,
                _acknowledgedErrorMessages, _dismissedForNowErrorMessages);
            // 2026-09-15 compile window: nothing shown mid-compile is final
            // (see ConsoleErrorProvider.Settling). The chip used to flash up
            // on every agent-driven compile and clear itself afterwards,
            // which reads as a phantom error rather than a call to action.
            bool visible = count > 0 && !ConsoleErrorProvider.Settling;
            _errorChip.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
            {
                _errorLabel.text = count == 1
                    ? L10n.S.CtxErrorSingle : L10n.F(L10n.S.CtxErrorPluralFmt, count);
            }
        }

        /// <summary>
        /// Pure decision behind the error chip's visibility (docs/design-
        /// notes/2026-08-13-error-chip-ignore.md): the number of currently
        /// VISIBLE (non-ignored) entries whose Message is NOT already in
        /// `acknowledged` -- the chip shows while this is &gt; 0, hides once
        /// every visible message has been sent via the fix button.
        /// Content-keyed by Message (Ordinal), not by count, which is
        /// exactly what fixes the OLD bug this design note's "Measured
        /// root cause" section 1 describes: removing an entry (e.g.
        /// OnCompilationStarted dropping superseded compiler errors) can
        /// only ever shrink this count or leave it unchanged -- it can
        /// never resurrect the chip for something no longer visible.
        /// internal (not private) so ContextBarViewLogicTests can exercise
        /// it directly with hand-built Entry arrays -- ContextBarView has
        /// no test-instantiation seam for its full VisualElement tree (same
        /// rationale as SettingsView's AreStringListsEqual/AddHint seams).
        /// </summary>
        /// <summary>
        /// Fix-button acknowledgment step (internal for
        /// ContextBarViewLogicTests): records only the messages actually IN
        /// the sent digest -- capped at
        /// ConsoleErrorProvider.DefaultDigestEntries, oldest first, the
        /// exact cap FormatDigest itself applies -- into the TRANSIENT
        /// acknowledged set. A visible error beyond that cap was never
        /// shown to Claude, so acknowledging it would hide the chip for
        /// something that was not, in fact, sent. Never touches
        /// PanelSettings: a failed fix must bring the chip back after a
        /// domain reload (design note: transient acknowledged set).
        /// </summary>
        internal static void AcknowledgeSentMessages(IList<ConsoleErrorProvider.Entry> visible,
            HashSet<string> acknowledged)
        {
            if (visible == null || acknowledged == null)
            {
                return;
            }
            int count = Math.Min(visible.Count, ConsoleErrorProvider.DefaultDigestEntries);
            for (int i = 0; i < count; i++)
            {
                if (!string.IsNullOrEmpty(visible[i].Message))
                {
                    acknowledged.Add(visible[i].Message);
                }
            }
        }

        internal static int CountUnacknowledgedVisible(IList<ConsoleErrorProvider.Entry> visible,
            HashSet<string> acknowledged)
        {
            return CountUnacknowledgedVisible(visible, acknowledged, null);
        }

        /// <summary>
        /// UXO-3 extension of the pure visibility decision: a message hides
        /// the chip when it is in EITHER transient set -- acknowledged
        /// (sent to Claude via the fix button) or dismissed-for-now (the
        /// bare X). Both sets share the same semantics: never persisted, a
        /// NEW message re-shows the chip, a reload starts clean.
        /// </summary>
        internal static int CountUnacknowledgedVisible(IList<ConsoleErrorProvider.Entry> visible,
            HashSet<string> acknowledged, HashSet<string> dismissedForNow)
        {
            return SelectSendable(visible, acknowledged, dismissedForNow).Count;
        }

        /// <summary>
        /// THE predicate behind the error chip (2026-09-07 design note
        /// section 3.2): the visible entries, in their original (oldest
        /// first) order, that are in NEITHER transient set -- not sent via
        /// the fix button (acknowledged) and not closed with the X
        /// (dismissed-for-now). The chip's count, the fix button's payload
        /// and title, and the post-send acknowledgment all derive from this
        /// one list, so they can never disagree again. Never returns null.
        /// </summary>
        internal static List<ConsoleErrorProvider.Entry> SelectSendable(
            IList<ConsoleErrorProvider.Entry> visible,
            HashSet<string> acknowledged, HashSet<string> dismissedForNow)
        {
            var result = new List<ConsoleErrorProvider.Entry>();
            if (visible == null || visible.Count == 0)
            {
                return result;
            }
            for (int i = 0; i < visible.Count; i++)
            {
                string message = visible[i].Message;
                bool hidden = message != null
                    && ((acknowledged != null && acknowledged.Contains(message))
                        || (dismissedForNow != null && dismissedForNow.Contains(message)));
                if (!hidden)
                {
                    result.Add(visible[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// The bare X's whole effect (UXO-3, internal for
        /// ContextBarViewLogicTests): records every visible message into
        /// the TRANSIENT dismissed-for-now set. Unlike
        /// AcknowledgeSentMessages there is no digest cap -- nothing is
        /// being sent anywhere; the user asked to close the chip, so the
        /// whole visible set is covered. Never touches PanelSettings.
        /// </summary>
        internal static void DismissVisibleForNow(IList<ConsoleErrorProvider.Entry> visible,
            HashSet<string> dismissedForNow)
        {
            if (visible == null || dismissedForNow == null)
            {
                return;
            }
            for (int i = 0; i < visible.Count; i++)
            {
                if (!string.IsNullOrEmpty(visible[i].Message))
                {
                    dismissedForNow.Add(visible[i].Message);
                }
            }
        }

        /// <summary>
        /// Transcript attachment title for a dropped object:
        /// "GameObject: Main Camera" for scene objects, "Asset: X (Type)"
        /// style for assets (short, human-readable, no wire delimiters).
        /// </summary>
        private static string BuildObjectTitle(UnityEngine.Object obj)
        {
            string name = ShortLabel(obj.name);
            var gameObject = obj as GameObject;
            if (gameObject != null && gameObject.scene.IsValid())
            {
                return L10n.F(L10n.S.CtxGameObjectTitleFmt, name);
            }
            return L10n.F(L10n.S.CtxObjectTitleTypeFirstFmt, obj.GetType().Name, name);
        }

        private static string ShortLabel(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return L10n.S.CtxUnnamedFallback;
            }
            if (name.Length <= MaxChipLabelChars)
            {
                return name;
            }
            return name.Substring(0, MaxChipLabelChars - 3) + "...";
        }
    }
}
