using System;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The main chat view: resume banner + message list + composer, with
    /// first-run/empty states switched in. Subscribes AgentHub.Changed and
    /// coalesces refreshes on a ~60 ms schedule so streaming deltas never
    /// trigger per-event relayout (ARCHITECTURE.md D6, R05 section 3.5).
    /// All state-driven enable/disable decisions live in RefreshNow.
    /// </summary>
    public sealed class ChatView : IAgentPanelView
    {
        private const long RefreshIntervalMillis = 60;

        private VisualElement _root;
        private VisualElement _contentHost;
        private StreamingLabelPump _pump;
        private MessageListController _list;
        private VisualElement _compactingRow;
        private ComposerView _composer;
        private FirstRunView _firstRun;
        private ResumeBanner _banner;
        private EmptyStateView _empty;
        private PermissionCard _permissionCard;
        private ContextBarView _contextBar;
        private DragDropAttachHandler _panelDrop;

        private IVisualElementScheduledItem _refreshLoop;
        private bool _dirty;
        private bool _active;

        /// <summary>
        /// Request id the inline-vs-window auto decision already ran for.
        /// One decision per request: closing the window by hand must not
        /// make the next refresh reopen it. Static on purpose -- a panel
        /// rebuild (language switch, re-entrant CreateGUI) swaps in a new
        /// ChatView instance while a request can still be pending, and an
        /// instance field reset to null there re-ran the decision and
        /// reopened a window the user had deliberately closed. A pending
        /// permission never survives a domain reload (the CLI is killed
        /// before reload), so no serialization is needed; cleared at the
        /// same point as before (hub pending gone).
        /// </summary>
        private static string s_autoWindowDecidedRequestId;

        // Resize refit (docs/design-notes/2026-09-16-inline-card-refit-on-
        // shrink.md): the request whose inline card THIS view collapsed
        // because the panel became too small for the inline layout, so
        // growing back past the threshold can re-expand it. Static for the
        // same reason as the decision id above (UICODE-8): a ChatView
        // rebuild mid-request must not forget that the collapse was ours.
        // Cleared together with the decision id when nothing is pending.
        private static string s_sizeCollapsedRequestId;

        // Last "too small for the inline card" verdict from a real chat
        // root size; the refit reacts to transitions of this flag only.
        private bool _wasTooSmall;

        internal static void ResetAutoWindowDecisionForTests()
        {
            s_autoWindowDecidedRequestId = null;
            s_sizeCollapsedRequestId = null;
        }

        internal static string AutoWindowDecidedRequestIdForTests
        {
            get { return s_autoWindowDecidedRequestId; }
        }

        public string Title
        {
            get { return "Chat"; }
        }

        // -- IAgentPanelView ------------------------------------------------------

        public VisualElement CreateGUI()
        {
            _root = new VisualElement();
            _root.AddToClassList("uap-chat");

            // Esc stops the stream from anywhere inside the chat view, not
            // only while the composer field has focus (R05 section 2.5; the
            // hint label advertises "Esc to stop"). TrickleDown runs this
            // ancestor handler before the composer's own, and stopping
            // propagation prevents a double interrupt.
            _root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            _pump = new StreamingLabelPump();
            _banner = new ResumeBanner();
            _root.Add(_banner.Root);

            _contentHost = new VisualElement();
            _contentHost.AddToClassList("uap-chat-content");

            _list = new MessageListController(_pump);
            _contentHost.Add(_list.Root);

            _empty = new EmptyStateView(OnSuggestionPicked);
            _empty.SetVisible(false);
            _contentHost.Add(_empty.Root);

            _firstRun = new FirstRunView();
            _contentHost.Add(_firstRun.Root);
            _root.Add(_contentHost);

            // Compaction progress row (design note 2026-09-10-compacting-
            // indicator.md): pinned between the message list and the
            // permission card, like the card itself, so it is visible
            // regardless of scroll. Lives OUTSIDE MessageListController on
            // purpose: the list reconciles rows by message id, and a
            // transient row that is not a message would have to be
            // special-cased in every incremental path there.
            _compactingRow = new VisualElement();
            _compactingRow.AddToClassList("uap-compacting-row");
            _compactingRow.Add(MessageBlockFactory.CreateSpinner());
            var compactingLabel = new Label(L10n.S.ChatCompactingIndicator);
            compactingLabel.enableRichText = false;
            compactingLabel.AddToClassList("uap-compacting-text");
            _compactingRow.Add(compactingLabel);
            _compactingRow.style.display = DisplayStyle.None;
            _root.Add(_compactingRow);

            // Inline permission card: pinned between the message list and
            // the composer so it stays visible regardless of scroll while a
            // can_use_tool request is pending (R05 section 3.7). The
            // composer below stays enabled (deny + type an alternative).
            // Hybrid hosting: the same card component also renders inside
            // the floating PermissionWindow when the panel is too small (or
            // on request); either host resolves the single pending request.
            _permissionCard = new PermissionCard(PermissionCard.HostKind.Inline);
            _permissionCard.OpenInWindowRequested += OnOpenPermissionWindowRequested;
            _permissionCard.ShowInlineRequested += OnShowPermissionInlineRequested;
            _root.Add(_permissionCard.Root);

            // Context chip strip directly above the composer (R05 section
            // 4.1): attach-selection, scene toggle, drag-drop chips and the
            // automatic Console-error chip. Chips serialize into the next
            // outgoing message via ComposerView.SetContextSource.
            _contextBar = new ContextBarView();
            _root.Add(_contextBar.Root);

            _composer = new ComposerView();
            _composer.SetContextSource(_contextBar);
            // UXA-4: the inline card's deny reason comes from the composer
            // (consume-on-read; see ComposerView.ConsumeTextForDeny). The
            // delegate reads the field at deny time, so wiring here --
            // after both exist -- is safe for the card built above.
            _permissionCard.ConsumeExternalDenyMessage = _composer.ConsumeTextForDeny;
            _root.Add(_composer.Root);

            // The expanded card is capped to 40 percent of the chat content
            // height; recompute from real geometry, never a hardcoded pixel
            // cap (the var(--uap-perm-card-min) floor lives in USS).
            _root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            _list.RestoreScrollOffset(SessionStateBridge.ScrollPosition);
            return _root;
        }

        public void OnActivate()
        {
            if (_active)
            {
                return;
            }
            _active = true;
            AgentHub.Changed += OnHubChanged;
            PermissionWindow.StateChanged += OnHubChanged;
            _contextBar.OnActivate();
            // Dropping Project/Hierarchy objects ANYWHERE on the panel
            // (message list, context bar, composer) turns them into
            // context chips; a full-panel "Drop to attach" overlay shows
            // during the drag (overlay host = the chat root).
            if (_panelDrop == null)
            {
                _panelDrop = new DragDropAttachHandler(_root, OnPanelDrop, _root);
            }
            _refreshLoop = _root.schedule.Execute(RefreshIfDirty).Every(RefreshIntervalMillis);
            if (_pump != null)
            {
                _pump.Resume();
            }
            _dirty = true;
            RefreshIfDirty();
        }

        public void OnDeactivate()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            AgentHub.Changed -= OnHubChanged;
            PermissionWindow.StateChanged -= OnHubChanged;
            _contextBar.OnDeactivate();
            _empty.Detach();
            if (_panelDrop != null)
            {
                _panelDrop.Detach();
                _panelDrop = null;
            }
            if (_refreshLoop != null)
            {
                _refreshLoop.Pause();
                _refreshLoop = null;
            }
            if (_pump != null)
            {
                // Suspend, not Clear: entries survive the view switch so
                // reactivating resumes the same streaming labels (a Clear
                // here froze them at the switch-away text until the block
                // finished, since text growth alone never re-Tracks).
                _pump.Suspend();
            }
        }

        public void SerializeState()
        {
            if (_list != null)
            {
                SessionStateBridge.ScrollPosition = _list.GetScrollOffset();
            }
            if (_composer != null)
            {
                _composer.SaveDraft();
            }
        }

        // -- Refresh pipeline --------------------------------------------------------

        private void OnHubChanged()
        {
            _dirty = true;
        }

        /// <summary>
        /// A drop landed on the panel: non-image objects become context
        /// chips, image files (OS drop or Texture asset) become composer
        /// image attachments (design note 2026-09-07 decision I4).
        /// </summary>
        private void OnPanelDrop(DropPayload payload)
        {
            if (payload == null)
            {
                return;
            }
            if (payload.Objects.Count > 0)
            {
                _contextBar.AddObjectChips(payload.Objects.ToArray());
            }
            if (payload.ImagePaths.Count > 0)
            {
                _composer.AddImageFiles(payload.ImagePaths);
            }
        }

        private void RefreshIfDirty()
        {
            if (!_dirty)
            {
                return;
            }
            _dirty = false;
            RefreshNow();
        }

        private void RefreshNow()
        {
            AgentClient client = AgentHub.Client;
            ChatSession session = AgentHub.Session;

            FirstRunView.Mode mode = ResolveFirstRunMode(
                AgentHub.LastError, client, session, AgentHub.CurrentAuthStatus,
                AgentHub.IsClaudeBackend, AgentHub.AcpSignInRequired);
            _firstRun.SetMode(mode, AgentHub.LastError);

            bool chatVisible = mode == FirstRunView.Mode.Hidden;
            bool hasMessages = session.messages.Count > 0;
            _list.Root.style.display = chatVisible && hasMessages
                ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.SetVisible(chatVisible && !hasMessages);

            if (chatVisible)
            {
                _list.Refresh(session);
            }
            _compactingRow.style.display = chatVisible && AgentHub.IsCompacting
                ? DisplayStyle.Flex : DisplayStyle.None;
            _banner.Refresh(client, AgentHub.ResumedMidTurn, hasMessages);
            RefreshPermissionHosting(chatVisible);
            _contextBar.Refresh(chatVisible);
            _composer.SetPendingPermissionHint(chatVisible && AgentHub.PendingPermission != null);
            _composer.Refresh(client, chatVisible);
        }

        /// <summary>
        /// Hybrid permission hosting: runs the inline-vs-window size
        /// decision once per request (PermissionCardLayout), then keeps the
        /// inline card and the wait-bar state in sync with the floating
        /// window. Responding in either host clears
        /// AgentHub.PendingPermission, which hides the card here and closes
        /// the window on its own refresh.
        /// </summary>
        private void RefreshPermissionHosting(bool chatVisible)
        {
            // The one-shot decision tracks the HUB's pending request, not
            // the chatVisible-masked view of it: a transient first-run mode
            // flip (e.g. an error block that looks like an auth failure
            // entering the last-3-messages window) must not re-run the
            // decision for a request it already ran for -- that could
            // auto-reopen a window the user explicitly closed.
            var hubPending = AgentHub.PendingPermission;
            if (hubPending == null)
            {
                s_autoWindowDecidedRequestId = null;
                s_sizeCollapsedRequestId = null;
            }

            var pending = chatVisible ? hubPending : null;
            if (pending != null
                && !string.Equals(pending.RequestId, s_autoWindowDecidedRequestId,
                    StringComparison.Ordinal))
            {
                float width = _root.resolvedStyle.width;
                float height = _root.resolvedStyle.height;
                // Consume the one-shot decision only with REAL geometry.
                // Before the first layout pass (request already pending
                // when the view activates) resolvedStyle is NaN; deciding
                // now would burn the request's only auto-open chance on
                // unknown sizes. OnRootGeometryChanged re-arms the refresh,
                // so the decision runs as soon as a real size exists.
                if (PermissionCardLayout.IsKnownGeometry(width, height))
                {
                    s_autoWindowDecidedRequestId = pending.RequestId;
                    if (PermissionCardLayout.ShouldOpenWindow(width, height))
                    {
                        PermissionWindow.OpenForPending(true);
                    }
                }
            }
            _permissionCard.Refresh(pending);
            _permissionCard.SetShownInWindow(pending != null && PermissionWindow.IsOpen);
        }

        private void OnOpenPermissionWindowRequested()
        {
            PermissionWindow.OpenForPending(false);
            _dirty = true;
        }

        private void OnShowPermissionInlineRequested()
        {
            PermissionWindow.CloseIfOpen();
            _permissionCard.SetShownInWindow(false);
            // In a panel too small for the inline layout (the auto decision
            // would have chosen the window), force-expanding would stack the
            // expanded card's min-height floor on top of the transcript and
            // composer minimums -- the sum overflows the window bounds with
            // no outer scrollbar, resurrecting the original overflow bug.
            // Come back collapsed to the summary row instead; the chevron
            // still lets the user expand deliberately.
            bool tooSmall = PermissionCardLayout.ShouldOpenWindow(
                _root.resolvedStyle.width, _root.resolvedStyle.height);
            _permissionCard.SetExpanded(!tooSmall);
            if (tooSmall)
            {
                // Collapsed for the panel's size, not by the user's choice:
                // let the resize refit expand it again once the panel is
                // large enough (same bookkeeping as its own collapse).
                var pending = AgentHub.PendingPermission;
                s_sizeCollapsedRequestId = pending != null ? pending.RequestId : null;
            }
            _dirty = true;
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            _permissionCard.SetMaxCardHeight(
                PermissionCardLayout.ComputeMaxCardHeight(evt.newRect.height));
            RefitInlineCard(evt.newRect.width, evt.newRect.height);
            // A pending request whose inline-vs-window decision was DEFERRED
            // (geometry unknown at refresh time) gets its decision as soon
            // as the first real size arrives. Same condition as the decision
            // itself: an undecided request id, whatever the previous one was.
            var pending = AgentHub.PendingPermission;
            if (pending != null
                && !string.Equals(pending.RequestId, s_autoWindowDecidedRequestId,
                    StringComparison.Ordinal))
            {
                _dirty = true;
            }
        }

        /// <summary>
        /// The inline-vs-window decision is one-shot per request, so it
        /// never revisits a card that was decided inline in a large panel
        /// and is then dragged below the inline minimum: the expanded body
        /// kept its usability floor (220px for a question) and, with no
        /// outer scrollbar, pushed the composer and status bar out of the
        /// window until the request was answered. Track the "too small"
        /// verdict from every real chat root size and act on its
        /// transitions (PermissionCardLayout.ResolveInlineFit): collapse
        /// the expanded card on the way in, expand a card we collapsed on
        /// the way out. A request shown in the floating window is left
        /// alone (its inline stand-in is the slim wait bar already).
        /// </summary>
        private void RefitInlineCard(float width, float height)
        {
            if (!PermissionCardLayout.IsKnownGeometry(width, height))
            {
                // A collapsed host or the pre-layout NaN is not a size the
                // user chose; deciding on it would collapse the card on a
                // phantom shrink and expand it on a phantom grow.
                return;
            }
            bool tooSmall = PermissionCardLayout.ShouldOpenWindow(width, height);
            bool wasTooSmall = _wasTooSmall;
            _wasTooSmall = tooSmall;
            var pending = AgentHub.PendingPermission;
            if (pending == null || _permissionCard.IsShownInWindow)
            {
                return;
            }
            bool collapsedBySize = string.Equals(pending.RequestId,
                s_sizeCollapsedRequestId, StringComparison.Ordinal);
            switch (PermissionCardLayout.ResolveInlineFit(
                wasTooSmall, tooSmall, _permissionCard.IsExpanded, collapsedBySize))
            {
                case PermissionCardLayout.InlineFitAction.Collapse:
                    s_sizeCollapsedRequestId = pending.RequestId;
                    _permissionCard.SetExpanded(false);
                    break;
                case PermissionCardLayout.InlineFitAction.Expand:
                    s_sizeCollapsedRequestId = null;
                    _permissionCard.SetExpanded(true);
                    break;
            }
        }

        /// <summary>
        /// Decides which setup card applies:
        /// - CLI probe/spawn failure (AgentHub.LastError) -&gt; CliNotFound.
        /// - A recent authentication error block -&gt; NotLoggedIn (the CLI
        ///   reports auth failure per message, e.g. assistant
        ///   error=="authentication_failed"; AgentHub records it as an
        ///   error block in the transcript).
        /// </summary>
        /// <summary>
        /// Internal + fully parameterized as the test seam (no statics
        /// read), mirroring the package's other pure decision helpers.
        /// UXO-1 added the <paramref name="auth"/> input: before it, a
        /// not-logged-in FIRST run looked fully usable (empty transcript,
        /// no LastError -- the CLI spawns fine without login) and the
        /// user's first send bounced off authentication_failed before the
        /// card could appear. A boot-time RefreshAuthStatus
        /// (AgentPanelWindow.StartHubDeferred) now fills the cache this
        /// reads. Precedence: CliNotFound first (no CLI trumps auth); a
        /// recent transcript auth error still counts even when the cached
        /// status says LoggedIn (an env-token revoked mid-session produces
        /// error blocks while the stale cache disagrees); the proactive
        /// check fires only on a POSITIVE "available and not logged in" --
        /// null (query not completed yet, worst case a 10 s timeout) and
        /// IsAvailable==false (CLI/spawn trouble, LastError territory)
        /// must stay Hidden or every healthy boot would flash the login
        /// card.
        /// </summary>
        internal static FirstRunView.Mode ResolveFirstRunMode(string lastError,
            AgentClient client, ChatSession session, AuthStatus auth)
        {
            return ResolveFirstRunMode(lastError, client, session, auth, true);
        }

        /// <summary>
        /// As above, with the backend: the NotLoggedIn card is Claude Code's
        /// in-panel login flow (design note 2026-09-10-acp-backends.md
        /// section 4) -- an ACP agent's sign-in error is rendered as an
        /// ordinary error block instead, so <paramref name="claudeBackend"/>
        /// false never yields NotLoggedIn.
        /// </summary>
        internal static FirstRunView.Mode ResolveFirstRunMode(string lastError,
            AgentClient client, ChatSession session, AuthStatus auth, bool claudeBackend)
        {
            return ResolveFirstRunMode(lastError, client, session, auth, claudeBackend, false);
        }

        /// <summary>
        /// As above, with the ACP sign-in state (design note
        /// 2026-09-13-acp-feature-parity.md section 2): an ACP agent whose
        /// sign-in failed AND whose backend has a login command the panel
        /// can run (<paramref name="acpSignInRequired"/> =
        /// AgentHub.AcpSignInRequired) gets the same NotLoggedIn card,
        /// worded for that agent. Claude's `auth status` cache and the
        /// transcript's auth-error heuristic stay Claude-only: an ACP
        /// agent's sign-in error is reported by the bridge itself.
        /// </summary>
        internal static FirstRunView.Mode ResolveFirstRunMode(string lastError,
            AgentClient client, ChatSession session, AuthStatus auth, bool claudeBackend,
            bool acpSignInRequired)
        {
            if (!string.IsNullOrEmpty(lastError)
                && (client == null || client.State == AgentClientState.NotStarted
                    || client.State == AgentClientState.Errored))
            {
                return FirstRunView.Mode.CliNotFound;
            }
            if (!claudeBackend)
            {
                return acpSignInRequired ? FirstRunView.Mode.NotLoggedIn : FirstRunView.Mode.Hidden;
            }
            if (HasRecentAuthError(session))
            {
                return FirstRunView.Mode.NotLoggedIn;
            }
            if (auth != null && auth.IsAvailable && !auth.LoggedIn)
            {
                return FirstRunView.Mode.NotLoggedIn;
            }
            return FirstRunView.Mode.Hidden;
        }

        private static bool HasRecentAuthError(ChatSession session)
        {
            int inspected = 0;
            for (int i = session.messages.Count - 1; i >= 0 && inspected < 3; i--, inspected++)
            {
                ChatMessage message = session.messages[i];
                for (int b = 0; b < message.blocks.Count; b++)
                {
                    ChatMessageBlock block = message.blocks[b];
                    if (block.kind != ChatBlockKind.Error || string.IsNullOrEmpty(block.text))
                    {
                        continue;
                    }
                    if (block.text.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0
                        || block.text.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                        || block.text.IndexOf("Invalid API key", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void OnSuggestionPicked(string text)
        {
            _composer.InsertText(text);
        }

        private static void OnRootKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape)
            {
                return;
            }
            AgentClient client = AgentHub.Client;
            if (client != null && client.TurnActive)
            {
                client.Interrupt();
                evt.StopPropagation();
            }
        }
    }
}
