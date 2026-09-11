using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// OPS-7 (SR-strict-coercion): the generic SerializedProperty writer
    /// refuses wrong-shaped values instead of writing lenient-accessor
    /// defaults. Before this, a bool on an int property silently wrote 0,
    /// and a scalar on a Vector property resolved every component to its
    /// CURRENT value -- a no-op reported as success. The writer is driven
    /// directly here (a GameObject's m_Layer int and a BoxCollider's
    /// m_Size Vector3 are the probes); uap_property_set's end-to-end
    /// plumbing is covered by UapComponentAddAndPropertySetToolTests.
    /// </summary>
    [TestFixture]
    public class UapPropertyValueWriterTests
    {
        private GameObject _go;
        private BoxCollider _collider;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("UapValueWriterTestObject");
            _collider = _go.AddComponent<BoxCollider>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        private static SerializedProperty Prop(Object target, string path)
        {
            return new SerializedObject(target).FindProperty(path);
        }

        private static void WriteAndApply(SerializedProperty prop, JsonNode value)
        {
            UapPropertyValueWriter.Write(prop, value, false);
            prop.serializedObject.ApplyModifiedProperties();
        }

        // -- integers ------------------------------------------------------

        /// <summary>
        /// OPS-7, corrected by CI: a boolean on an Integer property maps to
        /// 1/0 instead of being refused. Unity serializes plenty of
        /// conceptually-boolean settings as ints (TextureImporter's
        /// m_EnableMipMap), so `true` is the natural thing to send -- and
        /// the pre-OPS-7 code was wrong in the worst direction here, since
        /// AsLong on a bool returned its silent default 0 ("turn mipmaps
        /// on" reported success while writing OFF).
        /// </summary>
        [Test]
        public void IntProperty_BooleanValue_MapsToOneOrZero_NotASilentDefault()
        {
            SerializedProperty on = Prop(_go, "m_Layer");
            WriteAndApply(on, JsonNode.Of(true));
            Assert.AreEqual(1, _go.layer, "true must write 1, not AsLong's silent 0");

            SerializedProperty off = Prop(_go, "m_Layer");
            WriteAndApply(off, JsonNode.Of(false));
            Assert.AreEqual(0, _go.layer);
        }

        [Test]
        public void IntProperty_ObjectValue_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(Prop(_go, "m_Layer"), JsonNode.NewObject().Set("x", 1), false);
            });
        }

        [Test]
        public void IntProperty_NonNumericString_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(Prop(_go, "m_Layer"), JsonNode.Of("not-a-number"), false);
            });
        }

        [Test]
        public void IntProperty_FractionalString_Throws_InsteadOfSilentZero()
        {
            // "1.5" passes a float parse but AsLong's integer parse falls
            // to the silent default -- exactly the defect class OPS-7
            // closes, so the integer check requires an integral string.
            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(Prop(_go, "m_Layer"), JsonNode.Of("1.5"), false);
            });
        }

        [Test]
        public void IntProperty_NumericString_StillWrites()
        {
            SerializedProperty prop = Prop(_go, "m_Layer");
            WriteAndApply(prop, JsonNode.Of("5"));
            Assert.AreEqual(5, _go.layer);
        }

        [Test]
        public void IntProperty_Number_StillWrites()
        {
            SerializedProperty prop = Prop(_go, "m_Layer");
            WriteAndApply(prop, JsonNode.Of(3));
            Assert.AreEqual(3, _go.layer);
        }

        // -- floats --------------------------------------------------------

        [Test]
        public void FloatProperty_BooleanValue_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(Prop(_collider, "m_Size.x"), JsonNode.Of(true), false);
            });
        }

        [Test]
        public void FloatProperty_NumericString_StillWrites()
        {
            SerializedProperty prop = Prop(_collider, "m_Size.x");
            WriteAndApply(prop, JsonNode.Of("2.5"));
            Assert.AreEqual(2.5f, _collider.size.x, 0.0001f);
        }

        // -- structs -------------------------------------------------------

        [Test]
        public void Vector3Property_ScalarValue_ThrowsInsteadOfSilentNoOp()
        {
            var ex = Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(Prop(_collider, "m_Size"), JsonNode.Of(5.0), false);
            });
            StringAssert.Contains("a number", ex.Message);
            Assert.AreEqual(Vector3.one, _collider.size, "the refused write must change nothing");
        }

        [Test]
        public void Vector3Property_StringValue_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(Prop(_collider, "m_Size"), JsonNode.Of("5,6,7"), false);
            });
        }

        [Test]
        public void Vector3Property_ComponentObject_StillWrites()
        {
            SerializedProperty prop = Prop(_collider, "m_Size");
            WriteAndApply(prop, JsonNode.NewObject().Set("x", 5.0).Set("y", 6.0).Set("z", 7.0));
            Assert.AreEqual(new Vector3(5f, 6f, 7f), _collider.size);
        }

        [Test]
        public void Vector3Property_PartialObject_MergesOverCurrentValue()
        {
            // The partial-update feature stays: only the given components
            // change, the rest keep their current values.
            _collider.size = new Vector3(2f, 3f, 4f);
            SerializedProperty prop = Prop(_collider, "m_Size");
            WriteAndApply(prop, JsonNode.NewObject().Set("y", 9.0));
            Assert.AreEqual(new Vector3(2f, 9f, 4f), _collider.size);
        }

        [Test]
        public void ColorProperty_StringValue_Throws()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = Prop(camera, "m_BackGroundColor");
            Assert.AreEqual(SerializedPropertyType.Color, prop.propertyType);

            Assert.Throws<System.ArgumentException>(delegate
            {
                UapPropertyValueWriter.Write(prop, JsonNode.Of("red"), false);
            });
        }

        [Test]
        public void ColorProperty_ComponentObject_StillWrites()
        {
            var camera = _go.AddComponent<Camera>();
            SerializedProperty prop = Prop(camera, "m_BackGroundColor");
            WriteAndApply(prop, JsonNode.NewObject()
                .Set("r", 1.0).Set("g", 0.0).Set("b", 0.0).Set("a", 1.0));
            Assert.AreEqual(new Color(1f, 0f, 0f, 1f), camera.backgroundColor);
        }
    }
}
