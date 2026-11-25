using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure UapExecuteCodePolicy tests (docs/design-notes/2026-09-28-uloop-
    /// phase3.md): action parsing, usings validation, hashing/wrapping,
    /// line remapping, the static risk scan (including the comment/string
    /// stripper it depends on), and the generic half of the result-shape
    /// rules. No Unity API touched -- this fixture mirrors the same
    /// assertions ci/SmokeTests runs against the identical source file, plus
    /// a couple of Unity-adjacent shape checks (JsonNode) that smoke does
    /// not need duplicated.
    /// </summary>
    [TestFixture]
    public class UapExecuteCodePolicyTests
    {
        // -- action -----------------------------------------------------------

        [Test]
        public void ParseAction_Absent_DefaultsToRun()
        {
            Assert.AreEqual(UapExecuteCodeAction.Run, UapExecuteCodePolicy.ParseAction(null));
            Assert.AreEqual(UapExecuteCodeAction.Run, UapExecuteCodePolicy.ParseAction(string.Empty));
        }

        [TestCase("run", UapExecuteCodeAction.Run)]
        [TestCase("STATUS", UapExecuteCodeAction.Status)]
        [TestCase("Reset", UapExecuteCodeAction.Reset)]
        public void ParseAction_KnownNames_CaseInsensitive(string value, UapExecuteCodeAction expected)
        {
            Assert.AreEqual(expected, UapExecuteCodePolicy.ParseAction(value));
        }

        [Test]
        public void ParseAction_Unknown_ThrowsNamingIt()
        {
            var ex = Assert.Throws<ArgumentException>(delegate { UapExecuteCodePolicy.ParseAction("delete"); });
            StringAssert.Contains("delete", ex.Message);
        }

        // -- usings -------------------------------------------------------------

        [TestCase("System")]
        [TestCase("System.Collections.Generic")]
        [TestCase("_Foo.Bar_Baz")]
        public void IsValidUsing_PlainDottedIdentifiers_Accepted(string value)
        {
            Assert.IsTrue(UapExecuteCodePolicy.IsValidUsing(value));
        }

        [TestCase("System;")]
        [TestCase("System; }} public class Evil {{")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("1System")]
        [TestCase("System..Foo")]
        [TestCase("System.Foo ")]
        public void IsValidUsing_AnythingButAPlainIdentifier_Rejected(string value)
        {
            // The trailing-space case is intentionally rejected even though
            // BuildEffectiveUsings itself trims -- IsValidUsing alone must
            // not silently accept a raw untrimmed value some other caller
            // might pass directly.
            Assert.IsFalse(UapExecuteCodePolicy.IsValidUsing(value));
        }

        [Test]
        public void BuildEffectiveUsings_Null_ReturnsOnlyDefaultsSorted()
        {
            List<string> result = UapExecuteCodePolicy.BuildEffectiveUsings(null);
            var expected = new List<string>(UapExecuteCodePolicy.DefaultUsings);
            expected.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(expected, result);
        }

        [Test]
        public void BuildEffectiveUsings_CustomEntries_UnionedAndDeduped()
        {
            List<string> result = UapExecuteCodePolicy.BuildEffectiveUsings(
                new[] { "System.Text", "UnityEngine", "System.Text" });
            Assert.AreEqual(1, CountOccurrences(result, "System.Text"));
            CollectionAssert.Contains(result, "UnityEngine");
            CollectionAssert.Contains(result, "System");
        }

        [Test]
        public void BuildEffectiveUsings_OrderIndependent_SameHashInput()
        {
            List<string> a = UapExecuteCodePolicy.BuildEffectiveUsings(new[] { "System.Text", "System.IO" });
            List<string> b = UapExecuteCodePolicy.BuildEffectiveUsings(new[] { "System.IO", "System.Text" });
            CollectionAssert.AreEqual(a, b);
        }

        [Test]
        public void BuildEffectiveUsings_InvalidEntry_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                UapExecuteCodePolicy.BuildEffectiveUsings(new[] { "System; evil" });
            });
        }

        [Test]
        public void BuildEffectiveUsings_TooMany_Throws()
        {
            var many = new List<string>();
            for (int i = 0; i < UapExecuteCodePolicy.MaxUsingsCount + 1; i++)
            {
                many.Add("Ns" + i);
            }
            Assert.Throws<ArgumentException>(delegate { UapExecuteCodePolicy.BuildEffectiveUsings(many); });
        }

        private static int CountOccurrences(List<string> list, string value)
        {
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                {
                    count++;
                }
            }
            return count;
        }

        // -- hashing / wrapping -------------------------------------------------

        [Test]
        public void ComputeHash_IdenticalInput_IsDeterministic()
        {
            List<string> usings = UapExecuteCodePolicy.BuildEffectiveUsings(null);
            string a = UapExecuteCodePolicy.ComputeHash(usings, "return 1;");
            string b = UapExecuteCodePolicy.ComputeHash(usings, "return 1;");
            Assert.AreEqual(a, b);
            Assert.AreEqual(64, a.Length, "SHA-256 hex is 64 chars.");
        }

        [Test]
        public void ComputeHash_DifferentCode_DifferentHash()
        {
            List<string> usings = UapExecuteCodePolicy.BuildEffectiveUsings(null);
            string a = UapExecuteCodePolicy.ComputeHash(usings, "return 1;");
            string b = UapExecuteCodePolicy.ComputeHash(usings, "return 2;");
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void ComputeHash_DifferentUsingsOrder_SameHash()
        {
            string a = UapExecuteCodePolicy.ComputeHash(
                UapExecuteCodePolicy.BuildEffectiveUsings(new[] { "System.Text", "System.IO" }), "return 1;");
            string b = UapExecuteCodePolicy.ComputeHash(
                UapExecuteCodePolicy.BuildEffectiveUsings(new[] { "System.IO", "System.Text" }), "return 1;");
            Assert.AreEqual(a, b);
        }

        [Test]
        public void BuildWrappedSource_ContainsClassAndTrailingReturnNull()
        {
            List<string> usings = UapExecuteCodePolicy.BuildEffectiveUsings(null);
            string hash = UapExecuteCodePolicy.ComputeHash(usings, "int x = 1;");
            UapExecuteWrappedSource wrapped = UapExecuteCodePolicy.BuildWrappedSource(usings, "int x = 1;", hash);
            StringAssert.Contains("namespace UapSnippets", wrapped.Source);
            StringAssert.Contains("class S_" + hash, wrapped.Source);
            StringAssert.Contains("object Run()", wrapped.Source);
            StringAssert.Contains("int x = 1;", wrapped.Source);
            StringAssert.Contains("return null;", wrapped.Source);
            Assert.AreEqual(UapExecuteCodePolicy.PreambleLineCount, wrapped.PreambleLineCount);
        }

        [Test]
        public void BuildWrappedSource_PreambleLineCount_IndependentOfUsingsCount()
        {
            List<string> few = UapExecuteCodePolicy.BuildEffectiveUsings(null);
            List<string> many = UapExecuteCodePolicy.BuildEffectiveUsings(
                new[] { "System.Text", "System.IO", "System.Threading", "System.Globalization" });
            UapExecuteWrappedSource a = UapExecuteCodePolicy.BuildWrappedSource(few, "return 1;", "h1");
            UapExecuteWrappedSource b = UapExecuteCodePolicy.BuildWrappedSource(many, "return 1;", "h2");
            Assert.AreEqual(a.PreambleLineCount, b.PreambleLineCount);
        }

        [Test]
        public void BuildWrappedSource_UserCodeLandsExactlyAtPreambleLineCountPlusOne()
        {
            List<string> usings = UapExecuteCodePolicy.BuildEffectiveUsings(null);
            string code = "int marker = 12345;";
            UapExecuteWrappedSource wrapped = UapExecuteCodePolicy.BuildWrappedSource(usings, code, "h");
            string[] lines = wrapped.Source.Split('\n');
            int expectedLineIndex = wrapped.PreambleLineCount; // 0-based index of the (preamble+1)-th line
            StringAssert.Contains(code, lines[expectedLineIndex]);
        }

        [TestCase(5, 2, 3)]
        [TestCase(2, 2, 0)]
        [TestCase(1, 2, 0)]
        public void MapCompilerLineToUserLine_SubtractsPreamble_ClampsAtZero(int compilerLine, int preamble, int expected)
        {
            Assert.AreEqual(expected, UapExecuteCodePolicy.MapCompilerLineToUserLine(compilerLine, preamble));
        }

        [Test]
        public void MapCompilerLineToUserLine_OneArgOverload_UsesFixedPreamble()
        {
            int compilerLine = UapExecuteCodePolicy.PreambleLineCount + 4;
            Assert.AreEqual(4, UapExecuteCodePolicy.MapCompilerLineToUserLine(compilerLine));
        }

        // -- static risk scan -----------------------------------------------------

        [Test]
        public void StripCommentsAndStrings_LineComment_Blanked()
        {
            string stripped = UapExecuteCodePolicy.StripCommentsAndStrings("int x = 1; // Process.Start(\"x\")\nint y = 2;");
            StringAssert.DoesNotContain("Process", stripped);
            StringAssert.Contains("int y = 2;", stripped);
        }

        [Test]
        public void StripCommentsAndStrings_BlockComment_Blanked()
        {
            string stripped = UapExecuteCodePolicy.StripCommentsAndStrings("/* Process.Start(x) */ int z = 1;");
            StringAssert.DoesNotContain("Process", stripped);
            StringAssert.Contains("int z = 1;", stripped);
        }

        [Test]
        public void StripCommentsAndStrings_RegularString_Blanked()
        {
            string stripped = UapExecuteCodePolicy.StripCommentsAndStrings("string s = \"call Process.Start here\";");
            StringAssert.DoesNotContain("Process", stripped);
        }

        [Test]
        public void StripCommentsAndStrings_VerbatimString_Blanked()
        {
            string stripped = UapExecuteCodePolicy.StripCommentsAndStrings("string s = @\"Process.Start \"\"quoted\"\" here\";");
            StringAssert.DoesNotContain("Process", stripped);
        }

        [Test]
        public void StripCommentsAndStrings_InterpolatedStringHole_StillScanned()
        {
            // The literal text is blanked but a {expr} hole is left intact --
            // an attempt to hide a risky call inside one must still be caught.
            string stripped = UapExecuteCodePolicy.StripCommentsAndStrings("var s = $\"result={Process.Start(x)}\";");
            StringAssert.Contains("Process.Start", stripped);
            StringAssert.DoesNotContain("result=", stripped);
        }

        [Test]
        public void StripCommentsAndStrings_CharLiteralEscapedQuote_DoesNotDesync()
        {
            string stripped = UapExecuteCodePolicy.StripCommentsAndStrings("char c = '\\''; int after = 1;");
            StringAssert.Contains("int after = 1;", stripped);
        }

        [Test]
        public void ScanForRiskFlags_PlainSnippet_NoFlags()
        {
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags("return 1 + 2;");
            Assert.AreEqual(0, flags.Count);
        }

        [Test]
        public void ScanForRiskFlags_MentionInCommentOrString_NotFlagged()
        {
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags(
                "// Process.Start would be bad\nstring s = \"Process.Start\"; return s;");
            Assert.AreEqual(0, flags.Count);
        }

        [TestCase("System.Diagnostics.Process.Start(\"x\");", "process")]
        [TestCase("var c = new System.Net.Http.HttpClient();", "network")]
        [TestCase("System.IO.File.WriteAllText(\"x\", \"y\");", "io-write")]
        [TestCase("System.Environment.Exit(0);", "env")]
        [TestCase("UnityEditor.EditorApplication.Exit(0);", "editor-exit")]
        [TestCase("UnityEditor.AssetDatabase.DeleteAsset(\"x\");", "asset-delete")]
        [TestCase("UnityEditor.EditorPrefs.DeleteAll();", "prefs-clear")]
        [TestCase("System.Reflection.Assembly.LoadFrom(\"x\");", "dynamic-assembly")]
        [TestCase("unsafe { int* p = null; }", "unsafe")]
        [TestCase("System.Threading.Thread.Sleep(1000);", "sleep")]
        [TestCase("var t = System.Type.GetType(\"System.Diagnostics.\" + \"Process\");", "reflection")]
        [TestCase("System.Activator.CreateInstance(t);", "reflection")]
        [TestCase("t.InvokeMember(\"Start\", System.Reflection.BindingFlags.InvokeMethod, null, null, null);", "reflection")]
        [TestCase("var c = new System.Net.Sockets.TcpClient(\"host\", 80);", "network")]
        [TestCase("var c = new System.Net.Sockets.UdpClient();", "network")]
        [TestCase("System.IO.File.Open(\"x\", System.IO.FileMode.Create);", "io-write")]
        public void ScanForRiskFlags_KnownApis_Flagged(string code, string expectedFlagId)
        {
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags(code);
            bool found = false;
            for (int i = 0; i < flags.Count; i++)
            {
                if (flags[i].Id == expectedFlagId)
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, "expected flag '" + expectedFlagId + "' in: "
                + string.Join(",", flags.ConvertAll(f => f.Id).ToArray()));
        }

        [Test]
        public void ScanForRiskFlags_ReflectionEvasionOfProcessStart_CaughtByReflectionRule()
        {
            // Splitting the type name across a string concatenation defeats
            // the "process" rule's \bProcess\b word match entirely (the
            // literal word "Process" never appears in the code) -- this is
            // exactly the obfuscation an adversarial review of this scan
            // must assume an agent (or an attacker steering one) will try.
            // The "reflection" rule exists specifically to still flag it.
            string code = "var t = System.Type.GetType(\"System\" + \".Diagnostics.Process\");"
                + "var m = t.GetMethod(\"Start\", new[] { typeof(string) });"
                + "m.Invoke(null, new object[] { \"cmd.exe\" });";
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags(code);
            Assert.IsTrue(flags.Exists(f => f.Id == "reflection"));
            Assert.IsFalse(flags.Exists(f => f.Id == "process"),
                "this snippet must NOT be caught by the 'process' word-match rule -- proving 'reflection' is load-bearing, not redundant.");
        }

        [Test]
        public void ScanForRiskFlags_WhileTrueWithNoEscape_FlagsInfiniteLoop()
        {
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags("while (true) { int x = 1; }");
            Assert.IsTrue(flags.Exists(f => f.Id == "infinite-loop"));
        }

        [Test]
        public void ScanForRiskFlags_WhileTrueWithBreakElsewhere_NotFlagged()
        {
            // Coarse, code-wide heuristic (documented): a break/return/throw
            // ANYWHERE suppresses the flag, even in an unrelated loop.
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags(
                "while (true) { int x = 1; } return 0;");
            Assert.IsFalse(flags.Exists(f => f.Id == "infinite-loop"));
        }

        // -- result shape -----------------------------------------------------

        [Test]
        public void ShapeReturnValue_Null_ReturnsJsonNull()
        {
            JsonNode node = UapExecuteCodePolicy.ShapeReturnValue(null, null);
            Assert.IsTrue(node.IsNull);
        }

        [Test]
        public void ShapeReturnValue_Primitives_AsThemselves()
        {
            Assert.AreEqual(true, UapExecuteCodePolicy.ShapeReturnValue(true, null).AsBool());
            Assert.AreEqual("hi", UapExecuteCodePolicy.ShapeReturnValue("hi", null).AsString());
            Assert.AreEqual(42, UapExecuteCodePolicy.ShapeReturnValue(42, null).AsInt());
            Assert.AreEqual(3.5, UapExecuteCodePolicy.ShapeReturnValue(3.5, null).AsDouble(), 0.0001);
        }

        private enum SampleEnum { Alpha, Beta }

        [Test]
        public void ShapeReturnValue_Enum_AsName()
        {
            JsonNode node = UapExecuteCodePolicy.ShapeReturnValue(SampleEnum.Beta, null);
            Assert.AreEqual("Beta", node.AsString());
        }

        [Test]
        public void ShapeReturnValue_List_BecomesItemsObjectWithCount()
        {
            var list = new List<int> { 1, 2, 3 };
            JsonNode node = UapExecuteCodePolicy.ShapeReturnValue(list, null);
            Assert.IsTrue(node.IsObject);
            Assert.AreEqual(3, node["count"].AsInt());
            Assert.IsFalse(node["truncated"].AsBool());
            Assert.AreEqual(3, node["items"].Count);
        }

        private sealed class InfiniteSequence : IEnumerable<int>
        {
            public IEnumerator<int> GetEnumerator()
            {
                int i = 0;
                while (true)
                {
                    yield return i++;
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        [Test]
        public void ShapeReturnValue_InfiniteEnumerable_StopsAtCapWithoutHanging()
        {
            JsonNode node = UapExecuteCodePolicy.ShapeReturnValue(new InfiniteSequence(), null);
            Assert.AreEqual(UapExecuteCodePolicy.MaxReturnArrayItems, node["items"].Count);
            Assert.IsTrue(node["truncated"].AsBool());
        }

        [Test]
        public void ShapeReturnValue_UnityObjectDescribed_UsesDelegate()
        {
            UapExecuteCodePolicy.TryDescribeUnityObject describe = delegate(object v, out string name, out string type, out string path)
            {
                name = "Foo";
                type = "GameObject";
                path = "/Root/Foo";
                return true;
            };
            JsonNode node = UapExecuteCodePolicy.ShapeReturnValue(new object(), describe);
            Assert.AreEqual("Foo", node["name"].AsString());
            Assert.AreEqual("GameObject", node["type"].AsString());
            Assert.AreEqual("/Root/Foo", node["path"].AsString());
        }

        [Test]
        public void ShapeReturnValue_FallbackType_UsesToString()
        {
            JsonNode node = UapExecuteCodePolicy.ShapeReturnValue(new System.Text.StringBuilder("abc"), null);
            Assert.AreEqual("abc", node.AsString());
        }

        // -- memory warning -----------------------------------------------------

        [Test]
        public void DescribeMemoryWarning_UnderThreshold_Empty()
        {
            Assert.AreEqual(string.Empty,
                UapExecuteCodePolicy.DescribeMemoryWarning(UapExecuteCodePolicy.LoadedSnippetWarnThreshold));
        }

        [Test]
        public void DescribeMemoryWarning_OverThreshold_NamesReset()
        {
            string warning = UapExecuteCodePolicy.DescribeMemoryWarning(UapExecuteCodePolicy.LoadedSnippetWarnThreshold + 1);
            StringAssert.Contains("action:reset", warning);
        }
    }
}
