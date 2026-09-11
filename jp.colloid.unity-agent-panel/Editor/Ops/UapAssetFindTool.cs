using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Read-only: AssetDatabase.FindAssets search (design section 1.2 "query-type tools").</summary>
    public sealed class UapAssetFindTool : IUapTool
    {
        private const int DefaultMaxResults = 50;

        public string Name
        {
            get { return "uap_asset_find"; }
        }

        public string Description
        {
            get
            {
                return "Searches the project's assets using an AssetDatabase.FindAssets filter"
                    + " (e.g. \"t:Material Red\" or a plain name substring). Read-only.";
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
                            .Set("description", "AssetDatabase.FindAssets filter string."))
                        .Set("maxResults", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Cap on returned paths (default " + DefaultMaxResults + ").")))
                    .Set("required", JsonNode.NewArray().Add("query"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string query = input["query"].AsString(null);
            if (string.IsNullOrEmpty(query))
            {
                throw new ArgumentException("'query' is required.");
            }
            int maxResults = input["maxResults"].AsInt(DefaultMaxResults);
            if (maxResults <= 0)
            {
                maxResults = DefaultMaxResults;
            }

            string[] guids = AssetDatabase.FindAssets(query);
            JsonNode arr = JsonNode.NewArray();
            int count = Math.Min(guids.Length, maxResults);
            for (int i = 0; i < count; i++)
            {
                arr.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
            }
            JsonNode result = JsonNode.NewObject()
                .Set("query", query)
                .Set("totalMatches", guids.Length)
                .Set("paths", arr);
            return UapToolResults.Text(JsonWriter.Write(result));
        }
    }
}
