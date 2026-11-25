using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The manifest helpers the removal path and the Pro registry writer
    /// share (UloopManifestSupport; the install planner that used to sit next
    /// to them is gone, docs/design-notes/2026-10-02-uloop-card-final.md).
    /// Pure functions only -- no project directory is touched.
    /// </summary>
    [TestFixture]
    public class UloopManifestSupportTests
    {
        [Test]
        public void Constants_AreTheMeasuredValues()
        {
            Assert.AreEqual("io.github.hatayama.uloopmcp", UloopManifestSupport.UloopPackageId);
            Assert.AreEqual("https://package.openupm.com", UloopManifestSupport.OpenUpmRegistryUrl);
            Assert.AreEqual(".uap-backup", UloopManifestSupport.BackupFileSuffix);
        }

        // -- FindMatchingScopedRegistry: URL is the only key (2026-08-04 fix) ----------

        [Test]
        public void FindMatchingScopedRegistry_NoScopedRegistriesArray_ReturnsNull()
        {
            JsonNode root = JsonParser.Parse("{\"dependencies\":{}}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNull(matching);
        }

        [Test]
        public void FindMatchingScopedRegistry_UnrelatedNameAndUrl_ReturnsNull_LeftForCallerToLeaveAlone()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"some.other.registry\",\"url\":\"https://example.invalid\",\"scopes\":[]}]}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNull(matching, "a registry whose url does not match ours is unrelated, whatever it is named.");
        }

        [Test]
        public void FindMatchingScopedRegistry_SameNameDifferentUrl_ReturnsNull()
        {
            // The exact case the OLD predicate used to flag as the one blocking
            // conflict. Name is no longer consulted at all: a different url, even under
            // our own display name, is just a different, unrelated registry now.
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"https://mirror.invalid\","
                + "\"scopes\":[\"io.github.hatayama.uloopmcp\"]}]}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNull(matching);
        }

        [Test]
        public void FindMatchingScopedRegistry_DifferentNameSameUrl_ReturnsTheEntry()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM Registry\",\"url\":\"https://package.openupm.com\","
                + "\"scopes\":[\"com.some.other.thing\"]}]}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "url match must be found regardless of the entry's own display name.");
            Assert.AreEqual("OpenUPM Registry", matching["name"].AsString());
        }

        [Test]
        public void FindMatchingScopedRegistry_TrailingSlashUrl_StillMatches()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"https://package.openupm.com/\",\"scopes\":[]}]}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "a single trailing slash must not defeat the match.");
        }

        [Test]
        public void FindMatchingScopedRegistry_UrlCaseDiffers_StillMatches()
        {
            // The fix's own stated rule: "compare scheme+host OrdinalIgnoreCase".
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"HTTPS://PACKAGE.OPENUPM.COM\",\"scopes\":[]}]}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "scheme+host comparison must be case-insensitive.");
        }

        [Test]
        public void FindMatchingScopedRegistry_MalformedUrl_DoesNotThrow_ReturnsNull()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"not a url at all\",\"scopes\":[]}]}");

            JsonNode matching = null;
            Assert.DoesNotThrow(delegate
            {
                matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);
            });
            Assert.IsNull(matching);
        }

        [Test]
        public void FindMatchingScopedRegistry_ScopesAlreadyCoverOurs_StillReturnsTheEntry()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"https://package.openupm.com\","
                + "\"scopes\":[\"com.some.other.thing\",\"io.github.hatayama.uloopmcp\"]}]}");

            JsonNode matching = UloopManifestSupport.FindMatchingScopedRegistry(root, UloopManifestSupport.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "a url match is returned regardless of whether scopes already covers ours.");
        }


            // -- Line endings (2026-08-04) ------------------------------------------

        [Test]
        public void MatchLineEndings_CrlfSource_ConvertsSoTheWholeFileIsNotRewritten()
        {
            // PrettyPrintManifest always emits LF. On Windows with a CRLF
            // manifest that rewrites EVERY line, so git reports the whole file
            // changed for what the confirmation card showed as a three-line
            // insert -- and the card could not show it, because it normalized
            // both sides before diffing.
            string crlfOriginal = "{\r\n  \"dependencies\": {}\r\n}\r\n";
            string lfNew = "{\n  \"dependencies\": {},\n  \"scopedRegistries\": []\n}\n";

            string matched = UloopManifestSupport.MatchLineEndings(lfNew, crlfOriginal);

            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(matched, "(?<!\r)\n"),
                "every newline must be CRLF when the source was CRLF");
        }

        [Test]
        public void MatchLineEndings_LfSource_LeavesLfAlone()
        {
            string lfOriginal = "{\n  \"dependencies\": {}\n}\n";
            string lfNew = "{\n  \"dependencies\": {},\n  \"scopedRegistries\": []\n}\n";

            Assert.AreEqual(lfNew, UloopManifestSupport.MatchLineEndings(lfNew, lfOriginal));
        }
    }
}
