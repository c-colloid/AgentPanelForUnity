using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Display cache for one chat session: session_id, derived title,
    /// message list and cumulative usage/cost. The canonical transcript is
    /// the CLI's own JSONL (ARCHITECTURE.md D5); this object only feeds the
    /// panel UI and survives editor restarts via SessionCacheFile (plain
    /// JSON; never Unity-serialized to the State.asset -- UnityYAML
    /// corrupts assets that embed raw message text).
    /// </summary>
    [Serializable]
    public class ChatSession
    {
        private const int TitleMaxLength = 48;

        /// <summary>CLI session uuid (empty until system/init arrives).</summary>
        public string sessionId = string.Empty;
        /// <summary>
        /// The AgentBackend (as int) that issued <see cref="sessionId"/>,
        /// or -1 when unknown (caches written before this field existed).
        /// A session id only means something to the agent that created
        /// it, so AgentHub.StartClient refuses to resume it on another
        /// backend (design note 2026-09-10-backend-switch-session.md).
        /// </summary>
        public int agentBackend = -1;
        /// <summary>Derived from the first user text; empty = untitled.</summary>
        public string title = string.Empty;
        public List<ChatMessage> messages = new List<ChatMessage>();

        // Cumulative usage across turns (incremented per result).
        public long totalInputTokens;
        public long totalOutputTokens;
        public long totalCacheReadInputTokens;
        public long totalCacheCreationInputTokens;
        /// <summary>Cost as reported by the latest result (0/absent on subscription auth).</summary>
        public double totalCostUsd;
        /// <summary>Number of completed turns.</summary>
        public int completedTurns;
        /// <summary>ISO-8601 of the last activity (list ordering in Phase 3).</summary>
        public string lastActivityTimestamp = string.Empty;

        /// <summary>Appends a message and returns it.</summary>
        public ChatMessage AddMessage(ChatMessage message)
        {
            if (message != null)
            {
                messages.Add(message);
            }
            return message;
        }

        /// <summary>The last message, or null when empty.</summary>
        public ChatMessage LastMessage()
        {
            return messages.Count > 0 ? messages[messages.Count - 1] : null;
        }

        /// <summary>
        /// Sets the title from the first user text when still untitled:
        /// first line, trimmed, capped at 48 chars with an ASCII ellipsis.
        /// </summary>
        public void EnsureTitleFrom(string firstUserText)
        {
            if (!string.IsNullOrEmpty(title) || string.IsNullOrEmpty(firstUserText))
            {
                return;
            }
            string line = firstUserText;
            int newline = line.IndexOfAny(new[] { '\r', '\n' });
            if (newline >= 0)
            {
                line = line.Substring(0, newline);
            }
            line = line.Trim();
            if (line.Length > TitleMaxLength)
            {
                line = line.Substring(0, TitleMaxLength - 3) + "...";
            }
            title = line;
        }

        /// <summary>Accumulates usage numbers from a finished turn.</summary>
        public void AccumulateTurn(long inputTokens, long outputTokens,
            long cacheReadTokens, long cacheCreationTokens, double reportedTotalCostUsd)
        {
            totalInputTokens += inputTokens;
            totalOutputTokens += outputTokens;
            totalCacheReadInputTokens += cacheReadTokens;
            totalCacheCreationInputTokens += cacheCreationTokens;
            if (reportedTotalCostUsd > 0)
            {
                // result.total_cost_usd is the CLI's own running total for
                // the session, so overwrite instead of summing -- but
                // MONOTONICALLY (MODEL-13): a --resume spawns a fresh CLI
                // process whose running total restarts from zero, so its
                // first reports are smaller than what this session already
                // showed. A plain overwrite made the displayed total go
                // BACKWARDS on resume. Max keeps it non-decreasing; the
                // accepted trade (documented, measured judgment) is that
                // after a resume the display under-counts until the new
                // process's own total passes the old high-water mark.
                totalCostUsd = Math.Max(totalCostUsd, reportedTotalCostUsd);
            }
            completedTurns++;
            lastActivityTimestamp = DateTime.UtcNow.ToString("o");
        }
    }
}
