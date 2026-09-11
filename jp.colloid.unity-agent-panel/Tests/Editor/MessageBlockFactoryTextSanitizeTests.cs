using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// CreateTextBlock's pump==null fallback (MessageBlockFactory.cs)
    /// used to assign a streaming block's raw model text straight to
    /// label.text, completely bypassing IconLoader.SanitizeForDisplay --
    /// unlike the pump.Track(block, label) branch, which sanitizes via
    /// StreamingLabelPump. Not reachable through the production
    /// ChatView->MessageListController wiring (ChatView always
    /// constructs a non-null StreamingLabelPump), but it is a live code
    /// path exercised directly by any caller that supplies a null pump
    /// while a block is streaming, so it must sanitize too.
    /// </summary>
    public class MessageBlockFactoryTextSanitizeTests
    {
        [Test]
        public void StreamingTextBlock_NullPump_StillSanitizesDisplayText()
        {
            string grinningFace = char.ConvertFromUtf32(0x1F600); // uncurated pictograph
            string raw = "status " + grinningFace + " ok";
            ChatMessageBlock block = ChatMessageBlock.MakeText(raw, true);

            VisualElement element = MessageBlockFactory.CreateBlockElement(block, null);

            Assert.IsInstanceOf<Label>(element);
            var label = (Label)element;
            Assert.AreEqual(IconLoader.SanitizeForDisplay(raw), label.text);
            Assert.AreNotEqual(raw, label.text,
                "the emoji must not survive verbatim through the null-pump fallback");
        }

        [Test]
        public void StreamingTextBlock_WithPump_AlsoSanitizes_ForComparison()
        {
            // Cross-check against the pump.Track(block, label) branch of
            // the same switch (StreamingLabelPump appends a trailing
            // cursor glyph on top of the sanitized text, unlike the
            // null-pump fallback) -- confirms both branches sanitize the
            // SAME raw text, just with the pump's extra decoration.
            string grinningFace = char.ConvertFromUtf32(0x1F600);
            string raw = "status " + grinningFace + " ok";
            ChatMessageBlock block = ChatMessageBlock.MakeText(raw, true);

            var pump = new StreamingLabelPump();
            VisualElement element = MessageBlockFactory.CreateBlockElement(block, pump);

            Assert.IsInstanceOf<Label>(element);
            Assert.AreEqual(IconLoader.SanitizeForDisplay(raw) + IconLoader.GlyphCursor,
                ((Label)element).text);
        }

        // -----------------------------------------------------------------
        // SystemNote warning styling (design note 2026-08-14-ui-polish-
        // audit.md contract item 2): ChatMessageBlock.MakeSystemNote's new
        // `warning` flag controls whether the rendered note also carries
        // .uap-note--warn on top of the always-present .uap-note.
        // -----------------------------------------------------------------

        [Test]
        public void SystemNote_Warning_AddsTheWarnClass()
        {
            ChatMessageBlock block = ChatMessageBlock.MakeSystemNote("Claude CLI process died. Reconnecting...",
                warning: true);

            VisualElement element = MessageBlockFactory.CreateBlockElement(block, null);

            Assert.IsInstanceOf<Label>(element);
            Assert.IsTrue(element.ClassListContains("uap-note"));
            Assert.IsTrue(element.ClassListContains("uap-note--warn"),
                "a warning note must carry uap-note--warn alongside the plain uap-note class");
        }

        [Test]
        public void SystemNote_NonWarning_OmitsTheWarnClass()
        {
            ChatMessageBlock block = ChatMessageBlock.MakeSystemNote("Denied Bash");

            VisualElement element = MessageBlockFactory.CreateBlockElement(block, null);

            Assert.IsInstanceOf<Label>(element);
            Assert.IsTrue(element.ClassListContains("uap-note"));
            Assert.IsFalse(element.ClassListContains("uap-note--warn"),
                "the default (non-warning) note must render exactly like every pre-existing "
                    + "system note, without uap-note--warn");
        }
    }
}
