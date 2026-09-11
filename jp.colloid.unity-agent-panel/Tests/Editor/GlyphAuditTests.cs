using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.UI;
using Colloid.AgentPanel.UI.Markdown;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Glyph safety audit (live-feedback defect: the paperclip U+1F4CE
    /// and model-emitted U+FE0F variation selectors have no glyph in the
    /// editor's Inter SDF font -- users saw placeholder squares plus
    /// continuous "not found in [Inter-Regular SDF]" console spam).
    ///
    /// Two layers of guard:
    ///   1. A SOURCE SCAN over every Editor/ .cs file: all constructed
    ///      codepoints (char.ConvertFromUtf32 literals and \uXXXX /
    ///      \UXXXXXXXX escapes) must be inside
    ///      IconLoader.SafeGlyphCodepoints, no source may contain a
    ///      variation-selector escape, and sources stay strict ASCII.
    ///   2. Runtime checks on the centralized IconLoader glyph registry
    ///      and the display sanitizers (StripVariationSelectors, the
    ///      InlineMarkupConverter chokepoint).
    /// </summary>
    public class GlyphAuditTests
    {
        private const string PackageEditorDir =
            "Packages/jp.colloid.unity-agent-panel/Editor";

        private static readonly Regex ConvertFromUtf32Pattern = new Regex(
            @"ConvertFromUtf32\(\s*0x([0-9A-Fa-f]+)\s*\)");
        private static readonly Regex CharCastPattern = new Regex(
            @"\(char\)\s*0x([0-9A-Fa-f]{2,6})");
        private static readonly Regex UnicodeEscapePattern = new Regex(
            @"\\u([0-9A-Fa-f]{4})");
        private static readonly Regex LongUnicodeEscapePattern = new Regex(
            @"\\U([0-9A-Fa-f]{8})");

        private static string[] EditorSourceFiles()
        {
            string root = Path.GetFullPath(PackageEditorDir);
            Assert.IsTrue(Directory.Exists(root),
                "package Editor dir not found: " + root);
            return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        }

        // -- Source scan ---------------------------------------------------------

        [Test]
        public void SourceScan_AllConstructedCodepoints_AreWhitelisted()
        {
            var offenders = new List<string>();
            foreach (string file in EditorSourceFiles())
            {
                string text = File.ReadAllText(file);
                CheckMatches(offenders, file, ConvertFromUtf32Pattern, text, false);
                // Non-rendered char-cast constants are exempt:
                // (char)0xFE0F / (char)0xFE0E / (char)0x200D comparisons
                // inside the sanitizers (they exist to REMOVE those
                // codepoints -- IconLoader.SanitizeForDisplay drops every
                // zero-width joiner unconditionally, see its doc comment)
                // and the (char)0xFEFF BOM skip in JsonParser.
                CheckMatches(offenders, file, CharCastPattern, text, true);
                CheckMatches(offenders, file, UnicodeEscapePattern, text, false);
                CheckMatches(offenders, file, LongUnicodeEscapePattern, text, false);
            }
            Assert.IsEmpty(offenders,
                "constructed codepoints outside IconLoader.SafeGlyphCodepoints"
                + " (add to the whitelist ONLY after verifying editor font"
                + " coverage):\n" + string.Join("\n", offenders.ToArray()));
        }

        [Test]
        public void SourceScan_NoVariationSelectorAnywhere()
        {
            // ORDINAL scans on purpose: culture-sensitive IndexOf treats
            // variation selectors as collation-ignorable and can "find"
            // them in strings that do not contain them.
            foreach (string file in EditorSourceFiles())
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(ContainsOrdinal(text, (char)0xFE0F),
                    "literal U+FE0F in " + file);
                Assert.IsFalse(ContainsOrdinal(text, (char)0xFE0E),
                    "literal U+FE0E in " + file);
                Assert.IsFalse(Regex.IsMatch(text, @"\\u[Ff][Ee]0[EFef]"),
                    "variation-selector escape in " + file);
            }
        }

        private static bool ContainsOrdinal(string haystack, char needle)
        {
            return haystack.IndexOf(needle) >= 0; // char overload = ordinal
        }

        /// <summary>
        /// The ONE legitimate home for non-ASCII UI text in this package
        /// (docs/design-notes/2026-08-01-i18n.md #2/#4). UiStringsJa.cs is a
        /// plain string-literal catalog (Editor/UI/L10n/UiStringsJa.cs) --
        /// every value in it still flows through the same CJK-font +
        /// FE0F-strip display chokepoints as any other text once rendered
        /// (AgentPanelWindow.ApplyCjkUiFont / IconLoader.StripVariationSelectors),
        /// so excluding it from the RAW-SOURCE-BYTE restriction below does
        /// NOT weaken glyph safety -- it only stops flagging Japanese text
        /// that was never a glyph-safety hazard in the first place. No
        /// other GlyphAuditTests rule is touched: the codepoint whitelist
        /// scan and the variation-selector scan below still cover this file
        /// too (defense in depth).
        /// </summary>
        private const string JapaneseCatalogFileName = "UiStringsJa.cs";

        [Test]
        public void SourceScan_EditorSources_AreStrictAscii()
        {
            foreach (string file in EditorSourceFiles())
            {
                if (Path.GetFileName(file) == JapaneseCatalogFileName)
                {
                    continue;
                }
                byte[] bytes = File.ReadAllBytes(file);
                for (int i = 0; i < bytes.Length; i++)
                {
                    Assert.LessOrEqual((int)bytes[i], 0x7F,
                        "non-ASCII byte at offset " + i + " in " + file);
                }
            }
        }

        /// <summary>
        /// Chokepoint-coverage guard (in the spirit of UssHygieneTests'
        /// source scans): every render path the emoji-font-warning-flood
        /// probe identified for untrusted model/user text must call
        /// IconLoader.SanitizeForDisplay at least the given number of
        /// times, so a future edit that quietly reverts one of these call
        /// sites back to StripVariationSelectors (or drops the sanitize
        /// entirely) fails a test instead of shipping a silent regression.
        /// Counts are exact-as-of-fix minimums, not upper bounds.
        /// </summary>
        [Test]
        public void SourceScan_KnownChokepoints_CallSanitizeForDisplay()
        {
            var expectedMinimumCalls = new Dictionary<string, int>
            {
                { "StreamingLabelPump.cs", 3 },
                { "SubagentCard.cs", 3 },
                { Path.Combine("Markdown", "InlineMarkupConverter.cs"), 1 },
                { "PermissionCard.cs", 3 },
                { "MessageBlockFactory.cs", 3 },
                { Path.Combine("Markdown", "CodeBlockElement.cs"), 2 },
                { "ToolActivityCard.cs", 3 },
                { "HeaderView.cs", 1 },
                { "HistoryView.cs", 1 },
            };
            string uiRoot = Path.GetFullPath(Path.Combine(PackageEditorDir, "UI"));
            foreach (KeyValuePair<string, int> pair in expectedMinimumCalls)
            {
                string path = Path.Combine(uiRoot, pair.Key);
                Assert.IsTrue(File.Exists(path), "chokepoint file not found: " + path);
                string text = File.ReadAllText(path);
                int count = CountOccurrences(text, "SanitizeForDisplay(");
                Assert.GreaterOrEqual(count, pair.Value,
                    pair.Key + ": expected at least " + pair.Value
                    + " IconLoader.SanitizeForDisplay call(s), found " + count);
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static void CheckMatches(List<string> offenders, string file,
            Regex pattern, string text, bool allowSelectorComparisons)
        {
            foreach (Match match in pattern.Matches(text))
            {
                int codepoint = System.Convert.ToInt32(match.Groups[1].Value, 16);
                if (allowSelectorComparisons
                    && (codepoint == 0xFE0F || codepoint == 0xFE0E
                        || codepoint == 0xFEFF || codepoint == 0x200D))
                {
                    continue;
                }
                if (!IconLoader.IsSafeGlyphCodepoint(codepoint))
                {
                    offenders.Add(Path.GetFileName(file) + ": U+"
                        + codepoint.ToString("X4") + " (" + match.Value + ")");
                }
            }
        }

        // -- Runtime registry ---------------------------------------------------------

        [Test]
        public void IconLoader_AllGlyphConstants_AreWhitelisted()
        {
            FieldInfo[] fields = typeof(IconLoader).GetFields(
                BindingFlags.Public | BindingFlags.Static);
            int checkedCount = 0;
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType != typeof(string)
                    || !field.Name.StartsWith("Glyph", System.StringComparison.Ordinal))
                {
                    continue;
                }
                checkedCount++;
                var glyph = (string)field.GetValue(null);
                Assert.IsNotEmpty(glyph, field.Name);
                for (int i = 0; i < glyph.Length; i++)
                {
                    int codepoint = char.ConvertToUtf32(glyph, i);
                    if (char.IsHighSurrogate(glyph[i]))
                    {
                        i++;
                    }
                    Assert.IsTrue(IconLoader.IsSafeGlyphCodepoint(codepoint),
                        field.Name + " contains non-whitelisted U+"
                        + codepoint.ToString("X4"));
                }
            }
            Assert.GreaterOrEqual(checkedCount, 10,
                "glyph registry fields moved or renamed");
        }

        [Test]
        public void IconLoader_Whitelist_ExcludesEmojiPlaneAndSelectors()
        {
            Assert.IsFalse(IconLoader.IsSafeGlyphCodepoint(0x1F4CE),
                "the paperclip must never come back");
            Assert.IsFalse(IconLoader.IsSafeGlyphCodepoint(0xFE0F));
            Assert.IsFalse(IconLoader.IsSafeGlyphCodepoint(0xFE0E));
            Assert.IsFalse(IconLoader.IsSafeGlyphCodepoint(0x1F600));
        }

        [Test]
        public void IconLoader_AttachmentFallback_IsPureAscii()
        {
            foreach (char c in IconLoader.GlyphAttachAscii)
            {
                Assert.IsTrue(c >= 0x20 && c < 0x7F,
                    "attachment fallback must be printable ASCII");
            }
        }

        // -- Sanitizers ------------------------------------------------------------------

        // All non-ASCII test data is constructed from codepoints so this
        // source file stays strict ASCII itself.
        private static readonly string Warn = char.ConvertFromUtf32(0x26A0);
        private static readonly string Check = char.ConvertFromUtf32(0x2713);
        private static readonly string Fe0F = ((char)0xFE0F).ToString();
        private static readonly string Fe0E = ((char)0xFE0E).ToString();

        [Test]
        public void StripVariationSelectors_RemovesFe0FAndFe0E()
        {
            string input = Warn + Fe0F + " done " + Check + Fe0E + "!";
            string output = IconLoader.StripVariationSelectors(input);
            Assert.AreEqual(Warn + " done " + Check + "!", output);
        }

        [Test]
        public void StripVariationSelectors_ReturnsSameInstance_WhenClean()
        {
            string clean = "plain ascii and " + Check;
            Assert.AreSame(clean, IconLoader.StripVariationSelectors(clean));
        }

        [Test]
        public void StripVariationSelectors_NullAndEmpty_AreSafe()
        {
            Assert.AreEqual(string.Empty, IconLoader.StripVariationSelectors(null));
            Assert.AreEqual(string.Empty, IconLoader.StripVariationSelectors(""));
        }

        [Test]
        public void ConverterChokepoint_StripsVariationSelectors()
        {
            InlineMarkupConverter.ConfigureColors(
                InlineMarkupConverter.DefaultLinkColor,
                InlineMarkupConverter.DefaultCodeColor,
                InlineMarkupConverter.DefaultCodeMark);
            string converted = InlineMarkupConverter.Convert(
                "warning " + Warn + Fe0F + " ahead");
            // Ordinal on purpose: culture-sensitive Contains treats the
            // selector as ignorable and misreports on non-ASCII strings.
            Assert.IsFalse(ContainsOrdinal(converted, (char)0xFE0F),
                "chokepoint must strip U+FE0F");
            Assert.AreEqual("warning " + Warn + " ahead", converted);
        }

        [Test]
        public void ConverterEscape_StripsSelectors_KeepsLtNeutralization()
        {
            string escaped = InlineMarkupConverter.Escape("<x>" + Fe0F);
            Assert.IsFalse(ContainsOrdinal(escaped, (char)0xFE0F));
            Assert.AreEqual(InlineMarkupConverter.NeutralizedLt + "x>", escaped);
        }

        [Test]
        public void ConverterEscape_DropsUnknownEmoji_KeepsCuratedMapping()
        {
            string grinningFace = char.ConvertFromUtf32(0x1F600); // uncurated pictograph
            string checkMarkEmoji = char.ConvertFromUtf32(0x2705); // curated -> Check
            string escaped = InlineMarkupConverter.Escape(
                "go " + grinningFace + Check + checkMarkEmoji);
            Assert.AreEqual("go " + Check + Check, escaped);
        }

        // -- SanitizeForDisplay ------------------------------------------------------------

        private static readonly string Cross = char.ConvertFromUtf32(0x2715);

        [Test]
        public void SanitizeForDisplay_NullAndEmpty_AreSafe()
        {
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(null));
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(string.Empty));
        }

        [Test]
        public void SanitizeForDisplay_ReturnsSameInstance_WhenPureAscii()
        {
            string ascii = "plain ascii text 123 !?";
            Assert.AreSame(ascii, IconLoader.SanitizeForDisplay(ascii));
        }

        [Test]
        public void SanitizeForDisplay_CuratedMap_ReplacesCommonEmojiWithSafeGlyphs()
        {
            Assert.AreEqual(Check, IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x2705)));
            Assert.AreEqual(Check, IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x2714)));
            Assert.AreEqual(Cross, IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x274C)));
            Assert.AreEqual(Cross, IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x274E)));
            Assert.AreEqual("!", IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x2757)));
            Assert.AreEqual("?", IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x2753)));
            // Astral curated entry (pointer emoji -> existing chevron glyph).
            Assert.AreEqual(IconLoader.GlyphChevronRight,
                IconLoader.SanitizeForDisplay(char.ConvertFromUtf32(0x1F449)));
        }

        [Test]
        public void SanitizeForDisplay_WarningSign_StaysUnchanged()
        {
            // U+26A0 is already in SafeGlyphCodepoints -- the curated table
            // deliberately does not touch it (design note: "U+26A0 stays").
            Assert.AreEqual(Warn, IconLoader.SanitizeForDisplay(Warn));
        }

        [Test]
        public void SanitizeForDisplay_UnknownEmoji_IsDropped()
        {
            string grinningFace = char.ConvertFromUtf32(0x1F600);
            Assert.AreEqual("hello  world",
                IconLoader.SanitizeForDisplay("hello " + grinningFace + " world"));

            string thumbsUp = char.ConvertFromUtf32(0x1F44D);
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(thumbsUp));

            // Dingbats-block emoji with no curated entry and not whitelisted.
            string blackHeart = char.ConvertFromUtf32(0x2764);
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(blackHeart));
        }

        [Test]
        public void SanitizeForDisplay_RegionalIndicatorFlagEmoji_IsDropped()
        {
            // A two-letter country flag is a PAIR of Regional Indicator
            // Symbol codepoints (U+1F1E6-U+1F1FF), inside the Enclosed
            // Alphanumeric Supplement block (U+1F100-U+1F1FF) -- below the
            // OLD PictographRangeStart (0x1F300), so it used to pass
            // through completely unsanitized and reproduce the exact
            // font-warning-flood bug this sanitizer exists to eliminate.
            string regionalU = char.ConvertFromUtf32(0x1F1FA); // REGIONAL INDICATOR SYMBOL LETTER U
            string regionalS = char.ConvertFromUtf32(0x1F1F8); // REGIONAL INDICATOR SYMBOL LETTER S
            string usFlag = regionalU + regionalS;
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(usFlag));
            Assert.AreEqual("go  now",
                IconLoader.SanitizeForDisplay("go " + usFlag + " now"));
        }

        [Test]
        public void SanitizeForDisplay_MahjongDominoPlayingCardEnclosedIdeograph_AreDropped()
        {
            // Every sub-block between the NEW (0x1F000) and OLD (0x1F300)
            // PictographRangeStart is emoji/symbol-only: Mahjong Tiles,
            // Domino Tiles, Playing Cards, and the Enclosed Ideographic
            // Supplement -- all previously kept verbatim.
            string mahjongRedDragon = char.ConvertFromUtf32(0x1F004);
            string playingCardJoker = char.ConvertFromUtf32(0x1F0CF);
            string squaredKatakanaKoko = char.ConvertFromUtf32(0x1F201);
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(mahjongRedDragon));
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(playingCardJoker));
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(squaredKatakanaKoko));
        }

        [Test]
        public void SanitizeForDisplay_PreservesCjkAccentsAndBoxDrawing()
        {
            string japanese = char.ConvertFromUtf32(0x65E5) + char.ConvertFromUtf32(0x672C)
                + char.ConvertFromUtf32(0x8A9E); // "nihongo" (Japanese)
            string accented = char.ConvertFromUtf32(0x00E9); // "e" with acute accent
            string boxDrawing = char.ConvertFromUtf32(0x2500); // BOX DRAWINGS LIGHT HORIZONTAL
            string input = japanese + " " + accented + " " + boxDrawing;
            Assert.AreEqual(input, IconLoader.SanitizeForDisplay(input));
        }

        [Test]
        public void SanitizeForDisplay_PreservesNewlinesAndTabs()
        {
            string grinningFace = char.ConvertFromUtf32(0x1F600);
            string input = "line one" + grinningFace + "\nline two\tindented";
            Assert.AreEqual("line one\nline two\tindented",
                IconLoader.SanitizeForDisplay(input));
        }

        [Test]
        public void SanitizeForDisplay_ZwjSequence_FullyRemoved_NoOrphans()
        {
            string man = char.ConvertFromUtf32(0x1F468);
            string zwj = char.ConvertFromUtf32(0x200D);
            string woman = char.ConvertFromUtf32(0x1F469);
            string family = man + zwj + woman;
            string result = IconLoader.SanitizeForDisplay(family);
            Assert.AreEqual(string.Empty, result);
            Assert.IsFalse(ContainsOrdinal(result, (char)0x200D),
                "no orphaned ZWJ may survive");
        }

        [Test]
        public void SanitizeForDisplay_SkinToneModifier_NeverSurvivesAlone()
        {
            string wavingHand = char.ConvertFromUtf32(0x1F44B);
            string skinTone = char.ConvertFromUtf32(0x1F3FC); // medium-light skin tone
            Assert.AreEqual(string.Empty,
                IconLoader.SanitizeForDisplay(wavingHand + skinTone));
            // Even with no preceding base emoji, a modifier never survives
            // on its own -- it is dropped by the pictograph range itself.
            Assert.AreEqual(string.Empty, IconLoader.SanitizeForDisplay(skinTone));
        }

        [Test]
        public void SanitizeForDisplay_TrailingLoneHighSurrogate_IsHeldBack()
        {
            // A codepoint OUTSIDE the drop ranges (astral CJK Extension B
            // ideograph) so the test proves the surrogate is HELD, not
            // simply dropped because it would have been removed anyway.
            string cjkExtensionIdeograph = char.ConvertFromUtf32(0x20000);
            string highSurrogateOnly = cjkExtensionIdeograph.Substring(0, 1);

            string midStream = IconLoader.SanitizeForDisplay("loading" + highSurrogateOnly);
            Assert.AreEqual("loading", midStream);

            string completed = IconLoader.SanitizeForDisplay(
                "loading" + cjkExtensionIdeograph);
            Assert.AreEqual("loading" + cjkExtensionIdeograph, completed);
        }

        [Test]
        public void SanitizeForDisplay_IsIdempotent()
        {
            string sparkles = char.ConvertFromUtf32(0x2728);
            string grinningFace = char.ConvertFromUtf32(0x1F600);
            string zwjFamily = char.ConvertFromUtf32(0x1F468)
                + char.ConvertFromUtf32(0x200D) + char.ConvertFromUtf32(0x1F469);
            string input = "Status " + Warn + Fe0F + " done " + sparkles + " "
                + grinningFace + " " + zwjFamily + "!";
            string once = IconLoader.SanitizeForDisplay(input);
            string twice = IconLoader.SanitizeForDisplay(once);
            Assert.AreEqual(once, twice);
        }
        // -- UICODE-11: skin-qualified icon cache key ---------------------

        /// <summary>
        /// Find's resolution depends on the editor skin (candidate order),
        /// so the cache key must too -- a plain name key kept serving the
        /// OLD skin's texture after a live theme switch until the next
        /// domain reload. Pure key function pinned here; the texture side
        /// is editor-resource dependent by nature.
        /// </summary>
        [Test]
        public void IconCacheKey_SeparatesSkins_AndStaysStablePerSkin()
        {
            Assert.AreNotEqual(IconLoader.CacheKey("console.erroricon", true),
                IconLoader.CacheKey("console.erroricon", false),
                "dark and light must occupy separate cache slots");
            Assert.AreEqual(IconLoader.CacheKey("console.erroricon", true),
                IconLoader.CacheKey("console.erroricon", true));
            Assert.AreNotEqual(IconLoader.CacheKey("a", true), IconLoader.CacheKey("b", true),
                "different names must never collide within a skin");
        }

    }
}
