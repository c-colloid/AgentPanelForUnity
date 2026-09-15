using System.Collections.Generic;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Which installed packages no profile covers (docs/design-notes/
    /// 2026-09-15-profile-gaps-and-skill-scaffold.md). Pure, so the rules
    /// that decide what the Settings card says are pinned here rather than
    /// discovered against a live project's package list.
    /// </summary>
    [TestFixture]
    public class ExtensionProfileGapFinderTests
    {
        private static ExtensionProfile Profile(string id, params string[] packageIds)
        {
            var profile = new ExtensionProfile { Id = id, DisplayName = id };
            profile.PackageIds.AddRange(packageIds);
            return profile;
        }

        private static List<string> Find(IEnumerable<string> installed, IEnumerable<ExtensionProfile> profiles,
            out int total)
        {
            return ExtensionProfileGapFinder.FindUncovered(installed, profiles, 6, out total);
        }

        [Test]
        public void APackageAProfileNames_IsNotAGap()
        {
            int total;
            List<string> gaps = Find(
                new[] { "nadena.dev.ndmf", "com.vendor.sdk" },
                new[] { Profile("ndmf", "nadena.dev.ndmf") }, out total);

            CollectionAssert.AreEqual(new[] { "com.vendor.sdk" }, gaps);
            Assert.AreEqual(1, total);
        }

        [Test]
        public void APackageCacheDirectoryName_IsNormalisedToItsId()
        {
            // Library/PackageCache spells a resolved package "<id>@<version>";
            // manifest.json and Packages/ use the bare id. Without
            // normalising, the same package is both covered and a gap.
            int total;
            List<string> gaps = Find(
                new[] { "nadena.dev.ndmf@1.5.0", "nadena.dev.ndmf" },
                new[] { Profile("ndmf", "nadena.dev.ndmf") }, out total);

            CollectionAssert.IsEmpty(gaps);
            Assert.AreEqual(0, total);
        }

        [Test]
        public void TheSamePackageFromTwoListings_IsReportedOnce()
        {
            int total;
            List<string> gaps = Find(
                new[] { "com.vendor.sdk", "com.vendor.sdk@2.0.0", "com.vendor.sdk" },
                new ExtensionProfile[0], out total);

            CollectionAssert.AreEqual(new[] { "com.vendor.sdk" }, gaps);
            Assert.AreEqual(1, total);
        }

        [Test]
        public void ThePanelsOwnPackages_AreNeverGaps()
        {
            int total;
            List<string> gaps = Find(
                new[] { "jp.colloid.unity-agent-panel", "jp.colloid.agent-panel-pro" },
                new ExtensionProfile[0], out total);

            CollectionAssert.IsEmpty(gaps);
        }

        [Test]
        public void UnityFirstPartyPackages_SortLast_ButAreStillListed()
        {
            // Timeline and Cinemachine are reasonable profile subjects; they
            // are just never the ones a user is hunting for in a list of
            // forty, so they go after the third-party entries.
            int total;
            List<string> gaps = Find(
                new[] { "com.unity.timeline", "com.vendor.sdk", "com.unity.cinemachine", "aaa.vendor.two" },
                new ExtensionProfile[0], out total);

            CollectionAssert.AreEqual(
                new[] { "aaa.vendor.two", "com.vendor.sdk", "com.unity.cinemachine", "com.unity.timeline" },
                gaps);
        }

        [Test]
        public void TheListIsCapped_AndTheTotalIsThePreCapCount()
        {
            var installed = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                installed.Add("com.vendor.sdk" + i.ToString("00"));
            }

            int total;
            List<string> gaps = ExtensionProfileGapFinder.FindUncovered(
                installed, new ExtensionProfile[0], 3, out total);

            Assert.AreEqual(3, gaps.Count);
            Assert.AreEqual(20, total, "the caller needs the real count to say how many were not shown");
        }

        [Test]
        public void NullInputs_AreSafe()
        {
            int total;
            CollectionAssert.IsEmpty(ExtensionProfileGapFinder.FindUncovered(null, null, 6, out total));
            Assert.AreEqual(0, total);
        }

        [TestCase("com.vendor.sdk@1.0.0", "com.vendor.sdk")]
        [TestCase("com.vendor.sdk", "com.vendor.sdk")]
        [TestCase("  com.vendor.sdk  ", "com.vendor.sdk")]
        [TestCase("@nothing", "@nothing")]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void Normalize_StripsAVersionSuffixOnly(string input, string expected)
        {
            Assert.AreEqual(expected, ExtensionProfileGapFinder.Normalize(input));
        }
    }
}
