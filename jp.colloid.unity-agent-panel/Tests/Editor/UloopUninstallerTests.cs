using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor.PackageManager.Requests;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The removal counterpart to UloopInstallerTests, and it exists for the
    /// same reason that fixture does: this code edits manifest.json, which is
    /// the user's asset. Plan() is pure and runs over fake project
    /// directories under a temp root; both write paths run through their
    /// seams, so no test ever issues a real Client.Remove or risks a real
    /// file.
    ///
    /// The cases worth naming, all from docs/design-notes/2026-09-22-uloop-
    /// remove-from-panel.md section 3 -- each one is a way the cleanup could
    /// silently take something that is not its to take:
    /// a broader prefix scope the panel never wrote; a scope another
    /// dependency still resolves through; a registry that is not OpenUPM;
    /// and running the cleanup before the removal has landed.
    /// </summary>
    [TestFixture]
    public class UloopUninstallerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "UloopUninstallerTests_" + Guid.NewGuid().ToString("N"));
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

        /// <summary>uLoop installed exactly the way UloopInstaller leaves a project: one OpenUPM entry whose only scope is the uLoop id.</summary>
        private const string InstalledAloneJson = @"{
  ""dependencies"": {
    ""com.unity.ide.rider"": ""3.0.24"",
    ""io.github.hatayama.uloopmcp"": ""3.6.3""
  },
  ""scopedRegistries"": [
    {
      ""name"": ""OpenUPM"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""io.github.hatayama.uloopmcp"" ]
    }
  ]
}";

        // -- Plan: the happy path -------------------------------------------------

        [Test]
        public void Plan_InstalledAlone_RemovesTheWholeRegistryEntry()
        {
            WriteManifest(InstalledAloneJson);
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.IsTrue(plan.Installed);
            Assert.AreEqual("io.github.hatayama.uloopmcp", plan.PackageId);
            Assert.IsTrue(plan.CanProceed());
            Assert.AreEqual(UloopRegistryCleanup.DropRegistry, plan.RegistryCleanup,
                "the entry's only scope was uLoop's, so keeping the entry would leave a registry that serves nothing");
            Assert.AreEqual(0, plan.RetainedScopes.Count);
            Assert.AreEqual("OpenUPM", plan.RegistryName);
            StringAssert.DoesNotContain("io.github.hatayama.uloopmcp", plan.ManifestAfterProjected);
            StringAssert.Contains("com.unity.ide.rider", plan.ManifestAfterProjected,
                "every other dependency must survive the projection");
        }

        [Test]
        public void Plan_RegistrySharedWithOtherScopes_DropsOnlyOurScope()
        {
            WriteManifest(@"{
  ""dependencies"": {
    ""com.cysharp.unitask"": ""2.5.0"",
    ""io.github.hatayama.uloopmcp"": ""3.6.3""
  },
  ""scopedRegistries"": [
    {
      ""name"": ""OpenUPM"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""com.cysharp.unitask"", ""io.github.hatayama.uloopmcp"" ]
    }
  ]
}");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.AreEqual(UloopRegistryCleanup.DropScope, plan.RegistryCleanup);
            CollectionAssert.AreEqual(new List<string> { "com.cysharp.unitask" }, plan.RetainedScopes);
            StringAssert.Contains("com.cysharp.unitask", plan.ManifestAfterProjected);
            StringAssert.DoesNotContain("io.github.hatayama.uloopmcp", plan.ManifestAfterProjected);
        }

        // -- Plan: the four ways the cleanup must NOT overreach ---------------------

        [Test]
        public void Plan_BroaderPrefixScope_IsNeverDropped()
        {
            // The panel writes the exact id as a scope; a prefix like this
            // was written by someone else and covers packages this removal
            // knows nothing about.
            WriteManifest(@"{
  ""dependencies"": { ""io.github.hatayama.uloopmcp"": ""3.6.3"" },
  ""scopedRegistries"": [
    {
      ""name"": ""OpenUPM"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""io.github.hatayama"" ]
    }
  ]
}");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.IsTrue(plan.CanProceed(), "the package itself is still removable");
            Assert.AreEqual(UloopRegistryCleanup.None, plan.RegistryCleanup);
            StringAssert.Contains("io.github.hatayama", plan.ManifestAfterProjected);
        }

        [Test]
        public void Plan_ScopeStillCoveringAnotherDependency_IsKept_AndNamesIt()
        {
            // Unity resolves a scope as a dot-delimited prefix, so the uLoop
            // id as a scope still covers anything beneath it.
            WriteManifest(@"{
  ""dependencies"": {
    ""io.github.hatayama.uloopmcp"": ""3.6.3"",
    ""io.github.hatayama.uloopmcp.extras"": ""1.0.0""
  },
  ""scopedRegistries"": [
    {
      ""name"": ""OpenUPM"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""io.github.hatayama.uloopmcp"" ]
    }
  ]
}");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.AreEqual(UloopRegistryCleanup.None, plan.RegistryCleanup);
            CollectionAssert.Contains(plan.ScopeHolders, "io.github.hatayama.uloopmcp.extras");
            Assert.IsTrue(HasCaveat(plan, UloopUninstaller.CaveatCodeRegistryScopeKept),
                "a cleanup that was downgraded must say so, not quietly do less than it offered");
        }

        [Test]
        public void Plan_ForeignRegistryClaimingTheScope_IsLeftAloneAndReported()
        {
            WriteManifest(@"{
  ""dependencies"": { ""io.github.hatayama.uloopmcp"": ""3.6.3"" },
  ""scopedRegistries"": [
    {
      ""name"": ""Company Mirror"",
      ""url"": ""https://npm.example.invalid"",
      ""scopes"": [ ""io.github.hatayama.uloopmcp"" ]
    }
  ]
}");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.AreEqual(UloopRegistryCleanup.None, plan.RegistryCleanup);
            Assert.IsTrue(HasCaveat(plan, UloopUninstaller.CaveatCodeForeignRegistryKept));
            StringAssert.Contains("npm.example.invalid", plan.ManifestAfterProjected,
                "a registry the project pointed somewhere deliberately is not this panel's to edit");
        }

        [Test]
        public void Plan_AlwaysWarnsThatThePanelsOwnUloopSettingsStay()
        {
            WriteManifest(InstalledAloneJson);
            Assert.IsTrue(HasCaveat(UloopUninstaller.Plan(_root), UloopUninstaller.CaveatCodePanelSettingsKept));
        }

        // -- Plan: refusals --------------------------------------------------------

        [Test]
        public void Plan_NotInstalled_Blocks()
        {
            WriteManifest(@"{ ""dependencies"": { ""com.unity.ide.rider"": ""3.0.24"" } }");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.IsFalse(plan.Installed);
            Assert.IsFalse(plan.CanProceed());
            Assert.IsTrue(HasCaveat(plan, UloopUninstaller.CaveatCodeNotInstalled));
        }

        [Test]
        public void Plan_ScopeWithoutDependency_IsNotInstalled()
        {
            // The exact false positive UloopDetector's own doc comment
            // records: a scopes entry is not a dependency.
            WriteManifest(@"{
  ""dependencies"": { ""com.unity.ide.rider"": ""3.0.24"" },
  ""scopedRegistries"": [
    {
      ""name"": ""OpenUPM"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""io.github.hatayama.uloopmcp"" ]
    }
  ]
}");
            Assert.IsFalse(UloopUninstaller.Plan(_root).Installed);
        }

        [Test]
        public void Plan_MissingOrMalformedManifest_Blocks()
        {
            UloopUninstallPlan missing = UloopUninstaller.Plan(_root);
            Assert.IsFalse(missing.CanProceed());
            Assert.IsTrue(HasCaveat(missing, UloopInstaller.CaveatCodeManifestUnreadable));

            WriteManifest("{ not json");
            UloopUninstallPlan malformed = UloopUninstaller.Plan(_root);
            Assert.IsFalse(malformed.CanProceed());
            Assert.IsTrue(HasCaveat(malformed, UloopInstaller.CaveatCodeManifestUnreadable));

            Assert.IsFalse(UloopUninstaller.Plan(null).CanProceed());
        }

        [Test]
        public void Plan_VpmProject_WarnsButDoesNotBlock()
        {
            WriteManifest(InstalledAloneJson);
            File.WriteAllText(Path.Combine(_root, "Packages", "vpm-manifest.json"), "{}");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            Assert.IsTrue(plan.CanProceed());
            Assert.IsTrue(HasCaveat(plan, UloopInstaller.CaveatCodeVccProject));
        }

        // -- Apply (phase one): dispatch only, writes nothing ----------------------

        [Test]
        public void Apply_DispatchesRemoveForTheIdTheManifestActuallyCarries()
        {
            WriteManifest(InstalledAloneJson);
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);
            string requested = null;
            Func<string, RemoveRequest> remove = delegate (string id) { requested = id; return null; };

            UloopUninstallApplyResult result = UloopUninstaller.ApplyWithSeams(
                plan, UnchangedDiskReader(plan), remove, null);

            Assert.IsTrue(result.Success, result.FailureCode + " " + result.FailureDetail);
            Assert.AreEqual("io.github.hatayama.uloopmcp", requested);
        }

        [Test]
        public void Apply_RefusesAPlanThatCannotProceed_WithoutReadingAnything()
        {
            WriteManifest(@"{ ""dependencies"": {} }");
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);

            UloopUninstallApplyResult result = UloopUninstaller.ApplyWithSeams(
                plan, FailIfCalledReader(), FailIfCalledRemove(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopUninstaller.ApplyFailureRefused, result.FailureCode);
            Assert.IsNull(UloopUninstaller.ApplyWithSeams(null, FailIfCalledReader(), FailIfCalledRemove(), null)
                .ClientRemoveRequest);
        }

        [Test]
        public void Apply_RefusesWhenTheManifestMovedSincePlan()
        {
            WriteManifest(InstalledAloneJson);
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);
            Func<string, string> changedDisk = delegate { return InstalledAloneJson + "\n"; };

            UloopUninstallApplyResult result = UloopUninstaller.ApplyWithSeams(
                plan, changedDisk, FailIfCalledRemove(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopUninstaller.ApplyFailureManifestChangedSincePlan, result.FailureCode);
        }

        [Test]
        public void Apply_ClientRemoveThrowing_IsReportedAndNothingNeededUndoing()
        {
            WriteManifest(InstalledAloneJson);
            UloopUninstallPlan plan = UloopUninstaller.Plan(_root);
            Func<string, RemoveRequest> throwing = delegate { throw new InvalidOperationException("nope"); };

            UloopUninstallApplyResult result = UloopUninstaller.ApplyWithSeams(
                plan, UnchangedDiskReader(plan), throwing, null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopUninstaller.ApplyFailureClientRemoveThrew, result.FailureCode);
            Assert.AreEqual(InstalledAloneJson, File.ReadAllText(ManifestPath),
                "phase one writes nothing, so a throw has nothing to roll back");
        }

        // -- CleanUpRegistry (phase two) -------------------------------------------

        /// <summary>manifest.json as Unity leaves it once Client.Remove has landed: dependency gone, dead registry entry still there.</summary>
        private const string AfterRemovalJson = @"{
  ""dependencies"": {
    ""com.unity.ide.rider"": ""3.0.24""
  },
  ""scopedRegistries"": [
    {
      ""name"": ""OpenUPM"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""io.github.hatayama.uloopmcp"" ]
    }
  ]
}";

        [Test]
        public void Cleanup_AfterRemoval_WritesABackupThenTheTidiedManifest()
        {
            var written = new Dictionary<string, string>();
            UloopRegistryCleanupResult result = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { return AfterRemovalJson; },
                delegate (string path, string content) { written[path] = content; }, null);

            Assert.IsTrue(result.Success, result.FailureCode);
            Assert.IsTrue(result.Wrote);
            Assert.AreEqual(UloopRegistryCleanup.DropRegistry, result.Cleanup);
            Assert.AreEqual(AfterRemovalJson, written[ManifestPath + UloopInstaller.BackupFileSuffix],
                "the backup must hold the text as it was, not the text about to be written");
            StringAssert.DoesNotContain("io.github.hatayama.uloopmcp", written[ManifestPath]);
            StringAssert.Contains("com.unity.ide.rider", written[ManifestPath]);
        }

        [Test]
        public void Cleanup_RefusesWhileThePackageIsStillADependency()
        {
            // The removal has not landed. Stripping a registry a live
            // dependency resolves through is the one thing phase two must
            // never do, so it refuses instead of proceeding best-effort.
            UloopRegistryCleanupResult result = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { return InstalledAloneJson; }, FailIfCalledWriter(), null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopUninstaller.CleanupFailurePackageStillPresent, result.FailureCode);
        }

        [Test]
        public void Cleanup_NothingToClean_SucceedsWithoutWriting()
        {
            UloopRegistryCleanupResult result = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { return @"{ ""dependencies"": { ""com.unity.ide.rider"": ""3.0.24"" } }"; },
                FailIfCalledWriter(), null);

            Assert.IsTrue(result.Success, "nothing to clean is a success, not a failure");
            Assert.IsFalse(result.Wrote);
            Assert.AreEqual(UloopRegistryCleanup.None, result.Cleanup);
        }

        [Test]
        public void Cleanup_UnreadableManifest_FailsWithoutWriting()
        {
            UloopRegistryCleanupResult broken = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { throw new IOException("gone"); }, FailIfCalledWriter(), null);
            Assert.IsFalse(broken.Success);
            Assert.AreEqual(UloopUninstaller.CleanupFailureManifestUnreadable, broken.FailureCode);

            UloopRegistryCleanupResult malformed = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { return "{ not json"; }, FailIfCalledWriter(), null);
            Assert.IsFalse(malformed.Success);
            Assert.AreEqual(UloopUninstaller.CleanupFailureManifestUnreadable, malformed.FailureCode);
        }

        [Test]
        public void Cleanup_BackupWriteFailure_NeverTouchesTheManifest()
        {
            var written = new List<string>();
            Action<string, string> writer = delegate (string path, string content)
            {
                written.Add(path);
                throw new IOException("read-only volume");
            };

            UloopRegistryCleanupResult result = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { return AfterRemovalJson; }, writer, null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopUninstaller.CleanupFailureBackupWriteFailed, result.FailureCode);
            Assert.AreEqual(1, written.Count, "the manifest write must not be attempted after the backup failed");
            Assert.AreEqual(ManifestPath + UloopInstaller.BackupFileSuffix, written[0]);
        }

        [Test]
        public void Cleanup_ManifestWriteFailure_RestoresTheOriginal()
        {
            var writes = new List<KeyValuePair<string, string>>();
            Action<string, string> writer = delegate (string path, string content)
            {
                writes.Add(new KeyValuePair<string, string>(path, content));
                // Fail the first write to the manifest itself; let the
                // backup and the restore through.
                if (path == ManifestPath && writes.Count == 2)
                {
                    throw new IOException("disk full");
                }
            };

            UloopRegistryCleanupResult result = UloopUninstaller.CleanUpRegistryWithSeams(
                _root, delegate { return AfterRemovalJson; }, writer, null);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(UloopUninstaller.CleanupFailureManifestWriteFailed, result.FailureCode);
            Assert.AreEqual(3, writes.Count, "backup, failed manifest write, restore");
            Assert.AreEqual(ManifestPath, writes[2].Key);
            Assert.AreEqual(AfterRemovalJson, writes[2].Value, "the restore must put the ORIGINAL text back");
        }

        // -- helpers ---------------------------------------------------------------

        private static bool HasCaveat(UloopUninstallPlan plan, string code)
        {
            for (int i = 0; i < plan.Caveats.Count; i++)
            {
                if (plan.Caveats[i] != null && plan.Caveats[i].Code == code)
                {
                    return true;
                }
            }
            return false;
        }

        private static Func<string, string> UnchangedDiskReader(UloopUninstallPlan plan)
        {
            return delegate { return plan.ManifestBefore; };
        }

        private static Func<string, string> FailIfCalledReader()
        {
            return delegate
            {
                Assert.Fail("the manifest must not be read on a refused plan");
                return null;
            };
        }

        private static Func<string, RemoveRequest> FailIfCalledRemove()
        {
            return delegate
            {
                Assert.Fail("Client.Remove must not be dispatched here");
                return null;
            };
        }

        private static Action<string, string> FailIfCalledWriter()
        {
            return delegate { Assert.Fail("nothing should have been written"); };
        }
    }
}
