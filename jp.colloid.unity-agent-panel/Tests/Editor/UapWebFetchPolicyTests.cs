using System;
using System.Net;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The allow/deny surface of uap_web_fetch (design note
    /// 2026-09-17-web-fetch-tool.md section 5.2): only public http/https
    /// destinations, checked on every hop. No network involved.
    /// </summary>
    [TestFixture]
    public class UapWebFetchPolicyTests
    {
        [TestCase("https://example.com/a.png")]
        [TestCase("http://example.com")]
        [TestCase("  https://docs.unity3d.com/Manual/index.html?x=1#frag  ")]
        public void TryParseUrl_PublicHttp_Accepted(string url)
        {
            Uri uri;
            string error;
            Assert.IsTrue(UapWebFetchPolicy.TryParseUrl(url, out uri, out error), error);
            Assert.IsNotNull(uri);
        }

        [TestCase(null, "required")]
        [TestCase("", "required")]
        [TestCase("not a url", "absolute")]
        // No leading slash: on Linux/macOS "/x" parses as an implicit file: URI and fails on the scheme instead.
        [TestCase("relative/path.png", "absolute")]
        [TestCase("ftp://example.com/x", "http and https")]
        [TestCase("file:///C:/secrets.txt", "http and https")]
        [TestCase("data:image/png;base64,AAAA", "http and https")]
        [TestCase("https://user:pw@example.com/", "credentials")]
        [TestCase("http://localhost:7777/mcp", "local")]
        [TestCase("http://127.0.0.1/", "local")]
        [TestCase("http://[::1]/", "local")]
        [TestCase("http://192.168.1.10/", "local")]
        [TestCase("http://169.254.169.254/latest/meta-data/", "local")]
        [TestCase("http://printer.local/", "local")]
        public void TryParseUrl_Refused(string url, string messageFragment)
        {
            Uri uri;
            string error;
            Assert.IsFalse(UapWebFetchPolicy.TryParseUrl(url, out uri, out error));
            Assert.IsNull(uri);
            StringAssert.Contains(messageFragment, error);
        }

        [TestCase("127.0.0.1")]
        [TestCase("127.255.255.254")]
        [TestCase("10.0.0.1")]
        [TestCase("172.16.0.1")]
        [TestCase("172.31.255.255")]
        [TestCase("192.168.0.1")]
        [TestCase("169.254.169.254")]
        [TestCase("100.64.0.1")]
        [TestCase("0.0.0.0")]
        [TestCase("224.0.0.1")]
        [TestCase("255.255.255.255")]
        [TestCase("::1")]
        [TestCase("::")]
        [TestCase("fe80::1")]
        [TestCase("fc00::1")]
        [TestCase("fd12:3456::1")]
        [TestCase("ff02::1")]
        [TestCase("::ffff:127.0.0.1")]
        [TestCase("::ffff:10.1.2.3")]
        public void IsBlockedAddress_PrivateAndLocal_Blocked(string address)
        {
            Assert.IsTrue(UapWebFetchPolicy.IsBlockedAddress(IPAddress.Parse(address)), address);
        }

        [TestCase("8.8.8.8")]
        [TestCase("1.1.1.1")]
        [TestCase("172.15.0.1")]
        [TestCase("172.32.0.1")]
        [TestCase("100.63.0.1")]
        [TestCase("100.128.0.1")]
        [TestCase("192.167.0.1")]
        [TestCase("2606:4700:4700::1111")]
        [TestCase("::ffff:8.8.8.8")]
        public void IsBlockedAddress_Public_Allowed(string address)
        {
            Assert.IsFalse(UapWebFetchPolicy.IsBlockedAddress(IPAddress.Parse(address)), address);
        }

        [Test]
        public void IsBlockedAddress_Null_Blocked()
        {
            Assert.IsTrue(UapWebFetchPolicy.IsBlockedAddress(null));
        }

        [TestCase("localhost")]
        [TestCase("LOCALHOST.")]
        [TestCase("api.localhost")]
        [TestCase("nas.local")]
        [TestCase("db.internal")]
        [TestCase("[::1]")]
        [TestCase("")]
        public void IsBlockedHostName_LocalNames_Blocked(string host)
        {
            Assert.IsTrue(UapWebFetchPolicy.IsBlockedHostName(host), host);
        }

        [TestCase("example.com")]
        [TestCase("localhost.example.com")]
        [TestCase("internal.example.com")]
        [TestCase("8.8.8.8")]
        public void IsBlockedHostName_PublicNames_Allowed(string host)
        {
            Assert.IsFalse(UapWebFetchPolicy.IsBlockedHostName(host), host);
        }

        [Test]
        public void TryResolveRedirect_RelativeAndAbsolute()
        {
            var current = new Uri("https://example.com/a/b.html");
            Uri target;
            Assert.IsTrue(UapWebFetchPolicy.TryResolveRedirect(current, "/img/x.png", out target));
            Assert.AreEqual("https://example.com/img/x.png", target.AbsoluteUri);
            Assert.IsTrue(UapWebFetchPolicy.TryResolveRedirect(current, "c.html", out target));
            Assert.AreEqual("https://example.com/a/c.html", target.AbsoluteUri);
            Assert.IsTrue(UapWebFetchPolicy.TryResolveRedirect(current, "http://other.example/z", out target));
            Assert.AreEqual("http://other.example/z", target.AbsoluteUri);
            Assert.IsFalse(UapWebFetchPolicy.TryResolveRedirect(current, "   ", out target));
            Assert.IsFalse(UapWebFetchPolicy.TryResolveRedirect(null, "/x", out target));
        }

        [Test]
        public void Constants_MatchTheDesignNote()
        {
            Assert.AreEqual(20L * 1024 * 1024, UapWebFetchPolicy.MaxBytes);
            Assert.AreEqual(20000, UapWebFetchPolicy.TimeoutMillis);
            Assert.AreEqual(5, UapWebFetchPolicy.MaxRedirects);
            Assert.AreEqual(60000, UapWebFetchPolicy.DefaultMaxChars);
            // Same inline cap as a composer attachment.
            Assert.AreEqual(Colloid.AgentPanel.Integration.ImageAttachmentPolicy.MaxEncodedBytes,
                UapWebFetchPolicy.PassthroughImageMaxBytes);
        }
    }
}
