using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Permission / AskUserQuestion round-trip tests driving AgentClient
    /// through FakeCliProcess with the REAL captured can_use_tool lines
    /// (permission_inbound.jsonl, askuser3_inbound.jsonl) and comparing
    /// every outbound line against the verified captures
    /// (permission_outbound.jsonl, askuser3_outbound.jsonl). Ground truth:
    /// 02b sections 2, 3 and 5.
    /// </summary>
    [TestFixture]
    public class PermissionFlowTests
    {
        private FakeCliProcess _fake;
        private AgentClient _client;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeCliProcess();
            _log = new List<string>();
            _client = new AgentClient(_fake, _log.Add);
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>Starts the client and makes it Ready with the fixture's own init line.</summary>
        private void MakeReadyFrom(string inboundFixture)
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project"
            });
            _fake.ScriptLine(FixtureLine(inboundFixture, "\"subtype\":\"init\""));
            PumpAll();
            Assert.AreEqual(AgentClientState.Ready, _client.State);
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        /// <summary>
        /// Replays the captured turn up to the permission prompt: sends the
        /// exact user text from the outbound fixture, then feeds the
        /// captured can_use_tool line. Returns the surfaced request.
        /// </summary>
        private ControlRequestMessage DriveToPermissionPrompt(string inboundFixture,
            string outboundFixture)
        {
            MakeReadyFrom(inboundFixture);
            _client.SendUserText(CapturedUserText(outboundFixture));

            ControlRequestMessage request = null;
            _client.PermissionRequested += delegate (ControlRequestMessage r) { request = r; };
            _fake.ScriptLine(FixtureLine(inboundFixture, "\"subtype\":\"can_use_tool\""));
            PumpAll();

            Assert.IsNotNull(request, "can_use_tool line must raise PermissionRequested");
            Assert.AreEqual(AgentClientState.WaitingPermission, _client.State);
            return request;
        }

        /// <summary>The user text of the captured outbound turn (fixture line 2).</summary>
        private static string CapturedUserText(string outboundFixture)
        {
            JsonNode node = ParseJson(FixtureLine(outboundFixture, "\"type\":\"user\""));
            string text = node["message"]["content"][0]["text"].AsString();
            Assert.IsNotNull(text, outboundFixture + " user line must carry text");
            return text;
        }

        /// <summary>First fixture line containing all the given substrings.</summary>
        private static string FixtureLine(string fixture, params string[] substrings)
        {
            foreach (string line in FixtureLoader.ReadLines(fixture))
            {
                bool all = true;
                foreach (string s in substrings)
                {
                    if (line.IndexOf(s, System.StringComparison.Ordinal) < 0)
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    return line;
                }
            }
            Assert.Fail("No line in " + fixture + " contains: " + string.Join(" + ", substrings));
            return null;
        }

        private static JsonNode ParseJson(string line)
        {
            JsonNode node;
            string error;
            Assert.IsTrue(JsonParser.TryParse(line, out node, out error),
                "line must parse: " + error);
            return node;
        }

        /// <summary>Structural JSON equality (same values, key order ignored).</summary>
        private static bool JsonDeepEquals(JsonNode a, JsonNode b)
        {
            if (a == null || b == null)
            {
                return ReferenceEquals(a, b);
            }
            if (a.Type != b.Type)
            {
                return false;
            }
            switch (a.Type)
            {
                case JsonNodeType.Null:
                    return true;
                case JsonNodeType.Bool:
                    return a.AsBool() == b.AsBool();
                case JsonNodeType.String:
                    return a.AsString(string.Empty) == b.AsString(string.Empty);
                case JsonNodeType.Number:
                    return a.AsString(string.Empty) == b.AsString(string.Empty)
                        || a.AsDouble().Equals(b.AsDouble());
                case JsonNodeType.Array:
                    if (a.Count != b.Count)
                    {
                        return false;
                    }
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (!JsonDeepEquals(a[i], b[i]))
                        {
                            return false;
                        }
                    }
                    return true;
                case JsonNodeType.Object:
                    if (a.Count != b.Count)
                    {
                        return false;
                    }
                    foreach (KeyValuePair<string, JsonNode> pair in a.Properties)
                    {
                        if (!b.HasKey(pair.Key) || !JsonDeepEquals(pair.Value, b[pair.Key]))
                        {
                            return false;
                        }
                    }
                    return true;
                default:
                    return false;
            }
        }

        private static void AssertJsonLineEquals(string expectedLine, string actualLine,
            string context)
        {
            Assert.IsTrue(JsonDeepEquals(ParseJson(expectedLine), ParseJson(actualLine)),
                context + "\nexpected: " + expectedLine + "\nactual:   " + actualLine);
        }

        private string LastWrittenLine()
        {
            Assert.IsNotEmpty(_fake.WrittenLines);
            return _fake.WrittenLines[_fake.WrittenLines.Count - 1];
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null
                && haystack.IndexOf(needle, System.StringComparison.Ordinal) >= 0;
        }

        // ------------------------------------------------------------------
        // Inbound parsing: what PermissionRequested surfaces
        // ------------------------------------------------------------------

        [Test]
        public void WritePermissionRequest_SurfacesAllParsedFields()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "permission_inbound.jsonl", "permission_outbound.jsonl");

            Assert.IsTrue(request.IsCanUseTool);
            Assert.AreEqual("be4de54a-f21c-46a8-a6c5-075c83c7067a", request.RequestId);
            Assert.AreEqual(request.RequestId, _client.PendingPermissionRequestId);

            CanUseToolRequest tool = request.CanUseTool;
            Assert.AreEqual("Write", tool.ToolName);
            Assert.AreEqual("Write", tool.DisplayName);
            Assert.AreEqual("panel_permission_test.txt", tool.Description);
            Assert.AreEqual("toolu_016eevHx2qYfHFzin6454Nb6", tool.ToolUseId);
            Assert.IsFalse(tool.RequiresUserInteraction);
            StringAssert.EndsWith("panel_permission_test.txt",
                tool.Input["file_path"].AsString());
            Assert.AreEqual("PERMISSION_ROUNDTRIP_OK", tool.Input["content"].AsString());

            // permission_suggestions is the "always allow" material.
            Assert.AreEqual(1, tool.PermissionSuggestions.Count);
            Assert.AreEqual("setMode", tool.PermissionSuggestions[0]["type"].AsString());
            Assert.AreEqual("acceptEdits", tool.PermissionSuggestions[0]["mode"].AsString());
            Assert.AreEqual("session",
                tool.PermissionSuggestions[0]["destination"].AsString());
        }

        [Test]
        public void AskUserQuestion_SurfacesInteractionFlag_AndParsedQuestions()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "askuser3_inbound.jsonl", "askuser3_outbound.jsonl");

            CanUseToolRequest tool = request.CanUseTool;
            Assert.AreEqual("AskUserQuestion", tool.ToolName);
            Assert.IsTrue(tool.RequiresUserInteraction,
                "requires_user_interaction distinguishes the question card");

            AskUserQuestionInput parsed = AskUserQuestionInput.FromInput(tool.Input);
            Assert.AreEqual(1, parsed.Questions.Count);
            AskUserQuestionInput.Question question = parsed.Questions[0];
            Assert.AreEqual("Which color do you prefer?", question.QuestionText);
            Assert.AreEqual("Color", question.Header);
            Assert.IsFalse(question.MultiSelect);
            Assert.AreEqual(2, question.Options.Count);
            Assert.AreEqual("Red", question.Options[0].Label);
            Assert.AreEqual("Prefer red.", question.Options[0].Description);
            Assert.AreEqual("Blue", question.Options[1].Label);
            Assert.AreEqual("Prefer blue.", question.Options[1].Description);
        }

        // ------------------------------------------------------------------
        // Outbound payloads: exactly the verified wire shapes
        // ------------------------------------------------------------------

        [Test]
        public void AllowWrite_ReplaysTheExactCapturedOutboundTranscript()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "permission_inbound.jsonl", "permission_outbound.jsonl");

            // Bare allow: the client must echo the original input as
            // updatedInput (02b section 2 -- every verified allow carries it).
            _client.RespondToPermission(request.RequestId, PermissionDecision.AllowTool());
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            Assert.IsNull(_client.PendingPermissionRequestId);

            List<string> expected = FixtureLoader.ReadLines("permission_outbound.jsonl");
            Assert.AreEqual(expected.Count, _fake.WrittenLines.Count,
                "outbound line count must match the captured transcript");
            for (int i = 0; i < expected.Count; i++)
            {
                AssertJsonLineEquals(expected[i], _fake.WrittenLines[i],
                    "permission_outbound.jsonl line " + (i + 1));
            }
        }

        [Test]
        public void AskUserAnswer_KeyedByQuestionText_ReplaysTheExactCapturedTranscript()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "askuser3_inbound.jsonl", "askuser3_outbound.jsonl");

            // The verified reply: original input + answers keyed by QUESTION
            // TEXT (header keys do not reach the model; 02b section 5).
            JsonNode updatedInput = AskUserQuestionInput.BuildAnswersUpdatedInput(
                request.CanUseTool.Input,
                new[]
                {
                    new KeyValuePair<string, string>("Which color do you prefer?", "Red")
                });
            _client.RespondToPermission(request.RequestId,
                PermissionDecision.AllowTool(updatedInput));

            List<string> expected = FixtureLoader.ReadLines("askuser3_outbound.jsonl");
            Assert.AreEqual(expected.Count, _fake.WrittenLines.Count,
                "outbound line count must match the captured transcript");
            for (int i = 0; i < expected.Count; i++)
            {
                AssertJsonLineEquals(expected[i], _fake.WrittenLines[i],
                    "askuser3_outbound.jsonl line " + (i + 1));
            }
        }

        [Test]
        public void Deny_ProducesTheDocumentedShape_WithDefaultMessage()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "permission_inbound.jsonl", "permission_outbound.jsonl");

            _client.RespondToPermission(request.RequestId,
                PermissionDecision.DenyTool("User denied this tool use."));

            string expected = "{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":\"" + request.RequestId + "\","
                + "\"response\":{\"behavior\":\"deny\","
                + "\"message\":\"User denied this tool use.\",\"interrupt\":false}}}";
            AssertJsonLineEquals(expected, LastWrittenLine(), "deny payload");
            Assert.AreEqual(AgentClientState.Streaming, _client.State);
        }

        [Test]
        public void Skip_SendsAllowWithEmptyAnswersObject()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "askuser3_inbound.jsonl", "askuser3_outbound.jsonl");

            JsonNode updatedInput = AskUserQuestionInput.BuildAnswersUpdatedInput(
                request.CanUseTool.Input, null);
            _client.RespondToPermission(request.RequestId,
                PermissionDecision.AllowTool(updatedInput));

            JsonNode written = ParseJson(LastWrittenLine());
            JsonNode inner = written["response"]["response"];
            Assert.AreEqual("allow", inner["behavior"].AsString());
            JsonNode sent = inner["updatedInput"];
            Assert.IsTrue(sent.HasKey("answers"), "Skip must still send answers:{}");
            Assert.IsTrue(sent["answers"].IsObject);
            Assert.AreEqual(0, sent["answers"].Count);
            // The original questions array must be preserved untouched.
            Assert.IsTrue(JsonDeepEquals(request.CanUseTool.Input["questions"],
                sent["questions"]));
        }

        [Test]
        public void MultiSelectAnswers_AreJoinedWithCommaSpace()
        {
            // 02b section 5: multiSelect is unverified upstream; the agreed
            // panel behavior is a ", " join of the chosen labels.
            Assert.AreEqual("Red, Blue",
                AskUserQuestionInput.JoinLabels(new List<string> { "Red", "Blue" }));
            Assert.AreEqual("Red", AskUserQuestionInput.JoinLabels(
                new List<string> { "Red" }));
            Assert.AreEqual(string.Empty, AskUserQuestionInput.JoinLabels(null));

            JsonNode input = ParseJson(
                "{\"questions\":[{\"question\":\"Q?\",\"header\":\"H\","
                + "\"options\":[{\"label\":\"Red\"},{\"label\":\"Blue\"}],"
                + "\"multiSelect\":true}]}");
            JsonNode updated = AskUserQuestionInput.BuildAnswersUpdatedInput(input,
                new[] { new KeyValuePair<string, string>("Q?", "Red, Blue") });
            Assert.AreEqual("Red, Blue", updated["answers"]["Q?"].AsString());
            Assert.IsTrue(JsonDeepEquals(input["questions"], updated["questions"]));
        }

        // ------------------------------------------------------------------
        // Silence backstop vs. the pending permission prompt
        // ------------------------------------------------------------------

        [Test]
        public void SilenceBackstop_IsSuspendedWhilePermissionPromptIsPending()
        {
            // R05 3.7: the permission wait has no timeout. The CLI emits
            // nothing while blocked on can_use_tool (02b section 1), so an
            // unanswered card must never trip the 10-minute stall path
            // (false "turn was closed" note + hub/client state divergence).
            ControlRequestMessage request = DriveToPermissionPrompt(
                "permission_inbound.jsonl", "permission_outbound.jsonl");
            bool stalled = false;
            _client.TurnStalled += delegate { stalled = true; };
            _client.SilenceTimeoutSeconds = 0.001;

            System.Threading.Thread.Sleep(20);
            _client.Pump();
            Assert.IsFalse(stalled,
                "backstop must not fire while a can_use_tool prompt is pending");
            Assert.AreEqual(AgentClientState.WaitingPermission, _client.State);
            Assert.AreEqual(request.RequestId, _client.PendingPermissionRequestId);

            // Once answered, the backstop re-arms from that moment.
            _client.RespondToPermission(request.RequestId, PermissionDecision.AllowTool());
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            System.Threading.Thread.Sleep(20);
            _client.Pump();
            Assert.IsTrue(stalled,
                "backstop must resume counting after the prompt is answered");
            Assert.AreEqual(AgentClientState.Ready, _client.State);
        }

        // ------------------------------------------------------------------
        // Echo-backs and unknown request ids stay ignored
        // ------------------------------------------------------------------

        [Test]
        public void EchoedControlResponse_AfterAskUserAllow_IsIgnoredSilently()
        {
            ControlRequestMessage request = DriveToPermissionPrompt(
                "askuser3_inbound.jsonl", "askuser3_outbound.jsonl");
            _client.RespondToPermission(request.RequestId,
                PermissionDecision.AllowTool(AskUserQuestionInput.BuildAnswersUpdatedInput(
                    request.CanUseTool.Input,
                    new[]
                    {
                        new KeyValuePair<string, string>(
                            "Which color do you prefer?", "Red")
                    })));
            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);

            // The CLI echoes our own control_response back on stdout (02b
            // section 3). It must be ignored: no state change, no log line.
            _log.Clear();
            int writtenBefore = _fake.WrittenLines.Count;
            _fake.ScriptLine(FixtureLine("askuser3_inbound.jsonl",
                "\"type\":\"control_response\"", "\"behavior\":\"allow\""));
            PumpAll();

            Assert.AreEqual(AgentClientState.ToolRunning, _client.State);
            Assert.AreEqual(writtenBefore, _fake.WrittenLines.Count);
            foreach (string entry in _log)
            {
                Assert.IsFalse(Contains(entry, request.RequestId),
                    "echo-back produced log pollution: " + entry);
            }
        }

        [Test]
        public void ControlResponse_WithUnknownRequestId_IsIgnoredSilently()
        {
            MakeReadyFrom("permission_inbound.jsonl");
            _log.Clear();

            _fake.ScriptLine("{\"type\":\"control_response\",\"response\":"
                + "{\"subtype\":\"success\",\"request_id\":"
                + "\"00000000-dead-beef-0000-000000000000\","
                + "\"response\":{\"behavior\":\"allow\",\"updatedInput\":{}}}}");
            PumpAll();

            Assert.AreEqual(AgentClientState.Ready, _client.State);
            Assert.IsEmpty(_log, "unknown-request-id echo must be silent: "
                + string.Join("; ", _log));
        }

        // ------------------------------------------------------------------
        // Forward compatibility: the new captures fully dispatch
        // ------------------------------------------------------------------

        [Test]
        public void AskUserFixtures_EveryLineDispatchesWithoutDrops()
        {
            string[] fixtures =
            {
                "askuser_inbound.jsonl",
                "askuser3_inbound.jsonl",
                "permission_inbound.jsonl"
            };
            foreach (string fixture in fixtures)
            {
                var lines = FixtureLoader.ReadLines(fixture);
                Assert.IsNotEmpty(lines, fixture);
                for (int i = 0; i < lines.Count; i++)
                {
                    StreamJsonMessage msg = StreamJsonMessage.ParseLine(lines[i], _log.Add);
                    Assert.IsNotNull(msg,
                        fixture + " line " + (i + 1) + " must map (not drop)");
                }
            }
            Assert.IsEmpty(_log, "no capture line may be dropped: "
                + string.Join("; ", _log));
        }
    }
}
