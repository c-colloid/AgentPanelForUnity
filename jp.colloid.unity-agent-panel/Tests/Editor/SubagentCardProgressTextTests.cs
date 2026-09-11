using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Live report fix (docs/design-notes/2026-08-03-subagent-ux-and-
    /// midturn-input.md section 2, "no sign of activity between tool
    /// calls"). A long subagent run (7 steps) was captured for that note:
    /// the first task_progress event lands 5.7s after the subagent spawns,
    /// and BuildProgressText returning empty during that window used to set
    /// the WHOLE progress row to display:None -- exactly the blank,
    /// spinner-only card the user could not distinguish from a stall.
    ///
    /// SubagentCard.BuildProgressText is a pure static function of its six
    /// inputs (progress line, tool name, step count, token count, seconds-
    /// since-update, running flag) precisely so this suite can pin every
    /// field's presence/absence and the blank-window fallback without
    /// constructing a card, a SubagentRecord, or a live scheduler tick.
    /// SubagentCard.ShortenPathDescription is the second pure function this
    /// round adds (section 2.2 #4: measured descriptions are long absolute
    /// paths with no interior spaces, which the old white-space: normal
    /// styling could not wrap and which therefore overflowed the row).
    /// Both are internal, visible here via the existing
    /// InternalsVisibleTo("Colloid.AgentPanel.Editor.Tests")
    /// (Editor/AssemblyInfo.cs).
    /// </summary>
    public class SubagentCardProgressTextTests
    {
        // -- BuildProgressText: the blank-window case ----------------------

        [Test]
        public void BlankWindow_RunningWithNothingYet_ShowsWorking()
        {
            string result = SubagentCard.BuildProgressText(
                progressLine: string.Empty, lastToolName: string.Empty, toolUses: 0,
                totalTokens: 0, secondsSinceUpdate: 0, running: true);

            Assert.AreEqual(L10n.S.SubagentProgressWorking, result,
                "the exact 5.7s-blank window measured in the design note must show the "
                    + "'Working...' fallback instead of an empty string that hides the row");
        }

        [Test]
        public void BlankWindow_NotRunning_ReturnsEmpty()
        {
            // A finished card with nothing to show still hides the row --
            // the "Working..." fallback exists only to cover a RUNNING
            // card's blank startup window, not to invent activity for a
            // card that is already done (this branch is not reached by the
            // live UI today, since RefreshProgressLabel is only ever wired
            // up while status == "running", but the function contract
            // covers it explicitly per its required signature).
            string result = SubagentCard.BuildProgressText(
                progressLine: string.Empty, lastToolName: string.Empty, toolUses: 0,
                totalTokens: 0, secondsSinceUpdate: 0, running: false);

            Assert.AreEqual(string.Empty, result);
        }

        // -- Each field: present vs. absent --------------------------------

        [Test]
        public void ProgressLineAlone_IsReturnedUnchanged()
        {
            string result = SubagentCard.BuildProgressText(
                "Reading project settings", string.Empty, 0, 0, 0, true);

            Assert.AreEqual("Reading project settings", result);
        }

        [Test]
        public void ToolName_Present_IsAppendedInParens()
        {
            string result = SubagentCard.BuildProgressText("desc", "Write", 0, 0, 0, true);

            Assert.AreEqual("desc (Write)", result);
        }

        [Test]
        public void ToolName_Absent_IsOmitted()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 0, 0, true);

            Assert.AreEqual("desc", result);
        }

        [Test]
        public void ToolName_McpWireName_IsShortenedSameAsTheHeader()
        {
            // Review-fix regression guard from the earlier round (design
            // note section 7.3): a raw wire name here would read
            // inconsistently against the header's already-shortened
            // subagent type on the same card.
            string result = SubagentCard.BuildProgressText(
                "desc", "mcp__unity-ops__uap_query_hierarchy", 0, 0, 0, true);

            Assert.AreEqual("desc (uap_query_hierarchy)", result);
        }

        [Test]
        public void StepCount_Positive_IsAppended()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 3, 0, 0, true);

            Assert.AreEqual("desc -- 3 steps", result);
        }

        [Test]
        public void StepCount_Zero_IsOmitted()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 0, 0, true);

            Assert.AreEqual("desc", result);
        }

        [Test]
        public void TokenCount_Positive_IsAppended()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 500, 0, true);

            Assert.AreEqual("desc -- 500 tokens", result);
        }

        [Test]
        public void TokenCount_Zero_IsOmitted()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 0, 0, true);

            Assert.AreEqual("desc", result);
        }

        [Test]
        public void Staleness_RunningWithProgressLineAndPositiveSeconds_IsAppended()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 0, 5, true);

            Assert.AreEqual("desc -- updated 5s ago", result);
        }

        [Test]
        public void Staleness_ZeroSeconds_IsOmitted()
        {
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 0, 0, true);

            Assert.AreEqual("desc", result);
        }

        [Test]
        public void Staleness_NotRunning_IsOmittedEvenWithPositiveSeconds()
        {
            // Worded as "time since the last update", never as subagent
            // state -- a finished card has no meaningful "staleness" to
            // report even if the raw seconds value happens to be positive.
            string result = SubagentCard.BuildProgressText("desc", string.Empty, 0, 0, 5, false);

            Assert.AreEqual("desc", result);
        }

        [Test]
        public void Staleness_OmittedDuringBlankWindow_RegardlessOfSecondsValue()
        {
            // There is no "last update" to measure staleness against before
            // the first task_progress arrives -- gated on the progress line
            // itself being present, not merely on the running flag.
            string result = SubagentCard.BuildProgressText(
                string.Empty, string.Empty, 0, 0, 5, true);

            Assert.AreEqual(L10n.S.SubagentProgressWorking, result);
        }

        // -- Composition order, pinned against the measured log -----------

        [Test]
        public void AllFieldsPresent_ComposeInMeasuredLogOrder()
        {
            // Matches task_progress #7 from the design note's captured
            // 7-step run: "Running List contents.." Bash uses=7 tok=36176.
            string result = SubagentCard.BuildProgressText(
                "Running List contents..", "Bash", 7, 36176, 3, true);

            Assert.AreEqual("Running List contents.. (Bash) -- 7 steps -- 36176 tokens -- updated 3s ago",
                result);
        }

        [Test]
        public void AllFieldsPresent_WithPathShapedDescription_ShortensAndComposes()
        {
            string result = SubagentCard.BuildProgressText(
                "Writing ~\\AppData\\Local\\Temp\\claude\\proj\\guid\\scratchpad\\s1.txt",
                "Write", 1, 100, 2, true);

            Assert.AreEqual(
                "Writing ...\\scratchpad\\s1.txt (Write) -- 1 steps -- 100 tokens -- updated 2s ago",
                result);
        }

        // -- ShortenPathDescription -----------------------------------------------

        [Test]
        public void ShortenPathDescription_NonPathText_PassesThroughUnchanged()
        {
            string result = SubagentCard.ShortenPathDescription("Running List contents..");

            Assert.AreEqual("Running List contents..", result);
        }

        [Test]
        public void ShortenPathDescription_ProseWithoutSeparators_PassesThroughUnchanged()
        {
            string result = SubagentCard.ShortenPathDescription("Reading project settings");

            Assert.AreEqual("Reading project settings", result);
        }

        [Test]
        public void ShortenPathDescription_Empty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, SubagentCard.ShortenPathDescription(string.Empty));
        }

        [Test]
        public void ShortenPathDescription_Null_ReturnsEmptyWithoutThrowing()
        {
            Assert.AreEqual(string.Empty, SubagentCard.ShortenPathDescription(null));
        }

        [Test]
        public void ShortenPathDescription_LongBackslashPath_KeepsParentDirAndFileName()
        {
            string result = SubagentCard.ShortenPathDescription(
                "Writing ~\\AppData\\Local\\Temp\\claude\\proj\\guid\\scratchpad\\s1.txt");

            Assert.AreEqual("Writing ...\\scratchpad\\s1.txt", result);
        }

        [Test]
        public void ShortenPathDescription_LongForwardSlashPath_UsesForwardSlashInResult()
        {
            string result = SubagentCard.ShortenPathDescription(
                "Writing /home/user/project/scratchpad/s1.txt");

            Assert.AreEqual("Writing .../scratchpad/s1.txt", result);
        }

        [Test]
        public void ShortenPathDescription_NoLeadingVerb_StillShortensTheWholeString()
        {
            string result = SubagentCard.ShortenPathDescription(
                "~\\AppData\\Local\\Temp\\claude\\proj\\guid\\scratchpad\\s1.txt");

            Assert.AreEqual("...\\scratchpad\\s1.txt", result);
        }

        [Test]
        public void ShortenPathDescription_TwoSegmentPath_LeftUnchanged()
        {
            // Shortening "~\s1.txt" would make the string LONGER
            // ("...\~\s1.txt"), not shorter -- nothing to elide.
            string result = SubagentCard.ShortenPathDescription("Writing ~\\s1.txt");

            Assert.AreEqual("Writing ~\\s1.txt", result);
        }

        [Test]
        public void ShortenPathDescription_BareFileName_LeftUnchanged()
        {
            string result = SubagentCard.ShortenPathDescription("Writing s1.txt");

            Assert.AreEqual("Writing s1.txt", result);
        }

        [Test]
        public void ShortenPathDescription_TrailingSeparatorWithNoFileName_LeftUnchanged()
        {
            string result = SubagentCard.ShortenPathDescription("Listing C:\\Users\\colloid\\");

            Assert.AreEqual("Listing C:\\Users\\colloid\\", result);
        }
    }
}
