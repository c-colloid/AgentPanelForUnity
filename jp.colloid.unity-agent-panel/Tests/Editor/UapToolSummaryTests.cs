using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapToolSummary.FirstSentence/MatchesQuery (design note docs/design-
    /// notes/2026-09-22-uloop-phase1.md section 2.8 A'+B) -- pure string
    /// rules, mirrored verbatim in the license-free smoke gate
    /// (UapToolSummarySmoke), so these run against the exact same
    /// production methods there and here.
    /// </summary>
    [TestFixture]
    public class UapToolSummaryTests
    {
        [Test]
        public void FirstSentence_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, UapToolSummary.FirstSentence(null));
            Assert.AreEqual(string.Empty, UapToolSummary.FirstSentence(string.Empty));
            Assert.AreEqual(string.Empty, UapToolSummary.FirstSentence("   "));
        }

        [Test]
        public void FirstSentence_StopsAtTheFirstRealSentenceEnd()
        {
            Assert.AreEqual("Gets or sets the size.",
                UapToolSummary.FirstSentence("Gets or sets the size. Also does more things here."));
        }

        [Test]
        public void FirstSentence_DoesNotBreakOn_EgOrIe_Abbreviations()
        {
            string result = UapToolSummary.FirstSentence(
                "Runs a search, e.g. by name or module, and returns matches.");
            StringAssert.Contains("e.g.", result);
            StringAssert.EndsWith("matches.", result);
        }

        [Test]
        public void FirstSentence_DoesNotBreakOnADottedIdentifierOrAVersionNumber()
        {
            string result = UapToolSummary.FirstSentence(
                "Reads EditorApplication.isPlaying on Unity 2022.3 and reports it now.");
            StringAssert.EndsWith("reports it now.", result);
        }

        [Test]
        public void FirstSentence_NoTerminatorAtAll_ReturnsTheWholeCollapsedString()
        {
            Assert.AreEqual("No punctuation here at all",
                UapToolSummary.FirstSentence("  No   punctuation   here at all  "));
        }

        [Test]
        public void FirstSentence_LongSentence_CapsAtMaxChars_WithEllipsis_AtAWordBoundary()
        {
            string longSentence = "This is a very long sentence that keeps going and going and going "
                + "and going and going and going and going and going without ever actually stopping "
                + "until it eventually runs out of words to say, which takes a while.";
            string result = UapToolSummary.FirstSentence(longSentence);
            Assert.LessOrEqual(result.Length, UapToolSummary.MaxChars);
            StringAssert.EndsWith("...", result);
            Assert.IsFalse(result.Contains("  "), "collapsed whitespace should never leave a double space");
        }

        [Test]
        public void FirstSentence_CutRightAfterADoubleDash_NeverLeavesADanglingHyphen()
        {
            // Constructed so the word-boundary cut lands exactly on the
            // space right after " -- ": without the trailing '-'/' ' trim,
            // the result would end "...AAA --..." instead of "...AAA...".
            string longSentence = new string('A', 150) + " -- " + new string('b', 60) + " tail.";
            string result = UapToolSummary.FirstSentence(longSentence);
            StringAssert.EndsWith("...", result);
            string withoutEllipsis = result.Substring(0, result.Length - 3);
            StringAssert.DoesNotEndWith("-", withoutEllipsis);
            StringAssert.DoesNotEndWith(" ", withoutEllipsis);
        }

        [Test]
        public void MatchesQuery_EmptyQuery_MatchesEverything()
        {
            Assert.IsTrue(UapToolSummary.MatchesQuery("uap_ping", "core", "Echoes text back.", null));
            Assert.IsTrue(UapToolSummary.MatchesQuery("uap_ping", "core", "Echoes text back.", string.Empty));
        }

        [Test]
        public void MatchesQuery_MatchesByName_CaseInsensitive()
        {
            Assert.IsTrue(UapToolSummary.MatchesQuery("uap_editor_screenshot", "editor", "Captures.", "SCREENSHOT"));
        }

        [Test]
        public void MatchesQuery_MatchesByModule()
        {
            Assert.IsTrue(UapToolSummary.MatchesQuery("uap_x", "editor", "Does a thing.", "editor"));
        }

        [Test]
        public void MatchesQuery_MatchesBySummary_NotJustNameOrModule()
        {
            Assert.IsTrue(UapToolSummary.MatchesQuery("uap_x", "core", "Runs the project's build pipeline.", "pipeline"));
        }

        [Test]
        public void MatchesQuery_NoMatchAnywhere_ReturnsFalse()
        {
            Assert.IsFalse(UapToolSummary.MatchesQuery("uap_ping", "core", "Echoes text back.", "zzz_no_match"));
        }
    }
}
