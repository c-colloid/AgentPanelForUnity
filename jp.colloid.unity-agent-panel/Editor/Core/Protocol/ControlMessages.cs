using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=control_request (CLI to panel). The primary subtype is
    /// "can_use_tool" (permission prompt via --permission-prompt-tool stdio).
    /// Other observed/binary-confirmed subtypes (hook_callback,
    /// request_user_dialog, mcp_message, ...) are v1-ignored: the message
    /// still maps (so the caller can log it) but CanUseTool stays null.
    /// Required: request_id and request.subtype.
    /// </summary>
    public sealed class ControlRequestMessage : StreamJsonMessage
    {
        public string RequestId { get; private set; }
        public string Subtype { get; private set; }
        /// <summary>Populated only when Subtype == "can_use_tool".</summary>
        public CanUseToolRequest CanUseTool { get; private set; }
        /// <summary>The raw "request" object (for unhandled subtypes).</summary>
        public JsonNode Request { get; private set; }

        public bool IsCanUseTool
        {
            get { return Subtype == "can_use_tool"; }
        }

        private ControlRequestMessage()
        {
            Type = InboundType.ControlRequest;
        }

        public static ControlRequestMessage FromJson(JsonNode node, Action<string> logger)
        {
            string requestId = node["request_id"].AsString();
            if (string.IsNullOrEmpty(requestId))
            {
                Log(logger, "Dropped control_request: missing required field 'request_id'.");
                return null;
            }
            JsonNode request = node["request"];
            string subtype = request["subtype"].AsString();
            if (string.IsNullOrEmpty(subtype))
            {
                Log(logger, "Dropped control_request '" + requestId
                    + "': missing required field 'request.subtype'.");
                return null;
            }

            var msg = new ControlRequestMessage();
            msg.ReadCommonFields(node);
            msg.RequestId = requestId;
            msg.Subtype = subtype;
            msg.Request = request;
            if (subtype == "can_use_tool")
            {
                msg.CanUseTool = CanUseToolRequest.FromJson(request);
            }
            return msg;
        }
    }

    /// <summary>
    /// type=control_response (CLI to panel) -- answers our control_requests
    /// (initialize, interrupt, set_permission_mode, set_model). Wire shape:
    /// {"type":"control_response","response":{"subtype":"success"|"error",
    ///   "request_id":"...","response":{...} | "error":"..."}}.
    /// Required: response.request_id (needed for correlation).
    /// </summary>
    public sealed class ControlResponseMessage : StreamJsonMessage
    {
        /// <summary>Correlates with the request_id we sent.</summary>
        public string RequestId { get; private set; }
        /// <summary>True when response.subtype == "success".</summary>
        public bool Success { get; private set; }
        /// <summary>
        /// The inner payload object. For initialize this contains
        /// commands[], agents[], models[], account{}, output_style,
        /// available_output_styles[], pid; for interrupt: still_queued[].
        /// Null-node when absent.
        /// </summary>
        public JsonNode Response { get; private set; }
        /// <summary>Error text when Success is false. Null otherwise.</summary>
        public string Error { get; private set; }

        private ControlResponseMessage()
        {
            Type = InboundType.ControlResponse;
        }

        public static ControlResponseMessage FromJson(JsonNode node, Action<string> logger)
        {
            JsonNode envelope = node["response"];
            string requestId = envelope["request_id"].AsString();
            if (string.IsNullOrEmpty(requestId))
            {
                Log(logger, "Dropped control_response: missing required field 'response.request_id'.");
                return null;
            }

            var msg = new ControlResponseMessage();
            msg.ReadCommonFields(node);
            msg.RequestId = requestId;
            msg.Success = envelope["subtype"].AsString() == "success";
            msg.Response = envelope["response"];
            msg.Error = envelope["error"].AsString();
            return msg;
        }
    }
}
