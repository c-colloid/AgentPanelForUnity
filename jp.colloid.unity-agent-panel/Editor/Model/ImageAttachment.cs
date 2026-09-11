using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One image travelling with an outgoing user message (design note
    /// 2026-09-07 section 2.3.1 / decision I3). Only the SEND-READY file is
    /// referenced -- already downscaled and encoded by
    /// ImageAttachmentEncoder and stored under Library/AgentPanel/
    /// Attachments -- so the CompileGate queue, the composer's pending
    /// strip and the transcript cache all carry a path, never bytes.
    /// </summary>
    [Serializable]
    public sealed class ImageAttachment
    {
        /// <summary>Absolute path of the encoded file (PNG or JPEG).</summary>
        public string path = string.Empty;
        /// <summary>"image/png" or "image/jpeg" -- the API's media_type.</summary>
        public string mediaType = "image/png";
        public int width;
        public int height;
        public long bytes;
        /// <summary>What the user sees on the chip/thumbnail: the original file name, "Scene view", ...</summary>
        public string sourceName = string.Empty;

        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("path", path ?? string.Empty)
                .Set("mediaType", mediaType ?? string.Empty)
                .Set("width", width)
                .Set("height", height)
                .Set("bytes", bytes)
                .Set("sourceName", sourceName ?? string.Empty);
        }

        /// <summary>Null for a non-object or an entry without a path.</summary>
        public static ImageAttachment FromJson(JsonNode node)
        {
            if (node == null || !node.IsObject)
            {
                return null;
            }
            string path = node["path"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return new ImageAttachment
            {
                path = path,
                mediaType = node["mediaType"].AsString("image/png"),
                width = node["width"].AsInt(0),
                height = node["height"].AsInt(0),
                bytes = (long)node["bytes"].AsDouble(0),
                sourceName = node["sourceName"].AsString(string.Empty)
            };
        }
    }
}
