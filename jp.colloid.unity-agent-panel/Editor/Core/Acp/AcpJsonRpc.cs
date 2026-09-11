using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// One parsed JSON-RPC 2.0 line from an ACP agent (agent -> client).
    /// Exactly one of IsRequest / IsNotification / IsResponse is true.
    /// </summary>
    public sealed class AcpInbound
    {
        public JsonNode Raw;
        /// <summary>Request/response id as JSON (number or string); null-node for notifications.</summary>
        public JsonNode Id = JsonNode.Null;
        public string Method;
        public JsonNode Params = JsonNode.Null;
        public JsonNode Result = JsonNode.Null;
        public JsonNode Error = JsonNode.Null;

        public bool HasId
        {
            get { return Id != null && !Id.IsNull; }
        }

        public bool IsRequest
        {
            get { return Method != null && HasId; }
        }

        public bool IsNotification
        {
            get { return Method != null && !HasId; }
        }

        public bool IsResponse
        {
            get { return Method == null && HasId; }
        }

        public bool IsError
        {
            get { return Error != null && !Error.IsNull; }
        }

        /// <summary>Numeric id when the id is a number the bridge issued; -1 otherwise.</summary>
        public long NumericId
        {
            get { return HasId && Id.IsNumber ? Id.AsLong(-1) : -1; }
        }

        /// <summary>error.message, or a generic text when the error object carries none.</summary>
        public string ErrorMessage
        {
            get
            {
                if (!IsError)
                {
                    return null;
                }
                string message = Error["message"].AsString();
                if (string.IsNullOrEmpty(message))
                {
                    message = "JSON-RPC error";
                }
                long code = Error["code"].AsLong(0);
                JsonNode data = Error["data"];
                string detail = data != null && !data.IsNull
                    ? (data.IsString ? data.AsString(string.Empty) : JsonWriter.Write(data))
                    : null;
                string text = code != 0 ? message + " (code " + code + ")" : message;
                return string.IsNullOrEmpty(detail) ? text : text + ": " + detail;
            }
        }

        public long ErrorCode
        {
            get { return IsError ? Error["code"].AsLong(0) : 0; }
        }
    }

    /// <summary>
    /// Serializer/parser for the JSON-RPC 2.0 framing ACP uses (one JSON
    /// object per line over stdio). Pure, allocation-light, Unity-free;
    /// AcpProtocolBridge is the only consumer.
    /// </summary>
    public static class AcpJsonRpc
    {
        /// <summary>JSON-RPC "method not found" (the client refuses fs/terminal requests it never advertised).</summary>
        public const int MethodNotFound = -32601;

        /// <summary>ACP's "authentication required" error code on session/new.</summary>
        public const int AuthRequired = -32000;

        public static string Request(long id, string method, JsonNode parameters)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id)
                .Set("method", method);
            node.Set("params", parameters ?? JsonNode.NewObject());
            return JsonWriter.Write(node);
        }

        public static string Notification(string method, JsonNode parameters)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("method", method);
            node.Set("params", parameters ?? JsonNode.NewObject());
            return JsonWriter.Write(node);
        }

        /// <summary>Success response echoing the agent's request id verbatim (number or string).</summary>
        public static string Response(JsonNode id, JsonNode result)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? JsonNode.Null)
                .Set("result", result ?? JsonNode.Null);
            return JsonWriter.Write(node);
        }

        public static string ErrorResponse(JsonNode id, int code, string message)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? JsonNode.Null)
                .Set("error", JsonNode.NewObject()
                    .Set("code", code)
                    .Set("message", message ?? string.Empty));
            return JsonWriter.Write(node);
        }

        /// <summary>
        /// Parses one stdout line. Returns null for blank lines, non-JSON
        /// text (agents sometimes print banners to stdout) and JSON that is
        /// not a JSON-RPC object; never throws.
        /// </summary>
        public static AcpInbound TryParse(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                return null;
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(trimmed, out node, out error) || node == null || !node.IsObject)
            {
                return null;
            }
            var inbound = new AcpInbound();
            inbound.Raw = node;
            inbound.Id = node.HasKey("id") ? node["id"] : JsonNode.Null;
            inbound.Method = node["method"].AsString();
            inbound.Params = node["params"];
            inbound.Result = node["result"];
            inbound.Error = node["error"];
            if (inbound.Method == null && !inbound.HasId)
            {
                return null;
            }
            return inbound;
        }
    }
}
