using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real Transform re-parenting via uap_scene_reparent, including the cycle refusal and the Undo revert path.</summary>
    [TestFixture]
    public class UapSceneReparentToolTests
    {
        private UapSceneReparentTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapSceneReparentTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapReparentTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static JsonNode Input(string path, string newParentPath = null, bool? worldPositionStays = null)
        {
            JsonNode node = JsonNode.NewObject().Set("path", path);
            if (newParentPath != null)
            {
                node.Set("newParentPath", newParentPath);
            }
            if (worldPositionStays.HasValue)
            {
                node.Set("worldPositionStays", worldPositionStays.Value);
            }
            return node;
        }

        [Test]
        public void Execute_ReparentsUnderNewParent()
        {
            var parent = new GameObject("UapReparentTestParent");
            var child = new GameObject("UapReparentTestChild");

            _tool.Execute(Input("UapReparentTestChild", "UapReparentTestParent"));

            Assert.AreSame(parent.transform, child.transform.parent);
        }

        [Test]
        public void Execute_OmittedNewParentPath_MovesToSceneRoot()
        {
            var parent = new GameObject("UapReparentTestParent2");
            var child = new GameObject("UapReparentTestChild2");
            child.transform.SetParent(parent.transform);

            _tool.Execute(Input("UapReparentTestParent2/UapReparentTestChild2"));

            Assert.IsNull(child.transform.parent);
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
                _tool.Execute(Input("NoSuchObjectAtAll12345", "AlsoMissing12345"));
            });
        }

        [Test]
        public void Execute_NewParentIsTargetItself_Throws()
        {
            new GameObject("UapReparentTestSelf");

            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapReparentTestSelf", "UapReparentTestSelf"));
            });
        }

        [Test]
        public void Execute_NewParentIsDescendantOfTarget_Throws()
        {
            var target = new GameObject("UapReparentTestCycleParent");
            var descendant = new GameObject("UapReparentTestCycleChild");
            descendant.transform.SetParent(target.transform);

            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("UapReparentTestCycleParent", "UapReparentTestCycleParent/UapReparentTestCycleChild"));
            });
            Assert.AreSame(target.transform, descendant.transform.parent, "the cycle attempt must not have mutated the hierarchy.");
        }

        [Test]
        public void Execute_WorldPositionStaysFalse_PreservesLocalTransform()
        {
            var parent = new GameObject("UapReparentTestWPSParent");
            parent.transform.position = new Vector3(10f, 0f, 0f);
            var child = new GameObject("UapReparentTestWPSChild");
            child.transform.localPosition = new Vector3(1f, 2f, 3f);

            _tool.Execute(Input("UapReparentTestWPSChild", "UapReparentTestWPSParent", worldPositionStays: false));

            Assert.AreEqual(new Vector3(1f, 2f, 3f), child.transform.localPosition);
        }

        [Test]
        public void Execute_WorldPositionStaysTrue_KeepsWorldPosition()
        {
            var parent = new GameObject("UapReparentTestWPSTrueParent");
            parent.transform.position = new Vector3(10f, 0f, 0f);
            var child = new GameObject("UapReparentTestWPSTrueChild");
            child.transform.position = new Vector3(1f, 2f, 3f);

            _tool.Execute(Input("UapReparentTestWPSTrueChild", "UapReparentTestWPSTrueParent", worldPositionStays: true));

            Vector3 worldPos = child.transform.position;
            Assert.AreEqual(1f, worldPos.x, 0.0001f);
            Assert.AreEqual(2f, worldPos.y, 0.0001f);
            Assert.AreEqual(3f, worldPos.z, 0.0001f);
        }

        [Test]
        public void Execute_ThenUndo_RestoresThePreviousParent()
        {
            var originalParent = new GameObject("UapReparentTestUndoOriginal");
            var newParent = new GameObject("UapReparentTestUndoNew");
            var child = new GameObject("UapReparentTestUndoChild");
            child.transform.SetParent(originalParent.transform);
            Undo.IncrementCurrentGroup();

            _tool.Execute(Input("UapReparentTestUndoOriginal/UapReparentTestUndoChild", "UapReparentTestUndoNew"));
            Assert.AreSame(newParent.transform, child.transform.parent);

            Undo.PerformUndo();

            Assert.AreSame(originalParent.transform, child.transform.parent, "Undo should restore the previous parent.");
        }
    }
}
