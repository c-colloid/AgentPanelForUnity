namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Typed UI string table (docs/design-notes/2026-08-01-i18n.md #2): one
    /// public readonly field per user-visible string in the panel. This
    /// class IS the English catalog -- every field's inline initializer
    /// below is the single source of the English text, so `new UiStrings()`
    /// always yields English regardless of any language setting.
    ///
    /// UiStringsJa.Create() builds the Japanese catalog through the second
    /// (internal) constructor below, which takes every field as a REQUIRED
    /// parameter -- adding a field here without updating that constructor
    /// (and therefore every language factory) is a COMPILE ERROR, not a
    /// silently-still-English string at runtime. That is the whole reason
    /// this is a typed table instead of a Dictionary&lt;string,string&gt;.
    ///
    /// Sentence-building rule: every field that embeds a value uses a
    /// "...Fmt" name and `{0}`/`{1}`/... placeholders consumed through
    /// L10n.F (a string.Format wrapper) -- never string concatenation,
    /// because word order is not the same across languages.
    /// </summary>
    public sealed class UiStrings
    {
        // ==================================================================
        // FirstRunView.cs
        // ==================================================================

        public readonly string FirstRunCliNotFoundTitle = "Claude Code CLI not found";

        /// <summary>Single sentence; previously built by concatenating two literals.</summary>
        public readonly string FirstRunCliNotFoundBody =
            "The Agent Panel runs the Claude Code CLI as a subprocess. Install it,"
            + " or point the panel at an existing executable.";

        public readonly string FirstRunBrowseButton = "Browse...";
        public readonly string FirstRunRedetectButton = "Re-detect";
        public readonly string FirstRunLoginTitle = "Sign in to Claude";

        /// <summary>UXO-4 lead: says what the primary button actually does.</summary>
        public readonly string FirstRunLoginLead =
            "The Claude Code CLI is installed but not signed in. Press Log in"
            + " to start the CLI's sign-in flow: the Settings Agent card"
            + " opens with a browser link and a field for the confirmation"
            + " code, and the panel reconnects on its own once you finish.";

        /// <summary>UXO-4: title of the collapsed terminal-path foldout.</summary>
        public readonly string FirstRunLoginAltFoldout = "Another way: sign in from a terminal";

        /// <summary>UXO-4: terminal-path instructions inside the foldout.</summary>
        public readonly string FirstRunLoginAltBody =
            "Run the command below in a terminal, use the /login command"
            + " inside it, then press Check again here when it is done.";

        /// <summary>
        /// Single sentence (rewritten v0.40.0, docs/design-notes/2026-09-
        /// 10-claude-api-key-auth-passthrough.md): subscription login is
        /// the recommended path, but the panel no longer tells the user to
        /// avoid ANTHROPIC_API_KEY -- it now respects whichever auth the
        /// CLI itself picks and shows which one is active in Settings.
        /// </summary>
        public readonly string FirstRunLoginBody2 =
            "Sign in with a Claude Pro/Max or Console account -- the recommended path."
            + " An ANTHROPIC_API_KEY in this editor's environment is respected too and"
            + " switches billing to that key; Settings > Agent shows which one is active.";

        public readonly string FirstRunCheckAgainButton = "Check again";
        public readonly string FirstRunBrowseDialogTitle = "Select the claude executable";

        // ==================================================================
        // ResumeBanner.cs
        // ==================================================================

        public readonly string BannerContinueButton = "Continue";
        public readonly string BannerReconnectButton = "Reconnect";
        public readonly string BannerErroredText = "The agent process exited.";
        public readonly string BannerInterruptedText = "The last turn was interrupted by a script reload.";
        public readonly string BannerReconnectingText = "Reconnecting to the previous session...";
        public readonly string BannerResumedText = "Session resumed.";

        // ==================================================================
        // PermissionCard.cs
        // ==================================================================

        public readonly string PermWaitingInWindowText = "Waiting for permission - shown in window";
        public readonly string PermShowHereButton = "Show here";
        public readonly string PermShowHereTooltip = "Close the floating window and answer inline";
        public readonly string PermChevronTooltip = "Show or hide the request details";
        public readonly string PermOpenWindowTooltip = "Open this request in a floating window";

        /// <summary>{0} = tool display name. Used when the request has no description; see PermTitleWithDescriptionFmt.</summary>
        public readonly string PermTitleFmt = "{agent} wants to use {0}";

        /// <summary>{0} = tool display name, {1} = description.</summary>
        public readonly string PermTitleWithDescriptionFmt = "{agent} wants to use {0} - {1}";

        /// <summary>
        /// 2026-09-06 review fix 1: the INLINE card's title. The warn icon,
        /// the card colour and the Allow/Deny row already say "permission";
        /// the words "Claude wants to use" only pushed the tool name and its
        /// target off a 300-600px row. {0} = tool display name, {1} =
        /// description. Without a description the title is the bare name.
        /// The Window host keeps PermTitleFmt (it doubles as the window title).
        /// </summary>
        public readonly string PermInlineTitleWithDescriptionFmt = "{0} - {1}";

        public readonly string PermAllowButton = "Allow (Y)";
        public readonly string PermAlwaysButtonLabel = "Always";
        public readonly string PermAlwaysTooltip = "Always allow (choose a scope)";
        /// <summary>
        /// Design note 2026-09-10 section 1: shown on the permission card
        /// when more can_use_tool requests wait behind the visible one in
        /// AgentClient's FIFO (parallel tool_use blocks). {0} = count of
        /// requests waiting, EXCLUDING the one on screen.
        /// </summary>
        public readonly string PermQueueDepthFmt = "{0} more waiting";
        public readonly string PermDenyButton = "Deny (N)";
        public readonly string PermDenyFieldTooltip = "Optional deny reason sent to {agent}";

        /// <summary>Single sentence; previously built by concatenating two literals.</summary>
        public readonly string PermKeyboardHint =
            "Y allows, N denies while this card has focus. Type an alternative"
            + " above before denying to redirect {agent}.";

        /// <summary>{0} = permission mode, {1} = destination (e.g. project/local).</summary>
        public readonly string PermSetModeSuggestionFmt = "Set permission mode: {0} ({1})";

        /// <summary>{0} = number of hidden lines.</summary>
        public readonly string PermTruncatedMoreLinesFmt = "... ({0} more lines)";

        public readonly string PermQuestionTitleSingle = "{agent} has a question";
        public readonly string PermQuestionTitlePlural = "{agent} has questions";
        /// <summary>
        /// Tab chip for a question that supplied no 'header' -- {0} is the
        /// 1-based question number. Headers are optional in the tool schema,
        /// and an empty tab is unclickable in practice.
        /// </summary>
        public readonly string PermQuestionTabFallbackFmt = "Q{0}";
        /// <summary>
        /// The synthetic free-input choice appended to every AskUserQuestion
        /// option list. The tool's own upstream contract promises the user
        /// can always answer with custom text; a card that only renders the
        /// offered options silently breaks that promise (2026-08-12 report).
        /// </summary>
        public readonly string PermOtherOptionLabel = "Other...";
        public readonly string PermOtherPlaceholder = "Type your answer";
        /// <summary>
        /// Persistent captions above the two free-text fields (2026-08-14
        /// audit): 2022.3 TextField has no placeholder, so without a
        /// caption each field rests as an unlabeled blank box whose purpose
        /// only a hover tooltip reveals.
        /// </summary>
        public readonly string PermOtherFieldCaption = "Your answer";
        public readonly string PermDenyFieldCaption = "Tell {agent} what to do instead (optional)";
        public readonly string PermSubmitButton = "Submit";
        public readonly string PermSkipButton = "Skip";
        public readonly string PermSkipTooltip = "Continue without answering";

        // ==================================================================
        // PermissionWindow.cs
        // ==================================================================

        public readonly string PermWindowTitle = "{agent} - Permission Request";

        // ==================================================================
        // ContextBarView.cs
        // ==================================================================

        public readonly string CtxAttachSelectionButton = "+ Selection";
        public readonly string CtxAttachSelectionTooltip = "Attach the current Hierarchy/Project selection as context";
        public readonly string CtxAttachSceneButton = "+ Scene";
        public readonly string CtxAttachSceneTooltip = "Attach a summary of the active scene as context";
        public readonly string CtxSceneAttachedButton = "Scene attached";
        public readonly string CtxFixErrorsButton = "Ask {agent} to fix";
        public readonly string CtxRemoveTooltip = "Remove";

        /// <summary>{0} = selection summary text.</summary>
        public readonly string CtxSelectionChipTitleFmt = "Selection: {0}";

        /// <summary>{0} = scene name.</summary>
        public readonly string CtxSceneChipLabelFmt = "Scene: {0}";

        /// <summary>Also used verbatim as the displayed chat message, so it renders exactly as typed.</summary>
        public readonly string CtxFixErrorsPrompt =
            "Fix the Unity console errors listed in the attached context."
            + " Investigate each cause and apply the fixes.";

        public readonly string CtxConsoleErrorTitleSingle = "Console error (1)";

        /// <summary>{0} = error count (used when count &gt; 1; see CtxConsoleErrorTitleSingle).</summary>
        public readonly string CtxConsoleErrorTitlePluralFmt = "Console errors ({0})";

        public readonly string CtxDefaultChipLabel = "(context)";
        public readonly string CtxPingTooltip = "Click to ping in Unity";
        public readonly string CtxErrorSingle = "1 console error";

        /// <summary>{0} = error count (used when count &gt; 1; see CtxErrorSingle).</summary>
        public readonly string CtxErrorPluralFmt = "{0} console errors";

        /// <summary>Error chip X tooltip (UXO-3): the bare X is transient now, not a permanent ignore.</summary>
        public readonly string CtxErrorDismissForNowTooltip =
            "Hide these errors for now. They come back for new errors or after a reload; use the menu to ignore permanently.";

        /// <summary>Error chip menu button tooltip (UXO-3).</summary>
        public readonly string CtxErrorMenuTooltip = "More actions for these errors";

        /// <summary>Error chip menu item that persists the permanent ignore (UXO-3).</summary>
        public readonly string CtxErrorIgnoreForeverMenuItem = "Ignore these errors from now on";

        /// <summary>Undo notice after a permanent ignore, 1 error (UXO-3; see CtxErrorIgnoredNoticePluralFmt).</summary>
        public readonly string CtxErrorIgnoredNoticeSingle = "Ignored 1 console error.";

        /// <summary>{0} = error count (used when count &gt; 1; see CtxErrorIgnoredNoticeSingle).</summary>
        public readonly string CtxErrorIgnoredNoticePluralFmt = "Ignored {0} console errors.";

        /// <summary>Button on the ignore notice that reverses the just-persisted ignore (UXO-3).</summary>
        public readonly string CtxErrorIgnoreUndoButton = "Undo";

        /// <summary>{0} = GameObject name. Shared with SelectionContextProvider.cs.</summary>
        public readonly string CtxGameObjectTitleFmt = "GameObject: {0}";

        /// <summary>{0} = type, {1} = name (non-GameObject selection fallback).</summary>
        public readonly string CtxObjectTitleTypeFirstFmt = "{0}: {1}";

        /// <summary>Shared with SelectionContextProvider.cs.</summary>
        public readonly string CtxUnnamedFallback = "(unnamed)";

        // ==================================================================
        // ToolCardDescriber.cs / ToolActivityCard.cs (shared fallback)
        // ==================================================================

        /// <summary>Generic tool-name fallback; shared by ToolCardDescriber.cs and ToolActivityCard.cs.</summary>
        public readonly string ToolCardDefaultName = "Tool";

        // ==================================================================
        // HistoryView.cs
        // ==================================================================

        public readonly string HistoryTitle = "History";
        public readonly string HistoryRefreshTooltip = "Refresh the session list";
        public readonly string HistoryEmptyMessage = "No saved sessions found for this project yet.";
        public readonly string HistoryNoPreview = "(no preview available)";
        public readonly string HistoryCurrentBadge = "Current";
        public readonly string HistorySwitchConfirmText = "Switch to this session? The current conversation will be paused.";
        public readonly string HistoryCancelButton = "Cancel";
        public readonly string HistorySwitchButton = "Switch";
        public readonly string HistoryRelativeJustNow = "just now";
        public readonly string HistoryRelativeOneMinuteAgo = "1 minute ago";

        /// <summary>{0} = minute count (2+).</summary>
        public readonly string HistoryRelativeMinutesAgoFmt = "{0} minutes ago";

        public readonly string HistoryRelativeOneHourAgo = "1 hour ago";

        /// <summary>{0} = hour count (2+).</summary>
        public readonly string HistoryRelativeHoursAgoFmt = "{0} hours ago";

        public readonly string HistoryRelativeOneDayAgo = "1 day ago";

        /// <summary>{0} = day count (2+).</summary>
        public readonly string HistoryRelativeDaysAgoFmt = "{0} days ago";

        public readonly string HistoryRelativeOneMonthAgo = "1 month ago";

        /// <summary>{0} = month count (2+).</summary>
        public readonly string HistoryRelativeMonthsAgoFmt = "{0} months ago";

        public readonly string HistoryRelativeOneYearAgo = "1 year ago";

        /// <summary>{0} = year count (2+).</summary>
        public readonly string HistoryRelativeYearsAgoFmt = "{0} years ago";

        /// <summary>{0} = byte count.</summary>
        public readonly string HistorySizeBytesFmt = "{0} B";

        /// <summary>{0} = size in KB.</summary>
        public readonly string HistorySizeKbFmt = "{0} KB";

        /// <summary>{0} = size in MB.</summary>
        public readonly string HistorySizeMbFmt = "{0} MB";

        // -- History browsing (v0.15.0): search, grouping, row actions -----
        // Group HEADERS are localized here; the group KEYS they render come
        // from HistoryListModel and are stable sentinels that must never be
        // translated (the UI maps key -> label, see HistoryView).

        public readonly string HistorySearchPlaceholder = "Search sessions";
        public readonly string HistorySearchNoMatch = "No sessions match this search.";
        public readonly string HistoryGroupByLabel = "Group by";
        public readonly string HistoryGroupByDate = "Date";
        public readonly string HistoryGroupByScene = "Scene";
        public readonly string HistoryGroupByProject = "Project";
        public readonly string HistoryGroupByCustom = "Custom group";
        public readonly string HistoryShowArchivedLabel = "Show archived";
        public readonly string HistoryGroupPinned = "Pinned";
        public readonly string HistoryGroupToday = "Today";
        public readonly string HistoryGroupYesterday = "Yesterday";
        public readonly string HistoryGroupLast7 = "Previous 7 days";
        public readonly string HistoryGroupLast30 = "Previous 30 days";
        public readonly string HistoryGroupOlder = "Older";
        public readonly string HistoryGroupNoScene = "Scene not recorded";
        public readonly string HistoryGroupNoProject = "Project unknown";
        public readonly string HistoryGroupUngrouped = "Ungrouped";

        /// <summary>{0} = how many further sessions are hidden.</summary>
        public readonly string HistoryShowMoreFmt = "Show {0} more";

        public readonly string HistoryMenuTooltip = "Session actions";
        public readonly string HistoryActionPin = "Pin";
        public readonly string HistoryActionUnpin = "Unpin";
        public readonly string HistoryActionRename = "Rename...";
        public readonly string HistoryActionArchive = "Archive";
        public readonly string HistoryActionUnarchive = "Unarchive";
        public readonly string HistoryActionDelete = "Delete...";
        public readonly string HistoryActionGroupSubmenu = "Move to group";
        public readonly string HistoryActionGroupNone = "(none)";
        public readonly string HistoryActionGroupNew = "New group...";
        /// <summary>
        /// Row-menu popup additions (2026-08-14 history-menu design note):
        /// the back row of the group page, and per-reason tooltips for the
        /// two disabled-Delete cases -- GenericMenu could gray an item out
        /// but never explain WHY; the popup can.
        /// </summary>
        public readonly string HistoryMenuGroupBack = "Back";
        public readonly string HistoryDeleteDisabledForeignTooltip =
            "This transcript belongs to another project; delete it from that project's panel.";

        /// <summary>Row menu's restore action (UXIA-1: the keyboard/menu path to what a row click does).</summary>
        public readonly string HistoryActionOpen = "Open";
        public readonly string HistoryOpenDisabledForeignTooltip =
            "This session belongs to another project; open it from that project's panel.";
        public readonly string HistoryDeleteDisabledLiveTooltip =
            "This conversation is currently running; stop or switch away from it first.";
        public readonly string HistoryRenamePlaceholder = "Conversation title";
        public readonly string HistoryRenameSaveButton = "Save";
        public readonly string HistoryRenameResetButton = "Reset";

        /// <summary>{0} = folder the transcript is moved to.</summary>
        public readonly string HistoryDeleteConfirmTextFmt =
            "Delete this session? The transcript is moved to {0} and can be restored by hand.";

        public readonly string HistoryDeleteButton = "Delete";
        public readonly string HistoryDeleteFailed =
            "Could not move the transcript. It may be open in another program.";
        public readonly string HistoryNewGroupPlaceholder = "Group name";
        public readonly string HistoryNewGroupCreate = "Create";
        public readonly string HistoryForeignProjectNote =
            "This session belongs to another Unity project and cannot be resumed here.";

        // ==================================================================
        // HeaderView.cs
        // ==================================================================

        public readonly string HeaderDefaultTitle = "New session";

        /// <summary>Model picker's initial text; shared with StatusBarView.cs.</summary>
        public readonly string StatusModelUnknown = "-";

        public readonly string HeaderModelPickerTooltip = "Switch model";
        public readonly string HeaderHistoryButton = "History";
        public readonly string HeaderHistoryTooltip = "Session history";

        /// <summary>
        /// 2026-09-06 review fix 2: on a narrow dock the model chip and the
        /// auto-approve chip fold into one chip (showing the model name)
        /// whose menu offers both, giving the session title its width back.
        /// </summary>
        public readonly string HeaderCompactTooltip = "Model and auto-approve level";
        /// <summary>{0} = current model name.</summary>
        public readonly string HeaderCompactModelItemFmt = "Model: {0}...";
        /// <summary>{0} = current auto-approve level name.</summary>
        public readonly string HeaderCompactAutoApproveItemFmt = "Auto-approve: {0}...";
        public readonly string HeaderNewSessionTooltip = "Start a new session";
        public readonly string HeaderSettingsTooltip = "Settings";
        public readonly string HeaderReconnectTooltip = "The {agent} process exited -- click to reconnect";

        /// <summary>{0} = model display name, {1} = model description.</summary>
        public readonly string HeaderModelOptionWithDescriptionFmt = "{0} ({1})";
        public readonly string HeaderModelPickerConnectFirst = "Models can be chosen after connecting";

        // -- Auto-approve level (v0.16.0) ----------------------------------
        // Level names are shared by the header control and the Settings
        // dropdown so the two surfaces can never drift apart in wording.

        public readonly string AutoApproveLevelAsk = "Ask every time";
        public readonly string AutoApproveLevelReadOnly = "Read-only Unity ops";
        public readonly string AutoApproveLevelUndoable = "Undoable Unity ops";
        public readonly string AutoApproveLevelAllUnityOps = "All Unity ops";
        /// <summary>Design note 2026-09-10 (auto-approve-all-tools): every tool except questions.</summary>
        public readonly string AutoApproveLevelAllTools = "All tools (Bash, edits, MCP)";

        // Compact forms for the header chip. Measured in the live panel:
        // with the full names the chip was squeezed to its 46px min-width
        // and showed about three characters -- worse than useless, since a
        // truncated level name reads as a different level. The header shows
        // these; the menu, the Settings dropdown and the tooltip all keep
        // the full names above.
        public readonly string AutoApproveShortAsk = "Ask";
        public readonly string AutoApproveShortReadOnly = "Read";
        public readonly string AutoApproveShortUndoable = "Undoable";
        public readonly string AutoApproveShortAllUnityOps = "All ops";
        public readonly string AutoApproveShortAllTools = "All tools";

        public readonly string AutoApproveMenuTitle = "Auto-approve Unity operations";

        /// <summary>{0} = the current level's name.</summary>
        public readonly string HeaderAutoApproveTooltipFmt =
            "Auto-approve: {0}. Click to change -- it takes effect immediately,"
            + " including for a request already waiting. Shell commands, file"
            + " edits and uloop always ask, at every level.";

        /// <summary>UXA-3: EditorUtility.DisplayDialog title for escalating to AllUnityOps.</summary>
        public readonly string AutoApproveConfirmAllTitle =
            "Auto-approve ALL Unity operations?";

        /// <summary>UXA-3: DisplayDialog body -- must spell out the irreversibility and the immediacy.</summary>
        public readonly string AutoApproveConfirmAllBody =
            "Every Unity operation the agent requests will run without asking --"
            + " including operations Ctrl+Z cannot undo. The end-of-turn warning"
            + " becomes a notification after the fact, not a chance to stop."
            + " This takes effect immediately, including for a request already"
            + " waiting.";

        /// <summary>Design note 2026-09-10 (auto-approve-all-tools): DisplayDialog for the AllTools escalation.</summary>
        public readonly string AutoApproveConfirmAllToolsTitle =
            "Auto-approve EVERY tool?";
        public readonly string AutoApproveConfirmAllToolsBody =
            "Every permission request -- shell commands, file edits, web access, all Unity operations,"
            + " other MCP servers -- will be approved without asking. Only questions the agent asks you"
            + " (AskUserQuestion) still wait for your answer. The script-validation gate still blocks"
            + " direct .cs writes. This takes effect immediately, including for a request already"
            + " waiting.";

        /// <summary>UXA-3: DisplayDialog confirm button.</summary>
        public readonly string AutoApproveConfirmAllConfirmButton = "Approve everything";

        /// <summary>UXA-3: DisplayDialog cancel button.</summary>
        public readonly string AutoApproveConfirmAllCancelButton = "Cancel";

        // -- Phase 5c: uLoop integration + L3 auto-continue ------------------

        // Auto-continue transcript notes. These are USER-facing, so they are
        // localized like every other AgentHub system note. The continuation
        // turn's own text is deliberately NOT here: that one is model-facing
        // wire content and stays English, exactly like the UapOps steering
        // section (see AutoContinueAfterCompilePolicy.ComposeContinuationMessage).
        public readonly string HubAutoContinuePendingWillContinue =
            "This turn committed staged scripts. Unity will compile and reload the domain shortly,"
            + " and the panel will send a continuation turn automatically once that finishes,"
            + " carrying the compile result.";
        public readonly string HubAutoContinuePendingOff =
            "This turn committed staged scripts. Unity will compile and reload the domain shortly."
            + " The panel will reconnect automatically, but auto-continue after compile is off in"
            + " Settings, so you will need to prompt again once it finishes.";
        public readonly string HubAutoContinueResuming =
            "Auto-continue: sent \"continue\" after the compile and reload your script commit caused.";

        // -- Compaction notes (docs/design-notes/2026-09-07-slash-commands-and-compaction.md section 2.3);
        //    shared by AgentHub (live) and TranscriptLoader (restore) via CompactionNote.Describe. --
        /// <summary>{0} = short token count before the compaction (e.g. "84.2k").</summary>
        public readonly string HubCompactedManualFmt =
            "Context compacted with /compact ({0} tokens before). The conversation above is now a summary for the model.";
        public readonly string HubCompactedManual =
            "Context compacted with /compact. The conversation above is now a summary for the model.";
        /// <summary>{0} = short token count before the compaction.</summary>
        public readonly string HubCompactedAutoFmt =
            "The CLI auto-compacted the context because the window was nearly full ({0} tokens before)."
            + " The conversation above is now a summary for the model.";
        public readonly string HubCompactedAuto =
            "The CLI auto-compacted the context because the window was nearly full."
            + " The conversation above is now a summary for the model.";
        /// <summary>
        /// Retracts HubAutoContinueResuming. That note is written the moment
        /// the send is attempted, so every path that then drops the message
        /// -- the 60s timeout, a second domain reload wiping the statics --
        /// has to say so, or the transcript keeps asserting that a turn is
        /// coming which never arrives. {0} is the reason.
        /// </summary>
        public readonly string HubAutoContinueSendAbandonedFmt =
            "Auto-continue: the queued continuation could not be delivered ({0}) and was dropped."
            + " Nothing was sent -- prompt the agent yourself to continue.";
        public readonly string HubAutoContinueInterruptedResuming =
            "Auto-continue: sent \"continue\" after a domain reload interrupted the turn.";
        /// <summary>
        /// Design note 2026-09-10 section 3: transcript note after a
        /// domain reload discarded a permission card that was still
        /// waiting for the user's answer. {0} = the tool's display name.
        /// </summary>
        public readonly string HubReloadDroppedPermissionFmt =
            "The permission request for {0} was discarded by the domain reload. It was neither"
            + " allowed nor denied, and the tool did not run; the agent will ask again if it still"
            + " needs it.";
        /// <summary>
        /// Design note 2026-09-12: the panel is showing an empty transcript
        /// because the saved one could not be READ (a lock that outlived
        /// every retry), not because anything was lost. Says so, and says
        /// that saving is suspended -- otherwise an empty panel after a
        /// compile is indistinguishable from "my history was deleted",
        /// which is exactly how this defect was reported.
        /// </summary>
        public readonly string HubSessionCacheUnreadable =
            "The saved transcript could not be read (the file is locked by another program) and the"
            + " panel is showing an empty conversation. Nothing was lost: saving is suspended so the"
            + " file stays intact, and the transcript comes back on the next domain reload. The"
            + " conversation itself is unaffected -- the agent still has its full context.";

        public readonly string SettingsUloopSectionTitle = "uLoop integration";
        public readonly string SettingsUloopStatusInstalled = "Installed";
        public readonly string SettingsUloopStatusMissing = "Not installed";
        public readonly string SettingsUloopInstallButton = "Install uLoop";
        public readonly string SettingsUloopInstallConfirmTitle =
            "Install uLoop? Review the change first.";
        public readonly string SettingsUloopInstallApply = "Install";
        public readonly string SettingsUloopInstallCancel = "Cancel";

        /// <summary>{0} = the Git URL that would be added.</summary>
        public readonly string SettingsUloopInstallViaGitFmt =
            "Adds the package via the Package Manager API: {0}. Your manifest.json is not hand-edited.";

        /// <summary>
        /// One line, no paths -- the measured card carried two full file
        /// paths inline (2026-08-12 note section 1) and the paths moved to
        /// SettingsUloopInstallPathsTooltipFmt on the card's own tooltip.
        /// </summary>
        public readonly string SettingsUloopInstallViaManifest =
            "Adds an OpenUPM scoped registry to manifest.json, then asks Package Manager for the package.";
        /// <summary>{0} = manifest path, {1} = backup path (hover detail).</summary>
        public readonly string SettingsUloopInstallPathsTooltipFmt =
            "Edits {0}. A copy of the current file is saved to {1} first.";
        public readonly string SettingsUloopInstallDiffFoldout = "Show changes";
        /// <summary>{0} = elapsed whole seconds since Apply.</summary>
        public readonly string SettingsUloopInstallingFmt =
            "Installing... {0}s (Package Manager is resolving from OpenUPM; measured around 30s)";
        /// <summary>{0} = Package Manager's own error message.</summary>
        public readonly string SettingsUloopInstallAsyncFailedFmt =
            "Package Manager could not resolve the package: {0}. The registry entry in manifest.json is"
            + " harmless to keep; fix the connection or retry from the Package Manager window.";
        public readonly string SettingsUloopInstallStalled =
            "Still not resolved after several minutes -- check the Package Manager window for the reason.";

        // -- Unity official plugin (design note 2026-09-10 section 2) ------------
        public readonly string SettingsUnityPluginSectionTitle = "Unity official plugin";
        public readonly string SettingsUnityPluginStatusCliUnavailable =
            "Claude Code CLI not found, so the plugin status is unknown.";
        public readonly string SettingsUnityPluginStatusNotInstalled = "Not installed.";
        public readonly string SettingsUnityPluginStatusDisabled =
            "Installed but disabled. Enable it from a terminal: claude plugin enable unity@unity-agent-plugin";
        /// <summary>{0} = plugin version. No session observed yet.</summary>
        public readonly string SettingsUnityPluginStatusInstalledFmt =
            "Installed (v{0}). Each new chat loads it; skills appear as /unity:...";
        /// <summary>{0} = plugin version. A session was observed and did not load it.</summary>
        public readonly string SettingsUnityPluginStatusEnabledNotLoadedFmt =
            "Installed (v{0}), but the current chat did not load it. Start a new chat.";
        /// <summary>{0} = plugin version.</summary>
        public readonly string SettingsUnityPluginStatusLoadedFmt =
            "Loaded in the current chat (v{0}). Skills appear as /unity:...";
        /// <summary>{0} = plugin_errors[].message from the CLI.</summary>
        public readonly string SettingsUnityPluginStatusLoadErrorFmt = "Failed to load: {0}";
        public readonly string SettingsUnityPluginInstallButton = "Install plugin";
        public readonly string SettingsUnityPluginInstallConfirmTitle =
            "Adds Unity's official plugin to your Claude Code user settings.";
        public readonly string SettingsUnityPluginCaveatScope =
            "Installs at user scope (~/.claude), so it also applies to Claude Code in your other projects.";
        public readonly string SettingsUnityPluginCaveatUnity6 =
            "The plugin's skills target Unity 6+. On older Unity versions some of its guidance will not apply.";
        public readonly string SettingsUnityPluginInstallApply = "Install";
        public readonly string SettingsUnityPluginInstallCancel = "Cancel";
        /// <summary>{0} = elapsed whole seconds.</summary>
        public readonly string SettingsUnityPluginInstallingFmt =
            "Installing... {0}s (registers the marketplace, then installs; measured about 5s)";
        public readonly string SettingsUnityPluginInstalled =
            "Installed. New chats will load it; the current chat will not.";
        /// <summary>{0} = stage name, {1} = exit code, {2} = the CLI's last output line (may be empty).</summary>
        public readonly string SettingsUnityPluginInstallFailedFmt = "{0} failed (exit code {1}). {2}";
        public readonly string SettingsUnityPluginStageMarketplace = "Marketplace registration";
        public readonly string SettingsUnityPluginStageInstall = "Plugin install";
        public readonly string SettingsUnityPluginInstallStalled =
            "Still not confirmed after several minutes. Check from a terminal: claude plugin list";
        public readonly string SettingsUnityPluginDetailsFoldout = "Details";
        public readonly string SettingsUnityPluginDetailsBody =
            "Provided by Unity Technologies under the Unity Companion License; not bundled with this package,"
            + " the Claude Code CLI installs it. Remove from a terminal: claude plugin uninstall unity@unity-agent-plugin";
        public readonly string SettingsUnityPluginRepoLabel = "Repository";
        public readonly string SettingsUnityPluginSteeringLabel = "Tell Claude how to use the official skills";
        public readonly string SettingsUnityPluginSteeringTooltip =
            "Adds a short note to the system prompt: route UI, package and search work through the /unity: skills,"
            + " keep Editor control on the panel's own tools, and skip Unity 6-only guidance on older versions."
            + " Applies from the next chat.";

        public readonly string SettingsUloopCaveatVcc =
            "This project is managed by VCC/VPM (vpm-manifest.json found). Package changes made"
            + " here can be reverted or overwritten by VCC -- prefer installing through VCC.";
        public readonly string SettingsUloopCaveatOffline =
            "No network connection was detected. Installing needs to reach the package registry.";
        public readonly string SettingsUloopCaveatManifestUnreadable =
            "manifest.json could not be read or parsed, so the change cannot be previewed safely.";
        public readonly string SettingsUloopCaveatRegistryConflict =
            "A scoped registry with the same name already exists with different settings."
            + " Resolve it by hand rather than letting this overwrite it.";
        public readonly string SettingsUloopInstallFailed =
            "The install could not be applied. Nothing was changed (any backup taken was restored).";

        /// <summary>
        /// Shown ONLY for the two failure codes where the manifest write
        /// failed AND the restore that should have undone it also failed --
        /// the one case where the file's state is genuinely unknown. It used
        /// to render SettingsUloopInstallFailed, which tells the user
        /// "nothing was changed" at exactly the moment that is least true,
        /// and the CRITICAL log naming the backup went nowhere because the
        /// caller passed no logger.
        /// </summary>
        public readonly string SettingsUloopInstallFailedManifestUnknownFmt =
            "The install failed AND the automatic undo failed. Packages/manifest.json may be incomplete"
            + " -- do not let Unity resolve packages before you check it. A copy of the original is at"
            + " {0}. Details: {1}";

        public readonly string SettingsUloopPresetButton = "Allow uloop commands";
        public readonly string SettingsUloopPresetHelp =
            "Allows compile, get-logs, list and run-tests without asking. Everything else still asks first.";
        public readonly string SettingsUloopPresetTooltip =
            "Adds a short allowlist of safe uloop subcommands. Everything absent from it still asks first -- including execute-dynamic-code, which compiles and runs arbitrary C# in the editor. The three commands that have frozen this editor before (update, sync, launch) are additionally blocked outright, and any broad Bash(uloop *) already in your allowed tools is removed.";
        public readonly string SettingsUloopPresetApplied = "Preset added to allowed tools.";

        public readonly string SettingsUloopSnippetButton = "Insert guidance snippet";
        public readonly string SettingsUloopSnippetHelp =
            "Appends an instruction preferring this panel's Unity tools over uloop execute-dynamic-code.";
        public readonly string SettingsUloopSnippetTooltip =
            "Appends a short instruction to your custom instructions: prefer this panel's own Unity tools, reach for uloop execute-dynamic-code only when they cannot express the operation, and treat writing and compiling code as the last resort.";
        public readonly string SettingsUloopSnippetApplied = "Snippet appended to custom instructions.";

        public readonly string SettingsAutoContinueLabel = "Continue automatically after a compile";
        public readonly string SettingsAutoContinueHelp =
            "Off by default. Sends a continuation turn after the agent's own script changes trigger a reload, always announced in the transcript. Chains until the agent stops committing, the CLI is suspended, or you press Stop.";
        public readonly string SettingsAutoContinueTooltip =
            "When the agent's own script changes trigger a compile and domain reload, the panel sends a continuation turn carrying the compile result, so the work does not simply stop there. Only for the agent's own .cs/.asmdef changes, and never a silent background turn -- the transcript always says it happened. A continuation that commits more scripts continues again after that reload too, so a write-compile-fix loop runs unattended; it ends when the agent stops committing, when the CLI connection is suspended after repeated crashes, or when you press Stop.";

        public readonly string SettingsAutoContinueInterruptedLabel = "Continue automatically after an interruption";
        public readonly string SettingsAutoContinueInterruptedHelp =
            "Off by default. Sends \"continue\" by itself when a reload or Play Mode cuts a turn short.";
        public readonly string SettingsAutoContinueInterruptedTooltip =
            "When a domain reload the agent did not cause -- you saved a script and came back, recompiled, or entered Play Mode -- interrupts a running turn, the panel resumes the session and sends the same \"continue\" the banner button would, instead of waiting for the click. No fixed limit on how many times: it stops only while the connection is suspended after repeated CLI exits, or when you press Interrupt, and it is always announced in the transcript.";

        /// <summary>
        /// Design note 2026-09-10 section 4: live readout of the project's
        /// Enter Play Mode Options, placed under the interrupted-turn
        /// auto-continue toggle. Two inline lines (one per state) plus a
        /// shared tooltip and a button that opens Project Settings.
        /// </summary>
        public readonly string SettingsPlayModeReloadOnHint =
            "This project reloads the domain when entering Play Mode, so pressing Play interrupts a running turn.";
        public readonly string SettingsPlayModeReloadOffHint =
            "This project keeps the domain on Play (Reload Domain is off), so pressing Play does not interrupt a turn.";
        public readonly string SettingsPlayModeReloadTooltip =
            "Read from Project Settings > Editor > Enter Play Mode Settings. When \"Reload Domain\" is on (Unity's default), entering Play Mode reloads the domain and the panel must kill and resume the CLI, which interrupts a running turn; leaving Play Mode does not reload. Turning Reload Domain off keeps a running turn alive across Play, but only if your project tolerates unreset static state. Script recompiles reload the domain regardless of this setting. The panel never changes this setting for you.";
        public readonly string SettingsPlayModeOpenProjectSettingsButton = "Open Project Settings > Editor";

        public readonly string SettingsAutoApproveLabel = "Auto-approve level";
        public readonly string SettingsAutoApproveHelp =
            "Levels below \"All tools\" cover only this panel's Unity tools; \"All tools\" approves everything but questions.";
        public readonly string SettingsAutoApproveTooltip =
            "Ask / Read-only / Undoable / All Unity ops apply only to this panel's own Unity tools -- shell commands, file edits and other MCP servers still show a card at those levels. \"All tools\" approves every permission request except questions the agent asks you (AskUserQuestion). The script-validation gate, and the end-of-turn warning about operations Undo cannot take back, stay active at every level including the most permissive one.";

        /// <summary>Cross-reference left in the UapOps section after the level moved next to the permission mode (UXIA-3/4).</summary>
        public readonly string SettingsAutoApproveMovedHint =
            "The auto-approve level is set in the Conversation section, next to the permission mode.";
        public readonly string SettingsAutoApproveMovedTooltip =
            "It governs which of this panel's own Unity tools run without asking, so it lives with the other permission controls.";

        // ==================================================================
        // ToolActivityCard.cs
        // ==================================================================

        public readonly string ToolCardSectionInput = "Input";
        public readonly string ToolCardSectionResult = "Result";
        public readonly string ToolCardSectionError = "Error";
        /// <summary>Title of the Write/Edit/MultiEdit file-change view (design note 2026-09-13-toolcard-vertex-limit.md).</summary>
        public readonly string ToolCardSectionChanges = "Changes";
        /// <summary>{0} = diff lines not rendered past ToolActivityCard.DiffHardMaxLines.</summary>
        public readonly string ToolCardMoreLinesFmt = "... {0} more lines (Copy for the full text)";
        /// <summary>{0} = characters not rendered past ToolActivityCard.SectionMaxChars.</summary>
        public readonly string ToolCardMoreCharsFmt = "... {0} more characters (Copy for the full text)";

        /// <summary>{0} = elapsed seconds. Shared by SubagentCard via ToolActivityCard.FormatSeconds.</summary>
        public readonly string ToolCardDurationSecondsFmt = "{0}s";

        /// <summary>{0} = tool count. Used when duration is unknown/zero; see ToolGroupCountWithDurationFmt.</summary>
        public readonly string ToolGroupCountFmt = "{0} tools";

        /// <summary>{0} = tool count, {1} = formatted duration.</summary>
        public readonly string ToolGroupCountWithDurationFmt = "{0} tools ({1})";

        // ==================================================================
        // MessageBlockFactory.cs
        // ==================================================================

        public readonly string ChatRoleUser = "You";
        public readonly string ChatRoleAssistant = "{agent}";
        public readonly string ChatDefaultAttachTitle = "Attached context";
        public readonly string ChatThinkingStreaming = "Thinking...";
        public readonly string ChatThinkingDone = "Thinking";

        /// <summary>
        /// Compact one-line indicator show in place of the foldout when a
        /// Thinking block's text is empty (design note 2026-08-01-thinking-
        /// content-loss.md section 5 point 1: the CLI never sends thinking
        /// body text, only a token estimate). The non-Fmt variants below are
        /// used when the estimate is 0/unknown, omitting the parenthetical
        /// entirely rather than showing "(~0 tokens)".
        /// </summary>
        public readonly string ChatThinkingIndicatorStreaming = "Thinking...";

        /// <summary>{0} = estimated thinking tokens so far. See ChatThinkingIndicatorStreaming.</summary>
        public readonly string ChatThinkingIndicatorStreamingTokensFmt = "Thinking... (~{0} tokens)";

        public readonly string ChatThinkingIndicatorDone = "Thought";

        /// <summary>{0} = estimated thinking tokens. See ChatThinkingIndicatorDone.</summary>
        public readonly string ChatThinkingIndicatorDoneTokensFmt = "Thought (~{0} tokens)";

        // ==================================================================
        // MessageListController.cs
        // ==================================================================

        /// <summary>{0} = number of hidden earlier messages.</summary>
        public readonly string ChatPruneNoteFmt = "{0} earlier messages are hidden";

        public readonly string ChatJumpToLatestButton = "Latest";

        /// <summary>Spinner row pinned under the transcript while AgentHub.IsCompacting
        /// (docs/design-notes/2026-09-10-compacting-indicator.md).</summary>
        public readonly string ChatCompactingIndicator =
            "Compacting the context... The model is summarizing the conversation so far; this can take a while.";

        // ==================================================================
        // StatusBarView.cs
        // ==================================================================

        public readonly string StatusDisconnected = "Disconnected";
        public readonly string StatusCliNotFound = "CLI not found";
        public readonly string StatusConnecting = "Connecting...";
        public readonly string StatusConnectingSlow = "Still connecting... (taking longer than usual)";
        public readonly string StatusIdle = "Idle";
        public readonly string StatusResponding = "Responding...";
        /// <summary>Status text while AgentHub.IsCompacting (docs/design-notes/2026-09-10-compacting-indicator.md).</summary>
        public readonly string StatusCompacting = "Compacting context...";
        public readonly string StatusRunningTool = "Running tool...";
        public readonly string StatusWaitingPermission = "Waiting for permission";
        public readonly string StatusError = "Error";

        /// <summary>Settings auto-apply transient (docs/design-notes/2026-08-01-settings-auto-apply.md).</summary>
        public readonly string StatusApplyingSettings = "Applying settings...";

        /// <summary>{0} = context-window usage percent.</summary>
        public readonly string StatusContextPercentFmt = "{0}%";

        public readonly string StatusContextWarnTooltip =
            "Context window is nearly full; the CLI may auto-compact soon.";

        /// <summary>Meter label while AgentHub.ContextUnknownAfterCompaction (design note 2026-09-07 section 2.2).</summary>
        public readonly string StatusContextCompactedLabel = "compacted";
        public readonly string StatusContextCompactedTooltip =
            "The context was just compacted; the meter shows a real number again when the next turn completes.";

        public readonly string StatusUsageZeroTokens = "0 tok";

        /// <summary>{0} = formatted token count.</summary>
        public readonly string StatusUsageTokensFmt = "{0} tok";

        /// <summary>{0} = formatted token count, {1} = formatted cost (USD, no symbol).</summary>
        public readonly string StatusUsageTokensWithCostFmt = "{0} tok - ${1}";

        public readonly string StatusUsagePopoverTitle = "Usage this session";
        public readonly string StatusUsagePopoverEmpty = "No completed turn yet.";
        /// <summary>
        /// Shown when the SESSION has completed turns (restored totals sit
        /// right above this popover) but the per-model breakdown is absent
        /// -- a cache written before the breakdown was persisted (pre-
        /// v0.20.1). Telling the user 'no completed turn yet' next to a
        /// visible token total was a lie the 2026-08-05 screenshot caught.
        /// </summary>
        public readonly string StatusUsagePopoverBreakdownPending =
            "Totals were restored from the session cache. The per-model breakdown appears after the next completed turn.";

        /// <summary>{0} = input tokens, {1} = output tokens, {2} = cache tokens.</summary>
        public readonly string StatusUsagePopoverLineFmt = "in {0} - out {1} - cache {2}";

        /// <summary>{0} = base usage line (StatusUsagePopoverLineFmt's result), {1} = formatted cost.</summary>
        public readonly string StatusUsagePopoverCostSuffixFmt = "{0} - ${1}";

        /// <summary>{0} = context-window usage percent, {1} = formatted total token capacity.</summary>
        public readonly string StatusUsagePopoverContextFmt = "context: {0}% of {1}";

        // ==================================================================
        // EmptyStateView.cs
        // ==================================================================

        public readonly string EmptyTitle = "Agent Panel for Unity";
        public readonly string EmptySubtitle = "Ask {agent} anything about this project.";

        /// <summary>Also used as the chip label and the inserted prompt text.</summary>
        public readonly string EmptySuggestionProject = "Explain how this project is organized.";

        public readonly string EmptySuggestionScene = "Summarize the structure of the open scene.";
        public readonly string EmptySuggestionErrors = "Fix the console errors.";
        public readonly string EmptySuggestionSelection = "Explain the selected object.";

        // ==================================================================
        // ComposerView.cs
        // ==================================================================

        public readonly string ComposerQuickButton = "Quick";
        public readonly string ComposerQuickTooltip = "Insert a quick action";

        // -- Image attachments (design note 2026-09-07 section 2) --
        public readonly string ComposerAttachButton = "+";
        public readonly string ComposerAttachTooltip = "Attach an image: a file, or the Scene/Game view as it is now";
        public readonly string ComposerImageOnlyDisplay = "(image)";
        public readonly string AttachMenuImageFile = "Image file...";
        public readonly string AttachMenuSceneView = "Scene view";
        public readonly string AttachMenuGameView = "Game view";
        public readonly string AttachMenuSceneViewWindow = "Scene view (as displayed)";
        public readonly string AttachImageRemoveTooltip = "Remove this image";
        public readonly string AttachImageErrorTitle = "Attach image";
        public readonly string AttachImageErrorOk = "OK";
        /// <summary>{0} = file name.</summary>
        public readonly string AttachImageUnsupportedFmt = "{0}: only PNG and JPEG files can be attached.";
        /// <summary>{0} = path.</summary>
        public readonly string AttachImageMissingFmt = "File not found: {0}";
        /// <summary>{0} = file name, {1} = reason.</summary>
        public readonly string AttachImageLoadFailedFmt = "{0} could not be read as an image ({1}).";
        /// <summary>{0} = name, {1} = size in MB, {2} = cap in MB.</summary>
        public readonly string AttachImageTooLargeFmt = "{0} is {1} MB after encoding; the limit is {2} MB.";
        /// <summary>{0} = max images per message.</summary>
        public readonly string AttachImageTooManyFmt = "At most {0} images per message.";
        public readonly string AttachMenuClipboard = "Paste image from clipboard";
        public readonly string AttachClipboardEmpty = "No image in the clipboard.";
        /// <summary>{0} = reason from the OS helper.</summary>
        public readonly string AttachClipboardFailedFmt = "Could not read the clipboard image ({0}).";
        public readonly string ChatImageOpenTooltip = "Open the image";
        public readonly string ChatImageMissing = "(image file no longer available)";
        public readonly string ChatImageDefaultCaption = "image";
        public readonly string ComposerSendButton = "Send";
        public readonly string ComposerStopButton = "Stop";
        public readonly string ComposerNoQuickActionsMenuItem = "No quick actions yet (add some in Settings)";
        public readonly string ComposerQuickActionUntitled = "(untitled)";
        public readonly string ComposerPlaceholderCtrlEnter = "Ask {agent}... (Ctrl+Enter to send)";
        public readonly string ComposerPlaceholderEnter = "Ask {agent}... (Enter to send, Shift+Enter for newline)";
        public readonly string ComposerPlaceholderPermissionPending = "Type what to do instead, then press Deny...";
        public readonly string ComposerHintCompiling = "Unity is compiling; messages will be queued.";

        /// <summary>{0} = queued message count.</summary>
        public readonly string ComposerHintQueuedFmt = "{0} message(s) queued";

        public readonly string ComposerHintEscToStop = "Esc to stop";
        public readonly string ComposerHintTurnSendAndStop = "Enter sends into the running turn; Esc stops it";
        public readonly string ComposerSendTooltip = "Send the message";
        public readonly string ComposerStopTooltip = "Stop the current response";

        // -- Slash-command popup (docs/design-notes/2026-09-07-slash-commands-and-compaction.md section 1) --
        /// <summary>Hint under the field while the popup is open.</summary>
        public readonly string ComposerSlashHint = "Up/Down to choose, Tab to complete, Enter to send";
        public readonly string ComposerSlashNoMatch = "No matching command";
        /// <summary>Description shown for /compact when the CLI reported none.</summary>
        public readonly string SlashCompactDescription =
            "Summarize the conversation so far to free up context (optional: instructions for the summary)";
        /// <summary>Description for /clear -- always the panel's own, since the panel handles it (New chat).</summary>
        public readonly string SlashClearDescription = "Start a new chat (same as the New chat button)";

        // ==================================================================
        // SubagentCard.cs
        // ==================================================================

        public readonly string SubagentDefaultType = "Subagent";
        public readonly string SubagentDefaultDescription = "Subagent";

        /// <summary>{0} = number of omitted earlier steps.</summary>
        public readonly string SubagentDropNoteFmt = "{0} earlier steps omitted";

        /// <summary>{0} = progress text so far, {1} = last tool name.</summary>
        public readonly string SubagentProgressToolFmt = "{0} ({1})";

        /// <summary>{0} = prior progress text, {1} = total token count.</summary>
        public readonly string SubagentProgressTokensFmt = "{0} -- {1} tokens";

        // -- Running-subagent progress row (v0.17.0) -----------------------
        // Measured on a long subagent run: task_progress arrives per tool
        // step with a changing description, but the FIRST one only lands
        // ~5.7s after the subagent spawns, and gaps between them run
        // 2.4-4.4s. The row used to hide itself whenever it had nothing yet,
        // which is exactly the window the user could not tell work from a
        // stall. These three fill that gap without inventing data.

        /// <summary>Shown until the first task_progress arrives.</summary>
        public readonly string SubagentProgressWorking = "Working...";

        /// <summary>{0} = prior progress text, {1} = completed tool-call count.</summary>
        public readonly string SubagentProgressStepsFmt = "{0} -- {1} steps";

        /// <summary>
        /// {0} = prior progress text, {1} = whole seconds since the last
        /// task_progress. Deliberately worded as time since the last UPDATE,
        /// not as subagent state: the number is the panel's own wall clock,
        /// and claiming it describes the subagent would be inventing data.
        /// </summary>
        public readonly string SubagentProgressUpdatedAgoFmt = "{0} -- updated {1}s ago";

        // ==================================================================
        // SettingsView.cs
        // ==================================================================

        public readonly string SettingsTitle = "Settings";

        /// <summary>
        /// 2026-09-05 UI redesign (settings, S1): the fifteen cards are
        /// grouped under four headings so a scroll of the page reads as
        /// four topics, not fifteen equal boxes.
        /// </summary>
        public readonly string SettingsGroupConversation = "Agent";
        public readonly string SettingsGroupDisplay = "Panel";
        public readonly string SettingsGroupUnity = "Unity integration";
        public readonly string SettingsGroupConnection = "Connection & account";

        public readonly string SettingsCliPathLabel = "Executable path";

        /// <summary>Two-sentence hint; previously built by concatenating two literals.</summary>
        public readonly string SettingsCliPathHint =
            "Leave empty to auto-detect.";
        public readonly string SettingsCliPathTooltip =
            "Applies after the next reconnect. Use Reconnect now below to apply it now.";

        public readonly string SettingsReconnectNowButton = "Reconnect now";

        /// <summary>{0} = resolved CLI path.</summary>
        public readonly string SettingsCliResolvedFmt = "Resolved: {0}";

        /// <summary>{0} = semicolon-joined list of checked candidate paths.</summary>
        public readonly string SettingsCliNotFoundFmt = "Not found. Checked: {0}";

        public readonly string SettingsReconnectPendingHint = "Some changes will apply the next time you reconnect.";
        public readonly string SettingsReconnectPendingPill = "Pending";

        /// <summary>
        /// Settings auto-apply (docs/design-notes/2026-08-01-settings-auto-
        /// apply.md): shown instead of SettingsReconnectPendingHint/Pill
        /// while a change is deferred waiting for the running turn to end
        /// (AgentHub.IsAutoApplyDeferred), since applying no longer requires
        /// the user to click "Reconnect now" themselves.
        /// </summary>
        public readonly string SettingsReconnectPendingHintDeferred =
            "This will apply automatically once the current turn ends.";

        public readonly string SettingsReconnectPendingPillDeferred = "Apply after turn ends";
        public readonly string SettingsSectionConversation = "Conversation";
        public readonly string SettingsPermissionModeLabel = "Permission mode";

        public readonly string SettingsPermissionModeTooltip =
            "Applies immediately if {agent} is connected, and is remembered for the next session.";

        public readonly string SettingsCtrlEnterLabel = "Send with Ctrl+Enter";

        public readonly string SettingsCtrlEnterHint =
            "When off, Enter sends and Shift+Enter inserts a newline (the IME-friendly default).";
        public readonly string SettingsCtrlEnterTooltip =
            "Applies immediately.";

        public readonly string SettingsAllowedToolsLabel = "Allowed tools";

        public readonly string SettingsAllowedToolsHint =
            "One tool per line, e.g. Bash(git:*). Empty means no restriction.";
        public readonly string SettingsAllowedToolsTooltip =
            "Applies after the next reconnect.";

        public readonly string SettingsDisallowedToolsLabel = "Disallowed tools";

        public readonly string SettingsDisallowedToolsHint =
            "One tool per line.";
        public readonly string SettingsDisallowedToolsTooltip =
            "Applies after the next reconnect.";

        public readonly string SettingsDangerZoneTitle = "Danger zone";

        /// <summary>Five-literal HelpBox warning, kept as one sentence-block field.</summary>
        public readonly string SettingsDangerZoneWarning =
            "Skipping permissions lets {agent} read, write, and run ANYTHING"
            + " on this machine without asking first -- including deleting"
            + " files, running arbitrary shell commands, and installing"
            + " software. Only enable this in a disposable sandbox project"
            + " you do not mind losing. This is OFF by default and stays"
            + " off unless you explicitly turn it on here.";

        public readonly string SettingsDangerZoneToggle = "Skip ALL permission checks (dangerous)";
        public readonly string SettingsDangerZoneTooltip =
            "Applies after the next reconnect.";
        public readonly string SettingsSectionCustomInstructions = "Custom instructions";

        public readonly string SettingsCustomInstructionsHint =
            "Appended to {agent}'s system prompt every turn.";
        public readonly string SettingsCustomInstructionsTooltip =
            "Passed as --append-system-prompt. Use it for Unity-specific conventions -- for example \\\"Always reply in Japanese.\\\"";

        public readonly string SettingsSectionDisplay = "Display";
        public readonly string SettingsShowThinkingLabel = "Show thinking blocks";

        /// <summary>
        /// Split in two: hiding/showing already-received blocks applies
        /// immediately (MessageBlockFactory.SettingsGeneration bump), but
        /// getting real thinking TEXT instead of an empty block needs the
        /// `--thinking-display summarized` spawn argument, which only takes
        /// effect after a reconnect -- handled automatically here via the
        /// existing settings auto-apply path (docs/design-notes/2026-08-01-
        /// thinking-content-loss.md section 7).
        /// </summary>
        public readonly string SettingsShowThinkingTooltip =
            "Visibility itself applies immediately. With Claude Code, showing the real thinking text rather than an empty block requires CLI v2.1.218 or later and takes effect through a quick automatic reconnect; ACP agents stream their thoughts as they come.";
        public readonly string SettingsExpandSubagentLabel = "Expand subagent cards by default";

        public readonly string SettingsExpandSubagentHint =
            "Only affects subagent cards you have not manually expanded or collapsed yet.";

        public readonly string SettingsShowCostLabel = "Show cost in USD";

        public readonly string SettingsShowCostHint =
            "When off, token counts only -- useful on subscription auth, where the cost figure is not meaningful.";

        public readonly string SettingsSectionQuickActions = "Quick actions";

        public readonly string SettingsQuickActionsHint =
            "Reusable prompt shortcuts. Selecting one only inserts its text -- it is never sent automatically.";
        public readonly string SettingsQuickActionsTooltip =
            "Shown at the top of the empty-state suggestions and in the composer's Quick menu. Selecting one inserts its prompt into the composer so you can edit it before sending.";

        public readonly string SettingsAddQuickActionButton = "+ Add quick action";
        public readonly string SettingsQuickActionLabelTooltip = "Label";
        public readonly string SettingsQuickActionPromptTooltip = "Prompt";
        public readonly string SettingsQuickActionRemoveButton = "Remove";
        public readonly string SettingsQuickActionNewDefaultLabel = "New action";
        public readonly string SettingsSectionNotifications = "Notifications";

        public readonly string SettingsNotificationsHint =
            "Beeps only fire while the Agent Panel window does not have focus.";

        public readonly string SettingsPermissionBeepLabel = "Beep on permission request";
        public readonly string SettingsTurnCompleteBeepLabel = "Beep when a turn completes";

        public readonly string SettingsSectionConsoleErrors = "Console errors";
        public readonly string SettingsConsoleErrorsHint =
            "The error chip's X hides those errors for good; manage what is hidden here.";
        public readonly string SettingsConsoleErrorsTooltip =
            "The context bar shows a chip when the Console captures errors. Pressing its X"
            + " permanently ignores every error it currently shows (per exact message, stored in"
            + " this project). Ignored errors never re-show the chip, are left out of the 'fix'"
            + " prompt sent to Claude, and stay hidden across editor restarts. Remove one here to"
            + " un-hide it immediately.";
        public readonly string SettingsIgnoredErrorPatternsLabel = "Ignore patterns";
        public readonly string SettingsIgnoredErrorPatternsHint =
            "One text fragment per line: any console error containing it is ignored.";
        public readonly string SettingsIgnoredErrorPatternsTooltip =
            "Plain substring match, one pattern per line (no regex). Use this for SDK or"
            + " extension errors whose text varies slightly each time, so the X button's"
            + " exact-message ignore cannot catch them. Takes effect immediately.";
        public readonly string SettingsIgnoredErrorsClearAllButton = "Clear ignored errors";
        public readonly string SettingsIgnoredErrorRemoveButton = "Remove";
        public readonly string SettingsSectionAppearance = "Appearance";
        public readonly string SettingsFontSizeLabel = "Font size";
        public readonly string SettingsFontSizeTooltip =
            "Applies immediately to every open panel window.";

        /// <summary>{0} = font size in pixels.</summary>
        public readonly string SettingsFontSizeValueFmt = "{0}px";

        public readonly string SettingsCjkToggleLabel = "Prefer CJK-friendly UI font";

        public readonly string SettingsCjkDiagnosticNone =
            "No matching CJK UI font found on this machine; the editor"
            + " default font is used regardless of this toggle.";

        /// <summary>{0} = detected font source description.</summary>
        public readonly string SettingsCjkDiagnosticDetectedFmt = "Detected font: {0}";

        /// <summary>{0} = detected font source description; the font came from the UITK Font Fix package's settings.</summary>
        public readonly string SettingsCjkDiagnosticViaFontFixFmt = "Detected font: {0} (via UITK Font Fix)";

        public readonly string SettingsSectionDiagnostics = "CLI output (stderr)";

        public readonly string SettingsDiagnosticsHint =
            "Recent CLI stderr output (this editor session only; never saved to disk).";

        /// <summary>stderr tail's Copy button; a distinct instance from the code-block Copy button.</summary>
        public readonly string SettingsCopyButton = "Copy";

        public readonly string SettingsClearButton = "Clear";
        public readonly string SettingsDiagnosticsEmpty = "(no stderr output yet)";
        public readonly string SettingsSectionAbout = "About";

        /// <summary>{0} = package version string.</summary>
        public readonly string SettingsPackageVersionFmt = "Package version: {0}";

        public readonly string SettingsPackageVersionUnknown = "unknown";

        /// <summary>{0} = live CLI version string.</summary>
        public readonly string SettingsCliVersionFmt = "CLI version: {0}";

        public readonly string SettingsCliVersionNotConnected = "not connected";

        /// <summary>
        /// {0} = CLI version read directly from the resolved binary
        /// (`--version`) when no live or persisted connection version is
        /// known yet -- distinguishes "here is what the binary reports"
        /// from a confirmed live session (SettingsCliVersionFmt). See
        /// docs/design-notes/2026-08-01-cli-binary-version-probe.md.
        /// </summary>
        public readonly string SettingsCliVersionUnconfirmedFmt = "CLI version: {0} (not yet connected)";

        public readonly string SettingsOpenChangelogButton = "Open CHANGELOG";
        public readonly string SettingsOpenGitHubButton = "Open on GitHub";

        // -- Appearance: language dropdown (this stage's addition) --------

        public readonly string SettingsLanguageLabel = "Language";

        public readonly string SettingsLanguageTooltip =
            "Applies immediately and rebuilds any open panel windows.";

        /// <summary>
        /// "Auto" dropdown option label (translated per active UI language,
        /// unlike the "English"/"Japanese" endonym labels next to it --
        /// see UiStringsJa.JapaneseLanguageDisplayName's doc comment).
        /// </summary>
        public readonly string SettingsLanguageOptionAuto = "Auto";

        // ==================================================================
        // AgentPanelWindow.cs
        // ==================================================================

        public readonly string HubWindowTitle = "Agent Panel";
        public readonly string HubPermissionPendingTooltip = "{agent} is waiting for a permission decision";
        public readonly string NavBackButtonLabel = "Chat";
        public readonly string NavBackButtonTooltip = "Back to chat";

        // ==================================================================
        // Markdown/CodeBlockElement.cs
        // ==================================================================

        public readonly string MarkdownCodeDefaultLang = "code";

        /// <summary>A distinct instance from Settings diagnostics' Copy button.</summary>
        public readonly string MarkdownCodeCopyButton = "Copy";

        // ==================================================================
        // DragDropAttachHandler.cs
        // ==================================================================

        public readonly string DragDropOverlayLabel = "Drop to attach";

        // ==================================================================
        // SelectionContextProvider.cs
        // ==================================================================

        /// <summary>{0} = extra count, appended after a chip label (e.g. "Name (+2)").</summary>
        public readonly string CtxSelectionExtraSuffixFmt = " (+{0})";

        public readonly string CtxSelectionSummaryHeaderSingularFmt = "Editor selection (1 object):";

        /// <summary>{0} = object count (used when count != 1; see CtxSelectionSummaryHeaderSingularFmt).</summary>
        public readonly string CtxSelectionSummaryHeaderPluralFmt = "Editor selection ({0} objects):";

        /// <summary>{0} = count of additional unlisted objects.</summary>
        public readonly string CtxSelectionMoreObjectsFmt = "(+{0} more object(s) not listed)";

        /// <summary>{0} = asset name, {1} = asset type.</summary>
        public readonly string CtxSelectionAssetFmt = "Asset: {0} ({1})";

        /// <summary>{0} = object name, {1} = object type (generic non-asset fallback).</summary>
        public readonly string CtxSelectionGenericFmt = "{0} ({1})";

        /// <summary>Shared with SceneContextProvider.cs.</summary>
        public readonly string CtxSceneUntitledFallback = "(untitled)";

        /// <summary>{0} = scene name.</summary>
        public readonly string CtxSelectionSceneLabelFmt = "scene: {0}";

        /// <summary>{0} = "yes"/"no" (CtxSelectionYes/CtxSelectionNo).</summary>
        public readonly string CtxSelectionActiveLabelFmt = "active: {0}";

        public readonly string CtxSelectionYes = "yes";
        public readonly string CtxSelectionNo = "no";

        /// <summary>Appended suffix; deliberate leading space.</summary>
        public readonly string CtxSelectionInactiveInHierarchy = " (inactive in hierarchy)";

        /// <summary>Deliberate trailing space (component names are appended directly after it).</summary>
        public readonly string CtxSelectionComponentsLabel = "components: ";

        public readonly string CtxSelectionMissingScript = "(missing script)";

        /// <summary>{0} = count of additional unlisted components.</summary>
        public readonly string CtxSelectionMoreComponentsFmt = ", +{0} more";

        // ==================================================================
        // SceneContextProvider.cs
        // ==================================================================

        /// <summary>{0} = scene name.</summary>
        public readonly string CtxSceneSummaryHeaderFmt = "Active scene: {0}";

        /// <summary>{0} = scene asset path; appended only when non-empty. Deliberate leading space.</summary>
        public readonly string CtxSceneSummaryPathFmt = " ({0})";

        /// <summary>{0} = "yes"/"no".</summary>
        public readonly string CtxSceneLoadedLabelFmt = "loaded: {0}";

        /// <summary>{0} = root object count.</summary>
        public readonly string CtxSceneRootObjectsLabelFmt = "root objects: {0}";

        /// <summary>Per-root suffix; deliberate leading space.</summary>
        public readonly string CtxSceneRootInactiveSuffix = " (inactive)";

        /// <summary>{0} = count of additional unlisted root objects.</summary>
        public readonly string CtxSceneMoreRootsFmt = "... and {0} more root object(s).";

        // ==================================================================
        // ConsoleErrorProvider.cs
        // ==================================================================

        /// <summary>{0} = distinct error count.</summary>
        public readonly string CtxErrorsDigestHeaderFmt = "Unity Console errors ({0} distinct):";

        /// <summary>{0} = severity tag, {1} = message.</summary>
        public readonly string CtxErrorsDigestEntryFmt = "[{0}] {1}";

        /// <summary>{0} = occurrence count (appended when &gt; 1). Deliberate leading space.</summary>
        public readonly string CtxErrorsDigestOccurrencesFmt = " (x{0})";

        /// <summary>{0} = source location. Leading newline/indent stays in code, not in this field.</summary>
        public readonly string CtxErrorsDigestLocationFmt = "at {0}";

        /// <summary>{0} = count of additional unlisted errors.</summary>
        public readonly string CtxErrorsDigestMoreFmt = "... and {0} more error(s).";

        // ==================================================================
        // AgentHub.cs
        // ==================================================================

        /// <summary>{0} = process-exit detail. Rendered as a SystemNote block.</summary>
        public readonly string HubProcessDiedReconnectingFmt = "The agent process exited ({0}). Reconnecting...";

        /// <summary>{0} = process-exit detail. Rendered as an Error block (crash-loop branch).</summary>
        public readonly string HubProcessDiedSuspendedFmt =
            "The agent process exited repeatedly ({0}). Automatic reconnect"
            + " suspended; check the CLI login state, then press \"Reconnect\".";

        public readonly string HubTurnStalledNote =
            "No response from the CLI for 10 minutes; the turn was closed.";

        /// <summary>{0} = CLI-reported error text or HubSyntheticResponse. Rendered as an Error block.</summary>
        public readonly string HubCliErrorFmt = "CLI error: {0}";

        /// <summary>Fallback detail fed into HubCliErrorFmt when message.Error is empty but the message is synthetic.</summary>
        public readonly string HubSyntheticResponse = "synthetic response";

        /// <summary>{0} = semicolon-joined list of checked candidate paths.</summary>
        public readonly string HubCliNotFoundErrorFmt =
            "Claude CLI executable not found. Checked: {0}. Set a manual"
            + " path in the panel settings.";

        /// <summary>{0} = exception/process-start error detail.</summary>
        public readonly string HubCliStartFailedFmt = "Failed to start Claude CLI: {0}";

        public readonly string HubPermissionDefaultToolName = "tool";

        /// <summary>
        /// {0} = tool name. Rendered as a SystemNote in the transcript --
        /// DENY only (2026-08-02 design note section 2): an ALLOW decision
        /// no longer gets a transcript note at all, since the tool-use
        /// card already shows the call/input/result and the note was
        /// measured to be 32-of-36 messages of pure noise in one session.
        /// </summary>
        public readonly string HubPermissionDeniedFmt = "Denied {0}";

        // ==================================================================
        // MessageBlockFactory.cs -- redacted_thinking (design note
        // 2026-08-01-thinking-content-loss.md section 6). Appended here
        // (end of the field list) rather than grouped with the other
        // ChatThinking* fields above, per this round's merge-safety
        // convention for concurrent edits to this file.
        // ==================================================================

        /// <summary>Shown instead of a foldout for a redacted_thinking block (no readable text is ever available for it).</summary>
        public readonly string ChatThinkingRedactedNote = "Some thinking was redacted for safety.";

        // ==================================================================
        // SettingsView.cs -- Model section (v0.8.0, docs/design-notes/
        // 2026-08-01-model-settings.md). Appended here (end of the field
        // list) rather than grouped with the other Settings* fields above,
        // per this round's merge-safety convention for concurrent edits to
        // this file.
        // ==================================================================

        public readonly string SettingsSectionModel = "Model";

        public readonly string SettingsDefaultModelLabel = "Default model";

        /// <summary>
        /// Shown under the Default model dropdown once the model catalog
        /// has at least one entry. Reworded for v0.9.0 (docs/design-notes/
        /// 2026-08-01-model-settings-rework.md section 4.1): this dropdown
        /// no longer live-switches anything -- it only sets what a NEW
        /// session (started from the header "+") begins with. The wording
        /// itself is unchanged for SettingsDefaultModelHintNoCatalog below,
        /// which is about the catalog being empty, not about session
        /// scope.
        /// </summary>
        public readonly string SettingsDefaultModelHint =
            "Used when starting a new session from the header +. The header's picker changes the current session.";

        /// <summary>Shown instead of SettingsDefaultModelHint while the model catalog is still empty (never connected this project).</summary>
        public readonly string SettingsDefaultModelHintNoCatalog =
            "Connect once so the CLI can report its available models; only \"(Default)\" is selectable"
            + " until then.";

        /// <summary>Label for the empty-value choice in both the Default model dropdown and each override row's model dropdown.</summary>
        public readonly string SettingsModelChoiceDefaultLabel = "(Default)";

        /// <summary>
        /// Deliberately does NOT say "applies the next time Claude
        /// reconnects" (every other Settings hint in this file does) --
        /// docs/research/07-model-configuration.md section 10.8
        /// ("capture15") found that claim is false for this field
        /// specifically: reconnecting the EXISTING session can never apply
        /// a table change. See SettingsAgentOverridesNewSessionHint below
        /// for the correct timing, shown as a separate permanent hint under
        /// the row list.
        /// </summary>
        /// <summary>
        /// Reworded for v0.11.0 (docs/design-notes/2026-08-02-subagent-
        /// model-precedence.md section 3.2, work item B) from "override" to
        /// "per-type default": R07 section 12 (P6) measured that the
        /// agent's own explicit per-call Task/Agent `model` choice wins
        /// over one of these entries, so calling them "overrides" had it
        /// backwards.
        /// </summary>
        public readonly string SettingsAgentOverridesHint =
            "A per-type default model for specific subagents. The agent's own explicit choice still wins.";
        public readonly string SettingsAgentOverridesTooltip =
            "Sets a per-type DEFAULT model for specific subagents such as general-purpose or Explore. An explicit per-call choice by the agent still wins over these entries. Agents left at (Default), or not listed here at all, use the Default model above.";

        public readonly string SettingsAddAgentOverrideButton = "+ Add model override";

        /// <summary>
        /// The agent name becomes a `.claude/agents/&lt;name&gt;.md` filename
        /// (AgentDefinitionFileWriter, docs/research/07-model-configuration.md
        /// section 10) -- unlike the old --agents JSON key, this imposes a
        /// real filename-safety constraint the tooltip must disclose.
        /// </summary>
        public readonly string SettingsAgentOverrideNameTooltip =
            "Agent name (e.g. general-purpose). Letters, digits, - and _ only.";

        public readonly string SettingsAgentOverrideModelTooltip = "Model for this agent";

        public readonly string SettingsAgentOverrideRemoveButton = "Remove";

        /// <summary>
        /// Shown when two or more agent-override rows share the same agent
        /// name -- the CLI accepts this silently and only the LAST row's
        /// model actually applies (R07 sections 5/9.2), with no error the
        /// panel could otherwise detect.
        /// </summary>
        public readonly string SettingsAgentOverrideDuplicateNameWarning =
            "Two or more rows use the same agent name. Only the last one will actually apply --"
            + " rename or remove the duplicate.";

        // ==================================================================
        // SettingsView.cs -- Model section, agent override "new session
        // only" correction (docs/research/07-model-configuration.md section
        // 10.8, "capture15"; docs/design-notes/2026-08-01-model-settings.md
        // section 6.5). Appended here (end of the field list) per this
        // file's merge-safety convention.
        // ==================================================================

        /// <summary>
        /// Permanent hint shown under the agent-override row list (not
        /// gated by SettingsChangeDetector/pending state, unlike
        /// SettingsReconnectPendingHint) -- reconnecting the current
        /// session can NEVER apply a change to this table (the CLI
        /// snapshots `.claude/agents/*.md` at session creation), so the
        /// only correct call to action is starting a brand-new session.
        /// </summary>
        public readonly string SettingsAgentOverridesNewSessionHint =
            "Applies to new sessions only -- start a new session (+) to use the updated overrides.";

        // ==================================================================
        // SettingsView.cs -- Model settings rework (v0.9.0, docs/design-
        // notes/2026-08-01-model-settings-rework.md section 4.2): the
        // blanket subagent model field and the per-type overrides Foldout
        // it demotes. Appended here (end of the field list) per this
        // file's merge-safety convention.
        // ==================================================================

        /// <summary>
        /// Renamed for v0.11.0 (docs/design-notes/2026-08-02-subagent-
        /// model-precedence.md section 3.2, work item B) to make the FORCE
        /// semantics explicit -- this is the measured hard clamp (R07
        /// section 12, P5: the env var beats even the agent's own per-call
        /// choice outright), not a soft default like the field above it.
        /// </summary>
        public readonly string SettingsSubagentModelLabel = "Force subagent model";

        /// <summary>
        /// Reworded for v0.11.0 to state the FULL measured precedence (R07
        /// section 12): this overrides both the per-type table below AND
        /// the agent's own per-call model choice, so it is framed as a hard
        /// cost clamp meant to normally stay empty -- not a default like
        /// the Subagent cost policy field above it. Still reconnect-
        /// relevant and reaches the CURRENT session via the existing
        /// settings auto-apply machinery (R07 section 11: the env var is
        /// read fresh on every spawn, including --resume).
        /// </summary>
        public readonly string SettingsSubagentModelHint =
            "A hard cost clamp on every subagent -- overrides the per-type defaults below and the agent's own choice.";
        public readonly string SettingsSubagentModelTooltip =
            "Overrides BOTH the per-type defaults below and the agent's own per-call model choice, for every subagent the Task tool spawns, including custom types. It is a clamp, not a default. Changes reach the currently running session within seconds. Leave it at (same as default) to let per-call and per-type choices apply normally.";

        /// <summary>
        /// Label for the Subagent model dropdown's empty-value choice --
        /// deliberately distinct from SettingsModelChoiceDefaultLabel's
        /// "(Default)" (that means "let the CLI pick its own default";
        /// this means "inherit whatever the Default model above resolves
        /// to").
        /// </summary>
        public readonly string SettingsSubagentModelSameAsDefaultLabel = "(same as default)";

        /// <summary>{0} = backend display name. Shown under the subagent model fields while an ACP backend is selected (design note 2026-09-13-acp-feature-parity.md section 3).</summary>
        public readonly string SettingsSubagentModelAcpHintFmt =
            "With {0}, this is sent as an instruction at the start of each new session; hover for details.";

        /// <summary>Hover half of SettingsSubagentModelAcpHintFmt.</summary>
        public readonly string SettingsSubagentModelAcpTooltip =
            "An ACP agent has no equivalent of Claude Code's CLAUDE_CODE_SUBAGENT_MODEL or .claude/agents files,"
            + " so the panel puts the forced model and the per-type overrides into the standing instructions it"
            + " sends with the first prompt of a new session. The agent follows them when it can choose a"
            + " subagent's model; an agent without subagents ignores them. A change takes effect from the next"
            + " new session.";

        public readonly string SettingsAgentOverridesFoldoutTitle = "Per-type overrides (advanced)";

        /// <summary>
        /// Shown when SettingsView.HasSubagentModelPrecedenceConflict is
        /// true (R07 section 11, P3: the measured CLAUDE_CODE_SUBAGENT_MODEL
        /// env var beats `.claude/agents/*.md` per-type overrides outright).
        /// </summary>
        public readonly string SettingsSubagentPrecedenceWarning =
            "Subagent model above is set, so these per-type overrides have no effect until it is cleared.";

        /// <summary>Placeholder shown for a per-type override row's agent-name PopupField before a value is picked.</summary>
        public readonly string SettingsAgentOverrideNamePlaceholder = "(select agent)";

        // ==================================================================
        // v0.10.0: in-panel login/logout (docs/design-notes/2026-08-02-
        // auth-in-panel.md) -- FirstRunView.cs / SettingsView.cs additions.
        // ==================================================================

        public readonly string FirstRunLoginButton = "Log in";

        /// <summary>
        /// Title of the one card that holds the agent picker, the
        /// found/not-found line, the sign-in state and method, and (under
        /// Advanced) the launch fields -- the former CLI and Account cards
        /// (design note 2026-09-17-agent-card-merge.md).
        /// </summary>
        public readonly string SettingsSectionAgent = "Agent";

        /// <summary>Label of the sign-in method row, shared by its Claude shape (ClaudeAuthMode) and its ACP shape (authenticate method id).</summary>
        public readonly string SettingsSignInMethodLabel = "Sign-in method";

        /// <summary>The Agent card's closed-by-default foldout holding the executable path / command / arguments.</summary>
        public readonly string SettingsAgentAdvancedFoldout = "Advanced (executable / command)";

        /// <summary>The Agent card's one reconnect button (the banner keeps its own "Reconnect now").</summary>
        public readonly string SettingsReconnectButton = "Reconnect";
        public readonly string SettingsAccountCheckingStatus = "Checking sign-in status...";
        public readonly string SettingsAccountUnavailable = "Unable to check sign-in status (CLI not found).";
        public readonly string SettingsAccountNotLoggedIn = "Not logged in.";

        /// <summary>{0} = email, {1} = subscription type (or the "unknown plan" fallback below).</summary>
        public readonly string SettingsAccountLoggedInFmt = "Logged in as {0} ({1}).";

        public readonly string SettingsAccountSubscriptionUnknown = "unknown plan";
        public readonly string SettingsAccountLoginButton = "Log in";
        /// <summary>2026-09-06 settings review: the same button while already logged in -- a second login switches the account, so say so.</summary>
        public readonly string SettingsAccountSwitchButton = "Switch account";
        public readonly string SettingsAccountLogoutButton = "Log out";
        public readonly string SettingsAccountLogoutConfirmTitle = "Log out of Claude?";

        /// <summary>Single sentence; EditorUtility.DisplayDialog body.</summary>
        public readonly string SettingsAccountLogoutConfirmBody =
            "This will stop the current Claude Code session and sign out of the CLI."
            + " You can log back in at any time.";

        public readonly string SettingsAccountLogoutConfirmButton = "Log out";
        public readonly string SettingsAccountLogoutCancelButton = "Cancel";
        public readonly string SettingsAccountLoginStarting = "Starting sign-in... a browser window should open shortly.";
        public readonly string SettingsAccountLoginWaitingForCode = "Paste the authorization code from the browser below.";
        public readonly string SettingsAccountLoginVerifying = "Verifying the code...";
        public readonly string SettingsAccountLoginFailedNotice = "Login did not complete. The code may have been wrong or expired -- press Log in to try again.";
        public readonly string SettingsAccountLoginUrlLabel = "Sign-in URL";
        public readonly string SettingsAccountOpenBrowserButton = "Open browser";
        public readonly string SettingsAccountCopyButton = "Copy";
        public readonly string SettingsAccountCodeLabel = "Authorization code";
        public readonly string SettingsAccountSubmitCodeButton = "Submit";
        public readonly string SettingsAccountCancelLoginButton = "Cancel";
        /// <summary>Logged-in status line when the CLI reports no email/plan detail (e.g. environment-token auth).</summary>
        public readonly string SettingsAccountLoggedInNoDetail = "Logged in.";
        /// <summary>Shown when auth status reports authMethod "oauth_token" (environment-token auth).</summary>
        public readonly string SettingsAccountEnvTokenNote =
            "Authentication comes from an environment token (CLAUDE_CODE_OAUTH_TOKEN). Logging in or out here changes the stored credentials, but may not affect sessions while that token stays set.";

        // ==================================================================
        // SettingsView.cs / HeaderView.cs -- subagent model precedence
        // rework (v0.11.0, docs/design-notes/2026-08-02-subagent-model-
        // precedence.md). Appended here (end of the field list) per this
        // file's merge-safety convention.
        // ==================================================================

        public readonly string SettingsSubagentCostPolicyLabel = "Subagent cost policy";

        /// <summary>
        /// Shown under the Subagent cost policy dropdown -- the injection
        /// is guidance the agent can override per task, never a hard rule
        /// (unlike the Force subagent model dropdown in the advanced
        /// foldout).
        /// </summary>
        public readonly string SettingsSubagentCostPolicyHint =
            "Steers Claude toward Haiku for simple mechanical subtasks and the session model for complex work.";
        public readonly string SettingsSubagentCostPolicyTooltip =
            "Injects one guidance line into Claude's system prompt: Haiku for simple mechanical subtasks (searches, file listings, bulk renames, log scans), the session model for complex work. It is guidance the agent can still override per task, not a hard rule. Sessions created before this CLI version may silently ignore the per-task model choice; a new session (header +) always honors it.";

        public readonly string SettingsSubagentCostPolicyOptionAgentDecides = "Agent decides (recommended)";
        public readonly string SettingsSubagentCostPolicyOptionHaikuForSimpleTasks = "Cost-saving: Haiku for simple tasks";

        /// <summary>{0} = short resolved-model label (SettingsView.ShortenResolvedModel), e.g. "opus-5 (1m)".</summary>
        public readonly string SettingsDefaultModelChoiceResolvedFmt = "Default (recommended: {0})";

        /// <summary>{0} = the model option's own label (with or without its description already appended).</summary>
        public readonly string HeaderModelOptionDefaultSuffixFmt = "{0} (default)";

        // ==================================================================
        // SettingsView.cs -- UapOps (Phase 5a, docs/design-notes/2026-08-01-
        // phase5-unity-ops-design.md section 1/8.6). Appended here (end of
        // the field list) per this file's merge-safety convention.
        // ==================================================================

        public readonly string SettingsSectionUapOps = "Unity operations (UapOps)";

        public readonly string SettingsUapOpsHint =
            "Runs a small local MCP server so {agent} can operate Unity through typed tools instead of writing C#.";
        public readonly string SettingsUapOpsTooltip =
            "The server runs inside this editor and is loopback-only, protected by a per-session token. It lets {agent} perform typed Unity operations -- creating objects, setting properties, and more in later versions -- without writing and compiling C# code.";

        public readonly string SettingsUapOpsEnabledLabel = "Enable Unity operations (UapOps)";
        public readonly string SettingsUapOpsEnabledTooltip =
            "Applies after the next reconnect.";
        public readonly string SettingsUapOpsModuleCoreLabel = "Core";
        public readonly string SettingsUapOpsModuleCoreHint =
            "Scene/component/property/asset tools + the script validation gate's commit tool.";

        /// <summary>{0} = port number.</summary>
        public readonly string SettingsUapOpsStatusRunningFmt = "Running on port {0}";

        public readonly string SettingsUapOpsStatusStopped = "Not running";
        public readonly string SettingsUapOpsStatusDisabled = "Disabled";

        // ==================================================================
        // Phase 5a stream B -- script validation gate + core module toggle
        // (docs/design-notes/2026-08-01-phase5-unity-ops-design.md section
        // 7.4/8.2). Appended here (end of the field list) per this file's
        // merge-safety convention.
        // ==================================================================

        /// <summary>{0} = the file path the CLI's Write/Edit/MultiEdit tool targeted.</summary>
        public readonly string HubScriptGateAutoDeniedFmt =
            "Blocked a direct script write to {0} (script validation gate) -- staged writes"
            + " under UapStaging/ (project root, outside Assets/) and uap_scripts_commit are"
            + " required instead.";

        public readonly string SettingsUapOpsGateEnabledLabel = "Require staged scripts (validation gate)";

        public readonly string SettingsUapOpsGateEnabledHint =
            "Claude cannot edit Assets/**/*.cs directly -- it stages the change and commits it through a compile check. Recommended: on.";
        public readonly string SettingsUapOpsGateEnabledTooltip =
            "With the gate on, Claude's own Write/Edit/MultiEdit tools cannot touch Assets/**/*.cs or *.asmdef. It must write under the staging folder and call uap_scripts_commit, which compiles the result before anything reaches Assets/. A generated PreToolUse hook enforces this, so it holds even under the acceptEdits permission mode, on top of the existing permission-card check.";

        /// <summary>{0} = the staging folder path (ScriptGate.StagingFolder).</summary>
        public readonly string SettingsUapOpsStagingFolderHintFmt =
            "Staging folder: {0} (project root, outside Assets/).";
        public readonly string SettingsUapOpsStagingFolderTooltip =
            "It sits outside Assets/ so Unity never scans it during asset import. uap_scripts_commit reads from here and moves the files into Assets/ only after they compile.";

        // ==================================================================
        // Phase 5a design gate follow-up -- "not undoable" badge + turn-level
        // non-undoable warning (design section 8.2 B2(b)/(c), 8.5 criterion
        // 2). Appended here (end of the field list) per this file's
        // merge-safety convention.
        // ==================================================================

        /// <summary>Permission-card badge for a UapOps tool whose IUapTool.Undoable is false (design section 8.2 B2(b)).</summary>
        public readonly string PermUndoNotSupportedBadge = "Not undoable";

        /// <summary>Tooltip on the not-undoable badge: what the badge means and how to act on it (UXA-5).</summary>
        public readonly string PermUndoNotSupportedTooltip =
            "This operation cannot be reverted with Unity's Undo (Ctrl+Z). Review the details carefully before allowing.";

        /// <summary>{0} = total diff line count; expander under a truncated Edit approval diff (UXA-2).</summary>
        public readonly string PermDiffShowAllFmt = "Show all {0} lines";

        /// <summary>{0} = comma-joined display names of the non-undoable UapOps tools the just-completed turn ran (design section 8.2 B2(c)/8.5 criterion 2).</summary>
        public readonly string HubTurnNonUndoableWarningFmt =
            "This turn ran one or more operations Ctrl+Z cannot undo: {0}. Review the changes before continuing.";

        /// <summary>
        /// Shown inside an expanded subagent card that turned out to have
        /// nothing to display -- a card is expandable whenever a nested
        /// transcript MIGHT be loadable (design note 2026-08-02-subagent-
        /// card-not-expandable.md section 4), so the empty outcome needs to
        /// say so rather than render a blank box.
        /// </summary>
        public readonly string SubagentNoDetails = "No recorded details for this subagent.";

        // ==================================================================
        // Phase 5b stream A -- "prefab"/"editor" UapOps module toggles
        // (docs/design-notes/2026-08-01-phase5-unity-ops-design.md section
        // 7.3/3c). Appended here (end of the field list) per this file's
        // merge-safety convention.
        // ==================================================================

        public readonly string SettingsUapOpsModulePrefabLabel = "Prefab";

        public readonly string SettingsUapOpsModulePrefabHint =
            "Prefab create/apply/revert-overrides tools.";

        public readonly string SettingsUapOpsModuleEditorLabel = "Editor";

        public readonly string SettingsUapOpsModuleEditorHint =
            "Screenshot capture (Scene/Game view), object selection, and arbitrary Editor menu command execution.";

        // ==================================================================
        // Phase 5b stream B -- "anim" UapOps module toggle (docs/design-
        // notes/2026-08-01-phase5-unity-ops-design.md section 1.2/8.8;
        // default OFF, unlike prefab/editor above).
        // ==================================================================

        public readonly string SettingsUapOpsModuleMarkersLabel = "Scene-view markers";

        public readonly string SettingsUapOpsModuleMarkersHint =
            "Scene-view markers (uap_marker_add/list/clear) and your sketch strokes (uap_stroke_list). Overlays only.";

        // 2026-09-17 "web" UapOps module toggle (uap_web_fetch, design
        // note docs/design-notes/2026-09-17-web-fetch-tool.md); default ON.
        public readonly string SettingsUapOpsModuleWebLabel = "Web fetch";

        /// <summary>Kept under the 110-char settings-hint cap (L10nTests); the USER-GUIDE row carries the detail.</summary>
        public readonly string SettingsUapOpsModuleWebHint =
            "Lets the agent fetch a URL (uap_web_fetch): images inline, PDFs and files saved under Library, pages as text.";

        public readonly string SettingsWebFetchAllowedHostsLabel = "Allowed hosts";

        public readonly string SettingsWebFetchAllowedHostsHint =
            "One host per line, subdomains included (example.com). Empty = any public host. Applies to the next fetch.";

        public readonly string SettingsWebFetchBlockedHostsLabel = "Blocked hosts";

        public readonly string SettingsWebFetchBlockedHostsHint =
            "One host per line; a blocked host is refused even when it is on the allowed list.";

        /// <summary>Context-bar chip while the agent has markers on screen (count == 1).</summary>
        public readonly string CtxMarkersChipSingle = "1 scene marker";

        /// <summary>{0} = marker count (count &gt; 1).</summary>
        public readonly string CtxMarkersChipPluralFmt = "{0} scene markers";

        public readonly string CtxMarkersClearTooltip = "Remove every marker and pin from the Scene view";

        // -- User pins (design note 2026-09-07 section 1.3.4) --
        public readonly string CtxPinButton = "Pin";
        public readonly string CtxPinButtonArmed = "Click in Scene view...";
        public readonly string CtxPinToolbarLabel = "Pin";
        public readonly string CtxPinTooltip =
            "Drop a pin in the Scene view to tell the agent \"here\". The next left click places it (Shift for several, Esc to cancel).";
        /// <summary>{0} = pin id.</summary>
        public readonly string CtxPinChipLabelFmt = "Pin P{0}";
        /// <summary>{0} = pin id.</summary>
        public readonly string CtxPinChipTitleFmt = "Scene marker P{0}";
        public readonly string CtxPinMarkerLabel = "pin";
        public readonly string CtxPinHitSurface = "surface under the cursor (no collider)";
        /// <summary>{0} = fallback distance in metres.</summary>
        public readonly string CtxPinHitNothingFmt = "nothing under the cursor; placed {0} m along the view ray";
        /// <summary>{0} = pin id.</summary>
        public readonly string CtxPinPayloadHeaderFmt = "Scene marker P{0} (user-placed pin in the Scene view)";
        public readonly string CtxPinPayloadPositionFmt = "world position: {0}";
        public readonly string CtxPinPayloadHitFmt = "hit object: {0}";
        public readonly string CtxPinPayloadNearestFmt = "nearest objects: {0}";
        public readonly string CtxPinPayloadCameraFmt = "scene view camera: position {0}, pivot {1}";

        // Sketch strokes (design note 2026-09-17-scene-sketch-strokes.md).
        public readonly string CtxSketchButton = "Sketch";
        public readonly string CtxSketchButtonArmedPlane = "Sketching on plane...";
        public readonly string CtxSketchButtonArmedSurface = "Sketching on surface...";
        public readonly string CtxSketchTooltip =
            "Draw a stroke in the Scene view and hand it to the agent as points. Plane mode draws at a chosen depth"
            + " (wheel / [ ] change depth, F snaps to the surface under the cursor, X/Y/Z lock an axis plane, C faces"
            + " the camera, Shift for a straight line); surface mode draws on the mesh under the cursor. Esc leaves the mode.";
        public readonly string CtxSketchMenuPlane = "Sketch on a plane (choose depth)";
        public readonly string CtxSketchMenuSurface = "Sketch on mesh surfaces";
        public readonly string CtxSketchMenuStop = "Stop sketching";
        public readonly string CtxSketchPlaneToolbarLabel = "Plane";
        public readonly string CtxSketchPlaneTooltip =
            "Agent sketch: draw on a plane at a chosen depth (wheel / [ ]: depth, F: snap to surface, X/Y/Z/C: plane, Esc: exit)";
        public readonly string CtxSketchSurfaceToolbarLabel = "Surface";
        public readonly string CtxSketchSurfaceTooltip = "Agent sketch: draw on the mesh surface under the cursor (Esc: exit)";
        public readonly string CtxSketchChipLabelFmt = "Sketch S{0}";
        public readonly string CtxSketchChipTitleFmt = "Scene sketch S{0}";
        /// <summary>Scene-view readout: {0} = depth in metres (F2), {1} = plane axis name.</summary>
        public readonly string CtxSketchDepthReadoutFmt = "sketch plane: depth {0} m, {1}";
        public readonly string CtxSketchAxisCamera = "facing camera";
        public readonly string CtxSketchContourPartial = "(contour partial: scene too large)";
        /// <summary>{0} = distance in metres (F2).</summary>
        public readonly string CtxSketchSurfaceBehindFmt = "surface under cursor: {0} m behind the plane";
        public readonly string CtxSketchSurfaceInFrontFmt = "surface under cursor: {0} m in front of the plane";
        public readonly string CtxSketchKeyHints = "wheel / [ ]: depth   F: snap to surface   X Y Z / C: plane   Shift: straight   Esc: exit";
        public readonly string CtxStrokePayloadHeaderFmt = "Scene sketch S{0} (user-drawn stroke in the Scene view)";
        public readonly string CtxStrokePayloadModeFmt = "mode: {0}";
        /// <summary>{0} = point count, {1} = length in metres (F2), {2} = closed yes/no.</summary>
        public readonly string CtxStrokePayloadStatsFmt = "points: {0}, length: {1} m, closed: {2}";
        public readonly string CtxStrokePayloadClosedYes = "yes";
        public readonly string CtxStrokePayloadClosedNo = "no";
        public readonly string CtxStrokePayloadPlaneFmt = "plane: origin {0}, normal {1}";
        public readonly string CtxStrokePayloadObjectsFmt = "on objects: {0}";
        public readonly string CtxStrokePayloadBoundsFmt = "bounds: {0} - {1}";
        public readonly string CtxStrokePayloadPointsFmt = "points: {0}";
        /// <summary>{0} = points shown, {1} = total, {2} = stroke id.</summary>
        public readonly string CtxStrokePayloadMorePointsFmt = "({0} of {1} points shown; uap_stroke_list id={2} returns all)";
        /// <summary>{0} = stroke id.</summary>
        public readonly string CtxStrokePayloadAllPointsFmt = "(all points shown; uap_stroke_list id={0} returns them with normals)";

        public readonly string SettingsUapOpsModuleAnimLabel = "Anim";

        public readonly string SettingsUapOpsModuleAnimHint =
            "Animation clip/AnimatorController editing, material/shader properties, importer/avatar settings. Default OFF.";

        // ==================================================================
        // Phase 5b stream C -- Extension Profiles (docs/design-notes/
        // 2026-08-01-phase5-unity-ops-design.md section 3b/8.2 B3).
        // ==================================================================

        public readonly string SettingsSectionExtensionProfiles = "Extension profiles";

        public readonly string SettingsExtensionProfilesHint =
            "Detected third-party SDKs add a short knowledge block to {agent}'s system prompt.";
        public readonly string SettingsExtensionProfilesTooltip =
            "Detected third-party SDKs (VRChat SDK3, NDMF, Modular Avatar, Avatar Optimizer, UniVRM, MagicaCloth2, FinalIK and others) get a short knowledge block appended to {agent}'s system prompt, so it already knows their component types. Bundled profiles (shipped by add-on packages such as Agent Panel Pro) are injected automatically; user-supplied profiles from .uap-profiles/*.json require your explicit approval below.";

        public readonly string SettingsExtensionProfilesEnabledLabel = "Enable extension profiles";

        public readonly string SettingsExtensionProfilesEnabledTooltip =
            "Applies after the next reconnect.";

        public readonly string SettingsExtensionProfilesEmptyHint =
            "No supported SDK detected in this project yet.";

        public readonly string SettingsExtensionProfilesStatusBundled = "Bundled";

        public readonly string SettingsExtensionProfilesStatusApproved = "Approved";

        public readonly string SettingsExtensionProfilesStatusPending = "Pending approval";

        public readonly string SettingsExtensionProfilesApproveButton = "Review & approve";

        public readonly string SettingsExtensionProfilesRevokeButton = "Revoke";

        public readonly string SettingsExtensionProfilesApproveDialogTitleFmt = "Approve extension profile: {0}?";

        public readonly string SettingsExtensionProfilesApproveDialogBodyFmt =
            "This text will be appended to {agent}'s system prompt every time this profile is detected in this"
            + " project, until you revoke it:\n\n{0}";

        public readonly string SettingsExtensionProfilesApproveDialogConfirmButton = "Approve";

        public readonly string SettingsExtensionProfilesApproveDialogCancelButton = "Cancel";

        // ==================================================================
        // Stream B2 (2026-08-02 design note section 2 B2): read-only UapOps
        // tool auto-approve toggle.
        // ==================================================================



        /// <summary>{0} = the exact wire tool name (e.g. "mcp__unity-ops__uap_ping"). "Always" menu label for an addRules suggestion (CLI-provided or synthesized, Stream B3).</summary>
        public readonly string PermAddRuleSuggestionFmt = "Always allow {0}";
        /// <summary>{0} = formatted MCP tool name; labels a rule with no argument scope.</summary>
        public readonly string PermRuleAllCallsSuffixFmt = "{0} (all calls to this tool)";

        /// <summary>
        /// HUB-9: shown once per spawn when the script validation gate is
        /// switched ON but cannot actually block anything -- non-Windows
        /// (no PreToolUse hook is installed there) combined with
        /// dangerouslySkipPermissions (the CLI never asks, so the
        /// can_use_tool pre-filter never runs either). Silence here would
        /// leave the user believing a protection is active that is not.
        /// </summary>
        /// <summary>UXIA-7: permission-mode dropdown labels (moved out of Model-layer English literals).</summary>
        public readonly string SettingsPermissionModeOptionDefault = "Default (ask every time)";

        public readonly string SettingsPermissionModeOptionPlan = "Plan";

        public readonly string SettingsPermissionModeOptionAcceptEdits = "Accept Edits";

        // ==================================================================
        // ACP backends (design note docs/design-notes/2026-09-10-acp-backends.md)
        // ==================================================================

        /// <summary>{0} = backend display name, {1} = the command, {2} = candidate paths.</summary>
        public readonly string HubAcpCommandNotFoundErrorFmt =
            "{0} command '{1}' not found. Checked: {2}. Set the command under Settings > Agent > Advanced.";

        /// <summary>{0} = backend display name, {1} = login executable name.</summary>
        public readonly string HubAcpLoginCommandNotFoundFmt =
            "{0}'s login command '{1}' was not found on PATH. Install the CLI, or sign in from a terminal and press Reconnect.";

        /// <summary>{0} = backend display name, {1} = error detail.</summary>
        public readonly string HubAcpStartFailedFmt = "Failed to start {0}: {1}";

        /// <summary>{0} = backend display name.</summary>
        public readonly string FirstRunAcpNotFoundTitleFmt = "{0} not found";

        /// <summary>{0} = backend display name.</summary>
        public readonly string FirstRunAcpLoginTitleFmt =
            "Sign in to {0}";

        public readonly string FirstRunAcpNotFoundBody =
            "The Agent Panel runs this agent as a subprocess over the Agent Client"
            + " Protocol (ACP). Install it, or enter the command below (a bare name"
            + " on PATH, or a full path).";

        /// <summary>{0} = backend display name, {1} = the login command line.</summary>
        public readonly string FirstRunAcpLoginLeadFmt =
            "{0} is installed but reported that it is not signed in. Press Sign in to run `{1}` inside the panel: the Settings Agent card shows the browser link, and the panel reconnects on its own once the command finishes.";

        /// <summary>{0} = terminal login command.</summary>
        public readonly string FirstRunAcpLoginHintFmt =
            "When the agent needs you to sign in, it opens your browser and the"
            + " panel continues on its own. Terminal fallback: {0}";

        /// <summary>Terminal-path instructions inside the ACP login card's foldout.</summary>
        public readonly string FirstRunAcpLoginAltBody =
            "Run the command below in a terminal, then press Check again here when it is done.";

        public readonly string SettingsBackendLabel = "Agent";

        public readonly string SettingsBackendTooltip =
            "Which agent CLI the panel runs. Applies at the next reconnect; the"
            + " current session ends and a new one starts with the selected agent.";

        /// <summary>Inline line under the agent picker in the Agent &amp; account card; SettingsBackendTooltip is its hover half.</summary>
        public readonly string SettingsBackendHint =
            "Applies at the next reconnect. Everything below is about this agent.";

        public readonly string SettingsBackendOptionCustom = "Other ACP agent (custom command)";

        public readonly string SettingsAcpCommandLabel = "Command";

        public readonly string SettingsAcpArgumentsLabel = "Arguments";

        /// <summary>{0} = the default launch line (command + arguments).</summary>
        public readonly string SettingsAcpCommandHintFmt =
            "Empty = `{0}`. A bare name is looked up on PATH; a full path is used as is.";

        public readonly string SettingsAcpCommandHintCustom =
            "The command that starts the agent in ACP mode (for example"
            + " `qwen --experimental-acp`). Required.";

        public readonly string SettingsAcpAuthMethodHint =
            "The ACP authenticate method id. Empty = the first sign-in method the agent offers.";

        public readonly string SettingsAcpAuthMethodTooltip =
            "The ACP authenticate method id the bridge tries when the agent reports"
            + " that sign-in is required (Gemini CLI: oauth-personal, gemini-api-key,"
            + " vertex-ai). Leave empty to use the first method the agent advertises.";

        /// <summary>{0} = terminal login command.</summary>
        public readonly string SettingsAcpLoginHintFmt =
            "Sign-in opens in your browser when needed. Terminal fallback: {0}";

        // ==================================================================
        // In-panel install and ACP sign-in (design note docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md)
        // ==================================================================

        /// <summary>{0} = backend display name.</summary>
        public readonly string InstallButtonFmt = "Install {0}";
        public readonly string InstallManualFoldout = "Install manually instead";
        public readonly string InstallManualBody = "Run this in a terminal, then press Re-detect.";
        /// <summary>{0} = backend display name, {1} = elapsed seconds.</summary>
        public readonly string InstallRunningFmt = "Installing {0}... {1}s";
        /// <summary>{0} = backend display name.</summary>
        public readonly string InstallDoneFmt = "{0} installed. Connecting...";
        /// <summary>{0} = exit code, {1} = the installer's last output line.</summary>
        public readonly string InstallFailedFmt = "Install failed (exit code {0}): {1}";
        public readonly string InstallTimedOut = "The installer did not finish within 10 minutes and was stopped.";
        public readonly string InstallShellMissing =
            "The installer could not be started (the shell, curl or PowerShell is missing).";
        public readonly string InstallNodeMissing =
            "npm was not found. Install Node.js (LTS) first, then press Install again.";
        public readonly string InstallOpenNodeButton = "Get Node.js";
        /// <summary>{0} = backend display name.</summary>
        public readonly string InstallConfirmTitleFmt = "Install {0}?";
        /// <summary>{0} = the command line.</summary>
        public readonly string InstallConfirmBodyFmt =
            "The panel runs this command:\n\n{0}\n\nIt downloads and installs software on this computer.";
        public readonly string InstallConfirmButton = "Install";
        public readonly string InstallCancelButton = "Cancel";
        /// <summary>{0} = backend display name, {1} = sign-in method name.</summary>
        public readonly string HubAcpSignInStartedNoteFmt =
            "{0} needs you to sign in ({1}). Complete it in the browser window it opened;"
            + " the panel continues on its own.";

        /// <summary>{0} = backend display name, {1} = the login command line.</summary>
        public readonly string HubAcpLoginStartedNoteFmt =
            "Running `{1}` for {0}. Finish the sign-in in your browser; the panel reconnects when the command exits.";
        /// <summary>{0} = URL.</summary>
        public readonly string HubAcpSignInUrlNoteFmt = "If no browser window opened, open this link: {0}";
        /// <summary>{0} = backend display name, {1} = error.</summary>
        public readonly string HubAcpSignInFailedNoteFmt = "{0} sign-in failed: {1}";

        /// <summary>{0} = backend display name, {1} = the login command line, {2} = exit code, {3} = ": " + the command's last output line, or empty.</summary>
        public readonly string HubAcpLoginFinishedNoteFmt =
            "`{1}` finished (exit code {2}){3}. Reconnecting to {0}...";
        public readonly string StatusWaitingSignIn = "Waiting for sign-in in your browser...";
        /// <summary>{0} = backend display name.</summary>
        public readonly string SettingsAccountAcpConnectedFmt = "Connected to {0}.";
        public readonly string SettingsAccountAcpNotConnected = "Not connected.";
        /// <summary>{0} = backend display name.</summary>
        public readonly string SettingsAccountAcpSignInPendingFmt = "Waiting for the {0} sign-in in your browser...";

        /// <summary>{0} = backend display name, {1} = the login command line.</summary>
        public readonly string SettingsAccountAcpLoginRunningFmt =
            "Running `{1}` for {0}... finish the sign-in in your browser; the panel reconnects when the command exits.";

        /// <summary>In-panel sign-in for an ACP backend that has a login command (design note 2026-09-13-acp-feature-parity.md section 2).</summary>
        public readonly string SettingsAccountAcpLoginButton =
            "Sign in";
        public readonly string SettingsAccountAcpHint =
            "The agent signs in through its own browser flow; press the button if it did not start.";
        /// <summary>Account card status while the ACP agent process starts and has not asked for sign-in (design note 2026-09-17-acp-account-card-phases.md).</summary>
        public readonly string SettingsAccountAcpConnectingFmt =
            "Connecting to {0}...";

        /// <summary>{0} = the login command line. Replaces SettingsAccountAcpHint for backends with an in-panel login.</summary>
        public readonly string SettingsAccountAcpLoginHintFmt =
            "Sign in runs `{0}` in the panel and shows its link here; Reconnect restarts the agent as signed in.";
        /// <summary>{0} = backend display name, {1} = terminal login command.</summary>
        public readonly string HubAcpSignInRequiredNoteFmt =
            "{0} is not signed in, so the connection was not retried. Sign in from Settings > Agent"
            + " (or run `{1}` in a terminal), then press Reconnect there.";
        /// <summary>{0} = backend display name, {1} = exit detail, {2} = terminal login command.</summary>
        public readonly string HubAcpHandshakeDeathNoteFmt =
            "{0} exited before the connection was established ({1}), so it was not retried"
            + " automatically. Make sure it is installed and signed in (`{2}`), then press"
            + " Reconnect (Settings > Agent).";

        public readonly string SettingsAcpLimitationsHint =
            "Some features work differently with an ACP agent; hover for the details.";

        public readonly string SettingsAcpLimitationsTooltip =
            "With an ACP agent: the script-gate hook is unavailable (Claude Code only)."
            + " Custom instructions and the subagent model settings are sent as instructions"
            + " at the start of each new session. History lists the panel's own copy of each"
            + " conversation; an agent that cannot resume a session starts a new one and"
            + " receives the transcript with your next message. Sign in (Settings > Agent)"
            + " runs the agent's own login command inside the panel where it has one."
            + " Permission cards, thinking blocks and Unity ops work the same.";

        /// <summary>
        /// What `{agent}` expands to for a custom ACP agent that has not
        /// told the panel its name yet (L10n.AgentName). Named backends
        /// use their product name (Claude / Gemini / Codex / Grok).
        /// </summary>
        public readonly string AgentGenericName = "Agent";

        /// <summary>{0} = the agent that owns the cached session, {1} = the agent being started. SystemNote on a backend switch.</summary>
        public readonly string HubSessionNotResumedAcrossBackendsNoteFmt =
            "The conversation so far was with {0}. {1} starts a new session and receives the transcript above with your next message.";

        /// <summary>{0} = backend display name. An ACP agent could not resume its earlier session (no session/load), so the transcript is handed over with the next message.</summary>
        public readonly string HubAcpSessionNotResumedNoteFmt =
            "{0} could not resume this session and started a new one. The conversation above is sent along with your next message so it can continue from here.";

        public readonly string HubScriptGateInertWarning =
            "The script validation gate is ON but cannot block anything in this configuration:"
            + " outside Windows the pre-tool hook is not installed, and \"skip all permission"
            + " prompts\" stops the CLI from asking at all. Direct writes to Assets/**/*.cs are"
            + " NOT being stopped. Turn off \"skip all permission prompts\" to restore the gate.";

        // ==================================================================
        // v0.40.0: API key auth passthrough (docs/design-notes/2026-09-10-
        // claude-api-key-auth-passthrough.md) -- SettingsView.cs Account
        // card additions.
        // ==================================================================

        /// <summary>Short hint under the sign-in method dropdown (Claude shape).</summary>
        public readonly string SettingsClaudeAuthHint =
            "Auto lets an ANTHROPIC_API_KEY in the environment win (pay-per-use); Subscription only removes it.";

        public readonly string SettingsClaudeAuthTooltip =
            "Claude Code, not ACP agents. Auto (recommended, default) leaves ANTHROPIC_API_KEY"
            + " untouched in the spawned CLI's environment, so it authenticates exactly as it"
            + " would in a terminal: an API key set there wins and bills pay-per-use, otherwise"
            + " the stored subscription login is used. Subscription only strips the variable"
            + " from the CLI's environment so the subscription login is always used, even if"
            + " ANTHROPIC_API_KEY happens to be set in this editor's process.";

        public readonly string SettingsClaudeAuthOptionAuto = "Auto (leave it to the CLI)";
        public readonly string SettingsClaudeAuthOptionSubscriptionOnly = "Subscription only";

        /// <summary>
        /// Account card note shown whenever the CLI's own system/init
        /// reports a non-"none" apiKeySource -- {0} = that source (e.g.
        /// "ANTHROPIC_API_KEY", "apiKeyHelper").
        /// </summary>
        public readonly string SettingsAccountApiKeyAuthNoteFmt =
            "Connected with an API key ({0}) -- usage is billed to that key, not to a"
            + " subscription. Logging in or out here does not change this; set \"API key"
            + " authentication\" above to Subscription only to always use the subscription login.";

        /// <summary>
        /// One-line system note appended to the transcript, once per spawn,
        /// the first time system/init reports API-key auth is active --
        /// {0} = apiKeySource. Mirrors SettingsAccountApiKeyAuthNoteFmt for
        /// whoever is only watching the chat.
        /// </summary>
        /// <summary>{0} = requested model value. The header picker was used before the initialize handshake answered; the switch is held until it does.</summary>
        public readonly string HubModelSwitchQueuedNoteFmt =
            "Model switch to {0} is waiting for the connection to finish; it is applied as soon as the agent answers.";

        /// <summary>{0} = requested model value. Confirmation of a live set_model.</summary>
        public readonly string HubModelSwitchedNoteFmt =
            "Model switched to {0} for this session.";

        /// <summary>{0} = requested model value, {1} = CLI error text (or "timed out"). The live switch did not happen; the previous model stays.</summary>
        public readonly string HubModelSwitchFailedNoteFmt =
            "Model switch to {0} failed ({1}). The session keeps its previous model; pick again or start a new session with it as the default.";

        public readonly string HubApiKeyAuthNoteFmt =
            "Connected with an API key ({0}) -- usage is billed to that key, not to a subscription.";

        // ==================================================================
        // v0.41.0: ACP sign-in guidance and the connected auth method
        // (docs/design-notes/2026-09-10-acp-auth-guidance-and-method-display.md)
        // ==================================================================

        // The AcpAuthSummary* fields are the ONE short inline line under the
        // Agent picker and on the first-run card; the matching AcpAuthDetail*
        // field is its hover tooltip (docs/design-notes/2026-08-04-settings-
        // annotation-load.md section 4: "inline stays one short line, the long
        // explanation moves to the tooltip"). Keep the summaries under the
        // 110-char inline cap the hint fields are held to.

        /// <summary>Gemini CLI's sign-in paths, short form. Detail in <see cref="AcpAuthDetailGemini"/>.</summary>
        public readonly string AcpAuthSummaryGemini =
            "Sign-in: a Gemini API key (GEMINI_API_KEY, or ~/.gemini/.env), or Code Assist Standard/Enterprise.";

        public readonly string AcpAuthSummaryCodex =
            "Sign-in: your ChatGPT account, or an API key in CODEX_API_KEY / OPENAI_API_KEY.";

        public readonly string AcpAuthSummaryGrok =
            "Sign-in: your SuperGrok / X Premium+ account, or an API key in XAI_API_KEY.";

        /// <summary>
        /// Tooltip half of <see cref="AcpAuthSummaryGemini"/>. Google ended
        /// "Login with Google" for Gemini Code Assist for individuals /
        /// Google AI Pro / Ultra on 2026-06-18, so an individual's only
        /// paths are an API key or Vertex AI.
        /// </summary>
        public readonly string AcpAuthDetailGemini =
            "Google ended \"Login with Google\" for individuals -- Gemini Code Assist for"
            + " individuals, Google AI Pro and Google AI Ultra -- on 2026-06-18, so an"
            + " individual account signs in with a Gemini API key instead. The Google"
            + " sign-in continues only for Gemini Code Assist Standard / Enterprise, which"
            + " needs a Google Cloud project.";

        public readonly string AcpAuthDetailCodex =
            "The ChatGPT subscription login is the recommended path; an API key in"
            + " CODEX_API_KEY or OPENAI_API_KEY works too and bills that key rather than"
            + " the subscription.";

        public readonly string AcpAuthDetailGrok =
            "The SuperGrok / X Premium+ login is the recommended path; an API key in"
            + " XAI_API_KEY works too and bills that key rather than the subscription.";

        public readonly string AcpAuthKeysNotStoredNote =
            "The panel never stores API keys: set them in the agent CLI's own environment"
            + " variable or config file. An OS environment variable is only picked up after"
            + " the editor restarts.";

        /// <summary>{0} = backend display name, {1} = where the key goes (AgentBackends.ApiKeyHint).</summary>
        public readonly string HubAcpApiKeyGuidanceFmt =
            "To use {0} with an API key instead, set {1}, then reconnect (an OS environment"
            + " variable is only picked up after the editor restarts). The panel never stores"
            + " API keys.";

        /// <summary>{0} = the auth method's display name. Account card, whenever the ACP handshake authenticated.</summary>
        public readonly string SettingsAccountAcpAuthMethodFmt = "Signed in with: {0}.";

        /// <summary>Account card, when the agent accepted the session without an authenticate round trip.</summary>
        public readonly string SettingsAccountAcpAuthMethodStored = "Connected with a saved sign-in.";

        /// <summary>{0} = the auth method's display name. Account card note, shown only when that method is a key/gateway one.</summary>
        public readonly string SettingsAccountAcpApiKeyAuthNoteFmt =
            "{0} is an API key method -- usage is billed to that key, not to a subscription."
            + " The panel does not store the key; it lives in the agent CLI's own environment"
            + " variable or config file.";

        /// <summary>{0} = backend display name, {1} = auth method display name. One-shot transcript note, once per spawn.</summary>
        public readonly string HubAcpApiKeyAuthNoteFmt =
            "Connected to {0} with an API key method ({1}) -- usage is billed to that key,"
            + " not to a subscription.";

        // ==================================================================
        // 2026-09-11 core/pro split (docs/design-notes/2026-09-11-core-pro-
        // split.md "seam 4"): shown next to a UapOps module toggle whose
        // module currently has zero registered tools (Agent Panel Pro not
        // installed), instead of a live tool count. The toggle itself is
        // also disabled in that state.
        // ==================================================================

        /// <summary>{0} = the module's normal hint (what its tools do), kept so the reader still learns what the disabled toggle WOULD enable.</summary>
        public readonly string SettingsUapOpsProAbsentHintFmt =
            "{0} Requires the Agent Panel Pro add-on (sold separately; not installed).";

        /// <summary>Hover tooltip on a disabled Pro-only module toggle: what Pro is, that Core is complete without it, and how the toggle comes back.</summary>
        public readonly string SettingsUapOpsProAbsentTooltip =
            "These tools ship in the separately sold Agent Panel Pro package (jp.colloid.agent-panel-pro),"
            + " which is not installed in this project. The panel works fully without it. To add them, extract"
            + " the Pro package under the project's Packages/ folder; this toggle becomes available after the"
            + " next domain reload. See README > Core and Pro.";

        /// <summary>{0} = the module's own hint. Used for the "tests" row when Pro IS installed but com.unity.test-framework is not -- see SettingsView.ResolveTestsModuleHint.</summary>
        public readonly string SettingsUapOpsTestFrameworkAbsentHintFmt =
            "{0} Needs the Unity Test Framework package, absent here.";

        public readonly string SettingsUapOpsTestFrameworkAbsentTooltip =
            "uap_test_run drives the Unity Test Runner, so it ships in an assembly that only compiles when"
            + " com.unity.test-framework is in the project. Agent Panel Pro is installed here, but that"
            + " package is not, so the tool does not exist in this project at all. Add it from Package"
            + " Manager (Window > Package Manager > Unity Registry > Test Framework); this toggle becomes"
            + " available after the next domain reload.";

        // ==================================================================
        // SettingsView.cs -- "Agent Panel Pro updates" card (design note
        // 2026-09-12-pro-update-delivery.md section 3.3)
        // ==================================================================

        public readonly string SettingsSectionProUpdates = "Agent Panel Pro updates";

        public readonly string SettingsProUpdatesHint =
            "Paste the product key from your purchase once; Pro then updates through the Package Manager.";

        public readonly string SettingsProUpdatesTooltip =
            "The key is written to Unity's own credential file (~/.upmconfig.toml, or the directory in"
            + " UPM_USER_CONFIG_DIR) and the registry is added to this project's Packages/manifest.json as a"
            + " scoped registry for jp.colloid.agent-panel-pro. The panel keeps no copy of the key. After"
            + " that, Window > Package Manager > My Registries lists Agent Panel Pro and offers Update when a"
            + " new version is published. The registry URL is not a secret; it came with the key.";

        public readonly string SettingsProUpdatesUrlLabel = "Registry URL";
        public readonly string SettingsProUpdatesKeyLabel = "Product key";
        public readonly string SettingsProUpdatesApplyButton = "Save key";

        /// <summary>{0} = the .upmconfig.toml path that received the token.</summary>
        public readonly string SettingsProUpdatesStatusAppliedFmt =
            "Saved. Key written to {0}; the registry is in manifest.json. Package Manager is resolving --"
            + " open My Registries to see Agent Panel Pro.";

        public readonly string SettingsProUpdatesStatusErrorEmptyKey = "Enter the product key first.";
        public readonly string SettingsProUpdatesStatusErrorUrl = "Registry URL must be an https:// address.";

        /// <summary>{0} = the other registry's name, {1} = the Pro package id.</summary>
        public readonly string SettingsProUpdatesStatusErrorForeignFmt =
            "Another scoped registry (\"{0}\") already lists {1} in manifest.json. Remove that scope, then save again.";

        /// <summary>{0} = parser or IO detail.</summary>
        public readonly string SettingsProUpdatesStatusErrorManifestFmt =
            "Could not read Packages/manifest.json: {0}";

        /// <summary>{0} = IO detail.</summary>
        public readonly string SettingsProUpdatesStatusErrorWriteFmt =
            "Could not write the files: {0}";

        public readonly string SettingsProUpdatesVccButton = "Add to VCC / ALCOM";

        public readonly string SettingsProUpdatesVccTooltip =
            "Opens a vcc:// link that registers the Pro VPM repository in VRChat Creator Companion or ALCOM,"
            + " with your product key as the repository's Authorization header. If nothing opens, add the"
            + " repository by hand: Settings > Packages > Add Repository, paste the listing URL shown in the"
            + " status line, and under the header settings add Authorization: Bearer <your key>.";

        /// <summary>{0} = the VPM listing URL.</summary>
        public readonly string SettingsProUpdatesStatusVccOpenedFmt =
            "Opened VCC / ALCOM to add the repository. Listing URL (for a manual add): {0}";

        public readonly string SettingsProUpdatesStatusErrorVccNoKey =
            "Enter the product key first, or press Save key so it can be read back from .upmconfig.toml.";

        // 2026-09-12 core-only wording (docs/design-notes/2026-09-12-core-
        // only-wording.md): shown in the Extension profiles card instead of
        // "No supported SDK detected" when NO bundled profile is installed
        // at all -- with only the Core package present nothing can be
        // detected, and saying so is what tells a Core-only user why the
        // list is empty even though their project has VRChat SDK3 in it.
        public readonly string SettingsExtensionProfilesNoBundledHint =
            "No bundled SDK profiles are installed. Project profiles in .uap-profiles/*.json still work.";

        // Phase 5c "ui" module toggle, added 2026-09-12 (the module existed
        // since Phase 5c but had no Settings row -- see the 2026-09-11
        // core/pro split note's "Deliberately NOT done" list).
        public readonly string SettingsUapOpsModuleUiLabel = "UI automation";

        public readonly string SettingsUapOpsModuleUiHint =
            "Lists, dumps, clicks and sets values in UI Toolkit Editor windows (uap_editor_ui_*). Default OFF.";

        // 2026-09-15 "authoring" module (docs/design-notes/
        // 2026-09-15-profile-authoring-tools.md).
        public readonly string SettingsUapOpsModuleAuthoringLabel = "Profile authoring";

        public readonly string SettingsUapOpsModuleAuthoringHint =
            "Drafts and checks this project's own Extension Profiles in .uap-profiles/"
            + " (uap_profile_*). Default OFF.";

        // 2026-09-15 "avatar" module (docs/design-notes/
        // 2026-09-15-avatar-stats.md).
        public readonly string SettingsUapOpsModuleAvatarLabel = "Avatar stats";

        public readonly string SettingsUapOpsModuleAvatarHint =
            "Measures an avatar, bakes it with NDMF, and edits its VRChat expression menu."
            + " Default OFF.";

        // 2026-09-15 "batch" module (docs/design-notes/
        // 2026-09-15-batch-tool.md).
        public readonly string SettingsUapOpsModuleBatchLabel = "Batched calls";

        public readonly string SettingsUapOpsModuleBatchHint =
            "Runs many Unity operations in one call and one permission card (uap_batch)."
            + " Default OFF.";

        // 2026-09-15 "tests" module (docs/design-notes/
        // 2026-09-15-test-run.md).
        public readonly string SettingsUapOpsModuleTestsLabel = "Test runner";

        public readonly string SettingsUapOpsModuleTestsHint =
            "Runs the project's EditMode tests and reports the failures (uap_test_run)."
            + " Default OFF.";

        // 2026-09-15 "fx" module (docs/design-notes/
        // 2026-09-15-particle-set.md).
        public readonly string SettingsUapOpsModuleFxLabel = "Particle systems";

        public readonly string SettingsUapOpsModuleFxHint =
            "Reads and writes a Particle System's modules by their scripting names (uap_particle_set)."
            + " Default OFF.";

        // 2026-09-15 "mesh" module (docs/design-notes/
        // 2026-09-15-mesh-create.md).
        public readonly string SettingsUapOpsModuleMeshLabel = "Mesh creation";

        // The one inline line stays under L10nTests' 110-char cap; the
        // tool-by-tool detail is the row's hover tooltip (2026-09-15
        // mesh-ci-fixes note), the same split every other long Settings
        // annotation got in 2026-08-04-settings-annotation-load.md.
        public readonly string SettingsUapOpsModuleMeshHint =
            "Builds, edits, combines and repairs meshes with no script to compile (uap_mesh_*). Default OFF.";

        public readonly string SettingsUapOpsModuleMeshTooltip =
            "Primitives, extrusions, lathes, SDF blends and raw vertex lists (uap_mesh_create); a read-only"
            + " report of a mesh's counts, bounds and groups (uap_mesh_inspect); region-based vertex edits"
            + " with a proportional falloff (uap_mesh_edit); union / subtract / intersect of two closed"
            + " meshes (uap_mesh_boolean); defect reports and repair -- degenerate, duplicate and inside-out"
            + " triangles, open edges (uap_mesh_validate / uap_mesh_repair); and a scene-wide z-fighting"
            + " scan for coplanar overlapping faces (uap_scene_zfight_scan).";

        // 2026-09-15 profile-gap affordance (docs/design-notes/
        // 2026-09-15-profile-gaps-and-skill-scaffold.md): shown only when
        // Agent Panel Pro's authoring tools are present to act on it.

        /// <summary>{0} = comma-separated package ids.</summary>
        public readonly string SettingsExtensionProfilesGapsFmt =
            "No profile covers these installed packages: {0}";

        /// <summary>{0} = comma-separated package ids, {1} = how many more were not listed.</summary>
        public readonly string SettingsExtensionProfilesGapsMoreFmt =
            "No profile covers these installed packages: {0}, and {1} more";

        public readonly string SettingsExtensionProfilesGapsTooltip =
            "An Extension Profile tells the agent what an installed SDK is and what it would otherwise get"
            + " wrong about it. These package ids are named by no bundled or project profile. The button"
            + " copies a request you can paste into a chat; the agent drafts the profile with"
            + " uap_profile_scaffold into .uap-profiles/, and you still approve it here before it is"
            + " injected. A profile that detects by type name instead of package id (an Asset Store SDK with"
            + " no package id) is not counted, so this is a hint rather than a verdict.";

        public readonly string SettingsExtensionProfilesGapsButton = "Copy a request to draft one";

        public readonly string SettingsExtensionProfilesGapsCopied = "Copied -- paste it into a chat.";

        /// <summary>{0} = comma-separated package ids.</summary>
        public readonly string SettingsExtensionProfilesGapsRequestFmt =
            "This Unity project has installed packages that no Agent Panel Extension Profile covers: {0}."
            + " Pick the one that would help most, draft a profile for it with uap_profile_scaffold, fill in"
            + " every TODO line from that SDK's own documentation rather than from memory, and check the"
            + " result with uap_profile_validate. Tell me what you wrote before I approve it in"
            + " Settings > Extension profiles.";

        public readonly string SettingsExtensionProfilesNoBundledTooltip =
            "The bundled profiles -- VRChat SDK3, NDMF, Modular Avatar, VRCFury, Avatar Optimizer,"
            + " lilycalInventory, lilToon, UniVRM, MagicaCloth2, Final IK, Bakery, RPG Maker Unite and ProBuilder --"
            + " ship in the separately sold Agent Panel Pro package (jp.colloid.agent-panel-pro). Without it"
            + " this list only shows profiles you add yourself as .uap-profiles/*.json under the project root,"
            + " each approved below before it is injected.";

        // ==================================================================
        // Constructors
        // ==================================================================

        /// <summary>English catalog (the default/fallback) -- every field above already carries its English value via its own field initializer.</summary>
        public UiStrings()
        {
        }

        /// <summary>
        /// Full-catalog constructor used ONLY by language factories
        /// (UiStringsJa.Create() today). Every parameter is REQUIRED --
        /// there are no defaults here on purpose, so a field added above
        /// without a matching argument here is a compile error, not a
        /// silently-still-English string at runtime.
        /// </summary>
        internal UiStrings(
            string firstRunCliNotFoundTitle,
            string firstRunCliNotFoundBody,
            string firstRunBrowseButton,
            string firstRunRedetectButton,
            string firstRunLoginTitle,
            string firstRunLoginLead,
            string firstRunLoginAltFoldout,
            string firstRunLoginAltBody,
            string firstRunLoginBody2,
            string firstRunCheckAgainButton,
            string firstRunBrowseDialogTitle,
            string bannerContinueButton,
            string bannerReconnectButton,
            string bannerErroredText,
            string bannerInterruptedText,
            string bannerReconnectingText,
            string bannerResumedText,
            string permWaitingInWindowText,
            string permShowHereButton,
            string permShowHereTooltip,
            string permChevronTooltip,
            string permOpenWindowTooltip,
            string permTitleFmt,
            string permTitleWithDescriptionFmt,
            string permInlineTitleWithDescriptionFmt,
            string permAllowButton,
            string permAlwaysButtonLabel,
            string permAlwaysTooltip,
            string permQueueDepthFmt,
            string permDenyButton,
            string permDenyFieldTooltip,
            string permKeyboardHint,
            string permSetModeSuggestionFmt,
            string permTruncatedMoreLinesFmt,
            string permQuestionTitleSingle,
            string permQuestionTitlePlural,
            string permQuestionTabFallbackFmt,
            string permOtherOptionLabel,
            string permOtherPlaceholder,
            string permOtherFieldCaption,
            string permDenyFieldCaption,
            string permSubmitButton,
            string permSkipButton,
            string permSkipTooltip,
            string permWindowTitle,
            string ctxAttachSelectionButton,
            string ctxAttachSelectionTooltip,
            string ctxAttachSceneButton,
            string ctxAttachSceneTooltip,
            string ctxSceneAttachedButton,
            string ctxFixErrorsButton,
            string ctxRemoveTooltip,
            string ctxSelectionChipTitleFmt,
            string ctxSceneChipLabelFmt,
            string ctxFixErrorsPrompt,
            string ctxConsoleErrorTitleSingle,
            string ctxConsoleErrorTitlePluralFmt,
            string ctxDefaultChipLabel,
            string ctxPingTooltip,
            string ctxErrorSingle,
            string ctxErrorPluralFmt,
            string ctxErrorDismissForNowTooltip,
            string ctxErrorMenuTooltip,
            string ctxErrorIgnoreForeverMenuItem,
            string ctxErrorIgnoredNoticeSingle,
            string ctxErrorIgnoredNoticePluralFmt,
            string ctxErrorIgnoreUndoButton,
            string ctxGameObjectTitleFmt,
            string ctxObjectTitleTypeFirstFmt,
            string ctxUnnamedFallback,
            string toolCardDefaultName,
            string historyTitle,
            string historyRefreshTooltip,
            string historyEmptyMessage,
            string historyNoPreview,
            string historyCurrentBadge,
            string historySwitchConfirmText,
            string historyCancelButton,
            string historySwitchButton,
            string historyRelativeJustNow,
            string historyRelativeOneMinuteAgo,
            string historyRelativeMinutesAgoFmt,
            string historyRelativeOneHourAgo,
            string historyRelativeHoursAgoFmt,
            string historyRelativeOneDayAgo,
            string historyRelativeDaysAgoFmt,
            string historyRelativeOneMonthAgo,
            string historyRelativeMonthsAgoFmt,
            string historyRelativeOneYearAgo,
            string historyRelativeYearsAgoFmt,
            string historySizeBytesFmt,
            string historySizeKbFmt,
            string historySizeMbFmt,
            string historySearchPlaceholder,
            string historySearchNoMatch,
            string historyGroupByLabel,
            string historyGroupByDate,
            string historyGroupByScene,
            string historyGroupByProject,
            string historyGroupByCustom,
            string historyShowArchivedLabel,
            string historyGroupPinned,
            string historyGroupToday,
            string historyGroupYesterday,
            string historyGroupLast7,
            string historyGroupLast30,
            string historyGroupOlder,
            string historyGroupNoScene,
            string historyGroupNoProject,
            string historyGroupUngrouped,
            string historyShowMoreFmt,
            string historyMenuTooltip,
            string historyActionPin,
            string historyActionUnpin,
            string historyActionRename,
            string historyActionArchive,
            string historyActionUnarchive,
            string historyActionDelete,
            string historyActionGroupSubmenu,
            string historyActionGroupNone,
            string historyActionGroupNew,
            string historyMenuGroupBack,
            string historyDeleteDisabledForeignTooltip,
            string historyActionOpen,
            string historyOpenDisabledForeignTooltip,
            string historyDeleteDisabledLiveTooltip,
            string historyRenamePlaceholder,
            string historyRenameSaveButton,
            string historyRenameResetButton,
            string historyDeleteConfirmTextFmt,
            string historyDeleteButton,
            string historyDeleteFailed,
            string historyNewGroupPlaceholder,
            string historyNewGroupCreate,
            string historyForeignProjectNote,
            string headerDefaultTitle,
            string statusModelUnknown,
            string headerModelPickerTooltip,
            string headerHistoryButton,
            string headerHistoryTooltip,
            string headerCompactTooltip,
            string headerCompactModelItemFmt,
            string headerCompactAutoApproveItemFmt,
            string headerNewSessionTooltip,
            string headerSettingsTooltip,
            string headerReconnectTooltip,
            string headerModelOptionWithDescriptionFmt,
            string headerModelPickerConnectFirst,
            string autoApproveLevelAsk,
            string autoApproveLevelReadOnly,
            string autoApproveLevelUndoable,
            string autoApproveLevelAllUnityOps,
            string autoApproveLevelAllTools,
            string autoApproveShortAllTools,
            string autoApproveConfirmAllToolsTitle,
            string autoApproveConfirmAllToolsBody,
            string autoApproveShortAsk,
            string autoApproveShortReadOnly,
            string autoApproveShortUndoable,
            string autoApproveShortAllUnityOps,
            string autoApproveMenuTitle,
            string headerAutoApproveTooltipFmt,
            string autoApproveConfirmAllTitle,
            string autoApproveConfirmAllBody,
            string autoApproveConfirmAllConfirmButton,
            string autoApproveConfirmAllCancelButton,
            string hubAutoContinuePendingWillContinue,
            string hubAutoContinuePendingOff,
            string hubAutoContinueResuming,
            string hubCompactedManualFmt,
            string hubCompactedManual,
            string hubCompactedAutoFmt,
            string hubCompactedAuto,
            string hubAutoContinueSendAbandonedFmt,
            string hubAutoContinueInterruptedResuming,
            string hubReloadDroppedPermissionFmt,
            string hubSessionCacheUnreadable,
            string settingsUloopSectionTitle,
            string settingsUloopStatusInstalled,
            string settingsUloopStatusMissing,
            string settingsUloopInstallButton,
            string settingsUloopInstallConfirmTitle,
            string settingsUloopInstallApply,
            string settingsUloopInstallCancel,
            string settingsUloopInstallViaGitFmt,
            string settingsUloopInstallViaManifest,
            string settingsUloopInstallPathsTooltipFmt,
            string settingsUloopInstallDiffFoldout,
            string settingsUloopInstallingFmt,
            string settingsUloopInstallAsyncFailedFmt,
            string settingsUloopInstallStalled,
            string settingsUnityPluginSectionTitle,
            string settingsUnityPluginStatusCliUnavailable,
            string settingsUnityPluginStatusNotInstalled,
            string settingsUnityPluginStatusDisabled,
            string settingsUnityPluginStatusInstalledFmt,
            string settingsUnityPluginStatusEnabledNotLoadedFmt,
            string settingsUnityPluginStatusLoadedFmt,
            string settingsUnityPluginStatusLoadErrorFmt,
            string settingsUnityPluginInstallButton,
            string settingsUnityPluginInstallConfirmTitle,
            string settingsUnityPluginCaveatScope,
            string settingsUnityPluginCaveatUnity6,
            string settingsUnityPluginInstallApply,
            string settingsUnityPluginInstallCancel,
            string settingsUnityPluginInstallingFmt,
            string settingsUnityPluginInstalled,
            string settingsUnityPluginInstallFailedFmt,
            string settingsUnityPluginStageMarketplace,
            string settingsUnityPluginStageInstall,
            string settingsUnityPluginInstallStalled,
            string settingsUnityPluginDetailsFoldout,
            string settingsUnityPluginDetailsBody,
            string settingsUnityPluginRepoLabel,
            string settingsUnityPluginSteeringLabel,
            string settingsUnityPluginSteeringTooltip,
            string settingsUloopCaveatVcc,
            string settingsUloopCaveatOffline,
            string settingsUloopCaveatManifestUnreadable,
            string settingsUloopCaveatRegistryConflict,
            string settingsUloopInstallFailed,
            string settingsUloopInstallFailedManifestUnknownFmt,
            string settingsUloopPresetButton,
            string settingsUloopPresetHelp,
            string settingsUloopPresetTooltip,
            string settingsUloopPresetApplied,
            string settingsUloopSnippetButton,
            string settingsUloopSnippetHelp,
            string settingsUloopSnippetTooltip,
            string settingsUloopSnippetApplied,
            string settingsAutoContinueLabel,
            string settingsAutoContinueHelp,
            string settingsAutoContinueTooltip,
            string settingsAutoContinueInterruptedLabel,
            string settingsAutoContinueInterruptedHelp,
            string settingsAutoContinueInterruptedTooltip,
            string settingsPlayModeReloadOnHint,
            string settingsPlayModeReloadOffHint,
            string settingsPlayModeReloadTooltip,
            string settingsPlayModeOpenProjectSettingsButton,
            string settingsAutoApproveLabel,
            string settingsAutoApproveMovedHint,
            string settingsAutoApproveMovedTooltip,
            string settingsAutoApproveHelp,
            string settingsAutoApproveTooltip,
            string toolCardSectionInput,
            string toolCardSectionResult,
            string toolCardSectionError,
            string toolCardSectionChanges,
            string toolCardMoreLinesFmt,
            string toolCardMoreCharsFmt,
            string toolCardDurationSecondsFmt,
            string toolGroupCountFmt,
            string toolGroupCountWithDurationFmt,
            string chatRoleUser,
            string chatRoleAssistant,
            string chatDefaultAttachTitle,
            string chatThinkingStreaming,
            string chatThinkingDone,
            string chatThinkingIndicatorStreaming,
            string chatThinkingIndicatorStreamingTokensFmt,
            string chatThinkingIndicatorDone,
            string chatThinkingIndicatorDoneTokensFmt,
            string chatPruneNoteFmt,
            string chatJumpToLatestButton,
            string chatCompactingIndicator,
            string statusDisconnected,
            string statusCliNotFound,
            string statusConnecting,
            string statusConnectingSlow,
            string statusIdle,
            string statusResponding,
            string statusCompacting,
            string statusRunningTool,
            string statusWaitingPermission,
            string statusError,
            string statusApplyingSettings,
            string statusContextPercentFmt,
            string statusContextWarnTooltip,
            string statusContextCompactedLabel,
            string statusContextCompactedTooltip,
            string statusUsageZeroTokens,
            string statusUsageTokensFmt,
            string statusUsageTokensWithCostFmt,
            string statusUsagePopoverTitle,
            string statusUsagePopoverEmpty,
            string statusUsagePopoverBreakdownPending,
            string statusUsagePopoverLineFmt,
            string statusUsagePopoverCostSuffixFmt,
            string statusUsagePopoverContextFmt,
            string emptyTitle,
            string emptySubtitle,
            string emptySuggestionProject,
            string emptySuggestionScene,
            string emptySuggestionErrors,
            string emptySuggestionSelection,
            string composerQuickButton,
            string composerQuickTooltip,
            string composerAttachButton,
            string composerAttachTooltip,
            string composerImageOnlyDisplay,
            string attachMenuImageFile,
            string attachMenuSceneView,
            string attachMenuGameView,
            string attachMenuSceneViewWindow,
            string attachImageRemoveTooltip,
            string attachImageErrorTitle,
            string attachImageErrorOk,
            string attachImageUnsupportedFmt,
            string attachImageMissingFmt,
            string attachImageLoadFailedFmt,
            string attachImageTooLargeFmt,
            string attachImageTooManyFmt,
            string attachMenuClipboard,
            string attachClipboardEmpty,
            string attachClipboardFailedFmt,
            string chatImageOpenTooltip,
            string chatImageMissing,
            string chatImageDefaultCaption,
            string composerSendButton,
            string composerStopButton,
            string composerNoQuickActionsMenuItem,
            string composerQuickActionUntitled,
            string composerPlaceholderCtrlEnter,
            string composerPlaceholderEnter,
            string composerPlaceholderPermissionPending,
            string composerHintCompiling,
            string composerHintQueuedFmt,
            string composerHintEscToStop,
            string composerHintTurnSendAndStop,
            string composerSendTooltip,
            string composerStopTooltip,
            string composerSlashHint,
            string composerSlashNoMatch,
            string slashCompactDescription,
            string slashClearDescription,
            string subagentDefaultType,
            string subagentDefaultDescription,
            string subagentDropNoteFmt,
            string subagentProgressToolFmt,
            string subagentProgressTokensFmt,
            string subagentProgressWorking,
            string subagentProgressStepsFmt,
            string subagentProgressUpdatedAgoFmt,
            string settingsTitle,
            string settingsGroupConversation,
            string settingsGroupDisplay,
            string settingsGroupUnity,
            string settingsGroupConnection,
            string settingsCliPathLabel,
            string settingsCliPathHint,
            string settingsCliPathTooltip,
            string settingsReconnectNowButton,
            string settingsCliResolvedFmt,
            string settingsCliNotFoundFmt,
            string settingsReconnectPendingHint,
            string settingsReconnectPendingPill,
            string settingsReconnectPendingHintDeferred,
            string settingsReconnectPendingPillDeferred,
            string settingsSectionConversation,
            string settingsPermissionModeLabel,
            string settingsPermissionModeTooltip,
            string settingsCtrlEnterLabel,
            string settingsCtrlEnterHint,
            string settingsCtrlEnterTooltip,
            string settingsAllowedToolsLabel,
            string settingsAllowedToolsHint,
            string settingsAllowedToolsTooltip,
            string settingsDisallowedToolsLabel,
            string settingsDisallowedToolsHint,
            string settingsDisallowedToolsTooltip,
            string settingsDangerZoneTitle,
            string settingsDangerZoneWarning,
            string settingsDangerZoneToggle,
            string settingsDangerZoneTooltip,
            string settingsSectionCustomInstructions,
            string settingsCustomInstructionsHint,
            string settingsCustomInstructionsTooltip,
            string settingsSectionDisplay,
            string settingsShowThinkingLabel,
            string settingsShowThinkingTooltip,
            string settingsExpandSubagentLabel,
            string settingsExpandSubagentHint,
            string settingsShowCostLabel,
            string settingsShowCostHint,
            string settingsSectionQuickActions,
            string settingsQuickActionsHint,
            string settingsQuickActionsTooltip,
            string settingsAddQuickActionButton,
            string settingsQuickActionLabelTooltip,
            string settingsQuickActionPromptTooltip,
            string settingsQuickActionRemoveButton,
            string settingsQuickActionNewDefaultLabel,
            string settingsSectionNotifications,
            string settingsNotificationsHint,
            string settingsPermissionBeepLabel,
            string settingsTurnCompleteBeepLabel,
            string settingsSectionConsoleErrors,
            string settingsConsoleErrorsHint,
            string settingsConsoleErrorsTooltip,
            string settingsIgnoredErrorPatternsLabel,
            string settingsIgnoredErrorPatternsHint,
            string settingsIgnoredErrorPatternsTooltip,
            string settingsIgnoredErrorsClearAllButton,
            string settingsIgnoredErrorRemoveButton,
            string settingsSectionAppearance,
            string settingsFontSizeLabel,
            string settingsFontSizeTooltip,
            string settingsFontSizeValueFmt,
            string settingsCjkToggleLabel,
            string settingsCjkDiagnosticNone,
            string settingsCjkDiagnosticDetectedFmt,
            string settingsCjkDiagnosticViaFontFixFmt,
            string settingsSectionDiagnostics,
            string settingsDiagnosticsHint,
            string settingsCopyButton,
            string settingsClearButton,
            string settingsDiagnosticsEmpty,
            string settingsSectionAbout,
            string settingsPackageVersionFmt,
            string settingsPackageVersionUnknown,
            string settingsCliVersionFmt,
            string settingsCliVersionNotConnected,
            string settingsOpenChangelogButton,
            string settingsOpenGitHubButton,
            string settingsLanguageLabel,
            string settingsLanguageTooltip,
            string settingsLanguageOptionAuto,
            string hubWindowTitle,
            string hubPermissionPendingTooltip,
            string navBackButtonLabel,
            string navBackButtonTooltip,
            string markdownCodeDefaultLang,
            string markdownCodeCopyButton,
            string dragDropOverlayLabel,
            string ctxSelectionExtraSuffixFmt,
            string ctxSelectionSummaryHeaderSingularFmt,
            string ctxSelectionSummaryHeaderPluralFmt,
            string ctxSelectionMoreObjectsFmt,
            string ctxSelectionAssetFmt,
            string ctxSelectionGenericFmt,
            string ctxSceneUntitledFallback,
            string ctxSelectionSceneLabelFmt,
            string ctxSelectionActiveLabelFmt,
            string ctxSelectionYes,
            string ctxSelectionNo,
            string ctxSelectionInactiveInHierarchy,
            string ctxSelectionComponentsLabel,
            string ctxSelectionMissingScript,
            string ctxSelectionMoreComponentsFmt,
            string ctxSceneSummaryHeaderFmt,
            string ctxSceneSummaryPathFmt,
            string ctxSceneLoadedLabelFmt,
            string ctxSceneRootObjectsLabelFmt,
            string ctxSceneRootInactiveSuffix,
            string ctxSceneMoreRootsFmt,
            string ctxErrorsDigestHeaderFmt,
            string ctxErrorsDigestEntryFmt,
            string ctxErrorsDigestOccurrencesFmt,
            string ctxErrorsDigestLocationFmt,
            string ctxErrorsDigestMoreFmt,
            string hubProcessDiedReconnectingFmt,
            string hubProcessDiedSuspendedFmt,
            string hubTurnStalledNote,
            string hubCliErrorFmt,
            string hubSyntheticResponse,
            string hubCliNotFoundErrorFmt,
            string hubCliStartFailedFmt,
            string hubPermissionDefaultToolName,
            string hubPermissionDeniedFmt,
            string settingsCliVersionUnconfirmedFmt,
            string chatThinkingRedactedNote,
            string settingsSectionModel,
            string settingsDefaultModelLabel,
            string settingsDefaultModelHint,
            string settingsDefaultModelHintNoCatalog,
            string settingsModelChoiceDefaultLabel,
            string settingsAgentOverridesHint,
            string settingsAgentOverridesTooltip,
            string settingsAddAgentOverrideButton,
            string settingsAgentOverrideNameTooltip,
            string settingsAgentOverrideModelTooltip,
            string settingsAgentOverrideRemoveButton,
            string settingsAgentOverrideDuplicateNameWarning,
            string settingsAgentOverridesNewSessionHint,
            string settingsSubagentModelLabel,
            string settingsSubagentModelHint,
            string settingsSubagentModelTooltip,
            string settingsSubagentModelSameAsDefaultLabel,
            string settingsAgentOverridesFoldoutTitle,
            string settingsSubagentPrecedenceWarning,
            string settingsAgentOverrideNamePlaceholder,
            string firstRunLoginButton,
            string settingsAccountCheckingStatus,
            string settingsAccountUnavailable,
            string settingsAccountNotLoggedIn,
            string settingsAccountLoggedInFmt,
            string settingsAccountSubscriptionUnknown,
            string settingsAccountLoginButton,
            string settingsAccountSwitchButton,
            string settingsAccountLogoutButton,
            string settingsAccountLogoutConfirmTitle,
            string settingsAccountLogoutConfirmBody,
            string settingsAccountLogoutConfirmButton,
            string settingsAccountLogoutCancelButton,
            string settingsAccountLoginStarting,
            string settingsAccountLoginWaitingForCode,
            string settingsAccountLoginVerifying,
            string settingsAccountLoginFailedNotice,
            string settingsAccountLoginUrlLabel,
            string settingsAccountOpenBrowserButton,
            string settingsAccountCopyButton,
            string settingsAccountCodeLabel,
            string settingsAccountSubmitCodeButton,
            string settingsAccountCancelLoginButton,
            string settingsAccountLoggedInNoDetail,
            string settingsAccountEnvTokenNote,
            string settingsSubagentCostPolicyLabel,
            string settingsSubagentCostPolicyHint,
            string settingsSubagentCostPolicyTooltip,
            string settingsSubagentCostPolicyOptionAgentDecides,
            string settingsSubagentCostPolicyOptionHaikuForSimpleTasks,
            string settingsDefaultModelChoiceResolvedFmt,
            string headerModelOptionDefaultSuffixFmt,
            string settingsSectionUapOps,
            string settingsUapOpsHint,
            string settingsUapOpsTooltip,
            string settingsUapOpsEnabledLabel,
            string settingsUapOpsEnabledTooltip,
            string settingsUapOpsModuleCoreLabel,
            string settingsUapOpsModuleCoreHint,
            string settingsUapOpsStatusRunningFmt,
            string settingsUapOpsStatusStopped,
            string settingsUapOpsStatusDisabled,
            string hubScriptGateAutoDeniedFmt,
            string settingsUapOpsGateEnabledLabel,
            string settingsUapOpsGateEnabledHint,
            string settingsUapOpsGateEnabledTooltip,
            string settingsUapOpsStagingFolderHintFmt,
            string settingsUapOpsStagingFolderTooltip,
            string permUndoNotSupportedBadge,
            string permUndoNotSupportedTooltip,
            string permDiffShowAllFmt,
            string hubTurnNonUndoableWarningFmt,
            string subagentNoDetails,
            string settingsUapOpsModulePrefabLabel,
            string settingsUapOpsModulePrefabHint,
            string settingsUapOpsModuleEditorLabel,
            string settingsUapOpsModuleEditorHint,
            string settingsUapOpsModuleAnimLabel,
            string settingsUapOpsModuleAnimHint,
            string settingsUapOpsModuleMarkersLabel,
            string settingsUapOpsModuleMarkersHint,
            string ctxMarkersChipSingle,
            string ctxMarkersChipPluralFmt,
            string ctxMarkersClearTooltip,
            string ctxPinButton,
            string ctxPinButtonArmed,
            string ctxPinToolbarLabel,
            string ctxPinTooltip,
            string ctxPinChipLabelFmt,
            string ctxPinChipTitleFmt,
            string ctxPinMarkerLabel,
            string ctxPinHitSurface,
            string ctxPinHitNothingFmt,
            string ctxPinPayloadHeaderFmt,
            string ctxPinPayloadPositionFmt,
            string ctxPinPayloadHitFmt,
            string ctxPinPayloadNearestFmt,
            string ctxPinPayloadCameraFmt,
            string ctxSketchButton,
            string ctxSketchButtonArmedPlane,
            string ctxSketchButtonArmedSurface,
            string ctxSketchTooltip,
            string ctxSketchMenuPlane,
            string ctxSketchMenuSurface,
            string ctxSketchMenuStop,
            string ctxSketchPlaneToolbarLabel,
            string ctxSketchPlaneTooltip,
            string ctxSketchSurfaceToolbarLabel,
            string ctxSketchSurfaceTooltip,
            string ctxSketchChipLabelFmt,
            string ctxSketchChipTitleFmt,
            string ctxSketchDepthReadoutFmt,
            string ctxSketchAxisCamera,
            string ctxSketchContourPartial,
            string ctxSketchSurfaceBehindFmt,
            string ctxSketchSurfaceInFrontFmt,
            string ctxSketchKeyHints,
            string ctxStrokePayloadHeaderFmt,
            string ctxStrokePayloadModeFmt,
            string ctxStrokePayloadStatsFmt,
            string ctxStrokePayloadClosedYes,
            string ctxStrokePayloadClosedNo,
            string ctxStrokePayloadPlaneFmt,
            string ctxStrokePayloadObjectsFmt,
            string ctxStrokePayloadBoundsFmt,
            string ctxStrokePayloadPointsFmt,
            string ctxStrokePayloadMorePointsFmt,
            string ctxStrokePayloadAllPointsFmt,
            string settingsSectionExtensionProfiles,
            string settingsExtensionProfilesHint,
            string settingsExtensionProfilesTooltip,
            string settingsExtensionProfilesEnabledLabel,
            string settingsExtensionProfilesEnabledTooltip,
            string settingsExtensionProfilesEmptyHint,
            string settingsExtensionProfilesStatusBundled,
            string settingsExtensionProfilesStatusApproved,
            string settingsExtensionProfilesStatusPending,
            string settingsExtensionProfilesApproveButton,
            string settingsExtensionProfilesRevokeButton,
            string settingsExtensionProfilesApproveDialogTitleFmt,
            string settingsExtensionProfilesApproveDialogBodyFmt,
            string settingsExtensionProfilesApproveDialogConfirmButton,
            string settingsExtensionProfilesApproveDialogCancelButton,
            string permAddRuleSuggestionFmt,
            string permRuleAllCallsSuffixFmt,
            string hubScriptGateInertWarning,
            string settingsPermissionModeOptionDefault,
            string settingsPermissionModeOptionPlan,
            string settingsPermissionModeOptionAcceptEdits,
            string hubAcpCommandNotFoundErrorFmt,
            string hubAcpStartFailedFmt,
            string firstRunAcpNotFoundTitleFmt,
            string firstRunAcpNotFoundBody,
            string firstRunAcpLoginHintFmt,
            string settingsBackendLabel,
            string settingsBackendTooltip,
            string settingsBackendOptionCustom,
            string settingsAcpCommandLabel,
            string settingsAcpArgumentsLabel,
            string settingsAcpCommandHintFmt,
            string settingsAcpCommandHintCustom,
            string settingsAcpAuthMethodHint,
            string settingsAcpAuthMethodTooltip,
            string settingsAcpLoginHintFmt,
            string settingsAcpLimitationsHint,
            string settingsAcpLimitationsTooltip,
            string installButtonFmt,
            string installManualFoldout,
            string installManualBody,
            string installRunningFmt,
            string installDoneFmt,
            string installFailedFmt,
            string installTimedOut,
            string installShellMissing,
            string installNodeMissing,
            string installOpenNodeButton,
            string installConfirmTitleFmt,
            string installConfirmBodyFmt,
            string installConfirmButton,
            string installCancelButton,
            string hubAcpSignInStartedNoteFmt,
            string hubAcpSignInUrlNoteFmt,
            string hubAcpSignInFailedNoteFmt,
            string statusWaitingSignIn,
            string settingsAccountAcpConnectedFmt,
            string settingsAccountAcpNotConnected,
            string settingsAccountAcpSignInPendingFmt,
            string settingsAccountAcpHint,
            string hubAcpSignInRequiredNoteFmt,
            string hubAcpHandshakeDeathNoteFmt,
            string agentGenericName = null,
            string hubSessionNotResumedAcrossBackendsNoteFmt = null,
            string settingsClaudeAuthHint = null,
            string settingsClaudeAuthTooltip = null,
            string settingsClaudeAuthOptionAuto = null,
            string settingsClaudeAuthOptionSubscriptionOnly = null,
            string settingsAccountApiKeyAuthNoteFmt = null,
            string hubApiKeyAuthNoteFmt = null,
            string acpAuthSummaryGemini = null,
            string acpAuthSummaryCodex = null,
            string acpAuthSummaryGrok = null,
            string acpAuthDetailGemini = null,
            string acpAuthDetailCodex = null,
            string acpAuthDetailGrok = null,
            string acpAuthKeysNotStoredNote = null,
            string hubAcpApiKeyGuidanceFmt = null,
            string settingsAccountAcpAuthMethodFmt = null,
            string settingsAccountAcpAuthMethodStored = null,
            string settingsAccountAcpApiKeyAuthNoteFmt = null,
            string hubAcpApiKeyAuthNoteFmt = null,
            string settingsUapOpsProAbsentHintFmt = null,
            string settingsUapOpsProAbsentTooltip = null,
            string settingsExtensionProfilesNoBundledHint = null,
            string settingsExtensionProfilesNoBundledTooltip = null,
            string settingsExtensionProfilesGapsFmt = null,
            string settingsExtensionProfilesGapsMoreFmt = null,
            string settingsExtensionProfilesGapsTooltip = null,
            string settingsExtensionProfilesGapsButton = null,
            string settingsExtensionProfilesGapsCopied = null,
            string settingsExtensionProfilesGapsRequestFmt = null,
            string settingsUapOpsModuleUiLabel = null,
            string settingsUapOpsModuleUiHint = null,
            string settingsUapOpsModuleAuthoringLabel = null,
            string settingsUapOpsModuleAuthoringHint = null,
            string settingsUapOpsModuleAvatarLabel = null,
            string settingsUapOpsModuleAvatarHint = null,
            string settingsUapOpsModuleBatchLabel = null,
            string settingsUapOpsModuleBatchHint = null,
            string settingsUapOpsModuleTestsLabel = null,
            string settingsUapOpsModuleTestsHint = null,
            string settingsUapOpsModuleFxLabel = null,
            string settingsUapOpsModuleFxHint = null,
            string settingsUapOpsModuleMeshLabel = null,
            string settingsUapOpsModuleMeshHint = null,
            string settingsUapOpsModuleMeshTooltip = null,
            string settingsUapOpsTestFrameworkAbsentHintFmt = null,
            string settingsUapOpsTestFrameworkAbsentTooltip = null,
            string settingsSectionProUpdates = null,
            string settingsProUpdatesHint = null,
            string settingsProUpdatesTooltip = null,
            string settingsProUpdatesUrlLabel = null,
            string settingsProUpdatesKeyLabel = null,
            string settingsProUpdatesApplyButton = null,
            string settingsProUpdatesStatusAppliedFmt = null,
            string settingsProUpdatesStatusErrorEmptyKey = null,
            string settingsProUpdatesStatusErrorUrl = null,
            string settingsProUpdatesStatusErrorForeignFmt = null,
            string settingsProUpdatesStatusErrorManifestFmt = null,
            string settingsProUpdatesStatusErrorWriteFmt = null,
            string settingsProUpdatesVccButton = null,
            string settingsProUpdatesVccTooltip = null,
            string settingsProUpdatesStatusVccOpenedFmt = null,
            string settingsProUpdatesStatusErrorVccNoKey = null,
            string hubAcpSessionNotResumedNoteFmt = null,
            string settingsSubagentModelAcpHintFmt = null,
            string settingsAccountAcpLoginButton = null,
            string settingsAccountAcpLoginHintFmt = null,
            string settingsAccountAcpLoginRunningFmt = null,
            string hubAcpLoginCommandNotFoundFmt = null,
            string hubAcpLoginStartedNoteFmt = null,
            string hubAcpLoginFinishedNoteFmt = null,
            string firstRunAcpLoginTitleFmt = null,
            string firstRunAcpLoginLeadFmt = null,
            string firstRunAcpLoginAltBody = null,
            string settingsSubagentModelAcpTooltip = null,
            string hubModelSwitchQueuedNoteFmt = null,
            string hubModelSwitchedNoteFmt = null,
            string hubModelSwitchFailedNoteFmt = null,
            string settingsBackendHint = null,
            string settingsSectionAgent = null,
            string settingsSignInMethodLabel = null,
            string settingsAgentAdvancedFoldout = null,
            string settingsReconnectButton = null,
            string settingsAccountAcpConnectingFmt = null,
            string settingsUapOpsModuleWebLabel = null,
            string settingsUapOpsModuleWebHint = null,
            string settingsWebFetchAllowedHostsLabel = null,
            string settingsWebFetchAllowedHostsHint = null,
            string settingsWebFetchBlockedHostsLabel = null,
            string settingsWebFetchBlockedHostsHint = null)
        {
            FirstRunCliNotFoundTitle = firstRunCliNotFoundTitle;
            FirstRunCliNotFoundBody = firstRunCliNotFoundBody;
            FirstRunBrowseButton = firstRunBrowseButton;
            FirstRunRedetectButton = firstRunRedetectButton;
            FirstRunLoginTitle = firstRunLoginTitle;
            FirstRunLoginLead = firstRunLoginLead;
            FirstRunLoginAltFoldout = firstRunLoginAltFoldout;
            FirstRunLoginAltBody = firstRunLoginAltBody;
            FirstRunLoginBody2 = firstRunLoginBody2;
            FirstRunCheckAgainButton = firstRunCheckAgainButton;
            FirstRunBrowseDialogTitle = firstRunBrowseDialogTitle;
            BannerContinueButton = bannerContinueButton;
            BannerReconnectButton = bannerReconnectButton;
            BannerErroredText = bannerErroredText;
            BannerInterruptedText = bannerInterruptedText;
            BannerReconnectingText = bannerReconnectingText;
            BannerResumedText = bannerResumedText;
            PermWaitingInWindowText = permWaitingInWindowText;
            PermShowHereButton = permShowHereButton;
            PermShowHereTooltip = permShowHereTooltip;
            PermChevronTooltip = permChevronTooltip;
            PermOpenWindowTooltip = permOpenWindowTooltip;
            PermTitleFmt = permTitleFmt;
            PermTitleWithDescriptionFmt = permTitleWithDescriptionFmt;
            PermInlineTitleWithDescriptionFmt = permInlineTitleWithDescriptionFmt;
            PermAllowButton = permAllowButton;
            PermAlwaysButtonLabel = permAlwaysButtonLabel;
            PermAlwaysTooltip = permAlwaysTooltip;
            PermQueueDepthFmt = permQueueDepthFmt;
            PermDenyButton = permDenyButton;
            PermDenyFieldTooltip = permDenyFieldTooltip;
            PermKeyboardHint = permKeyboardHint;
            PermSetModeSuggestionFmt = permSetModeSuggestionFmt;
            PermTruncatedMoreLinesFmt = permTruncatedMoreLinesFmt;
            PermQuestionTitleSingle = permQuestionTitleSingle;
            PermQuestionTitlePlural = permQuestionTitlePlural;
            PermQuestionTabFallbackFmt = permQuestionTabFallbackFmt;
            PermOtherOptionLabel = permOtherOptionLabel;
            PermOtherPlaceholder = permOtherPlaceholder;
            PermOtherFieldCaption = permOtherFieldCaption;
            PermDenyFieldCaption = permDenyFieldCaption;
            PermSubmitButton = permSubmitButton;
            PermSkipButton = permSkipButton;
            PermSkipTooltip = permSkipTooltip;
            PermWindowTitle = permWindowTitle;
            CtxAttachSelectionButton = ctxAttachSelectionButton;
            CtxAttachSelectionTooltip = ctxAttachSelectionTooltip;
            CtxAttachSceneButton = ctxAttachSceneButton;
            CtxAttachSceneTooltip = ctxAttachSceneTooltip;
            CtxSceneAttachedButton = ctxSceneAttachedButton;
            CtxFixErrorsButton = ctxFixErrorsButton;
            CtxRemoveTooltip = ctxRemoveTooltip;
            CtxSelectionChipTitleFmt = ctxSelectionChipTitleFmt;
            CtxSceneChipLabelFmt = ctxSceneChipLabelFmt;
            CtxFixErrorsPrompt = ctxFixErrorsPrompt;
            CtxConsoleErrorTitleSingle = ctxConsoleErrorTitleSingle;
            CtxConsoleErrorTitlePluralFmt = ctxConsoleErrorTitlePluralFmt;
            CtxDefaultChipLabel = ctxDefaultChipLabel;
            CtxPingTooltip = ctxPingTooltip;
            CtxErrorSingle = ctxErrorSingle;
            CtxErrorPluralFmt = ctxErrorPluralFmt;
            CtxErrorDismissForNowTooltip = ctxErrorDismissForNowTooltip;
            CtxErrorMenuTooltip = ctxErrorMenuTooltip;
            CtxErrorIgnoreForeverMenuItem = ctxErrorIgnoreForeverMenuItem;
            CtxErrorIgnoredNoticeSingle = ctxErrorIgnoredNoticeSingle;
            CtxErrorIgnoredNoticePluralFmt = ctxErrorIgnoredNoticePluralFmt;
            CtxErrorIgnoreUndoButton = ctxErrorIgnoreUndoButton;
            CtxGameObjectTitleFmt = ctxGameObjectTitleFmt;
            CtxObjectTitleTypeFirstFmt = ctxObjectTitleTypeFirstFmt;
            CtxUnnamedFallback = ctxUnnamedFallback;
            ToolCardDefaultName = toolCardDefaultName;
            HistoryTitle = historyTitle;
            HistoryRefreshTooltip = historyRefreshTooltip;
            HistoryEmptyMessage = historyEmptyMessage;
            HistoryNoPreview = historyNoPreview;
            HistoryCurrentBadge = historyCurrentBadge;
            HistorySwitchConfirmText = historySwitchConfirmText;
            HistoryCancelButton = historyCancelButton;
            HistorySwitchButton = historySwitchButton;
            HistoryRelativeJustNow = historyRelativeJustNow;
            HistoryRelativeOneMinuteAgo = historyRelativeOneMinuteAgo;
            HistoryRelativeMinutesAgoFmt = historyRelativeMinutesAgoFmt;
            HistoryRelativeOneHourAgo = historyRelativeOneHourAgo;
            HistoryRelativeHoursAgoFmt = historyRelativeHoursAgoFmt;
            HistoryRelativeOneDayAgo = historyRelativeOneDayAgo;
            HistoryRelativeDaysAgoFmt = historyRelativeDaysAgoFmt;
            HistoryRelativeOneMonthAgo = historyRelativeOneMonthAgo;
            HistoryRelativeMonthsAgoFmt = historyRelativeMonthsAgoFmt;
            HistoryRelativeOneYearAgo = historyRelativeOneYearAgo;
            HistoryRelativeYearsAgoFmt = historyRelativeYearsAgoFmt;
            HistorySizeBytesFmt = historySizeBytesFmt;
            HistorySizeKbFmt = historySizeKbFmt;
            HistorySizeMbFmt = historySizeMbFmt;
            HistorySearchPlaceholder = historySearchPlaceholder;
            HistorySearchNoMatch = historySearchNoMatch;
            HistoryGroupByLabel = historyGroupByLabel;
            HistoryGroupByDate = historyGroupByDate;
            HistoryGroupByScene = historyGroupByScene;
            HistoryGroupByProject = historyGroupByProject;
            HistoryGroupByCustom = historyGroupByCustom;
            HistoryShowArchivedLabel = historyShowArchivedLabel;
            HistoryGroupPinned = historyGroupPinned;
            HistoryGroupToday = historyGroupToday;
            HistoryGroupYesterday = historyGroupYesterday;
            HistoryGroupLast7 = historyGroupLast7;
            HistoryGroupLast30 = historyGroupLast30;
            HistoryGroupOlder = historyGroupOlder;
            HistoryGroupNoScene = historyGroupNoScene;
            HistoryGroupNoProject = historyGroupNoProject;
            HistoryGroupUngrouped = historyGroupUngrouped;
            HistoryShowMoreFmt = historyShowMoreFmt;
            HistoryMenuTooltip = historyMenuTooltip;
            HistoryActionPin = historyActionPin;
            HistoryActionUnpin = historyActionUnpin;
            HistoryActionRename = historyActionRename;
            HistoryActionArchive = historyActionArchive;
            HistoryActionUnarchive = historyActionUnarchive;
            HistoryActionDelete = historyActionDelete;
            HistoryActionGroupSubmenu = historyActionGroupSubmenu;
            HistoryActionGroupNone = historyActionGroupNone;
            HistoryActionGroupNew = historyActionGroupNew;
            HistoryMenuGroupBack = historyMenuGroupBack;
            HistoryDeleteDisabledForeignTooltip = historyDeleteDisabledForeignTooltip;
            HistoryActionOpen = historyActionOpen;
            HistoryOpenDisabledForeignTooltip = historyOpenDisabledForeignTooltip;
            HistoryDeleteDisabledLiveTooltip = historyDeleteDisabledLiveTooltip;
            HistoryRenamePlaceholder = historyRenamePlaceholder;
            HistoryRenameSaveButton = historyRenameSaveButton;
            HistoryRenameResetButton = historyRenameResetButton;
            HistoryDeleteConfirmTextFmt = historyDeleteConfirmTextFmt;
            HistoryDeleteButton = historyDeleteButton;
            HistoryDeleteFailed = historyDeleteFailed;
            HistoryNewGroupPlaceholder = historyNewGroupPlaceholder;
            HistoryNewGroupCreate = historyNewGroupCreate;
            HistoryForeignProjectNote = historyForeignProjectNote;
            HeaderDefaultTitle = headerDefaultTitle;
            StatusModelUnknown = statusModelUnknown;
            HeaderModelPickerTooltip = headerModelPickerTooltip;
            HeaderHistoryButton = headerHistoryButton;
            HeaderHistoryTooltip = headerHistoryTooltip;
            HeaderCompactTooltip = headerCompactTooltip;
            HeaderCompactModelItemFmt = headerCompactModelItemFmt;
            HeaderCompactAutoApproveItemFmt = headerCompactAutoApproveItemFmt;
            HeaderNewSessionTooltip = headerNewSessionTooltip;
            HeaderSettingsTooltip = headerSettingsTooltip;
            HeaderReconnectTooltip = headerReconnectTooltip;
            HeaderModelOptionWithDescriptionFmt = headerModelOptionWithDescriptionFmt;
            HeaderModelPickerConnectFirst = headerModelPickerConnectFirst;
            AutoApproveLevelAsk = autoApproveLevelAsk;
            AutoApproveLevelReadOnly = autoApproveLevelReadOnly;
            AutoApproveLevelUndoable = autoApproveLevelUndoable;
            AutoApproveLevelAllUnityOps = autoApproveLevelAllUnityOps;
            AutoApproveLevelAllTools = autoApproveLevelAllTools;
            AutoApproveShortAllTools = autoApproveShortAllTools;
            AutoApproveConfirmAllToolsTitle = autoApproveConfirmAllToolsTitle;
            AutoApproveConfirmAllToolsBody = autoApproveConfirmAllToolsBody;
            AutoApproveShortAsk = autoApproveShortAsk;
            AutoApproveShortReadOnly = autoApproveShortReadOnly;
            AutoApproveShortUndoable = autoApproveShortUndoable;
            AutoApproveShortAllUnityOps = autoApproveShortAllUnityOps;
            AutoApproveMenuTitle = autoApproveMenuTitle;
            HeaderAutoApproveTooltipFmt = headerAutoApproveTooltipFmt;
            AutoApproveConfirmAllTitle = autoApproveConfirmAllTitle;
            AutoApproveConfirmAllBody = autoApproveConfirmAllBody;
            AutoApproveConfirmAllConfirmButton = autoApproveConfirmAllConfirmButton;
            AutoApproveConfirmAllCancelButton = autoApproveConfirmAllCancelButton;
            HubAutoContinuePendingWillContinue = hubAutoContinuePendingWillContinue;
            HubAutoContinuePendingOff = hubAutoContinuePendingOff;
            HubAutoContinueResuming = hubAutoContinueResuming;
            HubCompactedManualFmt = hubCompactedManualFmt;
            HubCompactedManual = hubCompactedManual;
            HubCompactedAutoFmt = hubCompactedAutoFmt;
            HubCompactedAuto = hubCompactedAuto;
            HubAutoContinueSendAbandonedFmt = hubAutoContinueSendAbandonedFmt;
            HubAutoContinueInterruptedResuming = hubAutoContinueInterruptedResuming;
            HubReloadDroppedPermissionFmt = hubReloadDroppedPermissionFmt;
            HubSessionCacheUnreadable = hubSessionCacheUnreadable;
            SettingsUloopSectionTitle = settingsUloopSectionTitle;
            SettingsUloopStatusInstalled = settingsUloopStatusInstalled;
            SettingsUloopStatusMissing = settingsUloopStatusMissing;
            SettingsUloopInstallButton = settingsUloopInstallButton;
            SettingsUloopInstallConfirmTitle = settingsUloopInstallConfirmTitle;
            SettingsUloopInstallApply = settingsUloopInstallApply;
            SettingsUloopInstallCancel = settingsUloopInstallCancel;
            SettingsUloopInstallViaGitFmt = settingsUloopInstallViaGitFmt;
            SettingsUloopInstallViaManifest = settingsUloopInstallViaManifest;
            SettingsUloopInstallPathsTooltipFmt = settingsUloopInstallPathsTooltipFmt;
            SettingsUloopInstallDiffFoldout = settingsUloopInstallDiffFoldout;
            SettingsUloopInstallingFmt = settingsUloopInstallingFmt;
            SettingsUloopInstallAsyncFailedFmt = settingsUloopInstallAsyncFailedFmt;
            SettingsUloopInstallStalled = settingsUloopInstallStalled;
            SettingsUnityPluginSectionTitle = settingsUnityPluginSectionTitle;
            SettingsUnityPluginStatusCliUnavailable = settingsUnityPluginStatusCliUnavailable;
            SettingsUnityPluginStatusNotInstalled = settingsUnityPluginStatusNotInstalled;
            SettingsUnityPluginStatusDisabled = settingsUnityPluginStatusDisabled;
            SettingsUnityPluginStatusInstalledFmt = settingsUnityPluginStatusInstalledFmt;
            SettingsUnityPluginStatusEnabledNotLoadedFmt = settingsUnityPluginStatusEnabledNotLoadedFmt;
            SettingsUnityPluginStatusLoadedFmt = settingsUnityPluginStatusLoadedFmt;
            SettingsUnityPluginStatusLoadErrorFmt = settingsUnityPluginStatusLoadErrorFmt;
            SettingsUnityPluginInstallButton = settingsUnityPluginInstallButton;
            SettingsUnityPluginInstallConfirmTitle = settingsUnityPluginInstallConfirmTitle;
            SettingsUnityPluginCaveatScope = settingsUnityPluginCaveatScope;
            SettingsUnityPluginCaveatUnity6 = settingsUnityPluginCaveatUnity6;
            SettingsUnityPluginInstallApply = settingsUnityPluginInstallApply;
            SettingsUnityPluginInstallCancel = settingsUnityPluginInstallCancel;
            SettingsUnityPluginInstallingFmt = settingsUnityPluginInstallingFmt;
            SettingsUnityPluginInstalled = settingsUnityPluginInstalled;
            SettingsUnityPluginInstallFailedFmt = settingsUnityPluginInstallFailedFmt;
            SettingsUnityPluginStageMarketplace = settingsUnityPluginStageMarketplace;
            SettingsUnityPluginStageInstall = settingsUnityPluginStageInstall;
            SettingsUnityPluginInstallStalled = settingsUnityPluginInstallStalled;
            SettingsUnityPluginDetailsFoldout = settingsUnityPluginDetailsFoldout;
            SettingsUnityPluginDetailsBody = settingsUnityPluginDetailsBody;
            SettingsUnityPluginRepoLabel = settingsUnityPluginRepoLabel;
            SettingsUnityPluginSteeringLabel = settingsUnityPluginSteeringLabel;
            SettingsUnityPluginSteeringTooltip = settingsUnityPluginSteeringTooltip;
            SettingsUloopCaveatVcc = settingsUloopCaveatVcc;
            SettingsUloopCaveatOffline = settingsUloopCaveatOffline;
            SettingsUloopCaveatManifestUnreadable = settingsUloopCaveatManifestUnreadable;
            SettingsUloopCaveatRegistryConflict = settingsUloopCaveatRegistryConflict;
            SettingsUloopInstallFailed = settingsUloopInstallFailed;
            SettingsUloopInstallFailedManifestUnknownFmt = settingsUloopInstallFailedManifestUnknownFmt;
            SettingsUloopPresetButton = settingsUloopPresetButton;
            SettingsUloopPresetHelp = settingsUloopPresetHelp;
            SettingsUloopPresetTooltip = settingsUloopPresetTooltip;
            SettingsUloopPresetApplied = settingsUloopPresetApplied;
            SettingsUloopSnippetButton = settingsUloopSnippetButton;
            SettingsUloopSnippetHelp = settingsUloopSnippetHelp;
            SettingsUloopSnippetTooltip = settingsUloopSnippetTooltip;
            SettingsUloopSnippetApplied = settingsUloopSnippetApplied;
            SettingsAutoContinueLabel = settingsAutoContinueLabel;
            SettingsAutoContinueHelp = settingsAutoContinueHelp;
            SettingsAutoContinueTooltip = settingsAutoContinueTooltip;
            SettingsAutoContinueInterruptedLabel = settingsAutoContinueInterruptedLabel;
            SettingsAutoContinueInterruptedHelp = settingsAutoContinueInterruptedHelp;
            SettingsAutoContinueInterruptedTooltip = settingsAutoContinueInterruptedTooltip;
            SettingsPlayModeReloadOnHint = settingsPlayModeReloadOnHint;
            SettingsPlayModeReloadOffHint = settingsPlayModeReloadOffHint;
            SettingsPlayModeReloadTooltip = settingsPlayModeReloadTooltip;
            SettingsPlayModeOpenProjectSettingsButton = settingsPlayModeOpenProjectSettingsButton;
            SettingsAutoApproveLabel = settingsAutoApproveLabel;
            SettingsAutoApproveMovedHint = settingsAutoApproveMovedHint;
            SettingsAutoApproveMovedTooltip = settingsAutoApproveMovedTooltip;
            SettingsAutoApproveHelp = settingsAutoApproveHelp;
            SettingsAutoApproveTooltip = settingsAutoApproveTooltip;
            ToolCardSectionInput = toolCardSectionInput;
            ToolCardSectionResult = toolCardSectionResult;
            ToolCardSectionError = toolCardSectionError;
            ToolCardSectionChanges = toolCardSectionChanges;
            ToolCardMoreLinesFmt = toolCardMoreLinesFmt;
            ToolCardMoreCharsFmt = toolCardMoreCharsFmt;
            ToolCardDurationSecondsFmt = toolCardDurationSecondsFmt;
            ToolGroupCountFmt = toolGroupCountFmt;
            ToolGroupCountWithDurationFmt = toolGroupCountWithDurationFmt;
            ChatRoleUser = chatRoleUser;
            ChatRoleAssistant = chatRoleAssistant;
            ChatDefaultAttachTitle = chatDefaultAttachTitle;
            ChatThinkingStreaming = chatThinkingStreaming;
            ChatThinkingDone = chatThinkingDone;
            ChatThinkingIndicatorStreaming = chatThinkingIndicatorStreaming;
            ChatThinkingIndicatorStreamingTokensFmt = chatThinkingIndicatorStreamingTokensFmt;
            ChatThinkingIndicatorDone = chatThinkingIndicatorDone;
            ChatThinkingIndicatorDoneTokensFmt = chatThinkingIndicatorDoneTokensFmt;
            ChatPruneNoteFmt = chatPruneNoteFmt;
            ChatJumpToLatestButton = chatJumpToLatestButton;
            ChatCompactingIndicator = chatCompactingIndicator;
            StatusDisconnected = statusDisconnected;
            StatusCliNotFound = statusCliNotFound;
            StatusConnecting = statusConnecting;
            StatusConnectingSlow = statusConnectingSlow;
            StatusIdle = statusIdle;
            StatusResponding = statusResponding;
            StatusCompacting = statusCompacting;
            StatusRunningTool = statusRunningTool;
            StatusWaitingPermission = statusWaitingPermission;
            StatusError = statusError;
            StatusApplyingSettings = statusApplyingSettings;
            StatusContextPercentFmt = statusContextPercentFmt;
            StatusContextWarnTooltip = statusContextWarnTooltip;
            StatusContextCompactedLabel = statusContextCompactedLabel;
            StatusContextCompactedTooltip = statusContextCompactedTooltip;
            StatusUsageZeroTokens = statusUsageZeroTokens;
            StatusUsageTokensFmt = statusUsageTokensFmt;
            StatusUsageTokensWithCostFmt = statusUsageTokensWithCostFmt;
            StatusUsagePopoverTitle = statusUsagePopoverTitle;
            StatusUsagePopoverEmpty = statusUsagePopoverEmpty;
            StatusUsagePopoverBreakdownPending = statusUsagePopoverBreakdownPending;
            StatusUsagePopoverLineFmt = statusUsagePopoverLineFmt;
            StatusUsagePopoverCostSuffixFmt = statusUsagePopoverCostSuffixFmt;
            StatusUsagePopoverContextFmt = statusUsagePopoverContextFmt;
            EmptyTitle = emptyTitle;
            EmptySubtitle = emptySubtitle;
            EmptySuggestionProject = emptySuggestionProject;
            EmptySuggestionScene = emptySuggestionScene;
            EmptySuggestionErrors = emptySuggestionErrors;
            EmptySuggestionSelection = emptySuggestionSelection;
            ComposerQuickButton = composerQuickButton;
            ComposerQuickTooltip = composerQuickTooltip;
            ComposerAttachButton = composerAttachButton;
            ComposerAttachTooltip = composerAttachTooltip;
            ComposerImageOnlyDisplay = composerImageOnlyDisplay;
            AttachMenuImageFile = attachMenuImageFile;
            AttachMenuSceneView = attachMenuSceneView;
            AttachMenuGameView = attachMenuGameView;
            AttachMenuSceneViewWindow = attachMenuSceneViewWindow;
            AttachImageRemoveTooltip = attachImageRemoveTooltip;
            AttachImageErrorTitle = attachImageErrorTitle;
            AttachImageErrorOk = attachImageErrorOk;
            AttachImageUnsupportedFmt = attachImageUnsupportedFmt;
            AttachImageMissingFmt = attachImageMissingFmt;
            AttachImageLoadFailedFmt = attachImageLoadFailedFmt;
            AttachImageTooLargeFmt = attachImageTooLargeFmt;
            AttachImageTooManyFmt = attachImageTooManyFmt;
            AttachMenuClipboard = attachMenuClipboard;
            AttachClipboardEmpty = attachClipboardEmpty;
            AttachClipboardFailedFmt = attachClipboardFailedFmt;
            ChatImageOpenTooltip = chatImageOpenTooltip;
            ChatImageMissing = chatImageMissing;
            ChatImageDefaultCaption = chatImageDefaultCaption;
            ComposerSendButton = composerSendButton;
            ComposerStopButton = composerStopButton;
            ComposerNoQuickActionsMenuItem = composerNoQuickActionsMenuItem;
            ComposerQuickActionUntitled = composerQuickActionUntitled;
            ComposerPlaceholderCtrlEnter = composerPlaceholderCtrlEnter;
            ComposerPlaceholderEnter = composerPlaceholderEnter;
            ComposerPlaceholderPermissionPending = composerPlaceholderPermissionPending;
            ComposerHintCompiling = composerHintCompiling;
            ComposerHintQueuedFmt = composerHintQueuedFmt;
            ComposerHintEscToStop = composerHintEscToStop;
            ComposerHintTurnSendAndStop = composerHintTurnSendAndStop;
            ComposerSendTooltip = composerSendTooltip;
            ComposerStopTooltip = composerStopTooltip;
            ComposerSlashHint = composerSlashHint;
            ComposerSlashNoMatch = composerSlashNoMatch;
            SlashCompactDescription = slashCompactDescription;
            SlashClearDescription = slashClearDescription;
            SubagentDefaultType = subagentDefaultType;
            SubagentDefaultDescription = subagentDefaultDescription;
            SubagentDropNoteFmt = subagentDropNoteFmt;
            SubagentProgressToolFmt = subagentProgressToolFmt;
            SubagentProgressTokensFmt = subagentProgressTokensFmt;
            SubagentProgressWorking = subagentProgressWorking;
            SubagentProgressStepsFmt = subagentProgressStepsFmt;
            SubagentProgressUpdatedAgoFmt = subagentProgressUpdatedAgoFmt;
            SettingsTitle = settingsTitle;
            SettingsGroupConversation = settingsGroupConversation;
            SettingsGroupDisplay = settingsGroupDisplay;
            SettingsGroupUnity = settingsGroupUnity;
            SettingsGroupConnection = settingsGroupConnection;
            SettingsCliPathLabel = settingsCliPathLabel;
            SettingsCliPathHint = settingsCliPathHint;
            SettingsCliPathTooltip = settingsCliPathTooltip;
            SettingsReconnectNowButton = settingsReconnectNowButton;
            SettingsCliResolvedFmt = settingsCliResolvedFmt;
            SettingsCliNotFoundFmt = settingsCliNotFoundFmt;
            SettingsReconnectPendingHint = settingsReconnectPendingHint;
            SettingsReconnectPendingPill = settingsReconnectPendingPill;
            SettingsReconnectPendingHintDeferred = settingsReconnectPendingHintDeferred;
            SettingsReconnectPendingPillDeferred = settingsReconnectPendingPillDeferred;
            SettingsSectionConversation = settingsSectionConversation;
            SettingsPermissionModeLabel = settingsPermissionModeLabel;
            SettingsPermissionModeTooltip = settingsPermissionModeTooltip;
            SettingsCtrlEnterLabel = settingsCtrlEnterLabel;
            SettingsCtrlEnterHint = settingsCtrlEnterHint;
            SettingsCtrlEnterTooltip = settingsCtrlEnterTooltip;
            SettingsAllowedToolsLabel = settingsAllowedToolsLabel;
            SettingsAllowedToolsHint = settingsAllowedToolsHint;
            SettingsAllowedToolsTooltip = settingsAllowedToolsTooltip;
            SettingsDisallowedToolsLabel = settingsDisallowedToolsLabel;
            SettingsDisallowedToolsHint = settingsDisallowedToolsHint;
            SettingsDisallowedToolsTooltip = settingsDisallowedToolsTooltip;
            SettingsDangerZoneTitle = settingsDangerZoneTitle;
            SettingsDangerZoneWarning = settingsDangerZoneWarning;
            SettingsDangerZoneToggle = settingsDangerZoneToggle;
            SettingsDangerZoneTooltip = settingsDangerZoneTooltip;
            SettingsSectionCustomInstructions = settingsSectionCustomInstructions;
            SettingsCustomInstructionsHint = settingsCustomInstructionsHint;
            SettingsCustomInstructionsTooltip = settingsCustomInstructionsTooltip;
            SettingsSectionDisplay = settingsSectionDisplay;
            SettingsShowThinkingLabel = settingsShowThinkingLabel;
            SettingsShowThinkingTooltip = settingsShowThinkingTooltip;
            SettingsExpandSubagentLabel = settingsExpandSubagentLabel;
            SettingsExpandSubagentHint = settingsExpandSubagentHint;
            SettingsShowCostLabel = settingsShowCostLabel;
            SettingsShowCostHint = settingsShowCostHint;
            SettingsSectionQuickActions = settingsSectionQuickActions;
            SettingsQuickActionsHint = settingsQuickActionsHint;
            SettingsQuickActionsTooltip = settingsQuickActionsTooltip;
            SettingsAddQuickActionButton = settingsAddQuickActionButton;
            SettingsQuickActionLabelTooltip = settingsQuickActionLabelTooltip;
            SettingsQuickActionPromptTooltip = settingsQuickActionPromptTooltip;
            SettingsQuickActionRemoveButton = settingsQuickActionRemoveButton;
            SettingsQuickActionNewDefaultLabel = settingsQuickActionNewDefaultLabel;
            SettingsSectionNotifications = settingsSectionNotifications;
            SettingsNotificationsHint = settingsNotificationsHint;
            SettingsPermissionBeepLabel = settingsPermissionBeepLabel;
            SettingsTurnCompleteBeepLabel = settingsTurnCompleteBeepLabel;
            SettingsSectionConsoleErrors = settingsSectionConsoleErrors;
            SettingsConsoleErrorsHint = settingsConsoleErrorsHint;
            SettingsConsoleErrorsTooltip = settingsConsoleErrorsTooltip;
            SettingsIgnoredErrorPatternsLabel = settingsIgnoredErrorPatternsLabel;
            SettingsIgnoredErrorPatternsHint = settingsIgnoredErrorPatternsHint;
            SettingsIgnoredErrorPatternsTooltip = settingsIgnoredErrorPatternsTooltip;
            SettingsIgnoredErrorsClearAllButton = settingsIgnoredErrorsClearAllButton;
            SettingsIgnoredErrorRemoveButton = settingsIgnoredErrorRemoveButton;
            SettingsSectionAppearance = settingsSectionAppearance;
            SettingsFontSizeLabel = settingsFontSizeLabel;
            SettingsFontSizeTooltip = settingsFontSizeTooltip;
            SettingsFontSizeValueFmt = settingsFontSizeValueFmt;
            SettingsCjkToggleLabel = settingsCjkToggleLabel;
            SettingsCjkDiagnosticNone = settingsCjkDiagnosticNone;
            SettingsCjkDiagnosticDetectedFmt = settingsCjkDiagnosticDetectedFmt;
            SettingsCjkDiagnosticViaFontFixFmt = settingsCjkDiagnosticViaFontFixFmt;
            SettingsSectionDiagnostics = settingsSectionDiagnostics;
            SettingsDiagnosticsHint = settingsDiagnosticsHint;
            SettingsCopyButton = settingsCopyButton;
            SettingsClearButton = settingsClearButton;
            SettingsDiagnosticsEmpty = settingsDiagnosticsEmpty;
            SettingsSectionAbout = settingsSectionAbout;
            SettingsPackageVersionFmt = settingsPackageVersionFmt;
            SettingsPackageVersionUnknown = settingsPackageVersionUnknown;
            SettingsCliVersionFmt = settingsCliVersionFmt;
            SettingsCliVersionNotConnected = settingsCliVersionNotConnected;
            SettingsOpenChangelogButton = settingsOpenChangelogButton;
            SettingsOpenGitHubButton = settingsOpenGitHubButton;
            SettingsLanguageLabel = settingsLanguageLabel;
            SettingsLanguageTooltip = settingsLanguageTooltip;
            SettingsLanguageOptionAuto = settingsLanguageOptionAuto;
            HubWindowTitle = hubWindowTitle;
            HubPermissionPendingTooltip = hubPermissionPendingTooltip;
            NavBackButtonLabel = navBackButtonLabel;
            NavBackButtonTooltip = navBackButtonTooltip;
            MarkdownCodeDefaultLang = markdownCodeDefaultLang;
            MarkdownCodeCopyButton = markdownCodeCopyButton;
            DragDropOverlayLabel = dragDropOverlayLabel;
            CtxSelectionExtraSuffixFmt = ctxSelectionExtraSuffixFmt;
            CtxSelectionSummaryHeaderSingularFmt = ctxSelectionSummaryHeaderSingularFmt;
            CtxSelectionSummaryHeaderPluralFmt = ctxSelectionSummaryHeaderPluralFmt;
            CtxSelectionMoreObjectsFmt = ctxSelectionMoreObjectsFmt;
            CtxSelectionAssetFmt = ctxSelectionAssetFmt;
            CtxSelectionGenericFmt = ctxSelectionGenericFmt;
            CtxSceneUntitledFallback = ctxSceneUntitledFallback;
            CtxSelectionSceneLabelFmt = ctxSelectionSceneLabelFmt;
            CtxSelectionActiveLabelFmt = ctxSelectionActiveLabelFmt;
            CtxSelectionYes = ctxSelectionYes;
            CtxSelectionNo = ctxSelectionNo;
            CtxSelectionInactiveInHierarchy = ctxSelectionInactiveInHierarchy;
            CtxSelectionComponentsLabel = ctxSelectionComponentsLabel;
            CtxSelectionMissingScript = ctxSelectionMissingScript;
            CtxSelectionMoreComponentsFmt = ctxSelectionMoreComponentsFmt;
            CtxSceneSummaryHeaderFmt = ctxSceneSummaryHeaderFmt;
            CtxSceneSummaryPathFmt = ctxSceneSummaryPathFmt;
            CtxSceneLoadedLabelFmt = ctxSceneLoadedLabelFmt;
            CtxSceneRootObjectsLabelFmt = ctxSceneRootObjectsLabelFmt;
            CtxSceneRootInactiveSuffix = ctxSceneRootInactiveSuffix;
            CtxSceneMoreRootsFmt = ctxSceneMoreRootsFmt;
            CtxErrorsDigestHeaderFmt = ctxErrorsDigestHeaderFmt;
            CtxErrorsDigestEntryFmt = ctxErrorsDigestEntryFmt;
            CtxErrorsDigestOccurrencesFmt = ctxErrorsDigestOccurrencesFmt;
            CtxErrorsDigestLocationFmt = ctxErrorsDigestLocationFmt;
            CtxErrorsDigestMoreFmt = ctxErrorsDigestMoreFmt;
            HubProcessDiedReconnectingFmt = hubProcessDiedReconnectingFmt;
            HubProcessDiedSuspendedFmt = hubProcessDiedSuspendedFmt;
            HubTurnStalledNote = hubTurnStalledNote;
            HubCliErrorFmt = hubCliErrorFmt;
            HubSyntheticResponse = hubSyntheticResponse;
            HubCliNotFoundErrorFmt = hubCliNotFoundErrorFmt;
            HubCliStartFailedFmt = hubCliStartFailedFmt;
            HubPermissionDefaultToolName = hubPermissionDefaultToolName;
            HubPermissionDeniedFmt = hubPermissionDeniedFmt;
            SettingsCliVersionUnconfirmedFmt = settingsCliVersionUnconfirmedFmt;
            ChatThinkingRedactedNote = chatThinkingRedactedNote;
            SettingsSectionModel = settingsSectionModel;
            SettingsDefaultModelLabel = settingsDefaultModelLabel;
            SettingsDefaultModelHint = settingsDefaultModelHint;
            SettingsDefaultModelHintNoCatalog = settingsDefaultModelHintNoCatalog;
            SettingsModelChoiceDefaultLabel = settingsModelChoiceDefaultLabel;
            SettingsAgentOverridesHint = settingsAgentOverridesHint;
            SettingsAgentOverridesTooltip = settingsAgentOverridesTooltip;
            SettingsAddAgentOverrideButton = settingsAddAgentOverrideButton;
            SettingsAgentOverrideNameTooltip = settingsAgentOverrideNameTooltip;
            SettingsAgentOverrideModelTooltip = settingsAgentOverrideModelTooltip;
            SettingsAgentOverrideRemoveButton = settingsAgentOverrideRemoveButton;
            SettingsAgentOverrideDuplicateNameWarning = settingsAgentOverrideDuplicateNameWarning;
            SettingsAgentOverridesNewSessionHint = settingsAgentOverridesNewSessionHint;
            SettingsSubagentModelLabel = settingsSubagentModelLabel;
            SettingsSubagentModelHint = settingsSubagentModelHint;
            SettingsSubagentModelTooltip = settingsSubagentModelTooltip;
            SettingsSubagentModelSameAsDefaultLabel = settingsSubagentModelSameAsDefaultLabel;
            SettingsAgentOverridesFoldoutTitle = settingsAgentOverridesFoldoutTitle;
            SettingsSubagentPrecedenceWarning = settingsSubagentPrecedenceWarning;
            SettingsAgentOverrideNamePlaceholder = settingsAgentOverrideNamePlaceholder;
            FirstRunLoginButton = firstRunLoginButton;
            SettingsAccountCheckingStatus = settingsAccountCheckingStatus;
            SettingsAccountUnavailable = settingsAccountUnavailable;
            SettingsAccountNotLoggedIn = settingsAccountNotLoggedIn;
            SettingsAccountLoggedInFmt = settingsAccountLoggedInFmt;
            SettingsAccountSubscriptionUnknown = settingsAccountSubscriptionUnknown;
            SettingsAccountLoginButton = settingsAccountLoginButton;
            SettingsAccountSwitchButton = settingsAccountSwitchButton;
            SettingsAccountLogoutButton = settingsAccountLogoutButton;
            SettingsAccountLogoutConfirmTitle = settingsAccountLogoutConfirmTitle;
            SettingsAccountLogoutConfirmBody = settingsAccountLogoutConfirmBody;
            SettingsAccountLogoutConfirmButton = settingsAccountLogoutConfirmButton;
            SettingsAccountLogoutCancelButton = settingsAccountLogoutCancelButton;
            SettingsAccountLoginStarting = settingsAccountLoginStarting;
            SettingsAccountLoginWaitingForCode = settingsAccountLoginWaitingForCode;
            SettingsAccountLoginVerifying = settingsAccountLoginVerifying;
            SettingsAccountLoginFailedNotice = settingsAccountLoginFailedNotice;
            SettingsAccountLoginUrlLabel = settingsAccountLoginUrlLabel;
            SettingsAccountOpenBrowserButton = settingsAccountOpenBrowserButton;
            SettingsAccountCopyButton = settingsAccountCopyButton;
            SettingsAccountCodeLabel = settingsAccountCodeLabel;
            SettingsAccountSubmitCodeButton = settingsAccountSubmitCodeButton;
            SettingsAccountCancelLoginButton = settingsAccountCancelLoginButton;
            SettingsAccountLoggedInNoDetail = settingsAccountLoggedInNoDetail;
            SettingsAccountEnvTokenNote = settingsAccountEnvTokenNote;
            SettingsSubagentCostPolicyLabel = settingsSubagentCostPolicyLabel;
            SettingsSubagentCostPolicyHint = settingsSubagentCostPolicyHint;
            SettingsSubagentCostPolicyTooltip = settingsSubagentCostPolicyTooltip;
            SettingsSubagentCostPolicyOptionAgentDecides = settingsSubagentCostPolicyOptionAgentDecides;
            SettingsSubagentCostPolicyOptionHaikuForSimpleTasks = settingsSubagentCostPolicyOptionHaikuForSimpleTasks;
            SettingsDefaultModelChoiceResolvedFmt = settingsDefaultModelChoiceResolvedFmt;
            HeaderModelOptionDefaultSuffixFmt = headerModelOptionDefaultSuffixFmt;
            SettingsSectionUapOps = settingsSectionUapOps;
            SettingsUapOpsHint = settingsUapOpsHint;
            SettingsUapOpsTooltip = settingsUapOpsTooltip;
            SettingsUapOpsEnabledLabel = settingsUapOpsEnabledLabel;
            SettingsUapOpsEnabledTooltip = settingsUapOpsEnabledTooltip;
            SettingsUapOpsModuleCoreLabel = settingsUapOpsModuleCoreLabel;
            SettingsUapOpsModuleCoreHint = settingsUapOpsModuleCoreHint;
            SettingsUapOpsStatusRunningFmt = settingsUapOpsStatusRunningFmt;
            SettingsUapOpsStatusStopped = settingsUapOpsStatusStopped;
            SettingsUapOpsStatusDisabled = settingsUapOpsStatusDisabled;
            HubScriptGateAutoDeniedFmt = hubScriptGateAutoDeniedFmt;
            SettingsUapOpsGateEnabledLabel = settingsUapOpsGateEnabledLabel;
            SettingsUapOpsGateEnabledHint = settingsUapOpsGateEnabledHint;
            SettingsUapOpsGateEnabledTooltip = settingsUapOpsGateEnabledTooltip;
            SettingsUapOpsStagingFolderHintFmt = settingsUapOpsStagingFolderHintFmt;
            SettingsUapOpsStagingFolderTooltip = settingsUapOpsStagingFolderTooltip;
            PermUndoNotSupportedBadge = permUndoNotSupportedBadge;
            PermUndoNotSupportedTooltip = permUndoNotSupportedTooltip;
            PermDiffShowAllFmt = permDiffShowAllFmt;
            HubTurnNonUndoableWarningFmt = hubTurnNonUndoableWarningFmt;
            SubagentNoDetails = subagentNoDetails;
            SettingsUapOpsModulePrefabLabel = settingsUapOpsModulePrefabLabel;
            SettingsUapOpsModulePrefabHint = settingsUapOpsModulePrefabHint;
            SettingsUapOpsModuleEditorLabel = settingsUapOpsModuleEditorLabel;
            SettingsUapOpsModuleEditorHint = settingsUapOpsModuleEditorHint;
            SettingsUapOpsModuleAnimLabel = settingsUapOpsModuleAnimLabel;
            SettingsUapOpsModuleAnimHint = settingsUapOpsModuleAnimHint;
            SettingsUapOpsModuleMarkersLabel = settingsUapOpsModuleMarkersLabel;
            SettingsUapOpsModuleMarkersHint = settingsUapOpsModuleMarkersHint;
            CtxMarkersChipSingle = ctxMarkersChipSingle;
            CtxMarkersChipPluralFmt = ctxMarkersChipPluralFmt;
            CtxMarkersClearTooltip = ctxMarkersClearTooltip;
            CtxPinButton = ctxPinButton;
            CtxPinButtonArmed = ctxPinButtonArmed;
            CtxPinToolbarLabel = ctxPinToolbarLabel;
            CtxPinTooltip = ctxPinTooltip;
            CtxPinChipLabelFmt = ctxPinChipLabelFmt;
            CtxPinChipTitleFmt = ctxPinChipTitleFmt;
            CtxPinMarkerLabel = ctxPinMarkerLabel;
            CtxPinHitSurface = ctxPinHitSurface;
            CtxPinHitNothingFmt = ctxPinHitNothingFmt;
            CtxPinPayloadHeaderFmt = ctxPinPayloadHeaderFmt;
            CtxPinPayloadPositionFmt = ctxPinPayloadPositionFmt;
            CtxPinPayloadHitFmt = ctxPinPayloadHitFmt;
            CtxPinPayloadNearestFmt = ctxPinPayloadNearestFmt;
            CtxPinPayloadCameraFmt = ctxPinPayloadCameraFmt;
            CtxSketchButton = ctxSketchButton;
            CtxSketchButtonArmedPlane = ctxSketchButtonArmedPlane;
            CtxSketchButtonArmedSurface = ctxSketchButtonArmedSurface;
            CtxSketchTooltip = ctxSketchTooltip;
            CtxSketchMenuPlane = ctxSketchMenuPlane;
            CtxSketchMenuSurface = ctxSketchMenuSurface;
            CtxSketchMenuStop = ctxSketchMenuStop;
            CtxSketchPlaneToolbarLabel = ctxSketchPlaneToolbarLabel;
            CtxSketchPlaneTooltip = ctxSketchPlaneTooltip;
            CtxSketchSurfaceToolbarLabel = ctxSketchSurfaceToolbarLabel;
            CtxSketchSurfaceTooltip = ctxSketchSurfaceTooltip;
            CtxSketchChipLabelFmt = ctxSketchChipLabelFmt;
            CtxSketchChipTitleFmt = ctxSketchChipTitleFmt;
            CtxSketchDepthReadoutFmt = ctxSketchDepthReadoutFmt;
            CtxSketchAxisCamera = ctxSketchAxisCamera;
            CtxSketchContourPartial = ctxSketchContourPartial;
            CtxSketchSurfaceBehindFmt = ctxSketchSurfaceBehindFmt;
            CtxSketchSurfaceInFrontFmt = ctxSketchSurfaceInFrontFmt;
            CtxSketchKeyHints = ctxSketchKeyHints;
            CtxStrokePayloadHeaderFmt = ctxStrokePayloadHeaderFmt;
            CtxStrokePayloadModeFmt = ctxStrokePayloadModeFmt;
            CtxStrokePayloadStatsFmt = ctxStrokePayloadStatsFmt;
            CtxStrokePayloadClosedYes = ctxStrokePayloadClosedYes;
            CtxStrokePayloadClosedNo = ctxStrokePayloadClosedNo;
            CtxStrokePayloadPlaneFmt = ctxStrokePayloadPlaneFmt;
            CtxStrokePayloadObjectsFmt = ctxStrokePayloadObjectsFmt;
            CtxStrokePayloadBoundsFmt = ctxStrokePayloadBoundsFmt;
            CtxStrokePayloadPointsFmt = ctxStrokePayloadPointsFmt;
            CtxStrokePayloadMorePointsFmt = ctxStrokePayloadMorePointsFmt;
            CtxStrokePayloadAllPointsFmt = ctxStrokePayloadAllPointsFmt;
            SettingsSectionExtensionProfiles = settingsSectionExtensionProfiles;
            SettingsExtensionProfilesHint = settingsExtensionProfilesHint;
            SettingsExtensionProfilesTooltip = settingsExtensionProfilesTooltip;
            SettingsExtensionProfilesEnabledLabel = settingsExtensionProfilesEnabledLabel;
            SettingsExtensionProfilesEnabledTooltip = settingsExtensionProfilesEnabledTooltip;
            SettingsExtensionProfilesEmptyHint = settingsExtensionProfilesEmptyHint;
            SettingsExtensionProfilesStatusBundled = settingsExtensionProfilesStatusBundled;
            SettingsExtensionProfilesStatusApproved = settingsExtensionProfilesStatusApproved;
            SettingsExtensionProfilesStatusPending = settingsExtensionProfilesStatusPending;
            SettingsExtensionProfilesApproveButton = settingsExtensionProfilesApproveButton;
            SettingsExtensionProfilesRevokeButton = settingsExtensionProfilesRevokeButton;
            SettingsExtensionProfilesApproveDialogTitleFmt = settingsExtensionProfilesApproveDialogTitleFmt;
            SettingsExtensionProfilesApproveDialogBodyFmt = settingsExtensionProfilesApproveDialogBodyFmt;
            SettingsExtensionProfilesApproveDialogConfirmButton = settingsExtensionProfilesApproveDialogConfirmButton;
            SettingsExtensionProfilesApproveDialogCancelButton = settingsExtensionProfilesApproveDialogCancelButton;
            PermAddRuleSuggestionFmt = permAddRuleSuggestionFmt;
            PermRuleAllCallsSuffixFmt = permRuleAllCallsSuffixFmt;
            HubScriptGateInertWarning = hubScriptGateInertWarning;
            SettingsPermissionModeOptionDefault = settingsPermissionModeOptionDefault;
            SettingsPermissionModeOptionPlan = settingsPermissionModeOptionPlan;
            SettingsPermissionModeOptionAcceptEdits = settingsPermissionModeOptionAcceptEdits;
            HubAcpCommandNotFoundErrorFmt = hubAcpCommandNotFoundErrorFmt;
            HubAcpStartFailedFmt = hubAcpStartFailedFmt;
            FirstRunAcpNotFoundTitleFmt = firstRunAcpNotFoundTitleFmt;
            FirstRunAcpNotFoundBody = firstRunAcpNotFoundBody;
            FirstRunAcpLoginHintFmt = firstRunAcpLoginHintFmt;
            SettingsBackendLabel = settingsBackendLabel;
            SettingsBackendTooltip = settingsBackendTooltip;
            SettingsBackendOptionCustom = settingsBackendOptionCustom;
            SettingsAcpCommandLabel = settingsAcpCommandLabel;
            SettingsAcpArgumentsLabel = settingsAcpArgumentsLabel;
            SettingsAcpCommandHintFmt = settingsAcpCommandHintFmt;
            SettingsAcpCommandHintCustom = settingsAcpCommandHintCustom;
            SettingsAcpAuthMethodHint = settingsAcpAuthMethodHint;
            SettingsAcpAuthMethodTooltip = settingsAcpAuthMethodTooltip;
            SettingsAcpLoginHintFmt = settingsAcpLoginHintFmt;
            SettingsAcpLimitationsHint = settingsAcpLimitationsHint;
            SettingsAcpLimitationsTooltip = settingsAcpLimitationsTooltip;
            InstallButtonFmt = installButtonFmt;
            InstallManualFoldout = installManualFoldout;
            InstallManualBody = installManualBody;
            InstallRunningFmt = installRunningFmt;
            InstallDoneFmt = installDoneFmt;
            InstallFailedFmt = installFailedFmt;
            InstallTimedOut = installTimedOut;
            InstallShellMissing = installShellMissing;
            InstallNodeMissing = installNodeMissing;
            InstallOpenNodeButton = installOpenNodeButton;
            InstallConfirmTitleFmt = installConfirmTitleFmt;
            InstallConfirmBodyFmt = installConfirmBodyFmt;
            InstallConfirmButton = installConfirmButton;
            InstallCancelButton = installCancelButton;
            HubAcpSignInStartedNoteFmt = hubAcpSignInStartedNoteFmt;
            HubAcpSignInUrlNoteFmt = hubAcpSignInUrlNoteFmt;
            HubAcpSignInFailedNoteFmt = hubAcpSignInFailedNoteFmt;
            StatusWaitingSignIn = statusWaitingSignIn;
            SettingsAccountAcpConnectedFmt = settingsAccountAcpConnectedFmt;
            SettingsAccountAcpNotConnected = settingsAccountAcpNotConnected;
            SettingsAccountAcpSignInPendingFmt = settingsAccountAcpSignInPendingFmt;
            SettingsAccountAcpHint = settingsAccountAcpHint;
            HubAcpSignInRequiredNoteFmt = hubAcpSignInRequiredNoteFmt;
            HubAcpHandshakeDeathNoteFmt = hubAcpHandshakeDeathNoteFmt;
            if (agentGenericName != null)
            {
                AgentGenericName = agentGenericName;
            }
            if (hubSessionNotResumedAcrossBackendsNoteFmt != null)
            {
                HubSessionNotResumedAcrossBackendsNoteFmt = hubSessionNotResumedAcrossBackendsNoteFmt;
            }
            if (settingsClaudeAuthHint != null)
            {
                SettingsClaudeAuthHint = settingsClaudeAuthHint;
            }
            if (settingsClaudeAuthTooltip != null)
            {
                SettingsClaudeAuthTooltip = settingsClaudeAuthTooltip;
            }
            if (settingsClaudeAuthOptionAuto != null)
            {
                SettingsClaudeAuthOptionAuto = settingsClaudeAuthOptionAuto;
            }
            if (settingsClaudeAuthOptionSubscriptionOnly != null)
            {
                SettingsClaudeAuthOptionSubscriptionOnly = settingsClaudeAuthOptionSubscriptionOnly;
            }
            if (settingsAccountApiKeyAuthNoteFmt != null)
            {
                SettingsAccountApiKeyAuthNoteFmt = settingsAccountApiKeyAuthNoteFmt;
            }
            if (hubApiKeyAuthNoteFmt != null)
            {
                HubApiKeyAuthNoteFmt = hubApiKeyAuthNoteFmt;
            }
            if (acpAuthSummaryGemini != null)
            {
                AcpAuthSummaryGemini = acpAuthSummaryGemini;
            }
            if (acpAuthSummaryCodex != null)
            {
                AcpAuthSummaryCodex = acpAuthSummaryCodex;
            }
            if (acpAuthSummaryGrok != null)
            {
                AcpAuthSummaryGrok = acpAuthSummaryGrok;
            }
            if (acpAuthDetailGemini != null)
            {
                AcpAuthDetailGemini = acpAuthDetailGemini;
            }
            if (acpAuthDetailCodex != null)
            {
                AcpAuthDetailCodex = acpAuthDetailCodex;
            }
            if (acpAuthDetailGrok != null)
            {
                AcpAuthDetailGrok = acpAuthDetailGrok;
            }
            if (acpAuthKeysNotStoredNote != null)
            {
                AcpAuthKeysNotStoredNote = acpAuthKeysNotStoredNote;
            }
            if (hubAcpApiKeyGuidanceFmt != null)
            {
                HubAcpApiKeyGuidanceFmt = hubAcpApiKeyGuidanceFmt;
            }
            if (settingsAccountAcpAuthMethodFmt != null)
            {
                SettingsAccountAcpAuthMethodFmt = settingsAccountAcpAuthMethodFmt;
            }
            if (settingsAccountAcpAuthMethodStored != null)
            {
                SettingsAccountAcpAuthMethodStored = settingsAccountAcpAuthMethodStored;
            }
            if (settingsAccountAcpApiKeyAuthNoteFmt != null)
            {
                SettingsAccountAcpApiKeyAuthNoteFmt = settingsAccountAcpApiKeyAuthNoteFmt;
            }
            if (hubAcpApiKeyAuthNoteFmt != null)
            {
                HubAcpApiKeyAuthNoteFmt = hubAcpApiKeyAuthNoteFmt;
            }
            if (settingsUapOpsProAbsentHintFmt != null)
            {
                SettingsUapOpsProAbsentHintFmt = settingsUapOpsProAbsentHintFmt;
            }
            if (settingsUapOpsProAbsentTooltip != null)
            {
                SettingsUapOpsProAbsentTooltip = settingsUapOpsProAbsentTooltip;
            }
            if (settingsExtensionProfilesNoBundledHint != null)
            {
                SettingsExtensionProfilesNoBundledHint = settingsExtensionProfilesNoBundledHint;
            }
            if (settingsExtensionProfilesNoBundledTooltip != null)
            {
                SettingsExtensionProfilesNoBundledTooltip = settingsExtensionProfilesNoBundledTooltip;
            }
            if (settingsExtensionProfilesGapsFmt != null)
            {
                SettingsExtensionProfilesGapsFmt = settingsExtensionProfilesGapsFmt;
            }
            if (settingsExtensionProfilesGapsMoreFmt != null)
            {
                SettingsExtensionProfilesGapsMoreFmt = settingsExtensionProfilesGapsMoreFmt;
            }
            if (settingsExtensionProfilesGapsTooltip != null)
            {
                SettingsExtensionProfilesGapsTooltip = settingsExtensionProfilesGapsTooltip;
            }
            if (settingsExtensionProfilesGapsButton != null)
            {
                SettingsExtensionProfilesGapsButton = settingsExtensionProfilesGapsButton;
            }
            if (settingsExtensionProfilesGapsCopied != null)
            {
                SettingsExtensionProfilesGapsCopied = settingsExtensionProfilesGapsCopied;
            }
            if (settingsExtensionProfilesGapsRequestFmt != null)
            {
                SettingsExtensionProfilesGapsRequestFmt = settingsExtensionProfilesGapsRequestFmt;
            }
            if (settingsUapOpsModuleUiLabel != null)
            {
                SettingsUapOpsModuleUiLabel = settingsUapOpsModuleUiLabel;
            }
            if (settingsUapOpsModuleUiHint != null)
            {
                SettingsUapOpsModuleUiHint = settingsUapOpsModuleUiHint;
            }
            if (settingsUapOpsModuleAuthoringLabel != null)
            {
                SettingsUapOpsModuleAuthoringLabel = settingsUapOpsModuleAuthoringLabel;
            }
            if (settingsUapOpsModuleAuthoringHint != null)
            {
                SettingsUapOpsModuleAuthoringHint = settingsUapOpsModuleAuthoringHint;
            }
            if (settingsUapOpsModuleAvatarLabel != null)
            {
                SettingsUapOpsModuleAvatarLabel = settingsUapOpsModuleAvatarLabel;
            }
            if (settingsUapOpsModuleAvatarHint != null)
            {
                SettingsUapOpsModuleAvatarHint = settingsUapOpsModuleAvatarHint;
            }
            if (settingsUapOpsModuleBatchLabel != null)
            {
                SettingsUapOpsModuleBatchLabel = settingsUapOpsModuleBatchLabel;
            }
            if (settingsUapOpsModuleBatchHint != null)
            {
                SettingsUapOpsModuleBatchHint = settingsUapOpsModuleBatchHint;
            }
            if (settingsUapOpsModuleTestsLabel != null)
            {
                SettingsUapOpsModuleTestsLabel = settingsUapOpsModuleTestsLabel;
            }
            if (settingsUapOpsModuleTestsHint != null)
            {
                SettingsUapOpsModuleTestsHint = settingsUapOpsModuleTestsHint;
            }
            if (settingsUapOpsModuleFxLabel != null)
            {
                SettingsUapOpsModuleFxLabel = settingsUapOpsModuleFxLabel;
            }
            if (settingsUapOpsModuleFxHint != null)
            {
                SettingsUapOpsModuleFxHint = settingsUapOpsModuleFxHint;
            }
            if (settingsUapOpsModuleMeshLabel != null)
            {
                SettingsUapOpsModuleMeshLabel = settingsUapOpsModuleMeshLabel;
            }
            if (settingsUapOpsModuleMeshHint != null)
            {
                SettingsUapOpsModuleMeshHint = settingsUapOpsModuleMeshHint;
            }
            if (settingsUapOpsModuleMeshTooltip != null)
            {
                SettingsUapOpsModuleMeshTooltip = settingsUapOpsModuleMeshTooltip;
            }
            if (settingsUapOpsTestFrameworkAbsentHintFmt != null)
            {
                SettingsUapOpsTestFrameworkAbsentHintFmt = settingsUapOpsTestFrameworkAbsentHintFmt;
            }
            if (settingsUapOpsTestFrameworkAbsentTooltip != null)
            {
                SettingsUapOpsTestFrameworkAbsentTooltip = settingsUapOpsTestFrameworkAbsentTooltip;
            }
            if (settingsSectionProUpdates != null)
            {
                SettingsSectionProUpdates = settingsSectionProUpdates;
            }
            if (settingsProUpdatesHint != null)
            {
                SettingsProUpdatesHint = settingsProUpdatesHint;
            }
            if (settingsProUpdatesTooltip != null)
            {
                SettingsProUpdatesTooltip = settingsProUpdatesTooltip;
            }
            if (settingsProUpdatesUrlLabel != null)
            {
                SettingsProUpdatesUrlLabel = settingsProUpdatesUrlLabel;
            }
            if (settingsProUpdatesKeyLabel != null)
            {
                SettingsProUpdatesKeyLabel = settingsProUpdatesKeyLabel;
            }
            if (settingsProUpdatesApplyButton != null)
            {
                SettingsProUpdatesApplyButton = settingsProUpdatesApplyButton;
            }
            if (settingsProUpdatesStatusAppliedFmt != null)
            {
                SettingsProUpdatesStatusAppliedFmt = settingsProUpdatesStatusAppliedFmt;
            }
            if (settingsProUpdatesStatusErrorEmptyKey != null)
            {
                SettingsProUpdatesStatusErrorEmptyKey = settingsProUpdatesStatusErrorEmptyKey;
            }
            if (settingsProUpdatesStatusErrorUrl != null)
            {
                SettingsProUpdatesStatusErrorUrl = settingsProUpdatesStatusErrorUrl;
            }
            if (settingsProUpdatesStatusErrorForeignFmt != null)
            {
                SettingsProUpdatesStatusErrorForeignFmt = settingsProUpdatesStatusErrorForeignFmt;
            }
            if (settingsProUpdatesStatusErrorManifestFmt != null)
            {
                SettingsProUpdatesStatusErrorManifestFmt = settingsProUpdatesStatusErrorManifestFmt;
            }
            if (settingsProUpdatesStatusErrorWriteFmt != null)
            {
                SettingsProUpdatesStatusErrorWriteFmt = settingsProUpdatesStatusErrorWriteFmt;
            }
            if (settingsProUpdatesVccButton != null)
            {
                SettingsProUpdatesVccButton = settingsProUpdatesVccButton;
            }
            if (settingsProUpdatesVccTooltip != null)
            {
                SettingsProUpdatesVccTooltip = settingsProUpdatesVccTooltip;
            }
            if (settingsProUpdatesStatusVccOpenedFmt != null)
            {
                SettingsProUpdatesStatusVccOpenedFmt = settingsProUpdatesStatusVccOpenedFmt;
            }
            if (settingsProUpdatesStatusErrorVccNoKey != null)
            {
                SettingsProUpdatesStatusErrorVccNoKey = settingsProUpdatesStatusErrorVccNoKey;
            }
            if (hubAcpSessionNotResumedNoteFmt != null)
            {
                HubAcpSessionNotResumedNoteFmt = hubAcpSessionNotResumedNoteFmt;
            }
            if (settingsSubagentModelAcpHintFmt != null)
            {
                SettingsSubagentModelAcpHintFmt = settingsSubagentModelAcpHintFmt;
            }
            if (settingsAccountAcpLoginButton != null)
            {
                SettingsAccountAcpLoginButton = settingsAccountAcpLoginButton;
            }
            if (settingsAccountAcpLoginHintFmt != null)
            {
                SettingsAccountAcpLoginHintFmt = settingsAccountAcpLoginHintFmt;
            }
            if (settingsAccountAcpLoginRunningFmt != null)
            {
                SettingsAccountAcpLoginRunningFmt = settingsAccountAcpLoginRunningFmt;
            }
            if (hubAcpLoginCommandNotFoundFmt != null)
            {
                HubAcpLoginCommandNotFoundFmt = hubAcpLoginCommandNotFoundFmt;
            }
            if (hubAcpLoginStartedNoteFmt != null)
            {
                HubAcpLoginStartedNoteFmt = hubAcpLoginStartedNoteFmt;
            }
            if (hubAcpLoginFinishedNoteFmt != null)
            {
                HubAcpLoginFinishedNoteFmt = hubAcpLoginFinishedNoteFmt;
            }
            if (firstRunAcpLoginTitleFmt != null)
            {
                FirstRunAcpLoginTitleFmt = firstRunAcpLoginTitleFmt;
            }
            if (firstRunAcpLoginLeadFmt != null)
            {
                FirstRunAcpLoginLeadFmt = firstRunAcpLoginLeadFmt;
            }
            if (firstRunAcpLoginAltBody != null)
            {
                FirstRunAcpLoginAltBody = firstRunAcpLoginAltBody;
            }
            if (settingsSubagentModelAcpTooltip != null)
            {
                SettingsSubagentModelAcpTooltip = settingsSubagentModelAcpTooltip;
            }
            if (hubModelSwitchQueuedNoteFmt != null)
            {
                HubModelSwitchQueuedNoteFmt = hubModelSwitchQueuedNoteFmt;
            }
            if (hubModelSwitchedNoteFmt != null)
            {
                HubModelSwitchedNoteFmt = hubModelSwitchedNoteFmt;
            }
            if (hubModelSwitchFailedNoteFmt != null)
            {
                HubModelSwitchFailedNoteFmt = hubModelSwitchFailedNoteFmt;
            }
            if (settingsBackendHint != null)
            {
                SettingsBackendHint = settingsBackendHint;
            }
            if (settingsSectionAgent != null)
            {
                SettingsSectionAgent = settingsSectionAgent;
            }
            if (settingsSignInMethodLabel != null)
            {
                SettingsSignInMethodLabel = settingsSignInMethodLabel;
            }
            if (settingsAgentAdvancedFoldout != null)
            {
                SettingsAgentAdvancedFoldout = settingsAgentAdvancedFoldout;
            }
            if (settingsReconnectButton != null)
            {
                SettingsReconnectButton = settingsReconnectButton;
            }
            if (settingsAccountAcpConnectingFmt != null)
            {
                SettingsAccountAcpConnectingFmt = settingsAccountAcpConnectingFmt;
            }
            if (settingsUapOpsModuleWebLabel != null)
            {
                SettingsUapOpsModuleWebLabel = settingsUapOpsModuleWebLabel;
            }
            if (settingsUapOpsModuleWebHint != null)
            {
                SettingsUapOpsModuleWebHint = settingsUapOpsModuleWebHint;
            }
            if (settingsWebFetchAllowedHostsLabel != null)
            {
                SettingsWebFetchAllowedHostsLabel = settingsWebFetchAllowedHostsLabel;
            }
            if (settingsWebFetchAllowedHostsHint != null)
            {
                SettingsWebFetchAllowedHostsHint = settingsWebFetchAllowedHostsHint;
            }
            if (settingsWebFetchBlockedHostsLabel != null)
            {
                SettingsWebFetchBlockedHostsLabel = settingsWebFetchBlockedHostsLabel;
            }
            if (settingsWebFetchBlockedHostsHint != null)
            {
                SettingsWebFetchBlockedHostsHint = settingsWebFetchBlockedHostsHint;
            }
        }
    }
}
