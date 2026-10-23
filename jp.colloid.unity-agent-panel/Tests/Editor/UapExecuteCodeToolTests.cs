using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real uap_execute_code tests (docs/design-notes/2026-09-28-uloop-
    /// phase3.md). Argument validation, action:status/reset, and the
    /// "flagged snippet without confirm previews and runs nothing" path
    /// all complete SYNCHRONOUSLY (no AssemblyBuilder involved) and run as
    /// plain [Test]s. The two cases that actually compile+run a snippet
    /// are [UnityTest] coroutines that yield to the editor's own update
    /// loop between polls, exactly like UapScriptsCommitToolTests drives
    /// the same AssemblyBuilder path -- this project's CI has AssemblyBuilder
    /// working in batch mode (that fixture does not Assert.Ignore it), so
    /// neither does this one.
    /// </summary>
    [TestFixture]
    public class UapExecuteCodeToolTests
    {
        private Action<Action> _originalScheduler;

        [SetUp]
        public void SetUp()
        {
            UapExecuteCodeTool.ResetForTests();
            _originalScheduler = UapExecuteCodeTool.Scheduler;
        }

        [TearDown]
        public void TearDown()
        {
            UapExecuteCodeTool.Scheduler = _originalScheduler;
            UapExecuteCodeTool.ResetForTests();
            string dir = Path.Combine(ProjectRoot(), "Temp", "uap-exec");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        /// <summary>Escapes a path for use inside a REGULAR (non-verbatim) C# string literal in a generated snippet.</summary>
        private static string EscapeForCSharpString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static JsonNode RunInput(string code, IEnumerable<string> usings = null, bool confirm = false)
        {
            JsonNode input = JsonNode.NewObject().Set("action", "run").Set("code", code);
            if (confirm)
            {
                input.Set("confirm", true);
            }
            if (usings != null)
            {
                JsonNode arr = JsonNode.NewArray();
                foreach (string u in usings)
                {
                    arr.Add(u);
                }
                input.Set("usings", arr);
            }
            return input;
        }

        // -- argument rules -----------------------------------------------------

        [Test]
        public void Execute_ActionRun_NoCode_Throws()
        {
            var tool = new UapExecuteCodeTool();
            Assert.Throws<ArgumentException>(delegate { tool.Execute(JsonNode.NewObject().Set("action", "run")); });
        }

        [Test]
        public void Execute_ActionRun_CodeTooLong_Throws()
        {
            var tool = new UapExecuteCodeTool();
            string huge = new string('x', UapExecuteCodePolicy.MaxCodeLength + 1);
            Assert.Throws<ArgumentException>(delegate { tool.Execute(RunInput(huge)); });
        }

        [Test]
        public void Execute_ActionRun_InvalidUsings_Throws()
        {
            var tool = new UapExecuteCodeTool();
            Assert.Throws<ArgumentException>(delegate
            {
                tool.Execute(RunInput("return 1;", new[] { "System; evil" }));
            });
        }

        [Test]
        public void Execute_UnknownAction_Throws()
        {
            var tool = new UapExecuteCodeTool();
            Assert.Throws<ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("action", "delete_everything"));
            });
        }

        // -- action:status / action:reset (fully synchronous) ------------------

        [Test]
        public void Execute_ActionStatus_ReportsZeroInitially()
        {
            var tool = new UapExecuteCodeTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("action", "status"));
            JsonNode json = ParseResultJson(result);
            Assert.AreEqual(0, json["loadedSnippets"].AsInt());
            Assert.AreEqual(0, json["loadedBytes"].AsLong());
        }

        [Test]
        public void Execute_ActionReset_WithoutConfirm_RefusesAndDoesNotSchedule()
        {
            bool scheduled = false;
            UapExecuteCodeTool.Scheduler = delegate { scheduled = true; };
            var tool = new UapExecuteCodeTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("action", "reset"));
            StringAssert.Contains("Refused", result[0]["text"].AsString());
            Assert.IsFalse(scheduled, "reset must not schedule a reload without confirm:true.");
        }

        [Test]
        public void Execute_ActionReset_WithConfirm_SchedulesReload()
        {
            var scheduled = new List<Action>();
            UapExecuteCodeTool.Scheduler = scheduled.Add;
            var tool = new UapExecuteCodeTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("action", "reset").Set("confirm", true));
            StringAssert.Contains("reload requested", result[0]["text"].AsString());
            Assert.AreEqual(1, scheduled.Count, "action:reset with confirm:true must defer exactly one action.");
        }

        // -- flagged snippet without confirm: preview only, nothing runs -------

        [Test]
        public void Execute_FlaggedSnippetWithoutConfirm_PreviewsAndDoesNotRun()
        {
            string markerPath = Path.Combine(ProjectRoot(), "Temp", "uap-exec-test-marker-" + Guid.NewGuid().ToString("N") + ".txt");
            string code = "System.IO.File.WriteAllText(\"" + EscapeForCSharpString(markerPath) + "\", \"ran\"); return 1;";
            try
            {
                var tool = new UapExecuteCodeTool();
                JsonNode result = tool.Execute(RunInput(code));
                string text = result[0]["text"].AsString();
                StringAssert.Contains("risk flag", text);
                StringAssert.Contains("confirm:true", text);
                Assert.IsFalse(File.Exists(markerPath), "a flagged snippet without confirm must not run at all.");
            }
            finally
            {
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
            }
        }

        // -- real compile+run (AssemblyBuilder) ----------------------------------

        /// <summary>
        /// Two callers issuing the SAME not-yet-cached code concurrently (a
        /// retried call racing the original after a perceived timeout). In a
        /// real Editor the first call's AssemblyBuilder makes
        /// EditorApplication.isCompiling true, so the second call is refused
        /// with the "compiling" message instead of racing it (design note
        /// docs/design-notes/2026-09-28-uloop-phase3.md section 7). If the
        /// first build is already done by then, the second simply runs.
        /// Either way the first call is always driven to completion, even
        /// when an assertion fails: a build left running keeps isCompiling
        /// true and makes every later test that refuses "while compiling"
        /// fail for the wrong reason (it did, in CI run 36406880492).
        /// </summary>
        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_SecondCallWhileTheFirstCompiles_IsRefusedOrRuns_AndTheFirstCompletes()
        {
            var toolA = new UapExecuteCodeTool();
            var toolB = new UapExecuteCodeTool();
            JsonNode input = RunInput("return 10 + 20;");
            object stateA = null;
            JsonNode resultA;
            bool doneA = toolA.Poll(input, ref stateA, out resultA);

            string refusal = null;
            object stateB = null;
            JsonNode resultB = null;
            bool doneB = false;
            try
            {
                doneB = toolB.Poll(input, ref stateB, out resultB);
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex.Message;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < 60 && (!doneA || (refusal == null && !doneB)))
            {
                if (!doneA)
                {
                    doneA = toolA.Poll(input, ref stateA, out resultA);
                }
                if (refusal == null && !doneB)
                {
                    doneB = toolB.Poll(input, ref stateB, out resultB);
                }
                yield return null;
            }

            Assert.IsTrue(doneA, "call A did not finish within the time budget.");
            JsonNode jsonA = ParseResultJson(resultA);
            Assert.IsFalse(jsonA["threw"].AsBool(true), "call A must compile and run cleanly.");
            Assert.AreEqual(30, jsonA["returnValue"].AsInt());
            if (refusal != null)
            {
                StringAssert.Contains("compiling", refusal);
            }
            else
            {
                Assert.IsTrue(doneB, "call B did not finish within the time budget.");
                Assert.AreEqual(30, ParseResultJson(resultB)["returnValue"].AsInt());
            }
        }

        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_TrivialSnippet_CompilesRunsAndCaches()
        {
            var tool = new UapExecuteCodeTool();
            object state = null;
            JsonNode input = RunInput("return 1 + 2;");
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

            Assert.IsNotNull(result, "run did not finish within the time budget.");
            JsonNode json = ParseResultJson(result);
            Assert.IsFalse(json["threw"].AsBool(true), "trivial snippet must not throw.");
            Assert.AreEqual(3, json["returnValue"].AsInt());
            Assert.AreEqual(1, json["loadedSnippets"].AsInt());

            // Second call with the SAME code must hit the cache (no second
            // compile) -- Begin() resolves this synchronously, so a single
            // Poll() call is enough to finish.
            object state2 = null;
            JsonNode result2;
            bool done2 = tool.Poll(input, ref state2, out result2);
            Assert.IsTrue(done2, "an identical, already-cached snippet must finish on the FIRST poll.");
            JsonNode json2 = ParseResultJson(result2);
            Assert.AreEqual(3, json2["returnValue"].AsInt());
            Assert.AreEqual(1, json2["loadedSnippets"].AsInt(), "identical code must not load a second assembly.");
        }

        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_FlaggedSnippetWithConfirm_RunsAndProducesTheSideEffect()
        {
            string markerPath = Path.Combine(ProjectRoot(), "Temp", "uap-exec-test-marker-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                string code = "System.IO.File.WriteAllText(\"" + EscapeForCSharpString(markerPath) + "\", \"ran\"); return 1;";
                var tool = new UapExecuteCodeTool();
                object state = null;
                JsonNode input = RunInput(code, null, true);
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

                Assert.IsNotNull(result, "confirmed run did not finish within the time budget.");
                JsonNode json = ParseResultJson(result);
                Assert.IsFalse(json["threw"].AsBool(true));
                Assert.IsTrue(File.Exists(markerPath), "confirm:true must let a flagged snippet actually run.");
            }
            finally
            {
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
            }
        }

        [UnityTest]
        [Timeout(90000)]
        public IEnumerator Poll_CompileError_ReportsMappedLineAndDoesNotCache()
        {
            var tool = new UapExecuteCodeTool();
            object state = null;
            // Line 1 of the user's own code (after the fixed 2-line
            // preamble) is deliberately broken.
            JsonNode input = RunInput("this is not valid C#;");
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

            Assert.IsNotNull(result, "compile did not finish within the time budget.");
            string text = result[0]["text"].AsString();
            StringAssert.Contains("Compilation failed", text);
            // Line mapping happened (a raw wrapped-source line number,
            // always >= 3, would be wrong here); this does not pin the
            // EXACT line Roslyn attributes a syntax-recovery error to,
            // which this sandbox cannot verify against a real compiler
            // (see this task's "unverified" list).
            StringAssert.DoesNotContain("(line 0,", text);

            object statusState = null;
            JsonNode statusResult;
            tool.Poll(JsonNode.NewObject().Set("action", "status"), ref statusState, out statusResult);
            JsonNode statusJson = ParseResultJson(statusResult);
            Assert.AreEqual(0, statusJson["loadedSnippets"].AsInt(), "a failed compile must not be cached.");
        }

        private static JsonNode ParseResultJson(JsonNode toolResult)
        {
            string text = toolResult[0]["text"].AsString();
            JsonNode node;
            string error;
            Assert.IsTrue(JsonParser.TryParse(text, out node, out error), "tool result must be valid JSON: " + error);
            return node;
        }
    }
}
