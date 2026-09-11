using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// Typed view of the AskUserQuestion tool input plus the builder for the
    /// verified reply shape (02b section 5).
    ///
    /// Wire facts (measured, askuser3 fixtures):
    /// - AskUserQuestion arrives as can_use_tool with
    ///   requires_user_interaction:true and
    ///   input.questions[{question, header, options[{label, description}],
    ///   multiSelect}].
    /// - The reply is a normal allow whose updatedInput is the ORIGINAL
    ///   input plus an "answers" object KEYED BY THE QUESTION TEXT:
    ///   {"questions":[...],"answers":{"&lt;question text&gt;":"&lt;label&gt;"}}.
    ///   Header-keyed answers do NOT reach the model; an allow without
    ///   answers counts as unanswered.
    /// - multiSelect reply format is unverified upstream: multiple labels
    ///   are joined with ", " (see JoinLabels) and callers should log a note
    ///   when they actually send a multi-label answer.
    /// </summary>
    public sealed class AskUserQuestionInput
    {
        /// <summary>Separator used to join multiSelect labels (unverified upstream).</summary>
        public const string MultiSelectSeparator = ", ";

        /// <summary>One selectable option of a question.</summary>
        public sealed class Option
        {
            public string Label = string.Empty;
            public string Description = string.Empty;
        }

        /// <summary>One question with its options.</summary>
        public sealed class Question
        {
            /// <summary>The full question text -- the verified answers key.</summary>
            public string QuestionText = string.Empty;
            /// <summary>Short UI heading. NOT a valid answers key.</summary>
            public string Header = string.Empty;
            public bool MultiSelect;
            public readonly List<Option> Options = new List<Option>();
        }

        public readonly List<Question> Questions = new List<Question>();

        /// <summary>
        /// Parses input.questions. Optional-first: malformed entries are
        /// skipped, an absent/foreign input yields an empty question list
        /// (callers fall back to the generic permission card).
        /// </summary>
        public static AskUserQuestionInput FromInput(JsonNode input)
        {
            var result = new AskUserQuestionInput();
            if (input == null || !input.IsObject)
            {
                return result;
            }
            foreach (JsonNode entry in input["questions"].Items)
            {
                string text = entry["question"].AsString();
                if (string.IsNullOrEmpty(text))
                {
                    // Without the question text there is no valid answers key.
                    continue;
                }
                var question = new Question
                {
                    QuestionText = text,
                    Header = entry["header"].AsString(string.Empty),
                    MultiSelect = entry["multiSelect"].AsBool(false)
                };
                foreach (JsonNode optionNode in entry["options"].Items)
                {
                    string label = optionNode["label"].AsString();
                    if (string.IsNullOrEmpty(label))
                    {
                        continue;
                    }
                    question.Options.Add(new Option
                    {
                        Label = label,
                        Description = optionNode["description"].AsString(string.Empty)
                    });
                }
                result.Questions.Add(question);
            }
            return result;
        }

        /// <summary>Joins selected labels for one answer value (", " for multiSelect).</summary>
        public static string JoinLabels(IList<string> labels)
        {
            if (labels == null || labels.Count == 0)
            {
                return string.Empty;
            }
            return string.Join(MultiSelectSeparator, labels);
        }

        /// <summary>
        /// Builds the allow updatedInput: a copy of the original input (key
        /// order preserved) with "answers" set to the given
        /// question-text-to-label map. A null/empty map produces the
        /// verified Skip shape: "answers":{} (the CLI reports the questions
        /// as unanswered in its own wording).
        /// </summary>
        public static JsonNode BuildAnswersUpdatedInput(JsonNode originalInput,
            IEnumerable<KeyValuePair<string, string>> answersByQuestionText)
        {
            JsonNode updated = JsonNode.NewObject();
            if (originalInput != null && originalInput.IsObject)
            {
                foreach (KeyValuePair<string, JsonNode> pair in originalInput.Properties)
                {
                    if (pair.Key == "answers")
                    {
                        continue; // Always replaced below.
                    }
                    updated.Set(pair.Key, pair.Value);
                }
            }
            JsonNode answers = JsonNode.NewObject();
            if (answersByQuestionText != null)
            {
                foreach (KeyValuePair<string, string> answer in answersByQuestionText)
                {
                    if (string.IsNullOrEmpty(answer.Key))
                    {
                        continue;
                    }
                    answers.Set(answer.Key, answer.Value ?? string.Empty);
                }
            }
            updated.Set("answers", answers);
            return updated;
        }
    }
}
