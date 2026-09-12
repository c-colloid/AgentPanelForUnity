using System;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Contract tests for the shared MODEL-1/MODEL-2 helper every
    /// persistence sidecar and Ops generated file now writes through
    /// (real temp-directory IO, the same convention as the stores'
    /// own tests). The crash-window claims in AtomicFile's class comment
    /// are pinned here by constructing each interrupted state by hand.
    /// </summary>
    [TestFixture]
    public class AtomicFileTests
    {
        private string _dir;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uap_atomic_" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "store.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // -- WriteAllText ----------------------------------------------------

        [Test]
        public void WriteAllText_CreatesTheDirectory_AndTheFile()
        {
            Assert.IsFalse(Directory.Exists(_dir));
            Assert.IsTrue(AtomicFile.WriteAllText(_path, "{\"v\":1}"));
            Assert.AreEqual("{\"v\":1}", File.ReadAllText(_path));
        }

        [Test]
        public void WriteAllText_Overwrite_LeavesNewContent_AndNoStagingTwins()
        {
            AtomicFile.WriteAllText(_path, "old");
            Assert.IsTrue(AtomicFile.WriteAllText(_path, "new"));
            Assert.AreEqual("new", File.ReadAllText(_path));
            Assert.IsFalse(File.Exists(_path + AtomicFile.TmpSuffix), "no stale .tmp");
            Assert.IsFalse(File.Exists(_path + AtomicFile.BackupSuffix), "no stale .bak");
        }

        [Test]
        public void WriteAllText_NullContent_WritesEmptyFile()
        {
            Assert.IsTrue(AtomicFile.WriteAllText(_path, null));
            Assert.AreEqual(string.Empty, File.ReadAllText(_path));
        }

        [Test]
        public void WriteAllText_CleansAStaleBackup_FromAPreviousInterruptedFallback()
        {
            // A .bak that survived (fallback crashed after the primary
            // landed) is one generation stale -- the next successful write
            // must remove it so ReadAllText can never resurrect it.
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path + AtomicFile.BackupSuffix, "ancient");
            File.WriteAllText(_path, "current");
            Assert.IsTrue(AtomicFile.WriteAllText(_path, "newer"));
            Assert.IsFalse(File.Exists(_path + AtomicFile.BackupSuffix));
            Assert.AreEqual("newer", File.ReadAllText(_path));
        }

        // -- ReadAllText -----------------------------------------------------

        [Test]
        public void ReadAllText_MissingFile_ReturnsNull()
        {
            Assert.IsNull(AtomicFile.ReadAllText(_path));
        }

        [Test]
        public void ReadAllText_InterruptedFallback_RestoresTheBackupGeneration()
        {
            // The exact MODEL-2 crash window: the fallback renamed the old
            // file aside and died before moving the new one in. The old
            // code's Delete+Move equivalent lost EVERYTHING here; now the
            // previous generation must come back.
            AtomicFile.WriteAllText(_path, "{\"gen\":1}");
            File.Move(_path, _path + AtomicFile.BackupSuffix);
            Assert.IsFalse(File.Exists(_path), "precondition: primary missing");

            Assert.AreEqual("{\"gen\":1}", AtomicFile.ReadAllText(_path));
            Assert.IsTrue(File.Exists(_path), "the backup must be restored to the primary path");
            Assert.IsFalse(File.Exists(_path + AtomicFile.BackupSuffix));
        }

        [Test]
        public void ReadAllText_NeverRestoresABackup_OverAnExistingPrimary()
        {
            AtomicFile.WriteAllText(_path, "current");
            File.WriteAllText(_path + AtomicFile.BackupSuffix, "stale");
            Assert.AreEqual("current", AtomicFile.ReadAllText(_path));
            Assert.AreEqual("stale", File.ReadAllText(_path + AtomicFile.BackupSuffix),
                "an existing primary always wins; the stale backup is left"
                + " for the next write to clean");
        }

        [Test]
        public void ReadAllText_WhileAnotherHandleHoldsTheFileForWriting_StillReads()
        {
            // MODEL-3 regression (design note 2026-09-12-session-cache-
            // transient-read-failure.md). THE defect: a writer handle that
            // explicitly permits readers (FileShare.Read) -- an antivirus
            // scanner, an indexer, a cloud-sync agent on a file we just
            // replaced -- still made the old File.ReadAllText fail, because
            // ITS OWN share request (FileShare.Read) refuses to coexist with
            // the holder's WRITE access. The panel read that failure as "no
            // cache" and eventually wrote an empty session over the user's
            // transcript. Opening with ReadWrite|Delete is what makes the
            // two handles compatible.
            AtomicFile.WriteAllText(_path, "{\"transcript\":\"mine\"}");
            using (new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                Assert.AreEqual("{\"transcript\":\"mine\"}", AtomicFile.ReadAllText(_path));
            }
        }

        [Test]
        public void ReadAllText_ExclusivelyLockedFile_StillThrowsIoException_ForTheCallerToClassify()
        {
            // The retry is bounded, not infinite: a holder that denies ALL
            // sharing must still surface as an IOException so the sidecars'
            // IsCorruption branch can keep the file instead of deleting it.
            AtomicFile.WriteAllText(_path, "locked-out");
            using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                IOException thrown = Assert.Throws<IOException>(
                    delegate { AtomicFile.ReadAllText(_path); });
                Assert.IsFalse(AtomicFile.IsCorruption(thrown),
                    "a lock is not content damage -- the file must be kept");
            }
        }

        [Test]
        public void ReadAllText_MissingFile_IsNotRetried()
        {
            // A vanished file is not a lock, and the retry budget is
            // main-thread time: the miss must come back immediately.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsNull(AtomicFile.ReadAllText(_path));
            clock.Stop();
            Assert.Less(clock.ElapsedMilliseconds, 50,
                "a missing file must not spend the read-retry backoff");
        }

        // -- IsCorruption (MODEL-1) -----------------------------------------

        [Test]
        public void IsCorruption_TransientIoClasses_AreNotCorruption()
        {
            Assert.IsFalse(AtomicFile.IsCorruption(new IOException("locked")));
            Assert.IsFalse(AtomicFile.IsCorruption(new FileNotFoundException()));
            Assert.IsFalse(AtomicFile.IsCorruption(new UnauthorizedAccessException()));
        }

        [Test]
        public void IsCorruption_ContentClasses_AreCorruption()
        {
            Assert.IsTrue(AtomicFile.IsCorruption(new JsonParseException("bad", 0)));
            Assert.IsTrue(AtomicFile.IsCorruption(new InvalidDataException("shape")));
            Assert.IsTrue(AtomicFile.IsCorruption(new FormatException()));
        }

        [Test]
        public void IsCorruption_UnknownOrNull_DefaultsToNotCorruption()
        {
            // The data-safe default: when we cannot NAME the failure as
            // content damage, we do not delete the user's file over it.
            Assert.IsFalse(AtomicFile.IsCorruption(new Exception("mystery")));
            Assert.IsFalse(AtomicFile.IsCorruption(null));
        }

        // -- WriteAllTextIfChanged ------------------------------------------

        [Test]
        public void WriteAllTextIfChanged_SameContent_DoesNotTouchTheFile()
        {
            AtomicFile.WriteAllText(_path, "stable");
            DateTime before = File.GetLastWriteTimeUtc(_path);
            Assert.IsTrue(AtomicFile.WriteAllTextIfChanged(_path, "stable"));
            Assert.AreEqual(before, File.GetLastWriteTimeUtc(_path),
                "identical content must skip the write (mtime preserved)");
        }

        [Test]
        public void WriteAllTextIfChanged_NewContent_Rewrites()
        {
            AtomicFile.WriteAllText(_path, "old");
            Assert.IsTrue(AtomicFile.WriteAllTextIfChanged(_path, "new"));
            Assert.AreEqual("new", File.ReadAllText(_path));
        }

        // -- TryDelete -------------------------------------------------------

        [Test]
        public void TryDelete_RemovesThePrimary_AndItsStaleTmp_ButKeepsTheBackup()
        {
            AtomicFile.WriteAllText(_path, "corrupt-content");
            File.WriteAllText(_path + AtomicFile.TmpSuffix, "half-written");
            File.WriteAllText(_path + AtomicFile.BackupSuffix, "previous-generation");

            AtomicFile.TryDelete(_path);

            Assert.IsFalse(File.Exists(_path));
            Assert.IsFalse(File.Exists(_path + AtomicFile.TmpSuffix));
            Assert.IsTrue(File.Exists(_path + AtomicFile.BackupSuffix),
                "the backup is the previous -- possibly healthy -- generation"
                + " and must survive a corrupt-primary delete so the next"
                + " read can restore it");
            // ...and the next read indeed falls back to it:
            Assert.AreEqual("previous-generation", AtomicFile.ReadAllText(_path));
        }
    }
}
