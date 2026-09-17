using System;
using System.Text;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>How uap_web_fetch hands a body to the model (design note 2026-09-17-web-fetch-tool.md section 4).</summary>
    public enum UapWebContentKind
    {
        /// <summary>PNG / JPEG: decodable by the editor, re-encoded to the attachment size and sent inline.</summary>
        Image,
        /// <summary>GIF / WebP / BMP: the editor cannot decode it; sent as-is when small enough, else saved only.</summary>
        ImagePassthrough,
        /// <summary>PDF: saved to a file the agent reads with its own file reader.</summary>
        Pdf,
        /// <summary>HTML: reduced to readable text plus the page's image and link URLs.</summary>
        Html,
        /// <summary>Any other text (plain, JSON, XML, SVG, CSV, Markdown...): returned as text.</summary>
        Text,
        /// <summary>Anything else: saved to a file, path returned.</summary>
        Binary
    }

    /// <summary>
    /// Decides the <see cref="UapWebContentKind"/> of a response from, in
    /// order, the body's magic number (a PNG served as
    /// application/octet-stream is still a PNG), the Content-Type header,
    /// the URL path's extension, and finally a text-vs-binary sniff. Pure.
    /// </summary>
    public static class UapWebContentKinds
    {
        public static UapWebContentKind Classify(string contentType, byte[] body, string urlPath, out string mime)
        {
            string headerMime = NormalizeMime(contentType);
            string sniffed = SniffMime(body);
            if (sniffed != null)
            {
                mime = sniffed;
                return KindOfMime(sniffed);
            }
            if (!string.IsNullOrEmpty(headerMime) && headerMime != "application/octet-stream"
                && headerMime != "binary/octet-stream")
            {
                UapWebContentKind byHeader = KindOfMime(headerMime);
                if (byHeader != UapWebContentKind.Binary)
                {
                    mime = headerMime;
                    return byHeader;
                }
                if (LooksLikeHtml(body))
                {
                    mime = "text/html";
                    return UapWebContentKind.Html;
                }
                mime = headerMime;
                return UapWebContentKind.Binary;
            }
            string byExtension = MimeOfExtension(urlPath);
            if (byExtension != null)
            {
                mime = byExtension;
                return KindOfMime(byExtension);
            }
            if (LooksLikeHtml(body))
            {
                mime = "text/html";
                return UapWebContentKind.Html;
            }
            if (LooksLikeText(body))
            {
                mime = "text/plain";
                return UapWebContentKind.Text;
            }
            mime = string.IsNullOrEmpty(headerMime) ? "application/octet-stream" : headerMime;
            return UapWebContentKind.Binary;
        }

        /// <summary>Lower-cased media type without parameters ("text/html; charset=utf-8" -> "text/html"); null for empty.</summary>
        public static string NormalizeMime(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return null;
            }
            string s = contentType;
            int semi = s.IndexOf(';');
            if (semi >= 0)
            {
                s = s.Substring(0, semi);
            }
            s = s.Trim().ToLowerInvariant();
            return s.Length == 0 ? null : s;
        }

        /// <summary>The charset parameter of a Content-Type header, or null.</summary>
        public static string CharsetOf(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return null;
            }
            int idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }
            string rest = contentType.Substring(idx + "charset=".Length).Trim();
            int end = rest.IndexOfAny(new[] { ';', ' ', '\t' });
            if (end >= 0)
            {
                rest = rest.Substring(0, end);
            }
            rest = rest.Trim('"', '\'');
            return rest.Length == 0 ? null : rest;
        }

        /// <summary>
        /// Decodes a text body: BOM first, then the header charset, then
        /// UTF-8. An unknown charset name falls back to UTF-8 rather than
        /// failing the whole fetch.
        /// </summary>
        public static string DecodeText(byte[] body, string contentType)
        {
            if (body == null || body.Length == 0)
            {
                return string.Empty;
            }
            Encoding encoding = null;
            int skip = 0;
            if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            {
                encoding = Encoding.UTF8;
                skip = 3;
            }
            else if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                skip = 2;
            }
            else if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                skip = 2;
            }
            if (encoding == null)
            {
                string charset = CharsetOf(contentType);
                if (charset != null)
                {
                    try
                    {
                        encoding = Encoding.GetEncoding(charset);
                    }
                    catch (ArgumentException)
                    {
                        encoding = null;
                    }
                }
            }
            if (encoding == null)
            {
                encoding = Encoding.UTF8;
            }
            return encoding.GetString(body, skip, body.Length - skip);
        }

        public static UapWebContentKind KindOfMime(string mime)
        {
            if (string.IsNullOrEmpty(mime))
            {
                return UapWebContentKind.Binary;
            }
            switch (mime)
            {
                case "image/png":
                case "image/jpeg":
                    return UapWebContentKind.Image;
                case "image/gif":
                case "image/webp":
                case "image/bmp":
                    return UapWebContentKind.ImagePassthrough;
                case "application/pdf":
                    return UapWebContentKind.Pdf;
                case "text/html":
                case "application/xhtml+xml":
                    return UapWebContentKind.Html;
                case "application/json":
                case "application/xml":
                case "application/javascript":
                case "application/x-javascript":
                case "application/x-yaml":
                case "application/yaml":
                case "image/svg+xml":
                    return UapWebContentKind.Text;
            }
            if (mime.StartsWith("text/", StringComparison.Ordinal)
                || mime.EndsWith("+json", StringComparison.Ordinal)
                || mime.EndsWith("+xml", StringComparison.Ordinal))
            {
                return UapWebContentKind.Text;
            }
            return UapWebContentKind.Binary;
        }

        /// <summary>Media type from the body's first bytes for the formats we recognize; null when unknown.</summary>
        public static string SniffMime(byte[] b)
        {
            if (b == null || b.Length < 4)
            {
                return null;
            }
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                return "image/png";
            }
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
            {
                return "image/gif";
            }
            if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            {
                return "image/webp";
            }
            if (b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F')
            {
                return "application/pdf";
            }
            return null;
        }

        /// <summary>Media type from the URL path's extension for the formats we route specially; null otherwise.</summary>
        public static string MimeOfExtension(string urlPath)
        {
            if (string.IsNullOrEmpty(urlPath))
            {
                return null;
            }
            int query = urlPath.IndexOfAny(new[] { '?', '#' });
            string path = query >= 0 ? urlPath.Substring(0, query) : urlPath;
            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');
            if (dot < 0 || dot < slash)
            {
                return null;
            }
            switch (path.Substring(dot + 1).ToLowerInvariant())
            {
                case "png": return "image/png";
                case "jpg":
                case "jpeg": return "image/jpeg";
                case "gif": return "image/gif";
                case "webp": return "image/webp";
                case "bmp": return "image/bmp";
                case "pdf": return "application/pdf";
                case "html":
                case "htm": return "text/html";
                case "txt": return "text/plain";
                case "md": return "text/markdown";
                case "csv": return "text/csv";
                case "json": return "application/json";
                case "xml": return "application/xml";
                case "svg": return "image/svg+xml";
                case "yaml":
                case "yml": return "application/yaml";
            }
            return null;
        }

        /// <summary>True when the first bytes read as an HTML document (BOM / whitespace allowed before the tag).</summary>
        public static bool LooksLikeHtml(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                return false;
            }
            int n = Math.Min(body.Length, 1024);
            string head = Encoding.ASCII.GetString(body, 0, n).TrimStart((char)0xFEFF, '\0', ' ', '\t', '\r', '\n', '?');
            string lower = head.ToLowerInvariant();
            return lower.StartsWith("<!doctype html", StringComparison.Ordinal)
                || lower.StartsWith("<html", StringComparison.Ordinal)
                || lower.StartsWith("<head", StringComparison.Ordinal)
                || lower.StartsWith("<body", StringComparison.Ordinal);
        }

        /// <summary>True when the first bytes hold no NUL and few control characters -- a plain-text heuristic.</summary>
        public static bool LooksLikeText(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                return true;
            }
            int n = Math.Min(body.Length, 512);
            int control = 0;
            for (int i = 0; i < n; i++)
            {
                byte c = body[i];
                if (c == 0)
                {
                    return false;
                }
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r' && c != 0x0C)
                {
                    control++;
                }
            }
            return control * 10 < n || n < 8;
        }

        /// <summary>
        /// Reads the pixel size out of a PNG (IHDR), GIF (logical screen) or
        /// baseline/progressive JPEG (first SOF marker) header without
        /// decoding the image. False for other formats or a damaged header.
        /// </summary>
        public static bool TryReadDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (b == null || b.Length < 10)
            {
                return false;
            }
            if (b[0] == 0x89 && b[1] == 0x50 && b.Length >= 24)
            {
                width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return width > 0 && height > 0;
            }
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            {
                width = b[6] | (b[7] << 8);
                height = b[8] | (b[9] << 8);
                return width > 0 && height > 0;
            }
            if (b[0] == 0xFF && b[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }
                    byte marker = b[i + 1];
                    if (marker == 0xFF)
                    {
                        i++;
                        continue;
                    }
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        i += 2;
                        continue;
                    }
                    int length = (b[i + 2] << 8) | b[i + 3];
                    bool sof = (marker >= 0xC0 && marker <= 0xCF) && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (sof)
                    {
                        height = (b[i + 5] << 8) | b[i + 6];
                        width = (b[i + 7] << 8) | b[i + 8];
                        return width > 0 && height > 0;
                    }
                    if (marker == 0xDA || length < 2)
                    {
                        return false;
                    }
                    i += 2 + length;
                }
            }
            return false;
        }
    }
}
