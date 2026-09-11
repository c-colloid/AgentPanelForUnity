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
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private bool _hooked;
        private double _lastFlush;

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
            label.text = IconLoader.SanitizeForDisplay(text) + IconLoader.GlyphCursor;
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
                if (entry.Label.panel == null)
                {
                    // The element was rebuilt or the view closed.
                    _entries.RemoveAt(i);
                    continue;
                }
                string target = entry.Block.text ?? string.Empty;
                if (!entry.Block.streaming)
                {
                    entry.Label.text = IconLoader.SanitizeForDisplay(target);
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
                    entry.Label.text = shown;
                    entry.LastShown = shown;
                }
            }

            if (_entries.Count == 0)
            {
                Unhook();
            }
        }
    }
}
