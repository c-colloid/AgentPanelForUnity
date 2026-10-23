using System;
using System.IO;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapStdioRelayBuilder (docs/design-notes/2026-09-22-uloop-phase1.md
    /// section 2.7 item 3): the pure path-resolution helpers run against a
    /// FAKE directory tree here (no real Unity toolchain needed, so these
    /// always run), and the cached-failure behaviour is exercised with a
    /// deliberately-broken "dotnet host" so the compile step fails fast and
    /// deterministically without needing a real csc.dll. The one test that
    /// touches the REAL Unity install (CapturePaths + an actual compile)
    /// is guarded with Assert.Ignore when this Editor's bundled dotnet/
    /// Roslyn cannot be found -- see that test's own comment.
    /// </summary>
    [TestFixture]
    public class UapStdioRelayBuilderTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "uap-relay-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            UapStdioRelayBuilder.ClearCachesForTests();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup; a locked file left by a failed
                // Process.Start attempt must not fail the test itself.
            }
        }

        // -- ResolveToolchainFromContentsPath ---------------------------------------

        private void WriteFakeToolchain(string root, bool isWindows)
        {
            string dotnetName = isWindows ? "dotnet.exe" : "dotnet";
            Directory.CreateDirectory(Path.Combine(root, "NetCoreRuntime"));
            File.WriteAllText(Path.Combine(root, "NetCoreRuntime", dotnetName), "fake host");
            string sharedDir = Path.Combine(root, "NetCoreRuntime", "shared", "Microsoft.NETCore.App", "8.0.10");
            Directory.CreateDirectory(sharedDir);
            File.WriteAllText(Path.Combine(sharedDir, "System.Private.CoreLib.dll"), "fake");
            Directory.CreateDirectory(Path.Combine(root, "DotNetSdkRoslyn"));
            File.WriteAllText(Path.Combine(root, "DotNetSdkRoslyn", "csc.dll"), "fake csc");
        }

        [Test]
        public void ResolveToolchainFromContentsPath_DirectLayout_FindsToolchain()
        {
            WriteFakeToolchain(_root, true);
            RelayToolchainPaths result = UapStdioRelayBuilder.ResolveToolchainFromContentsPath(_root, true);
            Assert.IsNull(result.FailureReason, result.FailureReason);
            Assert.AreEqual(Path.Combine(_root, "NetCoreRuntime", "dotnet.exe"), result.DotnetHost);
            Assert.AreEqual(Path.Combine(_root, "DotNetSdkRoslyn", "csc.dll"), result.CscDll);
            Assert.AreEqual("8.0.10", result.SharedFrameworkVersion);
        }

        [Test]
        public void ResolveToolchainFromContentsPath_ResourcesScriptingLayout_FindsToolchain()
        {
            // The macOS layout uLoop's own resolver also checks (this
            // class's doc comment): <contentsPath>/Resources/Scripting/...
            string scriptingRoot = Path.Combine(_root, "Resources", "Scripting");
            Directory.CreateDirectory(scriptingRoot);
            WriteFakeToolchain(scriptingRoot, false);
            RelayToolchainPaths result = UapStdioRelayBuilder.ResolveToolchainFromContentsPath(_root, false);
            Assert.IsNull(result.FailureReason, result.FailureReason);
            Assert.AreEqual(Path.Combine(scriptingRoot, "NetCoreRuntime", "dotnet"), result.DotnetHost);
        }

        [Test]
        public void ResolveToolchainFromContentsPath_MissingToolchain_ReturnsFailureReasonWithoutThrowing()
        {
            RelayToolchainPaths result = UapStdioRelayBuilder.ResolveToolchainFromContentsPath(_root, true);
            Assert.IsNotNull(result.FailureReason);
            Assert.IsNull(result.DotnetHost);
        }

        [Test]
        public void ResolveToolchainFromContentsPath_NonExistentRoot_ReturnsFailureReasonWithoutThrowing()
        {
            RelayToolchainPaths result = UapStdioRelayBuilder.ResolveToolchainFromContentsPath(
                Path.Combine(_root, "does-not-exist"), true);
            Assert.IsNotNull(result.FailureReason);
        }

        [Test]
        public void ResolveHighestVersionDirectory_PicksHighestSemver()
        {
            string sharedRoot = Path.Combine(_root, "shared");
            Directory.CreateDirectory(Path.Combine(sharedRoot, "6.0.1"));
            Directory.CreateDirectory(Path.Combine(sharedRoot, "8.0.10"));
            Directory.CreateDirectory(Path.Combine(sharedRoot, "8.0.2"));
            string best = UapStdioRelayBuilder.ResolveHighestVersionDirectory(sharedRoot);
            Assert.AreEqual(Path.Combine(sharedRoot, "8.0.10"), best);
        }

        [Test]
        public void ResolveHighestVersionDirectory_MissingDirectory_ReturnsNull()
        {
            Assert.IsNull(UapStdioRelayBuilder.ResolveHighestVersionDirectory(Path.Combine(_root, "nope")));
        }

        // -- TryGetOrBuild: argument/availability guards ----------------------------

        [Test]
        public void TryGetOrBuild_NullPaths_ReturnsNullWithoutThrowing()
        {
            Assert.IsNull(UapStdioRelayBuilder.TryGetOrBuild(null, "http://127.0.0.1:1/mcp", null));
        }

        [Test]
        public void TryGetOrBuild_FailedPaths_ReturnsNullWithoutThrowing()
        {
            var paths = new RelayToolchainPaths { FailureReason = "no toolchain" };
            Assert.IsNull(UapStdioRelayBuilder.TryGetOrBuild(paths, "http://127.0.0.1:1/mcp", null));
        }

        [Test]
        public void TryGetOrBuild_EmptyUrl_ReturnsNullWithoutThrowing()
        {
            WriteFakeToolchain(_root, true);
            RelayToolchainPaths paths = UapStdioRelayBuilder.ResolveToolchainFromContentsPath(_root, true);
            paths.SourceDir = _root;
            paths.OutputRoot = Path.Combine(_root, "out");
            Assert.IsNull(UapStdioRelayBuilder.TryGetOrBuild(paths, string.Empty, null));
        }

        [Test]
        public void TryGetOrBuild_MissingSourceFiles_ReturnsNullWithoutThrowing()
        {
            WriteFakeToolchain(_root, true);
            RelayToolchainPaths paths = UapStdioRelayBuilder.ResolveToolchainFromContentsPath(_root, true);
            paths.SourceDir = Path.Combine(_root, "no-source-here");
            paths.OutputRoot = Path.Combine(_root, "out");
            Assert.IsNull(UapStdioRelayBuilder.TryGetOrBuild(paths, "http://127.0.0.1:1/mcp", null));
        }

        // -- cached-failure behaviour -------------------------------------------------

        [Test]
        public void TryGetOrBuild_CompileFailure_IsCachedAndNotRecompiledOnSecondCall()
        {
            // Deterministic failure with no real csc.dll needed: the
            // "dotnet host" is a path that does not exist, so Process.Start
            // fails immediately -- Compile() still runs once, its failure
            // is cached by content hash, and a second call must reuse that
            // cached reason instead of starting the process again (design
            // section 2.7 item 3's "cache failure reasons").
            string sourceDir = Path.Combine(_root, "source");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "RelayCore.cs"), "// fake source for test");
            File.WriteAllText(Path.Combine(sourceDir, "RelayMain.cs"), "// fake source for test");
            string sharedDir = Path.Combine(_root, "shared-fw");
            Directory.CreateDirectory(sharedDir);
            File.WriteAllText(Path.Combine(sharedDir, "System.Private.CoreLib.dll"), "fake");

            var paths = new RelayToolchainPaths
            {
                SourceDir = sourceDir,
                DotnetHost = Path.Combine(_root, "no-such-dotnet-host"),
                CscDll = Path.Combine(_root, "no-such-csc.dll"),
                SharedFrameworkDir = sharedDir,
                SharedFrameworkVersion = "8.0.10",
                OutputRoot = Path.Combine(_root, "out")
            };

            int before = UapStdioRelayBuilder.CompileAttemptCountForTests;
            AcpStdioServer first = UapStdioRelayBuilder.TryGetOrBuild(paths, "http://127.0.0.1:1/mcp", null);
            AcpStdioServer second = UapStdioRelayBuilder.TryGetOrBuild(paths, "http://127.0.0.1:1/mcp", null);
            Assert.IsNull(first);
            Assert.IsNull(second);
            Assert.AreEqual(before + 1, UapStdioRelayBuilder.CompileAttemptCountForTests,
                "the second call should have reused the cached failure instead of recompiling");
        }

        [Test]
        public void ComputeSourceHash_SameContent_IsStableAndDeterministic()
        {
            string corePath = Path.Combine(_root, "RelayCore.cs");
            string mainPath = Path.Combine(_root, "RelayMain.cs");
            File.WriteAllText(corePath, "content-a");
            File.WriteAllText(mainPath, "content-b");
            string hash1 = UapStdioRelayBuilder.ComputeSourceHash(corePath, mainPath, "8.0.10");
            string hash2 = UapStdioRelayBuilder.ComputeSourceHash(corePath, mainPath, "8.0.10");
            Assert.AreEqual(hash1, hash2);
            string hash3 = UapStdioRelayBuilder.ComputeSourceHash(corePath, mainPath, "8.0.11");
            Assert.AreNotEqual(hash1, hash3, "a different shared-framework version must change the cache key");
        }

        [Test]
        public void TfmFor_ReadsMajorMinorFromFrameworkVersion()
        {
            Assert.AreEqual("net8.0", UapStdioRelayBuilder.TfmFor("8.0.10"));
            Assert.AreEqual("net6.0", UapStdioRelayBuilder.TfmFor("not-a-version"));
        }

        [Test]
        public void QuoteArg_WrapsAndEscapesEmbeddedQuotes()
        {
            Assert.AreEqual("\"\"", UapStdioRelayBuilder.QuoteArg(null));
            Assert.AreEqual("\"a b\"", UapStdioRelayBuilder.QuoteArg("a b"));
            Assert.AreEqual("\"a \\\"b\\\" c\"", UapStdioRelayBuilder.QuoteArg("a \"b\" c"));
        }

        // -- real toolchain (guarded) --------------------------------------------------

        [Test]
        public void CapturePaths_AndRealCompile_WhenThisEditorHasTheBundledToolchain()
        {
            // Runs the real thing end to end when THIS Editor's own
            // bundled dotnet/Roslyn are where UapStdioRelayBuilder expects
            // them; skipped otherwise (older/stripped installs, or a CI
            // host image that trims the bundled toolchain) -- design
            // section 2.7 item 6's "no real compile in batch mode unless
            // the paths exist" guard, same pattern
            // UapOptionalPackagePresenceTests uses for an optional package.
            RelayToolchainPaths paths = UapStdioRelayBuilder.CapturePaths();
            if (paths.FailureReason != null)
            {
                Assert.Ignore("Unity's bundled dotnet/Roslyn were not found in this Editor install: "
                    + paths.FailureReason);
                return;
            }
            AcpStdioServer relay = UapStdioRelayBuilder.TryGetOrBuild(paths, "http://127.0.0.1:65500/mcp", null);
            Assert.IsNotNull(relay, "toolchain paths resolved but the relay failed to compile");
            Assert.IsFalse(string.IsNullOrEmpty(relay.Command));
            Assert.IsTrue(relay.Args.Contains("http://127.0.0.1:65500/mcp"));
        }
    }
}
