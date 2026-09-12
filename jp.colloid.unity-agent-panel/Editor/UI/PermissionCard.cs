using System;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Hybrid non-modal permission card (R05 section 3.7, 02b sections 2/5).
    /// ONE implementation rendered in either host:
    ///
    /// - Inline (ChatView, between message list and composer): a compact
    ///   summary row (status icon + one-line ellipsized title + [Allow (Y)]
    ///   [Always v] [Deny (N)] + chevron expander + open-in-window button,
    ///   ~28-34px collapsed). Expanding reveals a details area inside an
    ///   internal ScrollView; the WHOLE card is capped via
    ///   SetMaxCardHeight (40 percent of the chat content height, fed by a
    ///   GeometryChangedEvent on the chat root) with a
    ///   var(--uap-perm-card-min) usability floor in USS, so nothing can
    ///   ever render outside the window bounds. While the request is shown
    ///   in the floating PermissionWindow instead, the inline area collapses
    ///   to a slim "Waiting for permission - shown in window [Show here]"
    ///   bar.
    /// - Window (PermissionWindow, HostKind.Window): always expanded, no
    ///   chevron / open-in-window buttons, details fill the window.
    ///
    /// Two variants:
    /// - Normal tools: title "Claude wants to use &lt;display_name&gt; -
    ///   &lt;description&gt;"; details hold the monospace input preview, the
    ///   optional deny-reason field and the keyboard hint. "Always v" comes
    ///   from permission_suggestions and is sent as updatedPermissions.
    ///   Starts COLLAPSED inline.
    /// - AskUserQuestion (requires_user_interaction): summary actions are
    ///   [Submit] [Skip]; the CURRENT question's header + text sit in a
    ///   pinned prompt block (uap-perm-qprompt) between the summary row
    ///   and the scrolling details, and the details hold one options
    ///   section per question (2026-09-12 design note: with the wrapped
    ///   summary row and the 40 percent cap the question text scrolled
    ///   out of view with the options and read like one more option, so
    ///   it now never scrolls and is set in bold). With 2+ questions a
    ///   Claude Desktop-style stepper shows ONE question at a time behind
    ///   a clickable tab strip (QuestionStepperModel; 2026-08-05 design
    ///   note -- a 4-question request measured 736px stacked, ~175px per
    ///   question, so one-at-a-time is what fits the hosts). Starts
    ///   EXPANDED (interaction required) but obeys the same height cap
    ///   with internal scrolling. Submit sends allow with
    ///   answers keyed by QUESTION TEXT (the only verified key; 02b
    ///   section 5); Skip sends allow with an empty answers object.
    ///   Every question also offers a synthetic "Other..." option with a
    ///   free-text field (Claude Desktop parity -- the tool's upstream
    ///   contract promises the user can always answer with custom text):
    ///   single-select, it is mutually exclusive with the real options and
    ///   counts as answered only while its text is non-empty; multiSelect,
    ///   it toggles alongside the options and its text is appended to the
    ///   joined labels. Selecting Other never auto-advances the stepper
    ///   (typing follows the click); Enter in the field advances instead,
    ///   once text is non-empty.
    ///
    /// Both hosts answer through AgentHub.RespondToPendingPermission, the
    /// single source of truth: responding in either host resolves both.
    ///
    /// Keyboard: Y/N answer the normal variant while the card has focus
    /// (never while typing in the deny-reason field). Escape is NOT deny --
    /// it stays the global interrupt (ChatView).
    ///
    /// All model-controlled text is rendered with enableRichText = false
    /// (ARCHITECTURE.md risk 8).
    /// </summary>
    public sealed class PermissionCard
    {
        /// <summary>Which surface hosts this card instance.</summary>
        public enum HostKind
        {
            Inline,
            Window
        }

        private const string DefaultDenyMessage = "User denied this tool use.";
        private const int PreviewMaxLines = 12;
        private const int PreviewMaxChars = 800;

        private readonly HostKind _host;
        private readonly VisualElement _root;
        private VisualElement _waitBar;
        private VisualElement _body;
        private VisualElement _summary;
        private Label _summaryTitle;
        private Label _undoBadge;
        private Label _queueDepthLabel;
        private Label _summaryTarget;
        private VisualElement _summaryActions;
        private Button _chevron;
        private ScrollView _details;

        private string _currentRequestId;
        private CanUseToolRequest _request;
        private bool _isQuestionVariant;
        private bool _expanded;
        private bool _shownInWindow;
        private bool _autoFocusEnabled = true;
        private float _maxCardHeight = -1f;
        private TextField _denyField;

        // AskUserQuestion selection state (per question -> selected option
        // indices; a HashSet supports both radio and multiSelect).
        private AskUserQuestionInput _questions;
        private List<HashSet<int>> _selection;
        private List<List<Button>> _optionButtons;
        private Button _submitButton;

        // AskUserQuestion "Other..." free-text answer (Claude Desktop
        // parity: the tool's upstream contract promises the user can always
        // answer with custom text, so every question gets one synthetic
        // option-shaped button plus a text field that only appears while
        // that button is selected). Kept OUTSIDE _selection/_optionButtons
        // on purpose: those lists are indexed by REAL option index
        // everywhere (payload building, class toggling), and splicing a
        // synthetic entry in would off-by-one every consumer.
        private List<Button> _otherButtons;
        private List<TextField> _otherFields;
        private List<bool> _otherSelected;
        // Caption label above each Other field (2026-08-14 UI polish audit
        // item 5, class uap-perm-field-caption). Shown/hidden together
        // with its field -- indexed the same way (question index), kept
        // as its own list for the same reason _otherFields is: toggled in
        // lockstep by SetOtherSelected, reset per-request alongside it.
        private List<Label> _otherCaptions;

        // AskUserQuestion stepper (design note 2026-08-05): with 2+
        // questions only the current question's section is displayed and a
        // tab strip above the details navigates between them. The pure
        // step/answered/advance rules live in QuestionStepperModel; this
        // card only mirrors them into element visibility and tab classes.
        private QuestionStepperModel _stepper;
        private VisualElement _questionTabsStrip;
        private List<Button> _questionTabs;
        private List<VisualElement> _questionSections;
        // Pinned prompt block (2026-09-12 design note): the current
        // question's header + text, inserted into _body between the
        // summary row (or the tab strip) and the scrolling _details, so
        // the question itself never scrolls away with its options.
        // Rebuilt per request like the tab strip (ResetStepperState).
        private VisualElement _questionPrompt;
        private Label _questionPromptHeader;
        private Label _questionPromptText;

        /// <summary>Inline host: the open-in-window button was clicked.</summary>
        public event Action OpenInWindowRequested;

        /// <summary>Inline host: [Show here] on the wait bar was clicked.</summary>
        public event Action ShowInlineRequested;

        public VisualElement Root
        {
            get { return _root; }
        }

        public PermissionCard()
            : this(HostKind.Inline)
        {
        }

        public PermissionCard(HostKind host)
        {
            _host = host;
            _root = new VisualElement();
            _root.AddToClassList("uap-perm");
            if (host == HostKind.Window)
            {
                _root.AddToClassList("uap-perm--windowhost");
            }
            _root.style.display = DisplayStyle.None;
            _root.focusable = true;
            _root.RegisterCallback<KeyDownEvent>(OnKeyDown);
            BuildSkeleton();
        }

        // -- Public API -----------------------------------------------------------

        /// <summary>
        /// Syncs the card with the hub's pending request. Rebuilds only when
        /// the request id changes; hides when nothing is pending.
        /// </summary>
        public void Refresh(ControlRequestMessage pending)
        {
            if (pending == null || pending.CanUseTool == null)
            {
                Hide();
                return;
            }
            if (pending.RequestId == _currentRequestId)
            {
                // The active request has not changed, but the queue behind
                // it may have (another can_use_tool queued or drained) --
                // RefreshQueueDepthLabel must run on every Refresh call,
                // not just when Build() rebuilds the whole card.
                RefreshQueueDepthLabel();
                return;
            }
            _currentRequestId = pending.RequestId;
            _request = pending.CanUseTool;
            Build();
            RefreshQueueDepthLabel();
            _root.style.display = DisplayStyle.Flex;
            ApplyWindowMode();
            FocusUnlessTyping();
        }

        /// <summary>
        /// Design note 2026-09-10 section 1: mirrors
        /// AgentHub.PendingPermissionQueueDepth into the header label --
        /// "{0} more waiting" when non-zero, hidden at zero (including
        /// whenever nothing is pending at all, since the hub itself
        /// reports 0 in that case).
        /// </summary>
        private void RefreshQueueDepthLabel()
        {
            int depth = AgentHub.PendingPermissionQueueDepth;
            if (depth > 0)
            {
                _queueDepthLabel.text = L10n.F(L10n.S.PermQueueDepthFmt, depth);
                _queueDepthLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _queueDepthLabel.style.display = DisplayStyle.None;
            }
        }

        /// <summary>
        /// Inline host: while the floating PermissionWindow shows this
        /// request, the card body is replaced by a slim wait bar. No-op for
        /// the Window host.
        /// </summary>
        public void SetShownInWindow(bool shown)
        {
            if (_host != HostKind.Inline || _shownInWindow == shown)
            {
                return;
            }
            _shownInWindow = shown;
            if (shown)
            {
                // The body is about to become display:none, but a focused
                // element keeps receiving key events regardless of display.
                // Without this blur a stray Y/N in the main panel would
                // answer a request whose actions are NOT visible here (they
                // live in the floating window). OnKeyDown guards the same
                // state as defense in depth.
                BlurIfFocusInsideCard();
            }
            ApplyWindowMode();
        }

        /// <summary>
        /// Window host, auto-open path: the floating window must never arm
        /// its Y/N hotkeys off a window the USER did not ask for (the
        /// composer keystrokes they were typing would silently allow/deny).
        /// False disables the self-focus in Refresh; an explicit click into
        /// the window still focuses normally.
        /// </summary>
        public void SetAutoFocusEnabled(bool enabled)
        {
            _autoFocusEnabled = enabled;
        }

        /// <summary>Expands or collapses the details area (inline host).</summary>
        public void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            UpdateExpansion();
        }

        /// <summary>
        /// Height cap for the expanded inline card, recomputed by the host
        /// from the chat root geometry (PermissionCardLayout). Non-positive
        /// clears the cap. The var(--uap-perm-card-min) USS floor outranks
        /// this value, keeping the card usable in short panels.
        /// </summary>
        public void SetMaxCardHeight(float maxHeight)
        {
            _maxCardHeight = maxHeight;
            ApplyHeightCap();
        }

        // -- Skeleton (built once per card instance) --------------------------------

        private void BuildSkeleton()
        {
            if (_host == HostKind.Inline)
            {
                _waitBar = new VisualElement();
                _waitBar.AddToClassList("uap-perm-waitbar");
                _waitBar.Add(IconLoader.CreateIcon("d_console.warnicon.sml", "!",
                    "uap-perm-icon", "uap-perm-icon-glyph"));
                Label waitText = PlainLabel(
                    L10n.S.PermWaitingInWindowText, "uap-perm-waitbar-text");
                _waitBar.Add(waitText);
                var showHere = new Button(RaiseShowInline) { text = L10n.S.PermShowHereButton };
                showHere.AddToClassList("uap-card-btn");
                showHere.tooltip = L10n.S.PermShowHereTooltip;
                _waitBar.Add(showHere);
                _waitBar.style.display = DisplayStyle.None;
                _root.Add(_waitBar);
            }

            _body = new VisualElement();
            _body.AddToClassList("uap-perm-body");

            _summary = new VisualElement();
            _summary.AddToClassList("uap-perm-summary");
            // 2026-09-06 review fix 1: the summary row wraps (USS
            // flex-wrap). Icon + title + target + badge travel together in
            // one text block; the buttons and icon buttons in one controls
            // block that drops to a second line when the row is too narrow
            // for both -- so WHAT is being approved is never the part that
            // gets pushed off the row that asks for approval. The icon
            // lives INSIDE the text block: as a sibling it wrapped onto a
            // line of its own whenever the text block was wider than the
            // remaining row (measured at 600px on the first screenshot).
            var summaryText = new VisualElement();
            summaryText.AddToClassList("uap-perm-summary-text");
            summaryText.Add(IconLoader.CreateIcon("d_console.warnicon.sml", "!",
                "uap-perm-icon", "uap-perm-icon-glyph"));
            _summary.Add(summaryText);
            _summaryTitle = PlainLabel(string.Empty, "uap-perm-summary-title");
            summaryText.Add(_summaryTitle);
            // UXA-1: the operation target (file name / command summary) --
            // what "Allow Write?" is actually about -- lives ON the summary
            // row so it stays visible even collapsed. Built once like
            // _summaryTitle, filled per request in BuildToolVariant, hidden
            // for the AskUserQuestion variant and when the describer has
            // nothing beyond the tool name to add.
            _summaryTarget = PlainLabel(string.Empty, "uap-perm-summary-target");
            MessageBlockFactory.ApplyMonoFont(_summaryTarget);
            _summaryTarget.style.display = DisplayStyle.None;
            summaryText.Add(_summaryTarget);
            // Design section 8.2 B2(b): a UapOps tool whose Undoable is
            // false gets a permission-card badge -- built once here (like
            // _summaryTitle) and toggled per request in BuildToolVariant;
            // hidden for every other tool/variant (AskUserQuestion never
            // resolves to a UapOps tool, so it stays hidden there too).
            _undoBadge = PlainLabel(L10n.S.PermUndoNotSupportedBadge, "uap-perm-undo-badge");
            // Non-model-controlled catalog text: no sanitize needed. Set
            // once here (display toggling never clears it) so the badge
            // finally explains WHAT not-undoable means and what to do.
            _undoBadge.tooltip = L10n.S.PermUndoNotSupportedTooltip;
            _undoBadge.style.display = DisplayStyle.None;
            summaryText.Add(_undoBadge);

            // Design note 2026-09-10 section 1: "N more waiting" behind the
            // one request this card shows -- built once here (like
            // _undoBadge above) and refreshed on every Refresh() call (not
            // just when the request id changes) via RefreshQueueDepthLabel,
            // since the queue depth changes independently of the active
            // request as other queued can_use_tool calls are answered.
            // Hidden whenever AgentHub.PendingPermissionQueueDepth is 0.
            _queueDepthLabel = PlainLabel(string.Empty, "uap-perm-queue-depth");
            _queueDepthLabel.style.display = DisplayStyle.None;
            summaryText.Add(_queueDepthLabel);

            var summaryControls = new VisualElement();
            summaryControls.AddToClassList("uap-perm-summary-controls");
            _summary.Add(summaryControls);
            _summaryActions = new VisualElement();
            _summaryActions.AddToClassList("uap-perm-summary-actions");
            summaryControls.Add(_summaryActions);

            if (_host == HostKind.Inline)
            {
                _chevron = new Button(OnChevronClicked);
                _chevron.text = IconLoader.GlyphChevronRight;
                _chevron.AddToClassList("uap-perm-iconbtn");
                _chevron.tooltip = L10n.S.PermChevronTooltip;
                summaryControls.Add(_chevron);

                var openWindow = new Button(RaiseOpenInWindow);
                openWindow.text = IconLoader.GlyphOpenWindow;
                openWindow.AddToClassList("uap-perm-iconbtn");
                openWindow.tooltip = L10n.S.PermOpenWindowTooltip;
                summaryControls.Add(openWindow);
            }
            _body.Add(_summary);

            // Everything beyond the summary row scrolls INSIDE the card;
            // combined with the root height cap nothing can render outside
            // the window bounds.
            _details = new ScrollView(ScrollViewMode.Vertical);
            _details.AddToClassList("uap-perm-details");
            _body.Add(_details);

            _root.Add(_body);
        }

        // -- Build (per request) ------------------------------------------------------

        private void Hide()
        {
            if (_currentRequestId == null)
            {
                return;
            }
            _currentRequestId = null;
            _request = null;
            _denyField = null;
            _questions = null;
            _selection = null;
            _optionButtons = null;
            _otherButtons = null;
            _otherFields = null;
            _otherSelected = null;
            _otherCaptions = null;
            _submitButton = null;
            ResetStepperState();
            _root.style.display = DisplayStyle.None;
            _summaryActions.Clear();
            _details.Clear();
            _queueDepthLabel.style.display = DisplayStyle.None;
        }

        private void Build()
        {
            _summaryActions.Clear();
            _details.Clear();
            _undoBadge.style.display = DisplayStyle.None;
            _summaryTarget.style.display = DisplayStyle.None;
            _summaryTarget.text = string.Empty;
            // _summaryTitle, like _undoBadge above, is built once in
            // BuildSkeleton and reused for every request this card instance
            // ever shows (ChatView/PermissionWindow hold one for their whole
            // lifetime) -- so its per-request tooltip must be reset here too.
            // BuildQuestionVariant never sets a tooltip of its own; without
            // this reset an AskUserQuestion card would keep showing whatever
            // PREVIOUS tool request's wire-name tooltip was left behind.
            _summaryTitle.tooltip = null;
            _denyField = null;
            _questions = null;
            _selection = null;
            _optionButtons = null;
            // The Other state must die with the request it belongs to
            // (same per-request hygiene as the tab strip): the elements go
            // away with _details.Clear() above, but a stale _otherFields
            // list would let a SECOND request inherit the previous
            // request's typed free text through CollectAnswersForSubmit.
            _otherButtons = null;
            _otherFields = null;
            _otherSelected = null;
            _otherCaptions = null;
            _submitButton = null;
            ResetStepperState();

            _isQuestionVariant = false;
            if (_request.RequiresUserInteraction)
            {
                AskUserQuestionInput parsed = AskUserQuestionInput.FromInput(_request.Input);
                if (parsed.Questions.Count > 0)
                {
                    _isQuestionVariant = true;
                    _questions = parsed;
                }
            }

            if (_isQuestionVariant)
            {
                BuildQuestionVariant();
            }
            else
            {
                BuildToolVariant();
            }
            // Higher usability floor for a question (USS .uap-perm--question,
            // 2026-09-12 note): measured at a 560px panel, the three-question
            // card shrank to 138px and showed zero options.
            _root.EnableInClassList("uap-perm--question", _isQuestionVariant);

            // AskUserQuestion needs its options visible to be answerable, so
            // it starts EXPANDED (same cap, internal scrolling). Plain tool
            // prompts start collapsed to a single summary row.
            _expanded = _isQuestionVariant;
            UpdateExpansion();
        }

        // -- Normal tool variant -------------------------------------------------------

        private void BuildToolVariant()
        {
            string displayName = !string.IsNullOrEmpty(_request.DisplayName)
                ? _request.DisplayName : (_request.ToolName ?? "tool");
            // PermissionModels.DisplayName already falls back to the raw
            // ToolName whenever the CLI's can_use_tool message carries no
            // display_name field, so an MCP tool with no CLI-supplied
            // display name reaches here as e.g.
            // "mcp__uap-ops__uap_prefab_revert_added_gameobject".
            //
            // This card is the AUTHORISATION surface, not an informational
            // one -- the user is deciding whether to ALLOW the call, unlike
            // ToolActivityCard/SubagentCard's after-the-fact activity rows
            // (where ShortenToolDisplayName correctly drops the server id
            // entirely). Extension Profiles make talking to more than one
            // MCP server a designed scenario, and two different servers are
            // free to expose the identical tool id (e.g. both defining
            // "delete_all"); ShortenToolDisplayName's output would render
            // both prompts as the identical "Allow delete_all?" with no way
            // to tell which server is asking. FormatMcpNameWithServer keeps
            // the server id instead ("unity-ops: delete_all") -- see that
            // method's doc comment for the full rationale. Do not swap this
            // for ShortenToolDisplayName "for consistency" with the other
            // cards; that would silently reintroduce the ambiguity. No-op
            // for a genuine human-readable display_name or any non-MCP tool
            // name.
            displayName = ToolCardDescriber.FormatMcpNameWithServer(displayName);
            // The Window host renders the description a second time, on
            // its own line below (the "if (!string.IsNullOrEmpty(...))"
            // block further down that adds a .uap-perm-desc label to
            // _details) -- so combining it into the title THERE would
            // show it twice. Only the Inline host, whose collapsed
            // summary row is the only place the description might ever
            // be seen without expanding, keeps the combined single-line
            // title (2026-08-14 UI polish audit item 5).
            // 2026-09-06 review fix 1: the inline title is the bare tool
            // name (plus description when there is one) -- see
            // UiStrings.PermInlineTitleWithDescriptionFmt for why the
            // "Claude wants to use" prefix stays Window-only.
            string title;
            if (_host == HostKind.Window)
            {
                title = L10n.F(L10n.S.PermTitleFmt, displayName);
            }
            else if (!string.IsNullOrEmpty(_request.Description))
            {
                title = L10n.F(L10n.S.PermInlineTitleWithDescriptionFmt, displayName, _request.Description);
            }
            else
            {
                title = displayName;
            }
            // _summaryTitle is built once in BuildSkeleton (not via
            // PlainLabel), so this direct .text assignment is its own
            // sanitize chokepoint -- displayName/Description are tool_use
            // fields the CLI/an MCP server can populate with model text.
            _summaryTitle.text = IconLoader.SanitizeForDisplay(title);
            // The tooltip is deliberately NOT the (already server-qualified)
            // title text above -- it is the EXACT raw wire tool_use name the
            // CLI sent (e.g. "mcp__uap-ops__uap_prefab_revert_added_gameobject"),
            // sanitized the same way the visible text is. This card is where
            // the user grants or denies a capability, so it must always have
            // ONE place the precise string the CLI used is recoverable
            // verbatim -- not just a formatted/shortened approximation of
            // it -- for debugging a prompt or writing a manual allow/deny
            // rule. Left cleared (see Build()'s reset above) when there is
            // no wire name to show.
            if (!string.IsNullOrEmpty(_request.ToolName))
            {
                _summaryTitle.tooltip = IconLoader.SanitizeForDisplay(_request.ToolName);
            }

            // UXA-1: what the call actually TOUCHES (file name, command
            // summary, pattern...) -- the same ToolCardDescriber table the
            // activity rows use, so the approval surface and the history
            // agree. The describer echoes the bare tool name back when the
            // input has nothing useful; showing that next to "Allow
            // Write?" adds nothing, so it stays hidden then (and for the
            // AskUserQuestion variant, which never reaches this method).
            string target = ToolCardDescriber.Describe(_request.ToolName, _request.Input).Summary;
            bool targetIsEcho = string.IsNullOrEmpty(target)
                || string.Equals(target, _request.ToolName, StringComparison.Ordinal)
                || string.Equals(target,
                    ToolCardDescriber.ShortenToolDisplayName(_request.ToolName), StringComparison.Ordinal)
                || string.Equals(target, L10n.S.ToolCardDefaultName, StringComparison.Ordinal);
            if (!targetIsEcho)
            {
                // Direct .text assignment on a skeleton-built label: this is
                // its own sanitize chokepoint (model-controlled input text).
                _summaryTarget.text = IconLoader.SanitizeForDisplay(target);
                _summaryTarget.style.display = DisplayStyle.Flex;
            }

            // Design section 8.2 B2(b): resolve the wire tool_use name back
            // to the registered UapOps tool (null for every non-UapOps tool
            // -- Write/Edit/Bash/etc. -- which correctly never badges).
            IUapTool resolvedTool = UapOpsServer.FindByWireName(_request.ToolName);
            _undoBadge.style.display = resolvedTool != null && !resolvedTool.Undoable
                ? DisplayStyle.Flex : DisplayStyle.None;

            var allow = new Button(OnAllowClicked) { text = L10n.S.PermAllowButton };
            allow.AddToClassList("uap-card-btn");
            allow.AddToClassList("uap-card-btn--primary");
            _summaryActions.Add(allow);

            List<JsonNode> suggestions = CollectSuggestions();
            if (suggestions.Count > 0)
            {
                Button always = null;
                always = new Button(delegate { OnAlwaysClicked(always, suggestions); });
                always.text = L10n.S.PermAlwaysButtonLabel + " " + IconLoader.GlyphChevronDown;
                always.tooltip = L10n.S.PermAlwaysTooltip;
                always.AddToClassList("uap-card-btn");
                _summaryActions.Add(always);
            }

            var deny = new Button(OnDenyClicked) { text = L10n.S.PermDenyButton };
            deny.AddToClassList("uap-card-btn");
            // 2026-09-05 UI redesign (D4): Deny keeps the plain shell (it
            // must never outrank Allow) but reads as the "stop this"
            // member of the row instead of a peer of Always.
            deny.AddToClassList("uap-card-btn--danger");
            _summaryActions.Add(deny);

            // Details: full description, monospace input preview, deny
            // reason, keyboard hint.
            if (!string.IsNullOrEmpty(_request.Description))
            {
                _details.Add(PlainLabel(_request.Description, "uap-perm-desc"));
            }

            // UXA-2 (ux-spec section 3.7): Edit/MultiEdit render a real
            // +/- line diff instead of the raw old/new block dump, so the
            // approver reads the CHANGE, not two walls of text. Anything
            // whose input lacks the edit shape falls back to the generic
            // key: value preview -- never a wrong diff.
            List<PermissionEditPreview.DiffLine> diff =
                PermissionEditPreview.IsEditTool(_request.ToolName)
                    ? PermissionEditPreview.BuildEditDiff(_request.Input)
                    : null;
            if (diff != null && diff.Count > 0)
            {
                var diffHost = new VisualElement();
                diffHost.AddToClassList("uap-perm-diff");
                RenderDiffLines(diffHost, diff, PreviewMaxLines);
                _details.Add(diffHost);
            }
            else
            {
                string preview = BuildInputPreview(_request.Input);
                if (preview.Length > 0)
                {
                    Label previewLabel = PlainLabel(preview, "uap-perm-preview");
                    MessageBlockFactory.ApplyMonoFont(previewLabel);
                    _details.Add(previewLabel);
                }
            }

            // Caption above the deny field (2026-08-14 UI polish audit
            // item 5): the field alone gave no visual cue it accepts a
            // reason FOR Claude rather than, say, a note to self.
            //
            // UXA-4: WINDOW HOST ONLY. The inline card's copy of this field
            // sat inside these collapsed details where nobody found it; the
            // inline deny reason now comes from the always-visible composer
            // (ConsumeExternalDenyMessage, wired by ChatView, placeholder
            // announcing it), which is where the ux-spec section 3.7 mock
            // put it all along. The floating PermissionWindow has no
            // composer, so it keeps the in-card field.
            if (_host == HostKind.Window)
            {
                _details.Add(PlainLabel(L10n.A(L10n.S.PermDenyFieldCaption), "uap-perm-field-caption"));
                _denyField = new TextField();
                _denyField.multiline = false;
                _denyField.AddToClassList("uap-perm-denyfield");
                _denyField.tooltip = L10n.A(L10n.S.PermDenyFieldTooltip);
                _details.Add(_denyField);
            }

            _details.Add(PlainLabel(L10n.A(L10n.S.PermKeyboardHint), "uap-perm-hint"));
        }

        private List<JsonNode> CollectSuggestions()
        {
            return CollectSuggestions(_request);
        }

        /// <summary>
        /// Pure/testable core of the "Always" menu's contents (2026-08-02
        /// design note section 2 B3): gathers whatever permission_suggestions
        /// the CLI sent verbatim, or -- ONLY when it sent none AND the wire
        /// tool name is one of ours-via-MCP ("mcp__..." -- our own UapOps
        /// tools plus any other configured MCP server) -- synthesizes the
        /// single obvious "always allow this exact tool" rule so the Always
        /// button still appears at all. Without this, an mcp__* tool that
        /// happens to get no suggestions from the CLI has no way to ever
        /// stop re-prompting every single call. Never synthesizes on TOP of
        /// a non-empty CLI-provided list -- exactly one or the other, never
        /// both, so accepting "Always" can never apply two rules at once.
        /// </summary>
        internal static List<JsonNode> CollectSuggestions(CanUseToolRequest request)
        {
            var list = new List<JsonNode>();
            if (request != null && request.PermissionSuggestions != null
                && request.PermissionSuggestions.IsArray)
            {
                foreach (JsonNode suggestion in request.PermissionSuggestions.Items)
                {
                    if (suggestion.IsObject)
                    {
                        list.Add(suggestion);
                    }
                }
            }
            // Design note 2026-09-10-auto-approve-all-tools section 2: a
            // tool-wide "always allow <Tool>" entry for EVERY tool, first
            // in the menu, unless the CLI already offered an unscoped rule
            // for this tool. The CLI's own suggestions are path- or
            // prefix-scoped ("Read(//proj/**)", "Bash(git status:*)") or a
            // mode switch; taken alone they left the user with no generic
            // grant at all, so "always" never stuck across a session. Not
            // for question cards (requires_user_interaction): those are
            // answered, not allowed.
            if (request != null && !request.RequiresUserInteraction
                && !string.IsNullOrEmpty(request.ToolName)
                && !ContainsUnscopedRuleFor(list, request.ToolName))
            {
                list.Insert(0, SynthesizeMcpAlwaysAllowRule(request.ToolName));
            }
            return list;
        }

        /// <summary>
        /// True when any addRules suggestion in <paramref name="suggestions"/>
        /// already names <paramref name="wireToolName"/> without ruleContent
        /// (an unscoped, tool-wide rule) -- in which case synthesizing one
        /// would only duplicate it.
        /// </summary>
        internal static bool ContainsUnscopedRuleFor(List<JsonNode> suggestions, string wireToolName)
        {
            for (int i = 0; i < suggestions.Count; i++)
            {
                List<string> rules = ExtractAddRuleStrings(suggestions[i]);
                for (int r = 0; r < rules.Count; r++)
                {
                    if (string.Equals(rules[r], wireToolName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsMcpToolName(string wireToolName)
        {
            return !string.IsNullOrEmpty(wireToolName)
                && wireToolName.StartsWith("mcp__", StringComparison.Ordinal);
        }

        /// <summary>
        /// Synthesizes the same wire shape a CLI-native MCP suggestion uses
        /// (measured, docs/research/08-mcp-transport.md: "a real capture
        /// against a live toy MCP server"):
        /// {"type":"addRules","rules":[{"toolName":"mcp__..."}],
        /// "behavior":"allow","destination":"localSettings"} -- so
        /// downstream code (DescribeSuggestion, OnAlwaysClicked's
        /// persistence step) never needs to distinguish a synthesized
        /// suggestion from one the CLI actually sent.
        /// </summary>
        internal static JsonNode SynthesizeMcpAlwaysAllowRule(string wireToolName)
        {
            return JsonNode.NewObject()
                .Set("type", "addRules")
                .Set("rules", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("toolName", wireToolName)))
                .Set("behavior", "allow")
                .Set("destination", "localSettings");
        }

        private void OnAllowClicked()
        {
            // Bare allow: AgentClient echoes the original input as
            // updatedInput (the verified wire shape, 02b section 2).
            AgentHub.RespondToPendingPermission(_currentRequestId,
                PermissionDecision.AllowTool());
        }

        private void OnAlwaysClicked(Button anchor, List<JsonNode> suggestions)
        {
            // Capture the id NOW: GenericMenu callbacks run after the
            // native menu closes, by which time the pending request (and
            // this card) may have moved on. AgentHub drops the decision
            // when the id no longer matches, so a suggestion captured for
            // request A can never widen permissions on a later request B.
            string requestId = _currentRequestId;
            var menu = new GenericMenu();
            for (int i = 0; i < suggestions.Count; i++)
            {
                JsonNode suggestion = suggestions[i];
                string label = FlattenMenuLabel(DescribeSuggestion(suggestion));
                menu.AddItem(new GUIContent(label), false, delegate
                {
                    JsonNode updatedPermissions = JsonNode.NewArray();
                    updatedPermissions.Add(suggestion);
                    // UICODE-6: respond FIRST and persist only when the hub
                    // ACCEPTED the decision for the live request. The old
                    // order persisted the durable panel-side allow rule
                    // unconditionally, so a menu callback firing after the
                    // request moved on (other host answered, hotkey, level
                    // raise, turn end, process death, supersession) had its
                    // wire response correctly dropped -- yet still wrote a
                    // permanent allowedTools entry for a decision the user
                    // never completed.
                    if (AgentHub.RespondToPendingPermission(requestId,
                        PermissionDecision.AllowTool(null, updatedPermissions)))
                    {
                        PersistAcceptedRuleIfApplicable(suggestion);
                    }
                });
            }
            menu.DropDown(anchor.worldBound);
        }

        /// <summary>
        /// UXA-4: where the inline host's deny reason comes from. The
        /// in-card deny field lives inside the collapsed details of the
        /// Inline card, where nobody found it -- the ux-spec (section 3.7)
        /// puts "deny + type an alternative" in the ALWAYS-VISIBLE
        /// composer, and ChatView wires this to a consume-on-read of the
        /// composer's current text (read once per deny, cleared on use so
        /// the same text cannot also be sent as a chat message). Null in
        /// the Window host, which has no composer and keeps its own field.
        /// </summary>
        internal Func<string> ConsumeExternalDenyMessage;

        /// <summary>
        /// UXA-4 pure decision: the in-card field wins when non-blank (the
        /// Window host's only entry), else the external (composer) text,
        /// else the protocol's default refusal line. Trims both; a
        /// whitespace-only entry is "no reason given", never an empty deny
        /// message on the wire.
        /// </summary>
        internal static string ResolveDenyMessage(string inCardText, string externalText)
        {
            string inCard = (inCardText ?? string.Empty).Trim();
            if (inCard.Length > 0)
            {
                return inCard;
            }
            string external = (externalText ?? string.Empty).Trim();
            if (external.Length > 0)
            {
                return external;
            }
            return DefaultDenyMessage;
        }

        private void OnDenyClicked()
        {
            string inCard = _denyField != null ? _denyField.value : null;
            // Consume the composer text only when the in-card field cannot
            // answer -- reading it always would clear a chat draft the
            // user typed for something else.
            string external = null;
            if (string.IsNullOrEmpty(inCard != null ? inCard.Trim() : null)
                && ConsumeExternalDenyMessage != null)
            {
                external = ConsumeExternalDenyMessage();
            }
            AgentHub.RespondToPendingPermission(_currentRequestId,
                PermissionDecision.DenyTool(ResolveDenyMessage(inCard, external)));
        }

        /// <summary>Menu label for one permission_suggestions entry.</summary>
        private static string DescribeSuggestion(JsonNode suggestion)
        {
            string type = suggestion["type"].AsString(string.Empty);
            if (type == "setMode")
            {
                string mode = suggestion["mode"].AsString("?");
                string destination = suggestion["destination"].AsString("session");
                return L10n.F(L10n.S.PermSetModeSuggestionFmt, mode, destination);
            }
            if (type == "addRules")
            {
                // The full rule strings, scope included: a domain-scoped
                // WebFetch rule used to be labeled as if it covered the
                // whole tool, so the user could not tell what they were
                // actually granting.
                List<string> ruleStrings = ExtractAddRuleStrings(suggestion);
                if (ruleStrings.Count > 0)
                {
                    // UXA-6: format each rule for HUMANS on the menu label
                    // only -- persistence (TryExtractAddRuleToolName ->
                    // PersistAcceptedRuleIfApplicable) keeps the raw wire
                    // grammar, or the stored rule would stop matching.
                    string[] display = new string[ruleStrings.Count];
                    for (int i = 0; i < ruleStrings.Count; i++)
                    {
                        display[i] = FormatRuleForDisplay(ruleStrings[i]);
                    }
                    return L10n.F(L10n.S.PermAddRuleSuggestionFmt,
                        string.Join(", ", display));
                }
            }
            // Unobserved suggestion kinds stay usable: show a compact JSON
            // summary and pass the node back verbatim.
            string raw = JsonWriter.Write(suggestion);
            // '/' is flattened by the caller (FlattenMenuLabel); truncate
            // on the raw text so the visible length stays the same.
            return raw.Length <= 60 ? raw : raw.Substring(0, 57) + "...";
        }

        /// <summary>
        /// Every label that goes into the Always menu passes through here.
        /// GenericMenu treats every '/' as a submenu separator, so a
        /// suggestion such as "Always allow Read(/home/user/**)" used to
        /// open as nested "Always allow Read(" > "home" > "user" > "**)"
        /// submenus. IconLoader.GlyphMenuSlash (DIVISION SLASH) renders as
        /// a slash without being one, so paths and globs stay readable
        /// (the '-' the other menus use would turn "/Assets/Scripts/" into
        /// "-Assets-Scripts-"). Applies to
        /// whatever DescribeSuggestion branch produced it: the addRules
        /// branch carries user paths and globs, setMode carries the wire
        /// destination, and the raw-JSON fallback carries anything the CLI
        /// sends. Flattening at the single AddItem site (instead of inside
        /// each branch) is what guarantees no future branch can regress
        /// into a submenu again. Display-only: the persisted rule string
        /// never comes from this method.
        /// </summary>
        internal static string FlattenMenuLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }
            return label.Replace("/", IconLoader.GlyphMenuSlash);
        }

        /// <summary>
        /// UXA-6 (SR section 4.4): the Always-allow menu used to print the
        /// raw settings-grammar rule ("Always allow
        /// mcp__unity-ops__uap_scene_create_object") -- protocol plumbing on
        /// the one label whose whole job is telling the user what they are
        /// granting. Display-only transform: the tool-name half goes through
        /// ToolCardDescriber.FormatMcpNameWithServer (server kept, per that
        /// method's authorisation-surface contract), a "(ruleContent)" scope
        /// rides along verbatim, and an MCP rule WITHOUT ruleContent -- the
        /// synthesized every-argument grant -- says so out loud instead of
        /// looking narrower than it is. Non-MCP names ("WebFetch",
        /// "Bash(git status:*)") pass through unchanged; the persisted rule
        /// string is NEVER this method's output.
        /// </summary>
        internal static string FormatRuleForDisplay(string rule)
        {
            if (string.IsNullOrEmpty(rule))
            {
                return string.Empty;
            }
            string name = rule;
            string content = null;
            int open = rule.IndexOf('(');
            if (open > 0 && rule[rule.Length - 1] == ')')
            {
                name = rule.Substring(0, open);
                content = rule.Substring(open + 1, rule.Length - open - 2);
            }
            string formatted = ToolCardDescriber.FormatMcpNameWithServer(name);
            bool isMcp = !string.Equals(formatted, name, StringComparison.Ordinal);
            if (content != null)
            {
                return formatted + "(" + content + ")";
            }
            return isMcp
                ? L10n.F(L10n.S.PermRuleAllCallsSuffixFmt, formatted)
                : formatted;
        }

        /// <summary>
        /// Extracts the single toolName out of an {"type":"addRules",
        /// "rules":[{"toolName":"..."}], ...} suggestion (the shape both
        /// the CLI itself and <see cref="SynthesizeMcpAlwaysAllowRule"/>
        /// use), or false for any other suggestion shape (e.g. setMode).
        /// </summary>
        internal static bool TryExtractAddRuleToolName(JsonNode suggestion, out string toolName)
        {
            List<string> rules = ExtractAddRuleStrings(suggestion);
            toolName = rules.Count > 0 ? rules[0] : null;
            return rules.Count > 0;
        }

        /// <summary>
        /// Every rule of an addRules suggestion, each in the settings-file
        /// rule grammar: bare "WebFetch" when the rule is unscoped,
        /// "WebFetch(domain:example.com)" when it carries ruleContent.
        ///
        /// ruleContent used to be silently DISCARDED here (2026-08-12
        /// report: "always allow" on WebFetch never stuck). That single
        /// omission broke in two directions at once. Toward the user, the
        /// Always menu labeled a domain-scoped rule as if it covered the
        /// whole tool, hiding what they were granting. Toward the settings,
        /// PersistAcceptedRuleIfApplicable stored the bare tool name -- a
        /// WIDER grant than the one the user accepted -- and the rule the
        /// CLI actually suggested (the scoped one) was never persisted
        /// panel-side at all, so whenever the CLI's own destination was
        /// "session", the grant died with the process. This panel restarts
        /// the CLI on every domain reload and settings auto-apply, so a
        /// session-destination grant that outlives a terminal session by
        /// hours dies here in minutes -- which is exactly "asked every
        /// time" as experienced from the panel.
        /// </summary>
        internal static List<string> ExtractAddRuleStrings(JsonNode suggestion)
        {
            var result = new List<string>();
            if (suggestion == null || !suggestion.IsObject)
            {
                return result;
            }
            if (suggestion["type"].AsString(string.Empty) != "addRules")
            {
                return result;
            }
            JsonNode rules = suggestion["rules"];
            if (rules == null || !rules.IsArray)
            {
                return result;
            }
            foreach (JsonNode rule in rules.Items)
            {
                string name = rule["toolName"].AsString(null);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                string content = rule["ruleContent"].AsString(null);
                result.Add(string.IsNullOrEmpty(content)
                    ? name
                    : name + "(" + content + ")");
            }
            return result;
        }

        /// <summary>
        /// Persists an accepted addRules suggestion's exact tool name into
        /// PanelSettings.allowedTools (2026-08-02 design note section 2 B3)
        /// so a LATER session's `--allowedTools` spawn argument covers it
        /// from the start, rather than relying solely on the CLI's own
        /// `destination:"localSettings"` persistence -- this panel has no
        /// way to read that back or confirm it actually happened. REUSES
        /// the existing allowed-tools store rather than inventing a second
        /// one; the shapes genuinely differ (a flat tool-name list here vs.
        /// the CLI's richer {type,rules,behavior,destination} rule object),
        /// so only the bare tool name is extracted and stored. No-op for
        /// any suggestion that is not an addRules-with-toolName shape (e.g.
        /// setMode) and for a name already present (no duplicate entries).
        /// </summary>
        internal static void PersistAcceptedRuleIfApplicable(JsonNode suggestion)
        {
            List<string> rules = ExtractAddRuleStrings(suggestion);
            if (rules.Count == 0)
            {
                return;
            }
            List<string> allowed = PanelStateStore.instance.Settings.allowedTools;
            bool changed = false;
            for (int r = 0; r < rules.Count; r++)
            {
                bool present = false;
                for (int i = 0; i < allowed.Count; i++)
                {
                    if (string.Equals(allowed[i], rules[r], StringComparison.Ordinal))
                    {
                        present = true;
                        break;
                    }
                }
                if (!present)
                {
                    allowed.Add(rules[r]);
                    changed = true;
                }
            }
            if (changed)
            {
                PanelStateStore.instance.SaveNow();
            }
        }

        /// <summary>
        /// One-line-per-key preview of the tool input (file_path / command /
        /// first lines of content). Model-controlled: rendered plain.
        /// </summary>
        /// <summary>
        /// Renders diff lines into <paramref name="host"/>, truncated to
        /// <paramref name="maxLines"/> with a "show all N lines" expander
        /// that re-renders unbounded in place (UXA-2). Static so the
        /// closure captures only the host + lines, never the card;
        /// internal so PermissionCardTests can drive the truncate/expand
        /// re-render directly (detached elements deliver no click events).
        /// </summary>
        internal static void RenderDiffLines(VisualElement host,
            List<PermissionEditPreview.DiffLine> lines, int maxLines)
        {
            host.Clear();
            int shown = Math.Min(lines.Count, maxLines);
            for (int i = 0; i < shown; i++)
            {
                host.Add(MakeDiffLineLabel(lines[i]));
            }
            if (lines.Count > maxLines)
            {
                var expand = new Button(delegate { RenderDiffLines(host, lines, int.MaxValue); });
                expand.text = L10n.F(L10n.S.PermDiffShowAllFmt, lines.Count);
                expand.AddToClassList("uap-card-btn");
                expand.AddToClassList("uap-perm-diff-expand");
                host.Add(expand);
            }
        }

        private static Label MakeDiffLineLabel(PermissionEditPreview.DiffLine line)
        {
            string prefix;
            string ussClass;
            switch (line.Kind)
            {
                case PermissionEditPreview.LineKind.Remove:
                    prefix = "- ";
                    ussClass = "uap-perm-diff-del";
                    break;
                case PermissionEditPreview.LineKind.Add:
                    prefix = "+ ";
                    ussClass = "uap-perm-diff-add";
                    break;
                case PermissionEditPreview.LineKind.Gap:
                    prefix = "  ";
                    ussClass = "uap-perm-diff-gap";
                    break;
                case PermissionEditPreview.LineKind.Separator:
                    prefix = string.Empty;
                    ussClass = "uap-perm-diff-sep";
                    break;
                default:
                    prefix = "  ";
                    ussClass = "uap-perm-diff-ctx";
                    break;
            }
            // PlainLabel sanitizes (old_string/new_string are
            // model-controlled text).
            Label label = PlainLabel(prefix + line.Text, "uap-perm-diff-line");
            label.AddToClassList(ussClass);
            MessageBlockFactory.ApplyMonoFont(label);
            return label;
        }

        private static string BuildInputPreview(JsonNode input)
        {
            if (input == null || !input.IsObject)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, JsonNode> pair in input.Properties)
            {
                string value = pair.Value.IsString
                    ? pair.Value.AsString(string.Empty)
                    : JsonWriter.Write(pair.Value);
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(pair.Key).Append(": ").Append(TruncateBlock(value));
            }
            return sb.ToString();
        }

        private static string TruncateBlock(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            if (value.Length > PreviewMaxChars)
            {
                value = value.Substring(0, PreviewMaxChars) + "...";
            }
            string[] lines = value.Split('\n');
            if (lines.Length <= PreviewMaxLines)
            {
                return value;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < PreviewMaxLines; i++)
            {
                sb.Append(lines[i].TrimEnd('\r')).Append('\n');
            }
            sb.Append(L10n.F(L10n.S.PermTruncatedMoreLinesFmt, lines.Length - PreviewMaxLines));
            return sb.ToString();
        }

        // -- AskUserQuestion variant ------------------------------------------------------

        private void BuildQuestionVariant()
        {
            _summaryTitle.text = _questions.Questions.Count == 1
                ? L10n.A(L10n.S.PermQuestionTitleSingle) : L10n.A(L10n.S.PermQuestionTitlePlural);

            _submitButton = new Button(OnSubmitClicked) { text = L10n.S.PermSubmitButton };
            _submitButton.AddToClassList("uap-card-btn");
            _submitButton.AddToClassList("uap-card-btn--primary");
            _submitButton.SetEnabled(false);
            _summaryActions.Add(_submitButton);

            var skip = new Button(OnSkipClicked) { text = L10n.S.PermSkipButton };
            skip.AddToClassList("uap-card-btn");
            // Quiet variant (2026-08-14 UI polish audit item 5, QUESTION
            // variant only): Skip is the "no answer" escape hatch, not a
            // peer of Submit -- it should not compete visually with it the
            // way the normal-tool variant's Deny legitimately competes
            // with Allow.
            skip.AddToClassList("uap-card-btn--quiet");
            skip.tooltip = L10n.S.PermSkipTooltip;
            _summaryActions.Add(skip);

            _selection = new List<HashSet<int>>();
            _optionButtons = new List<List<Button>>();
            _otherButtons = new List<Button>();
            _otherFields = new List<TextField>();
            _otherSelected = new List<bool>();
            _otherCaptions = new List<Label>();
            _questionSections = new List<VisualElement>();

            // The stepper model exists for EVERY question count -- it backs
            // the Submit gating (AllAnswered) uniformly -- but the visible
            // stepper UI (tab strip + one-section-at-a-time) only appears
            // for 2+ questions. A single question renders exactly the
            // pre-stepper layout: no tabs, its one section always shown.
            int questionCount = _questions.Questions.Count;
            var multiSelectFlags = new bool[questionCount];
            for (int i = 0; i < questionCount; i++)
            {
                multiSelectFlags[i] = _questions.Questions[i].MultiSelect;
            }
            _stepper = new QuestionStepperModel(questionCount, multiSelectFlags);

            if (questionCount >= 2)
            {
                BuildQuestionTabs(questionCount);
            }
            BuildQuestionPrompt();

            // ALL question sections are built up front (they are cheap:
            // a handful of labels/buttons each, ~175px of layout per
            // question measured in the design note) and switching merely
            // toggles style.display. Rebuilding the current section on
            // every tab switch would DESTROY the option buttons -- and
            // with them the user's in-progress multiSelect toggle state,
            // which lives partly in the buttons' --selected class and
            // would have to be re-derived; keeping the elements alive
            // makes the selection state trivially survive navigation.
            for (int q = 0; q < questionCount; q++)
            {
                AskUserQuestionInput.Question question = _questions.Questions[q];
                // Options only: the header and question text live in the
                // pinned prompt block above (_questionPrompt), never in
                // the scrolling section -- see BuildQuestionPrompt.
                var section = new VisualElement();
                section.AddToClassList("uap-perm-question");

                var selected = new HashSet<int>();
                var buttons = new List<Button>();
                for (int o = 0; o < question.Options.Count; o++)
                {
                    AskUserQuestionInput.Option option = question.Options[o];
                    int questionIndex = q;
                    int optionIndex = o;
                    var optionButton = new Button(delegate
                    {
                        OnOptionClicked(questionIndex, optionIndex);
                    });
                    // A TextElement paints its own .text across the whole
                    // content box, independent of child flex layout, so
                    // Button.text would overlap the description Label on a
                    // two-line button. Keep the button's own text empty and
                    // stack the label + description as child elements.
                    optionButton.text = string.Empty;
                    optionButton.enableRichText = false;
                    optionButton.AddToClassList("uap-perm-option");
                    Label optionLabel = PlainLabel(option.Label, "uap-perm-option-label");
                    optionLabel.pickingMode = PickingMode.Ignore;
                    optionButton.Add(optionLabel);
                    if (!string.IsNullOrEmpty(option.Description))
                    {
                        // Same untrusted string as the Label below -- must
                        // go through the same sanitize chokepoint (Unity
                        // tooltips render through the editor's default text
                        // pipeline too, so an unsanitized emoji here can
                        // reproduce the same font-warning/square-glyph bug).
                        optionButton.tooltip = IconLoader.SanitizeForDisplay(option.Description);
                        Label desc = PlainLabel(option.Description, "uap-perm-option-desc");
                        desc.pickingMode = PickingMode.Ignore;
                        optionButton.Add(desc);
                    }
                    section.Add(optionButton);
                    buttons.Add(optionButton);
                }

                // Synthetic "Other..." option (upstream contract parity:
                // the user can always answer with custom text). It is
                // deliberately option-SHAPED -- same class, same click
                // affordance -- so it reads as one more choice, with a
                // modifier class for styling/tests to tell it apart.
                int otherQuestionIndex = q;
                var otherButton = new Button(delegate
                {
                    OnOtherClicked(otherQuestionIndex);
                });
                otherButton.text = string.Empty;
                otherButton.enableRichText = false;
                otherButton.AddToClassList("uap-perm-option");
                otherButton.AddToClassList("uap-perm-option--other");
                Label otherLabel = PlainLabel(L10n.S.PermOtherOptionLabel,
                    "uap-perm-option-label");
                otherLabel.pickingMode = PickingMode.Ignore;
                otherButton.Add(otherLabel);
                section.Add(otherButton);

                // Caption above the Other field (2026-08-14 UI polish
                // audit item 5): shown/hidden together with the field
                // itself (SetOtherSelected toggles both), never on its
                // own -- an always-visible caption over a hidden field
                // would read as a floating label pointing at nothing.
                Label otherCaption = PlainLabel(
                    L10n.S.PermOtherFieldCaption, "uap-perm-field-caption");
                otherCaption.style.display = DisplayStyle.None;
                section.Add(otherCaption);

                // 2022.3's TextField has no placeholder, so the hint lives
                // in the tooltip and the field starts empty. Hidden until
                // the Other button is selected: an always-visible empty
                // field would read as a second, competing answer slot.
                var otherField = new TextField();
                otherField.multiline = false;
                otherField.AddToClassList("uap-perm-other-field");
                otherField.tooltip = L10n.S.PermOtherPlaceholder;
                otherField.style.display = DisplayStyle.None;
                otherField.RegisterValueChangedCallback(delegate
                {
                    OnOtherTextChanged(otherQuestionIndex);
                });
                otherField.RegisterCallback<KeyDownEvent>(delegate(KeyDownEvent evt)
                {
                    OnOtherFieldKeyDown(otherQuestionIndex, evt);
                });
                section.Add(otherField);

                _selection.Add(selected);
                _optionButtons.Add(buttons);
                _otherButtons.Add(otherButton);
                _otherCaptions.Add(otherCaption);
                _otherFields.Add(otherField);
                _otherSelected.Add(false);
                _questionSections.Add(section);
                _details.Add(section);
            }

            RefreshStepperVisibility();
        }

        /// <summary>
        /// Builds the stepper tab strip (2+ questions): one Button per
        /// question, inserted into _body BETWEEN the summary row and the
        /// scrolling details, so the navigation stays visible while a long
        /// question's options scroll inside _details.
        /// </summary>
        private void BuildQuestionTabs(int questionCount)
        {
            _questionTabsStrip = new VisualElement();
            _questionTabsStrip.AddToClassList("uap-perm-qtabs");
            _questionTabs = new List<Button>();
            for (int i = 0; i < questionCount; i++)
            {
                AskUserQuestionInput.Question question = _questions.Questions[i];
                int index = i;
                var tab = new Button(delegate { NavigateToQuestion(index); });
                // Header (and the tooltip's question text below) are
                // model-controlled strings: same sanitize chokepoint as
                // every other label this card renders, and rich text off
                // so markup cannot restyle the tab.
                tab.enableRichText = false;
                // Sanitize BEFORE the emptiness check, not after. A header
                // that consists only of glyphs the sanitizer strips (an
                // emoji-only header is the realistic case) passed the
                // pre-sanitize IsNullOrEmpty, then rendered as a blank,
                // unclickable-looking tab instead of falling back to Q{n}.
                string sanitizedHeader = IconLoader.SanitizeForDisplay(question.Header);
                tab.text = !string.IsNullOrEmpty(sanitizedHeader)
                    ? sanitizedHeader
                    : L10n.F(L10n.S.PermQuestionTabFallbackFmt, i + 1);
                tab.tooltip = IconLoader.SanitizeForDisplay(question.QuestionText);
                tab.AddToClassList("uap-perm-qtab");
                _questionTabsStrip.Add(tab);
                _questionTabs.Add(tab);
            }
            _body.Insert(_body.IndexOf(_details), _questionTabsStrip);
        }

        /// <summary>
        /// Builds the pinned prompt block (2026-09-12 design note): one
        /// header Label + one question-text Label, inserted into _body
        /// directly above _details (below the tab strip when there is
        /// one). It is the only place the question text is rendered --
        /// the per-question sections in _details hold options only -- so
        /// however far the options scroll, the question stays readable.
        /// flex-shrink: 0 in USS, same reasoning as the tab strip: the
        /// details area is the designated shrinker. Text is filled per
        /// current question by RefreshQuestionPrompt.
        /// </summary>
        private void BuildQuestionPrompt()
        {
            _questionPrompt = new VisualElement();
            _questionPrompt.AddToClassList("uap-perm-qprompt");
            _questionPromptHeader = PlainLabel(string.Empty, "uap-perm-qheader");
            _questionPromptHeader.style.display = DisplayStyle.None;
            _questionPrompt.Add(_questionPromptHeader);
            _questionPromptText = PlainLabel(string.Empty, "uap-perm-qtext");
            _questionPrompt.Add(_questionPromptText);
            _body.Insert(_body.IndexOf(_details), _questionPrompt);
        }

        /// <summary>
        /// Mirrors the current question into the prompt block. The header
        /// is shown only for a SINGLE question: with 2+ questions the
        /// current tab already carries it (bold, underlined) directly
        /// above, and repeating it would push the question text -- the
        /// part that was hard to see -- one line further down.
        /// </summary>
        private void RefreshQuestionPrompt()
        {
            if (_questionPrompt == null || _stepper == null || _questions == null)
            {
                return;
            }
            int index = _stepper.CurrentIndex;
            if (index < 0 || index >= _questions.Questions.Count)
            {
                return;
            }
            AskUserQuestionInput.Question question = _questions.Questions[index];
            // Model-controlled strings: same sanitize chokepoint as every
            // other label this card renders (PlainLabel does it at build
            // time; these labels are re-filled per navigation).
            string header = IconLoader.SanitizeForDisplay(question.Header);
            bool showHeader = _questions.Questions.Count == 1 && !string.IsNullOrEmpty(header);
            _questionPromptHeader.text = showHeader ? header : string.Empty;
            _questionPromptHeader.style.display = showHeader
                ? DisplayStyle.Flex : DisplayStyle.None;
            _questionPromptText.text = IconLoader.SanitizeForDisplay(question.QuestionText);
        }

        /// <summary>
        /// Free navigation to one question (tab click; also the seam the
        /// EditMode tests drive, because a detached Clickable never fires
        /// -- see UapUiClickDispatcherTests' class comment for why click
        /// simulation without a panel is not a meaningful test here).
        /// </summary>
        internal void NavigateToQuestion(int index)
        {
            if (_stepper == null)
            {
                return;
            }
            _stepper.GoTo(index);
            RefreshStepperVisibility();
        }

        /// <summary>
        /// Mirrors the stepper model into the elements: only the current
        /// question's section is displayed, the current tab carries
        /// --current and every answered tab carries --answered. With a
        /// single question there are no tabs and its one section is always
        /// current, so this reduces to keeping it visible.
        /// </summary>
        private void RefreshStepperVisibility()
        {
            if (_stepper == null || _questionSections == null)
            {
                return;
            }
            for (int i = 0; i < _questionSections.Count; i++)
            {
                bool isCurrent = i == _stepper.CurrentIndex;
                bool wasVisible = _questionSections[i].style.display != DisplayStyle.None;
                if (wasVisible && !isCurrent)
                {
                    // Two hazards travel with a section that goes
                    // display:none, both found by the diff review:
                    //
                    // 1. All sections share the ONE _details ScrollView, and
                    //    display toggling does not touch scrollOffset. After
                    //    scrolling deep into a long question, the next
                    //    question rendered pre-scrolled with its header and
                    //    question text out of view -- in exactly the
                    //    overflow condition this stepper exists for.
                    // 2. A focused element keeps receiving key events
                    //    regardless of display (this file already documents
                    //    and blurs against that in SetShownInWindow), so the
                    //    just-clicked option button would keep swallowing
                    //    Enter/Space inside its hidden section.
                    _details.scrollOffset = Vector2.zero;
                    BlurIfFocusInside(_questionSections[i]);
                }
                _questionSections[i].style.display = isCurrent
                    ? DisplayStyle.Flex : DisplayStyle.None;
                if (_questionTabs != null && i < _questionTabs.Count)
                {
                    _questionTabs[i].EnableInClassList("uap-perm-qtab--current", isCurrent);
                    _questionTabs[i].EnableInClassList("uap-perm-qtab--answered",
                        _stepper.IsAnswered(i));
                }
            }
            RefreshQuestionPrompt();
        }

        /// <summary>
        /// Blurs the panel's focused element when it sits inside
        /// <paramref name="container"/> -- the section-scoped variant of
        /// BlurIfFocusInsideCard, so hiding one question never yanks focus
        /// from the tab strip or anything else that stays visible.
        /// </summary>
        private void BlurIfFocusInside(VisualElement container)
        {
            if (container.panel == null || container.panel.focusController == null)
            {
                return;
            }
            var element = container.panel.focusController.focusedElement as VisualElement;
            if (element != null && (element == container || container.Contains(element)))
            {
                element.Blur();
            }
        }

        /// <summary>
        /// Removes the per-request stepper state. The tab strip lives in
        /// _body (not _details), so the _details.Clear() in Build()/Hide()
        /// never removes it -- without this a second request's card would
        /// stack a new strip under the previous one.
        /// </summary>
        private void ResetStepperState()
        {
            if (_questionTabsStrip != null)
            {
                _questionTabsStrip.RemoveFromHierarchy();
                _questionTabsStrip = null;
            }
            // Same hygiene for the prompt block: it lives in _body too, so
            // _details.Clear() never removes it.
            if (_questionPrompt != null)
            {
                _questionPrompt.RemoveFromHierarchy();
                _questionPrompt = null;
            }
            _questionPromptHeader = null;
            _questionPromptText = null;
            _stepper = null;
            _questionTabs = null;
            _questionSections = null;
        }

        private void OnOptionClicked(int questionIndex, int optionIndex)
        {
            if (_selection == null || questionIndex >= _selection.Count)
            {
                return;
            }
            HashSet<int> selected = _selection[questionIndex];
            bool multi = _questions.Questions[questionIndex].MultiSelect;
            if (multi)
            {
                if (!selected.Add(optionIndex))
                {
                    selected.Remove(optionIndex);
                }
            }
            else
            {
                selected.Clear();
                selected.Add(optionIndex);
                // Radio semantics extend to the synthetic Other option: a
                // real pick replaces it entirely, hiding the field and
                // DROPPING its text, so no leftover free text can ride
                // invisibly into Submit later. (multiSelect skips this --
                // there Other coexists with checked options by design.)
                SetOtherSelected(questionIndex, false);
            }

            List<Button> buttons = _optionButtons[questionIndex];
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].EnableInClassList("uap-perm-option--selected",
                    selected.Contains(i));
            }

            if (_stepper != null)
            {
                // Answered = "at least one real selection OR a selected
                // Other with non-empty text" -- mirrored into the model so
                // tab styling and Submit gating share one source of truth.
                // Must precede AdvanceAfterSingleSelect, whose
                // unanswered-search would otherwise still see this
                // question as unanswered and land right back on it.
                _stepper.NotifyAnswered(questionIndex, ComputeAnswered(questionIndex));
                if (!multi)
                {
                    // Claude Desktop behavior: a single-select pick is
                    // complete the instant it happens, so move on to the
                    // next unanswered question. multiSelect toggles NEVER
                    // navigate (no completion signal exists short of
                    // Submit -- see QuestionStepperModel's class comment).
                    _stepper.AdvanceAfterSingleSelect(questionIndex);
                }
                RefreshStepperVisibility();
            }

            RefreshSubmitEnabled();
        }

        /// <summary>
        /// The synthetic "Other..." option was clicked. Single-select: it
        /// behaves like one more radio choice (selecting it clears any real
        /// option; re-clicking keeps it selected rather than toggling off,
        /// matching how the real radio options never deselect on re-click).
        /// multiSelect: it toggles like any other option and coexists with
        /// checked options. NEVER auto-advances -- selecting Other is the
        /// START of an answer (typing follows), and advancing would yank
        /// the just-revealed field out from under the user.
        /// </summary>
        private void OnOtherClicked(int questionIndex)
        {
            if (_otherSelected == null || questionIndex >= _otherSelected.Count)
            {
                return;
            }
            bool multi = _questions.Questions[questionIndex].MultiSelect;
            bool select;
            if (multi)
            {
                select = !_otherSelected[questionIndex];
            }
            else
            {
                select = true;
                HashSet<int> selected = _selection[questionIndex];
                if (selected.Count > 0)
                {
                    selected.Clear();
                    List<Button> buttons = _optionButtons[questionIndex];
                    for (int i = 0; i < buttons.Count; i++)
                    {
                        buttons[i].EnableInClassList("uap-perm-option--selected", false);
                    }
                }
            }
            SetOtherSelected(questionIndex, select);
            if (select)
            {
                // The whole point of selecting Other is to type, so put the
                // caret there immediately. Panel guard: EditMode tests (and
                // any detached build) have no focus controller to talk to.
                TextField field = _otherFields[questionIndex];
                if (field.panel != null)
                {
                    field.Focus();
                }
            }
            if (_stepper != null)
            {
                _stepper.NotifyAnswered(questionIndex, ComputeAnswered(questionIndex));
                RefreshStepperVisibility();
            }
            RefreshSubmitEnabled();
        }

        /// <summary>
        /// Applies one question's Other selection state to the button class
        /// and the field visibility. Deselecting CLEARS the field text (one
        /// rule for both variants): the field is only ever visible while
        /// selected, so preserved-but-hidden text would be state the user
        /// cannot see, and re-selecting later would resurrect an answer
        /// they may have forgotten writing. Callers recompute the answered
        /// bit afterwards -- SetValueWithoutNotify keeps this method free
        /// of change-callback re-entry.
        /// </summary>
        private void SetOtherSelected(int questionIndex, bool selected)
        {
            if (_otherSelected == null || questionIndex >= _otherSelected.Count)
            {
                return;
            }
            _otherSelected[questionIndex] = selected;
            _otherButtons[questionIndex].EnableInClassList(
                "uap-perm-option--selected", selected);
            TextField field = _otherFields[questionIndex];
            field.style.display = selected ? DisplayStyle.Flex : DisplayStyle.None;
            if (_otherCaptions != null && questionIndex < _otherCaptions.Count)
            {
                // Same rule as the field itself -- one state, mirrored
                // into both elements (the field is only ever visible
                // while selected, and the caption exists only to label
                // it).
                _otherCaptions[questionIndex].style.display =
                    selected ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (!selected)
            {
                field.SetValueWithoutNotify(string.Empty);
            }
        }

        /// <summary>
        /// Text typed into the Other field. Only re-derives the answered
        /// state -- NEVER navigates: auto-advance mid-typing would yank the
        /// field out from under the user, so the stepper only ever moves on
        /// real single-select option clicks (and the explicit Enter in
        /// <see cref="OnOtherFieldKeyDown"/>). RefreshStepperVisibility is
        /// safe to call per keystroke: CurrentIndex is unchanged, so it
        /// only restyles the answered tab -- no section is hidden, no
        /// scroll reset, no blur.
        /// </summary>
        private void OnOtherTextChanged(int questionIndex)
        {
            if (_stepper == null || _otherFields == null
                || questionIndex >= _otherFields.Count)
            {
                return;
            }
            _stepper.NotifyAnswered(questionIndex, ComputeAnswered(questionIndex));
            RefreshStepperVisibility();
            RefreshSubmitEnabled();
        }

        /// <summary>
        /// Enter inside the Other field. The Y/N hotkeys are already
        /// unreachable here -- OnKeyDown returns immediately for the whole
        /// question variant -- so this handler exists for the OPPOSITE
        /// decision: Enter with non-empty text is the one explicit "done
        /// typing" signal a free-text answer has (unlike a keystroke, which
        /// says nothing), so in the stepper it advances to the next
        /// unanswered question exactly like a real single-select click
        /// would. multiSelect stays put by the same rule as its options
        /// (AdvanceAfterSingleSelect refuses multiSelect indices, and the
        /// user may still be toggling); empty text stays put because there
        /// is no answer to be done WITH. Propagation stops in every Enter
        /// case so no ancestor can reinterpret the key while it belongs to
        /// the field.
        /// </summary>
        private void OnOtherFieldKeyDown(int questionIndex, KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
            {
                return;
            }
            evt.StopPropagation();
            evt.PreventDefault();
            HandleOtherFieldEnter(questionIndex);
        }

        /// <summary>Non-event core of the Enter behavior (also the test
        /// seam target -- synthetic KeyDownEvents never fire on detached
        /// elements, same measured fact as clicks).</summary>
        private void HandleOtherFieldEnter(int questionIndex)
        {
            if (_stepper == null || _otherFields == null
                || questionIndex >= _otherFields.Count)
            {
                return;
            }
            if (!_otherSelected[questionIndex])
            {
                return;
            }
            if (EffectiveOtherText(_otherFields[questionIndex].value).Length == 0)
            {
                return;
            }
            // NotifyAnswered already ran via the value-changed callback, so
            // the advance search sees this question as answered.
            _stepper.AdvanceAfterSingleSelect(questionIndex);
            RefreshStepperVisibility();
        }

        /// <summary>
        /// One question's answered bit: at least one real selection, or a
        /// selected Other whose EFFECTIVE text is non-empty. Uses the same
        /// EffectiveOtherText the payload uses so Submit gating can never
        /// diverge from what Submit would actually send (e.g. an emoji-only
        /// entry the sanitizer strips to nothing must not enable Submit and
        /// then submit the question unanswered).
        /// </summary>
        private bool ComputeAnswered(int questionIndex)
        {
            if (_selection[questionIndex].Count > 0)
            {
                return true;
            }
            return _otherSelected != null && questionIndex < _otherSelected.Count
                && _otherSelected[questionIndex]
                && EffectiveOtherText(_otherFields[questionIndex].value).Length > 0;
        }

        private void RefreshSubmitEnabled()
        {
            // Submit stays gated on every question being answered; the
            // model's AllAnswered is fed exclusively by the NotifyAnswered
            // calls, so tab styling and this gate cannot disagree.
            if (_submitButton != null && _stepper != null)
            {
                _submitButton.SetEnabled(_stepper.AllAnswered);
            }
        }

        // -- Test seams (InternalsVisibleTo Colloid.AgentPanel.Editor.Tests) --------
        //
        // A detached Button's Clickable never fires from a synthetic
        // ClickEvent (measured in this repo -- see UapUiClickDispatcherTests'
        // class comment for the incident history), so EditMode tests drive
        // the same handlers the clicks are wired to instead of dispatching
        // events and asserting on silence.

        /// <summary>Test seam: exactly what an option button click runs.</summary>
        internal void SelectOptionForTests(int questionIndex, int optionIndex)
        {
            OnOptionClicked(questionIndex, optionIndex);
        }

        /// <summary>Test seam: exactly what the Other button click runs.</summary>
        internal void SelectOtherForTests(int questionIndex)
        {
            OnOtherClicked(questionIndex);
        }

        /// <summary>
        /// Test seam: types into the Other field. Sets the value WITHOUT
        /// notify and then runs the same handler the value-changed callback
        /// runs, because a detached BaseField's value setter never raises
        /// ChangeEvent (panel == null takes the SetValueWithoutNotify
        /// branch in 2022.3) -- the same "synthetic events do not fire
        /// detached" fact the class comment pins for clicks.
        /// </summary>
        internal void SetOtherTextForTests(int questionIndex, string text)
        {
            if (_otherFields == null || questionIndex >= _otherFields.Count)
            {
                return;
            }
            _otherFields[questionIndex].SetValueWithoutNotify(text ?? string.Empty);
            OnOtherTextChanged(questionIndex);
        }

        /// <summary>Test seam: exactly what Enter inside the Other field
        /// runs (minus the event plumbing).</summary>
        internal void EnterInOtherFieldForTests(int questionIndex)
        {
            HandleOtherFieldEnter(questionIndex);
        }

        /// <summary>Test seam: the live stepper model (null outside the
        /// question variant).</summary>
        internal QuestionStepperModel QuestionStepperForTests
        {
            get { return _stepper; }
        }

        /// <summary>
        /// Gathers the current card state into the exact answer pairs
        /// OnSubmitClicked sends. Split from OnSubmitClicked so the payload
        /// rules are assertable in EditMode without touching AgentHub (the
        /// respond call is process-global state no test should wire up).
        /// </summary>
        internal List<KeyValuePair<string, string>> CollectAnswersForSubmit()
        {
            if (_questions == null)
            {
                return new List<KeyValuePair<string, string>>();
            }
            List<string> otherTexts = null;
            if (_otherFields != null)
            {
                otherTexts = new List<string>();
                for (int i = 0; i < _otherFields.Count; i++)
                {
                    otherTexts.Add(_otherFields[i].value);
                }
            }
            return CollectAnswers(_questions, _selection, _otherSelected, otherTexts);
        }

        /// <summary>
        /// Pure payload-collection step: per question, the sorted selected
        /// real-option labels plus -- when Other is selected and its
        /// effective text is non-empty -- that text appended LAST (after
        /// the real labels, before JoinLabels). A question with neither
        /// contributes no pair at all (the verified "unanswered" shape).
        ///
        /// Single-select consequences fall out rather than being special-
        /// cased: mutual exclusion upstream guarantees an empty selection
        /// set whenever Other contributes, so the answer string is the bare
        /// sanitized text -- deliberately NOT prefixed with "Other:" or
        /// similar, because the answer goes to the model as the user's
        /// answer, same as any option label would.
        ///
        /// The text passes through EffectiveOtherText (trim + the same
        /// SanitizeForDisplay chokepoint every model-facing string here
        /// uses), which is also the gate ComputeAnswered applies -- one
        /// rule, so Submit can never enable on text that would then
        /// contribute nothing.
        /// </summary>
        internal static List<KeyValuePair<string, string>> CollectAnswers(
            AskUserQuestionInput questions,
            IList<HashSet<int>> selection,
            IList<bool> otherSelected,
            IList<string> otherTexts)
        {
            var answers = new List<KeyValuePair<string, string>>();
            if (questions == null || selection == null)
            {
                return answers;
            }
            for (int q = 0; q < questions.Questions.Count && q < selection.Count; q++)
            {
                AskUserQuestionInput.Question question = questions.Questions[q];
                var labels = new List<string>();
                foreach (int index in SortedIndices(selection[q]))
                {
                    if (index >= 0 && index < question.Options.Count)
                    {
                        labels.Add(question.Options[index].Label);
                    }
                }
                if (otherSelected != null && q < otherSelected.Count && otherSelected[q]
                    && otherTexts != null && q < otherTexts.Count)
                {
                    string otherText = EffectiveOtherText(otherTexts[q]);
                    if (otherText.Length > 0)
                    {
                        labels.Add(otherText);
                    }
                }
                if (labels.Count == 0)
                {
                    continue;
                }
                if (labels.Count > 1)
                {
                    // 02b section 5: the multiSelect reply format is
                    // unverified upstream; we join with ", " and log so a
                    // silent no-answer can be diagnosed from the Console.
                    Debug.Log("[AgentPanel] AskUserQuestion multiSelect answer sent as a"
                        + " comma-joined string (format unverified upstream): \""
                        + AskUserQuestionInput.JoinLabels(labels) + "\"");
                }
                answers.Add(new KeyValuePair<string, string>(
                    question.QuestionText, AskUserQuestionInput.JoinLabels(labels)));
            }
            return answers;
        }

        /// <summary>
        /// The one normalization applied to Other text, shared by the
        /// answered gate and the payload: trim (whitespace is not an
        /// answer), then the same IconLoader.SanitizeForDisplay chokepoint
        /// as every other model-facing string this card handles.
        /// </summary>
        internal static string EffectiveOtherText(string rawFieldValue)
        {
            return IconLoader.SanitizeForDisplay(
                (rawFieldValue ?? string.Empty).Trim());
        }

        private void OnSubmitClicked()
        {
            if (_questions == null || _request == null)
            {
                return;
            }
            List<KeyValuePair<string, string>> answers = CollectAnswersForSubmit();
            if (answers.Count == 0)
            {
                return; // Submit is disabled until every question is answered.
            }
            JsonNode updatedInput = AskUserQuestionInput.BuildAnswersUpdatedInput(
                _request.Input, answers);
            // No transcript note (2026-08-02 design note section 2 --
            // ALLOW never gets one): this is still an ALLOW decision, so a
            // custom row here would be dropped by AgentHub anyway.
            AgentHub.RespondToPendingPermission(_currentRequestId,
                PermissionDecision.AllowTool(updatedInput));
        }

        private void OnSkipClicked()
        {
            if (_request == null)
            {
                return;
            }
            // Allow with an empty answers object: the CLI reports the
            // questions as unanswered in its own wording (02b section 5).
            JsonNode updatedInput = AskUserQuestionInput.BuildAnswersUpdatedInput(
                _request.Input, null);
            AgentHub.RespondToPendingPermission(_currentRequestId,
                PermissionDecision.AllowTool(updatedInput));
        }

        private static List<int> SortedIndices(HashSet<int> set)
        {
            var list = new List<int>(set);
            list.Sort();
            return list;
        }

        // -- Expansion / hosting state --------------------------------------------------

        private bool IsExpandedEffective()
        {
            // The floating window exists to show the details comfortably;
            // it never collapses.
            return _host == HostKind.Window || _expanded;
        }

        private void OnChevronClicked()
        {
            _expanded = !_expanded;
            UpdateExpansion();
        }

        private void UpdateExpansion()
        {
            bool expanded = IsExpandedEffective();
            _details.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            if (_questionTabsStrip != null)
            {
                // The tab strip is navigation FOR the details area; a
                // collapsed inline card shows only the summary row, so the
                // strip must fold away with the details it navigates.
                _questionTabsStrip.style.display = expanded
                    ? DisplayStyle.Flex : DisplayStyle.None;
            }
            // The class carries min-height: var(--uap-perm-card-min) -- the
            // token floor that outranks the computed max-height cap.
            _root.EnableInClassList("uap-perm--expanded",
                expanded && _host == HostKind.Inline && !_shownInWindow);
            if (_chevron != null)
            {
                _chevron.text = expanded
                    ? IconLoader.GlyphChevronDown : IconLoader.GlyphChevronRight;
            }
            ApplyHeightCap();
        }

        private void ApplyWindowMode()
        {
            if (_host != HostKind.Inline)
            {
                return;
            }
            _body.style.display = _shownInWindow ? DisplayStyle.None : DisplayStyle.Flex;
            if (_waitBar != null)
            {
                _waitBar.style.display = _shownInWindow
                    ? DisplayStyle.Flex : DisplayStyle.None;
            }
            UpdateExpansion();
        }

        private void ApplyHeightCap()
        {
            if (_host == HostKind.Inline && !_shownInWindow
                && IsExpandedEffective() && _maxCardHeight > 0f)
            {
                _root.style.maxHeight = _maxCardHeight;
            }
            else
            {
                _root.style.maxHeight = StyleKeyword.Null;
            }
        }

        private void RaiseOpenInWindow()
        {
            Action handler = OpenInWindowRequested;
            if (handler != null)
            {
                handler();
            }
        }

        private void RaiseShowInline()
        {
            Action handler = ShowInlineRequested;
            if (handler != null)
            {
                handler();
            }
        }

        // -- Keyboard -----------------------------------------------------------------

        private void OnKeyDown(KeyDownEvent evt)
        {
            // Y/N shortcuts apply to the normal variant only, and never
            // while the user is typing a deny reason. Escape is deliberately
            // NOT handled: it remains the global interrupt.
            if (_isQuestionVariant || _request == null)
            {
                return;
            }
            // Wait-bar mode: the actions live in the floating window, so a
            // grant must never happen through the invisible inline body.
            if (_host == HostKind.Inline && _shownInWindow)
            {
                return;
            }
            var target = evt.target as VisualElement;
            if (_denyField != null && target != null
                && (target == _denyField || _denyField.Contains(target)))
            {
                return;
            }
            if (evt.keyCode == KeyCode.Y)
            {
                evt.StopPropagation();
                evt.PreventDefault();
                OnAllowClicked();
            }
            else if (evt.keyCode == KeyCode.N)
            {
                evt.StopPropagation();
                evt.PreventDefault();
                OnDenyClicked();
            }
        }

        /// <summary>
        /// Focuses the card so Y/N work immediately -- but never steals
        /// focus from a text field (the user may be typing an alternative in
        /// the composer while the request arrives).
        /// </summary>
        private void FocusUnlessTyping()
        {
            // Method group: the parameterless overload of Execute is
            // ambiguous for anonymous delegates in 2022.3.
            _root.schedule.Execute(FocusNowUnlessTyping).StartingIn(1);
        }

        private void FocusNowUnlessTyping()
        {
            if (_currentRequestId == null || _root.panel == null)
            {
                return;
            }
            // Never self-focus while the inline card is only a wait bar
            // (the actions are in the floating window), nor in a window
            // host that was opened automatically (SetAutoFocusEnabled):
            // both would arm Y/N somewhere the user is not looking.
            if (!_autoFocusEnabled || _shownInWindow)
            {
                return;
            }
            Focusable focused = _root.panel.focusController != null
                ? _root.panel.focusController.focusedElement : null;
            var element = focused as VisualElement;
            if (element != null && IsTextInput(element))
            {
                return;
            }
            _root.Focus();
        }

        /// <summary>
        /// Drops focus when it currently sits on the card root or any of
        /// its children (deny field included), so key events stop routing
        /// into a card whose body is hidden.
        /// </summary>
        private void BlurIfFocusInsideCard()
        {
            if (_root.panel == null || _root.panel.focusController == null)
            {
                return;
            }
            var element = _root.panel.focusController.focusedElement as VisualElement;
            if (element != null && (element == _root || _root.Contains(element)))
            {
                element.Blur();
            }
        }

        private static bool IsTextInput(VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.parent)
            {
                if (current is TextField)
                {
                    return true;
                }
            }
            return false;
        }

        // -- Shared -----------------------------------------------------------------------

        /// <summary>
        /// Injection-safe label (all card text may be model-controlled).
        /// enableRichText = false blocks markup injection; SanitizeForDisplay
        /// additionally drops/curated-maps emoji the editor fonts have no
        /// glyph for (see IconLoader.SanitizeForDisplay's doc comment for
        /// the dropped ranges and rationale) -- this is a real bypass fixed
        /// here: PlainLabel previously only ever disabled rich text.
        /// </summary>
        private static Label PlainLabel(string text, string ussClass)
        {
            var label = new Label(IconLoader.SanitizeForDisplay(text ?? string.Empty));
            label.enableRichText = false;
            if (!string.IsNullOrEmpty(ussClass))
            {
                label.AddToClassList(ussClass);
            }
            return label;
        }
    }
}
