using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure formatting tests for the context blocks appended to outgoing
    /// user messages (delimiters, ordering, truncation, empty handling).
    /// </summary>
    [TestFixture]
    public class ContextBlockFormatterTests
    {
        [Test]
        public void Compose_NoBlocks_ReturnsUserTextUnchanged()
        {
            Assert.AreEqual("hello", ContextBlockFormatter.Compose("hello", null));
            Assert.AreEqual("hello",
                ContextBlockFormatter.Compose("hello", new List<string>()));
        }

        [Test]
        public void Compose_NullAndEmptyBlocksAreSkipped()
        {
            string composed = ContextBlockFormatter.Compose(
                "hello", new List<string> { null, string.Empty });
            Assert.AreEqual("hello", composed);
        }

        [Test]
        public void Compose_NullUserTextBecomesEmpty()
        {
            string composed = ContextBlockFormatter.Compose(
                null, new List<string> { "block" });
            StringAssert.StartsWith("\n\n" + ContextBlockFormatter.Header, composed);
        }

        [Test]
        public void Compose_AppendsDelimitedSectionAfterUserText()
        {
            string composed = ContextBlockFormatter.Compose(
                "explain this", new List<string> { "Selection: Player" });

            StringAssert.StartsWith("explain this\n\n", composed);
            StringAssert.Contains(ContextBlockFormatter.Header, composed);
            StringAssert.Contains("[1] Selection: Player", composed);
            StringAssert.EndsWith(ContextBlockFormatter.Footer, composed);
            // The user text stays strictly before the header.
            Assert.Less(composed.IndexOf("explain this", System.StringComparison.Ordinal),
                composed.IndexOf(ContextBlockFormatter.Header, System.StringComparison.Ordinal));
        }

        [Test]
        public void Compose_NumbersBlocksInOrder()
        {
            string composed = ContextBlockFormatter.Compose(
                "q", new List<string> { "first", "second", "third" });

            int i1 = composed.IndexOf("[1] first", System.StringComparison.Ordinal);
            int i2 = composed.IndexOf("[2] second", System.StringComparison.Ordinal);
            int i3 = composed.IndexOf("[3] third", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(i1, 0);
            Assert.Greater(i2, i1);
            Assert.Greater(i3, i2);
        }

        [Test]
        public void Compose_TruncatesOversizedBlocks()
        {
            string huge = new string('x', ContextBlockFormatter.MaxBlockChars + 500);
            string composed = ContextBlockFormatter.Compose(
                "q", new List<string> { huge });

            StringAssert.Contains(ContextBlockFormatter.TruncationSuffix, composed);
            // The full oversized payload must not survive.
            StringAssert.DoesNotContain(huge, composed);
        }

        [Test]
        public void TruncateBlock_UnderCapPassesThrough()
        {
            Assert.AreEqual("short", ContextBlockFormatter.TruncateBlock("short", 100));
        }

        [Test]
        public void TruncateBlock_AtCapPassesThrough()
        {
            string exact = new string('a', 10);
            Assert.AreEqual(exact, ContextBlockFormatter.TruncateBlock(exact, 10));
        }

        [Test]
        public void TruncateBlock_OverCapCutsAndMarks()
        {
            string truncated = ContextBlockFormatter.TruncateBlock(
                new string('a', 20), 10);
            Assert.AreEqual(new string('a', 10)
                + ContextBlockFormatter.TruncationSuffix, truncated);
        }

        [Test]
        public void TruncateBlock_NullYieldsEmpty()
        {
            Assert.AreEqual(string.Empty, ContextBlockFormatter.TruncateBlock(null, 10));
        }
    }
}
