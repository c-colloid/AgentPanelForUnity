using System.Collections.Generic;
using System.Text;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Pure formatting for context chips attached to an outgoing user
    /// message (R05 section 4.1). The chips serialize into a clearly
    /// delimited block appended AFTER the user text so the CLI sees the
    /// user's own words first and the editor-provided context second.
    /// No Unity API usage: EditMode-testable without an editor session.
    /// </summary>
    public static class ContextBlockFormatter
    {
        /// <summary>Opens the appended context section.</summary>
        public const string Header = "===== ATTACHED UNITY EDITOR CONTEXT =====";

        /// <summary>Closes the appended context section.</summary>
        public const string Footer = "===== END OF UNITY EDITOR CONTEXT =====";

        /// <summary>Per-block size cap (2 KB) before truncation.</summary>
        public const int MaxBlockChars = 2048;

        /// <summary>Marker appended to a block cut at MaxBlockChars.</summary>
        public const string TruncationSuffix = "\n... (truncated)";

        /// <summary>
        /// Composes the wire text: the user text alone when there are no
        /// context blocks, otherwise the user text followed by a delimited
        /// section containing every non-empty block (each individually
        /// truncated to MaxBlockChars).
        /// </summary>
        public static string Compose(string userText, IList<string> contextBlocks)
        {
            string text = userText ?? string.Empty;
            if (contextBlocks == null || contextBlocks.Count == 0)
            {
                return text;
            }
            var kept = new List<string>();
            for (int i = 0; i < contextBlocks.Count; i++)
            {
                string block = contextBlocks[i];
                if (!string.IsNullOrEmpty(block))
                {
                    kept.Add(TruncateBlock(block, MaxBlockChars));
                }
            }
            if (kept.Count == 0)
            {
                return text;
            }

            var sb = new StringBuilder(text.Length + 64 + kept.Count * 128);
            sb.Append(text);
            sb.Append("\n\n").Append(Header);
            for (int i = 0; i < kept.Count; i++)
            {
                sb.Append("\n\n[").Append(i + 1).Append("] ").Append(kept[i]);
            }
            sb.Append("\n\n").Append(Footer);
            return sb.ToString();
        }

        /// <summary>
        /// Cuts the block to maxChars (plus the truncation marker) when it
        /// is longer. Blocks at or under the cap pass through unchanged.
        /// </summary>
        public static string TruncateBlock(string block, int maxChars)
        {
            if (string.IsNullOrEmpty(block) || block.Length <= maxChars)
            {
                return block ?? string.Empty;
            }
            return block.Substring(0, maxChars) + TruncationSuffix;
        }
    }
}
