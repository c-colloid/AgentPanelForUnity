using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Where uap_web_fetch keeps what it downloaded
    /// (<c>Library/AgentPanel/Downloads/</c>, outside version control like
    /// the composer's Attachments store) and how long: the same URL always
    /// lands on the same file name (a URL hash prefix plus the sanitized
    /// last path segment), and a prune pass drops files older than
    /// <see cref="MaxAge"/> or beyond <see cref="MaxTotalBytes"/>, oldest
    /// first (design note 2026-09-17-web-fetch-tool.md section 5.3). Pure
    /// aside from the file system so the naming and pruning rules are
    /// unit-tested in a temp directory.
    /// </summary>
    public static class UapWebDownloads
    {
        public const string RelativeRoot = "Library/AgentPanel/Downloads";
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
        public const long MaxTotalBytes = 200L * 1024L * 1024L;
        private const int HashPrefixChars = 12;
        private const int MaxNameChars = 60;

        /// <summary>Absolute downloads root for a project root.</summary>
        public static string RootFor(string projectRoot)
        {
            return Path.GetFullPath(Path.Combine(projectRoot, RelativeRoot.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>
        /// "&lt;12 hex of sha256(url)&gt;-&lt;name&gt;[.ext]": the hash keys the
        /// URL, the name keeps the file recognizable in a directory listing,
        /// and the extension is taken from the URL or, failing that, from
        /// the media type so the agent's file reader picks the right decoder.
        /// </summary>
        public static string BuildFileName(string url, string mime)
        {
            string hash = Sha256Hex(url ?? string.Empty).Substring(0, HashPrefixChars);
            string name = LastPathSegment(url);
            string ext = Path.GetExtension(name);
            string stem = ext.Length > 0 ? name.Substring(0, name.Length - ext.Length) : name;
            stem = SanitizeFileName(stem);
            if (stem.Length == 0)
            {
                stem = "download";
            }
            ext = SanitizeFileName(ext.TrimStart('.'));
            if (ext.Length == 0 || ext.Length > 8)
            {
                ext = ExtensionOfMime(mime);
            }
            return hash + "-" + stem + (ext.Length > 0 ? "." + ext : string.Empty);
        }

        /// <summary>Keeps letters, digits, '-', '_', '.' and spaces (as '_'); trims to <see cref="MaxNameChars"/>; never starts with '.'.</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                {
                    sb.Append(c);
                }
                else if (c == ' ')
                {
                    sb.Append('_');
                }
            }
            string s = sb.ToString().TrimStart('.');
            if (s.Length > MaxNameChars)
            {
                s = s.Substring(0, MaxNameChars);
            }
            return s;
        }

        public static string ExtensionOfMime(string mime)
        {
            switch (UapWebContentKinds.NormalizeMime(mime))
            {
                case "image/png": return "png";
                case "image/jpeg": return "jpg";
                case "image/gif": return "gif";
                case "image/webp": return "webp";
                case "image/bmp": return "bmp";
                case "image/svg+xml": return "svg";
                case "application/pdf": return "pdf";
                case "text/html": return "html";
                case "text/plain": return "txt";
                case "application/json": return "json";
                case "application/xml":
                case "text/xml": return "xml";
                case "application/zip": return "zip";
                case "application/gzip": return "gz";
            }
            return "bin";
        }

        /// <summary>Writes the body under <paramref name="root"/> (created on demand) and returns the absolute path.</summary>
        public static string Save(string root, string url, string mime, byte[] body)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, BuildFileName(url, mime));
            File.WriteAllBytes(path, body ?? new byte[0]);
            return Path.GetFullPath(path);
        }

        /// <summary>
        /// Deletes files older than <paramref name="maxAge"/> and then, while
        /// the directory still exceeds <paramref name="maxTotalBytes"/>, the
        /// oldest remaining ones. <paramref name="keep"/> (the file just
        /// written) is never deleted. Returns the number removed; a file
        /// that cannot be deleted is skipped, never thrown.
        /// </summary>
        public static int Prune(string root, DateTime nowUtc, TimeSpan maxAge, long maxTotalBytes, string keep)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return 0;
            }
            var files = new List<FileInfo>();
            foreach (string path in Directory.GetFiles(root))
            {
                try
                {
                    files.Add(new FileInfo(path));
                }
                catch (Exception)
                {
                }
            }
            files.Sort(delegate (FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
            string keepFull = keep == null ? null : Path.GetFullPath(keep);
            long total = 0;
            foreach (FileInfo f in files)
            {
                total += f.Length;
            }
            int removed = 0;
            foreach (FileInfo f in files)
            {
                bool isKeep = keepFull != null && string.Equals(Path.GetFullPath(f.FullName), keepFull, StringComparison.OrdinalIgnoreCase);
                if (isKeep)
                {
                    continue;
                }
                bool tooOld = nowUtc - f.LastWriteTimeUtc > maxAge;
                bool overBudget = total > maxTotalBytes;
                if (!tooOld && !overBudget)
                {
                    continue;
                }
                try
                {
                    long size = f.Length;
                    f.Delete();
                    total -= size;
                    removed++;
                }
                catch (Exception)
                {
                }
            }
            return removed;
        }

        private static string LastPathSegment(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }
            Uri uri;
            string path = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.AbsolutePath : url;
            int cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0)
            {
                path = path.Substring(0, cut);
            }
            path = path.TrimEnd('/');
            int slash = path.LastIndexOf('/');
            string segment = slash >= 0 ? path.Substring(slash + 1) : path;
            return Uri.UnescapeDataString(segment);
        }

        public static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
