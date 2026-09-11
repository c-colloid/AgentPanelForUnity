using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pins the chip-visibility decision that replaced the count-based
    /// dismiss (docs/design-notes/2026-08-13-error-chip-ignore.md
    /// "Measured root cause"): visibility is keyed by MESSAGE CONTENT, so
    /// removing entries -- what OnCompilationStarted does to superseded
    /// compiler errors on every compile -- can only ever shrink the
    /// unacknowledged count. Under the old `_errorDismissedAtCount != count`
    /// rule the same removal changed the count and RESURRECTED the chip
    /// with stale errors; the first test here fails against that behavior.
    /// </summary>
    [TestFixture]
    public class ContextBarViewLogicTests
    {
        private static ConsoleErrorProvider.Entry E(string message)
        {
            return new ConsoleErrorProvider.Entry { Message = message, Occurrences = 1 };
        }

        [Test]
        public void CountUnacknowledgedVisible_EntryRemovalAfterAcknowledge_StaysZero()
        {
            var acknowledged = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };
            var afterCompileDrop = new List<ConsoleErrorProvider.Entry> { E("a"), E("b") };

            Assert.AreEqual(0, ContextBarView.CountUnacknowledgedVisible(afterCompileDrop, acknowledged),
                "dropping entries (compile start) must never resurrect an acknowledged chip");
        }

        [Test]
        public void CountUnacknowledgedVisible_NewDistinctMessage_CountsOnlyTheNewOne()
        {
            var acknowledged = new HashSet<string>(StringComparer.Ordinal) { "a", "b" };
            var entries = new List<ConsoleErrorProvider.Entry> { E("a"), E("b"), E("fresh") };

            Assert.AreEqual(1, ContextBarView.CountUnacknowledgedVisible(entries, acknowledged));
        }

        [Test]
        public void CountUnacknowledgedVisible_NothingAcknowledged_CountsAll()
        {
            var entries = new List<ConsoleErrorProvider.Entry> { E("a"), E("b") };

            Assert.AreEqual(2, ContextBarView.CountUnacknowledgedVisible(
                entries, new HashSet<string>(StringComparer.Ordinal)));
        }

        [Test]
        public void CountUnacknowledgedVisible_NullOrEmptyEntries_Zero()
        {
            Assert.AreEqual(0, ContextBarView.CountUnacknowledgedVisible(
                null, new HashSet<string>(StringComparer.Ordinal)));
            Assert.AreEqual(0, ContextBarView.CountUnacknowledgedVisible(
                new List<ConsoleErrorProvider.Entry>(), new HashSet<string>(StringComparer.Ordinal)));
        }

        [Test]
        public void CountUnacknowledgedVisible_NullAcknowledgedSet_CountsAll()
        {
            var entries = new List<ConsoleErrorProvider.Entry> { E("a") };

            Assert.AreEqual(1, ContextBarView.CountUnacknowledgedVisible(entries, null));
        }

        [Test]
        public void AcknowledgeSentMessages_CapsAtTheDigestEntryLimit()
        {
            var visible = new List<ConsoleErrorProvider.Entry>();
            for (int i = 0; i < ConsoleErrorProvider.DefaultDigestEntries + 2; i++)
            {
                visible.Add(E("err" + i));
            }
            var acknowledged = new HashSet<string>(StringComparer.Ordinal);

            ContextBarView.AcknowledgeSentMessages(visible, acknowledged);

            Assert.AreEqual(ConsoleErrorProvider.DefaultDigestEntries, acknowledged.Count,
                "an error beyond FormatDigest's cap was never sent, so acknowledging it"
                + " would hide the chip for something Claude never saw");
            Assert.IsTrue(acknowledged.Contains("err0"), "the cap keeps the OLDEST entries, matching the digest");
            Assert.IsFalse(acknowledged.Contains("err" + (ConsoleErrorProvider.DefaultDigestEntries + 1)));
        }

        [Test]
        public void AcknowledgeSentMessages_NullInputs_DoNotThrow()
        {
            ContextBarView.AcknowledgeSentMessages(null, new HashSet<string>(StringComparer.Ordinal));
            ContextBarView.AcknowledgeSentMessages(new List<ConsoleErrorProvider.Entry>(), null);
        }

        // ------------------------------------------------------------------
        // UXO-3: the bare X is dismiss-for-now -- transient, uncapped,
        // never persisted.
        // ------------------------------------------------------------------

        [Test]
        public void DismissVisibleForNow_HidesExactlyTheVisibleMessages()
        {
            var visible = new List<ConsoleErrorProvider.Entry> { E("a"), E("b") };
            var dismissed = new HashSet<string>(StringComparer.Ordinal);

            ContextBarView.DismissVisibleForNow(visible, dismissed);

            Assert.AreEqual(0, ContextBarView.CountUnacknowledgedVisible(
                visible, new HashSet<string>(StringComparer.Ordinal), dismissed),
                "everything the X covered must be hidden");
        }

        [Test]
        public void DismissVisibleForNow_NewDistinctMessage_ShowsTheChipAgain()
        {
            var dismissed = new HashSet<string>(StringComparer.Ordinal);
            ContextBarView.DismissVisibleForNow(
                new List<ConsoleErrorProvider.Entry> { E("a") }, dismissed);

            var withFresh = new List<ConsoleErrorProvider.Entry> { E("a"), E("fresh") };

            Assert.AreEqual(1, ContextBarView.CountUnacknowledgedVisible(
                withFresh, new HashSet<string>(StringComparer.Ordinal), dismissed),
                "dismiss-for-now must not swallow errors that appear later");
        }

        [Test]
        public void DismissVisibleForNow_HasNoDigestCap()
        {
            // Unlike AcknowledgeSentMessages (which mirrors what was
            // actually SENT), the X covers the whole visible set.
            var visible = new List<ConsoleErrorProvider.Entry>();
            for (int i = 0; i < ConsoleErrorProvider.DefaultDigestEntries + 2; i++)
            {
                visible.Add(E("err" + i));
            }
            var dismissed = new HashSet<string>(StringComparer.Ordinal);

            ContextBarView.DismissVisibleForNow(visible, dismissed);

            Assert.AreEqual(visible.Count, dismissed.Count);
        }

        [Test]
        public void DismissVisibleForNow_NullInputs_DoNotThrow()
        {
            ContextBarView.DismissVisibleForNow(null, new HashSet<string>(StringComparer.Ordinal));
            ContextBarView.DismissVisibleForNow(new List<ConsoleErrorProvider.Entry>(), null);
        }

        [Test]
        public void CountUnacknowledgedVisible_EitherSetHides()
        {
            var entries = new List<ConsoleErrorProvider.Entry> { E("a"), E("b"), E("c") };
            var acknowledged = new HashSet<string>(StringComparer.Ordinal) { "a" };
            var dismissed = new HashSet<string>(StringComparer.Ordinal) { "b" };

            Assert.AreEqual(1, ContextBarView.CountUnacknowledgedVisible(
                entries, acknowledged, dismissed));
        }
    }
}
