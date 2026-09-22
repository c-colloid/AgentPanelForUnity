using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The Console capture behind uap_console_logs (design note
    /// docs/design-notes/2026-09-21-play-mode-and-console-log-tools.md).
    /// The pure selection rules also run in the license-free smoke gate
    /// (ci/SmokeTests); what needs a real Editor is the half that maps
    /// Unity's LogType and actually receives Application.logMessage
    /// callbacks, which is what this fixture covers alongside the
    /// selection cases the tool's own answers depend on.
    ///
    /// Capture is process-global state, so SetUp remembers whether a live
    /// agent session had it running and TearDown puts that back: running
    /// the suite must not leave a developer's session without its logs.
    /// </summary>
    [TestFixture]
    public class UapConsoleLogBufferTests
    {
        private bool _wasCapturing;

        [SetUp]
        public void SetUp()
        {
            _wasCapturing = UapConsoleLogStore.Capturing;
            UapConsoleLogBuffer.Install();
            UapConsoleLogStore.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            // Restore precisely: a test that uninstalled mid-body must not
            // leave a developer's live agent session without its capture.
            if (_wasCapturing)
            {
                UapConsoleLogBuffer.Install();
            }
            else
            {
                UapConsoleLogBuffer.Uninstall();
            }
            UapConsoleLogStore.Clear();
        }

        /// <summary>The captured entry with this exact message -- indexes would break on any stray editor log.</summary>
        private static UapConsoleLogEntry Find(string message)
        {
            UapConsoleLogEntry found = UapConsoleLogStore.Snapshot()
                .FindLast(delegate(UapConsoleLogEntry e) { return e.Message == message; });
            Assert.AreEqual(message, found.Message, "expected a captured entry with this message");
            return found;
        }

        [Test]
        public void TypeName_MapsEveryUnityLogType()
        {
            Assert.AreEqual("error", UapConsoleLogBuffer.TypeName(LogType.Error));
            Assert.AreEqual("assert", UapConsoleLogBuffer.TypeName(LogType.Assert));
            Assert.AreEqual("warning", UapConsoleLogBuffer.TypeName(LogType.Warning));
            Assert.AreEqual("exception", UapConsoleLogBuffer.TypeName(LogType.Exception));
            Assert.AreEqual("log", UapConsoleLogBuffer.TypeName(LogType.Log));
        }

        [Test]
        public void Install_CapturesARealUnityLog()
        {
            // Deliberately an info log: an error raised here would fail the
            // test run itself (Unity's LogAssert), and the error PATH is
            // covered by Capture() cases that need no Unity logging.
            Debug.Log("uap-console-log-fixture-marker");

            List<UapConsoleLogEntry> captured = UapConsoleLogStore.Snapshot()
                .FindAll(delegate(UapConsoleLogEntry e)
                {
                    return e.Message == "uap-console-log-fixture-marker";
                });
            Assert.AreEqual(1, captured.Count, "the log callback should have recorded exactly one line");
            Assert.AreEqual("log", captured[0].Type);
        }

        [Test]
        public void Uninstall_StopsCapturingAndForgetsWhatItHad()
        {
            UapConsoleLogStore.Capture("error", "before uninstall", "stack");
            UapConsoleLogBuffer.Uninstall();

            Assert.IsFalse(UapConsoleLogStore.Capturing);
            Assert.AreEqual(0, UapConsoleLogStore.Snapshot().Count);

            Debug.Log("uap-console-log-fixture-after-uninstall");
            Assert.AreEqual(0, UapConsoleLogStore.Snapshot().Count,
                "a log raised after Uninstall must not be recorded");
        }

        [Test]
        public void Capture_KeepsStackTracesOnlyForTheSeveritiesWorthTheTokens()
        {
            UapConsoleLogStore.Capture("error", "boom", "at Thing.Do()");
            UapConsoleLogStore.Capture("warning", "meh", "at Thing.Do()");

            Assert.AreEqual("at Thing.Do()", Find("boom").StackTrace);
            Assert.AreEqual(string.Empty, Find("meh").StackTrace);
        }

        [Test]
        public void Capture_TruncatesAnOverLongMessageAndSaysSo()
        {
            UapConsoleLogStore.Capture("log", new string('x', UapConsoleLogStore.MaxMessageChars + 50), null);

            string message = UapConsoleLogStore.Snapshot()[UapConsoleLogStore.Snapshot().Count - 1].Message;
            Assert.IsTrue(message.EndsWith(UapConsoleLogStore.TruncationMarker), "message: " + message);
            Assert.AreEqual(UapConsoleLogStore.MaxMessageChars + UapConsoleLogStore.TruncationMarker.Length,
                message.Length);
        }

        [Test]
        public void Select_CollapsesAFloodBeforeApplyingTheCount()
        {
            // The shape this tool exists for: one exception repeating while
            // the interesting lines sit around it. Without collapse-then-
            // count, a count of 2 would return two copies of the flood and
            // hide both.
            UapConsoleLogStore.Capture("warning", "shader variant missing", null);
            for (int i = 0; i < 20; i++)
            {
                UapConsoleLogStore.Capture("error", "NullReferenceException in Enemy.Update", "at Enemy.Update()");
            }
            UapConsoleLogStore.Capture("log", "player spawned", null);

            List<UapConsoleLogEntry> all = UapConsoleLogStore.Snapshot();
            List<UapConsoleLogEntry> selected = UapConsoleLogStore.Select(all,
                new UapConsoleLogSelection { Count = 2, Collapse = true });

            Assert.AreEqual(2, selected.Count);
            Assert.AreEqual(20, selected[0].Occurrences);
            Assert.AreEqual(all.FindLast(delegate(UapConsoleLogEntry e) { return e.Type == "error"; }).Id,
                selected[0].Id,
                "a collapsed run carries the newest id, so since_id paging cannot repeat it");
            Assert.AreEqual("player spawned", selected[1].Message);
        }

        [Test]
        public void Tool_ReportsTheBufferStateAndRefusesAnUnknownType()
        {
            UapConsoleLogStore.Capture("error", "boom", "at Thing.Do()");
            var tool = new UapConsoleLogsTool();

            string text = tool.Execute(JsonNode.NewObject())[0]["text"].AsString();
            StringAssert.Contains("boom", text);
            StringAssert.Contains("\"capturing\":true", text);
            StringAssert.Contains("\"lastId\":", text);

            Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("types", JsonNode.NewArray().Add("errors")));
            }, "a typo'd type must be refused rather than silently widening the answer");
        }

        [Test]
        public void Tool_OmitsStackTracesUnlessAsked()
        {
            UapConsoleLogStore.Capture("error", "boom", "at Thing.Do()");
            var tool = new UapConsoleLogsTool();

            string without = tool.Execute(JsonNode.NewObject())[0]["text"].AsString();
            Assert.IsFalse(without.Contains("stackTrace"), without);

            string with = tool.Execute(JsonNode.NewObject().Set("include_stack_trace", true))[0]["text"].AsString();
            StringAssert.Contains("at Thing.Do()", with);
        }
    }
}
