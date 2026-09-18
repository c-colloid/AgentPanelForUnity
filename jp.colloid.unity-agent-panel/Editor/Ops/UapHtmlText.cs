using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reduces an HTML document to what an agent can read: the title, the
    /// visible text with headings / paragraphs / list items kept on their
    /// own lines, and the page's image and link URLs (absolute) so a
    /// "search -> page -> the picture in it" chain is two or three calls
    /// (design note 2026-09-17-web-fetch-tool.md section 4.4). Deliberately
    /// NOT a Markdown converter or a Readability clone: a small tolerant
    /// scanner with no dependency, which is all the tool needs.
    ///
    /// Scripts, styles, templates, SVG and noscript blocks are dropped
    /// wholesale; when the page has a &lt;main&gt; or &lt;article&gt; element
    /// its text is preferred over the whole body (navigation and footers
    /// fall away). Links are collected instead of inlined so a navigation
    /// bar does not turn every line into "Home (url) About (url) ...".
    /// </summary>
    public static class UapHtmlText
    {
        public const int MaxImages = 20;
        public const int MaxLinks = 40;

        public sealed class Result
        {
            public string Title = string.Empty;
            public string Text = string.Empty;
            /// <summary>alt (may be empty) and absolute URL, document order, de-duplicated.</summary>
            public List<KeyValuePair<string, string>> Images = new List<KeyValuePair<string, string>>();
            /// <summary>link text (may be empty) and absolute URL, document order, de-duplicated.</summary>
            public List<KeyValuePair<string, string>> Links = new List<KeyValuePair<string, string>>();
            /// <summary>Total image / link counts before the caps, so the report can say "20 of 137".</summary>
            public int ImageCount;
            public int LinkCount;
        }

        private static readonly HashSet<string> SkipElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "script", "style", "noscript", "template", "svg", "head", "iframe", "object", "canvas"
        };

        private static readonly HashSet<string> BlockElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "p", "div", "section", "article", "main", "header", "footer", "nav", "aside", "ul", "ol",
            "table", "tr", "blockquote", "pre", "figure", "figcaption", "hr", "form", "fieldset",
            "dl", "dt", "dd", "address", "details", "summary"
        };

        public static Result Extract(string html, Uri baseUri)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(html))
            {
                return result;
            }
            var all = new StringBuilder(Math.Min(html.Length, 1 << 16));
            var main = new StringBuilder();
            var title = new StringBuilder();
            var seenImages = new HashSet<string>(StringComparer.Ordinal);
            var seenLinks = new HashSet<string>(StringComparer.Ordinal);
            var linkText = new StringBuilder();
            string pendingHref = null;
            bool inMain = false;
            int mainDepth = 0;
            bool sawMain = false;
            string skipUntil = null;
            bool inTitle = false;
            int i = 0;
            int n = html.Length;
            while (i < n)
            {
                char c = html[i];
                if (c == '<')
                {
                    if (StartsWith(html, i, "<!--"))
                    {
                        int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        i = end < 0 ? n : end + 3;
                        continue;
                    }
                    if (StartsWith(html, i, "<![CDATA["))
                    {
                        int end = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        i = end < 0 ? n : end + 3;
                        continue;
                    }
                    // A tag starts with a letter, '/', '!' or '?' right after
                    // the '<'; "a < b", "<<" and "< 2" are text.
                    char after = i + 1 < n ? html[i + 1] : '\0';
                    bool opensTag = char.IsLetter(after) || after == '/' || after == '!' || after == '?';
                    int tagEnd = opensTag ? FindTagEnd(html, i) : i;
                    if (tagEnd < 0)
                    {
                        break;
                    }
                    if (tagEnd <= i)
                    {
                        // "<<" or "a < b": this '<' opens no tag; keep it
                        // as text and carry on after it.
                        if (skipUntil == null && !inTitle)
                        {
                            Append(all, main, inMain, "<");
                        }
                        i++;
                        continue;
                    }
                    string tag = html.Substring(i + 1, tagEnd - i - 1);
                    i = tagEnd + 1;
                    bool closing = tag.StartsWith("/", StringComparison.Ordinal);
                    string name = TagName(tag, closing);
                    if (name.Length == 0)
                    {
                        continue;
                    }
                    if (skipUntil != null)
                    {
                        if (closing && name == skipUntil)
                        {
                            skipUntil = null;
                        }
                        continue;
                    }
                    if (!closing && SkipElements.Contains(name))
                    {
                        if (name == "head")
                        {
                            // The head holds the title; scan it for that
                            // one element and skip the rest.
                            int headEnd = IndexOfClosing(html, i, "head");
                            ExtractTitle(html, i, headEnd < 0 ? n : headEnd, title);
                            i = headEnd < 0 ? n : headEnd;
                            continue;
                        }
                        if (!tag.EndsWith("/", StringComparison.Ordinal))
                        {
                            skipUntil = name;
                        }
                        continue;
                    }
                    if (name == "title")
                    {
                        inTitle = !closing;
                        continue;
                    }
                    if (name == "main" || name == "article")
                    {
                        if (!closing)
                        {
                            if (!inMain)
                            {
                                inMain = true;
                                sawMain = true;
                                mainDepth = 0;
                            }
                            else
                            {
                                mainDepth++;
                            }
                        }
                        else if (inMain)
                        {
                            if (mainDepth == 0)
                            {
                                inMain = false;
                            }
                            else
                            {
                                mainDepth--;
                            }
                        }
                    }
                    if (name == "img")
                    {
                        string src = Attribute(tag, "src") ?? Attribute(tag, "data-src");
                        string alt = Decode(Attribute(tag, "alt") ?? string.Empty).Trim();
                        string abs = Absolute(baseUri, src);
                        if (abs != null && seenImages.Add(abs))
                        {
                            result.ImageCount++;
                            if (result.Images.Count < MaxImages)
                            {
                                result.Images.Add(new KeyValuePair<string, string>(alt, abs));
                            }
                        }
                        Append(all, main, inMain, alt.Length > 0 ? "[img: " + alt + "]" : "[img]");
                        continue;
                    }
                    if (name == "a")
                    {
                        if (!closing)
                        {
                            pendingHref = Absolute(baseUri, Attribute(tag, "href"));
                            linkText.Length = 0;
                        }
                        else if (pendingHref != null)
                        {
                            if (!pendingHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                                && seenLinks.Add(pendingHref))
                            {
                                result.LinkCount++;
                                if (result.Links.Count < MaxLinks)
                                {
                                    result.Links.Add(new KeyValuePair<string, string>(
                                        Collapse(linkText.ToString()).Trim(), pendingHref));
                                }
                            }
                            pendingHref = null;
                        }
                        continue;
                    }
                    if (name == "br")
                    {
                        Append(all, main, inMain, "\n");
                        continue;
                    }
                    if (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6')
                    {
                        Append(all, main, inMain, closing ? "\n\n" : "\n\n" + new string('#', name[1] - '0') + " ");
                        continue;
                    }
                    if (name == "li")
                    {
                        // The next item's own "\n- " (or the list's closing
                        // block break) ends this one; a closing newline here
                        // would leave a blank line between items.
                        if (!closing)
                        {
                            Append(all, main, inMain, "\n- ");
                        }
                        continue;
                    }
                    if (name == "tr")
                    {
                        if (!closing)
                        {
                            Append(all, main, inMain, "\n");
                        }
                        continue;
                    }
                    if (name == "td" || name == "th")
                    {
                        Append(all, main, inMain, closing ? " | " : string.Empty);
                        continue;
                    }
                    if (BlockElements.Contains(name))
                    {
                        Append(all, main, inMain, "\n\n");
                    }
                    continue;
                }
                // Text run up to the next tag.
                int next = html.IndexOf('<', i);
                if (next < 0)
                {
                    next = n;
                }
                string run = html.Substring(i, next - i);
                i = next;
                if (skipUntil != null)
                {
                    continue;
                }
                string decoded = Decode(run);
                if (decoded.Trim().Length == 0)
                {
                    // Source indentation between tags: a separator, never a
                    // line of its own.
                    decoded = " ";
                }
                if (inTitle)
                {
                    title.Append(decoded);
                    continue;
                }
                if (pendingHref != null)
                {
                    linkText.Append(decoded);
                }
                Append(all, main, inMain, decoded);
            }
            result.Title = Collapse(title.ToString()).Trim();
            string body = sawMain && main.Length > 0 ? main.ToString() : all.ToString();
            result.Text = Tidy(body);
            return result;
        }

        /// <summary>
        /// The text block uap_web_fetch returns for an HTML page: title,
        /// body, then the capped image and link lists.
        /// </summary>
        public static string Render(Result r)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(r.Title))
            {
                sb.Append("Title: ").Append(r.Title).Append('\n');
            }
            sb.Append(r.Text);
            if (r.Images.Count > 0)
            {
                sb.Append("\n\nImages (").Append(r.Images.Count);
                if (r.ImageCount > r.Images.Count)
                {
                    sb.Append(" of ").Append(r.ImageCount);
                }
                sb.Append("):");
                foreach (KeyValuePair<string, string> img in r.Images)
                {
                    sb.Append("\n- ");
                    if (img.Key.Length > 0)
                    {
                        sb.Append(img.Key).Append(" -- ");
                    }
                    sb.Append(img.Value);
                }
            }
            if (r.Links.Count > 0)
            {
                sb.Append("\n\nLinks (").Append(r.Links.Count);
                if (r.LinkCount > r.Links.Count)
                {
                    sb.Append(" of ").Append(r.LinkCount);
                }
                sb.Append("):");
                foreach (KeyValuePair<string, string> link in r.Links)
                {
                    sb.Append("\n- ");
                    if (link.Key.Length > 0)
                    {
                        sb.Append(link.Key).Append(" -- ");
                    }
                    sb.Append(link.Value);
                }
            }
            return sb.ToString();
        }

        /// <summary>Decodes the common named entities plus numeric ones; unknown names are left as written.</summary>
        public static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0)
            {
                return s ?? string.Empty;
            }
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c != '&')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                int semi = s.IndexOf(';', i + 1);
                if (semi < 0 || semi - i > 12)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                string name = s.Substring(i + 1, semi - i - 1);
                string replacement = Entity(name);
                if (replacement == null)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                sb.Append(replacement);
                i = semi + 1;
            }
            return sb.ToString();
        }


        /// <summary>
        /// One code point as a string, for the entity table: document-text
        /// conversion, not a UI glyph (see GlyphAuditTests; the same note
        /// as UapPdfText.Cp).
        /// </summary>
        private static string Cp(int codePoint)
        {
            return char.ConvertFromUtf32(codePoint);
        }

        private static string Entity(string name)
        {
            if (name.Length == 0)
            {
                return null;
            }
            if (name[0] == '#')
            {
                int code;
                bool ok = name.Length > 1 && (name[1] == 'x' || name[1] == 'X')
                    ? int.TryParse(name.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                    : int.TryParse(name.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
                if (!ok || code <= 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                {
                    return null;
                }
                return char.ConvertFromUtf32(code);
            }
            switch (name)
            {
                case "amp": return "&";
                case "lt": return "<";
                case "gt": return ">";
                case "quot": return "\"";
                case "apos": return "'";
                case "nbsp": return " ";
                case "copy": return Cp(0x00A9);
                case "reg": return Cp(0x00AE);
                case "trade": return Cp(0x2122);
                case "mdash": return Cp(0x2014);
                case "ndash": return Cp(0x2013);
                case "hellip": return Cp(0x2026);
                case "lsquo": return Cp(0x2018);
                case "rsquo": return Cp(0x2019);
                case "ldquo": return Cp(0x201C);
                case "rdquo": return Cp(0x201D);
                case "laquo": return Cp(0x00AB);
                case "raquo": return Cp(0x00BB);
                case "middot": return Cp(0x00B7);
                case "bull": return Cp(0x2022);
                case "times": return Cp(0x00D7);
                case "deg": return Cp(0x00B0);
                case "euro": return Cp(0x20AC);
                case "yen": return Cp(0x00A5);
                case "pound": return Cp(0x00A3);
            }
            return null;
        }

        private static void Append(StringBuilder all, StringBuilder main, bool inMain, string s)
        {
            if (s.Length == 0)
            {
                return;
            }
            all.Append(s);
            if (inMain)
            {
                main.Append(s);
            }
        }

        private static bool StartsWith(string s, int at, string prefix)
        {
            return string.CompareOrdinal(s, at, prefix, 0, prefix.Length) == 0;
        }

        /// <summary>Index of the '&gt;' that ends the tag opened at <paramref name="start"/>, honoring quoted attribute values.</summary>
        private static int FindTagEnd(string s, int start)
        {
            char quote = '\0';
            for (int i = start + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }
                if (c == '>')
                {
                    return i;
                }
                if (c == '<')
                {
                    // Another '<' before any '>': the first one opened no
                    // tag (the caller treats it as text). i - 1 == start
                    // for "<<", which the caller recognizes as "not a tag".
                    return i - 1;
                }
            }
            return -1;
        }

        private static string TagName(string tag, bool closing)
        {
            int start = closing ? 1 : 0;
            int end = start;
            while (end < tag.Length && !char.IsWhiteSpace(tag[end]) && tag[end] != '/' && tag[end] != '>')
            {
                end++;
            }
            return tag.Substring(start, end - start).ToLowerInvariant();
        }

        /// <summary>Value of <paramref name="attr"/> in a tag body, quoted or bare; null when absent.</summary>
        public static string Attribute(string tag, string attr)
        {
            int i = 0;
            while (i < tag.Length)
            {
                int idx = tag.IndexOf(attr, i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return null;
                }
                bool boundaryBefore = idx == 0 || char.IsWhiteSpace(tag[idx - 1]);
                int after = idx + attr.Length;
                int j = after;
                while (j < tag.Length && char.IsWhiteSpace(tag[j]))
                {
                    j++;
                }
                if (!boundaryBefore || j >= tag.Length || tag[j] != '=')
                {
                    i = after;
                    continue;
                }
                j++;
                while (j < tag.Length && char.IsWhiteSpace(tag[j]))
                {
                    j++;
                }
                if (j >= tag.Length)
                {
                    return string.Empty;
                }
                char q = tag[j];
                if (q == '"' || q == '\'')
                {
                    int close = tag.IndexOf(q, j + 1);
                    return close < 0 ? tag.Substring(j + 1) : tag.Substring(j + 1, close - j - 1);
                }
                int end = j;
                while (end < tag.Length && !char.IsWhiteSpace(tag[end]) && tag[end] != '>')
                {
                    end++;
                }
                return tag.Substring(j, end - j);
            }
            return null;
        }

        private static int IndexOfClosing(string s, int from, string name)
        {
            int idx = s.IndexOf("</" + name, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return -1;
            }
            int end = s.IndexOf('>', idx);
            return end < 0 ? -1 : end + 1;
        }

        private static void ExtractTitle(string s, int from, int to, StringBuilder title)
        {
            int open = s.IndexOf("<title", from, StringComparison.OrdinalIgnoreCase);
            if (open < 0 || open >= to)
            {
                return;
            }
            int openEnd = s.IndexOf('>', open);
            if (openEnd < 0 || openEnd >= to)
            {
                return;
            }
            int close = s.IndexOf("</title", openEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0 || close > to)
            {
                close = to;
            }
            title.Append(Decode(s.Substring(openEnd + 1, close - openEnd - 1)));
        }

        /// <summary>Absolute form of an href/src against the page URL; null for empty, fragment-only or unparsable values.</summary>
        public static string Absolute(Uri baseUri, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }
            string r = Decode(reference.Trim());
            if (r.StartsWith("#", StringComparison.Ordinal) || r.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            Uri abs;
            if (baseUri != null && Uri.TryCreate(baseUri, r, out abs))
            {
                return abs.AbsoluteUri;
            }
            if (Uri.TryCreate(r, UriKind.Absolute, out abs))
            {
                return abs.AbsoluteUri;
            }
            return null;
        }

        /// <summary>Collapses every whitespace run to one space.</summary>
        public static string Collapse(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = true;
                    continue;
                }
                if (space && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Collapses horizontal whitespace within a line, trims each line,
        /// and limits blank runs to one empty line -- HTML source indentation
        /// otherwise survives as pages of spaces.
        /// </summary>
        public static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder(s.Length);
            int blank = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = Collapse(lines[i]).Trim();
                if (line.EndsWith("|", StringComparison.Ordinal))
                {
                    line = line.TrimEnd('|', ' ');
                }
                if (line.Length == 0)
                {
                    blank++;
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(blank > 0 ? "\n\n" : "\n");
                }
                blank = 0;
                sb.Append(line);
            }
            return sb.ToString();
        }
    }
}
