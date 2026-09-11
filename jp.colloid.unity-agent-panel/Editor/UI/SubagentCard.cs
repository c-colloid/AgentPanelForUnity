using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI.Markdown;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Card for one subagent spawn (a ToolCallRecord with .subagent
    /// populated), rendered by MessageBlockFactory IN PLACE OF
    /// ToolActivityCard (Phase 4 design note section 5). Header: status
    /// icon + a "&#187;" badge + subagent type + description + elapsed/
    /// duration. While running: a live progress line (task_progress's
    /// current activity + last tool name + running token count). On
    /// completion: task_notification.summary rendered through the normal
    /// Markdown pipeline (MODEL-authored/untrusted text -- this is the
    /// only render path used for it, matching every other model-authored
    /// text block in the panel). Expand (default collapsed) reveals the
    /// nested transcript; a history-restored card with no nested blocks
    /// yet lazily loads them from the CLI's own subagent sidechain file on
    /// first expand, degrading silently to summary-only when that file is
    /// missing (acceptance criteria 5).
    /// </summary>
    public sealed class SubagentCard : VisualElement
    {
        private readonly SubagentRecord _subagent;
        private VisualElement _details;
        private Label _chevron;
        private Label _progressLabel;
        private ScrollView _nestedScroll;
        private IVisualElementScheduledItem _liveUpdate;
        private bool _expanded;
        private bool _lazyLoadAttempted;

        // -- Progress-line staleness tracking (v0.17.0) --------------------
        // "Updated Ns ago" (design note 2026-08-03-subagent-ux-and-midturn-
        // input.md section 2.2 #2/#3) needs a timestamp of the last REAL
        // task_progress update, but SubagentRecord carries no such field --
        // adding one would touch Model/SubagentRecord.cs and the AgentHub.cs
        // write site, both outside this round's edit scope (SubagentCard.cs,
        // ToolActivityCard.cs, AgentPanel.uss, Tests/Editor/ only). Instead
        // this element watches its OWN previous readings of the four
        // progress fields and stamps the moment it notices any of them
        // change -- the closest available proxy given the constraint. See
        // UpdateProgressChangeTracking's doc comment for the one place this
        // approximation can read slightly fresher than reality (never
        // staler), and why it self-corrects.
        private string _lastObservedProgressLine = string.Empty;
        private string _lastObservedToolName = string.Empty;
        private int _lastObservedToolUses;
        private long _lastObservedTotalTokens;
        private long _lastProgressChangeUtcTicks;

        /// <summary>
        /// Expand/collapse memory keyed by SubagentRecord.toolUseId (design
        /// note section 7b, review-fix). MessageListController still rebuilds
        /// the row -- discarding this VisualElement and its _expanded field
        /// -- whenever a real structural change lands (status transition, a
        /// nested block appended, a drop-count bump); without this dictionary
        /// every such rebuild would silently re-collapse a card the user had
        /// deliberately opened. Editor-session lifetime only, not persisted
        /// (same limitation recorded on ToolActivityCard.ExpandedByToolUseId
        /// and NestedScrollOffsetByToolUseId below -- SessionState was
        /// considered for all three and rejected: none of ToolCallRecord,
        /// SubagentRecord or a scroll pixel offset is a SessionState-
        /// serializable primitive today, and adding that plumbing would
        /// mean editing the model/session-cache files this round is
        /// deliberately scoped away from. A domain reload already discards
        /// the WHOLE live transcript back to whatever SessionCacheFile last
        /// wrote, at which point per-card UI expand/scroll state has
        /// nothing meaningful left to restore into anyway).
        /// </summary>
        private static readonly Dictionary<string, bool> ExpandedByToolUseId = new Dictionary<string, bool>();

        /// <summary>
        /// Nested-transcript ScrollView offset, keyed exactly like <see
        /// cref="ExpandedByToolUseId"/> above (design note section 1.1: "the
        /// mechanism ... already exists ... apply the SAME pattern"). Unlike
        /// the outer message list's ScrollView (MessageListController._scroll,
        /// which is created ONCE and reused for the panel's lifetime),
        /// PopulateDetails below builds a brand new ScrollView from scratch
        /// on every call -- both across a full SubagentCard rebuild AND,
        /// within the SAME instance, every time TryLazyLoadNestedBlocks
        /// re-renders after a lazy load -- so a freshly built one always
        /// starts at offset 0 with nothing remembering it was ever scrolled.
        /// </summary>
        private static readonly Dictionary<string, float> NestedScrollOffsetByToolUseId =
            new Dictionary<string, float>();

        public SubagentCard(ToolCallRecord record)
        {
            AddToClassList("uap-subcard");
            _subagent = record != null ? record.subagent : null;
            if (_subagent == null)
            {
                return;
            }

            bool failed = _subagent.status == "failed" || _subagent.status == "stopped";
            if (failed)
            {
                AddToClassList("uap-subcard--failed");
            }

            var header = new VisualElement();
            header.AddToClassList("uap-subcard-header");
            header.Add(CreateStatusIcon(_subagent));

            var badge = new Label(IconLoader.GlyphTask);
            badge.enableRichText = false;
            badge.AddToClassList("uap-subcard-badge");
            header.Add(badge);

            // .uap-subcard-type has the IDENTICAL "unshrinkable label starves
            // the row" shape ToolActivityCard's .uap-toolcard-name had (see
            // ToolCardDescriber.ShortenToolDisplayName's doc comment for the
            // live measurement); the USS side now caps/ellipsizes this label
            // too. subagentType is normally a short identifier
            // ("general-purpose", "Explore") or plugin-qualified
            // ("comfy:comfy-debugger") so ShortenToolDisplayName is a no-op
            // for it today, but running it through the same transform costs
            // nothing and means an "mcp__..."-shaped subagent type would
            // never need a special case here. The tooltip keeps the full,
            // un-truncated text reachable on hover regardless of whether
            // shortening or the USS ellipsis ever actually fires.
            string rawTypeText = string.IsNullOrEmpty(_subagent.subagentType)
                ? L10n.S.SubagentDefaultType : _subagent.subagentType;
            string typeText = ToolCardDescriber.ShortenToolDisplayName(rawTypeText);
            var type = new Label(IconLoader.SanitizeForDisplay(typeText));
            type.enableRichText = false;
            type.AddToClassList("uap-subcard-type");
            type.tooltip = IconLoader.SanitizeForDisplay(rawTypeText);
            header.Add(type);

            string descriptionText = !string.IsNullOrEmpty(_subagent.description)
                ? _subagent.description
                : (string.IsNullOrEmpty(_subagent.status) ? L10n.S.SubagentDefaultDescription : _subagent.status);
            var description = new Label(IconLoader.SanitizeForDisplay(descriptionText));
            description.enableRichText = false;
            description.AddToClassList("uap-subcard-desc");
            header.Add(description);

            header.Add(CreateTimeLabel(record));

            // A card is expandable when it has something to show NOW, or
            // could load something on demand: TryLazyLoadNestedBlocks pulls
            // the whole nested transcript from
            // subagents/agent-<taskId>.jsonl the first time a card is
            // expanded, and that only needs a toolUseId. Judging solely on
            // already-in-memory content (as this did before 2026-08-02)
            // renders a permanently inert card -- no chevron AND no click
            // handler -- for every restored subagent whose sidechain is
            // sitting on disk unread; see design note
            // 2026-08-02-subagent-card-not-expandable.md section 3.
            // PopulateDetails renders an explicit "no details" line when a
            // lazily-expanded card really does turn out empty, so the
            // affordance does not lie in the other direction either.
            bool hasDetails = _subagent.blocks.Count > 0
                || !string.IsNullOrEmpty(_subagent.summaryMarkdown)
                || !string.IsNullOrEmpty(_subagent.toolUseId)
                || _subagent.status == "running";
            _chevron = new Label(IconLoader.GlyphChevronRight);
            _chevron.enableRichText = false;
            _chevron.AddToClassList("uap-subcard-chevron");
            _chevron.style.visibility = hasDetails ? Visibility.Visible : Visibility.Hidden;
            header.Add(_chevron);
            Add(header);

            if (_subagent.status == "running")
            {
                // The row is created the moment the Agent tool_use is first
                // seen, well before the first task_progress event -- so
                // progressLine/lastToolName/totalTokens are usually still
                // empty here. Rather than skip the row (it would then never
                // appear, since none of those fields are part of the
                // rebuild signature anymore -- see ComputeSignature), always
                // create it while running and drive its text/visibility from
                // a scheduler that reads the live SubagentRecord in place.
                _progressLabel = new Label(string.Empty);
                _progressLabel.enableRichText = false;
                _progressLabel.AddToClassList("uap-subcard-progress");
                Add(_progressLabel);
                RefreshProgressLabel();
                StartLiveUpdate();
            }

            if (hasDetails)
            {
                _details = new VisualElement();
                _details.AddToClassList("uap-subcard-details");
                _details.style.display = DisplayStyle.None;
                PopulateDetails();
                Add(_details);
                header.RegisterCallback<ClickEvent>(delegate { ToggleExpanded(); });

                bool restoreExpanded = false;
                bool hasMemory = !string.IsNullOrEmpty(_subagent.toolUseId)
                    && ExpandedByToolUseId.TryGetValue(_subagent.toolUseId, out restoreExpanded);
                if (hasMemory)
                {
                    if (restoreExpanded)
                    {
                        // Reuses the exact ToggleExpanded/SetExpanded path so
                        // lazy-load of history-restored nested blocks still
                        // runs (TryLazyLoadNestedBlocks), instead of
                        // duplicating it.
                        SetExpanded(true);
                    }
                }
                else if (PanelStateStore.instance.Settings.subagentDefaultExpanded)
                {
                    // No remembered per-card state yet (design note
                    // 2026-08-01 #2): fall back to the Settings default.
                    // Memory (above) always takes priority once a card has
                    // been manually toggled.
                    SetExpanded(true);
                }
            }
        }

        /// <summary>
        /// Live-refreshes the progress row (design note section 7b) every
        /// 500ms, matching the existing CreateTimeLabel/CreateSpinner
        /// scheduling idiom in this file and ToolActivityCard. Stops itself
        /// once the record is no longer "running" -- though in practice a
        /// status change is a structural change (ComputeSignature) that
        /// rebuilds the row anyway, replacing this instance well before the
        /// next 500ms tick. Paused on detach so a discarded row (rebuilt
        /// elsewhere, or pruned) never keeps ticking against a live
        /// SubagentRecord forever.
        /// </summary>
        private void StartLiveUpdate()
        {
            SubagentRecord liveSubagent = _subagent;
            _liveUpdate = schedule.Execute(() =>
            {
                if (liveSubagent.status != "running")
                {
                    _liveUpdate.Pause();
                    return;
                }
                RefreshProgressLabel();
            }).Every(500);

            RegisterCallback<DetachFromPanelEvent>(delegate
            {
                if (_liveUpdate != null)
                {
                    _liveUpdate.Pause();
                }
            });
        }

        /// <summary>Rebuilds the progress label's text from the live record.
        /// A RUNNING card is never hidden any more (design note section 2.2
        /// #1 -- BuildProgressText falls back to "Working..." instead); a
        /// FINISHED card with nothing to show still hides the row, matching
        /// the pre-fix behavior for that case.</summary>
        private void RefreshProgressLabel()
        {
            UpdateProgressChangeTracking();
            long secondsSinceUpdate = _lastProgressChangeUtcTicks > 0
                ? (DateTime.UtcNow.Ticks - _lastProgressChangeUtcTicks) / TimeSpan.TicksPerSecond
                : 0;
            string text = IconLoader.SanitizeForDisplay(BuildProgressText(
                _subagent.progressLine, _subagent.lastToolName, _subagent.toolUses,
                _subagent.totalTokens, secondsSinceUpdate, _subagent.status == "running"));
            _progressLabel.text = text;
            _progressLabel.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// Stamps <see cref="_lastProgressChangeUtcTicks"/> the moment any of
        /// the four progress fields is observed to differ from the previous
        /// reading -- see the field group's doc comment above for why this
        /// UI-side observation is the proxy in place of a real timestamp on
        /// SubagentRecord. The one place this can read fresher than the true
        /// last update: MessageListController rebuilds this whole row
        /// whenever ANY block in the same chat message changes signature
        /// (e.g. a sibling tool call finishing), not just this subagent's
        /// own fields, so a brand new SubagentCard's "last observed" values
        /// start at empty/zero and the very first refresh always reads as
        /// "just changed" even if the true last task_progress was several
        /// seconds earlier. It never reads STALER than reality, and the next
        /// genuine update (or simply the next 500ms tick once none arrives)
        /// makes the displayed number accurate again either way.
        /// </summary>
        private void UpdateProgressChangeTracking()
        {
            bool changed =
                !string.Equals(_subagent.progressLine, _lastObservedProgressLine, StringComparison.Ordinal)
                || !string.Equals(_subagent.lastToolName, _lastObservedToolName, StringComparison.Ordinal)
                || _subagent.toolUses != _lastObservedToolUses
                || _subagent.totalTokens != _lastObservedTotalTokens;
            if (!changed)
            {
                return;
            }
            _lastObservedProgressLine = _subagent.progressLine;
            _lastObservedToolName = _subagent.lastToolName;
            _lastObservedToolUses = _subagent.toolUses;
            _lastObservedTotalTokens = _subagent.totalTokens;
            _lastProgressChangeUtcTicks = DateTime.UtcNow.Ticks;
        }

        private void ToggleExpanded()
        {
            if (_details == null)
            {
                return;
            }
            SetExpanded(!_expanded);
        }

        /// <summary>
        /// Applies expand/collapse state (used both by the user's click via
        /// ToggleExpanded and by construction-time restore of a previously
        /// expanded card) and records it in <see cref="ExpandedByToolUseId"/>
        /// keyed by toolUseId so the next rebuilt card for the same subagent
        /// starts in the same state. Records are skipped for a null/empty
        /// toolUseId (nothing to key on).
        /// </summary>
        private void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            if (_expanded)
            {
                TryLazyLoadNestedBlocks();
            }
            _details.style.display = _expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _chevron.text = _expanded ? IconLoader.GlyphChevronDown : IconLoader.GlyphChevronRight;
            if (!string.IsNullOrEmpty(_subagent.toolUseId))
            {
                ExpandedByToolUseId[_subagent.toolUseId] = _expanded;
            }
        }

        /// <summary>Test seam (InternalsVisibleTo "Colloid.AgentPanel.Editor.Tests",
        /// see Editor/AssemblyInfo.cs): drives the same click path as the
        /// header's ClickEvent handler without needing a panel to dispatch
        /// the event.</summary>
        internal void ToggleExpandedForTests()
        {
            ToggleExpanded();
        }

        /// <summary>Test seam: current expand state, including a state
        /// restored from <see cref="ExpandedByToolUseId"/> at construction.</summary>
        internal bool IsExpandedForTests
        {
            get { return _expanded; }
        }

        /// <summary>Test seam: clears the expand-state and nested-scroll-
        /// offset memory so tests don't leak state into each other via
        /// toolUseId collisions across either static dictionary's editor-
        /// session lifetime.</summary>
        internal static void ResetExpandedStateForTests()
        {
            ExpandedByToolUseId.Clear();
            NestedScrollOffsetByToolUseId.Clear();
        }

        /// <summary>Test seam: records a nested-scroll offset exactly as a
        /// real user scroll would (Scroller.valueChanged fires
        /// OnNestedScrollValueChanged) -- without needing a live panel and
        /// an actual Yoga layout pass to move a ScrollView for real. See
        /// RestoreNestedScrollOffset's doc comment for why the OTHER half
        /// (applying a restored offset to a freshly built ScrollView) is
        /// deliberately left to live verification instead of this suite,
        /// same testing boundary MessageListControllerScrollTests already
        /// draws around the outer list's equivalent restore path.</summary>
        internal void RecordNestedScrollOffsetForTests(float value)
        {
            OnNestedScrollValueChanged(value);
        }

        /// <summary>Test seam: the nested-scroll offset currently remembered
        /// for a toolUseId, or false if nothing has been recorded yet.</summary>
        internal static bool TryGetNestedScrollOffsetForTests(string toolUseId, out float offset)
        {
            return NestedScrollOffsetByToolUseId.TryGetValue(toolUseId, out offset);
        }

        /// <summary>
        /// History-restored subagents start with empty nested blocks (design
        /// note section 6: the sidechain jsonl is loaded lazily, not up
        /// front). Attempted once per card; a live subagent that legitimately
        /// has zero blocks so far just finds nothing to load (the sidechain
        /// file the CLI writes matches what's already shown, or does not
        /// exist yet) and moves on silently.
        /// </summary>
        private void TryLazyLoadNestedBlocks()
        {
            if (_lazyLoadAttempted || _subagent.blocks.Count > 0)
            {
                return;
            }
            _lazyLoadAttempted = true;
            string sessionId = AgentHub.Session != null ? AgentHub.Session.sessionId : null;
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(_subagent.toolUseId))
            {
                return;
            }
            string mainPath = System.IO.Path.Combine(
                SessionIndex.DefaultProjectsRoot(),
                SessionIndex.TransformCwdToProjectDirName(AgentHub.ProjectRoot),
                sessionId + ".jsonl");
            string sidechainPath = TranscriptLoader.FindSubagentJsonlPath(mainPath, _subagent.toolUseId);
            if (string.IsNullOrEmpty(sidechainPath))
            {
                return;
            }
            int droppedCount;
            List<ChatMessageBlock> loaded = TranscriptLoader.LoadSubagentBlocks(sidechainPath, out droppedCount);
            if (loaded.Count == 0)
            {
                return;
            }
            _subagent.blocks.AddRange(loaded);
            // LoadSubagentBlocks truncates internally (it has no
            // SubagentRecord to call AddBlock on while building the list),
            // so the caller must feed the drop count in itself -- see that
            // method's doc comment.
            _subagent.droppedBlockCount += droppedCount;
            PopulateDetails();
        }

        /// <summary>(Re)builds the expand-details content from current subagent state.</summary>
        private void PopulateDetails()
        {
            _details.Clear();

            if (!string.IsNullOrEmpty(_subagent.summaryMarkdown))
            {
                // Untrusted model output (task_notification.summary): MUST
                // render through the normal Markdown/escaping pipeline,
                // never a plain rich-text Label.
                VisualElement summary = MarkdownRenderer.Render(_subagent.summaryMarkdown);
                summary.AddToClassList("uap-subcard-summary");
                _details.Add(summary);
            }

            if (_subagent.droppedBlockCount > 0)
            {
                var dropNote = new Label(L10n.F(L10n.S.SubagentDropNoteFmt, _subagent.droppedBlockCount));
                dropNote.enableRichText = false;
                dropNote.AddToClassList("uap-subcard-drop-note");
                _details.Add(dropNote);
            }

            var nestedScroll = new ScrollView(ScrollViewMode.Vertical);
            nestedScroll.AddToClassList("uap-subcard-nested-scroll");
            _nestedScroll = nestedScroll;
            // See NestedScrollOffsetByToolUseId's doc comment: this method
            // builds a brand new ScrollView every time it runs, so every
            // call needs to both restore whatever offset is already on
            // record AND keep listening for the next real scroll.
            nestedScroll.verticalScroller.valueChanged += OnNestedScrollValueChanged;
            RestoreNestedScrollOffset(nestedScroll);
            var nestedHost = new VisualElement();
            nestedHost.AddToClassList("uap-subcard-nested");
            for (int i = 0; i < _subagent.blocks.Count; i++)
            {
                // Subagent content never streams (R02c) -- every nested
                // block is already finalized, so no StreamingLabelPump.
                // Keyed by the subagent's own toolUseId so a nested
                // thinking Foldout survives this card's rebuilds too.
                VisualElement element = MessageBlockFactory.CreateBlockElement(
                    _subagent.blocks[i], null,
                    ExpandStateMemory.BlockKey(_subagent.toolUseId, i));
                if (element != null)
                {
                    nestedHost.Add(element);
                }
            }
            nestedScroll.Add(nestedHost);
            _details.Add(nestedScroll);

            if (_details.childCount == 1 && _subagent.blocks.Count == 0)
            {
                // Only the (empty) nested scroll got added: a card that was
                // offered as expandable because a sidechain MIGHT exist, but
                // the lazy load found nothing (file missing/pruned, or the
                // subagent genuinely recorded nothing). Say so instead of
                // opening a blank box -- design note 2026-08-02-subagent-
                // card-not-expandable.md section 4.
                var empty = new Label(L10n.S.SubagentNoDetails);
                empty.enableRichText = false;
                empty.AddToClassList("uap-subcard-drop-note");
                _details.Insert(0, empty);
            }
        }

        /// <summary>Records a real user scroll of the nested transcript
        /// (Scroller.valueChanged) so the next ScrollView PopulateDetails
        /// builds for this same subagent can be re-positioned instead of
        /// silently restarting at the top.</summary>
        private void OnNestedScrollValueChanged(float value)
        {
            if (!string.IsNullOrEmpty(_subagent.toolUseId))
            {
                NestedScrollOffsetByToolUseId[_subagent.toolUseId] = value;
            }
        }

        /// <summary>
        /// Re-applies a remembered nested-scroll offset to the ScrollView
        /// PopulateDetails just built. A brand new ScrollView reports
        /// verticalScroller.highValue == 0 until its content has actually
        /// been through a layout pass -- MessageListController.
        /// RestoreScrollOffset's own doc comment records the identical fact
        /// for the outer list's restore-on-session-load path -- so setting
        /// .value immediately here would silently clamp to 0, and Scroller
        /// does not remember an out-of-range assignment for later: once
        /// real layout runs and widens the valid range, whatever survived
        /// the clamp (0) is simply what stays. Deferring to the first
        /// GeometryChangedEvent where highValue is non-zero mirrors that
        /// exact fix for THIS ScrollView, which -- unlike the outer one --
        /// is discarded and rebuilt from scratch on every PopulateDetails
        /// call rather than reused in place, so this runs every time, not
        /// just once at session load.
        /// </summary>
        private void RestoreNestedScrollOffset(ScrollView scroll)
        {
            if (string.IsNullOrEmpty(_subagent.toolUseId))
            {
                return;
            }
            float saved;
            if (!NestedScrollOffsetByToolUseId.TryGetValue(_subagent.toolUseId, out saved) || saved <= 0f)
            {
                return;
            }
            EventCallback<GeometryChangedEvent> onGeometry = null;
            onGeometry = delegate
            {
                float highValue = scroll.verticalScroller.highValue;
                if (highValue <= 0f)
                {
                    // Layout not settled yet; wait for the next geometry pass
                    // (same "not yet, try again" shape as MessageListController.
                    // OnRestoreGeometry).
                    return;
                }
                scroll.verticalScroller.value = ClampNestedScrollOffset(saved, highValue);
                scroll.contentContainer.UnregisterCallback(onGeometry);
            };
            scroll.contentContainer.RegisterCallback(onGeometry);
        }

        /// <summary>
        /// Pure clamp math behind <see cref="RestoreNestedScrollOffset"/>,
        /// factored out exactly like MessageListController.
        /// ComputeRestoredScrollValue so it is unit-testable without a live
        /// ScrollView/panel. EditMode tests cannot drive a real Yoga layout
        /// pass, so -- matching the testing boundary
        /// MessageListControllerScrollTests already draws around the outer
        /// scroll's equivalent restore path -- the GeometryChangedEvent
        /// wiring above is exercised live rather than in this suite; this
        /// is the part of the logic that actually can be pinned.
        /// </summary>
        internal static float ClampNestedScrollOffset(float savedOffset, float highValue)
        {
            float clamped = Math.Min(savedOffset, highValue);
            return clamped < 0f ? 0f : clamped;
        }

        /// <summary>
        /// Pure text composition for the running-subagent progress row
        /// (design note 2026-08-03-subagent-ux-and-midturn-input.md section
        /// 2, "the actual causes of 'I cannot tell if it is working'").
        /// Deliberately a plain static function of its inputs -- no
        /// SubagentRecord, no UI -- so every field's presence/absence and
        /// the blank-window fallback can be pinned without constructing a
        /// card. Composition order mirrors the measured task_progress log
        /// shape (description, tool, step count, token count, staleness):
        /// "Writing ...scratchpad/s1.txt (Write) -- 7 steps -- 36176 tokens
        /// -- updated 2s ago".
        /// </summary>
        /// <param name="progressLine">subagent.progressLine, raw (this method
        /// shortens path-shaped text itself via <see cref="ShortenPathDescription"/>).</param>
        /// <param name="lastToolName">subagent.lastToolName, raw wire name
        /// (shortened here via ToolCardDescriber, same as the header).</param>
        /// <param name="toolUses">subagent.toolUses -- captured, cached and
        /// restored today but rendered nowhere before this fix (design note
        /// section 2.2 #3).</param>
        /// <param name="totalTokens">subagent.totalTokens.</param>
        /// <param name="secondsSinceUpdate">Whole seconds since this
        /// element last observed progressLine/lastToolName/toolUses/
        /// totalTokens change (see UpdateProgressChangeTracking). Only
        /// rendered when a progress line actually exists -- there is no
        /// "last update" to report staleness against before the first one
        /// arrives.</param>
        /// <param name="running">subagent.status == "running". A NON-running
        /// card with nothing to show returns empty (RefreshProgressLabel
        /// hides the row) instead of "Working..." -- that fallback exists
        /// only to cover the running card's blank startup window, not to
        /// invent activity for a card that is already done.</param>
        internal static string BuildProgressText(string progressLine, string lastToolName, int toolUses,
            long totalTokens, long secondsSinceUpdate, bool running)
        {
            string text = ShortenPathDescription(progressLine);
            bool hasProgressLine = !string.IsNullOrEmpty(text);
            if (!hasProgressLine)
            {
                if (!running)
                {
                    return string.Empty;
                }
                // The row is created the moment the Agent tool_use is first
                // seen, well before the first task_progress event -- measured
                // at 5.7s on a real 7-step run (design note section 2.2 #1).
                // Before this fallback, BuildProgressText returning empty
                // set the whole row to display:None for that entire window,
                // which is exactly the blank spinner-only card the user
                // reported as indistinguishable from a stall.
                text = L10n.S.SubagentProgressWorking;
            }

            if (!string.IsNullOrEmpty(lastToolName))
            {
                // Same informational-surface treatment as the header's
                // .uap-subcard-type (see ToolCardDescriber.ShortenToolDisplayName's
                // doc comment): a subagent that called one of this
                // package's own UapOps tools would otherwise put raw wire
                // plumbing like "mcp__unity-ops__uap_query_hierarchy"
                // straight into this live progress line, right under a
                // header that already shows the clean, shortened form --
                // self-inconsistent within the same card.
                text = L10n.F(L10n.S.SubagentProgressToolFmt, text,
                    ToolCardDescriber.ShortenToolDisplayName(lastToolName));
            }
            if (toolUses > 0)
            {
                text = L10n.F(L10n.S.SubagentProgressStepsFmt, text, toolUses);
            }
            if (totalTokens > 0)
            {
                text = L10n.F(L10n.S.SubagentProgressTokensFmt, text, totalTokens);
            }
            if (running && hasProgressLine && secondsSinceUpdate > 0)
            {
                text = L10n.F(L10n.S.SubagentProgressUpdatedAgoFmt, text, secondsSinceUpdate);
            }
            return text;
        }

        // Recognizes exactly one path separator style per call (backslash OR
        // forward slash) -- see ShortenPathDescription's doc comment.
        private static readonly char[] PathSeparators = { '\\', '/' };

        /// <summary>
        /// Shortens a path-shaped LAST WORD of a task_progress description
        /// down to "parent-directory/filename", prefixed with an ASCII
        /// "..." marker, so the label can declare white-space: nowrap (USS)
        /// without either blowing out the row or hiding the one detail
        /// (which file) that makes the line meaningful.
        ///
        /// Why: design note section 2.2 #4 -- every measured task_progress
        /// description that names a file is shaped "&lt;verb&gt; &lt;absolute
        /// path&gt;" (e.g. "Writing ~\AppData\Local\Temp\claude\...\
        /// scratchpad\s1.txt"). A path has no interior spaces, so the OLD
        /// white-space: normal styling could never wrap it onto a second
        /// line the way it wraps ordinary prose -- it just overflowed the
        /// row horizontally instead, the same "silent layout failure, not a
        /// build error" class of defect this package has hit before (see
        /// docs/design-notes/2026-08-03-list-row-control-alignment.md).
        ///
        /// Rules:
        /// - Only the LAST whitespace-delimited word is ever treated as a
        ///   candidate path (matches every measured shape; a leading verb
        ///   like "Writing " is preserved untouched ahead of it).
        /// - A last word with no path separator at all is returned
        ///   completely unchanged -- this is a targeted fix for the one
        ///   shape that blows out the row, not a general-purpose truncator,
        ///   and the required behavior for ordinary prose ("Running List
        ///   contents..", "Reading project settings") is to pass through.
        /// - A path only ONE segment deep after removing the filename (no
        ///   further separator ahead of the parent directory, e.g. "~\
        ///   file.txt" or "dir/file.txt") is also returned unchanged:
        ///   prefixing "..." to something already this short would make the
        ///   string LONGER, not shorter, which would defeat the point.
        /// - A trailing separator with nothing after it ("...\dir\") is not
        ///   a file-shaped path and is returned unchanged rather than
        ///   guessed at.
        /// </summary>
        internal static string ShortenPathDescription(string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                return description ?? string.Empty;
            }

            int lastSpace = description.LastIndexOf(' ');
            string prefix = lastSpace >= 0 ? description.Substring(0, lastSpace + 1) : string.Empty;
            string lastWord = lastSpace >= 0 ? description.Substring(lastSpace + 1) : description;

            int lastSep = lastWord.LastIndexOfAny(PathSeparators);
            if (lastSep < 0 || lastSep == lastWord.Length - 1)
            {
                return description;
            }

            string fileName = lastWord.Substring(lastSep + 1);
            string beforeFile = lastWord.Substring(0, lastSep);
            int parentSep = beforeFile.LastIndexOfAny(PathSeparators);
            if (parentSep < 0)
            {
                return description;
            }
            string parentDir = beforeFile.Substring(parentSep + 1);
            if (string.IsNullOrEmpty(parentDir))
            {
                return description;
            }

            char sep = lastWord.IndexOf('\\') >= 0 ? '\\' : '/';
            // "..." built literally from ASCII period characters -- no
            // Unicode ellipsis glyph, per this package's IconLoader-only
            // policy for any non-ASCII UI character.
            return prefix + "..." + sep + parentDir + sep + fileName;
        }

        private static VisualElement CreateStatusIcon(SubagentRecord subagent)
        {
            switch (subagent.status)
            {
                case "running":
                    return MessageBlockFactory.CreateSpinner();
                case "completed":
                    return IconLoader.CreateIcon("TestPassed", IconLoader.GlyphCheck,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--ok");
                case "failed":
                case "stopped":
                    return IconLoader.CreateIcon("TestFailed", IconLoader.GlyphCross,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--fail");
                default:
                    var pending = new Label(IconLoader.GlyphBullet);
                    pending.AddToClassList("uap-tool-glyph");
                    pending.AddToClassList("uap-tool-glyph--pending");
                    return pending;
            }
        }

        /// <summary>Duration for a finished subagent; live elapsed while running
        /// (the outer ToolCallRecord's own timing spans the whole spawn).
        /// The live ticker is paused on detach so a discarded card (row
        /// rebuilt by MessageListController, or pruned) never keeps ticking
        /// outside the visual tree.</summary>
        private static Label CreateTimeLabel(ToolCallRecord record)
        {
            var time = new Label(string.Empty);
            time.enableRichText = false;
            time.AddToClassList("uap-toolcard-time");
            if (record.status == ToolCallStatus.Running && record.startedAtUtcTicks > 0)
            {
                long startTicks = record.startedAtUtcTicks;
                time.text = ToolActivityCard.FormatSeconds(ToolActivityCard.ElapsedMs(startTicks));
                IVisualElementScheduledItem ticker = time.schedule.Execute(() =>
                {
                    time.text = ToolActivityCard.FormatSeconds(ToolActivityCard.ElapsedMs(startTicks));
                }).Every(500);
                time.RegisterCallback<DetachFromPanelEvent>(delegate
                {
                    ticker.Pause();
                });
            }
            else if (record.durationMs > 0)
            {
                time.text = ToolActivityCard.FormatSeconds(record.durationMs);
            }
            return time;
        }
    }
}
