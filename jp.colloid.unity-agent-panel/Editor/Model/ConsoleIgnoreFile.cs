using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// MODEL-4: persists the Console-error ignore store
    /// (PanelSettings.ignoredConsoleErrors / ignoredConsoleErrorPatterns)
    /// as a JSON sidecar (default:
    /// UserSettings/AgentPanel/ConsoleIgnore.json) instead of inside the
    /// ScriptableSingleton State.asset. The ignored-error entries are RAW
    /// Console messages -- braces, quotes, stack traces, arbitrarily long
    /// lines -- exactly the string class that round-trips unreliably
    /// through UnityYAML and used to corrupt the WHOLE State.asset (the
    /// same rationale as SessionCacheFile and SessionMetaFile; see
    /// PanelStateStore's "never add message-content strings to this asset"
    /// rule). One X-click on a sufficiently hostile compiler error must
    /// only ever be able to hurt this one disposable sidecar, never every
    /// other panel setting stored alongside it.
    ///
    /// Contracts (mirrors SessionMetaFile):
    /// - Save() writes through <see cref="AtomicFile.WriteAllTextIfChanged"/>
    ///   (atomic swap, skip-when-identical so PanelStateStore.SaveNow can
    ///   call it on every hot path for free) and never throws.
    /// - Load() never throws. Parse/shape corruption logs, deletes the file
    ///   and reports nothing; a transient IO failure logs, KEEPS the file
    ///   and reports nothing for this attempt (MODEL-1,
    ///   <see cref="AtomicFile.IsCorruption"/>). Losing this sidecar is a
    ///   (regrettable but survivable) reset of the ignore list -- the raw
    ///   captured errors themselves live in ConsoleErrorProvider and are
    ///   never deleted.
    /// - formatVersion is written but not gated on read, the same
    ///   additive-fields policy as SessionMetaFile (see that class's doc
    ///   comment for the full rationale).
    /// - Pure C# + System.IO + Core only: unit-testable without Unity
    ///   serialization.
    /// </summary>
    public sealed class ConsoleIgnoreFile
    {
        /// <summary>Default sidecar location relative to the project root.</summary>
        public const string DefaultRelativePath = "UserSettings/AgentPanel/ConsoleIgnore.json";

        private const int FormatVersion = 1;

        private readonly string _filePath;
        private readonly Action<string> _log;

        public ConsoleIgnoreFile(string filePath, Action<string> log = null)
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
        public static ConsoleIgnoreFile CreateDefault(string projectRoot, Action<string> log = null)
        {
            return new ConsoleIgnoreFile(
                Path.Combine(projectRoot ?? ".", DefaultRelativePath), log);
        }

        /// <summary>
        /// Writes the store. Cheap when nothing changed (content-identical
        /// writes are skipped), so callers may invoke it unconditionally
        /// from every save path. Never throws.
        /// </summary>
        public void Save(List<string> ignoredErrors, string patterns)
        {
            try
            {
                JsonNode errors = JsonNode.NewArray();
                if (ignoredErrors != null)
                {
                    for (int i = 0; i < ignoredErrors.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(ignoredErrors[i]))
                        {
                            errors.Add(ignoredErrors[i]);
                        }
                    }
                }
                JsonNode root = JsonNode.NewObject()
                    .Set("formatVersion", FormatVersion)
                    .Set("ignoredErrors", errors)
                    .Set("patterns", patterns ?? string.Empty);
                AtomicFile.WriteAllTextIfChanged(_filePath, JsonWriter.Write(root), _log);
            }
            catch (Exception ex)
            {
                Log("Failed to save the console-ignore sidecar to '" + _filePath + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Loads the store into <paramref name="ignoredErrors"/> /
        /// <paramref name="patterns"/>. Returns false (empty outputs) when
        /// there is nothing usable -- missing file (normal), corrupt file
        /// (logged + deleted), or a transient IO failure (logged, file
        /// KEPT). Never throws.
        /// </summary>
        public bool Load(out List<string> ignoredErrors, out string patterns)
        {
            ignoredErrors = new List<string>();
            patterns = string.Empty;
            try
            {
                string json = AtomicFile.ReadAllText(_filePath, _log);
                if (json == null)
                {
                    return false;
                }
                JsonNode root = JsonParser.Parse(json);
                if (!root.IsObject)
                {
                    throw new InvalidDataException("root is not a JSON object");
                }
                foreach (JsonNode item in root["ignoredErrors"].Items)
                {
                    string message = item.AsString(null);
                    if (!string.IsNullOrEmpty(message))
                    {
                        ignoredErrors.Add(message);
                    }
                }
                patterns = root["patterns"].AsString(string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                ignoredErrors.Clear();
                patterns = string.Empty;
                if (AtomicFile.IsCorruption(ex))
                {
                    Log("Discarding corrupt console-ignore sidecar '" + _filePath + "': " + ex.Message);
                    AtomicFile.TryDelete(_filePath, _log);
                }
                else
                {
                    Log("Console-ignore sidecar '" + _filePath
                        + "' is temporarily unreadable (keeping the file): " + ex.Message);
                }
                return false;
            }
        }

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
