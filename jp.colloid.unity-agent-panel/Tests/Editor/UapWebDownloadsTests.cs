using System;
using System.IO;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// File naming and pruning of uap_web_fetch's download store (design
    /// note 2026-09-17-web-fetch-tool.md section 5.3), in a temp directory.
    /// </summary>
    [TestFixture]
    public class UapWebDownloadsTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "uap-web-downloads-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void BuildFileName_HashPrefix_NameAndExtensionFromUrl()
        {
            string name = UapWebDownloads.BuildFileName("https://example.com/tex/Cobble%20Stone.PNG?v=2", "image/png");
            StringAssert.IsMatch("^[0-9a-f]{12}-Cobble_Stone\\.PNG$", name);
            string again = UapWebDownloads.BuildFileName("https://example.com/tex/Cobble%20Stone.PNG?v=2", "image/png");
            Assert.AreEqual(name, again, "same URL, same file");
            string other = UapWebDownloads.BuildFileName("https://example.com/tex/Cobble%20Stone.PNG?v=3", "image/png");
            Assert.AreNotEqual(name, other, "the query string is part of the URL");
        }

        [Test]
        public void BuildFileName_ExtensionFromMimeWhenUrlHasNone()
        {
            Assert.That(UapWebDownloads.BuildFileName("https://example.com/paper", "application/pdf"), Does.EndWith("-paper.pdf"));
            Assert.That(UapWebDownloads.BuildFileName("https://example.com/", "image/jpeg"), Does.EndWith("-download.jpg"));
            Assert.That(UapWebDownloads.BuildFileName("https://example.com/api/thing", "application/x-unknown"), Does.EndWith("-thing.bin"));
            Assert.That(UapWebDownloads.BuildFileName("https://example.com/a/b/", "text/html"), Does.EndWith("-b.html"));
        }

        [Test]
        public void SanitizeFileName_DropsUnsafeCharacters_Truncates()
        {
            Assert.AreEqual("a_b-c.d", UapWebDownloads.SanitizeFileName("a b-c.d"));
            Assert.AreEqual("etcpasswd", UapWebDownloads.SanitizeFileName("../../etc/passwd"), "separators and leading dots go; BuildFileName only ever passes the last segment anyway");
            Assert.AreEqual("hidden", UapWebDownloads.SanitizeFileName(".hidden"));
            Assert.AreEqual("日本語", UapWebDownloads.SanitizeFileName("日本語"));
            Assert.AreEqual(60, UapWebDownloads.SanitizeFileName(new string('x', 200)).Length);
            Assert.AreEqual(string.Empty, UapWebDownloads.SanitizeFileName("<>:\"/\\|?*"));
        }

        [Test]
        public void Save_WritesUnderRoot_CreatesDirectory()
        {
            string path = UapWebDownloads.Save(_root, "https://example.com/x.txt", "text/plain", new byte[] { 65, 66 });
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(Path.GetFullPath(_root), Path.GetDirectoryName(path));
            Assert.AreEqual("AB", File.ReadAllText(path));
        }

        [Test]
        public void Prune_RemovesOld_ThenOldestOverBudget_NeverTheKeptFile()
        {
            Directory.CreateDirectory(_root);
            DateTime now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            string old = Write("old.bin", 10, now - TimeSpan.FromDays(8));
            string a = Write("a.bin", 100, now - TimeSpan.FromDays(3));
            string b = Write("b.bin", 100, now - TimeSpan.FromDays(2));
            string c = Write("c.bin", 100, now - TimeSpan.FromDays(1));
            string keep = Write("keep.bin", 100, now - TimeSpan.FromDays(6));

            int removed = UapWebDownloads.Prune(_root, now, TimeSpan.FromDays(7), 250, keep);

            Assert.AreEqual(3, removed);
            Assert.IsFalse(File.Exists(old), "past max age");
            Assert.IsFalse(File.Exists(a), "oldest over budget");
            Assert.IsFalse(File.Exists(b), "still over budget after a");
            Assert.IsTrue(File.Exists(c));
            Assert.IsTrue(File.Exists(keep), "the file just written is never pruned, even though it is older than the others");
        }

        [Test]
        public void Prune_MissingDirectory_IsZero()
        {
            Assert.AreEqual(0, UapWebDownloads.Prune(Path.Combine(_root, "nope"), DateTime.UtcNow, TimeSpan.FromDays(7), 1, null));
        }

        private string Write(string name, int bytes, DateTime lastWriteUtc)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }
    }
}
