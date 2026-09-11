using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Round-trip poison suite for the JSON transcript cache. This is the
    /// regression net for the live UnityYAML corruption bug ("Unable to
    /// parse file ...: [Parser Failure at line N: Expected closing '}']"):
    /// every payload here is the kind of message text that broke the old
    /// ScriptableSingleton-embedded transcript. All source literals are
    /// strict ASCII; CJK test data uses \u escapes.
    /// </summary>
    public class SessionCacheFileTests
    {
        private string _dir;
        private string _path;
        private List<string> _logs;
        private SessionCacheFile _cache;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelSessionCacheTests_" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "SessionCache.json");
            _logs = new List<string>();
            _cache = new SessionCacheFile(_path, delegate (string line) { _logs.Add(line); });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        // ---------------------------------------------------------------
        // Poison payloads (the exact content class that broke UnityYAML)
        // ---------------------------------------------------------------

        private static string CjkText()
        {
            // Japanese "hello, this is a test" style content plus emoji.
            return "\u65E5\u672C\u8A9E\u306E\u30C6\u30B9\u30C8 \u3053\u3093\u306B\u3061\u306F"
                + char.ConvertFromUtf32(0x1F600);
        }

        private static string NestedBraces50()
        {
            return new string('{', 50) + "core" + new string('}', 50);
        }

        private static string HundredKbSingleLine()
        {
            var sb = new StringBuilder(110 * 1024);
            while (sb.Length < 100 * 1024)
            {
                sb.Append("{\"key\": \"value\\path\"} {a: b} ");
            }
            Assert.IsTrue(sb.Length >= 100 * 1024);
            Assert.AreEqual(-1, sb.ToString().IndexOf('\n'));
            return sb.ToString();
        }

        private static ChatSession BuildPoisonSession()
        {
            var session = new ChatSession
            {
                sessionId = "sess-1234",
                title = "{title: with braces}",
                totalInputTokens = 1234567890123L,
                totalOutputTokens = 42,
                totalCacheReadInputTokens = 99,
                totalCacheCreationInputTokens = 7,
                totalCostUsd = 1.2345,
                completedTurns = 3,
                lastActivityTimestamp = "2026-07-30T12:00:00.0000000Z"
            };

            // Lines starting with '{' and '}' (YAML flow-map lookalikes).
            var user = new ChatMessage
            {
                role = ChatMessage.RoleUser,
                timestamp = "2026-07-30T11:00:00Z",
                turnId = 1,
                delivered = true
            };
            user.Add(ChatMessageBlock.MakeText("{\n  \"json\": true\n}\n} dangling\n{ leading"));
            // Structured context attachment (title + payload, poison-grade
            // payload text) -- the new display block kind must round-trip.
            user.Add(ChatMessageBlock.MakeContextAttachment(
                "GameObject: Main Camera",
                "GameObject: /Main Camera\n  components: Camera, {braces} \"quotes\""));
            session.AddMessage(user);

            // Nested braces x50, quotes/backslashes, CRLF mixes.
            var assistant = new ChatMessage
            {
                role = ChatMessage.RoleAssistant,
                timestamp = "2026-07-30T11:00:05Z",
                turnId = 1
            };
            assistant.Add(ChatMessageBlock.MakeText(NestedBraces50()));
            assistant.Add(ChatMessageBlock.MakeThinking(
                "quote \" backslash \\ path C:\\Users\\x \\\\server\\share 'single'"));
            assistant.Add(ChatMessageBlock.MakeText("line1\r\nline2\nline3\rline4"));
            assistant.Add(ChatMessageBlock.MakeText("{a: b}"));
            assistant.Add(ChatMessageBlock.MakeToolCall(new ToolCallRecord
            {
                toolUseId = "toolu_01",
                toolName = "Bash",
                inputSummary = "{\"command\":\"echo }\"}",
                inputJson = "{\"command\":\"echo }{\",\"description\":\"poison\"}",
                status = ToolCallStatus.Failed,
                startedAtUtcTicks = 638000000000000000L,
                durationMs = 1234,
                resultSummary = "exit 1: unexpected '}'",
                isError = true
            }));
            session.AddMessage(assistant);

            // 100KB single line + CJK.
            var big = new ChatMessage { role = ChatMessage.RoleAssistant, turnId = 2 };
            big.Add(ChatMessageBlock.MakeText(HundredKbSingleLine()));
            big.Add(ChatMessageBlock.MakeText(CjkText()));
            session.AddMessage(big);

            // Warn-flagged system note (2026-08-14 review finding: the flag
            // was initially dropped by Write/ReadBlock, un-warning the
            // process-died note on the exact reload that made it matter --
            // this block keeps the round-trip assertion non-vacuous).
            var warned = new ChatMessage { role = ChatMessage.RoleSystem, turnId = 2 };
            warned.Add(ChatMessageBlock.MakeSystemNote("Claude CLI process died. Reconnecting...", true));
            session.AddMessage(warned);

            // Null/empty blocks: empty block list, null text, streaming flag.
            var empty = new ChatMessage { role = ChatMessage.RoleSystem, turnId = 2 };
            session.AddMessage(empty);
            var nulls = new ChatMessage { role = ChatMessage.RoleAssistant, turnId = 2 };
            nulls.Add(ChatMessageBlock.MakeText(null, true));
            nulls.Add(new ChatMessageBlock
            {
                kind = ChatBlockKind.Permission,
                text = null,
                toolCall = null
            });
            session.AddMessage(nulls);

            return session;
        }

        private static void AssertSessionsEqual(ChatSession expected, ChatSession actual)
        {
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.sessionId, actual.sessionId);
            Assert.AreEqual(expected.title, actual.title);
            Assert.AreEqual(expected.totalInputTokens, actual.totalInputTokens);
            Assert.AreEqual(expected.totalOutputTokens, actual.totalOutputTokens);
            Assert.AreEqual(expected.totalCacheReadInputTokens, actual.totalCacheReadInputTokens);
            Assert.AreEqual(expected.totalCacheCreationInputTokens, actual.totalCacheCreationInputTokens);
            Assert.AreEqual(expected.totalCostUsd, actual.totalCostUsd, 1e-9);
            Assert.AreEqual(expected.completedTurns, actual.completedTurns);
            Assert.AreEqual(expected.lastActivityTimestamp, actual.lastActivityTimestamp);
            Assert.AreEqual(expected.messages.Count, actual.messages.Count);
            for (int m = 0; m < expected.messages.Count; m++)
            {
                ChatMessage em = expected.messages[m];
                ChatMessage am = actual.messages[m];
                string where = "message[" + m + "]";
                Assert.AreEqual(em.id, am.id, where + ".id");
                Assert.AreEqual(em.role, am.role, where + ".role");
                Assert.AreEqual(em.timestamp, am.timestamp, where + ".timestamp");
                Assert.AreEqual(em.turnId, am.turnId, where + ".turnId");
                Assert.AreEqual(em.delivered, am.delivered, where + ".delivered");
                Assert.AreEqual(em.blocks.Count, am.blocks.Count, where + ".blocks.Count");
                for (int b = 0; b < em.blocks.Count; b++)
                {
                    ChatMessageBlock eb = em.blocks[b];
                    ChatMessageBlock ab = am.blocks[b];
                    string blockWhere = where + ".blocks[" + b + "]";
                    Assert.AreEqual(eb.kind, ab.kind, blockWhere + ".kind");
                    Assert.AreEqual(eb.text ?? string.Empty, ab.text, blockWhere + ".text");
                    Assert.AreEqual(eb.title ?? string.Empty, ab.title, blockWhere + ".title");
                    Assert.AreEqual(eb.streaming, ab.streaming, blockWhere + ".streaming");
                    Assert.AreEqual(eb.thinkingTokens, ab.thinkingTokens, blockWhere + ".thinkingTokens");
                    Assert.AreEqual(eb.thinkingRedacted, ab.thinkingRedacted, blockWhere + ".thinkingRedacted");
                    // 2026-08-14 review finding: this flag was added for the
                    // process-died warn note and initially NOT round-tripped,
                    // which silently un-warned the note on the very reload
                    // that made it matter. Every additive block field must
                    // appear in this list or its loss is invisible to tests.
                    Assert.AreEqual(eb.warning, ab.warning, blockWhere + ".warning");
                    Assert.AreEqual(eb.toolCall == null, ab.toolCall == null,
                        blockWhere + ".toolCall nullness");
                    if (eb.toolCall != null)
                    {
                        Assert.AreEqual(eb.toolCall.toolUseId, ab.toolCall.toolUseId);
                        Assert.AreEqual(eb.toolCall.toolName, ab.toolCall.toolName);
                        Assert.AreEqual(eb.toolCall.inputSummary, ab.toolCall.inputSummary);
                        Assert.AreEqual(eb.toolCall.inputJson, ab.toolCall.inputJson);
                        Assert.AreEqual(eb.toolCall.status, ab.toolCall.status);
                        Assert.AreEqual(eb.toolCall.startedAtUtcTicks, ab.toolCall.startedAtUtcTicks);
                        Assert.AreEqual(eb.toolCall.durationMs, ab.toolCall.durationMs);
                        Assert.AreEqual(eb.toolCall.resultSummary, ab.toolCall.resultSummary);
                        Assert.AreEqual(eb.toolCall.isError, ab.toolCall.isError);
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        // Subagent round trip (Phase 4)
        // ---------------------------------------------------------------

        private static ChatSession BuildSubagentSession()
        {
            var session = new ChatSession { sessionId = "sess-sub" };
            var assistant = new ChatMessage { role = ChatMessage.RoleAssistant, turnId = 1 };
            var subagent = new SubagentRecord
            {
                taskId = "task_1",
                toolUseId = "toolu_agent",
                subagentType = "general-purpose",
                description = "Run echo fixture command",
                status = "completed",
                progressLine = "Running Echo fixture string",
                lastToolName = "Bash",
                totalTokens = 21038,
                toolUses = 1,
                durationMs = 4801,
                summaryMarkdown = "Command ran successfully:\n```\nSUBAGENT_FIXTURE_OK\n```",
                droppedBlockCount = 2
            };
            subagent.AddBlock(ChatMessageBlock.MakeToolCall(new ToolCallRecord
            {
                toolUseId = "toolu_bash",
                toolName = "Bash",
                inputJson = "{\"command\":\"echo SUBAGENT_FIXTURE_OK\"}",
                status = ToolCallStatus.Succeeded,
                resultSummary = "SUBAGENT_FIXTURE_OK"
            }));
            subagent.AddBlock(ChatMessageBlock.MakeText("Command ran successfully."));
            assistant.Add(ChatMessageBlock.MakeToolCall(new ToolCallRecord
            {
                toolUseId = "toolu_agent",
                toolName = "Agent",
                inputJson = "{\"subagent_type\":\"general-purpose\"}",
                status = ToolCallStatus.Succeeded,
                subagent = subagent
            }));
            session.AddMessage(assistant);
            return session;
        }

        // -- Per-model usage snapshot (2026-08-05: 'tokens show as none on the
        // first panel open after launching the project'). Session TOTALS were
        // always cached; the per-model breakdown -- the usage popover's and
        // context meter's only data source -- lived in a plain static and
        // died with every editor process. These pin the round trip and, more
        // importantly, the read of a cache that predates the key.

        [Test]
        public void RoundTrip_ModelUsage_PreservesEveryFieldPerModel()
        {
            var session = new ChatSession { sessionId = "mu" };
            var usage = new Dictionary<string, ModelUsage>
            {
                { "claude-opus-5", new ModelUsage { InputTokens = 11, OutputTokens = 22,
                    CacheReadInputTokens = 33, CacheCreationInputTokens = 44,
                    WebSearchRequests = 2, CostUsd = 1.25, ContextWindow = 200000,
                    MaxOutputTokens = 32000 } },
                { "claude-haiku-4-5", new ModelUsage { InputTokens = 5, OutputTokens = 6 } },
            };
            _cache.Save(session, usage);

            Dictionary<string, ModelUsage> loaded;
            Assert.IsNotNull(_cache.Load(out loaded));
            Assert.AreEqual(2, loaded.Count);
            ModelUsage opus = loaded["claude-opus-5"];
            Assert.AreEqual(11, opus.InputTokens);
            Assert.AreEqual(22, opus.OutputTokens);
            Assert.AreEqual(33, opus.CacheReadInputTokens);
            Assert.AreEqual(44, opus.CacheCreationInputTokens);
            Assert.AreEqual(2, opus.WebSearchRequests);
            Assert.AreEqual(1.25, opus.CostUsd, 0.0001);
            Assert.AreEqual(200000, opus.ContextWindow,
                "the context meter cannot revive at boot without this field");
            Assert.AreEqual(32000, opus.MaxOutputTokens);
            Assert.AreEqual(6, loaded["claude-haiku-4-5"].OutputTokens);
        }

        [Test]
        public void Load_CacheWrittenWithoutModelUsage_YieldsEmptyDictionary_NeverNull()
        {
            // Every cache written before this key existed takes this path on
            // the first boot after upgrading -- it must read as 'no per-turn
            // data yet', not as an error and not as null.
            var session = new ChatSession { sessionId = "old" };
            _cache.Save(session);

            Dictionary<string, ModelUsage> loaded;
            Assert.IsNotNull(_cache.Load(out loaded));
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.Count);
        }

        [Test]
        public void Save_TwoArg_WithNullUsage_WritesNoKey_AndOneArgDelegates()
        {
            var session = new ChatSession { sessionId = "nul" };
            _cache.Save(session, null);
            StringAssert.DoesNotContain("lastModelUsage", File.ReadAllText(_path),
                "null/empty usage must not write an empty object the reader has to special-case");
        }

        [Test]
        public void RoundTrip_SubagentRecord_PreservesEverything()
        {
            ChatSession original = BuildSubagentSession();
            _cache.Save(original);
            ChatSession loaded = _cache.Load();

            Assert.IsNotNull(loaded);
            ToolCallRecord loadedTool = loaded.messages[0].blocks[0].toolCall;
            Assert.IsNotNull(loadedTool);
            Assert.IsNotNull(loadedTool.subagent, "subagent must survive the round trip");

            SubagentRecord expected = original.messages[0].blocks[0].toolCall.subagent;
            SubagentRecord actual = loadedTool.subagent;
            Assert.AreEqual(expected.taskId, actual.taskId);
            Assert.AreEqual(expected.toolUseId, actual.toolUseId);
            Assert.AreEqual(expected.subagentType, actual.subagentType);
            Assert.AreEqual(expected.description, actual.description);
            Assert.AreEqual(expected.status, actual.status);
            Assert.AreEqual(expected.progressLine, actual.progressLine);
            Assert.AreEqual(expected.lastToolName, actual.lastToolName);
            Assert.AreEqual(expected.totalTokens, actual.totalTokens);
            Assert.AreEqual(expected.toolUses, actual.toolUses);
            Assert.AreEqual(expected.durationMs, actual.durationMs);
            Assert.AreEqual(expected.summaryMarkdown, actual.summaryMarkdown);
            Assert.AreEqual(expected.droppedBlockCount, actual.droppedBlockCount);

            Assert.AreEqual(2, actual.blocks.Count);
            Assert.AreEqual(ChatBlockKind.ToolCall, actual.blocks[0].kind);
            Assert.AreEqual("Bash", actual.blocks[0].toolCall.toolName);
            Assert.IsNull(actual.blocks[0].toolCall.subagent, "nested tool calls never carry their own subagent");
            Assert.AreEqual("SUBAGENT_FIXTURE_OK", actual.blocks[0].toolCall.resultSummary);
            Assert.AreEqual(ChatBlockKind.Text, actual.blocks[1].kind);
            Assert.AreEqual("Command ran successfully.", actual.blocks[1].text);

            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_ToolCallWithoutSubagentKey_BackwardCompatible()
        {
            // Pre-Phase-4 cache shape: the "subagent" key is simply absent.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"sessionId\":\"s\",\"messages\":[{\"role\":\"assistant\","
                + "\"blocks\":[{\"kind\":\"ToolCall\",\"toolCall\":{\"toolUseId\":\"t1\","
                + "\"toolName\":\"Bash\",\"status\":\"Succeeded\"}}]}]}");
            ChatSession loaded = _cache.Load();
            Assert.IsNotNull(loaded);
            ToolCallRecord tool = loaded.messages[0].blocks[0].toolCall;
            Assert.IsNotNull(tool);
            Assert.IsNull(tool.subagent, "old caches load with subagent == null, rendering as before");
        }

        [Test]
        public void Save_ToolCallWithoutSubagent_OmitsSubagentKeyEntirely()
        {
            // Ordinary tool calls must serialize byte-identically to before
            // Phase 4 -- no stray "subagent" key for the common case.
            var session = new ChatSession();
            var message = new ChatMessage { role = ChatMessage.RoleAssistant };
            message.Add(ChatMessageBlock.MakeToolCall(new ToolCallRecord
            {
                toolUseId = "t1",
                toolName = "Bash",
                status = ToolCallStatus.Succeeded
            }));
            session.AddMessage(message);
            _cache.Save(session);

            string json = File.ReadAllText(_path);
            StringAssert.DoesNotContain("\"subagent\"", json);
        }

        // ---------------------------------------------------------------
        // Round trips
        // ---------------------------------------------------------------

        [Test]
        public void RoundTrip_PoisonSession_PreservesEverything()
        {
            ChatSession original = BuildPoisonSession();
            _cache.Save(original);
            ChatSession loaded = _cache.Load();
            AssertSessionsEqual(original, loaded);
            Assert.IsEmpty(_logs, "no failures should be logged: "
                + string.Join(" | ", _logs.ToArray()));
        }

        [Test]
        public void RoundTrip_EmptySession_Works()
        {
            _cache.Save(new ChatSession());
            ChatSession loaded = _cache.Load();
            AssertSessionsEqual(new ChatSession(), loaded);
        }

        // ---------------------------------------------------------------
        // Thinking-token estimate (design note 2026-08-01-thinking-content-
        // loss.md section 5) -- additive field round trip + backward compat.
        // ---------------------------------------------------------------

        [Test]
        public void RoundTrip_ThinkingTokens_Preserved()
        {
            var session = new ChatSession { sessionId = "sess-thinking" };
            var assistant = new ChatMessage { role = ChatMessage.RoleAssistant, turnId = 1 };
            ChatMessageBlock thinking = ChatMessageBlock.MakeThinking(string.Empty);
            thinking.thinkingTokens = 143;
            assistant.Add(thinking);
            assistant.Add(ChatMessageBlock.MakeText("done"));
            session.AddMessage(assistant);

            _cache.Save(session);
            ChatSession loaded = _cache.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(143, loaded.messages[0].blocks[0].thinkingTokens);
            Assert.AreEqual(0, loaded.messages[0].blocks[1].thinkingTokens,
                "non-thinking blocks default to 0");
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_BlockWithoutThinkingTokensKey_BackwardCompatible()
        {
            // Pre-thinking-tokens cache shape: the key is simply absent.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"sessionId\":\"s\",\"messages\":[{\"role\":\"assistant\","
                + "\"blocks\":[{\"kind\":\"Thinking\",\"text\":\"\",\"streaming\":false}]}]}");
            ChatSession loaded = _cache.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.messages[0].blocks[0].thinkingTokens,
                "old caches load with thinkingTokens == 0, rendering as the plain "
                    + "no-estimate indicator instead of throwing/defaulting oddly");
        }

        // ---------------------------------------------------------------
        // redacted_thinking (design note 2026-08-01-thinking-content-loss.md
        // section 6) -- additive field round trip + backward compat, same
        // convention as RoundTrip_ThinkingTokens_Preserved above.
        // ---------------------------------------------------------------

        [Test]
        public void RoundTrip_RedactedThinking_Preserved()
        {
            var session = new ChatSession { sessionId = "sess-redacted" };
            var assistant = new ChatMessage { role = ChatMessage.RoleAssistant, turnId = 1 };
            assistant.Add(ChatMessageBlock.MakeRedactedThinking());
            assistant.Add(ChatMessageBlock.MakeText("done"));
            session.AddMessage(assistant);

            _cache.Save(session);
            ChatSession loaded = _cache.Load();

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.messages[0].blocks[0].thinkingRedacted);
            Assert.AreEqual(ChatBlockKind.Thinking, loaded.messages[0].blocks[0].kind);
            Assert.IsFalse(loaded.messages[0].blocks[1].thinkingRedacted,
                "non-redacted blocks default to false");
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_BlockWithoutThinkingRedactedKey_BackwardCompatible()
        {
            // Pre-redacted-thinking cache shape: the key is simply absent.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"sessionId\":\"s\",\"messages\":[{\"role\":\"assistant\","
                + "\"blocks\":[{\"kind\":\"Thinking\",\"text\":\"\",\"streaming\":false}]}]}");
            ChatSession loaded = _cache.Load();
            Assert.IsNotNull(loaded);
            Assert.IsFalse(loaded.messages[0].blocks[0].thinkingRedacted,
                "old caches load with thinkingRedacted == false, rendering as the plain "
                    + "no-estimate indicator instead of the redacted note");
        }

        [Test]
        public void RoundTrip_Twice_SecondSaveWins()
        {
            _cache.Save(BuildPoisonSession());
            var second = new ChatSession { sessionId = "second", title = "t2" };
            _cache.Save(second);
            ChatSession loaded = _cache.Load();
            Assert.AreEqual("second", loaded.sessionId);
            Assert.AreEqual(0, loaded.messages.Count);
        }

        // ---------------------------------------------------------------
        // Atomic write / directory handling
        // ---------------------------------------------------------------

        [Test]
        public void Save_CreatesMissingDirectory_AndLeavesNoTmpFile()
        {
            Assert.IsFalse(Directory.Exists(_dir), "SetUp must not pre-create the directory");
            _cache.Save(BuildPoisonSession());
            Assert.IsTrue(Directory.Exists(_dir));
            Assert.IsTrue(File.Exists(_path));
            Assert.IsFalse(File.Exists(_path + ".tmp"),
                "atomic write must not leave a .tmp behind");
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Save_OverExistingFile_LeavesNoTmpFile()
        {
            _cache.Save(new ChatSession { sessionId = "one" });
            _cache.Save(new ChatSession { sessionId = "two" });
            Assert.IsFalse(File.Exists(_path + ".tmp"));
            Assert.AreEqual("two", _cache.Load().sessionId);
        }

        [Test]
        public void Save_NullSession_IsNoOp()
        {
            _cache.Save(null);
            Assert.IsFalse(File.Exists(_path));
            Assert.IsEmpty(_logs);
        }

        // ---------------------------------------------------------------
        // Tolerant load
        // ---------------------------------------------------------------

        [Test]
        public void Load_MissingFile_ReturnsNullSilently()
        {
            Assert.IsNull(_cache.Load());
            Assert.IsEmpty(_logs, "a missing cache is normal, not an error");
        }

        [Test]
        public void Load_TruncatedJson_ReturnsNullDeletesFileLogsOnce()
        {
            _cache.Save(BuildPoisonSession());
            string json = File.ReadAllText(_path);
            File.WriteAllText(_path, json.Substring(0, json.Length / 2));

            Assert.IsNull(_cache.Load());
            Assert.IsFalse(File.Exists(_path), "the corrupt file must be deleted");
            Assert.AreEqual(1, _logs.Count, "exactly one log line, no spam");

            // A second Load sees no file and stays silent (no log spam).
            Assert.IsNull(_cache.Load());
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_GarbageBytes_ReturnsNullAndDeletes()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(_path, new byte[] { 0xFF, 0xFE, 0x00, 0x13, 0x37, 0x7B, 0x22 });
            Assert.IsNull(_cache.Load());
            Assert.IsFalse(File.Exists(_path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_NonObjectRoot_ReturnsNullAndDeletes()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "[1,2,3]");
            Assert.IsNull(_cache.Load());
            Assert.IsFalse(File.Exists(_path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_ContextAttachmentKind_MapsExplicitly()
        {
            // Explicit mapping regression: the enum-by-name reader must
            // resolve the new block kind (not fall back to Text) and read
            // its title alongside the payload.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"sessionId\":\"s\",\"messages\":[{\"role\":\"user\","
                + "\"blocks\":[{\"kind\":\"ContextAttachment\","
                + "\"title\":\"Console errors (3)\",\"text\":\"E1\\nE2\\nE3\"}]}]}");
            ChatSession loaded = _cache.Load();
            Assert.IsNotNull(loaded);
            ChatMessageBlock block = loaded.messages[0].blocks[0];
            Assert.AreEqual(ChatBlockKind.ContextAttachment, block.kind);
            Assert.AreEqual("Console errors (3)", block.title);
            Assert.AreEqual("E1\nE2\nE3", block.text);
        }

        [Test]
        public void Load_LegacyBlockWithoutTitle_DefaultsToEmpty()
        {
            // Caches written before the attachment feature carry no
            // "title" field; old raw-delimiter text keeps rendering as a
            // plain Text block (no migration parsing).
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"sessionId\":\"s\",\"messages\":[{\"role\":\"user\","
                + "\"blocks\":[{\"kind\":\"Text\","
                + "\"text\":\"hi\\n\\n===== ATTACHED UNITY EDITOR CONTEXT =====\\nx\"}]}]}");
            ChatSession loaded = _cache.Load();
            Assert.IsNotNull(loaded);
            ChatMessageBlock block = loaded.messages[0].blocks[0];
            Assert.AreEqual(ChatBlockKind.Text, block.kind);
            Assert.AreEqual(string.Empty, block.title);
            StringAssert.Contains("ATTACHED UNITY EDITOR CONTEXT", block.text);
        }

        [Test]
        public void Load_UnknownEnumNames_FallBackTolerantly()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"sessionId\":\"s\",\"messages\":[{\"role\":\"assistant\","
                + "\"blocks\":[{\"kind\":\"FutureKind\",\"text\":\"x\","
                + "\"toolCall\":{\"toolName\":\"T\",\"status\":\"FutureStatus\"}}]}]}");
            ChatSession loaded = _cache.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(ChatBlockKind.Text, loaded.messages[0].blocks[0].kind);
            Assert.AreEqual(ToolCallStatus.Pending, loaded.messages[0].blocks[0].toolCall.status);
        }

        // ---------------------------------------------------------------
        // PanelStateStore must never serialize transcript content again
        // ---------------------------------------------------------------

        [Test]
        public void PanelStateStore_SerializedGraph_ContainsNoTranscriptTypes()
        {
            var forbidden = new HashSet<Type>
            {
                typeof(ChatSession),
                typeof(ChatMessage),
                typeof(ChatMessageBlock),
                typeof(ToolCallRecord)
            };
            var visited = new HashSet<Type>();
            var queue = new Queue<Type>();
            queue.Enqueue(typeof(PanelStateStore));
            while (queue.Count > 0)
            {
                Type type = queue.Dequeue();
                if (!visited.Add(type))
                {
                    continue;
                }
                Assert.IsFalse(forbidden.Contains(type),
                    type.FullName + " must not appear in PanelStateStore's serialized"
                    + " graph: message content goes through SessionCacheFile, never"
                    + " UnityYAML (State.asset corruption regression).");
                foreach (FieldInfo field in SerializedFieldsOf(type))
                {
                    foreach (Type inner in ReferencedTypes(field.FieldType))
                    {
                        queue.Enqueue(inner);
                    }
                }
            }
            Assert.IsTrue(visited.Contains(typeof(PanelSettings)),
                "sanity: the walker must actually traverse into PanelSettings");
        }

        private static IEnumerable<FieldInfo> SerializedFieldsOf(Type type)
        {
            for (Type t = type; t != null && t != typeof(object)
                && t != typeof(ScriptableObject); t = t.BaseType)
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    bool serialized = field.IsPublic
                        ? !field.IsDefined(typeof(NonSerializedAttribute), false)
                        : field.IsDefined(typeof(SerializeField), false);
                    if (serialized)
                    {
                        yield return field;
                    }
                }
            }
        }

        /// <summary>Field type unwrapped to the project types Unity would serialize into.</summary>
        private static IEnumerable<Type> ReferencedTypes(Type fieldType)
        {
            Type type = fieldType;
            if (type.IsArray)
            {
                type = type.GetElementType();
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                type = type.GetGenericArguments()[0];
            }
            if (type == null || type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                yield break;
            }
            if (type.Namespace != null && type.Namespace.StartsWith("Colloid."))
            {
                yield return type;
            }
        }
    }
}
