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
    }
}
