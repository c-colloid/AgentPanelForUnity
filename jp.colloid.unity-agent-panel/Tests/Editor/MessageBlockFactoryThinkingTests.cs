using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards PanelSettings.showThinking (docs/design-notes/2026-08-01-
    /// settings-enrichment.md #2): MessageBlockFactory must skip Thinking
    /// blocks entirely when it is off. Mutates
    /// PanelStateStore.instance.Settings.showThinking directly WITHOUT
    /// calling SaveNow() -- the change never reaches disk, only this
    /// editor session's in-memory singleton -- and restores the original
    /// value in TearDown so this suite can never leak state into another
    /// test or the sandbox project's real settings.
    /// </summary>
    public class MessageBlockFactoryThinkingTests
    {
        private bool _originalShowThinking;

        [SetUp]
        public void SetUp()
        {
            _originalShowThinking = PanelStateStore.instance.Settings.showThinking;
        }

        [TearDown]
        public void TearDown()
        {
            PanelStateStore.instance.Settings.showThinking = _originalShowThinking;
        }

        private static ChatMessageBlock MakeThinkingBlock()
        {
            return ChatMessageBlock.MakeThinking("some reasoning text");
        }

        [Test]
        public void ShowThinkingTrue_RendersTheThinkingBlock()
        {
            PanelStateStore.instance.Settings.showThinking = true;
            VisualElement element = MessageBlockFactory.CreateBlockElement(MakeThinkingBlock(), null);
            Assert.IsNotNull(element);
        }

        [Test]
        public void ShowThinkingFalse_SkipsTheThinkingBlockEntirely()
        {
            PanelStateStore.instance.Settings.showThinking = false;
            VisualElement element = MessageBlockFactory.CreateBlockElement(MakeThinkingBlock(), null);
            Assert.IsNull(element);
        }

        [Test]
        public void ShowThinkingFalse_MessageWithOnlyAThinkingBlock_ProducesOnlyTheRoleRow()
        {
            PanelStateStore.instance.Settings.showThinking = false;
            var message = new ChatMessage { role = ChatMessage.RoleAssistant };
            message.Add(MakeThinkingBlock());
            VisualElement root = MessageBlockFactory.CreateMessageElement(message, null);
            // The role row (Claude/spark) still renders; only the thinking
            // block itself is dropped, so the root has exactly one child.
            Assert.AreEqual(1, root.childCount);
        }

        [Test]
        public void ShowThinkingFalse_StreamingThinkingBlock_IsAlsoSkipped()
        {
            // Live-streaming path (block.streaming == true) goes through
            // the exact same switch in CreateBlockElement -- covering it
            // separately guards against a future refactor accidentally
            // special-casing the streaming branch back in.
            PanelStateStore.instance.Settings.showThinking = false;
            ChatMessageBlock streaming = ChatMessageBlock.MakeThinking(string.Empty, true);
            VisualElement element = MessageBlockFactory.CreateBlockElement(streaming, null);
            Assert.IsNull(element);
        }

        // ---------------------------------------------------------------
        // Empty-text indicator vs. foldout (design note 2026-08-01-
        // thinking-content-loss.md section 5): the CLI never sends thinking
        // body text today, so an empty-text Thinking block must render the
        // compact token-count indicator instead of a foldout with nothing
        // inside it. Non-empty text (forward-compat) keeps the foldout.
        // ---------------------------------------------------------------

        [Test]
        public void EmptyText_NonStreaming_RendersIndicator_NotFoldout()
        {
            PanelStateStore.instance.Settings.showThinking = true;
            ChatMessageBlock block = ChatMessageBlock.MakeThinking(string.Empty);
            block.thinkingTokens = 7;
            VisualElement element = MessageBlockFactory.CreateBlockElement(block, null);

            Assert.IsNotNull(element);
            Assert.IsNotInstanceOf<Foldout>(element);
            Assert.IsInstanceOf<Label>(element);
            Assert.AreEqual(L10n.F(L10n.S.ChatThinkingIndicatorDoneTokensFmt, 7L),
                ((Label)element).text);
        }

        [Test]
        public void EmptyText_Streaming_RendersIndicator_WithLiveTokenCount()
        {
            PanelStateStore.instance.Settings.showThinking = true;
            ChatMessageBlock block = ChatMessageBlock.MakeThinking(string.Empty, true);
            block.thinkingTokens = 143;
            VisualElement element = MessageBlockFactory.CreateBlockElement(block, null);

            Assert.IsInstanceOf<Label>(element);
            Assert.AreEqual(L10n.F(L10n.S.ChatThinkingIndicatorStreamingTokensFmt, 143L),
                ((Label)element).text);
        }

        [Test]
        public void EmptyText_ZeroTokenEstimate_OmitsTheParenthetical()
        {
            PanelStateStore.instance.Settings.showThinking = true;
            ChatMessageBlock block = ChatMessageBlock.MakeThinking(string.Empty);
            // thinkingTokens left at its default (0/unknown).
            VisualElement element = MessageBlockFactory.CreateBlockElement(block, null);

            Assert.AreEqual(L10n.S.ChatThinkingIndicatorDone, ((Label)element).text);
        }

        [Test]
        public void NonEmptyText_StillRendersFoldout_NotIndicator()
        {
            PanelStateStore.instance.Settings.showThinking = true;
            VisualElement element = MessageBlockFactory.CreateBlockElement(MakeThinkingBlock(), null);

            Assert.IsInstanceOf<Foldout>(element);
        }

        // ---------------------------------------------------------------
        // redacted_thinking (design note 2026-08-01-thinking-content-loss.md
        // section 6): a subtle one-line note, never a foldout -- there is
        // no readable text to expand.
        // ---------------------------------------------------------------

        [Test]
        public void RedactedThinking_RendersTheRedactedNote_NotFoldout()
        {
            PanelStateStore.instance.Settings.showThinking = true;
            VisualElement element = MessageBlockFactory.CreateBlockElement(
                ChatMessageBlock.MakeRedactedThinking(), null);

            Assert.IsNotNull(element);
            Assert.IsNotInstanceOf<Foldout>(element);
            Assert.IsInstanceOf<Label>(element);
            Assert.AreEqual(L10n.S.ChatThinkingRedactedNote, ((Label)element).text);
        }

        [Test]
        public void RedactedThinking_ShowThinkingFalse_IsAlsoSkipped()
        {
            // Same gate as every other Thinking-kind block (it reuses the
            // kind on purpose -- see ChatMessageBlock.thinkingRedacted's
            // doc comment).
            PanelStateStore.instance.Settings.showThinking = false;
            VisualElement element = MessageBlockFactory.CreateBlockElement(
                ChatMessageBlock.MakeRedactedThinking(), null);

            Assert.IsNull(element);
        }
    }
}
