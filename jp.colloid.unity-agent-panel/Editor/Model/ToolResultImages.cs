using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Finds the pictures a tool_result carries so the transcript can show
    /// them (design note docs/design-notes/2026-09-12-tool-result-image-
    /// preview.md). Two sources, in this order:
    ///
    /// 1. Embedded image blocks -- the API shape the stream carries
    ///    ({"type":"image","source":{"type":"base64","media_type":...,
    ///    "data":...}}: Read on a PNG, uap_editor_screenshot with
    ///    return_image:true, any MCP image-generation tool) and the raw MCP
    ///    shape ({"type":"image","data":...,"mimeType":...}) in case a
    ///    bridge forwards it untranslated. Decoded through the injected
    ///    saver (ImageAttachmentStore.Save in the editor), so the card
    ///    references a file, never bytes.
    /// 2. File paths named in the result TEXT that exist on disk
    ///    (uap_editor_screenshot without return_image: "Captured ... to
    ///    C:/proj/Temp/UapScreenshots/x.png (1920x1080)"; a generator that
    ///    writes a file and reports where). Only consulted when no image
    ///    was embedded: a screenshot returned inline also names its path,
    ///    and the same picture must not appear twice.
    ///
    /// Pure and Unity-free (Core.Json only) so every branch runs under
    /// EditMode tests with no store and no live editor; a failure to
    /// decode or save one picture skips that picture, never the result.
    /// </summary>
    public static class ToolResultImages
    {
        /// <summary>Upper bound per tool_result so a runaway result cannot flood the card.</summary>
        public const int MaxImagesPerResult = 4;

        /// <summary>Largest base64 payload decoded (matches the transcript restore cap).</summary>
        public const int MaxBase64Chars = TranscriptLoader.MaxRestoredImageBase64Chars;

        /// <summary>
        /// Absolute or project-relative paths ending in an extension the
        /// thumbnail cache can decode (ImageConversion.LoadImage reads PNG
        /// and JPEG only). Stops at whitespace and quote/bracket
        /// characters so "to /a/b.png (1920x1080)." yields just the path;
        /// the trailing lookaheads keep "x.png.bak" and "x.pngfoo" out while
        /// still accepting a sentence-ending "x.png.".
        /// </summary>
        private static readonly Regex StrictPathPattern = new Regex(
            @"(?:[A-Za-z]:[\\/]|/|\\\\|(?:Assets|Library|Temp|Packages)[\\/])[^\s""'<>|*?()\[\]]+?\.(?:png|jpe?g)(?![A-Za-z0-9_])(?!\.[A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Fallback for paths WITH spaces ("/Users/me/My Project/Temp/x.png"):
        /// same anchors, but the body may contain spaces and stops only at
        /// quotes, brackets or a line break. Over-matches prose ("see /docs
        /// and /tmp/x.png"), so it is consulted only when no strict match
        /// resolved to an existing file -- a wrong guess never exists on
        /// disk and is dropped.
        /// </summary>
        private static readonly Regex LenientPathPattern = new Regex(
            @"(?:[A-Za-z]:[\\/]|/|\\\\|(?:Assets|Library|Temp|Packages)[\\/])[^""'<>|*?()\[\]\r\n]+?\.(?:png|jpe?g)(?![A-Za-z0-9_])(?!\.[A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Resolves the pictures of one tool_result "content" value (a
        /// string or an array of blocks) to absolute file paths, at most
        /// <see cref="MaxImagesPerResult"/>, in result order, deduplicated.
        /// </summary>
        /// <param name="saver">(bytes, mediaType) -&gt; absolute path of the
        /// stored file; null skips every embedded image.</param>
        /// <param name="resolvePath">Path token found in the text -&gt; absolute
        /// path of an existing file, or null when it does not exist; null
        /// skips the text scan.</param>
        public static List<string> Resolve(JsonNode content,
            Func<byte[], string, string> saver, Func<string, string> resolvePath)
        {
            var paths = new List<string>();
            if (content == null || content.IsNull)
            {
                return paths;
            }
            if (saver != null && content.IsArray)
            {
                foreach (JsonNode item in content.Items)
                {
                    if (paths.Count >= MaxImagesPerResult)
                    {
                        break;
                    }
                    byte[] bytes;
                    string mediaType;
                    if (!TryDecodeEmbedded(item, out bytes, out mediaType))
                    {
                        continue;
                    }
                    string saved;
                    try
                    {
                        saved = saver(bytes, mediaType);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    AddUnique(paths, saved);
                }
            }
            if (paths.Count > 0 || resolvePath == null)
            {
                return paths;
            }
            string text = ExtractText(content);
            ResolveCandidates(FindImagePaths(text, false), resolvePath, paths);
            if (paths.Count == 0)
            {
                ResolveCandidates(FindImagePaths(text, true), resolvePath, paths);
            }
            return paths;
        }

        private static void ResolveCandidates(List<string> candidates, Func<string, string> resolvePath, List<string> paths)
        {
            for (int i = 0; i < candidates.Count && paths.Count < MaxImagesPerResult; i++)
            {
                string resolved;
                try
                {
                    resolved = resolvePath(candidates[i]);
                }
                catch (Exception)
                {
                    continue;
                }
                AddUnique(paths, resolved);
            }
        }

        /// <summary>
        /// Decodes one embedded image block in either wire shape. False for
        /// anything else: a non-image block, a URL source, an unsupported
        /// media type (only PNG/JPEG render as thumbnails), an empty or
        /// oversized payload, or invalid base64.
        /// </summary>
        internal static bool TryDecodeEmbedded(JsonNode item, out byte[] bytes, out string mediaType)
        {
            bytes = null;
            mediaType = null;
            if (item == null || !item.IsObject
                || !string.Equals(item["type"].AsString(string.Empty), "image", StringComparison.Ordinal))
            {
                return false;
            }
            string data;
            string media;
            JsonNode source = item["source"];
            if (source != null && source.IsObject)
            {
                if (!string.Equals(source["type"].AsString(string.Empty), "base64", StringComparison.Ordinal))
                {
                    return false;
                }
                data = source["data"].AsString(null);
                media = source["media_type"].AsString(null);
            }
            else
            {
                data = item["data"].AsString(null);
                media = item["mimeType"].AsString(null) ?? item["media_type"].AsString(null);
            }
            media = NormalizeMediaType(media);
            if (media == null || string.IsNullOrEmpty(data) || data.Length > MaxBase64Chars)
            {
                return false;
            }
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                return false;
            }
            if (bytes.Length == 0)
            {
                bytes = null;
                return false;
            }
            mediaType = media;
            return true;
        }

        /// <summary>"image/png" or "image/jpeg" (accepting "image/jpg"); null for anything else.</summary>
        internal static string NormalizeMediaType(string media)
        {
            if (string.IsNullOrEmpty(media))
            {
                return null;
            }
            string trimmed = media.Trim().ToLowerInvariant();
            if (trimmed == "image/png")
            {
                return "image/png";
            }
            if (trimmed == "image/jpeg" || trimmed == "image/jpg")
            {
                return "image/jpeg";
            }
            return null;
        }

        /// <summary>Every PNG/JPEG path token in <paramref name="text"/>, in order, forward slashes, deduplicated.</summary>
        internal static List<string> FindImagePaths(string text)
        {
            return FindImagePaths(text, false);
        }

        /// <summary>
        /// Longest path token looked at. The scan below only ever hands the
        /// regex a window of at most this many characters in front of an
        /// image extension, which is what keeps it linear in the text
        /// length (design note 2026-09-17-modal-menu-and-base64-scan
        /// section 2).
        /// </summary>
        internal const int MaxPathChars = 1024;

        /// <summary>
        /// <paramref name="lenient"/> allows spaces inside the path (see
        /// LenientPathPattern). Never runs a pattern over the whole text:
        /// both patterns start at every '/' and lazily walk to the next
        /// stop character, so a result that carries a base64 picture AS
        /// TEXT (Codex hands an MCP result over as one JSON string; base64
        /// has a '/' every ~64 characters and no stop character at all)
        /// cost O(n^2) -- 431 s of blocked Editor for a 498 KB screenshot,
        /// measured. Instead every ".png"/".jpg"/".jpeg" occurrence (base64
        /// has no '.') gets a window reaching back to the nearest character
        /// no path can contain, at most <see cref="MaxPathChars"/>, and the
        /// pattern runs inside that window only.
        /// </summary>
        internal static List<string> FindImagePaths(string text, bool lenient)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            Regex pattern = lenient ? LenientPathPattern : StrictPathPattern;
            // Where the previous match ended: a later window never reaches
            // back over it, the same as one left-to-right pass would not.
            int cursor = 0;
            int dot = text.IndexOf('.');
            while (dot >= 0)
            {
                int extensionEnd = ImageExtensionEnd(text, dot);
                if (extensionEnd > 0 && dot >= cursor)
                {
                    int start = dot;
                    while (start > cursor && dot - start < MaxPathChars && !IsPathStop(text[start - 1], lenient))
                    {
                        start--;
                    }
                    // Two characters of context for the patterns' lookaheads.
                    int end = Math.Min(text.Length, extensionEnd + 2);
                    Match match = pattern.Match(text, start, end - start);
                    while (match.Success)
                    {
                        AddUnique(found, match.Value.Replace('\\', '/'));
                        cursor = match.Index + match.Length;
                        match = match.NextMatch();
                    }
                }
                dot = text.IndexOf('.', dot + 1);
            }
            return found;
        }

        /// <summary>Index just past ".png" / ".jpg" / ".jpeg" (any case) at <paramref name="dot"/>, or -1.</summary>
        private static int ImageExtensionEnd(string text, int dot)
        {
            if (string.Compare(text, dot, ".png", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                || string.Compare(text, dot, ".jpg", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return dot + 4;
            }
            if (string.Compare(text, dot, ".jpeg", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return dot + 5;
            }
            return -1;
        }

        /// <summary>The characters neither pattern's path body can contain: no match spans one.</summary>
        private static bool IsPathStop(char c, bool lenient)
        {
            switch (c)
            {
                case '"':
                case '\'':
                case '<':
                case '>':
                case '|':
                case '*':
                case '?':
                case '(':
                case ')':
                case '[':
                case ']':
                case '\r':
                case '\n':
                    return true;
                default:
                    return !lenient && char.IsWhiteSpace(c);
            }
        }

        /// <summary>Concatenated text of a string content or of every text block in an array content.</summary>
        internal static string ExtractText(JsonNode content)
        {
            if (content == null)
            {
                return string.Empty;
            }
            if (content.IsString)
            {
                return content.AsString(string.Empty);
            }
            if (!content.IsArray)
            {
                return string.Empty;
            }
            var sb = new System.Text.StringBuilder();
            foreach (JsonNode item in content.Items)
            {
                if (item.IsObject
                    && string.Equals(item["type"].AsString(string.Empty), "text", StringComparison.Ordinal))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(item["text"].AsString(string.Empty));
                }
            }
            return sb.ToString();
        }

        private static void AddUnique(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            string normalized = path.Replace('\\', '/');
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            list.Add(normalized);
        }
    }
}
