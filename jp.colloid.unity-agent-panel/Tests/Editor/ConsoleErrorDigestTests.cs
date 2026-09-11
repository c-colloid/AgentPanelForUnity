using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Tests for the pure digest formatting of captured Console errors
    /// (the payload behind the "Ask Claude to fix" chip). The capture
    /// hooks themselves are editor-event driven and compile-verified only.
    /// Pins English via L10n.OverrideForTests since FormatDigest now reads
    /// L10n.S (docs/design-notes/2026-08-01-i18n.md) and this machine's OS
    /// language is Japanese.
    /// </summary>
    [TestFixture]
    public class ConsoleErrorDigestTests
    {
        [SetUp]
        public void SetUp()
        {
            L10n.OverrideForTests(PanelLanguage.English);
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
        }

        private static ConsoleErrorProvider.Entry Entry(string message,
            string location = null, int occurrences = 1, bool fromCompiler = false)
        {
            return new ConsoleErrorProvider.Entry
            {
                Message = message,
                Location = location,
                Occurrences = occurrences,
                FromCompiler = fromCompiler
            };
        }

        [Test]
        public void FormatDigest_EmptyOrNull_ReturnsNull()
        {
            Assert.IsNull(ConsoleErrorProvider.FormatDigest(
                new List<ConsoleErrorProvider.Entry>(), 10));
            Assert.IsNull(ConsoleErrorProvider.FormatDigest(null, 10));
        }

        [Test]
        public void FormatDigest_ZeroMax_ReturnsNull()
        {
            var entries = new List<ConsoleErrorProvider.Entry> { Entry("boom") };
            Assert.IsNull(ConsoleErrorProvider.FormatDigest(entries, 0));
        }

        [Test]
        public void FormatDigest_SingleEntry_HasHeaderNumberAndLocation()
        {
            var entries = new List<ConsoleErrorProvider.Entry>
            {
                Entry("NullReferenceException: boom", "Assets/Scripts/Player.cs:42")
            };
            string digest = ConsoleErrorProvider.FormatDigest(entries, 10);

            StringAssert.StartsWith("Unity Console errors (1 distinct):", digest);
            StringAssert.Contains("[1] NullReferenceException: boom", digest);
            StringAssert.Contains("at Assets/Scripts/Player.cs:42", digest);
        }

        [Test]
        public void FormatDigest_OmitsMissingLocation()
        {
            var entries = new List<ConsoleErrorProvider.Entry> { Entry("boom") };
            string digest = ConsoleErrorProvider.FormatDigest(entries, 10);
            StringAssert.DoesNotContain("at ", digest);
        }

        [Test]
        public void FormatDigest_AnnotatesRepeatedOccurrences()
        {
            var entries = new List<ConsoleErrorProvider.Entry>
            {
                Entry("spammy exception", "Assets/A.cs:1", 37)
            };
            string digest = ConsoleErrorProvider.FormatDigest(entries, 10);
            StringAssert.Contains("(x37)", digest);
        }

        [Test]
        public void FormatDigest_SingleOccurrenceHasNoAnnotation()
        {
            var entries = new List<ConsoleErrorProvider.Entry> { Entry("once") };
            string digest = ConsoleErrorProvider.FormatDigest(entries, 10);
            StringAssert.DoesNotContain("(x", digest);
        }

        [Test]
        public void FormatDigest_CapsEntriesAndReportsOverflow()
        {
            var entries = new List<ConsoleErrorProvider.Entry>();
            for (int i = 0; i < 13; i++)
            {
                entries.Add(Entry("error " + i));
            }
            string digest = ConsoleErrorProvider.FormatDigest(entries, 10);

            StringAssert.StartsWith("Unity Console errors (13 distinct):", digest);
            StringAssert.Contains("[10] error 9", digest);
            StringAssert.DoesNotContain("[11]", digest);
            StringAssert.Contains("... and 3 more error(s).", digest);
        }

        [Test]
        public void FormatDigest_KeepsEntryOrder()
        {
            var entries = new List<ConsoleErrorProvider.Entry>
            {
                Entry("first"), Entry("second")
            };
            string digest = ConsoleErrorProvider.FormatDigest(entries, 10);
            Assert.Less(digest.IndexOf("[1] first", System.StringComparison.Ordinal),
                digest.IndexOf("[2] second", System.StringComparison.Ordinal));
        }
    }
}
