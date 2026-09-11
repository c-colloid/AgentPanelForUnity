using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Persists the user-edited quick-action list (Settings UI, docs/
    /// design-notes/2026-08-01-settings-enrichment.md #3) as JSON via the
    /// package's own JsonWriter/JsonParser (default: UserSettings/
    /// AgentPanel/QuickActions.json), mirroring SessionCacheFile's contract
    /// exactly.
    ///
    /// Why not the .asset: a quick-action prompt is free-form user text and
    /// can be code-shaped (brace-heavy), the same UnityYAML corruption risk
    /// SessionCacheFile documents for the transcript cache.
    ///
    /// Contracts:
    /// - Save() is atomic (tmp file + replace) and never throws; failures
    ///   log one line through the injectable logger.
    /// - Load() never throws: any IO/parse/shape failure logs one line,
    ///   deletes the bad file and returns an empty list. A missing file is
    ///   a normal "no quick actions yet" and returns an empty list
    ///   silently.
    /// - Pure C# + System.IO + Core/Json only (unit-testable without Unity
    ///   serialization).
    /// </summary>
    public sealed class QuickActionStore
    {
        /// <summary>Default sidecar location relative to the project root.</summary>
        public const string DefaultRelativePath = "UserSettings/AgentPanel/QuickActions.json";

        private const int FormatVersion = 1;

        private readonly string _filePath;
        private readonly Action<string> _log;

        public QuickActionStore(string filePath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("filePath is required", "filePath");
            }
            _filePath = filePath;
            _log = log;
        }

        /// <summary>Absolute path of the sidecar file.</summary>
        public string FilePath
        {
            get { return _filePath; }
        }

        /// <summary>Sidecar file at the default location under the project root.</summary>
        public static QuickActionStore CreateDefault(string projectRoot, Action<string> log = null)
        {
            return new QuickActionStore(
                Path.Combine(projectRoot ?? ".", DefaultRelativePath), log);
        }

        // -- Save ----------------------------------------------------------------

        /// <summary>
        /// Serializes the list and writes it atomically through the shared
        /// <see cref="AtomicFile"/> helper (staged tmp + swap, backup
        /// preserved through the fallback), so a crash mid-write can never
        /// leave a truncated -- or missing -- store. Creates the parent
        /// directory as needed. Null/empty writes an empty (but valid)
        /// store. Never throws.
        /// </summary>
        public void Save(List<QuickAction> actions)
        {
            try
            {
                JsonNode root = JsonNode.NewObject().Set("version", FormatVersion);
                JsonNode items = JsonNode.NewArray();
                if (actions != null)
                {
                    for (int i = 0; i < actions.Count; i++)
                    {
                        QuickAction action = actions[i];
                        if (action == null)
                        {
                            continue;
                        }
                        items.Add(JsonNode.NewObject()
                            .Set("label", action.label ?? string.Empty)
                            .Set("prompt", action.prompt ?? string.Empty));
                    }
                }
                root.Set("actions", items);
                string json = JsonWriter.Write(root);
                AtomicFile.WriteAllText(_filePath, json, _log);
            }
            catch (Exception ex)
            {
                Log("Failed to save quick actions to '" + _filePath + "': " + ex.Message);
            }
        }

        // -- Load ----------------------------------------------------------------

        /// <summary>
        /// Loads the saved quick-action list, or an empty list when there
        /// is no usable store. A parse/shape failure logs one line, deletes
        /// the corrupt file and returns an empty list; a transient IO
        /// failure logs, KEEPS the file and returns an empty list (MODEL-1,
        /// <see cref="AtomicFile.IsCorruption"/>). Either way a broken
        /// store can never wedge the panel. Never throws.
        /// </summary>
        public List<QuickAction> Load()
        {
            try
            {
                string json = AtomicFile.ReadAllText(_filePath, _log);
                if (json == null)
                {
                    return new List<QuickAction>();
                }
                JsonNode root = JsonParser.Parse(json);
                if (!root.IsObject)
                {
                    throw new InvalidDataException("root is not a JSON object");
                }
                var result = new List<QuickAction>();
                foreach (JsonNode item in root["actions"].Items)
                {
                    if (!item.IsObject)
                    {
                        continue;
                    }
                    result.Add(new QuickAction
                    {
                        label = item["label"].AsString(string.Empty),
                        prompt = item["prompt"].AsString(string.Empty)
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                if (AtomicFile.IsCorruption(ex))
                {
                    Log("Discarding corrupt quick actions '" + _filePath + "': " + ex.Message);
                    AtomicFile.TryDelete(_filePath, _log);
                }
                else
                {
                    Log("Quick actions '" + _filePath
                        + "' are temporarily unreadable (keeping the file): " + ex.Message);
                }
                return new List<QuickAction>();
            }
        }

        // -- Small helpers ------------------------------------------------------------

        private void Log(string message)
        {
            Action<string> log = _log;
            if (log != null)
            {
                log(message);
            }
        }
    }
}
