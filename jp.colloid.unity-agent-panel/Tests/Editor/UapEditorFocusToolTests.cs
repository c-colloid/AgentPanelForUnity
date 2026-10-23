using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_editor_focus (docs/design-notes/2026-09-22-uloop-phase1.md
    /// section 5).
    ///
    /// What is NOT asserted here, on purpose: that the OS actually
    /// changed foreground focus. A batch-mode CI runner has no window
    /// server (Windows: no top-level window, so MainWindowHandle is
    /// IntPtr.Zero; Linux CI images generally lack wmctrl and any
    /// X server; macOS batch mode has no window server session either),
    /// so the OS-level half is expected to fail there -- and its whole
    /// job is to report that failure honestly rather than pretend. The
    /// cases below cover the pure command-building/parsing rules (also
    /// pinned in ci/SmokeTests) and that Execute() answers with a
    /// well-shaped reply and never throws, on whatever platform CI runs.
    /// </summary>
    [TestFixture]
    public class UapEditorFocusToolTests
    {
        [Test]
        public void DetectPlatform_MapsEachFlag_AndNoneIsUnsupported()
        {
            Assert.AreEqual(UapEditorFocusPlatform.Windows, UapEditorFocusPolicy.DetectPlatform(true, false, false));
            Assert.AreEqual(UapEditorFocusPlatform.MacOS, UapEditorFocusPolicy.DetectPlatform(false, true, false));
            Assert.AreEqual(UapEditorFocusPlatform.Linux, UapEditorFocusPolicy.DetectPlatform(false, false, true));
            Assert.AreEqual(UapEditorFocusPlatform.Unsupported, UapEditorFocusPolicy.DetectPlatform(false, false, false));
        }

        [Test]
        public void BuildOsascriptActivateArguments_TargetsThePidNotATitle()
        {
            string[] args = UapEditorFocusPolicy.BuildOsascriptActivateArguments(4242);
            Assert.AreEqual(2, args.Length);
            Assert.AreEqual("-e", args[0]);
            StringAssert.Contains("System Events", args[1]);
            StringAssert.Contains("unix id is 4242", args[1]);
            StringAssert.Contains("frontmost", args[1]);
        }

        [Test]
        public void BuildWmctrlListArguments_IsJustDashL()
        {
            CollectionAssert.AreEqual(new[] { "-l" }, UapEditorFocusPolicy.BuildWmctrlListArguments());
        }

        [Test]
        public void BuildWmctrlActivateArguments_UsesDashIDashAWithTheExactId()
        {
            CollectionAssert.AreEqual(new[] { "-i", "-a", "0x02c00003" },
                UapEditorFocusPolicy.BuildWmctrlActivateArguments("0x02c00003"));
        }

        private const string SampleWmctrlList =
            "0x02c00003  0 host1   My Game - Unity 2022.3.22f1 Personal <DX11>\n"
            + "0x01800004  1 host1   Mozilla Firefox\n"
            + "0x03e00007 -1 host1   Some Sticky Panel\n";

        [Test]
        public void FindWindowId_MatchesByTitleSubstring_CaseInsensitive()
        {
            Assert.AreEqual("0x02c00003", UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, "My Game"));
            Assert.AreEqual("0x02c00003", UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, "my game"));
            Assert.AreEqual("0x01800004", UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, "Firefox"));
        }

        [Test]
        public void FindWindowId_HandlesANegativeDesktopField()
        {
            Assert.AreEqual("0x03e00007", UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, "Sticky"));
        }

        [Test]
        public void FindWindowId_NoMatch_OrEmptyInput_ReturnsNull()
        {
            Assert.IsNull(UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, "Nonexistent App"));
            Assert.IsNull(UapEditorFocusPolicy.FindWindowId(string.Empty, "My Game"));
            Assert.IsNull(UapEditorFocusPolicy.FindWindowId(null, "My Game"));
            Assert.IsNull(UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, string.Empty));
            Assert.IsNull(UapEditorFocusPolicy.FindWindowId(SampleWmctrlList, null));
        }

        [Test]
        public void FindWindowId_IgnoresUnparsableLines()
        {
            string withJunk = "not a wmctrl line at all\n" + SampleWmctrlList;
            Assert.AreEqual("0x02c00003", UapEditorFocusPolicy.FindWindowId(withJunk, "My Game"));
        }

        [Test]
        public void BuildLinuxWmctrlShellCommand_ChecksPresenceBeforeRunning()
        {
            string command = UapEditorFocusPolicy.BuildLinuxWmctrlShellCommand(new[] { "-l" });
            StringAssert.Contains("command -v wmctrl", command);
            StringAssert.Contains("wmctrl -l", command);
            StringAssert.Contains("exit " + UapEditorFocusPolicy.LinuxWmctrlMissingExitCode, command);
        }

        [Test]
        public void NeedsQuoting_TriggersOnSpaceTabQuoteAndControlChars_NotOnPlainText()
        {
            Assert.IsFalse(UapEditorFocusPolicy.NeedsQuoting("0x02c00003"));
            Assert.IsFalse(UapEditorFocusPolicy.NeedsQuoting(string.Empty));
            Assert.IsTrue(UapEditorFocusPolicy.NeedsQuoting("has space"));
            Assert.IsTrue(UapEditorFocusPolicy.NeedsQuoting("has\ttab"));
            Assert.IsTrue(UapEditorFocusPolicy.NeedsQuoting("has\"quote"));
            Assert.IsTrue(UapEditorFocusPolicy.NeedsQuoting("has\nnewline"));
        }

        [Test]
        public void QuoteArgument_PlainTokenIsUnchanged()
        {
            Assert.AreEqual("0x02c00003", UapEditorFocusPolicy.QuoteArgument("0x02c00003"));
            Assert.AreEqual("-a", UapEditorFocusPolicy.QuoteArgument("-a"));
        }

        [Test]
        public void QuoteArgument_WrapsAndEscapesWhenNeeded()
        {
            Assert.AreEqual("\"tell app \\\"X\\\" to run\"",
                UapEditorFocusPolicy.QuoteArgument("tell app \"X\" to run"));
        }

        [Test]
        public void QuoteArgument_TrailingBackslashIsDoubledBeforeTheClosingQuote()
        {
            // The embedded space is what makes NeedsQuoting true in the
            // first place -- a bare "C:\Temp\" has no space/quote/control
            // char, so it needs no quoting at all and QuoteArgument would
            // correctly return it unchanged (a trailing backslash is only
            // ambiguous immediately before a CLOSING QUOTE). Once quoting
            // IS triggered, a naive Replace("\"","\\\"") would leave the
            // closing quote escaped away instead of terminated -- the same
            // bug AgentClient.QuoteArg's own comment documents.
            Assert.AreEqual("\"C:\\Temp Path\\\\\"", UapEditorFocusPolicy.QuoteArgument("C:\\Temp Path\\"));
        }

        [Test]
        public void JoinArguments_OnlyQuotesTheTokensThatNeedIt()
        {
            Assert.AreEqual("-i -a 0x02c00003",
                UapEditorFocusPolicy.JoinArguments(new[] { "-i", "-a", "0x02c00003" }));
            Assert.AreEqual("-c \"command -v wmctrl >/dev/null 2>&1; then wmctrl -l; fi\"",
                UapEditorFocusPolicy.JoinArguments(new[] { "-c", "command -v wmctrl >/dev/null 2>&1; then wmctrl -l; fi" }));
            Assert.AreEqual(string.Empty, UapEditorFocusPolicy.JoinArguments(new string[0]));
        }

        // -- Tool metadata + a real Execute() in this batch-mode project --------

        [Test]
        public void Tool_IsNotReadOnly_NotUndoable_InEditorModule()
        {
            var tool = new UapEditorFocusTool();
            Assert.AreEqual("uap_editor_focus", tool.Name);
            Assert.AreEqual("editor", tool.Module);
            Assert.IsFalse(tool.ReadOnly, "taking OS-level foreground focus is a real, visible side effect.");
            Assert.IsFalse(tool.Undoable);
            Assert.IsFalse(string.IsNullOrEmpty(tool.Description));
            StringAssert.Contains("Run In Background", tool.Description);
        }

        [Test]
        public void Tool_InputSchema_TakesNoArguments()
        {
            JsonNode schema = new UapEditorFocusTool().InputSchema;
            Assert.AreEqual("object", schema["type"].AsString());
            Assert.IsTrue(schema["properties"].IsObject);
            Assert.AreEqual(0, schema["properties"].Count);
        }

        [Test]
        public void Execute_NeverThrows_AndAnswersWithAWellShapedReply()
        {
            string text = new UapEditorFocusTool().Execute(JsonNode.NewObject())[0]["text"].AsString();
            JsonNode reply = JsonParser.Parse(text);

            Assert.IsFalse(string.IsNullOrEmpty(reply["platform"].AsString()), text);
            Assert.IsTrue(reply["editorWindowFocused"].IsBool, text);
            Assert.IsTrue(reply["osLevelFocused"].IsBool, text);
            Assert.IsFalse(string.IsNullOrEmpty(reply["message"].AsString()), text);
            if (!reply["osLevelFocused"].AsBool())
            {
                // Batch mode has no window server on any platform, so the
                // OS-level half is expected to fail here; the point is
                // that it fails WITH a reason, not silently.
                Assert.IsFalse(string.IsNullOrEmpty(reply["osLevelDetail"].AsString(string.Empty)), text);
            }
        }
    }
}
