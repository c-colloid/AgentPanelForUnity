using System.Collections.Generic;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The ONE addressing helper's tests (task line: "define ONE addressing
    /// helper with tests"). Pure-function cases first, then real scene
    /// object resolution against the active scene (EditMode tests are
    /// allowed real scene object creation per this stream's brief) --
    /// every created GameObject is torn down again so no test leaks scene
    /// state into the rest of the suite.
    /// </summary>
    [TestFixture]
    public class UapAddressingTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _created)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            _created.Clear();
        }

        private GameObject Create(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            _created.Add(go);
            return go;
        }

        // -- SplitHierarchyPath (pure) -------------------------------------

        [Test]
        public void SplitHierarchyPath_SimplePath_SplitsOnSlash()
        {
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, UapAddressing.SplitHierarchyPath("A/B/C"));
        }

        [Test]
        public void SplitHierarchyPath_LeadingTrailingSlashes_CollapseAway()
        {
            CollectionAssert.AreEqual(new[] { "A", "B" }, UapAddressing.SplitHierarchyPath("/A/B/"));
        }

        [Test]
        public void SplitHierarchyPath_DoubleSlash_CollapsesAway()
        {
            CollectionAssert.AreEqual(new[] { "A", "B" }, UapAddressing.SplitHierarchyPath("A//B"));
        }

        [Test]
        public void SplitHierarchyPath_NullOrEmpty_ReturnsEmptyArray()
        {
            Assert.AreEqual(0, UapAddressing.SplitHierarchyPath(null).Length);
            Assert.AreEqual(0, UapAddressing.SplitHierarchyPath(string.Empty).Length);
        }

        [Test]
        public void SplitHierarchyPath_SingleSegment_ReturnsOneElement()
        {
            CollectionAssert.AreEqual(new[] { "Root" }, UapAddressing.SplitHierarchyPath("Root"));
        }

        // -- ResolveSceneIndex (pure) ---------------------------------------

        [Test]
        public void ResolveSceneIndex_EmptyQuery_ReturnsDefaultIndex()
        {
            int result = UapAddressing.ResolveSceneIndex(null, new[] { "A", "B" }, new[] { "pA", "pB" }, 1);
            Assert.AreEqual(1, result);
        }

        [Test]
        public void ResolveSceneIndex_MatchesByName()
        {
            int result = UapAddressing.ResolveSceneIndex("B", new[] { "A", "B" }, new[] { "pA", "pB" }, 0);
            Assert.AreEqual(1, result);
        }

        [Test]
        public void ResolveSceneIndex_MatchesByPath_WhenNameDoesNotMatch()
        {
            int result = UapAddressing.ResolveSceneIndex("pB", new[] { "A", "B" }, new[] { "pA", "pB" }, 0);
            Assert.AreEqual(1, result);
        }

        [Test]
        public void ResolveSceneIndex_NoMatch_ReturnsMinusOne()
        {
            int result = UapAddressing.ResolveSceneIndex("Nope", new[] { "A", "B" }, new[] { "pA", "pB" }, 0);
            Assert.AreEqual(-1, result);
        }

        // -- ResolveTargetScene (real, no prefab stage open) -----------------

        [Test]
        public void ResolveTargetScene_EmptyQuery_NoPrefabStage_ReturnsActiveScene()
        {
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage(), "test assumes no prefab stage is open");
            string error;
            Scene scene = UapAddressing.ResolveTargetScene(null, out error);
            Assert.IsNull(error);
            Assert.AreEqual(EditorSceneManager.GetActiveScene(), scene);
        }

        [Test]
        public void ResolveTargetScene_UnknownQuery_ReturnsError()
        {
            string error;
            UapAddressing.ResolveTargetScene("NoSuchSceneAtAll12345", out error);
            Assert.IsNotNull(error);
        }

        // -- ResolveInScene / ResolveHierarchyPath (real GameObjects) --------

        [Test]
        public void ResolveInScene_TopLevelObject_Found()
        {
            GameObject root = Create("UapAddrTestRoot1");
            string error;
            GameObject found = UapAddressing.ResolveInScene(root.scene, "UapAddrTestRoot1", out error);
            Assert.IsNull(error);
            Assert.AreSame(root, found);
        }

        [Test]
        public void ResolveInScene_NestedChild_Found()
        {
            GameObject root = Create("UapAddrTestRoot2");
            GameObject child = Create("Child", root.transform);
            GameObject grandchild = Create("Grandchild", child.transform);

            string error;
            GameObject found = UapAddressing.ResolveInScene(root.scene, "UapAddrTestRoot2/Child/Grandchild", out error);

            Assert.IsNull(error);
            Assert.AreSame(grandchild, found);
        }

        [Test]
        public void ResolveInScene_MissingSegment_ReturnsDescriptiveError()
        {
            GameObject root = Create("UapAddrTestRoot3");
            string error;
            GameObject found = UapAddressing.ResolveInScene(root.scene, "UapAddrTestRoot3/NoSuchChild", out error);
            Assert.IsNull(found);
            StringAssert.Contains("NoSuchChild", error);
        }

        [Test]
        public void ResolveInScene_EmptyPath_ReturnsError()
        {
            string error;
            GameObject found = UapAddressing.ResolveInScene(EditorSceneManager.GetActiveScene(), string.Empty, out error);
            Assert.IsNull(found);
            Assert.IsNotNull(error);
        }

        [Test]
        public void ResolveHierarchyPath_EmptySceneQuery_ResolvesInActiveScene()
        {
            GameObject root = Create("UapAddrTestRoot4");
            string error;
            GameObject found = UapAddressing.ResolveHierarchyPath(null, "UapAddrTestRoot4", out error);
            Assert.IsNull(error);
            Assert.AreSame(root, found);
        }

        // -- DescribeHierarchyPath --------------------------------------------

        [Test]
        public void DescribeHierarchyPath_BuildsSlashJoinedPathFromRoot()
        {
            GameObject root = Create("UapAddrTestRoot5");
            GameObject child = Create("Mid", root.transform);
            GameObject leaf = Create("Leaf", child.transform);

            string path = UapAddressing.DescribeHierarchyPath(leaf.transform);

            Assert.AreEqual("UapAddrTestRoot5/Mid/Leaf", path);
        }

        [Test]
        public void DescribeHierarchyPath_Null_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, UapAddressing.DescribeHierarchyPath(null));
        }

        // -- ResolveAsset -----------------------------------------------------

        [Test]
        public void ResolveAsset_EmptyPath_ReturnsError()
        {
            string error;
            var asset = UapAddressing.ResolveAsset(string.Empty, out error);
            Assert.IsNull(asset);
            Assert.IsNotNull(error);
        }

        [Test]
        public void ResolveAsset_NonExistentPath_ReturnsError()
        {
            string error;
            var asset = UapAddressing.ResolveAsset("Assets/UapAddressingTests_DoesNotExist_12345.asset", out error);
            Assert.IsNull(asset);
            StringAssert.Contains("not found", error);
        }

        // -- Index suffix / instance id (D6) --------------------------------

        [TestCase("Cube[2]", true, "Cube", 2)]
        [TestCase("Cube[0]", true, "Cube", 0)]
        [TestCase("A[b][12]", true, "A[b]", 12)]
        [TestCase("Cube", false, null, 0)]
        [TestCase("Cube[]", false, null, 0)]
        [TestCase("Cube[x]", false, null, 0)]
        [TestCase("[1]", false, null, 0)]
        [TestCase("Cube[-1]", false, null, 0)]
        public void TryParseIndexedSegment_Cases(string segment, bool expected, string expectedName, int expectedIndex)
        {
            string name;
            int index;
            Assert.AreEqual(expected, UapAddressing.TryParseIndexedSegment(segment, out name, out index));
            if (expected)
            {
                Assert.AreEqual(expectedName, name);
                Assert.AreEqual(expectedIndex, index);
            }
        }

        [Test]
        public void ResolveInScene_IndexSuffix_PicksTheNthSameNamedSibling()
        {
            GameObject root = Create("UapAddrIdxRoot");
            GameObject first = Create("UapAddrCube", root.transform);
            GameObject second = Create("UapAddrCube", root.transform);
            string error;

            Assert.AreSame(first, UapAddressing.ResolveInScene(root.scene, "UapAddrIdxRoot/UapAddrCube", out error));
            Assert.AreSame(first, UapAddressing.ResolveInScene(root.scene, "UapAddrIdxRoot/UapAddrCube[0]", out error));
            Assert.AreSame(second, UapAddressing.ResolveInScene(root.scene, "UapAddrIdxRoot/UapAddrCube[1]", out error));
            Assert.IsNull(UapAddressing.ResolveInScene(root.scene, "UapAddrIdxRoot/UapAddrCube[2]", out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void ResolveInScene_LiteralNameWithBrackets_WinsOverIndexInterpretation()
        {
            GameObject root = Create("UapAddrBrRoot");
            Create("UapAddrFoo", root.transform);
            GameObject literal = Create("UapAddrFoo[0]", root.transform);
            string error;

            Assert.AreSame(literal, UapAddressing.ResolveInScene(root.scene, "UapAddrBrRoot/UapAddrFoo[0]", out error));
        }

        [Test]
        public void ResolveInScene_InstanceIdForm_ResolvesTheGameObject()
        {
            GameObject go = Create("UapAddrIdTarget");
            string error;

            GameObject found = UapAddressing.ResolveInScene(go.scene, "#" + go.GetInstanceID(), out error);

            Assert.AreSame(go, found);
            Assert.IsNull(error);
        }

        [Test]
        public void ResolveInScene_InstanceIdForm_UnknownId_ReturnsError()
        {
            string error;
            Assert.IsNull(UapAddressing.ResolveInScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "#2000000000", out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void DescribeHierarchyPath_SameNamedSiblings_GetIndexSuffix_AndRoundTrip()
        {
            GameObject root = Create("UapAddrDescRoot");
            Create("UapAddrTwin", root.transform);
            GameObject second = Create("UapAddrTwin", root.transform);
            GameObject only = Create("UapAddrSolo", root.transform);

            string secondPath = UapAddressing.DescribeHierarchyPath(second.transform);
            Assert.AreEqual("UapAddrDescRoot/UapAddrTwin[1]", secondPath);
            Assert.AreEqual("UapAddrDescRoot/UapAddrSolo", UapAddressing.DescribeHierarchyPath(only.transform));
            string error;
            Assert.AreSame(second, UapAddressing.ResolveInScene(root.scene, secondPath, out error));
        }
    }
}
