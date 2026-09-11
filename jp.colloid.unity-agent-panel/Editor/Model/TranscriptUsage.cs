using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Token usage reconstructed from a session transcript, so that
    /// restoring a session from History brings its numbers back instead of
    /// showing "0 tok" / "No completed turn yet"
    /// (docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
    /// section 1). Filled by <see cref="TranscriptLoader"/> during the scan
    /// it already performs over every line, so it costs no extra I/O.
    ///
    /// What CANNOT be reconstructed, measured across 18 real transcripts:
    ///
    ///   * COST. There is no cost field of any kind on disk --
    ///     result.total_cost_usd is stream-only. (Moot under subscription
    ///     auth, where the CLI reports 0 and the display is gated by
    ///     PanelSettings.showCostUsd anyway.)
    ///   * CONTEXT WINDOW. modelUsage.contextWindow is stream-only too, so
    ///     <see cref="LastTurnModelUsage"/> entries carry ContextWindow 0
    ///     and the status bar's context meter stays hidden until the next
    ///     turn completes -- which is already its documented "no data"
    ///     behaviour, not a new failure mode.
    ///   * SUBAGENT TOKENS. Subagent turns live in their own transcript
    ///     files and are not summed here. None of the sampled transcripts
    ///     spawned a subagent, so how far this diverges from a live
    ///     result.usage total is UNMEASURED -- it is not claimed to be zero.
    /// </summary>
    public sealed class TranscriptUsage
    {
        /// <summary>
        /// Model name the CLI writes on locally-generated placeholder
        /// assistant messages (isApiErrorMessage lines). Measured in a real
        /// transcript alongside the genuine model. These were never sent to
        /// the API, so they are excluded entirely -- counting them would put
        /// a meaningless "&lt;synthetic&gt;" row in the usage popover.
        /// </summary>
        internal const string SyntheticModelName = "<synthetic>";

        /// <summary>Session-cumulative input tokens (deduped, see <see cref="Accumulate"/>).</summary>
        public long TotalInputTokens;
        public long TotalOutputTokens;
        public long TotalCacheReadInputTokens;
        public long TotalCacheCreationInputTokens;

        /// <summary>
        /// Number of genuine user prompts in the transcript (tool_result
        /// lines, which the CLI also records with role "user", excluded).
        /// Restored into ChatSession.completedTurns.
        /// </summary>
        public int UserPromptCount;

        /// <summary>Last timestamp seen on any line; empty when none parsed.</summary>
        public string LastTimestampIso = string.Empty;

        /// <summary>
        /// Per-model usage for the LAST turn only -- the assistant messages
        /// that follow the final user prompt. This mirrors what
        /// AgentHub.LastModelUsage means (one turn, the context meter's
        /// source), NOT a session-cumulative total: feeding a cumulative
        /// figure to the context meter would read as a wildly overfull
        /// context the moment a contextWindow became available.
        /// </summary>
        public readonly Dictionary<string, ModelUsage> LastTurnModelUsage =
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

        /// <summary>
        /// message.id values already counted. One API response is written to
        /// the JSONL as SEVERAL lines (one per content block), each
        /// repeating the same usage object -- measured 23 assistant lines
        /// for 16 distinct ids in one file, and 101 for 68 in another, with
        /// ZERO ids whose repeated usage differed. Summing per line
        /// therefore roughly doubles every number (6679 vs 3147 output
        /// tokens in the sampled file), so the first line per id wins and
        /// the rest are skipped.
        /// </summary>
        private readonly HashSet<string> _countedMessageIds =
            new HashSet<string>(StringComparer.Ordinal);

        private int _idlessCounter;

        /// <summary>
        /// Clears the last-turn accumulator. Called when a genuine user
        /// prompt is seen, so what remains at end of file describes only
        /// the final turn.
        /// </summary>
        public void BeginTurn()
        {
            UserPromptCount++;
            LastTurnModelUsage.Clear();
        }

        /// <summary>
        /// Folds one assistant line's message.usage in, ignoring repeats of
        /// a message.id already counted and skipping synthetic models.
        /// Returns true when the line actually contributed.
        /// </summary>
        public bool Accumulate(JsonNode message)
        {
            if (message == null || !message.IsObject)
            {
                return false;
            }
            JsonNode usage = message["usage"];
            if (!usage.IsObject)
            {
                return false;
            }
            string model = message["model"].AsString(string.Empty);
            if (string.Equals(model, SyntheticModelName, StringComparison.Ordinal))
            {
                return false;
            }
            string id = message["id"].AsString(string.Empty);
            if (string.IsNullOrEmpty(id))
            {
                // No id to dedupe on: count it once under a key that can
                // never collide, rather than dropping real usage.
                id = "\0noid" + (++_idlessCounter).ToString();
            }
            if (!_countedMessageIds.Add(id))
            {
                return false;
            }

            long input = usage["input_tokens"].AsLong();
            long output = usage["output_tokens"].AsLong();
            long cacheRead = usage["cache_read_input_tokens"].AsLong();
            long cacheCreate = usage["cache_creation_input_tokens"].AsLong();

            TotalInputTokens += input;
            TotalOutputTokens += output;
            TotalCacheReadInputTokens += cacheRead;
            TotalCacheCreationInputTokens += cacheCreate;

            if (string.IsNullOrEmpty(model))
            {
                // Usage with no model attribution still belongs in the
                // session totals above, but there is no row to put it in.
                return true;
            }
            ModelUsage entry;
            if (!LastTurnModelUsage.TryGetValue(model, out entry))
            {
                entry = new ModelUsage();
                LastTurnModelUsage[model] = entry;
            }
            entry.InputTokens += input;
            entry.OutputTokens += output;
            entry.CacheReadInputTokens += cacheRead;
            entry.CacheCreationInputTokens += cacheCreate;
            // ContextWindow / CostUsd deliberately left at 0 -- see the
            // class doc comment; neither exists on disk.
            return true;
        }
    }
}
