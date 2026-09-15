using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The compile window (docs/design-notes/2026-09-15-panel-ux-
    /// followups.md section 1): a compilation run's compiler errors stay unpublished
    /// until the run ENDS, Settling marks the stretch in which the "ask the
    /// agent to fix these" affordances stay down, and what the panel's own
    /// staged-script validation build logs never reaches the capture list
    /// at all. Driven through the provider's test seams, which call the
    /// very methods CompilationPipeline and the log callback call, so these
    /// exercise the production path rather than a parallel one.
    /// </summary>
    [TestFixture]
    public class ConsoleErrorCompileWindowTests
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

        private static void CaptureLogError(string message)
        {
            ConsoleErrorProvider.EnqueueLogMessageForTests(message, "at X:1", LogType.Error);
            ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
        }

        [Test]
        public void CompilerErrors_StayUnpublishedUntilTheRunFinishes()
        {
            ConsoleErrorProvider.NotifyCompilationStartedForTests();
            ConsoleErrorProvider.NotifyCompilerErrorForTests("CS0103: the name 'Foo' does not exist", "A.cs", 12);

            Assert.AreEqual(0, ConsoleErrorProvider.Count,
                "assemblyCompilationFinished fires mid-run; its errors are not final yet");
            Assert.IsTrue(ConsoleErrorProvider.Settling, "a run is in flight");

            ConsoleErrorProvider.NotifyCompilationFinishedForTests();

            Assert.AreEqual(1, ConsoleErrorProvider.Count);
            Assert.AreEqual("CS0103: the name 'Foo' does not exist",
                ConsoleErrorProvider.VisibleSnapshot()[0].Message);
            Assert.IsTrue(ConsoleErrorProvider.VisibleSnapshot()[0].FromCompiler);
            Assert.IsFalse(ConsoleErrorProvider.Settling, "the run is over");
        }

        [Test]
        public void ARunThatIsSupersededBeforeItFinishes_PublishesNothing()
        {
            // The reported symptom: the chip appeared on every agent-driven
            // compile and cleared itself afterwards. A run whose errors are
            // superseded by the next run must never have been shown.
            ConsoleErrorProvider.NotifyCompilationStartedForTests();
            ConsoleErrorProvider.NotifyCompilerErrorForTests("CS0246: transient", "A.cs", 1);
            ConsoleErrorProvider.NotifyCompilationStartedForTests();
            ConsoleErrorProvider.NotifyCompilationFinishedForTests();

            Assert.AreEqual(0, ConsoleErrorProvider.Count);
        }

        [Test]
        public void ANewRun_DropsThePreviousRunsPublishedCompilerErrors_ButKeepsRuntimeOnes()
        {
            ConsoleErrorProvider.NotifyCompilationStartedForTests();
            ConsoleErrorProvider.NotifyCompilerErrorForTests("CS1002: ; expected", "A.cs", 3);
            ConsoleErrorProvider.NotifyCompilationFinishedForTests();
            CaptureLogError("NullReferenceException: object reference not set");
            Assert.AreEqual(2, ConsoleErrorProvider.Count);

            ConsoleErrorProvider.NotifyCompilationStartedForTests();

            Assert.AreEqual(1, ConsoleErrorProvider.Count);
            Assert.AreEqual("NullReferenceException: object reference not set",
                ConsoleErrorProvider.Snapshot()[0].Message);
        }

        [Test]
        public void TheSameCompilerErrorFromSeveralAssemblies_PublishesOnce()
        {
            ConsoleErrorProvider.NotifyCompilationStartedForTests();
            ConsoleErrorProvider.NotifyCompilerErrorForTests("CS0234: missing namespace", "A.cs", 1);
            ConsoleErrorProvider.NotifyCompilerErrorForTests("CS0234: missing namespace", "B.cs", 1);
            ConsoleErrorProvider.NotifyCompilationFinishedForTests();

            Assert.AreEqual(1, ConsoleErrorProvider.Count);
        }

        [Test]
        public void CompilationFinished_RaisesChanged_SoTheChipReEvaluates()
        {
            int raises = 0;
            System.Action handler = delegate { raises++; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.NotifyCompilationStartedForTests();
                Assert.AreEqual(1, raises, "Settling flipped on: consumers must re-read");
                ConsoleErrorProvider.NotifyCompilationFinishedForTests();
                Assert.AreEqual(2, raises, "Settling flipped off: consumers must re-read");
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }
        }

        [Test]
        public void ValidationBuildWindow_DropsWhatItLogs_AndMarksSettling()
        {
            ConsoleErrorProvider.BeginValidationBuild();
            Assert.IsTrue(ConsoleErrorProvider.Settling);

            CaptureLogError("CS0103 in a staged script the project has never seen");
            Assert.AreEqual(0, ConsoleErrorProvider.Count,
                "staged-script diagnostics go back to the agent, not onto the user's chip");

            ConsoleErrorProvider.EndValidationBuild();
            Assert.IsFalse(ConsoleErrorProvider.Settling);

            CaptureLogError("a real error after the window closed");
            Assert.AreEqual(1, ConsoleErrorProvider.Count);
        }

        [Test]
        public void ValidationBuildWindow_RaisesChangedOnTheMainThreadPump()
        {
            int raises = 0;
            System.Action handler = delegate { raises++; };
            ConsoleErrorProvider.Changed += handler;
            try
            {
                ConsoleErrorProvider.BeginValidationBuild();
                Assert.AreEqual(0, raises, "the raise is deferred, never made from the build callback's thread");
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
                Assert.AreEqual(1, raises);

                ConsoleErrorProvider.EndValidationBuild();
                ConsoleErrorProvider.PumpQueuedLogEntriesForTests();
                Assert.AreEqual(2, raises);
            }
            finally
            {
                ConsoleErrorProvider.Changed -= handler;
            }
        }

        [Test]
        public void ValidationBuildWindow_Nests_AndNeverGoesNegative()
        {
            ConsoleErrorProvider.BeginValidationBuild();
            ConsoleErrorProvider.BeginValidationBuild();
            ConsoleErrorProvider.EndValidationBuild();
            Assert.IsTrue(ConsoleErrorProvider.Settling, "the outer window is still open");
            ConsoleErrorProvider.EndValidationBuild();
            Assert.IsFalse(ConsoleErrorProvider.Settling);

            // An unpaired close (a commit released twice) must not push the
            // depth below zero and suppress the NEXT build's capture.
            ConsoleErrorProvider.EndValidationBuild();
            ConsoleErrorProvider.BeginValidationBuild();
            Assert.IsTrue(ConsoleErrorProvider.Settling);
            ConsoleErrorProvider.EndValidationBuild();
            Assert.IsFalse(ConsoleErrorProvider.Settling);
        }
    }
}
