using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real scene transform reads/writes via uap_transform_set (mirrors
    /// UapSceneCreateObjectToolTests -- real GameObjects, real Undo, no
    /// mocking of the Editor).
    /// </summary>
    [TestFixture]
    public class UapTransformSetToolTests
    {
        private UapTransformSetTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapTransformSetTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapTransformSetToolTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static JsonNode Vec3(double x, double y, double z)
        {
            return JsonNode.NewObject().Set("x", x).Set("y", y).Set("z", z);
        }

        [Test]
        public void Execute_LocalPosition_WritesLocalPosition()
        {
            var go = new GameObject("UapTransformSetToolTestLocal");
            JsonNode input = JsonNode.NewObject()
                .Set("path", go.name)
                .Set("position", Vec3(1.0, 2.0, 3.0));

            _tool.Execute(input);

            Assert.AreEqual(new Vector3(1f, 2f, 3f), go.transform.localPosition);
        }

        [Test]
        public void Execute_WorldPosition_OnChildOfTranslatedParent_SetsWorldPosition()
        {
            var parent = new GameObject("UapTransformSetToolTestParent");
            parent.transform.position = new Vector3(10f, 0f, 0f);
            var child = new GameObject("UapTransformSetToolTestChild");
            child.transform.SetParent(parent.transform, false);

            JsonNode input = JsonNode.NewObject()
                .Set("path", "UapTransformSetToolTestParent/UapTransformSetToolTestChild")
                .Set("space", "world")
                .Set("position", Vec3(5.0, 0.0, 0.0));

            _tool.Execute(input);

            Assert.AreEqual(new Vector3(5f, 0f, 0f), child.transform.position);
            Assert.AreNotEqual(new Vector3(5f, 0f, 0f), child.transform.localPosition,
                "world position 5,0,0 under a parent at 10,0,0 must NOT equal local position");
            Assert.AreEqual(new Vector3(-5f, 0f, 0f), child.transform.localPosition);
        }

        [Test]
        public void Execute_PartialRotation_OnlyOverridesGivenAxis()
        {
            var go = new GameObject("UapTransformSetToolTestRot");
            go.transform.localEulerAngles = new Vector3(10f, 20f, 30f);

            JsonNode input = JsonNode.NewObject()
                .Set("path", go.name)
                .Set("rotation", JsonNode.NewObject().Set("y", 90.0));

            _tool.Execute(input);

            Vector3 result = go.transform.localEulerAngles;
            Assert.AreEqual(10f, result.x, 0.01f);
            Assert.AreEqual(90f, result.y, 0.01f);
            Assert.AreEqual(30f, result.z, 0.01f);
        }

        [Test]
        public void Execute_Scale_WritesLocalScale()
        {
            var go = new GameObject("UapTransformSetToolTestScale");
            JsonNode input = JsonNode.NewObject()
                .Set("path", go.name)
                .Set("scale", Vec3(2.0, 3.0, 4.0));

            _tool.Execute(input);

            Assert.AreEqual(new Vector3(2f, 3f, 4f), go.transform.localScale);
        }

        [Test]
        public void Execute_NoFieldsGiven_ReadsCurrentValues_AndChangesNothing()
        {
            var go = new GameObject("UapTransformSetToolTestRead");
            go.transform.localPosition = new Vector3(1f, 2f, 3f);
            go.transform.localEulerAngles = new Vector3(4f, 5f, 6f);
            go.transform.localScale = new Vector3(7f, 8f, 9f);

            JsonNode input = JsonNode.NewObject().Set("path", go.name);
            JsonNode result = _tool.Execute(input);

            Assert.AreEqual(new Vector3(1f, 2f, 3f), go.transform.localPosition);
            Assert.AreEqual(new Vector3(7f, 8f, 9f), go.transform.localScale);
            string text = result[0]["text"].AsString();
            StringAssert.Contains("\"local\"", text);
            StringAssert.Contains("\"world\"", text);
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            JsonNode input = JsonNode.NewObject().Set("path", "NoSuchObjectAtAll12345");
            Assert.Throws<System.InvalidOperationException>(delegate { _tool.Execute(input); });
        }

        [Test]
        public void Execute_Undo_RestoresPreviousPosition()
        {
            var go = new GameObject("UapTransformSetToolTestUndo");
            go.transform.localPosition = new Vector3(0f, 0f, 0f);
            Undo.IncrementCurrentGroup();

            JsonNode input = JsonNode.NewObject()
                .Set("path", go.name)
                .Set("position", Vec3(9.0, 9.0, 9.0));
            _tool.Execute(input);
            Assert.AreEqual(new Vector3(9f, 9f, 9f), go.transform.localPosition);

            Undo.PerformUndo();

            Assert.AreEqual(new Vector3(0f, 0f, 0f), go.transform.localPosition);
        }
    }
}
