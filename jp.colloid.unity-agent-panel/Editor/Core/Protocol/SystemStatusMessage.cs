using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=system, subtype=status -- the CLI's coarse "what am I doing
    /// right now" signal (Agent SDK SDKStatusMessage). Wire shape:
    /// {"type":"system","subtype":"status","status":"compacting"|"requesting"|null,
    ///  "session_id":"...","uuid":"..."}.
    /// The one value the panel acts on is "compacting" (design note
    /// docs/design-notes/2026-09-10-compacting-indicator.md): it is the
    /// ONLY thing the CLI emits between the start of a compaction and its
    /// system/compact_boundary, and a compaction is one long
    /// summarization call with no stream_event traffic -- without this
    /// signal the panel looks frozen for its whole duration. Other values
    /// ("requesting" is emitted right before each API call in the real
    /// captures, Tests/Editor/Fixtures/success_bidi_inbound.jsonl line 3)
    /// and null map fine and simply read as "not compacting". Nothing is
    /// required: a bare {"type":"system","subtype":"status"} maps with an
    /// empty status.
    /// </summary>
    public sealed class SystemStatusMessage : StreamJsonMessage
    {
        public const string StatusCompacting = "compacting";

        /// <summary>The raw status value; empty when absent or JSON null.</summary>
        public string Status { get; private set; }

        /// <summary>True while the CLI is summarizing the conversation (manual /compact or auto).</summary>
        public bool IsCompacting
        {
            get { return string.Equals(Status, StatusCompacting, StringComparison.Ordinal); }
        }

        private SystemStatusMessage()
        {
            Type = InboundType.SystemStatus;
        }

        /// <summary>Maps a system/status line. Never drops (no required field).</summary>
        public static SystemStatusMessage FromJson(JsonNode node, Action<string> logger)
        {
            var msg = new SystemStatusMessage();
            msg.ReadCommonFields(node);
            msg.Status = node["status"].AsString(string.Empty) ?? string.Empty;
            return msg;
        }
    }
}
