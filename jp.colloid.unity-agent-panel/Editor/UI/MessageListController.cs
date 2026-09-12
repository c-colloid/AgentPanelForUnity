using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Manual ScrollView message list (ARCHITECTURE.md D6: no ListView --
    /// 2022.3 DynamicHeight virtualization is buggy and streaming is its
    /// worst case).
    ///
    /// - Incremental updates: per-message elements are cached by message id
    ///   and rebuilt only when a structural signature changes (block count,
    ///   kinds, streaming flags, tool status). Streaming text growth is
    ///   excluded from the signature -- StreamingLabelPump updates those
    ///   labels in place.
    /// - Stick-to-bottom: follows content growth via GeometryChangedEvent
    ///   on the content container + verticalScroller.highValue; scrolling
    ///   up (wheel or scroller drag) detaches and shows the jump pill.
    /// - Pruning: only the newest 300 messages are rendered; a small note
    ///   reports how many older ones are hidden (risk 9).
    /// </summary>
    public sealed class MessageListController
    {
        private const int MaxRenderedMessages = 300;

        /// <summary>
        /// UICODE-3 hysteresis: how far past MaxRenderedMessages the
        /// rendered window may grow before the anchor moves forward. The
        /// old anchor (always count - max) advanced by one on EVERY append
        /// past the cap, which failed the prefix check and full-rebuilt
        /// all 300 rows per message. With slack, appends between anchor
        /// moves stay on the O(delta) incremental path, and each anchor
        /// move prunes a batch of head rows instead of rebuilding.
        /// </summary>
        internal const int PruneSlack = 50;

        private const float StickSlackPixels = 4f;

        private sealed class Row
        {
            public VisualElement Element;
            public int Signature;
        }

        private readonly VisualElement _root;
        private readonly ScrollView _scroll;
        private readonly Button _pill;
        private readonly StreamingLabelPump _pump;
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly List<string> _renderedIds = new List<string>();
        private int _renderedStart = -1;
        private bool _pruneNoteVisible;
        private bool _stick = true;
        private bool _pillShown;
        private float _pendingRestoreOffset = -1f;

        /// <summary>Container holding the scroll view and the jump pill.</summary>
        public VisualElement Root
        {
            get { return _root; }
        }

        public MessageListController(StreamingLabelPump pump)
        {
            _pump = pump;
            _root = new VisualElement();
            _root.AddToClassList("uap-msg-list");

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("uap-msg-scroll");
            _root.Add(_scroll);

            _pill = new Button(OnPillClicked);
            _pill.text = IconLoader.GlyphDownArrow + " " + L10n.S.ChatJumpToLatestButton;
            _pill.AddToClassList("uap-jump-pill");
            _pill.style.display = DisplayStyle.None;
            _root.Add(_pill);

            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnContentGeometryChanged);
            _scroll.verticalScroller.valueChanged += OnScrollerValueChanged;
            _scroll.RegisterCallback<WheelEvent>(OnWheel);
        }

        // -- Public API ---------------------------------------------------------

        /// <summary>Synchronizes the rendered rows with the session transcript.</summary>
        public void Refresh(ChatSession session)
        {
            List<ChatMessage> messages = session != null ? session.messages : null;
            if (messages == null || messages.Count == 0)
            {
                if (_renderedIds.Count > 0 || _renderedStart != 0)
                {
                    FullRebuild(new List<ChatMessage>(), 0);
                }
                return;
            }

            int start = ComputePruneAnchor(
                messages.Count, _renderedStart, MaxRenderedMessages, PruneSlack);
            int windowCount = messages.Count - start;

            // Two ways onto the incremental path: the anchor is unchanged
            // and the rendered rows are a prefix of the window (the
            // ordinary case), or the anchor moved FORWARD and the rendered
            // rows minus a droppable head are still that prefix (UICODE-3:
            // prune the head rows, keep everything else incremental).
            int pruneDrop = 0;
            bool incremental = false;
            if (start == _renderedStart && _renderedIds.Count <= windowCount)
            {
                incremental = RenderedIdsMatch(messages, start, 0);
            }
            else if (_renderedStart >= 0 && start > _renderedStart
                && start - _renderedStart < _renderedIds.Count
                && _renderedIds.Count - (start - _renderedStart) <= windowCount)
            {
                pruneDrop = start - _renderedStart;
                incremental = RenderedIdsMatch(messages, start, pruneDrop);
            }
            if (!incremental)
            {
                FullRebuild(messages, start);
                return;
            }

            VisualElement content = _scroll.contentContainer;

            // Capture scroll intent BEFORE any mutation below (design note
            // 2026-08-03 section 1.1): the swap loop's RemoveAt+Insert used
            // to run with nothing saving or restoring _scroll's offset
            // around it, so a subagent card updating every couple of
            // seconds yanked the list out from under a user who was
            // reading it. wasSticking/savedOffset are re-applied once,
            // after every mutation this call makes has already happened --
            // see ComputeRestoredScrollValue's doc comment for why that
            // also makes the "transient clamp" hazard moot regardless of
            // whether it can actually occur.
            bool wasSticking = _stick;
            float savedOffset = _scroll.verticalScroller.value;
            bool contentMutated = false;

            if (pruneDrop > 0)
            {
                PruneHeadRows(pruneDrop, start);
                contentMutated = true;
            }
            int offset = _pruneNoteVisible ? 1 : 0;

            // Update structurally-changed existing rows in place.
            for (int i = 0; i < _renderedIds.Count; i++)
            {
                ChatMessage message = messages[start + i];
                Row row = _rows[message.id];
                int signature = ComputeSignature(message);
                if (signature == row.Signature)
                {
                    continue;
                }
                VisualElement fresh = MessageBlockFactory.CreateMessageElement(message, _pump);
                int childIndex = i + offset;
                content.RemoveAt(childIndex);
                content.Insert(childIndex, fresh);
                row.Element = fresh;
                row.Signature = signature;
                contentMutated = true;
            }

            // Append rows for new messages.
            for (int i = _renderedIds.Count; i < windowCount; i++)
            {
                ChatMessage message = messages[start + i];
                AppendRow(message);
                contentMutated = true;
            }

            if (contentMutated)
            {
                // Re-assert rather than trust whatever OnScrollerValueChanged
                // may have done in between: this line runs after every
                // RemoveAt/Insert/Add in this call has already happened, so
                // it overwrites any intermediate state unconditionally. A
                // user pinned at the bottom stays pinned; a user who had
                // scrolled up stays at the same absolute offset instead of
                // snapping back to wherever the swap happened to leave the
                // scroller.
                _stick = wasSticking;
                _scroll.verticalScroller.value =
                    ComputeRestoredScrollValue(wasSticking, savedOffset, _scroll.verticalScroller.highValue);
                UpdatePill();
            }
        }

        /// <summary>Scrolls to the bottom and re-enables follow mode.</summary>
        public void ScrollToBottom()
        {
            _stick = true;
            _scroll.verticalScroller.value = _scroll.verticalScroller.highValue;
            UpdatePill();
        }

        /// <summary>Current offset for SessionState persistence (-1 = following bottom).</summary>
        public float GetScrollOffset()
        {
            return _stick ? -1f : _scroll.verticalScroller.value;
        }

        /// <summary>
        /// Restores a persisted offset once layout has actually produced a
        /// scrollable range. A fixed timer is not enough: on a large
        /// restored transcript the 30 ms tick can fire while highValue is
        /// still 0, which would clamp the target to the top. The offset is
        /// applied from the first GeometryChangedEvent where highValue can
        /// satisfy it (or at least is non-zero), then the hook unregisters.
        /// </summary>
        public void RestoreScrollOffset(float offset)
        {
            if (offset < 0f)
            {
                _stick = true;
                _scroll.schedule.Execute(ScrollToBottom).StartingIn(30);
                return;
            }
            _stick = false;
            _pendingRestoreOffset = offset;
            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnRestoreGeometry);
        }

        private void OnRestoreGeometry(GeometryChangedEvent evt)
        {
            if (_pendingRestoreOffset < 0f)
            {
                _scroll.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnRestoreGeometry);
                return;
            }
            float high = _scroll.verticalScroller.highValue;
            if (high <= 0f)
            {
                // Layout not settled yet; wait for the next geometry pass.
                return;
            }
            _scroll.verticalScroller.value = Math.Min(_pendingRestoreOffset, high);
            _pendingRestoreOffset = -1f;
            _scroll.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnRestoreGeometry);
        }

        // -- Rebuild helpers ------------------------------------------------------

        /// <summary>
        /// Pure anchor decision (UICODE-3, internal for the EditMode
        /// table): where the rendered window starts. Holds the CURRENT
        /// anchor while the window fits within max + slack (so ordinary
        /// appends past the cap stay incremental), and re-anchors to the
        /// newest max once the slack is exhausted (the caller then prunes
        /// the head rows). A negative renderedStart means nothing is
        /// rendered yet -- anchor straight to the newest max.
        /// </summary>
        internal static int ComputePruneAnchor(int messageCount, int renderedStart,
            int max, int slack)
        {
            // Nothing rendered yet, or the transcript shrank below the old
            // anchor (session switch/clear): anchor to the newest max.
            if (renderedStart < 0 || renderedStart > messageCount)
            {
                return Math.Max(0, messageCount - max);
            }
            if (messageCount - renderedStart <= max + slack)
            {
                return renderedStart;
            }
            return messageCount - max;
        }

        /// <summary>Whether the rendered ids, minus a dropped head of
        /// <paramref name="drop"/>, are a prefix of the window at
        /// <paramref name="start"/>.</summary>
        private bool RenderedIdsMatch(List<ChatMessage> messages, int start, int drop)
        {
            for (int i = drop; i < _renderedIds.Count; i++)
            {
                if (!string.Equals(_renderedIds[i], messages[start + i - drop].id,
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// UICODE-3: drops the oldest <paramref name="drop"/> rendered rows
        /// and moves the anchor to <paramref name="newStart"/>, updating
        /// (or creating) the prune note in place -- O(drop), where the old
        /// path rebuilt all rendered rows. Runs inside Refresh's scroll
        /// capture/restore window like every other mutation there.
        /// </summary>
        private void PruneHeadRows(int drop, int newStart)
        {
            VisualElement content = _scroll.contentContainer;
            int noteOffset = _pruneNoteVisible ? 1 : 0;
            for (int i = 0; i < drop; i++)
            {
                content.RemoveAt(noteOffset);
                _rows.Remove(_renderedIds[i]);
            }
            _renderedIds.RemoveRange(0, drop);
            _renderedStart = newStart;

            if (newStart <= 0)
            {
                return;
            }
            if (_pruneNoteVisible)
            {
                var note = content[0] as Label;
                if (note != null)
                {
                    note.text = L10n.F(L10n.S.ChatPruneNoteFmt, newStart);
                }
            }
            else
            {
                var note = new Label(L10n.F(L10n.S.ChatPruneNoteFmt, newStart));
                note.AddToClassList("uap-prune-note");
                content.Insert(0, note);
                _pruneNoteVisible = true;
            }
        }

        private void FullRebuild(List<ChatMessage> messages, int start)
        {
            // UICODE-3: a full rebuild must preserve scroll intent exactly
            // like the incremental path -- content.Clear() zeroes the
            // scroller, and without this capture/restore a reader who had
            // scrolled up was yanked to the top (then, if sticking logic
            // kicked in, to the bottom) whenever a rebuild landed.
            bool wasSticking = _stick;
            float savedOffset = _scroll.verticalScroller.value;

            VisualElement content = _scroll.contentContainer;
            content.Clear();
            _rows.Clear();
            _renderedIds.Clear();
            _renderedStart = start;
            _pruneNoteVisible = start > 0;

            if (_pruneNoteVisible)
            {
                var note = new Label(L10n.F(L10n.S.ChatPruneNoteFmt, start));
                note.AddToClassList("uap-prune-note");
                content.Add(note);
            }
            for (int i = start; i < messages.Count; i++)
            {
                AppendRow(messages[i]);
            }

            _stick = wasSticking;
            _scroll.verticalScroller.value = ComputeRestoredScrollValue(
                wasSticking, savedOffset, _scroll.verticalScroller.highValue);
            UpdatePill();
        }

        private void AppendRow(ChatMessage message)
        {
            VisualElement element = MessageBlockFactory.CreateMessageElement(message, _pump);
            _scroll.contentContainer.Add(element);
            _rows[message.id] = new Row
            {
                Element = element,
                Signature = ComputeSignature(message)
            };
            _renderedIds.Add(message.id);
        }

        /// <summary>
        /// Structural signature: rebuild triggers. Streaming block text is
        /// deliberately excluded (the pump owns those labels); finalized
        /// text length, tool status and duration are included.
        /// </summary>
        private static int ComputeSignature(ChatMessage message)
        {
            unchecked
            {
                int hash = 17;
                // Mixed in so a display-setting toggle that changes how an
                // already-rendered block looks (currently showThinking --
                // see MessageBlockFactory.SettingsGeneration's doc comment)
                // forces every cached row to rebuild on the next Refresh.
                hash = hash * 31 + MessageBlockFactory.SettingsGeneration;
                hash = hash * 31 + (message.role != null ? message.role.GetHashCode() : 0);
                hash = hash * 31 + message.blocks.Count;
                for (int i = 0; i < message.blocks.Count; i++)
                {
                    ChatMessageBlock block = message.blocks[i];
                    hash = hash * 31 + (int)block.kind;
                    hash = hash * 31 + (block.streaming ? 1 : 0);
                    if (block.toolCall != null)
                    {
                        hash = hash * 31 + (int)block.toolCall.status;
                        hash = hash * 31 + (int)block.toolCall.durationMs;
                        hash = hash * 31 + (block.toolCall.resultSummary != null
                            ? block.toolCall.resultSummary.Length : 0);
                        hash = hash * 31 + (block.toolCall.resultImagePaths != null
                            ? block.toolCall.resultImagePaths.Count : 0);
                        if (block.toolCall.subagent != null)
                        {
                            // Structural fields ONLY (design note section 7b,
                            // review-fix): progressLine/lastToolName/
                            // totalTokens are deliberately EXCLUDED here.
                            // AgentHub mutates those on every task_progress
                            // tick (far more often than ChatView's 60ms
                            // Refresh), so hashing them tore the row down and
                            // rebuilt a fresh (collapsed) SubagentCard on
                            // almost every refresh -- a running card's
                            // expand state could never survive. SubagentCard
                            // now live-updates those hot fields itself via
                            // its own scheduler; only a real structural
                            // change (status transition, a nested block
                            // appended, or blocks dropped for the cap)
                            // should trigger a rebuild.
                            SubagentRecord subagent = block.toolCall.subagent;
                            hash = hash * 31 + (subagent.status != null ? subagent.status.GetHashCode() : 0);
                            hash = hash * 31 + subagent.blocks.Count;
                            hash = hash * 31 + subagent.droppedBlockCount;

                            // 2026-08-03 defect (design note section 1.2): none
                            // of the three fields above move when a NESTED
                            // tool call finishes -- blocks.Count and
                            // droppedBlockCount only change on append/
                            // eviction, and the outer subagent.status only
                            // changes at spawn/finish, not per inner step --
                            // so a nested ToolActivityCard's spinner used to
                            // keep spinning after its own Running -> Succeeded/
                            // Failed transition until some unrelated change
                            // forced a rebuild. Mixing in each nested block's
                            // own toolCall.status closes exactly that gap.
                            // Deliberately narrow, to respect the same
                            // per-tick-rebuild lesson as the comment above:
                            // no nested text/duration/resultSummary length,
                            // and no recursive descent into a nested block's
                            // own subagent hot fields -- nested tool calls
                            // are depth-1 only (SubagentRecord's doc comment
                            // on MaxNestedBlocks), so block.toolCall.subagent
                            // is always null here and there is nothing
                            // further to walk into. Anything more than this
                            // one int would reintroduce the exact
                            // per-progress-tick rebuild this file already
                            // fixed once, just one level deeper.
                            for (int j = 0; j < subagent.blocks.Count; j++)
                            {
                                ToolCallRecord nestedToolCall = subagent.blocks[j].toolCall;
                                if (nestedToolCall != null)
                                {
                                    hash = hash * 31 + (int)nestedToolCall.status;
                                }
                            }
                        }
                    }
                    else if (!block.streaming)
                    {
                        hash = hash * 31 + (block.text != null ? block.text.Length : 0);
                    }
                }
                return hash;
            }
        }

        /// <summary>Test seam (InternalsVisibleTo "Colloid.AgentPanel.Editor.Tests",
        /// see Editor/AssemblyInfo.cs and AgentHub's *ForTests precedent):
        /// ComputeSignature itself stays private, this just forwards to it so
        /// signature-stability regressions (section 7b) can be pinned
        /// directly against a ChatMessage fixture.</summary>
        internal static int ComputeSignatureForTests(ChatMessage message)
        {
            return ComputeSignature(message);
        }

        /// <summary>
        /// Pure decision for where the vertical scroller should land right
        /// after Refresh's swap loop mutates rows in place (design note
        /// 2026-08-03 section 1.1). wasSticking/savedOffset must be read
        /// BEFORE the mutation; newHighValue is read immediately after, in
        /// the same synchronous call.
        ///
        /// - wasSticking: pin to newHighValue. This mirrors what
        ///   OnContentGeometryChanged already does once the deferred layout
        ///   pass actually catches up to the new content, so this call is
        ///   really about _stick itself surviving the swap (see the call
        ///   site) rather than about the exact number returned here.
        /// - not sticking: clamp the pre-mutation offset into whatever
        ///   range is known right now. Preserving the same absolute pixel
        ///   offset (rather than, say, a fraction of scroll height) matches
        ///   what the user is actually looking at -- a card updating below
        ///   or at their current position should not move the text they
        ///   are reading.
        ///
        /// Second-order hazard investigated per the design note: if content
        /// height ever transiently shrank mid-swap, the Scroller would
        /// clamp `value`, firing valueChanged -> OnScrollerValueChanged ->
        /// a spurious _stick flip, which the next OnContentGeometryChanged
        /// would turn into an unwanted jump to the bottom for a user who
        /// had deliberately scrolled up. Verdict: it cannot happen here.
        /// RemoveAt and Insert (and Add, for appended rows) all run
        /// synchronously within one Refresh call, and UI Toolkit's layout
        /// is dirty-flagged and recomputed once per panel update rather
        /// than once per DOM mutation -- nothing in this loop reads a
        /// layout-dependent property that would force an early pass -- so
        /// no GeometryChangedEvent, and therefore no highValue clamp, has
        /// any opportunity to land between two mutations in the same call.
        /// It is moot either way: the call site re-assigns _stick from
        /// wasSticking AFTER every mutation in the call has already run,
        /// so even if the transient existed, nothing it could have done to
        /// _stick would survive past that line.
        /// </summary>
        internal static float ComputeRestoredScrollValue(bool wasSticking, float savedOffset, float newHighValue)
        {
            if (wasSticking)
            {
                return newHighValue;
            }
            float clamped = Math.Min(savedOffset, newHighValue);
            return clamped < 0f ? 0f : clamped;
        }

        // -- Stick-to-bottom ----------------------------------------------------------

        private void OnContentGeometryChanged(GeometryChangedEvent evt)
        {
            if (_stick)
            {
                _scroll.verticalScroller.value = _scroll.verticalScroller.highValue;
            }
            UpdatePill();
        }

        private void OnScrollerValueChanged(float value)
        {
            float high = _scroll.verticalScroller.highValue;
            _stick = value >= high - StickSlackPixels;
            UpdatePill();
        }

        private void OnWheel(WheelEvent evt)
        {
            if (evt.delta.y < 0f)
            {
                // User scrolled upward: stop following.
                _stick = false;
                UpdatePill();
            }
        }

        private void OnPillClicked()
        {
            ScrollToBottom();
        }

        private void UpdatePill()
        {
            bool show = !_stick && _scroll.verticalScroller.highValue > 0f;
            if (show != _pillShown)
            {
                _pillShown = show;
                _pill.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
    }
}
