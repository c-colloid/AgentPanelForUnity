using Colloid.AgentPanel.UI;
using Colloid.AgentPanel.UI.Markdown;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression suite for the escape chokepoint (ARCHITECTURE.md risk 8).
    /// The converter output feeds Labels with enableRichText = TRUE, so
    /// the injection cases here are the security boundary of the panel:
    /// if any of them regresses, model output can inject live rich-text
    /// tags into the editor UI.
    /// </summary>
    public class MarkdownConverterTests
    {
        private System.Func<string, bool> _savedPathExists;

        [SetUp]
        public void SetUp()
        {
            _savedPathExists = AssetLinkHub.PathExists;
            AssetLinkHub.PathExists = null;
            InlineMarkupConverter.ConfigureColors(
                InlineMarkupConverter.DefaultLinkColor,
                InlineMarkupConverter.DefaultCodeColor,
                InlineMarkupConverter.DefaultCodeMark);
        }

        [TearDown]
        public void TearDown()
        {
            AssetLinkHub.PathExists = _savedPathExists;
        }

        // -- Injection suite (the centerpiece) ----------------------------------
        //
        // 2022.3 TextCore decodes NO HTML entities (verified against
        // UnityCsReference 2022.3 TextGenerator.cs), so the chokepoint
        // neutralizes '<' via a self-closing noparse pair
        // (InlineMarkupConverter.NeutralizedLt) and leaves the inert
        // '>' / '&' raw for display fidelity.

        private const string Lt = InlineMarkupConverter.NeutralizedLt;

        [Test]
        public void Convert_GenericTypeName_IsNeutralized()
        {
            Assert.AreEqual("List" + Lt + "int>",
                InlineMarkupConverter.Convert("List<int>"));
        }

        [Test]
        public void Convert_ColorTagInjection_IsNeutralized()
        {
            string result = InlineMarkupConverter.Convert("<color=red>x</color>");
            Assert.AreEqual(Lt + "color=red>x" + Lt + "/color>", result);
            StringAssert.DoesNotContain("<color", result);
        }

        [Test]
        public void Convert_BoldTagInjection_IsNeutralized()
        {
            Assert.AreEqual(Lt + "b>", InlineMarkupConverter.Convert("<b>"));
        }

        [Test]
        public void Convert_EntityText_PassesThroughRaw()
        {
            // The renderer does not decode entities: a model emitting the
            // literal text "&lt;" must display exactly "&lt;". '&' is
            // inert to the tag parser, so it stays raw.
            Assert.AreEqual("&lt;", InlineMarkupConverter.Convert("&lt;"));
        }

        [Test]
        public void Convert_LinkTagInjection_IsNeutralized()
        {
            string result = InlineMarkupConverter.Convert("<link=\"evil\">x</link>");
            StringAssert.DoesNotContain("<link", result);
            StringAssert.Contains(Lt + "link=\"evil\">", result);
        }

        [Test]
        public void Convert_NoparseCloserInjection_IsNeutralized()
        {
            // "</noparse>" in model text must not close OUR noparse pair:
            // its '<' is itself neutralized, leaving "/noparse>" as text.
            Assert.AreEqual(Lt + "/noparse>",
                InlineMarkupConverter.Convert("</noparse>"));
        }

        [Test]
        public void Convert_NoparseOpenerInjection_IsNeutralized()
        {
            // "<noparse>" in model text must not open a live noparse
            // region (that would swallow our own following tags).
            Assert.AreEqual(Lt + "noparse>",
                InlineMarkupConverter.Convert("<noparse>"));
        }

        [Test]
        public void Convert_CdataCloser_PassesThroughRaw()
        {
            // '>' with no opener is a literal glyph to the parser.
            Assert.AreEqual("]]>", InlineMarkupConverter.Convert("]]>"));
        }

        [Test]
        public void Convert_TagInsideInlineCode_StaysNeutralized()
        {
            string result = InlineMarkupConverter.Convert("`<color=red>`");
            StringAssert.Contains(Lt + "color=red>", result);
            StringAssert.DoesNotContain("<color=red>", result);
        }

        [Test]
        public void Convert_AmpersandAlone_PassesThroughRaw()
        {
            Assert.AreEqual("a & b", InlineMarkupConverter.Convert("a & b"));
        }

        [Test]
        public void Convert_CjkText_PassesThroughUnchanged()
        {
            // "Japanese text + kanji + halfwidth katakana" built from \u
            // escapes so the source file stays strictly ASCII.
            string cjk = "\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8\u3068"
                + "\u6F22\u5B57\u3001\uFF76\uFF80\uFF76\uFF85\u3002";
            Assert.AreEqual(cjk, InlineMarkupConverter.Convert(cjk));
        }

        // -- Inline conversions ---------------------------------------------------

        [Test]
        public void Convert_Bold_ProducesBoldTags()
        {
            Assert.AreEqual("<b>bold</b>", InlineMarkupConverter.Convert("**bold**"));
        }

        [Test]
        public void Convert_Italic_ProducesItalicTags()
        {
            Assert.AreEqual("<i>italic</i>", InlineMarkupConverter.Convert("*italic*"));
        }

        [Test]
        public void Convert_UnterminatedBold_StaysLiteral()
        {
            Assert.AreEqual("**dangling", InlineMarkupConverter.Convert("**dangling"));
        }

        [Test]
        public void Convert_AsteriskMath_StaysLiteral()
        {
            // "2 * 3 * 4": closing '*' exists but inner text has boundary
            // whitespace, so no italic conversion happens.
            Assert.AreEqual("2 * 3 * 4", InlineMarkupConverter.Convert("2 * 3 * 4"));
        }

        [Test]
        public void Convert_InlineCode_ProducesColorOnly_NoMark()
        {
            // REGRESSION GUARD (2026-07-31 live feedback): <mark> quads
            // render above some glyphs and below others under a DynamicOS
            // FontAsset (per-atlas-page ordering), producing patchy-bold /
            // washed-out inline code -- and fully hiding text when opaque.
            // Inline code must therefore stay color-only.
            string result = InlineMarkupConverter.Convert("`x`");
            Assert.AreEqual("<color=#D8B4A0>x</color>", result);
            StringAssert.DoesNotContain("<mark", result);
        }

        [Test]
        public void Convert_MarkdownInsideInlineCode_IsNotConverted()
        {
            string result = InlineMarkupConverter.Convert("`**a** [b](https://c.d)`");
            // Short code spans keep their chip on one line: internal
            // spaces become NO-BREAK SPACE (U+00A0) -- see
            // InlineMarkupConverter.NoWrapShortCodeSpan. Ordinal Contains
            // on purpose (culture comparison collates NBSP like a space).
            string nbsp = ((char)0x00A0).ToString();
            Assert.IsTrue(result.Contains("**a**" + nbsp + "[b](https://c.d)"),
                "markdown inside the span must stay literal: " + result);
            StringAssert.DoesNotContain("<b>", result);
            StringAssert.DoesNotContain("<link", result);
        }

        [Test]
        public void Convert_UnterminatedBacktick_StaysLiteral()
        {
            Assert.AreEqual("a `b", InlineMarkupConverter.Convert("a `b"));
        }

        [Test]
        public void Convert_BoldWithNeutralizedPayload_KeepsMarkersInside()
        {
            Assert.AreEqual("<b>List" + Lt + "T></b>",
                InlineMarkupConverter.Convert("**List<T>**"));
        }

        // -- Links ------------------------------------------------------------------

        [Test]
        public void Convert_MarkdownHttpLink_ProducesLinkTag()
        {
            string result = InlineMarkupConverter.Convert("[docs](https://example.com/a)");
            Assert.AreEqual(
                "<link=\"https://example.com/a\"><color=#4C7EFF>docs</color></link>",
                result);
        }

        [Test]
        public void Convert_MarkdownLinkNonHttpScheme_StaysLiteral()
        {
            string result = InlineMarkupConverter.Convert("[x](javascript:alert(1))");
            StringAssert.DoesNotContain("<link", result);
        }

        [Test]
        public void Convert_BareUrl_ProducesLinkTag()
        {
            string result = InlineMarkupConverter.Convert("see https://a.example/path.");
            Assert.AreEqual(
                "see <link=\"https://a.example/path\"><color=#4C7EFF>"
                + "https://a.example/path</color></link>.",
                result);
        }

        [Test]
        public void Convert_BareUrlWithQuery_KeepsRawAmpersandAndRoundTrips()
        {
            string result = InlineMarkupConverter.Convert("https://a.b/?x=1&y=2");
            StringAssert.Contains("<link=\"https://a.b/?x=1&y=2\">", result);
            Assert.AreEqual("https://a.b/?x=1&y=2",
                InlineMarkupConverter.Unescape("https://a.b/?x=1&y=2"));
        }

        [Test]
        public void Convert_UrlInsideWord_IsNotLinkified()
        {
            string result = InlineMarkupConverter.Convert("xhttps://a.b");
            StringAssert.DoesNotContain("<link", result);
        }

        [Test]
        public void Convert_BareUrlFollowedByLt_EndsLinkAtTheMarker()
        {
            // The neutralized '<' must never be swallowed into a link id
            // (it would corrupt the quoted attribute).
            string result = InlineMarkupConverter.Convert("https://a.b/x<y");
            StringAssert.Contains("<link=\"https://a.b/x\">", result);
            StringAssert.Contains(Lt + "y", result);
        }

        [Test]
        public void Convert_MarkdownLinkUrlWithLt_RejectsTheMarkdownLink()
        {
            // The [text](url) construct must be rejected ('[' stays
            // literal) and the marker may never enter a link id. The safe
            // bare-url prefix linkifying on its own is acceptable.
            string result = InlineMarkupConverter.Convert("[x](https://a.b/<i>)");
            StringAssert.Contains("[x](", result);
            StringAssert.DoesNotContain("<link=\"https://a.b/<", result);
        }

        [Test]
        public void Convert_MarkdownLinkUrlWithGt_RejectsTheMarkdownLink()
        {
            // '>' ends a rich-text tag even inside a quoted attribute, so
            // it may never enter a link id.
            string result = InlineMarkupConverter.Convert("[x](https://a.b/a>b)");
            StringAssert.Contains("[x](", result);
            StringAssert.DoesNotContain("<link=\"https://a.b/a>b\">", result);
        }

        // -- Asset path gating -----------------------------------------------------------

        [Test]
        public void Convert_ExistingAssetPath_IsLinkified()
        {
            AssetLinkHub.PathExists = delegate(string p)
            {
                return p == "Assets/Scripts/Player.cs";
            };
            string result = InlineMarkupConverter.Convert("see Assets/Scripts/Player.cs now");
            StringAssert.Contains("<link=\"Assets/Scripts/Player.cs\">", result);
        }

        [Test]
        public void Convert_MissingAssetPath_StaysPlainText()
        {
            AssetLinkHub.PathExists = delegate { return false; };
            string result = InlineMarkupConverter.Convert("see Assets/Nope.cs now");
            StringAssert.DoesNotContain("<link", result);
            StringAssert.Contains("Assets/Nope.cs", result);
        }

        [Test]
        public void Convert_AssetPathWithoutProbe_StaysPlainText()
        {
            // PathExists unassigned (null) must behave as "false".
            string result = InlineMarkupConverter.Convert("Assets/Foo.cs");
            StringAssert.DoesNotContain("<link", result);
        }

        [Test]
        public void Convert_PackagesPath_IsLinkified()
        {
            AssetLinkHub.PathExists = delegate { return true; };
            string result = InlineMarkupConverter.Convert(
                "Packages/jp.colloid.unity-agent-panel/README.md,");
            StringAssert.Contains(
                "<link=\"Packages/jp.colloid.unity-agent-panel/README.md\">", result);
            StringAssert.EndsWith(",", result);
        }

        [Test]
        public void Convert_ThrowingPathProbe_IsSwallowedAndNotLinkified()
        {
            AssetLinkHub.PathExists = delegate(string p)
            {
                throw new System.InvalidOperationException("boom");
            };
            string result = InlineMarkupConverter.Convert("Assets/Foo.cs");
            StringAssert.DoesNotContain("<link", result);
        }

        // -- Escape / Unescape -------------------------------------------------------------

        [Test]
        public void EscapeUnescape_RoundTripsMetaCharacters()
        {
            string raw = "a<b>&c &lt; d <noparse>";
            Assert.AreEqual(raw,
                InlineMarkupConverter.Unescape(InlineMarkupConverter.Escape(raw)));
        }

        [Test]
        public void Escape_NeutralizesOnlyLt()
        {
            Assert.AreEqual("x" + Lt + "y > z & w",
                InlineMarkupConverter.Escape("x<y > z & w"));
        }

        // -- Block-level fences (MarkdownRenderer) -------------------------------------------

        [Test]
        public void Render_UnterminatedFence_RendersTailAsCode()
        {
            VisualElement root = MarkdownRenderer.Render(
                "before\n```csharp\nvar x = 1;\nvar y = 2;");
            Assert.AreEqual(1, root.Query<CodeBlockElement>().ToList().Count);
            TextField field = root.Q<TextField>();
            Assert.IsNotNull(field);
            StringAssert.Contains("var x = 1;", field.value);
            StringAssert.Contains("var y = 2;", field.value);
        }

        [Test]
        public void Render_FenceMarkersInsideCode_DoNotNest()
        {
            // The first ``` after the opener closes the block: the inner
            // "nested" fence content becomes a separate paragraph/blocks,
            // never a runaway nested parse.
            VisualElement root = MarkdownRenderer.Render(
                "```\nouter\n```\nplain\n```\ninner\n```");
            Assert.AreEqual(2, root.Query<CodeBlockElement>().ToList().Count);
        }

        [Test]
        public void Render_ParagraphWithGenericType_KeepsNeutralizedLt()
        {
            VisualElement root = MarkdownRenderer.Render("returns List<int> now");
            Label label = root.Q<Label>(className: "uap-md-p");
            Assert.IsNotNull(label);
            StringAssert.Contains("List" + Lt + "int>", label.text);
            Assert.IsTrue(label.enableRichText);
        }

        [Test]
        public void Render_CodeFenceBody_IsNotEscapedOrConverted()
        {
            // The code body goes into a plain TextField (no rich text), so
            // it must keep raw angle brackets for copy fidelity.
            VisualElement root = MarkdownRenderer.Render("```\nList<int> x;\n```");
            TextField field = root.Q<TextField>();
            Assert.IsNotNull(field);
            Assert.AreEqual("List<int> x;", field.value);
            Assert.IsTrue(field.isReadOnly);
        }

        /// <summary>
        /// UICODE-12: output cut right after the fence opener used to drop
        /// the fence entirely -- body-length was guarded before the
        /// defensive tail flush. An empty read-only code block (language
        /// signal preserved) is the honest rendering of that cut.
        /// </summary>
        [TestCase("```csharp")]
        [TestCase("```csharp\n")]
        public void Render_UnterminatedFenceWithEmptyBody_StillRendersACodeBlock(string markdown)
        {
            VisualElement root = MarkdownRenderer.Render(markdown);
            TextField field = root.Q<TextField>();
            Assert.IsNotNull(field, "the unterminated fence must render, not vanish");
            Assert.AreEqual(string.Empty, field.value);
        }

        [Test]
        public void Render_HeadingsAndLists_ProduceExpectedClasses()
        {
            VisualElement root = MarkdownRenderer.Render(
                "# H1\n## H2\n- item one\n  - nested\n1. ordered\n> quoted\n---");
            Assert.IsNotNull(root.Q<Label>(className: "uap-md-h1"));
            Assert.IsNotNull(root.Q<Label>(className: "uap-md-h2"));
            Assert.AreEqual(3, root.Query(className: "uap-md-li").ToList().Count);
            Assert.AreEqual(1, root.Query(className: "uap-md-li--nested").ToList().Count);
            Assert.IsNotNull(root.Q(className: "uap-md-quote"));
            Assert.IsNotNull(root.Q(className: "uap-md-hr"));
        }
    }
}
