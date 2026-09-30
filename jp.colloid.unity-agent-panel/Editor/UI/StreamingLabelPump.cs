using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Typewriter renderer for in-progress message blocks (ARCHITECTURE.md
    /// D6 / R01 section 9 pattern): each tracked entry has a target (the
    /// block's current text, mutated by AgentHub on every delta) and a
    /// shown cursor that advances toward it. Labels are updated in batches
    /// on EditorApplication.update at ~80 ms intervals so per-token
    /// relayout never happens. Entries whose label left the panel or whose
    /// block finished streaming are dropped automatically; the structural
    /// rebuild in MessageListController renders the final text.
    ///
    /// Long text: a single UITK text element may not allocate more than
    /// 65535 vertices (~16,000 glyphs; design note
    /// 2026-09-13-toolcard-vertex-limit.md), and a streaming thinking or
    /// answer block can easily grow past that before it finalizes. The
    /// pump therefore never puts more than LongTextChunker.DefaultMaxChars
    /// into the tracked label: the shown text is split with
    /// LongTextChunker and the overflow goes into continuation Labels
    /// inserted right after the tracked one (same parent, same USS
    /// classes plus <c>uap-stream-cont</c>), added and removed as the text
    /// grows or snaps back (design note
    /// 2026-09-29-thinking-vertex-limit.md).
    /// </summary>
    public sealed class StreamingLabelPump
    {
        private const double FlushIntervalSeconds = 0.08;
        private const int MinAdvanceChars = 4;

        private sealed class Entry
        {
            public ChatMessageBlock Block;
            public Label Label;
            public int ShownLength;
            public string LastShown;
            /// <summary>Continuation labels holding chunks 1..n of the
            /// shown text; empty while the text fits in Label.</summary>
            public readonly List<Label> Overflow = new List<Label>();
        }

        /// <summary>USS class every continuation label carries in
        /// addition to the tracked label's own classes.</summary>
        public const string ContinuationClass = "uap-stream-cont";

        private readonly List<Entry> _entries = new List<Entry>();
        private bool _hooked;
        private double _lastFlush;
        private bool _keepDetachedForTests;

        /// <summary>
        /// Starts driving the label from the block's text. Re-tracking the
        /// same label replaces its previous entry.
        /// </summary>
        public void Track(ChatMessageBlock block, Label label)
        {
            if (block == null || label == null)
            {
                return;
            }
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Label == label)
                {
                    // The new entry re-chunks from scratch; the old
                    // continuation labels would otherwise linger beside
                    // the new ones and repeat their text.
                    RemoveOverflow(_entries[i]);
                    _entries.RemoveAt(i);
                }
            }
            string text = block.text ?? string.Empty;
            var entry = new Entry
            {
                Block = block,
                Label = label,
                ShownLength = text.Length,
                LastShown = null
            };
            SetShown(entry, IconLoader.SanitizeForDisplay(text) + IconLoader.GlyphCursor);
            _entries.Add(entry);
            Hook();
        }

        /// <summary>Drops all entries and unhooks from the editor update loop.</summary>
        public void Clear()
        {
            _entries.Clear();
            Unhook();
        }

        /// <summary>
        /// Stops driving labels but keeps every entry, so a later
        /// <see cref="Resume"/> picks the same streaming labels back up.
        /// Used when the chat view deactivates (view switch): the labels
        /// stay parented under the hidden view (display:none keeps
        /// panel != null), and Clear() here would freeze them at the
        /// switch-away text until the block finishes streaming.
        /// </summary>
        public void Suspend()
        {
            Unhook();
        }

        /// <summary>
        /// Resumes driving after <see cref="Suspend"/>. A no-op with no
        /// live entries; entries whose label was rebuilt while suspended
        /// are dropped by OnUpdate's panel check as usual.
        /// </summary>
        public void Resume()
        {
            if (_entries.Count > 0)
            {
                Hook();
            }
        }

        internal int TrackedEntryCountForTests => _entries.Count;
        internal bool IsDrivingForTests => _hooked;

        // -- Update loop -------------------------------------------------------

        private void Hook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            EditorApplication.update += OnUpdate;
        }

        private void Unhook()
        {
            if (!_hooked)
            {
                return;
            }
            _hooked = false;
            EditorApplication.update -= OnUpdate;
        }

        private void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastFlush < FlushIntervalSeconds)
            {
                return;
            }
            _lastFlush = now;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Label.panel == null && !_keepDetachedForTests)
                {
                    // The element was rebuilt or the view closed.
                    _entries.RemoveAt(i);
                    continue;
                }
                string target = entry.Block.text ?? string.Empty;
                if (!entry.Block.streaming)
                {
                    SetShown(entry, IconLoader.SanitizeForDisplay(target));
                    _entries.RemoveAt(i);
                    continue;
                }

                if (entry.ShownLength > target.Length)
                {
                    // Target shrank (block replaced); snap.
                    entry.ShownLength = target.Length;
                }
                else if (entry.ShownLength < target.Length)
                {
                    int remaining = target.Length - entry.ShownLength;
                    int step = Math.Max(MinAdvanceChars, remaining / 3);
                    entry.ShownLength = Math.Min(target.Length, entry.ShownLength + step);
                    // Never split a surrogate pair mid-character.
                    if (entry.ShownLength > 0 && entry.ShownLength < target.Length
                        && char.IsHighSurrogate(target[entry.ShownLength - 1]))
                    {
                        entry.ShownLength++;
                    }
                }

                // Display-only sanitize: variation selectors and other
                // font-uncovered emoji have no glyph in the editor fonts
                // (one console warning per draw -- the flood this pump
                // exists to fix). ShownLength indexes the RAW target, so
                // the sanitize never desynchronizes the advance; a target
                // that momentarily ends in a lone high surrogate (a
                // streaming delta split a 4-byte emoji exactly here) is
                // handled by SanitizeForDisplay itself, which holds that
                // trailing half back until the next flush completes it.
                string shown = IconLoader.SanitizeForDisplay(
                    target.Substring(0, entry.ShownLength)) + IconLoader.GlyphCursor;
                if (!string.Equals(shown, entry.LastShown, StringComparison.Ordinal))
                {
                    SetShown(entry, shown);
                    entry.LastShown = shown;
                }
            }

            if (_entries.Count == 0)
            {
                Unhook();
            }
        }

        // -- Chunked display ---------------------------------------------------

        /// <summary>
        /// Writes <paramref name="shown"/> across the tracked label and
        /// as many continuation labels as LongTextChunker needs. Chunk
        /// boundaries depend only on the text before them, so as the
        /// text grows every earlier chunk keeps its content and only the
        /// last label (and any newly needed one) changes; a shrinking
        /// target drops the labels it no longer needs. Continuation
        /// labels need a parent to live in: a tracked label that is not
        /// in a hierarchy (tests) keeps the whole text.
        /// </summary>
        private static void SetShown(Entry entry, string shown)
        {
            List<string> chunks = LongTextChunker.Split(shown);
            entry.Label.text = chunks[0];

            VisualElement parent = entry.Label.parent;
            if (parent == null)
            {
                if (chunks.Count > 1)
                {
                    entry.Label.text = shown;
                }
                return;
            }

            int needed = chunks.Count - 1;
            for (int i = entry.Overflow.Count - 1; i >= needed; i--)
            {
                entry.Overflow[i].RemoveFromHierarchy();
                entry.Overflow.RemoveAt(i);
            }
            for (int i = 0; i < needed; i++)
            {
                Label cont;
                if (i < entry.Overflow.Count)
                {
                    cont = entry.Overflow[i];
                    if (cont.parent != parent)
                    {
                        // The tracked label was re-parented (row rebuilt
                        // around it); follow it.
                        cont.RemoveFromHierarchy();
                        parent.Insert(parent.IndexOf(Previous(entry, i)) + 1, cont);
                    }
                }
                else
                {
                    cont = CreateContinuation(entry.Label);
                    parent.Insert(parent.IndexOf(Previous(entry, i)) + 1, cont);
                    entry.Overflow.Add(cont);
                }
                string chunk = chunks[i + 1];
                if (!string.Equals(cont.text, chunk, StringComparison.Ordinal))
                {
                    cont.text = chunk;
                }
            }
        }

        /// <summary>The label that chunk <paramref name="index"/> + 1
        /// follows: the tracked label for the first continuation, else
        /// the previous continuation.</summary>
        private static Label Previous(Entry entry, int index)
        {
            return index == 0 ? entry.Label : entry.Overflow[index - 1];
        }

        /// <summary>
        /// A continuation label that looks exactly like the tracked one
        /// (same USS classes, rich text off, escape parsing off as
        /// TextEscapes.Disable would have set on a build-time label) plus
        /// the <see cref="ContinuationClass"/> hook for seam styling.
        /// </summary>
        private static Label CreateContinuation(Label primary)
        {
            var cont = new Label();
            foreach (string cls in primary.GetClasses())
            {
                cont.AddToClassList(cls);
            }
            cont.AddToClassList(ContinuationClass);
            cont.enableRichText = false;
            cont.parseEscapeSequences = false;
            return cont;
        }

        /// <summary>Total labels (tracked + continuation) an entry for
        /// <paramref name="label"/> currently drives; 0 when untracked.</summary>
        internal int LabelCountForTests(Label label)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Label == label)
                {
                    return 1 + _entries[i].Overflow.Count;
                }
            }
            return 0;
        }

        private static void RemoveOverflow(Entry entry)
        {
            for (int i = 0; i < entry.Overflow.Count; i++)
            {
                entry.Overflow[i].RemoveFromHierarchy();
            }
            entry.Overflow.Clear();
        }

        /// <summary>
        /// Runs one flush now, ignoring the interval, and keeps entries
        /// whose label has no panel (tests build detached trees; in the
        /// editor a panel-less label means the row was rebuilt).
        /// </summary>
        internal void FlushNowForTests()
        {
            _keepDetachedForTests = true;
            try
            {
                _lastFlush = double.NegativeInfinity;
                OnUpdate();
            }
            finally
            {
                _keepDetachedForTests = false;
            }
        }
    }
}
