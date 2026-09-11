using System;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Provider-side visibility filtering (docs/design-notes/2026-08-13-
    /// error-chip-ignore.md): VisibleSnapshot/VisibleCount/FormatDigest
    /// hide ignored entries WITHOUT deleting the raw captured entry, so
    /// un-ignoring restores it with no re-log. Settings are injected via
    /// the SettingsSourceForTests seam -- these tests never touch the
    /// sandbox project's real PanelStateStore asset.
    /// </summary>
    [TestFixture]
    public class ConsoleErrorVisibilityTests
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

        [Test]
        public void VisibleSnapshot_ExactIgnoredMessage_IsExcludedButStaysCaptured()
        {
            Capture("SdkNoise: harmless");
            Capture("RealBug: fix me");
            _settings.ignoredConsoleErrors.Add("SdkNoise: harmless");

            ConsoleErrorProvider.Entry[] visible = ConsoleErrorProvider.VisibleSnapshot();

            Assert.AreEqual(1, visible.Length);
            Assert.AreEqual("RealBug: fix me", visible[0].Message);
            Assert.AreEqual(2, ConsoleErrorProvider.Count,
                "the raw capture list must keep the ignored entry (un-ignore restores it)");
            Assert.AreEqual(1, ConsoleErrorProvider.VisibleCount);
        }

        [Test]
        public void VisibleSnapshot_PatternMatch_IsExcluded()
        {
            Capture("InvalidOperationException: SomeSdk.Internal.Widget bind failed (id 42)");
            Capture("RealBug: fix me");
            _settings.ignoredConsoleErrorPatterns = "SomeSdk.Internal\n";

            ConsoleErrorProvider.Entry[] visible = ConsoleErrorProvider.VisibleSnapshot();

            Assert.AreEqual(1, visible.Length);
            Assert.AreEqual("RealBug: fix me", visible[0].Message);
        }

        [Test]
        public void FormatDigest_ExcludesIgnoredEntries()
        {
            Capture("SdkNoise: harmless");
            Capture("RealBug: fix me");
            _settings.ignoredConsoleErrors.Add("SdkNoise: harmless");

            string digest = ConsoleErrorProvider.FormatDigest();

            StringAssert.Contains("RealBug: fix me", digest);
            StringAssert.DoesNotContain("SdkNoise", digest);
        }

        [Test]
        public void FormatDigest_EverythingIgnored_ReturnsNull()
        {
            Capture("SdkNoise: harmless");
            _settings.ignoredConsoleErrors.Add("SdkNoise: harmless");

            Assert.IsNull(ConsoleErrorProvider.FormatDigest(),
                "an all-ignored capture set must read as 'nothing to fix', not an empty-bodied digest");
        }

        [Test]
        public void RemovingIgnore_RestoresEntry_WithoutRelogging()
        {
            Capture("SdkNoise: harmless");
            _settings.ignoredConsoleErrors.Add("SdkNoise: harmless");
            Assert.AreEqual(0, ConsoleErrorProvider.VisibleCount);

            _settings.ignoredConsoleErrors.Clear();

            Assert.AreEqual(1, ConsoleErrorProvider.VisibleCount,
                "un-ignoring must restore visibility from the intact raw list, with no new log event");
        }

        [Test]
        public void IgnoreCurrentlyVisible_PersistsEveryVisibleMessage_AndHidesThem()
        {
            Capture("SdkNoise: harmless");
            Capture("OtherNoise: also harmless");
            bool raised = false;
            Action handler = delegate { raised = true; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.IgnoreCurrentlyVisible();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            CollectionAssert.AreEquivalent(
                new[] { "SdkNoise: harmless", "OtherNoise: also harmless" },
                _settings.ignoredConsoleErrors,
                "the X press must persist the exact visible message set");
            Assert.AreEqual(0, ConsoleErrorProvider.VisibleCount);
            Assert.AreEqual(2, ConsoleErrorProvider.Count, "raw entries must survive the ignore");
            Assert.IsTrue(raised, "consumers refresh off Changed; the X press must raise it");
        }

        [Test]
        public void IgnoreCurrentlyVisible_AlreadyIgnoredEntriesOnly_LeavesStoreUntouched()
        {
            Capture("SdkNoise: harmless");
            _settings.ignoredConsoleErrors.Add("SdkNoise: harmless");

            ConsoleErrorProvider.IgnoreCurrentlyVisible();

            Assert.AreEqual(1, _settings.ignoredConsoleErrors.Count,
                "with nothing visible there is nothing to add -- no duplicates, no churn");
        }

        [Test]
        public void NotifyIgnoreStoreChanged_RaisesChanged()
        {
            bool raised = false;
            Action handler = delegate { raised = true; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.NotifyIgnoreStoreChanged();
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }
            Assert.IsTrue(raised,
                "settings-side ignore edits must poke Changed so the chip refreshes immediately");
        }

        // ------------------------------------------------------------------
        // UXO-3: the ignore-forever menu action can be undone in place.
        // ------------------------------------------------------------------

        [Test]
        public void IgnoreCurrentlyVisible_ReturnsTheJustIgnoredMessages()
        {
            Capture("SdkNoise: harmless");
            Capture("OtherNoise: also harmless");

            System.Collections.Generic.List<string> ignored =
                ConsoleErrorProvider.IgnoreCurrentlyVisible();

            CollectionAssert.AreEquivalent(
                new[] { "SdkNoise: harmless", "OtherNoise: also harmless" }, ignored,
                "the caller needs the exact set to offer an in-place undo");
        }

        [Test]
        public void Unignore_RemovesFromStore_AndRestoresVisibility()
        {
            Capture("SdkNoise: harmless");
            Capture("RealBug: fix me");
            System.Collections.Generic.List<string> ignored =
                ConsoleErrorProvider.IgnoreCurrentlyVisible();
            Assert.AreEqual(0, ConsoleErrorProvider.VisibleCount, "precondition");

            bool raised = false;
            Action handler = delegate { raised = true; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.Unignore(ignored);
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            Assert.AreEqual(0, _settings.ignoredConsoleErrors.Count,
                "undo must remove exactly what the ignore added");
            Assert.AreEqual(2, ConsoleErrorProvider.VisibleCount,
                "visibility restores from the intact raw list, no new log event needed");
            Assert.IsTrue(raised, "consumers refresh off Changed");
        }

        [Test]
        public void Unignore_UnknownMessages_IsANoOp()
        {
            Capture("SdkNoise: harmless");
            _settings.ignoredConsoleErrors.Add("SdkNoise: harmless");
            bool raised = false;
            Action handler = delegate { raised = true; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.Unignore(new[] { "NeverIgnored: x" });
                ConsoleErrorProvider.Unignore(null);
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }

            Assert.AreEqual(1, _settings.ignoredConsoleErrors.Count);
            Assert.IsFalse(raised, "nothing changed, so Changed must stay quiet");
        }
    }
}
