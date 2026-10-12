using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_game_pointer (design note docs/design-notes/2026-09-22-game-
    /// input-tools.md section 3).
    ///
    /// What is NOT tested here, on purpose: a click that reaches a Button.
    /// That needs Play Mode, a Canvas with a GraphicRaycaster and an
    /// EventSystem, none of which a batch-mode EditMode run has -- so the
    /// cases cover the argument rules (pure, also pinned in
    /// ci/SmokeTests), above all the top-left to bottom-left flip that
    /// would otherwise click the wrong widget, and the refusals that must
    /// be right outside Play Mode. The real click is verified by hand in a
    /// sandbox project (section 6 of the note).
    /// </summary>
    [TestFixture]
    public class UapGamePointerToolTests
    {
        [Test]
        public void ParseAction_DefaultsToClick_AcceptsAliases_RefusesUnknown()
        {
            Assert.AreEqual(UapGamePointerAction.Click, UapGamePointerPolicy.ParseAction(null));
            Assert.AreEqual(UapGamePointerAction.Click, UapGamePointerPolicy.ParseAction(""));
            Assert.AreEqual(UapGamePointerAction.LongPress, UapGamePointerPolicy.ParseAction("long_press"));
            Assert.AreEqual(UapGamePointerAction.LongPress, UapGamePointerPolicy.ParseAction("LongPress"));
            Assert.AreEqual(UapGamePointerAction.Drag, UapGamePointerPolicy.ParseAction(" drag "));
            var ex = Assert.Throws<ArgumentException>(delegate { UapGamePointerPolicy.ParseAction("hover"); });
            StringAssert.Contains("click, long_press, drag", ex.Message);
        }

        [Test]
        public void ParseButton_DefaultsToLeft_RefusesUnknown()
        {
            Assert.AreEqual(UapGamePointerButton.Left, UapGamePointerPolicy.ParseButton(null));
            Assert.AreEqual(UapGamePointerButton.Right, UapGamePointerPolicy.ParseButton("Right"));
            Assert.AreEqual(UapGamePointerButton.Middle, UapGamePointerPolicy.ParseButton("middle"));
            Assert.Throws<ArgumentException>(delegate { UapGamePointerPolicy.ParseButton("back"); });
        }

        [Test]
        public void ToScreenSpace_FlipsYToBottomLeftOrigin()
        {
            double sx, sy;
            string error;
            // 10 px below the top edge of a 1080-high view is 1070 above the bottom.
            Assert.IsTrue(UapGamePointerPolicy.TryToScreenSpace(100, 10, 1920, 1080, false, out sx, out sy, out error), error);
            Assert.AreEqual(100, sx, 1e-9);
            Assert.AreEqual(1070, sy, 1e-9);
            // The corners map onto each other.
            Assert.IsTrue(UapGamePointerPolicy.TryToScreenSpace(0, 0, 1920, 1080, false, out sx, out sy, out error));
            Assert.AreEqual(1080, sy, 1e-9);
            Assert.IsTrue(UapGamePointerPolicy.TryToScreenSpace(1920, 1080, 1920, 1080, false, out sx, out sy, out error));
            Assert.AreEqual(0, sy, 1e-9);
        }

        [Test]
        public void ToScreenSpace_NormalizedScalesByTheView()
        {
            double sx, sy;
            string error;
            Assert.IsTrue(UapGamePointerPolicy.TryToScreenSpace(0.5, 0.25, 800, 600, true, out sx, out sy, out error), error);
            Assert.AreEqual(400, sx, 1e-9);
            Assert.AreEqual(450, sy, 1e-9);
            Assert.IsFalse(UapGamePointerPolicy.TryToScreenSpace(1.5, 0.25, 800, 600, true, out sx, out sy, out error));
            StringAssert.Contains("0 to 1", error);
        }

        [Test]
        public void ToScreenSpace_RefusesOutsideTheView_AndNamesItsSize()
        {
            double sx, sy;
            string error;
            Assert.IsFalse(UapGamePointerPolicy.TryToScreenSpace(2000, 10, 1920, 1080, false, out sx, out sy, out error));
            StringAssert.Contains("1920 x 1080", error);
            Assert.IsFalse(UapGamePointerPolicy.TryToScreenSpace(10, -1, 1920, 1080, false, out sx, out sy, out error));
            Assert.IsFalse(UapGamePointerPolicy.TryToScreenSpace(10, 10, 0, 0, false, out sx, out sy, out error));
            StringAssert.Contains("Game View", error);
        }

        [Test]
        public void DragProgress_InstantWhenSpeedIsZero_ClampedOtherwise()
        {
            Assert.AreEqual(1.0, UapGamePointerPolicy.DragProgress(0, 500, 0), 1e-9);
            Assert.AreEqual(1.0, UapGamePointerPolicy.DragProgress(0, 0, 2000), 1e-9);
            Assert.AreEqual(0.5, UapGamePointerPolicy.DragProgress(0.25, 1000, 2000), 1e-9);
            Assert.AreEqual(1.0, UapGamePointerPolicy.DragProgress(10, 1000, 2000), 1e-9);
            Assert.AreEqual(0.0, UapGamePointerPolicy.DragProgress(-1, 1000, 2000), 1e-9);
        }

        [Test]
        public void ValidateHold_RefusesMoreThanTheDispatcherBudgetAllows()
        {
            string error;
            Assert.IsTrue(UapGameInputPolicy.ValidateHold(0, "duration", out error), error);
            Assert.IsTrue(UapGameInputPolicy.ValidateHold(UapGameInputPolicy.MaxHoldSeconds, "duration", out error), error);
            Assert.IsFalse(UapGameInputPolicy.ValidateHold(UapGameInputPolicy.MaxHoldSeconds + 1, "duration", out error));
            StringAssert.Contains("down", error);
            Assert.IsFalse(UapGameInputPolicy.ValidateHold(-0.5, "duration", out error));
            Assert.IsFalse(UapGameInputPolicy.ValidateHold(double.NaN, "duration", out error));
        }

        [Test]
        public void CheckPlayState_NamesTheToolThatFixesEachState()
        {
            string error;
            var edit = new UapPlayModeSnapshot { IsPlaying = false };
            Assert.IsFalse(UapGameInputPolicy.CheckPlayState(edit, false, out error));
            StringAssert.Contains("uap_play_mode action:start", error);

            var paused = new UapPlayModeSnapshot { IsPlaying = true, IsPaused = true };
            Assert.IsFalse(UapGameInputPolicy.CheckPlayState(paused, false, out error));
            StringAssert.Contains("resume", error);
            Assert.IsTrue(UapGameInputPolicy.CheckPlayState(paused, true, out error), "recovery actions work while paused");

            var compiling = new UapPlayModeSnapshot { IsPlaying = true, IsCompiling = true };
            Assert.IsFalse(UapGameInputPolicy.CheckPlayState(compiling, true, out error));
            StringAssert.Contains("compiling", error);

            var playing = new UapPlayModeSnapshot { IsPlaying = true };
            Assert.IsTrue(UapGameInputPolicy.CheckPlayState(playing, false, out error));
            Assert.IsNull(error);
        }

        [Test]
        public void Execute_OutsidePlayMode_RefusesAndNamesPlayModeTool()
        {
            // The batch-mode test run is never in Play Mode.
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGamePointerTool().Execute(JsonNode.NewObject().Set("x", 10).Set("y", 10));
            });
            StringAssert.Contains("uap_play_mode", ex.Message);
        }

        [Test]
        public void Execute_BadArguments_AreReportedBeforeThePlayStateCheck()
        {
            // An argument mistake is reported as such even outside Play Mode,
            // so an agent fixes the call once instead of twice.
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGamePointerTool().Execute(JsonNode.NewObject().Set("action", "hover"));
            });
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGamePointerTool().Execute(JsonNode.NewObject()
                    .Set("action", "long_press").Set("duration", UapGameInputPolicy.MaxHoldSeconds + 5));
            });
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGamePointerTool().Execute(JsonNode.NewObject().Set("action", "drag").Set("speed", -1));
            });
        }

        [Test]
        public void UguiAccess_LoadsOrNamesThePackage()
        {
            // The CI host projects have no com.unity.ugui; a developer's
            // sandbox usually does. Either way the answer must be usable.
            string error;
            bool loaded = UapUguiEventAccess.TryLoad(out error);
            if (!loaded)
            {
                StringAssert.Contains("com.unity.ugui", error);
            }
            else
            {
                Assert.IsNull(error);
            }
        }
    }
}
