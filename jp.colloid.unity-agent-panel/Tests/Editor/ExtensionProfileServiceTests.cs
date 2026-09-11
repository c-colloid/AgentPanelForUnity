using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// End-to-end (still disk-only, no CLI/PanelSettings) coverage of
    /// <see cref="ExtensionProfileService"/> wiring the bundled catalog,
    /// the user sidecar, detection and trust together -- the same pipeline
    /// AgentHub.StartClient and SettingsView both call through.
    ///
    /// <see cref="ExtensionProfileDetectionCache"/>'s environment snapshot
    /// (manifest.json/PackageCache) is a per-domain-load static cache keyed
    /// only by "has it loaded yet", NOT by which project root asked for it
    /// -- fine in production (there is only ever one real project root per
    /// domain-load) but exactly the kind of thing that would let one test's
    /// fake project root leak into another's, so every test that supplies
    /// its own fake project root resets it first.
    /// </summary>
    public class ExtensionProfileServiceTests
    {
        private string _projectRoot;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(Path.GetTempPath(),
                "ExtensionProfileServiceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectRoot);
            ExtensionProfileDetectionCache.ResetForTests();
            // OPS-11: deterministic machine salt so approval tokens are
            // computable in-test and EditorPrefs stays untouched.
            MachineApprovalSalt.OverrideForTests = delegate { return "service-test-salt"; };
        }

        [TearDown]
        public void TearDown()
        {
            ExtensionProfileDetectionCache.ResetForTests();
            MachineApprovalSalt.OverrideForTests = null;
            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        private void WriteUserProfile(string fileName, string json)
        {
            string dir = UserExtensionProfileStore.ResolveDirectory(_projectRoot);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), json, new UTF8Encoding(false));
        }

        [Test]
        public void BuildStatuses_AlwaysIncludesAllBundledProfiles()
        {
            List<ExtensionProfileStatus> statuses = ExtensionProfileService.BuildStatuses(
                _projectRoot, new List<string>());
            int bundledCount = 0;
            foreach (ExtensionProfileStatus status in statuses)
            {
                if (status.IsBundled)
                {
                    bundledCount++;
                    Assert.IsTrue(status.Trusted, status.Profile.Id + " (bundled) must always be Trusted");
                }
            }
            // Since the 2026-09-11 core/pro split the bundled JSONs ship in
            // Agent Panel Pro and reach Core through IExtensionProfileProvider,
            // so the count is 0 without Pro and 5 with it; Pro's own tests
            // pin the five. What Core guarantees is that every bundled
            // profile the catalog knows shows up here, always trusted.
            Assert.AreEqual(ExtensionProfileCatalog.LoadBundled().Count, bundledCount,
                "every bundled profile the catalog loads must appear in BuildStatuses");
        }

        [Test]
        public void BuildStatuses_UserProfileWithRealTypeName_IsDetected_ButPendingUntilApproved()
        {
            // "Transform" is a real, always-compiled UnityEngine type, so
            // detection is deterministic without depending on any
            // third-party SDK being present in the test environment.
            WriteUserProfile("fake-sdk.json",
                "{ \"id\": \"fake-sdk\", \"displayName\": \"Fake SDK\","
                + " \"detection\": { \"typeNames\": [\"Transform\"] },"
                + " \"instructionLines\": [\"fake instruction\"] }");

            List<ExtensionProfileStatus> statuses = ExtensionProfileService.BuildStatuses(
                _projectRoot, new List<string>());
            ExtensionProfileStatus fakeSdk = FindById(statuses, "fake-sdk");
            Assert.IsNotNull(fakeSdk);
            Assert.IsTrue(fakeSdk.Detected, "Transform always exists -- fake-sdk must be Detected");
            Assert.IsFalse(fakeSdk.Trusted, "an unapproved user profile must be Pending, not Trusted");
            Assert.IsNotEmpty(fakeSdk.ContentHashHex);

            // Not yet approved -- must not be injected.
            string section = ExtensionProfileService.ComposeAppendSection(_projectRoot, true, new List<string>());
            StringAssert.DoesNotContain("Fake SDK", section);
        }

        [Test]
        public void BuildStatuses_ApprovedUserProfileHash_BecomesTrusted_AndIsInjected()
        {
            WriteUserProfile("fake-sdk.json",
                "{ \"id\": \"fake-sdk\", \"displayName\": \"Fake SDK\","
                + " \"detection\": { \"typeNames\": [\"Transform\"] },"
                + " \"instructionLines\": [\"fake instruction\"] }");

            List<ExtensionProfileStatus> firstPass = ExtensionProfileService.BuildStatuses(
                _projectRoot, new List<string>());
            string hash = FindById(firstPass, "fake-sdk").ContentHashHex;

            // OPS-11: the store holds machine-bound tokens, not raw hashes.
            var approved = new List<string>
            {
                ExtensionProfileTrust.ComputeApprovalToken("service-test-salt", hash)
            };
            List<ExtensionProfileStatus> secondPass = ExtensionProfileService.BuildStatuses(_projectRoot, approved);
            ExtensionProfileStatus fakeSdk = FindById(secondPass, "fake-sdk");
            Assert.IsTrue(fakeSdk.Trusted);

            string section = ExtensionProfileService.ComposeAppendSection(_projectRoot, true, approved);
            StringAssert.Contains("## Fake SDK", section);
            StringAssert.Contains("fake instruction", section);
        }

        [Test]
        public void ComposeAppendSection_ToggleOff_NeverInjectsEvenAnApprovedDetectedProfile()
        {
            WriteUserProfile("fake-sdk.json",
                "{ \"id\": \"fake-sdk\", \"displayName\": \"Fake SDK\","
                + " \"detection\": { \"typeNames\": [\"Transform\"] },"
                + " \"instructionLines\": [\"fake instruction\"] }");
            List<ExtensionProfileStatus> statuses = ExtensionProfileService.BuildStatuses(
                _projectRoot, new List<string>());
            string hash = FindById(statuses, "fake-sdk").ContentHashHex;
            var approved = new List<string>
            {
                ExtensionProfileTrust.ComputeApprovalToken("service-test-salt", hash)
            };

            string section = ExtensionProfileService.ComposeAppendSection(_projectRoot, false, approved);
            Assert.AreEqual(string.Empty, section);
        }

        [Test]
        public void BuildStatuses_UserProfileNotDetected_NeverAppearsInjected_EvenIfApproved()
        {
            WriteUserProfile("fake-sdk.json",
                "{ \"id\": \"fake-sdk\", \"displayName\": \"Fake SDK\","
                + " \"detection\": { \"typeNames\": [\"ThisTypeDefinitelyDoesNotExistAnywhere12345\"] },"
                + " \"instructionLines\": [\"fake instruction\"] }");
            List<ExtensionProfileStatus> statuses = ExtensionProfileService.BuildStatuses(
                _projectRoot, new List<string>());
            ExtensionProfileStatus fakeSdk = FindById(statuses, "fake-sdk");
            Assert.IsFalse(fakeSdk.Detected);

            string hash = fakeSdk.ContentHashHex;
            string section = ExtensionProfileService.ComposeAppendSection(
                _projectRoot, true, new List<string>
                {
                    ExtensionProfileTrust.ComputeApprovalToken("service-test-salt", hash)
                });
            StringAssert.DoesNotContain("Fake SDK", section);
        }

        private static ExtensionProfileStatus FindById(List<ExtensionProfileStatus> statuses, string id)
        {
            foreach (ExtensionProfileStatus status in statuses)
            {
                if (status.Profile.Id == id)
                {
                    return status;
                }
            }
            return null;
        }
    }
}
