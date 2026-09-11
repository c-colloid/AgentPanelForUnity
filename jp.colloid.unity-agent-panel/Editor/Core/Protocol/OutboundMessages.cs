using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// The ONLY factory for CLI stdin payloads. Every method returns a
    /// serialized single-line JSON string (no trailing newline) produced by
    /// JsonWriter; the process layer appends "\n" and writes UTF-8 bytes to
    /// StandardInput.BaseStream.
    ///
    /// There is deliberately no raw-string passthrough API: one malformed
    /// stdin line kills the CLI (exit 1, no output), so arbitrary text must
    /// never reach the pipe. Do not add methods that accept pre-serialized
    /// JSON strings.
    /// </summary>
    public static class OutboundMessages
    {
        // ---------------------------------------------------------------
        // (1) User messages -- array content form (always used):
        // {"type":"user","message":{"role":"user","content":[{"type":"text","text":"..."}]}}
        // ---------------------------------------------------------------

        /// <summary>Builds a user text message. The text may contain any Unicode including newlines.</summary>
        public static string UserText(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }
            return UserContent(text, null);
        }

        /// <summary>One image content block: media type + base64 payload (design note 2026-09-07 decision I1).</summary>
        public struct ImageBlock
        {
            public string MediaType;
            public string Base64Data;
        }

        /// <summary>
        /// Builds a user message whose content array carries the text (when
        /// non-empty) followed by one base64 "image" block per image --
        /// the shape the CLI's stream-json stdin accepts (verified against
        /// v2.1.263, docs/verify/2026-09-07-cli-image-block.jsonl). At
        /// least one block must remain; an empty text with no images
        /// throws rather than emit an empty content array.
        /// </summary>
        public static string UserContent(string text, IList<ImageBlock> images)
        {
            JsonNode content = JsonNode.NewArray();
            if (!string.IsNullOrEmpty(text))
            {
                content.Add(JsonNode.NewObject().Set("type", "text").Set("text", text));
            }
            if (images != null)
            {
                for (int i = 0; i < images.Count; i++)
                {
                    ImageBlock image = images[i];
                    RequireNonEmpty(image.MediaType, "images[" + i + "].MediaType");
                    RequireNonEmpty(image.Base64Data, "images[" + i + "].Base64Data");
                    content.Add(JsonNode.NewObject()
                        .Set("type", "image")
                        .Set("source", JsonNode.NewObject()
                            .Set("type", "base64")
                            .Set("media_type", image.MediaType)
                            .Set("data", image.Base64Data)));
                }
            }
            if (content.Count == 0)
            {
                throw new ArgumentException("A user message needs text or at least one image.", "text");
            }
            var payload = JsonNode.NewObject()
                .Set("type", "user")
                .Set("message", JsonNode.NewObject()
                    .Set("role", "user")
                    .Set("content", content));
            return JsonWriter.Write(payload);
        }

        // ---------------------------------------------------------------
        // (2) control_request (panel -> CLI)
        // {"type":"control_request","request_id":"...","request":{"subtype":"..."}}
        // ---------------------------------------------------------------

        /// <summary>Handshake; the CLI answers with models/commands/account/pid.</summary>
        public static string Initialize(string requestId)
        {
            return ControlRequest(requestId, JsonNode.NewObject()
                .Set("subtype", "initialize"));
        }

        /// <summary>Interrupts the running turn (stop button).</summary>
        public static string Interrupt(string requestId)
        {
            return ControlRequest(requestId, JsonNode.NewObject()
                .Set("subtype", "interrupt"));
        }

        /// <summary>Switches the permission mode of the running session (e.g. "acceptEdits").</summary>
        public static string SetPermissionMode(string requestId, string mode)
        {
            RequireNonEmpty(mode, "mode");
            return ControlRequest(requestId, JsonNode.NewObject()
                .Set("subtype", "set_permission_mode")
                .Set("mode", mode));
        }

        /// <summary>Switches the model of the running session (alias or full name).</summary>
        public static string SetModel(string requestId, string model)
        {
            RequireNonEmpty(model, "model");
            return ControlRequest(requestId, JsonNode.NewObject()
                .Set("subtype", "set_model")
                .Set("model", model));
        }

        /// <summary>
        /// Undocumented-but-measured (docs/research/08-mcp-transport.md
        /// section 3.4) control_request that makes the CLI retry connecting
        /// to a "failed" MCP server without a process restart -- the Phase
        /// 5 UapOps reconnect safety net (Colloid.AgentPanel.Ops).
        /// </summary>
        public static string McpReconnect(string requestId, string serverName)
        {
            RequireNonEmpty(serverName, "serverName");
            return ControlRequest(requestId, JsonNode.NewObject()
                .Set("subtype", "mcp_reconnect")
                .Set("serverName", serverName));
        }

        // ---------------------------------------------------------------
        // (3) control_response for can_use_tool (panel -> CLI)
        // {"type":"control_response","response":{"subtype":"success",
        //   "request_id":"<same id>","response":{...}}}
        // ---------------------------------------------------------------

        /// <summary>
        /// Allows a tool call. <paramref name="updatedInput"/> should be the
        /// (possibly edited) input object from the can_use_tool request;
        /// null omits the field. <paramref name="updatedPermissions"/> is the
        /// array of permission rules to persist ("always allow" scopes),
        /// typically taken from permission_suggestions; null omits it.
        /// </summary>
        public static string AllowToolUse(string requestId, JsonNode updatedInput = null,
            JsonNode updatedPermissions = null)
        {
            RequireNonEmpty(requestId, "requestId");
            var inner = JsonNode.NewObject()
                .Set("behavior", "allow");
            if (updatedInput != null && !updatedInput.IsNull)
            {
                inner.Set("updatedInput", updatedInput);
            }
            if (updatedPermissions != null && !updatedPermissions.IsNull)
            {
                inner.Set("updatedPermissions", updatedPermissions);
            }
            return ControlResponse(requestId, inner);
        }

        /// <summary>
        /// Denies a tool call with an explanatory message that the model sees.
        /// Set <paramref name="interrupt"/> to also abort the whole turn.
        /// </summary>
        public static string DenyToolUse(string requestId, string message, bool interrupt = false)
        {
            RequireNonEmpty(requestId, "requestId");
            var inner = JsonNode.NewObject()
                .Set("behavior", "deny")
                .Set("message", message ?? string.Empty)
                .Set("interrupt", interrupt);
            return ControlResponse(requestId, inner);
        }

        // ---------------------------------------------------------------

        private static string ControlRequest(string requestId, JsonNode request)
        {
            RequireNonEmpty(requestId, "requestId");
            var payload = JsonNode.NewObject()
                .Set("type", "control_request")
                .Set("request_id", requestId)
                .Set("request", request);
            return JsonWriter.Write(payload);
        }

        private static string ControlResponse(string requestId, JsonNode innerResponse)
        {
            var payload = JsonNode.NewObject()
                .Set("type", "control_response")
                .Set("response", JsonNode.NewObject()
                    .Set("subtype", "success")
                    .Set("request_id", requestId)
                    .Set("response", innerResponse));
            return JsonWriter.Write(payload);
        }

        private static void RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("Value must be a non-empty string.", paramName);
            }
        }
    }
}
