using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Shared "content" array builder used by every core tool's Execute return value.</summary>
    internal static class UapToolResults
    {
        public static JsonNode Text(string text)
        {
            return JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("type", "text").Set("text", text ?? string.Empty));
        }

        /// <summary>
        /// Appends an MCP image content block ({"type":"image","data":
        /// base64,"mimeType":...}) to <paramref name="content"/> (design
        /// note 2026-09-07 section 2.3.7): the model sees the picture in
        /// the tool result itself instead of having to Read the file.
        /// </summary>
        public static JsonNode AddImage(JsonNode content, byte[] bytes, string mimeType)
        {
            if (content == null || !content.IsArray)
            {
                content = JsonNode.NewArray();
            }
            if (bytes != null && bytes.Length > 0)
            {
                content.Add(JsonNode.NewObject()
                    .Set("type", "image")
                    .Set("data", System.Convert.ToBase64String(bytes))
                    .Set("mimeType", string.IsNullOrEmpty(mimeType) ? "image/png" : mimeType));
            }
            return content;
        }
    }
}
