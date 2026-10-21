using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_input_record / uap_input_replay (docs/design-notes/2026-09-23-
    /// uloop-phase2.md section 3.4). Most of the decision table runs here
    /// against UapInputRecordingPolicy directly (format round-trip,
    /// validation refusals, diffing, replay timeline scheduling, path/
    /// argument rules) with no Unity Editor state involved at all -- these
    /// are exactly the parts a future integration pass can also wire into
    /// the license-free smoke gate (ci/SmokeTests) verbatim, since the
    /// policy class has no UnityEditor/UnityEngine/InputSystem using.
    ///
    /// What needs the Editor here is argument/state-refusal wiring through
    /// UapInputRecordTool/UapInputReplayTool -- action:start always refuses
    /// in batch mode (never Play Mode; see IUapTool.cs's own note that
    /// EditMode tests never enter Play Mode), which this suite uses
    /// directly rather than working around: it is exactly the "requires
    /// Play Mode" refusal the brief asks to cover. A real recording/replay
    /// session (Play Mode running, an actual Keyboard/Mouse device) can only
    /// be verified by hand in an interactive Editor -- see the design note's
    /// "unverified" list.
    /// </summary>
    [TestFixture]
    public class UapInputRecordingTests
    {
        [SetUp]
        public void SetUp()
        {
            UapInputRecorder.ResetForTests();
            UapInputReplayer.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            UapInputRecorder.ResetForTests();
            UapInputReplayer.ResetForTests();
        }

        // -- Action parsing -------------------------------------------------

        [Test]
        public void ParseRecordAction_NoAction_IsStart()
        {
            Assert.AreEqual(UapInputRecordAction.Start, UapInputRecordingPolicy.ParseRecordAction(null));
            Assert.AreEqual(UapInputRecordAction.Start, UapInputRecordingPolicy.ParseRecordAction(""));
        }

        [Test]
        public void ParseRecordAction_Unknown_ThrowsByName()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                UapInputRecordingPolicy.ParseRecordAction("pause");
            });
            StringAssert.Contains("start, stop, status", ex.Message);
        }

        [Test]
        public void ParseReplayAction_NoAction_IsStart()
        {
            Assert.AreEqual(UapInputReplayAction.Start, UapInputRecordingPolicy.ParseReplayAction(null));
        }

        [Test]
        public void ParseReplayAction_Unknown_ThrowsByName()
        {
            Assert.Throws<ArgumentException>(delegate { UapInputRecordingPolicy.ParseReplayAction("rewind"); });
        }

        // -- Start refusals (mutual exclusion) -------------------------------

        [Test]
        public void DecideRecordStart_AlreadyRecording_Refuses()
        {
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.DecideRecordStart(recordingActive: true, replayActive: false, out error));
            StringAssert.Contains("already in progress", error);
        }

        [Test]
        public void DecideRecordStart_ReplayActive_Refuses()
        {
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.DecideRecordStart(recordingActive: false, replayActive: true, out error));
            StringAssert.Contains("uap_input_replay", error);
        }

        [Test]
        public void DecideReplayStart_AlreadyReplaying_Refuses()
        {
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.DecideReplayStart(replayActive: true, recordingActive: false, out error));
            StringAssert.Contains("already in progress", error);
        }

        [Test]
        public void DecideReplayStart_RecordingActive_Refuses()
        {
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.DecideReplayStart(replayActive: false, recordingActive: true, out error));
            StringAssert.Contains("uap_input_record", error);
        }

        [Test]
        public void DecideStart_NeitherActive_Allows()
        {
            string error;
            Assert.IsTrue(UapInputRecordingPolicy.DecideRecordStart(false, false, out error));
            Assert.IsTrue(UapInputRecordingPolicy.DecideReplayStart(false, false, out error));
        }

        // -- Argument validation ----------------------------------------------

        [Test]
        public void ValidateMaxSeconds_OutOfRange_Refuses()
        {
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.ValidateMaxSeconds(0, out error));
            Assert.IsFalse(UapInputRecordingPolicy.ValidateMaxSeconds(-1, out error));
            Assert.IsFalse(UapInputRecordingPolicy.ValidateMaxSeconds(UapInputRecordingPolicy.MaxMaxSeconds + 1, out error));
            Assert.IsFalse(UapInputRecordingPolicy.ValidateMaxSeconds(double.NaN, out error));
        }

        [Test]
        public void ValidateMaxSeconds_InRange_Allows()
        {
            string error;
            Assert.IsTrue(UapInputRecordingPolicy.ValidateMaxSeconds(UapInputRecordingPolicy.DefaultMaxSeconds, out error));
            Assert.IsTrue(UapInputRecordingPolicy.ValidateMaxSeconds(UapInputRecordingPolicy.MaxMaxSeconds, out error));
        }

        [Test]
        public void ValidateSpeed_OutOfRange_Refuses()
        {
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.ValidateSpeed(UapInputRecordingPolicy.MinSpeed - 0.01, out error));
            Assert.IsFalse(UapInputRecordingPolicy.ValidateSpeed(UapInputRecordingPolicy.MaxSpeed + 0.01, out error));
            Assert.IsFalse(UapInputRecordingPolicy.ValidateSpeed(double.PositiveInfinity, out error));
        }

        [Test]
        public void ValidateSpeed_InRange_Allows()
        {
            string error;
            Assert.IsTrue(UapInputRecordingPolicy.ValidateSpeed(1.0, out error));
            Assert.IsTrue(UapInputRecordingPolicy.ValidateSpeed(UapInputRecordingPolicy.MinSpeed, out error));
            Assert.IsTrue(UapInputRecordingPolicy.ValidateSpeed(UapInputRecordingPolicy.MaxSpeed, out error));
        }

        // -- Path resolution ----------------------------------------------------

        private const string Root = "/proj";

        [Test]
        public void TryResolveRecordPath_Empty_UsesDefaultUnderLibrary()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveRecordPath(Root, null, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), out absolutePath, out error);
            Assert.IsTrue(ok, error);
            StringAssert.StartsWith("/proj/Library/UnityAgentPanel/InputRecordings/", absolutePath);
            StringAssert.EndsWith(".json", absolutePath);
        }

        [Test]
        public void TryResolveRecordPath_Relative_ResolvesUnderRoot()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveRecordPath(Root, "MyRecordings/take1.json", DateTime.UtcNow, out absolutePath, out error);
            Assert.IsTrue(ok, error);
            Assert.AreEqual("/proj/MyRecordings/take1.json", absolutePath);
        }

        [Test]
        public void TryResolveRecordPath_AbsoluteInsideProject_Allows()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveRecordPath(Root, "/proj/Library/UnityAgentPanel/InputRecordings/x.json", DateTime.UtcNow, out absolutePath, out error);
            Assert.IsTrue(ok, error);
            Assert.AreEqual("/proj/Library/UnityAgentPanel/InputRecordings/x.json", absolutePath);
        }

        [Test]
        public void TryResolveRecordPath_AbsoluteOutsideProject_Refuses()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveRecordPath(Root, "/etc/passwd", DateTime.UtcNow, out absolutePath, out error);
            Assert.IsFalse(ok);
            StringAssert.Contains("inside the project", error);
        }

        [Test]
        public void TryResolveRecordPath_DotDotSegment_Refuses()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveRecordPath(Root, "../outside.json", DateTime.UtcNow, out absolutePath, out error);
            Assert.IsFalse(ok);
            StringAssert.Contains("..", error);
        }

        [Test]
        public void TryResolveRecordPath_DotDotInsideAbsolutePath_Refuses()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveRecordPath(Root, "/proj/Library/../../../etc/passwd", DateTime.UtcNow, out absolutePath, out error);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryResolveReplayPath_Empty_Refuses()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveReplayPath(Root, null, out absolutePath, out error);
            Assert.IsFalse(ok);
            StringAssert.Contains("'path' is required", error);
        }

        [Test]
        public void TryResolveReplayPath_Relative_ResolvesUnderRoot()
        {
            string absolutePath;
            string error;
            bool ok = UapInputRecordingPolicy.TryResolveReplayPath(Root, "Library/UnityAgentPanel/InputRecordings/x.json", out absolutePath, out error);
            Assert.IsTrue(ok, error);
            Assert.AreEqual("/proj/Library/UnityAgentPanel/InputRecordings/x.json", absolutePath);
        }

        // -- Pruning --------------------------------------------------------

        [Test]
        public void SelectFilesToPrune_KeepsNewestOnly()
        {
            var files = new List<string> { "a", "b", "c", "d", "e" };
            List<string> toDelete = UapInputRecordingPolicy.SelectFilesToPrune(files, 2, null);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, toDelete);
        }

        [Test]
        public void SelectFilesToPrune_NeverDeletesJustWritten()
        {
            var files = new List<string> { "a", "b" };
            List<string> toDelete = UapInputRecordingPolicy.SelectFilesToPrune(files, 0, "a");
            CollectionAssert.AreEqual(new[] { "b" }, toDelete);
        }

        // -- Event diffing ----------------------------------------------------

        [Test]
        public void DiffKeys_PressAndRelease_EmitsSortedEvents()
        {
            var previous = new HashSet<string>(new[] { "W", "A" });
            var current = new HashSet<string>(new[] { "A", "S" });
            var events = new List<UapInputRecordEvent>();
            UapInputRecordingPolicy.DiffKeys(previous, current, 1.5, events);

            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(UapInputRecordEventType.KeyUp, events[0].Type);
            Assert.AreEqual("W", events[0].Key);
            Assert.AreEqual(UapInputRecordEventType.KeyDown, events[1].Type);
            Assert.AreEqual("S", events[1].Key);
            Assert.AreEqual(1.5, events[0].T);
        }

        [Test]
        public void DiffKeys_NoChange_EmitsNothing()
        {
            var set = new HashSet<string> { "W" };
            var events = new List<UapInputRecordEvent>();
            UapInputRecordingPolicy.DiffKeys(set, new HashSet<string>(set), 2.0, events);
            Assert.AreEqual(0, events.Count);
        }

        [Test]
        public void DiffButtons_PressAndRelease_FollowsFixedOrder()
        {
            var previous = new HashSet<string> { "Left" };
            var current = new HashSet<string> { "Right" };
            var events = new List<UapInputRecordEvent>();
            UapInputRecordingPolicy.DiffButtons(previous, current, 0.2, events);

            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(UapInputRecordEventType.MouseButtonUp, events[0].Type);
            Assert.AreEqual("Left", events[0].Button);
            Assert.AreEqual(UapInputRecordEventType.MouseButtonDown, events[1].Type);
            Assert.AreEqual("Right", events[1].Button);
        }

        [Test]
        public void TryDiffMouseMove_FirstSample_AlwaysEmits()
        {
            UapInputRecordEvent evt;
            bool emitted = UapInputRecordingPolicy.TryDiffMouseMove(false, 0, 0, 100, 200, 0.0, out evt);
            Assert.IsTrue(emitted);
            Assert.AreEqual(100, evt.X);
            Assert.AreEqual(200, evt.Y);
        }

        [Test]
        public void TryDiffMouseMove_SamePosition_EmitsNothing()
        {
            UapInputRecordEvent evt;
            bool emitted = UapInputRecordingPolicy.TryDiffMouseMove(true, 50, 60, 50, 60, 1.0, out evt);
            Assert.IsFalse(emitted);
            Assert.IsNull(evt);
        }

        [Test]
        public void TryScrollEvent_Zero_EmitsNothing()
        {
            UapInputRecordEvent evt;
            Assert.IsFalse(UapInputRecordingPolicy.TryScrollEvent(0, 0, 1.0, out evt));
        }

        [Test]
        public void TryScrollEvent_NonZero_Emits()
        {
            UapInputRecordEvent evt;
            Assert.IsTrue(UapInputRecordingPolicy.TryScrollEvent(0, 1.5, 1.0, out evt));
            Assert.AreEqual(1.5, evt.Dy);
        }

        // -- Format round-trip --------------------------------------------------

        [Test]
        public void ToJson_TryParseFile_RoundTrips()
        {
            var file = new UapInputRecordingFile
            {
                FormatVersion = UapInputRecordingPolicy.CurrentFormatVersion,
                RecordedAtUtcTicks = 123456789L,
                DurationSeconds = 3.25,
            };
            file.Events.Add(new UapInputRecordEvent { T = 0.0, Type = UapInputRecordEventType.MouseMove, X = 10, Y = 20 });
            file.Events.Add(new UapInputRecordEvent { T = 0.0, Type = UapInputRecordEventType.KeyDown, Key = "W" });
            file.Events.Add(new UapInputRecordEvent { T = 0.5, Type = UapInputRecordEventType.MouseButtonDown, Button = "Left" });
            file.Events.Add(new UapInputRecordEvent { T = 0.6, Type = UapInputRecordEventType.MouseScroll, Dx = 0, Dy = -1 });
            file.Events.Add(new UapInputRecordEvent { T = 1.0, Type = UapInputRecordEventType.MouseButtonUp, Button = "Left" });
            file.Events.Add(new UapInputRecordEvent { T = 1.2, Type = UapInputRecordEventType.KeyUp, Key = "W" });

            JsonNode node = UapInputRecordingPolicy.ToJson(file);
            string text = JsonWriter.Write(node);
            JsonNode reparsedNode;
            string parseError;
            Assert.IsTrue(JsonParser.TryParse(text, out reparsedNode, out parseError), parseError);

            UapInputRecordingFile roundTripped;
            string validateError;
            Assert.IsTrue(UapInputRecordingPolicy.TryParseFile(reparsedNode, out roundTripped, out validateError), validateError);

            Assert.AreEqual(file.FormatVersion, roundTripped.FormatVersion);
            Assert.AreEqual(file.RecordedAtUtcTicks, roundTripped.RecordedAtUtcTicks);
            Assert.AreEqual(file.DurationSeconds, roundTripped.DurationSeconds, 0.0001);
            Assert.AreEqual(file.Events.Count, roundTripped.Events.Count);
            for (int i = 0; i < file.Events.Count; i++)
            {
                Assert.AreEqual(file.Events[i].Type, roundTripped.Events[i].Type, "event " + i);
                Assert.AreEqual(file.Events[i].T, roundTripped.Events[i].T, 0.0001, "event " + i);
                Assert.AreEqual(file.Events[i].Key, roundTripped.Events[i].Key, "event " + i);
                Assert.AreEqual(file.Events[i].Button, roundTripped.Events[i].Button, "event " + i);
                Assert.AreEqual(file.Events[i].X, roundTripped.Events[i].X, 0.0001, "event " + i);
                Assert.AreEqual(file.Events[i].Y, roundTripped.Events[i].Y, 0.0001, "event " + i);
                Assert.AreEqual(file.Events[i].Dx, roundTripped.Events[i].Dx, 0.0001, "event " + i);
                Assert.AreEqual(file.Events[i].Dy, roundTripped.Events[i].Dy, 0.0001, "event " + i);
            }
        }

        [Test]
        public void ToJson_EmptyRecording_RoundTrips()
        {
            var file = new UapInputRecordingFile { FormatVersion = UapInputRecordingPolicy.CurrentFormatVersion };
            UapInputRecordingFile roundTripped;
            string error;
            Assert.IsTrue(UapInputRecordingPolicy.TryParseFile(UapInputRecordingPolicy.ToJson(file), out roundTripped, out error), error);
            Assert.AreEqual(0, roundTripped.Events.Count);
        }

        // -- Validation refusals ----------------------------------------------

        [Test]
        public void TryParseFile_NotAnObject_Refuses()
        {
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(JsonNode.NewArray(), out file, out error));
            StringAssert.Contains("JSON object", error);
        }

        [Test]
        public void TryParseFile_WrongFormatVersion_Refuses()
        {
            JsonNode node = JsonNode.NewObject().Set("formatVersion", 99).Set("events", JsonNode.NewArray());
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("format version", error);
        }

        [Test]
        public void TryParseFile_MissingEventsArray_Refuses()
        {
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("events", error);
        }

        [Test]
        public void TryParseFile_UnsortedTimes_Refuses()
        {
            JsonNode events = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("t", 1.0).Set("type", "key_down").Set("key", "W"))
                .Add(JsonNode.NewObject().Set("t", 0.5).Set("type", "key_up").Set("key", "W"));
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("order", error);
        }

        [Test]
        public void TryParseFile_UnknownEventType_Refuses()
        {
            JsonNode events = JsonNode.NewArray().Add(JsonNode.NewObject().Set("t", 0.0).Set("type", "teleport"));
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("unknown 'type'", error);
        }

        [Test]
        public void TryParseFile_KeyEventMissingKey_Refuses()
        {
            JsonNode events = JsonNode.NewArray().Add(JsonNode.NewObject().Set("t", 0.0).Set("type", "key_down"));
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("'key'", error);
        }

        [Test]
        public void TryParseFile_UnknownKeyName_Refuses()
        {
            JsonNode events = JsonNode.NewArray().Add(JsonNode.NewObject().Set("t", 0.0).Set("type", "key_down").Set("key", "Frobnicate"));
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("unknown key", error);
        }

        [Test]
        public void TryParseFile_UnknownButton_Refuses()
        {
            JsonNode events = JsonNode.NewArray().Add(JsonNode.NewObject().Set("t", 0.0).Set("type", "mouse_button_down").Set("button", "Forward"));
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("unknown 'button'", error);
        }

        [Test]
        public void TryParseFile_MouseMoveMissingCoordinates_Refuses()
        {
            JsonNode events = JsonNode.NewArray().Add(JsonNode.NewObject().Set("t", 0.0).Set("type", "mouse_move"));
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("'x'/'y'", error);
        }

        [Test]
        public void TryParseFile_TooManyEvents_Refuses()
        {
            JsonNode events = JsonNode.NewArray();
            // Cheaper than building MaxEvents+1 real events: this class only
            // checks eventsNode.Count against the cap before parsing any of
            // them, so padding with minimal valid mouse_move objects is fine.
            for (int i = 0; i <= UapInputRecordingPolicy.MaxEvents; i++)
            {
                events.Add(JsonNode.NewObject().Set("t", 0.0).Set("type", "mouse_move").Set("x", 0).Set("y", 0));
            }
            JsonNode node = JsonNode.NewObject().Set("formatVersion", UapInputRecordingPolicy.CurrentFormatVersion).Set("events", events);
            UapInputRecordingFile file;
            string error;
            Assert.IsFalse(UapInputRecordingPolicy.TryParseFile(node, out file, out error));
            StringAssert.Contains("more than", error);
        }

        // -- Replay timeline scheduling -----------------------------------------

        private static List<UapInputRecordEvent> Events(params UapInputRecordEvent[] events)
        {
            return new List<UapInputRecordEvent>(events);
        }

        private static UapInputRecordEvent KeyDown(double t, string key) { return new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.KeyDown, Key = key }; }
        private static UapInputRecordEvent KeyUp(double t, string key) { return new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.KeyUp, Key = key }; }
        private static UapInputRecordEvent BtnDown(double t, string button) { return new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseButtonDown, Button = button }; }
        private static UapInputRecordEvent BtnUp(double t, string button) { return new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseButtonUp, Button = button }; }
        private static UapInputRecordEvent Move(double t, double x, double y) { return new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseMove, X = x, Y = y }; }
        private static UapInputRecordEvent Scroll(double t, double dx, double dy) { return new UapInputRecordEvent { T = t, Type = UapInputRecordEventType.MouseScroll, Dx = dx, Dy = dy }; }

        [Test]
        public void AdvanceReplay_AppliesDueEvents_StopsAtFirstNotYetDue()
        {
            List<UapInputRecordEvent> events = Events(Move(0.0, 10, 20), KeyDown(0.0, "W"), KeyDown(5.0, "Space"));
            var state = new UapInputReplayState();
            var downTicks = new Dictionary<string, int>();
            double scrollDx, scrollDy;
            bool keysChanged, mouseChanged;

            int newIndex = UapInputRecordingPolicy.AdvanceReplay(events, 0, elapsedScaledSeconds: 0.0, currentTick: 0,
                minHoldPolls: 2, downTicks, state, out scrollDx, out scrollDy, out keysChanged, out mouseChanged);

            Assert.AreEqual(2, newIndex);
            Assert.IsTrue(keysChanged);
            Assert.IsTrue(mouseChanged);
            Assert.IsTrue(state.Keys.Contains("W"));
            Assert.AreEqual(10, state.MouseX);
            Assert.AreEqual(20, state.MouseY);
        }

        [Test]
        public void AdvanceReplay_KeyUp_DeferredUntilMinHoldPollsElapsed()
        {
            List<UapInputRecordEvent> events = Events(KeyDown(0.0, "W"), KeyUp(0.001, "W"));
            var state = new UapInputReplayState();
            var downTicks = new Dictionary<string, int>();
            double scrollDx, scrollDy;
            bool keysChanged, mouseChanged;

            // Tick 0: both events are already "due" by elapsed time, but the
            // Up must wait -- only the Down is applied.
            int index = UapInputRecordingPolicy.AdvanceReplay(events, 0, elapsedScaledSeconds: 1.0, currentTick: 0,
                minHoldPolls: 2, downTicks, state, out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(1, index);
            Assert.IsTrue(state.Keys.Contains("W"), "key should still be held after tick 0");

            // Tick 1: still only 1 tick since the down -- still deferred.
            index = UapInputRecordingPolicy.AdvanceReplay(events, index, 1.0, currentTick: 1,
                minHoldPolls: 2, downTicks, state, out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(1, index);
            Assert.IsTrue(state.Keys.Contains("W"), "key should still be held after tick 1");

            // Tick 2: 2 ticks have now passed since the down -- the release applies.
            index = UapInputRecordingPolicy.AdvanceReplay(events, index, 1.0, currentTick: 2,
                minHoldPolls: 2, downTicks, state, out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(2, index);
            Assert.IsFalse(state.Keys.Contains("W"), "key should be released by tick 2");
        }

        [Test]
        public void AdvanceReplay_MouseButtonUp_AlsoDeferred()
        {
            List<UapInputRecordEvent> events = Events(BtnDown(0.0, "Left"), BtnUp(0.0, "Left"));
            var state = new UapInputReplayState();
            var downTicks = new Dictionary<string, int>();
            double scrollDx, scrollDy;
            bool keysChanged, mouseChanged;

            int index = UapInputRecordingPolicy.AdvanceReplay(events, 0, 1.0, 0, 2, downTicks, state,
                out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(1, index);
            Assert.IsTrue(state.MouseButtons.Contains("Left"));

            index = UapInputRecordingPolicy.AdvanceReplay(events, index, 1.0, 2, 2, downTicks, state,
                out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(2, index);
            Assert.IsFalse(state.MouseButtons.Contains("Left"));
        }

        [Test]
        public void AdvanceReplay_ScrollEventsInSameTick_Accumulate()
        {
            List<UapInputRecordEvent> events = Events(Scroll(0.0, 0, 1), Scroll(0.01, 0, 2));
            var state = new UapInputReplayState();
            var downTicks = new Dictionary<string, int>();
            double scrollDx, scrollDy;
            bool keysChanged, mouseChanged;

            int index = UapInputRecordingPolicy.AdvanceReplay(events, 0, 1.0, 0, 2, downTicks, state,
                out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(2, index);
            Assert.AreEqual(0, scrollDx);
            Assert.AreEqual(3, scrollDy);
            Assert.IsTrue(mouseChanged);
        }

        [Test]
        public void AdvanceReplay_NothingDueYet_DoesNotAdvance()
        {
            List<UapInputRecordEvent> events = Events(KeyDown(5.0, "W"));
            var state = new UapInputReplayState();
            var downTicks = new Dictionary<string, int>();
            double scrollDx, scrollDy;
            bool keysChanged, mouseChanged;

            int index = UapInputRecordingPolicy.AdvanceReplay(events, 0, 1.0, 0, 2, downTicks, state,
                out scrollDx, out scrollDy, out keysChanged, out mouseChanged);
            Assert.AreEqual(0, index);
            Assert.IsFalse(keysChanged);
            Assert.IsFalse(mouseChanged);
        }

        [Test]
        public void ReplayProgress_ComputesFraction()
        {
            Assert.AreEqual(0.0, UapInputRecordingPolicy.ReplayProgress(0, 10));
            Assert.AreEqual(0.5, UapInputRecordingPolicy.ReplayProgress(5, 10));
            Assert.AreEqual(1.0, UapInputRecordingPolicy.ReplayProgress(10, 10));
            Assert.AreEqual(1.0, UapInputRecordingPolicy.ReplayProgress(0, 0), "an empty recording is always 'done'");
        }

        // -- Tool-level refusals outside Play Mode / without the package --------

        [Test]
        public void RecordTool_Start_OutsidePlayMode_Refuses()
        {
            // Batch-mode EditMode tests never enter Play Mode (repo hard
            // rule), so this always exercises the "requires Play Mode"
            // refusal -- whether the com.unity.inputsystem package happens
            // to be present in this CI host or not, the Play Mode check
            // runs first (see UapInputRecorder.Start's ordering).
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapInputRecordTool().Execute(JsonNode.NewObject());
            });
            StringAssert.Contains("Play Mode", ex.Message);
        }

        [Test]
        public void ReplayTool_Start_OutsidePlayMode_Refuses()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapInputReplayTool().Execute(JsonNode.NewObject().Set("path", "somewhere.json"));
            });
            StringAssert.Contains("Play Mode", ex.Message);
        }

        [Test]
        public void RecordTool_Stop_WithNothingRecording_Refuses()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapInputRecordTool().Execute(JsonNode.NewObject().Set("action", "stop"));
            });
            StringAssert.Contains("No recording is in progress", ex.Message);
        }

        [Test]
        public void ReplayTool_Stop_WithNothingReplaying_Refuses()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapInputReplayTool().Execute(JsonNode.NewObject().Set("action", "stop"));
            });
            StringAssert.Contains("No replay is in progress", ex.Message);
        }

        [Test]
        public void RecordTool_Status_WithNothingRecorded_ReportsIdle()
        {
            JsonNode content = new UapInputRecordTool().Execute(JsonNode.NewObject().Set("action", "status"));
            string text = content[0]["text"].AsString(string.Empty);
            StringAssert.Contains("\"state\":\"idle\"", text);
        }

        [Test]
        public void ReplayTool_Status_WithNothingReplayed_ReportsIdle()
        {
            JsonNode content = new UapInputReplayTool().Execute(JsonNode.NewObject().Set("action", "status"));
            string text = content[0]["text"].AsString(string.Empty);
            StringAssert.Contains("\"state\":\"idle\"", text);
        }

        [Test]
        public void RecordTool_Start_MaxSecondsOutOfRange_RefusesBeforePlayModeCheck()
        {
            // Argument-shape errors are diagnosed before the Play Mode check
            // (UapInputRecorder.Start's ordering) precisely so this is
            // reachable in batch mode.
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapInputRecordTool().Execute(JsonNode.NewObject().Set("max_seconds", 0));
            });
            StringAssert.Contains("'max_seconds'", ex.Message);
        }

        [Test]
        public void RecordTool_Start_PathOutsideProject_RefusesBeforePlayModeCheck()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapInputRecordTool().Execute(JsonNode.NewObject().Set("path", "/etc/passwd"));
            });
            StringAssert.Contains("inside the project", ex.Message);
        }

        [Test]
        public void ReplayTool_Start_SpeedOutOfRange_RefusesBeforePlayModeCheck()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapInputReplayTool().Execute(JsonNode.NewObject().Set("path", "somewhere.json").Set("speed", 10));
            });
            StringAssert.Contains("'speed'", ex.Message);
        }

        [Test]
        public void ReplayTool_Start_MissingPath_RefusesBeforePlayModeCheck()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapInputReplayTool().Execute(JsonNode.NewObject());
            });
            StringAssert.Contains("'path' is required", ex.Message);
        }

        [Test]
        public void RecordTool_Execute_UnknownAction_RefusesByName()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapInputRecordTool().Execute(JsonNode.NewObject().Set("action", "pause"));
            });
            StringAssert.Contains("start, stop, status", ex.Message);
        }

        [Test]
        public void ReplayTool_Execute_UnknownAction_RefusesByName()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapInputReplayTool().Execute(JsonNode.NewObject().Set("action", "rewind"));
            });
            StringAssert.Contains("start, stop, status", ex.Message);
        }

        // -- Metadata sanity ----------------------------------------------------

        [Test]
        public void Tools_AreNotReadOnly()
        {
            Assert.IsFalse(new UapInputRecordTool().ReadOnly);
            Assert.IsFalse(new UapInputReplayTool().ReadOnly);
        }

        [Test]
        public void Tools_ModuleIsEditor()
        {
            Assert.AreEqual("editor", new UapInputRecordTool().Module);
            Assert.AreEqual("editor", new UapInputReplayTool().Module);
        }
    }
}
