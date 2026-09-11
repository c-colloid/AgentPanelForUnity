using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real component removal via uap_component_remove, including the Transform refusal and the Undo revert path.</summary>
    [TestFixture]
    public class UapComponentRemoveToolTests
    {
        private GameObject _go;
        private UapComponentRemoveTool _tool;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("UapComponentRemoveTestObject");
            _tool = new UapComponentRemoveTool();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        private static JsonNode Input(string path, string componentType, int? componentIndex = null)
        {
            JsonNode node = JsonNode.NewObject().Set("path", path).Set("componentType", componentType);
            if (componentIndex.HasValue)
            {
                node.Set("componentIndex", componentIndex.Value);
            }
            return node;
        }

        [Test]
        public void Execute_RemovesTheComponent()
        {
            _go.AddComponent<BoxCollider>();

            _tool.Execute(Input("UapComponentRemoveTestObject", "BoxCollider"));

            Assert.IsNull(_go.GetComponent<BoxCollider>());
        }

        [Test]
        public void Execute_MissingRequiredFields_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("NoSuchObjectAtAll12345", "BoxCollider"));
            });
        }

        [Test]
        public void Execute_UnknownComponentType_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapComponentRemoveTestObject", "NotARealComponentType12345"));
            });
        }

        [Test]
        public void Execute_RemovingTransform_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapComponentRemoveTestObject", "Transform"));
            });
            Assert.IsNotNull(_go.transform, "Transform must survive the refused removal.");
        }

        [Test]
        public void Execute_IndexOutOfRange_Throws()
        {
            _go.AddComponent<BoxCollider>();
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapComponentRemoveTestObject", "BoxCollider", 5));
            });
        }

        [Test]
        public void Execute_ThenUndo_RestoresTheComponent()
        {
            _go.AddComponent<BoxCollider>();
            Undo.IncrementCurrentGroup();

            _tool.Execute(Input("UapComponentRemoveTestObject", "BoxCollider"));
            Assert.IsNull(_go.GetComponent<BoxCollider>());

            Undo.PerformUndo();

            Assert.IsNotNull(_go.GetComponent<BoxCollider>(), "Undo should restore the removed component.");
        }

        /// <summary>
        /// Regression: removing a component still required by a sibling
        /// component's [RequireComponent] must fail loudly instead of
        /// reporting a false "Removed ..." success while Unity silently
        /// refuses the underlying destroy.
        /// </summary>
        [Test]
        public void Execute_ComponentRequiredByAnotherComponent_ThrowsAndLeavesComponentInPlace()
        {
            _go.AddComponent<BoxCollider>();
            _go.AddComponent<RequiresBoxCollider>();

            var ex = Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapComponentRemoveTestObject", "BoxCollider"));
            });
            StringAssert.Contains("RequiresBoxCollider", ex.Message);
            Assert.IsNotNull(_go.GetComponent<BoxCollider>(), "BoxCollider must survive the refused removal.");
        }

        /// <summary>
        /// Regression: the RequireComponent guard must not over-block --
        /// removing one of two components of the required type is fine as
        /// long as another one remains to satisfy the requirement.
        /// </summary>
        [Test]
        public void Execute_DuplicateOfRequiredComponentRemains_RemovalSucceeds()
        {
            _go.AddComponent<BoxCollider>();
            _go.AddComponent<BoxCollider>();
            _go.AddComponent<RequiresBoxCollider>();

            _tool.Execute(Input("UapComponentRemoveTestObject", "BoxCollider", 0));

            Assert.AreEqual(1, _go.GetComponents<BoxCollider>().Length,
                "Exactly one BoxCollider should remain, satisfying RequiresBoxCollider.");
        }

        [UnityEngine.RequireComponent(typeof(BoxCollider))]
        private class RequiresBoxCollider : MonoBehaviour
        {
        }
    }
}
