using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// uap_ask_user: the question card for agents that have no
    /// AskUserQuestion of their own (design note
    /// docs/design-notes/2026-11-25-acp-ask-user.md). The input is
    /// AskUserQuestion's, so the panel shows it with the card it already
    /// has; the answer comes back as this call's result.
    ///
    /// Runs off the main thread (<see cref="IUapOffThreadTool"/>): it waits
    /// for a person, and must not hold the Editor while it does. It touches
    /// nothing but <see cref="AskUserQuestionBroker"/>.
    /// </summary>
    public sealed class UapAskUserTool : IUapOffThreadTool
    {
        public const string ToolName = AskUserQuestionBroker.ToolName;

        /// <summary>
        /// Longest one call waits for the answer. Below the 30 s HTTP
        /// timeout of the stdio relay and the 60 s MCP tool timeout Codex
        /// defaults to, the two shortest budgets a call passes through; a
        /// call that outlived either would fail with the card still open.
        /// Mutable for tests.
        /// </summary>
        public int WaitMillis = 20000;

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Asks the user multiple-choice questions on a card in the panel and returns what"
                    + " they picked -- the default way to ask the user anything: use it instead of"
                    + " guessing or asking in prose whenever a decision or clarification is theirs"
                    + " (which object, which approach, overwrite or keep). For an open-ended question"
                    + " offer the likeliest answers as options. 1-"
                    + AskUserQuestionBroker.MaxQuestions + " questions per call, "
                    + AskUserQuestionBroker.MinOptions + "-" + AskUserQuestionBroker.MaxOptions
                    + " options each; the card adds a free-text \"Other\" itself, so do not add one."
                    + " The result is JSON: {\"status\":\"answered\",\"answers\":{\"<question text>\":"
                    + "\"<chosen label>\"}} (several labels of a multiSelect question are joined with"
                    + " \", \"; a free-text answer appears in place of a label). status \"pending\" means"
                    + " the user has not answered yet: call again with exactly the same questions to keep"
                    + " waiting. Claude Code has AskUserQuestion built in; use that there.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                JsonNode option = JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("label", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "The choice as shown on its button, 1-5 words. Returned"
                                + " as the answer when picked."))
                        .Set("description", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "One line on what picking this means or leads to.")))
                    .Set("required", JsonNode.NewArray().Add("label"))
                    .Set("additionalProperties", false);
                JsonNode question = JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("question", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "The full question, ending in a question mark. It is"
                                + " also the key of this question's answer in the result."))
                        .Set("header", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Short tab label for the question, at most 12"
                                + " characters (e.g. \"Target\")."))
                        .Set("options", JsonNode.NewObject().Set("type", "array")
                            .Set("minItems", AskUserQuestionBroker.MinOptions)
                            .Set("maxItems", AskUserQuestionBroker.MaxOptions)
                            .Set("items", option)
                            .Set("description", "The choices, the recommended one first."))
                        .Set("multiSelect", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "True when more than one option may be picked"
                                + " (default false).")))
                    .Set("required", JsonNode.NewArray().Add("question").Add("options"))
                    .Set("additionalProperties", false);
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("questions", JsonNode.NewObject().Set("type", "array")
                            .Set("minItems", 1)
                            .Set("maxItems", AskUserQuestionBroker.MaxQuestions)
                            .Set("items", question)
                            .Set("description", "The questions to ask, each with its own options.")))
                    .Set("required", JsonNode.NewArray().Add("questions"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string problem = AskUserQuestionBroker.Validate(input);
            if (problem != null)
            {
                throw new InvalidOperationException(problem);
            }
            AskUserQuestionBroker.Reply reply = AskUserQuestionBroker.Ask(input, WaitMillis);
            return UapToolResults.Text(JsonWriter.Write(Describe(reply)));
        }

        /// <summary>The result body for one broker reply. Pure; the wording is what the agent acts on.</summary>
        public static JsonNode Describe(AskUserQuestionBroker.Reply reply)
        {
            switch (reply.Outcome)
            {
                case AskUserQuestionBroker.Outcome.Answered:
                    return JsonNode.NewObject()
                        .Set("status", "answered")
                        .Set("answers", reply.Answers);
                case AskUserQuestionBroker.Outcome.Skipped:
                    return JsonNode.NewObject()
                        .Set("status", "skipped")
                        .Set("note", "The user closed the question card without answering. Do not ask the"
                            + " same thing again; go on with the safest reading, or say what you still need.");
                case AskUserQuestionBroker.Outcome.Pending:
                    return JsonNode.NewObject()
                        .Set("status", "pending")
                        .Set("note", "The question card is open in the panel and the user has not answered"
                            + " yet. Call " + ToolName + " again with exactly the same questions to keep"
                            + " waiting for it; do not reword them and do not go on without the answer.");
                case AskUserQuestionBroker.Outcome.Dismissed:
                    return JsonNode.NewObject()
                        .Set("status", "dismissed")
                        .Set("note", "The turn was stopped while the question card was open; there is no"
                            + " answer.");
                default:
                    return JsonNode.NewObject()
                        .Set("status", "unavailable")
                        .Set("note", "No question card can be shown for this agent right now. Ask the same"
                            + " questions in plain text in your reply, list the options, and end your turn"
                            + " (Claude Code: use the built-in AskUserQuestion tool instead).");
            }
        }
    }
}
