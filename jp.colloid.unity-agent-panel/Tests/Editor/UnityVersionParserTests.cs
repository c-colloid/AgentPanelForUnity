using Colloid.AgentPanel.Ops.UnityPlugin;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    [TestFixture]
    public class UnityVersionParserTests
    {
        [Test]
        public void Major_LtsAndUnity6Forms()
        {
            Assert.AreEqual(2022, UnityVersionParser.Major("2022.3.22f1"));
            Assert.AreEqual(6000, UnityVersionParser.Major("6000.0.1f1"));
            Assert.AreEqual(6000, UnityVersionParser.Major("6000.5.8f1"));
            Assert.AreEqual(2021, UnityVersionParser.Major("2021.3"));
            Assert.AreEqual(2023, UnityVersionParser.Major("2023"));
        }

        [Test]
        public void Major_Unparseable_MinusOne()
        {
            Assert.AreEqual(-1, UnityVersionParser.Major(null));
            Assert.AreEqual(-1, UnityVersionParser.Major(string.Empty));
            Assert.AreEqual(-1, UnityVersionParser.Major("beta"));
            Assert.AreEqual(-1, UnityVersionParser.Major(".3.22f1"));
            Assert.AreEqual(-1, UnityVersionParser.Major("1234567890.0"), "absurd digit runs are not a version");
        }

        [Test]
        public void IsUnity6OrNewer_ErrsTowardTheCaveat()
        {
            Assert.IsTrue(UnityVersionParser.IsUnity6OrNewer("6000.0.1f1"));
            Assert.IsTrue(UnityVersionParser.IsUnity6OrNewer("6001.0.0a1"));
            Assert.IsFalse(UnityVersionParser.IsUnity6OrNewer("2022.3.22f1"));
            Assert.IsFalse(UnityVersionParser.IsUnity6OrNewer("garbage"));
            Assert.IsFalse(UnityVersionParser.IsUnity6OrNewer(null));
        }
    }
}
