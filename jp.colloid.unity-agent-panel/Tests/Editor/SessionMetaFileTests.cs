using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Round-trip and tolerant-load suite for the session-meta JSON sidecar.
    /// This store's whole reason for existing outside the ScriptableSingleton
    /// State.asset is <see cref="SessionMeta.titleOverride"/>: raw user text
    /// typed into a rename box, the same content class that broke UnityYAML
    /// for the transcript cache (see SessionCacheFileTests and
    /// SessionMetaFile's class comment). All source literals here are strict
    /// ASCII; where a non-ASCII character is needed it is built from a char
    /// code rather than embedded directly.
    /// </summary>
    public class SessionMetaFileTests
    {
        private string _dir;
        private string _path;
        private List<string> _logs;
        private SessionMetaFile _file;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelSessionMetaTests_" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "SessionMeta.json");
            _logs = new List<string>();
            _file = new SessionMetaFile(_path, delegate (string line) { _logs.Add(line); });
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
        // Round trips
        // ---------------------------------------------------------------

        [Test]
        public void RoundTrip_AllFields_PreservesEverything()
        {
            var store = new SessionMetaStore();
            SessionMeta meta = store.Edit("sess-1");
            meta.pinned = true;
            meta.archived = true;
            meta.titleOverride = "My renamed session";
            meta.customGroupId = "group-a";
            meta.recordedScenePath = "Assets/Scenes/Main.unity";
            store.groups.Add(new SessionGroup { id = "group-a", name = "Group A", order = 0 });

            _file.Save(store);
            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.bySessionId.ContainsKey("sess-1"));
            SessionMeta loadedMeta = loaded.bySessionId["sess-1"];
            Assert.IsTrue(loadedMeta.pinned);
            Assert.IsTrue(loadedMeta.archived);
            Assert.AreEqual("My renamed session", loadedMeta.titleOverride);
            Assert.AreEqual("group-a", loadedMeta.customGroupId);
            Assert.AreEqual("Assets/Scenes/Main.unity", loadedMeta.recordedScenePath);

            Assert.AreEqual(1, loaded.groups.Count);
            Assert.AreEqual("group-a", loaded.groups[0].id);
            Assert.AreEqual("Group A", loaded.groups[0].name);
            Assert.AreEqual(0, loaded.groups[0].order);

            Assert.IsEmpty(_logs);
        }

        [Test]
        public void RoundTrip_EmptyStore_Works()
        {
            _file.Save(new SessionMetaStore());
            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.bySessionId.Count);
            Assert.AreEqual(0, loaded.groups.Count);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void RoundTrip_PoisonTitleOverride_PreservesExactText()
        {
            // The exact content class that broke UnityYAML for the transcript
            // cache: braces, quotes, backslashes, and non-ASCII text, all in
            // one raw user-typed rename string.
            string hiragana = ((char)0x3042).ToString(); // "a" in hiragana
            string poison = "{title: \"quoted\"} \\server\\share " + hiragana + " trailing\\";

            var store = new SessionMetaStore();
            store.Edit("sess-poison").titleOverride = poison;

            _file.Save(store);
            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(poison, loaded.bySessionId["sess-poison"].titleOverride);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void RoundTrip_Groups_PreserveInsertionOrder()
        {
            // Insertion order must survive even when it disagrees with the
            // numeric "order" field -- the array position is the source of
            // truth for round-tripping, not a re-sort by that field.
            var store = new SessionMetaStore();
            store.groups.Add(new SessionGroup { id = "g-third", name = "Third", order = 5 });
            store.groups.Add(new SessionGroup { id = "g-first", name = "First", order = 1 });
            store.groups.Add(new SessionGroup { id = "g-second", name = "Second", order = 3 });

            _file.Save(store);
            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(3, loaded.groups.Count);
            Assert.AreEqual("g-third", loaded.groups[0].id);
            Assert.AreEqual(5, loaded.groups[0].order);
            Assert.AreEqual("g-first", loaded.groups[1].id);
            Assert.AreEqual(1, loaded.groups[1].order);
            Assert.AreEqual("g-second", loaded.groups[2].id);
            Assert.AreEqual(3, loaded.groups[2].order);
        }

        [Test]
        public void RoundTrip_Twice_SecondSaveWins()
        {
            var first = new SessionMetaStore();
            first.Edit("sess-1").pinned = true;
            _file.Save(first);

            var second = new SessionMetaStore();
            second.Edit("sess-2").archived = true;
            _file.Save(second);

            SessionMetaStore loaded = _file.Load();
            Assert.IsFalse(loaded.bySessionId.ContainsKey("sess-1"));
            Assert.IsTrue(loaded.bySessionId.ContainsKey("sess-2"));
        }

        // ---------------------------------------------------------------
        // Compact() on save
        // ---------------------------------------------------------------

        [Test]
        public void Save_Compacts_DropsEmptyEntriesButKeepsRealOnes()
        {
            var store = new SessionMetaStore();
            store.Edit("empty-session"); // touched, but every field left default -> IsEmpty()
            store.Edit("real-session").pinned = true;

            _file.Save(store);

            // Compact() mutates the passed-in store too, per SessionMetaFile's contract.
            Assert.IsFalse(store.bySessionId.ContainsKey("empty-session"),
                "Save must compact the caller's store, not just its own copy");
            Assert.IsTrue(store.bySessionId.ContainsKey("real-session"));

            string json = File.ReadAllText(_path);
            StringAssert.DoesNotContain("empty-session", json);
            StringAssert.Contains("real-session", json);

            SessionMetaStore loaded = _file.Load();
            Assert.IsFalse(loaded.bySessionId.ContainsKey("empty-session"));
            Assert.IsTrue(loaded.bySessionId.ContainsKey("real-session"));
        }

        [Test]
        public void Save_Compacts_EntryEmptiedAfterPreviousSave_IsDroppedOnNextSave()
        {
            var store = new SessionMetaStore();
            SessionMeta meta = store.Edit("sess-1");
            meta.pinned = true;
            _file.Save(store);

            // User unpins it -- the entry now carries no information.
            meta.pinned = false;
            _file.Save(store);

            SessionMetaStore loaded = _file.Load();
            Assert.IsFalse(loaded.bySessionId.ContainsKey("sess-1"),
                "an entry that reverted to all-default values must not persist forever");
        }

        // ---------------------------------------------------------------
        // Atomic write / directory handling
        // ---------------------------------------------------------------

        [Test]
        public void Save_CreatesMissingDirectory_AndLeavesNoTmpFile()
        {
            Assert.IsFalse(Directory.Exists(_dir), "SetUp must not pre-create the directory");
            var store = new SessionMetaStore();
            store.Edit("sess-1").pinned = true;

            _file.Save(store);

            Assert.IsTrue(Directory.Exists(_dir));
            Assert.IsTrue(File.Exists(_path));
            Assert.IsFalse(File.Exists(_path + ".tmp"),
                "atomic write must not leave a .tmp behind");
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Save_OverExistingFile_LeavesNoTmpFile()
        {
            var first = new SessionMetaStore();
            first.Edit("sess-1").pinned = true;
            _file.Save(first);

            var second = new SessionMetaStore();
            second.Edit("sess-2").archived = true;
            _file.Save(second);

            Assert.IsFalse(File.Exists(_path + ".tmp"));
            SessionMetaStore loaded = _file.Load();
            Assert.IsTrue(loaded.bySessionId.ContainsKey("sess-2"));
        }

        [Test]
        public void Save_NullStore_IsNoOp()
        {
            _file.Save(null);
            Assert.IsFalse(File.Exists(_path));
            Assert.IsEmpty(_logs);
        }

        // ---------------------------------------------------------------
        // Tolerant load
        // ---------------------------------------------------------------

        [Test]
        public void Load_MissingFile_ReturnsNullSilently()
        {
            Assert.IsNull(_file.Load());
            Assert.IsEmpty(_logs, "a missing sidecar is normal, not an error");
        }

        [Test]
        public void Load_TruncatedJson_ReturnsNullDeletesFileLogsOnce()
        {
            var store = new SessionMetaStore();
            store.Edit("sess-1").pinned = true;
            _file.Save(store);

            string json = File.ReadAllText(_path);
            File.WriteAllText(_path, json.Substring(0, json.Length / 2));

            Assert.IsNull(_file.Load());
            Assert.IsFalse(File.Exists(_path), "the corrupt file must be deleted");
            Assert.AreEqual(1, _logs.Count, "exactly one log line, no spam");

            // A second Load sees no file and stays silent (no log spam).
            Assert.IsNull(_file.Load());
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_GarbageBytes_ReturnsNullAndDeletes()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(_path, new byte[] { 0xFF, 0xFE, 0x00, 0x13, 0x37, 0x7B, 0x22 });

            Assert.IsNull(_file.Load());
            Assert.IsFalse(File.Exists(_path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_NonObjectRoot_ReturnsNullAndDeletes()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "[1,2,3]");

            Assert.IsNull(_file.Load());
            Assert.IsFalse(File.Exists(_path));
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Load_UnknownExtraKeys_AreIgnored()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"formatVersion\":1,\"unknownTopLevelKey\":true,"
                + "\"sessions\":{\"sess-1\":{\"pinned\":true,\"extraField\":123,"
                + "\"nested\":{\"x\":1}}},"
                + "\"groups\":[{\"id\":\"g1\",\"name\":\"G1\",\"order\":0,"
                + "\"extraGroupField\":\"z\"}]}");

            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.bySessionId["sess-1"].pinned);
            Assert.AreEqual(1, loaded.groups.Count);
            Assert.AreEqual("g1", loaded.groups[0].id);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_MissingKeysWithinEntries_DefaultTolerantly()
        {
            // A session entry and a group entry each missing every optional
            // key: every accessor must fall back rather than throw.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"formatVersion\":1,\"sessions\":{\"sess-1\":{}},"
                + "\"groups\":[{}]}");

            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            SessionMeta meta = loaded.bySessionId["sess-1"];
            Assert.IsFalse(meta.pinned);
            Assert.IsFalse(meta.archived);
            Assert.AreEqual(string.Empty, meta.titleOverride);
            Assert.AreEqual(string.Empty, meta.customGroupId);
            Assert.AreEqual(string.Empty, meta.recordedScenePath);

            Assert.AreEqual(1, loaded.groups.Count);
            Assert.AreEqual(string.Empty, loaded.groups[0].id);
            Assert.AreEqual(string.Empty, loaded.groups[0].name);
            Assert.AreEqual(0, loaded.groups[0].order);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_MissingSessionsAndGroupsKeys_ReturnsEmptyStore()
        {
            // A hand-written or future-shaped file that omits both
            // collection keys entirely must still degrade to an empty,
            // usable store rather than throwing.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{\"formatVersion\":1}");

            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.bySessionId.Count);
            Assert.AreEqual(0, loaded.groups.Count);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_FutureFormatVersion_StillLoadsTolerantly()
        {
            // Same policy as SessionCacheFile: formatVersion is written but
            // never gated on read. A file from a newer panel build must
            // still load with whatever fields this reader understands.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path,
                "{\"formatVersion\":999,\"sessions\":{\"sess-1\":{\"pinned\":true,"
                + "\"titleOverride\":\"kept\"}},\"groups\":[]}");

            SessionMetaStore loaded = _file.Load();

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.bySessionId["sess-1"].pinned);
            Assert.AreEqual("kept", loaded.bySessionId["sess-1"].titleOverride);
            Assert.IsEmpty(_logs);
        }

        // -- MODEL-11: SessionMetaStore.Get's miss path ----------------------

        /// <summary>
        /// Get used to hand out ONE shared static Empty for every miss,
        /// "read-only by convention". A single caller mutating that return
        /// value would have poisoned the result of every later miss. Fresh
        /// instance per miss kills the failure class outright.
        /// </summary>
        [Test]
        public void StoreGet_MutatingAMissResult_DoesNotPoisonLaterMisses()
        {
            var store = new SessionMetaStore();

            store.Get("nope").pinned = true;

            Assert.IsFalse(store.Get("other").pinned,
                "a miss result must be a private copy, never a shared instance");
            Assert.IsFalse(store.Get("nope").pinned,
                "not even the SAME id may see the stray write -- nothing was ever stored");
        }

        [Test]
        public void StoreGet_MissNeverCreatesAnEntry_UnlikeEdit()
        {
            var store = new SessionMetaStore();

            store.Get("nope");
            Assert.AreEqual(0, store.bySessionId.Count, "Get must stay read-only");

            store.Edit("made").pinned = true;
            Assert.AreEqual(1, store.bySessionId.Count, "Edit is the creating accessor");
            Assert.IsTrue(store.Get("made").pinned, "and Get then returns the REAL stored entry");
        }
    }
}
