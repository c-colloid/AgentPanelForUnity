using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Best-effort, dependency-free PDF text extraction for uap_web_fetch
    /// (design note 2026-09-17-web-fetch-tool.md section 4.3, stage 2):
    /// enough for an agent to read a paper, a manual or a spec sheet
    /// fetched from the web when its own file reader cannot open PDFs
    /// (Codex / Grok; Claude Code reads the saved file directly).
    ///
    /// What it does: scans every "N G obj" in the file (no xref trust --
    /// a damaged or incrementally updated table is common), opens object
    /// streams (/ObjStm), walks the page tree (falls back to every /Page
    /// in file order), decodes each page's content streams (FlateDecode
    /// with PNG predictors, ASCIIHex, ASCII85, RunLength, LZW), and runs a
    /// small text-operator interpreter (Tj TJ ' " Tf Td TD T* Tm BT ET)
    /// that maps codes to text through the font's /ToUnicode CMap, or its
    /// /Encoding (WinAnsi / Standard + /Differences glyph names) for
    /// simple fonts. Line breaks follow the text-position operators; word
    /// gaps come from the strings themselves and from large negative TJ
    /// adjustments.
    ///
    /// What it does not do: render glyphs, reorder columns, read text in
    /// XObject forms or annotations, or decrypt encrypted files (those
    /// return an empty page and a warning). Pure and exception-safe: any
    /// malformed structure degrades to less text, never to a throw.
    /// </summary>
    public static class UapPdfText
    {
        public sealed class Result
        {
            /// <summary>One entry per extracted page (1-based order as in the document), already tidied.</summary>
            public readonly List<string> Pages = new List<string>();
            /// <summary>Pages in the document (best knowledge), independent of the range extracted.</summary>
            public int PageCount;
            /// <summary>1-based index of the first entry in <see cref="Pages"/>.</summary>
            public int FirstPage;
            public readonly List<string> Warnings = new List<string>();
            public bool Encrypted;
        }

        /// <summary>Parses "3", "1-5", "7-" or "-4" (1-based, inclusive) into a range; false with a message otherwise.</summary>
        public static bool TryParsePageRange(string spec, out int first, out int last, out string error)
        {
            first = 1;
            last = int.MaxValue;
            error = null;
            if (string.IsNullOrWhiteSpace(spec))
            {
                return true;
            }
            string s = spec.Trim();
            int dash = s.IndexOf('-');
            string a = dash < 0 ? s : s.Substring(0, dash).Trim();
            string b = dash < 0 ? s : s.Substring(dash + 1).Trim();
            if (a.Length > 0 && (!int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out first) || first < 1))
            {
                error = "pages must look like \"3\", \"1-5\" or \"7-\" (got \"" + spec + "\").";
                return false;
            }
            if (b.Length > 0 && (!int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out last) || last < 1))
            {
                error = "pages must look like \"3\", \"1-5\" or \"7-\" (got \"" + spec + "\").";
                return false;
            }
            if (a.Length == 0)
            {
                first = 1;
            }
            if (b.Length == 0 && dash >= 0)
            {
                last = int.MaxValue;
            }
            if (last < first)
            {
                error = "pages: the range end is before its start (\"" + spec + "\").";
                return false;
            }
            return true;
        }

        /// <summary>Extracts pages <paramref name="firstPage"/>..<paramref name="lastPage"/> (1-based, inclusive; clamp with int.MaxValue).</summary>
        public static Result Extract(byte[] pdf, int firstPage, int lastPage)
        {
            var result = new Result();
            if (pdf == null || pdf.Length == 0)
            {
                result.Warnings.Add("empty file");
                return result;
            }
            Document doc;
            try
            {
                doc = Document.Load(pdf);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("could not parse the file structure: " + ex.Message);
                return result;
            }
            result.Encrypted = doc.Encrypted;
            if (doc.Encrypted)
            {
                result.Warnings.Add("the PDF is encrypted; text cannot be extracted without the password");
            }
            List<Dict> pages = doc.CollectPages();
            result.PageCount = pages.Count;
            if (firstPage < 1)
            {
                firstPage = 1;
            }
            result.FirstPage = firstPage;
            for (int i = firstPage - 1; i < pages.Count && i < lastPage; i++)
            {
                string text;
                try
                {
                    text = doc.Encrypted ? string.Empty : doc.ExtractPage(pages[i]);
                }
                catch (Exception ex)
                {
                    text = string.Empty;
                    result.Warnings.Add("page " + (i + 1) + ": " + ex.Message);
                }
                result.Pages.Add(Tidy(text));
            }
            return result;
        }

        /// <summary>The text block uap_web_fetch returns: pages separated by a marker line, with the warnings first.</summary>
        public static string Render(Result r)
        {
            var sb = new StringBuilder();
            if (r.Warnings.Count > 0)
            {
                sb.Append("(extraction notes: ").Append(string.Join("; ", r.Warnings.ToArray())).Append(")\n");
            }
            for (int i = 0; i < r.Pages.Count; i++)
            {
                if (i > 0 || r.Warnings.Count > 0)
                {
                    sb.Append("\n\n");
                }
                sb.Append("--- Page ").Append(r.FirstPage + i).Append(" of ").Append(r.PageCount).Append(" ---\n");
                sb.Append(r.Pages[i].Length == 0 ? "(no extractable text on this page)" : r.Pages[i]);
            }
            if (r.Pages.Count == 0)
            {
                sb.Append("(no pages in the requested range; the document has ").Append(r.PageCount).Append(" pages)");
            }
            return sb.ToString();
        }

        /// <summary>Collapses runs of spaces, trims lines, limits blank runs to one.</summary>
        public static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder(s.Length);
            int blank = 0;
            foreach (string raw in lines)
            {
                string line = CollapseSpaces(raw).Trim();
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


        /// <summary>
        /// One code point as a string. The glyph-name, encoding and entity
        /// tables below are DOCUMENT-TEXT conversions (what a PDF byte means
        /// as Unicode), not UI glyphs: they are written as code points so
        /// GlyphAuditTests, which pins the panel's rendered glyph set, does
        /// not read them as icon constants. Text produced here reaches the
        /// UI only through the tool-result path and its display sanitizer.
        /// </summary>
        private static string Cp(int codePoint)
        {
            return char.ConvertFromUtf32(codePoint);
        }

        private static string CollapseSpaces(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (c == ' ' || c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator)
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

        // ------------------------------------------------------------------
        // Object model
        // ------------------------------------------------------------------

        private sealed class Name
        {
            public readonly string Value;
            public Name(string value) { Value = value; }
            public override string ToString() { return "/" + Value; }
        }

        private sealed class Ref
        {
            public readonly int Num;
            public readonly int Gen;
            public Ref(int num, int gen) { Num = num; Gen = gen; }
        }

        private sealed class PdfString
        {
            public readonly byte[] Bytes;
            public PdfString(byte[] bytes) { Bytes = bytes; }
        }

        private sealed class Dict : Dictionary<string, object>
        {
            public Dict() : base(StringComparer.Ordinal) { }
            public object Get(string key)
            {
                object v;
                return TryGetValue(key, out v) ? v : null;
            }
        }

        private sealed class Stream
        {
            public Dict Dict;
            public byte[] Raw;
            private byte[] _decoded;
            private bool _decodedTried;

            public byte[] Decoded(Document doc)
            {
                if (!_decodedTried)
                {
                    _decodedTried = true;
                    try
                    {
                        _decoded = Filters.Decode(Raw, Dict, doc);
                    }
                    catch (Exception)
                    {
                        _decoded = null;
                    }
                }
                return _decoded;
            }
        }

        private sealed class Operator
        {
            public readonly string Value;
            public Operator(string value) { Value = value; }
        }

        private static readonly object EndOfInput = new object();

        // ------------------------------------------------------------------
        // Lexer / parser over the Latin-1 view of the bytes
        // ------------------------------------------------------------------

        private sealed class Lexer
        {
            private readonly byte[] _b;
            public int Pos;
            public readonly int End;

            public Lexer(byte[] bytes, int start, int end)
            {
                _b = bytes;
                Pos = start;
                End = Math.Min(end, bytes.Length);
            }

            public byte[] Bytes { get { return _b; } }

            private static bool IsWhite(byte c)
            {
                return c == 0x20 || c == 0x0A || c == 0x0D || c == 0x09 || c == 0x0C || c == 0x00;
            }

            private static bool IsDelim(byte c)
            {
                return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
            }

            public void SkipWhite()
            {
                while (Pos < End)
                {
                    byte c = _b[Pos];
                    if (IsWhite(c))
                    {
                        Pos++;
                    }
                    else if (c == '%')
                    {
                        while (Pos < End && _b[Pos] != '\n' && _b[Pos] != '\r')
                        {
                            Pos++;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            /// <summary>
            /// Next object: number (double), Name, PdfString, List&lt;object&gt;,
            /// Dict, Ref, bool, null (as DBNull.Value), Operator (bare keyword),
            /// or EndOfInput. <paramref name="allowRefs"/> is off inside content
            /// streams, where "1 0 R" cannot occur but "1 0 Td" can.
            /// </summary>
            public object Next(bool allowRefs)
            {
                SkipWhite();
                if (Pos >= End)
                {
                    return EndOfInput;
                }
                byte c = _b[Pos];
                if (c == '/')
                {
                    Pos++;
                    int start = Pos;
                    while (Pos < End && !IsWhite(_b[Pos]) && !IsDelim(_b[Pos]))
                    {
                        Pos++;
                    }
                    return new Name(DecodeName(Latin1(_b, start, Pos - start)));
                }
                if (c == '(')
                {
                    return ReadLiteralString();
                }
                if (c == '<')
                {
                    if (Pos + 1 < End && _b[Pos + 1] == '<')
                    {
                        Pos += 2;
                        return ReadDict(allowRefs);
                    }
                    return ReadHexString();
                }
                if (c == '[')
                {
                    Pos++;
                    var list = new List<object>();
                    while (true)
                    {
                        SkipWhite();
                        if (Pos >= End)
                        {
                            break;
                        }
                        if (_b[Pos] == ']')
                        {
                            Pos++;
                            break;
                        }
                        object item = Next(allowRefs);
                        if (item == EndOfInput)
                        {
                            break;
                        }
                        list.Add(item);
                    }
                    return list;
                }
                if (c == ']' || c == '>' || c == ')' || c == '{' || c == '}')
                {
                    Pos++;
                    return Next(allowRefs);
                }
                if ((c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.')
                {
                    int start = Pos;
                    while (Pos < End && !IsWhite(_b[Pos]) && !IsDelim(_b[Pos]))
                    {
                        Pos++;
                    }
                    string tok = Latin1(_b, start, Pos - start);
                    double value;
                    if (!TryNumber(tok, out value))
                    {
                        return new Operator(tok);
                    }
                    if (allowRefs && value >= 0 && value == Math.Floor(value))
                    {
                        int save = Pos;
                        SkipWhite();
                        int genStart = Pos;
                        while (Pos < End && _b[Pos] >= '0' && _b[Pos] <= '9')
                        {
                            Pos++;
                        }
                        if (Pos > genStart)
                        {
                            int afterGen = Pos;
                            SkipWhite();
                            if (Pos < End && _b[Pos] == 'R' && (Pos + 1 >= End || IsWhite(_b[Pos + 1]) || IsDelim(_b[Pos + 1])))
                            {
                                Pos++;
                                int gen;
                                int.TryParse(Latin1(_b, genStart, afterGen - genStart), out gen);
                                return new Ref((int)value, gen);
                            }
                        }
                        Pos = save;
                    }
                    return value;
                }
                // Bare keyword.
                int kStart = Pos;
                while (Pos < End && !IsWhite(_b[Pos]) && !IsDelim(_b[Pos]))
                {
                    Pos++;
                }
                if (Pos == kStart)
                {
                    Pos++;
                    return Next(allowRefs);
                }
                string keyword = Latin1(_b, kStart, Pos - kStart);
                if (keyword == "true")
                {
                    return true;
                }
                if (keyword == "false")
                {
                    return false;
                }
                if (keyword == "null")
                {
                    return DBNull.Value;
                }
                return new Operator(keyword);
            }

            private Dict ReadDict(bool allowRefs)
            {
                var dict = new Dict();
                while (true)
                {
                    SkipWhite();
                    if (Pos >= End)
                    {
                        break;
                    }
                    if (_b[Pos] == '>')
                    {
                        Pos++;
                        if (Pos < End && _b[Pos] == '>')
                        {
                            Pos++;
                        }
                        break;
                    }
                    object key = Next(allowRefs);
                    if (key == EndOfInput)
                    {
                        break;
                    }
                    var name = key as Name;
                    if (name == null)
                    {
                        continue;
                    }
                    object value = Next(allowRefs);
                    if (value == EndOfInput)
                    {
                        break;
                    }
                    dict[name.Value] = value;
                }
                return dict;
            }

            private PdfString ReadLiteralString()
            {
                Pos++;
                var ms = new MemoryStream();
                int depth = 1;
                while (Pos < End)
                {
                    byte c = _b[Pos++];
                    if (c == '\\')
                    {
                        if (Pos >= End)
                        {
                            break;
                        }
                        byte e = _b[Pos++];
                        switch (e)
                        {
                            case (byte)'n': ms.WriteByte(10); break;
                            case (byte)'r': ms.WriteByte(13); break;
                            case (byte)'t': ms.WriteByte(9); break;
                            case (byte)'b': ms.WriteByte(8); break;
                            case (byte)'f': ms.WriteByte(12); break;
                            case (byte)'\r':
                                if (Pos < End && _b[Pos] == '\n')
                                {
                                    Pos++;
                                }
                                break;
                            case (byte)'\n': break;
                            default:
                                if (e >= '0' && e <= '7')
                                {
                                    int v = e - '0';
                                    for (int k = 0; k < 2 && Pos < End && _b[Pos] >= '0' && _b[Pos] <= '7'; k++)
                                    {
                                        v = v * 8 + (_b[Pos++] - '0');
                                    }
                                    ms.WriteByte((byte)(v & 0xFF));
                                }
                                else
                                {
                                    ms.WriteByte(e);
                                }
                                break;
                        }
                        continue;
                    }
                    if (c == '(')
                    {
                        depth++;
                    }
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                    ms.WriteByte(c);
                }
                return new PdfString(ms.ToArray());
            }

            private PdfString ReadHexString()
            {
                Pos++;
                var ms = new MemoryStream();
                int hi = -1;
                while (Pos < End)
                {
                    byte c = _b[Pos++];
                    if (c == '>')
                    {
                        break;
                    }
                    int v = HexValue(c);
                    if (v < 0)
                    {
                        continue;
                    }
                    if (hi < 0)
                    {
                        hi = v;
                    }
                    else
                    {
                        ms.WriteByte((byte)(hi * 16 + v));
                        hi = -1;
                    }
                }
                if (hi >= 0)
                {
                    ms.WriteByte((byte)(hi * 16));
                }
                return new PdfString(ms.ToArray());
            }
        }

        private static int HexValue(int c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static bool TryNumber(string tok, out double value)
        {
            // PDF numbers: optional sign, digits, optional single '.'; things
            // like "--5" or "3.4.5" appear in the wild and are tolerated.
            string cleaned = tok.Replace("--", "-");
            if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
            value = 0;
            return false;
        }

        private static string DecodeName(string raw)
        {
            if (raw.IndexOf('#') < 0)
            {
                return raw;
            }
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '#' && i + 2 < raw.Length && HexValue(raw[i + 1]) >= 0 && HexValue(raw[i + 2]) >= 0)
                {
                    sb.Append((char)(HexValue(raw[i + 1]) * 16 + HexValue(raw[i + 2])));
                    i += 2;
                }
                else
                {
                    sb.Append(raw[i]);
                }
            }
            return sb.ToString();
        }

        private static string Latin1(byte[] b, int start, int count)
        {
            var chars = new char[count];
            for (int i = 0; i < count; i++)
            {
                chars[i] = (char)b[start + i];
            }
            return new string(chars);
        }

        private static int IndexOf(byte[] hay, string needle, int from, int to)
        {
            int n = needle.Length;
            int limit = Math.Min(to, hay.Length) - n;
            for (int i = Math.Max(0, from); i <= limit; i++)
            {
                int k = 0;
                while (k < n && hay[i + k] == (byte)needle[k])
                {
                    k++;
                }
                if (k == n)
                {
                    return i;
                }
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // Document: object table, page tree, page text
        // ------------------------------------------------------------------

        private sealed class Document
        {
            private readonly byte[] _bytes;
            private readonly Dictionary<int, object> _objects = new Dictionary<int, object>();
            private readonly Dictionary<object, Font> _fonts = new Dictionary<object, Font>();
            public bool Encrypted;

            private static readonly Regex ObjHeader = new Regex(@"(?<![0-9])(\d{1,10})\s+(\d{1,5})\s+obj\b", RegexOptions.CultureInvariant);

            private Document(byte[] bytes)
            {
                _bytes = bytes;
            }

            public static Document Load(byte[] bytes)
            {
                var doc = new Document(bytes);
                string latin1 = Latin1(bytes, 0, bytes.Length);
                var headers = new List<KeyValuePair<int, int>>();
                foreach (Match m in ObjHeader.Matches(latin1))
                {
                    int num;
                    if (int.TryParse(m.Groups[1].Value, out num))
                    {
                        headers.Add(new KeyValuePair<int, int>(num, m.Index + m.Length));
                    }
                }
                for (int i = 0; i < headers.Count; i++)
                {
                    int num = headers[i].Key;
                    int start = headers[i].Value;
                    int limit = i + 1 < headers.Count ? headers[i + 1].Value : bytes.Length;
                    try
                    {
                        object value = doc.ParseIndirect(start, limit);
                        if (value != null)
                        {
                            // Later definitions win (incremental updates append).
                            doc._objects[num] = value;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                doc.ExpandObjectStreams();
                doc.Encrypted = latin1.IndexOf("/Encrypt", StringComparison.Ordinal) >= 0
                    && latin1.IndexOf("trailer", StringComparison.Ordinal) >= 0
                    || (latin1.IndexOf("/Encrypt", StringComparison.Ordinal) >= 0 && doc.HasXrefStreamWithEncrypt());
                return doc;
            }

            private bool HasXrefStreamWithEncrypt()
            {
                foreach (object o in _objects.Values)
                {
                    var s = o as Stream;
                    if (s != null && IsType(s.Dict, "XRef") && s.Dict.ContainsKey("Encrypt"))
                    {
                        return true;
                    }
                }
                return false;
            }

            private object ParseIndirect(int start, int limit)
            {
                var lexer = new Lexer(_bytes, start, limit);
                object value = lexer.Next(true);
                if (value == EndOfInput || value is Operator)
                {
                    return null;
                }
                var dict = value as Dict;
                if (dict != null)
                {
                    lexer.SkipWhite();
                    if (IndexOf(_bytes, "stream", lexer.Pos, lexer.Pos + 6) == lexer.Pos)
                    {
                        int dataStart = lexer.Pos + 6;
                        if (dataStart < _bytes.Length && _bytes[dataStart] == '\r')
                        {
                            dataStart++;
                        }
                        if (dataStart < _bytes.Length && _bytes[dataStart] == '\n')
                        {
                            dataStart++;
                        }
                        int length = -1;
                        object lengthObj = dict.Get("Length");
                        double d;
                        if (lengthObj is double)
                        {
                            length = (int)(double)lengthObj;
                        }
                        else if (lengthObj is Ref)
                        {
                            // Resolved lazily below once every object is known;
                            // fall back to scanning for endstream now.
                            length = -1;
                        }
                        int endstream = IndexOf(_bytes, "endstream", dataStart, limit);
                        if (length < 0 || dataStart + length > limit
                            || (endstream >= 0 && Math.Abs(endstream - (dataStart + length)) > 3
                                && IndexOf(_bytes, "endstream", dataStart + length, dataStart + length + 4) < 0))
                        {
                            if (endstream < 0)
                            {
                                endstream = limit;
                            }
                            int end = endstream;
                            while (end > dataStart && (_bytes[end - 1] == '\n' || _bytes[end - 1] == '\r'))
                            {
                                end--;
                            }
                            length = end - dataStart;
                        }
                        var raw = new byte[Math.Max(0, length)];
                        Array.Copy(_bytes, dataStart, raw, 0, raw.Length);
                        return new Stream { Dict = dict, Raw = raw };
                    }
                }
                return value;
            }

            private void ExpandObjectStreams()
            {
                var streams = new List<Stream>();
                foreach (object o in _objects.Values)
                {
                    var s = o as Stream;
                    if (s != null && IsType(s.Dict, "ObjStm"))
                    {
                        streams.Add(s);
                    }
                }
                foreach (Stream s in streams)
                {
                    try
                    {
                        byte[] data = s.Decoded(this);
                        if (data == null)
                        {
                            continue;
                        }
                        int n = (int)ToDouble(Resolve(s.Dict.Get("N")));
                        int first = (int)ToDouble(Resolve(s.Dict.Get("First")));
                        var header = new Lexer(data, 0, first);
                        var pairs = new List<KeyValuePair<int, int>>();
                        for (int i = 0; i < n; i++)
                        {
                            object a = header.Next(false);
                            object b = header.Next(false);
                            if (!(a is double) || !(b is double))
                            {
                                break;
                            }
                            pairs.Add(new KeyValuePair<int, int>((int)(double)a, (int)(double)b));
                        }
                        for (int i = 0; i < pairs.Count; i++)
                        {
                            int start = first + pairs[i].Value;
                            int limit = i + 1 < pairs.Count ? first + pairs[i + 1].Value : data.Length;
                            if (start < 0 || start >= data.Length)
                            {
                                continue;
                            }
                            object value = new Lexer(data, start, limit).Next(true);
                            if (value != EndOfInput && !(value is Operator) && !_objects.ContainsKey(pairs[i].Key))
                            {
                                _objects[pairs[i].Key] = value;
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            public object Resolve(object o)
            {
                int guard = 0;
                while (o is Ref && guard++ < 32)
                {
                    object v;
                    if (!_objects.TryGetValue(((Ref)o).Num, out v))
                    {
                        return null;
                    }
                    o = v;
                }
                if (o == DBNull.Value)
                {
                    return null;
                }
                return o;
            }

            public Dict ResolveDict(object o)
            {
                object r = Resolve(o);
                var s = r as Stream;
                return s != null ? s.Dict : r as Dict;
            }

            private static bool IsType(Dict d, string type)
            {
                if (d == null)
                {
                    return false;
                }
                var n = d.Get("Type") as Name;
                return n != null && n.Value == type;
            }

            public List<Dict> CollectPages()
            {
                var pages = new List<Dict>();
                Dict root = null;
                foreach (object o in _objects.Values)
                {
                    var d = o as Dict;
                    if (IsType(d, "Catalog"))
                    {
                        Dict candidate = ResolveDict(d.Get("Pages"));
                        if (candidate != null)
                        {
                            root = candidate;
                        }
                    }
                }
                if (root != null)
                {
                    var seen = new HashSet<Dict>();
                    Walk(root, pages, seen, 0);
                }
                if (pages.Count == 0)
                {
                    var nums = new List<int>(_objects.Keys);
                    nums.Sort();
                    foreach (int num in nums)
                    {
                        var d = _objects[num] as Dict;
                        if (IsType(d, "Page"))
                        {
                            pages.Add(d);
                        }
                    }
                }
                return pages;
            }

            private void Walk(Dict node, List<Dict> pages, HashSet<Dict> seen, int depth)
            {
                if (node == null || depth > 64 || !seen.Add(node))
                {
                    return;
                }
                var kids = Resolve(node.Get("Kids")) as List<object>;
                if (IsType(node, "Page") || (kids == null && node.ContainsKey("Contents")))
                {
                    pages.Add(node);
                    return;
                }
                if (kids == null)
                {
                    return;
                }
                foreach (object kid in kids)
                {
                    Walk(ResolveDict(kid), pages, seen, depth + 1);
                }
            }

            private object Inherited(Dict page, string key)
            {
                Dict node = page;
                int guard = 0;
                while (node != null && guard++ < 64)
                {
                    object v = Resolve(node.Get(key));
                    if (v != null)
                    {
                        return v;
                    }
                    node = ResolveDict(node.Get("Parent"));
                }
                return null;
            }

            public string ExtractPage(Dict page)
            {
                object contents = Resolve(page.Get("Contents"));
                var buffer = new MemoryStream();
                var list = contents as List<object>;
                if (list != null)
                {
                    foreach (object item in list)
                    {
                        AppendStream(buffer, Resolve(item) as Stream);
                    }
                }
                else
                {
                    AppendStream(buffer, contents as Stream);
                }
                if (buffer.Length == 0)
                {
                    return string.Empty;
                }
                var resources = Inherited(page, "Resources") as Dict;
                Dict fonts = resources != null ? ResolveDict(resources.Get("Font")) : null;
                return new ContentInterpreter(this, fonts).Run(buffer.ToArray());
            }

            private void AppendStream(MemoryStream buffer, Stream s)
            {
                if (s == null)
                {
                    return;
                }
                byte[] data = s.Decoded(this);
                if (data == null)
                {
                    return;
                }
                buffer.Write(data, 0, data.Length);
                buffer.WriteByte((byte)'\n');
            }

            public Font GetFont(Dict fonts, string resourceName)
            {
                if (fonts == null)
                {
                    return null;
                }
                object key = fonts.Get(resourceName);
                if (key == null)
                {
                    return null;
                }
                object cacheKey = key is Ref ? (object)("R" + ((Ref)key).Num) : key;
                Font font;
                if (_fonts.TryGetValue(cacheKey, out font))
                {
                    return font;
                }
                Dict dict = ResolveDict(key);
                font = dict == null ? null : Font.Build(this, dict);
                _fonts[cacheKey] = font;
                return font;
            }

            public static double ToDouble(object o)
            {
                return o is double ? (double)o : 0;
            }
        }

        // ------------------------------------------------------------------
        // Filters
        // ------------------------------------------------------------------

        private static class Filters
        {
            public static byte[] Decode(byte[] raw, Dict dict, Document doc)
            {
                object filter = doc.Resolve(dict.Get("Filter"));
                object parms = doc.Resolve(dict.Get("DecodeParms"));
                var filters = new List<string>();
                var parmList = new List<Dict>();
                var single = filter as Name;
                if (single != null)
                {
                    filters.Add(single.Value);
                    parmList.Add(parms as Dict);
                }
                else
                {
                    var many = filter as List<object>;
                    if (many != null)
                    {
                        var parmArray = parms as List<object>;
                        for (int i = 0; i < many.Count; i++)
                        {
                            var n = doc.Resolve(many[i]) as Name;
                            if (n != null)
                            {
                                filters.Add(n.Value);
                                parmList.Add(parmArray != null && i < parmArray.Count ? doc.Resolve(parmArray[i]) as Dict : parms as Dict);
                            }
                        }
                    }
                }
                byte[] data = raw;
                for (int i = 0; i < filters.Count; i++)
                {
                    switch (filters[i])
                    {
                        case "FlateDecode":
                        case "Fl":
                            data = Predictor(Inflate(data), parmList[i], doc);
                            break;
                        case "LZWDecode":
                        case "LZW":
                            data = Predictor(Lzw(data, parmList[i], doc), parmList[i], doc);
                            break;
                        case "ASCIIHexDecode":
                        case "AHx":
                            data = AsciiHex(data);
                            break;
                        case "ASCII85Decode":
                        case "A85":
                            data = Ascii85(data);
                            break;
                        case "RunLengthDecode":
                        case "RL":
                            data = RunLength(data);
                            break;
                        case "DCTDecode":
                        case "DCT":
                        case "JPXDecode":
                        case "CCITTFaxDecode":
                        case "CCF":
                        case "JBIG2Decode":
                            // Image data: nothing to read as text.
                            return null;
                        default:
                            return null;
                    }
                    if (data == null)
                    {
                        return null;
                    }
                }
                return data;
            }

            public static byte[] Inflate(byte[] data)
            {
                if (data == null || data.Length == 0)
                {
                    return new byte[0];
                }
                int start = 0;
                while (start < data.Length && (data[start] == 0x0A || data[start] == 0x0D || data[start] == 0x20 || data[start] == 0x09))
                {
                    start++;
                }
                // zlib header (CMF/FLG) when the low nibble of CMF is 8 (deflate) and the check passes.
                if (start + 2 <= data.Length && (data[start] & 0x0F) == 8 && ((data[start] << 8) | data[start + 1]) % 31 == 0)
                {
                    start += 2;
                }
                var output = new MemoryStream();
                try
                {
                    using (var input = new MemoryStream(data, start, data.Length - start))
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        var chunk = new byte[16 * 1024];
                        int read;
                        while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            output.Write(chunk, 0, read);
                            if (output.Length > 256L * 1024L * 1024L)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Corrupt tail: keep whatever inflated.
                }
                return output.ToArray();
            }

            private static byte[] Predictor(byte[] data, Dict parms, Document doc)
            {
                if (data == null || parms == null)
                {
                    return data;
                }
                int predictor = (int)Document.ToDouble(doc.Resolve(parms.Get("Predictor")));
                if (predictor < 2)
                {
                    return data;
                }
                int colors = Math.Max(1, (int)Document.ToDouble(doc.Resolve(parms.Get("Colors"))));
                if (!parms.ContainsKey("Colors")) colors = 1;
                int bpc = parms.ContainsKey("BitsPerComponent") ? (int)Document.ToDouble(doc.Resolve(parms.Get("BitsPerComponent"))) : 8;
                int columns = parms.ContainsKey("Columns") ? (int)Document.ToDouble(doc.Resolve(parms.Get("Columns"))) : 1;
                int bpp = Math.Max(1, colors * bpc / 8);
                int rowLength = (columns * colors * bpc + 7) / 8;
                if (predictor == 2)
                {
                    // TIFF predictor, 8-bit only.
                    if (bpc == 8)
                    {
                        for (int r = 0; r + rowLength <= data.Length; r += rowLength)
                        {
                            for (int i = bpp; i < rowLength; i++)
                            {
                                data[r + i] = (byte)(data[r + i] + data[r + i - bpp]);
                            }
                        }
                    }
                    return data;
                }
                // PNG predictors: each row is prefixed with its filter type.
                int rows = data.Length / (rowLength + 1);
                var output = new byte[rows * rowLength];
                var prev = new byte[rowLength];
                for (int r = 0; r < rows; r++)
                {
                    int ft = data[r * (rowLength + 1)];
                    int src = r * (rowLength + 1) + 1;
                    int dst = r * rowLength;
                    for (int i = 0; i < rowLength; i++)
                    {
                        int raw = data[src + i];
                        int left = i >= bpp ? output[dst + i - bpp] : 0;
                        int up = prev[i];
                        int upLeft = i >= bpp ? prev[i - bpp] : 0;
                        int value;
                        switch (ft)
                        {
                            case 0: value = raw; break;
                            case 1: value = raw + left; break;
                            case 2: value = raw + up; break;
                            case 3: value = raw + ((left + up) >> 1); break;
                            case 4:
                                {
                                    int p = left + up - upLeft;
                                    int pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - upLeft);
                                    int pred = pa <= pb && pa <= pc ? left : (pb <= pc ? up : upLeft);
                                    value = raw + pred;
                                    break;
                                }
                            default: value = raw; break;
                        }
                        output[dst + i] = (byte)value;
                    }
                    Array.Copy(output, dst, prev, 0, rowLength);
                }
                return output;
            }

            private static byte[] AsciiHex(byte[] data)
            {
                var ms = new MemoryStream();
                int hi = -1;
                foreach (byte c in data)
                {
                    if (c == '>')
                    {
                        break;
                    }
                    int v = HexValue(c);
                    if (v < 0)
                    {
                        continue;
                    }
                    if (hi < 0)
                    {
                        hi = v;
                    }
                    else
                    {
                        ms.WriteByte((byte)(hi * 16 + v));
                        hi = -1;
                    }
                }
                if (hi >= 0)
                {
                    ms.WriteByte((byte)(hi * 16));
                }
                return ms.ToArray();
            }

            private static byte[] Ascii85(byte[] data)
            {
                var ms = new MemoryStream();
                var group = new int[5];
                int count = 0;
                int i = 0;
                if (data.Length >= 2 && data[0] == '<' && data[1] == '~')
                {
                    i = 2;
                }
                for (; i < data.Length; i++)
                {
                    byte c = data[i];
                    if (c == '~')
                    {
                        break;
                    }
                    if (c == 'z' && count == 0)
                    {
                        ms.Write(new byte[4], 0, 4);
                        continue;
                    }
                    if (c < '!' || c > 'u')
                    {
                        continue;
                    }
                    group[count++] = c - '!';
                    if (count == 5)
                    {
                        WriteGroup(ms, group, 5);
                        count = 0;
                    }
                }
                if (count > 0)
                {
                    for (int k = count; k < 5; k++)
                    {
                        group[k] = 84;
                    }
                    WriteGroup(ms, group, count);
                }
                return ms.ToArray();
            }

            private static void WriteGroup(MemoryStream ms, int[] group, int count)
            {
                uint value = 0;
                for (int k = 0; k < 5; k++)
                {
                    value = value * 85 + (uint)group[k];
                }
                var bytes = new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
                ms.Write(bytes, 0, count - 1);
            }

            private static byte[] RunLength(byte[] data)
            {
                var ms = new MemoryStream();
                int i = 0;
                while (i < data.Length)
                {
                    int len = data[i++];
                    if (len == 128)
                    {
                        break;
                    }
                    if (len < 128)
                    {
                        int n = Math.Min(len + 1, data.Length - i);
                        ms.Write(data, i, n);
                        i += n;
                    }
                    else if (i < data.Length)
                    {
                        byte b = data[i++];
                        for (int k = 0; k < 257 - len; k++)
                        {
                            ms.WriteByte(b);
                        }
                    }
                }
                return ms.ToArray();
            }

            private static byte[] Lzw(byte[] data, Dict parms, Document doc)
            {
                int early = 1;
                if (parms != null && parms.ContainsKey("EarlyChange"))
                {
                    early = (int)Document.ToDouble(doc.Resolve(parms.Get("EarlyChange")));
                }
                var ms = new MemoryStream();
                var table = new List<byte[]>();
                Action reset = delegate
                {
                    table.Clear();
                    for (int k = 0; k < 256; k++)
                    {
                        table.Add(new[] { (byte)k });
                    }
                    table.Add(null);
                    table.Add(null);
                };
                reset();
                int codeLength = 9;
                byte[] previous = null;
                long bitBuffer = 0;
                int bitCount = 0;
                int pos = 0;
                while (true)
                {
                    while (bitCount < codeLength && pos < data.Length)
                    {
                        bitBuffer = (bitBuffer << 8) | data[pos++];
                        bitCount += 8;
                    }
                    if (bitCount < codeLength)
                    {
                        break;
                    }
                    int code = (int)((bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1));
                    bitCount -= codeLength;
                    if (code == 256)
                    {
                        reset();
                        codeLength = 9;
                        previous = null;
                        continue;
                    }
                    if (code == 257)
                    {
                        break;
                    }
                    byte[] entry;
                    if (code < table.Count && table[code] != null)
                    {
                        entry = table[code];
                        if (previous != null)
                        {
                            var added = new byte[previous.Length + 1];
                            Array.Copy(previous, added, previous.Length);
                            added[previous.Length] = entry[0];
                            table.Add(added);
                        }
                    }
                    else if (previous != null)
                    {
                        entry = new byte[previous.Length + 1];
                        Array.Copy(previous, entry, previous.Length);
                        entry[previous.Length] = previous[0];
                        table.Add(entry);
                    }
                    else
                    {
                        break;
                    }
                    ms.Write(entry, 0, entry.Length);
                    previous = entry;
                    if (table.Count + early - 1 >= (1 << codeLength) && codeLength < 12)
                    {
                        codeLength++;
                    }
                }
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------
        // Fonts: code -> text
        // ------------------------------------------------------------------

        private sealed class Font
        {
            public bool TwoByte;
            public Dictionary<int, string> ToUnicode;
            public string[] SimpleEncoding;
            /// <summary>Glyph advance per code in 1/1000 text-space units (from /Widths or /W); missing codes use DefaultWidth.</summary>
            public Dictionary<int, double> Widths;
            public double DefaultWidth = 500;

            public static Font Build(Document doc, Dict dict)
            {
                var font = new Font();
                var subtype = doc.Resolve(dict.Get("Subtype")) as Name;
                font.TwoByte = subtype != null && subtype.Value == "Type0";
                var toUnicode = doc.Resolve(dict.Get("ToUnicode")) as Stream;
                if (toUnicode != null)
                {
                    byte[] cmap = toUnicode.Decoded(doc);
                    if (cmap != null)
                    {
                        int codeBytes;
                        font.ToUnicode = ParseCMap(cmap, out codeBytes);
                        if (codeBytes == 2)
                        {
                            font.TwoByte = true;
                        }
                        else if (codeBytes == 1 && !font.TwoByte)
                        {
                            font.TwoByte = false;
                        }
                    }
                }
                if (!font.TwoByte)
                {
                    font.SimpleEncoding = BuildSimpleEncoding(doc, dict);
                }
                font.LoadWidths(doc, dict);
                return font;
            }

            private void LoadWidths(Document doc, Dict dict)
            {
                Widths = new Dictionary<int, double>();
                var descendants = doc.Resolve(dict.Get("DescendantFonts")) as List<object>;
                if (descendants != null && descendants.Count > 0)
                {
                    Dict cid = doc.ResolveDict(descendants[0]);
                    if (cid != null)
                    {
                        DefaultWidth = cid.ContainsKey("DW") ? Document.ToDouble(doc.Resolve(cid.Get("DW"))) : 1000;
                        var w = doc.Resolve(cid.Get("W")) as List<object>;
                        if (w != null)
                        {
                            int i = 0;
                            while (i < w.Count)
                            {
                                object a = doc.Resolve(w[i]);
                                if (!(a is double))
                                {
                                    i++;
                                    continue;
                                }
                                int first = (int)(double)a;
                                if (i + 1 < w.Count)
                                {
                                    object b = doc.Resolve(w[i + 1]);
                                    var list = b as List<object>;
                                    if (list != null)
                                    {
                                        for (int k = 0; k < list.Count; k++)
                                        {
                                            object wk = doc.Resolve(list[k]);
                                            if (wk is double)
                                            {
                                                Widths[first + k] = (double)wk;
                                            }
                                        }
                                        i += 2;
                                        continue;
                                    }
                                    if (b is double && i + 2 < w.Count)
                                    {
                                        object c = doc.Resolve(w[i + 2]);
                                        int last = (int)(double)b;
                                        if (c is double && last >= first && last - first < 65536)
                                        {
                                            for (int code = first; code <= last; code++)
                                            {
                                                Widths[code] = (double)c;
                                            }
                                        }
                                        i += 3;
                                        continue;
                                    }
                                }
                                i++;
                            }
                        }
                    }
                    return;
                }
                var widths = doc.Resolve(dict.Get("Widths")) as List<object>;
                if (widths != null && widths.Count > 0)
                {
                    int firstChar = (int)Document.ToDouble(doc.Resolve(dict.Get("FirstChar")));
                    for (int k = 0; k < widths.Count; k++)
                    {
                        object wk = doc.Resolve(widths[k]);
                        if (wk is double && (double)wk > 0)
                        {
                            Widths[firstChar + k] = (double)wk;
                        }
                    }
                    var descriptor = doc.ResolveDict(dict.Get("FontDescriptor"));
                    if (descriptor != null && descriptor.ContainsKey("MissingWidth"))
                    {
                        DefaultWidth = Document.ToDouble(doc.Resolve(descriptor.Get("MissingWidth")));
                    }
                    else
                    {
                        DefaultWidth = 0;
                    }
                }
                else
                {
                    // Standard-14 font without /Widths: an average glyph.
                    DefaultWidth = 500;
                }
            }

            /// <summary>
            /// Appends the text for <paramref name="bytes"/> and returns the
            /// advance in 1/1000 text-space units plus the number of glyphs
            /// and of single-byte spaces (for Tc / Tw).
            /// </summary>
            public double Decode(byte[] bytes, StringBuilder into, out int glyphs, out int spaces)
            {
                glyphs = 0;
                spaces = 0;
                double advance = 0;
                if (bytes == null)
                {
                    return 0;
                }
                int step = TwoByte ? 2 : 1;
                for (int i = 0; i + step <= bytes.Length; i += step)
                {
                    int code = step == 2 ? (bytes[i] << 8) | bytes[i + 1] : bytes[i];
                    glyphs++;
                    if (step == 1 && code == 32)
                    {
                        spaces++;
                    }
                    double w;
                    advance += Widths != null && Widths.TryGetValue(code, out w) ? w : DefaultWidth;
                    string mapped;
                    if (ToUnicode != null && ToUnicode.TryGetValue(code, out mapped))
                    {
                        into.Append(mapped);
                        continue;
                    }
                    if (SimpleEncoding != null && code < SimpleEncoding.Length && SimpleEncoding[code] != null)
                    {
                        into.Append(SimpleEncoding[code]);
                        continue;
                    }
                    if (!TwoByte && ToUnicode == null && code >= 0x20 && code < 0x7F)
                    {
                        into.Append((char)code);
                    }
                    // Unmapped code: dropped rather than guessed.
                }
                return advance;
            }

            private static Dictionary<int, string> ParseCMap(byte[] cmap, out int codeBytes)
            {
                var map = new Dictionary<int, string>();
                codeBytes = 0;
                var lexer = new Lexer(cmap, 0, cmap.Length);
                var stack = new List<object>();
                while (true)
                {
                    object tok = lexer.Next(false);
                    if (tok == EndOfInput)
                    {
                        break;
                    }
                    var op = tok as Operator;
                    if (op == null)
                    {
                        stack.Add(tok);
                        if (stack.Count > 64)
                        {
                            stack.RemoveAt(0);
                        }
                        continue;
                    }
                    switch (op.Value)
                    {
                        case "begincodespacerange":
                            {
                                object lo = lexer.Next(false);
                                var s = lo as PdfString;
                                if (s != null && codeBytes == 0)
                                {
                                    codeBytes = s.Bytes.Length >= 2 ? 2 : 1;
                                }
                                SkipUntil(lexer, "endcodespacerange");
                                break;
                            }
                        case "beginbfchar":
                            while (true)
                            {
                                object src = lexer.Next(false);
                                if (src == EndOfInput || src is Operator)
                                {
                                    break;
                                }
                                object dst = lexer.Next(false);
                                if (dst == EndOfInput || dst is Operator)
                                {
                                    break;
                                }
                                var sb = src as PdfString;
                                if (sb != null)
                                {
                                    if (codeBytes == 0) codeBytes = sb.Bytes.Length >= 2 ? 2 : 1;
                                    map[CodeOf(sb.Bytes)] = Utf16Of(dst);
                                }
                            }
                            break;
                        case "beginbfrange":
                            while (true)
                            {
                                object lo = lexer.Next(false);
                                if (lo == EndOfInput || lo is Operator)
                                {
                                    break;
                                }
                                object hi = lexer.Next(false);
                                object dst = lexer.Next(false);
                                if (hi == EndOfInput || dst == EndOfInput || hi is Operator || dst is Operator)
                                {
                                    break;
                                }
                                var lob = lo as PdfString;
                                var hib = hi as PdfString;
                                if (lob == null || hib == null)
                                {
                                    continue;
                                }
                                if (codeBytes == 0) codeBytes = lob.Bytes.Length >= 2 ? 2 : 1;
                                int from = CodeOf(lob.Bytes);
                                int to = Math.Min(CodeOf(hib.Bytes), from + 65535);
                                var dstList = dst as List<object>;
                                if (dstList != null)
                                {
                                    for (int c = from, k = 0; c <= to && k < dstList.Count; c++, k++)
                                    {
                                        map[c] = Utf16Of(dstList[k]);
                                    }
                                }
                                else
                                {
                                    string baseText = Utf16Of(dst);
                                    for (int c = from; c <= to; c++)
                                    {
                                        map[c] = Increment(baseText, c - from);
                                    }
                                }
                            }
                            break;
                    }
                }
                return map;
            }

            private static void SkipUntil(Lexer lexer, string keyword)
            {
                while (true)
                {
                    object tok = lexer.Next(false);
                    if (tok == EndOfInput)
                    {
                        return;
                    }
                    var op = tok as Operator;
                    if (op != null && op.Value == keyword)
                    {
                        return;
                    }
                }
            }

            private static int CodeOf(byte[] b)
            {
                int code = 0;
                for (int i = 0; i < b.Length && i < 4; i++)
                {
                    code = (code << 8) | b[i];
                }
                return code;
            }

            private static string Utf16Of(object dst)
            {
                var s = dst as PdfString;
                if (s != null)
                {
                    byte[] b = s.Bytes;
                    if (b.Length >= 2 && b.Length % 2 == 0)
                    {
                        var chars = new char[b.Length / 2];
                        for (int i = 0; i < chars.Length; i++)
                        {
                            chars[i] = (char)((b[2 * i] << 8) | b[2 * i + 1]);
                        }
                        return new string(chars);
                    }
                    if (b.Length == 1)
                    {
                        return ((char)b[0]).ToString();
                    }
                    return string.Empty;
                }
                var n = dst as Name;
                if (n != null)
                {
                    return GlyphNames.Lookup(n.Value) ?? string.Empty;
                }
                return string.Empty;
            }

            private static string Increment(string baseText, int by)
            {
                if (by == 0 || baseText.Length == 0)
                {
                    return baseText;
                }
                char last = baseText[baseText.Length - 1];
                int next = last + by;
                if (next > 0xFFFF)
                {
                    return baseText;
                }
                return baseText.Substring(0, baseText.Length - 1) + (char)next;
            }


            /// <summary>Mac OS Roman, codes 0x80-0xFF, as code points (0xF0 is the Apple logo, mapped to nothing).</summary>
            private static readonly int[] MacRomanHigh =
            {
                196, 197, 199, 201, 209, 214, 220, 225, 224, 226, 228, 227, 229, 231, 233, 232,
                234, 235, 237, 236, 238, 239, 241, 243, 242, 244, 246, 245, 250, 249, 251, 252,
                8224, 176, 162, 163, 167, 8226, 182, 223, 174, 169, 8482, 180, 168, 8800, 198, 216,
                8734, 177, 8804, 8805, 165, 181, 8706, 8721, 8719, 960, 8747, 170, 186, 937, 230, 248,
                191, 161, 172, 8730, 402, 8776, 8710, 171, 187, 8230, 160, 192, 195, 213, 338, 339,
                8211, 8212, 8220, 8221, 8216, 8217, 247, 9674, 255, 376, 8260, 8364, 8249, 8250, 64257, 64258,
                8225, 183, 8218, 8222, 8240, 194, 202, 193, 203, 200, 205, 206, 207, 204, 211, 212,
                63743, 210, 218, 219, 217, 305, 710, 732, 175, 728, 729, 730, 184, 733, 731, 711,
            };

            private static void ApplyMacRomanHighRange(string[] table)
            {
                for (int c = 0x80; c <= 0xFF; c++)
                {
                    int cp = MacRomanHigh[c - 0x80];
                    table[c] = cp == 63743 ? string.Empty : Cp(cp);
                }
                table[0x27] = "'";
                table[0x60] = "`";
            }

            private static string[] BuildSimpleEncoding(Document doc, Dict dict)
            {
                var table = new string[256];
                object encoding = doc.Resolve(dict.Get("Encoding"));
                var encName = encoding as Name;
                var encDict = encoding as Dict;
                string baseName = encName != null ? encName.Value : null;
                if (encDict != null)
                {
                    var b = doc.Resolve(encDict.Get("BaseEncoding")) as Name;
                    if (b != null)
                    {
                        baseName = b.Value;
                    }
                }
                bool symbolic = false;
                var descriptor = doc.ResolveDict(dict.Get("FontDescriptor"));
                if (descriptor != null)
                {
                    int flags = (int)Document.ToDouble(doc.Resolve(descriptor.Get("Flags")));
                    symbolic = (flags & 4) != 0 && (flags & 32) == 0;
                }
                if (baseName == "WinAnsiEncoding" || baseName == "MacRomanEncoding" || baseName == "StandardEncoding" || (!symbolic && baseName == null) || encDict != null)
                {
                    for (int c = 0x20; c < 0x7F; c++)
                    {
                        table[c] = ((char)c).ToString();
                    }
                    table[0x27] = "'";
                    table[0x60] = "`";
                    if (baseName == "StandardEncoding")
                    {
                        table[0x27] = Cp(0x2019);
                        table[0x60] = Cp(0x2018);
                    }
                    for (int c = 0xA0; c <= 0xFF; c++)
                    {
                        table[c] = ((char)c).ToString();
                    }
                    if (baseName != "StandardEncoding")
                    {
                        table[0x80] = Cp(0x20AC); table[0x82] = Cp(0x201A); table[0x83] = Cp(0x0192); table[0x84] = Cp(0x201E);
                        table[0x85] = Cp(0x2026); table[0x86] = Cp(0x2020); table[0x87] = Cp(0x2021); table[0x88] = Cp(0x02C6);
                        table[0x89] = Cp(0x2030); table[0x8A] = Cp(0x0160); table[0x8B] = Cp(0x2039); table[0x8C] = Cp(0x0152);
                        table[0x8E] = Cp(0x017D); table[0x91] = Cp(0x2018); table[0x92] = Cp(0x2019); table[0x93] = Cp(0x201C);
                        table[0x94] = Cp(0x201D); table[0x95] = Cp(0x2022); table[0x96] = Cp(0x2013); table[0x97] = Cp(0x2014);
                        table[0x98] = Cp(0x02DC); table[0x99] = Cp(0x2122); table[0x9A] = Cp(0x0161); table[0x9B] = Cp(0x203A);
                        table[0x9C] = Cp(0x0153); table[0x9E] = Cp(0x017E); table[0x9F] = Cp(0x0178);
                        table[0xA0] = " "; table[0xAD] = "-";
                    }
                }
                else
                {
                    // Symbolic font with a built-in encoding: ASCII is a safe
                    // guess; a symbolic TrueType font most often carries a
                    // Mac Roman cmap, so its high range is read that way.
                    for (int c = 0x20; c < 0x7F; c++)
                    {
                        table[c] = ((char)c).ToString();
                    }
                    var subtype = doc.Resolve(dict.Get("Subtype")) as Name;
                    if (subtype != null && subtype.Value == "TrueType")
                    {
                        ApplyMacRomanHighRange(table);
                    }
                }
                if (baseName == "MacRomanEncoding")
                {
                    ApplyMacRomanHighRange(table);
                }
                if (encDict != null)
                {
                    var differences = doc.Resolve(encDict.Get("Differences")) as List<object>;
                    if (differences != null)
                    {
                        int code = 0;
                        foreach (object item in differences)
                        {
                            object v = doc.Resolve(item);
                            if (v is double)
                            {
                                code = (int)(double)v;
                                continue;
                            }
                            var n = v as Name;
                            if (n != null && code >= 0 && code < 256)
                            {
                                string text = GlyphNames.Lookup(n.Value);
                                if (text != null)
                                {
                                    table[code] = text;
                                }
                                code++;
                            }
                        }
                    }
                }
                return table;
            }
        }

        /// <summary>Adobe Glyph List subset plus uniXXXX / uXXXX[X] names and composed accents.</summary>
        private static class GlyphNames
        {
            private static readonly Dictionary<string, string> Table = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "space", " " }, { "exclam", "!" }, { "quotedbl", "\"" }, { "numbersign", "#" }, { "dollar", "$" },
                { "percent", "%" }, { "ampersand", "&" }, { "quotesingle", "'" }, { "quoteright", Cp(0x2019) },
                { "quoteleft", Cp(0x2018) }, { "parenleft", "(" }, { "parenright", ")" }, { "asterisk", "*" }, { "plus", "+" },
                { "comma", "," }, { "hyphen", "-" }, { "period", "." }, { "slash", "/" }, { "zero", "0" }, { "one", "1" },
                { "two", "2" }, { "three", "3" }, { "four", "4" }, { "five", "5" }, { "six", "6" }, { "seven", "7" },
                { "eight", "8" }, { "nine", "9" }, { "colon", ":" }, { "semicolon", ";" }, { "less", "<" }, { "equal", "=" },
                { "greater", ">" }, { "question", "?" }, { "at", "@" }, { "bracketleft", "[" }, { "backslash", "\\" },
                { "bracketright", "]" }, { "asciicircum", "^" }, { "underscore", "_" }, { "grave", "`" },
                { "braceleft", "{" }, { "bar", "|" }, { "braceright", "}" }, { "asciitilde", "~" },
                { "quotedblleft", Cp(0x201C) }, { "quotedblright", Cp(0x201D) }, { "quotedblbase", Cp(0x201E) }, { "quotesinglbase", Cp(0x201A) },
                { "endash", Cp(0x2013) }, { "emdash", Cp(0x2014) }, { "ellipsis", Cp(0x2026) }, { "bullet", Cp(0x2022) },
                { "fi", "fi" }, { "fl", "fl" }, { "ff", "ff" }, { "ffi", "ffi" }, { "ffl", "ffl" }, { "minus", Cp(0x2212) },
                { "multiply", Cp(0x00D7) }, { "divide", Cp(0x00F7) }, { "degree", Cp(0x00B0) }, { "copyright", Cp(0x00A9) },
                { "registered", Cp(0x00AE) }, { "trademark", Cp(0x2122) }, { "section", Cp(0x00A7) }, { "paragraph", Cp(0x00B6) },
                { "dagger", Cp(0x2020) }, { "daggerdbl", Cp(0x2021) }, { "periodcentered", Cp(0x00B7) }, { "guillemotleft", Cp(0x00AB) },
                { "guillemotright", Cp(0x00BB) }, { "guilsinglleft", Cp(0x2039) }, { "guilsinglright", Cp(0x203A) },
                { "exclamdown", Cp(0x00A1) }, { "questiondown", Cp(0x00BF) }, { "plusminus", Cp(0x00B1) }, { "sterling", Cp(0x00A3) },
                { "yen", Cp(0x00A5) }, { "Euro", Cp(0x20AC) }, { "cent", Cp(0x00A2) }, { "currency", Cp(0x00A4) }, { "AE", Cp(0x00C6) },
                { "ae", Cp(0x00E6) }, { "OE", Cp(0x0152) }, { "oe", Cp(0x0153) }, { "Oslash", Cp(0x00D8) }, { "oslash", Cp(0x00F8) },
                { "germandbls", Cp(0x00DF) }, { "dotlessi", Cp(0x0131) }, { "Lslash", Cp(0x0141) }, { "lslash", Cp(0x0142) },
                { "Eth", Cp(0x00D0) }, { "eth", Cp(0x00F0) }, { "Thorn", Cp(0x00DE) }, { "thorn", Cp(0x00FE) }, { "mu", Cp(0x00B5) },
                { "onesuperior", Cp(0x00B9) }, { "twosuperior", Cp(0x00B2) }, { "threesuperior", Cp(0x00B3) }, { "onequarter", Cp(0x00BC) },
                { "onehalf", Cp(0x00BD) }, { "threequarters", Cp(0x00BE) }, { "ordfeminine", Cp(0x00AA) }, { "ordmasculine", Cp(0x00BA) },
                { "logicalnot", Cp(0x00AC) }, { "brokenbar", Cp(0x00A6) }, { "macron", Cp(0x00AF) }, { "acute", Cp(0x00B4) },
                { "cedilla", Cp(0x00B8) }, { "dieresis", Cp(0x00A8) }, { "circumflex", Cp(0x02C6) }, { "tilde", Cp(0x02DC) },
                { "caron", Cp(0x02C7) }, { "breve", Cp(0x02D8) }, { "dotaccent", Cp(0x02D9) }, { "ring", Cp(0x02DA) },
                { "ogonek", Cp(0x02DB) }, { "hungarumlaut", Cp(0x02DD) }, { "florin", Cp(0x0192) }, { "perthousand", Cp(0x2030) },
                { "fraction", Cp(0x2044) }, { "arrowright", Cp(0x2192) }, { "arrowleft", Cp(0x2190) }, { "arrowup", Cp(0x2191) },
                { "arrowdown", Cp(0x2193) }, { "lessequal", Cp(0x2264) }, { "greaterequal", Cp(0x2265) }, { "notequal", Cp(0x2260) },
                { "infinity", Cp(0x221E) }, { "summation", Cp(0x2211) }, { "product", Cp(0x220F) }, { "radical", Cp(0x221A) },
                { "partialdiff", Cp(0x2202) }, { "Delta", Cp(0x0394) }, { "Omega", Cp(0x03A9) }, { "pi", Cp(0x03C0) }, { "alpha", Cp(0x03B1) },
                { "beta", Cp(0x03B2) }, { "gamma", Cp(0x03B3) }, { "delta", Cp(0x03B4) }, { "epsilon", Cp(0x03B5) }, { "theta", Cp(0x03B8) },
                { "lambda", Cp(0x03BB) }, { "sigma", Cp(0x03C3) }, { "tau", Cp(0x03C4) }, { "phi", Cp(0x03C6) }, { "omega", Cp(0x03C9) },
                { "nbspace", " " }, { "sfthyphen", "-" }, { "hyphenminus", "-" }, { "softhyphen", "-" }, { "middot", Cp(0x00B7) },
                { "checkmark", Cp(0x2713) }, { "heart", Cp(0x2665) }, { "star", Cp(0x2605) }, { "Scaron", Cp(0x0160) }, { "scaron", Cp(0x0161) },
                { "Zcaron", Cp(0x017D) }, { "zcaron", Cp(0x017E) }, { "Ydieresis", Cp(0x0178) }, { "ydieresis", Cp(0x00FF) },
            };

            private static readonly Dictionary<string, string> Accents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "acute", Cp(0x0301) }, { "grave", Cp(0x0300) }, { "circumflex", Cp(0x0302) }, { "tilde", Cp(0x0303) },
                { "dieresis", Cp(0x0308) }, { "ring", Cp(0x030A) }, { "cedilla", Cp(0x0327) }, { "caron", Cp(0x030C) },
                { "macron", Cp(0x0304) }, { "breve", Cp(0x0306) }, { "ogonek", Cp(0x0328) }, { "dotaccent", Cp(0x0307) },
                { "hungarumlaut", Cp(0x030B) }, { "slash", Cp(0x0338) },
            };

            public static string Lookup(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                string direct;
                if (Table.TryGetValue(name, out direct))
                {
                    return direct;
                }
                if (name.Length == 1)
                {
                    return name;
                }
                if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7)
                {
                    var sb = new StringBuilder();
                    for (int i = 3; i + 4 <= name.Length; i += 4)
                    {
                        int code;
                        if (!int.TryParse(name.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            return null;
                        }
                        sb.Append((char)code);
                    }
                    return sb.ToString();
                }
                if (name.Length >= 5 && name.Length <= 7 && name[0] == 'u')
                {
                    int code;
                    if (int.TryParse(name.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) && code <= 0x10FFFF)
                    {
                        return char.ConvertFromUtf32(code);
                    }
                }
                // "Eacute", "ccedilla", "Ntilde"...: base letter + accent name.
                if (name.Length > 1 && char.IsLetter(name[0]))
                {
                    string accentMark;
                    string rest = name.Substring(1);
                    if (Accents.TryGetValue(rest, out accentMark))
                    {
                        string composed = (name[0].ToString() + accentMark).Normalize(NormalizationForm.FormC);
                        return composed;
                    }
                }
                int dot = name.IndexOf('.');
                if (dot > 0)
                {
                    // "a.sc", "one.oldstyle": variant of the base glyph.
                    return Lookup(name.Substring(0, dot));
                }
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Content stream interpreter
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the text operators of one page and turns positions into
        /// line breaks and word gaps. The pen position is tracked in user
        /// space from Tm / Td / TD / T* and advanced by glyph widths
        /// (Font.Decode) so a Td that merely continues the current word
        /// (kerned runs) inserts nothing, while a jump wider than about an
        /// eighth of the font size becomes a space and a vertical move of
        /// about half the font size or more becomes a line break.
        /// Sub/superscript shifts stay on the line.
        /// </summary>
        private sealed class ContentInterpreter
        {
            private readonly Document _doc;
            private readonly Dict _fonts;
            private readonly StringBuilder _out = new StringBuilder();
            private Font _font;
            private double _fontSize = 1;
            private double _hScale = 1;
            private double _vScale = 1;
            private double _hzScale = 1;
            private double _charSpacing;
            private double _wordSpacing;
            private double _lineX;
            private double _curX;
            private double _y = double.NaN;
            private bool _positioned;

            public ContentInterpreter(Document doc, Dict fonts)
            {
                _doc = doc;
                _fonts = fonts;
            }

            public string Run(byte[] content)
            {
                var lexer = new Lexer(content, 0, content.Length);
                var operands = new List<object>();
                int guard = 0;
                while (guard++ < 5000000)
                {
                    object tok = lexer.Next(false);
                    if (tok == EndOfInput)
                    {
                        break;
                    }
                    var op = tok as Operator;
                    if (op == null)
                    {
                        operands.Add(tok);
                        if (operands.Count > 64)
                        {
                            operands.RemoveAt(0);
                        }
                        continue;
                    }
                    try
                    {
                        Apply(op.Value, operands, lexer);
                    }
                    catch (Exception)
                    {
                    }
                    operands.Clear();
                }
                return _out.ToString();
            }

            private static double Num(List<object> operands, int fromEnd)
            {
                int index = operands.Count - fromEnd;
                return index >= 0 && operands[index] is double ? (double)operands[index] : 0;
            }

            private static bool HasNums(List<object> operands, int count)
            {
                if (operands.Count < count)
                {
                    return false;
                }
                for (int i = 1; i <= count; i++)
                {
                    if (!(operands[operands.Count - i] is double))
                    {
                        return false;
                    }
                }
                return true;
            }

            private double GlyphUnit
            {
                get { return Math.Max(0.5, _fontSize * _hScale); }
            }

            private void Apply(string op, List<object> operands, Lexer lexer)
            {
                switch (op)
                {
                    case "BT":
                        _lineX = 0;
                        _curX = 0;
                        _positioned = false;
                        break;
                    case "ET":
                        break;
                    case "Tf":
                        {
                            var name = operands.Count >= 2 ? operands[operands.Count - 2] as Name : null;
                            if (name != null)
                            {
                                _font = _doc.GetFont(_fonts, name.Value);
                            }
                            if (HasNums(operands, 1))
                            {
                                _fontSize = Math.Abs(Num(operands, 1));
                                if (_fontSize == 0)
                                {
                                    _fontSize = 1;
                                }
                            }
                            break;
                        }
                    case "Tc":
                        _charSpacing = Num(operands, 1);
                        break;
                    case "Tw":
                        _wordSpacing = Num(operands, 1);
                        break;
                    case "Tz":
                        _hzScale = HasNums(operands, 1) && Num(operands, 1) != 0 ? Num(operands, 1) / 100.0 : 1;
                        break;
                    case "Td":
                    case "TD":
                        if (HasNums(operands, 2))
                        {
                            double newX = _lineX + Num(operands, 2) * _hScale;
                            double newY = (double.IsNaN(_y) ? 0 : _y) + Num(operands, 1) * _vScale;
                            MoveTo(newX, newY, false);
                        }
                        break;
                    case "T*":
                        NewLine();
                        _curX = _lineX;
                        break;
                    case "Tm":
                        if (HasNums(operands, 6))
                        {
                            double a = Num(operands, 6), b = Num(operands, 5), c = Num(operands, 4), d = Num(operands, 3);
                            _hScale = Math.Max(0.01, Math.Sqrt(a * a + b * b));
                            _vScale = Math.Max(0.01, Math.Sqrt(c * c + d * d));
                            MoveTo(Num(operands, 2), Num(operands, 1), true);
                        }
                        break;
                    case "Tj":
                        Show(operands.Count >= 1 ? operands[operands.Count - 1] as PdfString : null);
                        break;
                    case "\'":
                        NewLine();
                        _curX = _lineX;
                        Show(operands.Count >= 1 ? operands[operands.Count - 1] as PdfString : null);
                        break;
                    case "\"":
                        if (HasNums(operands, 3) || operands.Count >= 3)
                        {
                            _wordSpacing = Num(operands, 3);
                            _charSpacing = Num(operands, 2);
                        }
                        NewLine();
                        _curX = _lineX;
                        Show(operands.Count >= 1 ? operands[operands.Count - 1] as PdfString : null);
                        break;
                    case "TJ":
                        {
                            var array = operands.Count >= 1 ? operands[operands.Count - 1] as List<object> : null;
                            if (array == null)
                            {
                                break;
                            }
                            foreach (object item in array)
                            {
                                var s = item as PdfString;
                                if (s != null)
                                {
                                    Show(s);
                                }
                                else if (item is double)
                                {
                                    double shift = -(double)item / 1000.0 * _fontSize * _hScale * _hzScale;
                                    if (shift > GlyphUnit * 0.12)
                                    {
                                        Space();
                                    }
                                    _curX += shift;
                                }
                            }
                            break;
                        }
                    case "BI":
                        {
                            byte[] bytes = lexer.Bytes;
                            int i = lexer.Pos;
                            while (i + 1 < lexer.End)
                            {
                                if (bytes[i] == 'E' && bytes[i + 1] == 'I' && (i == 0 || bytes[i - 1] == ' ' || bytes[i - 1] == '\n' || bytes[i - 1] == '\r')
                                    && (i + 2 >= lexer.End || bytes[i + 2] == ' ' || bytes[i + 2] == '\n' || bytes[i + 2] == '\r' || bytes[i + 2] == '\t'))
                                {
                                    i += 2;
                                    break;
                                }
                                i++;
                            }
                            lexer.Pos = i;
                            break;
                        }
                }
            }

            /// <summary>New line start at (x, y) in user space: decides line break / word gap against the pen.</summary>
            private void MoveTo(double x, double y, bool absolute)
            {
                if (_positioned && !double.IsNaN(_y))
                {
                    double dy = Math.Abs(y - _y);
                    if (dy > Math.Max(1.0, _fontSize * _vScale * 0.45))
                    {
                        NewLine();
                    }
                    else if (x - _curX > GlyphUnit * 0.12)
                    {
                        Space();
                    }
                    else if (_curX - x > GlyphUnit * 2)
                    {
                        // Far back on the same baseline: a new column or a
                        // table cell; keep it apart with a space.
                        Space();
                    }
                }
                else if (!double.IsNaN(_y) && Math.Abs(y - _y) > Math.Max(1.0, _fontSize * _vScale * 0.45))
                {
                    // First positioning after BT, on a different line than
                    // the previous block ended.
                    NewLine();
                }
                _lineX = x;
                _curX = x;
                _y = y;
                _positioned = true;
            }

            private void Show(PdfString s)
            {
                if (s == null)
                {
                    return;
                }
                int glyphs = 0;
                int spaces = 0;
                double advance;
                if (_font == null)
                {
                    // No Tf seen (or an unresolvable font): assume ASCII.
                    foreach (byte b in s.Bytes)
                    {
                        if (b >= 0x20 && b < 0x7F)
                        {
                            _out.Append((char)b);
                        }
                        glyphs++;
                    }
                    advance = glyphs * 500;
                }
                else
                {
                    advance = _font.Decode(s.Bytes, _out, out glyphs, out spaces);
                }
                double width = (advance / 1000.0 * _fontSize + glyphs * _charSpacing + spaces * _wordSpacing) * _hScale * _hzScale;
                _curX += width;
                _positioned = true;
            }

            private void Space()
            {
                if (_out.Length > 0 && _out[_out.Length - 1] != ' ' && _out[_out.Length - 1] != '\n')
                {
                    _out.Append(' ');
                }
            }

            private void NewLine()
            {
                if (_out.Length > 0 && _out[_out.Length - 1] != '\n')
                {
                    _out.Append('\n');
                }
            }
        }
    }
}
