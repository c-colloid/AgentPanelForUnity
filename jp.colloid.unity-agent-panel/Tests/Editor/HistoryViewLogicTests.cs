using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guards for HistoryView's row captions: relative-time
    /// formatting (nowUtc is always a parameter, never DateTime.UtcNow
    /// read internally, so these are exact and never flaky) and human
    /// file-size formatting.
    /// </summary>
    public class HistoryViewLogicTests
    {
        private static readonly DateTime Now = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// FormatRelativeTime/FormatSize now read L10n.S (docs/design-notes/
        /// 2026-08-01-i18n.md): pin English explicitly so these assertions
        /// never depend on this machine's Japanese OS language or on test
        /// execution order relative to any test that applies a different
        /// language.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            L10n.OverrideForTests(PanelLanguage.English);
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
        }

        // -- FormatRelativeTime ---------------------------------------------------------

        [Test]
        public void FormatRelativeTime_UnderFortyFiveSeconds_IsJustNow()
        {
            Assert.AreEqual(L10n.S.HistoryRelativeJustNow, HistoryView.FormatRelativeTime(Now, Now));
            Assert.AreEqual(L10n.S.HistoryRelativeJustNow, HistoryView.FormatRelativeTime(Now, Now.AddSeconds(-44)));
        }

        [Test]
        public void FormatRelativeTime_AboutOneMinute_IsSingular()
        {
            Assert.AreEqual(L10n.S.HistoryRelativeOneMinuteAgo, HistoryView.FormatRelativeTime(Now, Now.AddSeconds(-60)));
        }

        [Test]
        public void FormatRelativeTime_SeveralMinutes_IsPluralWithCount()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistoryRelativeMinutesAgoFmt, 5), HistoryView.FormatRelativeTime(Now, Now.AddMinutes(-5)));
            Assert.AreEqual(L10n.F(L10n.S.HistoryRelativeMinutesAgoFmt, 44), HistoryView.FormatRelativeTime(Now, Now.AddMinutes(-44)));
        }

        [Test]
        public void FormatRelativeTime_AboutOneHour_IsSingular()
        {
            Assert.AreEqual(L10n.S.HistoryRelativeOneHourAgo, HistoryView.FormatRelativeTime(Now, Now.AddMinutes(-60)));
        }

        [Test]
        public void FormatRelativeTime_SeveralHours_IsPluralWithCount()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistoryRelativeHoursAgoFmt, 3), HistoryView.FormatRelativeTime(Now, Now.AddHours(-3)));
        }

        [Test]
        public void FormatRelativeTime_AboutOneDay_IsSingular()
        {
            Assert.AreEqual(L10n.S.HistoryRelativeOneDayAgo, HistoryView.FormatRelativeTime(Now, Now.AddHours(-24)));
        }

        [Test]
        public void FormatRelativeTime_SeveralDays_IsPluralWithCount()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistoryRelativeDaysAgoFmt, 5), HistoryView.FormatRelativeTime(Now, Now.AddDays(-5)));
        }

        [Test]
        public void FormatRelativeTime_AboutOneMonth_IsSingular()
        {
            Assert.AreEqual(L10n.S.HistoryRelativeOneMonthAgo, HistoryView.FormatRelativeTime(Now, Now.AddDays(-30)));
        }

        [Test]
        public void FormatRelativeTime_SeveralMonths_IsPluralWithCount()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistoryRelativeMonthsAgoFmt, 3), HistoryView.FormatRelativeTime(Now, Now.AddDays(-90)));
        }

        [Test]
        public void FormatRelativeTime_AboutOneYear_IsSingular()
        {
            Assert.AreEqual(L10n.S.HistoryRelativeOneYearAgo, HistoryView.FormatRelativeTime(Now, Now.AddDays(-365)));
        }

        [Test]
        public void FormatRelativeTime_SeveralYears_IsPluralWithCount()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistoryRelativeYearsAgoFmt, 2), HistoryView.FormatRelativeTime(Now, Now.AddDays(-730)));
        }

        [Test]
        public void FormatRelativeTime_FutureTimestamp_ClampsToJustNow_ClockSkewGuard()
        {
            // A file mtime slightly ahead of DateTime.UtcNow (clock skew,
            // filesystem timestamp rounding) must never render as a
            // negative duration.
            Assert.AreEqual(L10n.S.HistoryRelativeJustNow, HistoryView.FormatRelativeTime(Now, Now.AddSeconds(30)));
            Assert.AreEqual(L10n.S.HistoryRelativeJustNow, HistoryView.FormatRelativeTime(Now, Now.AddDays(1)));
        }

        // -- FormatSize -------------------------------------------------------------

        [Test]
        public void FormatSize_Bytes_UnderOneKilobyte()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeBytesFmt, "0"), HistoryView.FormatSize(0));
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeBytesFmt, "512"), HistoryView.FormatSize(512));
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeBytesFmt, "1023"), HistoryView.FormatSize(1023));
        }

        [Test]
        public void FormatSize_Kilobytes_OneDecimalPlace()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeKbFmt, "1"), HistoryView.FormatSize(1024));
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeKbFmt, "3.4"), HistoryView.FormatSize(3482));
        }

        [Test]
        public void FormatSize_Megabytes_OneDecimalPlace()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeMbFmt, "1"), HistoryView.FormatSize(1024L * 1024L));
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeMbFmt, "2.5"), HistoryView.FormatSize((long)(2.5 * 1024 * 1024)));
        }

        [Test]
        public void FormatSize_NegativeBytes_ClampsToZero()
        {
            Assert.AreEqual(L10n.F(L10n.S.HistorySizeBytesFmt, "0"), HistoryView.FormatSize(-1));
        }

        // -- Row-action and refresh guards (v0.15.1) -------------------------
        //
        // Both rules below were raised by an adversarial review of the
        // v0.15.0 history rework. They live as pure methods purely so they
        // can be pinned here: neither a GenericMenu nor a scheduler tick is
        // reachable from an EditMode test.

        [Test]
        public void ShouldDeferRefresh_WhileRenaming_DefersSoTypingIsNotDiscarded()
        {
            // A queued refresh recreates every row, and with it the rename
            // TextField and whatever was half-typed in it. This is reachable
            // with the user's hands on the keyboard and no user action at
            // all: a turn completing anywhere in the panel records the
            // active scene, which saves session metadata, which raises
            // Changed, which dirties the history view.
            Assert.IsTrue(HistoryView.ShouldDeferRefresh("session-a", string.Empty));
        }

        [Test]
        public void ShouldDeferRefresh_WhileNamingANewGroup_Defers()
        {
            Assert.IsTrue(HistoryView.ShouldDeferRefresh(string.Empty, "session-a"));
        }

        [Test]
        public void ShouldDeferRefresh_NoEditorOpen_RefreshesImmediately()
        {
            Assert.IsFalse(HistoryView.ShouldDeferRefresh(string.Empty, string.Empty));
            Assert.IsFalse(HistoryView.ShouldDeferRefresh(null, null));
        }

        [Test]
        public void CanDeleteRow_ForeignSession_IsNotOffered()
        {
            // Deleting another project's transcript from this project's
            // panel would move a file the user is not looking at into THIS
            // project's UserSettings folder.
            Assert.IsFalse(HistoryView.CanDeleteRow(true, false));
        }

        [Test]
        public void CanDeleteRow_LiveSession_IsNotOffered()
        {
            // The CLI still has that transcript open and is appending to it.
            // The move happens to fail with a sharing violation on Windows
            // today, but that is the file system saving us rather than a
            // decision this view made -- so the operation is not offered.
            Assert.IsFalse(HistoryView.CanDeleteRow(false, true));
        }

        [Test]
        public void CanDeleteRow_OrdinaryLocalSession_IsOffered()
        {
            Assert.IsTrue(HistoryView.CanDeleteRow(false, false));
        }

        // -- Row/group tooltips (2026-08-14 ui-polish audit item 7) -----------------
        //
        // HistoryView.BuildRowForTests / BuildGroupHeaderForTests forward to
        // the otherwise-private row/header builders (InternalsVisibleTo,
        // same idiom as ToolActivityCard/MessageListController's *ForTests
        // seams) so the returned VisualElement can be inspected directly. A
        // detached element's .tooltip property is set synchronously by the
        // builder and is safe to assert -- dispatching a TooltipEvent
        // against a detached element is what resolves empty (2026-08-05
        // measurements), so these tests deliberately read the property
        // instead of simulating a hover.

        [Test]
        public void BuildRow_MainBody_IsAKeyboardTarget()
        {
            // UXIA-1: the row body must be reachable by Tab and carry the
            // focus ring class hook. Detached elements deliver no key
            // events (2026-08-05 measurements), so the focusable/tabIndex
            // properties are what a test can and should pin -- the
            // Enter/Space handler shares OnRowClicked with the click path.
            var view = new HistoryView();
            var row = new HistoryRow
            {
                SessionId = "session-kbd",
                AiTitle = "any",
                LastModifiedUtc = Now
            };

            VisualElement built = view.BuildRowForTests(row, Now);

            VisualElement main = built.Q<VisualElement>(className: "uap-history-row-main");
            Assert.IsNotNull(main, "row must render the main click/keyboard target");
            Assert.IsTrue(main.focusable, "Tab must be able to reach the row body");
            Assert.GreaterOrEqual(main.tabIndex, 0, "the row must participate in tab order");
        }

        // -- Badge colour vocabulary (2026-09-05 UI redesign D7) --------------------

        [Test]
        public void BuildRow_PinnedAndArchivedBadges_CarryTheirOwnModifiers()
        {
            var view = new HistoryView();
            var row = new HistoryRow
            {
                SessionId = "session-badges",
                AiTitle = "Badged",
                Pinned = true,
                Archived = true,
                LastModifiedUtc = Now
            };

            VisualElement built = view.BuildRowForTests(row, Now);

            Label pinned = built.Q<Label>(className: HistoryView.BadgePinnedClass);
            Label archived = built.Q<Label>(className: HistoryView.BadgeArchivedClass);
            Assert.IsNotNull(pinned, "a pinned row must render the pinned badge modifier");
            Assert.IsNotNull(archived, "an archived row must render the archived badge modifier");
            Assert.IsTrue(pinned.ClassListContains("uap-history-row-badge"),
                "modifiers sit on top of the shared badge shell");
            Assert.AreEqual(L10n.S.HistoryGroupPinned, pinned.text);
            Assert.AreEqual(L10n.S.HistoryActionArchive, archived.text);
            Assert.IsNull(built.Q<Label>(className: HistoryView.BadgeCurrentClass),
                "a row that is not the live session must not claim the Current badge");
        }

        [Test]
        public void BuildRow_PlainRow_HasNoBadges()
        {
            var view = new HistoryView();
            var row = new HistoryRow
            {
                SessionId = "session-plain",
                AiTitle = "Plain",
                LastModifiedUtc = Now
            };

            VisualElement built = view.BuildRowForTests(row, Now);

            Assert.IsNull(built.Q<Label>(className: "uap-history-row-badge"));
        }

        [Test]
        public void BuildRow_TitleLabel_TooltipMirrorsSanitizedDisplayText()
        {
            var view = new HistoryView();
            var row = new HistoryRow
            {
                SessionId = "session-title-tooltip",
                AiTitle = "A very long ai-generated title that would overflow the row",
                LastModifiedUtc = Now
            };

            VisualElement built = view.BuildRowForTests(row, Now);

            Label titleLabel = built.Q<Label>(className: "uap-history-row-title");
            Assert.IsNotNull(titleLabel, "row must render a title label");
            Assert.IsFalse(string.IsNullOrEmpty(titleLabel.tooltip),
                "a long title with no on-screen ellipsis affordance needs a tooltip");
            Assert.AreEqual(titleLabel.text, titleLabel.tooltip,
                "tooltip must mirror the exact sanitized text shown, not the raw source string");
        }

        [Test]
        public void BuildRow_SubtitleLabel_TooltipMirrorsSanitizedPreviewText()
        {
            var view = new HistoryView();
            var row = new HistoryRow
            {
                SessionId = "session-subtitle-tooltip",
                AiTitle = "Distinct Title",
                Preview = "First user message preview, different from the title",
                LastModifiedUtc = Now
            };

            VisualElement built = view.BuildRowForTests(row, Now);

            Label subLabel = built.Q<Label>(className: "uap-history-row-subtitle");
            Assert.IsNotNull(subLabel,
                "row must render a subtitle label when the preview differs from the title");
            Assert.IsFalse(string.IsNullOrEmpty(subLabel.tooltip));
            Assert.AreEqual(subLabel.text, subLabel.tooltip);
        }

        [Test]
        public void BuildGroupHeader_DateMode_TooltipMirrorsItsOwnLabelText()
        {
            var view = new HistoryView();
            var group = new HistoryGroup { Key = HistoryListModel.DateToday, Rows = new List<HistoryRow>() };

            VisualElement built = view.BuildGroupHeaderForTests(group, HistoryGroupMode.Date);

            var header = built as Label;
            Assert.IsNotNull(header, "group header must be a Label");
            Assert.IsFalse(string.IsNullOrEmpty(header.tooltip));
            Assert.AreEqual(header.text, header.tooltip);
        }

        [Test]
        public void BuildGroupHeader_ProjectMode_TooltipMirrorsSanitizedProjectName()
        {
            var view = new HistoryView();
            var group = new HistoryGroup { Key = "C:/Projects/SomeGame", Rows = new List<HistoryRow>() };

            VisualElement built = view.BuildGroupHeaderForTests(group, HistoryGroupMode.Project);

            var header = built as Label;
            Assert.IsNotNull(header);
            Assert.AreEqual("SomeGame", header.text);
            Assert.AreEqual(header.text, header.tooltip);
        }

        // -- UXIA-5: the row meta line carries the model ------------------

        [Test]
        public void FormatRowMeta_WithModel_AppendsItAfterSize()
        {
            string meta = HistoryView.FormatRowMeta("2h ago", "1.7k", "opus-5");
            StringAssert.StartsWith("2h ago", meta);
            StringAssert.Contains("1.7k", meta);
            StringAssert.EndsWith("opus-5", meta);
        }

        [Test]
        public void FormatRowMeta_NoModel_HasNoDanglingBullet()
        {
            string withoutModel = HistoryView.FormatRowMeta("2h ago", "1.7k", null);
            string alsoEmpty = HistoryView.FormatRowMeta("2h ago", "1.7k", string.Empty);
            StringAssert.EndsWith("1.7k", withoutModel);
            Assert.AreEqual(withoutModel, alsoEmpty);
        }

    }
}
