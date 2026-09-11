using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// <see cref="ExtensionProfileCatalog.LoadFromDirectory"/> behavior
    /// (temp-directory fixtures) plus the bundled *.json files' own content
    /// rules (design section C1 hard content rules, C6 "bundled JSON files
    /// parse + content rules").
    /// </summary>
    public class ExtensionProfileCatalogTests
    {
        private string _dir;
        private List<string> _logs;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "ExtensionProfileCatalogTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _logs = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private void Write(string fileName, string content)
        {
            File.WriteAllText(Path.Combine(_dir, fileName), content);
        }

        [Test]
        public void LoadFromDirectory_MissingDirectory_ReturnsEmptyList_NeverThrows()
        {
            string bogus = Path.Combine(_dir, "does-not-exist");
            List<ExtensionProfile> result = ExtensionProfileCatalog.LoadFromDirectory(bogus, delegate (string l) { _logs.Add(l); });
            Assert.IsEmpty(result);
        }

        [Test]
        public void LoadFromDirectory_NullOrEmptyDirectory_ReturnsEmptyList()
        {
            Assert.IsEmpty(ExtensionProfileCatalog.LoadFromDirectory(null));
            Assert.IsEmpty(ExtensionProfileCatalog.LoadFromDirectory(string.Empty));
        }

        [Test]
        public void LoadFromDirectory_ParsesEveryValidFile_InOrdinalFilenameOrder()
        {
            Write("b.json", "{ \"id\": \"b-profile\" }");
            Write("a.json", "{ \"id\": \"a-profile\" }");
            List<ExtensionProfile> result = ExtensionProfileCatalog.LoadFromDirectory(_dir);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("a-profile", result[0].Id);
            Assert.AreEqual("b-profile", result[1].Id);
        }

        [Test]
        public void LoadFromDirectory_SkipsUnparsableFile_KeepsTheRest_AndLogs()
        {
            Write("good.json", "{ \"id\": \"good-profile\" }");
            Write("bad.json", "{ not json");
            List<ExtensionProfile> result = ExtensionProfileCatalog.LoadFromDirectory(
                _dir, delegate (string l) { _logs.Add(l); });
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("good-profile", result[0].Id);
            Assert.IsNotEmpty(_logs);
        }

        [Test]
        public void LoadFromDirectory_IgnoresNonJsonFiles()
        {
            Write("profile.json", "{ \"id\": \"only-one\" }");
            Write("readme.txt", "not a profile");
            List<ExtensionProfile> result = ExtensionProfileCatalog.LoadFromDirectory(_dir);
            Assert.AreEqual(1, result.Count);
        }

        // -- Core's own (framework) directory ------------------------------------
        // 2026-09-11 core/pro split (design note docs/design-notes/
        // 2026-09-11-core-pro-split.md): the five bundled *.json profiles
        // moved to Agent Panel Pro -- Core's ResolveProfilesDirectory() code
        // path stays (LoadBundled still merges it in), but the directory
        // itself now ships with zero *.json of its own. The bundled-content
        // rule tests that used to live here (exactly five ids, bakery/
        // finalik specifics, ASCII/line-count caps) moved with the profiles
        // to ExtensionProfileCatalogBundledTests in Pro's test assembly.

        [Test]
        public void CoreOwnDirectory_ResolvesToARealDirectory_WithNoBundledJsonOfItsOwn()
        {
            string dir = ExtensionProfileCatalog.ResolveProfilesDirectory();
            Assert.IsTrue(Directory.Exists(dir), "ResolveProfilesDirectory() did not resolve to a real directory: " + dir);
            Assert.AreEqual(0, Directory.GetFiles(dir, "*.json").Length,
                "Core ships no bundled profile JSON of its own since the 2026-09-11 core/pro split -- see Agent Panel Pro.");
        }
    }
}
