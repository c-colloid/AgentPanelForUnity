using System;
using System.Collections.Generic;
using System.Threading;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_ask_user: the question card for ACP agents (design note
    /// docs/design-notes/2026-11-25-acp-ask-user.md). Drives the real
    /// bridge, broker and tool without a process or a panel; the panel's
    /// side is the can_use_tool line the bridge emits, parsed the way
    /// AgentClient parses it.
    /// </summary>
    [TestFixture]
    public class AcpAskUserTests
    {
        private List<string> _toAgent;
        private List<string> _toPanel;
        private AcpProtocolBridge _bridge;
        private AcpInbound _prompt;
        private UapAskUserTool _tool;

        [SetUp]
        public void SetUp()
        {
            AskUserQuestionBroker.ResetForTests();
            _toAgent = new List<string>();
            _toPanel = new List<string>();
            _tool = new UapAskUserTool { WaitMillis = 30 };
            _bridge = new AcpProtocolBridge(new AcpLaunchSpec { Backend = AgentBackend.GeminiCli },
                "C:/proj", _toAgent.Add, _toPanel.Add, null);
            _bridge.OnPanelLine("{\"type\":\"control_request\",\"request_id\":\"req_1\",\"request\":{\"subtype\":\"initialize\"}}");
            _bridge.OnAgentLine(AcpJsonRpc.Response(AgentLine("initialize").Id,
                JsonNode.NewObject().Set("protocolVersion", 1)));
            _bridge.OnAgentLine(AcpJsonRpc.Response(AgentLine("session/new").Id,
                JsonNode.NewObject().Set("sessionId", "s1")));
        }

        [TearDown]
        public void TearDown()
        {
            AskUserQuestionBroker.ResetForTests();
        }

        // -- helpers ---------------------------------------------------------------

        private void BeginTurn()
        {
            _bridge.OnPanelLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");
            _prompt = AgentLine("session/prompt");
        }

        private AcpInbound AgentLine(string method)
        {
            for (int i = _toAgent.Count - 1; i >= 0; i--)
            {
                AcpInbound parsed = AcpJsonRpc.TryParse(_toAgent[i]);
                if (parsed != null && parsed.Method == method)
                {
                    return parsed;
                }
            }
            Assert.Fail("no '" + method + "' was sent to the agent");
            return null;
        }

        private ControlRequestMessage LastCard()
        {
            StreamJsonMessage message = StreamJsonMessage.ParseLine(_toPanel[_toPanel.Count - 1],
                delegate(string ignored) { });
            Assert.IsInstanceOf<ControlRequestMessage>(message, _toPanel[_toPanel.Count - 1]);
            return (ControlRequestMessage)message;
        }

        private static JsonNode Questions(string question)
        {
            return JsonNode.NewObject().Set("questions", JsonNode.NewArray().Add(JsonNode.NewObject()
                .Set("question", question).Set("header", "Target")
                .Set("options", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("label", "Cube").Set("description", "The cube."))
                    .Add(JsonNode.NewObject().Set("label", "Sphere")))));
        }

        private void RequestPermission(long id, string toolCallId, string title, JsonNode rawInput)
        {
            _bridge.OnAgentLine(AcpJsonRpc.Request(id, "session/request_permission", JsonNode.NewObject()
                .Set("sessionId", "s1")
                .Set("toolCall", JsonNode.NewObject().Set("toolCallId", toolCallId).Set("title", title)
                    .Set("kind", "other").Set("rawInput", rawInput))
                .Set("options", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("optionId", "y").Set("kind", "allow_once"))
                    .Add(JsonNode.NewObject().Set("optionId", "a").Set("kind", "allow_always"))
                    .Add(JsonNode.NewObject().Set("optionId", "n").Set("kind", "reject_once")))));
        }

        /// <summary>The panel's answer exactly as the card sends it: an allow whose updatedInput carries the answers.</summary>
        private void Answer(ControlRequestMessage card, IEnumerable<KeyValuePair<string, string>> answers)
        {
            JsonNode updated = AskUserQuestionInput.BuildAnswersUpdatedInput(card.CanUseTool.Input, answers);
            _bridge.OnPanelLine(JsonWriter.Write(JsonNode.NewObject().Set("type", "control_response")
                .Set("response", JsonNode.NewObject().Set("subtype", "success").Set("request_id", card.RequestId)
                    .Set("response", JsonNode.NewObject().Set("behavior", "allow").Set("updatedInput", updated)))));
        }

        private static KeyValuePair<string, string>[] One(string question, string label)
        {
            return new[] { new KeyValuePair<string, string>(question, label) };
        }

        private JsonNode Call(JsonNode input)
        {
            return JsonParser.Parse(_tool.Execute(input)[0]["text"].AsString());
        }

        // -- input ------------------------------------------------------------------

        [Test]
        public void Validate_AcceptsTheShapeTheCardShows()
        {
            Assert.IsNull(AskUserQuestionBroker.Validate(Questions("Which one?")));
        }

        [Test]
        public void Validate_RefusesWhatTheCardCannotShow()
        {
            Assert.IsNotNull(AskUserQuestionBroker.Validate(JsonNode.NewObject()));

            JsonNode oneOption = Questions("Which one?");
            oneOption["questions"][0].Set("options",
                JsonNode.NewArray().Add(JsonNode.NewObject().Set("label", "Cube")));
            StringAssert.Contains("2 to 4", AskUserQuestionBroker.Validate(oneOption));

            // Answers are keyed by the question text: a repeat would lose one.
            JsonNode repeated = Questions("Which one?");
            repeated["questions"].Add(Questions("Which one?")["questions"][0]);
            StringAssert.Contains("repeats", AskUserQuestionBroker.Validate(repeated));

            JsonNode five = Questions("Q1?");
            for (int i = 2; i <= 5; i++)
            {
                five["questions"].Add(Questions("Q" + i + "?")["questions"][0]);
            }
            StringAssert.Contains("At most 4", AskUserQuestionBroker.Validate(five));
        }

        [Test]
        public void Tool_ThrowsOnMalformedInput_InsteadOfShowingABrokenCard()
        {
            Assert.Throws<InvalidOperationException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Tool_InputParsesAsAskUserQuestion()
        {
            // The card is the AskUserQuestion card: the two inputs must stay one shape.
            AskUserQuestionInput parsed = AskUserQuestionInput.FromInput(Questions("Which one?"));

            Assert.AreEqual(1, parsed.Questions.Count);
            Assert.AreEqual("Which one?", parsed.Questions[0].QuestionText);
            Assert.AreEqual(2, parsed.Questions[0].Options.Count);
            JsonNode schema = _tool.InputSchema["properties"]["questions"]["items"]["properties"];
            Assert.IsTrue(schema["question"].IsObject && schema["header"].IsObject
                && schema["options"].IsObject && schema["multiSelect"].IsObject);
        }

        [Test]
        public void Tool_RunsOffTheMainThread()
        {
            // It waits for a person; on the main thread that would freeze the Editor.
            Assert.IsInstanceOf<IUapOffThreadTool>(_tool);
            Assert.Less(_tool.WaitMillis, 30000, "must return before the stdio relay's 30 s HTTP timeout");
        }

        // -- permission first ---------------------------------------------------------

        [Test]
        public void PermissionRequest_BecomesTheQuestionCard()
        {
            BeginTurn();
            RequestPermission(5, "c1", "uap_ask_user", Questions("Which one?"));

            ControlRequestMessage card = LastCard();
            Assert.IsTrue(card.IsCanUseTool);
            Assert.IsTrue(card.CanUseTool.RequiresUserInteraction,
                "no auto-approve level may answer a question, and the card must take its question form");
            Assert.AreEqual("mcp__unity-ops__uap_ask_user", card.CanUseTool.ToolName);
            Assert.AreEqual(1, AskUserQuestionInput.FromInput(card.CanUseTool.Input).Questions.Count);
            Assert.AreEqual(0, card.CanUseTool.PermissionSuggestions.Count, "a question has no 'always allow'");
        }

        [Test]
        public void Answer_ReachesTheToolCallThatFollows()
        {
            BeginTurn();
            RequestPermission(5, "c1", "uap_ask_user", Questions("Which one?"));
            Answer(LastCard(), One("Which one?", "Sphere"));

            AcpInbound outcome = AcpJsonRpc.TryParse(_toAgent[_toAgent.Count - 1]);
            Assert.AreEqual("y", outcome.Result["outcome"]["optionId"].AsString(), "allowed once, never always");

            JsonNode result = Call(Questions("Which one?"));
            Assert.AreEqual("answered", result["status"].AsString());
            Assert.AreEqual("Sphere", result["answers"]["Which one?"].AsString());
        }

        [Test]
        public void Answer_IsTakenOnce()
        {
            BeginTurn();
            RequestPermission(5, "c1", "uap_ask_user", Questions("Which one?"));
            Answer(LastCard(), One("Which one?", "Sphere"));
            Call(Questions("Which one?"));

            // The same question asked again is a new question, not the old answer.
            Assert.AreNotEqual("answered", Call(Questions("Which one?"))["status"].AsString());
        }

        [Test]
        public void Skip_IsReportedAsSkipped()
        {
            BeginTurn();
            RequestPermission(5, "c1", "uap_ask_user", Questions("Which one?"));
            Answer(LastCard(), null);

            Assert.AreEqual("skipped", Call(Questions("Which one?"))["status"].AsString());
        }

        [Test]
        public void CodexAndGrokEnvelopes_AndUapCall_AreUnwrapped()
        {
            BeginTurn();
            RequestPermission(5, "c1", "mcp.unity-ops.uap_ask_user", JsonNode.NewObject()
                .Set("server", "unity-ops").Set("tool", "uap_ask_user").Set("arguments", Questions("Codex?")));
            Assert.AreEqual("Codex?",
                AskUserQuestionInput.FromInput(LastCard().CanUseTool.Input).Questions[0].QuestionText);

            RequestPermission(6, "c2", "unity-ops__uap_ask_user", JsonNode.NewObject()
                .Set("tool_name", "unity-ops__uap_ask_user").Set("tool_input", Questions("Grok?")));
            Assert.AreEqual("Grok?",
                AskUserQuestionInput.FromInput(LastCard().CanUseTool.Input).Questions[0].QuestionText);

            // Compact tool list: the call arrives as uap_call {name, arguments}.
            RequestPermission(7, "c3", "uap_call", JsonNode.NewObject()
                .Set("name", "uap_ask_user").Set("arguments", Questions("Compact?")));
            ControlRequestMessage card = LastCard();
            Assert.IsTrue(card.CanUseTool.RequiresUserInteraction);
            Assert.AreEqual("Compact?",
                AskUserQuestionInput.FromInput(card.CanUseTool.Input).Questions[0].QuestionText);
        }

        [Test]
        public void MalformedQuestion_StaysAnOrdinaryPermission()
        {
            BeginTurn();
            JsonNode oneOption = Questions("Which one?");
            oneOption["questions"][0].Set("options",
                JsonNode.NewArray().Add(JsonNode.NewObject().Set("label", "Cube")));
            RequestPermission(5, "c1", "uap_ask_user", oneOption);

            // The tool refuses this input with its own message; a question
            // card for it would collect an answer nothing returns.
            Assert.IsFalse(LastCard().CanUseTool.RequiresUserInteraction);
        }

        [Test]
        public void OtherUapTools_AreNotTurnedIntoQuestions()
        {
            BeginTurn();
            RequestPermission(5, "c1", "uap_ping", JsonNode.NewObject().Set("message", "x"));

            Assert.IsFalse(LastCard().CanUseTool.RequiresUserInteraction);
        }

        // -- no permission request ------------------------------------------------------

        [Test]
        public void ToolCallWithoutPermission_RaisesTheCard_AndWaits()
        {
            BeginTurn();
            AskUserQuestionBroker.SetPresenter(_bridge.PresentAskUser);
            int before = _toPanel.Count;

            Assert.AreEqual("pending", Call(Questions("Overwrite?"))["status"].AsString());
            Assert.AreEqual(before + 1, _toPanel.Count);
            Assert.IsTrue(LastCard().CanUseTool.RequiresUserInteraction);

            // Asking again waits for the same card instead of stacking a second one.
            Call(Questions("Overwrite?"));
            Assert.AreEqual(before + 1, _toPanel.Count);
        }

        [Test]
        public void WaitingCall_WakesOnTheAnswer_AndNoAgentRequestIsAnswered()
        {
            BeginTurn();
            AskUserQuestionBroker.SetPresenter(_bridge.PresentAskUser);
            Call(Questions("Overwrite?"));
            ControlRequestMessage card = LastCard();
            int agentLines = _toAgent.Count;

            var waiting = new UapAskUserTool { WaitMillis = 5000 };
            JsonNode result = null;
            var worker = new Thread(delegate() { result = waiting.Execute(Questions("Overwrite?")); });
            worker.Start();
            Thread.Sleep(50);
            Answer(card, One("Overwrite?", "Cube"));

            Assert.IsTrue(worker.Join(3000), "the waiting call must return when the card is answered");
            Assert.AreEqual("Cube",
                JsonParser.Parse(result[0]["text"].AsString())["answers"]["Overwrite?"].AsString());
            Assert.AreEqual(agentLines, _toAgent.Count, "the card had no permission request behind it");
        }

        [Test]
        public void TurnEnd_DismissesAnOpenQuestion_AndDropsUntakenAnswers()
        {
            BeginTurn();
            RequestPermission(5, "c1", "uap_ask_user", Questions("Stale?"));
            Answer(LastCard(), One("Stale?", "Cube"));
            AskUserQuestionBroker.SetPresenter(_bridge.PresentAskUser);
            Call(Questions("Open?"));

            _bridge.OnAgentLine(AcpJsonRpc.Response(_prompt.Id, JsonNode.NewObject().Set("stopReason", "end_turn")));

            Assert.AreEqual("dismissed", Call(Questions("Open?"))["status"].AsString());
            // An answer the agent never collected must not answer the same question next turn.
            Assert.AreEqual("unavailable", Call(Questions("Stale?"))["status"].AsString());
        }

        [Test]
        public void NoCard_OutsideATurn_OrWithNoPresenter()
        {
            Assert.IsFalse(_bridge.PresentAskUser(
                AskUserQuestionBroker.Fingerprint(Questions("Which one?")), Questions("Which one?")));
            Assert.AreEqual("unavailable", Call(Questions("Which one?"))["status"].AsString());
        }

        [Test]
        public void ClearPresenter_OnlyRemovesItsOwn()
        {
            Func<string, JsonNode, bool> first = delegate { return true; };
            Func<string, JsonNode, bool> second = delegate { return true; };
            AskUserQuestionBroker.SetPresenter(first);
            AskUserQuestionBroker.SetPresenter(second);

            // A transport that is going away must not unhook its successor.
            AskUserQuestionBroker.ClearPresenter(first);

            Assert.IsTrue(AskUserQuestionBroker.HasPresenter);
        }

        // -- panel side -------------------------------------------------------------------

        [Test]
        public void QuestionRequest_IsNeverAutoApproved()
        {
            // ReadOnly would auto-approve at every level; requires_user_interaction is what stops it.
            Assert.IsTrue(_tool.ReadOnly);
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, true, true, false, true, false));
        }

        [Test]
        public void Steering_OnlyForAcpAgentsThatHaveTheTool()
        {
            var core = new List<string> { "core" };

            StringAssert.Contains("uap_ask_user", AgentHub.ComposeAskUserSteeringSection(true, true, core));
            Assert.AreEqual(string.Empty, AgentHub.ComposeAskUserSteeringSection(false, true, core),
                "Claude Code has AskUserQuestion");
            Assert.AreEqual(string.Empty, AgentHub.ComposeAskUserSteeringSection(true, false, core));
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeAskUserSteeringSection(true, true, new List<string> { "editor" }));
        }

        [Test]
        public void Steering_MakesTheCardTheDefaultWayToAsk()
        {
            // Design note 2026-11-26-ask-user-by-default.md: the card is the default, an
            // open-ended question still goes through it, and prose is the stated exception.
            string steering = AgentHub.ComposeAskUserSteeringSection(
                true, true, new List<string> { "core" });

            StringAssert.Contains("by default", steering);
            StringAssert.Contains("open-ended", steering);
            StringAssert.Contains("Ask in prose only when", steering);
            StringAssert.DoesNotContain("Do not use it for open-ended", steering);
        }
    }
}
