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
                    + " (e.g. \"PhysBone\" -> VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone). Each hit reports"
                    + " a \"kind\": \"component\" (uap_component_add), \"stateMachineBehaviour\""
                    + " (uap_animator_behaviour -- NOT a component) or \"scriptableObject\" (uap_asset_create)."
                    + " Read-only.";
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
            CollectMatches(TypeCache.GetTypesDerivedFrom<Component>(), query, results, maxResults, "component");
            if (results.Count < maxResults)
            {
                CollectMatches(TypeCache.GetTypesDerivedFrom<ScriptableObject>(), query, results, maxResults,
                    "scriptableObject");
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
            int maxResults, string kind)
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
                    .Set("assembly", t.Assembly.GetName().Name)
                    .Set("kind", DescribeKind(t, kind)));
            }
        }

        /// <summary>
        /// A StateMachineBehaviour IS a ScriptableObject, so TypeCache hands
        /// it back in the ScriptableObject pass -- but it is attached to an
        /// animator state by uap_animator_behaviour, never created as an
        /// asset by uap_asset_create nor added by uap_component_add. The
        /// kind field is what tells an agent which of the three tools the
        /// hit belongs to.
        /// </summary>
        private static string DescribeKind(Type type, string kind)
        {
            if (string.Equals(kind, "scriptableObject", StringComparison.Ordinal)
                && typeof(StateMachineBehaviour).IsAssignableFrom(type))
            {
                return "stateMachineBehaviour";
            }
            return kind;
        }
    }
}
