using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Session history browser (ARCHITECTURE.md Phase 3 / R05 section 2.2):
    /// lists the CLI's own session transcripts for this project
    /// (SessionIndex), with search, grouping, paging and per-row
    /// organisation. Selecting a row restores the transcript
    /// (TranscriptLoader) and reconnects with --resume via
    /// AgentHub.SwitchToSession. See
    /// docs/design-notes/2026-07-31-history-restore-flow-and-model-picker.md
    /// for the confirm-bar / double-resume-guard / missing-file-race
    /// rationale, and
    /// docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
    /// for the v0.15.0 browsing rework.
    ///
    /// Every filter/group/sort/page DECISION lives in
    /// <see cref="HistoryListModel"/> as pure functions, and this class only
    /// turns the result into VisualElements. That split is deliberate: an
    /// EditorWindow view is effectively untestable, so anything with a rule
    /// in it is pushed across the boundary where HistoryListModelTests can
    /// pin it.
    /// </summary>
    public sealed class HistoryView : IAgentPanelView
    {
        private const long RefreshIntervalMillis = 250;

        /// <summary>
        /// Rows rendered before the "show more" button appears, and how many
        /// more each press adds. UI Toolkit builds every row eagerly (there
        /// is no virtualisation in this hand-built list), so an unbounded
        /// render makes opening History cost O(all sessions ever) -- the
        /// exact complaint this cap answers.
        /// </summary>
        private const int VisibleRowsPage = 50;

        /// <summary>
        /// SessionState keys for the view preferences. SessionState (not
        /// PanelSettings) on purpose: these are view state, not settings.
        /// They must survive a domain reload -- Unity performs one on every
        /// script compile, and losing the chosen grouping that often would
        /// be maddening -- but they must NOT reach PanelSettings, where
        /// SettingsChangeDetector and the next-spawn snapshot would then
        /// have to grow a case for a preference that has nothing to do with
        /// how the CLI is launched.
        /// </summary>
        private const string GroupModeStateKey = "Colloid.AgentPanel.History.GroupMode";
        private const string ShowArchivedStateKey = "Colloid.AgentPanel.History.ShowArchived";

        private VisualElement _root;
        private VisualElement _listHost;
        private Label _emptyLabel;
        private ToolbarSearchField _searchField;
        private PopupField<string> _groupPopup;
        private Toggle _archivedToggle;

        private SessionIndex _index;
        private bool _active;
        private bool _dirty;
        private IVisualElementScheduledItem _refreshLoop;
        private string _lastKnownLiveSessionId = string.Empty;

        private string _query = string.Empty;
        private HistoryGroupMode _groupMode = HistoryGroupMode.Date;
        private bool _showArchived;
        private int _visibleLimit = VisibleRowsPage;

        /// <summary>
        /// Cross-project scan results, populated lazily the first time the
        /// user actually picks "group by project". It stats every session
        /// file on the machine, so it must never run just because History
        /// was opened.
        /// </summary>
        private List<ProjectSessionScanner.ProjectSessions> _foreignProjects;
        private bool _foreignScanned;

        /// <summary>Session id -> where its transcript is and whether it belongs to this project.</summary>
        private readonly Dictionary<string, RowSource> _rowSources =
            new Dictionary<string, RowSource>(StringComparer.Ordinal);

        /// <summary>Session id whose row currently shows the switch confirm bar (empty = none).</summary>
        private string _pendingSwitchSessionId = string.Empty;
        /// <summary>Session id whose row currently shows the delete confirm bar (empty = none).</summary>
        private string _pendingDeleteSessionId = string.Empty;
        /// <summary>Session id whose row currently shows the rename editor (empty = none).</summary>
        private string _renamingSessionId = string.Empty;
        /// <summary>Session id whose row currently shows the new-group editor (empty = none).</summary>
        private string _newGroupForSessionId = string.Empty;
        /// <summary>Non-empty when the last delete attempt failed, so the row can say so.</summary>
        private string _deleteFailedSessionId = string.Empty;

        private sealed class RowSource
        {
            public SessionIndexEntry Entry;
            public bool Foreign;
        }

        public string Title
        {
            get { return L10n.S.HistoryTitle; }
        }

        // -- IAgentPanelView ------------------------------------------------------

        public VisualElement CreateGUI()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            // ScrollViewMode.Vertical does NOT stop a horizontal scrollbar
            // appearing: horizontalScrollerVisibility stays Auto, so any
            // child that overflows sideways turns into a scrollbar plus a
            // blank strip, and the whole list can be dragged off-screen
            // horizontally. Measured that exact state before the filter bar
            // was made to wrap -- the view was sitting at
            // scrollOffset.x = 189.5 with 190 px of the list scrolled out of
            // sight. A session list is a vertical list; there is no content
            // here that sideways scrolling is ever the right answer for, so
            // the affordance is removed rather than left as a trap that only
            // shows up in a narrow dock or a long localization.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.AddToClassList("uap-history");
            _root = scroll;

            RestoreViewPreferences();

            var toolbar = new VisualElement();
            toolbar.AddToClassList("uap-history-toolbar");
            var title = new Label(L10n.S.HistoryTitle);
            title.AddToClassList("uap-history-title");
            title.enableRichText = false;
            toolbar.Add(title);
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            toolbar.Add(spacer);
            var refresh = new Button(OnRefreshClicked);
            refresh.AddToClassList("uap-history-refresh-btn");
            refresh.tooltip = L10n.S.HistoryRefreshTooltip;
            refresh.Add(IconLoader.CreateIcon("d_Refresh", IconLoader.GlyphDownArrow,
                "uap-viewstrip-icon", "uap-viewstrip-glyph"));
            toolbar.Add(refresh);
            scroll.Add(toolbar);

            scroll.Add(BuildFilterBar());

            _emptyLabel = new Label(L10n.S.HistoryEmptyMessage);
            _emptyLabel.AddToClassList("uap-history-empty");
            _emptyLabel.enableRichText = false;
            scroll.Add(_emptyLabel);

            _listHost = new VisualElement();
            _listHost.AddToClassList("uap-history-list");
            scroll.Add(_listHost);

            return scroll;
        }

        private VisualElement BuildFilterBar()
        {
            var bar = new VisualElement();
            bar.AddToClassList("uap-history-filterbar");

            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("uap-history-search");
            _searchField.tooltip = L10n.S.HistorySearchPlaceholder;
            _searchField.RegisterValueChangedCallback(delegate (ChangeEvent<string> evt)
            {
                _query = evt.newValue ?? string.Empty;
                // Typing narrows the list, so an old "show more" expansion is
                // meaningless and a stale one would hide early matches behind
                // a button. Reset to the first page on every keystroke.
                _visibleLimit = VisibleRowsPage;
                RenderRows();
            });
            bar.Add(_searchField);

            var groupLabel = new Label(L10n.S.HistoryGroupByLabel);
            groupLabel.AddToClassList("uap-history-filter-label");
            groupLabel.enableRichText = false;
            bar.Add(groupLabel);

            List<string> choices = new List<string>
            {
                L10n.S.HistoryGroupByDate,
                L10n.S.HistoryGroupByScene,
                L10n.S.HistoryGroupByProject,
                L10n.S.HistoryGroupByCustom
            };
            _groupPopup = new PopupField<string>(choices, GroupModeToIndex(_groupMode));
            _groupPopup.AddToClassList("uap-history-groupby");
            _groupPopup.RegisterValueChangedCallback(delegate (ChangeEvent<string> evt)
            {
                _groupMode = IndexToGroupMode(choices.IndexOf(evt.newValue));
                SessionState.SetInt(GroupModeStateKey, (int)_groupMode);
                _visibleLimit = VisibleRowsPage;
                RenderRows();
            });
            bar.Add(_groupPopup);

            _archivedToggle = new Toggle(L10n.S.HistoryShowArchivedLabel) { value = _showArchived };
            _archivedToggle.AddToClassList("uap-history-archived-toggle");
            _archivedToggle.RegisterValueChangedCallback(delegate (ChangeEvent<bool> evt)
            {
                _showArchived = evt.newValue;
                SessionState.SetBool(ShowArchivedStateKey, _showArchived);
                _visibleLimit = VisibleRowsPage;
                RenderRows();
            });
            bar.Add(_archivedToggle);

            return bar;
        }

        private void RestoreViewPreferences()
        {
            _groupMode = IndexToGroupMode(SessionState.GetInt(GroupModeStateKey, 0));
            _showArchived = SessionState.GetBool(ShowArchivedStateKey, false);
        }

        private static int GroupModeToIndex(HistoryGroupMode mode)
        {
            switch (mode)
            {
                case HistoryGroupMode.Scene: return 1;
                case HistoryGroupMode.Project: return 2;
                case HistoryGroupMode.Custom: return 3;
                default: return 0;
            }
        }

        private static HistoryGroupMode IndexToGroupMode(int index)
        {
            switch (index)
            {
                case 1: return HistoryGroupMode.Scene;
                case 2: return HistoryGroupMode.Project;
                case 3: return HistoryGroupMode.Custom;
                default: return HistoryGroupMode.Date;
            }
        }

        public void OnActivate()
        {
            if (_active)
            {
                return;
            }
            _active = true;
            AgentHub.Changed += OnHubChanged;
            SessionMetaStoreAccess.Changed += OnMetaChanged;
            _refreshLoop = _root.schedule.Execute(RefreshIfDirty).Every(RefreshIntervalMillis);
            RefreshIndexFromDisk();
        }

        public void OnDeactivate()
        {
            if (!_active)
            {
                return;
            }
            _active = false;
            AgentHub.Changed -= OnHubChanged;
            SessionMetaStoreAccess.Changed -= OnMetaChanged;
            if (_refreshLoop != null)
            {
                _refreshLoop.Pause();
                _refreshLoop = null;
            }
            // Leaving the view collapses any open inline editor -- coming
            // back later should not resurrect a stale destructive prompt or
            // a half-typed rename.
            ClearRowEditors();
        }

        public void SerializeState()
        {
            // The group mode and archived toggle persist through
            // SessionState at the moment they change (see their callbacks),
            // which is what has to survive a domain reload. The search text
            // and any open inline editor are intentionally ephemeral.
        }

        // -- Refresh pipeline --------------------------------------------------------

        private void OnHubChanged()
        {
            // Re-enumerating the directory on every AgentHub.Changed tick
            // would mean a disk scan per streamed token while History
            // happens to be the active view -- wasteful and pointless
            // (files only change on disk once a turn's messages are
            // flushed, not per delta). Instead, only re-render (cheap: no
            // disk I/O, SessionIndex.Entries is already in memory) when the
            // LIVE session id itself changes, so the "Current" highlight
            // follows a session switch/new-session without a manual
            // refresh. A brand new session's first jsonl write can still
            // race this check (see the design note section 1.5's
            // missing-file-race companion case); the explicit Refresh
            // button always resolves it.
            if (!string.Equals(_lastKnownLiveSessionId, AgentHub.Session.sessionId,
                StringComparison.Ordinal))
            {
                _dirty = true;
            }
        }

        private void OnMetaChanged()
        {
            // A pin/archive/rename/group change reorders or relabels rows
            // without touching the transcripts, so re-render but do NOT
            // re-stat the directory.
            _dirty = true;
        }

        private void RefreshIfDirty()
        {
            if (!_dirty)
            {
                return;
            }
            // Never rebuild the list out from under a text field the user is
            // typing into. RenderRows() recreates every row, and with it the
            // rename / new-group TextField and whatever was half-typed in
            // it. This is reachable without the user doing anything: a turn
            // completing anywhere in the panel calls
            // SessionMetaStoreAccess.RecordActiveScene, which saves and
            // raises Changed, which dirties this view -- so an agent
            // finishing in the background would silently wipe a rename in
            // progress. The dirty flag is deliberately LEFT SET so the
            // pending refresh lands as soon as the editor closes.
            if (ShouldDeferRefresh(_renamingSessionId, _newGroupForSessionId))
            {
                return;
            }
            _dirty = false;
            RenderRows();
        }

        /// <summary>
        /// True while an inline text editor owns the list, so a queued
        /// refresh must wait rather than recreating the field the user is
        /// typing into. Pure so HistoryViewLogicTests can pin it; see
        /// <see cref="RefreshIfDirty"/> for why this is reachable without
        /// any user action.
        /// </summary>
        internal static bool ShouldDeferRefresh(string renamingSessionId, string newGroupSessionId)
        {
            return !string.IsNullOrEmpty(renamingSessionId)
                || !string.IsNullOrEmpty(newGroupSessionId);
        }

        /// <summary>
        /// Whether a row's "Delete..." action is offered. False for another
        /// project's session and for the session currently running -- see
        /// the comment in <see cref="ShowRowMenu"/> for both reasons. Pure
        /// so the rule is pinned by a test rather than only by a menu that
        /// no test can open.
        /// </summary>
        internal static bool CanDeleteRow(bool foreign, bool isLive)
        {
            return !foreign && !isLive;
        }

        private void OnRefreshClicked()
        {
            // An explicit refresh also invalidates the cross-project scan:
            // it is the user's "I know something changed on disk" signal,
            // and a stale foreign list is exactly the kind of thing they
            // would be pressing this button to fix.
            _foreignScanned = false;
            _foreignProjects = null;
            RefreshIndexFromDisk();
        }

        private void RefreshIndexFromDisk()
        {
            if (_index == null)
            {
                _index = new SessionIndex();
            }
            _index.Refresh(AgentHub.ProjectRoot);
            ClearRowEditors();
            _dirty = false;
            RenderRows();
        }

        private void ClearRowEditors()
        {
            _pendingSwitchSessionId = string.Empty;
            _pendingDeleteSessionId = string.Empty;
            _renamingSessionId = string.Empty;
            _newGroupForSessionId = string.Empty;
            _deleteFailedSessionId = string.Empty;
        }

        // -- Row model assembly ------------------------------------------------------

        /// <summary>
        /// True when this render needs the transcript-derived text
        /// (ai-title / first user message / cwd) for EVERY session rather
        /// than only the page being displayed.
        ///
        /// Why this exists: those three are lazy properties on
        /// SessionIndexEntry, and touching one scans that session's file.
        /// Reading them while assembling the full row list would make a
        /// render cost one file scan per session on disk -- which quietly
        /// defeats the whole point of the 50-row page cap, since the cap
        /// only bounds VisualElement construction. So the common case
        /// (no search text, not grouping by project) assembles rows WITHOUT
        /// them, lets HistoryListModel filter/group/sort/page on the
        /// timestamp and sidecar metadata alone, and then hydrates just the
        /// rows that survived.
        ///
        /// The two cases that genuinely cannot be deferred:
        ///   * a non-empty search query has to match against the title and
        ///     preview of every candidate, by definition;
        ///   * project grouping keys off cwd, which is read from the file.
        /// Both are explicit user actions on a screen about finding things,
        /// so paying for a scan there is the honest trade.
        /// </summary>
        private bool NeedsTextForEveryRow()
        {
            return !string.IsNullOrWhiteSpace(_query)
                || _groupMode == HistoryGroupMode.Project;
        }

        /// <summary>
        /// Turns the on-disk index plus our own metadata into the pure
        /// <see cref="HistoryRow"/> shape HistoryListModel operates on, and
        /// records where each row's transcript lives so the click handlers
        /// can find it again. See <see cref="NeedsTextForEveryRow"/> for why
        /// the transcript-derived fields may come back empty here.
        /// </summary>
        private List<HistoryRow> BuildRows(bool withText)
        {
            _rowSources.Clear();
            var rows = new List<HistoryRow>();
            SessionMetaStore meta = SessionMetaStoreAccess.Store;

            IReadOnlyList<SessionIndexEntry> entries = _index != null
                ? _index.Entries : Array.Empty<SessionIndexEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                AddRow(rows, entries[i], meta, false, withText);
            }

            if (_groupMode == HistoryGroupMode.Project)
            {
                EnsureForeignScan();
                for (int p = 0; _foreignProjects != null && p < _foreignProjects.Count; p++)
                {
                    ProjectSessionScanner.ProjectSessions project = _foreignProjects[p];
                    for (int i = 0; project != null && i < project.Entries.Count; i++)
                    {
                        AddRow(rows, project.Entries[i], meta, true, withText);
                    }
                }
            }
            return rows;
        }

        /// <summary>
        /// Fills in the transcript-derived text for the rows that actually
        /// survived filtering and paging. No-op for a row already hydrated
        /// (the entry caches its scan anyway, so a second call is cheap).
        /// </summary>
        private void HydrateVisibleRows(List<HistoryGroup> groups)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                List<HistoryRow> rows = groups[g].Rows;
                for (int i = 0; rows != null && i < rows.Count; i++)
                {
                    HistoryRow row = rows[i];
                    RowSource source;
                    if (row == null || !_rowSources.TryGetValue(row.SessionId, out source)
                        || source == null || source.Entry == null)
                    {
                        continue;
                    }
                    row.AiTitle = source.Entry.AiTitle;
                    row.Preview = source.Entry.FirstUserTextPreview;
                    row.Cwd = source.Entry.Cwd;
                    row.ModelName = source.Entry.ModelName;
                    row.AgentBackend = source.Entry.AgentBackend;
                }
            }
        }

        private void AddRow(List<HistoryRow> rows, SessionIndexEntry entry,
            SessionMetaStore meta, bool foreign, bool withText)
        {
            if (entry == null || string.IsNullOrEmpty(entry.SessionId))
            {
                return;
            }
            // The current project's own directory also turns up in the
            // cross-project scan; first writer wins so the local (resumable)
            // row is never replaced by a browse-only duplicate.
            if (_rowSources.ContainsKey(entry.SessionId))
            {
                return;
            }
            SessionMeta m = meta.Get(entry.SessionId);
            rows.Add(new HistoryRow
            {
                SessionId = entry.SessionId,
                AiTitle = withText ? entry.AiTitle : string.Empty,
                Preview = withText ? entry.FirstUserTextPreview : string.Empty,
                LastModifiedUtc = entry.LastModifiedUtc,
                SizeBytes = entry.SizeBytes,
                AgentBackend = entry.IsPanelStore && withText ? entry.AgentBackend : (entry.IsPanelStore ? -1 : 0),
                Cwd = withText ? entry.Cwd : string.Empty,
                ModelName = withText ? entry.ModelName : string.Empty,
                Pinned = m.pinned,
                Archived = m.archived,
                TitleOverride = m.titleOverride,
                CustomGroupId = m.customGroupId,
                RecordedScenePath = m.recordedScenePath
            });
            _rowSources[entry.SessionId] = new RowSource { Entry = entry, Foreign = foreign };
        }

        private void EnsureForeignScan()
        {
            if (_foreignScanned)
            {
                return;
            }
            _foreignScanned = true;
            _foreignProjects = ProjectSessionScanner.ScanUnityProjects();
        }

        // -- Rendering ---------------------------------------------------------------

        private void RenderRows()
        {
            _lastKnownLiveSessionId = AgentHub.Session.sessionId ?? string.Empty;
            _listHost.Clear();

            bool withText = NeedsTextForEveryRow();
            List<HistoryRow> rows = BuildRows(withText);
            DateTime nowUtc = DateTime.UtcNow;
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(rows, _query, _groupMode,
                _showArchived, nowUtc, _visibleLimit, out hiddenCount);
            if (!withText)
            {
                // Only now, on the rows that survived, do we pay for the
                // per-session file scans -- see NeedsTextForEveryRow.
                HydrateVisibleRows(groups);
            }

            bool anyOnDisk = rows.Count > 0;
            bool anyShown = groups.Count > 0;
            // Two different empty states: "you have no sessions" and "your
            // search matched none of them" are different problems and a
            // single message for both reads as a bug in whichever case it
            // does not describe.
            _emptyLabel.text = anyOnDisk
                ? L10n.S.HistorySearchNoMatch
                : L10n.S.HistoryEmptyMessage;
            _emptyLabel.style.display = anyShown ? DisplayStyle.None : DisplayStyle.Flex;
            _listHost.style.display = anyShown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!anyShown)
            {
                return;
            }

            for (int g = 0; g < groups.Count; g++)
            {
                HistoryGroup group = groups[g];
                _listHost.Add(BuildGroupHeader(group));
                for (int i = 0; i < group.Rows.Count; i++)
                {
                    _listHost.Add(BuildRow(group.Rows[i], nowUtc));
                }
            }

            if (hiddenCount > 0)
            {
                var more = new Button(delegate
                {
                    _visibleLimit += VisibleRowsPage;
                    RenderRows();
                })
                {
                    text = L10n.F(L10n.S.HistoryShowMoreFmt, hiddenCount)
                };
                more.AddToClassList("uap-history-showmore");
                _listHost.Add(more);
            }
        }

        private VisualElement BuildGroupHeader(HistoryGroup group)
        {
            var header = new Label(ResolveGroupLabel(group));
            header.AddToClassList("uap-history-group-header");
            header.enableRichText = false;
            // Same rule as the row title/subtitle: the tooltip mirrors this
            // Label's own (already-sanitized, for the Scene/Project/Custom
            // cases) text rather than re-deriving it -- a narrow dock or a
            // long localization can truncate a group header exactly like a
            // row title can.
            header.tooltip = header.text;
            return header;
        }

        /// <summary>
        /// Maps a HistoryListModel group KEY (a stable sentinel, never
        /// display text) to the localized header. Keys that carry data --
        /// a scene path, a working directory, a custom group id -- are
        /// resolved to something readable here rather than in the model,
        /// which has no business knowing about localization or Unity paths.
        /// </summary>
        private string ResolveGroupLabel(HistoryGroup group)
        {
            string key = group != null ? group.Key : string.Empty;
            if (group != null && group.IsPinnedGroup)
            {
                return L10n.S.HistoryGroupPinned;
            }
            switch (_groupMode)
            {
                case HistoryGroupMode.Date:
                    if (key == HistoryListModel.DateToday) { return L10n.S.HistoryGroupToday; }
                    if (key == HistoryListModel.DateYesterday) { return L10n.S.HistoryGroupYesterday; }
                    if (key == HistoryListModel.DateLast7) { return L10n.S.HistoryGroupLast7; }
                    if (key == HistoryListModel.DateLast30) { return L10n.S.HistoryGroupLast30; }
                    return L10n.S.HistoryGroupOlder;
                case HistoryGroupMode.Scene:
                    return string.IsNullOrEmpty(key)
                        ? L10n.S.HistoryGroupNoScene
                        : IconLoader.SanitizeForDisplay(Path.GetFileNameWithoutExtension(key));
                case HistoryGroupMode.Project:
                    return string.IsNullOrEmpty(key)
                        ? L10n.S.HistoryGroupNoProject
                        : IconLoader.SanitizeForDisplay(LastPathSegment(key));
                default:
                    SessionGroup custom = SessionMetaStoreAccess.Store.FindGroup(key);
                    return custom != null && !string.IsNullOrEmpty(custom.name)
                        ? IconLoader.SanitizeForDisplay(custom.name)
                        : L10n.S.HistoryGroupUngrouped;
            }
        }

        private static string LastPathSegment(string path)
        {
            string trimmed = (path ?? string.Empty).TrimEnd('/', '\\');
            int slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            return slash >= 0 && slash + 1 < trimmed.Length
                ? trimmed.Substring(slash + 1)
                : trimmed;
        }

        // -- Row construction ---------------------------------------------------------

        private VisualElement BuildRow(HistoryRow data, DateTime nowUtc)
        {
            bool isLive = !string.IsNullOrEmpty(_lastKnownLiveSessionId)
                && string.Equals(data.SessionId, _lastKnownLiveSessionId, StringComparison.Ordinal);
            RowSource source;
            _rowSources.TryGetValue(data.SessionId, out source);
            bool foreign = source != null && source.Foreign;

            var row = new VisualElement();
            row.AddToClassList("uap-history-row");
            row.EnableInClassList("uap-history-row--active", isLive);
            row.EnableInClassList("uap-history-row--archived", data.Archived);

            var top = new VisualElement();
            top.AddToClassList("uap-history-row-top");
            row.Add(top);

            var main = new VisualElement();
            main.AddToClassList("uap-history-row-main");
            main.RegisterCallback<ClickEvent>(delegate { OnRowClicked(data, foreign); });
            // UXIA-1: the row body is a real keyboard target -- Tab reaches
            // it (focusable + tabIndex), :focus paints it, Enter/Space
            // activate it exactly like a click. KeyDown + PreventDefault is
            // the house pattern (PermissionCard.OnOtherFieldKeyDown);
            // deliberately NO NavigationSubmitEvent registration, so one
            // key press can never double-activate (which would confirm a
            // switch the first activation only opened the confirm bar
            // for). foreign rows stay safe: OnRowClicked early-returns.
            main.focusable = true;
            main.tabIndex = 0;
            main.RegisterCallback<KeyDownEvent>(delegate (KeyDownEvent evt)
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter
                    && evt.keyCode != KeyCode.Space)
                {
                    return;
                }
                evt.StopPropagation();
                evt.PreventDefault();
                OnRowClicked(data, foreign);
            });
            top.Add(main);

            var titleRow = new VisualElement();
            titleRow.AddToClassList("uap-history-row-title-row");
            // Resolved title and preview are both free-form text pulled off
            // disk (a CLI-generated ai-title, the user's own first message,
            // or their rename) -- sanitize at this render chokepoint the
            // same way every other one does (see IconLoader.SanitizeForDisplay).
            string resolved = HistoryListModel.ResolveTitle(data);
            var titleLabel = new Label(!string.IsNullOrEmpty(resolved)
                ? IconLoader.SanitizeForDisplay(resolved)
                : L10n.S.HistoryNoPreview);
            titleLabel.AddToClassList("uap-history-row-title");
            titleLabel.enableRichText = false;
            // Mirrors the Label's own (already-sanitized) text rather than
            // re-deriving it from `resolved` -- one chokepoint, so the
            // tooltip can never drift from what is actually on screen (see
            // PermissionCardOptionTooltipTests' TooltipAndLabel_StaySanitizedTheSame
            // for the same rule applied to PermissionCard). A long ai-title
            // or rename routinely overflows the row's fixed width with no
            // ellipsis affordance, so the full text belongs somewhere the
            // user can actually read it.
            titleLabel.tooltip = titleLabel.text;
            titleRow.Add(titleLabel);
            if (data.Pinned)
            {
                titleRow.Add(MakeBadge(L10n.S.HistoryGroupPinned, BadgePinnedClass));
            }
            if (data.Archived)
            {
                titleRow.Add(MakeBadge(L10n.S.HistoryActionArchive, BadgeArchivedClass));
            }
            if (isLive)
            {
                titleRow.Add(MakeBadge(L10n.S.HistoryCurrentBadge, BadgeCurrentClass));
            }
            main.Add(titleRow);

            // Sub-line: the first user message, but only when it is not
            // already what the title shows -- repeating the same string
            // twice in one row is noise, and it happens for every session
            // with no ai-title and no rename.
            string preview = data.Preview ?? string.Empty;
            if (!string.IsNullOrEmpty(preview)
                && !string.Equals(preview, resolved, StringComparison.Ordinal))
            {
                var sub = new Label(IconLoader.SanitizeForDisplay(preview));
                sub.AddToClassList("uap-history-row-subtitle");
                sub.enableRichText = false;
                // Same rule as titleLabel above: tooltip mirrors this
                // Label's own sanitized text, not the raw preview string.
                sub.tooltip = sub.text;
                main.Add(sub);
            }

            var meta = new Label(FormatRowMeta(
                FormatRelativeTime(nowUtc, data.LastModifiedUtc),
                FormatSize(data.SizeBytes),
                SettingsView.ShortenResolvedModel(data.ModelName),
                AgentLabelFor(data.AgentBackend)));
            meta.AddToClassList("uap-history-row-meta");
            meta.enableRichText = false;
            main.Add(meta);

            // Plain ASCII, not a horizontal-ellipsis glyph: this package
            // has already shipped one round of missing-glyph warnings from
            // decorative Unicode in the UI font (v0.13.1), and three dots
            // reads identically without depending on font coverage.
            var menuButton = new Button { text = "..." };
            // clicked is wired up AFTER construction (rather than through
            // the Button(Action) constructor used everywhere else in this
            // file) so the click handler can capture menuButton itself as
            // ShowRowMenu's anchor -- referencing a variable inside its
            // own initializer expression is legal C# (the delegate body
            // only runs later, once menuButton is fully assigned) but
            // reads as a trap, so it is written this way instead.
            menuButton.clicked += delegate { ShowRowMenu(data, foreign, menuButton); };
            menuButton.AddToClassList("uap-history-row-menu-btn");
            menuButton.tooltip = L10n.S.HistoryMenuTooltip;
            top.Add(menuButton);

            AppendRowEditors(row, data, foreign);
            return row;
        }

        /// <summary>
        /// 2026-09-05 UI redesign (D7): one modifier per badge meaning so
        /// colour carries the difference, not only the text. The base class
        /// keeps the user accent, which is Current's colour.
        /// </summary>
        internal const string BadgePinnedClass = "uap-history-row-badge--pinned";
        internal const string BadgeArchivedClass = "uap-history-row-badge--archived";
        internal const string BadgeCurrentClass = "uap-history-row-badge--current";

        private static Label MakeBadge(string text, string modifierClass)
        {
            var badge = new Label(text);
            badge.AddToClassList("uap-history-row-badge");
            badge.AddToClassList(modifierClass);
            badge.enableRichText = false;
            return badge;
        }

        /// <summary>
        /// Appends whichever inline editor this row currently owns. At most
        /// one row can own one at a time (the fields are single-valued), so
        /// opening a second collapses the first -- there is deliberately no
        /// way to have two destructive prompts on screen at once.
        /// </summary>
        private void AppendRowEditors(VisualElement row, HistoryRow data, bool foreign)
        {
            if (string.Equals(_pendingSwitchSessionId, data.SessionId, StringComparison.Ordinal))
            {
                row.Add(BuildSwitchConfirmBar(data));
            }
            if (string.Equals(_pendingDeleteSessionId, data.SessionId, StringComparison.Ordinal))
            {
                row.Add(BuildDeleteConfirmBar(data));
            }
            if (string.Equals(_renamingSessionId, data.SessionId, StringComparison.Ordinal))
            {
                row.Add(BuildRenameEditor(data));
            }
            if (string.Equals(_newGroupForSessionId, data.SessionId, StringComparison.Ordinal))
            {
                row.Add(BuildNewGroupEditor(data));
            }
            if (string.Equals(_deleteFailedSessionId, data.SessionId, StringComparison.Ordinal))
            {
                var failed = new Label(L10n.S.HistoryDeleteFailed);
                failed.AddToClassList("uap-history-row-error");
                failed.enableRichText = false;
                row.Add(failed);
            }
            if (foreign)
            {
                var note = new Label(L10n.S.HistoryForeignProjectNote);
                note.AddToClassList("uap-history-row-foreign-note");
                note.enableRichText = false;
                row.Add(note);
            }
        }

        private VisualElement BuildSwitchConfirmBar(HistoryRow data)
        {
            var bar = new VisualElement();
            bar.AddToClassList("uap-history-confirm");

            var text = new Label(L10n.S.HistorySwitchConfirmText);
            text.AddToClassList("uap-history-confirm-text");
            text.enableRichText = false;
            bar.Add(text);

            var buttons = new VisualElement();
            buttons.AddToClassList("uap-history-confirm-row");
            var cancel = new Button(OnCancelSwitchClicked) { text = L10n.S.HistoryCancelButton };
            cancel.AddToClassList("uap-history-confirm-btn");
            buttons.Add(cancel);
            var confirm = new Button(delegate { PerformSwitch(data.SessionId); })
            {
                text = L10n.S.HistorySwitchButton
            };
            confirm.AddToClassList("uap-history-confirm-btn");
            confirm.AddToClassList("uap-history-confirm-btn--primary");
            buttons.Add(confirm);
            bar.Add(buttons);
            return bar;
        }

        private VisualElement BuildDeleteConfirmBar(HistoryRow data)
        {
            var bar = new VisualElement();
            bar.AddToClassList("uap-history-confirm");

            // The confirm text names the destination folder because this
            // "delete" MOVES the CLI's transcript rather than unlinking it
            // (SessionMetaStoreAccess.MoveTranscriptToDeleted explains why).
            // Telling the user where it went is the difference between a
            // reversible action and one they have to take on faith.
            var text = new Label(L10n.F(L10n.S.HistoryDeleteConfirmTextFmt,
                SessionMetaStoreAccess.DeletedSessionsDirectory()));
            text.AddToClassList("uap-history-confirm-text");
            text.enableRichText = false;
            bar.Add(text);

            var buttons = new VisualElement();
            buttons.AddToClassList("uap-history-confirm-row");
            var cancel = new Button(delegate
            {
                _pendingDeleteSessionId = string.Empty;
                RenderRows();
            })
            { text = L10n.S.HistoryCancelButton };
            cancel.AddToClassList("uap-history-confirm-btn");
            buttons.Add(cancel);
            var confirm = new Button(delegate { PerformDelete(data.SessionId); })
            {
                text = L10n.S.HistoryDeleteButton
            };
            confirm.AddToClassList("uap-history-confirm-btn");
            confirm.AddToClassList("uap-history-confirm-btn--primary");
            buttons.Add(confirm);
            bar.Add(buttons);
            return bar;
        }

        private VisualElement BuildRenameEditor(HistoryRow data)
        {
            var bar = new VisualElement();
            bar.AddToClassList("uap-history-confirm");

            var field = new TextField { value = data.TitleOverride ?? string.Empty };
            field.AddToClassList("uap-history-rename-field");
            field.tooltip = L10n.S.HistoryRenamePlaceholder;
            bar.Add(field);

            var buttons = new VisualElement();
            buttons.AddToClassList("uap-history-confirm-row");
            // "Reset" clears the override rather than writing an empty
            // title: an empty override means "fall back to the CLI's
            // ai-title", never "this conversation has no name".
            var reset = new Button(delegate { ApplyRename(data.SessionId, string.Empty); })
            {
                text = L10n.S.HistoryRenameResetButton
            };
            reset.AddToClassList("uap-history-confirm-btn");
            buttons.Add(reset);
            var cancel = new Button(delegate
            {
                _renamingSessionId = string.Empty;
                RenderRows();
            })
            { text = L10n.S.HistoryCancelButton };
            cancel.AddToClassList("uap-history-confirm-btn");
            buttons.Add(cancel);
            var save = new Button(delegate { ApplyRename(data.SessionId, field.value); })
            {
                text = L10n.S.HistoryRenameSaveButton
            };
            save.AddToClassList("uap-history-confirm-btn");
            save.AddToClassList("uap-history-confirm-btn--primary");
            buttons.Add(save);
            bar.Add(buttons);
            return bar;
        }

        private VisualElement BuildNewGroupEditor(HistoryRow data)
        {
            var bar = new VisualElement();
            bar.AddToClassList("uap-history-confirm");

            var field = new TextField();
            field.AddToClassList("uap-history-rename-field");
            field.tooltip = L10n.S.HistoryNewGroupPlaceholder;
            bar.Add(field);

            var buttons = new VisualElement();
            buttons.AddToClassList("uap-history-confirm-row");
            var cancel = new Button(delegate
            {
                _newGroupForSessionId = string.Empty;
                RenderRows();
            })
            { text = L10n.S.HistoryCancelButton };
            cancel.AddToClassList("uap-history-confirm-btn");
            buttons.Add(cancel);
            var create = new Button(delegate { CreateGroupFor(data.SessionId, field.value); })
            {
                text = L10n.S.HistoryNewGroupCreate
            };
            create.AddToClassList("uap-history-confirm-btn");
            create.AddToClassList("uap-history-confirm-btn--primary");
            buttons.Add(create);
            bar.Add(buttons);
            return bar;
        }

        // -- Row actions ---------------------------------------------------------------

        /// <summary>
        /// Per-row action menu. A panel-styled popup (HistoryRowMenuPopover,
        /// ShowAsDropDown) rather than GenericMenu (IMGUI): the audit's
        /// complaint was visual (native menu chrome inside an otherwise
        /// fully custom-styled panel), not behavioral -- see docs/design-notes/
        /// 2026-08-14-history-menu-and-token-hygiene.md Part 2. Item order,
        /// checked state and the disabled-Delete tooltip are all decided by
        /// HistoryRowMenuModel (pure, tested); this method only computes
        /// the two inputs that decision needs and dispatches whichever id
        /// the popover reports back to the SAME handlers the old GenericMenu
        /// called directly.
        /// </summary>
        private void ShowRowMenu(HistoryRow data, bool foreign, VisualElement anchor)
        {
            string sessionId = data.SessionId;

            // Two rows must not offer Delete. Disabled rather than hidden in
            // both cases, so the menu keeps the same shape on every row and
            // the entry's absence is never mistaken for a rendering glitch.
            //
            //   * FOREIGN: deleting another project's transcript from this
            //     project's panel would move a file the user is not looking
            //     at into THIS project's UserSettings folder.
            //   * LIVE: the CLI process still has that transcript open and
            //     is appending to it. On Windows the move fails with a
            //     sharing violation and the row just reports failure, so
            //     nothing is destroyed today -- but that is the file system
            //     saving us, not a decision this view made. If the handle
            //     were ever opened with FILE_SHARE_DELETE the move would
            //     succeed and pull the transcript out from under a running
            //     session. Same spirit as the double-resume guard in
            //     OnRowClicked: do not offer an operation on the
            //     conversation that is currently running.
            bool isLive = IsSameSession(sessionId, AgentHub.Session.sessionId);
            bool canDelete = CanDeleteRow(foreign, isLive);

            // foreign and isLive can never both be true (a foreign session,
            // by definition, is never this project's live one), so passing
            // `foreign` alone tells HistoryRowMenuModel which of the two
            // disabled-Delete tooltips applies whenever canDelete is false.
            HistoryRowMenuPopover.Show(anchor, data.Pinned, data.Archived, canDelete, foreign,
                data.CustomGroupId, SessionMetaStoreAccess.Store.groups,
                delegate (string id) { OnRowMenuChoice(data, foreign, id); });
        }

        /// <summary>
        /// Maps a HistoryRowMenuModel item id back to the exact handler the
        /// old GenericMenu called for that same action -- behavior parity
        /// is the contract (see the design note). group-open/group-back
        /// never reach here: the popover handles that page navigation
        /// itself (HistoryRowMenuPopover.OnItemChosen).
        /// </summary>
        private void OnRowMenuChoice(HistoryRow data, bool foreign, string id)
        {
            string sessionId = data.SessionId;
            if (string.Equals(id, HistoryRowMenuModel.ActionOpen, StringComparison.Ordinal))
            {
                // UXIA-1: the menu's Open is the SAME restore the row click
                // performs (double-resume guard, switch confirm and all) --
                // never a second code path. The item is disabled for
                // foreign rows, and OnRowClicked early-returns for them
                // anyway.
                OnRowClicked(data, foreign);
                return;
            }
            if (string.Equals(id, HistoryRowMenuModel.ActionPin, StringComparison.Ordinal))
            {
                TogglePinned(sessionId);
                return;
            }
            if (string.Equals(id, HistoryRowMenuModel.ActionRename, StringComparison.Ordinal))
            {
                ClearRowEditors();
                _renamingSessionId = sessionId;
                RenderRows();
                return;
            }
            if (string.Equals(id, HistoryRowMenuModel.ActionArchive, StringComparison.Ordinal))
            {
                ToggleArchived(sessionId);
                return;
            }
            if (string.Equals(id, HistoryRowMenuModel.ActionGroupNone, StringComparison.Ordinal))
            {
                AssignGroup(sessionId, string.Empty);
                return;
            }
            if (string.Equals(id, HistoryRowMenuModel.ActionGroupNew, StringComparison.Ordinal))
            {
                ClearRowEditors();
                _newGroupForSessionId = sessionId;
                RenderRows();
                return;
            }
            if (string.Equals(id, HistoryRowMenuModel.ActionDelete, StringComparison.Ordinal))
            {
                ClearRowEditors();
                _pendingDeleteSessionId = sessionId;
                RenderRows();
                return;
            }
            if (id != null && id.StartsWith(HistoryRowMenuModel.GroupIdPrefix, StringComparison.Ordinal))
            {
                AssignGroup(sessionId, id.Substring(HistoryRowMenuModel.GroupIdPrefix.Length));
            }
        }

        /// <summary>
        /// Per-row action popup content: a themed EditorWindow shown via
        /// ShowAsDropDown, cloned from StatusBarView.UsagePopover's pattern
        /// (same "ShowAsDropDown host + UIToolkit content" decision -- see
        /// docs/design-notes/2026-08-14-history-menu-and-token-hygiene.md
        /// Part 2). ShowAsDropDown keeps every bit of native menu behavior
        /// that would otherwise have to be rebuilt by hand (click-outside
        /// auto-close, screen-edge clamping, borderless chrome); only the
        /// content is ours, built from HistoryRowMenuModel's pure item
        /// lists instead of GenericMenu.
        ///
        /// Two swappable pages match the model: the main action list, and
        /// the group-assignment list reached via "Move to group". A page
        /// swap rebuilds the content in place (SwapToPage) and resizes the
        /// window rather than closing and reopening one -- no reopen
        /// flicker, and the click-outside-to-close behavior never resets.
        /// </summary>
        // Row-menu popover height budget. Pure and internal so
        // HistoryRowMenuModelTests can pin the CAP: the 2026-08-14 review
        // caught the group page growing without bound (3 + one row per
        // user-created group, and group count has no cap) with no
        // scrollbar and no screen re-clamp on the page swap -- lower
        // groups could land off-screen and be unreachable. GenericMenu's
        // native submenu scrolled for free; the cap here plus the
        // ScrollView in BuildContent restore that property.
        //
        // Values are MEASURED, not estimated (user report 2026-08-14:
        // the first release guessed 24/9/8 and the window ran ~25%
        // taller than its rows): a rendered .uap-rowmenu-item is 19pt
        // (row pitch also 19 -- no margins), a separator is 1pt of box
        // plus 4pt of margins = 5pt, and the container adds 2+2pt
        // padding plus 1+1px border = 6pt of chrome.
        internal const float RowMenuRowHeight = 19f;
        internal const float RowMenuSepHeight = 5f;
        internal const float RowMenuChromePadding = 6f;
        internal const float RowMenuMaxHeight = 320f;

        internal static float ComputeRowMenuHeight(IList<HistoryRowMenuItem> items)
        {
            float height = RowMenuChromePadding;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    height += items[i].IsSeparator ? RowMenuSepHeight : RowMenuRowHeight;
                }
            }
            float floor = RowMenuRowHeight + RowMenuChromePadding;
            return Mathf.Clamp(height, floor, RowMenuMaxHeight);
        }

        private sealed class HistoryRowMenuPopover : EditorWindow
        {
            private const float Width = 200f;

            private Action<string> _onChoose;
            private string _currentGroupId = string.Empty;
            private List<SessionGroup> _groups = new List<SessionGroup>();
            private List<HistoryRowMenuItem> _mainItems = new List<HistoryRowMenuItem>();
            private Rect _anchorScreenRect;

            public static void Show(VisualElement anchor, bool pinned, bool archived,
                bool canDelete, bool deleteDisabledBecauseForeign, string currentGroupId,
                List<SessionGroup> groups, Action<string> onChoose)
            {
                var window = CreateInstance<HistoryRowMenuPopover>();
                window._onChoose = onChoose;
                window._currentGroupId = currentGroupId ?? string.Empty;
                window._groups = groups ?? new List<SessionGroup>();
                window._mainItems = HistoryRowMenuModel.BuildMainItems(
                    pinned, archived, canDelete, deleteDisabledBecauseForeign);
                window._anchorScreenRect = GUIUtility.GUIToScreenRect(anchor.worldBound);

                window.BuildContent(window._mainItems);
                window.ShowAsDropDown(window._anchorScreenRect, ComputeSize(window._mainItems));
            }

            private static Vector2 ComputeSize(List<HistoryRowMenuItem> items)
            {
                return new Vector2(Width, ComputeRowMenuHeight(items));
            }

            /// <summary>
            /// Page swap = CLOSE AND REOPEN at the original anchor, never a
            /// re-geometry of the live window. The first release resized in
            /// place (minSize/maxSize + position), and setting geometry on
            /// a shown ShowAsDropDown window relocates it to the SCREEN
            /// ORIGIN -- measured 2026-08-14 after the user report: position
            /// read back (482,282) fine, but the set landed the window at
            /// (0,0). Reopening also re-runs ShowAsDropDown's own
            /// screen-edge clamping for the new page's size, which the
            /// in-place path never did.
            /// </summary>
            private void SwapToPage(List<HistoryRowMenuItem> items)
            {
                var next = CreateInstance<HistoryRowMenuPopover>();
                next._onChoose = _onChoose;
                next._currentGroupId = _currentGroupId;
                next._groups = _groups;
                next._mainItems = _mainItems;
                next._anchorScreenRect = _anchorScreenRect;
                next.BuildContent(items);
                // Close BEFORE showing the replacement (state above is
                // already copied): the new dropdown must be the last
                // window to take focus, or the old one's teardown could
                // count as the focus loss that auto-closes it.
                Rect anchor = _anchorScreenRect;
                Close();
                next.ShowAsDropDown(anchor, ComputeSize(items));
            }

            private void BuildContent(List<HistoryRowMenuItem> items)
            {
                VisualElement root = rootVisualElement;
                root.Clear();
                AgentPanelWindow.ApplyThemeAndStyles(root);
                root.AddToClassList("uap-rowmenu");

                // ScrollView, not direct children: paired with the
                // RowMenuMaxHeight cap (see ComputeRowMenuHeight's comment)
                // so a group list taller than the capped window scrolls
                // instead of running off-screen unreachable.
                var scroll = new ScrollView(ScrollViewMode.Vertical);
                scroll.AddToClassList("uap-rowmenu-scroll");
                root.Add(scroll);

                for (int i = 0; i < items.Count; i++)
                {
                    HistoryRowMenuItem item = items[i];
                    if (item.IsSeparator)
                    {
                        var sep = new VisualElement();
                        sep.AddToClassList("uap-rowmenu-sep");
                        scroll.Add(sep);
                        continue;
                    }
                    scroll.Add(BuildRow(item));
                }
            }

            private VisualElement BuildRow(HistoryRowMenuItem item)
            {
                bool isBack = string.Equals(item.Id, HistoryRowMenuModel.ActionGroupBack,
                    StringComparison.Ordinal);
                bool isGroupOpen = string.Equals(item.Id, HistoryRowMenuModel.ActionGroupOpen,
                    StringComparison.Ordinal);

                var row = new Button();
                row.clicked += delegate { OnItemChosen(item); };
                row.AddToClassList("uap-rowmenu-item");
                row.EnableInClassList("uap-rowmenu-item--back", isBack);
                row.SetEnabled(item.Enabled);
                row.tooltip = item.Tooltip ?? string.Empty;

                // Fixed-width leading check slot (2026-08-03 standing rule:
                // a repeated leading control must form one column) -- an
                // Image when the built-in checkmark icon resolves, an empty
                // same-class placeholder otherwise, exactly like every
                // other IconLoader.CreateIcon call in this package.
                VisualElement check = item.Checked
                    ? IconLoader.CreateIcon("TestPassed", IconLoader.GlyphCheck,
                        "uap-rowmenu-check", "uap-rowmenu-check")
                    : new Label(string.Empty);
                if (!item.Checked)
                {
                    check.AddToClassList("uap-rowmenu-check");
                }
                row.Add(check);

                // Directional cue for the two navigation rows is added HERE,
                // never on HistoryRowMenuItem.Label -- the model keeps Label
                // as the exact L10n string (or raw group name) so
                // HistoryRowMenuModelTests can pin it, and this is plain
                // ASCII rather than a decorative glyph, the same call the
                // "..." row-menu button already makes for the same reason
                // (no unverified Unicode in the UI font).
                string text = item.Label ?? string.Empty;
                if (isGroupOpen)
                {
                    text += " >";
                }
                else if (isBack)
                {
                    text = "< " + text;
                }
                // Group names are raw user text; action labels are already
                // clean L10n catalog strings that pass through unchanged --
                // sanitizing both through the one chokepoint uniformly is
                // simpler than special-casing which items need it.
                var label = new Label(IconLoader.SanitizeForDisplay(text));
                label.AddToClassList("uap-rowmenu-label");
                label.enableRichText = false;
                row.Add(label);

                return row;
            }

            private void OnItemChosen(HistoryRowMenuItem item)
            {
                if (!item.Enabled)
                {
                    return;
                }
                if (string.Equals(item.Id, HistoryRowMenuModel.ActionGroupOpen, StringComparison.Ordinal))
                {
                    SwapToPage(HistoryRowMenuModel.BuildGroupItems(_currentGroupId, _groups));
                    return;
                }
                if (string.Equals(item.Id, HistoryRowMenuModel.ActionGroupBack, StringComparison.Ordinal))
                {
                    SwapToPage(_mainItems);
                    return;
                }
                // Captured to locals before Close(): closing the window is
                // what the design note calls out as happening AFTER the
                // callback fires, and reading from `this`/`item` after
                // Close() has run is worth avoiding on principle even
                // though neither actually depends on the window afterward.
                Action<string> callback = _onChoose;
                string id = item.Id;
                if (callback != null)
                {
                    callback(id);
                }
                Close();
            }
        }

        private void TogglePinned(string sessionId)
        {
            SessionMeta meta = SessionMetaStoreAccess.Store.Edit(sessionId);
            if (meta == null)
            {
                return;
            }
            meta.pinned = !meta.pinned;
            SessionMetaStoreAccess.Save();
            RenderRows();
        }

        private void ToggleArchived(string sessionId)
        {
            SessionMeta meta = SessionMetaStoreAccess.Store.Edit(sessionId);
            if (meta == null)
            {
                return;
            }
            meta.archived = !meta.archived;
            SessionMetaStoreAccess.Save();
            RenderRows();
        }

        private void AssignGroup(string sessionId, string groupId)
        {
            SessionMeta meta = SessionMetaStoreAccess.Store.Edit(sessionId);
            if (meta == null)
            {
                return;
            }
            meta.customGroupId = groupId ?? string.Empty;
            SessionMetaStoreAccess.Save();
            RenderRows();
        }

        private void ApplyRename(string sessionId, string title)
        {
            SessionMeta meta = SessionMetaStoreAccess.Store.Edit(sessionId);
            if (meta == null)
            {
                return;
            }
            meta.titleOverride = (title ?? string.Empty).Trim();
            SessionMetaStoreAccess.Save();
            _renamingSessionId = string.Empty;
            RenderRows();
        }

        private void CreateGroupFor(string sessionId, string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                // An unnamed group could never be told apart from any other
                // unnamed group in the menu; treat it as a cancel.
                _newGroupForSessionId = string.Empty;
                RenderRows();
                return;
            }
            SessionMetaStore store = SessionMetaStoreAccess.Store;
            var group = new SessionGroup
            {
                id = Guid.NewGuid().ToString("N"),
                name = trimmed,
                order = store.groups.Count
            };
            store.groups.Add(group);
            SessionMeta meta = store.Edit(sessionId);
            if (meta != null)
            {
                meta.customGroupId = group.id;
            }
            SessionMetaStoreAccess.Save();
            _newGroupForSessionId = string.Empty;
            // Creating a group is only ever done in order to use it, so put
            // the user where they can see the result immediately.
            _groupMode = HistoryGroupMode.Custom;
            SessionState.SetInt(GroupModeStateKey, (int)_groupMode);
            if (_groupPopup != null)
            {
                _groupPopup.SetValueWithoutNotify(L10n.S.HistoryGroupByCustom);
            }
            RenderRows();
        }

        private void PerformDelete(string sessionId)
        {
            RowSource source;
            if (!_rowSources.TryGetValue(sessionId, out source) || source == null)
            {
                return;
            }
            string moved = SessionMetaStoreAccess.MoveTranscriptToDeleted(
                sessionId, source.Entry.FilePath);
            _pendingDeleteSessionId = string.Empty;
            _deleteFailedSessionId = string.IsNullOrEmpty(moved) ? sessionId : string.Empty;
            if (!string.IsNullOrEmpty(moved))
            {
                // The row is gone from disk, so the in-memory index is now a
                // lie -- re-stat rather than just re-render.
                RefreshIndexFromDisk();
                return;
            }
            RenderRows();
        }

        // -- Selection / switch logic ---------------------------------------------------

        private void OnRowClicked(HistoryRow data, bool foreign)
        {
            if (foreign)
            {
                // Resuming another project's session against THIS project's
                // working directory would hand the CLI a transcript whose
                // file references, git branch and MCP servers all belong
                // somewhere else. The row already carries the explanation.
                return;
            }
            string liveId = AgentHub.Session.sessionId;
            if (IsSameSession(data.SessionId, liveId))
            {
                // Double-resume guard: this row IS the open conversation --
                // just bring it back on screen, never tear down and
                // respawn the client for a no-op switch.
                _pendingSwitchSessionId = string.Empty;
                AgentPanelWindow.ShowChat();
                return;
            }
            if (string.Equals(_pendingSwitchSessionId, data.SessionId, StringComparison.Ordinal))
            {
                // Confirm bar already open for this row and clicked again:
                // treat as confirming (matches "click to expand, click
                // again to act" -- there is no separate collapse target on
                // the row itself, Cancel is the explicit way out).
                PerformSwitch(data.SessionId);
                return;
            }
            if (ActiveConversationNeedsConfirm())
            {
                ClearRowEditors();
                _pendingSwitchSessionId = data.SessionId;
                RenderRows();
                return;
            }
            PerformSwitch(data.SessionId);
        }

        private void OnCancelSwitchClicked()
        {
            _pendingSwitchSessionId = string.Empty;
            RenderRows();
        }

        private static bool IsSameSession(string candidateId, string liveId)
        {
            return !string.IsNullOrEmpty(liveId)
                && string.Equals(candidateId, liveId, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when leaving the current session would discard something:
        /// it already has transcript content, or a turn is in flight. A
        /// brand new, still-empty session (just opened the panel, never
        /// sent anything) switches without friction.
        /// </summary>
        private static bool ActiveConversationNeedsConfirm()
        {
            ChatSession session = AgentHub.Session;
            if (session.messages.Count > 0)
            {
                return true;
            }
            AgentClient client = AgentHub.Client;
            return client != null && client.TurnActive;
        }

        private void PerformSwitch(string sessionId)
        {
            RowSource source;
            if (!_rowSources.TryGetValue(sessionId, out source) || source == null)
            {
                return;
            }
            SessionIndexEntry entry = source.Entry;
            _pendingSwitchSessionId = string.Empty;
            string titleHint = SessionMetaStoreAccess.Store.Get(sessionId).titleOverride;
            if (string.IsNullOrEmpty(titleHint))
            {
                titleHint = !string.IsNullOrEmpty(entry.AiTitle)
                    ? entry.AiTitle : entry.FirstUserTextPreview;
            }
            if (entry.IsPanelStore)
            {
                // An ACP agent's session: the panel's own store already
                // holds the ChatSession as it was displayed (design note
                // 2026-09-13-acp-feature-parity.md section 1). A file that
                // fails to load restores as an empty transcript, same
                // tolerance as the jsonl path below.
                Dictionary<string, ModelUsage> modelUsage;
                ChatSession stored = new SessionCacheFile(entry.FilePath, AgentHub.Log)
                    .Load(out modelUsage);
                if (stored == null)
                {
                    stored = new ChatSession { sessionId = sessionId, agentBackend = entry.AgentBackend };
                }
                if (string.IsNullOrEmpty(stored.sessionId))
                {
                    stored.sessionId = sessionId;
                }
                AgentHub.SwitchToStoredSession(stored, modelUsage, titleHint);
                AgentPanelWindow.ShowChat();
                return;
            }
            // TranscriptLoader.Load never throws (missing/unreadable/
            // malformed file all degrade to an empty list) -- the
            // missing-file race documented in the design note section 1.5
            // is handled by simply proceeding with whatever came back. The
            // usage it reports is what keeps the status bar's token total
            // and the usage popover from resetting to zero on restore.
            TranscriptUsage usage;
            List<ChatMessage> messages = TranscriptLoader.Load(entry.FilePath, out usage);
            AgentHub.SwitchToSession(sessionId, messages, titleHint, usage);
            AgentPanelWindow.ShowChat();
        }

        // -- Test seams (InternalsVisibleTo "Colloid.AgentPanel.Editor.Tests", see
        // Editor/AssemblyInfo.cs and ToolActivityCard/MessageListController's
        // *ForTests precedent) ----------------------------------------------------

        /// <summary>
        /// Builds one row exactly as RenderRows does, without needing
        /// AgentHub/SessionMetaStoreAccess wired up: BuildRow itself only
        /// reads this instance's own fields (all default-empty on a freshly
        /// constructed HistoryView, so the row renders as an ordinary local,
        /// non-live session with no editor open) plus static helpers. Exists
        /// so tooltip regressions (title/subtitle) can be pinned by
        /// inspecting the returned element's properties directly -- a
        /// detached element's .tooltip is set synchronously by the row
        /// builder, but dispatching a TooltipEvent against it resolves empty
        /// (see the 2026-08-05 tooltip measurements), so tests must read the
        /// property rather than simulate the hover.
        /// </summary>
        internal VisualElement BuildRowForTests(HistoryRow data, DateTime nowUtc)
        {
            return BuildRow(data, nowUtc);
        }

        /// <summary>
        /// Builds one group header exactly as RenderRows does, for the same
        /// reason as <see cref="BuildRowForTests"/>. <paramref name="groupMode"/>
        /// stands in for the RestoreViewPreferences/BuildFilterBar callback
        /// that normally sets <c>_groupMode</c>, so a test can cover the
        /// Scene/Project/Custom label-resolution branches without touching
        /// SessionState.
        /// </summary>
        internal VisualElement BuildGroupHeaderForTests(HistoryGroup group, HistoryGroupMode groupMode)
        {
            _groupMode = groupMode;
            return BuildGroupHeader(group);
        }

        // -- Pure helpers (EditMode tested via HistoryViewLogicTests) ------------------

        /// <summary>
        /// Coarse relative-time label ("just now" / "N minutes ago" / ...).
        /// nowUtc is a parameter (not DateTime.UtcNow read internally) so
        /// tests are exact and never flaky against wall-clock time.
        /// Negative deltas (clock skew, or a file mtime that is slightly in
        /// the future) clamp to zero rather than producing a nonsensical
        /// negative duration.
        /// </summary>
        /// <summary>
        /// UXIA-5: the row meta line, bullet-joined. File size alone was a
        /// weak proxy for "which conversation was this"; the model the
        /// session started on is appended when known (shortened via
        /// SettingsView.ShortenResolvedModel, the Settings dropdown's own
        /// rule) and omitted -- no dangling bullet -- when the transcript
        /// carries none. Cost is deliberately NOT shown: measured
        /// 2026-08-03, result.total_cost_usd never reaches the transcript
        /// on disk, so it cannot be restored here.
        /// </summary>
        internal static string FormatRowMeta(string relativeTime, string size, string modelShort)
        {
            return FormatRowMeta(relativeTime, size, modelShort, null);
        }

        /// <summary>
        /// As above, with the owning agent's name appended when given: an
        /// ACP agent's session sits in the same list as Claude Code's, so
        /// the row says whose it is (design note
        /// 2026-09-13-acp-feature-parity.md section 1).
        /// </summary>
        internal static string FormatRowMeta(string relativeTime, string size, string modelShort, string agentLabel)
        {
            string gap = "  " + IconLoader.GlyphBullet + "  ";
            string meta = relativeTime + gap + size;
            if (!string.IsNullOrEmpty(modelShort))
            {
                meta += gap + modelShort;
            }
            if (!string.IsNullOrEmpty(agentLabel))
            {
                meta += gap + agentLabel;
            }
            return meta;
        }

        /// <summary>
        /// Pure: the agent label for a row's backend -- empty for Claude
        /// Code (the historical default, its rows are unchanged) and for an
        /// unknown backend, the backend's display name otherwise.
        /// </summary>
        internal static string AgentLabelFor(int agentBackend)
        {
            if (agentBackend <= 0)
            {
                return string.Empty;
            }
            return AgentBackends.DisplayName((AgentBackend)agentBackend);
        }

        public static string FormatRelativeTime(DateTime nowUtc, DateTime whenUtc)
        {
            TimeSpan delta = nowUtc - whenUtc;
            if (delta < TimeSpan.Zero)
            {
                delta = TimeSpan.Zero;
            }
            if (delta.TotalSeconds < 45)
            {
                return L10n.S.HistoryRelativeJustNow;
            }
            if (delta.TotalMinutes < 1.5)
            {
                return L10n.S.HistoryRelativeOneMinuteAgo;
            }
            if (delta.TotalMinutes < 45)
            {
                return L10n.F(L10n.S.HistoryRelativeMinutesAgoFmt, RoundToInt(delta.TotalMinutes));
            }
            if (delta.TotalHours < 1.5)
            {
                return L10n.S.HistoryRelativeOneHourAgo;
            }
            if (delta.TotalHours < 22)
            {
                return L10n.F(L10n.S.HistoryRelativeHoursAgoFmt, RoundToInt(delta.TotalHours));
            }
            if (delta.TotalDays < 1.5)
            {
                return L10n.S.HistoryRelativeOneDayAgo;
            }
            if (delta.TotalDays < 26)
            {
                return L10n.F(L10n.S.HistoryRelativeDaysAgoFmt, RoundToInt(delta.TotalDays));
            }
            if (delta.TotalDays < 45)
            {
                return L10n.S.HistoryRelativeOneMonthAgo;
            }
            if (delta.TotalDays < 320)
            {
                return L10n.F(L10n.S.HistoryRelativeMonthsAgoFmt, RoundToInt(delta.TotalDays / 30.0));
            }
            if (delta.TotalDays < 545)
            {
                return L10n.S.HistoryRelativeOneYearAgo;
            }
            return L10n.F(L10n.S.HistoryRelativeYearsAgoFmt, RoundToInt(delta.TotalDays / 365.0));
        }

        /// <summary>Human file size ("512 B" / "3.4 KB" / "1.2 MB"), base 1024.</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }
            if (bytes < 1024)
            {
                return L10n.F(L10n.S.HistorySizeBytesFmt, bytes.ToString(CultureInfo.InvariantCulture));
            }
            double kb = bytes / 1024.0;
            if (kb < 1024.0)
            {
                return L10n.F(L10n.S.HistorySizeKbFmt, kb.ToString("0.#", CultureInfo.InvariantCulture));
            }
            double mb = kb / 1024.0;
            return L10n.F(L10n.S.HistorySizeMbFmt, mb.ToString("0.#", CultureInfo.InvariantCulture));
        }

        private static int RoundToInt(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }
}
