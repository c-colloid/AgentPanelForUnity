using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Persists a <see cref="SessionMetaStore"/> (pin/archive/rename/group
    /// state for the session history list, plus the recorded-scene path) as
    /// a JSON sidecar (default: UserSettings/AgentPanel/SessionMeta.json).
    ///
    /// Why a JSON sidecar and NOT the ScriptableSingleton State.asset (the
    /// project's usual "survives editor restart" store, see
    /// docs/ARCHITECTURE.md D5): the one field in this store that is
    /// genuinely dangerous is <see cref="SessionMeta.titleOverride"/> -- raw,
    /// unconstrained text a user typed into a rename box. That is EXACTLY
    /// the content class that forced the transcript display cache out of
    /// State.asset and into <see cref="SessionCacheFile"/>: Unity's YAML
    /// writer can emit a string (braces, embedded quotes, certain control
    /// characters, a sufficiently long line) that its OWN parser then
    /// rejects on the very next domain reload ("Parser Failure ... Expected
    /// closing '}'"). Because State.asset is a single file, one poisoned
    /// title does not just lose that title -- it corrupts every OTHER
    /// setting stored alongside it (pinned flags, archived flags, panel
    /// settings, everything). A rename box is exactly the kind of input a
    /// user can and will paste arbitrary text into (copy-pasted error
    /// messages, code snippets, emoji), so this store carries the same risk
    /// the transcript cache did and gets the same fix: move the free-text
    /// field out to a JSON file whose reader we wrote ourselves (JsonWriter
    /// escapes every control character and quote; see JsonWriter's class
    /// comment), so a single bad entry can only ever corrupt this one
    /// sidecar -- which Load() below already treats as fully disposable --
    /// and never takes the rest of the panel's settings down with it.
    ///
    /// Contracts (mirrors SessionCacheFile exactly -- see that class for the
    /// original rationale on each point):
    /// - Save() calls <see cref="SessionMetaStore.Compact"/> first. Pin,
    ///   archive, rename and group-assignment are all reversible UI actions
    ///   (pin then unpin, rename then clear the rename box); without this
    ///   call every session a user ever touched, even if every field is
    ///   back to its default, would keep an entry in this file forever.
    ///   Compact() drops exactly the entries that carry no information, so
    ///   the sidecar only grows with real, current customization.
    /// - Save() writes atomically through the shared
    ///   <see cref="AtomicFile"/> helper (tmp + File.Replace, with a
    ///   backup-preserving fallback -- see that class for the crash-window
    ///   analysis) and never throws; failures log one line through the
    ///   injectable logger.
    /// - Load() never throws. A parse/shape failure (the CONTENT is bad)
    ///   logs one line, deletes the bad file and returns null; a transient
    ///   IO failure (cloud-sync/antivirus lock -- <see cref="AtomicFile.IsCorruption"/>
    ///   says false) logs and returns null but KEEPS the file, so a lock
    ///   that clears a second later no longer costs the user their
    ///   pins/renames/groups (MODEL-1). A missing file is a normal
    ///   "nothing recorded yet" and returns null silently -- this sidecar is
    ///   pure user customization layered on top of the CLI's own transcript
    ///   history, so even the corrupt-delete case is a (regrettable but
    ///   survivable) reset of pins/archives/renames, never data loss of the
    ///   conversations themselves.
    /// - formatVersion is written but deliberately NOT gated on read, the
    ///   same policy SessionCacheFile uses for its own "version" key (that
    ///   class writes FormatVersion but ReadSession never inspects it).
    ///   There is no min/max check and no throw-on-future-version: a file
    ///   written by a newer format is read with exactly the same
    ///   field-by-field tolerant defaults as a file written by an older one
    ///   (missing keys fall back to AsBool/AsString's default argument,
    ///   keys this reader does not recognise are simply never looked up
    ///   because JsonNode access is by name, not by iterating "whatever is
    ///   present"). With only additive fields in this format so far, a
    ///   version bump has nothing for this reader to refuse; refusing would
    ///   throw away a user's hand-entered pin/archive/rename/group state the
    ///   moment an older panel build is reinstalled over a newer one, for no
    ///   safety actually gained. If a future change ever needs a real
    ///   breaking migration, that migration should key off the presence/
    ///   shape of the new fields themselves, not this counter.
    /// - Pure C# + System.IO + Core/Json only (unit-testable without Unity
    ///   serialization; no UnityEngine, no UnityEditor, no JsonUtility, no
    ///   Newtonsoft).
    /// </summary>
    public sealed class SessionMetaFile
    {
        /// <summary>Default sidecar location relative to the project root.</summary>
        public const string DefaultRelativePath = "UserSettings/AgentPanel/SessionMeta.json";

        private const int FormatVersion = 1;

        private readonly string _filePath;
        private readonly Action<string> _log;

        public SessionMetaFile(string filePath, Action<string> log = null)
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
        public static SessionMetaFile CreateDefault(string projectRoot, Action<string> log = null)
        {
            return new SessionMetaFile(
                Path.Combine(projectRoot ?? ".", DefaultRelativePath), log);
        }

        // -- Save ----------------------------------------------------------------

        /// <summary>
        /// Compacts the store (drops entries that carry no information --
        /// see <see cref="SessionMeta.IsEmpty"/> -- so undone customization
        /// does not accumulate forever), then writes it atomically through
        /// <see cref="AtomicFile.WriteAllText"/> (staged tmp + swap, backup
        /// preserved through the fallback), so a crash mid-write can never
        /// leave a truncated -- or missing -- sidecar. Creates the parent
        /// directory as needed. Never throws.
        /// </summary>
        public void Save(SessionMetaStore store)
        {
            if (store == null)
            {
                return;
            }
            try
            {
                store.Compact();
                string json = JsonWriter.Write(WriteStore(store));
                AtomicFile.WriteAllText(_filePath, json, _log);
            }
            catch (Exception ex)
            {
                Log("Failed to save the session meta sidecar to '" + _filePath + "': " + ex.Message);
            }
        }

        // -- Load ----------------------------------------------------------------

        /// <summary>
        /// Loads the sidecar, or null when there is nothing usable. A
        /// parse/shape failure logs one line, deletes the corrupt file and
        /// returns null; a transient IO failure logs, KEEPS the file and
        /// returns null (MODEL-1 -- see <see cref="AtomicFile.IsCorruption"/>).
        /// Either way a broken sidecar can never wedge the panel and never
        /// takes any other setting down with it. Never throws.
        /// </summary>
        public SessionMetaStore Load()
        {
            try
            {
                string json = AtomicFile.ReadAllText(_filePath, _log);
                if (json == null)
                {
                    return null;
                }
                JsonNode root = JsonParser.Parse(json);
                if (!root.IsObject)
                {
                    throw new InvalidDataException("root is not a JSON object");
                }
                return ReadStore(root);
            }
            catch (Exception ex)
            {
                if (AtomicFile.IsCorruption(ex))
                {
                    Log("Discarding corrupt session meta sidecar '" + _filePath + "': " + ex.Message);
                    AtomicFile.TryDelete(_filePath, _log);
                }
                else
                {
                    Log("Session meta sidecar '" + _filePath
                        + "' is temporarily unreadable (keeping the file): " + ex.Message);
                }
                return null;
            }
        }

        // -- SessionMetaStore <-> JsonNode mapping --------------------------------

        private static JsonNode WriteStore(SessionMetaStore store)
        {
            JsonNode root = JsonNode.NewObject()
                .Set("formatVersion", FormatVersion);

            JsonNode sessions = JsonNode.NewObject();
            foreach (KeyValuePair<string, SessionMeta> pair in store.bySessionId)
            {
                if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                {
                    sessions.Set(pair.Key, WriteMeta(pair.Value));
                }
            }
            root.Set("sessions", sessions);

            JsonNode groups = JsonNode.NewArray();
            for (int i = 0; i < store.groups.Count; i++)
            {
                if (store.groups[i] != null)
                {
                    groups.Add(WriteGroup(store.groups[i]));
                }
            }
            root.Set("groups", groups);

            return root;
        }

        private static JsonNode WriteMeta(SessionMeta meta)
        {
            return JsonNode.NewObject()
                .Set("pinned", meta.pinned)
                .Set("archived", meta.archived)
                .Set("titleOverride", meta.titleOverride ?? string.Empty)
                .Set("customGroupId", meta.customGroupId ?? string.Empty)
                .Set("recordedScenePath", meta.recordedScenePath ?? string.Empty);
        }

        private static JsonNode WriteGroup(SessionGroup group)
        {
            return JsonNode.NewObject()
                .Set("id", group.id ?? string.Empty)
                .Set("name", group.name ?? string.Empty)
                .Set("order", group.order);
        }

        private static SessionMetaStore ReadStore(JsonNode root)
        {
            var store = new SessionMetaStore();

            JsonNode sessions = root["sessions"];
            if (sessions.IsObject)
            {
                foreach (KeyValuePair<string, JsonNode> pair in sessions.Properties)
                {
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value.IsObject)
                    {
                        store.bySessionId[pair.Key] = ReadMeta(pair.Value);
                    }
                }
            }

            foreach (JsonNode item in root["groups"].Items)
            {
                if (item.IsObject)
                {
                    store.groups.Add(ReadGroup(item));
                }
            }

            return store;
        }

        private static SessionMeta ReadMeta(JsonNode node)
        {
            return new SessionMeta
            {
                pinned = node["pinned"].AsBool(),
                archived = node["archived"].AsBool(),
                titleOverride = node["titleOverride"].AsString(string.Empty),
                customGroupId = node["customGroupId"].AsString(string.Empty),
                recordedScenePath = node["recordedScenePath"].AsString(string.Empty)
            };
        }

        private static SessionGroup ReadGroup(JsonNode node)
        {
            return new SessionGroup
            {
                id = node["id"].AsString(string.Empty),
                name = node["name"].AsString(string.Empty),
                order = node["order"].AsInt()
            };
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
