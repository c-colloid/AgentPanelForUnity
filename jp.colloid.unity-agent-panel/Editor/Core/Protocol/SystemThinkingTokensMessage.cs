using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=system, subtype=thinking_tokens -- fires while a thinking block
    /// is streaming. Carries ONLY a running token estimate: the CLI's
    /// headless stream-json never sends thinking body text (design note
    /// docs/design-notes/2026-08-01-thinking-content-loss.md section 4 --
    /// every captured thinking_delta/finalized thinking block is
    /// empty-text + signature only).
    ///
    /// Protocol choice (design note section 5 point 1): the panel needs a
    /// cumulative token estimate on AgentHub's streaming Thinking block.
    /// Two wire events carry one: stream_event's thinking_delta also has an
    /// "estimated_tokens" field, but this system/thinking_tokens event is
    /// the leaner, more complete source -- it is explicitly cumulative
    /// (estimated_tokens) AND carries the incremental delta
    /// (estimated_tokens_delta) in one shape, and the real captures show it
    /// firing MORE often than thinking_delta: Tests/Editor/Fixtures/
    /// task_subagent_inbound.jsonl lines 8-10 show two thinking_tokens
    /// events (50, then 143) bracketing a single thinking_delta (which only
    /// ever reported 50). Wiring thinking_delta's own field as well would
    /// have been redundant and, per that capture, less complete -- so it is
    /// intentionally left alone here.
    /// </summary>
    public sealed class SystemThinkingTokensMessage : StreamJsonMessage
    {
        /// <summary>Cumulative estimated thinking tokens for the current block.</summary>
        public long EstimatedTokens { get; private set; }
        /// <summary>Delta vs. the previous thinking_tokens event.</summary>
        public long EstimatedTokensDelta { get; private set; }

        private SystemThinkingTokensMessage()
        {
            Type = InboundType.SystemThinkingTokens;
        }

        /// <summary>Maps a system/thinking_tokens line. Never drops (no required field).</summary>
        public static SystemThinkingTokensMessage FromJson(JsonNode node, Action<string> logger)
        {
            var msg = new SystemThinkingTokensMessage();
            msg.ReadCommonFields(node);
            msg.EstimatedTokens = node["estimated_tokens"].AsLong();
            msg.EstimatedTokensDelta = node["estimated_tokens_delta"].AsLong();
            return msg;
        }
    }
}
