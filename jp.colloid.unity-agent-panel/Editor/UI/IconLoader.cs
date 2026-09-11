using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Built-in editor icon lookup with graceful fallback to text glyphs
    /// (ARCHITECTURE.md risk 15 / R05 section 5.5). Icon names change
    /// between Unity versions, so every lookup is cached (including
    /// misses) and callers always provide a fallback glyph.
    ///
    /// All glyph strings are built from code points at runtime so the
    /// source file stays strictly ASCII.
    /// </summary>
    public static class IconLoader
    {
        private static readonly Dictionary<string, Texture2D> Cache =
            new Dictionary<string, Texture2D>();

        private static Texture2D[] _spinnerFrames;

        // -- Glyphs (runtime-built, source stays ASCII) -----------------------
        //
        // EVERY text glyph the panel constructs lives here (or is plain
        // ASCII) so GlyphAuditTests has a single place to guard. Only
        // codepoints proven present in the editor's Inter SDF font may
        // appear (SafeGlyphCodepoints); emoji-plane characters (the old
        // paperclip U+1F4CE) and variation selectors (U+FE0F) rendered as
        // squares and spammed "not found in [Inter-Regular SDF]" console
        // warnings on user machines, so anything outside the whitelist
        // must become an EditorGUIUtility icon or pure ASCII.

        /// <summary>Claude spark: HEAVY ASTERISK (U+2731).</summary>
        public static readonly string GlyphSpark = char.ConvertFromUtf32(0x2731);
        /// <summary>Gemini mark: BLACK DIAMOND (U+25C6), the four-pointed sparkle's nearest safe glyph.</summary>
        public static readonly string GlyphAgentGemini = char.ConvertFromUtf32(0x25C6);
        /// <summary>Codex mark: LARGE CIRCLE (U+25EF), for OpenAI's ring.</summary>
        public static readonly string GlyphAgentCodex = char.ConvertFromUtf32(0x25EF);
        /// <summary>Custom ACP agent mark: WHITE DIAMOND (U+25C7), an unbranded outline.</summary>
        public static readonly string GlyphAgentCustom = char.ConvertFromUtf32(0x25C7);
        /// <summary>Check mark (U+2713).</summary>
        public static readonly string GlyphCheck = char.ConvertFromUtf32(0x2713);
        /// <summary>Multiplication X (U+2715): failure/denied marker.</summary>
        public static readonly string GlyphCross = char.ConvertFromUtf32(0x2715);
        /// <summary>Downwards arrow (U+2193).</summary>
        public static readonly string GlyphDownArrow = char.ConvertFromUtf32(0x2193);
        /// <summary>Left seven-eighths block used as streaming caret (U+258D).</summary>
        public static readonly string GlyphCursor = char.ConvertFromUtf32(0x258D);
        /// <summary>Gear (U+2699, WITHOUT the U+FE0F emoji selector).</summary>
        public static readonly string GlyphGear = char.ConvertFromUtf32(0x2699);
        /// <summary>Bullet (U+2022).</summary>
        public static readonly string GlyphBullet = char.ConvertFromUtf32(0x2022);
        /// <summary>White bullet (U+25E6): nested list marker.</summary>
        public static readonly string GlyphWhiteBullet = char.ConvertFromUtf32(0x25E6);
        /// <summary>Small right-pointing triangle (U+25B8): collapsed chevron.</summary>
        public static readonly string GlyphChevronRight = char.ConvertFromUtf32(0x25B8);
        /// <summary>Small down-pointing triangle (U+25BE): expanded chevron.</summary>
        public static readonly string GlyphChevronDown = char.ConvertFromUtf32(0x25BE);
        /// <summary>North east arrow (U+2197): open in a floating window.</summary>
        public static readonly string GlyphOpenWindow = char.ConvertFromUtf32(0x2197);
        /// <summary>Multiplication sign (U+00D7): chip remove button.</summary>
        public static readonly string GlyphX = char.ConvertFromUtf32(0x00D7);
        /// <summary>Warning sign (U+26A0, WITHOUT the U+FE0F emoji selector).</summary>
        public static readonly string GlyphWarn = char.ConvertFromUtf32(0x26A0);
        /// <summary>Identical-to (U+2261): file tool fallback.</summary>
        public static readonly string GlyphFile = char.ConvertFromUtf32(0x2261);
        /// <summary>Bullseye (U+25CE): search tool fallback.</summary>
        public static readonly string GlyphSearch = char.ConvertFromUtf32(0x25CE);
        /// <summary>Right guillemet (U+00BB): subagent/task fallback.</summary>
        public static readonly string GlyphTask = char.ConvertFromUtf32(0x00BB);
        /// <summary>
        /// Division slash (U+2215): visible stand-in for '/' on GenericMenu
        /// labels, where a real '/' opens a submenu. Part of the standard
        /// Latin symbol set (Mac OS Roman / Adobe Latin-3) that Inter
        /// ships, so it draws in the editor font; on Windows and macOS the
        /// menu is native and uses the OS menu font anyway.
        /// </summary>
        public static readonly string GlyphMenuSlash = char.ConvertFromUtf32(0x2215);

        /// <summary>
        /// Attachment marker: the old paperclip (U+1F4CE) is OUTSIDE the
        /// editor font coverage (rendered as a square + console warning
        /// per draw), so attachments use a built-in icon with this pure
        /// ASCII text fallback.
        /// </summary>
        public const string GlyphAttachAscii = "[@]";
        /// <summary>Built-in icon name for attachment chips (2022.3).</summary>
        public const string IconNameAttachment = "d_Linked";

        /// <summary>
        /// Codepoints allowed in runtime-constructed UI glyph strings, in
        /// addition to printable ASCII. Everything listed is BMP,
        /// text-presentation-default and confirmed rendering in the
        /// 2022.3 editor Inter SDF (live user session showed warnings
        /// ONLY for U+1F4CE and U+FE0F while these glyphs drew fine).
        /// U+00A0 is included for the inline-code no-wrap assembly in
        /// InlineMarkupConverter.
        /// </summary>
        public static readonly int[] SafeGlyphCodepoints =
        {
            0x00A0, // NO-BREAK SPACE (inline-code no-wrap assembly)
            0x00BB, // RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK
            0x00D7, // MULTIPLICATION SIGN
            0x2022, // BULLET
            0x2193, // DOWNWARDS ARROW
            0x2197, // NORTH EAST ARROW
            0x2215, // DIVISION SLASH (GenericMenu-safe '/')
            0x2261, // IDENTICAL TO
            0x25B8, // BLACK RIGHT-POINTING SMALL TRIANGLE
            0x25BE, // BLACK DOWN-POINTING SMALL TRIANGLE
            0x25CE, // BULLSEYE
            0x25E6, // WHITE BULLET
            0x258D, // LEFT SEVEN EIGHTHS BLOCK (streaming caret)
            0x2699, // GEAR (text presentation)
            0x26A0, // WARNING SIGN (text presentation)
            0x2713, // CHECK MARK
            0x2715, // MULTIPLICATION X
            0x2731, // HEAVY ASTERISK (Claude spark)
            // Agent marks (design note 2026-09-10-agent-name-in-ui.md
            // section 4): all three are in Inter 3.19's own cmap (checked
            // against Inter-Regular.otf on 2026-09-10), so they need no
            // fallback font at all, unlike several entries above.
            0x25C6, // BLACK DIAMOND (Gemini)
            0x25C7, // WHITE DIAMOND (custom ACP agent)
            0x25EF  // LARGE CIRCLE (Codex)
        };

        // -- Per-agent mark and accent (design note 2026-09-10-agent-name-in-ui.md section 4) --

        /// <summary>Root-element class names that pick the agent accent colour in ThemeDark/ThemeLight.uss.</summary>
        public static readonly string[] AgentAccentClasses =
        {
            "uap-agent--claude", "uap-agent--gemini", "uap-agent--codex", "uap-agent--grok", "uap-agent--custom"
        };

        /// <summary>The spark-style mark shown next to the agent's name for a backend (header, empty state, role rows).</summary>
        public static string AgentGlyph(Colloid.AgentPanel.Core.Acp.AgentBackend backend)
        {
            switch (backend)
            {
                case Colloid.AgentPanel.Core.Acp.AgentBackend.GeminiCli:
                    return GlyphAgentGemini;
                case Colloid.AgentPanel.Core.Acp.AgentBackend.CodexAcp:
                    return GlyphAgentCodex;
                case Colloid.AgentPanel.Core.Acp.AgentBackend.GrokBuild:
                    // xAI's mark is an X; the accent colour keeps it apart
                    // from the red failure marker that shares the glyph.
                    return GlyphCross;
                case Colloid.AgentPanel.Core.Acp.AgentBackend.AcpCustom:
                    return GlyphAgentCustom;
                default:
                    return GlyphSpark;
            }
        }

        /// <summary>The root class that selects the backend's accent colour (one of <see cref="AgentAccentClasses"/>).</summary>
        public static string AgentAccentClass(Colloid.AgentPanel.Core.Acp.AgentBackend backend)
        {
            switch (backend)
            {
                case Colloid.AgentPanel.Core.Acp.AgentBackend.GeminiCli:
                    return "uap-agent--gemini";
                case Colloid.AgentPanel.Core.Acp.AgentBackend.CodexAcp:
                    return "uap-agent--codex";
                case Colloid.AgentPanel.Core.Acp.AgentBackend.GrokBuild:
                    return "uap-agent--grok";
                case Colloid.AgentPanel.Core.Acp.AgentBackend.AcpCustom:
                    return "uap-agent--custom";
                default:
                    return "uap-agent--claude";
            }
        }

        /// <summary>Mark for the backend the settings currently select.</summary>
        public static string CurrentAgentGlyph
        {
            get { return AgentGlyph(Colloid.AgentPanel.Integration.AgentHub.CurrentBackend); }
        }

        /// <summary>
        /// Puts exactly one agent accent class on <paramref name="root"/>
        /// for the selected backend (idempotent; cheap enough to call on
        /// every hub refresh so a backend switch recolours at once).
        /// </summary>
        public static void ApplyAgentAccent(VisualElement root)
        {
            if (root == null)
            {
                return;
            }
            string wanted = AgentAccentClass(Colloid.AgentPanel.Integration.AgentHub.CurrentBackend);
            if (root.ClassListContains(wanted))
            {
                return;
            }
            for (int i = 0; i < AgentAccentClasses.Length; i++)
            {
                root.RemoveFromClassList(AgentAccentClasses[i]);
            }
            root.AddToClassList(wanted);
        }

        /// <summary>True when a codepoint may appear in a UI glyph string.</summary>
        public static bool IsSafeGlyphCodepoint(int codepoint)
        {
            if (codepoint >= 0x20 && codepoint < 0x7F)
            {
                return true; // Printable ASCII.
            }
            for (int i = 0; i < SafeGlyphCodepoints.Length; i++)
            {
                if (SafeGlyphCodepoints[i] == codepoint)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes emoji/text variation selectors (U+FE0F / U+FE0E) from
        /// display text. Model output routinely appends U+FE0F to symbols
        /// ("warning sign" + selector); the editor fonts have no glyph
        /// for it, so every draw logged "U+FE0F not found in
        /// [Inter-Regular SDF]" and rendered a square. Stripping is
        /// display-only and never changes visible content. Returns the
        /// SAME instance when nothing needs removing (fast path).
        /// </summary>
        public static string StripVariationSelectors(string text)
        {
            const char emojiSelector = (char)0xFE0F; // VARIATION SELECTOR-16
            const char textSelector = (char)0xFE0E;  // VARIATION SELECTOR-15
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }
            int first = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == emojiSelector || c == textSelector)
                {
                    first = i;
                    break;
                }
            }
            if (first < 0)
            {
                return text;
            }
            var sb = new System.Text.StringBuilder(text.Length - 1);
            sb.Append(text, 0, first);
            for (int i = first + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c != emojiSelector && c != textSelector)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // -- Broader display sanitizer -----------------------------------------------
        //
        // StripVariationSelectors above only removes U+FE0F/U+FE0E; it does
        // NOT catch the wider class of emoji codepoints (U+2705 "white
        // heavy check mark", U+274C "cross mark", U+1F600-family
        // pictographs, ...) that the editor's Inter SDF body font has no
        // glyph for either -- confirmed empirically (Phase probe P2b) that
        // Unity's TextGenerator logs a "not found" console warning once
        // PER MISSING-CHARACTER OCCURRENCE on every GenerateText call with
        // zero caching, so a bad glyph visible on screen re-emits its
        // warning on every redraw for as long as it stays on screen -- and
        // the two live-poll chokepoints (StreamingLabelPump at ~80ms,
        // SubagentCard's progress line at 500ms) turn that into a console
        // flood for the whole time a message streams.
        //
        // SanitizeForDisplay is the single broader seam every render
        // chokepoint for model/user text now calls instead (or in
        // addition to, for callers that still use StripVariationSelectors
        // directly for an unrelated reason).

        private const char ZeroWidthJoiner = (char)0x200D;

        /// <summary>
        /// Curated codepoint replacements applied by <see
        /// cref="SanitizeForDisplay"/> before the broader drop-or-keep
        /// pass, so a handful of very common model emoji keep their
        /// MEANING instead of silently vanishing. Every replacement value
        /// is itself already a codepoint in <see cref="SafeGlyphCodepoints"/>
        /// or plain ASCII, so this table can never introduce an unverified
        /// glyph. Deliberately kept SHORT: add an entry only for a
        /// genuinely common model emoji with an obvious, defensible text
        /// equivalent -- this is not meant to become a general emoji-to-
        /// text transliteration table.
        /// </summary>
        private static readonly Dictionary<int, char> CuratedEmojiMap = new Dictionary<int, char>
        {
            { 0x2705, '\u2713' }, // WHITE HEAVY CHECK MARK -> CHECK MARK
            { 0x2714, '\u2713' }, // HEAVY CHECK MARK -> CHECK MARK
            { 0x274C, '\u2715' }, // CROSS MARK -> MULTIPLICATION X
            { 0x274E, '\u2715' }, // NEGATIVE SQUARED CROSS MARK -> MULTIPLICATION X
            { 0x2757, '!' },      // HEAVY EXCLAMATION MARK -> ASCII "!"
            { 0x2755, '!' },      // WHITE EXCLAMATION MARK ORNAMENT -> ASCII "!"
            { 0x2753, '?' },      // BLACK QUESTION MARK ORNAMENT -> ASCII "?"
            { 0x2754, '?' },      // WHITE QUESTION MARK ORNAMENT -> ASCII "?"
            { 0x2728, '\u2731' }, // SPARKLES -> HEAVY ASTERISK (existing Claude spark glyph)
            { 0x1F449, '\u25B8' }, // WHITE RIGHT POINTING BACKHAND INDEX -> right chevron
            { 0x1F447, '\u25BE' }, // WHITE DOWN POINTING BACKHAND INDEX -> down chevron
            { 0x27A1, '\u25B8' },  // BLACK RIGHTWARDS ARROW -> right chevron
        };

        /// <summary>
        /// Lower bound (inclusive) of the Misc Symbols / Dingbats block
        /// (U+2600-U+27BF) dropped by <see cref="SanitizeForDisplay"/> when
        /// a codepoint in it is neither curated above nor already in <see
        /// cref="SafeGlyphCodepoints"/> (e.g. U+26A0 WARNING SIGN, which
        /// stays -- it is already whitelisted and renders fine).
        /// </summary>
        private const int DingbatsRangeStart = 0x2600;
        private const int DingbatsRangeEnd = 0x27BF;

        /// <summary>
        /// Lower/upper bound (inclusive) of the emoji/pictograph astral
        /// blocks dropped by <see cref="SanitizeForDisplay"/>: Mahjong
        /// Tiles, Domino Tiles, Playing Cards, Enclosed Alphanumeric
        /// Supplement (this is where the Regional Indicator Symbols
        /// U+1F1E6-U+1F1FF live -- every two-letter country-flag emoji is
        /// a pair of codepoints from this sub-block), Enclosed Ideographic
        /// Supplement, Emoticons, Transport, Supplemental Symbols and
        /// Pictographs, Symbols and Pictographs Extended-A, .... Skin-tone
        /// modifiers (U+1F3FB-U+1F3FF) fall inside this range too, so a
        /// modifier can never survive on its own once the base emoji it
        /// followed is dropped -- and it is dropped on its own merits even
        /// when, by construction bug elsewhere, it is not adjacent to one.
        /// </summary>
        private const int PictographRangeStart = 0x1F000;
        private const int PictographRangeEnd = 0x1FAFF;

        /// <summary>
        /// Broader successor to <see cref="StripVariationSelectors"/> used
        /// at every render chokepoint for model/user-controlled text:
        /// variation selectors (U+FE0F/U+FE0E) and zero-width joiners are
        /// stripped as before; a short CURATED table
        /// (<see cref="CuratedEmojiMap"/>) maps a handful of very common
        /// model emoji onto glyphs already proven in the editor's Inter
        /// SDF font so their meaning survives; and any remaining codepoint
        /// the font is known not to cover is dropped BY RANGE rather than
        /// queried live against the font: this package has no reference
        /// anywhere to the actual FontAsset instance backing UI Toolkit's
        /// default body-text font (FontLoader only resolves the SEPARATE
        /// mono and Japanese-UI fonts used for code blocks / CJK text), so
        /// there is no live "HasCharacter"-style coverage API reachable
        /// here -- the range-drop fallback is the one shipped, and this
        /// doc comment (plus GlyphAuditTests' SanitizeForDisplay_* cases)
        /// is the authoritative record of which ranges are dropped and why.
        ///
        /// Dropped ranges: U+1F000-U+1FAFF (every emoji/pictograph block,
        /// including Regional Indicator Symbols / flag emoji and skin-tone
        /// modifiers) and U+2600-U+27BF (Misc Symbols/Dingbats) MINUS
        /// whatever <see cref="CuratedEmojiMap"/> or
        /// <see cref="SafeGlyphCodepoints"/> already claims (e.g. U+26A0
        /// WARNING SIGN stays untouched). Everything else -- CJK, Japanese
        /// kana, accented Latin, box-drawing, general punctuation, even
        /// astral CJK Extension ideographs -- passes through completely
        /// unchanged: only the known-bad ranges are ever removed, so
        /// Japanese output is never mangled.
        ///
        /// A trailing LONE high surrogate (no paired low surrogate at the
        /// very end of the input) is held back -- dropped from THIS pass'
        /// output only, never emitted broken or mis-decoded -- because a
        /// streaming delta can split a 4-byte emoji exactly at that
        /// boundary; every call site that streams (StreamingLabelPump) always
        /// re-supplies the FULL accumulated text on the next flush, at
        /// which point the pair is whole again and resolves normally.
        ///
        /// Returns the SAME instance when the input is pure ASCII (fast
        /// path; the overwhelming majority of calls on the
        /// StreamingLabelPump / SubagentCard flood chokepoints).
        /// </summary>
        public static string SanitizeForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            int length = text.Length;
            bool needsWork = false;
            for (int i = 0; i < length; i++)
            {
                if (text[i] > 0x7E)
                {
                    needsWork = true;
                    break;
                }
            }
            if (!needsWork)
            {
                return text;
            }

            var sb = new System.Text.StringBuilder(length);
            int j = 0;
            while (j < length)
            {
                char c = text[j];

                if (c == (char)0xFE0F || c == (char)0xFE0E || c == ZeroWidthJoiner)
                {
                    j++;
                    continue;
                }

                if (char.IsHighSurrogate(c))
                {
                    if (j + 1 < length && char.IsLowSurrogate(text[j + 1]))
                    {
                        int codepoint = char.ConvertToUtf32(c, text[j + 1]);
                        AppendAstralCodepoint(sb, codepoint, c, text[j + 1]);
                        j += 2;
                        continue;
                    }
                    if (j + 1 == length)
                    {
                        // Trailing lone high surrogate: held back, see the
                        // doc comment above. Nothing follows it either way.
                        break;
                    }
                    // Malformed mid-string (no low surrogate follows): keep
                    // verbatim rather than guess at intent.
                    sb.Append(c);
                    j++;
                    continue;
                }
                if (char.IsLowSurrogate(c))
                {
                    // Lone low surrogate with no preceding high surrogate:
                    // keep verbatim (malformed input this panel never
                    // manufactures itself).
                    sb.Append(c);
                    j++;
                    continue;
                }

                AppendBmpCodepoint(sb, c);
                j++;
            }
            return sb.ToString();
        }

        private static void AppendBmpCodepoint(System.Text.StringBuilder sb, char c)
        {
            if (c < 0x20)
            {
                sb.Append(c); // Control chars (newline/tab/CR/...): never touched.
                return;
            }
            char mapped;
            if (CuratedEmojiMap.TryGetValue(c, out mapped))
            {
                sb.Append(mapped);
                return;
            }
            if (IsSafeGlyphCodepoint(c))
            {
                sb.Append(c);
                return;
            }
            if (c >= DingbatsRangeStart && c <= DingbatsRangeEnd)
            {
                return; // Misc Symbols/Dingbats, not curated or whitelisted: drop.
            }
            sb.Append(c); // Everything else (CJK, accents, box-drawing, ...): keep.
        }

        private static void AppendAstralCodepoint(System.Text.StringBuilder sb, int codepoint,
            char high, char low)
        {
            char mapped;
            if (CuratedEmojiMap.TryGetValue(codepoint, out mapped))
            {
                sb.Append(mapped);
                return;
            }
            if (codepoint >= PictographRangeStart && codepoint <= PictographRangeEnd)
            {
                return; // Modern emoji/pictograph blocks (incl. skin-tone modifiers): drop.
            }
            sb.Append(high).Append(low); // Astral but not a known emoji block (e.g. CJK ext.): keep.
        }

        // -- Texture lookup ------------------------------------------------------

        /// <summary>
        /// Resolves a built-in icon texture by name, or null. Pass the
        /// dark-skin name (d_ prefix); the light-skin variant is derived
        /// automatically. Results (including misses) are cached.
        /// </summary>
        public static Texture2D Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            // UICODE-11: the resolution DEPENDS on the current editor skin
            // (candidate order below), so the cache key must too -- a plain
            // name key kept serving the old skin's texture after a live
            // theme switch until the next domain reload. Dark and light get
            // separate slots; a switch simply resolves into the other slot
            // on the next Find, no manual invalidation needed.
            bool isProSkin = EditorGUIUtility.isProSkin;
            string cacheKey = CacheKey(name, isProSkin);
            Texture2D cached;
            if (Cache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            Texture2D texture = null;
            bool hasDarkPrefix = name.StartsWith("d_", System.StringComparison.Ordinal);
            string plain = hasDarkPrefix ? name.Substring(2) : name;
            string[] candidates = isProSkin
                ? new[] { hasDarkPrefix ? name : "d_" + name, plain }
                : new[] { plain, hasDarkPrefix ? name : "d_" + name };

            for (int i = 0; i < candidates.Length && texture == null; i++)
            {
                texture = LoadOne(candidates[i]);
            }
            Cache[cacheKey] = texture;
            return texture;
        }

        /// <summary>UICODE-11: skin-qualified cache key, pure for tests.</summary>
        internal static string CacheKey(string name, bool isProSkin)
        {
            return (isProSkin ? "d|" : "l|") + name;
        }

        /// <summary>
        /// Creates an icon element: an Image when the built-in texture
        /// exists, otherwise a Label with the fallback glyph. The class is
        /// applied either way (glyph color/size comes from USS).
        /// </summary>
        public static VisualElement CreateIcon(string iconName, string fallbackGlyph,
            string ussClass, string glyphUssClass = null)
        {
            Texture2D texture = Find(iconName);
            if (texture != null)
            {
                var image = new Image { image = texture, scaleMode = ScaleMode.ScaleToFit };
                if (!string.IsNullOrEmpty(ussClass))
                {
                    image.AddToClassList(ussClass);
                }
                return image;
            }
            var label = new Label(fallbackGlyph ?? string.Empty);
            if (!string.IsNullOrEmpty(glyphUssClass))
            {
                label.AddToClassList(glyphUssClass);
            }
            else if (!string.IsNullOrEmpty(ussClass))
            {
                label.AddToClassList(ussClass);
            }
            return label;
        }

        /// <summary>
        /// The WaitSpin00..11 frame textures, or an empty array when the
        /// icons are unavailable in this editor version (callers fall back
        /// to an ASCII text spinner).
        /// </summary>
        public static Texture2D[] SpinnerFrames
        {
            get
            {
                if (_spinnerFrames == null)
                {
                    var frames = new List<Texture2D>();
                    for (int i = 0; i < 12; i++)
                    {
                        Texture2D frame = Find("d_WaitSpin" + i.ToString("00"));
                        if (frame == null)
                        {
                            frames.Clear();
                            break;
                        }
                        frames.Add(frame);
                    }
                    _spinnerFrames = frames.ToArray();
                }
                return _spinnerFrames;
            }
        }

        private static Texture2D LoadOne(string name)
        {
            try
            {
                // NEVER call EditorGUIUtility.IconContent here with an
                // unverified name: in 2022.3 it logs
                // "Unable to load the icon: '<name>'" to the Console for
                // unknown names (it does not throw), so a try/catch fallback
                // still spams the Console. EditorGUIUtility.FindTexture is
                // the silent probe -- it returns null without logging for
                // missing icons and resolves the same built-in icon-bundle
                // textures, so we use it exclusively and skip IconContent.
                Texture2D found = EditorGUIUtility.FindTexture(name);
                if (found != null)
                {
                    return found;
                }
                // 2026-09-06 settings review, measured on 2022.3.22f1:
                // FindTexture resolves "d_UnityEditor.GameView"-style names
                // but NOT the "<Type> Icon" family ("d_AudioSource Icon",
                // "d_Font Icon", "d_TextAsset Icon" ...), which is why five
                // Settings section icons had been silently rendering their
                // text fallback. EditorGUIUtility.Load resolves both
                // families without logging, so it is the second attempt.
                return EditorGUIUtility.Load(name) as Texture2D;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
