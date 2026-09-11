using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// MODEL-4: the Console-error ignore store moves out of the
    /// UnityYAML-serialized State.asset (raw Console message text is
    /// exactly the string class that used to corrupt it) into a JSON
    /// sidecar. Covers the sidecar's contract, the one-time legacy
    /// migration, and -- the core pin -- that the ignore text can no
    /// longer reach Unity serialization at all.
    /// </summary>
    [TestFixture]
    public class ConsoleIgnoreFileTests
    {
        private string _dir;
        private string _path;
        private ConsoleIgnoreFile _file;
        private List<string> _logs;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uap_conignore_" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "ConsoleIgnore.json");
            _logs = new List<string>();
            _file = new ConsoleIgnoreFile(_path, _logs.Add);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // -- Sidecar round trip ---------------------------------------------

        [Test]
        public void RoundTrip_HostileConsoleText_PreservesEverything()
        {
            // Exactly the content class that broke State.asset: braces,
            // embedded quotes, newlines, long compiler noise.
            var errors = new List<string>
            {
                "Assets\\X.cs(3,7): error CS1002: ; expected { \"json\": true }",
                "NullReferenceException: Object reference not set\n  at Foo.Bar () [0x00000]"
            };
            _file.Save(errors, "SomeSdk.Internal\nOtherNoise");

            List<string> loadedErrors;
            string loadedPatterns;
            Assert.IsTrue(_file.Load(out loadedErrors, out loadedPatterns));
            CollectionAssert.AreEqual(errors, loadedErrors);
            Assert.AreEqual("SomeSdk.Internal\nOtherNoise", loadedPatterns);
            Assert.IsEmpty(_logs);
        }

        [Test]
        public void Load_MissingFile_ReturnsFalseSilently()
        {
            List<string> errors;
            string patterns;
            Assert.IsFalse(_file.Load(out errors, out patterns));
            Assert.IsEmpty(errors);
            Assert.AreEqual(string.Empty, patterns);
            Assert.IsEmpty(_logs, "a missing sidecar is normal, not an error");
        }

        [Test]
        public void Load_CorruptFile_ReturnsFalse_DeletesIt_LogsOnce()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{ not json");

            List<string> errors;
            string patterns;
            Assert.IsFalse(_file.Load(out errors, out patterns));
            Assert.IsFalse(File.Exists(_path), "parse-class corruption deletes (MODEL-1 classification)");
            Assert.AreEqual(1, _logs.Count);
        }

        [Test]
        public void Save_EmptyStore_StillWritesAValidFile()
        {
            _file.Save(new List<string>(), string.Empty);
            List<string> errors;
            string patterns;
            Assert.IsTrue(_file.Load(out errors, out patterns));
            Assert.IsEmpty(errors);
        }

        // -- The MODEL-4 core pin: ignore text stays out of Unity serialization

        [Test]
        public void IgnoreFields_NeverReachUnitySerialization()
        {
            var settings = new PanelSettings();
            settings.ignoredConsoleErrors.Add("HostileError { \"brace\": \"soup\" }");
            settings.ignoredConsoleErrorPatterns = "Hostile.Pattern";

            string serialized = JsonUtility.ToJson(settings);

            StringAssert.DoesNotContain("HostileError", serialized,
                "ignored-error text must be [NonSerialized] -- one hostile"
                + " Console message in the YAML corrupted the WHOLE"
                + " State.asset before MODEL-4");
            StringAssert.DoesNotContain("Hostile.Pattern", serialized);
        }

        // -- Legacy migration -----------------------------------------------

        [Test]
        public void MigrateLegacy_MovesDataToTheLiveFields_AndClearsTheHolders()
        {
            var settings = new PanelSettings();
            settings.SetLegacyConsoleIgnoresForTests(
                new List<string> { "old ignored error" }, "old.pattern");

            Assert.IsTrue(settings.MigrateLegacyConsoleIgnores());
            CollectionAssert.Contains(settings.ignoredConsoleErrors, "old ignored error");
            Assert.AreEqual("old.pattern", settings.ignoredConsoleErrorPatterns);

            // The holders are gone: serializing now carries no legacy text,
            // and a second migration reports nothing to do.
            StringAssert.DoesNotContain("old ignored error", JsonUtility.ToJson(settings));
            Assert.IsFalse(settings.MigrateLegacyConsoleIgnores());
        }

        [Test]
        public void MigrateLegacy_SidecarDataWins_LegacyOnlyFillsGaps()
        {
            var settings = new PanelSettings();
            // Simulates OnEnable's order: the sidecar hydrated the live
            // fields first, THEN the legacy asset data folds in.
            settings.ignoredConsoleErrors.Add("from sidecar");
            settings.ignoredConsoleErrorPatterns = "sidecar.pattern";
            settings.SetLegacyConsoleIgnoresForTests(
                new List<string> { "from sidecar", "legacy only" }, "legacy.pattern");

            Assert.IsTrue(settings.MigrateLegacyConsoleIgnores());

            CollectionAssert.AreEqual(
                new List<string> { "from sidecar", "legacy only" },
                settings.ignoredConsoleErrors,
                "existing sidecar entries stay put (no duplicate); missing"
                + " legacy entries append");
            Assert.AreEqual("sidecar.pattern", settings.ignoredConsoleErrorPatterns,
                "a non-empty live pattern text wins over the legacy one");
        }

        [Test]
        public void MigrateLegacy_NothingLegacy_ReportsFalse()
        {
            Assert.IsFalse(new PanelSettings().MigrateLegacyConsoleIgnores());
        }
    }
}
