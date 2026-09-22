using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_console_clear (design note docs/design-notes/2026-09-21-console-
    /// clear-and-game-view-size.md section 2). The load-bearing behaviour is
    /// that it clears ALL THREE views of the Console -- the window, the
    /// agent's log buffer and the panel's error chip -- and that it says
    /// which of them it actually managed to clear.
    ///
    /// Capture is process-global, so SetUp remembers whether a live agent
    /// session had it running and TearDown restores exactly that.
    /// </summary>
    [TestFixture]
    public class UapConsoleClearToolTests
    {
        private bool _wasCapturing;

        [SetUp]
        public void SetUp()
        {
            _wasCapturing = UapConsoleLogStore.Capturing;
            UapConsoleLogBuffer.Install();
            UapConsoleLogStore.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (_wasCapturing)
            {
                UapConsoleLogBuffer.Install();
            }
            else
            {
                UapConsoleLogBuffer.Uninstall();
            }
            UapConsoleLogStore.Clear();
        }

        [Test]
        public void Execute_EmptiesTheBufferUapConsoleLogsReads()
        {
            UapConsoleLogStore.Capture("error", "before the clear", "at Thing.Do()");
            Assert.Greater(UapConsoleLogStore.Snapshot().Count, 0, "precondition: something is buffered");

            string text = new UapConsoleClearTool().Execute(JsonNode.NewObject())[0]["text"].AsString();

            Assert.AreEqual(0, UapConsoleLogStore.Snapshot().Count);
            JsonNode reply = JsonParser.Parse(text);
            Assert.GreaterOrEqual(reply["bufferedEntriesDiscarded"].AsInt(-1), 1);
            Assert.IsTrue(reply["consoleWindowCleared"].IsBool,
                "the reply must state whether the Console WINDOW was cleared, not just the buffer");
        }

        [Test]
        public void Execute_TakesNoArguments()
        {
            JsonNode schema = new UapConsoleClearTool().InputSchema;
            Assert.IsTrue(schema["properties"].IsObject);
            Assert.AreEqual(0, schema["properties"].Count,
                "an argument-less tool must not advertise arguments");
            Assert.IsFalse(schema["additionalProperties"].AsBool(true));
        }

        [Test]
        public void Describe_SaysWhichHalfHappened()
        {
            StringAssert.Contains("Cleared the Unity Console window",
                UapConsoleClearTool.Describe(true, 3));
            StringAssert.Contains("could NOT be cleared",
                UapConsoleClearTool.Describe(false, 3));
            StringAssert.Contains("still on screen",
                UapConsoleClearTool.Describe(false, 3),
                "the agent must not conclude the user's Console is empty when it is not");
        }

        [Test]
        public void Describe_CountsOneEntryInTheSingular()
        {
            StringAssert.Contains("1 entry", UapConsoleClearTool.Describe(true, 1));
            StringAssert.Contains("2 entries", UapConsoleClearTool.Describe(true, 2));
            StringAssert.Contains("0 entries", UapConsoleClearTool.Describe(true, 0));
        }
    }
}
