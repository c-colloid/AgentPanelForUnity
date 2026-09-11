using System.Collections.Generic;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Injection composition matrix (design section 3b/C3, C6 "injection
    /// composition (bundled auto, user pending excluded, approved
    /// included, toggle off = nothing)"). Operates on hand-built
    /// <see cref="ExtensionProfileStatus"/> lists -- no disk/TypeCache/
    /// PanelSettings dependency, so every branch of
    /// <see cref="ExtensionProfileInjector.ComposeProfilesSection"/> is
    /// pinned directly. The "toggle off" half of the matrix is covered
    /// separately below via <see cref="ExtensionProfileService.ComposeAppendSection"/>,
    /// since the toggle itself is a parameter on that method, not on the
    /// injector.
    /// </summary>
    public class ExtensionProfileInjectorTests
    {
        private static ExtensionProfile MakeProfile(string displayName, params string[] lines)
        {
            return new ExtensionProfile
            {
                Id = displayName,
                DisplayName = displayName,
                InstructionLines = new List<string>(lines)
            };
        }

        [Test]
        public void ComposeProfilesSection_NullList_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, ExtensionProfileInjector.ComposeProfilesSection(null));
        }

        [Test]
        public void ComposeProfilesSection_EmptyList_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                ExtensionProfileInjector.ComposeProfilesSection(new List<ExtensionProfileStatus>()));
        }

        [Test]
        public void ComposeProfilesSection_BundledDetected_IncludedAutomatically_NoApprovalNeeded()
        {
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("VRChat SDK3", "line1", "line2"),
                    Detected = true,
                    Trusted = true, // bundled is always trusted
                    IsBundled = true
                }
            };
            string result = ExtensionProfileInjector.ComposeProfilesSection(statuses);
            Assert.AreEqual("## VRChat SDK3\nline1\nline2", result);
        }

        [Test]
        public void ComposeProfilesSection_BundledNotDetected_Excluded()
        {
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("VRChat SDK3", "line1"),
                    Detected = false,
                    Trusted = true,
                    IsBundled = true
                }
            };
            Assert.AreEqual(string.Empty, ExtensionProfileInjector.ComposeProfilesSection(statuses));
        }

        [Test]
        public void ComposeProfilesSection_UserProfileDetectedButPending_Excluded()
        {
            // B3: never injected without approval, even though it IS
            // detected in this project.
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("RPGMaker Unite", "line1"),
                    Detected = true,
                    Trusted = false, // pending approval
                    IsBundled = false,
                    ContentHashHex = "some-hash"
                }
            };
            Assert.AreEqual(string.Empty, ExtensionProfileInjector.ComposeProfilesSection(statuses));
        }

        [Test]
        public void ComposeProfilesSection_UserProfileDetectedAndApproved_Included()
        {
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("RPGMaker Unite", "line1"),
                    Detected = true,
                    Trusted = true, // approved: hash is pinned in PanelSettings
                    IsBundled = false,
                    ContentHashHex = "some-hash"
                }
            };
            Assert.AreEqual("## RPGMaker Unite\nline1", ExtensionProfileInjector.ComposeProfilesSection(statuses));
        }

        [Test]
        public void ComposeProfilesSection_UserProfileApprovedButNoLongerDetected_Excluded()
        {
            // Trusted alone is not enough -- the SDK also has to actually
            // be present in THIS project right now.
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("RPGMaker Unite", "line1"),
                    Detected = false,
                    Trusted = true,
                    IsBundled = false,
                    ContentHashHex = "some-hash"
                }
            };
            Assert.AreEqual(string.Empty, ExtensionProfileInjector.ComposeProfilesSection(statuses));
        }

        [Test]
        public void ComposeProfilesSection_MultipleIncludedBlocks_JoinedWithBlankLine_InListOrder()
        {
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("VRChat SDK3", "vline1"),
                    Detected = true, Trusted = true, IsBundled = true
                },
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("UniVRM", "uline1", "uline2"),
                    Detected = true, Trusted = true, IsBundled = true
                }
            };
            string result = ExtensionProfileInjector.ComposeProfilesSection(statuses);
            Assert.AreEqual("## VRChat SDK3\nvline1" + "\n\n" + "## UniVRM\nuline1\nuline2", result);
        }

        [Test]
        public void ComposeProfilesSection_MixOfIncludedAndExcluded_OnlyIncludedContributes()
        {
            var statuses = new List<ExtensionProfileStatus>
            {
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("VRChat SDK3", "vline1"),
                    Detected = true, Trusted = true, IsBundled = true
                },
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("Pending SDK", "pline1"),
                    Detected = true, Trusted = false, IsBundled = false, ContentHashHex = "h"
                },
                new ExtensionProfileStatus
                {
                    Profile = MakeProfile("Not Installed SDK", "nline1"),
                    Detected = false, Trusted = true, IsBundled = true
                }
            };
            Assert.AreEqual("## VRChat SDK3\nvline1", ExtensionProfileInjector.ComposeProfilesSection(statuses));
        }

        [Test]
        public void ComposeProfilesSection_NullEntryInList_SkippedWithoutThrowing()
        {
            var statuses = new List<ExtensionProfileStatus> { null };
            Assert.AreEqual(string.Empty, ExtensionProfileInjector.ComposeProfilesSection(statuses));
        }

        // -- ExtensionProfileService.ComposeAppendSection's toggle short-circuit --

        [Test]
        public void ComposeAppendSection_ToggleOff_ReturnsEmpty_EvenWithABogusProjectRoot()
        {
            // The master toggle must short-circuit BEFORE touching disk/
            // TypeCache -- verified here by passing a project root that
            // cannot possibly resolve to a real project, and asserting no
            // exception AND an empty result.
            string result = ExtensionProfileService.ComposeAppendSection(
                "Z:/this/path/does/not/exist/at/all", profilesEnabled: false,
                approvedHashes: new List<string>());
            Assert.AreEqual(string.Empty, result);
        }
    }
}
