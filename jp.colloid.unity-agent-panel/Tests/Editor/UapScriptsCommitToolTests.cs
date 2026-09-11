using System.Collections;
using System.Diagnostics;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real uap_scripts_commit tests (design section 7.4/8.1 P1, staging
    /// location moved OUTSIDE Assets/ by section 8.7): the "nothing staged"
    /// case completes synchronously (no compiler, no staged files ever
    /// exist) and runs as a plain [Test]; every case that puts a REAL file
    /// under `&lt;projectRoot&gt;/UapStaging/` wraps that window in
    /// AssetDatabase.DisallowAutoRefresh/AllowAutoRefresh.
    ///
    /// This is not optional bookkeeping: an EARLIER version of this
    /// fixture, back when staging still lived under Assets/UapStaging~/,
    /// found that WITHOUT that wrapping Unity's own background
    /// auto-refresh would notice the transient staged .cs file and feed it
    /// into the normal Assembly-CSharp compile (a real "Tundra build
    /// failed" in this project, observed empirically) -- i.e. the tilde
    /// suffix did NOT by itself protect against Unity's compiler in this
    /// Unity version/config the way R11 assumed; the real protection is
    /// UapTurnScope's turn-scoped DisallowAutoRefresh (held for the whole
    /// turn in production, from the first tool call to TurnCompleted), not
    /// the folder name/location alone. The wrapping stays here even now
    /// that staging is outside Assets/ entirely (belt-and-suspenders, and
    /// this fixture also predates the move). See docs/design-notes for
    /// this stream's deviations note.
    ///
    /// The two cases that actually invoke AssemblyBuilder run as
    /// [UnityTest] coroutines that yield to the editor's own update loop
    /// between polls -- exactly like production
    /// (UapMainThreadDispatcher.Pump from EditorApplication.update) --
    /// rather than busy-blocking the single editor main thread the way a
    /// synchronous [Test] calling Execute() would (Execute() is a fallback
    /// for DirectUapToolExecutor only; see its own doc comment).
    /// </summary>
    [TestFixture]
    public class UapScriptsCommitToolTests
    {
        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static string StagingRoot()
        {
            return Path.Combine(ProjectRoot(), "UapStaging");
        }

        [TearDown]
        public void TearDown()
        {
            string staging = StagingRoot();
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Committed.cs"));
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Committed.cs.meta"));
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Bad.cs"));
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Toctou.cs"));
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Toctou.cs.meta"));
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_DerivedFromCompiled.cs"));
            DeleteIfExists(Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_DerivedFromCompiled.cs.meta"));
            string tempDir = Path.Combine(ProjectRoot(), "Temp", "UapScriptsCommit");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
            AssetDatabase.Refresh();
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Execute_NothingStaged_ReturnsMessageWithoutCompiling()
        {
            var tool = new UapScriptsCommitTool();
            JsonNode result = tool.Execute(JsonNode.NewObject());
            StringAssert.Contains("Nothing staged", result[0]["text"].AsString());
        }

        [Test]
        public void Execute_MalformedAsmdef_ThrowsAndLeavesStageIntact()
        {
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                string staging = StagingRoot();
                Directory.CreateDirectory(staging);
                string asmdefPath = Path.Combine(staging, "Broken.asmdef");
                File.WriteAllText(asmdefPath, "{ not valid json ");

                var tool = new UapScriptsCommitTool();
                Assert.Throws<System.InvalidOperationException>(delegate { tool.Execute(JsonNode.NewObject()); });
                Assert.IsTrue(File.Exists(asmdefPath), "a malformed asmdef must not be moved.");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }

        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_ValidStagedScript_CompilesAndMovesIntoAssets()
        {
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                string staging = StagingRoot();
                Directory.CreateDirectory(staging);
                string stagedFile = Path.Combine(staging, "UapScriptsCommitTests_Committed.cs");
                File.WriteAllText(stagedFile,
                    "using UnityEngine;\npublic class UapScriptsCommitTests_Committed : MonoBehaviour { }\n");

                var tool = new UapScriptsCommitTool();
                object state = null;
                JsonNode input = JsonNode.NewObject();
                JsonNode result = null;
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < 60)
                {
                    if (tool.Poll(input, ref state, out result))
                    {
                        break;
                    }
                    yield return null;
                }

                Assert.IsNotNull(result, "commit did not finish within the time budget.");
                string text = result[0]["text"].AsString();
                StringAssert.Contains("Committed", text);
                Assert.IsFalse(File.Exists(stagedFile), "the staged file must be gone after a successful commit.");
                string destPath = Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Committed.cs");
                Assert.IsTrue(File.Exists(destPath), "the file must have moved into Assets/.");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }

        /// <summary>
        /// SEC-5 (TOCTOU): AssemblyBuilder spans editor ticks, and an agent
        /// can rewrite a staged file between validation and commit. The
        /// commit must move the MIRRORED (validated) bytes, never whatever
        /// the staged original contains by then. The asmdef variant of the
        /// same window is deliberately not staged here -- committing a real
        /// .asmdef into this host project's Assets/ would hijack its
        /// assembly layout if any refresh imported it; the .cs variant pins
        /// the shared mirror-as-move-source mechanism, and the existing
        /// malformed-asmdef test covers the asmdef check reading the
        /// mirror.
        /// </summary>
        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_StagedFileMutatedDuringCompile_CommitsValidatedBytesNotTheMutation()
        {
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                string staging = StagingRoot();
                Directory.CreateDirectory(staging);
                string stagedFile = Path.Combine(staging, "UapScriptsCommitTests_Toctou.cs");
                string validated =
                    "using UnityEngine;\npublic class UapScriptsCommitTests_Toctou : MonoBehaviour { }\n";
                File.WriteAllText(stagedFile, validated);

                var tool = new UapScriptsCommitTool();
                object state = null;
                JsonNode input = JsonNode.NewObject();
                JsonNode result = null;
                bool mutated = false;
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < 60)
                {
                    if (tool.Poll(input, ref state, out result))
                    {
                        break;
                    }
                    if (!mutated)
                    {
                        // The mirror copy was taken synchronously in the
                        // first Poll's BeginCommit; this rewrite lands in
                        // the validation window AssemblyBuilder leaves open.
                        File.WriteAllText(stagedFile, "this is not C# at all {{{");
                        mutated = true;
                    }
                    yield return null;
                }

                Assert.IsNotNull(result, "commit did not finish within the time budget.");
                StringAssert.Contains("Committed", result[0]["text"].AsString());
                Assert.IsTrue(mutated,
                    "the compile finished before the mutation window opened; the test proved nothing");
                string destPath = Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_Toctou.cs");
                Assert.IsTrue(File.Exists(destPath));
                Assert.AreEqual(validated, File.ReadAllText(destPath),
                    "Assets/ must receive the exact bytes that were validated, not the mid-compile rewrite");
                Assert.IsFalse(File.Exists(stagedFile), "a successful commit still consumes the stage");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }

        /// <summary>
        /// Pins the 2026-08-13 CS0012 regression (design note
        /// 2026-08-13-scripts-commit-cs0012): a staged script deriving from
        /// a type in an ALREADY-COMPILED project assembly (UnityEngine.UI
        /// here -- compiled against the engine MODULE dlls) failed with
        /// "CS0012 ... add a reference to UnityEngine.CoreModule" under
        /// AssemblyBuilder's default monolithic-facade reference set, while
        /// the self-contained MonoBehaviour case above stayed green -- which
        /// is exactly why this fixture never caught it. UseEngineModules on
        /// the builder is the fix; this test fails without it.
        /// </summary>
        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_StagedScriptDerivingFromCompiledAssemblyType_CompilesAndMovesIntoAssets()
        {
            if (System.Type.GetType("UnityEngine.UI.Selectable, UnityEngine.UI") == null)
            {
                Assert.Ignore("com.unity.ugui is not part of this project; the compiled-assembly-derivation case cannot be staged here.");
            }
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                string staging = StagingRoot();
                Directory.CreateDirectory(staging);
                string stagedFile = Path.Combine(staging, "UapScriptsCommitTests_DerivedFromCompiled.cs");
                File.WriteAllText(stagedFile,
                    "public class UapScriptsCommitTests_DerivedFromCompiled : UnityEngine.UI.Selectable { }\n");

                var tool = new UapScriptsCommitTool();
                object state = null;
                JsonNode input = JsonNode.NewObject();
                JsonNode result = null;
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < 60)
                {
                    if (tool.Poll(input, ref state, out result))
                    {
                        break;
                    }
                    yield return null;
                }

                Assert.IsNotNull(result, "commit did not finish within the time budget.");
                string text = result[0]["text"].AsString();
                StringAssert.DoesNotContain("CS0012", text);
                StringAssert.Contains("Committed", text);
                string destPath = Path.Combine(ProjectRoot(), "Assets", "UapScriptsCommitTests_DerivedFromCompiled.cs");
                Assert.IsTrue(File.Exists(destPath), "the file must have moved into Assets/.");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }

        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_BrokenStagedScript_FailsAndLeavesStageIntact()
        {
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                string staging = StagingRoot();
                Directory.CreateDirectory(staging);
                string stagedFile = Path.Combine(staging, "UapScriptsCommitTests_Bad.cs");
                File.WriteAllText(stagedFile, "this is not valid C# at all {{{");

                var tool = new UapScriptsCommitTool();
                object state = null;
                JsonNode input = JsonNode.NewObject();
                JsonNode result = null;
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < 60)
                {
                    if (tool.Poll(input, ref state, out result))
                    {
                        break;
                    }
                    yield return null;
                }

                Assert.IsNotNull(result, "commit did not finish within the time budget.");
                string text = result[0]["text"].AsString();
                StringAssert.Contains("Compilation failed", text);
                Assert.IsTrue(File.Exists(stagedFile), "a failed compile must leave the stage untouched.");
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }
    }
}
