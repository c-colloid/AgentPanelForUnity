using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Exhaustive pure-logic coverage for HistoryListModel -- the entire
    /// filter/group/sort/page decision behind the session-history browser
    /// (docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
    /// section 2.4). Every test passes an explicit nowUtc so date-bucket
    /// assertions are exact and never flaky around the real wall clock or
    /// UTC midnight. ASCII-only string literals throughout (non-ASCII has
    /// repeatedly failed to round-trip through this repo's tooling).
    /// </summary>
    public class HistoryListModelTests
    {
        // Wednesday 2026-08-03T12:00:00Z. Picking a mid-day instant (rather
        // than exactly midnight) is deliberate: it proves date bucketing
        // keys off the *date*, not off "nowUtc minus N*24h".
        private static readonly DateTime Now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        private static HistoryRow Row(
            string sessionId,
            DateTime lastModifiedUtc,
            bool pinned = false,
            bool archived = false,
            string aiTitle = "",
            string preview = "",
            string titleOverride = "",
            string cwd = "",
            string customGroupId = "",
            string recordedScenePath = "",
            long sizeBytes = 0)
        {
            return new HistoryRow
            {
                SessionId = sessionId,
                LastModifiedUtc = lastModifiedUtc,
                Pinned = pinned,
                Archived = archived,
                AiTitle = aiTitle,
                Preview = preview,
                TitleOverride = titleOverride,
                Cwd = cwd,
                CustomGroupId = customGroupId,
                RecordedScenePath = recordedScenePath,
                SizeBytes = sizeBytes
            };
        }

        // -- ResolveTitle -----------------------------------------------------------

        [Test]
        public void ResolveTitle_TitleOverrideSet_WinsOverEverything()
        {
            HistoryRow row = Row("s1", Now, aiTitle: "ai title", preview: "preview text", titleOverride: "my rename");
            Assert.AreEqual("my rename", HistoryListModel.ResolveTitle(row));
        }

        [Test]
        public void ResolveTitle_NoOverride_FallsBackToAiTitle()
        {
            HistoryRow row = Row("s1", Now, aiTitle: "ai title", preview: "preview text");
            Assert.AreEqual("ai title", HistoryListModel.ResolveTitle(row));
        }

        [Test]
        public void ResolveTitle_NoOverrideNoAiTitle_FallsBackToPreview()
        {
            HistoryRow row = Row("s1", Now, preview: "preview text");
            Assert.AreEqual("preview text", HistoryListModel.ResolveTitle(row));
        }

        [Test]
        public void ResolveTitle_NothingSet_ReturnsEmpty_NeverInventsPlaceholderText()
        {
            HistoryRow row = Row("s1", Now);
            Assert.AreEqual(string.Empty, HistoryListModel.ResolveTitle(row));
        }

        // -- Matches ------------------------------------------------------------------

        [Test]
        public void Matches_NullOrWhitespaceQuery_MatchesEverything()
        {
            HistoryRow row = Row("s1", Now, aiTitle: "totally unrelated content");
            Assert.IsTrue(HistoryListModel.Matches(row, null));
            Assert.IsTrue(HistoryListModel.Matches(row, string.Empty));
            Assert.IsTrue(HistoryListModel.Matches(row, "   "));
        }

        [Test]
        public void Matches_CaseInsensitiveSubstring_OverResolvedTitle()
        {
            HistoryRow row = Row("s1", Now, titleOverride: "Fix The Compile Error");
            Assert.IsTrue(HistoryListModel.Matches(row, "compile"));
            Assert.IsTrue(HistoryListModel.Matches(row, "COMPILE"));
            Assert.IsFalse(HistoryListModel.Matches(row, "runtime"));
        }

        [Test]
        public void Matches_CaseInsensitiveSubstring_OverAiTitle_EvenWhenOverriddenElsewhere()
        {
            // The resolved title is the rename, but a search must still hit
            // the original ai-title -- renaming a session must not make it
            // unfindable by what it used to be called.
            HistoryRow row = Row("s1", Now, aiTitle: "Original Ai Title", titleOverride: "Renamed");
            Assert.IsTrue(HistoryListModel.Matches(row, "original"));
        }

        [Test]
        public void Matches_CaseInsensitiveSubstring_OverPreview()
        {
            HistoryRow row = Row("s1", Now, preview: "please refactor the widget loader");
            Assert.IsTrue(HistoryListModel.Matches(row, "widget"));
        }

        [Test]
        public void Matches_CaseInsensitiveSubstring_OverSessionId()
        {
            HistoryRow row = Row("ABCD-1234-EFGH", Now);
            Assert.IsTrue(HistoryListModel.Matches(row, "abcd"));
        }

        [Test]
        public void Matches_NonMatchingQuery_ReturnsFalse()
        {
            HistoryRow row = Row("s1", Now, aiTitle: "alpha", preview: "beta");
            Assert.IsFalse(HistoryListModel.Matches(row, "gamma"));
        }

        // -- GroupKeyFor: Date mode calendar-day bucketing -----------------------------

        [Test]
        public void GroupKeyFor_SameUtcCalendarDateAsNow_IsToday()
        {
            DateTime earlierToday = new DateTime(2026, 8, 3, 0, 0, 1, DateTimeKind.Utc);
            Assert.AreEqual(HistoryListModel.DateToday,
                HistoryListModel.GroupKeyFor(Row("s1", earlierToday), HistoryGroupMode.Date, Now));
        }

        [Test]
        public void GroupKeyFor_FutureTimestamp_BucketsAsToday_NeverCrashesOrFallsToOlder()
        {
            DateTime oneDayFuture = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            DateTime farFuture = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(HistoryListModel.DateToday,
                HistoryListModel.GroupKeyFor(Row("s1", oneDayFuture), HistoryGroupMode.Date, Now));
            Assert.AreEqual(HistoryListModel.DateToday,
                HistoryListModel.GroupKeyFor(Row("s2", farFuture), HistoryGroupMode.Date, Now));
        }

        [Test]
        public void GroupKeyFor_PreviousUtcCalendarDate_IsYesterday()
        {
            DateTime lateYesterday = new DateTime(2026, 8, 2, 23, 59, 59, DateTimeKind.Utc);
            Assert.AreEqual(HistoryListModel.DateYesterday,
                HistoryListModel.GroupKeyFor(Row("s1", lateYesterday), HistoryGroupMode.Date, Now));
        }

        [Test]
        public void GroupKeyFor_TwoToSevenDaysAgo_IsLast7_AtBothBoundaries()
        {
            DateTime twoDaysAgo = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            DateTime sevenDaysAgo = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(HistoryListModel.DateLast7,
                HistoryListModel.GroupKeyFor(Row("s1", twoDaysAgo), HistoryGroupMode.Date, Now));
            Assert.AreEqual(HistoryListModel.DateLast7,
                HistoryListModel.GroupKeyFor(Row("s2", sevenDaysAgo), HistoryGroupMode.Date, Now));
        }

        [Test]
        public void GroupKeyFor_EightToThirtyDaysAgo_IsLast30_AtBothBoundaries()
        {
            DateTime eightDaysAgo = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
            DateTime thirtyDaysAgo = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(HistoryListModel.DateLast30,
                HistoryListModel.GroupKeyFor(Row("s1", eightDaysAgo), HistoryGroupMode.Date, Now));
            Assert.AreEqual(HistoryListModel.DateLast30,
                HistoryListModel.GroupKeyFor(Row("s2", thirtyDaysAgo), HistoryGroupMode.Date, Now));
        }

        [Test]
        public void GroupKeyFor_MoreThanThirtyDaysAgo_IsOlder()
        {
            DateTime thirtyOneDaysAgo = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
            DateTime wayBack = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(HistoryListModel.DateOlder,
                HistoryListModel.GroupKeyFor(Row("s1", thirtyOneDaysAgo), HistoryGroupMode.Date, Now));
            Assert.AreEqual(HistoryListModel.DateOlder,
                HistoryListModel.GroupKeyFor(Row("s2", wayBack), HistoryGroupMode.Date, Now));
        }

        // -- GroupKeyFor: Scene / Project / Custom modes -------------------------------

        [Test]
        public void GroupKeyFor_SceneMode_ReturnsRecordedScenePath_OrUngroupedWhenEmpty()
        {
            Assert.AreEqual("Assets/Scenes/Main.unity",
                HistoryListModel.GroupKeyFor(Row("s1", Now, recordedScenePath: "Assets/Scenes/Main.unity"), HistoryGroupMode.Scene, Now));
            Assert.AreEqual(HistoryListModel.UngroupedKey,
                HistoryListModel.GroupKeyFor(Row("s2", Now), HistoryGroupMode.Scene, Now));
        }

        [Test]
        public void GroupKeyFor_ProjectMode_ReturnsCwd_OrUngroupedWhenEmpty()
        {
            Assert.AreEqual("C:/Projects/Foo",
                HistoryListModel.GroupKeyFor(Row("s1", Now, cwd: "C:/Projects/Foo"), HistoryGroupMode.Project, Now));
            Assert.AreEqual(HistoryListModel.UngroupedKey,
                HistoryListModel.GroupKeyFor(Row("s2", Now), HistoryGroupMode.Project, Now));
        }

        [Test]
        public void GroupKeyFor_CustomMode_ReturnsCustomGroupId_OrUngroupedWhenEmpty()
        {
            Assert.AreEqual("group-1",
                HistoryListModel.GroupKeyFor(Row("s1", Now, customGroupId: "group-1"), HistoryGroupMode.Custom, Now));
            Assert.AreEqual(HistoryListModel.UngroupedKey,
                HistoryListModel.GroupKeyFor(Row("s2", Now), HistoryGroupMode.Custom, Now));
        }

        // -- Build rule 1: archived visibility ------------------------------------------

        [Test]
        public void Build_ArchivedRow_HiddenByDefault()
        {
            var rows = new List<HistoryRow> { Row("s1", Now, archived: true) };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, includeArchived: false, nowUtc: Now, limit: 0, hiddenCount: out hiddenCount);
            Assert.AreEqual(0, groups.Count);
            Assert.AreEqual(0, hiddenCount, "Archive filtering is not limit truncation; it must not inflate hiddenCount.");
        }

        [Test]
        public void Build_ArchivedRow_ShownWhenIncludeArchivedTrue()
        {
            var rows = new List<HistoryRow> { Row("s1", Now, archived: true) };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, includeArchived: true, nowUtc: Now, limit: 0, hiddenCount: out hiddenCount);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(1, groups[0].Rows.Count);
            Assert.AreEqual("s1", groups[0].Rows[0].SessionId);
        }

        // -- Build rule 2: search filtering + empty-query passthrough --------------------

        [Test]
        public void Build_QueryFiltersNonMatchingRows()
        {
            var rows = new List<HistoryRow>
            {
                Row("s1", Now, aiTitle: "fix login bug"),
                Row("s2", Now, aiTitle: "unrelated topic")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, "login", HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(1, groups[0].Rows.Count);
            Assert.AreEqual("s1", groups[0].Rows[0].SessionId);
        }

        [Test]
        public void Build_EmptyQuery_PassesEveryRowThrough()
        {
            var rows = new List<HistoryRow>
            {
                Row("s1", Now, aiTitle: "alpha"),
                Row("s2", Now, aiTitle: "beta")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, "", HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);
            int total = 0;
            for (int i = 0; i < groups.Count; i++) total += groups[i].Rows.Count;
            Assert.AreEqual(2, total);
        }

        // -- Build rule 3: pinned leading group --------------------------------------------

        [Test]
        public void Build_PinnedRows_FormLeadingGroup_RegardlessOfMode()
        {
            var rows = new List<HistoryRow>
            {
                Row("pinned-1", Now.AddDays(-1), pinned: true, customGroupId: "g1"),
                Row("normal-1", Now, customGroupId: "g1")
            };
            foreach (HistoryGroupMode mode in new[]
                     {
                         HistoryGroupMode.Date, HistoryGroupMode.Scene,
                         HistoryGroupMode.Project, HistoryGroupMode.Custom
                     })
            {
                int hiddenCount;
                List<HistoryGroup> groups = HistoryListModel.Build(
                    rows, null, mode, false, Now, 0, out hiddenCount);
                Assert.Greater(groups.Count, 0, "mode=" + mode);
                Assert.AreEqual(HistoryListModel.PinnedGroupKey, groups[0].Key, "mode=" + mode);
                Assert.IsTrue(groups[0].IsPinnedGroup, "mode=" + mode);
                Assert.AreEqual(1, groups[0].Rows.Count, "mode=" + mode);
                Assert.AreEqual("pinned-1", groups[0].Rows[0].SessionId, "mode=" + mode);
            }
        }

        [Test]
        public void Build_PinnedRow_NeverAppearsInAnyOtherGroup()
        {
            var rows = new List<HistoryRow>
            {
                Row("pinned-1", Now, pinned: true, customGroupId: "g1"),
                Row("normal-1", Now, customGroupId: "g1")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);

            // Exactly the pinned group, plus exactly one "g1" group holding
            // only the non-pinned row.
            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual(HistoryListModel.PinnedGroupKey, groups[0].Key);
            Assert.AreEqual("g1", groups[1].Key);
            Assert.AreEqual(1, groups[1].Rows.Count);
            Assert.AreEqual("normal-1", groups[1].Rows[0].SessionId);
        }

        [Test]
        public void Build_NoPinnedRows_NoPinnedGroupEmitted()
        {
            var rows = new List<HistoryRow> { Row("s1", Now, customGroupId: "g1") };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);
            Assert.AreEqual(1, groups.Count);
            Assert.AreNotEqual(HistoryListModel.PinnedGroupKey, groups[0].Key);
        }

        // -- Build rule 4: Date mode fixed canonical order ---------------------------------

        [Test]
        public void Build_DateMode_FixedCanonicalOrder_NotSortedByContent()
        {
            // Deliberately inserted out of chronological order to prove the
            // output order comes from the fixed canonical array, not from
            // sorting the buckets by anything.
            var rows = new List<HistoryRow>
            {
                Row("older-1", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Row("today-1", Now),
                Row("last30-1", new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)),
                Row("yesterday-1", new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)),
                Row("last7-1", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Date, false, Now, 0, out hiddenCount);

            Assert.AreEqual(5, groups.Count);
            Assert.AreEqual(HistoryListModel.DateToday, groups[0].Key);
            Assert.AreEqual(HistoryListModel.DateYesterday, groups[1].Key);
            Assert.AreEqual(HistoryListModel.DateLast7, groups[2].Key);
            Assert.AreEqual(HistoryListModel.DateLast30, groups[3].Key);
            Assert.AreEqual(HistoryListModel.DateOlder, groups[4].Key);
        }

        [Test]
        public void Build_DateMode_EmptyBucketsAreSkipped()
        {
            // Only "today" and "older" are populated; yesterday/last7/last30
            // must not appear as empty headers.
            var rows = new List<HistoryRow>
            {
                Row("today-1", Now),
                Row("older-1", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Date, false, Now, 0, out hiddenCount);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual(HistoryListModel.DateToday, groups[0].Key);
            Assert.AreEqual(HistoryListModel.DateOlder, groups[1].Key);
        }

        // -- Build rule 4: non-Date modes ordered by newest member, ungrouped last --------

        [Test]
        public void Build_NonDateMode_GroupsOrderedByNewestMemberDescending()
        {
            var rows = new List<HistoryRow>
            {
                Row("proj-a-1", Now.AddDays(-5), cwd: "proj-a"),
                Row("proj-b-1", Now, cwd: "proj-b"),
                Row("proj-c-1", Now.AddDays(-1), cwd: "proj-c")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Project, false, Now, 0, out hiddenCount);

            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual("proj-b", groups[0].Key); // newest (Now)
            Assert.AreEqual("proj-c", groups[1].Key); // Now - 1 day
            Assert.AreEqual("proj-a", groups[2].Key); // Now - 5 days (oldest)
        }

        [Test]
        public void Build_NonDateMode_UngroupedKey_AlwaysLast_EvenIfMostRecent()
        {
            var rows = new List<HistoryRow>
            {
                Row("ungrouped-1", Now, cwd: ""), // most recent of all, but ungrouped
                Row("proj-a-1", Now.AddDays(-10), cwd: "proj-a")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Project, false, Now, 0, out hiddenCount);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("proj-a", groups[0].Key);
            Assert.AreEqual(HistoryListModel.UngroupedKey, groups[1].Key);
        }

        [Test]
        public void Build_NonDateMode_EqualNewestMember_TieBreaksByKeyOrdinal_Deterministic()
        {
            var rows = new List<HistoryRow>
            {
                Row("b-1", Now, customGroupId: "group-b"),
                Row("a-1", Now, customGroupId: "group-a")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("group-a", groups[0].Key);
            Assert.AreEqual("group-b", groups[1].Key);
        }

        // -- Build rule 5: deterministic within-group sort ---------------------------------

        [Test]
        public void Build_RowsWithinGroup_SortByLastModifiedDescending()
        {
            var rows = new List<HistoryRow>
            {
                Row("old", Now.AddDays(-2), customGroupId: "g1"),
                Row("new", Now, customGroupId: "g1"),
                Row("mid", Now.AddDays(-1), customGroupId: "g1")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);

            Assert.AreEqual(1, groups.Count);
            List<HistoryRow> sorted = groups[0].Rows;
            Assert.AreEqual("new", sorted[0].SessionId);
            Assert.AreEqual("mid", sorted[1].SessionId);
            Assert.AreEqual("old", sorted[2].SessionId);
        }

        [Test]
        public void Build_RowsWithinGroup_EqualTimestamps_TieBreakBySessionIdOrdinalAscending_Deterministic()
        {
            var rows = new List<HistoryRow>
            {
                Row("zeta", Now, customGroupId: "g1"),
                Row("alpha", Now, customGroupId: "g1"),
                Row("mike", Now, customGroupId: "g1")
            };
            int hiddenCount;

            // Run twice: determinism means the same input always yields the
            // same output order, not just "some" order.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                List<HistoryGroup> groups = HistoryListModel.Build(
                    rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);
                List<HistoryRow> sorted = groups[0].Rows;
                Assert.AreEqual("alpha", sorted[0].SessionId);
                Assert.AreEqual("mike", sorted[1].SessionId);
                Assert.AreEqual("zeta", sorted[2].SessionId);
            }
        }

        // -- Build rule 6: limit / hiddenCount ---------------------------------------------

        [Test]
        public void Build_LimitZero_MeansUnlimited()
        {
            var rows = new List<HistoryRow>
            {
                Row("s1", Now, customGroupId: "g1"),
                Row("s2", Now.AddDays(-1), customGroupId: "g1"),
                Row("s3", Now.AddDays(-2), customGroupId: "g1")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);
            Assert.AreEqual(3, groups[0].Rows.Count);
            Assert.AreEqual(0, hiddenCount);
        }

        [Test]
        public void Build_LimitEqualToRowCount_HidesNothing()
        {
            var rows = new List<HistoryRow>
            {
                Row("s1", Now, customGroupId: "g1"),
                Row("s2", Now.AddDays(-1), customGroupId: "g1"),
                Row("s3", Now.AddDays(-2), customGroupId: "g1")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 3, out hiddenCount);
            Assert.AreEqual(3, groups[0].Rows.Count);
            Assert.AreEqual(0, hiddenCount);
        }

        [Test]
        public void Build_LimitOneLessThanRowCount_HidesExactlyOne_CutsTheOldestRow()
        {
            var rows = new List<HistoryRow>
            {
                Row("newest", Now, customGroupId: "g1"),
                Row("middle", Now.AddDays(-1), customGroupId: "g1"),
                Row("oldest", Now.AddDays(-2), customGroupId: "g1")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 2, out hiddenCount);
            Assert.AreEqual(1, hiddenCount);
            Assert.AreEqual(2, groups[0].Rows.Count);
            Assert.AreEqual("newest", groups[0].Rows[0].SessionId);
            Assert.AreEqual("middle", groups[0].Rows[1].SessionId);
        }

        [Test]
        public void Build_LimitCutsAcrossGroups_DropsGroupsWithNoRemainingCapacityEntirely()
        {
            var rows = new List<HistoryRow>
            {
                Row("a1", Now, cwd: "proj-a"),
                Row("a2", Now.AddDays(-1), cwd: "proj-a"),
                Row("b1", Now.AddDays(-2), cwd: "proj-b")
            };
            int hiddenCount;
            // Newest-first group order is proj-a (newest member = Now) then
            // proj-b. limit=1 should keep only "a1" and drop "a2" and the
            // entire proj-b group -- a group left with zero rows must not
            // be returned at all.
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Project, false, Now, 1, out hiddenCount);

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("proj-a", groups[0].Key);
            Assert.AreEqual(1, groups[0].Rows.Count);
            Assert.AreEqual("a1", groups[0].Rows[0].SessionId);
            Assert.AreEqual(2, hiddenCount);
        }

        [Test]
        public void Build_HiddenCount_OnlyCountsLimitCutRows_NotArchivedOrQueryFiltered()
        {
            var rows = new List<HistoryRow>
            {
                Row("kept", Now, customGroupId: "g1"),
                Row("archived-out", Now.AddDays(-1), customGroupId: "g1", archived: true),
                Row("query-out", Now.AddDays(-2), customGroupId: "g1", aiTitle: "does not match")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, "kept", HistoryGroupMode.Custom, includeArchived: false, nowUtc: Now, limit: 5, hiddenCount: out hiddenCount);
            Assert.AreEqual(1, groups[0].Rows.Count);
            Assert.AreEqual(0, hiddenCount, "Only 1 row survives filtering and limit=5 has room for it; nothing was cut by paging.");
        }

        // -- Build rule 7: never null, tolerant of empty/null input ------------------------

        [Test]
        public void Build_EmptyRowList_ReturnsEmptyGroupsAndZeroHidden()
        {
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                new List<HistoryRow>(), null, HistoryGroupMode.Date, false, Now, 0, out hiddenCount);
            Assert.IsNotNull(groups);
            Assert.AreEqual(0, groups.Count);
            Assert.AreEqual(0, hiddenCount);
        }

        [Test]
        public void Build_NullRowList_ReturnsEmptyGroupsAndZeroHidden_NeverThrows()
        {
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                null, null, HistoryGroupMode.Date, false, Now, 0, out hiddenCount);
            Assert.IsNotNull(groups);
            Assert.AreEqual(0, groups.Count);
            Assert.AreEqual(0, hiddenCount);
        }

        [Test]
        public void Build_NullEntriesInsideRowList_AreSkipped_NeverThrows()
        {
            var rows = new List<HistoryRow>
            {
                Row("s1", Now, customGroupId: "g1"),
                null,
                Row("s2", Now.AddDays(-1), customGroupId: "g1")
            };
            int hiddenCount;
            List<HistoryGroup> groups = HistoryListModel.Build(
                rows, null, HistoryGroupMode.Custom, false, Now, 0, out hiddenCount);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(2, groups[0].Rows.Count);
            Assert.AreEqual(0, hiddenCount);
        }
    }
}
