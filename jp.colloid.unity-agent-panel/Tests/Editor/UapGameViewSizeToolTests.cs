using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_game_view_size (design note docs/design-notes/2026-09-21-console-
    /// clear-and-game-view-size.md section 3).
    ///
    /// What is NOT tested here, on purpose: the successful 'set' path. It
    /// would resize the Game View of whoever runs the suite in an
    /// interactive editor, and the batch-mode runner has no Game View to
    /// resize at all -- so the cases below cover the argument rules (pure,
    /// also pinned in ci/SmokeTests) and the two answers that must be right
    /// when no Game View is open: 'get' reports open:false, 'set' fails and
    /// says how to fix it. The real resize is verified by hand in a
    /// sandbox project (section 5 of the note).
    /// </summary>
    [TestFixture]
    public class UapGameViewSizeToolTests
    {
        [Test]
        public void ValidateDimensions_MissingValuesReadAsMissing_NotOutOfRange()
        {
            string error;
            Assert.IsFalse(UapGameViewSizePolicy.ValidateDimensions(0, 1080, out error));
            StringAssert.Contains("required", error);
            Assert.IsFalse(UapGameViewSizePolicy.ValidateDimensions(1920, 0, out error));
            StringAssert.Contains("required", error);
        }

        [Test]
        public void ValidateDimensions_RefusesOutOfRange_AndNamesTheRange()
        {
            string error;
            Assert.IsFalse(UapGameViewSizePolicy.ValidateDimensions(
                UapGameViewSizePolicy.MinDimension - 1, 1080, out error));
            StringAssert.Contains(UapGameViewSizePolicy.MinDimension.ToString(), error);
            Assert.IsFalse(UapGameViewSizePolicy.ValidateDimensions(
                1920, UapGameViewSizePolicy.MaxDimension + 1, out error));
            StringAssert.Contains(UapGameViewSizePolicy.MaxDimension.ToString(), error);
        }

        [Test]
        public void ValidateDimensions_AcceptsTheUsualSizes()
        {
            string error;
            Assert.IsTrue(UapGameViewSizePolicy.ValidateDimensions(1920, 1080, out error), error);
            Assert.IsNull(error);
            Assert.IsTrue(UapGameViewSizePolicy.ValidateDimensions(
                UapGameViewSizePolicy.MinDimension, UapGameViewSizePolicy.MaxDimension, out error), error);
        }

        [Test]
        public void CustomSizeLabel_NamesThePanelSoTheEntryIsRecognizable()
        {
            string label = UapGameViewSizePolicy.CustomSizeLabel(1920, 1080);
            StringAssert.Contains("1920", label);
            StringAssert.Contains("1080", label);
            StringAssert.Contains("Agent Panel", label);
        }

        [Test]
        public void DescribeSize_UsesTheEditorsOwnLabelWhenThereIsOne()
        {
            var withLabel = new UapGameViewSizeInfo
            {
                Width = 1920, Height = 1080, DisplayText = "Full HD (1920x1080)"
            };
            Assert.AreEqual("1920 x 1080 (Full HD (1920x1080))", UapGameViewSizeTool.DescribeSize(withLabel));

            var bare = new UapGameViewSizeInfo { Width = 800, Height = 600 };
            Assert.AreEqual("800 x 600", UapGameViewSizeTool.DescribeSize(bare));
        }

        [Test]
        public void Execute_Set_WithBadDimensions_IsRefusedBeforeTouchingTheEditor()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameViewSizeTool().Execute(JsonNode.NewObject()
                    .Set("action", "set").Set("width", 4).Set("height", 4));
            });
            StringAssert.Contains(UapGameViewSizePolicy.MinDimension.ToString(), error.Message);
        }

        [Test]
        public void Execute_UnknownAction_IsRefusedByName()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameViewSizeTool().Execute(JsonNode.NewObject().Set("action", "resize"));
            });
            StringAssert.Contains("get, set", error.Message);
        }

        [Test]
        public void Execute_Get_AnswersEvenWithNoGameViewOpen()
        {
            string text = new UapGameViewSizeTool().Execute(JsonNode.NewObject())[0]["text"].AsString();
            JsonNode reply = JsonParser.Parse(text);

            Assert.IsTrue(reply["open"].IsBool, "the reply must say whether a Game View is open");
            if (reply["open"].AsBool(false))
            {
                // An interactive editor with a Game View: the size fields
                // must be there instead.
                Assert.IsTrue(reply["width"].AsInt(0) > 0, text);
                Assert.IsTrue(reply["height"].AsInt(0) > 0, text);
            }
            else
            {
                StringAssert.Contains("Game View", reply["message"].AsString(string.Empty));
            }
        }

        [Test]
        public void Execute_Set_WithNoGameViewOpen_FailsAndSaysHowToFixIt()
        {
            if (UapScreenCapture.FindOpenGameView() != null)
            {
                Assert.Ignore("A Game View is open; this case covers the closed-window answer, and"
                    + " resizing the runner's Game View is not this fixture's business.");
            }
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameViewSizeTool().Execute(JsonNode.NewObject()
                    .Set("action", "set").Set("width", 1920).Set("height", 1080));
            });
            StringAssert.Contains("Game View", error.Message);
        }
    }
}
