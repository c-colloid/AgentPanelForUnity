using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Pure reentrant-counter tests (R11 section 1: "counter-based, reentrant-safe, always balanced").</summary>
    [TestFixture]
    public class RefreshSuppressionCounterTests
    {
        [Test]
        public void FirstIncrement_ReturnsTrue_AndIsSuppressed()
        {
            var counter = new RefreshSuppressionCounter();
            Assert.IsTrue(counter.Increment());
            Assert.AreEqual(1, counter.Count);
            Assert.IsTrue(counter.IsSuppressed);
        }

        [Test]
        public void NestedIncrements_OnlyFirstReturnsTrue()
        {
            var counter = new RefreshSuppressionCounter();
            Assert.IsTrue(counter.Increment());
            Assert.IsFalse(counter.Increment());
            Assert.IsFalse(counter.Increment());
            Assert.AreEqual(3, counter.Count);
        }

        [Test]
        public void Decrement_OnlyReturnsTrue_OnFinalRelease()
        {
            var counter = new RefreshSuppressionCounter();
            counter.Increment();
            counter.Increment();
            counter.Increment();
            Assert.IsFalse(counter.Decrement());
            Assert.IsFalse(counter.Decrement());
            Assert.IsTrue(counter.Decrement());
            Assert.AreEqual(0, counter.Count);
            Assert.IsFalse(counter.IsSuppressed);
        }

        [Test]
        public void Decrement_BelowZero_ClampsAndReturnsFalse()
        {
            var counter = new RefreshSuppressionCounter();
            Assert.IsFalse(counter.Decrement());
            Assert.AreEqual(0, counter.Count);
            Assert.IsFalse(counter.Decrement());
            Assert.AreEqual(0, counter.Count);
        }

        [Test]
        public void ForceReset_ZeroesCount_WithoutReturningATransition()
        {
            var counter = new RefreshSuppressionCounter();
            counter.Increment();
            counter.Increment();
            counter.ForceReset();
            Assert.AreEqual(0, counter.Count);
            Assert.IsFalse(counter.IsSuppressed);
        }

        [Test]
        public void IncrementThenDecrementThenIncrementAgain_RestartsCleanly()
        {
            var counter = new RefreshSuppressionCounter();
            Assert.IsTrue(counter.Increment());
            Assert.IsTrue(counter.Decrement());
            Assert.IsTrue(counter.Increment());
            Assert.AreEqual(1, counter.Count);
        }
    }
}
