using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_game_key (design note docs/design-notes/2026-09-22-game-
    /// input-tools.md section 4).
    ///
    /// What is NOT tested here, on purpose: a press the game observes.
    /// That needs Play Mode and the Input System package with a Keyboard
    /// device, and the CI host projects have neither -- so the cases cover
    /// the argument rules (pure, also pinned in ci/SmokeTests), above all
    /// key-name normalisation, and the refusals that must be right outside
    /// Play Mode. The real press is verified by hand in a sandbox project
    /// (section 6 of the note).
    /// </summary>
    [TestFixture]
    public class UapGameKeyToolTests
    {
        [SetUp]
        public void ResetHeld()
        {
            UapGameKeyTool.ResetHeldForTests();
        }

        [Test]
        public void ParseAction_DefaultsToPress_AcceptsAliases_RefusesUnknown()
        {
            Assert.AreEqual(UapGameKeyAction.Press, UapGameKeyPolicy.ParseAction(null));
            Assert.AreEqual(UapGameKeyAction.Press, UapGameKeyPolicy.ParseAction("tap"));
            Assert.AreEqual(UapGameKeyAction.Down, UapGameKeyPolicy.ParseAction("KeyDown"));
            Assert.AreEqual(UapGameKeyAction.Down, UapGameKeyPolicy.ParseAction("hold"));
            Assert.AreEqual(UapGameKeyAction.Up, UapGameKeyPolicy.ParseAction("key-up"));
            Assert.AreEqual(UapGameKeyAction.ReleaseAll, UapGameKeyPolicy.ParseAction("release_all"));
            Assert.AreEqual(UapGameKeyAction.ReleaseAll, UapGameKeyPolicy.ParseAction("ReleaseAll"));
            var ex = Assert.Throws<ArgumentException>(delegate { UapGameKeyPolicy.ParseAction("type"); });
            StringAssert.Contains("press, down, up, release_all", ex.Message);
        }

        [TestCase("W", "W")]
        [TestCase("w", "W")]
        [TestCase("space", "Space")]
        [TestCase("Space", "Space")]
        [TestCase("LEFTSHIFT", "LeftShift")]
        [TestCase("left_shift", "LeftShift")]
        [TestCase("Left Shift", "LeftShift")]
        [TestCase("shift", "LeftShift")]
        [TestCase("ctrl", "LeftCtrl")]
        [TestCase("alt", "LeftAlt")]
        [TestCase("esc", "Escape")]
        [TestCase("return", "Enter")]
        [TestCase("1", "Digit1")]
        [TestCase("0", "Digit0")]
        [TestCase("num5", "Numpad5")]
        [TestCase("up", "UpArrow")]
        [TestCase("ArrowLeft", "LeftArrow")]
        [TestCase("f5", "F5")]
        [TestCase("del", "Delete")]
        public void NormalizeKeyName_AcceptsTheSpellingsAnAgentWrites(string typed, string canonical)
        {
            string error;
            Assert.AreEqual(canonical, UapGameKeyPolicy.NormalizeKeyName(typed, out error));
            Assert.IsNull(error);
        }

        [Test]
        public void NormalizeKeyName_UnknownNameSuggestsNearbyKeys()
        {
            string error;
            Assert.IsNull(UapGameKeyPolicy.NormalizeKeyName("Numpa", out error));
            StringAssert.Contains("Numpad", error);
            StringAssert.Contains("Did you mean", error);
            Assert.IsNull(UapGameKeyPolicy.NormalizeKeyName("Fire", out error));
            StringAssert.Contains("Unknown key 'Fire'", error);
        }

        [Test]
        public void NormalizeKeyName_EmptyIsReportedAsRequired()
        {
            string error;
            Assert.IsNull(UapGameKeyPolicy.NormalizeKeyName(null, out error));
            StringAssert.Contains("required", error);
            Assert.IsNull(UapGameKeyPolicy.NormalizeKeyName("  ", out error));
            StringAssert.Contains("required", error);
        }

        [Test]
        public void KnownKeys_HaveNoDuplicates_AndCoverTheGameKeys()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in UapGameKeyPolicy.KnownKeys)
            {
                Assert.IsTrue(seen.Add(key), "duplicate key name: " + key);
            }
            CollectionAssert.Contains(UapGameKeyPolicy.KnownKeys, "Space");
            CollectionAssert.Contains(UapGameKeyPolicy.KnownKeys, "Digit0");
            CollectionAssert.Contains(UapGameKeyPolicy.KnownKeys, "LeftShift");
            CollectionAssert.Contains(UapGameKeyPolicy.KnownKeys, "F12");
        }

        [Test]
        public void CheckHeldState_RefusesTheCallsThatMeanTheAgentIsWrongAboutTheKey()
        {
            string error;
            Assert.IsFalse(UapGameKeyPolicy.CheckHeldState(UapGameKeyAction.Down, true, "W", out error));
            StringAssert.Contains("already held", error);
            Assert.IsFalse(UapGameKeyPolicy.CheckHeldState(UapGameKeyAction.Press, true, "W", out error));
            Assert.IsFalse(UapGameKeyPolicy.CheckHeldState(UapGameKeyAction.Up, false, "W", out error));
            StringAssert.Contains("not held", error);
            Assert.IsTrue(UapGameKeyPolicy.CheckHeldState(UapGameKeyAction.Down, false, "W", out error));
            Assert.IsTrue(UapGameKeyPolicy.CheckHeldState(UapGameKeyAction.Up, true, "W", out error));
            Assert.IsTrue(UapGameKeyPolicy.CheckHeldState(UapGameKeyAction.ReleaseAll, true, null, out error));
        }

        [Test]
        public void Execute_OutsidePlayMode_RefusesAndNamesPlayModeTool()
        {
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameKeyTool().Execute(JsonNode.NewObject().Set("key", "Space"));
            });
            StringAssert.Contains("uap_play_mode", ex.Message);
            Assert.IsEmpty(UapGameKeyTool.HeldKeyNamesForTests(), "a refused call must not record a held key");
        }

        [Test]
        public void Execute_BadArguments_AreReportedBeforeThePlayStateCheck()
        {
            var unknownKey = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameKeyTool().Execute(JsonNode.NewObject().Set("key", "Fire"));
            });
            StringAssert.Contains("Unknown key", unknownKey.Message);
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGameKeyTool().Execute(JsonNode.NewObject().Set("action", "type").Set("key", "A"));
            });
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGameKeyTool().Execute(JsonNode.NewObject()
                    .Set("key", "A").Set("duration", UapGameInputPolicy.MaxHoldSeconds + 1));
            });
            var missingKey = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameKeyTool().Execute(JsonNode.NewObject().Set("action", "down"));
            });
            StringAssert.Contains("required", missingKey.Message);
        }

        [Test]
        public void InputSystemAccess_LoadsOrNamesThePackage()
        {
            // The CI host projects have no com.unity.inputsystem; a
            // developer's sandbox may. Either way the answer must be usable,
            // and the "missing" answer must point at the uGUI alternative.
            string error;
            bool loaded = UapInputSystemKeyboardAccess.TryLoad(out error);
            if (!loaded)
            {
                StringAssert.Contains("com.unity.inputsystem", error);
                StringAssert.Contains("uap_game_pointer", error);
            }
            else
            {
                Assert.IsNull(error);
            }
        }
    }
}
