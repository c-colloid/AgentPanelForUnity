using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_play_mode (design note docs/design-notes/2026-09-21-play-mode-
    /// and-console-log-tools.md). The decision table also runs in the
    /// license-free smoke gate; what needs the Editor -- and is the whole
    /// reason this tool is not a one-line isPlaying setter -- is that
    /// Execute must DEFER the play-state change instead of applying it
    /// inline, because entering Play Mode reloads the domain and would take
    /// this call's own HTTP response down with it.
    ///
    /// The fixture never actually enters Play Mode: it swaps the scheduler
    /// for a recorder, so "was the change deferred?" is observable without
    /// a test run reloading the domain underneath the test runner.
    /// </summary>
    [TestFixture]
    public class UapPlayModeToolTests
    {
        private Action<Action> _originalScheduler;
        private List<Action> _scheduled;

        [SetUp]
        public void SetUp()
        {
            _originalScheduler = UapPlayModeTool.Scheduler;
            _scheduled = new List<Action>();
            UapPlayModeTool.Scheduler = _scheduled.Add;
        }

        [TearDown]
        public void TearDown()
        {
            UapPlayModeTool.Scheduler = _originalScheduler;
        }

        [Test]
        public void Execute_Start_DefersTheTransitionAndReportsTheStateBeforeIt()
        {
            RequireEditMode();

            JsonNode reply = new UapPlayModeTool().Execute(JsonNode.NewObject().Set("action", "start"));

            Assert.AreEqual(1, _scheduled.Count,
                "the play-state change must be scheduled after the reply, never applied inside Execute");
            Assert.IsFalse(EditorApplication.isPlaying, "Execute itself must not have entered Play Mode");

            string text = reply[0]["text"].AsString();
            StringAssert.Contains("\"state\":\"edit\"", text);
            StringAssert.Contains("\"requested\":\"playing\"", text);
            StringAssert.Contains("uap_ping", text);
        }

        [Test]
        public void Execute_Status_ReportsWithoutSchedulingAnything()
        {
            JsonNode reply = new UapPlayModeTool().Execute(JsonNode.NewObject().Set("action", "status"));

            Assert.AreEqual(0, _scheduled.Count);
            StringAssert.Contains("\"state\":", reply[0]["text"].AsString());
        }

        [Test]
        public void Execute_NoAction_IsStatus()
        {
            JsonNode reply = new UapPlayModeTool().Execute(JsonNode.NewObject());

            Assert.AreEqual(0, _scheduled.Count, "an argument-less call must never change the Editor's run state");
            StringAssert.Contains("Play state:", reply[0]["text"].AsString());
        }

        [Test]
        public void Execute_Stop_InEditMode_IsANoOpNotAFailure()
        {
            RequireEditMode();

            JsonNode reply = new UapPlayModeTool().Execute(JsonNode.NewObject().Set("action", "stop"));

            Assert.AreEqual(0, _scheduled.Count);
            StringAssert.Contains("Not in Play Mode", reply[0]["text"].AsString());
        }

        [Test]
        public void Execute_Pause_WithNothingRunning_Refuses()
        {
            RequireEditMode();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapPlayModeTool().Execute(JsonNode.NewObject().Set("action", "pause"));
            });
            StringAssert.Contains("action:start", error.Message, "a refusal must name the way out");
        }

        [Test]
        public void Execute_UnknownAction_IsRefusedByName()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(delegate
            {
                new UapPlayModeTool().Execute(JsonNode.NewObject().Set("action", "play"));
            });
            StringAssert.Contains("status, start, stop", error.Message);
        }

        [Test]
        public void Decide_RefusesEveryStateChangeWhileCompiling_ButStillAnswersStatus()
        {
            var compiling = new UapPlayModeSnapshot { IsCompiling = true };
            string message;

            Assert.AreEqual(UapPlayModeOutcome.Refuse,
                UapPlayModePolicy.Decide(UapPlayModeAction.Start, compiling, out message));
            StringAssert.Contains("uap_ping", message);
            Assert.AreEqual(UapPlayModeOutcome.Reported,
                UapPlayModePolicy.Decide(UapPlayModeAction.Status, compiling, out message));
        }

        [Test]
        public void Decide_StartWhilePlaying_ReportsInsteadOfFailing()
        {
            var playing = new UapPlayModeSnapshot { IsPlaying = true, WillChangePlayMode = true };
            string message;

            Assert.AreEqual(UapPlayModeOutcome.Reported,
                UapPlayModePolicy.Decide(UapPlayModeAction.Start, playing, out message));
            StringAssert.Contains("Already in Play Mode", message);
        }

        [Test]
        public void NextStepHint_OnlyTheDomainReloadingTransitionsGetOne()
        {
            Assert.IsNotEmpty(UapPlayModePolicy.NextStepHint(UapPlayModeOutcome.Enter));
            Assert.IsNotEmpty(UapPlayModePolicy.NextStepHint(UapPlayModeOutcome.Exit));
            Assert.IsEmpty(UapPlayModePolicy.NextStepHint(UapPlayModeOutcome.Pause));
            Assert.IsEmpty(UapPlayModePolicy.NextStepHint(UapPlayModeOutcome.Reported));
        }

        /// <summary>
        /// The Execute cases read the REAL Editor state; a suite started
        /// while Play Mode or a compile is running would exercise a
        /// different branch than the one under test, so say so rather than
        /// fail on it.
        /// </summary>
        private static void RequireEditMode()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling)
            {
                Assert.Ignore("Needs an idle Editor in Edit Mode (not playing, not compiling).");
            }
        }
    }
}
