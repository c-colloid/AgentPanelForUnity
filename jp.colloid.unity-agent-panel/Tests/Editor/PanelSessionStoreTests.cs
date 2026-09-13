using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The panel's own per-session store for ACP agents' sessions and its
    /// History integration (design note
    /// docs/design-notes/2026-09-13-acp-feature-parity.md section 1):
    /// write/list/read/rename against a temp directory, SessionIndex
    /// listing both sources, the row's agent label, and the handover
    /// wording for a same-agent restart.
    /// </summary>
    public class PanelSessionStoreTests
    {
        private string _root;
        private List<string> _logs;
        private PanelSessionStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "AgentPanelPanelSessionStoreTests_" + Guid.NewGuid().ToString("N"));
            _logs = new List<string>();
            _store = new PanelSessionStore(Path.Combine(_root, "Sessions"), delegate (string line) { _logs.Add(line); });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private static ChatSession MakeSession(string id, AgentBackend backend, string firstUserText)
        {
            var session = new ChatSession { sessionId = id, agentBackend = (int)backend };
            var user = new ChatMessage { role = ChatMessage.RoleUser };
            user.Add(ChatMessageBlock.MakeText(firstUserText));
            session.AddMessage(user);
            var assistant = new ChatMessage { role = ChatMessage.RoleAssistant };
            assistant.Add(ChatMessageBlock.MakeText("done"));
            session.AddMessage(assistant);
            session.EnsureTitleFrom(firstUserText);
            return session;
        }

        [Test]
        public void SafeFileStem_KeepsUuidLikeIds_ReplacesEverythingElse()
        {
            Assert.AreEqual("0b1c-2d3e_f.4", PanelSessionStore.SafeFileStem("0b1c-2d3e_f.4"));
            Assert.AreEqual("a_b_c", PanelSessionStore.SafeFileStem("a/b\\c"));
            Assert.AreEqual("_", PanelSessionStore.SafeFileStem(".../"));  // leading dots trimmed, never a dot-only stem
            Assert.IsNull(PanelSessionStore.SafeFileStem(string.Empty));
            Assert.IsNull(PanelSessionStore.SafeFileStem(null));
            Assert.IsNull(PanelSessionStore.SafeFileStem("..."));
        }

        [Test]
        public void Save_ThenEnumerate_ListsTheSessionWithItsOwnerAndPreview()
        {
            ChatSession session = MakeSession("sess-1", AgentBackend.CodexAcp, "Rename the player prefab\nsecond line");
            var usage = new Dictionary<string, ModelUsage> { { "gpt-5-codex", new ModelUsage() } };
            Assert.IsTrue(_store.Save(session, usage));

            List<PanelSessionEntry> entries = _store.Enumerate();
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("sess-1", entries[0].SessionId);
            Assert.AreEqual((int)AgentBackend.CodexAcp, entries[0].AgentBackend);
            Assert.AreEqual("Rename the player prefab", entries[0].FirstUserTextPreview);
            Assert.AreEqual("Rename the player prefab", entries[0].Title);
            Assert.AreEqual("gpt-5-codex", entries[0].ModelName);
            Assert.IsTrue(entries[0].SizeBytes > 0);
        }

        [Test]
        public void Save_SkipsEmptySessions_AndSessionsWithoutAnId()
        {
            Assert.IsFalse(_store.Save(new ChatSession { sessionId = "x", agentBackend = 1 }, null));
            ChatSession noId = MakeSession(string.Empty, AgentBackend.GeminiCli, "hi");
            Assert.IsFalse(_store.Save(noId, null));
            Assert.AreEqual(0, _store.Enumerate().Count);
        }

        [Test]
        public void Load_RoundTripsTheSession_AndTheModelUsage()
        {
            ChatSession session = MakeSession("sess-2", AgentBackend.GrokBuild, "hello");
            session.totalInputTokens = 42;
            var usage = new Dictionary<string, ModelUsage> { { "grok-4", new ModelUsage { InputTokens = 7 } } };
            _store.Save(session, usage);

            Dictionary<string, ModelUsage> loadedUsage;
            ChatSession loaded = _store.Load("sess-2", out loadedUsage);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("sess-2", loaded.sessionId);
            Assert.AreEqual((int)AgentBackend.GrokBuild, loaded.agentBackend);
            Assert.AreEqual(42, loaded.totalInputTokens);
            Assert.AreEqual(2, loaded.messages.Count);
            Assert.AreEqual(7, loadedUsage["grok-4"].InputTokens);
        }

        [Test]
        public void Load_MissingSession_IsNullWithEmptyUsage()
        {
            Dictionary<string, ModelUsage> usage;
            Assert.IsNull(_store.Load("nope", out usage));
            Assert.AreEqual(0, usage.Count);
        }

        [Test]
        public void Rename_MovesTheFileToTheNewId_ReplacingAnExistingOne()
        {
            _store.Save(MakeSession("old", AgentBackend.GeminiCli, "first"), null);
            _store.Save(MakeSession("new", AgentBackend.GeminiCli, "stale"), null);
            Assert.IsTrue(_store.Rename("old", "new"));
            Assert.IsFalse(File.Exists(_store.PathFor("old")));
            Dictionary<string, ModelUsage> usage;
            Assert.AreEqual("first", _store.Load("new", out usage).title);
            Assert.IsFalse(_store.Rename("old", "new"), "nothing left to move");
            Assert.IsFalse(_store.Rename("new", "new"), "same id is a no-op");
        }

        [Test]
        public void ReadSummary_NeverDeletesAnUnparseableFile()
        {
            Directory.CreateDirectory(_store.Directory);
            string path = _store.PathFor("bad");
            File.WriteAllText(path, "{not json");
            PanelSessionSummary summary = PanelSessionStore.ReadSummary(path, delegate (string line) { _logs.Add(line); });
            Assert.AreEqual(-1, summary.AgentBackend);
            Assert.AreEqual(string.Empty, summary.Preview);
            Assert.IsTrue(File.Exists(path), "a History scan must not destroy files");
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Enumerate_MissingDirectory_IsEmpty_NewestFirstOtherwise()
        {
            Assert.AreEqual(0, _store.Enumerate().Count);
            _store.Save(MakeSession("a", AgentBackend.GeminiCli, "a"), null);
            File.SetLastWriteTimeUtc(_store.PathFor("a"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            _store.Save(MakeSession("b", AgentBackend.GeminiCli, "b"), null);
            File.SetLastWriteTimeUtc(_store.PathFor("b"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            List<PanelSessionEntry> entries = _store.Enumerate();
            Assert.AreEqual("b", entries[0].SessionId);
            Assert.AreEqual("a", entries[1].SessionId);
        }

        // -- SessionIndex lists both sources --------------------------------------

        [Test]
        public void SessionIndex_ListsPanelStoreSessionsNextToClaudeTranscripts()
        {
            string claudeRoot = Path.Combine(_root, "claude-projects");
            string cwd = "C:\\Unity\\Proj";
            string dir = Path.Combine(claudeRoot, SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "claude-1.jsonl"),
                "{\"type\":\"user\",\"cwd\":\"C:\\\\Unity\\\\Proj\",\"message\":{\"role\":\"user\",\"content\":\"fix it\"}}\n");
            _store.Save(MakeSession("acp-1", AgentBackend.CodexAcp, "ship it"), null);

            var index = new SessionIndex(claudeRoot, _store.Directory);
            index.Refresh(cwd);
            Assert.AreEqual(2, index.Entries.Count);
            SessionIndexEntry claude = null;
            SessionIndexEntry acp = null;
            foreach (SessionIndexEntry entry in index.Entries)
            {
                if (entry.SessionId == "claude-1") claude = entry;
                if (entry.SessionId == "acp-1") acp = entry;
            }
            Assert.IsNotNull(claude);
            Assert.IsNotNull(acp);
            Assert.IsFalse(claude.IsPanelStore);
            Assert.AreEqual((int)AgentBackend.ClaudeCode, claude.AgentBackend);
            Assert.IsTrue(acp.IsPanelStore);
            Assert.AreEqual((int)AgentBackend.CodexAcp, acp.AgentBackend);
            Assert.AreEqual("ship it", acp.FirstUserTextPreview);
            Assert.AreEqual("ship it", acp.AiTitle);
            Assert.AreEqual(cwd, acp.Cwd, "a stored session belongs to the project it was saved under");
            StringAssert.EndsWith(".json", acp.FilePath);
        }

        [Test]
        public void SessionIndex_WithoutAPanelStoreOverride_DerivesItFromTheCwd()
        {
            string cwd = Path.Combine(_root, "Project");
            Directory.CreateDirectory(cwd);
            var projectStore = PanelSessionStore.CreateDefault(cwd);
            projectStore.Save(MakeSession("acp-2", AgentBackend.GeminiCli, "hello"), null);
            var index = new SessionIndex(Path.Combine(_root, "no-claude-here"));
            index.Refresh(cwd);
            Assert.AreEqual(1, index.Entries.Count);
            Assert.AreEqual("acp-2", index.Entries[0].SessionId);
            StringAssert.StartsWith(PanelSessionStore.DefaultDirectory(cwd), index.Entries[0].FilePath);
        }

        // -- Row label / handover wording ------------------------------------------

        [Test]
        public void AgentLabel_NamesAcpAgentsOnly()
        {
            Assert.AreEqual(string.Empty, HistoryView.AgentLabelFor((int)AgentBackend.ClaudeCode));
            Assert.AreEqual(string.Empty, HistoryView.AgentLabelFor(-1));
            Assert.AreEqual("Gemini CLI", HistoryView.AgentLabelFor((int)AgentBackend.GeminiCli));
            Assert.AreEqual("Codex (codex-acp)", HistoryView.AgentLabelFor((int)AgentBackend.CodexAcp));
        }

        [Test]
        public void FormatRowMeta_AppendsTheAgentAfterTheModel()
        {
            string meta = HistoryView.FormatRowMeta("2h ago", "1.7k", "gemini-2.5-pro", "Gemini CLI");
            StringAssert.EndsWith("Gemini CLI", meta);
            StringAssert.Contains("gemini-2.5-pro", meta);
            Assert.AreEqual(HistoryView.FormatRowMeta("2h ago", "1.7k", "x"),
                HistoryView.FormatRowMeta("2h ago", "1.7k", "x", string.Empty));
        }

        [Test]
        public void Handover_SameAgent_SaysTheEarlierSessionCouldNotBeResumed()
        {
            ChatSession session = MakeSession("s", AgentBackend.GeminiCli, "hello");
            string text = ConversationHandover.Build(session.messages, "Gemini CLI", "Gemini CLI");
            StringAssert.Contains("talking to you (Gemini CLI)", text);
            StringAssert.Contains("could not be resumed", text);
            StringAssert.DoesNotContain("switched to you", text);
            string cross = ConversationHandover.Build(session.messages, "Claude Code", "Gemini CLI");
            StringAssert.Contains("switched to you (Gemini CLI)", cross);
        }

        [Test]
        public void HasHandoverContent_FalseForNotesOnly()
        {
            var session = new ChatSession();
            var note = new ChatMessage { role = ChatMessage.RoleSystem };
            note.Add(ChatMessageBlock.MakeSystemNote("only a note"));
            session.AddMessage(note);
            Assert.IsFalse(Integration.AgentHub.HasHandoverContent(session.messages));
            session.AddMessage(MakeSession("x", AgentBackend.GeminiCli, "real text").messages[0]);
            Assert.IsTrue(Integration.AgentHub.HasHandoverContent(session.messages));
        }
    }
}
