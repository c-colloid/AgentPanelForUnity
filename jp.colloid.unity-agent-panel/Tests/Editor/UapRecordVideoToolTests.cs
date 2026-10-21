using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_record_video (docs/design-notes/2026-09-23-uloop-phase2.md
    /// section 3.1). Most of the decision table also runs in the
    /// license-free smoke gate via UapRecordVideoPolicy; what needs the
    /// Editor here is argument-refusal wiring through UapRecordVideoTool,
    /// status/stop with nothing recorded, and the policy's pure helpers.
    ///
    /// What is NOT tested here, on purpose: a real action:start. That path
    /// needs an open Game or Scene view (TryResolveSourceSize refuses
    /// otherwise) and a working UnityEditor.Media.MediaEncoder, neither of
    /// which the batch-mode EditMode runner has -- "no Game View window
    /// exists" in batch mode, so a real recording can only be verified by
    /// hand in an interactive Editor. The one test that attempts it is
    /// guarded with Assert.Ignore for exactly that reason.
    /// </summary>
    [TestFixture]
    public class UapRecordVideoToolTests
    {
        [SetUp]
        public void SetUp()
        {
            UapVideoRecorder.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            UapVideoRecorder.ResetForTests();
        }

        // -- Execute: argument refusals (no Editor view/encoder touched) ---

        [Test]
        public void Execute_NoAction_IsStart()
        {
            // An unresolvable view is refused before anything Unity-side is
            // touched, so this proves the default action without needing a
            // live Game view.
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("view", "sideways"));
            });
            StringAssert.Contains("scene", ex.Message);
        }

        [Test]
        public void Execute_UnknownAction_IsRefusedByName()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("action", "pause"));
            });
            StringAssert.Contains("start, stop, status", ex.Message);
        }

        [Test]
        public void Execute_Start_UnknownView_Refuses()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("view", "front"));
            });
            StringAssert.Contains("'view'", ex.Message);
        }

        [Test]
        public void Execute_Start_UnknownCapture_Refuses()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("capture", "screenshot"));
            });
            StringAssert.Contains("'capture'", ex.Message);
        }

        [Test]
        public void Execute_Start_FpsOutOfRange_Refuses()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("fps", 0));
            });
            StringAssert.Contains("'fps'", ex.Message);

            ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("fps", 61));
            });
            StringAssert.Contains("'fps'", ex.Message);
        }

        [Test]
        public void Execute_Start_MaxSecondsOutOfRange_Refuses()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("max_seconds", 0));
            });
            StringAssert.Contains("'max_seconds'", ex.Message);

            ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("max_seconds", 301));
            });
            StringAssert.Contains("'max_seconds'", ex.Message);
        }

        [Test]
        public void Execute_Start_MaxWidthOutOfRange_Refuses()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("max_width", 4));
            });
            StringAssert.Contains("'max_width'", ex.Message);
        }

        [Test]
        public void Execute_Stop_WithNothingRecording_Refuses()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("action", "stop"));
            });
            StringAssert.Contains("No recording is in progress", ex.Message);
        }

        [Test]
        public void Execute_Status_WithNothingRecorded_ReportsIdle()
        {
            JsonNode reply = new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("action", "status"));
            string text = reply[0]["text"].AsString();
            StringAssert.Contains("\"state\":\"idle\"", text);
            JsonNode parsed = JsonParser.Parse(text);
            Assert.AreEqual(string.Empty, parsed["recordingId"].AsString());
            Assert.AreEqual(string.Empty, parsed["stoppedBy"].AsString());
        }

        [Test]
        public void IsActive_InitiallyFalse()
        {
            Assert.IsFalse(UapVideoRecorder.IsActive);
        }

        /// <summary>
        /// Best-effort real start/stop cycle. Ignored unless an interactive
        /// Editor happens to have a Game view open (never true in the
        /// batch-mode CI runner) -- see the class comment.
        /// </summary>
        [Test]
        public void Execute_Start_ThenStop_WhenAGameViewIsOpen()
        {
            if (UapScreenCapture.FindOpenGameView() == null)
            {
                Assert.Ignore("Needs an open Game view (never true in the batch-mode EditMode runner).");
            }

            JsonNode startReply = new UapRecordVideoTool().Execute(
                JsonNode.NewObject().Set("action", "start").Set("max_seconds", 5).Set("fps", 10));
            JsonNode started = JsonParser.Parse(startReply[0]["text"].AsString());
            Assert.AreEqual("recording", started["state"].AsString());
            Assert.IsTrue(UapVideoRecorder.IsActive);

            JsonNode stopReply = new UapRecordVideoTool().Execute(JsonNode.NewObject().Set("action", "stop"));
            JsonNode stopped = JsonParser.Parse(stopReply[0]["text"].AsString());
            Assert.AreEqual("stopped", stopped["state"].AsString());
            Assert.AreEqual("stop", stopped["stoppedBy"].AsString());
            Assert.IsFalse(UapVideoRecorder.IsActive);
        }

        // -- UapRecordVideoPolicy: pure decision table -----------------------

        [Test]
        public void ParseAction_EmptyIsStart_UnknownThrows()
        {
            Assert.AreEqual(UapRecordVideoAction.Start, UapRecordVideoPolicy.ParseAction(null));
            Assert.AreEqual(UapRecordVideoAction.Start, UapRecordVideoPolicy.ParseAction(string.Empty));
            Assert.AreEqual(UapRecordVideoAction.Start, UapRecordVideoPolicy.ParseAction("START"));
            Assert.AreEqual(UapRecordVideoAction.Stop, UapRecordVideoPolicy.ParseAction("stop"));
            Assert.AreEqual(UapRecordVideoAction.Status, UapRecordVideoPolicy.ParseAction("status"));
            Assert.Throws<ArgumentException>(delegate { UapRecordVideoPolicy.ParseAction("pause"); });
        }

        [Test]
        public void DecideStart_RefusesOnlyWhenAlreadyActive()
        {
            string error;
            Assert.IsTrue(UapRecordVideoPolicy.DecideStart(false, out error));
            Assert.IsFalse(UapRecordVideoPolicy.DecideStart(true, out error));
            StringAssert.Contains("already in progress", error);
        }

        [Test]
        public void ValidateFps_RangeIsInclusive()
        {
            string error;
            Assert.IsTrue(UapRecordVideoPolicy.ValidateFps(1, out error));
            Assert.IsTrue(UapRecordVideoPolicy.ValidateFps(60, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateFps(0, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateFps(61, out error));
        }

        [Test]
        public void ValidateMaxSeconds_RangeAndNonFinite()
        {
            string error;
            Assert.IsTrue(UapRecordVideoPolicy.ValidateMaxSeconds(1, out error));
            Assert.IsTrue(UapRecordVideoPolicy.ValidateMaxSeconds(300, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateMaxSeconds(0, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateMaxSeconds(301, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateMaxSeconds(double.NaN, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateMaxSeconds(double.PositiveInfinity, out error));
        }

        [Test]
        public void ValidateMaxWidth_RangeIsInclusive()
        {
            string error;
            Assert.IsTrue(UapRecordVideoPolicy.ValidateMaxWidth(16, out error));
            Assert.IsTrue(UapRecordVideoPolicy.ValidateMaxWidth(4096, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateMaxWidth(15, out error));
            Assert.IsFalse(UapRecordVideoPolicy.ValidateMaxWidth(4097, out error));
        }

        [Test]
        public void RoundDownToEven_FloorsOddNeverBelowMinimum()
        {
            Assert.AreEqual(4, UapRecordVideoPolicy.RoundDownToEven(4));
            Assert.AreEqual(4, UapRecordVideoPolicy.RoundDownToEven(5));
            Assert.AreEqual(2, UapRecordVideoPolicy.RoundDownToEven(1));
            Assert.AreEqual(2, UapRecordVideoPolicy.RoundDownToEven(0));
            Assert.AreEqual(2, UapRecordVideoPolicy.RoundDownToEven(-3));
        }

        [Test]
        public void ResolveEncodeSize_NoMaxWidth_OnlyRoundsToEven()
        {
            int width, height;
            UapRecordVideoPolicy.ResolveEncodeSize(1281, 721, null, out width, out height);
            Assert.AreEqual(1280, width);
            Assert.AreEqual(720, height);
        }

        [Test]
        public void ResolveEncodeSize_MaxWidth_DownscalesPreservingAspectThenRounds()
        {
            int width, height;
            UapRecordVideoPolicy.ResolveEncodeSize(1920, 1080, 960, out width, out height);
            Assert.AreEqual(960, width);
            Assert.AreEqual(540, height);
        }

        [Test]
        public void ResolveEncodeSize_MaxWidthLargerThanSource_NeverUpscales()
        {
            int width, height;
            UapRecordVideoPolicy.ResolveEncodeSize(640, 360, 1920, out width, out height);
            Assert.AreEqual(640, width);
            Assert.AreEqual(360, height);
        }

        [Test]
        public void ChooseExtension_WindowsAndMacAreMp4_OthersAreWebm()
        {
            Assert.AreEqual("mp4", UapRecordVideoPolicy.ChooseExtension(UapVideoHostOs.Windows));
            Assert.AreEqual("mp4", UapRecordVideoPolicy.ChooseExtension(UapVideoHostOs.Mac));
            Assert.AreEqual("webm", UapRecordVideoPolicy.ChooseExtension(UapVideoHostOs.Linux));
            Assert.AreEqual("webm", UapRecordVideoPolicy.ChooseExtension(UapVideoHostOs.Other));
        }

        [Test]
        public void BuildOutputPath_IsUnderTempUapOpsVideos_WithChosenExtension()
        {
            string path = UapRecordVideoPolicy.BuildOutputPath("/proj", new DateTime(2026, 9, 23, 1, 2, 3, 4, DateTimeKind.Utc), "mp4");
            Assert.AreEqual("/proj/Temp/UapOpsVideos/uap_video_20260923_010203_004.mp4", path);
        }

        [Test]
        public void SelectFilesToPrune_KeepsNewestNAndNeverPrunesJustWritten()
        {
            var files = new List<string> { "a", "b", "c", "d", "e" };
            List<string> toDelete = UapRecordVideoPolicy.SelectFilesToPrune(files, 2, null);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, toDelete);

            toDelete = UapRecordVideoPolicy.SelectFilesToPrune(files, 10, null);
            Assert.AreEqual(0, toDelete.Count);

            toDelete = UapRecordVideoPolicy.SelectFilesToPrune(files, 0, "a");
            CollectionAssert.DoesNotContain(toDelete, "a");
            Assert.AreEqual(4, toDelete.Count);
        }

        [Test]
        public void TargetFrameCount_CountsFrameZeroAtTimeZero_ThenAdvancesWithFps()
        {
            Assert.AreEqual(1, UapRecordVideoPolicy.TargetFrameCount(0, 30));
            Assert.AreEqual(1, UapRecordVideoPolicy.TargetFrameCount(0.03, 30));
            Assert.AreEqual(2, UapRecordVideoPolicy.TargetFrameCount(1.0 / 30, 30));
            Assert.AreEqual(31, UapRecordVideoPolicy.TargetFrameCount(1.0, 30));
        }

        [Test]
        public void StateName_RoundTripsThroughTryParse()
        {
            foreach (UapRecordVideoState state in new[]
            {
                UapRecordVideoState.Idle, UapRecordVideoState.Recording, UapRecordVideoState.Stopped,
            })
            {
                UapRecordVideoState parsed;
                Assert.IsTrue(UapRecordVideoPolicy.TryParseStateName(UapRecordVideoPolicy.StateName(state), out parsed));
                Assert.AreEqual(state, parsed);
            }
        }

        [Test]
        public void TryParseStateName_Garbage_FallsBackToIdle()
        {
            UapRecordVideoState parsed;
            Assert.IsFalse(UapRecordVideoPolicy.TryParseStateName("not-a-state", out parsed));
            Assert.AreEqual(UapRecordVideoState.Idle, parsed);
        }

        [Test]
        public void DescribeStoppedBy_CoversEveryVocabularyWord()
        {
            StringAssert.Contains("request", UapRecordVideoPolicy.DescribeStoppedBy(UapRecordVideoPolicy.StoppedByStop));
            StringAssert.Contains("max_seconds", UapRecordVideoPolicy.DescribeStoppedBy(UapRecordVideoPolicy.StoppedByDuration));
            StringAssert.Contains("Play Mode ended", UapRecordVideoPolicy.DescribeStoppedBy(UapRecordVideoPolicy.StoppedByPlayModeExit));
            StringAssert.Contains("domain reload", UapRecordVideoPolicy.DescribeStoppedBy(UapRecordVideoPolicy.StoppedByAssemblyReload));
            StringAssert.Contains("error", UapRecordVideoPolicy.DescribeStoppedBy(UapRecordVideoPolicy.StoppedByError));
            Assert.AreEqual("Not stopped.", UapRecordVideoPolicy.DescribeStoppedBy(string.Empty));
        }

        [Test]
        public void ToJson_FromJson_RoundTrips()
        {
            var result = new UapRecordVideoResult
            {
                State = UapRecordVideoState.Stopped,
                RecordingId = "abc123",
                Path = "/proj/Temp/UapOpsVideos/uap_video_1.mp4",
                View = "game",
                Capture = "camera",
                Fps = 30,
                Width = 1280,
                Height = 720,
                MaxSeconds = 60,
                StopOnPlayExit = true,
                StartedAtUtcTicks = 100L,
                FinishedAtUtcTicks = 200L,
                FramesWritten = 42,
                DroppedFrames = 3,
                SecondsRecorded = 1.4,
                FileSizeBytes = 123456L,
                StoppedBy = "stop",
                Message = "Stopped by request (action:stop).",
                Warning = string.Empty,
            };

            UapRecordVideoResult round = UapRecordVideoPolicy.FromJson(UapRecordVideoPolicy.ToJson(result));

            Assert.AreEqual(result.State, round.State);
            Assert.AreEqual(result.RecordingId, round.RecordingId);
            Assert.AreEqual(result.Path, round.Path);
            Assert.AreEqual(result.View, round.View);
            Assert.AreEqual(result.Capture, round.Capture);
            Assert.AreEqual(result.Fps, round.Fps);
            Assert.AreEqual(result.Width, round.Width);
            Assert.AreEqual(result.Height, round.Height);
            Assert.AreEqual(result.MaxSeconds, round.MaxSeconds, 1e-9);
            Assert.AreEqual(result.StopOnPlayExit, round.StopOnPlayExit);
            Assert.AreEqual(result.StartedAtUtcTicks, round.StartedAtUtcTicks);
            Assert.AreEqual(result.FinishedAtUtcTicks, round.FinishedAtUtcTicks);
            Assert.AreEqual(result.FramesWritten, round.FramesWritten);
            Assert.AreEqual(result.DroppedFrames, round.DroppedFrames);
            Assert.AreEqual(result.SecondsRecorded, round.SecondsRecorded, 1e-9);
            Assert.AreEqual(result.FileSizeBytes, round.FileSizeBytes);
            Assert.AreEqual(result.StoppedBy, round.StoppedBy);
        }

        [Test]
        public void FromJson_Garbled_DegradesToIdleRatherThanThrowing()
        {
            UapRecordVideoResult result = UapRecordVideoPolicy.FromJson(null);
            Assert.AreEqual(UapRecordVideoState.Idle, result.State);

            result = UapRecordVideoPolicy.FromJson(JsonNode.Of("not an object"));
            Assert.AreEqual(UapRecordVideoState.Idle, result.State);

            result = UapRecordVideoPolicy.FromJson(JsonNode.NewObject().Set("state", "sideways"));
            Assert.AreEqual(UapRecordVideoState.Idle, result.State);
        }

        // -- UapVideoRecorder persistence -------------------------------------

        [Test]
        public void LoadPersisted_WithNothingSaved_IsIdle()
        {
            UapRecordVideoResult result = UapVideoRecorder.LoadPersisted();
            Assert.AreEqual(UapRecordVideoState.Idle, result.State);
            Assert.AreEqual(string.Empty, result.RecordingId);
        }
    }
}
