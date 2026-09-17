using System.Text;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Body-kind routing of uap_web_fetch: magic number beats Content-Type,
    /// Content-Type beats the URL extension, and a text sniff is the last
    /// resort (design note 2026-09-17-web-fetch-tool.md section 4).
    /// </summary>
    [TestFixture]
    public class UapWebContentKindTests
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x08, 0x00, 0, 0, 0x00, 0x10, 8, 6, 0, 0, 0 };
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
            0xFF, 0xC0, 0, 17, 8, 0x01, 0x00, 0x02, 0x00, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA };
        private static readonly byte[] Gif = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x20, 0x00, 0x10, 0x00, 0, 0, 0 };
        private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        [Test]
        public void MagicNumber_BeatsOctetStreamHeader()
        {
            string mime;
            Assert.AreEqual(UapWebContentKind.Image, UapWebContentKinds.Classify("application/octet-stream", Png, "/x", out mime));
            Assert.AreEqual("image/png", mime);
            Assert.AreEqual(UapWebContentKind.Pdf, UapWebContentKinds.Classify("application/octet-stream", Pdf, "/paper", out mime));
            Assert.AreEqual("application/pdf", mime);
        }

        [Test]
        public void MagicNumber_BeatsWrongHeader()
        {
            string mime;
            Assert.AreEqual(UapWebContentKind.Image, UapWebContentKinds.Classify("text/html", Jpeg, "/x.html", out mime));
            Assert.AreEqual("image/jpeg", mime);
            Assert.AreEqual(UapWebContentKind.ImagePassthrough, UapWebContentKinds.Classify("image/png", Gif, "/x.png", out mime));
            Assert.AreEqual("image/gif", mime);
        }

        [Test]
        public void Header_DecidesWhenNoMagic()
        {
            string mime;
            byte[] text = Encoding.UTF8.GetBytes("hello");
            Assert.AreEqual(UapWebContentKind.Html, UapWebContentKinds.Classify("text/html; charset=utf-8", text, "/", out mime));
            Assert.AreEqual("text/html", mime);
            Assert.AreEqual(UapWebContentKind.Text, UapWebContentKinds.Classify("application/json", text, "/api", out mime));
            Assert.AreEqual(UapWebContentKind.Text, UapWebContentKinds.Classify("application/ld+json", text, "/api", out mime));
            Assert.AreEqual(UapWebContentKind.Text, UapWebContentKinds.Classify("image/svg+xml", text, "/icon.svg", out mime));
            Assert.AreEqual(UapWebContentKind.Binary, UapWebContentKinds.Classify("application/zip", new byte[] { 0x50, 0x4B, 3, 4 }, "/a.zip", out mime));
            Assert.AreEqual("application/zip", mime);
        }

        [Test]
        public void Extension_DecidesWhenHeaderIsMissingOrOctetStream()
        {
            string mime;
            byte[] text = Encoding.UTF8.GetBytes("# Title\n\nbody");
            Assert.AreEqual(UapWebContentKind.Text, UapWebContentKinds.Classify(null, text, "/README.md?raw=1", out mime));
            Assert.AreEqual("text/markdown", mime);
            Assert.AreEqual(UapWebContentKind.Binary, UapWebContentKinds.Classify("application/octet-stream",
                new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, "/model.fbx", out mime));
            Assert.AreEqual("application/octet-stream", mime);
        }

        [Test]
        public void Sniff_HtmlAndText_LastResort()
        {
            string mime;
            byte[] html = Encoding.UTF8.GetBytes("\n  <!DOCTYPE html><html><body>x</body></html>");
            Assert.AreEqual(UapWebContentKind.Html, UapWebContentKinds.Classify(null, html, "/page", out mime));
            Assert.AreEqual(UapWebContentKind.Html, UapWebContentKinds.Classify("application/octet-stream", html, "/page", out mime));
            byte[] text = Encoding.UTF8.GetBytes("plain words\nand lines");
            Assert.AreEqual(UapWebContentKind.Text, UapWebContentKinds.Classify(null, text, "/thing", out mime));
            Assert.AreEqual("text/plain", mime);
            byte[] binary = { 0, 0xFF, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.AreEqual(UapWebContentKind.Binary, UapWebContentKinds.Classify(null, binary, "/thing", out mime));
        }

        [Test]
        public void NormalizeMime_And_Charset()
        {
            Assert.AreEqual("text/html", UapWebContentKinds.NormalizeMime("Text/HTML; charset=Shift_JIS"));
            Assert.IsNull(UapWebContentKinds.NormalizeMime("  "));
            Assert.AreEqual("Shift_JIS", UapWebContentKinds.CharsetOf("text/html; charset=Shift_JIS"));
            Assert.AreEqual("utf-8", UapWebContentKinds.CharsetOf("text/plain;charset=\"utf-8\";x=y"));
            Assert.IsNull(UapWebContentKinds.CharsetOf("text/plain"));
        }

        [Test]
        public void DecodeText_Bom_Charset_Fallback()
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF, (byte)'a' };
            Assert.AreEqual("a", UapWebContentKinds.DecodeText(bom, "text/plain; charset=iso-8859-1"));
            byte[] latin1 = { 0xE9 };
            Assert.AreEqual("é", UapWebContentKinds.DecodeText(latin1, "text/plain; charset=iso-8859-1"));
            byte[] utf8 = Encoding.UTF8.GetBytes("日本語");
            Assert.AreEqual("日本語", UapWebContentKinds.DecodeText(utf8, "text/plain; charset=no-such-charset"));
            Assert.AreEqual("日本語", UapWebContentKinds.DecodeText(utf8, null));
            Assert.AreEqual(string.Empty, UapWebContentKinds.DecodeText(null, null));
        }

        [Test]
        public void TryReadDimensions_PngGifJpeg()
        {
            int w, h;
            Assert.IsTrue(UapWebContentKinds.TryReadDimensions(Png, out w, out h));
            Assert.AreEqual(2048, w);
            Assert.AreEqual(16, h);
            Assert.IsTrue(UapWebContentKinds.TryReadDimensions(Gif, out w, out h));
            Assert.AreEqual(32, w);
            Assert.AreEqual(16, h);
            Assert.IsTrue(UapWebContentKinds.TryReadDimensions(Jpeg, out w, out h));
            Assert.AreEqual(512, w);
            Assert.AreEqual(256, h);
            Assert.IsFalse(UapWebContentKinds.TryReadDimensions(Pdf, out w, out h));
            Assert.IsFalse(UapWebContentKinds.TryReadDimensions(new byte[] { 0x89, 0x50 }, out w, out h));
        }
    }
}
