using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Pure manifest.json string-scan tests (design section 7.2's capability-matrix precondition).</summary>
    [TestFixture]
    public class UloopDetectorTests
    {
        [Test]
        public void IsPresentInManifest_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(UloopDetector.IsPresentInManifest(null));
            Assert.IsFalse(UloopDetector.IsPresentInManifest(string.Empty));
        }

        [Test]
        public void IsPresentInManifest_WithoutUloop_ReturnsFalse()
        {
            string manifest = "{\"dependencies\":{\"com.unity.textmeshpro\":\"3.0.6\"}}";
            Assert.IsFalse(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void IsPresentInManifest_WithOriginalPackageId_ReturnsTrue()
        {
            string manifest = "{\"dependencies\":{\"io.github.hatayama.uloopmcp\":\"1.0.0\"}}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void IsPresentInManifest_WithRenamedPackageId_ReturnsTrue()
        {
            string manifest = "{\"dependencies\":{\"io.github.hatayama.unitycliloop\":\"2.0.0\"}}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void IsPresentInManifest_IsCaseInsensitive()
        {
            string manifest = "{\"dependencies\":{\"IO.GITHUB.HATAYAMA.ULOOPMCP\":\"1.0.0\"}}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void IsPresentInManifest_IdNotImmediatelyQuoted_DoesNotMatch()
        {
            // The id must be immediately wrapped in quotes ("<id>") to match --
            // free text merely containing the id (even inside a JSON string
            // value, with whitespace around it) is not a dependency-key-shaped
            // match and must not false-positive.
            string manifest = "{\"comment\":\"see io.github.hatayama.uloopmcp docs\"}";
            Assert.IsFalse(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void IsPresentInManifest_IdOnlyInScopedRegistryScopes_DoesNotMatch()
        {
            // THIS TEST USED TO ASSERT THE OPPOSITE. It was called
            // IsPresentInManifest_QuotedAnywhereInText_Matches and it
            // approvingly pinned a plain substring scan as intended
            // behaviour. Running the one-click install end to end for the
            // first time (2026-08-04) showed what that costs: UloopInstaller
            // writes this exact quoted id into scopedRegistries[].scopes, so
            // the detector reported uLoop present in a project where the
            // install had only just started -- and would keep reporting it
            // even if OpenUPM resolution never succeeded. The bug was
            // self-inflicted and the test guarded it.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM\","
                + "\"url\":\"https://package.openupm.com\","
                + "\"scopes\":[\"io.github.hatayama.uloopmcp\"]}],\"dependencies\":{}}";
            Assert.IsFalse(UloopDetector.IsPresentInManifest(manifest),
                "a scopes entry declares where a package COULD come from, not that it is installed");
        }

        [Test]
        public void IsPresentInManifest_RealPostInstallManifest_Matches()
        {
            // The shape actually produced by a successful install, captured
            // from the 2026-08-04 end-to-end run: the id appears TWICE --
            // once in scopes and once as a real dependency key. The
            // dependency must still be found, and the scopes occurrence
            // coming first in the text must not short-circuit the scan.
            string manifest = "{\"dependencies\":{\"io.github.hatayama.uloopmcp\":\"2.2.0\"},"
                + "\"scopedRegistries\":[{\"scopes\":[\"io.github.hatayama.uloopmcp\"]}]}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(manifest));

            string scopesFirst = "{\"scopedRegistries\":[{\"scopes\":[\"io.github.hatayama.uloopmcp\"]}],"
                + "\"dependencies\":{\"io.github.hatayama.uloopmcp\":\"2.2.0\"}}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(scopesFirst),
                "the scan must keep looking past a non-dependency occurrence");
        }

        [Test]
        public void IsPresentInManifest_RealInstallerOutputFormatting_ScopesEntryAloneDoesNotMatch()
        {
            // Byte-for-byte the shape UloopInstaller actually writes, copied
            // out of the 2026-08-04 end-to-end run. The scopes entry sits on
            // its own line, so what follows the closing quote is a newline
            // and six spaces before the "]" -- not the "]" the single-line
            // fixtures above produce. That is precisely the case a colon
            // check without whitespace skipping would get right by accident
            // and a careless one would get wrong, so it is pinned separately.
            string registryOnly =
                "{\n"
                + "  \"dependencies\": {\n"
                + "    \"com.unity.test-framework\": \"1.1.33\"\n"
                + "  },\n"
                + "  \"scopedRegistries\": [\n"
                + "    {\n"
                + "      \"name\": \"OpenUPM\",\n"
                + "      \"url\": \"https://package.openupm.com\",\n"
                + "      \"scopes\": [\n"
                + "        \"io.github.hatayama.uloopmcp\"\n"
                + "      ]\n"
                + "    }\n"
                + "  ]\n"
                + "}\n";
            Assert.IsFalse(UloopDetector.IsPresentInManifest(registryOnly),
                "this is the state the installer leaves behind for ~30s before OpenUPM resolution"
                + " lands, and reporting 'installed' during it is what the old scan did");

            string resolved = registryOnly.Replace(
                "    \"com.unity.test-framework\": \"1.1.33\"\n",
                "    \"com.unity.test-framework\": \"1.1.33\",\n"
                + "    \"io.github.hatayama.uloopmcp\": \"2.2.0\"\n");
            Assert.IsTrue(UloopDetector.IsPresentInManifest(resolved),
                "and once resolution lands, the same file must read as installed");
        }

        [Test]
        public void IsPresentInManifest_WhitespaceBeforeColon_StillMatches()
        {
            // Legal JSON, and the colon check must not trade one wrong answer
            // for another.
            string manifest = "{\"dependencies\":{\"io.github.hatayama.uloopmcp\"  :  \"2.2.0\"}}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void IsPresentInManifest_RenamedPackageIdAsDependency_StillMatches()
        {
            // The unity-cli-loop rename must keep working through the new
            // key-shaped check, not just the original id.
            string manifest = "{\"dependencies\":{\"io.github.hatayama.unitycliloop\":\"1.0.0\"}}";
            Assert.IsTrue(UloopDetector.IsPresentInManifest(manifest));
        }

        [Test]
        public void DetectInProject_MissingManifest_ReturnsFalse()
        {
            Assert.IsFalse(UloopDetector.DetectInProject(System.IO.Path.GetTempPath()));
        }

        [Test]
        public void DetectInProject_NullOrEmptyRoot_ReturnsFalse()
        {
            Assert.IsFalse(UloopDetector.DetectInProject(null));
            Assert.IsFalse(UloopDetector.DetectInProject(string.Empty));
        }
    }
}
