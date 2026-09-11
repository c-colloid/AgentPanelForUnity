using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Read-only inspection tool tests (uap_component_list / uap_object_inspect).</summary>
    [TestFixture]
    public class UapComponentListAndInspectToolTests
    {
        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("UapInspectTestObject");
            _go.AddComponent<BoxCollider>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        [Test]
        public void ComponentList_ListsTransformAndBoxCollider()
        {
            var tool = new UapComponentListTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("path", "UapInspectTestObject"));
            string text = result[0]["text"].AsString();
            StringAssert.Contains("UnityEngine.Transform", text);
            StringAssert.Contains("UnityEngine.BoxCollider", text);
        }

        /// <summary>
        /// OPS-1 (SR-component-typeindex): with duplicate components, each
        /// list entry carries BOTH the all-components 'index' and the
        /// per-type 'typeIndex' -- consumers' componentIndex means the
        /// latter (GetComponents(type)[componentIndex]), and emitting only
        /// the former invited destroying the wrong component.
        /// </summary>
        [Test]
        public void ComponentList_DuplicateComponents_CarryAPerTypeTypeIndex()
        {
            _go.AddComponent<BoxCollider>();
            _go.AddComponent<Rigidbody>();

            var tool = new UapComponentListTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("path", "UapInspectTestObject"));
            JsonNode components = JsonParser.Parse(result[0]["text"].AsString())["components"];

            // GetComponents order: Transform, BoxCollider (SetUp),
            // BoxCollider (above), Rigidbody.
            Assert.AreEqual(4, components.Count);
            Assert.AreEqual("UnityEngine.BoxCollider", components[2]["type"].AsString());
            Assert.AreEqual(2, components[2]["index"].AsInt(-1));
            Assert.AreEqual(1, components[2]["typeIndex"].AsInt(-1),
                "the second BoxCollider is typeIndex 1 even though it sits at all-components index 2");
            Assert.AreEqual("UnityEngine.Rigidbody", components[3]["type"].AsString());
            Assert.AreEqual(0, components[3]["typeIndex"].AsInt(-1),
                "the first Rigidbody is typeIndex 0 even though it sits at all-components index 3");
        }

        /// <summary>
        /// OPS-1 cross-test: the typeIndex the LIST reports is exactly what
        /// uap_component_remove's componentIndex accepts -- feeding the
        /// second BoxCollider's typeIndex removes THAT BoxCollider, not the
        /// component at the same position in the all-components list.
        /// </summary>
        [Test]
        public void ComponentRemove_AcceptsTheListedTypeIndex_RemovingThatDuplicate()
        {
            BoxCollider second = _go.AddComponent<BoxCollider>();
            second.size = new Vector3(9f, 9f, 9f);
            _go.AddComponent<Rigidbody>();

            JsonNode listResult = new UapComponentListTool().Execute(
                JsonNode.NewObject().Set("path", "UapInspectTestObject"));
            JsonNode components = JsonParser.Parse(listResult[0]["text"].AsString())["components"];
            int reportedTypeIndex = components[2]["typeIndex"].AsInt(-1);

            new UapComponentRemoveTool().Execute(JsonNode.NewObject()
                .Set("path", "UapInspectTestObject")
                .Set("componentType", "BoxCollider")
                .Set("componentIndex", reportedTypeIndex));

            BoxCollider[] remaining = _go.GetComponents<BoxCollider>();
            Assert.AreEqual(1, remaining.Length);
            Assert.AreEqual(Vector3.one, remaining[0].size,
                "the FIRST BoxCollider (default size) must survive -- the resized second one was removed");
            Assert.IsNotNull(_go.GetComponent<Rigidbody>(),
                "the Rigidbody (all-components index 3) must never be touched by componentIndex 1");
        }

        [Test]
        public void ComponentList_MissingPath_Throws()
        {
            var tool = new UapComponentListTool();
            Assert.Throws<System.ArgumentException>(delegate { tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void ComponentList_UnknownPath_Throws()
        {
            var tool = new UapComponentListTool();
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", "NoSuchObjectAtAll12345"));
            });
        }

        [Test]
        public void ObjectInspect_ListsIsTriggerProperty()
        {
            var tool = new UapObjectInspectTool();
            JsonNode result = tool.Execute(JsonNode.NewObject()
                .Set("path", "UapInspectTestObject")
                .Set("componentType", "BoxCollider"));
            string text = result[0]["text"].AsString();
            StringAssert.Contains("m_IsTrigger", text);
        }

        [Test]
        public void ObjectInspect_UnknownComponentType_Throws()
        {
            var tool = new UapObjectInspectTool();
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject()
                    .Set("path", "UapInspectTestObject")
                    .Set("componentType", "NotARealComponentType12345"));
            });
        }

        [Test]
        public void ObjectInspect_ComponentIndexOutOfRange_Throws()
        {
            var tool = new UapObjectInspectTool();
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject()
                    .Set("path", "UapInspectTestObject")
                    .Set("componentType", "BoxCollider")
                    .Set("componentIndex", 5));
            });
        }
    }
}
