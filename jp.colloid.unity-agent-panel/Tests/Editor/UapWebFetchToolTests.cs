using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_web_fetch end to end with a canned fetcher and encoder (no
    /// network, no Texture2D): what block shape each body kind produces
    /// (design note 2026-09-17-web-fetch-tool.md section 4), the argument
    /// validation, and the metadata the permission surface relies on.
    /// </summary>
    [TestFixture]
    public class UapWebFetchToolTests
    {
        private sealed class FakeFetcher : IUapWebFetcher
        {
            public readonly Dictionary<string, UapWebFetchResponse> Responses = new Dictionary<string, UapWebFetchResponse>();
            public string Throw;
            public Uri LastUrl;

            public UapWebFetchResponse Fetch(Uri url)
            {
                LastUrl = url;
                if (Throw != null)
                {
                    throw new UapWebFetchException(Throw);
                }
                UapWebFetchResponse r;
                if (!Responses.TryGetValue(url.AbsoluteUri, out r))
                {
                    throw new UapWebFetchException("no canned response for " + url);
                }
                if (r.FinalUrl == null)
                {
                    r.FinalUrl = url;
                }
                return r;
            }
        }

        private sealed class FakeEncoder : IUapWebImageEncoder
        {
            public byte[] Encoded = { 1, 2, 3 };
            public string Mime = "image/jpeg";
            public int Width = 1568;
            public int Height = 12;
            public string Fail;
            public int Calls;

            public bool TryEncode(byte[] raw, string sourceName, out byte[] encoded, out string mimeType,
                out int width, out int height, out string error)
            {
                Calls++;
                encoded = Encoded;
                mimeType = Mime;
                width = Width;
                height = Height;
                error = Fail;
                return Fail == null;
            }
        }

        private sealed class FakeImporter : IUapWebAssetImporter
        {
            public string LastPath;
            public byte[] LastBody;
            public bool LastOverwrite;
            public string Fail;

            public string Import(string assetPath, byte[] body, bool overwrite)
            {
                LastPath = assetPath;
                LastBody = body;
                LastOverwrite = overwrite;
                if (Fail != null)
                {
                    throw new InvalidOperationException(Fail);
                }
                return "0123456789abcdef0123456789abcdef";
            }
        }

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x08, 0x00, 0, 0, 0x00, 0x10, 8, 6, 0, 0, 0 };
        private static readonly byte[] Gif = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x20, 0x00, 0x10, 0x00, 0, 0, 0 };

        private string _root;
        private FakeFetcher _fetcher;
        private FakeEncoder _encoder;
        private FakeImporter _importer;
        private UapWebHostRules _savedRules;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "uap-web-fetch-" + Guid.NewGuid().ToString("N"));
            _fetcher = new FakeFetcher();
            _encoder = new FakeEncoder();
            _importer = new FakeImporter();
            // The host lists are a process-wide snapshot; start every test
            // from "allow all" and put back whatever was there.
            _savedRules = UapWebFetchTool.HostRules;
            UapWebFetchTool.HostRules = UapWebHostRules.AllowAll;
        }

        [TearDown]
        public void TearDown()
        {
            UapWebFetchTool.HostRules = _savedRules ?? UapWebHostRules.AllowAll;
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private UapWebFetchTool Tool(IUapWebImageEncoder encoder)
        {
            return new UapWebFetchTool(_fetcher, encoder, _importer, _root,
                delegate { return new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc); });
        }

        private void Can(string url, string contentType, byte[] body, int status = 200)
        {
            _fetcher.Responses[url] = new UapWebFetchResponse { StatusCode = status, ContentType = contentType, Body = body };
        }

        private static JsonNode Args(string url)
        {
            return JsonNode.NewObject().Set("url", url);
        }

        // -- metadata --------------------------------------------------------

        [Test]
        public void Metadata_WebModule_NotReadOnly_OffThread()
        {
            var tool = Tool(_encoder);
            Assert.AreEqual("uap_web_fetch", tool.Name);
            Assert.AreEqual("web", tool.Module);
            Assert.IsFalse(tool.ReadOnly, "an outbound URL is an exfiltration channel; the permission card must show it");
            Assert.IsFalse(tool.Undoable);
            Assert.IsInstanceOf<IUapOffThreadTool>(tool, "a download must not run on (or wait for) the main thread");
            Assert.AreEqual("url", tool.InputSchema["required"][0].AsString());
            Assert.IsFalse(tool.InputSchema["additionalProperties"].AsBool(true));
        }

        [Test]
        public void Registry_RegistersTheTool()
        {
            ToolRegistry registry = ToolRegistry.CreateDefault(false);
            List<IUapTool> tools = registry.ListEnabled(new[] { "web" });
            Assert.AreEqual(1, tools.Count);
            Assert.AreEqual("uap_web_fetch", tools[0].Name);
            Assert.IsTrue(registry.HasToolsInModule("web"));
        }

        // -- images ------------------------------------------------------------

        [Test]
        public void Png_EncodedOnMainThread_ImageBlockAndSavedFile()
        {
            Can("https://example.com/t/cobble.png", "image/png", Png);
            JsonNode result = Tool(_encoder).Execute(Args("https://example.com/t/cobble.png"));

            Assert.AreEqual(2, result.Count);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("Fetched https://example.com/t/cobble.png (image/png, 29 bytes, HTTP 200)", text);
            StringAssert.Contains("Image 2048x16, shown at 1568x12. Saved to ", text);
            Assert.AreEqual("image", result[1]["type"].AsString());
            Assert.AreEqual("image/jpeg", result[1]["mimeType"].AsString());
            Assert.AreEqual(Convert.ToBase64String(new byte[] { 1, 2, 3 }), result[1]["data"].AsString());
            Assert.AreEqual(1, _encoder.Calls);
            string saved = SavedPath(text);
            Assert.IsTrue(File.Exists(saved), saved);
            Assert.That(saved, Does.EndWith("-cobble.png"));
            CollectionAssert.AreEqual(Png, File.ReadAllBytes(saved), "the ORIGINAL bytes are what is saved");
        }

        [Test]
        public void Png_EncoderFails_OriginalBytesPassedThrough()
        {
            Can("https://example.com/a.png", "image/png", Png);
            _encoder.Fail = "no main thread";
            JsonNode result = Tool(_encoder).Execute(Args("https://example.com/a.png"));

            Assert.AreEqual(2, result.Count);
            StringAssert.Contains("shown as downloaded", result[0]["text"].AsString());
            Assert.AreEqual("image/png", result[1]["mimeType"].AsString());
            Assert.AreEqual(Convert.ToBase64String(Png), result[1]["data"].AsString());
        }

        [Test]
        public void Png_NoEncoder_TooLargeForPassthrough_SavedOnly()
        {
            var big = new byte[UapWebFetchPolicy.PassthroughImageMaxBytes + 1];
            Array.Copy(Png, big, Png.Length);
            Can("https://example.com/huge.png", "image/png", big);
            JsonNode result = Tool(null).Execute(Args("https://example.com/huge.png"));

            Assert.AreEqual(1, result.Count, "no image block");
            string text = result[0]["text"].AsString();
            StringAssert.Contains("Not shown inline", text);
            StringAssert.Contains("read the file instead", text);
            Assert.IsTrue(File.Exists(SavedPath(text)));
        }

        [Test]
        public void Png_ReturnImageFalse_PathOnly()
        {
            Can("https://example.com/a.png", "image/png", Png);
            JsonNode result = Tool(_encoder).Execute(Args("https://example.com/a.png").Set("return_image", false));
            Assert.AreEqual(1, result.Count);
            StringAssert.Contains("return_image is false", result[0]["text"].AsString());
            Assert.AreEqual(0, _encoder.Calls);
        }

        [Test]
        public void Gif_PassedThroughWithoutEncoding()
        {
            Can("https://example.com/anim.gif", "image/gif", Gif);
            JsonNode result = Tool(_encoder).Execute(Args("https://example.com/anim.gif"));
            Assert.AreEqual(2, result.Count);
            StringAssert.Contains("image/gif is not re-encoded", result[0]["text"].AsString());
            Assert.AreEqual("image/gif", result[1]["mimeType"].AsString());
            Assert.AreEqual(0, _encoder.Calls);
        }

        [Test]
        public void OctetStreamPng_StillAnImage()
        {
            Can("https://cdn.example/blob/123", "application/octet-stream", Png);
            JsonNode result = Tool(_encoder).Execute(Args("https://cdn.example/blob/123"));
            Assert.AreEqual(2, result.Count);
            StringAssert.Contains("(image/png,", result[0]["text"].AsString());
        }

        // -- pdf ---------------------------------------------------------------

        [Test]
        public void Pdf_SavedWithPageEstimateAndReadHint()
        {
            string pdf = "%PDF-1.7\n1 0 obj\n<< /Type /Pages /Kids [2 0 R 3 0 R] /Count 24 >>\nendobj\n"
                + "2 0 obj\n<< /Type /Pages /Parent 1 0 R /Count 12 >>\nendobj\n%%EOF";
            Can("https://example.com/paper.pdf", "application/pdf", Encoding.ASCII.GetBytes(pdf));
            JsonNode result = Tool(_encoder).Execute(Args("https://example.com/paper.pdf"));

            Assert.AreEqual(1, result.Count);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("PDF saved to ", text);
            StringAssert.Contains("about 24 pages", text);
            StringAssert.Contains("Read with pages=\"1-20\", then \"21-24\"", text);
            Assert.That(SavedPath(text), Does.EndWith("-paper.pdf"));
            Assert.IsTrue(File.Exists(SavedPath(text)));
        }

        private static byte[] TextPdf(string content)
        {
            string body = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
                + "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"
                + "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>\nendobj\n"
                + "4 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n%%EOF";
            return Encoding.ASCII.GetBytes(body);
        }

        [Test]
        public void Pdf_AsText_ExtractsHere_InsteadOfTheReadHint()
        {
            Can("https://example.com/doc.pdf", "application/pdf", TextPdf("BT 72 700 Td (Hello) Tj 40 0 Td (there) Tj ET"));
            string text = Tool(_encoder).Execute(Args("https://example.com/doc.pdf").Set("as", "text"))[0]["text"].AsString();
            StringAssert.Contains("PDF saved to ", text);
            StringAssert.Contains("Text extracted here", text);
            StringAssert.Contains("--- Page 1 of 1 ---\nHello there", text);
            StringAssert.DoesNotContain("Read with pages", text);
        }

        [Test]
        public void Pdf_PagesArgument_ExtractsThatRange()
        {
            Can("https://example.com/doc.pdf", "application/pdf", TextPdf("BT (only) Tj ET"));
            string text = Tool(_encoder).Execute(Args("https://example.com/doc.pdf").Set("pages", "2-3"))[0]["text"].AsString();
            StringAssert.Contains("no pages in the requested range; the document has 1 pages", text);
            string first = Tool(_encoder).Execute(Args("https://example.com/doc.pdf").Set("pages", "1"))[0]["text"].AsString();
            StringAssert.Contains("--- Page 1 of 1 ---\nonly", first);
        }

        [Test]
        public void Pdf_BadPages_RejectedBeforeFetching()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                Tool(_encoder).Execute(Args("https://example.com/doc.pdf").Set("pages", "5-2"));
            });
            StringAssert.Contains("pages", ex.Message);
            Assert.IsNull(_fetcher.LastUrl);
        }

        [Test]
        public void Pdf_NoCount_SaysNothingAboutPages()
        {
            Can("https://example.com/x.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
            string text = Tool(_encoder).Execute(Args("https://example.com/x.pdf"))[0]["text"].AsString();
            StringAssert.DoesNotContain("pages,", text);
            StringAssert.Contains("PDF saved to", text);
        }

        // -- html / text ---------------------------------------------------------

        [Test]
        public void Html_TitleBodyImagesLinks_NoFileSaved()
        {
            string html = "<html><head><title>Docs</title></head><body><main><h1>Texture2D</h1><p>Body.</p>"
                + "<img src=\"/img/tex.png\" alt=\"Diagram\"><a href=\"/next.html\">Next</a></main></body></html>";
            Can("https://docs.example/page.html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            JsonNode result = Tool(_encoder).Execute(Args("https://docs.example/page.html"));

            Assert.AreEqual(1, result.Count);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("(text/html,", text);
            StringAssert.Contains("Title: Docs", text);
            StringAssert.Contains("# Texture2D", text);
            StringAssert.Contains("Body.", text);
            StringAssert.Contains("- Diagram -- https://docs.example/img/tex.png", text);
            StringAssert.Contains("- Next -- https://docs.example/next.html", text);
            Assert.IsFalse(Directory.Exists(_root), "text bodies are not written to the download store");
        }

        [Test]
        public void Text_MaxCharsAndOffset_Window()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                sb.Append("line ").Append(i).Append('\n');
            }
            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
            Can("https://example.com/log.txt", "text/plain", body);
            var tool = Tool(_encoder);

            string first = tool.Execute(Args("https://example.com/log.txt").Set("max_chars", 50))[0]["text"].AsString();
            StringAssert.Contains("line 0", first);
            StringAssert.Contains("(showing chars 0-50 of " + sb.Length + "; call again with offset=50 for more)", first);
            StringAssert.DoesNotContain("line 99", first);

            string rest = tool.Execute(Args("https://example.com/log.txt").Set("offset", 50))[0]["text"].AsString();
            StringAssert.Contains("line 99", rest);
            StringAssert.Contains("(showing chars 50-" + sb.Length + " of " + sb.Length + ")", rest);
        }

        [Test]
        public void Json_ReturnedAsText_WithCharset()
        {
            // UTF-16 with a BOM: the BOM wins over (and here agrees with) the header charset.
            byte[] payload = Encoding.Unicode.GetBytes("{\"name\":\"日本語\"}");
            byte[] body = new byte[2 + payload.Length];
            body[0] = 0xFF;
            body[1] = 0xFE;
            Array.Copy(payload, 0, body, 2, payload.Length);
            Can("https://api.example/v1", "application/json; charset=utf-16", body);
            string text = Tool(_encoder).Execute(Args("https://api.example/v1"))[0]["text"].AsString();
            StringAssert.Contains("{\"name\":\"日本語\"}", text);
        }

        // -- binary ------------------------------------------------------------

        [Test]
        public void Binary_SavedAndPathReturned()
        {
            byte[] zip = { 0x50, 0x4B, 3, 4, 0, 0, 0, 0, 0, 0 };
            Can("https://example.com/pkg.zip", "application/zip", zip);
            string text = Tool(_encoder).Execute(Args("https://example.com/pkg.zip"))[0]["text"].AsString();
            StringAssert.Contains("Saved to ", text);
            StringAssert.Contains("Read or import it from there.", text);
            Assert.That(SavedPath(text), Does.EndWith("-pkg.zip"));
        }

        [Test]
        public void AsFile_ForcesSaveEvenForHtml()
        {
            Can("https://example.com/page", "text/html", Encoding.UTF8.GetBytes("<p>x</p>"));
            string text = Tool(_encoder).Execute(Args("https://example.com/page").Set("as", "file"))[0]["text"].AsString();
            StringAssert.Contains("Saved to ", text);
            Assert.That(SavedPath(text), Does.EndWith("-page.html"));
        }

        // -- redirects / errors --------------------------------------------------

        [Test]
        public void Redirect_NotedInHeaderLine()
        {
            _fetcher.Responses["https://short.example/x"] = new UapWebFetchResponse
            {
                StatusCode = 200,
                ContentType = "text/plain",
                Body = Encoding.UTF8.GetBytes("hi"),
                FinalUrl = new Uri("https://long.example/real.txt"),
                RedirectedFrom = new Uri("https://short.example/x")
            };
            string text = Tool(_encoder).Execute(Args("https://short.example/x"))[0]["text"].AsString();
            StringAssert.StartsWith("Fetched https://long.example/real.txt (text/plain, 2 bytes, HTTP 200, redirected from https://short.example/x)", text);
        }

        [Test]
        public void FetchFailure_BecomesToolError_WithTheFetcherMessage()
        {
            _fetcher.Throw = "HTTP 404 Not Found for https://example.com/missing.";
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                Tool(_encoder).Execute(Args("https://example.com/missing"));
            });
            Assert.AreEqual("HTTP 404 Not Found for https://example.com/missing.", ex.Message);
        }

        [TestCase("{}", "url is required")]
        [TestCase("{\"url\":\"ftp://x/y\"}", "http and https")]
        [TestCase("{\"url\":\"http://127.0.0.1:7777/\"}", "local")]
        [TestCase("{\"url\":\"https://example.com/\",\"as\":\"pdf\"}", "as must be one of")]
        [TestCase("{\"url\":\"https://example.com/\",\"max_chars\":0}", "max_chars")]
        [TestCase("{\"url\":\"https://example.com/\",\"offset\":-1}", "offset")]
        public void BadArguments_ThrowBeforeFetching(string json, string fragment)
        {
            JsonNode input;
            string parseError;
            Assert.IsTrue(JsonParser.TryParse(json, out input, out parseError), parseError);
            var ex = Assert.Throws<ArgumentException>(delegate { Tool(_encoder).Execute(input); });
            StringAssert.Contains(fragment, ex.Message);
            Assert.IsNull(_fetcher.LastUrl, "validation must happen before any request");
        }

        [Test]
        public void AsImage_OnHtml_Refused()
        {
            Can("https://example.com/page", "text/html", Encoding.UTF8.GetBytes("<p>x</p>"));
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                Tool(_encoder).Execute(Args("https://example.com/page").Set("as", "image"));
            });
            StringAssert.Contains("not an image", ex.Message);
        }

        // -- save_to (stage 2) ----------------------------------------------------

        [Test]
        public void SaveTo_ImportsTheOriginalBytes_ReportsPathAndGuid()
        {
            Can("https://example.com/t/cobble.png", "image/png", Png);
            JsonNode result = Tool(_encoder).Execute(Args("https://example.com/t/cobble.png")
                .Set("save_to", "Assets\\Textures\\..\\Textures\\cobble.png"));

            Assert.AreEqual("Assets/Textures/cobble.png", _importer.LastPath, "normalized under Assets/");
            CollectionAssert.AreEqual(Png, _importer.LastBody, "the ORIGINAL bytes, not the downscaled block");
            Assert.IsFalse(_importer.LastOverwrite);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("Imported as Assets/Textures/cobble.png (GUID 0123456789abcdef0123456789abcdef).", text);
            Assert.AreEqual(2, result.Count, "the image block still comes back alongside the import");
        }

        [Test]
        public void SaveTo_Overwrite_PassedThrough()
        {
            Can("https://example.com/a.txt", "text/plain", Encoding.UTF8.GetBytes("x"));
            Tool(_encoder).Execute(Args("https://example.com/a.txt").Set("save_to", "Assets/a.txt").Set("overwrite", true));
            Assert.IsTrue(_importer.LastOverwrite);
        }

        [Test]
        public void SaveTo_ImporterFailure_IsAToolError()
        {
            Can("https://example.com/a.txt", "text/plain", Encoding.UTF8.GetBytes("x"));
            _importer.Fail = "'Assets/a.txt' already exists; pass overwrite:true to replace it.";
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                Tool(_encoder).Execute(Args("https://example.com/a.txt").Set("save_to", "Assets/a.txt"));
            });
            StringAssert.Contains("save_to failed: 'Assets/a.txt' already exists", ex.Message);
        }

        [TestCase("Library/x.png", "under 'Assets/'")]
        [TestCase("Assets/../Library/x.png", "under 'Assets/'")]
        [TestCase("Assets/Textures/", "extension")]
        [TestCase("Assets/noext", "extension")]
        public void SaveTo_BadPath_RejectedBeforeFetching(string saveTo, string fragment)
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                Tool(_encoder).Execute(Args("https://example.com/a.png").Set("save_to", saveTo));
            });
            StringAssert.Contains(fragment, ex.Message);
            Assert.IsNull(_fetcher.LastUrl);
            Assert.IsNull(_importer.LastPath);
        }

        [Test]
        public void SaveTo_WithoutImporter_Refused()
        {
            var tool = new UapWebFetchTool(_fetcher, _encoder, _root, null);
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                tool.Execute(Args("https://example.com/a.png").Set("save_to", "Assets/a.png"));
            });
            StringAssert.Contains("no asset importer", ex.Message);
            Assert.IsNull(_fetcher.LastUrl);
        }

        // -- host rules (stage 2) --------------------------------------------------

        [Test]
        public void HostRules_BlockedHost_RefusedBeforeFetching()
        {
            UapWebFetchTool.HostRules = new UapWebHostRules(null, new[] { "example.com" });
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                Tool(_encoder).Execute(Args("https://cdn.example.com/a.png"));
            });
            StringAssert.Contains("blocked-hosts list", ex.Message);
            Assert.IsNull(_fetcher.LastUrl);
        }

        [Test]
        public void HostRules_AllowList_OtherHostsRefused_ListedHostFetched()
        {
            UapWebFetchTool.HostRules = new UapWebHostRules(new[] { "docs.unity3d.com" }, null);
            Assert.Throws<InvalidOperationException>(delegate
            {
                Tool(_encoder).Execute(Args("https://example.com/a.txt"));
            });
            Assert.IsNull(_fetcher.LastUrl);
            Can("https://docs.unity3d.com/x.txt", "text/plain", Encoding.UTF8.GetBytes("ok"));
            string text = Tool(_encoder).Execute(Args("https://docs.unity3d.com/x.txt"))[0]["text"].AsString();
            StringAssert.Contains("ok", text);
        }

        // -- pure helpers --------------------------------------------------------

        [Test]
        public void Window_Slices()
        {
            Assert.AreEqual("abc", UapWebFetchTool.Window("abc", 0, 10));
            Assert.AreEqual("(empty)", UapWebFetchTool.Window("", 0, 10));
            Assert.AreEqual("ab\n(showing chars 0-2 of 3; call again with offset=2 for more)", UapWebFetchTool.Window("abc", 0, 2));
            Assert.AreEqual("c\n(showing chars 2-3 of 3)", UapWebFetchTool.Window("abc", 2, 10));
            StringAssert.Contains("past the end", UapWebFetchTool.Window("abc", 5, 10));
        }

        [Test]
        public void EstimatePdfPageCount_LargestCountWins()
        {
            Assert.AreEqual(24, UapWebFetchTool.EstimatePdfPageCount(Encoding.ASCII.GetBytes("/Count 12 ... /Count 24 ... /Count 3")));
            Assert.AreEqual(0, UapWebFetchTool.EstimatePdfPageCount(Encoding.ASCII.GetBytes("%PDF-1.4")));
            Assert.AreEqual(0, UapWebFetchTool.EstimatePdfPageCount(null));
        }

        private static string SavedPath(string text)
        {
            int idx = text.IndexOf("aved to ", StringComparison.Ordinal);
            Assert.GreaterOrEqual(idx, 0, text);
            string rest = text.Substring(idx + "aved to ".Length);
            int end = rest.IndexOfAny(new[] { ' ', '\n' });
            return (end < 0 ? rest : rest.Substring(0, end)).TrimEnd('.');
        }
    }
}
