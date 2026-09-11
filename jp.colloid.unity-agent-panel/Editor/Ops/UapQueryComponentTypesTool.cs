using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Read-only type discovery over TypeCache (design section 3b): lets
    /// the agent find a Component/ScriptableObject type by a substring of
    /// its name before calling uap_component_add/uap_asset_create --
    /// including third-party SDK types (VRCPhysBone, MagicaCloth, ...) that
    /// are already compiled into the project but not otherwise
    /// discoverable without a documentation lookup.
    /// </summary>
    public sealed class UapQueryComponentTypesTool : IUapTool
    {
        private const int DefaultMaxResults = 50;

        public string Name
        {
            get { return "uap_query_component_types"; }
        }

        public string Description
        {
            get
            {
                return "Searches already-compiled Component and ScriptableObject types by name substring"
                    + " (e.g. \"PhysBone\" -> VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone). Read-only.";
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
                        .Set("query", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Substring to match against short and fully-qualified type names. Empty lists everything up to maxResults."))
                        .Set("maxResults", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Cap on returned types (default " + DefaultMaxResults + ").")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string query = input["query"].AsString(string.Empty);
            int maxResults = input["maxResults"].AsInt(DefaultMaxResults);
            if (maxResults <= 0)
            {
                maxResults = DefaultMaxResults;
            }

            var results = new List<JsonNode>();
            CollectMatches(TypeCache.GetTypesDerivedFrom<Component>(), query, results, maxResults);
            if (results.Count < maxResults)
            {
                CollectMatches(TypeCache.GetTypesDerivedFrom<ScriptableObject>(), query, results, maxResults);
            }

            JsonNode arr = JsonNode.NewArray();
            for (int i = 0; i < results.Count; i++)
            {
                arr.Add(results[i]);
            }
            JsonNode result = JsonNode.NewObject().Set("query", query).Set("types", arr);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static void CollectMatches(TypeCache.TypeCollection types, string query, List<JsonNode> results,
            int maxResults)
        {
            foreach (Type t in types)
            {
                if (results.Count >= maxResults)
                {
                    return;
                }
                if (t.IsAbstract || t.IsGenericTypeDefinition)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(query)
                    && t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && (t.FullName == null || t.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                results.Add(JsonNode.NewObject()
                    .Set("shortName", t.Name)
                    .Set("fullName", t.FullName ?? t.Name)
                    .Set("assembly", t.Assembly.GetName().Name));
            }
        }
    }
}
