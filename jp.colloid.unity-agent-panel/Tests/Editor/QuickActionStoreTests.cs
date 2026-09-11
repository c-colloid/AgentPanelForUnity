using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Round-trip/poison suite for the quick-action JSON sidecar (docs/
    /// design-notes/2026-08-01-settings-enrichment.md #3/#4), mirroring
    /// SessionCacheFileTests's contract exactly since QuickActionStore
    /// exists for the identical reason (a prompt can be code-shaped /
    /// brace-heavy, which corrupts UnityYAML). All source literals are
    /// strict ASCII; CJK test data uses \u escapes.
    /// </summary>
    public class QuickActionStoreTests
    {
        private string _dir;
        private string _path;
        private List<string> _logs;
        private QuickActionStore _store;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelQuickActionTests_" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "QuickActions.json");
            _logs = new List<string>();
            _store = new QuickActionStore(_path, delegate (string line) { _logs.Add(line); });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private static string CjkText()
        {
            // Japanese "hello, this is a test" style content (this file
            // lives under Tests/Editor, not Editor/, so
            // GlyphAuditTests.SourceScan_EditorSources_AreStrictAscii does
            // not require ASCII-only source here).
            return "日本語のテスト こんにちは";
        }

        private static string BraceHeavyPrompt()
        {
            return "Refactor this:\n"
                + "{\n  \"nested\": { \"a\": [1, 2, { \"b\": true }] }\n}\n"
                + "} dangling\n{ leading";
        }

        // -- Round trip -----------------------------------------------------------

        [Test]
        public void RoundTrip_BraceHeavyCjkAndNewlines_PreservesEverything()
        {
            var actions = new List<QuickAction>
            {
                new QuickAction { label = "Fix", prompt = BraceHeavyPrompt() },
                new QuickAction { label = CjkText(), prompt = CjkText() + "\n" + BraceHeavyPrompt() },
                new QuickAction { label = "Empty prompt", prompt = string.Empty }
            };
            _store.Save(actions);
            List<QuickAction> loaded = _store.Load();

            Assert.AreEqual(actions.Count, loaded.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                Assert.AreEqual(actions[i].label, loaded[i].label, "label at index " + i);
                Assert.AreEqual(actions[i].prompt, loaded[i].prompt, "prompt at index " + i);
            }
            Assert.IsEmpty(_logs, "no failures should be logged: "
                + string.Join(" | ", _logs.ToArray()));
        }

        [Test]
        public void RoundTrip_Twice_SecondSaveWins()
        {
            _store.Save(new List<QuickAction> { new QuickAction { label = "a", prompt = "1" } });
            _store.Save(new List<QuickAction> { new QuickAction { label = "b", prompt = "2" } });
            List<QuickAction> loaded = _store.Load();
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual("b", loaded[0].label);
            Assert.AreEqual("2", loaded[0].prompt);
        }

        // -- Empty-store defaults --------------------------------------------------

        [Test]
        public void Load_MissingFile_ReturnsEmptyListSilently()
        {
            List<QuickAction> loaded = _store.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.Count);
            Assert.IsEmpty(_logs, "a missing store is normal, not an error");
        }

        [Test]
        public void Save_NullList_WritesAnEmptyValidStore()
        {
            _store.Save(null);
            List<QuickAction> loaded = _store.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.Count);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Save_EmptyList_RoundTripsToEmptyList()
        {
            _store.Save(new List<QuickAction>());
            List<QuickAction> loaded = _store.Load();
            Assert.AreEqual(0, loaded.Count);
        }

        // -- Corrupted file recovery ------------------------------------------------

        [Test]
        public void Load_TruncatedJson_ReturnsEmptyDeletesFileLogsOnce()
        {
            _store.Save(new List<QuickAction> { new QuickAction { label = "a", prompt = "1" } });
            string json = File.ReadAllText(_path);
            File.WriteAllText(_path, json.Substring(0, json.Length / 2));

            List<QuickAction> loaded = _store.Load();
            Assert.AreEqual(0, loaded.Count);
            Assert.IsFalse(File.Exists(_path), "the corrupt file must be deleted");
            Assert.AreEqual(1, _logs.Count, "exactly one log line, no spam");

            // A second Load sees no file and stays silent (no log spam).
            _store.Load();
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_GarbageBytes_ReturnsEmptyAndDeletes()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(_path, new byte[] { 0xFF, 0xFE, 0x00, 0x13, 0x37, 0x7B, 0x22 });
            Assert.AreEqual(0, _store.Load().Count);
            Assert.IsFalse(File.Exists(_path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_NonObjectRoot_ReturnsEmptyAndDeletes()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "[1,2,3]");
            Assert.AreEqual(0, _store.Load().Count);
            Assert.IsFalse(File.Exists(_path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_MissingActionsKey_ReturnsEmptyListWithoutError()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{\"version\":1}");
            List<QuickAction> loaded = _store.Load();
            Assert.AreEqual(0, loaded.Count);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_NonObjectItemsInArray_AreSkipped()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"version\":1,\"actions\":[42,\"str\",{\"label\":\"ok\",\"prompt\":\"p\"}]}");
            List<QuickAction> loaded = _store.Load();
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual("ok", loaded[0].label);
        }

        // -- Atomic write -----------------------------------------------------------

        [Test]
        public void Save_CreatesMissingDirectory_AndLeavesNoTmpFile()
        {
            Assert.IsFalse(Directory.Exists(_dir), "SetUp must not pre-create the directory");
            _store.Save(new List<QuickAction> { new QuickAction { label = "a", prompt = "1" } });
            Assert.IsTrue(Directory.Exists(_dir));
            Assert.IsTrue(File.Exists(_path));
            Assert.IsFalse(File.Exists(_path + ".tmp"),
                "atomic write must not leave a .tmp behind");
            Assert.IsEmpty(_logs);
        }
    }
}
