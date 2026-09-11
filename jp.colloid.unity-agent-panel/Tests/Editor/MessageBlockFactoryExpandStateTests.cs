using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards ExpandStateMemory (design note 2026-09-10-foldouts-reset-on-
    /// transcript-update.md): the collapsible pieces of a message that have
    /// no toolUseId of their own -- thinking Foldout, "N tools" group row,
    /// context-attachment chip -- must come back in the state the user left
    /// them when MessageListController rebuilds the message element, which
    /// it does on every appended block / status change.
    ///
    /// Panel-less SendEvent is a no-op in EditMode (ComposerEnterActionTests
    /// records the same), so the user's click/toggle is simulated by writing
    /// the memory through ExpandStateMemory directly and the rebuilt element
    /// is inspected structurally; the one-line callbacks that write it in
    /// production are the same Set() call. Same test-seam idiom as
    /// ToolActivityCardExpandStateTests.
    /// </summary>
    public class MessageBlockFactoryExpandStateTests
    {
        private bool _originalShowThinking;

        [SetUp]
        public void SetUp()
        {
            ExpandStateMemory.ResetForTests();
            ToolActivityCard.ResetExpandedStateForTests();
            _originalShowThinking = PanelStateStore.instance.Settings.showThinking;
            PanelStateStore.instance.Settings.showThinking = true;
        }

        [TearDown]
        public void TearDown()
        {
            ExpandStateMemory.ResetForTests();
            ToolActivityCard.ResetExpandedStateForTests();
            PanelStateStore.instance.Settings.showThinking = _originalShowThinking;
        }

        private static ToolCallRecord MakeCompletedToolCall(string toolUseId)
        {
            return new ToolCallRecord
            {
                toolUseId = toolUseId,
                toolName = "Read",
                status = ToolCallStatus.Succeeded,
                inputJson = "{\"file_path\":\"Assets/A.cs\"}",
                resultSummary = "12 lines"
            };
        }

        private static ChatMessage MakeAssistantWithThinkingAndTools(int toolCount)
        {
            var message = new ChatMessage { role = ChatMessage.RoleAssistant };
            message.Add(ChatMessageBlock.MakeThinking("reasoning body"));
            for (int i = 0; i < toolCount; i++)
            {
                message.Add(ChatMessageBlock.MakeToolCall(MakeCompletedToolCall("toolu_" + i)));
            }
            return message;
        }

        // -- BlockKey -------------------------------------------------------------

        [Test]
        public void BlockKey_IsStablePerMessageAndIndex_AndNullWithoutOwner()
        {
            Assert.AreEqual("m1/3", ExpandStateMemory.BlockKey("m1", 3));
            Assert.AreEqual(ExpandStateMemory.BlockKey("m1", 3), ExpandStateMemory.BlockKey("m1", 3));
            Assert.AreNotEqual(ExpandStateMemory.BlockKey("m1", 3), ExpandStateMemory.BlockKey("m2", 3));
            Assert.IsNull(ExpandStateMemory.BlockKey(null, 0));
            Assert.IsNull(ExpandStateMemory.BlockKey(string.Empty, 0));
        }

        [Test]
        public void Set_WithNullKey_IsIgnored_AndGetFallsBack()
        {
            Assert.DoesNotThrow(() => ExpandStateMemory.Set(null, true));
            Assert.IsFalse(ExpandStateMemory.Get(null, false));
            Assert.IsTrue(ExpandStateMemory.Get(null, true));
        }

        // -- Thinking Foldout --------------------------------------------------------

        [Test]
        public void ThinkingFoldout_FreshMessage_StartsClosed()
        {
            ChatMessage message = MakeAssistantWithThinkingAndTools(0);
            VisualElement element = MessageBlockFactory.CreateMessageElement(message, null);

            Foldout foldout = element.Q<Foldout>(className: "uap-thinking");
            Assert.IsNotNull(foldout);
            Assert.IsFalse(foldout.value);
        }

        [Test]
        public void ThinkingFoldout_RebuiltAfterBlockAppended_RestoresOpenState()
        {
            ChatMessage message = MakeAssistantWithThinkingAndTools(0);
            MessageBlockFactory.CreateMessageElement(message, null);

            // The user opens the foldout (its ChangeEvent writes this key).
            ExpandStateMemory.Set(ExpandStateMemory.BlockKey(message.id, 0), true);

            // A tool block lands -> signature changes -> the whole message
            // element is rebuilt from scratch.
            message.Add(ChatMessageBlock.MakeToolCall(MakeCompletedToolCall("toolu_x")));
            VisualElement rebuilt = MessageBlockFactory.CreateMessageElement(message, null);

            Foldout foldout = rebuilt.Q<Foldout>(className: "uap-thinking");
            Assert.IsTrue(foldout.value,
                "the thinking foldout must not close just because a tool block was appended");
        }

        [Test]
        public void ThinkingFoldout_StreamingToFinal_KeepsOpenState()
        {
            var message = new ChatMessage { role = ChatMessage.RoleAssistant };
            message.Add(ChatMessageBlock.MakeThinking("partial", true));
            MessageBlockFactory.CreateMessageElement(message, null);
            ExpandStateMemory.Set(ExpandStateMemory.BlockKey(message.id, 0), true);

            message.blocks[0].streaming = false;
            VisualElement rebuilt = MessageBlockFactory.CreateMessageElement(message, null);

            Assert.IsTrue(rebuilt.Q<Foldout>(className: "uap-thinking").value);
        }

        [Test]
        public void ThinkingFoldout_OtherMessage_StaysClosed()
        {
            ChatMessage opened = MakeAssistantWithThinkingAndTools(0);
            ExpandStateMemory.Set(ExpandStateMemory.BlockKey(opened.id, 0), true);

            ChatMessage other = MakeAssistantWithThinkingAndTools(0);
            VisualElement element = MessageBlockFactory.CreateMessageElement(other, null);

            Assert.IsFalse(element.Q<Foldout>(className: "uap-thinking").value);
        }

        [Test]
        public void ThinkingFoldout_WithoutStateKey_StartsClosed()
        {
            VisualElement element = MessageBlockFactory.CreateBlockElement(
                ChatMessageBlock.MakeThinking("body"), null);
            Assert.IsFalse(((Foldout)element).value);
        }

        // -- Tool group row ----------------------------------------------------------

        private static VisualElement GroupChildren(VisualElement root)
        {
            return root.Q<VisualElement>(className: "uap-toolgroup-children");
        }

        [Test]
        public void GroupRow_Fresh_StartsClosed()
        {
            ChatMessage message = MakeAssistantWithThinkingAndTools(3);
            VisualElement element = MessageBlockFactory.CreateMessageElement(message, null);

            VisualElement children = GroupChildren(element);
            Assert.IsNotNull(children, "3 completed tools compress into one group row");
            Assert.AreEqual(DisplayStyle.None, children.style.display.value);
        }

        [Test]
        public void GroupRow_RebuiltAfterBlockAppended_RestoresOpenState()
        {
            ChatMessage message = MakeAssistantWithThinkingAndTools(3);
            MessageBlockFactory.CreateMessageElement(message, null);
            // The group's key is the run's FIRST block (index 1: index 0 is
            // the thinking block).
            ExpandStateMemory.Set(ExpandStateMemory.BlockKey(message.id, 1), true);

            message.Add(ChatMessageBlock.MakeText("done"));
            VisualElement rebuilt = MessageBlockFactory.CreateMessageElement(message, null);

            Assert.AreEqual(DisplayStyle.Flex, GroupChildren(rebuilt).style.display.value,
                "an opened group row must stay open across a message rebuild");
        }

        [Test]
        public void GroupRow_OpensItself_WhenASwallowedCardWasExpanded()
        {
            // Two completed cards render individually; the user expands
            // the first one.
            ChatMessage message = MakeAssistantWithThinkingAndTools(2);
            VisualElement before = MessageBlockFactory.CreateMessageElement(message, null);
            List<ToolActivityCard> cards = before.Query<ToolActivityCard>().ToList();
            Assert.AreEqual(2, cards.Count);
            Assert.IsNull(GroupChildren(before), "sanity: below the 3-card threshold, no group");
            cards[0].ToggleExpandedForTests();
            Assert.IsTrue(cards[0].IsExpandedForTests);

            // The third completion folds all three into a group row.
            message.Add(ChatMessageBlock.MakeToolCall(MakeCompletedToolCall("toolu_2")));
            VisualElement after = MessageBlockFactory.CreateMessageElement(message, null);

            VisualElement children = GroupChildren(after);
            Assert.IsNotNull(children);
            Assert.AreEqual(DisplayStyle.Flex, children.style.display.value,
                "a group must not hide a card the user had open behind a closed header");
            Assert.IsTrue(after.Query<ToolActivityCard>().ToList()[0].IsExpandedForTests,
                "and the card inside keeps its own expanded state");
        }

        [Test]
        public void GroupRow_OwnMemoryOutranksChildMemory()
        {
            var records = new List<ToolCallRecord>
            {
                MakeCompletedToolCall("toolu_a"), MakeCompletedToolCall("toolu_b"), MakeCompletedToolCall("toolu_c")
            };
            ToolActivityCard.ResetExpandedStateForTests();
            new ToolActivityCard(records[1]).ToggleExpandedForTests();
            Assert.IsTrue(ToolActivityCard.IsRememberedExpanded("toolu_b"), "sanity");

            // No group memory yet: child memory opens it.
            Assert.IsTrue(ToolActivityCard.ResolveGroupInitialExpanded(records, "m/1"));
            // The user closed the group explicitly: that wins.
            ExpandStateMemory.Set("m/1", false);
            Assert.IsFalse(ToolActivityCard.ResolveGroupInitialExpanded(records, "m/1"));
            // Without a key the child memory still applies.
            Assert.IsTrue(ToolActivityCard.ResolveGroupInitialExpanded(records, null));
        }

        // -- Context attachment chip -----------------------------------------------

        [Test]
        public void AttachmentChip_RebuiltMessage_RestoresOpenState()
        {
            var message = new ChatMessage { role = ChatMessage.RoleUser };
            message.Add(ChatMessageBlock.MakeContextAttachment("GameObject: Main Camera", "payload"));
            VisualElement first = MessageBlockFactory.CreateMessageElement(message, null);
            Assert.IsNull(first.Q<ScrollView>(className: "uap-attach-scroll"),
                "sanity: the payload box is built lazily on first expand");

            ExpandStateMemory.Set(ExpandStateMemory.BlockKey(message.id, 0), true);
            VisualElement rebuilt = MessageBlockFactory.CreateMessageElement(message, null);

            ScrollView payload = rebuilt.Q<ScrollView>(className: "uap-attach-scroll");
            Assert.IsNotNull(payload, "a remembered-open chip builds and shows its payload");
            Assert.AreEqual(DisplayStyle.Flex, payload.style.display.value);
            Assert.AreEqual(IconLoader.GlyphChevronDown,
                rebuilt.Q<Label>(className: "uap-attach-chevron").text);
        }

        [Test]
        public void AttachmentChip_Fresh_StartsClosed()
        {
            var message = new ChatMessage { role = ChatMessage.RoleUser };
            message.Add(ChatMessageBlock.MakeContextAttachment("Scene", "payload"));
            VisualElement element = MessageBlockFactory.CreateMessageElement(message, null);

            Assert.IsNull(element.Q<ScrollView>(className: "uap-attach-scroll"));
            Assert.AreEqual(IconLoader.GlyphChevronRight,
                element.Q<Label>(className: "uap-attach-chevron").text);
        }
    }
}
