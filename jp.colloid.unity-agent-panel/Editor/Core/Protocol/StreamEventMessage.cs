using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=stream_event -- emitted with --include-partial-messages. Wraps a
    /// raw Anthropic SSE event: {"type":"stream_event","event":{...}}.
    /// Only content_block_delta text/thinking deltas are extracted; all other
    /// event types (message_start, content_block_start, ...) are carried raw
    /// and ignored by the typewriter.
    /// Required: the "event" object.
    /// </summary>
    public sealed class StreamEventMessage : StreamJsonMessage
    {
        /// <summary>The raw SSE event object.</summary>
        public JsonNode Event { get; private set; }

        /// <summary>event.type, e.g. "content_block_delta".</summary>
        public string EventType { get; private set; }

        /// <summary>event.index (content block index) or -1 when absent.</summary>
        public int BlockIndex { get; private set; }

        public string ParentToolUseId { get; private set; }

        private StreamEventMessage()
        {
            Type = InboundType.StreamEvent;
        }

        /// <summary>Maps a stream_event line. Returns null (logged) when 'event' is missing.</summary>
        public static StreamEventMessage FromJson(JsonNode node, Action<string> logger)
        {
            JsonNode evt = node["event"];
            if (!evt.IsObject)
            {
                Log(logger, "Dropped stream_event message: missing required field 'event'.");
                return null;
            }

            var msg = new StreamEventMessage();
            msg.ReadCommonFields(node);
            msg.Event = evt;
            msg.EventType = evt["type"].AsString();
            msg.BlockIndex = evt["index"].AsInt(-1);
            msg.ParentToolUseId = node["parent_tool_use_id"].AsString();
            return msg;
        }

        /// <summary>
        /// Extracts a text delta from a content_block_delta event
        /// (delta.type == "text_delta"). Returns false for every other event.
        /// </summary>
        public bool TryGetTextDelta(out string text)
        {
            text = null;
            if (EventType != "content_block_delta")
            {
                return false;
            }
            JsonNode delta = Event["delta"];
            if (delta["type"].AsString() != "text_delta")
            {
                return false;
            }
            text = delta["text"].AsString(string.Empty);
            return true;
        }

        /// <summary>
        /// Extracts a thinking delta from a content_block_delta event
        /// (delta.type == "thinking_delta"). Returns false otherwise.
        /// </summary>
        public bool TryGetThinkingDelta(out string thinking)
        {
            thinking = null;
            if (EventType != "content_block_delta")
            {
                return false;
            }
            JsonNode delta = Event["delta"];
            if (delta["type"].AsString() != "thinking_delta")
            {
                return false;
            }
            thinking = delta["thinking"].AsString(string.Empty);
            return true;
        }

        /// <summary>
        /// True for a content_block_start event whose content_block.type is
        /// "thinking" -- the earliest possible signal that a thinking block
        /// has begun (design note 2026-08-01-thinking-content-loss.md
        /// section 5 point 2), ahead of either a thinking_delta or a
        /// system/thinking_tokens event. Used to create the streaming
        /// Thinking block right away instead of waiting on those, since the
        /// content_block itself is always empty text + empty signature on
        /// the wire (nothing to extract, only a presence signal).
        /// </summary>
        public bool IsThinkingBlockStart()
        {
            if (EventType != "content_block_start")
            {
                return false;
            }
            return Event["content_block"]["type"].AsString() == "thinking";
        }
    }
}
