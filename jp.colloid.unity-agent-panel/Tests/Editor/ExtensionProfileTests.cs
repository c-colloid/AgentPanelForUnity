using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure parsing tests for <see cref="ExtensionProfile.Parse(string, out string)"/>
    /// (design section 3b/C1) -- no disk/Unity dependency, mirroring
    /// JsonParserTests's style for this codebase's own JSON DOM.
    /// </summary>
    public class ExtensionProfileTests
    {
        private const string ValidJson =
            "{"
            + "\"id\": \"vrchat-sdk3\","
            + "\"displayName\": \"VRChat SDK3 (Avatars)\","
            + "\"detection\": { \"packageIds\": [\"com.vrchat.avatars\"], \"typeNames\": [\"VRCPhysBone\"] },"
            + "\"instructionLines\": [\"line one\", \"line two\"]"
            + "}";

        [Test]
        public void Parse_ValidJson_PopulatesEveryField()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse(ValidJson, out error);
            Assert.IsNull(error);
            Assert.IsNotNull(profile);
            Assert.AreEqual("vrchat-sdk3", profile.Id);
            Assert.AreEqual("VRChat SDK3 (Avatars)", profile.DisplayName);
            CollectionAssert.AreEqual(new[] { "com.vrchat.avatars" }, profile.PackageIds);
            CollectionAssert.AreEqual(new[] { "VRCPhysBone" }, profile.TypeNames);
            CollectionAssert.AreEqual(new[] { "line one", "line two" }, profile.InstructionLines);
        }

        [Test]
        public void Parse_MissingDisplayName_FallsBackToId()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse(
                "{ \"id\": \"foo\", \"detection\": {}, \"instructionLines\": [] }", out error);
            Assert.IsNull(error);
            Assert.AreEqual("foo", profile.DisplayName);
        }

        [Test]
        public void Parse_MissingDetectionObject_YieldsEmptyLists()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse(
                "{ \"id\": \"foo\", \"instructionLines\": [] }", out error);
            Assert.IsNull(error);
            Assert.IsEmpty(profile.PackageIds);
            Assert.IsEmpty(profile.TypeNames);
        }

        [Test]
        public void Parse_MissingId_ReturnsNullWithError()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse(
                "{ \"displayName\": \"No Id\" }", out error);
            Assert.IsNull(profile);
            Assert.IsNotNull(error);
        }

        [Test]
        public void Parse_EmptyString_ReturnsNullWithError()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse(string.Empty, out error);
            Assert.IsNull(profile);
            Assert.IsNotNull(error);
        }

        [Test]
        public void Parse_NullString_ReturnsNullWithError()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse((string)null, out error);
            Assert.IsNull(profile);
            Assert.IsNotNull(error);
        }

        [Test]
        public void Parse_MalformedJson_ReturnsNullWithError_NeverThrows()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse("{ not json", out error);
            Assert.IsNull(profile);
            Assert.IsNotNull(error);
        }

        [Test]
        public void Parse_RootIsArray_ReturnsNullWithError()
        {
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse("[1, 2, 3]", out error);
            Assert.IsNull(profile);
            Assert.IsNotNull(error);
        }

        [Test]
        public void Parse_JsonNodeOverload_SkipsNullAndEmptyArrayEntries()
        {
            JsonNode root = JsonNode.NewObject()
                .Set("id", "foo")
                .Set("detection", JsonNode.NewObject()
                    .Set("packageIds", JsonNode.NewArray().Add("real.id").Add(JsonNode.Null).Add(string.Empty))
                    .Set("typeNames", JsonNode.NewArray()))
                .Set("instructionLines", JsonNode.NewArray().Add("only line"));
            string error;
            ExtensionProfile profile = ExtensionProfile.Parse(root, out error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "real.id" }, profile.PackageIds);
        }
    }
}
