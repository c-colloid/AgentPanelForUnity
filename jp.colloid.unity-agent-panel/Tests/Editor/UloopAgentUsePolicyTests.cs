using System.Collections.Generic;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pins the "let the agent run uloop commands" switch's pure rules
    /// (docs/design-notes/2026-09-21-uloop-always-loaded-cost.md section 5,
    /// option A). Three things this fixture exists to hold:
    ///
    /// 1. The detector catches uloop wherever it sits in a command line.
    ///    A deny that only matches a command STARTING with uloop is worth
    ///    nothing -- `cd Foo && uloop compile` is the shape an agent
    ///    actually writes.
    /// 2. The deny-pattern overlay never edits the user's own list. The
    ///    switch is a spawn-time overlay; a bug that wrote into
    ///    PanelSettings.disallowedTools would leave the patterns behind
    ///    after the user switched it back on.
    /// 3. The steering wording differs between the two states. With the
    ///    switch off, an agent that only learns about the refusal from a
    ///    denial has already burned a turn.
    /// </summary>
    [TestFixture]
    public class UloopAgentUsePolicyTests
    {
        [TestCase("uloop compile")]
        [TestCase("uloop")]
        [TestCase("  uloop   run-tests  ")]
        [TestCase("cd MyProject && uloop compile")]
        [TestCase("echo hi | uloop execute-dynamic-code")]
        [TestCase("echo hi; uloop status")]
        [TestCase("./uloop compile")]
        [TestCase("/usr/local/bin/uloop compile")]
        [TestCase("C:\\tools\\uloop.exe compile")]
        [TestCase("ULOOP compile")]
        [TestCase("uloop.cmd compile")]
        public void IsUloopInvocation_CatchesTheShapesAnAgentWrites(string command)
        {
            Assert.IsTrue(UloopAgentUsePolicy.IsUloopInvocation(command), command);
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("git status")]
        [TestCase("echo uloop")]
        [TestCase("cat uloop.md")]
        [TestCase("dotnet build uloop.csproj")]
        // A command whose NAME merely starts with the four letters is not
        // uloop: denying it would silently break an unrelated tool.
        [TestCase("uloopctl status")]
        [TestCase("my-uloop compile")]
        public void IsUloopInvocation_LeavesEverythingElseAlone(string command)
        {
            Assert.IsFalse(UloopAgentUsePolicy.IsUloopInvocation(command), command ?? "(null)");
        }

        [Test]
        public void BuildDisallowedPatterns_CoversEveryShellToolTheGateKnows()
        {
            List<string> patterns = UloopAgentUsePolicy.BuildDisallowedPatterns();
            foreach (string shell in ScriptGate.ShellToolNameList)
            {
                // Both forms: the wildcard grammar is unmeasured, the same
                // reason the uLoop ALLOW preset writes an exact and a
                // wildcard entry for every subcommand.
                CollectionAssert.Contains(patterns, shell + "(uloop)");
                CollectionAssert.Contains(patterns, shell + "(uloop *)");
            }
            Assert.AreEqual(patterns.Count, new HashSet<string>(patterns).Count,
                "the deny patterns must not repeat");
        }

        [Test]
        public void ComposeDisallowedTools_SwitchOn_IsTheUsersListVerbatim()
        {
            var configured = new List<string> { "Bash(rm *)", "WebFetch" };
            List<string> composed = UloopAgentUsePolicy.ComposeDisallowedTools(configured, true);
            CollectionAssert.AreEqual(configured, composed);
        }

        [Test]
        public void ComposeDisallowedTools_SwitchOff_AppendsWithoutTouchingTheUsersList()
        {
            var configured = new List<string> { "Bash(rm *)", "WebFetch" };
            List<string> composed = UloopAgentUsePolicy.ComposeDisallowedTools(configured, false);

            Assert.AreEqual(2, configured.Count, "the caller's own list must never be mutated");
            Assert.AreEqual("Bash(rm *)", composed[0], "the user's entries keep their order and position");
            Assert.AreEqual("WebFetch", composed[1]);
            foreach (string pattern in UloopAgentUsePolicy.BuildDisallowedPatterns())
            {
                CollectionAssert.Contains(composed, pattern);
            }
        }

        [Test]
        public void ComposeDisallowedTools_SwitchOff_DoesNotDuplicateWhatTheUserAlreadyWrote()
        {
            var configured = new List<string> { " Bash(uloop *) " };
            List<string> composed = UloopAgentUsePolicy.ComposeDisallowedTools(configured, false);
            int occurrences = 0;
            foreach (string entry in composed)
            {
                if (entry.Trim() == "Bash(uloop *)")
                {
                    occurrences++;
                }
            }
            Assert.AreEqual(1, occurrences,
                "an entry the user already wrote (even padded) must not be added a second time");
        }

        [Test]
        public void ComposeDisallowedTools_NullConfigured_StillCarriesTheDenyPatterns()
        {
            List<string> composed = UloopAgentUsePolicy.ComposeDisallowedTools(null, false);
            CollectionAssert.AreEquivalent(UloopAgentUsePolicy.BuildDisallowedPatterns(), composed);
            Assert.AreEqual(0, UloopAgentUsePolicy.ComposeDisallowedTools(null, true).Count);
        }

        [Test]
        public void DescribeCommandForNote_IsOneTruncatedLine()
        {
            Assert.AreEqual("uloop compile", UloopAgentUsePolicy.DescribeCommandForNote("  uloop compile \n"));
            Assert.AreEqual("uloop a b", UloopAgentUsePolicy.DescribeCommandForNote("uloop a\nb"));
            Assert.AreEqual(string.Empty, UloopAgentUsePolicy.DescribeCommandForNote(null));

            string huge = "uloop " + new string('x', UloopAgentUsePolicy.NoteCommandMaxChars * 2);
            string described = UloopAgentUsePolicy.DescribeCommandForNote(huge);
            Assert.AreEqual(UloopAgentUsePolicy.NoteCommandMaxChars + 3, described.Length);
            Assert.IsTrue(described.EndsWith("..."));
        }

        [Test]
        public void SteeringLine_OffSaysRefused_OnKeepsTheEscapeHatchWording()
        {
            string on = UloopAgentUsePolicy.SteeringLine(true);
            string off = UloopAgentUsePolicy.SteeringLine(false);
            Assert.AreNotEqual(on, off);
            StringAssert.Contains("slower", on);
            StringAssert.Contains("refused", off);
            StringAssert.Contains("Do NOT call uloop", off);
            Assert.IsTrue(on.EndsWith("\n") && off.EndsWith("\n"),
                "both lines are spliced into a paragraph list and must end the line themselves");
        }

        /// <summary>
        /// The refusal the agent reads has to name the replacement tools:
        /// a bare "denied" is what makes a model try another shell.
        /// </summary>
        [Test]
        public void DenyMessage_NamesTheReplacementsAndWhoCanTurnItBackOn()
        {
            StringAssert.Contains("uap_play_mode", UloopAgentUsePolicy.DenyMessage);
            StringAssert.Contains("uap_console_logs", UloopAgentUsePolicy.DenyMessage);
            StringAssert.Contains("Settings", UloopAgentUsePolicy.DenyMessage);
            StringAssert.Contains("let the user decide", UloopAgentUsePolicy.DenyMessage);
        }
    }
}
