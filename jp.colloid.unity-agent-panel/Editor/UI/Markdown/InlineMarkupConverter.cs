using System.Text;

namespace Colloid.AgentPanel.UI.Markdown
{
    /// <summary>
    /// THE single escape chokepoint (ARCHITECTURE.md D7 / risk 8). Every
    /// string that ends up in a Label with enableRichText = true MUST pass
    /// through Convert (or Escape for tag-free text). The contract:
    ///
    ///   1. Escape() runs FIRST and neutralizes every raw '&lt;' by
    ///      bracketing it in noparse (NeutralizedLt), so no model-authored
    ///      character can ever open a rich-text tag. '&gt;' and '&amp;'
    ///      stay raw: the 2022.3 TextCore parser only reacts to '&lt;'
    ///      (verified against UnityCsReference 2022.3 TextGenerator.cs --
    ///      it performs NO HTML-entity decoding, so &amp;lt;-style
    ///      entities would render as literal entity text; ValidateHtmlTag
    ///      aborts on a second '&lt;' and, while noparse is active,
    ///      rejects every tag except the closer, so NeutralizedLt renders
    ///      as exactly one literal '&lt;').
    ///   2. Only then does the scanner add Unity rich-text tags for the
    ///      supported inline markdown subset: **bold**, *italic*,
    ///      `inline code`, [text](url), bare http(s) URLs and
    ///      Assets/... | Packages/... paths (the latter only when
    ///      AssetLinkHub.PathExists confirms the asset -- null-safe,
    ///      default false).
    ///
    /// Tag payloads are constructed exclusively from already-escaped text,
    /// and URL/path tokens additionally exclude '"' '&lt;' '&gt;' so a
    /// payload can never break out of a &lt;link="..."&gt; attribute
    /// ('"' would close the attribute, '&gt;' would end the tag early --
    /// TextCore ends a tag at '&gt;' even inside a quoted value -- and
    /// '&lt;' would abort tag validation). Link ids for URLs/paths are
    /// therefore raw text; click handlers still run them through Unescape
    /// so a NeutralizedLt marker can never leak into a URL or asset path.
    ///
    /// Pure string -&gt; string, no UnityEngine dependency (unit-testable).
    /// </summary>
    public static class InlineMarkupConverter
    {
        /// <summary>Dark-theme link blue (docs/research/05-ux-spec.md 5.2).</summary>
        public const string DefaultLinkColor = "#4C7EFF";
        /// <summary>Dark-theme inline-code text tint.</summary>
        public const string DefaultCodeColor = "#D8B4A0";
        /// <summary>Dark-theme inline-code highlight (RRGGBBAA).</summary>
        public const string DefaultCodeMark = "#00000060";

        private static string _linkColor = DefaultLinkColor;
        private static string _codeColor = DefaultCodeColor;
        private static string _codeMark = DefaultCodeMark;

        /// <summary>Current link color hex (rich-text literal).</summary>
        public static string LinkColor
        {
            get { return _linkColor; }
        }

        /// <summary>
        /// Sets the color literals baked into emitted tags (rich text
        /// cannot reference USS variables). Null/empty keeps the previous
        /// value. Values must be trusted literals ("#RRGGBB[AA]"), never
        /// model output.
        /// </summary>
        public static void ConfigureColors(string linkHex, string codeHex, string codeMarkHex)
        {
            if (!string.IsNullOrEmpty(linkHex))
            {
                _linkColor = linkHex;
            }
            if (!string.IsNullOrEmpty(codeHex))
            {
                _codeColor = codeHex;
            }
            if (!string.IsNullOrEmpty(codeMarkHex))
            {
                _codeMark = codeMarkHex;
            }
        }

        // -- Escaping -----------------------------------------------------------

        /// <summary>
        /// Neutralized literal '&lt;': renders as exactly one '&lt;'
        /// character and can never open a tag. The noparse pair is closed
        /// within the constant itself, so model text can neither extend
        /// nor prematurely terminate the region.
        /// </summary>
        public const string NeutralizedLt = "<noparse><</noparse>";

        /// <summary>
        /// Neutralizes the one rich-text metacharacter. Only '&lt;' can
        /// open a tag in the 2022.3 TextCore parser; '&gt;' and '&amp;'
        /// are inert literals and MUST stay raw because the parser does
        /// not decode HTML entities (an "&amp;lt;" would display as the
        /// raw entity text).
        /// </summary>
        public static string Escape(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }
            // IconLoader.SanitizeForDisplay runs FIRST (before the '<'
            // scan below): it strips variation selectors/ZWJ, curated-maps
            // a handful of common emoji onto font-safe glyphs, and drops
            // any remaining codepoint the editor fonts have no glyph for
            // (see IconLoader.SanitizeForDisplay's doc comment for the
            // dropped ranges and rationale).
            // This is the finalized-markdown render's ONLY sanitize pass
            // (MessageBlockFactory's streaming path uses StreamingLabelPump
            // instead), so every markdown-rendered block -- paragraphs,
            // headings, list items, table cells, subagent summaries --
            // goes through it here.
            string sanitized = IconLoader.SanitizeForDisplay(raw);
            var sb = new StringBuilder(sanitized.Length + 32);
            for (int i = 0; i < sanitized.Length; i++)
            {
                char c = sanitized[i];
                if (c == '<')
                {
                    sb.Append(NeutralizedLt);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reverses Escape (for link ids handed back by click events).
        /// URL/path tokens exclude '&lt;' so ids are normally raw already;
        /// this stays the single inverse of Escape for defense in depth.
        /// </summary>
        public static string Unescape(string escaped)
        {
            if (string.IsNullOrEmpty(escaped))
            {
                return string.Empty;
            }
            return escaped.Replace(NeutralizedLt, "<");
        }

        // -- Conversion ------------------------------------------------------------

        /// <summary>
        /// Escapes the raw text, then converts the inline markdown subset
        /// to Unity rich-text tags. Output is safe for a rich-text Label.
        /// </summary>
        public static string Convert(string raw)
        {
            string escaped = Escape(raw);
            var sb = new StringBuilder(escaped.Length + 32);
            AppendConverted(sb, escaped, 0, escaped.Length, true);
            return sb.ToString();
        }

        /// <summary>
        /// Scanner over ALREADY-ESCAPED text. All indices below address the
        /// escaped string; every literal char is appended verbatim (it is
        /// already entity-safe).
        /// </summary>
        private static void AppendConverted(StringBuilder sb, string s, int start,
            int end, bool allowLinks)
        {
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '`')
                {
                    int close = IndexOnSameLine(s, i + 1, end, '`');
                    if (close > i + 1)
                    {
                        // NO <mark> chip here, deliberately: with a
                        // DynamicOS FontAsset (the CJK body font), TextCore
                        // draws the mark quad above SOME glyphs and below
                        // others depending on their atlas page -- covered
                        // glyphs look washed out, uncovered ones look
                        // bolder ("patchy bold" / faint inline code, live
                        // feedback 2026-07-31; opaque marks hide the text
                        // entirely). Verified with an on-editor probe
                        // (docs/verify/UAPFontProbe_*.png): color-only
                        // renders uniformly. Inline code is therefore
                        // color-only.
                        sb.Append("<color=").Append(_codeColor).Append('>');
                        sb.Append(NoWrapShortCodeSpan(
                            s.Substring(i + 1, close - i - 1)));
                        sb.Append("</color>");
                        i = close + 1;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '*')
                {
                    if (i + 1 < end && s[i + 1] == '*')
                    {
                        int close = FindDoubleStar(s, i + 2, end);
                        if (close > i + 2)
                        {
                            sb.Append("<b>");
                            AppendConverted(sb, s, i + 2, close, allowLinks);
                            sb.Append("</b>");
                            i = close + 2;
                            continue;
                        }
                    }
                    else
                    {
                        int close = IndexOnSameLine(s, i + 1, end, '*');
                        if (close > i + 1
                            && !char.IsWhiteSpace(s[i + 1])
                            && !char.IsWhiteSpace(s[close - 1]))
                        {
                            sb.Append("<i>");
                            AppendConverted(sb, s, i + 1, close, allowLinks);
                            sb.Append("</i>");
                            i = close + 1;
                            continue;
                        }
                    }
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '[' && allowLinks)
                {
                    int consumed = TryAppendMarkdownLink(sb, s, i, end);
                    if (consumed > 0)
                    {
                        i += consumed;
                        continue;
                    }
                }
                if ((c == 'h' || c == 'H') && allowLinks && IsWordBoundary(s, start, i))
                {
                    int consumed = TryAppendBareUrl(sb, s, i, end);
                    if (consumed > 0)
                    {
                        i += consumed;
                        continue;
                    }
                }
                if ((c == 'A' || c == 'P') && IsWordBoundary(s, start, i))
                {
                    int consumed = TryAppendAssetPath(sb, s, i, end, allowLinks);
                    if (consumed > 0)
                    {
                        i += consumed;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
        }

        /// <summary>
        /// Visible-character cap under which an inline-code span is kept
        /// on one line (see NoWrapShortCodeSpan).
        /// </summary>
        public const int ShortCodeNoWrapMaxChars = 24;

        /// <summary>
        /// TextCore draws a &lt;mark&gt; as one rect per line fragment, so
        /// a code chip that wraps mid-span breaks into untidy partial
        /// highlights. Chosen fix: SHORT spans (at most
        /// ShortCodeNoWrapMaxChars visible chars) get their internal
        /// spaces replaced with NO-BREAK SPACE (U+00A0 -- present in the
        /// editor fonts, unlike zero-width joiners), so the whole chip
        /// wraps as one word onto the next line. Long spans keep normal
        /// wrapping: forcing them onto one line would overflow the panel,
        /// which is worse than a split highlight.
        /// Input/output is ALREADY-ESCAPED text (a NeutralizedLt marker
        /// counts as one visible char and contains no spaces).
        /// </summary>
        private static string NoWrapShortCodeSpan(string escapedSpan)
        {
            const char noBreakSpace = (char)0x00A0;
            if (escapedSpan.IndexOf(' ') < 0)
            {
                return escapedSpan;
            }
            int visibleLength = escapedSpan
                .Replace(NeutralizedLt, "<").Length;
            if (visibleLength > ShortCodeNoWrapMaxChars)
            {
                return escapedSpan;
            }
            return escapedSpan.Replace(' ', noBreakSpace);
        }

        // -- Link constructs ----------------------------------------------------------

        /// <summary>
        /// [text](url). The url must be an http(s) URL or an existing
        /// Assets/Packages path; otherwise the whole construct stays
        /// literal text. Returns consumed char count (0 = no match).
        /// </summary>
        private static int TryAppendMarkdownLink(StringBuilder sb, string s, int i, int end)
        {
            int closeBracket = IndexOnSameLine(s, i + 1, end, ']');
            if (closeBracket <= i + 1 || closeBracket + 1 >= end || s[closeBracket + 1] != '(')
            {
                return 0;
            }
            int closeParen = IndexOnSameLine(s, closeBracket + 2, end, ')');
            if (closeParen <= closeBracket + 2)
            {
                return 0;
            }
            string url = s.Substring(closeBracket + 2, closeParen - closeBracket - 2);
            // '"' closes the link attribute, '>' ends the tag early (even
            // inside quotes), '<' aborts tag validation -- none may appear
            // in an attribute payload. A '<' here means the raw url
            // contained one (it arrives as a NeutralizedLt marker).
            if (url.IndexOf('"') >= 0 || url.IndexOf('<') >= 0
                || url.IndexOf('>') >= 0 || ContainsWhitespace(url))
            {
                return 0;
            }
            string rawUrl = Unescape(url);
            bool isHttp = IsHttpUrl(rawUrl);
            bool isAsset = !isHttp && IsExistingAssetPath(rawUrl);
            if (!isHttp && !isAsset)
            {
                return 0;
            }
            sb.Append("<link=\"").Append(url).Append("\"><color=")
              .Append(_linkColor).Append('>');
            AppendConverted(sb, s, i + 1, closeBracket, false);
            sb.Append("</color></link>");
            return closeParen + 1 - i;
        }

        /// <summary>Bare http(s)://... URL; trailing punctuation stays outside.</summary>
        private static int TryAppendBareUrl(StringBuilder sb, string s, int i, int end)
        {
            int schemeLen = MatchScheme(s, i, end);
            if (schemeLen == 0)
            {
                return 0;
            }
            int j = i + schemeLen;
            // Stop at '"' '<' '>' as well: the token becomes a quoted
            // <link> attribute and must stay tag-safe ('<' additionally
            // marks a NeutralizedLt run, which must never join a URL).
            while (j < end && !char.IsWhiteSpace(s[j])
                && s[j] != '"' && s[j] != '<' && s[j] != '>')
            {
                j++;
            }
            j = TrimTrailingPunctuation(s, i + schemeLen, j);
            if (j <= i + schemeLen)
            {
                return 0;
            }
            string url = s.Substring(i, j - i);
            sb.Append("<link=\"").Append(url).Append("\"><color=")
              .Append(_linkColor).Append('>').Append(url).Append("</color></link>");
            return j - i;
        }

        /// <summary>
        /// Assets/... or Packages/... token. Linkified only when the
        /// context side confirmed the path exists; otherwise the token is
        /// emitted as-is (and skipped, so it is never rescanned).
        /// </summary>
        private static int TryAppendAssetPath(StringBuilder sb, string s, int i, int end,
            bool allowLinks)
        {
            int prefixLen = MatchAssetPrefix(s, i, end);
            if (prefixLen == 0)
            {
                return 0;
            }
            int j = i + prefixLen;
            while (j < end && IsPathChar(s[j]))
            {
                j++;
            }
            j = TrimTrailingPunctuation(s, i + prefixLen, j);
            string token = s.Substring(i, j - i);
            string rawPath = Unescape(token);
            if (allowLinks && IsExistingAssetPath(rawPath))
            {
                sb.Append("<link=\"").Append(token).Append("\"><color=")
                  .Append(_linkColor).Append('>').Append(token).Append("</color></link>");
            }
            else
            {
                sb.Append(token);
            }
            return j - i;
        }

        // -- Classification helpers ------------------------------------------------------

        /// <summary>True for raw (unescaped) http:// or https:// URLs.</summary>
        public static bool IsHttpUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            return raw.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for raw project-relative paths ("Assets/..." | "Packages/...").</summary>
        public static bool IsAssetPath(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            return raw.StartsWith("Assets/", System.StringComparison.Ordinal)
                || raw.StartsWith("Packages/", System.StringComparison.Ordinal);
        }

        private static bool IsExistingAssetPath(string raw)
        {
            if (!IsAssetPath(raw))
            {
                return false;
            }
            System.Func<string, bool> probe = AssetLinkHub.PathExists;
            if (probe == null)
            {
                return false;
            }
            try
            {
                return probe(raw);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // -- Low-level scanning helpers --------------------------------------------------------

        private static int MatchScheme(string s, int i, int end)
        {
            const string http = "http://";
            const string https = "https://";
            if (RegionEqualsIgnoreCase(s, i, end, https))
            {
                return https.Length;
            }
            if (RegionEqualsIgnoreCase(s, i, end, http))
            {
                return http.Length;
            }
            return 0;
        }

        private static int MatchAssetPrefix(string s, int i, int end)
        {
            const string assets = "Assets/";
            const string packages = "Packages/";
            if (RegionEquals(s, i, end, assets))
            {
                return assets.Length;
            }
            if (RegionEquals(s, i, end, packages))
            {
                return packages.Length;
            }
            return 0;
        }

        private static bool RegionEquals(string s, int i, int end, string prefix)
        {
            if (i + prefix.Length > end)
            {
                return false;
            }
            return string.CompareOrdinal(s, i, prefix, 0, prefix.Length) == 0;
        }

        private static bool RegionEqualsIgnoreCase(string s, int i, int end, string prefix)
        {
            if (i + prefix.Length > end)
            {
                return false;
            }
            return string.Compare(s, i, prefix, 0, prefix.Length,
                System.StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>Index of ch between from..end on the same line, or -1.</summary>
        private static int IndexOnSameLine(string s, int from, int end, char ch)
        {
            for (int i = from; i < end; i++)
            {
                if (s[i] == '\n')
                {
                    return -1;
                }
                if (s[i] == ch)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindDoubleStar(string s, int from, int end)
        {
            for (int i = from; i + 1 < end; i++)
            {
                if (s[i] == '\n')
                {
                    return -1;
                }
                if (s[i] == '*' && s[i + 1] == '*')
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsWordBoundary(string s, int start, int i)
        {
            if (i == start)
            {
                return true;
            }
            char prev = s[i - 1];
            return !char.IsLetterOrDigit(prev) && prev != '/' && prev != '.' && prev != '_';
        }

        private static bool ContainsWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Path token characters. '"' '&lt;' '&gt;' are excluded so a path
        /// token can always sit inside a quoted &lt;link&gt; attribute
        /// ('&lt;' also marks a NeutralizedLt run, which must terminate
        /// the token).
        /// </summary>
        private static bool IsPathChar(char c)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
            switch (c)
            {
                case '"':
                case '<':
                case '>':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '*':
                case '`':
                case '|':
                    return false;
                default:
                    return true;
            }
        }

        private static int TrimTrailingPunctuation(string s, int contentStart, int j)
        {
            while (j > contentStart)
            {
                char last = s[j - 1];
                if (last == '.' || last == ',' || last == ':' || last == ';'
                    || last == '!' || last == '?' || last == '\''
                    || last == ')' || last == ']')
                {
                    j--;
                    continue;
                }
                break;
            }
            return j;
        }
    }
}
