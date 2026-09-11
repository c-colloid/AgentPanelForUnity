using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=user -- two roles on the wire:
    /// 1. Echo of a user message we sent (with --replay-user-messages the CLI
    ///    replays it with "isReplay":true) -- used as a delivery ack.
    /// 2. Tool results: message.content[] contains tool_result blocks that
    ///    close out a pending tool_use (card completion).
    /// Required: the "message" object.
    /// </summary>
    public sealed class UserEchoMessage : StreamJsonMessage
    {
        /// <summary>True when this line is the CLI echoing back our own input (ack).</summary>
        public bool IsReplay { get; private set; }
        public ContentBlock[] Content { get; private set; }
        public string ParentToolUseId { get; private set; }
        public string Timestamp { get; private set; }

        /// <summary>Convenience: true when any block is a tool_result.</summary>
        public bool HasToolResult
        {
            get
            {
                for (int i = 0; i < Content.Length; i++)
                {
                    if (Content[i].Type == ContentBlockType.ToolResult)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private UserEchoMessage()
        {
            Type = InboundType.User;
        }

        /// <summary>Maps a user line. Returns null (logged) when 'message' is missing.</summary>
        public static UserEchoMessage FromJson(JsonNode node, Action<string> logger)
        {
            JsonNode message = node["message"];
            if (!message.IsObject)
            {
                Log(logger, "Dropped user message: missing required field 'message'.");
                return null;
            }

            var msg = new UserEchoMessage();
            msg.ReadCommonFields(node);
            msg.IsReplay = node["isReplay"].AsBool(false);
            msg.Content = ContentBlock.ArrayFromJson(message["content"]);
            msg.ParentToolUseId = node["parent_tool_use_id"].AsString();
            msg.Timestamp = node["timestamp"].AsString();
            return msg;
        }
    }
}
