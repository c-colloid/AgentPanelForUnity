using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Where encoded image attachments live (design note 2026-09-07
    /// decision I3): Library/AgentPanel/Attachments/&lt;sha1&gt;.&lt;ext&gt;.
    /// Library/ is per-project scratch that is never versioned or
    /// asset-imported, and the content hash makes re-attaching the same
    /// image a no-op. Files older than <see cref="RetentionDays"/> are
    /// removed on editor load; a message that still references a removed
    /// file is sent without it (AgentHub appends a note).
    /// </summary>
    public static class ImageAttachmentStore
    {
        public const int RetentionDays = 7;

        private static string _rootOverride;

        /// <summary>The store directory (created on demand).</summary>
        public static string Root
        {
            get
            {
                if (!string.IsNullOrEmpty(_rootOverride))
                {
                    return _rootOverride;
                }
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(Path.Combine(Path.Combine(projectRoot, "Library"), "AgentPanel"), "Attachments");
            }
        }

        /// <summary>Writes <paramref name="bytes"/> under its content hash; returns the absolute path.</summary>
        public static string Save(byte[] bytes, string mediaType)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("bytes must not be empty.", "bytes");
            }
            Directory.CreateDirectory(Root);
            string path = Path.Combine(Root, ImageAttachmentPolicy.BuildStoreFileName(Sha1Hex(bytes), mediaType));
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, bytes);
            }
            else
            {
                // Refresh the timestamp so retention counts from the last use.
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception) { }
            }
            return path.Replace('\\', '/');
        }

        public static string Sha1Hex(byte[] bytes)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>Deletes store files not written in the last <see cref="RetentionDays"/>. Returns the count removed.</summary>
        public static int Cleanup(DateTime utcNow)
        {
            string root = Root;
            if (!Directory.Exists(root))
            {
                return 0;
            }
            int removed = 0;
            DateTime cutoff = utcNow.AddDays(-RetentionDays);
            foreach (string file in Directory.GetFiles(root))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception)
                {
                    // A locked or vanished file is not worth a warning.
                }
            }
            return removed;
        }

        [InitializeOnLoadMethod]
        private static void CleanupOnLoad()
        {
            // History restore writes the transcript's base64 images back
            // through the same store (TranscriptLoader stays Unity-free).
            Colloid.AgentPanel.Model.TranscriptLoader.ImageSaver = Save;
            try
            {
                Cleanup(DateTime.UtcNow);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Test seam: redirects the store to a scratch directory (null restores the default).</summary>
        internal static void SetRootForTests(string root)
        {
            _rootOverride = root;
        }
    }
}
