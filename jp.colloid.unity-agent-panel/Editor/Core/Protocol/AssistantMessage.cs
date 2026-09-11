using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>Kind of a content block inside an assistant/user message.</summary>
    public enum ContentBlockType
    {
        Text,
        Thinking,
        /// <summary>
        /// A thinking block the model flagged as safety-sensitive: the API
        /// sends only encrypted data (no readable text), see
        /// docs/design-notes/2026-08-01-thinking-content-loss.md section 6.
        /// </summary>
        RedactedThinking,
        ToolUse,
        ToolResult,
        Unknown
    }

    /// <summary>
    /// One entry of message.content[]. Optional-first: only the fields that
    /// apply to the block type are populated; unknown block types are kept
    /// as ContentBlockType.Unknown (with Raw) and skipped by renderers.
    /// </summary>
    public sealed class ContentBlock
    {
        public ContentBlockType Type { get; private set; }
        /// <summary>Raw "type" string (useful for unknown blocks).</summary>
        public string RawType { get; private set; }
        /// <summary>Text for Text blocks.</summary>
        public string Text { get; private set; }
        /// <summary>Thinking text for Thinking blocks.</summary>
        public string Thinking { get; private set; }
        /// <summary>tool_use: block id ("toolu_...").</summary>
        public string Id { get; private set; }
        /// <summary>tool_use: tool name (e.g. "Bash").</summary>
        public string Name { get; private set; }
        /// <summary>tool_use: input object (raw JSON, tool-specific schema).</summary>
        public JsonNode Input { get; private set; }
        /// <summary>tool_result: id of the tool_use block this result answers.</summary>
        public string ToolUseId { get; private set; }
        /// <summary>tool_result: content (string or array of blocks; raw JSON).</summary>
        public JsonNode ResultContent { get; private set; }
        /// <summary>tool_result: is_error flag.</summary>
        public bool IsError { get; private set; }
        /// <summary>Full raw block node.</summary>
        public JsonNode Raw { get; private set; }

        public static ContentBlock FromJson(JsonNode node)
        {
            var block = new ContentBlock();
            block.Raw = node;
            block.RawType = node["type"].AsString();
            switch (block.RawType)
            {
                case "text":
                    block.Type = ContentBlockType.Text;
                    block.Text = node["text"].AsString(string.Empty);
                    break;
                case "thinking":
                    block.Type = ContentBlockType.Thinking;
                    block.Thinking = node["thinking"].AsString(string.Empty);
                    break;
                case "redacted_thinking":
                    // Spec: only an opaque encrypted "data" string, no
                    // readable text -- Thinking stays empty on purpose;
                    // Raw (set above) keeps the full node for forward-compat.
                    block.Type = ContentBlockType.RedactedThinking;
                    break;
                case "tool_use":
                    block.Type = ContentBlockType.ToolUse;
                    block.Id = node["id"].AsString();
                    block.Name = node["name"].AsString();
                    block.Input = node["input"];
                    break;
                case "tool_result":
                    block.Type = ContentBlockType.ToolResult;
                    block.ToolUseId = node["tool_use_id"].AsString();
                    block.ResultContent = node["content"];
                    block.IsError = node["is_error"].AsBool(false);
                    break;
                default:
                    block.Type = ContentBlockType.Unknown;
                    break;
            }
            return block;
        }

        /// <summary>Maps a message.content value; tolerates the plain-string form.</summary>
        public static ContentBlock[] ArrayFromJson(JsonNode content)
        {
            if (content.IsString)
            {
                // {"content":"plain string"} is an accepted user-message form.
                var single = JsonNode.NewObject()
                    .Set("type", "text")
                    .Set("text", content.AsString(string.Empty));
                return new[] { FromJson(single) };
            }
            var blocks = new List<ContentBlock>();
            foreach (var item in content.Items)
            {
                if (item.IsObject)
                {
                    blocks.Add(FromJson(item));
                }
            }
            return blocks.ToArray();
        }
    }

    /// <summary>
    /// Token usage attached to assistant messages and the final result.
    /// All fields default to zero when absent.
    /// </summary>
    public sealed class UsageInfo
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheCreationInputTokens;
        public long CacheReadInputTokens;
        public long WebSearchRequests;
        public long WebFetchRequests;
        public string ServiceTier;

        /// <summary>
        /// Four-field token total of the LAST entry of the result's
        /// "iterations" array, or -1 when the payload carries none.
        ///
        /// This is the only field on the wire that means "how full is the
        /// context window RIGHT NOW". Everything else in a result payload
        /// is CUMULATIVE BILLING over every API call the turn made: the
        /// enclosing UsageInfo sums each iteration's cache re-read of the
        /// same context, and modelUsage additionally folds in every
        /// subagent. Measured across the five real result captures in
        /// Tests/Editor/Fixtures: the last iteration reads ~28k while the
        /// enclosing usage reads ~56k (2x) and, with one subagent,
        /// modelUsage reads 97,886 (3.46x) -- for a context that never
        /// exceeded 28,299.
        ///
        /// -1, not 0, marks "absent": a genuinely empty context is 0 and
        /// callers must not confuse the two.
        /// </summary>
        public long LastIterationContextTokens = -1;

        public static UsageInfo FromJson(JsonNode node)
        {
            var usage = new UsageInfo();
            usage.LastIterationContextTokens = ReadLastIterationContextTokens(node["iterations"]);
            usage.InputTokens = node["input_tokens"].AsLong();
            usage.OutputTokens = node["output_tokens"].AsLong();
            usage.CacheCreationInputTokens = node["cache_creation_input_tokens"].AsLong();
            usage.CacheReadInputTokens = node["cache_read_input_tokens"].AsLong();
            usage.WebSearchRequests = node["server_tool_use"]["web_search_requests"].AsLong();
            usage.WebFetchRequests = node["server_tool_use"]["web_fetch_requests"].AsLong();
            usage.ServiceTier = node["service_tier"].AsString();
            return usage;
        }

        /// <summary>
        /// The LAST iteration is deliberate: context grows within a turn
        /// and the most recent entry is the current state. MEASURED CAVEAT:
        /// all five captured results carry exactly ONE iteration (while
        /// their enclosing usage totals show the turn made two API calls),
        /// so the array is not a complete per-call log and its ordering
        /// could not be confirmed from captures. With one element both
        /// "last" and "only" agree; if a future capture shows several,
        /// "last" remains the honest reading of "now".
        /// </summary>
        private static long ReadLastIterationContextTokens(JsonNode iterations)
        {
            if (iterations == null || !iterations.IsArray)
            {
                return -1;
            }
            JsonNode last = null;
            foreach (JsonNode item in iterations.Items)
            {
                if (item != null && item.IsObject)
                {
                    last = item;
                }
            }
            if (last == null)
            {
                return -1;
            }
            return last["input_tokens"].AsLong()
                + last["output_tokens"].AsLong()
                + last["cache_read_input_tokens"].AsLong()
                + last["cache_creation_input_tokens"].AsLong();
        }
    }

    /// <summary>
    /// type=assistant -- arrives multiple times per turn. The nested "message"
    /// object is the Anthropic Messages API response shape.
    /// Required: the "message" object itself. Notable signals:
    /// - Model == "&lt;synthetic&gt;" marks a CLI-internal (error) response.
    /// - Error is a top-level field (e.g. "authentication_failed").
    /// - ParentToolUseId non-null marks a subagent-originated message.
    /// </summary>
    public sealed class AssistantMessage : StreamJsonMessage
    {
        public string MessageId { get; private set; }
        public string Model { get; private set; }
        public string StopReason { get; private set; }
        public ContentBlock[] Content { get; private set; }
        public UsageInfo Usage { get; private set; }
        public string ParentToolUseId { get; private set; }
        /// <summary>Top-level error marker, e.g. "authentication_failed". Null when absent.</summary>
        public string Error { get; private set; }
        public string Timestamp { get; private set; }

        /// <summary>True when this is a CLI-synthesized message (not from the model).</summary>
        public bool IsSynthetic
        {
            get { return Model == "<synthetic>"; }
        }

        private AssistantMessage()
        {
            Type = InboundType.Assistant;
        }

        /// <summary>Maps an assistant line. Returns null (logged) when 'message' is missing.</summary>
        public static AssistantMessage FromJson(JsonNode node, Action<string> logger)
        {
            JsonNode message = node["message"];
            if (!message.IsObject)
            {
                Log(logger, "Dropped assistant message: missing required field 'message'.");
                return null;
            }

            var msg = new AssistantMessage();
            msg.ReadCommonFields(node);
            msg.MessageId = message["id"].AsString();
            msg.Model = message["model"].AsString();
            msg.StopReason = message["stop_reason"].AsString();
            msg.Content = ContentBlock.ArrayFromJson(message["content"]);
            msg.Usage = UsageInfo.FromJson(message["usage"]);
            msg.ParentToolUseId = node["parent_tool_use_id"].AsString();
            msg.Error = node["error"].AsString();
            msg.Timestamp = node["timestamp"].AsString();
            return msg;
        }
    }
}
