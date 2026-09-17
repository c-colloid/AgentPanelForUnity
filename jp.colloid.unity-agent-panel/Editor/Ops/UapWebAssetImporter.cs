using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The save_to half of uap_web_fetch (design note
    /// 2026-09-17-web-fetch-tool.md section 4.5, stage 2): writes the
    /// downloaded bytes to a path under Assets/ and imports them so the
    /// agent can apply a fetched texture to a material in one step.
    /// AssetDatabase is main-thread only while the tool runs on the HTTP
    /// worker, hence the seam; tests substitute a fake.
    /// </summary>
    public interface IUapWebAssetImporter
    {
        /// <summary>
        /// Writes and imports; returns the asset GUID. Throws with an
        /// agent-readable message when the file exists and
        /// <paramref name="overwrite"/> is false, or the import fails.
        /// </summary>
        string Import(string assetPath, byte[] body, bool overwrite);
    }

    /// <summary>
    /// Production importer: hops through the UapOps dispatcher
    /// (<see cref="UapWebFetchTool.MainThreadExecutor"/>) and runs the
    /// write + import there. Unlike the image encoder there is no
    /// fallback -- an import that cannot reach the main thread is an
    /// error, because the agent asked for the asset to exist.
    /// </summary>
    public sealed class UapWebMainThreadAssetImporter : IUapWebAssetImporter
    {
        private readonly Func<IUapToolExecutor> _executor;
        private readonly string _projectRoot;

        public UapWebMainThreadAssetImporter(Func<IUapToolExecutor> executor, string projectRoot)
        {
            _executor = executor;
            _projectRoot = projectRoot;
        }

        public string Import(string assetPath, byte[] body, bool overwrite)
        {
            IUapToolExecutor executor = _executor == null ? null : _executor();
            if (executor == null)
            {
                throw new InvalidOperationException("save_to needs the UapOps server running (no main-thread executor);"
                    + " the file was not imported.");
            }
            JsonNode result = executor.Execute(new ImportStep(_projectRoot, assetPath, body, overwrite), JsonNode.NewObject());
            JsonNode info = result != null && result.IsArray && result.Count > 0 ? result[0] : JsonNode.Null;
            return info["guid"].AsString(string.Empty);
        }

        /// <summary>Main-thread half, shaped as an IUapTool for the dispatcher. Never registered.</summary>
        private sealed class ImportStep : IUapTool
        {
            private readonly string _projectRoot;
            private readonly string _assetPath;
            private readonly byte[] _body;
            private readonly bool _overwrite;

            public ImportStep(string projectRoot, string assetPath, byte[] body, bool overwrite)
            {
                _projectRoot = projectRoot;
                _assetPath = assetPath;
                _body = body;
                _overwrite = overwrite;
            }

            public string Name { get { return "uap_web_fetch.import_asset"; } }
            public string Description { get { return "Internal: writes a fetched file under Assets/ and imports it."; } }
            public string Module { get { return "web"; } }
            public bool Undoable { get { return false; } }
            public bool ReadOnly { get { return false; } }
            public JsonNode InputSchema { get { return JsonNode.NewObject().Set("type", "object"); } }

            public JsonNode Execute(JsonNode input)
            {
                string full = Path.GetFullPath(Path.Combine(_projectRoot, _assetPath));
                if (File.Exists(full) && !_overwrite)
                {
                    throw new InvalidOperationException("'" + _assetPath + "' already exists; pass overwrite:true to replace it.");
                }
                if (Directory.Exists(full))
                {
                    throw new InvalidOperationException("'" + _assetPath + "' is a folder.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllBytes(full, _body ?? new byte[0]);
                UnityEditor.AssetDatabase.ImportAsset(_assetPath, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                string guid = UnityEditor.AssetDatabase.AssetPathToGUID(_assetPath);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new InvalidOperationException("'" + _assetPath + "' was written but the AssetDatabase did not import it.");
                }
                return JsonNode.NewArray().Add(JsonNode.NewObject().Set("guid", guid));
            }
        }
    }
}
