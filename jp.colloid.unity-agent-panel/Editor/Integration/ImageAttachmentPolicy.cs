using System;
using System.IO;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// The pure rules behind image attachments (design note 2026-09-07
    /// decision I2): what is accepted, how far it is downscaled, which
    /// encoding is used and where the hard limits are. No Unity API, so
    /// ImageAttachmentPolicyTests pin every number here.
    /// </summary>
    public static class ImageAttachmentPolicy
    {
        /// <summary>Long-edge cap in pixels -- the API resizes above this anyway, so sending more only costs.</summary>
        public const int MaxLongEdge = 1568;
        /// <summary>A PNG larger than this is re-encoded as JPEG (photos compress far better).</summary>
        public const long JpegFallbackBytes = 1536L * 1024L;
        /// <summary>JPEG quality for the fallback.</summary>
        public const int JpegQuality = 85;
        /// <summary>Hard cap after encoding (the API's per-image limit).</summary>
        public const long MaxEncodedBytes = 5L * 1024L * 1024L;
        /// <summary>Images per message.</summary>
        public const int MaxPerMessage = 4;

        public const string PngMediaType = "image/png";
        public const string JpegMediaType = "image/jpeg";

        /// <summary>True for the file types ImageConversion.LoadImage decodes (png/jpg/jpeg).</summary>
        public static bool IsSupportedFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                return false;
            }
            switch (ext.ToLowerInvariant())
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Target size after the long-edge cap: unchanged when already
        /// within the cap, otherwise scaled uniformly (never below 1 px).
        /// </summary>
        public static void FitToLongEdge(int width, int height, out int targetWidth, out int targetHeight)
        {
            int longEdge = Math.Max(width, height);
            if (longEdge <= MaxLongEdge || longEdge <= 0)
            {
                targetWidth = Math.Max(1, width);
                targetHeight = Math.Max(1, height);
                return;
            }
            double scale = (double)MaxLongEdge / longEdge;
            targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        }

        /// <summary>PNG unless it is over the JPEG fallback threshold.</summary>
        public static bool ShouldFallBackToJpeg(long pngBytes)
        {
            return pngBytes > JpegFallbackBytes;
        }

        /// <summary>True when the encoded image is over the hard cap.</summary>
        public static bool IsTooLarge(long encodedBytes)
        {
            return encodedBytes > MaxEncodedBytes;
        }

        /// <summary>File extension for a media type ("png"/"jpg").</summary>
        public static string ExtensionFor(string mediaType)
        {
            return string.Equals(mediaType, JpegMediaType, StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
        }

        /// <summary>Store file name: content hash plus the extension, so the same image is stored once.</summary>
        public static string BuildStoreFileName(string sha1Hex, string mediaType)
        {
            if (string.IsNullOrEmpty(sha1Hex))
            {
                throw new ArgumentException("sha1Hex must not be empty.", "sha1Hex");
            }
            return sha1Hex.ToLowerInvariant() + "." + ExtensionFor(mediaType);
        }

        /// <summary>Estimated input tokens for an image of this size (the API's ~w*h/750 rule).</summary>
        public static int EstimateTokens(int width, int height)
        {
            return Math.Max(0, (width * height) / 750);
        }
    }
}
