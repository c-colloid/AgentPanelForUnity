using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// P6 of the 2026-09-07 design note (decision E2): the Console-clear
    /// sync's pure decision, its effect on the provider, and that the
    /// internal LogEntries API it relies on resolves on this editor.
    /// </summary>
    [TestFixture]
    public class ConsoleWindowSyncTests
    {
        private PanelSettings _settings;

        [SetUp]
        public void SetUp()
        {
            ConsoleErrorProvider.ResetForTests();
            ConsoleWindowSync.ResetForTests();
            _settings = new PanelSettings();
            ConsoleErrorProvider.SettingsSourceForTests = delegate { return _settings; };
        }

        [TearDown]
        public void TearDown()
        {
            ConsoleErrorProvider.SettingsSourceForTests = null;
            ConsoleWindowSync.ResetForTests();
            ConsoleErrorProvider.ResetForTests();
        }

        private static void Capture(string message)
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests(message, "at X:1", LogType.Error);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
        }

        [Test]
        public void ShouldClear_OnlyOnADropWithABaseline()
        {
            Assert.IsFalse(ConsoleWindowSync.ShouldClear(-1, 0), "first observation never clears");
            Assert.IsFalse(ConsoleWindowSync.ShouldClear(-1, 5));
            Assert.IsFalse(ConsoleWindowSync.ShouldClear(3, 3));
            Assert.IsFalse(ConsoleWindowSync.ShouldClear(3, 7), "new errors are captured by the log callback, not here");
            Assert.IsTrue(ConsoleWindowSync.ShouldClear(3, 0));
            Assert.IsTrue(ConsoleWindowSync.ShouldClear(3, 2), "Clear on Recompile can leave new errors behind; still a clear");
        }

        [Test]
        public void ApplyCount_DropClearsTheProvider_RiseDoesNot()
        {
            Capture("real error");
            ConsoleWindowSync.ApplyCount(1);
            Assert.AreEqual(1, ConsoleErrorProvider.Count);
            ConsoleWindowSync.ApplyCount(2);
            Assert.AreEqual(1, ConsoleErrorProvider.Count, "a rise leaves captures alone");
            ConsoleWindowSync.ApplyCount(0);
            Assert.AreEqual(0, ConsoleErrorProvider.Count, "the Console was cleared, so is the chip");
            Assert.AreEqual(0, ConsoleWindowSync.LastErrorCountForTests);
        }

        [Test]
        public void FirstObservation_NeverClears()
        {
            Capture("error from before this domain");
            ConsoleWindowSync.ApplyCount(0);
            Assert.AreEqual(1, ConsoleErrorProvider.Count);
        }

        [Test]
        public void InternalConsoleApi_ResolvesOnThisEditor()
        {
            Assert.IsTrue(ConsoleWindowSync.Available, "UnityEditor.LogEntries.GetCountsByType must resolve on 2022.3");
            int errors;
            Assert.IsTrue(ConsoleWindowSync.TryReadErrorCount(out errors));
            Assert.GreaterOrEqual(errors, 0);
        }
    }
}
