using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// OPS-3 (SR-asset-path): the shared pure path guard behind every
    /// asset-writing UapOps tool. The regression it exists for: a bare
    /// StartsWith("Assets/") accepted "Assets/../Evil.mat", which RESOLVES
    /// to the project root. Dot segments collapse first; the collapsed
    /// result must still live under Assets/. (The same table also runs in
    /// the license-free smoke tier, which compiles this file's subject
    /// directly -- keep UapAssetPath free of Unity API.)
    /// </summary>
    [TestFixture]
    public class UapAssetPathTests
    {
        [TestCase("Assets/Sub/File.mat", "Assets/Sub/File.mat")]
        [TestCase("Assets\\Sub\\File.mat", "Assets/Sub/File.mat")]
        [TestCase("Assets/./Sub/File.mat", "Assets/Sub/File.mat")]
        [TestCase("Assets/Sub/../File.mat", "Assets/File.mat")]
        [TestCase("Assets//Sub///File.mat", "Assets/Sub/File.mat")]
        [TestCase("assets/File.mat", "assets/File.mat")]
        public void NormalizeUnderAssets_AcceptedPaths_ReturnTheNormalizedForm(string input, string expected)
        {
            string error;
            Assert.AreEqual(expected, UapAssetPath.NormalizeUnderAssets(input, out error));
            Assert.IsNull(error);
        }

        [TestCase("Assets/../Evil.mat")]
        [TestCase("Assets/Sub/../../Outside.mat")]
        [TestCase("Assets/Sub/../../../../Outside.mat")]
        [TestCase("Packages/Foo.mat")]
        [TestCase("Library/Foo.mat")]
        [TestCase("Assets")]
        [TestCase("AssetsEvil/Foo.mat")]
        [TestCase("../Assets/Foo.mat")]
        public void NormalizeUnderAssets_EscapingOrOutsidePaths_AreRejected(string input)
        {
            string error;
            Assert.IsNull(UapAssetPath.NormalizeUnderAssets(input, out error));
            StringAssert.Contains("Assets/", error);
        }

        [Test]
        public void NormalizeUnderAssets_EscapeError_NamesTheCollapsedResult()
        {
            string error;
            UapAssetPath.NormalizeUnderAssets("Assets/../Evil.mat", out error);
            StringAssert.Contains("Evil.mat", error,
                "the error must show WHERE the path actually resolves, so the rejection is explainable");
        }

        [Test]
        public void NormalizeUnderAssets_NullOrEmpty_IsRejected()
        {
            string error;
            Assert.IsNull(UapAssetPath.NormalizeUnderAssets(null, out error));
            Assert.IsNotNull(error);
            Assert.IsNull(UapAssetPath.NormalizeUnderAssets(string.Empty, out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void CollapseDotSegments_UnresolvableLeadingDotDot_IsKeptLiterally()
        {
            // normpath semantics: a ".." with nothing left to pop stays,
            // so the Assets/ check afterwards still sees (and rejects) it.
            Assert.AreEqual("../Foo", UapAssetPath.CollapseDotSegments("../Foo"));
        }
    }
}
