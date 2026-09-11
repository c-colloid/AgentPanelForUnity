using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Persists the transcript display cache (ChatSession) as a JSON file
    /// (default: UserSettings/AgentPanel/SessionCache.json), replacing the
    /// old ScriptableSingleton-embedded transcript (ARCHITECTURE.md D5).
    ///
    /// Why not the .asset: raw message text (code blocks, brace-heavy JSON,
    /// very long lines) round-trips unreliably through UnityYAML -- the
    /// writer emits strings the parser then rejects ("Parser Failure ...
    /// Expected closing '}'"), corrupting the WHOLE State.asset including
    /// settings. Our JsonWriter escapes every control character and quote,
    /// and a parse failure's blast radius is now just this cache file,
    /// which Load() discards tolerantly.
    ///
    /// Contracts:
    /// - Save() is atomic (tmp file + replace) and never throws; failures
    ///   log one line through the injectable logger.
    /// - Load() never throws: any IO/parse/shape failure logs one line,
    ///   deletes the bad file and returns null. A missing file is a normal
    ///   "no cache" and returns null silently.
    /// - Pure C# + System.IO + Core/Json only (unit-testable without Unity
    ///   serialization).
    /// </summary>
    public sealed class SessionCacheFile
    {
        /// <summary>Default cache location relative to the project root.</summary>
        public const string DefaultRelativePath = "UserSettings/AgentPanel/SessionCache.json";

        private const int FormatVersion = 1;

        private readonly string _filePath;
        private readonly Action<string> _log;

        public SessionCacheFile(string filePath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("filePath is required", "filePath");
            }
            _filePath = filePath;
            _log = log;
        }

        /// <summary>Absolute path of the cache file.</summary>
        public string FilePath
        {
            get { return _filePath; }
        }

        /// <summary>Cache file at the default location under the project root.</summary>
        public static SessionCacheFile CreateDefault(string projectRoot, Action<string> log = null)
        {
            return new SessionCacheFile(
                Path.Combine(projectRoot ?? ".", DefaultRelativePath), log);
        }

        // -- Save ----------------------------------------------------------------

        /// <summary>
        /// Serializes the session and writes it atomically through the
        /// shared <see cref="AtomicFile"/> helper (staged tmp + swap, backup
        /// preserved through the fallback), so a crash mid-write can never
        /// leave a truncated -- or missing -- cache. Creates the parent
        /// directory as needed. Never throws.
        /// </summary>
        public void Save(ChatSession session)
        {
            Save(session, null);
        }

        /// <summary>
        /// Save overload carrying the per-model usage snapshot (the usage
        /// popover's and context meter's data source). Session TOTALS have
        /// always been cached, but the per-model breakdown lived only in a
        /// plain static -- so every editor start showed restored totals
        /// next to an empty popover and a dead context meter until the
        /// first turn completed. Measured 2026-08-05 with a planted cache
        /// read by a fresh Unity process: FormatUsage said '19.1k tok'
        /// while LastModelUsage had 0 entries. Null/empty writes no key,
        /// which is also what pre-fix caches look like on read.
        /// </summary>
        public void Save(ChatSession session,
            IReadOnlyDictionary<string, ModelUsage> modelUsage)
        {
            if (session == null)
            {
                return;
            }
            try
            {
                JsonNode rootNode = WriteSession(session);
                if (modelUsage != null && modelUsage.Count > 0)
                {
                    JsonNode usageNode = JsonNode.NewObject();
                    foreach (KeyValuePair<string, ModelUsage> pair in modelUsage)
                    {
                        if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                        {
                            usageNode.Set(pair.Key, pair.Value.ToJson());
                        }
                    }
                    rootNode.Set("lastModelUsage", usageNode);
                }
                string json = JsonWriter.Write(rootNode);
                AtomicFile.WriteAllText(_filePath, json, _log);
            }
            catch (Exception ex)
            {
                Log("Failed to save the session cache to '" + _filePath + "': " + ex.Message);
            }
        }

        // -- Load ----------------------------------------------------------------

        /// <summary>
        /// Loads the cached session, or null when there is no usable cache.
        /// A parse/shape failure logs one line, deletes the corrupt file and
        /// returns null; a transient IO failure logs, KEEPS the file and
        /// returns null (MODEL-1, <see cref="AtomicFile.IsCorruption"/>).
        /// Either way a broken cache can never wedge the panel. Never
        /// throws.
        /// </summary>
        public ChatSession Load()
        {
            Dictionary<string, ModelUsage> ignored;
            return Load(out ignored);
        }

        /// <summary>
        /// Load overload restoring the per-model usage snapshot alongside
        /// the session. Never null; a cache written before this key existed
        /// (or with no usage yet) yields an empty dictionary, so callers
        /// get exactly the pre-turn state they would have had live.
        /// </summary>
        public ChatSession Load(
            out Dictionary<string, ModelUsage> modelUsage)
        {
            modelUsage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
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
                ChatSession session = ReadSession(root);
                JsonNode usageNode = root["lastModelUsage"];
                if (usageNode.IsObject)
                {
                    foreach (string model in usageNode.Keys)
                    {
                        if (!string.IsNullOrEmpty(model))
                        {
                            modelUsage[model] = ModelUsage.FromJson(usageNode[model]);
                        }
                    }
                }
                return session;
            }
            catch (Exception ex)
            {
                modelUsage.Clear();
                if (AtomicFile.IsCorruption(ex))
                {
                    Log("Discarding corrupt session cache '" + _filePath + "': " + ex.Message);
                    AtomicFile.TryDelete(_filePath, _log);
                }
                else
                {
                    // MODEL-1: a transient IO failure (cloud-sync/antivirus
                    // lock) is NOT corruption -- keep the file so the next
                    // load can still restore the transcript.
                    Log("Session cache '" + _filePath
                        + "' is temporarily unreadable (keeping the file): " + ex.Message);
                }
                return null;
            }
        }

        // -- ChatSession <-> JsonNode mapping --------------------------------------

        private static JsonNode WriteSession(ChatSession session)
        {
            JsonNode root = JsonNode.NewObject()
                .Set("version", FormatVersion)
                .Set("sessionId", session.sessionId ?? string.Empty)
                .Set("agentBackend", session.agentBackend)
                .Set("title", session.title ?? string.Empty)
                .Set("totalInputTokens", session.totalInputTokens)
                .Set("totalOutputTokens", session.totalOutputTokens)
                .Set("totalCacheReadInputTokens", session.totalCacheReadInputTokens)
                .Set("totalCacheCreationInputTokens", session.totalCacheCreationInputTokens)
                .Set("totalCostUsd", session.totalCostUsd)
                .Set("completedTurns", session.completedTurns)
                .Set("lastActivityTimestamp", session.lastActivityTimestamp ?? string.Empty);
            JsonNode messages = JsonNode.NewArray();
            for (int i = 0; i < session.messages.Count; i++)
            {
                if (session.messages[i] != null)
                {
                    messages.Add(WriteMessage(session.messages[i]));
                }
            }
            root.Set("messages", messages);
            return root;
        }

        private static JsonNode WriteMessage(ChatMessage message)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("id", message.id ?? string.Empty)
                .Set("role", message.role ?? ChatMessage.RoleUser)
                .Set("timestamp", message.timestamp ?? string.Empty)
                .Set("turnId", message.turnId)
                .Set("delivered", message.delivered);
            JsonNode blocks = JsonNode.NewArray();
            for (int i = 0; i < message.blocks.Count; i++)
            {
                if (message.blocks[i] != null)
                {
                    blocks.Add(WriteBlock(message.blocks[i]));
                }
            }
            node.Set("blocks", blocks);
            return node;
        }

        private static JsonNode WriteBlock(ChatMessageBlock block)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("kind", block.kind.ToString())
                .Set("text", block.text ?? string.Empty)
                .Set("title", block.title ?? string.Empty)
                .Set("streaming", block.streaming);
            if (block.toolCall != null)
            {
                node.Set("toolCall", WriteToolCall(block.toolCall));
            }
            // Additive (design note 2026-08-01-thinking-content-loss.md
            // section 5): only meaningful for Thinking blocks, but written
            // unconditionally like every other scalar field here -- 0 for
            // every other kind, indistinguishable from "absent" on read.
            node.Set("thinkingTokens", block.thinkingTokens);
            // Additive (design note 2026-08-01-thinking-content-loss.md
            // section 6): same "always write, default false is absent"
            // convention as thinkingTokens above.
            node.Set("thinkingRedacted", block.thinkingRedacted);
            // Additive (2026-08-14 ui-polish-audit review finding): a
            // system note's warn styling must survive the domain reload --
            // reloads are exactly when the process-died note exists, so
            // dropping the flag here silently demoted the one note the
            // user should read back to narration grey after every restore.
            node.Set("warning", block.warning);
            return node;
        }

        private static JsonNode WriteToolCall(ToolCallRecord record)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("toolUseId", record.toolUseId ?? string.Empty)
                .Set("toolName", record.toolName ?? string.Empty)
                .Set("inputSummary", record.inputSummary ?? string.Empty)
                .Set("inputJson", record.inputJson ?? string.Empty)
                .Set("status", record.status.ToString())
                .Set("startedAtUtcTicks", record.startedAtUtcTicks)
                .Set("durationMs", record.durationMs)
                .Set("resultSummary", record.resultSummary ?? string.Empty)
                .Set("isError", record.isError);
            // Additive (Phase 4): omitted entirely for ordinary tool calls,
            // so caches written before subagent support stay byte-identical
            // and old caches load fine (the reader below just sees no key).
            if (record.subagent != null)
            {
                node.Set("subagent", WriteSubagent(record.subagent));
            }
            return node;
        }

        private static JsonNode WriteSubagent(SubagentRecord subagent)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("taskId", subagent.taskId ?? string.Empty)
                .Set("toolUseId", subagent.toolUseId ?? string.Empty)
                .Set("subagentType", subagent.subagentType ?? string.Empty)
                .Set("description", subagent.description ?? string.Empty)
                .Set("status", subagent.status ?? string.Empty)
                .Set("progressLine", subagent.progressLine ?? string.Empty)
                .Set("lastToolName", subagent.lastToolName ?? string.Empty)
                .Set("totalTokens", subagent.totalTokens)
                .Set("toolUses", subagent.toolUses)
                .Set("durationMs", subagent.durationMs)
                .Set("summaryMarkdown", subagent.summaryMarkdown ?? string.Empty)
                .Set("droppedBlockCount", subagent.droppedBlockCount);
            JsonNode blocks = JsonNode.NewArray();
            for (int i = 0; i < subagent.blocks.Count; i++)
            {
                if (subagent.blocks[i] != null)
                {
                    blocks.Add(WriteBlock(subagent.blocks[i]));
                }
            }
            node.Set("blocks", blocks);
            return node;
        }

        private static ChatSession ReadSession(JsonNode root)
        {
            var session = new ChatSession
            {
                sessionId = root["sessionId"].AsString(string.Empty),
                agentBackend = root["agentBackend"].AsInt(-1),
                title = root["title"].AsString(string.Empty),
                totalInputTokens = root["totalInputTokens"].AsLong(),
                totalOutputTokens = root["totalOutputTokens"].AsLong(),
                totalCacheReadInputTokens = root["totalCacheReadInputTokens"].AsLong(),
                totalCacheCreationInputTokens = root["totalCacheCreationInputTokens"].AsLong(),
                totalCostUsd = root["totalCostUsd"].AsDouble(),
                completedTurns = root["completedTurns"].AsInt(),
                lastActivityTimestamp = root["lastActivityTimestamp"].AsString(string.Empty)
            };
            foreach (JsonNode item in root["messages"].Items)
            {
                if (item.IsObject)
                {
                    session.messages.Add(ReadMessage(item));
                }
            }
            return session;
        }

        private static ChatMessage ReadMessage(JsonNode node)
        {
            var message = new ChatMessage
            {
                role = node["role"].AsString(ChatMessage.RoleUser),
                timestamp = node["timestamp"].AsString(string.Empty),
                turnId = node["turnId"].AsInt(),
                delivered = node["delivered"].AsBool()
            };
            string id = node["id"].AsString(null);
            if (!string.IsNullOrEmpty(id))
            {
                message.id = id;
            }
            foreach (JsonNode item in node["blocks"].Items)
            {
                if (item.IsObject)
                {
                    message.blocks.Add(ReadBlock(item));
                }
            }
            return message;
        }

        private static ChatMessageBlock ReadBlock(JsonNode node)
        {
            var block = new ChatMessageBlock
            {
                kind = ParseEnum(node["kind"].AsString(null), ChatBlockKind.Text),
                text = node["text"].AsString(string.Empty),
                // Absent in pre-attachment caches: defaults to empty.
                title = node["title"].AsString(string.Empty),
                streaming = node["streaming"].AsBool(),
                // Absent in pre-thinking-tokens caches (backward compat):
                // AsLong defaults to 0, same as a freshly constructed block.
                thinkingTokens = node["thinkingTokens"].AsLong(),
                // Absent in pre-redacted-thinking caches (backward compat):
                // AsBool defaults to false, same as a freshly constructed block.
                thinkingRedacted = node["thinkingRedacted"].AsBool(),
                // Absent in pre-warn-note caches (backward compat, 2026-08-14):
                // AsBool defaults to false, same as a freshly constructed block.
                warning = node["warning"].AsBool()
            };
            JsonNode toolCall = node["toolCall"];
            if (toolCall.IsObject)
            {
                block.toolCall = ReadToolCall(toolCall);
            }
            return block;
        }

        private static ToolCallRecord ReadToolCall(JsonNode node)
        {
            var record = new ToolCallRecord
            {
                toolUseId = node["toolUseId"].AsString(string.Empty),
                toolName = node["toolName"].AsString(string.Empty),
                inputSummary = node["inputSummary"].AsString(string.Empty),
                inputJson = node["inputJson"].AsString(string.Empty),
                status = ParseEnum(node["status"].AsString(null), ToolCallStatus.Pending),
                startedAtUtcTicks = node["startedAtUtcTicks"].AsLong(),
                durationMs = node["durationMs"].AsLong(),
                resultSummary = node["resultSummary"].AsString(string.Empty),
                isError = node["isError"].AsBool()
            };
            // Absent in pre-Phase-4 caches (backward compat): stays null,
            // rendering as a plain ToolActivityCard exactly as before.
            JsonNode subagent = node["subagent"];
            if (subagent.IsObject)
            {
                record.subagent = ReadSubagent(subagent);
            }
            return record;
        }

        private static SubagentRecord ReadSubagent(JsonNode node)
        {
            var subagent = new SubagentRecord
            {
                taskId = node["taskId"].AsString(string.Empty),
                toolUseId = node["toolUseId"].AsString(string.Empty),
                subagentType = node["subagentType"].AsString(string.Empty),
                description = node["description"].AsString(string.Empty),
                status = node["status"].AsString("running"),
                progressLine = node["progressLine"].AsString(string.Empty),
                lastToolName = node["lastToolName"].AsString(string.Empty),
                totalTokens = node["totalTokens"].AsLong(),
                toolUses = node["toolUses"].AsInt(),
                durationMs = node["durationMs"].AsLong(),
                summaryMarkdown = node["summaryMarkdown"].AsString(string.Empty),
                droppedBlockCount = node["droppedBlockCount"].AsInt()
            };
            foreach (JsonNode item in node["blocks"].Items)
            {
                if (item.IsObject)
                {
                    subagent.blocks.Add(ReadBlock(item));
                }
            }
            return subagent;
        }

        /// <summary>Enum-by-name with a tolerant fallback for unknown values.</summary>
        private static TEnum ParseEnum<TEnum>(string name, TEnum fallback)
            where TEnum : struct
        {
            TEnum parsed;
            if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, false, out parsed))
            {
                return parsed;
            }
            return fallback;
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
