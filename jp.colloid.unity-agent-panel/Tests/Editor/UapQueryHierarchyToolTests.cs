using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Real-scene hierarchy dumping via uap_query_hierarchy, including the maxDepth/maxNodes truncation caps.</summary>
    [TestFixture]
    public class UapQueryHierarchyToolTests
    {
        private UapQueryHierarchyTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapQueryHierarchyTool();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith("UapQueryHierarchyTest"))
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        private static JsonNode Input(string path = null, int? maxDepth = null, int? maxNodes = null)
        {
            JsonNode node = JsonNode.NewObject();
            if (path != null)
            {
                node.Set("path", path);
            }
            if (maxDepth.HasValue)
            {
                node.Set("maxDepth", maxDepth.Value);
            }
            if (maxNodes.HasValue)
            {
                node.Set("maxNodes", maxNodes.Value);
            }
            return node;
        }

        private static string ExecuteText(UapQueryHierarchyTool tool, JsonNode input)
        {
            JsonNode result = tool.Execute(input);
            return result[0]["text"].AsString();
        }

        [Test]
        public void Execute_ListsRootAndChild_WithComponentTypesAndChildCount()
        {
            var root = new GameObject("UapQueryHierarchyTestRoot");
            root.AddComponent<BoxCollider>();
            var child = new GameObject("UapQueryHierarchyTestChild");
            child.transform.SetParent(root.transform);

            string text = ExecuteText(_tool, Input("UapQueryHierarchyTestRoot"));

            StringAssert.Contains("UapQueryHierarchyTestRoot [BoxCollider] (1 child)", text);
            StringAssert.Contains("UapQueryHierarchyTestChild [] (0 children)", text);
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                _tool.Execute(Input("NoSuchObjectAtAll12345"));
            });
        }

        [Test]
        public void Execute_MaxDepthZero_OmitsChildren()
        {
            var root = new GameObject("UapQueryHierarchyTestDepthRoot");
            var child = new GameObject("UapQueryHierarchyTestDepthChild");
            child.transform.SetParent(root.transform);

            string text = ExecuteText(_tool, Input("UapQueryHierarchyTestDepthRoot", maxDepth: 0));

            StringAssert.Contains("UapQueryHierarchyTestDepthRoot", text);
            StringAssert.DoesNotContain("UapQueryHierarchyTestDepthChild", text);
        }

        [Test]
        public void Execute_MaxNodesCap_TruncatesWithMarker()
        {
            var root = new GameObject("UapQueryHierarchyTestCapRoot");
            for (int i = 0; i < 5; i++)
            {
                var child = new GameObject("UapQueryHierarchyTestCapChild" + i);
                child.transform.SetParent(root.transform);
            }

            string text = ExecuteText(_tool, Input("UapQueryHierarchyTestCapRoot", maxNodes: 3));

            StringAssert.Contains("...truncated (3 more)", text);
        }

        [Test]
        public void Execute_WithinCaps_NoTruncationMarker()
        {
            new GameObject("UapQueryHierarchyTestNoCapRoot");

            string text = ExecuteText(_tool, Input("UapQueryHierarchyTestNoCapRoot"));

            StringAssert.DoesNotContain("truncated", text);
        }

        /// <summary>
        /// Regression for the walk-budget fix: a hierarchy far wider than
        /// maxNodes but still within maxDepth must not be walked node-by-node
        /// in full just to compute an exact "(N more)" count -- the walk
        /// itself must stop at UapQueryHierarchyTool.MaxWalkBudget, reporting
        /// an "(over N more)" lower bound instead. This proves the recursion
        /// is actually bounded, not just the emitted-line count.
        /// </summary>
        [Test]
        public void Execute_HierarchyBeyondWalkBudget_StopsWalkingAndReportsLowerBound()
        {
            var root = new GameObject("UapQueryHierarchyTestBudgetRoot");
            int childCount = UapQueryHierarchyTool.MaxWalkBudget + 50;
            for (int i = 0; i < childCount; i++)
            {
                var child = new GameObject("UapQueryHierarchyTestBudgetChild" + i);
                child.transform.SetParent(root.transform);
            }

            string text = ExecuteText(_tool, Input("UapQueryHierarchyTestBudgetRoot"));

            StringAssert.Contains("...truncated (over ", text);
            // The walk must have stopped at the budget, not counted all childCount+1
            // nodes -- so the reported "more" count is well short of the true total.
            int reportedMore = ExtractTruncatedCount(text);
            Assert.Less(reportedMore, childCount, "Walk should have stopped before counting every child.");
        }

        private static int ExtractTruncatedCount(string text)
        {
            const string marker = "...truncated (over ";
            int start = text.IndexOf(marker, System.StringComparison.Ordinal) + marker.Length;
            int end = text.IndexOf(" more)", start, System.StringComparison.Ordinal);
            return int.Parse(text.Substring(start, end - start));
        }
    }
}
