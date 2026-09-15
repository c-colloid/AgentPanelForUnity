using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Source-scan guard for the UnityEngine.Object APIs that newer editors
    /// made obsolete-as-error (design note
    /// docs/design-notes/2026-09-11-unity-6-support.md). On 6000.5,
    /// `GetInstanceID()` is an ERROR, not a warning, and the EntityId to int
    /// cast is gone with it; `FindObjectsSortMode` went obsolete a version
    /// earlier. So a single call added anywhere in either package breaks
    /// the build on that editor only, which the matrix catches a full CI
    /// round later than the author.
    ///
    /// It has already happened twice, so it is pinned here rather than left
    /// to review. `UnityObjectCompat.cs` is the one place these APIs are
    /// spelled, behind version conditionals; everything else calls
    /// `UnityObjectId.Of` / `UnityObjectCompat.FindAll`.
    ///
    /// Scan mechanics follow LayerHygieneTests: comments are stripped first
    /// (a doc comment NAMING the API is legal and, in this repo, expected --
    /// every replacement carries one), offenders are reported as file:line,
    /// and the forbidden tokens are assembled by concatenation so this file
    /// never trips its own scan. Pro is scanned when it is installed; the
    /// Core-only CI job simply has nothing to scan there, which is correct.
    /// </summary>
    [TestFixture]
    public class Unity6ApiHygieneTests
    {
        private const string CoreEditorDir = "Packages/jp.colloid.unity-agent-panel/Editor";
        private const string ProEditorDir = "Packages/jp.colloid.agent-panel-pro/Editor";

        /// <summary>The one file allowed to spell these APIs -- it is the version conditional.</summary>
        private const string CompatFileName = "UnityObjectCompat.cs";

        /// <summary>
        /// Assembled, never literal -- see the class doc.
        ///
        /// Only the APIs that still COMPILE today and fail on a newer
        /// editor are listed. FindObjectsOfType is not: it is gone from
        /// every supported version, so the compiler already catches it, and
        /// scanning for it would flag the unrelated and perfectly current
        /// Resources.FindObjectsOfTypeAll, which this package uses in eight
        /// places on purpose.
        /// </summary>
        private static readonly string[] ForbiddenTokens =
        {
            "GetInstance" + "ID(",
            "FindObjects" + "SortMode",
        };

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/",
                match => Regex.Replace(match.Value, "[^\n]", " "), RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\n]*", string.Empty);
        }

        private static void ScanInto(string dir, List<string> offenders)
        {
            string root = Path.GetFullPath(dir);
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == CompatFileName)
                {
                    continue;
                }
                string[] lines = StripComments(File.ReadAllText(file)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    for (int t = 0; t < ForbiddenTokens.Length; t++)
                    {
                        if (lines[i].IndexOf(ForbiddenTokens[t], System.StringComparison.Ordinal) >= 0)
                        {
                            offenders.Add(file + ":" + (i + 1) + " -> " + ForbiddenTokens[t]);
                        }
                    }
                }
            }
        }

        [Test]
        public void NoEditorSourceCallsTheObsoleteObjectApis_OutsideTheCompatShim()
        {
            Assert.IsTrue(Directory.Exists(Path.GetFullPath(CoreEditorDir)),
                "Core's Editor directory was not found at " + CoreEditorDir
                + " -- this scan would silently pass on nothing.");

            var offenders = new List<string>();
            ScanInto(CoreEditorDir, offenders);
            ScanInto(ProEditorDir, offenders);

            CollectionAssert.IsEmpty(offenders,
                "These call a UnityEngine.Object API that is obsolete-as-error on Unity 6000.5, so the"
                + " package will not compile there. Use UnityObjectId.Of / UnityObjectCompat.FindAll"
                + " instead:\n" + string.Join("\n", offenders.ToArray()));
        }

        [Test]
        public void TheCompatShimItself_StillSpellsThem()
        {
            // A negative self-test: if the tokens ever stop matching (an API
            // rename, a typo in the assembled strings), the scan above would
            // pass on everything and this catches that instead.
            string compat = Path.GetFullPath(Path.Combine(CoreEditorDir, "Ops/" + CompatFileName));
            Assert.IsTrue(File.Exists(compat), "compat shim not found at " + compat);

            string source = StripComments(File.ReadAllText(compat));
            Assert.IsTrue(source.IndexOf(ForbiddenTokens[0], System.StringComparison.Ordinal) >= 0,
                "the detector's token no longer matches the shim's own call -- the scan above is dead");
        }
    }
}
