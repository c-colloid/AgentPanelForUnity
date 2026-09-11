using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Design note docs/design-notes/2026-09-10-backend-switch-session.md section 5.</summary>
    [TestFixture]
    public class ConversationHandoverTests
    {
        private static ChatMessage Msg(string role, params ChatMessageBlock[] blocks)
        {
            var message = new ChatMessage { role = role };
            foreach (ChatMessageBlock block in blocks)
            {
                message.Add(block);
            }
            return message;
        }

        [Test]
        public void Build_RendersUserAndAssistantText_SkipsNotesAndThinking_ReducesToolCallsToOneLine()
        {
            var messages = new List<ChatMessage>
            {
                Msg(ChatMessage.RoleUser, ChatMessageBlock.MakeText("Make the cube red"),
                    ChatMessageBlock.MakeContextAttachment("GameObject: Cube", "payload")),
                Msg(ChatMessage.RoleSystem, ChatMessageBlock.MakeSystemNote("connected")),
                Msg(ChatMessage.RoleAssistant, ChatMessageBlock.MakeThinking("hmm"),
                    ChatMessageBlock.MakeToolCall(new ToolCallRecord { toolName = "Edit", inputSummary = "Cube.cs" }),
                    ChatMessageBlock.MakeText("Done, the cube is red."))
            };
            string text = ConversationHandover.Build(messages, "Claude Code", "Gemini CLI");
            StringAssert.StartsWith(ConversationHandover.Header, text);
            StringAssert.EndsWith(ConversationHandover.Footer, text);
            StringAssert.Contains("User: Make the cube red\n[attached: GameObject: Cube]", text);
            StringAssert.Contains("Assistant: [tool Edit: Cube.cs]\nDone, the cube is red.", text);
            StringAssert.Contains("Claude Code", text);
            StringAssert.Contains("Gemini CLI", text);
            StringAssert.DoesNotContain("connected", text);
            StringAssert.DoesNotContain("hmm", text);
            StringAssert.DoesNotContain("payload", text);
        }

        [Test]
        public void Build_NothingToHandOver_ReturnsNull()
        {
            Assert.IsNull(ConversationHandover.Build(null, "a", "b"));
            Assert.IsNull(ConversationHandover.Build(new List<ChatMessage>(), "a", "b"));
            Assert.IsNull(ConversationHandover.Build(new List<ChatMessage>
            {
                Msg(ChatMessage.RoleSystem, ChatMessageBlock.MakeSystemNote("x"))
            }, "a", "b"));
        }

        [Test]
        public void Build_OverBudget_KeepsTheNewestMessages_AndMarksTheOmission()
        {
            var messages = new List<ChatMessage>();
            for (int i = 0; i < 20; i++)
            {
                messages.Add(Msg(ChatMessage.RoleUser, ChatMessageBlock.MakeText("message " + i + " " + new string('x', 100))));
            }
            string text = ConversationHandover.Build(messages, "a", "b", 500);
            StringAssert.Contains(ConversationHandover.OmittedMarker, text);
            StringAssert.Contains("message 19", text);
            StringAssert.DoesNotContain("message 0 ", text);
            Assert.Less(text.Length, 500 + 400, "header/footer/preamble aside, the transcript respects the budget");
        }

        [Test]
        public void Compose_PutsTheHandoverBeforeTheUserText_AndPassesThroughWithoutOne()
        {
            Assert.AreEqual("H\n\nhello", ConversationHandover.Compose("H", "hello"));
            Assert.AreEqual("hello", ConversationHandover.Compose(null, "hello"));
            Assert.AreEqual("H", ConversationHandover.Compose("H", ""));
        }
    }
}
