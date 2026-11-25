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
    /// It then happened a third time, in the two places this scan did not
    /// look (design note docs/design-notes/2026-11-25-unity6-ci-guards.md):
    /// a test called `GetInstanceID()` (only the Editor directories were
    /// scanned), and Pro's Hierarchy badges used the instance-id row
    /// callback and `InstanceIDToObject(int)` (neither was on the list).
    /// So the scan now covers every directory of both packages and the
    /// editor-callback pair, and reads `#if UNITY_6000_N_OR_NEWER`: a line
    /// that only an editor older than 6000.5 compiles is legal anywhere,
    /// which is what lets a file outside the shim keep the old call in its
    /// `#else` when the new API cannot be routed through Core.
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
        private const string CoreDir = "Packages/jp.colloid.unity-agent-panel";
        private const string ProDir = "Packages/jp.colloid.agent-panel-pro";
        private const string CoreEditorDir = CoreDir + "/Editor";

        /// <summary>The newest Unity 6 minor the matrix covers; a branch for an older editor is not scanned.</summary>
        private const int NewestMinor = 5;

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
            "InstanceID" + "ToObject(",
            "hierarchyWindowItem" + "OnGUI",
        };

        private static readonly Regex Conditional =
            new Regex(@"^\s*#\s*(if|elif|else|endif)\b\s*(.*)$", RegexOptions.Compiled);

        private static readonly Regex Unity6Symbol =
            new Regex(@"^(!?)\s*UNITY_6000_(\d+)_OR_NEWER$", RegexOptions.Compiled);

        /// <summary>
        /// Pure: for each line of <paramref name="lines"/>, whether Unity
        /// 6000.<see cref="NewestMinor"/> compiles it. Only the simple
        /// conditions `UNITY_6000_N_OR_NEWER` / `!UNITY_6000_N_OR_NEWER`
        /// are decided; under any other condition both branches count as
        /// compiled, so the scan errs towards reporting.
        /// </summary>
        internal static bool[] CompiledOnNewestEditor(string[] lines)
        {
            var compiled = new bool[lines.Length];
            // One frame per open #if: null = undecided (both branches count),
            // otherwise whether the branch currently being read is compiled.
            var frames = new List<bool?>();
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = Conditional.Match(lines[i]);
                if (m.Success)
                {
                    string kind = m.Groups[1].Value;
                    if (kind == "if")
                    {
                        frames.Add(Decide(m.Groups[2].Value));
                    }
                    else if (kind == "endif")
                    {
                        if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    }
                    else if (frames.Count > 0)
                    {
                        bool? current = frames[frames.Count - 1];
                        // #else of a decided #if is the other branch; an
                        // #elif chain is left undecided.
                        frames[frames.Count - 1] = kind == "else" && current.HasValue ? !current.Value : (bool?)null;
                    }
                    continue;
                }
                bool on = true;
                for (int f = 0; f < frames.Count; f++)
                {
                    if (frames[f].HasValue && !frames[f].Value) on = false;
                }
                compiled[i] = on;
            }
            return compiled;
        }

        private static bool? Decide(string condition)
        {
            Match m = Unity6Symbol.Match(condition.Trim());
            if (!m.Success)
            {
                return null;
            }
            bool defined = int.Parse(m.Groups[2].Value) <= NewestMinor;
            return m.Groups[1].Value == "!" ? !defined : defined;
        }

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
                bool[] compiled = CompiledOnNewestEditor(lines);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!compiled[i])
                    {
                        continue;
                    }
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

            // Whole packages, not just Editor/: the tests and Pro's Runtime
            // assembly compile on the same editors.
            var offenders = new List<string>();
            ScanInto(CoreDir, offenders);
            ScanInto(ProDir, offenders);

            CollectionAssert.IsEmpty(offenders,
                "These call an API that is obsolete-as-error on Unity 6000.5, so the package (or its"
                + " tests) will not compile there. Use UnityObjectId.Of / UnityObjectId.TextOf /"
                + " UnityObjectCompat.FindAll, or put the old call in the #else of"
                + " #if UNITY_6000_5_OR_NEWER:\n" + string.Join("\n", offenders.ToArray()));
        }

        [Test]
        public void CompiledOnNewestEditor_FollowsTheUnity6Conditionals()
        {
            string[] lines =
            {
                "a",                                // 0 compiled
                "#if UNITY_6000_5_OR_NEWER",
                "new",                              // 2 compiled
                "#else",
                "old",                              // 4 not compiled
                "#endif",
                "#if !UNITY_6000_3_OR_NEWER",
                "older",                            // 7 not compiled
                "#else",
                "newer",                            // 9 compiled
                "#endif",
                "#if UNITY_6000_9_OR_NEWER",
                "future",                           // 12 not compiled (newer than the matrix)
                "#endif",
                "#if UNITY_EDITOR_WIN",
                "undecided",                        // 15 compiled (not a Unity 6 condition)
                "#else",
                "also undecided",                   // 17 compiled
                "#endif",
                "  #  if UNITY_6000_4_OR_NEWER",
                "#if SOMETHING",
                "nested",                           // 21 compiled
                "#endif",
                "#else",
                "nested old",                       // 24 not compiled
                "#endif",
                "z",                                // 26 compiled
            };

            bool[] compiled = CompiledOnNewestEditor(lines);

            Assert.IsTrue(compiled[0]);
            Assert.IsTrue(compiled[2]);
            Assert.IsFalse(compiled[4]);
            Assert.IsFalse(compiled[7]);
            Assert.IsTrue(compiled[9]);
            Assert.IsFalse(compiled[12]);
            Assert.IsTrue(compiled[15]);
            Assert.IsTrue(compiled[17]);
            Assert.IsTrue(compiled[21]);
            Assert.IsFalse(compiled[24]);
            Assert.IsTrue(compiled[26]);
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
