using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=system, subtype=compact_boundary -- the CLI has just replaced
    /// the conversation so far with a summary (design note
    /// docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 2). Emitted once per compaction, either because the user
    /// sent "/compact" (trigger "manual") or because the context window
    /// filled up mid-turn (trigger "auto"). Wire shape (Agent SDK
    /// documentation, CLI v2.1.218):
    /// {"type":"system","subtype":"compact_boundary",
    ///  "compact_metadata":{"trigger":"manual"|"auto","pre_tokens":12345},
    ///  "session_id":"...","uuid":"..."}.
    /// The on-disk transcript writes the same fact in camelCase
    /// ("compactMetadata":{"trigger","preTokens"}); the mapper accepts
    /// both spellings so TranscriptLoader can share it. Nothing here is
    /// required: a bare {"type":"system","subtype":"compact_boundary"}
    /// still maps (unknown trigger, PreTokens -1) -- the boundary itself
    /// is the fact the panel must react to, the metadata only decorates it.
    /// </summary>
    public sealed class SystemCompactBoundaryMessage : StreamJsonMessage
    {
        public const string TriggerManual = "manual";
        public const string TriggerAuto = "auto";

        /// <summary>"manual" | "auto" (observed values), or empty when absent.</summary>
        public string Trigger { get; private set; }

        /// <summary>Context size in tokens right before the compaction, or -1 when not reported.</summary>
        public long PreTokens { get; private set; }

        /// <summary>True when the compaction was requested by the user ("/compact").</summary>
        public bool IsManual
        {
            get { return string.Equals(Trigger, TriggerManual, StringComparison.Ordinal); }
        }

        private SystemCompactBoundaryMessage()
        {
            Type = InboundType.SystemCompactBoundary;
        }

        public static SystemCompactBoundaryMessage FromJson(JsonNode node, Action<string> logger)
        {
            var msg = new SystemCompactBoundaryMessage();
            msg.ReadCommonFields(node);
            string trigger;
            long preTokens;
            ReadMetadata(node, out trigger, out preTokens);
            msg.Trigger = trigger;
            msg.PreTokens = preTokens;
            return msg;
        }

        /// <summary>
        /// Reads the compaction metadata off a compact_boundary line in
        /// either spelling: the live wire's snake_case
        /// compact_metadata.pre_tokens or the on-disk transcript's
        /// camelCase compactMetadata.preTokens. Shared with
        /// TranscriptLoader so the two never drift. trigger is empty and
        /// preTokens is -1 when the field is absent or not a number.
        /// </summary>
        public static void ReadMetadata(JsonNode node, out string trigger, out long preTokens)
        {
            trigger = string.Empty;
            preTokens = -1;
            if (node == null || !node.IsObject)
            {
                return;
            }
            JsonNode meta = node["compact_metadata"];
            if (!meta.IsObject)
            {
                meta = node["compactMetadata"];
            }
            if (!meta.IsObject)
            {
                return;
            }
            trigger = meta["trigger"].AsString(string.Empty) ?? string.Empty;
            JsonNode tokens = meta.HasKey("pre_tokens") ? meta["pre_tokens"] : meta["preTokens"];
            if (tokens.IsNumber)
            {
                preTokens = tokens.AsLong(-1);
            }
        }
    }
}
