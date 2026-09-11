using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guard for AgentDefinitionFileWriter -- the replacement for
    /// the `--agents` CLI argument (docs/research/07-model-configuration.md
    /// section 10: --agents is silently ignored by the CLI whenever
    /// --resume is also passed, which the panel always does once a session
    /// exists). Covers the exact file format verified against a live CLI
    /// (section 10.5/10.6), the panel-ownership marker that gates every
    /// write/delete, and cleanup of stale entries -- mirroring
    /// QuickActionStoreTests's temp-directory contract.
    /// </summary>
    public class AgentDefinitionFileWriterTests
    {
        private string _dir;
        private List<string> _logs;
        private AgentDefinitionFileWriter _writer;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelAgentDefFileTests_" + Guid.NewGuid().ToString("N"), ".claude", "agents");
            _logs = new List<string>();
            _writer = new AgentDefinitionFileWriter(_dir, delegate(string line) { _logs.Add(line); });
        }

        [TearDown]
        public void TearDown()
        {
            // TearDown must walk up to the per-test GUID root, not just
            // _dir (which is the nested ".claude/agents" leaf).
            string root = Directory.GetParent(Directory.GetParent(_dir).FullName).FullName;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static AgentModelOverride Entry(string name, string model)
        {
            return new AgentModelOverride { agentName = name, modelAlias = model };
        }

        // -- BuildFileContent / HasMarker (pure string logic) ------------------------

        [Test]
        public void BuildFileContent_MatchesVerifiedMinimalFormat()
        {
            string content = AgentDefinitionFileWriter.BuildFileContent("haiku");
            Assert.AreEqual(
                "---\n"
                + "model: haiku\n"
                + "# unity-agent-panel:managed - regenerated from Settings > Model overrides;"
                + " do not edit by hand\n"
                + "---\n",
                content);
        }

        [Test]
        public void BuildFileContent_AlwaysCarriesTheMarker()
        {
            string content = AgentDefinitionFileWriter.BuildFileContent("sonnet");
            Assert.IsTrue(AgentDefinitionFileWriter.HasMarker(content));
        }

        [Test]
        public void HasMarker_UserAuthoredFile_IsFalse()
        {
            string userFile = "---\nname: my-agent\ndescription: hand-written\n---\nDo the thing.\n";
            Assert.IsFalse(AgentDefinitionFileWriter.HasMarker(userFile));
        }

        [Test]
        public void HasMarker_NullContent_IsFalse()
        {
            Assert.IsFalse(AgentDefinitionFileWriter.HasMarker(null));
        }

        // -- IsValidAgentName ---------------------------------------------------------

        [TestCase("general-purpose", true)]
        [TestCase("Explore", true)]
        [TestCase("uap_worker-2", true)]
        [TestCase("", false)]
        [TestCase("../escape", false)]
        [TestCase("sub/dir", false)]
        [TestCase("back\\slash", false)]
        [TestCase("has space", false)]
        [TestCase("weird:name", false)]
        public void IsValidAgentName_RejectsFilenameUnsafeInput(string name, bool expected)
        {
            Assert.AreEqual(expected, AgentDefinitionFileWriter.IsValidAgentName(name));
        }

        // -- Sync: no-op cases ----------------------------------------------------------

        [Test]
        public void Sync_NullOverrides_CreatesNoDirectory()
        {
            _writer.Sync(null);
            Assert.IsFalse(Directory.Exists(_dir));
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Sync_EmptyList_CreatesNoDirectory()
        {
            _writer.Sync(new List<AgentModelOverride>());
            Assert.IsFalse(Directory.Exists(_dir));
        }

        [Test]
        public void Sync_AllBlankEntries_CreatesNoDirectory()
        {
            _writer.Sync(new List<AgentModelOverride>
            {
                Entry(string.Empty, "haiku"),
                Entry("general-purpose", string.Empty),
                null
            });
            Assert.IsFalse(Directory.Exists(_dir));
        }

        // -- Sync: writes -----------------------------------------------------------

        [Test]
        public void Sync_SingleEntry_WritesExpectedFile()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });
            string path = Path.Combine(_dir, "general-purpose.md");
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(AgentDefinitionFileWriter.BuildFileContent("haiku"),
                File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"), "atomic write must not leave a .tmp behind");
        }

        [Test]
        public void Sync_MultipleEntries_WritesOneFilePerAgent()
        {
            _writer.Sync(new List<AgentModelOverride>
            {
                Entry("general-purpose", "haiku"),
                Entry("Explore", "sonnet")
            });
            Assert.AreEqual("haiku",
                ExtractModel(File.ReadAllText(Path.Combine(_dir, "general-purpose.md"))));
            Assert.AreEqual("sonnet",
                ExtractModel(File.ReadAllText(Path.Combine(_dir, "Explore.md"))));
        }

        [Test]
        public void Sync_InvalidAgentName_SkipsAndLogsWithoutWriting()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("../escape", "haiku") });
            Assert.IsFalse(Directory.Exists(_dir));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Sync_ModelAliasWithNewline_SkipsAndLogs()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "ha\nik") });
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "general-purpose.md")));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Sync_DuplicateAgentName_LastEntryWins()
        {
            _writer.Sync(new List<AgentModelOverride>
            {
                Entry("general-purpose", "haiku"),
                Entry("general-purpose", "sonnet")
            });
            Assert.AreEqual("sonnet",
                ExtractModel(File.ReadAllText(Path.Combine(_dir, "general-purpose.md"))));
        }

        /// <summary>
        /// MODEL-9: 'Explore' and 'explore' are one FILE on the
        /// case-insensitive filesystems this writes to. The desired map now
        /// folds case variants (OrdinalIgnoreCase, last row wins) instead
        /// of fighting itself over one path with Ordinal keys.
        /// </summary>
        [Test]
        public void Sync_CaseVariantDuplicates_FoldIntoOneFile_LastRowWins()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("Explore", "sonnet"), Entry("explore", "haiku") });

            string[] files = Directory.GetFiles(_dir, "*.md");
            Assert.AreEqual(1, files.Length, "case variants must produce exactly one file");
            StringAssert.Contains("model: haiku", File.ReadAllText(files[0]),
                "the LAST row must win, same as exact duplicates");
        }

        /// <summary>MODEL-9: CON.md, NUL.md etc. address Windows DEVICES, not files -- refused like any invalid name.</summary>
        [TestCase("CON")]
        [TestCase("con")]
        [TestCase("NUL")]
        [TestCase("COM1")]
        [TestCase("LPT9")]
        [TestCase("-")]
        [TestCase("__--")]
        public void Sync_ReservedOrDegenerateName_SkipsAndLogsWithoutWriting(string name)
        {
            _writer.Sync(new List<AgentModelOverride> { Entry(name, "haiku") });

            Assert.IsFalse(Directory.Exists(_dir)
                && Directory.GetFiles(_dir, "*.md").Length > 0,
                "no file may be created for '" + name + "'");
            Assert.IsTrue(_logs.Count > 0, "the skip must be visible in the log");
        }

        [TestCase("CONSOLE", true)]
        [TestCase("COM10", true)]
        [TestCase("nul", false)]
        [TestCase("Aux", false)]
        [TestCase("code-reviewer", true)]
        public void IsValidAgentName_ReservedNameEdges(string name, bool expected)
        {
            Assert.AreEqual(expected, AgentDefinitionFileWriter.IsValidAgentName(name),
                "'CONSOLE'/'COM10' are ordinary names; only the exact device names are reserved");
        }

        /// <summary>
        /// MODEL-10: the alias is embedded as a bare YAML plain scalar, so
        /// ' #' starts a comment mid-value and ': ' re-keys the line --
        /// silently truncating what the CLI reads back. Refused with a log.
        /// </summary>
        [TestCase("sonnet #note")]
        [TestCase("a: b")]
        [TestCase("trailing:")]
        [TestCase("[haiku]")]
        [TestCase("'quoted'")]
        public void Sync_YamlUnsafeAlias_SkipsAndLogsWithoutWriting(string alias)
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("agent-a", alias) });

            Assert.IsFalse(Directory.Exists(_dir)
                && Directory.GetFiles(_dir, "*.md").Length > 0,
                "no file may be created for alias '" + alias + "'");
            Assert.IsTrue(_logs.Count > 0);
        }

        [TestCase("haiku", true)]
        [TestCase("claude-haiku-4", true)]
        [TestCase("claude-haiku-4-5-20251001", true)]
        [TestCase("sonnet #note", false)]
        [TestCase("a: b", false)]
        [TestCase("trailing:", false)]
        [TestCase("[haiku]", false)]
        public void IsYamlSafeModelAlias_Table(string alias, bool expected)
        {
            Assert.AreEqual(expected, AgentDefinitionFileWriter.IsYamlSafeModelAlias(alias),
                "every real model alias must pass; the YAML-breaking shapes must not");
        }

        [Test]
        public void Sync_MixedValidAndBlank_WritesOnlyTheValidOne()
        {
            _writer.Sync(new List<AgentModelOverride>
            {
                Entry("general-purpose", "haiku"),
                Entry("Explore", string.Empty)
            });
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "general-purpose.md")));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "Explore.md")));
        }

        // -- Sync: idempotence / cleanup ------------------------------------------------

        [Test]
        public void Sync_CalledTwiceWithSameTable_LeavesFileUnchanged()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });
            string path = Path.Combine(_dir, "general-purpose.md");
            string firstContent = File.ReadAllText(path);

            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });
            Assert.AreEqual(firstContent, File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [Test]
        public void Sync_EntryRemovedFromTable_DeletesTheStaleManagedFile()
        {
            _writer.Sync(new List<AgentModelOverride>
            {
                Entry("general-purpose", "haiku"),
                Entry("Explore", "haiku")
            });
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "Explore.md")));

            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });

            Assert.IsTrue(File.Exists(Path.Combine(_dir, "general-purpose.md")));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "Explore.md")),
                "an override dropped from the table must have its managed file removed");
        }

        [Test]
        public void Sync_TableEmptiedEntirely_DeletesAllManagedFilesButKeepsDirectory()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });
            _writer.Sync(new List<AgentModelOverride>());
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "general-purpose.md")));
        }

        // -- Never touches non-panel files -----------------------------------------------

        [Test]
        public void Sync_ExistingUserFileAtSamePath_IsNeverOverwritten()
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, "general-purpose.md");
            string userContent = "---\nname: general-purpose\ndescription: hand-written\n---\nCustom body.\n";
            File.WriteAllText(path, userContent);

            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });

            Assert.AreEqual(userContent, File.ReadAllText(path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Sync_UnrelatedUserFile_IsNeverDeletedByCleanup()
        {
            Directory.CreateDirectory(_dir);
            string userPath = Path.Combine(_dir, "my-custom-agent.md");
            string userContent = "---\nname: my-custom-agent\ndescription: hand-written\n---\nCustom body.\n";
            File.WriteAllText(userPath, userContent);

            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });
            Assert.IsTrue(File.Exists(userPath));
            Assert.AreEqual(userContent, File.ReadAllText(userPath));

            // Even after the whole table empties out, the user's own file
            // (no marker) must survive cleanup.
            _writer.Sync(new List<AgentModelOverride>());
            Assert.IsTrue(File.Exists(userPath));
        }

        [Test]
        public void Sync_RefreshesAStaleManagedFileWhoseModelChanged()
        {
            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "haiku") });
            _writer.Sync(new List<AgentModelOverride> { Entry("general-purpose", "opus") });
            Assert.AreEqual("opus",
                ExtractModel(File.ReadAllText(Path.Combine(_dir, "general-purpose.md"))));
        }

        // -- Small helpers ------------------------------------------------------------

        private static string ExtractModel(string content)
        {
            const string prefix = "model: ";
            foreach (string line in content.Split('\n'))
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line.Substring(prefix.Length);
                }
            }
            return null;
        }
    }
}
