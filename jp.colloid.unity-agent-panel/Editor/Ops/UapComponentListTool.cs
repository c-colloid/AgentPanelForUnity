using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Read-only: lists every component (by full type name) on a GameObject (design section 1.2 "query-type tools").</summary>
    public sealed class UapComponentListTool : IUapTool
    {
        public string Name
        {
            get { return "uap_component_list"; }
        }

        public string Description
        {
            get
            {
                return "Lists the components attached to a GameObject, by full type name."
                    + " Each entry carries 'index' (position among ALL components) and"
                    + " 'typeIndex' (ordinal among components of the SAME type)."
                    + " Tools taking componentIndex expect typeIndex, not index. Read-only.";
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
                            .Set("description", "Hierarchy path of the target GameObject."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage).")))
                    .Set("required", JsonNode.NewArray().Add("path"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string path = input["path"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            string sceneQuery = input["scene"].AsString(null);
            string error;
            GameObject go = UapAddressing.ResolveHierarchyPath(sceneQuery, path, out error);
            if (go == null)
            {
                throw new InvalidOperationException(error);
            }

            Component[] comps = go.GetComponents<Component>();
            JsonNode arr = JsonNode.NewArray();
            // OPS-1 (SR-component-typeindex): the consumers' componentIndex
            // (remove/inspect/property_set/prefab_revert_override) is a
            // PER-TYPE ordinal -- GetComponents(type)[componentIndex], the
            // natural Unity semantics. Emitting only the all-components
            // index here invited passing it straight into componentIndex
            // and destroying the wrong component (list index 2 read as
            // "third of that type"). typeIndex is the value those tools
            // actually accept; index stays for display/order.
            var perTypeCounters = new Dictionary<Type, int>();
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                int typeIndex = 0;
                if (c != null)
                {
                    Type type = c.GetType();
                    perTypeCounters.TryGetValue(type, out typeIndex);
                    perTypeCounters[type] = typeIndex + 1;
                }
                arr.Add(JsonNode.NewObject()
                    .Set("index", i)
                    .Set("typeIndex", typeIndex)
                    .Set("type", c != null ? c.GetType().FullName : "<Missing Script>"));
            }
            JsonNode result = JsonNode.NewObject().Set("path", path).Set("components", arr);
            return UapToolResults.Text(JsonWriter.Write(result));
        }
    }
}
