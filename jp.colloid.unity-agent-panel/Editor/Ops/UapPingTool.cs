using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The one built-in tool for Phase 5a (design section "One built-in
    /// tool only in this stream"): a pure echo used as the registry/
    /// transport smoke test. No side effects, no Undo, module "core".
    /// </summary>
    public sealed class UapPingTool : IUapTool
    {
        public const string DefaultMessage = "pong";

        public string Name
        {
            get { return "uap_ping"; }
        }

        // First words are the searchable verb/noun (design section 1.4):
        // ToolSearch indexes this text, not the tool name.
        public string Description
        {
            get
            {
                return "Echoes a message back from the Unity Editor UapOps server."
                    + " No side effects -- use this to verify the MCP connection/registry"
                    + " are working before relying on any other Unity operation tool.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        /// <summary>
        /// True: this tool echoes a string back and touches nothing --
        /// no scene, no asset, no file.
        ///
        /// It read false until v0.16.0 purely by accident of history: the
        /// ReadOnly flag was added in v0.14.0, after this connectivity-check
        /// tool shipped in v0.12.0, and nobody revisited it. That was
        /// harmless while the flag only suppressed a permission card, but
        /// the v0.16.0 auto-approve levels sort tools by exactly this pair
        /// of flags -- so a tool that cannot change anything was sitting in
        /// the tier reserved for work Undo cannot take back, and asking the
        /// user to approve a ping is the sort of prompt that makes people
        /// stop reading prompts. Found by counting the real registry while
        /// verifying those levels against the live editor.
        /// </summary>
        public bool ReadOnly
        {
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("message", JsonNode.NewObject()
                            .Set("type", "string")
                            .Set("description",
                                "Text to echo back. Defaults to \"" + DefaultMessage + "\" when omitted.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string message = input != null ? input["message"].AsString(DefaultMessage) : DefaultMessage;
            if (string.IsNullOrEmpty(message))
            {
                message = DefaultMessage;
            }
            return JsonNode.NewArray()
                .Add(JsonNode.NewObject()
                    .Set("type", "text")
                    .Set("text", message));
        }
    }
}
