using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The user's host allow / deny lists for uap_web_fetch (design note
    /// 2026-09-17-web-fetch-tool.md section 5.2, stage 2): entry
    /// normalization, subdomain matching, deny-over-allow, empty allow
    /// list = everything public.
    /// </summary>
    [TestFixture]
    public class UapWebHostRulesTests
    {
        [Test]
        public void Normalize_TrimsLowercasesStripsWildcardSchemeAndComments()
        {
            string[] entries = UapWebHostRules.Normalize(new[]
            {
                "  Example.COM  ", "*.cdn.example", "https://Docs.Unity3D.com/Manual/x.html", "# a comment",
                "github.com # trailing comment", "", null, "example.com.", "example.com"
            });
            CollectionAssert.AreEqual(new[] { "example.com", "cdn.example", "docs.unity3d.com", "github.com" }, entries);
        }

        [Test]
        public void EmptyLists_AllowEverything()
        {
            string reason;
            Assert.IsTrue(UapWebHostRules.AllowAll.IsAllowed("anything.example", out reason));
            Assert.IsNull(reason);
            Assert.IsFalse(UapWebHostRules.AllowAll.HasAllowList);
        }

        [Test]
        public void AllowList_HostAndSubdomainsOnly()
        {
            var rules = new UapWebHostRules(new[] { "example.com", "*.unity3d.com" }, null);
            string reason;
            Assert.IsTrue(rules.IsAllowed("example.com", out reason));
            Assert.IsTrue(rules.IsAllowed("EXAMPLE.com.", out reason));
            Assert.IsTrue(rules.IsAllowed("cdn.assets.example.com", out reason));
            Assert.IsTrue(rules.IsAllowed("docs.unity3d.com", out reason));
            Assert.IsFalse(rules.IsAllowed("notexample.com", out reason), "suffix without a dot boundary is a different host");
            StringAssert.Contains("only allow example.com, unity3d.com", reason);
            Assert.IsFalse(rules.IsAllowed("example.com.evil.test", out reason));
        }

        [Test]
        public void DenyList_WinsOverAllow()
        {
            var rules = new UapWebHostRules(new[] { "example.com" }, new[] { "tracker.example.com" });
            string reason;
            Assert.IsTrue(rules.IsAllowed("www.example.com", out reason));
            Assert.IsFalse(rules.IsAllowed("tracker.example.com", out reason));
            StringAssert.Contains("blocked-hosts list", reason);
            StringAssert.Contains("tracker.example.com", reason);
            Assert.IsFalse(rules.IsAllowed("a.tracker.example.com", out reason), "deny covers subdomains too");
        }

        [Test]
        public void DenyOnly_EverythingElseAllowed()
        {
            var rules = new UapWebHostRules(null, new[] { "bad.example" });
            string reason;
            Assert.IsTrue(rules.IsAllowed("good.example", out reason));
            Assert.IsFalse(rules.IsAllowed("bad.example", out reason));
        }

        [Test]
        public void LongAllowList_ReasonIsAbbreviated()
        {
            var rules = new UapWebHostRules(new[] { "a.test", "b.test", "c.test", "d.test", "e.test" }, null);
            string reason;
            Assert.IsFalse(rules.IsAllowed("z.test", out reason));
            StringAssert.Contains("a.test, b.test, c.test and 2 more", reason);
        }

        [Test]
        public void Matches_ExactOrSubdomain()
        {
            Assert.IsTrue(UapWebHostRules.Matches("example.com", "example.com"));
            Assert.IsTrue(UapWebHostRules.Matches("a.example.com", "example.com"));
            Assert.IsFalse(UapWebHostRules.Matches("aexample.com", "example.com"));
            Assert.IsFalse(UapWebHostRules.Matches("example.com", "a.example.com"));
            Assert.IsFalse(UapWebHostRules.Matches("", "example.com"));
            Assert.IsFalse(UapWebHostRules.Matches("example.com", ""));
        }
    }
}
