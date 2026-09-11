using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// Payload of a control_request subtype "can_use_tool" (CLI asks the panel
    /// for permission to run a tool). Everything except ToolName is optional.
    /// The panel answers via OutboundMessages.AllowToolUse / DenyToolUse.
    /// </summary>
    public sealed class CanUseToolRequest
    {
        /// <summary>Tool identifier, e.g. "Bash", "Edit".</summary>
        public string ToolName { get; private set; }
        /// <summary>Human-facing name (may differ from ToolName).</summary>
        public string DisplayName { get; private set; }
        /// <summary>The exact tool input object (tool-specific schema, raw JSON).</summary>
        public JsonNode Input { get; private set; }
        /// <summary>Correlates with the assistant tool_use block id.</summary>
        public string ToolUseId { get; private set; }
        /// <summary>Optional human-readable description of the action.</summary>
        public string Description { get; private set; }
        /// <summary>
        /// Suggested permission rules (raw JSON array). Passed back verbatim in
        /// updatedPermissions when the user chooses "always allow".
        /// </summary>
        public JsonNode PermissionSuggestions { get; private set; }
        /// <summary>Path that triggered the prompt, when applicable.</summary>
        public string BlockedPath { get; private set; }
        /// <summary>Raw decision_reason object, when present.</summary>
        public JsonNode DecisionReason { get; private set; }
        public bool RequiresUserInteraction { get; private set; }

        public static CanUseToolRequest FromJson(JsonNode request)
        {
            var req = new CanUseToolRequest();
            req.ToolName = request["tool_name"].AsString();
            req.DisplayName = request["display_name"].AsString(req.ToolName);
            req.Input = request["input"];
            req.ToolUseId = request["tool_use_id"].AsString();
            req.Description = request["description"].AsString();
            req.PermissionSuggestions = request["permission_suggestions"];
            req.BlockedPath = request["blocked_path"].AsString();
            req.DecisionReason = request["decision_reason"];
            req.RequiresUserInteraction = request["requires_user_interaction"].AsBool(false);
            return req;
        }
    }

    /// <summary>
    /// One entry of result.permission_denials (tool calls rejected without
    /// user interaction). Shape is optional-first; captured sessions only
    /// contained empty arrays, so every field tolerates absence.
    /// </summary>
    public sealed class PermissionDenial
    {
        public string ToolName { get; private set; }
        public string ToolUseId { get; private set; }
        /// <summary>Raw tool input of the denied call.</summary>
        public JsonNode ToolInput { get; private set; }
        /// <summary>Full raw entry for forward compatibility.</summary>
        public JsonNode Raw { get; private set; }

        public static PermissionDenial FromJson(JsonNode node)
        {
            var denial = new PermissionDenial();
            denial.Raw = node;
            denial.ToolName = node["tool_name"].AsString();
            denial.ToolUseId = node["tool_use_id"].AsString();
            denial.ToolInput = node["tool_input"];
            return denial;
        }
    }
}
