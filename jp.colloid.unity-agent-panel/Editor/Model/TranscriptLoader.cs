using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Parses one CLI session transcript (~/.claude/projects/&lt;cwd
    /// transform&gt;/&lt;session-uuid&gt;.jsonl) into the same ChatMessage /
    /// ChatMessageBlock / ToolCallRecord shapes Integration.AgentHub builds
    /// while a session is live, so a restored history entry renders
    /// identically to a freshly streamed one. Pure C# + Core/Json only --
    /// no UnityEngine/UnityEditor dependency, no dependency on
    /// Editor/Integration (see the design note for why the layering rule
    /// forced ContextBlockFormatter's header/footer markers to be
    /// duplicated here as literals instead of referenced).
    ///
    /// Mapping summary (full table in the design note):
    /// - "user" line, isMeta:true                    -&gt; skipped entirely
    ///   (matches live: AgentHub only reacts to tool_result content on a
    ///   user echo, so CLI-injected meta text is already invisible live)
    /// - "user" line with a text content item        -&gt; new ChatMessage
    ///   (role=user, delivered=true, turnId=0) and CLOSES the current
    ///   assistant accumulator, mirroring AgentHub.SendUserMessage setting
    ///   _streamingAssistant = null on every real user send
    /// - "user" line with a tool_result content item  -&gt; completes the
    ///   matching open ToolCallRecord; never creates a message row
    ///   (matches OnToolResultReceived)
    /// - "assistant" line                              -&gt; appends
    ///   text/thinking/tool_use blocks to the CURRENT assistant
    ///   ChatMessage (creating one if none is open), because on-disk
    ///   transcripts never carry a "result" line to close a turn -- only
    ///   the next real user message does (see design note)
    /// - "system" line, subtype compact_boundary        -&gt; a system-note
    ///   row worded by CompactionNote.Describe (the same note AgentHub
    ///   writes live on system/compact_boundary) and CLOSES the current
    ///   assistant accumulator; "user" lines flagged isCompactSummary
    ///   (the CLI-authored summary that follows the boundary) are skipped
    ///   like isMeta lines -- the person never typed them (design note
    ///   2026-09-07-slash-commands-and-compaction.md section 2.4)
    /// - anything else (other system subtypes/summary/queue-operation/
    ///   attachment/last-prompt/ai-title/mode/control_*/stream_event/
    ///   result/unknown) -&gt; skipped silently
    /// </summary>
    public static class TranscriptLoader
    {
        /// <summary>
        /// Where a restored image block's bytes go (design note 2026-09-07
        /// section 2.3.5, phase 2): (bytes, mediaType) -&gt; absolute path.
        /// Null (the default) skips images entirely so the loader stays
        /// Unity-free for tests that do not care; AgentHub sets it to
        /// ImageAttachmentStore.Save at editor load.
        /// </summary>
        public static Func<byte[], string, string> ImageSaver;

        /// <summary>
        /// Words a compact_boundary line for the transcript: (trigger,
        /// preTokens) -&gt; note text. Installed at editor load by
        /// Integration's CompactionNote (the localized wording the live
        /// path uses) so the loader stays UI-free (D9 layering). Null (the
        /// default) falls back to a plain English line so a restore never
        /// loses the boundary even when nothing installed the seam.
        /// </summary>
        public static Func<string, long, string> CompactionDescriber;

        /// <summary>Largest base64 image payload decoded on restore (a bit over the 5 MB send cap).</summary>
        public const int MaxRestoredImageBase64Chars = 8 * 1024 * 1024;

        /// <summary>Matches AgentHub's SummaryMaxChars for tool input/result summaries.</summary>
        private const int SummaryMaxChars = 160;

        /// <summary>Default cap on restored messages (matches the 300-message
        /// history-pruning threshold documented in ARCHITECTURE.md risk #9).</summary>
        public const int DefaultMaxMessages = 300;

        /// <summary>
        /// MODEL-3: byte cap on how much of a transcript Load will parse.
        /// Everything here is synchronous on the main thread, so a
        /// pathological transcript (a runaway session logging hundreds of
        /// MB) used to freeze the editor for its ENTIRE length even though
        /// only the last DefaultMaxMessages messages survive the trim.
        /// Sizing: this package's own capture fixtures run ~0.4-4 KB per
        /// line and a message is a handful of lines, so 300 messages fit in
        /// well under 4 MB even with bulky tool payloads; 16 MB is 4x that
        /// worst case, cheap to read (tens of ms), and two orders of
        /// magnitude below the pathology this bounds. When the cap trips,
        /// only the TAIL window is parsed -- so the reconstructed usage
        /// totals then cover the tail, not the whole file (documented on
        /// the Load overload; a fast, slightly-partial number over a frozen
        /// editor is the measured judgment here).
        /// </summary>
        public const long DefaultMaxTranscriptBytes = 16L * 1024L * 1024L;

        // Duplicated from Editor/Integration/ContextBlockFormatter on
        // purpose: Model must not depend on Integration (D9 layering is
        // one-way from UI into each of Model/Integration/Core, never
        // Model->Integration). TranscriptLoaderTests asserts these two
        // literals stay byte-for-byte in sync with the real constants.
        private const string ContextHeaderMarker =
            "\n\n===== ATTACHED UNITY EDITOR CONTEXT =====";
        private const string ContextFooterLiteral =
            "===== END OF UNITY EDITOR CONTEXT =====";

        // MODEL-6: scaffold tags the CLI writes into TERMINAL-session
        // transcripts around slash-command invocations and their local
        // output. Constants (not inline literals) for the same reason as
        // the context markers above -- Model cannot reference the CLI's
        // own definitions, so TranscriptLoaderTests keeps a canary fixture
        // asserting these literals still occur in a real-format transcript
        // (format drift then fails a test instead of silently mis-rendering).
        internal const string CommandNameOpenTag = "<command-name>";
        internal const string CommandArgsOpenTag = "<command-args>";
        internal const string LocalCommandStdoutOpenTag = "<local-command-stdout>";
        internal const string LocalCommandStderrOpenTag = "<local-command-stderr>";
        internal const string LocalCommandCaveatOpenTag = "<local-command-caveat>";

        /// <summary>
        /// Loads and maps a session transcript. Never throws: a missing
        /// file, an unreadable file, or any parse failure degrades to an
        /// empty list rather than surfacing an error (a broken history
        /// entry must never block the History view or crash the panel --
        /// see the design note for the rationale). Tolerant of a truncated
        /// last line (crash-time transcripts): that line simply fails to
        /// parse and is skipped like any other malformed line.
        /// </summary>
        public static List<ChatMessage> Load(string filePath, int maxMessages = DefaultMaxMessages)
        {
            TranscriptUsage ignored;
            return Load(filePath, out ignored, maxMessages);
        }

        /// <summary>
        /// Same as <see cref="Load(string,int)"/>, additionally reporting the
        /// token usage reconstructed from the file so a restored session can
        /// show its numbers instead of "0 tok" (see
        /// <see cref="TranscriptUsage"/> for what is and is not
        /// recoverable). Always assigns <paramref name="usage"/>, including
        /// on every degrade-to-empty path -- callers never have to null
        /// check it.
        ///
        /// Note the totals cover the whole PARSED region even when
        /// <paramref name="maxMessages"/> trims the returned messages: the
        /// trim happens after the scan. When the MODEL-3 byte cap trips
        /// (file larger than <see cref="DefaultMaxTranscriptBytes"/>), the
        /// parsed region IS the tail window, so the totals then describe
        /// the tail only -- see that constant's doc for why a fast,
        /// slightly-partial number beats freezing the editor on a
        /// pathological file.
        /// </summary>
        public static List<ChatMessage> Load(string filePath, out TranscriptUsage usage,
            int maxMessages = DefaultMaxMessages)
        {
            return Load(filePath, out usage, maxMessages, DefaultMaxTranscriptBytes);
        }

        /// <summary>
        /// MODEL-3 test seam: same as the public overload with an
        /// injectable byte cap, so the tail-window behavior is testable
        /// with tiny files instead of a 16 MB fixture. When the file is
        /// larger than <paramref name="maxBytes"/>, only the trailing
        /// window is parsed (the first, almost certainly partial, line of
        /// the window is discarded) and the usage totals describe that
        /// window only.
        /// </summary>
        internal static List<ChatMessage> Load(string filePath, out TranscriptUsage usage,
            int maxMessages, long maxBytes)
        {
            usage = new TranscriptUsage();
            var messages = new List<ChatMessage>();
            if (string.IsNullOrEmpty(filePath))
            {
                return messages;
            }

            IEnumerable<string> lines;
            try
            {
                if (!File.Exists(filePath))
                {
                    return messages;
                }
                long length = new FileInfo(filePath).Length;
                lines = maxBytes > 0 && length > maxBytes
                    ? ReadTailLines(filePath, length, maxBytes)
                    : File.ReadLines(filePath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return messages;
            }

            var state = new LoaderState(messages, usage);
            try
            {
                foreach (string rawLine in lines)
                {
                    ProcessLine(rawLine, state);
                    // MODEL-3: bounded retention. The old code accumulated
                    // EVERY message and trimmed once at the end, holding
                    // the whole transcript's ChatMessages in memory to
                    // return the last 300. Pruning the head in chunks keeps
                    // the list O(maxMessages) at all times; CurrentAssistant
                    // is always the newest message and open ToolCallRecords
                    // are reached through the OpenToolCalls map (mutating a
                    // record whose head message was pruned is harmless), so
                    // nothing downstream sees the difference.
                    if (maxMessages > 0 && messages.Count >= maxMessages * 2)
                    {
                        messages.RemoveRange(0, messages.Count - maxMessages);
                    }
                }
            }
            catch (IOException)
            {
                // A mid-stream IO error (e.g. the file is deleted or locked
                // while a lazy File.ReadLines enumeration is in progress)
                // still returns whatever was parsed so far rather than
                // throwing out of Load(). Deliberately NOT a catch-all
                // Exception here: a bug inside ProcessLine/HandleUserLine/
                // HandleAssistantLine must fail loudly (a test failure or a
                // visible crash) rather than silently truncating the
                // restored transcript at the offending line -- that exact
                // failure mode (a bad DateTimeStyles combination throwing
                // mid-file and being swallowed here) is what let this
                // loader ship broken before TranscriptLoaderTests caught it.
            }
            catch (UnauthorizedAccessException)
            {
                // Same rationale as IOException above (permission revoked
                // mid-scan is an environment condition, not a code bug).
            }

            // End of file: any tool_use left "Running" never got a
            // tool_result (interrupted turn, or the transcript itself was
            // truncated mid tool-call). Demote to Pending, exactly like
            // AgentHub.FinalizeStreamingMessage does for a live turn that
            // ends without every card resolving.
            foreach (KeyValuePair<string, ToolCallRecord> pair in state.OpenToolCalls)
            {
                if (pair.Value.status == ToolCallStatus.Running)
                {
                    pair.Value.status = ToolCallStatus.Pending;
                }
            }

            if (maxMessages > 0 && messages.Count > maxMessages)
            {
                return messages.GetRange(messages.Count - maxMessages, maxMessages);
            }
            return messages;
        }

        /// <summary>
        /// MODEL-3: lazily yields the lines of the file's trailing
        /// <paramref name="maxBytes"/> window. The seek almost certainly
        /// lands mid-line, so the first read is discarded as a partial
        /// line; a malformed JSON fragment would have been skipped by
        /// ProcessLine anyway, this just makes the intent explicit. UTF-8
        /// multi-byte sequences cut by the seek are confined to that same
        /// discarded fragment.
        /// </summary>
        private static IEnumerable<string> ReadTailLines(string filePath, long fileLength, long maxBytes)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(fileLength - maxBytes, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    reader.ReadLine(); // the partial first line of the window
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        yield return line;
                    }
                }
            }
        }

        private static void ProcessLine(string rawLine, LoaderState state)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return;
            }
            JsonNode node;
            try
            {
                node = JsonParser.Parse(rawLine);
            }
            catch (JsonParseException)
            {
                return;
            }
            if (!node.IsObject)
            {
                return;
            }
            bool isMeta = node["isMeta"].AsBool();
            string type = node["type"].AsString(string.Empty);
            if (type == "system")
            {
                // The one system subtype that is a visible fact of the
                // conversation. Checked BEFORE the isMeta gate: the CLI
                // writes the boundary line with isMeta false today, but
                // the boundary must survive even if that flag ever flips.
                if (node["subtype"].AsString(string.Empty) == "compact_boundary")
                {
                    HandleCompactBoundaryLine(node, state);
                }
                if (!isMeta)
                {
                    NoteTimestamp(node, state);
                }
                return;
            }
            if (isMeta)
            {
                return;
            }
            NoteTimestamp(node, state);
            if (type == "user")
            {
                if (node["isCompactSummary"].AsBool())
                {
                    // The CLI-authored summary injected after a
                    // compaction: not the person's words, and the model
                    // already has it. Rendering it as a user bubble showed
                    // "This session is being continued from a previous
                    // conversation..." as if the user had typed it.
                    return;
                }
                HandleUserLine(node, state);
            }
            else if (type == "assistant")
            {
                // Accumulated BEFORE the content dispatch on purpose:
                // HandleAssistantLine bails out early when content is not an
                // array, but such a line's usage is still real usage.
                state.Usage.Accumulate(node["message"]);
                HandleAssistantLine(node, state);
            }
            // Every other type (system, summary, queue-operation,
            // attachment, last-prompt, ai-title, mode, compact_boundary,
            // control_request/response, stream_event, result, and any
            // future/unknown type) is ignored by design.
        }

        /// <summary>Latest timestamp seen so far (the restored session's "last activity").</summary>
        private static void NoteTimestamp(JsonNode node, LoaderState state)
        {
            string timestamp = node["timestamp"].AsString(string.Empty);
            if (!string.IsNullOrEmpty(timestamp))
            {
                state.Usage.LastTimestampIso = timestamp;
            }
        }

        // -- "system" lines -------------------------------------------------------

        /// <summary>
        /// A compact_boundary line read back from disk: the same note the
        /// live path writes (AgentHub.OnCompactBoundaryReceived), so a
        /// restored session shows where its summary boundary is (through
        /// the CompactionDescriber seam). Closes
        /// the current assistant accumulator the way a user line does --
        /// assistant text after the boundary belongs to a new bubble.
        /// Metadata is read through SystemCompactBoundaryMessage.ReadMetadata,
        /// which accepts both the on-disk camelCase (compactMetadata.
        /// preTokens) and the wire snake_case spelling.
        /// </summary>
        private static void HandleCompactBoundaryLine(JsonNode node, LoaderState state)
        {
            string trigger;
            long preTokens;
            SystemCompactBoundaryMessage.ReadMetadata(node, out trigger, out preTokens);
            state.CurrentAssistant = null;
            var note = new ChatMessage
            {
                role = ChatMessage.RoleSystem,
                timestamp = node["timestamp"].AsString(string.Empty),
                turnId = 0,
                delivered = true
            };
            Func<string, long, string> describe = CompactionDescriber;
            string text = describe != null
                ? describe(trigger, preTokens)
                : DescribeCompactionFallback(preTokens);
            note.Add(ChatMessageBlock.MakeSystemNote(text));
            state.Messages.Add(note);
        }

        /// <summary>Un-localized wording used only when no CompactionDescriber is installed.</summary>
        private static string DescribeCompactionFallback(long preTokens)
        {
            return preTokens < 0
                ? "Conversation compacted."
                : "Conversation compacted (" + TokenCountFormat.Short(preTokens) + " tokens before).";
        }

        // -- "user" lines ---------------------------------------------------------

        /// <summary>
        /// MODEL-6: true when <paramref name="text"/> is CLI terminal
        /// scaffolding rather than user prose. For a slash-command wrapper,
        /// <paramref name="visibleText"/> becomes the honest minimal form
        /// the person actually typed ("/model claude-fable-5"); for local
        /// command output/caveat blocks it stays empty, which the caller's
        /// empty-check turns into a silent skip. Panel-authored sessions
        /// never contain these tags, so this is a no-op for them.
        /// </summary>
        internal static bool TryNormalizeCliScaffold(string text, out string visibleText)
        {
            visibleText = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.IndexOf(CommandNameOpenTag, StringComparison.Ordinal) >= 0)
            {
                string name = ExtractTagContent(text, "command-name");
                string args = ExtractTagContent(text, "command-args");
                visibleText = string.IsNullOrEmpty(args) ? name : (name + " " + args).Trim();
                return true;
            }
            if (text.IndexOf(LocalCommandStdoutOpenTag, StringComparison.Ordinal) >= 0
                || text.IndexOf(LocalCommandStderrOpenTag, StringComparison.Ordinal) >= 0
                || text.IndexOf(LocalCommandCaveatOpenTag, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            return false;
        }

        /// <summary>The trimmed text between &lt;tag&gt; and &lt;/tag&gt;, or empty when absent/malformed.</summary>
        private static string ExtractTagContent(string text, string tag)
        {
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }
            start += open.Length;
            int end = text.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                return string.Empty;
            }
            return text.Substring(start, end - start).Trim();
        }

        private static void HandleUserLine(JsonNode node, LoaderState state)
        {
            JsonNode content = node["message"]["content"];
            ChatMessage userMessage = null;
            foreach (JsonNode item in NormalizeContent(content))
            {
                string itemType = item.IsObject ? item["type"].AsString(string.Empty) : "text";
                if (itemType == "tool_result")
                {
                    CompleteToolCall(node, item, state);
                    continue;
                }
                if (itemType == "image")
                {
                    // An image the user attached (2026-09-07): the JSONL
                    // carries the base64 the panel sent; write it back into
                    // the attachment store and show the same thumbnail.
                    string imagePath = RestoreImage(item);
                    if (imagePath != null)
                    {
                        userMessage = EnsureUserMessage(node, state, userMessage);
                        userMessage.Add(ChatMessageBlock.MakeImage(imagePath, string.Empty));
                    }
                    continue;
                }
                // Anything else that is not a tool_result is treated as
                // user-authored text (the shape AgentHub sends: a
                // {"type":"text"} block, optionally followed by images).
                string text;
                if (item.IsObject)
                {
                    text = itemType == "text" ? item["text"].AsString(string.Empty) : string.Empty;
                }
                else
                {
                    text = item.AsString(string.Empty);
                }
                // MODEL-6: transcripts written by the CLI's TERMINAL
                // sessions (History lists those too) wrap slash-command
                // invocations and their local output in scaffold tags that
                // are not user prose. Rendering them raw showed the user
                // "<command-name>/model</command-name>..." as their own
                // words. Normalize: a command wrapper becomes its visible
                // "/name args" form; local command output vanishes.
                string scaffoldVisible;
                if (TryNormalizeCliScaffold(text, out scaffoldVisible))
                {
                    text = scaffoldVisible;
                }
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                userMessage = EnsureUserMessage(node, state, userMessage);
                AppendUserTextWithContextSplit(userMessage, text);
            }
        }

        /// <summary>
        /// The user bubble for this line, created on first real content. A
        /// genuine user send closes whatever assistant bubble was
        /// accumulating (mirroring AgentHub.SendUserMessage's
        /// _streamingAssistant = null) and starts a new usage turn; tool
        /// results never reach here, so they never inflate the turn count.
        /// </summary>
        private static ChatMessage EnsureUserMessage(JsonNode node, LoaderState state, ChatMessage existing)
        {
            if (existing != null)
            {
                return existing;
            }
            state.CurrentAssistant = null;
            state.Usage.BeginTurn();
            var message = new ChatMessage
            {
                role = ChatMessage.RoleUser,
                timestamp = node["timestamp"].AsString(string.Empty),
                turnId = 0,
                delivered = true
            };
            state.Messages.Add(message);
            return message;
        }

        /// <summary>
        /// Decodes a {"type":"image","source":{"type":"base64",...}} block
        /// through <see cref="ImageSaver"/>. Null (skip the block) when no
        /// saver is set, the block is not base64, the payload is empty or
        /// over <see cref="MaxRestoredImageBase64Chars"/>, or the saver
        /// throws -- a broken image must never break the whole restore.
        /// </summary>
        internal static string RestoreImage(JsonNode item)
        {
            if (ImageSaver == null || item == null || !item.IsObject)
            {
                return null;
            }
            JsonNode source = item["source"];
            if (source == null || !source.IsObject || source["type"].AsString(string.Empty) != "base64")
            {
                return null;
            }
            string data = source["data"].AsString(null);
            if (string.IsNullOrEmpty(data) || data.Length > MaxRestoredImageBase64Chars)
            {
                return null;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(data);
                if (bytes.Length == 0)
                {
                    return null;
                }
                return ImageSaver(bytes, source["media_type"].AsString("image/png"));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AppendUserTextWithContextSplit(ChatMessage message, string text)
        {
            int headerIndex = text.IndexOf(ContextHeaderMarker, StringComparison.Ordinal);
            if (headerIndex < 0)
            {
                message.Add(ChatMessageBlock.MakeText(text));
                return;
            }
            string visible = text.Substring(0, headerIndex);
            if (!string.IsNullOrEmpty(visible))
            {
                message.Add(ChatMessageBlock.MakeText(visible));
            }
            string rest = text.Substring(headerIndex + ContextHeaderMarker.Length);
            int footerIndex = rest.IndexOf(ContextFooterLiteral, StringComparison.Ordinal);
            string payload = footerIndex >= 0 ? rest.Substring(0, footerIndex) : rest;
            payload = payload.Trim('\n', '\r', ' ');
            if (!string.IsNullOrEmpty(payload))
            {
                // The wire format (ContextBlockFormatter.Compose) never
                // preserves the original per-chip titles -- only the
                // payload text survives -- so restored history can only
                // show one generic collapsed block, not the original chips.
                message.Add(ChatMessageBlock.MakeContextAttachment(
                    "Restored context", payload));
            }
        }

        private static void CompleteToolCall(JsonNode userNode, JsonNode toolResultItem, LoaderState state)
        {
            string toolUseId = toolResultItem["tool_use_id"].AsString(string.Empty);
            if (string.IsNullOrEmpty(toolUseId))
            {
                return;
            }
            ToolCallRecord record;
            if (!state.OpenToolCalls.TryGetValue(toolUseId, out record))
            {
                return;
            }
            state.OpenToolCalls.Remove(toolUseId);
            string summary = ExtractResultSummary(toolResultItem["content"]);
            bool isError = toolResultItem["is_error"].AsBool();
            long ticks = ParseTimestampTicks(userNode["timestamp"].AsString(null));
            if (ticks <= 0)
            {
                // No parseable completion time: fall back to "started" so
                // ToolCallRecord.Complete's duration guard (now > started)
                // simply skips the duration instead of going negative.
                ticks = record.startedAtUtcTicks;
            }
            record.Complete(isError, summary, ticks);
            record.resultImagePaths = RestoreToolResultImages(toolResultItem["content"]);

            if (record.subagent != null)
            {
                // The Agent tool_result's own envelope (sibling of
                // "message", NOT inside it) carries the subagent's final
                // status/usage/summary (R02c section 1 point 4).
                //
                // The SAME envelope has two spellings and this reader sees
                // the disk one (measured 2026-08-02, design note
                // 2026-08-02-subagent-card-not-expandable.md): the stdout
                // WIRE stream spells it "tool_use_result" (real captures in
                // Tests/Editor/Fixtures/*_inbound.jsonl), the on-disk
                // transcript JSONL this class parses spells it
                // "toolUseResult". Reading only the wire spelling here
                // silently dropped every restored subagent's status, usage
                // and summary -- which in turn made SubagentCard compute
                // hasDetails == false and render a permanently inert card.
                // Both are accepted so the reader stays correct whichever
                // serialization it is handed.
                JsonNode toolUseResult = userNode["toolUseResult"];
                if (!toolUseResult.IsObject)
                {
                    toolUseResult = userNode["tool_use_result"];
                }
                if (toolUseResult.IsObject)
                {
                    ApplySubagentCompletion(record.subagent, toolUseResult);
                }
                else if (record.subagent.status == "running")
                {
                    record.subagent.status = isError ? "failed" : "completed";
                }
            }
        }

        /// <summary>
        /// Restores a subagent's final state from the top-level Agent
        /// tool_result's envelope ("toolUseResult" on disk,
        /// "tool_use_result" on the wire -- see the caller) (design note section
        /// 6): status, usage totals and a summary (the envelope's first
        /// text content item -- the same "content[0].text" the design note
        /// specifies, before the boilerplate "agentId: ..." / usage text
        /// the CLI appends as a second item, see R02c section 1 point 4).
        /// </summary>
        private static void ApplySubagentCompletion(SubagentRecord subagent, JsonNode toolUseResult)
        {
            string status = toolUseResult["status"].AsString(string.Empty);
            if (!string.IsNullOrEmpty(status))
            {
                subagent.status = status;
            }
            else if (subagent.status == "running")
            {
                subagent.status = "completed";
            }
            if (string.IsNullOrEmpty(subagent.subagentType))
            {
                subagent.subagentType = toolUseResult["agentType"].AsString(subagent.subagentType);
            }
            subagent.totalTokens = toolUseResult["totalTokens"].AsLong(subagent.totalTokens);
            subagent.toolUses = toolUseResult["totalToolUseCount"].AsInt(subagent.toolUses);
            subagent.durationMs = toolUseResult["totalDurationMs"].AsLong(subagent.durationMs);
            JsonNode content = toolUseResult["content"];
            if (content.IsArray)
            {
                foreach (JsonNode item in content.Items)
                {
                    if (item.IsObject && item["type"].AsString(string.Empty) == "text")
                    {
                        string text = item["text"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(text))
                        {
                            subagent.summaryMarkdown = text;
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Same rule as AgentHub.IsSubagentSpawnTool (Model must not
        /// depend on Integration, so this is a deliberate, small duplicate).</summary>
        private static bool IsSubagentSpawnTool(string toolName, JsonNode input)
        {
            if (string.Equals(toolName, "Agent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "Task", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return input != null && input.IsObject && input.HasKey("subagent_type");
        }

        /// <summary>
        /// One-line summary of a tool_result "content": the string itself,
        /// or the first non-empty text block of an array. Shared with
        /// AgentHub's live path so both spell a result the same way.
        /// </summary>
        public static string ExtractResultSummary(JsonNode content)
        {
            if (content == null)
            {
                return string.Empty;
            }
            if (content.IsString)
            {
                return Truncate(content.AsString(string.Empty), SummaryMaxChars);
            }
            if (content.IsArray)
            {
                foreach (JsonNode item in content.Items)
                {
                    if (item.IsObject && item["type"].AsString(string.Empty) == "text")
                    {
                        string text = item["text"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return Truncate(text, SummaryMaxChars);
                        }
                    }
                }
            }
            return string.Empty;
        }

        // -- "assistant" lines ------------------------------------------------------

        private static void HandleAssistantLine(JsonNode node, LoaderState state)
        {
            JsonNode content = node["message"]["content"];
            if (!content.IsArray)
            {
                return;
            }
            ChatMessage target = state.CurrentAssistant;
            if (target == null)
            {
                target = new ChatMessage
                {
                    role = ChatMessage.RoleAssistant,
                    timestamp = node["timestamp"].AsString(string.Empty),
                    turnId = 0
                };
                state.Messages.Add(target);
                state.CurrentAssistant = target;
            }

            foreach (JsonNode block in content.Items)
            {
                if (!block.IsObject)
                {
                    continue;
                }
                string blockType = block["type"].AsString(string.Empty);
                switch (blockType)
                {
                    case "text":
                        {
                            string text = block["text"].AsString(string.Empty);
                            if (!string.IsNullOrEmpty(text))
                            {
                                target.Add(ChatMessageBlock.MakeText(text));
                            }
                            break;
                        }
                    case "thinking":
                        {
                            string thinking = block["thinking"].AsString(string.Empty);
                            if (!string.IsNullOrEmpty(thinking))
                            {
                                target.Add(ChatMessageBlock.MakeThinking(thinking));
                            }
                            break;
                        }
                    case "tool_use":
                        {
                            string id = block["id"].AsString(string.Empty);
                            if (!string.IsNullOrEmpty(id) && !state.OpenToolCalls.ContainsKey(id))
                            {
                                string name = block["name"].AsString(string.Empty);
                                JsonNode input = block["input"];
                                string inputJson = !input.IsNull
                                    ? JsonWriter.Write(input) : string.Empty;
                                var record = new ToolCallRecord
                                {
                                    toolUseId = id,
                                    toolName = name,
                                    inputJson = inputJson,
                                    inputSummary = Truncate(inputJson, SummaryMaxChars),
                                    status = ToolCallStatus.Running,
                                    startedAtUtcTicks = ParseTimestampTicks(
                                        node["timestamp"].AsString(null))
                                };
                                // The main transcript only ever contains
                                // TOP-LEVEL lines (R02c section 3: sidechain
                                // rows live in a separate subagents/*.jsonl
                                // file), so every tool_use seen here is
                                // depth 0 -- no cutoff needed, unlike the
                                // live AgentHub path.
                                if (IsSubagentSpawnTool(name, input))
                                {
                                    record.subagent = new SubagentRecord
                                    {
                                        toolUseId = id,
                                        subagentType = input["subagent_type"].AsString(string.Empty),
                                        description = input["description"].AsString(string.Empty)
                                    };
                                }
                                state.OpenToolCalls[id] = record;
                                target.Add(ChatMessageBlock.MakeToolCall(record));
                            }
                            break;
                        }
                    default:
                        // Unknown/unsupported block types (images, etc.)
                        // are skipped, matching AgentHub's default case.
                        break;
                }
            }

            string model = node["message"]["model"].AsString(string.Empty);
            string error = node["error"].AsString(null);
            bool synthetic = model == "<synthetic>";
            if (!string.IsNullOrEmpty(error) || synthetic)
            {
                target.Add(ChatMessageBlock.MakeError(
                    "CLI error: " + (error ?? "synthetic response")));
            }
        }

        // -- Subagent sidechain restore (design note section 6) ---------------------

        /// <summary>
        /// Locates the sidechain transcript for one subagent spawn: scans
        /// "&lt;sessionDir&gt;/subagents/agent-*.meta.json" for the entry whose
        /// "toolUseId" matches, then returns the sibling ".jsonl" file's
        /// path. Returns null (never throws) when the subagents directory
        /// is missing, unreadable, or has no matching entry -- callers must
        /// degrade to summary-only display, not surface an error
        /// (acceptance criteria 5). mainTranscriptFilePath is the SAME
        /// "&lt;projectDir&gt;/&lt;sessionId&gt;.jsonl" path TranscriptLoader.Load
        /// itself was given; the sidechain directory is a same-named
        /// sibling directory (R02c section 3).
        /// </summary>
        public static string FindSubagentJsonlPath(string mainTranscriptFilePath, string toolUseId)
        {
            if (string.IsNullOrEmpty(mainTranscriptFilePath) || string.IsNullOrEmpty(toolUseId))
            {
                return null;
            }
            try
            {
                string projectDir = Path.GetDirectoryName(mainTranscriptFilePath);
                string sessionId = Path.GetFileNameWithoutExtension(mainTranscriptFilePath);
                if (string.IsNullOrEmpty(projectDir) || string.IsNullOrEmpty(sessionId))
                {
                    return null;
                }
                string subagentsDir = Path.Combine(projectDir, sessionId, "subagents");
                if (!Directory.Exists(subagentsDir))
                {
                    return null;
                }
                const string metaSuffix = ".meta.json";
                foreach (string metaPath in Directory.GetFiles(subagentsDir, "agent-*.meta.json"))
                {
                    JsonNode meta;
                    try
                    {
                        meta = JsonParser.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
                    }
                    catch (Exception)
                    {
                        // One unreadable/malformed meta file must not block
                        // matching the rest (permission race, partial write).
                        continue;
                    }
                    if (!meta.IsObject
                        || meta["toolUseId"].AsString(string.Empty) != toolUseId)
                    {
                        continue;
                    }
                    string fileName = Path.GetFileName(metaPath);
                    if (fileName.EndsWith(metaSuffix, StringComparison.Ordinal))
                    {
                        fileName = fileName.Substring(0, fileName.Length - metaSuffix.Length) + ".jsonl";
                    }
                    return Path.Combine(subagentsDir, fileName);
                }
            }
            catch (Exception)
            {
                // Any other IO failure (missing project dir entirely, a
                // permission error on the directory itself, ...) degrades
                // to "no sidechain found", never an exception.
            }
            return null;
        }

        /// <summary>
        /// Lazily loads one subagent's nested transcript (a
        /// "subagents/agent-&lt;taskId&gt;.jsonl" sidechain file, all lines
        /// isSidechain:true) into a flat block list, reusing the same
        /// content shapes HandleAssistantLine/CompleteToolCall already
        /// understand. Never throws: a missing/unreadable file returns an
        /// empty list (acceptance criteria 5's summary-only degradation).
        /// The subagent's own initiating user line (its "prompt", the first
        /// line of every capture) is intentionally skipped -- it duplicates
        /// the spawning Agent tool_use's own "prompt" input, already visible
        /// on the outer card, matching the live-path rule in
        /// AgentHub (parent-tagged user text is never nested either).
        /// </summary>
        public static List<ChatMessageBlock> LoadSubagentBlocks(string filePath)
        {
            int discardedDroppedCount;
            return LoadSubagentBlocks(filePath, out discardedDroppedCount);
        }

        /// <summary>
        /// Same as <see cref="LoadSubagentBlocks(string)"/>, but also
        /// reports how many of the oldest blocks were truncated by the
        /// MaxNestedBlocks cap (0 when the sidechain fit entirely). Callers
        /// that lazily append the result onto a live SubagentRecord (see
        /// SubagentCard.TryLazyLoadNestedBlocks) MUST feed this into
        /// droppedBlockCount themselves -- unlike SubagentRecord.AddBlock,
        /// this method builds the list before any SubagentRecord exists, so
        /// it cannot increment droppedBlockCount on the caller's behalf.
        /// </summary>
        public static List<ChatMessageBlock> LoadSubagentBlocks(string filePath, out int droppedCount)
        {
            droppedCount = 0;
            var blocks = new List<ChatMessageBlock>();
            if (string.IsNullOrEmpty(filePath))
            {
                return blocks;
            }
            IEnumerable<string> lines;
            try
            {
                if (!File.Exists(filePath))
                {
                    return blocks;
                }
                lines = File.ReadLines(filePath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return blocks;
            }

            var openToolCalls = new Dictionary<string, ToolCallRecord>();
            try
            {
                foreach (string rawLine in lines)
                {
                    ProcessSubagentLine(rawLine, blocks, openToolCalls);
                }
            }
            catch (IOException)
            {
                // Partial read is still useful; same tolerance as Load().
            }
            catch (UnauthorizedAccessException)
            {
            }

            foreach (KeyValuePair<string, ToolCallRecord> pair in openToolCalls)
            {
                if (pair.Value.status == ToolCallStatus.Running)
                {
                    pair.Value.status = ToolCallStatus.Pending;
                }
            }
            if (blocks.Count > SubagentRecord.MaxNestedBlocks)
            {
                droppedCount = blocks.Count - SubagentRecord.MaxNestedBlocks;
                return blocks.GetRange(blocks.Count - SubagentRecord.MaxNestedBlocks,
                    SubagentRecord.MaxNestedBlocks);
            }
            return blocks;
        }

        private static void ProcessSubagentLine(string rawLine, List<ChatMessageBlock> blocks,
            Dictionary<string, ToolCallRecord> openToolCalls)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return;
            }
            JsonNode node;
            try
            {
                node = JsonParser.Parse(rawLine);
            }
            catch (JsonParseException)
            {
                return;
            }
            if (!node.IsObject)
            {
                return;
            }
            string type = node["type"].AsString(string.Empty);
            if (type == "user")
            {
                foreach (JsonNode item in NormalizeContent(node["message"]["content"]))
                {
                    if (item.IsObject && item["type"].AsString(string.Empty) == "tool_result")
                    {
                        CompleteSubagentToolCall(node, item, openToolCalls);
                    }
                    // Plain user text (the initiating prompt, or any
                    // mid-conversation text) is never nested -- see the
                    // LoadSubagentBlocks doc comment.
                }
                return;
            }
            if (type != "assistant")
            {
                // attachment / other sidechain-only line kinds: skipped.
                return;
            }
            JsonNode content = node["message"]["content"];
            if (!content.IsArray)
            {
                return;
            }
            foreach (JsonNode block in content.Items)
            {
                if (!block.IsObject)
                {
                    continue;
                }
                switch (block["type"].AsString(string.Empty))
                {
                    case "text":
                        string text = block["text"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(text))
                        {
                            blocks.Add(ChatMessageBlock.MakeText(text));
                        }
                        break;
                    case "thinking":
                        string thinking = block["thinking"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            blocks.Add(ChatMessageBlock.MakeThinking(thinking));
                        }
                        break;
                    case "tool_use":
                        {
                            string id = block["id"].AsString(string.Empty);
                            if (string.IsNullOrEmpty(id) || openToolCalls.ContainsKey(id))
                            {
                                break;
                            }
                            JsonNode input = block["input"];
                            string inputJson = !input.IsNull ? JsonWriter.Write(input) : string.Empty;
                            var record = new ToolCallRecord
                            {
                                toolUseId = id,
                                toolName = block["name"].AsString(string.Empty),
                                inputJson = inputJson,
                                inputSummary = Truncate(inputJson, SummaryMaxChars),
                                status = ToolCallStatus.Running,
                                startedAtUtcTicks = ParseTimestampTicks(node["timestamp"].AsString(null))
                            };
                            // Depth-1 cutoff (design note section 3): a
                            // grandchild spawn (an Agent/Task tool_use INSIDE
                            // a subagent) renders as a plain card, never a
                            // nested SubagentRecord.
                            openToolCalls[id] = record;
                            blocks.Add(ChatMessageBlock.MakeToolCall(record));
                            break;
                        }
                    default:
                        break;
                }
            }
        }

        private static void CompleteSubagentToolCall(JsonNode userNode, JsonNode toolResultItem,
            Dictionary<string, ToolCallRecord> openToolCalls)
        {
            string toolUseId = toolResultItem["tool_use_id"].AsString(string.Empty);
            if (string.IsNullOrEmpty(toolUseId))
            {
                return;
            }
            ToolCallRecord record;
            if (!openToolCalls.TryGetValue(toolUseId, out record))
            {
                return;
            }
            openToolCalls.Remove(toolUseId);
            string summary = ExtractResultSummary(toolResultItem["content"]);
            bool isError = toolResultItem["is_error"].AsBool();
            long ticks = ParseTimestampTicks(userNode["timestamp"].AsString(null));
            if (ticks <= 0)
            {
                ticks = record.startedAtUtcTicks;
            }
            record.Complete(isError, summary, ticks);
            record.resultImagePaths = RestoreToolResultImages(toolResultItem["content"]);
        }

        /// <summary>
        /// The pictures of a restored tool_result: embedded blocks are
        /// written back through <see cref="ImageSaver"/> (same seam as
        /// user image blocks; nothing is restored when it is unset), a
        /// path named in the text counts when the file still exists.
        /// </summary>
        private static List<string> RestoreToolResultImages(JsonNode content)
        {
            try
            {
                return ToolResultImages.Resolve(content, ImageSaver, ResolveExistingFile);
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static string ResolveExistingFile(string candidate)
        {
            return !string.IsNullOrEmpty(candidate) && Path.IsPathRooted(candidate) && File.Exists(candidate)
                ? candidate : null;
        }

        // -- Small helpers ------------------------------------------------------------

        /// <summary>
        /// Normalizes a "content" field into a sequence of items: passes
        /// an array through as-is, wraps a plain string as a single
        /// synthetic {"type":"text"} node, and yields nothing for anything
        /// else (null/object/number -- none of which the CLI ever sends
        /// here, but D8's "optional-first" rule says never throw on it).
        /// </summary>
        private static IEnumerable<JsonNode> NormalizeContent(JsonNode content)
        {
            if (content.IsArray)
            {
                foreach (JsonNode item in content.Items)
                {
                    yield return item;
                }
            }
            else if (content.IsString)
            {
                yield return JsonNode.NewObject().Set("type", "text").Set("text", content.AsString(string.Empty));
            }
        }

        private static long ParseTimestampTicks(string iso8601)
        {
            if (string.IsNullOrEmpty(iso8601))
            {
                return 0L;
            }
            // RoundtripKind cannot be combined with AdjustToUniversal/
            // AssumeUniversal/AssumeLocal (DateTime.TryParse throws
            // ArgumentException for that combination -- it is a style
            // validation error, not a parse failure, so it is NOT swallowed
            // by TryParse's normal "return false" contract). Every
            // timestamp this loader ever sees carries a "Z" suffix, so
            // RoundtripKind alone already yields Kind=Utc; ToUniversalTime()
            // afterward is then just a safety net for an unexpected
            // offset/local timestamp, not the primary conversion path.
            DateTime parsed;
            if (DateTime.TryParse(iso8601, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToUniversalTime().Ticks;
            }
            return 0L;
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, maxChars - 3) + "...";
        }

        /// <summary>Per-Load() mutable scan state (open tool calls, current assistant bubble, usage).</summary>
        private sealed class LoaderState
        {
            public readonly List<ChatMessage> Messages;
            public readonly TranscriptUsage Usage;
            public readonly Dictionary<string, ToolCallRecord> OpenToolCalls =
                new Dictionary<string, ToolCallRecord>();
            public ChatMessage CurrentAssistant;

            public LoaderState(List<ChatMessage> messages, TranscriptUsage usage)
            {
                Messages = messages;
                Usage = usage;
            }
        }
    }
}
