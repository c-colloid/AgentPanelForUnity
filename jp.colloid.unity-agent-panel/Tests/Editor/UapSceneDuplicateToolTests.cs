using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real duplication via uap_scene_duplicate: count, offsets, unique names, Undo.</summary>
    [TestFixture]
    public class UapSceneDuplicateToolTests
    {
        private UapSceneDuplicateTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapSceneDuplicateTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapDupTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static JsonNode Offset(float x, float y, float z)
        {
            return JsonNode.NewObject().Set("x", (double)x).Set("y", (double)y).Set("z", (double)z);
        }

        [Test]
        public void Execute_ThreeCopiesWithOffset_PositionsAndUniqueNames()
        {
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = "UapDupTestCube";
            source.transform.localPosition = new Vector3(1f, 0f, 0f);

            Undo.IncrementCurrentGroup();
            JsonNode result = _tool.Execute(JsonNode.NewObject()
                .Set("path", "UapDupTestCube")
                .Set("count", 3)
                .Set("offset", Offset(2f, 0f, 0f)));

            var copies = new List<GameObject>();
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go != source && go.name.StartsWith("UapDupTestCube"))
                {
                    copies.Add(go);
                }
            }
            Assert.AreEqual(3, copies.Count);
            var names = new HashSet<string>();
            var xs = new List<float>();
            foreach (GameObject c in copies)
            {
                names.Add(c.name);
                xs.Add(c.transform.localPosition.x);
            }
            Assert.AreEqual(3, names.Count, "names are unique");
            Assert.IsFalse(names.Contains(source.name));
            xs.Sort();
            Assert.AreEqual(3f, xs[0], 1e-4f);
            Assert.AreEqual(5f, xs[1], 1e-4f);
            Assert.AreEqual(7f, xs[2], 1e-4f);
            StringAssert.Contains("instanceId", JsonWriter.Write(result));

            Undo.RevertAllInCurrentGroup();

            int remaining = 0;
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapDupTestCube"))
                {
                    remaining++;
                }
            }
            Assert.AreEqual(1, remaining, "undo removed the copies, the source stays");
        }

        [Test]
        public void Execute_NamedCopyUnderParent_UsesNameAndParent()
        {
            var parent = new GameObject("UapDupTestParent");
            var source = new GameObject("UapDupTestSource");

            _tool.Execute(JsonNode.NewObject()
                .Set("path", "UapDupTestSource")
                .Set("name", "UapDupTestCopy")
                .Set("parentPath", "UapDupTestParent"));

            Assert.AreEqual(1, parent.transform.childCount);
            Assert.AreEqual("UapDupTestCopy", parent.transform.GetChild(0).name);
            Assert.IsNotNull(source);
        }

        [Test]
        public void Execute_InvalidCount_Throws()
        {
            new GameObject("UapDupTestSource2");
            Assert.Throws<System.ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("path", "UapDupTestSource2").Set("count", 0));
            });
            Assert.Throws<System.ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("path", "UapDupTestSource2").Set("count", 101));
            });
        }

        [Test]
        public void Execute_MissingPath_Throws()
        {
            Assert.Throws<System.ArgumentException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("path", "UapDupTestNope"));
            });
        }
    }
}
