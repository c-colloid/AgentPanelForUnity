using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Pure (de)serialization for CompileGate's reload-surviving send
    /// queue (INFRA-2c). Extracted from CompileGate's private
    /// LoadQueue/SaveQueue so the round-trip -- including the
    /// legacy-string entry shape, attachment payloads and the
    /// malformed-input discard rule -- is directly EditMode-testable
    /// without touching EditorApplication or AgentHub. No Unity API here;
    /// CompileGate owns the SessionState read/write and the warning log.
    /// </summary>
    public static class CompileGateQueue
    {
        /// <summary>One queued outgoing message (wire + display forms).</summary>
        public sealed class Entry
        {
            public string WireText;
            public string DisplayText;
            public List<ContextAttachment> Attachments;
            /// <summary>Images (paths only) sent as content blocks after the text; null = none.</summary>
            public List<ImageAttachment> Images;
        }

        /// <summary>
        /// Parses a persisted queue. Empty/null input is an empty queue
        /// (no error). Unparseable input or a non-array root returns an
        /// empty list with <paramref name="discardError"/> set -- the
        /// caller logs and clears the store. Item tolerance, unchanged
        /// from CompileGate's original loader: legacy plain-string entries
        /// become wire-only sends; objects need a non-empty "wire"
        /// (anything else is skipped); attachment arrays keep only their
        /// object items.
        /// </summary>
        public static List<Entry> Deserialize(string json, out string discardError)
        {
            discardError = null;
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(json))
            {
                return list;
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(json, out node, out error) || !node.IsArray)
            {
                discardError = error ?? "not an array";
                return list;
            }
            foreach (JsonNode item in node.Items)
            {
                // Legacy entries (pre-attachment queues) were plain strings.
                if (item.IsString)
                {
                    string text = item.AsString(null);
                    if (!string.IsNullOrEmpty(text))
                    {
                        list.Add(new Entry { WireText = text });
                    }
                    continue;
                }
                if (!item.IsObject)
                {
                    continue;
                }
                string wire = item["wire"].AsString(null);
                if (string.IsNullOrEmpty(wire))
                {
                    continue;
                }
                var send = new Entry
                {
                    WireText = wire,
                    DisplayText = item["display"].AsString(null)
                };
                JsonNode attachments = item["attachments"];
                if (attachments.IsArray)
                {
                    foreach (JsonNode att in attachments.Items)
                    {
                        if (!att.IsObject)
                        {
                            continue;
                        }
                        if (send.Attachments == null)
                        {
                            send.Attachments = new List<ContextAttachment>();
                        }
                        send.Attachments.Add(new ContextAttachment(
                            att["title"].AsString(string.Empty),
                            att["payload"].AsString(string.Empty)));
                    }
                }
                JsonNode images = item["images"];
                if (images.IsArray)
                {
                    foreach (JsonNode img in images.Items)
                    {
                        ImageAttachment image = ImageAttachment.FromJson(img);
                        if (image == null)
                        {
                            continue;
                        }
                        if (send.Images == null)
                        {
                            send.Images = new List<ImageAttachment>();
                        }
                        send.Images.Add(image);
                    }
                }
                list.Add(send);
            }
            return list;
        }

        /// <summary>
        /// Serializes the queue; an empty queue serializes to the empty
        /// string (the store's "nothing persisted" value), matching the
        /// original SaveQueue contract.
        /// </summary>
        public static string Serialize(IList<Entry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return string.Empty;
            }
            JsonNode array = JsonNode.NewArray();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry send = entries[i];
                if (send == null)
                {
                    continue;
                }
                JsonNode item = JsonNode.NewObject()
                    .Set("wire", send.WireText ?? string.Empty)
                    .Set("display", send.DisplayText ?? string.Empty);
                if (send.Attachments != null && send.Attachments.Count > 0)
                {
                    JsonNode attachments = JsonNode.NewArray();
                    for (int a = 0; a < send.Attachments.Count; a++)
                    {
                        ContextAttachment attachment = send.Attachments[a];
                        if (attachment == null)
                        {
                            continue;
                        }
                        attachments.Add(JsonNode.NewObject()
                            .Set("title", attachment.title ?? string.Empty)
                            .Set("payload", attachment.payload ?? string.Empty));
                    }
                    item.Set("attachments", attachments);
                }
                if (send.Images != null && send.Images.Count > 0)
                {
                    JsonNode images = JsonNode.NewArray();
                    for (int m = 0; m < send.Images.Count; m++)
                    {
                        if (send.Images[m] != null)
                        {
                            images.Add(send.Images[m].ToJson());
                        }
                    }
                    item.Set("images", images);
                }
                array.Add(item);
            }
            return JsonWriter.Write(array);
        }
    }
}
