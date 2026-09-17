using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Turns a downloaded PNG/JPEG into the block the model gets: long edge
    /// capped at the composer-attachment size, PNG or JPEG, hard byte cap
    /// -- the same pipeline as uap_editor_screenshot's return_image. The
    /// seam exists because uap_web_fetch runs OFF the main thread while
    /// Texture2D work must run ON it; tests substitute a fake.
    /// </summary>
    public interface IUapWebImageEncoder
    {
        bool TryEncode(byte[] raw, string sourceName, out byte[] encoded, out string mimeType,
            out int width, out int height, out string error);
    }

    /// <summary>
    /// Production encoder: hops to the Unity main thread through the
    /// UapOps dispatcher (an <see cref="IUapToolExecutor"/> that
    /// UapOpsServer installs while running) and runs
    /// ImageAttachmentEncoder there. When no executor is installed -- the
    /// server is not running, or the tool is invoked directly -- it reports
    /// failure and the tool falls back to passing the original bytes
    /// through, so a fetch never depends on the hop to succeed.
    /// </summary>
    public sealed class UapWebMainThreadImageEncoder : IUapWebImageEncoder
    {
        private readonly Func<IUapToolExecutor> _executor;

        public UapWebMainThreadImageEncoder(Func<IUapToolExecutor> executor)
        {
            _executor = executor;
        }

        public bool TryEncode(byte[] raw, string sourceName, out byte[] encoded, out string mimeType,
            out int width, out int height, out string error)
        {
            encoded = null;
            mimeType = null;
            width = 0;
            height = 0;
            error = null;
            IUapToolExecutor executor = _executor == null ? null : _executor();
            if (executor == null)
            {
                error = "no main-thread executor is available";
                return false;
            }
            JsonNode result;
            try
            {
                result = executor.Execute(new EncodeStep(raw, sourceName), JsonNode.NewObject());
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            JsonNode info = result != null && result.IsArray && result.Count > 0 ? result[0] : JsonNode.Null;
            string path = info["path"].AsString(null);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "the encoder returned no file";
                return false;
            }
            encoded = File.ReadAllBytes(path);
            mimeType = info["mimeType"].AsString("image/png");
            width = info["width"].AsInt(0);
            height = info["height"].AsInt(0);
            return encoded.Length > 0;
        }

        /// <summary>
        /// The main-thread half, shaped as an IUapTool so the dispatcher
        /// runs it like any tools/call (with the normal timeout and Console
        /// error scoping). Never registered; internal to the hop.
        /// </summary>
        private sealed class EncodeStep : IUapTool
        {
            private readonly byte[] _raw;
            private readonly string _sourceName;

            public EncodeStep(byte[] raw, string sourceName)
            {
                _raw = raw;
                _sourceName = sourceName;
            }

            public string Name { get { return "uap_web_fetch.encode_image"; } }
            public string Description { get { return "Internal: encodes a fetched image on the main thread."; } }
            public string Module { get { return "web"; } }
            public bool Undoable { get { return false; } }
            public bool ReadOnly { get { return true; } }
            public JsonNode InputSchema { get { return JsonNode.NewObject().Set("type", "object"); } }

            public JsonNode Execute(JsonNode input)
            {
                Colloid.AgentPanel.Model.ImageAttachment attachment;
                string error;
                if (!Colloid.AgentPanel.Integration.ImageAttachmentEncoder.TryImportBytes(_raw, _sourceName, out attachment, out error))
                {
                    throw new InvalidOperationException(error);
                }
                return JsonNode.NewArray().Add(JsonNode.NewObject()
                    .Set("path", attachment.path)
                    .Set("mimeType", attachment.mediaType)
                    .Set("width", attachment.width)
                    .Set("height", attachment.height));
            }
        }
    }
}
