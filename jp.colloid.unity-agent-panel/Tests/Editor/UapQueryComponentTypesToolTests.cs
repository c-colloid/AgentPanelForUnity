using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

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
        public void Execute_Component_IsReportedWithKindComponent()
        {
            var tool = new UapQueryComponentTypesTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("query", "BoxCollider"));
            JsonNode parsed;
            string error;
            Assert.IsTrue(JsonParser.TryParse(result[0]["text"].AsString(), out parsed, out error));
            Assert.AreEqual("component", FindKind(parsed, "BoxCollider"));
        }

        /// <summary>
        /// A StateMachineBehaviour derives from ScriptableObject, so it
        /// comes back in the ScriptableObject pass -- but it is attached by
        /// uap_animator_behaviour, never by uap_component_add or
        /// uap_asset_create. The kind field is how an agent tells them
        /// apart.
        /// </summary>
        [Test]
        public void Execute_StateMachineBehaviour_IsReportedWithItsOwnKind()
        {
            var tool = new UapQueryComponentTypesTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("query", "UapQueryTestBehaviour"));
            JsonNode parsed;
            string error;
            Assert.IsTrue(JsonParser.TryParse(result[0]["text"].AsString(), out parsed, out error));
            Assert.AreEqual("stateMachineBehaviour", FindKind(parsed, "UapQueryTestBehaviour"));
        }

        private static string FindKind(JsonNode parsed, string shortName)
        {
            foreach (JsonNode type in parsed["types"].Items)
            {
                if (type["shortName"].AsString() == shortName)
                {
                    return type["kind"].AsString();
                }
            }
            Assert.Fail(shortName + " was not in the result: " + JsonWriter.Write(parsed));
            return null;
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

    /// <summary>Fixture type for the kind-reporting test above; never attached to anything.</summary>
    public class UapQueryTestBehaviour : StateMachineBehaviour
    {
    }
}
