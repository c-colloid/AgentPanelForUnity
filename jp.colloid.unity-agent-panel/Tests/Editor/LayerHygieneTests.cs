using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// INFRA-4: source-scan enforcement of the layering rules ARCHITECTURE
    /// section 2 states but the asmdef layout (one Editor + one Tests
    /// assembly) cannot compiler-enforce:
    ///
    ///   - Core/Json, Core/Protocol, Core/Client and Core/FileIo are
    ///     engine-free -- no UnityEngine/UnityEditor tokens. (Core/Process
    ///     is deliberately NOT in scope: AuthCli/CliVersionProbe/
    ///     AuthLoginSession touch UnityEditor by design -- EditorApplication
    ///     pumps, AssemblyReloadEvents -- and an asmdef split would require
    ///     moving those files, evaluated and rejected in the INFRA-4 note.)
    ///   - Model never references the UI layer (namespace or L10n/UiStrings)
    ///     -- the rule MODEL-5's HistoryRowMenuModel violated for a month
    ///     before a review caught it.
    ///
    /// Scan mechanics follow UssHygieneTests: comments are stripped first
    /// (every historical near-miss was a doc comment MENTIONING the other
    /// layer, which is legal and encouraged), offenders are listed as
    /// file:line, and the forbidden tokens are assembled by concatenation
    /// so this file never trips its own scan. A negative self-test proves
    /// the detector actually detects.
    /// </summary>
    [TestFixture]
    public class LayerHygieneTests
    {
        private const string PackageEditorDir = "Packages/jp.colloid.unity-agent-panel/Editor";

        // Assembled, never literal -- see class doc.
        private static readonly string EngineToken = "Unity" + "Engine";
        private static readonly string EditorToken = "Unity" + "Editor";
        private static readonly string UiNamespaceToken = "Colloid.AgentPanel" + ".UI";
        private static readonly string L10nToken = "L10" + "n.";
        private static readonly string UiStringsToken = "Ui" + "Strings";

        /// <summary>
        /// Line + block comments removed, line structure preserved so the
        /// reported line numbers stay true. Not a full lexer: a string
        /// literal containing "//" loses its tail, which can only HIDE an
        /// offender on that same line -- acceptable for a hygiene sweep
        /// whose real targets (using directives, type references) sit at
        /// the start of their line.
        /// </summary>
        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/",
                match => Regex.Replace(match.Value, "[^\n]", " "), RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\n]*", string.Empty);
        }

        private static List<string> FindOffenders(string subDir, string[] tokens)
        {
            var offenders = new List<string>();
            string root = Path.GetFullPath(Path.Combine(PackageEditorDir, subDir));
            Assert.IsTrue(Directory.Exists(root), "layer dir not found: " + root);
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = StripComments(File.ReadAllText(file)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    for (int t = 0; t < tokens.Length; t++)
                    {
                        if (lines[i].IndexOf(tokens[t], System.StringComparison.Ordinal) >= 0)
                        {
                            offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + " (" + tokens[t] + ")");
                            break;
                        }
                    }
                }
            }
            return offenders;
        }

        [TestCase("Core/Json")]
        [TestCase("Core/Protocol")]
        [TestCase("Core/Client")]
        [TestCase("Core/FileIo")]
        public void SourceScan_CoreLayer_HasNoEngineReference(string subDir)
        {
            List<string> offenders = FindOffenders(subDir, new[] { EngineToken, EditorToken });
            Assert.IsEmpty(offenders,
                subDir + " must stay engine-free (ARCHITECTURE section 2; the smoke tier and any"
                + " future asmdef split depend on it): " + string.Join(", ", offenders.ToArray()));
        }

        [Test]
        public void SourceScan_ModelLayer_DoesNotReferenceTheUiLayer()
        {
            List<string> offenders = FindOffenders("Model",
                new[] { UiNamespaceToken, L10nToken, UiStringsToken });
            Assert.IsEmpty(offenders,
                "Model must not reference UI (D9 one-way layering; MODEL-5's HistoryRowMenuModel"
                + " is the precedent this guards against): " + string.Join(", ", offenders.ToArray()));
        }

        /// <summary>The detector must actually detect -- a scan that can never fail proves nothing.</summary>
        [Test]
        public void Detector_NegativeSelfTest_FindsAPlantedOffender()
        {
            string planted = "using " + UiNamespaceToken + ";\nclass X { }";
            string stripped = StripComments(planted);
            Assert.IsTrue(stripped.IndexOf(UiNamespaceToken, System.StringComparison.Ordinal) >= 0,
                "a genuine using directive must survive comment stripping and be found");

            string commented = "// " + UiNamespaceToken + " mentioned in prose\nclass X { }";
            Assert.IsTrue(StripComments(commented)
                    .IndexOf(UiNamespaceToken, System.StringComparison.Ordinal) < 0,
                "a comment MENTIONING the layer is legal and must be stripped before the scan");
        }
    }
}
