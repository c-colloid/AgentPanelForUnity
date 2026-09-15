using System.Collections.Generic;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Detection matrix for <see cref="ExtensionProfileDetector"/> (design
    /// section 3b/C2): package hit / type hit / neither, with every
    /// environment fact injected as a plain string/fake resolver -- no
    /// disk or TypeCache access, matching this class's own doc comment.
    /// </summary>
    public class ExtensionProfileDetectorTests
    {
        /// <summary>Fake <see cref="IUapTypeExistenceResolver"/> backed by a fixed set (C2: "typeNames via a TypeCache-backed resolver seam (interface so tests fake it)").</summary>
        private sealed class FakeTypeResolver : IUapTypeExistenceResolver
        {
            private readonly HashSet<string> _existing;

            public FakeTypeResolver(params string[] existing)
            {
                _existing = new HashSet<string>(existing);
            }

            public bool TypeExists(string typeName)
            {
                return _existing.Contains(typeName);
            }
        }

        private static ExtensionProfile MakeProfile(string[] packageIds, string[] typeNames)
        {
            return new ExtensionProfile
            {
                Id = "test-profile",
                DisplayName = "Test Profile",
                PackageIds = new List<string>(packageIds),
                TypeNames = new List<string>(typeNames)
            };
        }

        private const string ManifestWithVrchatAvatars =
            "{ \"dependencies\": { \"com.vrchat.avatars\": \"3.10.4\", \"com.unity.textmeshpro\": \"3.0.6\" } }";

        // -- IsPackageIdPresent ---------------------------------------------------

        [Test]
        public void IsPackageIdPresent_ManifestDependencyKeyMatches_ReturnsTrue()
        {
            Assert.IsTrue(ExtensionProfileDetector.IsPackageIdPresent(
                "com.vrchat.avatars", ManifestWithVrchatAvatars, null));
        }

        [Test]
        public void IsPackageIdPresent_ManifestDoesNotHaveIt_PackageCacheHasIt_ReturnsTrue()
        {
            var cacheDirs = new List<string> { "com.vrmc.vrm@0.128.2" };
            Assert.IsTrue(ExtensionProfileDetector.IsPackageIdPresent(
                "com.vrmc.vrm", ManifestWithVrchatAvatars, cacheDirs));
        }

        [Test]
        public void IsPackageIdPresent_PackageCacheExactDirectoryNameMatch_ReturnsTrue()
        {
            var cacheDirs = new List<string> { "com.vrmc.vrm" };
            Assert.IsTrue(ExtensionProfileDetector.IsPackageIdPresent(
                "com.vrmc.vrm", string.Empty, cacheDirs));
        }

        [Test]
        public void IsPackageIdPresent_PackageCacheHasUnrelatedPrefix_DoesNotFalsePositive()
        {
            // "com.vrmc.vrm" must not match a cache dir for a DIFFERENT
            // package that merely starts with the same characters.
            var cacheDirs = new List<string> { "com.vrmc.vrmshaders@1.0.0" };
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent(
                "com.vrmc.vrm", string.Empty, cacheDirs));
        }

        [Test]
        public void IsPackageIdPresent_VpmEmbeddedPackageDirectory_ReturnsTrue()
        {
            // VCC/ALCOM install a VPM package by COPYING it into
            // Packages/<id>/ and recording it in Packages/vpm-manifest.json
            // -- it is an embedded package, so it is absent from
            // manifest.json's dependencies and from Library/PackageCache.
            // ExtensionProfileDetectionCache therefore feeds the Packages/
            // listing in alongside the PackageCache one; without it the
            // packageIds half could never fire for NDMF, Modular Avatar or
            // the VRChat SDK itself.
            var packageDirs = new List<string> { "nadena.dev.ndmf", "nadena.dev.modular-avatar", "com.vrchat.avatars" };
            Assert.IsTrue(ExtensionProfileDetector.IsPackageIdPresent(
                "nadena.dev.modular-avatar", "{ \"dependencies\": { \"com.unity.ide.rider\": \"3.0.28\" } }", packageDirs));
        }

        [Test]
        public void IsPackageIdPresent_VpmSiblingPackage_DoesNotFalsePositive()
        {
            // "nadena.dev.ndmf" must not be satisfied by the separate
            // "nadena.dev.ndmf-preview" style sibling that merely shares
            // its prefix -- only an exact name or an "<id>@..." suffix.
            var packageDirs = new List<string> { "nadena.dev.ndmf-experimental" };
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent(
                "nadena.dev.ndmf", string.Empty, packageDirs));
        }

        [Test]
        public void IsPackageIdPresent_NeitherManifestNorCache_ReturnsFalse()
        {
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent(
                "com.vrmc.vrm", ManifestWithVrchatAvatars, new List<string> { "com.unity.timeline@1.7.5" }));
        }

        [Test]
        public void IsPackageIdPresent_EmptyPackageId_ReturnsFalse()
        {
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent(
                string.Empty, ManifestWithVrchatAvatars, null));
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent(
                null, ManifestWithVrchatAvatars, null));
        }

        [Test]
        public void IsPackageIdPresent_NullManifestAndCache_ReturnsFalse_NeverThrows()
        {
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent("com.vrchat.avatars", null, null));
        }

        [Test]
        public void IsPackageIdPresent_MalformedManifestText_FallsBackToSubstringMatch()
        {
            // Not valid JSON, but still contains the quoted package id --
            // the resilient fallback (ManifestHasDependency's JsonParser.Parse
            // failure path) still finds it rather than treating a corrupt
            // manifest as "nothing installed".
            string malformed = "not-json but has \"com.vrchat.avatars\" in it somewhere";
            Assert.IsTrue(ExtensionProfileDetector.IsPackageIdPresent("com.vrchat.avatars", malformed, null));
        }

        [Test]
        public void IsPackageIdPresent_MalformedManifestText_SubstringFallbackStillMisses_ReturnsFalse()
        {
            string malformed = "not-json at all";
            Assert.IsFalse(ExtensionProfileDetector.IsPackageIdPresent("com.vrchat.avatars", malformed, null));
        }

        [Test]
        public void IsPackageIdPresent_ManifestWithoutDependenciesObject_FallsThroughToCacheCheck()
        {
            string manifestNoDeps = "{ \"scopedRegistries\": [] }";
            var cacheDirs = new List<string> { "com.vrchat.avatars@3.10.4" };
            Assert.IsTrue(ExtensionProfileDetector.IsPackageIdPresent("com.vrchat.avatars", manifestNoDeps, cacheDirs));
        }

        // -- IsDetected (full profile: package OR type) ----------------------------

        [Test]
        public void IsDetected_PackageHit_ReturnsTrue_RegardlessOfTypeResolver()
        {
            ExtensionProfile profile = MakeProfile(new[] { "com.vrchat.avatars" }, new[] { "SomeTypeThatDoesNotExist" });
            var resolver = new FakeTypeResolver(); // nothing exists
            Assert.IsTrue(ExtensionProfileDetector.IsDetected(profile, ManifestWithVrchatAvatars, null, resolver));
        }

        [Test]
        public void IsDetected_TypeHit_NoPackageId_ReturnsTrue()
        {
            // The FinalIK shape: no packageIds at all, detection relies
            // entirely on the typeNames fallback.
            ExtensionProfile profile = MakeProfile(new string[0], new[] { "FullBodyBipedIK" });
            var resolver = new FakeTypeResolver("FullBodyBipedIK", "CCDIK");
            Assert.IsTrue(ExtensionProfileDetector.IsDetected(profile, string.Empty, null, resolver));
        }

        [Test]
        public void IsDetected_NeitherPackageNorTypePresent_ReturnsFalse()
        {
            ExtensionProfile profile = MakeProfile(new[] { "com.vrmc.vrm" }, new[] { "VRMMeta" });
            var resolver = new FakeTypeResolver("UnrelatedType");
            Assert.IsFalse(ExtensionProfileDetector.IsDetected(profile, ManifestWithVrchatAvatars, null, resolver));
        }

        [Test]
        public void IsDetected_NullProfile_ReturnsFalse()
        {
            Assert.IsFalse(ExtensionProfileDetector.IsDetected(null, ManifestWithVrchatAvatars, null, new FakeTypeResolver()));
        }

        [Test]
        public void IsDetected_NullTypeResolver_StillEvaluatesPackageIds()
        {
            ExtensionProfile profile = MakeProfile(new[] { "com.vrchat.avatars" }, new[] { "VRCPhysBone" });
            Assert.IsTrue(ExtensionProfileDetector.IsDetected(profile, ManifestWithVrchatAvatars, null, null));
        }

        [Test]
        public void IsDetected_QualifiedTypeName_MatchesResolverExactly()
        {
            ExtensionProfile profile = MakeProfile(new string[0],
                new[] { "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone" });
            var resolver = new FakeTypeResolver("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            Assert.IsTrue(ExtensionProfileDetector.IsDetected(profile, string.Empty, null, resolver));
        }
    }
}
