using System.Collections.Generic;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Editor-session expand/collapse memory for the collapsible pieces
    /// of a message that have NO id of their own: the thinking Foldout,
    /// the "N tools" group row and the context-attachment chip
    /// (MessageBlockFactory). MessageListController discards and rebuilds
    /// a whole message element whenever its structural signature changes
    /// -- every block appended (each tool call the model makes), every
    /// streaming -&gt; finalized flip, every tool status transition -- so
    /// a foldout whose open state lived only on the discarded
    /// VisualElement re-closed on almost every transcript update, which
    /// made reading a long tool result while the turn was still running
    /// impossible.
    ///
    /// ToolActivityCard and SubagentCard already solved the same problem
    /// for themselves with a per-class dictionary keyed by toolUseId;
    /// this is the same mechanism for elements keyed by
    /// <c>messageId/blockIndex</c> (see <see cref="BlockKey"/>). Blocks
    /// are only ever appended to a message, never inserted or removed,
    /// so an index-based key stays stable for the message's lifetime.
    /// Editor-session lifetime only, not persisted -- same limitation and
    /// same reasoning as SubagentCard.ExpandedByToolUseId.
    /// </summary>
    internal static class ExpandStateMemory
    {
        private static readonly Dictionary<string, bool> Expanded = new Dictionary<string, bool>();

        /// <summary>Key for the block at <paramref name="blockIndex"/> of
        /// the message/subagent identified by <paramref name="ownerId"/>.
        /// Returns null (no memory) when the owner has no id.</summary>
        public static string BlockKey(string ownerId, int blockIndex)
        {
            if (string.IsNullOrEmpty(ownerId))
            {
                return null;
            }
            return ownerId + "/" + blockIndex;
        }

        /// <summary>Records the state; a null key is ignored.</summary>
        public static void Set(string key, bool expanded)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            Expanded[key] = expanded;
        }

        /// <summary>Whether a state is on record for the key.</summary>
        public static bool TryGet(string key, out bool expanded)
        {
            expanded = false;
            return !string.IsNullOrEmpty(key) && Expanded.TryGetValue(key, out expanded);
        }

        /// <summary>Remembered state, or <paramref name="fallback"/> when
        /// nothing is on record.</summary>
        public static bool Get(string key, bool fallback)
        {
            bool expanded;
            return TryGet(key, out expanded) ? expanded : fallback;
        }

        /// <summary>Test seam: clears the memory so suites cannot leak
        /// state into each other through the static dictionary.</summary>
        internal static void ResetForTests()
        {
            Expanded.Clear();
        }
    }
}
