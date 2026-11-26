using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// Meeting point between the uap_ask_user MCP tool (asks, on an HTTP
    /// worker thread) and the ACP bridge (shows the question card and
    /// learns the answer). Design note
    /// docs/design-notes/2026-11-25-acp-ask-user.md.
    ///
    /// An ACP permission answer is one option id: it cannot carry the
    /// answers the way Claude Code's updatedInput does. So the answers
    /// travel through here instead, keyed by the questions themselves
    /// (<see cref="Fingerprint"/>): the bridge posts what the card
    /// collected, the tool call that follows takes it and returns it as its
    /// result.
    ///
    /// Two orders, one store:
    /// - The agent asks permission first (the usual ACP order). The bridge
    ///   turns that request into the question card, posts the answers and
    ///   only then lets the call through, so <see cref="Ask"/> finds them
    ///   waiting and returns at once.
    /// - The agent runs the tool without asking (a mode that approves
    ///   everything). <see cref="Ask"/> finds nothing, hands the questions
    ///   to the presenter (the bridge raises the same card) and waits.
    ///
    /// Static because the tool registry and the bridge have no object in
    /// common; one agent process is connected at a time. Unity-free and
    /// thread-safe. The presenter is never called while the internal lock
    /// is held, so it may take its own locks.
    /// </summary>
    public static class AskUserQuestionBroker
    {
        /// <summary>Wire name of the tool, without the MCP server prefix.</summary>
        public const string ToolName = "uap_ask_user";

        /// <summary>Limits the card is built for; the same ones Claude Code's AskUserQuestion has.</summary>
        public const int MaxQuestions = 4;
        public const int MinOptions = 2;
        public const int MaxOptions = 4;

        /// <summary>How an <see cref="Ask"/> call ended.</summary>
        public enum Outcome
        {
            /// <summary>The user submitted; <see cref="Reply.Answers"/> holds question text -> label(s).</summary>
            Answered,
            /// <summary>The user closed the card without choosing.</summary>
            Skipped,
            /// <summary>The wait ran out with the card still open.</summary>
            Pending,
            /// <summary>The turn ended or was cancelled while the card was open.</summary>
            Dismissed,
            /// <summary>Nothing connected can show a question card.</summary>
            Unavailable
        }

        /// <summary>Result of <see cref="Ask"/>.</summary>
        public sealed class Reply
        {
            public Outcome Outcome;
            /// <summary>Question text -> selected label(s); an empty object unless <see cref="Outcome.Answered"/>.</summary>
            public JsonNode Answers = JsonNode.NewObject();
        }

        private sealed class Entry
        {
            public Outcome Outcome;
            public JsonNode Answers;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Settled =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly HashSet<string> Open = new HashSet<string>(StringComparer.Ordinal);
        private static Func<string, JsonNode, bool> _presenter;

        /// <summary>
        /// Sets who shows the card for a question nobody has answered yet:
        /// called with the fingerprint and the tool input, returns false
        /// when it cannot show one right now. Replaces any earlier
        /// presenter.
        /// </summary>
        public static void SetPresenter(Func<string, JsonNode, bool> presenter)
        {
            lock (Gate)
            {
                _presenter = presenter;
            }
        }

        /// <summary>Removes <paramref name="presenter"/> if it is still the current one, and drops everything it left open.</summary>
        public static void ClearPresenter(Func<string, JsonNode, bool> presenter)
        {
            lock (Gate)
            {
                if (_presenter != presenter)
                {
                    return;
                }
                _presenter = null;
            }
            DismissAll();
        }

        /// <summary>True when a presenter is set.</summary>
        public static bool HasPresenter
        {
            get
            {
                lock (Gate)
                {
                    return _presenter != null;
                }
            }
        }

        /// <summary>
        /// Checks the tool input against what the card can show. Null when
        /// it is fine, otherwise a sentence the agent can act on.
        /// </summary>
        public static string Validate(JsonNode input)
        {
            if (input == null || !input.IsObject || !input["questions"].IsArray || input["questions"].Count == 0)
            {
                return "'questions' must be a non-empty array of {question, header, options, multiSelect}.";
            }
            JsonNode questions = input["questions"];
            if (questions.Count > MaxQuestions)
            {
                return "At most " + MaxQuestions + " questions per call (got " + questions.Count + ").";
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int q = 0; q < questions.Count; q++)
            {
                JsonNode question = questions[q];
                string text = question["question"].AsString();
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
                {
                    return "questions[" + q + "].question must be a non-empty string.";
                }
                if (!seen.Add(text))
                {
                    return "questions[" + q + "].question repeats an earlier question; answers are keyed by"
                        + " the question text, so each must be different.";
                }
                JsonNode options = question["options"];
                int count = options.IsArray ? options.Count : 0;
                if (count < MinOptions || count > MaxOptions)
                {
                    return "questions[" + q + "].options must hold " + MinOptions + " to " + MaxOptions
                        + " entries (got " + count + "); the card adds a free-text \"Other\" by itself.";
                }
                var labels = new HashSet<string>(StringComparer.Ordinal);
                for (int o = 0; o < count; o++)
                {
                    string label = options[o]["label"].AsString();
                    if (string.IsNullOrEmpty(label) || label.Trim().Length == 0)
                    {
                        return "questions[" + q + "].options[" + o + "].label must be a non-empty string.";
                    }
                    if (!labels.Add(label))
                    {
                        return "questions[" + q + "].options[" + o + "].label repeats another option of the"
                            + " same question.";
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The key a question set is stored under: its question texts, in
        /// order. The same texts are the keys of the answers object, so two
        /// inputs with one fingerprint are answered by the same answers.
        /// Empty when the input has no question.
        /// </summary>
        public static string Fingerprint(JsonNode input)
        {
            if (input == null || !input.IsObject)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            foreach (JsonNode question in input["questions"].Items)
            {
                string text = question["question"].AsString();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                // Length-prefixed, so no separator character is needed and
                // ["a", "b"] can never read as one question "a...b".
                sb.Append(text.Length).Append(':').Append(text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Records what the card collected for <paramref name="fingerprint"/>
        /// and wakes a waiting <see cref="Ask"/>. A null or empty
        /// <paramref name="answers"/> object means the user skipped.
        /// </summary>
        public static void PostAnswers(string fingerprint, JsonNode answers)
        {
            bool answered = answers != null && answers.IsObject && answers.Count > 0;
            Settle(fingerprint, answered ? Outcome.Answered : Outcome.Skipped,
                answered ? answers : JsonNode.NewObject());
        }

        /// <summary>Closes one open question without an answer (its card went away).</summary>
        public static void Dismiss(string fingerprint)
        {
            Settle(fingerprint, Outcome.Dismissed, JsonNode.NewObject());
        }

        /// <summary>
        /// Ends everything in flight: waiting calls return
        /// <see cref="Outcome.Dismissed"/>, and answers nobody took are
        /// dropped so they can never answer a later, identical question.
        /// Called when a turn ends and when the agent goes away.
        /// </summary>
        public static void DismissAll()
        {
            lock (Gate)
            {
                Settled.Clear();
                foreach (string fingerprint in Open)
                {
                    Settled[fingerprint] = new Entry { Outcome = Outcome.Dismissed, Answers = JsonNode.NewObject() };
                }
                Monitor.PulseAll(Gate);
            }
        }

        private static void Settle(string fingerprint, Outcome outcome, JsonNode answers)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return;
            }
            lock (Gate)
            {
                Settled[fingerprint] = new Entry { Outcome = outcome, Answers = answers };
                Monitor.PulseAll(Gate);
            }
        }

        /// <summary>
        /// Returns the user's answer to <paramref name="input"/>'s
        /// questions: the one already posted for them, or else the one the
        /// presenter's card produces within <paramref name="waitMillis"/>.
        /// On <see cref="Outcome.Pending"/> the card stays open; calling
        /// again with the same questions keeps waiting for the same card.
        /// Blocks the calling thread, so never call it on the main thread.
        /// </summary>
        public static Reply Ask(JsonNode input, int waitMillis)
        {
            string fingerprint = Fingerprint(input);
            Func<string, JsonNode, bool> presenter;
            bool present;
            lock (Gate)
            {
                Reply ready = TakeSettled(fingerprint);
                if (ready != null)
                {
                    return ready;
                }
                presenter = _presenter;
                if (presenter == null || fingerprint.Length == 0)
                {
                    return new Reply { Outcome = Outcome.Unavailable };
                }
                present = Open.Add(fingerprint);
            }
            if (present)
            {
                bool shown;
                try
                {
                    shown = presenter(fingerprint, input);
                }
                catch (Exception)
                {
                    shown = false;
                }
                if (!shown)
                {
                    lock (Gate)
                    {
                        Open.Remove(fingerprint);
                        Settled.Remove(fingerprint);
                    }
                    return new Reply { Outcome = Outcome.Unavailable };
                }
            }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMillis < 0 ? 0 : waitMillis);
            lock (Gate)
            {
                while (true)
                {
                    Reply ready = TakeSettled(fingerprint);
                    if (ready != null)
                    {
                        return ready;
                    }
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0)
                    {
                        return new Reply { Outcome = Outcome.Pending };
                    }
                    Monitor.Wait(Gate, remaining);
                }
            }
        }

        /// <summary>Caller holds <see cref="Gate"/>.</summary>
        private static Reply TakeSettled(string fingerprint)
        {
            Entry entry;
            if (fingerprint.Length == 0 || !Settled.TryGetValue(fingerprint, out entry))
            {
                return null;
            }
            Settled.Remove(fingerprint);
            Open.Remove(fingerprint);
            return new Reply { Outcome = entry.Outcome, Answers = entry.Answers };
        }

        /// <summary>Test seam: forgets the presenter and every stored or open question.</summary>
        public static void ResetForTests()
        {
            lock (Gate)
            {
                _presenter = null;
                Settled.Clear();
                Open.Clear();
                Monitor.PulseAll(Gate);
            }
        }
    }
}
