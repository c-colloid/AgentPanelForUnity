using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Editor-loop throttle notice (design note 2026-09-06 section 7): a
    /// live report had uap_query_hierarchy die as a bare "The operation
    /// timed out." while Play Mode ran with the Editor unfocused, with no
    /// hint in any uap_* response about why. These pin the three pieces:
    /// the pure Editor-state -> notice decision, the dispatcher's timeout
    /// message (stall age + hint, shorter budget while throttled), and
    /// the request handler appending the notice to successful results.
    /// </summary>
    [TestFixture]
    public class UapOpsThrottleNoticeTests
    {
        private const string Token = "test-token";

        [Test]
        public void ComputeThrottleNotice_OnlyPlayModeUnfocusedWithoutRunInBackground()
        {
            Assert.AreEqual(UapOpsServer.PlayModeUnfocusedNotice,
                UapOpsServer.ComputeThrottleNotice(true, false, false));
            Assert.IsNull(UapOpsServer.ComputeThrottleNotice(true, true, false), "focused: ticks normally");
            Assert.IsNull(UapOpsServer.ComputeThrottleNotice(true, false, true), "run in background: ticks normally");
            Assert.IsNull(UapOpsServer.ComputeThrottleNotice(false, false, false), "edit mode: not throttled by Play");
        }

        [Test]
        public void DescribeStall_NamesTheTickAge_AndAppendsTheHint()
        {
            string text = UapMainThreadDispatcher.DescribeStall(3.25, "Focus the window.");
            StringAssert.Contains("last ticked 3.3 s ago", text);
            StringAssert.EndsWith("Focus the window.", text);
            StringAssert.Contains("has not ticked since", UapMainThreadDispatcher.DescribeStall(-1, null));
        }

        [Test]
        public void Timeout_WhileThrottled_UsesTheShorterBudget_AndSaysWhy()
        {
            var dispatcher = new UapMainThreadDispatcher
            {
                DefaultTimeoutMillis = 5000,
                ThrottledTimeoutMillis = 50,
                StallHintProvider = () => "Play Mode is running unfocused."
            };
            var tool = new StubUapTool();
            DateTime started = DateTime.UtcNow;
            TimeoutException ex = Assert.Throws<TimeoutException>(
                delegate { dispatcher.Execute(tool, JsonNode.NewObject()); });
            Assert.Less((DateTime.UtcNow - started).TotalMilliseconds, 2000,
                "the throttled budget (50 ms), not the default (5 s), must apply");
            // 2026-09-08: the first sentence now names the outcome (the
            // pump never ran, so "was never started") instead of the
            // generic "timed out waiting" -- the throttle hint that this
            // test is about still follows it.
            StringAssert.Contains("was never started", ex.Message);
            StringAssert.Contains("has not ticked since", ex.Message);
            StringAssert.Contains("Play Mode is running unfocused.", ex.Message);
        }

        [Test]
        public void Timeout_WhenNotThrottled_KeepsTheDefaultBudget_AndReportsTickAge()
        {
            var dispatcher = new UapMainThreadDispatcher
            {
                DefaultTimeoutMillis = 50,
                ThrottledTimeoutMillis = 5000,
                StallHintProvider = () => null
            };
            dispatcher.Pump();
            TimeoutException ex = Assert.Throws<TimeoutException>(
                delegate { dispatcher.Execute(new StubUapTool(), JsonNode.NewObject()); });
            StringAssert.Contains("last ticked", ex.Message);
            StringAssert.DoesNotContain("Play Mode", ex.Message);
            Assert.GreaterOrEqual(dispatcher.SecondsSinceLastPump, 0);
        }

        [Test]
        public void Pump_StampsTheLastTickTime()
        {
            var dispatcher = new UapMainThreadDispatcher();
            Assert.AreEqual(0, dispatcher.LastPumpUtcTicks);
            Assert.AreEqual(-1, dispatcher.SecondsSinceLastPump);
            dispatcher.Pump();
            Assert.Greater(dispatcher.LastPumpUtcTicks, 0);
        }

        [Test]
        public void ToolsCall_AppendsTheNotice_OnlyWhileOneIsActive()
        {
            string notice = null;
            ToolRegistry registry = ToolRegistry.CreateDefault();
            var handler = new UapOpsRequestHandler(registry, () => Token, () => new[] { "core" },
                new DirectUapToolExecutor(), null, () => notice);

            JsonNode body = Call(handler);
            Assert.AreEqual(1, body["result"]["content"].Count, "no notice: content is untouched");

            notice = UapOpsServer.PlayModeUnfocusedNotice;
            body = Call(handler);
            Assert.AreEqual(2, body["result"]["content"].Count);
            Assert.AreEqual(UapPingTool.DefaultMessage, body["result"]["content"][0]["text"].AsString(),
                "the tool's own result stays first");
            StringAssert.StartsWith("Warning: ", body["result"]["content"][1]["text"].AsString());
            StringAssert.Contains("Focus the Unity Editor window", body["result"]["content"][1]["text"].AsString());
            Assert.IsFalse(body["result"]["isError"].AsBool(false), "a notice is not an error");
        }

        private static JsonNode Call(UapOpsRequestHandler handler)
        {
            JsonNode request = JsonNode.NewObject().Set("jsonrpc", "2.0").Set("id", 1L)
                .Set("method", "tools/call")
                .Set("params", JsonNode.NewObject().Set("name", "uap_ping"));
            UapOpsHttpResponse response = handler.Handle(new UapOpsHttpRequest
            {
                Method = "POST",
                Path = "/mcp",
                AuthorizationHeader = "Bearer " + Token,
                Body = JsonWriter.Write(request)
            });
            return JsonParser.Parse(response.Body);
        }
    }
}
