using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// uap_web_fetch: fetches one URL and hands its content to the model in
    /// the form the model can use (design note
    /// docs/design-notes/2026-09-17-web-fetch-tool.md). The agent's own
    /// WebFetch flattens everything to text; this returns a PNG/JPEG as an
    /// image block (downscaled like a composer attachment), saves a PDF or
    /// any other file under Library/AgentPanel/Downloads for the agent's
    /// file reader, and reduces HTML to readable text plus the page's
    /// image and link URLs. The same MCP tool for every backend (Claude
    /// Code, Codex, Grok).
    ///
    /// Runs OFF the Unity main thread (<see cref="IUapOffThreadTool"/>): a
    /// download must never stall the Editor, and the dispatcher's 15 s
    /// main-thread budget would abandon a slow one. The only Unity-side
    /// step -- decoding and downscaling an image -- hops back to the main
    /// thread through <see cref="IUapWebImageEncoder"/>. Everything else
    /// here is plain .NET and touches no Unity API (the project root is
    /// captured in the constructor, on the main thread).
    ///
    /// ReadOnly is FALSE although nothing in the project changes: an
    /// outbound request carries whatever the agent puts in the URL, so an
    /// auto-approved fetch would be a data-exfiltration channel for a
    /// prompt-injected agent. The permission card showing the URL is the
    /// one safeguard, so it stays (section 5.1).
    /// </summary>
    public sealed class UapWebFetchTool : IUapOffThreadTool
    {
        /// <summary>
        /// Installed by UapOpsServer while it runs: the dispatcher the tool
        /// uses to run image encoding on the main thread. Null when the
        /// server is stopped (the tool then passes images through as-is).
        /// </summary>
        public static IUapToolExecutor MainThreadExecutor;

        /// <summary>
        /// The user's host allow / deny lists (Settings > Web fetch), as a
        /// snapshot the worker reads without touching PanelSettings.
        /// Replaced whole by UapOpsServer at start and by SettingsView on
        /// every edit; never null.
        /// </summary>
        public static volatile UapWebHostRules HostRules = UapWebHostRules.AllowAll;

        private readonly IUapWebFetcher _fetcher;
        private readonly IUapWebImageEncoder _encoder;
        private readonly IUapWebAssetImporter _importer;
        private readonly string _downloadsRoot;
        private readonly Func<DateTime> _utcNow;

        /// <summary>Production wiring: HttpWebRequest fetcher, main-thread encoder and importer, this project's Library folder.</summary>
        public UapWebFetchTool()
            : this(new UapHttpWebFetcher(UnityEngine.Application.unityVersion, delegate { return HostRules; }),
                new UapWebMainThreadImageEncoder(delegate { return MainThreadExecutor; }),
                new UapWebMainThreadAssetImporter(delegate { return MainThreadExecutor; }, ProjectRoot()),
                UapWebDownloads.RootFor(ProjectRoot()),
                null)
        {
        }

        private static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
        }

        public UapWebFetchTool(IUapWebFetcher fetcher, IUapWebImageEncoder encoder, string downloadsRoot, Func<DateTime> utcNow)
            : this(fetcher, encoder, null, downloadsRoot, utcNow)
        {
        }

        public UapWebFetchTool(IUapWebFetcher fetcher, IUapWebImageEncoder encoder, IUapWebAssetImporter importer,
            string downloadsRoot, Func<DateTime> utcNow)
        {
            if (fetcher == null)
            {
                throw new ArgumentNullException("fetcher");
            }
            if (string.IsNullOrEmpty(downloadsRoot))
            {
                throw new ArgumentException("downloadsRoot must be non-empty.", "downloadsRoot");
            }
            _fetcher = fetcher;
            _encoder = encoder;
            _importer = importer;
            _downloadsRoot = downloadsRoot;
            _utcNow = utcNow;
            if (_utcNow == null)
            {
                _utcNow = delegate { return DateTime.UtcNow; };
            }
        }

        public string Name
        {
            get { return "uap_web_fetch"; }
        }

        public string Description
        {
            get
            {
                return "Fetches a URL and returns its content in the form you can use: images (png/jpeg,"
                    + " also gif/webp) inline as an image block, PDFs saved to a local file you can Read"
                    + " (or, with as:\"text\" / pages, their text extracted here),"
                    + " HTML pages as readable text with the page's image and link URLs, anything else"
                    + " saved to a file under Library/AgentPanel/Downloads (or, with save_to, imported"
                    + " under Assets/ as a project asset). Use it after a web search when"
                    + " you need to SEE an image or read a PDF or page -- your own web fetch tool returns"
                    + " text only. Public http/https hosts only, 20 MB limit.";
            }
        }

        public string Module
        {
            get { return "web"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("url", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Absolute http or https URL to fetch."))
                        .Set("as", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("auto").Add("image").Add("text").Add("file"))
                            .Set("description", "How to treat the body. \"auto\" (default) decides from the"
                                + " content type and the first bytes; \"image\" insists on an image block;"
                                + " \"text\" returns the body as text (HTML reduced to readable text);"
                                + " \"file\" only saves it and returns the path."))
                        .Set("max_chars", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Most characters of text to return (default 60000, max 400000)."
                                + " Longer text is cut and the result says how to continue."))
                        .Set("offset", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Character offset to start the returned text at (default 0);"
                                + " use the value the previous result suggested to read on."))
                        .Set("return_image", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "For images: include the picture itself as an image block"
                                + " (default true). false returns only the saved file path."))
                        .Set("pages", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "For PDFs: extract the text of these pages here (\"3\", \"1-5\","
                                + " \"7-\"; 1-based) instead of only saving the file. as:\"text\" extracts every page."
                                + " Best-effort: layout is flattened, images and scanned pages yield nothing."))
                        .Set("save_to", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Optional project path under Assets/ (with extension, e.g."
                                + " \"Assets/Textures/cobble.png\") to write the downloaded file to and import as an"
                                + " asset; the result reports the asset path and GUID. Fails if the file exists"
                                + " unless overwrite is true."))
                        .Set("overwrite", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "With save_to: replace an existing file at that path (default false).")))
                    .Set("required", JsonNode.NewArray().Add("url"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            Uri uri;
            string error;
            if (!UapWebFetchPolicy.TryParseUrl(input["url"].AsString(null), out uri, out error))
            {
                throw new ArgumentException(error);
            }
            string asArg = (input["as"].AsString("auto") ?? "auto").Trim().ToLowerInvariant();
            if (asArg != "auto" && asArg != "image" && asArg != "text" && asArg != "file")
            {
                throw new ArgumentException("as must be one of \"auto\", \"image\", \"text\", \"file\" (got \"" + asArg + "\").");
            }
            int maxChars = input["max_chars"].AsInt(UapWebFetchPolicy.DefaultMaxChars);
            if (maxChars < 1 || maxChars > UapWebFetchPolicy.MaxMaxChars)
            {
                throw new ArgumentException("max_chars must be between 1 and " + UapWebFetchPolicy.MaxMaxChars + ".");
            }
            int offset = input["offset"].AsInt(0);
            if (offset < 0)
            {
                throw new ArgumentException("offset must be 0 or more.");
            }
            bool returnImage = input["return_image"].AsBool(true);
            string saveTo = null;
            if (input.HasKey("save_to") && !string.IsNullOrEmpty(input["save_to"].AsString(null)))
            {
                string pathError;
                saveTo = UapAssetPath.NormalizeUnderAssets(input["save_to"].AsString(null), out pathError);
                if (saveTo == null)
                {
                    throw new ArgumentException("save_to: " + pathError);
                }
                if (saveTo.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(Path.GetExtension(saveTo)))
                {
                    throw new ArgumentException("save_to must name a file with an extension under Assets/ (got '" + saveTo + "').");
                }
                if (_importer == null)
                {
                    throw new InvalidOperationException("save_to is not available in this context (no asset importer).");
                }
            }
            bool overwrite = input["overwrite"].AsBool(false);
            int pdfFirst = 1;
            int pdfLast = int.MaxValue;
            bool pagesGiven = input.HasKey("pages") && !string.IsNullOrEmpty(input["pages"].AsString(null));
            if (pagesGiven)
            {
                string pagesError;
                if (!UapPdfText.TryParsePageRange(input["pages"].AsString(null), out pdfFirst, out pdfLast, out pagesError))
                {
                    throw new ArgumentException(pagesError);
                }
            }
            string rulesReason;
            if (!HostRules.IsAllowed(uri.Host, out rulesReason))
            {
                throw new InvalidOperationException(rulesReason);
            }

            UapWebFetchResponse response;
            try
            {
                response = _fetcher.Fetch(uri);
            }
            catch (UapWebFetchException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            byte[] body = response.Body ?? new byte[0];
            Uri finalUrl = response.FinalUrl ?? uri;

            string mime;
            UapWebContentKind kind = UapWebContentKinds.Classify(response.ContentType, body, finalUrl.AbsolutePath, out mime);
            kind = ApplyAsOverride(asArg, kind, mime, finalUrl);

            var sb = new StringBuilder(256);
            sb.Append("Fetched ").Append(finalUrl.AbsoluteUri).Append(" (").Append(mime).Append(", ")
              .Append(body.Length.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes, HTTP ")
              .Append(response.StatusCode);
            if (response.RedirectedFrom != null)
            {
                sb.Append(", redirected from ").Append(response.RedirectedFrom.AbsoluteUri);
            }
            sb.Append(')');

            JsonNode content = null;
            switch (kind)
            {
                case UapWebContentKind.Image:
                    content = DescribeImage(sb, body, mime, finalUrl, returnImage, true);
                    break;
                case UapWebContentKind.ImagePassthrough:
                    content = DescribeImage(sb, body, mime, finalUrl, returnImage, false);
                    break;
                case UapWebContentKind.Pdf:
                    DescribePdf(sb, body, mime, finalUrl, asArg == "text" || pagesGiven, pdfFirst, pdfLast, offset, maxChars);
                    break;
                case UapWebContentKind.Html:
                    {
                        string html = UapWebContentKinds.DecodeText(body, response.ContentType);
                        UapHtmlText.Result page = UapHtmlText.Extract(html, finalUrl);
                        sb.Append('\n').Append(Window(UapHtmlText.Render(page), offset, maxChars));
                        break;
                    }
                case UapWebContentKind.Text:
                    sb.Append('\n').Append(Window(UapWebContentKinds.DecodeText(body, response.ContentType), offset, maxChars));
                    break;
                default:
                    {
                        string path = SaveAndPrune(finalUrl, mime, body);
                        sb.Append("\nSaved to ").Append(path).Append(" (").Append(UapHttpWebFetcher.FormatBytes(body.Length))
                          .Append("). Read or import it from there.");
                        break;
                    }
            }
            if (saveTo != null)
            {
                string guid;
                try
                {
                    guid = _importer.Import(saveTo, body, overwrite);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("save_to failed: " + ex.Message, ex);
                }
                sb.Append("\nImported as ").Append(saveTo);
                if (!string.IsNullOrEmpty(guid))
                {
                    sb.Append(" (GUID ").Append(guid).Append(')');
                }
                sb.Append('.');
            }
            if (content == null)
            {
                return UapToolResults.Text(sb.ToString());
            }
            // The image branch built its blocks first (so the text block
            // precedes them); the summary text goes in front.
            JsonNode result = UapToolResults.Text(sb.ToString());
            for (int i = 0; i < content.Count; i++)
            {
                result.Add(content[i]);
            }
            return result;
        }

        private static UapWebContentKind ApplyAsOverride(string asArg, UapWebContentKind detected, string mime, Uri url)
        {
            switch (asArg)
            {
                case "image":
                    if (detected == UapWebContentKind.Image || detected == UapWebContentKind.ImagePassthrough)
                    {
                        return detected;
                    }
                    throw new InvalidOperationException("as:\"image\" was requested but " + url + " is " + mime
                        + ", not an image the tool recognizes (png, jpeg, gif, webp, bmp).");
                case "text":
                    if (detected == UapWebContentKind.Html || detected == UapWebContentKind.Text)
                    {
                        return detected;
                    }
                    if (detected == UapWebContentKind.Pdf)
                    {
                        // Handled by the PDF branch: text extraction.
                        return detected;
                    }
                    if (detected == UapWebContentKind.Image || detected == UapWebContentKind.ImagePassthrough)
                    {
                        throw new InvalidOperationException("as:\"text\" was requested but " + url + " is " + mime
                            + "; use as:\"auto\" (images come back inline).");
                    }
                    return UapWebContentKind.Text;
                case "file":
                    return UapWebContentKind.Binary;
            }
            return detected;
        }

        private JsonNode DescribeImage(StringBuilder sb, byte[] body, string mime, Uri url, bool returnImage, bool decodable)
        {
            string path = SaveAndPrune(url, mime, body);
            int width;
            int height;
            bool knownSize = UapWebContentKinds.TryReadDimensions(body, out width, out height);
            sb.Append("\nImage");
            if (knownSize)
            {
                sb.Append(' ').Append(width).Append('x').Append(height);
            }
            JsonNode blocks = JsonNode.NewArray();
            if (!returnImage)
            {
                sb.Append(". Saved to ").Append(path).Append(" (not shown inline: return_image is false).");
                return blocks;
            }
            byte[] encoded = null;
            string encodedMime = null;
            int encodedWidth = 0;
            int encodedHeight = 0;
            string encodeError = null;
            if (decodable && _encoder != null)
            {
                if (!_encoder.TryEncode(body, Path.GetFileName(path), out encoded, out encodedMime,
                        out encodedWidth, out encodedHeight, out encodeError))
                {
                    encoded = null;
                }
            }
            if (encoded != null)
            {
                if (encodedWidth > 0 && (encodedWidth != width || encodedHeight != height))
                {
                    sb.Append(", shown at ").Append(encodedWidth).Append('x').Append(encodedHeight);
                }
                sb.Append(". Saved to ").Append(path);
                UapToolResults.AddImage(blocks, encoded, encodedMime);
                return blocks;
            }
            if (body.LongLength <= UapWebFetchPolicy.PassthroughImageMaxBytes)
            {
                sb.Append(", shown as downloaded");
                if (!decodable)
                {
                    sb.Append(" (").Append(mime).Append(" is not re-encoded)");
                }
                sb.Append(". Saved to ").Append(path);
                UapToolResults.AddImage(blocks, body, mime);
                return blocks;
            }
            sb.Append(". Saved to ").Append(path).Append(". Not shown inline: ")
              .Append(UapHttpWebFetcher.FormatBytes(body.LongLength)).Append(" is over the ")
              .Append(UapHttpWebFetcher.FormatBytes(UapWebFetchPolicy.PassthroughImageMaxBytes)).Append(" inline limit");
            if (encodeError != null)
            {
                sb.Append(" and downscaling failed (").Append(encodeError).Append(')');
            }
            sb.Append("; read the file instead.");
            return blocks;
        }

        private void DescribePdf(StringBuilder sb, byte[] body, string mime, Uri url, bool extractText,
            int firstPage, int lastPage, int offset, int maxChars)
        {
            string path = SaveAndPrune(url, mime, body);
            UapPdfText.Result extracted = null;
            if (extractText)
            {
                extracted = UapPdfText.Extract(body, firstPage, lastPage);
            }
            int pages = extracted != null && extracted.PageCount > 0 ? extracted.PageCount : EstimatePdfPageCount(body);
            sb.Append("\nPDF saved to ").Append(path).Append(" (");
            if (pages > 0)
            {
                sb.Append("about ").Append(pages).Append(pages == 1 ? " page, " : " pages, ");
            }
            sb.Append(UapHttpWebFetcher.FormatBytes(body.LongLength)).Append(").");
            if (extracted != null)
            {
                sb.Append("\nText extracted here (best effort; pass pages=\"N-M\" for other pages):\n\n");
                sb.Append(Window(UapPdfText.Render(extracted), offset, maxChars));
                return;
            }
            sb.Append("\nRead it with your file reader (Claude Code: Read with pages=\"1-20\"");
            if (pages > 20)
            {
                sb.Append(", then \"21-").Append(Math.Min(40, pages)).Append('"');
                if (pages > 40)
                {
                    sb.Append(" and so on");
                }
            }
            sb.Append("), or call again with as:\"text\" or pages=\"1-5\" to get the text extracted here.");
        }

        private static readonly Regex PdfCount = new Regex(@"/Count\s+(\d+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The largest /Count in the file: the page tree root's count is the
        /// page total and every intermediate node's count is smaller. A
        /// heuristic (compressed object streams hide the tree), so the
        /// result is reported as "about N pages" and 0 means unknown.
        /// </summary>
        public static int EstimatePdfPageCount(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                return 0;
            }
            string latin1 = Encoding.GetEncoding("iso-8859-1").GetString(body);
            int best = 0;
            foreach (Match m in PdfCount.Matches(latin1))
            {
                int value;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > best)
                {
                    best = value;
                }
            }
            return best;
        }

        private string SaveAndPrune(Uri url, string mime, byte[] body)
        {
            string path = UapWebDownloads.Save(_downloadsRoot, url.AbsoluteUri, mime, body);
            UapWebDownloads.Prune(_downloadsRoot, _utcNow(), UapWebDownloads.MaxAge, UapWebDownloads.MaxTotalBytes, path);
            return path;
        }

        /// <summary>
        /// The [offset, offset+maxChars) slice of <paramref name="text"/>
        /// with a trailer that tells the agent how much there is and what
        /// offset continues it. Pure and public for tests.
        /// </summary>
        public static string Window(string text, int offset, int maxChars)
        {
            text = text ?? string.Empty;
            int total = text.Length;
            if (offset >= total)
            {
                return offset == 0
                    ? "(empty)"
                    : "(offset " + offset + " is past the end; the text is " + total + " chars long)";
            }
            int end = Math.Min(total, offset + maxChars);
            string slice = text.Substring(offset, end - offset);
            if (offset == 0 && end == total)
            {
                return slice;
            }
            return slice + "\n(showing chars " + offset + "-" + end + " of " + total
                + (end < total ? "; call again with offset=" + end + " for more)" : ")");
        }
    }
}
