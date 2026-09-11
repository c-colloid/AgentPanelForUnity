using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure ignore-decision tests (docs/design-notes/2026-08-13-error-chip-
    /// ignore.md): exact-match store, substring patterns, pattern parsing,
    /// and the FIFO bound on the persistent store. No editor state at all
    /// -- this is the layer that must stay testable without the log
    /// pipeline or a live PanelStateStore.
    /// </summary>
    [TestFixture]
    public class ConsoleErrorIgnoreFilterTests
    {
        [Test]
        public void IsIgnored_ExactMatchInStore_ReturnsTrue()
        {
            var store = new List<string> { "NullReferenceException: boom" };
            Assert.IsTrue(ConsoleErrorIgnoreFilter.IsIgnored(
                "NullReferenceException: boom", store, null));
        }

        [Test]
        public void IsIgnored_DifferentMessage_ReturnsFalse()
        {
            var store = new List<string> { "NullReferenceException: boom" };
            Assert.IsFalse(ConsoleErrorIgnoreFilter.IsIgnored(
                "NullReferenceException: boom 2", store, null));
        }

        [Test]
        public void IsIgnored_SubstringPattern_ReturnsTrue()
        {
            var patterns = new List<string> { "SomeSdk.Internal" };
            Assert.IsTrue(ConsoleErrorIgnoreFilter.IsIgnored(
                "InvalidOperationException: SomeSdk.Internal.Widget failed to bind", null, patterns));
        }

        [Test]
        public void IsIgnored_PatternNotContained_ReturnsFalse()
        {
            var patterns = new List<string> { "SomeSdk.Internal" };
            Assert.IsFalse(ConsoleErrorIgnoreFilter.IsIgnored(
                "InvalidOperationException: unrelated", null, patterns));
        }

        [Test]
        public void IsIgnored_EmptyPatternEntry_NeverMatches()
        {
            // ParsePatterns drops blanks, but IsIgnored must not depend on
            // that: an empty substring would otherwise match EVERYTHING.
            var patterns = new List<string> { "" };
            Assert.IsFalse(ConsoleErrorIgnoreFilter.IsIgnored("anything", null, patterns));
        }

        [Test]
        public void IsIgnored_NullMessage_ReturnsFalse()
        {
            var store = new List<string> { "x" };
            Assert.IsFalse(ConsoleErrorIgnoreFilter.IsIgnored(null, store, store));
        }

        [Test]
        public void IsIgnored_NullLists_ReturnsFalse()
        {
            Assert.IsFalse(ConsoleErrorIgnoreFilter.IsIgnored("boom", null, null));
        }

        [Test]
        public void ParsePatterns_TrimsLinesAndDropsBlanks()
        {
            List<string> parsed = ConsoleErrorIgnoreFilter.ParsePatterns(
                "  first  \r\n\r\n\tsecond\t\n   \nthird");
            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, parsed);
        }

        [Test]
        public void ParsePatterns_NullOrEmpty_ReturnsEmptyListNeverNull()
        {
            Assert.IsNotNull(ConsoleErrorIgnoreFilter.ParsePatterns(null));
            Assert.AreEqual(0, ConsoleErrorIgnoreFilter.ParsePatterns(null).Count);
            Assert.AreEqual(0, ConsoleErrorIgnoreFilter.ParsePatterns(string.Empty).Count);
        }

        [Test]
        public void AddIgnores_SkipsDuplicatesAndEmptyMessages()
        {
            var store = new List<string> { "a" };
            ConsoleErrorIgnoreFilter.AddIgnores(store,
                new[] { "a", "b", null, "", "b" }, 10);
            CollectionAssert.AreEqual(new[] { "a", "b" }, store);
        }

        [Test]
        public void AddIgnores_BeyondMax_EvictsOldestFirst()
        {
            var store = new List<string> { "old1", "old2" };
            ConsoleErrorIgnoreFilter.AddIgnores(store, new[] { "new1", "new2" }, 3);
            // Capacity 3: old1 (the OLDEST) is evicted, insertion order kept.
            CollectionAssert.AreEqual(new[] { "old2", "new1", "new2" }, store);
        }

        [Test]
        public void AddIgnores_NonPositiveMax_EmptiesStoreWithoutLooping()
        {
            var store = new List<string> { "a" };
            ConsoleErrorIgnoreFilter.AddIgnores(store, new[] { "b" }, -1);
            Assert.AreEqual(0, store.Count);
        }

        [Test]
        public void AddIgnores_NullStoreOrMessages_DoesNotThrow()
        {
            ConsoleErrorIgnoreFilter.AddIgnores(null, new[] { "a" }, 5);
            ConsoleErrorIgnoreFilter.AddIgnores(new List<string>(), null, 5);
        }
    }
}
