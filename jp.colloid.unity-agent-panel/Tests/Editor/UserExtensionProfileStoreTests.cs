using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// <see cref="UserExtensionProfileStore"/>: loading user-supplied
    /// profiles from a `.uap-profiles`-shaped directory, and the
    /// content-hash computation design section 8.2 B3's trust pinning is
    /// built on.
    /// </summary>
    public class UserExtensionProfileStoreTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "UserExtensionProfileStoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private void Write(string fileName, string content)
        {
            File.WriteAllText(Path.Combine(_dir, fileName), content, new UTF8Encoding(false));
        }

        [Test]
        public void ResolveDirectory_JoinsProjectRootWithDefaultRelativeDirectory()
        {
            string resolved = UserExtensionProfileStore.ResolveDirectory("C:/MyProject");
            StringAssert.Contains(".uap-profiles", resolved);
        }

        [Test]
        public void LoadFromDirectory_MissingDirectory_ReturnsEmptyList()
        {
            string bogus = Path.Combine(_dir, "does-not-exist");
            Assert.IsEmpty(UserExtensionProfileStore.LoadFromDirectory(bogus));
        }

        [Test]
        public void LoadFromDirectory_ValidFile_ReturnsEntryWithProfileAndHash()
        {
            string json = "{ \"id\": \"my-sdk\", \"displayName\": \"My SDK\","
                + " \"detection\": { \"packageIds\": [\"com.example.mysdk\"] },"
                + " \"instructionLines\": [\"line1\"] }";
            Write("my-sdk.json", json);
            List<UserExtensionProfileEntry> entries = UserExtensionProfileStore.LoadFromDirectory(_dir);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("my-sdk", entries[0].Profile.Id);
            Assert.IsNotEmpty(entries[0].ContentHashHex);
            StringAssert.EndsWith("my-sdk.json", entries[0].FilePath);
        }

        [Test]
        public void LoadFromDirectory_UnparsableFile_SkippedWithoutThrowing()
        {
            Write("broken.json", "{ this is not json");
            List<UserExtensionProfileEntry> entries = UserExtensionProfileStore.LoadFromDirectory(_dir);
            Assert.IsEmpty(entries);
        }

        [Test]
        public void ComputeContentHash_SameBytes_SameHash()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("{ \"id\": \"x\" }");
            string h1 = UserExtensionProfileStore.ComputeContentHash(bytes);
            string h2 = UserExtensionProfileStore.ComputeContentHash((byte[])bytes.Clone());
            Assert.AreEqual(h1, h2);
        }

        [Test]
        public void ComputeContentHash_IsLowercaseHexSha256Length()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("anything");
            string hash = UserExtensionProfileStore.ComputeContentHash(bytes);
            Assert.AreEqual(64, hash.Length); // SHA-256 = 32 bytes = 64 hex chars
            Assert.AreEqual(hash, hash.ToLowerInvariant());
        }

        [Test]
        public void ComputeContentHash_AnyByteChange_TamperInvalidation_DifferentHash()
        {
            // Design section 8.2 B3: "any change invalidates" -- the
            // approval pin is the raw-byte hash, so even a single-byte
            // difference (here: one trailing character) must yield a
            // different hash.
            byte[] original = Encoding.UTF8.GetBytes("{ \"id\": \"x\", \"instructionLines\": [\"a\"] }");
            byte[] tampered = Encoding.UTF8.GetBytes("{ \"id\": \"x\", \"instructionLines\": [\"a!\"] }");
            string originalHash = UserExtensionProfileStore.ComputeContentHash(original);
            string tamperedHash = UserExtensionProfileStore.ComputeContentHash(tampered);
            Assert.AreNotEqual(originalHash, tamperedHash);
        }

        [Test]
        public void LoadFromDirectory_TwoFiles_DeterministicOrdinalOrder()
        {
            Write("b.json", "{ \"id\": \"b\" }");
            Write("a.json", "{ \"id\": \"a\" }");
            List<UserExtensionProfileEntry> entries = UserExtensionProfileStore.LoadFromDirectory(_dir);
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual("a", entries[0].Profile.Id);
            Assert.AreEqual("b", entries[1].Profile.Id);
        }
    }
}
