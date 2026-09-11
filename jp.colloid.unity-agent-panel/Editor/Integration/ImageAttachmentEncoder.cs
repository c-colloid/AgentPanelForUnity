using System;
using System.IO;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using UnityEngine;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Turns a file, a texture or an editor view into a send-ready
    /// <see cref="ImageAttachment"/> (design note 2026-09-07 section
    /// 2.3.3): decode, downscale to the long-edge cap on the CPU (no
    /// RenderTexture, so it works headless and deterministically), encode
    /// PNG -- or JPEG when the PNG is over the fallback threshold -- refuse
    /// anything over the hard cap, and store under the content hash.
    /// Errors come back as user-facing text, never exceptions.
    /// </summary>
    public static class ImageAttachmentEncoder
    {
        /// <summary>Encodes an image file (png/jpg) from disk.</summary>
        public static bool TryImportFile(string sourcePath, out ImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            if (!ImageAttachmentPolicy.IsSupportedFile(sourcePath))
            {
                error = L10n.F(L10n.S.AttachImageUnsupportedFmt, Path.GetFileName(sourcePath ?? string.Empty));
                return false;
            }
            if (!File.Exists(sourcePath))
            {
                error = L10n.F(L10n.S.AttachImageMissingFmt, sourcePath);
                return false;
            }
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(sourcePath);
            }
            catch (Exception e)
            {
                error = L10n.F(L10n.S.AttachImageLoadFailedFmt, Path.GetFileName(sourcePath), e.Message);
                return false;
            }
            return TryImportBytes(raw, Path.GetFileName(sourcePath), out attachment, out error);
        }

        /// <summary>Encodes raw PNG/JPEG bytes (a file's content, or a fresh capture).</summary>
        public static bool TryImportBytes(byte[] raw, string sourceName, out ImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            if (raw == null || raw.Length == 0)
            {
                error = L10n.F(L10n.S.AttachImageLoadFailedFmt, sourceName, "empty");
                return false;
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, raw, false) || texture.width < 1 || texture.height < 1)
                {
                    error = L10n.F(L10n.S.AttachImageLoadFailedFmt, sourceName, "not a PNG/JPEG the editor can decode");
                    return false;
                }
                return TryImportTexture(texture, sourceName, out attachment, out error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>Encodes a readable texture (already in memory). The texture is not modified.</summary>
        public static bool TryImportTexture(Texture2D texture, string sourceName, out ImageAttachment attachment, out string error)
        {
            attachment = null;
            error = null;
            if (texture == null)
            {
                error = L10n.F(L10n.S.AttachImageLoadFailedFmt, sourceName, "no texture");
                return false;
            }
            int targetWidth, targetHeight;
            ImageAttachmentPolicy.FitToLongEdge(texture.width, texture.height, out targetWidth, out targetHeight);
            Texture2D working = texture;
            bool ownsWorking = false;
            try
            {
                if (targetWidth != texture.width || targetHeight != texture.height)
                {
                    working = Resize(texture, targetWidth, targetHeight);
                    ownsWorking = true;
                }
                byte[] encoded = working.EncodeToPNG();
                string mediaType = ImageAttachmentPolicy.PngMediaType;
                if (encoded == null || ImageAttachmentPolicy.ShouldFallBackToJpeg(encoded.Length))
                {
                    byte[] jpeg = working.EncodeToJPG(ImageAttachmentPolicy.JpegQuality);
                    if (jpeg != null && (encoded == null || jpeg.Length < encoded.Length))
                    {
                        encoded = jpeg;
                        mediaType = ImageAttachmentPolicy.JpegMediaType;
                    }
                }
                if (encoded == null || encoded.Length == 0)
                {
                    error = L10n.F(L10n.S.AttachImageLoadFailedFmt, sourceName, "encoding failed");
                    return false;
                }
                if (ImageAttachmentPolicy.IsTooLarge(encoded.Length))
                {
                    error = L10n.F(L10n.S.AttachImageTooLargeFmt, sourceName,
                        (encoded.Length / (1024.0 * 1024.0)).ToString("F1"),
                        ImageAttachmentPolicy.MaxEncodedBytes / (1024 * 1024));
                    return false;
                }
                attachment = new ImageAttachment
                {
                    path = ImageAttachmentStore.Save(encoded, mediaType),
                    mediaType = mediaType,
                    width = working.width,
                    height = working.height,
                    bytes = encoded.Length,
                    sourceName = sourceName ?? string.Empty
                };
                return true;
            }
            finally
            {
                if (ownsWorking && working != null)
                {
                    UnityEngine.Object.DestroyImmediate(working);
                }
            }
        }

        /// <summary>
        /// Captures an editor view through the same code uap_editor_screenshot
        /// uses (UapScreenCapture) and encodes the PNG it wrote.
        /// </summary>
        public static bool TryCaptureView(UapScreenshotView view, UapScreenshotCapture capture,
            out ImageAttachment attachment, out string error)
        {
            attachment = null;
            string tempPath = Path.Combine(Path.GetTempPath(), "uap-attach-" + Guid.NewGuid().ToString("N") + ".png");
            int width, height;
            string description;
            if (!UapScreenCapture.TryCaptureView(view, capture, tempPath, out width, out height, out description, out error))
            {
                return false;
            }
            try
            {
                return TryImportBytes(File.ReadAllBytes(tempPath), description, out attachment, out error);
            }
            finally
            {
                try { File.Delete(tempPath); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Bilinear CPU resample into a new RGBA32 texture. Pure pixel
        /// math on GetPixels32 arrays, so it needs no graphics device.
        /// </summary>
        public static Texture2D Resize(Texture2D source, int width, int height)
        {
            Color32[] src = source.GetPixels32();
            int sw = source.width;
            int sh = source.height;
            var dst = new Color32[width * height];
            float sx = (float)sw / width;
            float sy = (float)sh / height;
            for (int y = 0; y < height; y++)
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, sh - 1);
                int y1 = Mathf.Min(y0 + 1, sh - 1);
                float ty = Mathf.Clamp01(fy - y0);
                for (int x = 0; x < width; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, sw - 1);
                    int x1 = Mathf.Min(x0 + 1, sw - 1);
                    float tx = Mathf.Clamp01(fx - x0);
                    Color32 a = src[y0 * sw + x0];
                    Color32 b = src[y0 * sw + x1];
                    Color32 c = src[y1 * sw + x0];
                    Color32 d = src[y1 * sw + x1];
                    dst[y * width + x] = new Color32(
                        Lerp2(a.r, b.r, c.r, d.r, tx, ty),
                        Lerp2(a.g, b.g, c.g, d.g, tx, ty),
                        Lerp2(a.b, b.b, c.b, d.b, tx, ty),
                        Lerp2(a.a, b.a, c.a, d.a, tx, ty));
                }
            }
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.SetPixels32(dst);
            result.Apply(false, false);
            return result;
        }

        private static byte Lerp2(byte a, byte b, byte c, byte d, float tx, float ty)
        {
            float top = a + (b - a) * tx;
            float bottom = c + (d - c) * tx;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(top + (bottom - top) * ty), 0, 255);
        }
    }
}
