using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// INFRA-2e: the fixtures are captures of ONE specific CLI version,
    /// and nothing used to say so machine-checkably -- a CLI upgrade
    /// could silently leave the whole protocol suite pinning a wire shape
    /// the shipping CLI no longer speaks. _fixtures.meta.json records the
    /// provenance; this suite keeps it honest: every fixture file has an
    /// entry, every entry has a file, the recorded version matches
    /// FixtureLoader.ExpectedCliVersion, and a stale verification date
    /// raises a WARNING (not a failure -- age alone proves nothing, it
    /// just says "re-verify against the current CLI").
    /// </summary>
    [TestFixture]
    public class FixtureProvenanceTests
    {
        private const string ProvenanceFileName = "_fixtures.meta.json";

        /// <summary>Verification older than this warns (staleness signal).</summary>
        private const double StaleAfterDays = 180.0;

        private static string FixturesDir()
        {
            return Path.GetDirectoryName(FixtureLoader.GetPath("out1.jsonl"));
        }

        private static JsonNode ReadProvenance()
        {
            string path = Path.Combine(FixturesDir(), ProvenanceFileName);
            Assert.IsTrue(File.Exists(path), "provenance record missing: " + path);
            JsonNode node;
            string error;
            Assert.IsTrue(JsonParser.TryParse(File.ReadAllText(path), out node, out error),
                "provenance record unparseable: " + error);
            return node;
        }

        private static List<string> FixtureFilesOnDisk()
        {
            var names = new List<string>();
            foreach (string path in Directory.GetFiles(FixturesDir()))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ProvenanceFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                names.Add(name);
            }
            return names;
        }

        [Test]
        public void EveryFixtureFile_HasAProvenanceEntry()
        {
            JsonNode fixtures = ReadProvenance()["fixtures"];
            Assert.IsTrue(fixtures.IsObject, "provenance must carry a fixtures object");

            var missing = new List<string>();
            foreach (string name in FixtureFilesOnDisk())
            {
                if (!fixtures[name].IsObject)
                {
                    missing.Add(name);
                }
            }
            Assert.IsEmpty(missing,
                "new fixture(s) without provenance -- add them to " + ProvenanceFileName
                + ": " + string.Join(", ", missing.ToArray()));
        }

        [Test]
        public void EveryProvenanceEntry_HasItsFile()
        {
            JsonNode fixtures = ReadProvenance()["fixtures"];
            var files = FixtureFilesOnDisk();
            var orphans = new List<string>();
            foreach (KeyValuePair<string, JsonNode> pair in fixtures.Properties)
            {
                if (!files.Contains(pair.Key))
                {
                    orphans.Add(pair.Key);
                }
            }
            Assert.IsEmpty(orphans,
                "provenance entries whose fixture no longer exists: "
                + string.Join(", ", orphans.ToArray()));
        }

        [Test]
        public void RecordedCliVersion_MatchesTheExpectedConstant()
        {
            string recorded = ReadProvenance()["cliVersion"].AsString(null);
            Assert.AreEqual(FixtureLoader.ExpectedCliVersion, recorded,
                "the fixtures and FixtureLoader.ExpectedCliVersion must move together; "
                + "re-capture the fixtures when bumping either");
        }

        [Test]
        public void VerificationDate_NotStale_OrWarn()
        {
            string verifiedOn = ReadProvenance()["verifiedOn"].AsString(null);
            Assert.IsFalse(string.IsNullOrEmpty(verifiedOn), "verifiedOn missing");
            DateTime parsed;
            Assert.IsTrue(DateTime.TryParseExact(verifiedOn, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal, out parsed),
                "verifiedOn must be yyyy-MM-dd, was: " + verifiedOn);

            double ageDays = (DateTime.UtcNow - parsed).TotalDays;
            if (ageDays > StaleAfterDays)
            {
                // A warning, never a failure: age alone proves nothing, it
                // just says "re-verify against the current CLI". (Not
                // Assert.Warn -- Unity's bundled NUnit predates it.)
                UnityEngine.Debug.LogWarning(
                    "[AgentPanel] Fixture provenance was last verified " + (int)ageDays
                    + " days ago (against claude v" + FixtureLoader.ExpectedCliVersion
                    + "). Re-verify the captures against the current CLI and bump "
                    + "verifiedOn in " + ProvenanceFileName + ".");
            }
        }
    }
}
