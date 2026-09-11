using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>Top-level discriminator for inbound stream-json lines.</summary>
    public enum InboundType
    {
        SystemInit,
        System,
        /// <summary>system, subtype in {task_started, task_progress, task_updated,
        /// task_notification} -- subagent lifecycle events (R02c, Phase 4).</summary>
        SystemTaskEvent,
        /// <summary>system, subtype=thinking_tokens -- running thinking-token
        /// estimate while a thinking block streams (design note
        /// 2026-08-01-thinking-content-loss.md section 5).</summary>
        SystemThinkingTokens,
        /// <summary>system, subtype=compact_boundary -- the CLI replaced the
        /// conversation so far with a summary (manual /compact or auto).</summary>
        SystemCompactBoundary,
        /// <summary>system, subtype=status -- the CLI's coarse activity
        /// signal; "compacting" is the value the panel reacts to.</summary>
        SystemStatus,
        Assistant,
        User,
        Result,
        StreamEvent,
        ControlRequest,
        ControlResponse,
        Unknown
    }

    /// <summary>
    /// Base class for all inbound stream-json messages plus the single
    /// dispatch entry point. Contract (forward compatibility first):
    /// - Unknown top-level "type" values map to <see cref="UnknownMessage"/>
    ///   (InboundType.Unknown) and are never an error.
    /// - Known "system" messages with a subtype other than "init",
    ///   task_started/task_progress/task_updated/task_notification,
    ///   thinking_tokens, compact_boundary or status map to
    ///   InboundType.System (raw node preserved) and are ignored by
    ///   callers; those eight subtypes map to their own typed classes
    ///   instead.
    /// - Mappers read fields optional-first: absent fields become defaults.
    /// - A missing REQUIRED field drops the message: the dispatcher logs a
    ///   reason through the injected logger and returns null. It never throws,
    ///   so one bad line cannot stop the stream pump.
    /// </summary>
    public abstract class StreamJsonMessage
    {
        /// <summary>Message kind (set by the concrete mapper).</summary>
        public InboundType Type { get; protected set; }

        /// <summary>The full parsed line for diagnostics and forward-compat probing.</summary>
        public JsonNode Raw { get; protected set; }

        /// <summary>session_id when present on the line (system/assistant/user/result).</summary>
        public string SessionId { get; protected set; }

        /// <summary>Line-level uuid when present.</summary>
        public string Uuid { get; protected set; }

        protected void ReadCommonFields(JsonNode node)
        {
            Raw = node;
            SessionId = node["session_id"].AsString();
            Uuid = node["uuid"].AsString();
        }

        /// <summary>
        /// Parses one stdout line and dispatches it to a typed message.
        /// Returns null when the line is dropped (malformed JSON or a missing
        /// required field); the reason is reported via <paramref name="logger"/>.
        /// Never throws.
        /// </summary>
        public static StreamJsonMessage ParseLine(string line, Action<string> logger)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            JsonNode node;
            string error;
            if (!JsonParser.TryParse(line, out node, out error))
            {
                Log(logger, "Dropped unparseable stream-json line: " + error);
                return null;
            }
            return FromNode(node, logger);
        }

        /// <summary>Dispatches an already-parsed line. Returns null when dropped. Never throws.</summary>
        public static StreamJsonMessage FromNode(JsonNode node, Action<string> logger)
        {
            if (node == null || !node.IsObject)
            {
                Log(logger, "Dropped stream-json line: top-level value is not an object.");
                return null;
            }

            string type = node["type"].AsString();
            if (type == null)
            {
                Log(logger, "Dropped stream-json line: missing required field 'type'.");
                return null;
            }

            switch (type)
            {
                case "system":
                    {
                        string subtype = node["subtype"].AsString();
                        if (subtype == "init")
                        {
                            return SystemInitMessage.FromJson(node, logger);
                        }
                        if (subtype == "task_started" || subtype == "task_progress"
                            || subtype == "task_updated" || subtype == "task_notification")
                        {
                            return SystemTaskEventMessage.FromJson(node, logger);
                        }
                        if (subtype == "thinking_tokens")
                        {
                            return SystemThinkingTokensMessage.FromJson(node, logger);
                        }
                        if (subtype == "compact_boundary")
                        {
                            return SystemCompactBoundaryMessage.FromJson(node, logger);
                        }
                        if (subtype == "status")
                        {
                            return SystemStatusMessage.FromJson(node, logger);
                        }
                        // Known type, unhandled subtype (informational, ...):
                        // keep the raw node, callers ignore InboundType.System.
                        return new UnknownMessage(InboundType.System, node);
                    }
                case "assistant":
                    return AssistantMessage.FromJson(node, logger);
                case "user":
                    return UserEchoMessage.FromJson(node, logger);
                case "result":
                    return ResultMessage.FromJson(node, logger);
                case "stream_event":
                    return StreamEventMessage.FromJson(node, logger);
                case "control_request":
                    return ControlRequestMessage.FromJson(node, logger);
                case "control_response":
                    return ControlResponseMessage.FromJson(node, logger);
                default:
                    // Forward compatibility: unknown types are silently ignored.
                    return new UnknownMessage(InboundType.Unknown, node);
            }
        }

        protected static void Log(Action<string> logger, string message)
        {
            if (logger != null)
            {
                logger(message);
            }
        }
    }

    /// <summary>
    /// Placeholder for lines the panel does not act on: unknown top-level
    /// types (InboundType.Unknown) and unhandled system subtypes
    /// (InboundType.System). The raw node stays available for logging.
    /// </summary>
    public sealed class UnknownMessage : StreamJsonMessage
    {
        public string RawType { get; private set; }
        public string RawSubtype { get; private set; }

        public UnknownMessage(InboundType type, JsonNode node)
        {
            Type = type;
            ReadCommonFields(node);
            RawType = node["type"].AsString();
            RawSubtype = node["subtype"].AsString();
        }
    }
}
