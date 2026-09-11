using System;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Read-only: dumps a scene's hierarchy (or the subtree under an
    /// optional target) as indented "name [Type,Type,...] (N children)"
    /// lines (design section 1.2 "query_hierarchy"). Hard-capped by
    /// maxDepth/maxNodes so a huge scene cannot blow up the tool result;
    /// an explicit "...truncated (N more)" marker line is appended when the
    /// maxNodes cap cuts the dump short.
    /// </summary>
    public sealed class UapQueryHierarchyTool : IUapTool
    {
        private const int DefaultMaxDepth = 6;
        private const int DefaultMaxNodes = 200;

        /// <summary>
        /// Hard ceiling on how many nodes a single call will ever visit
        /// (count towards totalCount / recurse into), independent of
        /// maxDepth/maxNodes. Without this, a wide-but-within-maxDepth
        /// hierarchy (e.g. a procedural/terrain scene with many thousands
        /// of siblings) would still be walked in full just to produce the
        /// "N more" truncation count, even though maxNodes already capped
        /// the emitted lines -- defeating the "a huge scene cannot blow up
        /// the tool result" guarantee. Internal (rather than private) so
        /// tests can reference the exact value instead of hardcoding it.
        /// </summary>
        internal const int MaxWalkBudget = 2000;

        public string Name
        {
            get { return "uap_query_hierarchy"; }
        }

        public string Description
        {
            get
            {
                return "Dumps a scene's GameObject hierarchy (or the subtree under an optional target)"
                    + " as indented lines with component types and child counts. Read-only.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the subtree root. Omit to dump the whole scene."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("maxDepth", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Max depth to descend, relative to the dump root (default " + DefaultMaxDepth + ")."))
                        .Set("maxNodes", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Max total lines to emit before truncating (default " + DefaultMaxNodes + ").")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string sceneQuery = input["scene"].AsString(null);
            string path = input["path"].AsString(null);
            int maxDepth = input["maxDepth"].AsInt(DefaultMaxDepth);
            if (maxDepth < 0)
            {
                maxDepth = DefaultMaxDepth;
            }
            int maxNodes = input["maxNodes"].AsInt(DefaultMaxNodes);
            if (maxNodes <= 0)
            {
                maxNodes = DefaultMaxNodes;
            }

            string error;
            Scene scene = UapAddressing.ResolveTargetScene(sceneQuery, out error);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }

            var roots = new List<Transform>();
            if (!string.IsNullOrEmpty(path))
            {
                GameObject go = UapAddressing.ResolveInScene(scene, path, out error);
                if (go == null)
                {
                    throw new InvalidOperationException(error);
                }
                roots.Add(go.transform);
            }
            else
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    roots.Add(root.transform);
                }
            }

            var lines = new List<string>();
            int totalCount = 0;
            bool budgetExceeded = false;
            foreach (Transform root in roots)
            {
                if (budgetExceeded)
                {
                    break;
                }
                Walk(root, 0, maxDepth, maxNodes, lines, ref totalCount, ref budgetExceeded);
            }

            int remaining = totalCount - lines.Count;
            if (remaining > 0)
            {
                lines.Add(budgetExceeded
                    ? "...truncated (over " + remaining + " more)"
                    : "...truncated (" + remaining + " more)");
            }

            return UapToolResults.Text(string.Join("\n", lines.ToArray()));
        }

        private static void Walk(Transform t, int depth, int maxDepth, int maxNodes, List<string> lines,
            ref int totalCount, ref bool budgetExceeded)
        {
            if (depth > maxDepth || budgetExceeded)
            {
                return;
            }
            totalCount++;
            if (lines.Count < maxNodes)
            {
                lines.Add(FormatLine(t, depth));
            }
            if (totalCount >= MaxWalkBudget)
            {
                // Hard safety net: stop visiting further nodes entirely (not just
                // emitting lines for them) so an arbitrarily huge/wide hierarchy
                // cannot make this call do unbounded work. The reported "more"
                // count from here on is a lower bound, not exact.
                budgetExceeded = true;
                return;
            }
            if (depth < maxDepth)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    Walk(t.GetChild(i), depth + 1, maxDepth, maxNodes, lines, ref totalCount, ref budgetExceeded);
                    if (budgetExceeded)
                    {
                        return;
                    }
                }
            }
        }

        private static string FormatLine(Transform t, int depth)
        {
            Component[] comps = t.GetComponents<Component>();
            var typeNames = new List<string>();
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null)
                {
                    continue;
                }
                if (c.GetType() == typeof(Transform))
                {
                    continue;
                }
                typeNames.Add(c.GetType().Name);
            }
            var sb = new StringBuilder();
            sb.Append(' ', depth * 2);
            sb.Append(t.name).Append(" [").Append(string.Join(",", typeNames.ToArray())).Append("] (")
                .Append(t.childCount).Append(t.childCount == 1 ? " child)" : " children)");
            return sb.ToString();
        }
    }
}
