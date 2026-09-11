using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// TranscriptLoader: session JSONL -&gt; ChatMessage restoration. Covers
    /// both real captured fixtures (Tests/Editor/Fixtures/*.jsonl -- stdout
    /// captures of the live protocol, which share the same message.content
    /// shapes as the on-disk transcript format) and a synthetic torture
    /// file exercising the edge cases real fixtures don't happen to hit
    /// (truncated last line, unknown types, a huge single line, CJK).
    /// See docs/design-notes/2026-07-31-session-history-restore.md.
    /// All source literals are strict ASCII; CJK test data uses \u escapes
    /// (same convention as SessionCacheFileTests).
    /// </summary>
    public class TranscriptLoaderTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelTranscriptLoaderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private string WriteFile(params string[] lines)
        {
            string path = Path.Combine(_dir, "session.jsonl");
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
            return path;
        }

        // ---------------------------------------------------------------
        // Missing / empty input
        // ---------------------------------------------------------------

        [Test]
        public void Load_MissingFile_ReturnsEmptyList()
        {
            List<ChatMessage> result = TranscriptLoader.Load(
                Path.Combine(_dir, "does-not-exist.jsonl"));
            Assert.IsNotNull(result);
            Assert.IsEmpty(result);
        }

        [Test]
        public void Load_NullOrEmptyPath_ReturnsEmptyList()
        {
            Assert.IsEmpty(TranscriptLoader.Load(null));
            Assert.IsEmpty(TranscriptLoader.Load(string.Empty));
        }

        [Test]
        public void Load_EmptyFile_ReturnsEmptyList()
        {
            string path = WriteFile(string.Empty);
            Assert.IsEmpty(TranscriptLoader.Load(path));
        }

        // ---------------------------------------------------------------
        // MODEL-3: byte cap + bounded retention
        // ---------------------------------------------------------------

        private static string UserLine(int i)
        {
            return "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                + "[{\"type\":\"text\",\"text\":\"msg-" + i + "\"}]},"
                + "\"timestamp\":\"2026-08-23T10:00:00.000Z\"}";
        }

        /// <summary>
        /// MODEL-3: a file over the byte cap parses only its TAIL window --
        /// the guard exists so a pathological transcript cannot freeze the
        /// editor for its whole length. Injected tiny cap so the fixture
        /// stays tiny too.
        /// </summary>
        [Test]
        public void Load_FileOverTheByteCap_ParsesOnlyTheTailWindow()
        {
            var lines = new List<string>();
            for (int i = 0; i < 40; i++)
            {
                lines.Add(UserLine(i));
            }
            string path = WriteFile(lines.ToArray());
            long fullLength = new FileInfo(path).Length;

            TranscriptUsage usage;
            List<ChatMessage> full = TranscriptLoader.Load(path, out usage, 300, fullLength + 1);
            Assert.AreEqual(40, full.Count, "under the cap: everything parses, exactly as before");

            List<ChatMessage> tail = TranscriptLoader.Load(path, out usage, 300, fullLength / 4);
            Assert.Greater(tail.Count, 0, "the tail window must still yield messages");
            Assert.Less(tail.Count, 40, "the head must have been skipped");
            StringAssert.Contains("msg-39", tail[tail.Count - 1].blocks[0].text,
                "the NEWEST messages are the ones that survive");
            Assert.AreEqual(tail.Count, usage.UserPromptCount,
                "usage then describes the parsed tail window (documented trade)");
        }

        /// <summary>MODEL-3: the partial first line of the seek window is discarded, not mis-parsed.</summary>
        [Test]
        public void Load_TailWindow_FirstPartialLine_IsDiscardedCleanly()
        {
            string path = WriteFile(UserLine(0), UserLine(1), UserLine(2), UserLine(3));
            long fullLength = new FileInfo(path).Length;

            // A cap that lands mid-line-2 on purpose.
            TranscriptUsage usage;
            List<ChatMessage> tail = TranscriptLoader.Load(path, out usage, 300,
                (long)(UserLine(3).Length * 1.5));
            foreach (ChatMessage message in tail)
            {
                StringAssert.StartsWith("msg-", message.blocks[0].text,
                    "no message may be built from a partial JSON fragment");
            }
        }

        /// <summary>
        /// MODEL-3: retention is bounded DURING the scan (head pruned in
        /// chunks), so the returned window matches the old end-trim exactly.
        /// </summary>
        [Test]
        public void Load_ManyMoreMessagesThanMax_ReturnsTheNewestMaxExactly()
        {
            var lines = new List<string>();
            for (int i = 0; i < 25; i++)
            {
                lines.Add(UserLine(i));
            }
            string path = WriteFile(lines.ToArray());

            List<ChatMessage> result = TranscriptLoader.Load(path, 10);

            Assert.AreEqual(10, result.Count);
            StringAssert.Contains("msg-15", result[0].blocks[0].text);
            StringAssert.Contains("msg-24", result[result.Count - 1].blocks[0].text);
        }

        // ---------------------------------------------------------------
        // MODEL-6: terminal-session scaffolds (canary fixture)
        // ---------------------------------------------------------------

        /// <summary>
        /// MODEL-6: a terminal session's slash-command wrapper must render
        /// as the command the person typed, its local output must vanish,
        /// and a genuine typed message must still come through -- all
        /// pinned against terminal_session_disk.jsonl, whose format models
        /// a real CLI terminal transcript (see the provenance record).
        /// </summary>
        [Test]
        public void Load_TerminalSessionFixture_ScaffoldsNormalized_ProseKept()
        {
            List<ChatMessage> messages = TranscriptLoader.Load(
                FixtureLoader.GetPath("terminal_session_disk.jsonl"));

            var userTexts = new List<string>();
            foreach (ChatMessage message in messages)
            {
                if (message.role == ChatMessage.RoleUser)
                {
                    userTexts.Add(message.blocks[0].text);
                }
            }

            Assert.AreEqual(2, userTexts.Count,
                "command wrapper + real prose; the stdout and caveat blocks must not become messages");
            Assert.AreEqual("/model claude-fable-5", userTexts[0],
                "the wrapper renders as what the person actually typed");
            Assert.AreEqual("please rename the player script", userTexts[1]);

            foreach (string text in userTexts)
            {
                StringAssert.DoesNotContain("<command-name>", text);
                StringAssert.DoesNotContain("<local-command-stdout>", text);
            }
        }

        /// <summary>
        /// The canary half: the fixture must actually CONTAIN the literals
        /// the loader keys on, so if the CLI's on-disk format drifts and
        /// the fixture is re-modeled without them, this fails loudly
        /// instead of the scaffold handling silently never matching (the
        /// exact shape ContextMarkers_StaySyncedWithContextBlockFormatter
        /// already guards for the context markers).
        /// </summary>
        [Test]
        public void TerminalScaffoldTags_ExistInTheCanaryFixture()
        {
            string raw = File.ReadAllText(FixtureLoader.GetPath("terminal_session_disk.jsonl"));
            StringAssert.Contains(TranscriptLoader.CommandNameOpenTag, raw);
            StringAssert.Contains(TranscriptLoader.CommandArgsOpenTag, raw);
            StringAssert.Contains(TranscriptLoader.LocalCommandStdoutOpenTag, raw);
            StringAssert.Contains(TranscriptLoader.LocalCommandCaveatOpenTag, raw);
        }

        /// <summary>MODEL-6 unit table for the pure normalizer.</summary>
        [Test]
        public void TryNormalizeCliScaffold_Table()
        {
            string visible;
            Assert.IsTrue(TranscriptLoader.TryNormalizeCliScaffold(
                "<command-name>/clear</command-name>\n<command-message>clear</command-message>", out visible));
            Assert.AreEqual("/clear", visible, "no args: just the command name");

            Assert.IsTrue(TranscriptLoader.TryNormalizeCliScaffold(
                "<local-command-stdout>anything</local-command-stdout>", out visible));
            Assert.AreEqual(string.Empty, visible, "local output renders as nothing");

            Assert.IsFalse(TranscriptLoader.TryNormalizeCliScaffold(
                "plain user prose mentioning a command-name in words", out visible),
                "prose without the literal tags must pass through untouched");

            Assert.IsFalse(TranscriptLoader.TryNormalizeCliScaffold(string.Empty, out visible));
        }

        // ---------------------------------------------------------------
        // Real captured fixtures
        // ---------------------------------------------------------------

        [Test]
        public void Load_RealFixture_Out1_SyntheticAuthErrorBecomesOneAssistantMessage()
        {
            // out1.jsonl: system/init, then one "assistant" line with
            // message.model=="<synthetic>" and a top-level "error" field
            // (auth failure envelope, R02 section 0), then a "result" line
            // that must be ignored (never persisted on disk anyway, but
            // the loader must tolerate it appearing in a stdout capture).
            List<ChatMessage> messages = TranscriptLoader.Load(FixtureLoader.GetPath("out1.jsonl"));

            Assert.AreEqual(1, messages.Count, "only the assistant message should surface");
            ChatMessage assistant = messages[0];
            Assert.AreEqual(ChatMessage.RoleAssistant, assistant.role);
            Assert.AreEqual(2, assistant.blocks.Count);
            Assert.AreEqual(ChatBlockKind.Text, assistant.blocks[0].kind);
            StringAssert.Contains("Not logged in", assistant.blocks[0].text);
            Assert.AreEqual(ChatBlockKind.Error, assistant.blocks[1].kind);
            StringAssert.Contains("authentication_failed", assistant.blocks[1].text);
        }

        [Test]
        public void Load_RealFixture_SuccessBidiInbound_MergesToolRoundTripIntoOneAssistantMessage()
        {
            // success_bidi_inbound.jsonl: one user send (with a CJK
            // sentence), an assistant text block, a Bash tool_use, its
            // tool_result, and a second assistant text block -- all before
            // any further user message, so it must all merge into ONE
            // assistant ChatMessage (design note section 3).
            List<ChatMessage> messages = TranscriptLoader.Load(
                FixtureLoader.GetPath("success_bidi_inbound.jsonl"));

            Assert.AreEqual(2, messages.Count);

            ChatMessage user = messages[0];
            Assert.AreEqual(ChatMessage.RoleUser, user.role);
            Assert.IsTrue(user.delivered);
            Assert.AreEqual(1, user.blocks.Count);
            StringAssert.Contains("AGENT_PANEL_E2E_OK", user.blocks[0].text);
            // CJK survives (the fixture's Japanese sentence ends in the
            // greeting "konnichiwa"); \u escapes keep this source ASCII.
            StringAssert.Contains("\u3053\u3093\u306B\u3061\u306F", user.blocks[0].text);

            ChatMessage assistant = messages[1];
            Assert.AreEqual(ChatMessage.RoleAssistant, assistant.role);
            Assert.AreEqual(3, assistant.blocks.Count,
                "text + tool_use + text must merge into one message, not three");
            Assert.AreEqual(ChatBlockKind.Text, assistant.blocks[0].kind);
            Assert.AreEqual("I'll run the command.", assistant.blocks[0].text);

            Assert.AreEqual(ChatBlockKind.ToolCall, assistant.blocks[1].kind);
            ToolCallRecord tool = assistant.blocks[1].toolCall;
            Assert.IsNotNull(tool);
            Assert.AreEqual("Bash", tool.toolName);
            StringAssert.Contains("echo AGENT_PANEL_E2E_OK", tool.inputJson);
            Assert.AreEqual(ToolCallStatus.Succeeded, tool.status);
            Assert.IsFalse(tool.isError);
            Assert.AreEqual("AGENT_PANEL_E2E_OK", tool.resultSummary);
            Assert.Greater(tool.startedAtUtcTicks, 0);
            Assert.GreaterOrEqual(tool.durationMs, 0);

            Assert.AreEqual(ChatBlockKind.Text, assistant.blocks[2].kind);
            Assert.AreEqual("DONE", assistant.blocks[2].text);
        }

        [Test]
        public void Load_AllCapturedFixtures_NeverThrows()
        {
            foreach (string name in Directory.GetFiles(
                Path.GetDirectoryName(FixtureLoader.GetPath("out1.jsonl")), "*.jsonl"))
            {
                List<ChatMessage> result = null;
                Assert.DoesNotThrow(delegate { result = TranscriptLoader.Load(name); },
                    "must never throw on fixture: " + name);
                Assert.IsNotNull(result);
            }
        }

        // ---------------------------------------------------------------
        // Synthetic torture file
        // ---------------------------------------------------------------

        [Test]
        public void Load_TortureFile_SkipsMetaUnknownAndTruncatedLines_KeepsRealConversation()
        {
            string hugeText = new string('x', 200 * 1024) + "\u65E5\u672C\u8A9E\u30C6\u30B9\u30C8";
            string huge = "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-sonnet-5\","
                + "\"content\":[{\"type\":\"text\",\"text\":\"" + hugeText + "\"}]},"
                + "\"timestamp\":\"2026-07-30T00:00:03Z\"}";

            string path = WriteFile(
                "{\"type\":\"queue-operation\",\"operation\":\"enqueue\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"\u65E5\u672C\u8A9E\u3067\u8CEA\u554F\uFF1A\u3053\u3093\u306B\u3061\u306F\"}]},"
                    + "\"timestamp\":\"2026-07-30T00:00:00Z\"}",
                "{\"type\":\"attachment\",\"attachment\":{\"type\":\"deferred_tools_delta\"}}",
                "{\"type\":\"some-future-type\",\"foo\":\"bar\"}",
                "{\"parentUuid\":\"x\",\"isMeta\":true,\"type\":\"user\",\"message\":"
                    + "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"skill body injected text\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-sonnet-5\","
                    + "\"content\":[{\"type\":\"text\",\"text\":\"Sure, here you go.\"},"
                    + "{\"type\":\"unknown_future_block\",\"data\":123}]},"
                    + "\"timestamp\":\"2026-07-30T00:00:02Z\"}",
                huge,
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\"");
                // ^ truncated last line (crash-time transcript): unterminated JSON.

            List<ChatMessage> messages = TranscriptLoader.Load(path);

            Assert.AreEqual(2, messages.Count,
                "queue-operation/attachment/some-future-type/isMeta lines must not"
                + " create rows, and all three real assistant lines merge into one"
                + " message (no user text closed the bubble in between)");

            ChatMessage user = messages[0];
            Assert.AreEqual(ChatMessage.RoleUser, user.role);
            StringAssert.Contains("\u3053\u3093\u306B\u3061\u306F", user.blocks[0].text);

            ChatMessage assistant = messages[1];
            Assert.AreEqual(ChatMessage.RoleAssistant, assistant.role);
            // "Sure, here you go." + huge text; the unknown block and the
            // isMeta user line contribute nothing.
            Assert.AreEqual(2, assistant.blocks.Count);
            Assert.AreEqual("Sure, here you go.", assistant.blocks[0].text);
            Assert.AreEqual(hugeText, assistant.blocks[1].text);
        }

        [Test]
        public void Load_UnresolvedToolCall_DemotesRunningToPending()
        {
            // tool_use with no matching tool_result anywhere in the file:
            // an interrupted turn or a transcript truncated right after
            // the tool call started.
            string path = WriteFile(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"do something\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\","
                    + "\"input\":{\"command\":\"sleep 999\"}}]},"
                    + "\"timestamp\":\"2026-07-30T00:00:01Z\"}");

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            Assert.AreEqual(2, messages.Count);
            ToolCallRecord tool = messages[1].blocks[0].toolCall;
            Assert.IsNotNull(tool);
            Assert.AreEqual(ToolCallStatus.Pending, tool.status,
                "an unresolved tool_use must not be left as 'Running' in restored history");
        }

        [Test]
        public void Load_ToolResultWithArrayContent_ExtractsFirstTextBlockAsSummary()
        {
            // Defensive branch: tool_result.content as an array of blocks
            // (Anthropic API allows this even though every real capture we
            // have observed uses a plain string).
            string path = WriteFile(
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_2\",\"name\":\"Read\",\"input\":{}}]},"
                    + "\"timestamp\":\"2026-07-30T00:00:01Z\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_2\",\"is_error\":false,"
                    + "\"content\":[{\"type\":\"text\",\"text\":\"file contents here\"}]}]},"
                    + "\"timestamp\":\"2026-07-30T00:00:02Z\"}");

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            ToolCallRecord tool = messages[0].blocks[0].toolCall;
            Assert.AreEqual(ToolCallStatus.Succeeded, tool.status);
            Assert.AreEqual("file contents here", tool.resultSummary);
        }

        [Test]
        public void Load_UserMetaLine_NeverResetsTheOpenAssistantBubble()
        {
            // isMeta user lines (CLI-injected skill body text) must not
            // count as a "real" user send, or the tool_use that follows
            // would wrongly start a brand new assistant message.
            string path = WriteFile(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"run the skill\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_3\",\"name\":\"Skill\",\"input\":{}}]}}",
                "{\"isMeta\":true,\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"Base directory for this skill: ...\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"Done.\"}]}}");

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            Assert.AreEqual(2, messages.Count,
                "the isMeta line must not appear as a message or split the assistant bubble");
            Assert.AreEqual(ChatMessage.RoleAssistant, messages[1].role);
            Assert.AreEqual(2, messages[1].blocks.Count);
            Assert.AreEqual(ChatBlockKind.ToolCall, messages[1].blocks[0].kind);
            Assert.AreEqual("Done.", messages[1].blocks[1].text);
        }

        // ---------------------------------------------------------------
        // Cap policy
        // ---------------------------------------------------------------

        [Test]
        public void Load_CapsAtMostRecentMaxMessages()
        {
            var lines = new List<string>();
            const int totalTurns = 160; // 160 user + 160 assistant = 320 messages
            for (int i = 0; i < totalTurns; i++)
            {
                lines.Add("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"msg " + i + "\"}]}}");
                lines.Add("{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"reply " + i + "\"}]}}");
            }
            string path = WriteFile(lines.ToArray());

            List<ChatMessage> messages = TranscriptLoader.Load(path, 300);
            Assert.AreEqual(300, messages.Count);
            // The oldest 20 messages (10 turns) were dropped; the first
            // surviving message is the 11th user message (index 10).
            Assert.AreEqual("msg 10", messages[0].blocks[0].text);
            Assert.AreEqual("reply " + (totalTurns - 1), messages[messages.Count - 1].blocks[0].text);
        }

        [Test]
        public void Load_MaxMessagesZeroOrNegative_DisablesCap()
        {
            string path = WriteFile(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"a\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"b\"}]}}");
            Assert.AreEqual(2, TranscriptLoader.Load(path, 0).Count);
            Assert.AreEqual(2, TranscriptLoader.Load(path, -1).Count);
        }

        // ---------------------------------------------------------------
        // Context-attachment wire split (design note section 4)
        // ---------------------------------------------------------------

        [Test]
        public void Load_ContextHeaderMarker_SplitsVisibleTextFromContextAttachment()
        {
            string wire = "explain this GameObject"
                + "\n\n" + ContextBlockFormatter.Header
                + "\n\n[1] GameObject: /Main Camera\n  components: Camera"
                + "\n\n" + ContextBlockFormatter.Footer;
            string path = WriteFile(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":" + JsonQuote(wire) + "}]}}");

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            Assert.AreEqual(1, messages.Count);
            ChatMessage user = messages[0];
            Assert.AreEqual(2, user.blocks.Count);
            Assert.AreEqual(ChatBlockKind.Text, user.blocks[0].kind);
            Assert.AreEqual("explain this GameObject", user.blocks[0].text);
            Assert.AreEqual(ChatBlockKind.ContextAttachment, user.blocks[1].kind);
            StringAssert.Contains("Main Camera", user.blocks[1].text);
            StringAssert.DoesNotContain(ContextBlockFormatter.Header, user.blocks[1].text);
            StringAssert.DoesNotContain(ContextBlockFormatter.Footer, user.blocks[1].text);
        }

        [Test]
        public void ContextMarkers_StaySyncedWithContextBlockFormatter()
        {
            // TranscriptLoader duplicates these literals (Model must not
            // depend on Integration -- design note section 4). This guard
            // fails loudly if ContextBlockFormatter's wire format ever
            // changes without updating the copy here.
            Type type = typeof(TranscriptLoader);
            string header = (string)type
                .GetField("ContextHeaderMarker", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
            string footer = (string)type
                .GetField("ContextFooterLiteral", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

            Assert.AreEqual("\n\n" + ContextBlockFormatter.Header, header);
            Assert.AreEqual(ContextBlockFormatter.Footer, footer);
        }

        // ---------------------------------------------------------------
        // Subagent restore (Phase 4 design note section 6)
        // ---------------------------------------------------------------

        [Test]
        public void Load_AgentToolUse_WithToolUseResultEnvelope_RestoresSubagentState()
        {
            // Synthetic MAIN transcript: R02c section 3 confirms the on-disk
            // file never contains parent_tool_use_id-tagged lines (those
            // live only in the separate sidechain jsonl) -- only the
            // top-level Agent tool_use + its tool_result envelope.
            string path = WriteFile(
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"spawn a subagent\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_agent\",\"name\":\"Agent\","
                    + "\"input\":{\"subagent_type\":\"general-purpose\",\"description\":\"do the thing\"}}]},"
                    + "\"timestamp\":\"2026-07-31T00:00:01Z\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"tool_use_id\":\"toolu_agent\",\"type\":\"tool_result\",\"content\":"
                    + "[{\"type\":\"text\",\"text\":\"boilerplate\"}]}]},"
                    + "\"timestamp\":\"2026-07-31T00:00:05Z\","
                    + "\"tool_use_result\":{\"status\":\"completed\",\"agentType\":\"general-purpose\","
                    + "\"totalTokens\":21007,\"totalToolUseCount\":1,\"totalDurationMs\":4802,"
                    + "\"content\":[{\"type\":\"text\",\"text\":\"Command ran successfully: OK\"}]}}");

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            ToolCallRecord tool = messages[1].blocks[0].toolCall;
            Assert.IsNotNull(tool);
            Assert.IsNotNull(tool.subagent, "an Agent tool_use must restore with a SubagentRecord");
            Assert.AreEqual("completed", tool.subagent.status);
            Assert.AreEqual("general-purpose", tool.subagent.subagentType);
            Assert.AreEqual(21007, tool.subagent.totalTokens);
            Assert.AreEqual(1, tool.subagent.toolUses);
            Assert.AreEqual(4802, tool.subagent.durationMs);
            Assert.AreEqual("Command ran successfully: OK", tool.subagent.summaryMarkdown);
        }

        [Test]
        public void Load_AgentToolUse_DiskSpelledEnvelope_RestoresStatusUsageAndSummary()
        {
            // THE regression this whole test group failed to catch until
            // 2026-08-02: the same envelope is spelled "tool_use_result" on
            // the stdout WIRE but "toolUseResult" in the on-disk transcript
            // this loader parses. The pre-existing test above hand-wrote the
            // wire spelling, so it stayed green while every real restore
            // silently dropped status/usage/summary -- which made
            // SubagentCard compute hasDetails == false and render a
            // permanently inert card (design note 2026-08-02-subagent-card-
            // not-expandable.md). The tool_result line below is a SANITIZED
            // COPY OF A REAL TRANSCRIPT LINE (Fixtures/
            // subagent_toolresult_disk.jsonl), not hand-authored JSON.
            string toolResultLine = File.ReadAllText(
                FixtureLoader.GetPath("subagent_toolresult_disk.jsonl")).Trim();
            string path = WriteFile(
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_FIXTURE01\",\"name\":\"Agent\","
                    + "\"input\":{\"subagent_type\":\"general-purpose\",\"description\":\"do the thing\"}}]},"
                    + "\"timestamp\":\"2026-08-02T00:00:01Z\"}",
                toolResultLine);

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            ToolCallRecord tool = messages[0].blocks[0].toolCall;
            Assert.IsNotNull(tool);
            Assert.IsNotNull(tool.subagent);
            Assert.AreEqual("completed", tool.subagent.status);
            Assert.AreEqual("general-purpose", tool.subagent.subagentType);
            Assert.Greater(tool.subagent.totalTokens, 0,
                "the disk-spelled envelope's usage totals must be restored");
            Assert.AreEqual("MODEL_TEST", tool.subagent.summaryMarkdown,
                "the disk-spelled envelope's summary must be restored -- an empty "
                + "summary is what made the card non-expandable");
        }

        [Test]
        public void Load_AgentToolUse_ToolResultWithoutEnvelope_FallsBackToPlainCompletion()
        {
            // Defensive branch: no "tool_use_result" sibling at all (should
            // never happen for a real Agent tool_use, but must degrade
            // gracefully rather than leave the record stuck "running").
            string path = WriteFile(
                "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_agent2\",\"name\":\"Task\","
                    + "\"input\":{\"subagent_type\":\"general-purpose\"}}]},"
                    + "\"timestamp\":\"2026-07-31T00:00:01Z\"}",
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":"
                    + "[{\"tool_use_id\":\"toolu_agent2\",\"type\":\"tool_result\","
                    + "\"content\":\"done\",\"is_error\":false}]},"
                    + "\"timestamp\":\"2026-07-31T00:00:02Z\"}");

            List<ChatMessage> messages = TranscriptLoader.Load(path);
            ToolCallRecord tool = messages[0].blocks[0].toolCall;
            Assert.IsNotNull(tool.subagent);
            Assert.AreEqual("completed", tool.subagent.status);
        }

        [Test]
        public void FindSubagentJsonlPath_MatchesByToolUseId_ReturnsSiblingJsonlFile()
        {
            string sessionId = "f8e35260-c065-4f43-860e-d7af1a0796c3";
            string mainPath = Path.Combine(_dir, sessionId + ".jsonl");
            File.WriteAllText(mainPath, string.Empty);
            string subagentsDir = Path.Combine(_dir, sessionId, "subagents");
            Directory.CreateDirectory(subagentsDir);
            File.Copy(FixtureLoader.GetPath("subagent_sidechain.meta.json"),
                Path.Combine(subagentsDir, "agent-ac1ae679e88d103e8.meta.json"));
            File.Copy(FixtureLoader.GetPath("subagent_sidechain.jsonl"),
                Path.Combine(subagentsDir, "agent-ac1ae679e88d103e8.jsonl"));

            string found = TranscriptLoader.FindSubagentJsonlPath(mainPath,
                "toolu_01X1fFH9irZFSYzquGCCEXW6");

            Assert.AreEqual(Path.Combine(subagentsDir, "agent-ac1ae679e88d103e8.jsonl"), found);
        }

        [Test]
        public void FindSubagentJsonlPath_NoMatchingToolUseId_ReturnsNull()
        {
            string sessionId = "sess";
            string mainPath = Path.Combine(_dir, sessionId + ".jsonl");
            File.WriteAllText(mainPath, string.Empty);
            string subagentsDir = Path.Combine(_dir, sessionId, "subagents");
            Directory.CreateDirectory(subagentsDir);
            File.Copy(FixtureLoader.GetPath("subagent_sidechain.meta.json"),
                Path.Combine(subagentsDir, "agent-ac1ae679e88d103e8.meta.json"));

            Assert.IsNull(TranscriptLoader.FindSubagentJsonlPath(mainPath, "toolu_does_not_exist"));
        }

        [Test]
        public void FindSubagentJsonlPath_MissingSubagentsDirectory_DegradesToNull_NeverThrows()
        {
            // Acceptance criteria 5: directory/file missing -> graceful
            // degradation, never an exception.
            string mainPath = Path.Combine(_dir, "lonely-session.jsonl");
            File.WriteAllText(mainPath, string.Empty);

            string found = null;
            Assert.DoesNotThrow(delegate
            {
                found = TranscriptLoader.FindSubagentJsonlPath(mainPath, "toolu_anything");
            });
            Assert.IsNull(found);
        }

        [Test]
        public void FindSubagentJsonlPath_NullOrEmptyArgs_ReturnsNull()
        {
            Assert.IsNull(TranscriptLoader.FindSubagentJsonlPath(null, "toolu_x"));
            Assert.IsNull(TranscriptLoader.FindSubagentJsonlPath(
                Path.Combine(_dir, "s.jsonl"), null));
        }

        [Test]
        public void LoadSubagentBlocks_RealSidechainFixture_ProducesNestedTranscript()
        {
            // subagent_sidechain.jsonl: initiating prompt (skipped), two
            // attachment lines (skipped), one Bash tool_use, its
            // tool_result, and a final text reply.
            List<ChatMessageBlock> blocks = TranscriptLoader.LoadSubagentBlocks(
                FixtureLoader.GetPath("subagent_sidechain.jsonl"));

            Assert.AreEqual(2, blocks.Count,
                "the initiating prompt and attachment lines must not become blocks");
            Assert.AreEqual(ChatBlockKind.ToolCall, blocks[0].kind);
            ToolCallRecord tool = blocks[0].toolCall;
            Assert.IsNotNull(tool);
            Assert.AreEqual("Bash", tool.toolName);
            StringAssert.Contains("echo SUBAGENT_FIXTURE_OK", tool.inputJson);
            Assert.AreEqual(ToolCallStatus.Succeeded, tool.status);
            Assert.AreEqual("SUBAGENT_FIXTURE_OK", tool.resultSummary);
            Assert.IsNull(tool.subagent, "depth-1 cutoff: nested tool calls never carry a subagent");

            Assert.AreEqual(ChatBlockKind.Text, blocks[1].kind);
            StringAssert.Contains("Command ran successfully", blocks[1].text);
        }

        [Test]
        public void LoadSubagentBlocks_MissingFile_ReturnsEmptyList_NeverThrows()
        {
            List<ChatMessageBlock> blocks = null;
            Assert.DoesNotThrow(delegate
            {
                blocks = TranscriptLoader.LoadSubagentBlocks(
                    Path.Combine(_dir, "does-not-exist.jsonl"));
            });
            Assert.IsNotNull(blocks);
            Assert.IsEmpty(blocks);
        }

        [Test]
        public void LoadSubagentBlocks_NullOrEmptyPath_ReturnsEmptyList()
        {
            Assert.IsEmpty(TranscriptLoader.LoadSubagentBlocks(null));
            Assert.IsEmpty(TranscriptLoader.LoadSubagentBlocks(string.Empty));
        }

        /// <summary>
        /// Review defect (docs/design-notes/2026-07-31-subagent-display.md
        /// section 3/6): SubagentCard.TryLazyLoadNestedBlocks used to call
        /// the single-arg LoadSubagentBlocks overload and AddRange the
        /// result directly, bypassing SubagentRecord.AddBlock -- the only
        /// place that increments droppedBlockCount -- so a history-restored
        /// card's "N earlier steps omitted" note never fired even when the
        /// sidechain exceeded MaxNestedBlocks. This pins the out-param
        /// overload's own truncation math: the caller can only wire the
        /// count through correctly if LoadSubagentBlocks reports it right.
        /// </summary>
        [Test]
        public void LoadSubagentBlocks_ExceedsMaxNestedBlocks_TruncatesOldestAndReportsDroppedCount()
        {
            const int total = SubagentRecord.MaxNestedBlocks + 10;
            var lines = new string[total];
            for (int i = 0; i < total; i++)
            {
                lines[i] = "{\"type\":\"assistant\",\"message\":{\"content\":"
                    + "[{\"type\":\"text\",\"text\":" + JsonQuote("line " + i) + "}]}}";
            }
            string path = WriteFile(lines);

            int droppedCount;
            List<ChatMessageBlock> blocks = TranscriptLoader.LoadSubagentBlocks(path, out droppedCount);

            Assert.AreEqual(10, droppedCount, "130 blocks over a cap of 120 must report 10 dropped");
            Assert.AreEqual(SubagentRecord.MaxNestedBlocks, blocks.Count);
            Assert.AreEqual("line 10", blocks[0].text,
                "the oldest 10 blocks (line 0..9) must be the ones dropped");
            Assert.AreEqual("line " + (total - 1), blocks[blocks.Count - 1].text);
        }

        [Test]
        public void LoadSubagentBlocks_UnresolvedToolCall_DemotesRunningToPending()
        {
            string path = Path.Combine(_dir, "agent-x.jsonl");
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"isSidechain\":true,\"message\":{\"content\":"
                    + "[{\"type\":\"tool_use\",\"id\":\"toolu_stuck\",\"name\":\"Bash\","
                    + "\"input\":{\"command\":\"sleep 999\"}}]},\"timestamp\":\"2026-07-30T00:00:01Z\"}");

            List<ChatMessageBlock> blocks = TranscriptLoader.LoadSubagentBlocks(path);
            Assert.AreEqual(1, blocks.Count);
            Assert.AreEqual(ToolCallStatus.Pending, blocks[0].toolCall.status);
        }

        private static string JsonQuote(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
