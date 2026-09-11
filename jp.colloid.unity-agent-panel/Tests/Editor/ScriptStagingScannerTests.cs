using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real-filesystem tests for the staging scanner (plain System.IO, no Unity dependency) against a temp directory tree.</summary>
    [TestFixture]
    public class ScriptStagingScannerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "UapStagingScannerTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void ListStagedScriptFiles_MissingRoot_ReturnsEmpty()
        {
            List<string> result = ScriptStagingScanner.ListStagedScriptFiles(Path.Combine(_root, "does-not-exist"));
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ListStagedScriptFiles_NullOrEmptyRoot_ReturnsEmpty()
        {
            Assert.AreEqual(0, ScriptStagingScanner.ListStagedScriptFiles(null).Count);
            Assert.AreEqual(0, ScriptStagingScanner.ListStagedScriptFiles(string.Empty).Count);
        }

        [Test]
        public void ListStagedScriptFiles_FindsTopLevelCsAndAsmdef()
        {
            File.WriteAllText(Path.Combine(_root, "Foo.cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(_root, "MyAsm.asmdef"), "{}");
            File.WriteAllText(Path.Combine(_root, "readme.txt"), "not staged");

            List<string> result = ScriptStagingScanner.ListStagedScriptFiles(_root);

            CollectionAssert.AreEquivalent(new[] { "Foo.cs", "MyAsm.asmdef" }, result);
        }

        [Test]
        public void ListStagedScriptFiles_FindsNestedFiles_WithForwardSlashRelativePaths()
        {
            string nested = Path.Combine(_root, "Sub", "Deeper");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "Bar.cs"), "class Bar {}");

            List<string> result = ScriptStagingScanner.ListStagedScriptFiles(_root);

            CollectionAssert.Contains(result, "Sub/Deeper/Bar.cs");
        }

        [Test]
        public void ListStagedScriptFiles_IsCaseInsensitiveOnExtension()
        {
            File.WriteAllText(Path.Combine(_root, "Foo.CS"), "class Foo {}");
            List<string> result = ScriptStagingScanner.ListStagedScriptFiles(_root);
            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void ListStagedScriptFiles_ResultIsSorted()
        {
            File.WriteAllText(Path.Combine(_root, "Zed.cs"), "class Zed {}");
            File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha {}");

            List<string> result = ScriptStagingScanner.ListStagedScriptFiles(_root);

            CollectionAssert.AreEqual(new[] { "Alpha.cs", "Zed.cs" }, result);
        }

        [Test]
        public void ToAssetsRelativePath_IsIdentityMapping()
        {
            Assert.AreEqual("Sub/Bar.cs", ScriptStagingScanner.ToAssetsRelativePath("Sub/Bar.cs"));
        }
    }
}
