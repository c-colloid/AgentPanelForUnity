using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure text rules behind the "Agent Panel Pro updates" settings card
    /// (design note docs/design-notes/2026-09-12-pro-update-delivery.md
    /// section 3.3): the .upmconfig.toml block, the manifest.json scoped
    /// registry, and the apply flow over fake IO.
    /// </summary>
    [TestFixture]
    public class ProRegistryAccessTests
    {
        private const string Url = "https://updates.example.test/npm";
        private const string Key = "apu_pk_0123456789abcdef";

        // -- URL / key validation -------------------------------------------

        [TestCase("https://updates.example.test/npm", "https://updates.example.test/npm")]
        [TestCase("  https://updates.example.test/npm/  ", "https://updates.example.test/npm")]
        [TestCase("http://localhost:8787/npm", "http://localhost:8787/npm")]
        public void NormalizeUrl_AcceptsHttpsAndLocalHttp(string input, string expected)
        {
            string normalized;
            Assert.IsTrue(ProRegistryAccess.TryNormalizeRegistryUrl(input, out normalized));
            Assert.AreEqual(expected, normalized);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("updates.example.test/npm")]
        [TestCase("http://updates.example.test/npm")]
        [TestCase("https://updates.example.test/npm?x=1")]
        [TestCase("https://user:pw@updates.example.test/npm")]
        [TestCase("ftp://updates.example.test/npm")]
        public void NormalizeUrl_RejectsEverythingElse(string input)
        {
            string normalized;
            Assert.IsFalse(ProRegistryAccess.TryNormalizeRegistryUrl(input, out normalized));
        }

        [Test]
        public void PlausibleKey_RequiresOnePrintableToken()
        {
            Assert.IsTrue(ProRegistryAccess.IsPlausibleKey(Key));
            Assert.IsTrue(ProRegistryAccess.IsPlausibleKey("  " + Key + "\n"), "surrounding whitespace is trimmed");
            Assert.IsFalse(ProRegistryAccess.IsPlausibleKey(""));
            Assert.IsFalse(ProRegistryAccess.IsPlausibleKey("short"));
            Assert.IsFalse(ProRegistryAccess.IsPlausibleKey("apu pk with spaces"));
            Assert.IsFalse(ProRegistryAccess.IsPlausibleKey("apu_pk_\"quoted\""));
            Assert.IsFalse(ProRegistryAccess.IsPlausibleKey("apu_pk_\u00e9accent"));
        }

        // -- .upmconfig.toml ---------------------------------------------------

        [Test]
        public void UpsertUpmConfig_AppendsBlock_ToEmptyFile()
        {
            string toml = ProRegistryAccess.UpsertUpmConfig(string.Empty, Url, Key);
            Assert.AreEqual(
                "[npmAuth.\"" + Url + "\"]\n"
                + "token = \"" + Key + "\"\n"
                + "alwaysAuth = true\n", toml);
        }

        [Test]
        public void UpsertUpmConfig_AppendsAfterOtherRegistries_WithBlankLineSeparator()
        {
            string existing = "[npmAuth.\"https://package.openupm.com\"]\ntoken = \"abc\"\nalwaysAuth = true\n";
            string toml = ProRegistryAccess.UpsertUpmConfig(existing, Url, Key);
            StringAssert.StartsWith(existing + "\n[npmAuth.\"" + Url + "\"]\n", toml);
            StringAssert.Contains("token = \"" + Key + "\"", toml);
            Assert.AreEqual(1, CountOccurrences(toml, "package.openupm.com"), "other registries untouched");
        }

        [Test]
        public void UpsertUpmConfig_AppendsNewline_WhenFileLacksTrailingOne()
        {
            string toml = ProRegistryAccess.UpsertUpmConfig("[other]\nx = 1", Url, Key);
            StringAssert.StartsWith("[other]\nx = 1\n\n[npmAuth.", toml);
        }

        [Test]
        public void UpsertUpmConfig_ReplacesExistingBlock_InPlace_AndKeepsNeighbours()
        {
            string existing =
                "[npmAuth.\"https://a.example\"]\ntoken = \"aaa\"\n\n"
                + "[npmAuth.\"" + Url + "\"]\ntoken = \"old-key\"\nemail = \"me@example.test\"\nalwaysAuth = true\n\n"
                + "[npmAuth.\"https://z.example\"]\ntoken = \"zzz\"\n";
            string toml = ProRegistryAccess.UpsertUpmConfig(existing, Url, "apu_pk_newkey0000");
            Assert.AreEqual(
                "[npmAuth.\"https://a.example\"]\ntoken = \"aaa\"\n\n"
                + "[npmAuth.\"" + Url + "\"]\ntoken = \"apu_pk_newkey0000\"\nalwaysAuth = true\n\n"
                + "[npmAuth.\"https://z.example\"]\ntoken = \"zzz\"\n", toml);
            StringAssert.DoesNotContain("old-key", toml);
            StringAssert.DoesNotContain("me@example.test", toml, "stale keys inside the replaced block do not survive");
        }

        [Test]
        public void UpsertUpmConfig_ReplacesLastBlock_WithoutTrailingTable()
        {
            string existing = "[npmAuth.\"" + Url + "\"]\ntoken = \"old\"\nalwaysAuth = true\n";
            string toml = ProRegistryAccess.UpsertUpmConfig(existing, Url, Key);
            Assert.AreEqual("[npmAuth.\"" + Url + "\"]\ntoken = \"" + Key + "\"\nalwaysAuth = true\n", toml);
        }

        [Test]
        public void UpsertUpmConfig_KeepsCrlf()
        {
            string existing = "[other]\r\nx = 1\r\n";
            string toml = ProRegistryAccess.UpsertUpmConfig(existing, Url, Key);
            StringAssert.Contains("\r\n[npmAuth.", toml);
            Assert.AreEqual(0, CountOccurrences(toml.Replace("\r\n", string.Empty), "\n"), "no bare LF introduced");
        }

        [Test]
        public void UpsertUpmConfig_EscapesQuotesAndBackslashesInToken()
        {
            string toml = ProRegistryAccess.UpsertUpmConfig(string.Empty, Url, "a\"b\\c");
            StringAssert.Contains("token = \"a\\\"b\\\\c\"", toml);
        }

        // -- manifest.json ------------------------------------------------------

        private const string ManifestNoRegistries =
            "{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"1.0.0\"\n  }\n}\n";

        [Test]
        public void Manifest_AddsRegistry_WhenNoneExist()
        {
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsTrue(ProRegistryAccess.TryUpsertManifest(ManifestNoRegistries, Url, out after, out error, out detail));
            JsonNode root = JsonParser.Parse(after);
            Assert.AreEqual("1.0.0", root["dependencies"]["com.unity.ugui"].AsString(), "dependencies untouched");
            JsonNode entry = root["scopedRegistries"][0];
            Assert.AreEqual(ProRegistryAccess.RegistryDisplayName, entry["name"].AsString());
            Assert.AreEqual(Url, entry["url"].AsString());
            Assert.AreEqual(ProRegistryAccess.ProPackageId, entry["scopes"][0].AsString());
            StringAssert.Contains("\n  \"scopedRegistries\"", after, "pretty-printed, not collapsed to one line");
        }

        [Test]
        public void Manifest_AddsScope_ToExistingEntryAtSameUrl_EvenWithTrailingSlash()
        {
            string before = "{\"dependencies\":{},\"scopedRegistries\":[{\"name\":\"Mine\",\"url\":\"" + Url + "/\",\"scopes\":[\"com.other\"]}]}";
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsTrue(ProRegistryAccess.TryUpsertManifest(before, Url, out after, out error, out detail));
            JsonNode registries = JsonParser.Parse(after)["scopedRegistries"];
            Assert.AreEqual(1, registries.Count, "no duplicate entry");
            Assert.AreEqual("Mine", registries[0]["name"].AsString(), "user's display name kept");
            Assert.AreEqual(2, registries[0]["scopes"].Count);
            Assert.AreEqual(ProRegistryAccess.ProPackageId, registries[0]["scopes"][1].AsString());
        }

        [Test]
        public void Manifest_IsNoOp_WhenAlreadyConfigured()
        {
            string before = "{\"dependencies\":{},\"scopedRegistries\":[{\"name\":\"Agent Panel Pro\",\"url\":\"" + Url + "\",\"scopes\":[\"" + ProRegistryAccess.ProPackageId + "\"]}]}";
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsTrue(ProRegistryAccess.TryUpsertManifest(before, Url, out after, out error, out detail));
            Assert.AreSame(before, after, "unchanged text is returned as-is so callers can skip the write");
        }

        [Test]
        public void Manifest_LeavesOtherRegistriesAlone()
        {
            string before = "{\"dependencies\":{},\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"https://package.openupm.com\",\"scopes\":[\"io.github.hatayama.uloopmcp\"]}]}";
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsTrue(ProRegistryAccess.TryUpsertManifest(before, Url, out after, out error, out detail));
            JsonNode registries = JsonParser.Parse(after)["scopedRegistries"];
            Assert.AreEqual(2, registries.Count);
            Assert.AreEqual("OpenUPM", registries[0]["name"].AsString());
            Assert.AreEqual(1, registries[0]["scopes"].Count);
            Assert.AreEqual(Url, registries[1]["url"].AsString());
        }

        [Test]
        public void Manifest_RefusesWhenAnotherRegistryClaimsTheProScope()
        {
            string before = "{\"dependencies\":{},\"scopedRegistries\":[{\"name\":\"Company mirror\",\"url\":\"https://mirror.example\",\"scopes\":[\"" + ProRegistryAccess.ProPackageId + "\"]}]}";
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsFalse(ProRegistryAccess.TryUpsertManifest(before, Url, out after, out error, out detail));
            Assert.AreEqual(ProRegistryApplyError.ForeignRegistry, error);
            Assert.AreEqual("Company mirror", detail);
            Assert.AreSame(before, after);
        }

        [Test]
        public void Manifest_ReportsUnparsableText()
        {
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsFalse(ProRegistryAccess.TryUpsertManifest("{ not json", Url, out after, out error, out detail));
            Assert.AreEqual(ProRegistryApplyError.ManifestUnreadable, error);
            Assert.IsFalse(ProRegistryAccess.TryUpsertManifest("[1,2]", Url, out after, out error, out detail));
            Assert.AreEqual(ProRegistryApplyError.ManifestUnreadable, error);
        }

        [Test]
        public void Manifest_KeepsCrlfLineEndings()
        {
            string before = ManifestNoRegistries.Replace("\n", "\r\n");
            string after;
            ProRegistryApplyError error;
            string detail;
            Assert.IsTrue(ProRegistryAccess.TryUpsertManifest(before, Url, out after, out error, out detail));
            Assert.AreEqual(0, CountOccurrences(after.Replace("\r\n", string.Empty), "\n"));
        }

        // -- paths --------------------------------------------------------------

        [Test]
        public void UpmConfigPath_PrefersEnvOverride_ElseHome()
        {
            string home = Path.Combine("home", "me");
            Assert.AreEqual(Path.Combine(home, ".upmconfig.toml"),
                ProRegistryAccess.UpmConfigPath(name => null, home));
            Assert.AreEqual(Path.Combine(home, ".upmconfig.toml"),
                ProRegistryAccess.UpmConfigPath(name => string.Empty, home));
            string custom = Path.Combine("cfg", "dir");
            Assert.AreEqual(Path.Combine(custom, ".upmconfig.toml"),
                ProRegistryAccess.UpmConfigPath(
                    name => name == ProRegistryAccess.UpmUserConfigDirEnvVar ? custom : null, home));
        }

        // -- apply over fake IO --------------------------------------------------

        private sealed class FakeFs
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<string> Writes = new List<string>();

            public string Read(string path)
            {
                string text;
                if (!Files.TryGetValue(path, out text))
                {
                    throw new FileNotFoundException(path);
                }
                return text;
            }

            public void Write(string path, string text)
            {
                Files[path] = text;
                Writes.Add(path);
            }
        }

        private const string TomlPath = "/home/me/.upmconfig.toml";
        private const string ManifestPath = "/proj/Packages/manifest.json";

        [Test]
        public void Apply_WritesTomlAndManifest_FirstTime()
        {
            var fs = new FakeFs();
            fs.Files[ManifestPath] = ManifestNoRegistries;
            ProRegistryApplyResult result = ProRegistryAccess.ApplyWithSeams(Url + "/", " " + Key + " ",
                TomlPath, ManifestPath, fs.Read, fs.Write);
            Assert.IsTrue(result.Success, result.Error + " " + result.Detail);
            Assert.IsTrue(result.ManifestChanged);
            Assert.AreEqual(TomlPath, result.UpmConfigPath);
            CollectionAssert.AreEqual(new[] { TomlPath, ManifestPath }, fs.Writes);
            StringAssert.Contains("[npmAuth.\"" + Url + "\"]", fs.Files[TomlPath], "trailing slash normalized away");
            StringAssert.Contains("token = \"" + Key + "\"", fs.Files[TomlPath], "key trimmed");
            StringAssert.Contains(ProRegistryAccess.ProPackageId, fs.Files[ManifestPath]);
        }

        [Test]
        public void Apply_SecondTime_RewritesTomlOnly()
        {
            var fs = new FakeFs();
            fs.Files[ManifestPath] = ManifestNoRegistries;
            ProRegistryAccess.ApplyWithSeams(Url, Key, TomlPath, ManifestPath, fs.Read, fs.Write);
            fs.Writes.Clear();
            ProRegistryApplyResult result = ProRegistryAccess.ApplyWithSeams(Url, "apu_pk_rotated00000",
                TomlPath, ManifestPath, fs.Read, fs.Write);
            Assert.IsTrue(result.Success);
            Assert.IsFalse(result.ManifestChanged);
            CollectionAssert.AreEqual(new[] { TomlPath }, fs.Writes);
            StringAssert.Contains("apu_pk_rotated00000", fs.Files[TomlPath]);
            StringAssert.DoesNotContain(Key, fs.Files[TomlPath]);
        }

        [Test]
        public void Apply_ValidatesBeforeTouchingAnything()
        {
            var fs = new FakeFs();
            fs.Files[ManifestPath] = ManifestNoRegistries;
            Assert.AreEqual(ProRegistryApplyError.InvalidUrl,
                ProRegistryAccess.ApplyWithSeams("not a url", Key, TomlPath, ManifestPath, fs.Read, fs.Write).Error);
            Assert.AreEqual(ProRegistryApplyError.EmptyKey,
                ProRegistryAccess.ApplyWithSeams(Url, "", TomlPath, ManifestPath, fs.Read, fs.Write).Error);
            Assert.IsEmpty(fs.Writes);
        }

        [Test]
        public void Apply_ForeignRegistry_FailsBeforeWritingTheToken()
        {
            var fs = new FakeFs();
            fs.Files[ManifestPath] = "{\"dependencies\":{},\"scopedRegistries\":[{\"name\":\"Mirror\",\"url\":\"https://m.example\",\"scopes\":[\"" + ProRegistryAccess.ProPackageId + "\"]}]}";
            ProRegistryApplyResult result = ProRegistryAccess.ApplyWithSeams(Url, Key, TomlPath, ManifestPath, fs.Read, fs.Write);
            Assert.IsFalse(result.Success);
            Assert.AreEqual(ProRegistryApplyError.ForeignRegistry, result.Error);
            Assert.AreEqual("Mirror", result.Detail);
            Assert.IsEmpty(fs.Writes, "the key must not be written when the manifest cannot be fixed");
        }

        [Test]
        public void Apply_MissingManifest_IsReported()
        {
            var fs = new FakeFs();
            ProRegistryApplyResult result = ProRegistryAccess.ApplyWithSeams(Url, Key, TomlPath, ManifestPath, fs.Read, fs.Write);
            Assert.AreEqual(ProRegistryApplyError.ManifestUnreadable, result.Error);
            Assert.IsEmpty(fs.Writes);
        }

        [Test]
        public void Apply_WriteFailure_IsReported()
        {
            var fs = new FakeFs();
            fs.Files[ManifestPath] = ManifestNoRegistries;
            ProRegistryApplyResult result = ProRegistryAccess.ApplyWithSeams(Url, Key, TomlPath, ManifestPath,
                fs.Read, (p, t) => { throw new IOException("disk full"); });
            Assert.AreEqual(ProRegistryApplyError.WriteFailed, result.Error);
            StringAssert.Contains("disk full", result.Detail);
        }

        [Test]
        public void ProRegistryUrl_SettingDefaultsToEmpty_AndFallsBackToBuiltIn()
        {
            var settings = new Model.PanelSettings();
            Assert.AreEqual(string.Empty, settings.proRegistryUrl);
            Assert.AreEqual(ProRegistryAccess.DefaultRegistryUrl, UI.SettingsView.EffectiveProRegistryUrl(settings));
            settings.proRegistryUrl = Url;
            Assert.AreEqual(Url, UI.SettingsView.EffectiveProRegistryUrl(settings));
        }

        [Test]
        public void DescribeApplyResult_CoversEveryOutcome_Distinctly()
        {
            UI.L10n.OverrideForTests(null);
            var texts = new List<string>
            {
                UI.SettingsView.DescribeProApplyResult(new ProRegistryApplyResult { Success = true, UpmConfigPath = TomlPath }),
                UI.SettingsView.DescribeProApplyResult(new ProRegistryApplyResult { Error = ProRegistryApplyError.EmptyKey }),
                UI.SettingsView.DescribeProApplyResult(new ProRegistryApplyResult { Error = ProRegistryApplyError.InvalidUrl }),
                UI.SettingsView.DescribeProApplyResult(new ProRegistryApplyResult { Error = ProRegistryApplyError.ForeignRegistry, Detail = "Mirror" }),
                UI.SettingsView.DescribeProApplyResult(new ProRegistryApplyResult { Error = ProRegistryApplyError.ManifestUnreadable, Detail = "bad json" }),
                UI.SettingsView.DescribeProApplyResult(new ProRegistryApplyResult { Error = ProRegistryApplyError.WriteFailed, Detail = "disk full" }),
            };
            foreach (string text in texts)
            {
                Assert.IsNotEmpty(text);
            }
            Assert.AreEqual(texts.Count, new HashSet<string>(texts).Count, "each outcome reads differently");
            StringAssert.Contains(TomlPath, texts[0]);
            StringAssert.Contains("Mirror", texts[3]);
            StringAssert.Contains(ProRegistryAccess.ProPackageId, texts[3]);
            StringAssert.Contains("bad json", texts[4]);
            StringAssert.Contains("disk full", texts[5]);
        }

        // -- VPM (VCC / ALCOM) ----------------------------------------------------

        [Test]
        public void ReadUpmConfigToken_FindsTheBlockForTheUrl_OnlyThatBlock()
        {
            string toml = ProRegistryAccess.UpsertUpmConfig(
                ProRegistryAccess.UpsertUpmConfig(string.Empty, "https://other.example/npm", "apu_pk_otherkey0000"),
                Url, Key);
            Assert.AreEqual(Key, ProRegistryAccess.ReadUpmConfigToken(toml, Url));
            Assert.AreEqual("apu_pk_otherkey0000", ProRegistryAccess.ReadUpmConfigToken(toml, "https://other.example/npm"));
            Assert.IsNull(ProRegistryAccess.ReadUpmConfigToken(toml, "https://absent.example/npm"));
            Assert.IsNull(ProRegistryAccess.ReadUpmConfigToken(string.Empty, Url));
            Assert.IsNull(ProRegistryAccess.ReadUpmConfigToken(null, Url));
        }

        [Test]
        public void ReadUpmConfigToken_RoundTripsEscapes_AndCrlf()
        {
            string toml = ProRegistryAccess.UpsertUpmConfig(string.Empty, Url, "a\"b\\c").Replace("\n", "\r\n");
            Assert.AreEqual("a\"b\\c", ProRegistryAccess.ReadUpmConfigToken(toml, Url));
        }

        [TestCase("https://updates.example.test/npm", "https://updates.example.test/vpm/index.json")]
        [TestCase("https://updates.example.test/npm/", "https://updates.example.test/vpm/index.json")]
        [TestCase("https://updates.example.test/NPM", "https://updates.example.test/vpm/index.json")]
        [TestCase("https://updates.example.test", "https://updates.example.test/vpm/index.json")]
        [TestCase("http://localhost:8787/npm", "http://localhost:8787/vpm/index.json")]
        public void VpmListingUrl_DerivesFromTheRegistryUrl(string registry, string expected)
        {
            Assert.AreEqual(expected, ProRegistryAccess.VpmListingUrl(registry));
        }

        [Test]
        public void VpmListingUrl_IsNullForAnInvalidRegistryUrl()
        {
            Assert.IsNull(ProRegistryAccess.VpmListingUrl("not a url"));
            Assert.IsNull(ProRegistryAccess.VpmListingUrl(""));
        }

        [Test]
        public void BuildVccDeepLink_EncodesUrlAndHeader()
        {
            string link = ProRegistryAccess.BuildVccDeepLink("https://updates.example.test/vpm/index.json", " " + Key + " ");
            Assert.AreEqual(
                "vcc://vpm/addRepo?url=https%3A%2F%2Fupdates.example.test%2Fvpm%2Findex.json"
                + "&headers[]=Authorization%3ABearer%20" + Key, link);
        }

        [Test]
        public void ResolveTokenForVcc_PrefersTheField_ElseReadsUpmConfig()
        {
            var fs = new FakeFs();
            fs.Files[TomlPath] = ProRegistryAccess.UpsertUpmConfig(string.Empty, Url, "apu_pk_savedkey00000");
            Assert.AreEqual(Key, ProRegistryAccess.ResolveTokenForVcc(" " + Key + " ", Url, fs.Read, TomlPath));
            Assert.AreEqual("apu_pk_savedkey00000", ProRegistryAccess.ResolveTokenForVcc("", Url + "/", fs.Read, TomlPath));
            Assert.IsNull(ProRegistryAccess.ResolveTokenForVcc("", "https://absent.example/npm", fs.Read, TomlPath));
            Assert.IsNull(ProRegistryAccess.ResolveTokenForVcc("", "not a url", fs.Read, TomlPath));
            Assert.IsNull(ProRegistryAccess.ResolveTokenForVcc("", Url, new FakeFs().Read, TomlPath), "missing file is not an error, just no token");
        }

        [Test]
        public void DescribeVccAttempt_CoversEveryOutcome()
        {
            UI.L10n.OverrideForTests(null);
            string badUrl = UI.SettingsView.DescribeProVccAttempt(null, null);
            string noKey = UI.SettingsView.DescribeProVccAttempt("https://updates.example.test/vpm/index.json", null);
            string opened = UI.SettingsView.DescribeProVccAttempt("https://updates.example.test/vpm/index.json", Key);
            Assert.AreEqual(3, new HashSet<string> { badUrl, noKey, opened }.Count);
            StringAssert.Contains("https://updates.example.test/vpm/index.json", opened);
            StringAssert.DoesNotContain(Key, opened, "the key never appears in the status line");
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
