using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The error chip's fix button must send EXACTLY the errors the chip
    /// is showing (docs/design-notes/2026-09-07-scene-markers-image-
    /// attachments-error-chip.md section 3). The chip counts
    /// visible − acknowledged − dismissed-for-now
    /// (ContextBarView.CountUnacknowledgedVisible); the button used to send
    /// FormatDigest() over ALL visible entries, so already-sent and
    /// dismissed errors went out again, the attachment title claimed the
    /// wrong count, and -- because the digest keeps the OLDEST ten -- a new
    /// error behind ten acknowledged ones was never sent at all while the
    /// chip kept saying "1 console error".
    /// </summary>
    [TestFixture]
    public class ContextBarViewFixSendConsistencyTests
    {
        private PanelSettings _settings;

        [SetUp]
        public void SetUp()
        {
            ConsoleErrorProvider.ResetForTests();
            _settings = new PanelSettings();
            ConsoleErrorProvider.SettingsSourceForTests = delegate { return _settings; };
        }

        [TearDown]
        public void TearDown()
        {
            ConsoleErrorProvider.SettingsSourceForTests = null;
            ConsoleErrorProvider.ResetForTests();
        }

        private static void Capture(string message)
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests(message, "at X:1", LogType.Error);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
        }

        private static ConsoleErrorProvider.Entry E(string message)
        {
            return new ConsoleErrorProvider.Entry { Message = message, Occurrences = 1 };
        }

        [Test]
        public void SelectSendable_ExcludesAcknowledgedAndDismissed_KeepsOrder()
        {
            var visible = new List<ConsoleErrorProvider.Entry> { E("old-sent"), E("new-1"), E("closed"), E("new-2") };
            var acknowledged = new HashSet<string>(StringComparer.Ordinal) { "old-sent" };
            var dismissed = new HashSet<string>(StringComparer.Ordinal) { "closed" };

            List<ConsoleErrorProvider.Entry> sendable =
                ContextBarView.SelectSendable(visible, acknowledged, dismissed);

            Assert.AreEqual(2, sendable.Count);
            Assert.AreEqual("new-1", sendable[0].Message);
            Assert.AreEqual("new-2", sendable[1].Message);
            Assert.AreEqual(ContextBarView.CountUnacknowledgedVisible(visible, acknowledged, dismissed),
                sendable.Count, "what is sent and what the chip counts are the same set");
        }

        [Test]
        public void SelectSendable_NullSets_ReturnEveryVisibleEntry()
        {
            var visible = new List<ConsoleErrorProvider.Entry> { E("a"), E("b") };
            Assert.AreEqual(2, ContextBarView.SelectSendable(visible, null, null).Count);
            Assert.AreEqual(0, ContextBarView.SelectSendable(null, null, null).Count);
        }

        [Test]
        public void FixPayload_NewErrorBehindTenAcknowledgedOnes_IsTheOneSent()
        {
            var acknowledged = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ConsoleErrorProvider.DefaultDigestEntries; i++)
            {
                Capture("old-" + i);
                acknowledged.Add("old-" + i);
            }
            Capture("NEW: NullReferenceException in Player.Update");
            ConsoleErrorProvider.Entry[] visible = ConsoleErrorProvider.VisibleSnapshot();
            Assert.AreEqual(1, ContextBarView.CountUnacknowledgedVisible(visible, acknowledged, null),
                "precondition: the chip says 1 console error");

            List<ConsoleErrorProvider.Entry> sendable = ContextBarView.SelectSendable(visible, acknowledged, null);
            string digest = ConsoleErrorProvider.FormatDigest(sendable, ConsoleErrorProvider.DefaultDigestEntries);

            StringAssert.Contains("NEW: NullReferenceException", digest,
                "the error the chip is showing must be in the payload");
            StringAssert.DoesNotContain("old-0", digest, "an already-sent error must not be re-sent");
            StringAssert.Contains("(1 distinct)", digest, "the digest header counts what is sent");
        }

        [Test]
        public void FixPayload_AcknowledgesOnlyWhatWasSent()
        {
            var visible = new List<ConsoleErrorProvider.Entry> { E("old-sent"), E("new-1") };
            var acknowledged = new HashSet<string>(StringComparer.Ordinal) { "old-sent" };
            var dismissed = new HashSet<string>(StringComparer.Ordinal);

            List<ConsoleErrorProvider.Entry> sendable = ContextBarView.SelectSendable(visible, acknowledged, dismissed);
            ContextBarView.AcknowledgeSentMessages(sendable, acknowledged);

            Assert.IsTrue(acknowledged.Contains("new-1"));
            Assert.AreEqual(0, ContextBarView.CountUnacknowledgedVisible(visible, acknowledged, dismissed),
                "after sending, the chip hides -- nothing unsent remains");
        }
    }
}
