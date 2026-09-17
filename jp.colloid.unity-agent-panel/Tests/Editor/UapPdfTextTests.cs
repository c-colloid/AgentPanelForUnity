using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The dependency-free PDF text extractor behind uap_web_fetch's
    /// as:"text" / pages (design note 2026-09-17-web-fetch-tool.md section
    /// 4.3, stage 2), on PDFs assembled here byte by byte: uncompressed,
    /// FlateDecode with the page objects inside an object stream, a Type0
    /// font with a ToUnicode CMap, /Differences glyph names, inherited
    /// resources, page ranges, and damaged input.
    /// </summary>
    [TestFixture]
    public class UapPdfTextTests
    {
        // -- builders ----------------------------------------------------------

        private static byte[] Latin1(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                b[i] = (byte)s[i];
            }
            return b;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (byte[] p in parts)
            {
                ms.Write(p, 0, p.Length);
            }
            return ms.ToArray();
        }

        private static byte[] Obj(int num, string body)
        {
            return Latin1(num + " 0 obj\n" + body + "\nendobj\n");
        }

        private static byte[] StreamObj(int num, string dictEntries, byte[] data)
        {
            return Concat(Latin1(num + " 0 obj\n<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n"),
                data, Latin1("\nendstream\nendobj\n"));
        }

        /// <summary>zlib-wrapped deflate, the way FlateDecode streams are written.</summary>
        private static byte[] Zlib(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }
                // No Adler-32 trailer: the decoder ignores it.
                return ms.ToArray();
            }
        }

        private static byte[] Pdf(params byte[][] objects)
        {
            var parts = new List<byte[]> { Latin1("%PDF-1.5\n%âã\n") };
            parts.AddRange(objects);
            parts.Add(Latin1("trailer\n<< /Root 1 0 R >>\n%%EOF\n"));
            return Concat(parts.ToArray());
        }

        private static byte[] SimplePdf(string content, string fontExtra = "")
        {
            return Pdf(
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj(3, "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
                Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding " + fontExtra + " >>"),
                StreamObj(5, "", Latin1(content)));
        }

        // -- tests ------------------------------------------------------------------

        [Test]
        public void Plain_TjAndTJ_SpacesFromPositions_LinesFromMoves()
        {
            // Default glyph advance is 500/1000 em: "Hello" at 12 pt is 30
            // units wide, so a 36-unit Td leaves a 6-unit gap = a space;
            // the -300 TJ adjustment is 3.6 units = a space; the vertical
            // move is a line break.
            byte[] pdf = SimplePdf("BT /F1 12 Tf 72 700 Td (Hello) Tj 36 0 Td (World) Tj 0 -14 Td [(Second) -300 (line)] TJ ET");
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);

            Assert.AreEqual(1, r.PageCount);
            Assert.AreEqual(1, r.Pages.Count);
            Assert.AreEqual("Hello World\nSecond line", r.Pages[0]);
            Assert.IsEmpty(r.Warnings);
        }

        [Test]
        public void KernedRun_TdEqualToAdvance_NoSpaceInserted()
        {
            // "Dumm" then a Td of exactly its width, then "y": one word.
            byte[] pdf = SimplePdf("BT /F1 16 Tf 56 758 Td (Dumm) Tj 32 0 Td (y) Tj 12 0 Td (PDF) Tj ET");
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual("Dummy PDF", r.Pages[0]);
        }

        [Test]
        public void SuperscriptShift_StaysOnTheLine_LargeShiftBreaks()
        {
            byte[] pdf = SimplePdf("BT /F1 10 Tf 1 0 0 1 72 700 Tm (x) Tj 1 0 0 1 77 703.5 Tm (2) Tj 1 0 0 1 72 688 Tm (next) Tj ET");
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual("x2\nnext", r.Pages[0]);
        }

        [Test]
        public void WinAnsi_HighRange_AndEscapes()
        {
            byte[] pdf = SimplePdf("BT /F1 12 Tf 72 700 Td (caf\\351 \\223quoted\\224 \\(paren\\) \\\\) Tj ET");
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual("café “quoted” (paren) \\", r.Pages[0]);
        }

        [Test]
        public void Differences_GlyphNames_MapCodes()
        {
            byte[] pdf = Pdf(
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj(3, "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
                Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Custom /Encoding << /Type /Encoding"
                    + " /Differences [65 /eacute /fi /uni00DF 200 /quoteright /endash /a.sc] >> >>"),
                StreamObj(5, "", Latin1("BT /F1 12 Tf 72 700 Td <414243C8C9CA> Tj ET")));
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual("éfiß’–a", r.Pages[0]);
        }

        [Test]
        public void Flate_ObjectStream_Type0_ToUnicode()
        {
            // Catalog, pages, page and fonts live INSIDE a compressed object
            // stream; the content stream and the CMap are FlateDecode too.
            string cmap = "/CIDInit /ProcSet findresource begin begincmap\n"
                + "1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
                + "2 beginbfchar <0001> <0048> <0002> <0069> endbfchar\n"
                + "1 beginbfrange <0010> <0012> <0061> endbfrange\n"
                + "1 beginbfrange <0020> <0021> [<0058> <005900200021>] endbfrange\n"
                + "endcmap CMapName currentdict /CMap defineresource pop end end";
            string inner1 = "<< /Type /Catalog /Pages 2 0 R >>";
            string inner2 = "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /Font << /F1 4 0 R >> >> >>";
            string inner3 = "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>";
            string inner4 = "<< /Type /Font /Subtype /Type0 /BaseFont /Any /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 6 0 R >>";
            string inner7 = "<< /Type /Font /Subtype /CIDFontType2 /DW 600 /W [1 [700 300] 16 18 500] >>";
            var offsets = new StringBuilder();
            var bodies = new StringBuilder();
            int[] nums = { 1, 2, 3, 4, 7 };
            string[] inners = { inner1, inner2, inner3, inner4, inner7 };
            for (int i = 0; i < nums.Length; i++)
            {
                offsets.Append(nums[i]).Append(' ').Append(bodies.Length).Append(' ');
                bodies.Append(inners[i]).Append('\n');
            }
            string header = offsets.ToString();
            byte[] objStmData = Latin1(header + bodies);
            // Codes: 0001 0002 -> "Hi"; then a 0.6 em gap via the "W" advance
            // difference is not needed -- an explicit Td makes the space;
            // 0010 0011 0012 -> "abc"; 0020 0021 -> "X" and "Y !".
            string content = "BT /F1 10 Tf 72 700 Td <00010002> Tj 20 0 Td <001000110012> Tj 0 -12 Td <00200021> Tj ET";
            byte[] pdf = Pdf(
                StreamObj(8, "/Type /ObjStm /N " + nums.Length + " /First " + header.Length + " /Filter /FlateDecode", Zlib(objStmData)),
                StreamObj(5, "/Filter /FlateDecode", Zlib(Latin1(content))),
                StreamObj(6, "/Filter /FlateDecode", Zlib(Latin1(cmap))));
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);

            Assert.IsEmpty(r.Warnings);
            Assert.AreEqual(1, r.PageCount, "page tree found through the object stream");
            Assert.AreEqual("Hi abc\nXY !", r.Pages[0]);
        }

        [Test]
        public void Flate_WithPngPredictor_Decodes()
        {
            // Two rows of 4 bytes, PNG "Up" predictor: row 2 stored as deltas.
            byte[] predicted = { 0, 1, 2, 3, 4, 2, 1, 1, 1, 1 };
            byte[] pdf = Pdf(
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>"),
                StreamObj(5, "/Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 >>", Zlib(predicted)));
            // The decoded bytes are not valid content, but decoding must not
            // throw and the page must come back (empty).
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual(1, r.Pages.Count);
            Assert.AreEqual(string.Empty, r.Pages[0]);
        }

        [Test]
        public void AsciiHexAndAscii85Filters()
        {
            string content = "BT /F1 12 Tf 72 700 Td (Hex) Tj ET";
            var hex = new StringBuilder();
            foreach (byte b in Latin1(content))
            {
                hex.Append(b.ToString("X2"));
            }
            hex.Append('>');
            byte[] pdf = Pdf(
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>"),
                Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>"),
                StreamObj(5, "/Filter /ASCIIHexDecode", Latin1(hex.ToString())),
                Obj(6, "<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>"),
                // "BT (A85) Tj ET" in ASCII85 (computed by hand: "BT (A" "85) T" "j ET" -> groups).
                StreamObj(7, "/Filter /ASCII85Decode", Latin1(Ascii85(Latin1("BT (A85) Tj ET")))));
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual(2, r.Pages.Count);
            Assert.AreEqual("Hex", r.Pages[0]);
            Assert.AreEqual("A85", r.Pages[1]);
        }

        private static string Ascii85(byte[] data)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(4, data.Length - i);
                uint value = 0;
                for (int k = 0; k < 4; k++)
                {
                    value = (value << 8) | (k < n ? data[i + k] : (byte)0);
                }
                var chars = new char[5];
                for (int k = 4; k >= 0; k--)
                {
                    chars[k] = (char)('!' + value % 85);
                    value /= 85;
                }
                sb.Append(chars, 0, n + 1);
                i += n;
            }
            return sb.Append("~>").ToString();
        }

        [Test]
        public void PageRange_AndRender()
        {
            byte[] pdf = Pdf(
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>"),
                Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>"),
                Obj(4, "<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>"),
                Obj(5, "<< /Type /Page /Parent 2 0 R >>"),
                StreamObj(6, "", Latin1("BT (one) Tj ET")),
                StreamObj(7, "", Latin1("BT (two) Tj ET")));

            UapPdfText.Result middle = UapPdfText.Extract(pdf, 2, 2);
            Assert.AreEqual(3, middle.PageCount);
            Assert.AreEqual(2, middle.FirstPage);
            Assert.AreEqual(1, middle.Pages.Count);
            Assert.AreEqual("two", middle.Pages[0]);
            string rendered = UapPdfText.Render(middle);
            StringAssert.Contains("--- Page 2 of 3 ---\ntwo", rendered);

            UapPdfText.Result tail = UapPdfText.Extract(pdf, 3, int.MaxValue);
            Assert.AreEqual("(no extractable text on this page)", UapPdfText.Render(tail).Split('\n')[1]);

            UapPdfText.Result beyond = UapPdfText.Extract(pdf, 7, 9);
            Assert.AreEqual(0, beyond.Pages.Count);
            StringAssert.Contains("no pages in the requested range; the document has 3 pages", UapPdfText.Render(beyond));
        }

        [TestCase("", 1, int.MaxValue)]
        [TestCase("3", 3, 3)]
        [TestCase("1-5", 1, 5)]
        [TestCase("7-", 7, int.MaxValue)]
        [TestCase("-4", 1, 4)]
        [TestCase(" 2 - 3 ", 2, 3)]
        public void TryParsePageRange_Accepts(string spec, int first, int last)
        {
            int f, l;
            string error;
            Assert.IsTrue(UapPdfText.TryParsePageRange(spec, out f, out l, out error), error);
            Assert.AreEqual(first, f);
            Assert.AreEqual(last, l);
        }

        [TestCase("abc")]
        [TestCase("0")]
        [TestCase("5-2")]
        [TestCase("1-x")]
        public void TryParsePageRange_Rejects(string spec)
        {
            int f, l;
            string error;
            Assert.IsFalse(UapPdfText.TryParsePageRange(spec, out f, out l, out error));
            StringAssert.Contains("pages", error);
        }

        [Test]
        public void Garbage_AndEmpty_DoNotThrow()
        {
            UapPdfText.Result empty = UapPdfText.Extract(new byte[0], 1, 5);
            Assert.AreEqual(0, empty.PageCount);
            Assert.IsNotEmpty(empty.Warnings);

            UapPdfText.Result junk = UapPdfText.Extract(Latin1("%PDF-1.4 this is not << a real /Type /Page (file stream endstream obj"), 1, 5);
            Assert.AreEqual(0, junk.Pages.Count);

            byte[] truncated = SimplePdf("BT /F1 12 Tf 72 700 Td (Hello) Tj");
            UapPdfText.Result cut = UapPdfText.Extract(truncated, 1, 5);
            Assert.AreEqual("Hello", cut.Pages[0], "an unterminated content stream still yields what it had");
        }

        [Test]
        public void Encrypted_FlaggedAndWarned()
        {
            byte[] pdf = Concat(
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>"),
                StreamObj(5, "", Latin1("BT (secret) Tj ET")),
                Latin1("trailer\n<< /Root 1 0 R /Encrypt 9 0 R >>\n%%EOF\n"));
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.IsTrue(r.Encrypted);
            Assert.AreEqual(1, r.PageCount);
            Assert.AreEqual(string.Empty, r.Pages[0], "no text is guessed out of an encrypted stream");
            StringAssert.Contains("encrypted", string.Join(" ", r.Warnings.ToArray()));
        }

        [Test]
        public void NoPageTree_FallsBackToPageObjectsInOrder()
        {
            byte[] pdf = Pdf(
                Obj(3, "<< /Type /Page /Contents 5 0 R >>"),
                StreamObj(5, "", Latin1("BT (A) Tj ET")),
                Obj(4, "<< /Type /Page /Contents 6 0 R >>"),
                StreamObj(6, "", Latin1("BT (B) Tj ET")));
            UapPdfText.Result r = UapPdfText.Extract(pdf, 1, int.MaxValue);
            Assert.AreEqual(2, r.PageCount);
            Assert.AreEqual("A", r.Pages[0]);
            Assert.AreEqual("B", r.Pages[1]);
        }

        [Test]
        public void Tidy_CollapsesSpacesAndBlankRuns()
        {
            Assert.AreEqual("a b\nc\n\nd", UapPdfText.Tidy("  a   b \n c \n\n\n\n d  "));
            Assert.AreEqual(string.Empty, UapPdfText.Tidy(null));
        }
    }
}
