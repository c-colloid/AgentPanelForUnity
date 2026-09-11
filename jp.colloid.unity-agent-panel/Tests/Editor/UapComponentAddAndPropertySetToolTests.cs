using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>OPS-5 fixture: a component Unity refuses to add twice, so the AddComponent-returns-null path is deterministic.</summary>
    [DisallowMultipleComponent]
    internal sealed class UapDisallowMultipleFixture : MonoBehaviour
    {
    }

    /// <summary>
    /// OPS-5 fixture for the IsAbstract PRE-check. It has to be a custom
    /// abstract MonoBehaviour: the Unity built-ins the engine refuses as
    /// "abstract" (UnityEngine.Collider, Renderer) are declared CONCRETE in
    /// the C# API -- measured in CI -- so they exercise the null-return
    /// guard instead, never this one.
    /// </summary>
    internal abstract class UapAbstractComponentFixture : MonoBehaviour
    {
    }

    /// <summary>Real component-add + generic SerializedProperty-based property-set tests (design section 1.2/3.2, the "heart of Phase 5").</summary>
    [TestFixture]
    public class UapComponentAddAndPropertySetToolTests
    {
        private GameObject _go;
        private UapComponentAddTool _addTool;
        private UapPropertySetTool _setTool;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("UapPropSetTestObject");
            _addTool = new UapComponentAddTool();
            _setTool = new UapPropertySetTool();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        private static JsonNode AddInput(string path, string componentType)
        {
            return JsonNode.NewObject().Set("path", path).Set("componentType", componentType);
        }

        [Test]
        public void ComponentAdd_AddsBoxColliderToGameObject()
        {
            _addTool.Execute(AddInput("UapPropSetTestObject", "BoxCollider"));
            Assert.IsNotNull(_go.GetComponent<BoxCollider>());
        }

        [Test]
        public void ComponentAdd_UnknownPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _addTool.Execute(AddInput("NoSuchObjectAtAll12345", "BoxCollider"));
            });
        }

        [Test]
        public void ComponentAdd_UnknownComponentType_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _addTool.Execute(AddInput("UapPropSetTestObject", "NotARealComponentType12345"));
            });
        }

        [Test]
        public void ComponentAdd_MissingRequiredFields_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate { _addTool.Execute(JsonNode.NewObject()); });
        }

        /// <summary>
        /// OPS-5: AddComponent returns null (with a Console error, no
        /// exception) when [DisallowMultipleComponent] refuses a duplicate.
        /// The old code passed that null to RegisterCreatedObjectUndo and
        /// reported a false "Added ..." success.
        /// </summary>
        [Test]
        public void ComponentAdd_DisallowMultipleDuplicate_ThrowsInsteadOfFalseSuccess()
        {
            _go.AddComponent<UapDisallowMultipleFixture>();

            // Unity logs its own refusal error; that log is expected here,
            // not a test failure.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var ex = Assert.Throws<System.InvalidOperationException>(delegate
                {
                    _addTool.Execute(AddInput("UapPropSetTestObject", "UapDisallowMultipleFixture"));
                });
                StringAssert.Contains("did not add", ex.Message);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
            Assert.AreEqual(1, _go.GetComponents<UapDisallowMultipleFixture>().Length,
                "exactly the original component must remain");
        }

        /// <summary>OPS-5 pre-check: an abstract type resolves via TypeCache but can never be added -- refuse it by name before AddComponent.</summary>
        [Test]
        public void ComponentAdd_AbstractType_ThrowsNamingTheProblem()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(delegate
            {
                _addTool.Execute(AddInput("UapPropSetTestObject", "UapAbstractComponentFixture"));
            });
            StringAssert.Contains("abstract", ex.Message);
            Assert.IsNull(_go.GetComponent<UapAbstractComponentFixture>());
        }

        /// <summary>
        /// OPS-5, measured in CI: UnityEngine.Collider is CONCRETE in the C#
        /// API, so the IsAbstract pre-check cannot see it -- the engine
        /// refuses the add natively and AddComponent returns null. What
        /// matters is that this reports a failure rather than the old false
        /// "Added UnityEngine.Collider" success; which of the two guards
        /// catches it does not.
        /// </summary>
        [Test]
        public void ComponentAdd_NativelyRefusedBuiltin_ThrowsInsteadOfFalseSuccess()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var ex = Assert.Throws<System.InvalidOperationException>(delegate
                {
                    _addTool.Execute(AddInput("UapPropSetTestObject", "Collider"));
                });
                StringAssert.Contains("UnityEngine.Collider", ex.Message);
                StringAssert.Contains("did not add", ex.Message);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
            Assert.IsNull(_go.GetComponent<Collider>(), "the refused add must leave the object untouched");
        }

        [Test]
        public void PropertySet_BooleanProperty_OnComponent_WritesThroughSerializedObject()
        {
            _go.AddComponent<BoxCollider>();
            JsonNode input = JsonNode.NewObject()
                .Set("path", "UapPropSetTestObject")
                .Set("componentType", "BoxCollider")
                .Set("propertyPath", "m_IsTrigger")
                .Set("value", true);

            _setTool.Execute(input);

            Assert.IsTrue(_go.GetComponent<BoxCollider>().isTrigger);
        }

        [Test]
        public void PropertySet_Vector3Property_OnComponent_WritesAllThreeComponents()
        {
            _go.AddComponent<BoxCollider>();
            JsonNode input = JsonNode.NewObject()
                .Set("path", "UapPropSetTestObject")
                .Set("componentType", "BoxCollider")
                .Set("propertyPath", "m_Size")
                .Set("value", JsonNode.NewObject().Set("x", 2.0).Set("y", 3.0).Set("z", 4.0));

            _setTool.Execute(input);

            Assert.AreEqual(new Vector3(2f, 3f, 4f), _go.GetComponent<BoxCollider>().size);
        }

        [Test]
        public void PropertySet_UnknownPropertyPath_Throws()
        {
            _go.AddComponent<BoxCollider>();
            JsonNode input = JsonNode.NewObject()
                .Set("path", "UapPropSetTestObject")
                .Set("componentType", "BoxCollider")
                .Set("propertyPath", "m_NoSuchProperty12345")
                .Set("value", true);

            Assert.Throws<System.InvalidOperationException>(delegate { _setTool.Execute(input); });
        }

        [Test]
        public void PropertySet_NeitherPathNorAssetPath_Throws()
        {
            JsonNode input = JsonNode.NewObject().Set("propertyPath", "m_IsTrigger").Set("value", true);
            Assert.Throws<System.ArgumentException>(delegate { _setTool.Execute(input); });
        }

        [Test]
        public void PropertySet_BothPathAndAssetPath_Throws()
        {
            JsonNode input = JsonNode.NewObject()
                .Set("path", "UapPropSetTestObject")
                .Set("assetPath", "Assets/Foo.mat")
                .Set("propertyPath", "m_IsTrigger")
                .Set("value", true);
            Assert.Throws<System.ArgumentException>(delegate { _setTool.Execute(input); });
        }

        [Test]
        public void PropertySet_MissingComponentType_Throws()
        {
            JsonNode input = JsonNode.NewObject()
                .Set("path", "UapPropSetTestObject")
                .Set("propertyPath", "m_IsTrigger")
                .Set("value", true);
            Assert.Throws<System.ArgumentException>(delegate { _setTool.Execute(input); });
        }

        // -- Enum writes. The string form always validated against enumNames
        // and threw with the valid list; the numeric form, three lines away in
        // the same method, wrote enumValueIndex with no validation at all.
        // Found while fixing the identical shape in the ui module (design note
        // section 9.4). enumValueIndex is an INDEX, so an out-of-range one does
        // not throw -- it leaves a serialized value no user could have picked
        // and reports success.

        private JsonNode EnumInput(JsonNode value)
        {
            return JsonNode.NewObject()
                .Set("path", "UapPropSetTestObject")
                .Set("componentType", "Rigidbody")
                .Set("propertyPath", "m_CollisionDetection")
                .Set("value", value);
        }

        [Test]
        public void PropertySet_EnumByValidIndex_Writes()
        {
            Rigidbody body = _go.AddComponent<Rigidbody>();

            _setTool.Execute(EnumInput(JsonNode.Of((long)1)));

            Assert.AreEqual(CollisionDetectionMode.Continuous, body.collisionDetectionMode);
        }

        [Test]
        public void PropertySet_EnumByOutOfRangeIndex_Throws_InsteadOfWritingAnUnpickableValue()
        {
            _go.AddComponent<Rigidbody>();

            Assert.Throws<System.ArgumentException>(
                delegate { _setTool.Execute(EnumInput(JsonNode.Of((long)99))); });
        }

        [Test]
        public void PropertySet_EnumGivenABoolean_Throws_InsteadOfSilentlyWritingIndexZero()
        {
            // AsLong is lenient, so before the fix a boolean returned the
            // default and quietly selected the first enum member.
            _go.AddComponent<Rigidbody>();

            Assert.Throws<System.ArgumentException>(
                delegate { _setTool.Execute(EnumInput(JsonNode.Of(true))); });
        }

        [Test]
        public void PropertySet_EnumByName_StillWorks()
        {
            // The branch that was already correct must survive the change.
            //
            // Note the SPACE. SerializedProperty.enumNames returns Unity's
            // display-form names ("Continuous Dynamic"), not the CLR member
            // names ("ContinuousDynamic") -- this test was written with the
            // CLR name first and failed, which is how that got measured. The
            // implementation is right either way: it validates against
            // enumNames and its refusal lists them verbatim, so a caller that
            // guesses the CLR name is told exactly what to send instead.
            Rigidbody body = _go.AddComponent<Rigidbody>();

            _setTool.Execute(EnumInput(JsonNode.Of("Continuous Dynamic")));

            Assert.AreEqual(CollisionDetectionMode.ContinuousDynamic, body.collisionDetectionMode);
        }
    }
}
