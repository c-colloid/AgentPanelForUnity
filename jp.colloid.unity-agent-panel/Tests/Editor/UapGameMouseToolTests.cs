using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_game_mouse (design note docs/design-notes/2026-09-22-uloop-
    /// phase1.md section 2.2).
    ///
    /// What is NOT tested here, on purpose: a click/move/scroll the game
    /// observes through Mouse.current, and a dry_run hit on real scene
    /// geometry. Those need Play Mode with the Input System's Mouse device,
    /// or a scene with a tagged MainCamera and colliders, and the CI host
    /// projects have neither -- so the cases cover the argument rules
    /// (pure, also pinned in ci/SmokeTests), the refusals that must be
    /// right outside Play Mode, and dry_run's own refusals (bad
    /// coordinates, no Camera.main). The real injection and a real hit are
    /// verified by hand in a sandbox project.
    /// </summary>
    [TestFixture]
    public class UapGameMouseToolTests
    {
        [SetUp]
        public void ResetHeld()
        {
            UapGameMouseTool.ResetHeldForTests();
        }

        [Test]
        public void ParseAction_DefaultsToClick_AcceptsAliases_RefusesUnknown()
        {
            Assert.AreEqual(UapGameMouseAction.Click, UapGameMousePolicy.ParseAction(null));
            Assert.AreEqual(UapGameMouseAction.Click, UapGameMousePolicy.ParseAction(""));
            Assert.AreEqual(UapGameMouseAction.LongPress, UapGameMousePolicy.ParseAction("long_press"));
            Assert.AreEqual(UapGameMouseAction.LongPress, UapGameMousePolicy.ParseAction("LongPress"));
            Assert.AreEqual(UapGameMouseAction.Move, UapGameMousePolicy.ParseAction("move"));
            Assert.AreEqual(UapGameMouseAction.MoveDelta, UapGameMousePolicy.ParseAction("move-delta"));
            Assert.AreEqual(UapGameMouseAction.Scroll, UapGameMousePolicy.ParseAction("Scroll"));
            var ex = Assert.Throws<ArgumentException>(delegate { UapGameMousePolicy.ParseAction("hover"); });
            StringAssert.Contains("click, long_press, move, move_delta, scroll", ex.Message);
        }

        [Test]
        public void ToDeviceDelta_FlipsYOnly()
        {
            double deviceDx, deviceDy;
            UapGameMousePolicy.ToDeviceDelta(12, 8, out deviceDx, out deviceDy);
            Assert.AreEqual(12, deviceDx, 1e-9);
            Assert.AreEqual(-8, deviceDy, 1e-9);
            UapGameMousePolicy.ToDeviceDelta(-5, -3, out deviceDx, out deviceDy);
            Assert.AreEqual(-5, deviceDx, 1e-9);
            Assert.AreEqual(3, deviceDy, 1e-9);
        }

        [Test]
        public void ValidateDelta_RefusesNonFiniteNumbers()
        {
            string error;
            Assert.IsTrue(UapGameMousePolicy.ValidateDelta(0, 0, "dx", "dy", out error), error);
            Assert.IsTrue(UapGameMousePolicy.ValidateDelta(-100, 250, "dx", "dy", out error), error);
            Assert.IsFalse(UapGameMousePolicy.ValidateDelta(double.NaN, 0, "dx", "dy", out error));
            Assert.IsFalse(UapGameMousePolicy.ValidateDelta(0, double.PositiveInfinity, "dx", "dy", out error));
            StringAssert.Contains("dx", error);
            StringAssert.Contains("dy", error);
        }

        [Test]
        public void DurationProgress_InstantWhenDurationIsZeroOrLess_ClampedOtherwise()
        {
            Assert.AreEqual(1.0, UapGameMousePolicy.DurationProgress(0, 0), 1e-9);
            Assert.AreEqual(1.0, UapGameMousePolicy.DurationProgress(0, -1), 1e-9);
            Assert.AreEqual(0.5, UapGameMousePolicy.DurationProgress(0.25, 0.5), 1e-9);
            Assert.AreEqual(1.0, UapGameMousePolicy.DurationProgress(10, 0.5), 1e-9);
            Assert.AreEqual(0.0, UapGameMousePolicy.DurationProgress(-1, 0.5), 1e-9);
        }

        [Test]
        public void ValidateMaxDistance_AllowsPositiveAndInfinity_RefusesZeroNegativeOrNaN()
        {
            string error;
            Assert.IsTrue(UapGameMousePolicy.ValidateMaxDistance(1, out error), error);
            Assert.IsTrue(UapGameMousePolicy.ValidateMaxDistance(double.PositiveInfinity, out error), error);
            Assert.IsFalse(UapGameMousePolicy.ValidateMaxDistance(0, out error));
            Assert.IsFalse(UapGameMousePolicy.ValidateMaxDistance(-5, out error));
            Assert.IsFalse(UapGameMousePolicy.ValidateMaxDistance(double.NaN, out error));
        }

        [Test]
        public void Execute_OutsidePlayMode_RefusesAndNamesPlayModeTool()
        {
            // The batch-mode test run is never in Play Mode.
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject());
            });
            StringAssert.Contains("uap_play_mode", ex.Message);
            CollectionAssert.IsEmpty(UapGameMouseTool.HeldButtonNamesForTests(), "a refused call must not record a held button");
        }

        [Test]
        public void Execute_BadArguments_AreReportedBeforeThePlayStateCheck()
        {
            // An argument mistake is reported as such even outside Play
            // Mode, so an agent fixes the call once instead of twice
            // (mirrors uap_game_pointer / uap_game_key).
            var unknownAction = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject().Set("action", "hover"));
            });
            StringAssert.Contains("Unknown action", unknownAction.Message);

            var badButton = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject().Set("button", "back"));
            });
            StringAssert.Contains("Unknown button", badButton.Message);

            var badDuration = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject()
                    .Set("action", "long_press").Set("duration", UapGameInputPolicy.MaxHoldSeconds + 5));
            });
            StringAssert.Contains("duration", badDuration.Message);

            var missingXy = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject().Set("action", "move"));
            });
            StringAssert.Contains("required", missingXy.Message);

            var missingDxDy = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject().Set("action", "move_delta"));
            });
            StringAssert.Contains("required", missingDxDy.Message);

            var missingDxDyScroll = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject().Set("action", "scroll").Set("dx", 1));
            });
            StringAssert.Contains("required", missingDxDyScroll.Message);
        }

        [Test]
        public void InputSystemAccess_LoadsOrNamesThePackage()
        {
            // The CI host projects have no com.unity.inputsystem; a
            // developer's sandbox may. Either way the answer must be usable,
            // and the "missing" answer must point at the alternatives.
            string error;
            bool loaded = UapInputSystemMouseAccess.TryLoad(out error);
            if (!loaded)
            {
                StringAssert.Contains("com.unity.inputsystem", error);
                StringAssert.Contains("uap_game_pointer", error);
                StringAssert.Contains("dry_run", error);
            }
            else
            {
                Assert.IsNull(error);
            }
        }

        [Test]
        public void DryRun_RequiresXAndY()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject().Set("dry_run", true));
            });
            StringAssert.Contains("required", ex.Message);
        }

        [Test]
        public void DryRun_RefusesAnOutOfRangeMaxDistance()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameMouseTool().Execute(JsonNode.NewObject()
                    .Set("dry_run", true).Set("x", 10).Set("y", 10).Set("max_distance", -1));
            });
            StringAssert.Contains("max_distance", ex.Message);
        }

        [Test]
        public void DryRun_NeedsNoPlayModeOrInputSystem()
        {
            // dry_run must not throw the "uap_play_mode" refusal Execute()
            // gives every other action outside Play Mode -- whatever it
            // does throw (a missing Camera.main in an empty batch-mode
            // scene) or return, it must not be that one.
            try
            {
                JsonNode result = new UapGameMouseTool().Execute(JsonNode.NewObject()
                    .Set("dry_run", true).Set("x", 10).Set("y", 10));
                Assert.IsNotNull(result);
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains("Camera.main", ex.Message);
                Assert.That(ex.Message, Does.Not.Contain("uap_play_mode"));
            }
        }
    }
}
