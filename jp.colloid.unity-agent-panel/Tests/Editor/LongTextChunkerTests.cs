using System.Collections.Generic;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Boundary rules of the splitter every unbounded text sink goes
    /// through (design note 2026-09-13-toolcard-vertex-limit.md): no
    /// chunk may exceed the cap (UITK's 65535-vertex ceiling per element),
    /// nothing may be lost, newlines win over spaces over hard cuts, and a
    /// surrogate pair is never split.
    /// </summary>
    [TestFixture]
    public class LongTextChunkerTests
    {
        private static string Repeat(string unit, int count)
        {
            var sb = new System.Text.StringBuilder(unit.Length * count);
            for (int i = 0; i < count; i++)
            {
                sb.Append(unit);
            }
            return sb.ToString();
        }

        [Test]
        public void Split_NullOrEmpty_YieldsOneEmptyChunk()
        {
            CollectionAssert.AreEqual(new[] { string.Empty }, LongTextChunker.Split(null));
            CollectionAssert.AreEqual(new[] { string.Empty }, LongTextChunker.Split(string.Empty));
        }

        [Test]
        public void Split_AtOrUnderCap_IsUnchanged()
        {
            string exact = Repeat("x", 100);
            CollectionAssert.AreEqual(new[] { exact }, LongTextChunker.Split(exact, 100));
            CollectionAssert.AreEqual(new[] { "short" }, LongTextChunker.Split("short", 100));
        }

        [Test]
        public void Split_DefaultCap_HoldsWellUnderTheVertexCeiling()
        {
            // 65535 vertices / 4 per glyph = 16383 glyphs; the default
            // keeps a 2x margin for fallback fonts and decoration quads.
            Assert.LessOrEqual(LongTextChunker.DefaultMaxChars, 16383 / 2);
        }

        [Test]
        public void Split_PrefersNewline_AndKeepsItAtTheEndOfTheEarlierChunk()
        {
            // 60 chars of text, a newline at index 60, then 60 more.
            string text = Repeat("a", 60) + "\n" + Repeat("b", 60);
            List<string> chunks = LongTextChunker.Split(text, 100);
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual(Repeat("a", 60) + "\n", chunks[0]);
            Assert.AreEqual(Repeat("b", 60), chunks[1]);
        }

        [Test]
        public void Split_FallsBackToSpace_WhenNoNewlineInWindow()
        {
            string text = Repeat("a", 70) + " " + Repeat("b", 70);
            List<string> chunks = LongTextChunker.Split(text, 100);
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual(Repeat("a", 70) + " ", chunks[0]);
            Assert.AreEqual(Repeat("b", 70), chunks[1]);
        }

        [Test]
        public void Split_HardCuts_WhenNoBreakCharacter()
        {
            string text = Repeat("x", 250);
            List<string> chunks = LongTextChunker.Split(text, 100);
            CollectionAssert.AreEqual(new[] { 100, 100, 50 },
                new[] { chunks[0].Length, chunks[1].Length, chunks[2].Length });
            Assert.AreEqual(text, string.Concat(chunks));
        }

        [Test]
        public void Split_NeverSplitsASurrogatePair()
        {
            // 99 ASCII chars then an astral emoji (2 UTF-16 units) landing
            // exactly across the 100-unit cap.
            string text = Repeat("x", 99) + "\U0001F600" + Repeat("y", 10);
            List<string> chunks = LongTextChunker.Split(text, 100);
            Assert.AreEqual(Repeat("x", 99), chunks[0]);
            Assert.IsTrue(chunks[1].StartsWith("\U0001F600"), "pair moved whole into the next chunk");
            Assert.AreEqual(text, string.Concat(chunks));
        }

        [Test]
        public void Split_LosesNothing_OnRealisticFileText()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 3000; i++)
            {
                sb.Append("    var line").Append(i).Append(" = \"value\"; // comment\n");
            }
            string text = sb.ToString();
            List<string> chunks = LongTextChunker.Split(text);
            Assert.Greater(chunks.Count, 1);
            foreach (string chunk in chunks)
            {
                Assert.LessOrEqual(chunk.Length, LongTextChunker.DefaultMaxChars);
                Assert.IsTrue(chunk.EndsWith("\n"), "every chunk of newline-rich text breaks at a newline");
            }
            Assert.AreEqual(text, string.Concat(chunks));
        }

        [Test]
        public void NeedsSplit_TracksTheCap()
        {
            Assert.IsFalse(LongTextChunker.NeedsSplit(null));
            Assert.IsFalse(LongTextChunker.NeedsSplit(Repeat("x", LongTextChunker.DefaultMaxChars)));
            Assert.IsTrue(LongTextChunker.NeedsSplit(Repeat("x", LongTextChunker.DefaultMaxChars + 1)));
        }
    }
}
