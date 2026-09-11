using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Round-trip/poison suite for the custom-instructions plain-text
    /// sidecar (docs/design-notes/2026-08-01-settings-enrichment.md #1),
    /// mirroring SessionCacheFileTests/QuickActionStoreTests's contract.
    /// </summary>
    public class CustomInstructionsFileTests
    {
        private string _dir;
        private string _path;
        private List<string> _logs;
        private CustomInstructionsFile _file;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelCustomInstructionsTests_" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "CustomInstructions.txt");
            _logs = new List<string>();
            _file = new CustomInstructionsFile(_path, delegate (string line) { _logs.Add(line); });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Test]
        public void RoundTrip_BraceHeavyMultilineText_PreservesEverything()
        {
            string text = "Always reply in Japanese.\n"
                + "{\n  \"nested\": { \"a\": [1, 2, { \"b\": true }] }\n}\n"
                + "} dangling\n{ leading\n"
                // Japanese "the code review conventions are as follows" style content.
                + "コードレビューの規約は以下の通りです";
            _file.Save(text);
            Assert.AreEqual(text, _file.Load());
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_MissingFile_ReturnsEmptyStringSilently()
        {
            Assert.AreEqual(string.Empty, _file.Load());
            Assert.IsEmpty(_logs, "a missing sidecar is normal, not an error");
        }

        [Test]
        public void Save_NullText_WritesEmptyFile()
        {
            _file.Save(null);
            Assert.IsTrue(File.Exists(_path));
            Assert.AreEqual(string.Empty, _file.Load());
        }

        [Test]
        public void Save_EmptyText_ClearsAPreviouslySavedValue()
        {
            _file.Save("something");
            _file.Save(string.Empty);
            Assert.AreEqual(string.Empty, _file.Load());
        }

        [Test]
        public void RoundTrip_Twice_SecondSaveWins()
        {
            _file.Save("first");
            _file.Save("second");
            Assert.AreEqual("second", _file.Load());
        }

        [Test]
        public void Save_CreatesMissingDirectory_AndLeavesNoTmpFile()
        {
            Assert.IsFalse(Directory.Exists(_dir), "SetUp must not pre-create the directory");
            _file.Save("hello");
            Assert.IsTrue(Directory.Exists(_dir));
            Assert.IsTrue(File.Exists(_path));
            Assert.IsFalse(File.Exists(_path + ".tmp"),
                "atomic write must not leave a .tmp behind");
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_LockedFile_ReturnsEmptyStringAndLogsOnce()
        {
            // An exclusively-locked file simulates an IO failure during
            // Load() without relying on OS-specific ACL setup; File.Exists
            // still reports true here (unlike a missing file), so this
            // exercises the catch branch rather than the "no cache" branch.
            _file.Save("something");
            using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.AreEqual(string.Empty, _file.Load());
            }
            Assert.AreEqual(1, _logs.Count, "exactly one log line, no spam");
        }
    }
}
