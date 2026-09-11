using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Plan() is pure -- exercised over fake project directories under a temp root, no
    /// real Unity project touched. Apply() is exercised through the ApplyWithSeams test
    /// seam so a simulated read/write failure never risks corrupting a real file (task
    /// spec: "inject the failure rather than corrupting a real file") and so no test
    /// ever issues a real UnityEditor.PackageManager.Client.Add request.
    ///
    /// Fixture ids/names below are the MEASURED values (2026-08-03 revision, from a
    /// real working install's Packages/manifest.json + packages-lock.json) --
    /// "io.github.hatayama.uloopmcp" and registry name "OpenUPM" -- NOT the original
    /// guesses ("unitycliloop" / "package.openupm.com") this stream shipped first.
    ///
    /// 2026-08-04: ApplyWithSeams gained a fourth-from-front <c>readAllText</c>
    /// parameter (the stale-plan guard -- see UloopInstaller.ApplyManifestRoute's doc
    /// comment) and its <c>clientAdd</c> seam changed from <c>Action&lt;string&gt;</c>
    /// to <c>Func&lt;string, AddRequest&gt;</c> (the discarded-result fix -- see
    /// UloopInstallApplyResult.ClientAddRequest's doc comment). Every existing
    /// Apply-side test below was updated for both signature changes; tests that only
    /// exercise steps AFTER the stale-plan guard pass <see cref="UnchangedDiskReader"/>
    /// so the guard is a no-op for them, and tests that must never reach ApplyManifestRoute
    /// at all pass <see cref="FailIfCalledReader"/> to prove the new seam is not invoked
    /// on a refused plan either.
    /// </summary>
    [TestFixture]
    public class UloopInstallerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "UloopInstallerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Packages"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private string ManifestPath
        {
            get { return Path.Combine(_root, "Packages", "manifest.json"); }
        }

        private void WriteManifest(string json)
        {
            File.WriteAllText(ManifestPath, json);
        }

        private void WriteVpmManifest()
        {
            File.WriteAllText(Path.Combine(_root, "Packages", "vpm-manifest.json"), "{}");
        }

        private const string CleanManifest = "{\"dependencies\":{\"com.unity.textmeshpro\":\"3.0.6\"}}";

        // -- Plan: already installed ---------------------------------------------

        [Test]
        public void Plan_AlreadyInstalled_SetsAlreadyInstalledTrueAndNoMethod()
        {
            WriteManifest("{\"dependencies\":{\"io.github.hatayama.uloopmcp\":\"3.0.0-beta.48\"}}");

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.IsTrue(plan.AlreadyInstalled);
            Assert.AreEqual(UloopInstallMethod.None, plan.Method);
            Assert.IsFalse(plan.CanProceed());
        }

        [Test]
        public void Plan_PackageIdOnlyInScopedRegistryScopes_NotDependencies_IsNotAlreadyInstalled()
        {
            // Regression pin for a real bug this stream's own tests caught: a project
            // can have a scoped-registry "scopes" entry naming uLoop's package id
            // WITHOUT the package actually being a "dependencies" entry (e.g. a shared
            // team manifest template, or a leftover scope from a removed dependency).
            // The FIRST implementation of IsAlreadyInstalled scanned the whole raw
            // manifest.json text and treated the quoted id inside "scopes" as proof of
            // an installed dependency -- which made this method incorrectly report
            // AlreadyInstalled=true, hiding the Install button for a project where
            // uLoop is NOT actually installed. IsAlreadyInstalled now scans only the
            // "dependencies" object, so this must resolve to the normal
            // ManifestScopedRegistry plan instead.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM\","
                + "\"url\":\"https://package.openupm.com\",\"scopes\":[\"io.github.hatayama.uloopmcp\"]}],"
                + "\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.IsFalse(plan.AlreadyInstalled,
                "a scope declaration alone must not be mistaken for an installed dependency.");
            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method);
        }

        // -- Plan: clean manifest -------------------------------------------------

        [Test]
        public void Plan_CleanManifest_ProducesManifestScopedRegistryPlanWithNoCaveats()
        {
            WriteManifest(CleanManifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.IsFalse(plan.AlreadyInstalled);
            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method);
            Assert.IsTrue(plan.CanProceed());
            Assert.AreEqual(0, plan.Caveats.Count);
            Assert.AreEqual(ManifestPath, plan.ManifestPath);
            Assert.AreEqual(ManifestPath + UloopInstaller.BackupFileSuffix, plan.BackupPath);
            Assert.AreEqual(CleanManifest, plan.ManifestBefore);
        }

        [Test]
        public void Plan_CleanManifest_AfterText_AddsOnlyTheRegistryStanza_NoDependencyLine()
        {
            // 2026-08-03 revision: no version is ever hand-written any more -- the
            // "dependencies" object must come back UNCHANGED. Client.Add (called from
            // Apply, with no version) is what adds the dependency line, with whatever
            // version Unity actually resolves.
            WriteManifest(CleanManifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            JsonNode after = JsonParser.Parse(plan.ManifestAfter);
            Assert.AreEqual(1, after["scopedRegistries"].Count);
            JsonNode registry = after["scopedRegistries"][0];
            Assert.AreEqual(UloopInstaller.OpenUpmRegistryName, registry["name"].AsString());
            Assert.AreEqual(UloopInstaller.OpenUpmRegistryUrl, registry["url"].AsString());
            Assert.AreEqual(1, registry["scopes"].Count);
            Assert.AreEqual(UloopInstaller.UloopPackageId, registry["scopes"][0].AsString());

            Assert.IsFalse(after["dependencies"].HasKey(UloopInstaller.UloopPackageId),
                "no dependency line should ever be hand-written -- Client.Add(no version) writes it.");
            // The pre-existing dependency must survive the round trip untouched.
            Assert.AreEqual("3.0.6", after["dependencies"]["com.unity.textmeshpro"].AsString());
        }

        [Test]
        public void Plan_MeasuredConstants_MatchTheRealInstall()
        {
            // Pins the exact measured values verbatim (2026-08-03) so a future edit
            // cannot silently drift back to a guess.
            Assert.AreEqual("io.github.hatayama.uloopmcp", UloopInstaller.UloopPackageId);
            Assert.AreEqual("OpenUPM", UloopInstaller.OpenUpmRegistryName);
            Assert.AreEqual("https://package.openupm.com", UloopInstaller.OpenUpmRegistryUrl);
        }

        // -- Plan: existing scoped registry, same name -----------------------------

        [Test]
        public void Plan_ExistingRegistry_SameNameAndUrl_ScopesAlreadyCoverOurs_NotAConflict_NoDuplicate()
        {
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM\","
                + "\"url\":\"https://package.openupm.com\",\"scopes\":[\"io.github.hatayama.uloopmcp\"]}],"
                + "\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method);
            Assert.IsTrue(plan.CanProceed());
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                Assert.AreNotEqual(UloopInstaller.CaveatCodeScopedRegistryConflict, caveat.Code);
            }

            JsonNode after = JsonParser.Parse(plan.ManifestAfter);
            Assert.AreEqual(1, after["scopedRegistries"].Count, "the existing entry must not be duplicated.");
            Assert.AreEqual(1, after["scopedRegistries"][0]["scopes"].Count, "the scope must not be duplicated either.");
        }

        [Test]
        public void Plan_ExistingRegistry_SameNameAndUrl_MissingOurScope_MergesInsteadOfConflicting()
        {
            // 2026-08-03 revision: a same-named/same-url registry that simply lacks our
            // scope is the NORMAL case for a project already using OpenUPM for other
            // packages -- this must MERGE (append our scope, keep the existing one),
            // not block as a conflict.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM\","
                + "\"url\":\"https://package.openupm.com\",\"scopes\":[\"com.other.thing\"]}],"
                + "\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method,
                "missing-scope-only must merge, not block.");
            Assert.IsTrue(plan.CanProceed());
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                Assert.AreNotEqual(UloopInstaller.CaveatCodeScopedRegistryConflict, caveat.Code);
            }

            JsonNode after = JsonParser.Parse(plan.ManifestAfter);
            Assert.AreEqual(1, after["scopedRegistries"].Count, "must merge into the existing entry, not add a second one.");
            string[] scopes = after["scopedRegistries"][0]["scopes"].AsStringArray();
            Assert.AreEqual(2, scopes.Length);
            Assert.AreEqual("com.other.thing", scopes[0], "the pre-existing scope must be preserved.");
            Assert.AreEqual(UloopInstaller.UloopPackageId, scopes[1], "our scope is appended.");
        }

        [Test]
        public void Plan_ExistingRegistry_UnrelatedNameAndUrl_LeftUntouched_OurEntryAddedSeparately()
        {
            // 2026-08-04 regression pin for defect (3)(a-inverse): earlier revisions
            // matched on registry NAME first, so a registry that happened to be named
            // "OpenUPM" but pointed somewhere else entirely was declared a BLOCKING
            // conflict and the button was disabled outright. Since the fix matches on
            // URL only, this is simply an unrelated registry: it is left completely
            // untouched, and our own OpenUPM/package.openupm.com entry is appended
            // alongside it as a second, independent scopedRegistries entry.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM\","
                + "\"url\":\"https://example.invalid\",\"scopes\":[\"com.some.unrelated.thing\"]}],"
                + "\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method,
                "an unrelated registry (different url) must not block the install.");
            Assert.IsTrue(plan.CanProceed());
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                Assert.AreNotEqual(UloopInstaller.CaveatCodeScopedRegistryConflict, caveat.Code);
            }

            JsonNode after = JsonParser.Parse(plan.ManifestAfter);
            Assert.AreEqual(2, after["scopedRegistries"].Count,
                "the unrelated entry is left in place and our own entry is added alongside it.");
            JsonNode unrelated = after["scopedRegistries"][0];
            Assert.AreEqual("https://example.invalid", unrelated["url"].AsString(), "the unrelated entry's url must be untouched.");
            Assert.AreEqual(1, unrelated["scopes"].Count, "the unrelated entry's scopes must be untouched.");
            Assert.AreEqual("com.some.unrelated.thing", unrelated["scopes"][0].AsString());
            JsonNode ours = after["scopedRegistries"][1];
            Assert.AreEqual(UloopInstaller.OpenUpmRegistryUrl, ours["url"].AsString());
            Assert.AreEqual(UloopInstaller.UloopPackageId, ours["scopes"][0].AsString());
        }

        [Test]
        public void Plan_ForeignRegistryAlreadyClaimsOurScope_Blocks()
        {
            // The one case URL-only matching would walk straight into, added after
            // reviewing the URL-matching change: a registry at a DIFFERENT host that
            // already lists this package id in its scopes -- a company mirror, a VPM
            // feed. It is not "unrelated", so leaving it alone and appending ours
            // beside it leaves two registries claiming one scope, and which one Unity
            // serves the package from is not predictable. Blocking is the honest
            // answer; silently redirecting a package the project deliberately points
            // somewhere else is not.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"Internal\","
                + "\"url\":\"https://npm.example.invalid\","
                + "\"scopes\":[\"io.github.hatayama.uloopmcp\"]}],\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.IsFalse(plan.CanProceed());
            bool blocked = false;
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                if (caveat.Blocking && caveat.Code == UloopInstaller.CaveatCodeScopedRegistryConflict)
                {
                    blocked = true;
                    StringAssert.Contains("npm.example.invalid", caveat.Detail);
                }
            }
            Assert.IsTrue(blocked, "the blocking caveat must name the registry that already claims the scope");
        }

        [Test]
        public void Plan_ExistingRegistry_DifferentNameSameUrl_MergesInsteadOfDuplicating()
        {
            // 2026-08-04 regression pin for defect (3)(a): a project already on OpenUPM
            // under a different display name (a very common real-world shape -- e.g.
            // "OpenUPM Registry") must be recognized by URL and merged into, not missed
            // by a name-only match and duplicated into a second, redundant entry
            // pointing at the exact same host.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM Registry\","
                + "\"url\":\"https://package.openupm.com\",\"scopes\":[\"com.other.thing\"]}],"
                + "\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method);
            Assert.IsTrue(plan.CanProceed());

            JsonNode after = JsonParser.Parse(plan.ManifestAfter);
            Assert.AreEqual(1, after["scopedRegistries"].Count, "must merge into the existing entry, not add a duplicate.");
            JsonNode merged = after["scopedRegistries"][0];
            Assert.AreEqual("OpenUPM Registry", merged["name"].AsString(), "the entry's own display name must not be renamed.");
            string[] scopes = merged["scopes"].AsStringArray();
            Assert.AreEqual(2, scopes.Length);
            Assert.AreEqual("com.other.thing", scopes[0], "the pre-existing scope must be preserved.");
            Assert.AreEqual(UloopInstaller.UloopPackageId, scopes[1]);
        }

        [Test]
        public void Plan_ExistingRegistry_SameNameTrailingSlashUrl_MergesInsteadOfBlocking()
        {
            // 2026-08-04 regression pin for defect (3)(b): "https://package.openupm.com/"
            // -- a single trailing slash, a form OpenUPM's own docs emit -- used to fail
            // an ordinal url compare and get reported as a BLOCKING conflict for a
            // manifest that needed no resolution at all. It must now be recognized as
            // the same registry and merged into.
            string manifest = "{\"scopedRegistries\":[{\"name\":\"OpenUPM\","
                + "\"url\":\"https://package.openupm.com/\",\"scopes\":[]}],"
                + "\"dependencies\":{}}";
            WriteManifest(manifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method,
                "a trailing-slash url must not be treated as a conflicting registry.");
            Assert.IsTrue(plan.CanProceed());
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                Assert.AreNotEqual(UloopInstaller.CaveatCodeScopedRegistryConflict, caveat.Code);
            }

            JsonNode after = JsonParser.Parse(plan.ManifestAfter);
            Assert.AreEqual(1, after["scopedRegistries"].Count, "must merge, not add a second entry.");
            JsonNode merged = after["scopedRegistries"][0];
            Assert.AreEqual("https://package.openupm.com/", merged["url"].AsString(),
                "the existing entry's own url text is left exactly as it was -- only 'scopes' is edited.");
            Assert.AreEqual(UloopInstaller.UloopPackageId, merged["scopes"][0].AsString());
        }

        // -- FindMatchingScopedRegistry: direct tests of the lookup itself, independent
        // of Plan()'s other branches (see that method's doc comment for why this
        // matters -- it is what let this stream isolate the IsAlreadyInstalled bug from
        // a predicate bug instead of guessing from one failing assertion). Renamed from
        // HasConflictingScopedRegistry as part of the 2026-08-04 URL-primary-key fix --
        // see FindMatchingScopedRegistry's doc comment for the full history of why the
        // old name-first, bool-returning shape was retired. --------------------------

        [Test]
        public void FindMatchingScopedRegistry_NoScopedRegistriesArray_ReturnsNull()
        {
            JsonNode root = JsonParser.Parse("{\"dependencies\":{}}");

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

            Assert.IsNull(matching);
        }

        [Test]
        public void FindMatchingScopedRegistry_UnrelatedNameAndUrl_ReturnsNull_LeftForCallerToLeaveAlone()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"some.other.registry\",\"url\":\"https://example.invalid\",\"scopes\":[]}]}");

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

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

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

            Assert.IsNull(matching);
        }

        [Test]
        public void FindMatchingScopedRegistry_DifferentNameSameUrl_ReturnsTheEntry()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM Registry\",\"url\":\"https://package.openupm.com\","
                + "\"scopes\":[\"com.some.other.thing\"]}]}");

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "url match must be found regardless of the entry's own display name.");
            Assert.AreEqual("OpenUPM Registry", matching["name"].AsString());
        }

        [Test]
        public void FindMatchingScopedRegistry_TrailingSlashUrl_StillMatches()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"https://package.openupm.com/\",\"scopes\":[]}]}");

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "a single trailing slash must not defeat the match.");
        }

        [Test]
        public void FindMatchingScopedRegistry_UrlCaseDiffers_StillMatches()
        {
            // The fix's own stated rule: "compare scheme+host OrdinalIgnoreCase".
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"HTTPS://PACKAGE.OPENUPM.COM\",\"scopes\":[]}]}");

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

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
                matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);
            });
            Assert.IsNull(matching);
        }

        [Test]
        public void FindMatchingScopedRegistry_ScopesAlreadyCoverOurs_StillReturnsTheEntry()
        {
            JsonNode root = JsonParser.Parse(
                "{\"scopedRegistries\":[{\"name\":\"OpenUPM\",\"url\":\"https://package.openupm.com\","
                + "\"scopes\":[\"com.some.other.thing\",\"io.github.hatayama.uloopmcp\"]}]}");

            JsonNode matching = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

            Assert.IsNotNull(matching, "a url match is returned regardless of whether scopes already covers ours -- "
                + "BuildManifestAfterText is what decides whether to append the scope.");
        }

        // -- Plan: unreadable / malformed manifest ---------------------------------

        [Test]
        public void Plan_MissingManifestFile_ReturnsBlockingManifestUnreadableCaveat()
        {
            // No manifest.json written at all under Packages/.
            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.None, plan.Method);
            Assert.IsFalse(plan.CanProceed());
            Assert.AreEqual(1, plan.Caveats.Count);
            Assert.AreEqual(UloopInstaller.CaveatCodeManifestUnreadable, plan.Caveats[0].Code);
            Assert.IsTrue(plan.Caveats[0].Blocking);
        }

        [Test]
        public void Plan_MalformedManifestJson_ReturnsBlockingManifestUnreadableCaveat()
        {
            WriteManifest("{ this is not valid json");

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.None, plan.Method);
            Assert.IsFalse(plan.CanProceed());
            Assert.AreEqual(UloopInstaller.CaveatCodeManifestUnreadable, plan.Caveats[0].Code);
            Assert.IsTrue(plan.Caveats[0].Blocking);
        }

        [Test]
        public void Plan_ManifestRootIsAnArrayNotAnObject_ReturnsBlockingManifestUnreadableCaveat()
        {
            WriteManifest("[1,2,3]");

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.None, plan.Method);
            Assert.AreEqual(UloopInstaller.CaveatCodeManifestUnreadable, plan.Caveats[0].Code);
        }

        [Test]
        public void Plan_NullOrEmptyProjectRoot_ReturnsBlockingManifestUnreadableCaveat()
        {
            Assert.AreEqual(UloopInstaller.CaveatCodeManifestUnreadable,
                UloopInstaller.Plan(null, () => true).Caveats[0].Code);
            Assert.AreEqual(UloopInstaller.CaveatCodeManifestUnreadable,
                UloopInstaller.Plan(string.Empty, () => true).Caveats[0].Code);
        }

        // -- Plan: vpm-manifest.json (VCC/VPM project) -----------------------------

        [Test]
        public void Plan_VpmManifestPresent_AddsNonBlockingVccCaveat_StillCanProceed()
        {
            WriteManifest(CleanManifest);
            WriteVpmManifest();

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, plan.Method);
            Assert.IsTrue(plan.CanProceed(), "VCC is a warning, not a blocker.");
            bool found = false;
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                if (caveat.Code == UloopInstaller.CaveatCodeVccProject)
                {
                    found = true;
                    Assert.IsFalse(caveat.Blocking);
                }
            }
            Assert.IsTrue(found, "expected a vcc-project caveat.");
        }

        [Test]
        public void Plan_NoVpmManifest_NoVccCaveat()
        {
            WriteManifest(CleanManifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                Assert.AreNotEqual(UloopInstaller.CaveatCodeVccProject, caveat.Code);
            }
        }

        // -- Plan: offline probe ---------------------------------------------------

        [Test]
        public void Plan_NetworkUnavailable_AddsNonBlockingOfflineCaveat_StillCanProceed()
        {
            WriteManifest(CleanManifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => false);

            Assert.IsTrue(plan.CanProceed(), "offline is a warning, not a blocker -- editing text needs no network.");
            bool found = false;
            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                if (caveat.Code == UloopInstaller.CaveatCodeOffline)
                {
                    found = true;
                    Assert.IsFalse(caveat.Blocking);
                }
            }
            Assert.IsTrue(found, "expected an offline caveat.");
        }

        [Test]
        public void Plan_NetworkProbeThrows_FailsOpen_NoOfflineCaveat()
        {
            WriteManifest(CleanManifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, delegate { throw new InvalidOperationException("boom"); });

            foreach (UloopInstallCaveat caveat in plan.Caveats)
            {
                Assert.AreNotEqual(UloopInstaller.CaveatCodeOffline, caveat.Code);
            }
        }

        // -- Plan: the Git route no longer exists (2026-08-03 revision) ------------

        [Test]
        public void Plan_NeverProducesTheGitUrlMethod()
        {
            // UloopInstallMethod.GitUrl still exists on the shared enum (this stream
            // does not own UloopInstallPlan.cs), but this class retired that route
            // entirely -- see the class doc comment for why (measurement showed the
            // real-world distribution is OpenUPM, not Git).
            WriteManifest(CleanManifest);

            UloopInstallPlan plan = UloopInstaller.Plan(_root, () => true);

            Assert.AreNotEqual(UloopInstallMethod.GitUrl, plan.Method);
        }

        // -- Apply: refuses a blocked/invalid plan ---------------------------------
        // None of these plans ever reach ApplyManifestRoute, so the stale-plan guard
        // (readAllText) must not be invoked either -- FailIfCalledReader() proves that
        // the same way FailIfCalledWriter()/FailIfCalledClientAdd() already did for the
        // other two seams.

        [Test]
        public void Apply_NullPlan_Refused_DoesNotInvokeAnySeam()
        {
            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                null,
                FailIfCalledReader(),
                FailIfCalledWriter(),
                FailIfCalledClientAdd(),
                null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureRefused, result.FailureCode);
        }

        [Test]
        public void Apply_AlreadyInstalledPlan_Refused_DoesNotInvokeAnySeam()
        {
            var plan = new UloopInstallPlan { AlreadyInstalled = true };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, FailIfCalledReader(), FailIfCalledWriter(), FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureRefused, result.FailureCode);
        }

        [Test]
        public void Apply_PlanWithBlockingCaveat_Refused_DoesNotInvokeAnySeam()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                ManifestBefore = "BEFORE",
                ManifestAfter = "AFTER",
                BackupPath = "C:\\fake\\manifest.json.uap-backup"
            };
            plan.Caveats.Add(new UloopInstallCaveat { Code = "scoped-registry-conflict", Blocking = true });

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, FailIfCalledReader(), FailIfCalledWriter(), FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureRefused, result.FailureCode);
        }

        [Test]
        public void Apply_GitUrlMethodPlan_RefusedDefensively_EvenThoughCanProceedIsTrue()
        {
            // A hand-constructed GitUrl-method plan (Plan() itself never produces one
            // any more) must still be refused rather than reaching removed code.
            var plan = new UloopInstallPlan { Method = UloopInstallMethod.GitUrl, GitUrl = "https://example.invalid/whatever.git" };
            Assert.IsTrue(plan.CanProceed(), "sanity: CanProceed() itself does not know GitUrl was retired.");

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, FailIfCalledReader(), FailIfCalledWriter(), FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureRefused, result.FailureCode);
        }

        // -- Apply: the 2026-08-04 stale-plan guard ---------------------------------
        // Regression coverage for defect (1): ApplyManifestRoute used to write
        // plan.ManifestBefore to the backup and plan.ManifestAfter over the manifest
        // UNCONDITIONALLY, never re-reading disk first. If the real manifest.json had
        // changed since Plan() captured ManifestBefore (the user added a package while
        // the confirmation card sat open, a git pull landed, etc.), the backup received
        // stale text and the live manifest was overwritten with text computed from that
        // same stale snapshot -- the intervening change gone from both places,
        // unrecoverably. These tests pin the fix: a changed-or-unreadable disk state
        // must refuse immediately, before either write and before Client.Add.

        [Test]
        public void Apply_ManifestUnchangedSincePlan_ProceedsToWriteBackupAndManifest()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            string requestedReadPath = null;

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan,
                delegate (string path) { requestedReadPath = path; return plan.ManifestBefore; },
                NoOpWriter(),
                NoOpClientAdd(),
                null);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(plan.ManifestPath, requestedReadPath,
                "the guard must re-read exactly the path Plan() recorded.");
        }

        [Test]
        public void Apply_ManifestChangedSincePlan_RefusesBeforeAnyWriteOrClientAdd_ReturnsDistinctFailureCode()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            // Simulates a package having been added to the real manifest.json while the
            // confirmation card sat open: the live file no longer reads back as
            // ManifestBefore.
            Func<string, string> reader = delegate (string path) { return "BEFORE-TEXT-PLUS-AN-INTERVENING-EDIT"; };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, reader, FailIfCalledWriter(), FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureManifestChangedSincePlan, result.FailureCode);
            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, result.Method);
        }

        [Test]
        public void Apply_RereadManifestThrows_RefusesBeforeAnyWriteOrClientAdd_ReturnsDistinctFailureCode()
        {
            // The file being gone/unreadable at Apply time is just as much "not what
            // Plan saw" as a text difference is -- must refuse the same way, not throw
            // and not proceed on a guess.
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            Func<string, string> reader = delegate (string path)
            {
                throw new IOException("simulated: manifest.json no longer exists");
            };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, reader, FailIfCalledWriter(), FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureManifestChangedSincePlan, result.FailureCode);
            StringAssert.Contains("simulated: manifest.json no longer exists", result.FailureDetail);
        }

        // -- Apply: success path, including the Client.Add(no version) call --------

        [Test]
        public void Apply_Success_WritesBackupThenManifestThenCallsClientAddWithBarePackageId()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            var writes = new List<Tuple<string, string>>();
            Action<string, string> writer = delegate (string path, string content)
            {
                writes.Add(Tuple.Create(path, content));
            };
            string requestedPackageId = null;
            Func<string, AddRequest> clientAdd = delegate (string id) { requestedPackageId = id; return null; };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(plan, UnchangedDiskReader(plan), writer, clientAdd, null);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(UloopInstallMethod.ManifestScopedRegistry, result.Method);
            Assert.AreEqual(2, writes.Count);
            Assert.AreEqual(plan.BackupPath, writes[0].Item1);
            Assert.AreEqual(plan.ManifestBefore, writes[0].Item2);
            Assert.AreEqual(plan.ManifestPath, writes[1].Item1);
            Assert.AreEqual(plan.ManifestAfter, writes[1].Item2);
            Assert.AreEqual(UloopInstaller.UloopPackageId, requestedPackageId,
                "Client.Add must be called with the bare package id -- no version, no URL.");
        }

        // -- Apply: backup write fails ----------------------------------------------

        [Test]
        public void Apply_BackupWriteFails_NeverTouchesManifestOrCallsClientAdd_ReturnsBackupWriteFailed()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            Action<string, string> writer = delegate (string path, string content)
            {
                throw new IOException("simulated backup write failure");
            };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, UnchangedDiskReader(plan), writer, FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureBackupWriteFailed, result.FailureCode);
            StringAssert.Contains("simulated backup write failure", result.FailureDetail);
        }

        // -- Apply: manifest write fails -> restore ---------------------------------

        [Test]
        public void Apply_ManifestWriteFails_RestoresBackup_NeverCallsClientAdd_ReturnsManifestWriteFailed()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            var fakeDisk = new Dictionary<string, string>();
            Action<string, string> writer = delegate (string path, string content)
            {
                if (path == plan.ManifestPath && content == plan.ManifestAfter)
                {
                    throw new IOException("simulated manifest write failure");
                }
                fakeDisk[path] = content;
            };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, UnchangedDiskReader(plan), writer, FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureManifestWriteFailed, result.FailureCode);
            Assert.AreEqual(plan.ManifestBefore, fakeDisk[plan.ManifestPath],
                "the manifest must be restored to its original content after the failed write.");
            Assert.AreEqual(plan.ManifestBefore, fakeDisk[plan.BackupPath],
                "the backup itself must remain intact as evidence/recovery.");
        }

        [Test]
        public void Apply_ManifestWriteFails_RestoreAlsoFails_ReturnsDistinctFailureCode()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            Action<string, string> writer = delegate (string path, string content)
            {
                if (path == plan.ManifestPath)
                {
                    throw new IOException("manifest write always fails, including the restore attempt");
                }
            };

            // 2026-08-04: TryRestoreBackup's failure path now always logs through
            // UnityEngine.Debug.LogError (see its doc comment) -- the Unity test runner
            // fails a test outright on an unexpected error-level log, so the expected
            // CRITICAL message must be declared here or this test would fail for the
            // wrong reason (a passing assertion but a "stray error log" failure).
            LogAssert.Expect(LogType.Error, new Regex("CRITICAL.*restore manifest\\.json"));

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(
                plan, UnchangedDiskReader(plan), writer, FailIfCalledClientAdd(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureManifestWriteFailedRestoreFailed, result.FailureCode);
        }

        // -- Apply: Client.Add throws -> restore ------------------------------------

        [Test]
        public void Apply_ClientAddThrows_RestoresBackup_ReturnsClientAddThrew()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            var fakeDisk = new Dictionary<string, string>();
            Action<string, string> writer = delegate (string path, string content)
            {
                fakeDisk[path] = content;
            };
            Func<string, AddRequest> clientAdd = delegate (string id)
            {
                throw new InvalidOperationException("simulated Client.Add failure");
            };

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(plan, UnchangedDiskReader(plan), writer, clientAdd, null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureClientAddThrew, result.FailureCode);
            StringAssert.Contains("simulated Client.Add failure", result.FailureDetail);
            Assert.AreEqual(plan.ManifestBefore, fakeDisk[plan.ManifestPath],
                "a synchronous Client.Add throw means the request never dispatched -- the manifest edit must be undone.");
        }

        [Test]
        public void Apply_ClientAddThrows_RestoreAlsoFails_ReturnsDistinctFailureCode()
        {
            var plan = new UloopInstallPlan
            {
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ManifestPath = "C:\\fake\\manifest.json",
                BackupPath = "C:\\fake\\manifest.json.uap-backup",
                ManifestBefore = "BEFORE-TEXT",
                ManifestAfter = "AFTER-TEXT"
            };
            Action<string, string> writer = delegate (string path, string content)
            {
                if (path == plan.ManifestPath && content == plan.ManifestBefore)
                {
                    // Only the RESTORE write (manifest path, original content) fails;
                    // the earlier backup write and the initial manifest write succeed.
                    throw new IOException("restore write fails");
                }
            };
            Func<string, AddRequest> clientAdd = delegate (string id)
            {
                throw new InvalidOperationException("simulated Client.Add failure");
            };

            // See the identical comment in Apply_ManifestWriteFails_RestoreAlsoFails_
            // ReturnsDistinctFailureCode above -- same LogCritical path, same reason.
            LogAssert.Expect(LogType.Error, new Regex("CRITICAL.*restore manifest\\.json"));

            UloopInstallApplyResult result = UloopInstaller.ApplyWithSeams(plan, UnchangedDiskReader(plan), writer, clientAdd, null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopInstaller.ApplyFailureClientAddThrewRestoreFailed, result.FailureCode);
        }

        // -- IsNetworkAvailable: smoke test -----------------------------------------

        [Test]
        public void IsNetworkAvailable_NeverThrows()
        {
            Assert.DoesNotThrow(delegate { UloopInstaller.IsNetworkAvailable(); });
        }

        // -- small helpers ------------------------------------------------------------

        /// <summary>Read seam stub simulating "disk still matches exactly what Plan() captured".</summary>
        private static Func<string, string> UnchangedDiskReader(UloopInstallPlan plan)
        {
            return delegate (string path) { return plan.ManifestBefore; };
        }

        private static Func<string, string> FailIfCalledReader()
        {
            return delegate (string path)
            {
                Assert.Fail("a refused/wrong-route plan must never re-read manifest.json (path='" + path + "').");
                return null;
            };
        }

        private static Action<string, string> NoOpWriter()
        {
            return delegate (string path, string content) { };
        }

        private static Func<string, AddRequest> NoOpClientAdd()
        {
            return delegate (string id) { return null; };
        }

        private static Action<string, string> FailIfCalledWriter()
        {
            return delegate (string path, string content)
            {
                Assert.Fail("a refused/wrong-route plan must never write a file (path='" + path + "').");
            };
        }

        private static Func<string, AddRequest> FailIfCalledClientAdd()
        {
            return delegate (string id)
            {
                Assert.Fail("a refused/wrong-route plan must never call Client.Add (id='" + id + "').");
                return null;
            };
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

            string matched = UloopInstaller.MatchLineEndings(lfNew, crlfOriginal);

            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(matched, "(?<!\r)\n"),
                "every newline must be CRLF when the source was CRLF");
        }

        [Test]
        public void MatchLineEndings_LfSource_LeavesLfAlone()
        {
            string lfOriginal = "{\n  \"dependencies\": {}\n}\n";
            string lfNew = "{\n  \"dependencies\": {},\n  \"scopedRegistries\": []\n}\n";

            Assert.AreEqual(lfNew, UloopInstaller.MatchLineEndings(lfNew, lfOriginal));
        }
    }
}