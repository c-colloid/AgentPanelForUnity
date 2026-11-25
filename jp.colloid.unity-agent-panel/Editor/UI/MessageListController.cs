using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using UnityEngine;
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
    /// - Virtualized window (docs/design-notes/2026-09-30-transcript-
    ///   virtualization.md): only the messages near the viewport are live
    ///   elements. Everything above and below is represented by two
    ///   spacers whose heights are the measured heights of rows that were
    ///   live once, or an estimate for rows never built. Scrolling builds
    ///   rows as they approach the viewport and releases rows that moved
    ///   far away, so the live tree stays a few dozen rows however long
    ///   the conversation is. That bounds the cost of the full re-style /
    ///   re-measure UI Toolkit runs every time the docked tab is re-shown
    ///   (Unity detaches and re-attaches the window root), which used to
    ///   stall for seconds on a long transcript.
    /// </summary>
    public sealed class MessageListController
    {
        /// <summary>
        /// Rows a fresh build makes around its anchor (the tail when
        /// following the bottom). Enough to fill any reasonable viewport
        /// with slack; the window pass grows it if not.
        /// </summary>
        internal const int InitialRows = 24;

        /// <summary>Rows one window pass adds on a side that ran short.</summary>
        internal const int LoadChunk = 8;

        /// <summary>
        /// Viewport heights beyond the visible range that must be covered
        /// by live rows (rows are built when the covered range is shorter).
        /// </summary>
        internal const float LoadMarginViewports = 1.0f;

        /// <summary>
        /// Viewport heights beyond the visible range past which a live
        /// row is released. Larger than the load margin so a row is never
        /// built and released again by the same scroll (hysteresis).
        /// </summary>
        internal const float ReleaseMarginViewports = 2.5f;

        /// <summary>Height assumed for a row that has never been measured.</summary>
        internal const float DefaultRowHeight = 96f;

        private const float StickSlackPixels = 4f;
        private const long WindowPassDelayMillis = 16;

        private sealed class Row
        {
            public VisualElement Element;
            public int Signature;
        }

        private readonly VisualElement _root;
        private readonly ScrollView _scroll;
        private readonly Button _pill;
        private readonly StreamingLabelPump _pump;
        private readonly VisualElement _topSpacer;
        private readonly VisualElement _bottomSpacer;
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly List<string> _renderedIds = new List<string>();

        /// <summary>Measured outer height of every row that was live once, by id.</summary>
        private readonly Dictionary<string, float> _heights = new Dictionary<string, float>();
        private float _measuredSum;
        private int _measuredCount;

        private List<ChatMessage> _messages;
        private int _renderedStart = -1;
        private int _lastCount;
        private bool _stick = true;
        private bool _pillShown;
        private IVisualElementScheduledItem _windowPass;

        // Scroll anchoring for a head insert: rows built ABOVE the
        // viewport replace an estimated spacer height with real heights,
        // and the scroller's pixel offset would then show different
        // content. The offset and content height captured before the
        // insert are re-applied with the height delta once layout lands.
        private bool _anchorPending;
        private float _anchorOffset;
        private float _anchorBaselineHeight;

        // Restore after a rebuild: the row (counted from the end, so a
        // head-truncated reload still resolves) and the pixel offset from
        // its top that the viewport top sat at.
        private bool _restorePending;
        private int _restoreFromEnd;
        private float _restoreDelta;

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
            // Resize drags used to re-wrap every rendered message on every
            // frame (docs/design-notes/2026-09-16-resize-reflow-throttle.md).
            ScrollReflowThrottle.Attach(_scroll);

            _pill = new Button(OnPillClicked);
            _pill.text = IconLoader.GlyphDownArrow + " " + L10n.S.ChatJumpToLatestButton;
            _pill.AddToClassList("uap-jump-pill");
            _pill.style.display = DisplayStyle.None;
            _root.Add(_pill);

            // Spacers stand in for the rows outside the live window: the
            // content container is always [top spacer][rows...][bottom
            // spacer], so row i is child i + 1.
            _topSpacer = new VisualElement();
            _topSpacer.AddToClassList("uap-msg-spacer");
            _bottomSpacer = new VisualElement();
            _bottomSpacer.AddToClassList("uap-msg-spacer");
            _scroll.contentContainer.Add(_topSpacer);
            _scroll.contentContainer.Add(_bottomSpacer);

            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnContentGeometryChanged);
            _scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(OnViewportGeometryChanged);
            _scroll.verticalScroller.valueChanged += OnScrollerValueChanged;
            _scroll.RegisterCallback<WheelEvent>(OnWheel);
        }

        // -- Public API ---------------------------------------------------------

        /// <summary>Synchronizes the rendered rows with the session transcript.</summary>
        public void Refresh(ChatSession session)
        {
            List<ChatMessage> messages = session != null ? session.messages : null;
            _messages = messages;
            if (messages == null || messages.Count == 0)
            {
                if (_renderedIds.Count > 0 || _renderedStart != 0)
                {
                    Rebuild(new List<ChatMessage>());
                }
                _lastCount = 0;
                return;
            }

            // Rows must be exactly messages[_renderedStart .. +count):
            // anything else (nothing built yet, session switch, clear,
            // restored transcript) is a fresh build around the anchor.
            if (_renderedStart < 0 || !RenderedIdsMatch(messages))
            {
                Rebuild(messages);
                _lastCount = messages.Count;
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

            // Update structurally-changed live rows in place.
            for (int i = 0; i < _renderedIds.Count; i++)
            {
                ChatMessage message = messages[_renderedStart + i];
                Row row = _rows[message.id];
                int signature = ComputeSignature(message);
                if (signature == row.Signature)
                {
                    continue;
                }
                VisualElement fresh = MessageBlockFactory.CreateMessageElement(message, _pump);
                int childIndex = i + 1;
                content.RemoveAt(childIndex);
                content.Insert(childIndex, fresh);
                row.Element = fresh;
                row.Signature = signature;
                contentMutated = true;
            }

            // New messages: appended as live rows while the tail is live
            // (the usual case: following the bottom, or reading near it);
            // otherwise they only grow the bottom spacer and the window
            // pass builds them when the user scrolls down to them.
            int renderedEnd = _renderedStart + _renderedIds.Count;
            if (renderedEnd == _lastCount && messages.Count > _lastCount)
            {
                for (int i = renderedEnd; i < messages.Count; i++)
                {
                    InsertRow(messages[i], _renderedIds.Count);
                }
                contentMutated = true;
            }
            _lastCount = messages.Count;
            UpdateSpacers();

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
            RequestWindowPass();
        }

        /// <summary>Scrolls to the bottom and re-enables follow mode.</summary>
        public void ScrollToBottom()
        {
            _stick = true;
            if (_messages != null && _renderedStart >= 0
                && _renderedStart + _renderedIds.Count < _messages.Count)
            {
                // The tail is not live (the user was reading far up):
                // rebuild around it instead of paging through everything
                // in between.
                Rebuild(_messages);
                _lastCount = _messages.Count;
            }
            _scroll.verticalScroller.value = _scroll.verticalScroller.highValue;
            UpdatePill();
        }

        /// <summary>Current offset for SessionState persistence (-1 = following bottom).</summary>
        public float GetScrollOffset()
        {
            if (_stick)
            {
                return -1f;
            }
            int index = FindViewportAnchorRow();
            if (index < 0)
            {
                return _scroll.verticalScroller.value;
            }
            return Math.Max(0f, _scroll.verticalScroller.value - RowAt(index).layout.y);
        }

        /// <summary>
        /// The row <see cref="GetScrollOffset"/> is measured from, counted
        /// from the end of the transcript (1 = the newest message); 0 when
        /// following the bottom or nothing is live. Counted from the end
        /// so a reload that truncates the transcript's head (the loader's
        /// message cap) still resolves the same message.
        /// </summary>
        public int GetScrollAnchorFromEnd()
        {
            if (_stick || _messages == null)
            {
                return 0;
            }
            int index = FindViewportAnchorRow();
            if (index < 0)
            {
                return 0;
            }
            return _messages.Count - (_renderedStart + index);
        }

        /// <summary>
        /// Restores a persisted scroll state before the first Refresh:
        /// <paramref name="offset"/> below zero follows the bottom;
        /// otherwise the first build renders around the message
        /// <paramref name="anchorFromEnd"/> from the end and puts the
        /// viewport top <paramref name="offset"/> pixels below that row's
        /// top once it has a layout. With no anchor (0) the offset is a
        /// plain pixel offset from the top, clamped into range.
        /// </summary>
        public void RestoreScrollOffset(float offset, int anchorFromEnd)
        {
            if (offset < 0f)
            {
                _stick = true;
                _restorePending = false;
                _scroll.schedule.Execute(ScrollToBottom).StartingIn(30);
                return;
            }
            _stick = false;
            _restorePending = true;
            _restoreFromEnd = anchorFromEnd;
            _restoreDelta = offset;
        }

        /// <summary>Pixel-offset restore with no anchor row (legacy callers).</summary>
        public void RestoreScrollOffset(float offset)
        {
            RestoreScrollOffset(offset, 0);
        }

        // -- Window building --------------------------------------------------------

        /// <summary>Whether the live rows are exactly the transcript slice at _renderedStart.</summary>
        private bool RenderedIdsMatch(List<ChatMessage> messages)
        {
            if (_renderedStart + _renderedIds.Count > messages.Count)
            {
                return false;
            }
            for (int i = 0; i < _renderedIds.Count; i++)
            {
                if (!string.Equals(_renderedIds[i], messages[_renderedStart + i].id,
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Fresh build: live rows around the anchor -- the pending restore
        /// row, or the tail. Everything else becomes spacer height.
        /// </summary>
        private void Rebuild(List<ChatMessage> messages)
        {
            bool wasSticking = _stick;
            float savedOffset = _scroll.verticalScroller.value;

            VisualElement content = _scroll.contentContainer;
            content.Clear();
            _rows.Clear();
            _renderedIds.Clear();
            _anchorPending = false;
            content.Add(_topSpacer);
            content.Add(_bottomSpacer);

            int count = messages.Count;
            int anchor = -1;
            if (_restorePending && _restoreFromEnd > 0)
            {
                anchor = count - _restoreFromEnd;
                if (anchor < 0 || anchor >= count)
                {
                    // The transcript no longer holds that message: nothing
                    // to anchor to, fall back to the newest rows.
                    _restorePending = false;
                    anchor = -1;
                }
            }
            int start = ComputeWindowStart(count, anchor, InitialRows);
            int end = Math.Min(count, start + InitialRows);
            _renderedStart = start;
            for (int i = start; i < end; i++)
            {
                InsertRow(messages[i], _renderedIds.Count);
            }
            UpdateSpacers();

            if (_restorePending)
            {
                // Applied by OnContentGeometryChanged once the anchor row
                // (or, with no anchor row, the content) has a layout.
                return;
            }
            _stick = wasSticking;
            _scroll.verticalScroller.value = ComputeRestoredScrollValue(
                wasSticking, savedOffset, _scroll.verticalScroller.highValue);
            UpdatePill();
            RequestWindowPass();
        }

        /// <summary>
        /// Pure: the first live row of a fresh build. With an anchor row,
        /// a few rows above it are included so the viewport top has slack
        /// to scroll up into without an immediate head insert; without
        /// one, the newest <paramref name="rows"/>.
        /// </summary>
        internal static int ComputeWindowStart(int messageCount, int anchor, int rows)
        {
            if (anchor < 0)
            {
                return Math.Max(0, messageCount - rows);
            }
            int above = rows / 4;
            return Math.Max(0, Math.Min(anchor - above, messageCount - rows));
        }

        /// <summary>Builds the element for a message and makes it live row <paramref name="rowIndex"/>.</summary>
        private void InsertRow(ChatMessage message, int rowIndex)
        {
            VisualElement element = MessageBlockFactory.CreateMessageElement(message, _pump);
            _scroll.contentContainer.Insert(rowIndex + 1, element);
            _rows[message.id] = new Row
            {
                Element = element,
                Signature = ComputeSignature(message)
            };
            _renderedIds.Insert(rowIndex, message.id);
        }

        /// <summary>
        /// Releases live row <paramref name="rowIndex"/>, remembering its
        /// measured height so the spacer that takes its place has exactly
        /// the same extent (no scroll jump).
        /// </summary>
        private void ReleaseRow(int rowIndex)
        {
            string id = _renderedIds[rowIndex];
            Row row = _rows[id];
            RecordHeight(id, OuterHeight(row.Element));
            row.Element.RemoveFromHierarchy();
            _rows.Remove(id);
            _renderedIds.RemoveAt(rowIndex);
        }

        private VisualElement RowAt(int rowIndex)
        {
            return _rows[_renderedIds[rowIndex]].Element;
        }

        private static float OuterHeight(VisualElement element)
        {
            float height = element.layout.height;
            if (float.IsNaN(height))
            {
                return float.NaN;
            }
            float top = element.resolvedStyle.marginTop;
            float bottom = element.resolvedStyle.marginBottom;
            return height + (float.IsNaN(top) ? 0f : top) + (float.IsNaN(bottom) ? 0f : bottom);
        }

        private void RecordHeight(string id, float height)
        {
            if (float.IsNaN(height) || height <= 0f)
            {
                return;
            }
            float previous;
            if (_heights.TryGetValue(id, out previous))
            {
                _measuredSum += height - previous;
            }
            else
            {
                _measuredSum += height;
                _measuredCount++;
            }
            _heights[id] = height;
        }

        /// <summary>Estimated outer height of a message that is not live.</summary>
        private float EstimateHeight(ChatMessage message)
        {
            float height;
            if (_heights.TryGetValue(message.id, out height))
            {
                return height;
            }
            return _measuredCount > 0 ? _measuredSum / _measuredCount : DefaultRowHeight;
        }

        /// <summary>Total estimated height of messages[from, to).</summary>
        private float EstimateRange(List<ChatMessage> messages, int from, int to)
        {
            float total = 0f;
            for (int i = from; i < to; i++)
            {
                total += EstimateHeight(messages[i]);
            }
            return total;
        }

        private void UpdateSpacers()
        {
            List<ChatMessage> messages = _messages;
            if (messages == null || _renderedStart < 0)
            {
                _topSpacer.style.height = 0f;
                _bottomSpacer.style.height = 0f;
                return;
            }
            int end = _renderedStart + _renderedIds.Count;
            _topSpacer.style.height = EstimateRange(messages, 0, _renderedStart);
            _bottomSpacer.style.height = EstimateRange(messages, end, messages.Count);
        }

        // -- Window pass (virtualization) ---------------------------------------------

        private void RequestWindowPass()
        {
            if (_windowPass == null)
            {
                _windowPass = _scroll.schedule.Execute(WindowPass);
            }
            // ExecuteLater on an existing item cancels its pending run and
            // re-schedules it: scroll events and geometry changes within
            // one frame coalesce into a single pass.
            _windowPass.ExecuteLater(WindowPassDelayMillis);
        }

        /// <summary>
        /// One virtualization step: release rows far outside the viewport,
        /// then build a chunk on any side whose live coverage falls short
        /// of the load margin. A pass that changed the tree ends in new
        /// geometry, which requests the next pass, so a long scroll
        /// converges in a few frames without ever touching more than a
        /// chunk per frame.
        /// </summary>
        private void WindowPass()
        {
            List<ChatMessage> messages = _messages;
            if (messages == null || _renderedStart < 0 || _renderedIds.Count == 0
                || _anchorPending || _restorePending)
            {
                return;
            }
            float viewport = _scroll.contentViewport.layout.height;
            if (float.IsNaN(viewport) || viewport <= 0f)
            {
                return;
            }
            VisualElement first = RowAt(0);
            VisualElement last = RowAt(_renderedIds.Count - 1);
            if (float.IsNaN(first.layout.y) || float.IsNaN(last.layout.height))
            {
                // Not laid out yet; the geometry event re-requests the pass.
                return;
            }

            // Messages appended since the last Refresh are not this pass's
            // business (Refresh appends them when the tail is live): the
            // list is mutated by AgentHub between refreshes, so the count
            // Refresh saw is the bound here.
            int count = Math.Min(_lastCount, messages.Count);
            if (_stick && _renderedStart + _renderedIds.Count < count)
            {
                // Following the bottom with the tail not live (the user
                // dragged the thumb to the end from far up): rebuild at
                // the tail instead of paging through everything between.
                Rebuild(messages);
                _lastCount = messages.Count;
                return;
            }

            float value = _scroll.verticalScroller.value;
            float visibleTop = value;
            float visibleBottom = value + viewport;
            float loadMargin = viewport * LoadMarginViewports;
            float releaseMargin = viewport * ReleaseMarginViewports;

            // Refresh the height record of every live row (a streaming row
            // grows; a card expands) so the spacers stay honest.
            for (int i = 0; i < _renderedIds.Count; i++)
            {
                RecordHeight(_renderedIds[i], OuterHeight(RowAt(i)));
            }

            bool changed = false;

            // Release above: rows whose bottom edge is far above the
            // visible top. Never the last live row.
            while (_renderedIds.Count > 1
                && ShouldReleaseAbove(RowAt(0).layout.yMax, visibleTop, releaseMargin))
            {
                ReleaseRow(0);
                _renderedStart++;
                changed = true;
            }

            // Release below: rows whose top edge is far below the visible
            // bottom. Not while following the bottom (the tail must stay
            // live to be followed).
            while (!_stick && _renderedIds.Count > 1
                && ShouldReleaseBelow(RowAt(_renderedIds.Count - 1).layout.y, visibleBottom, releaseMargin))
            {
                ReleaseRow(_renderedIds.Count - 1);
                changed = true;
            }

            int end = _renderedStart + _renderedIds.Count;

            // Load below first (no anchoring needed): the last live row's
            // bottom edge is within the load margin below the visible
            // bottom and there are messages after it.
            if (end < count
                && ShouldLoadBelow(RowAt(_renderedIds.Count - 1).layout.yMax, visibleBottom, loadMargin))
            {
                int take = Math.Min(LoadChunk, count - end);
                for (int i = 0; i < take; i++)
                {
                    InsertRow(messages[end + i], _renderedIds.Count);
                }
                changed = true;
            }

            // Load above: the first live row's top edge is within the load
            // margin above the visible top and there are messages before
            // it. The spacer shrinks by the estimate the new rows carried
            // and the rows arrive with their real heights, so the offset is
            // re-applied with the height delta once layout lands.
            if (_renderedStart > 0
                && ShouldLoadAbove(RowAt(0).layout.y, visibleTop, loadMargin))
            {
                float baseline = _scroll.contentContainer.layout.height;
                if (!float.IsNaN(baseline) && baseline > 0f)
                {
                    _anchorPending = true;
                    _anchorOffset = value;
                    _anchorBaselineHeight = baseline;
                }
                int take = Math.Min(LoadChunk, _renderedStart);
                for (int i = 0; i < take; i++)
                {
                    InsertRow(messages[_renderedStart - 1], 0);
                    _renderedStart--;
                }
                changed = true;
            }

            if (changed)
            {
                UpdateSpacers();
                UpdatePill();
            }
        }

        /// <summary>Pure: a row whose bottom edge is more than the release margin above the visible top is released.</summary>
        internal static bool ShouldReleaseAbove(float rowBottom, float visibleTop, float releaseMargin)
        {
            return rowBottom < visibleTop - releaseMargin;
        }

        /// <summary>Pure: a row whose top edge is more than the release margin below the visible bottom is released.</summary>
        internal static bool ShouldReleaseBelow(float rowTop, float visibleBottom, float releaseMargin)
        {
            return rowTop > visibleBottom + releaseMargin;
        }

        /// <summary>Pure: rows are built above when the first live row's top is within the load margin of the visible top.</summary>
        internal static bool ShouldLoadAbove(float firstRowTop, float visibleTop, float loadMargin)
        {
            return firstRowTop > visibleTop - loadMargin;
        }

        /// <summary>Pure: rows are built below when the last live row's bottom is within the load margin of the visible bottom.</summary>
        internal static bool ShouldLoadBelow(float lastRowBottom, float visibleBottom, float loadMargin)
        {
            return lastRowBottom < visibleBottom + loadMargin;
        }

        /// <summary>
        /// Pure: the scroller value that keeps the same content in view
        /// after rows were inserted above it. The content grew by
        /// (newHeight - baselineHeight); shifting the offset by exactly
        /// that keeps the first previously-visible row where it was.
        /// </summary>
        internal static float ComputeAnchoredScrollValue(float savedOffset,
            float baselineHeight, float newHeight)
        {
            float value = savedOffset + (newHeight - baselineHeight);
            return value < 0f ? 0f : value;
        }

        /// <summary>Index of the live row under the viewport top (-1 = none / not laid out).</summary>
        private int FindViewportAnchorRow()
        {
            if (_renderedStart < 0 || _renderedIds.Count == 0)
            {
                return -1;
            }
            float value = _scroll.verticalScroller.value;
            for (int i = 0; i < _renderedIds.Count; i++)
            {
                Rect layout = RowAt(i).layout;
                if (float.IsNaN(layout.y))
                {
                    return -1;
                }
                if (layout.yMax > value)
                {
                    return i;
                }
            }
            return _renderedIds.Count - 1;
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
            if (_restorePending)
            {
                if (!TryApplyRestore())
                {
                    return;
                }
            }
            if (_anchorPending)
            {
                // The first layout after a head insert: the delta is the
                // real height of the inserted rows minus the estimate the
                // spacer gave up, whatever else moved in between.
                _anchorPending = false;
                _scroll.verticalScroller.value = ComputeAnchoredScrollValue(
                    _anchorOffset, _anchorBaselineHeight, evt.newRect.height);
            }
            if (_stick)
            {
                _scroll.verticalScroller.value = _scroll.verticalScroller.highValue;
            }
            UpdatePill();
            RequestWindowPass();
        }

        /// <summary>
        /// Applies a pending restore once layout can satisfy it: the anchor
        /// row has a real height (or, with no anchor row, the scroller has
        /// a range). A fixed timer is not enough: on a large restored
        /// transcript the first ticks can fire while highValue is still 0,
        /// which would clamp the target to the top.
        /// </summary>
        private bool TryApplyRestore()
        {
            float high = _scroll.verticalScroller.highValue;
            float target;
            int anchorRow = _messages != null && _restoreFromEnd > 0
                ? _messages.Count - _restoreFromEnd - _renderedStart : -1;
            if (anchorRow >= 0 && anchorRow < _renderedIds.Count)
            {
                Rect layout = RowAt(anchorRow).layout;
                if (float.IsNaN(layout.y) || layout.height <= 0f)
                {
                    return false;
                }
                target = layout.y + _restoreDelta;
            }
            else
            {
                if (high <= 0f)
                {
                    return false;
                }
                target = _restoreDelta;
            }
            _restorePending = false;
            _stick = false;
            _scroll.verticalScroller.value = Math.Max(0f, Math.Min(target, high));
            return true;
        }

        private void OnViewportGeometryChanged(GeometryChangedEvent evt)
        {
            // A taller viewport needs more live rows to cover its margins.
            RequestWindowPass();
        }

        private void OnScrollerValueChanged(float value)
        {
            float high = _scroll.verticalScroller.highValue;
            _stick = value >= high - StickSlackPixels;
            UpdatePill();
            RequestWindowPass();
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
