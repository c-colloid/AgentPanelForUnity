using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>uap_query_component_types read-tool tests (TypeCache-backed type discovery, design section 3b).</summary>
    [TestFixture]
    public class UapQueryComponentTypesToolTests
    {
        [Test]
        public void Execute_QueryMatchingBoxCollider_ReturnsIt()
        {
            var tool = new UapQueryComponentTypesTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("query", "BoxCollider"));
            string text = result[0]["text"].AsString();
            StringAssert.Contains("UnityEngine.BoxCollider", text);
        }

        [Test]
        public void Execute_EmptyQuery_ReturnsUpToMaxResults()
        {
            var tool = new UapQueryComponentTypesTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("maxResults", 5));
            JsonNode parsed;
            string error;
            Assert.IsTrue(JsonParser.TryParse(result[0]["text"].AsString(), out parsed, out error));
            Assert.LessOrEqual(parsed["types"].Count, 5);
            Assert.Greater(parsed["types"].Count, 0);
        }

        [Test]
        public void Execute_UnknownQuery_ReturnsEmptyList()
        {
            var tool = new UapQueryComponentTypesTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("query", "ThisMatchesNothingAtAll12345"));
            JsonNode parsed;
            string error;
            Assert.IsTrue(JsonParser.TryParse(result[0]["text"].AsString(), out parsed, out error));
            Assert.AreEqual(0, parsed["types"].Count);
        }
    }
}
