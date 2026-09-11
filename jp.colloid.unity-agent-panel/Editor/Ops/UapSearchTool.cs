using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// `uap_search` (docs/design-notes/2026-09-10-unity-official-plugin-
    /// integration.md section 4): runs a Unity Search query synchronously
    /// and returns the hits as the same path forms the other uap_* tools
    /// accept as targets. Exists so a query composed by the official
    /// plugin's /unity:generate-editor-search-query skill can be answered
    /// in the chat instead of by opening the Search window.
    ///
    /// Provider vocabulary is the agent-facing "asset" / "scene"; "asset"
    /// maps to Unity's `adb` provider, NOT `asset`: the indexed `asset`
    /// provider missed assets created moments earlier (verification report
    /// section 3, batchmode), while `adb` answers straight from the
    /// AssetDatabase in about 10ms after a 1s first call.
    /// </summary>
    public sealed class UapSearchTool : IUapTool
    {
        public const int DefaultMaxResults = 50;

        public const string ProviderAsset = "asset";
        public const string ProviderScene = "scene";

        /// <summary>Unity's provider id for the AssetDatabase-backed (non-indexed) asset search.</summary>
        internal const string UnityAssetDatabaseProviderId = "adb";
        internal const string UnitySceneProviderId = "scene";

        public string Name
        {
            get { return "uap_search"; }
        }

        public string Description
        {
            get
            {
                return "Runs a Unity Search query (the syntax /unity:generate-editor-search-query produces,"
                    + " e.g. \"t:prefab ref:Player.prefab\" or \"t:Light -isstatic\") over project assets"
                    + " and/or the open scenes and returns matches as paths usable as the target of"
                    + " uap_object_inspect / uap_property_set / uap_asset_set_property. Read-only.";
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
                            .Set("description", "Unity Search query text."))
                        .Set("providers", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "string")
                                .Set("enum", JsonNode.NewArray().Add(ProviderAsset).Add(ProviderScene)))
                            .Set("description", "Where to search: \"asset\" (project assets), \"scene\" (open"
                                + " scenes' GameObjects), or both. Default: both."))
                        .Set("maxResults", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Cap on returned items (default " + DefaultMaxResults + ").")))
                    .Set("required", JsonNode.NewArray().Add("query"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string query = input["query"].AsString(null);
            if (string.IsNullOrEmpty(query) || query.Trim().Length == 0)
            {
                throw new ArgumentException("'query' is required.");
            }
            List<string> providers = ResolveProviders(input["providers"]);
            int maxResults = input["maxResults"].AsInt(DefaultMaxResults);
            if (maxResults <= 0)
            {
                maxResults = DefaultMaxResults;
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < providers.Count; i++)
            {
                wanted.Add(ToUnityProviderId(providers[i]));
            }

            if (wanted.Contains(UnitySceneProviderId))
            {
                InvalidateSceneProviderCache();
            }

            JsonNode items = JsonNode.NewArray();
            int total = 0;
            using (SearchContext context = SearchService.CreateContext(new List<string>(wanted), query))
            {
                ISearchList list = SearchService.Request(context, SearchFlags.Synchronous);
                // No provider-id filter here: the context was created with
                // exactly the requested providers, and the ids items carry
                // are NOT the ids they were requested under -- `adb` hands
                // back items stamped "asset" (or "_group_provider_Resources"
                // for built-in resources; verification spike log lines 157
                // and 173). Classification below only asks "scene or not".
                foreach (SearchItem item in list)
                {
                    total++;
                    if (items.Count < maxResults)
                    {
                        items.Add(DescribeItem(item, context));
                    }
                }
            }

            JsonNode providersOut = JsonNode.NewArray();
            for (int i = 0; i < providers.Count; i++)
            {
                providersOut.Add(providers[i]);
            }
            JsonNode result = JsonNode.NewObject()
                .Set("query", query)
                .Set("providers", providersOut)
                .Set("totalMatches", total)
                .Set("truncated", total > items.Count)
                .Set("items", items);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static System.Reflection.MethodInfo s_invalidateScene;
        private static bool s_invalidateSceneLookedUp;

        /// <summary>
        /// Measured on 2022.3 (verification report section 3 addendum): the
        /// scene provider (UnityEditor.Search.Providers.SceneProvider, in
        /// UnityEditor.QuickSearchModule) builds its GameObject cache on the
        /// first query and refreshes it only from its own hierarchyChanged
        /// hook, which never fired across editor ticks in the batchmode
        /// spike -- an object created after the first query stayed
        /// invisible to every later per-call context (and to a shared one).
        /// Its private `InvalidateScene()` is the switch: invoked before a
        /// scene query, the next request rebuilds the cache and finds fresh
        /// objects. Reflection, best effort: a missing method (a future
        /// Unity) just leaves the provider's own refresh in charge. Cost is
        /// one rebuild per call (about 130ms for 300 objects).
        /// </summary>
        private static void InvalidateSceneProviderCache()
        {
            try
            {
                object target = null;
                foreach (SearchProvider provider in SearchService.Providers)
                {
                    if (string.Equals(provider.id, UnitySceneProviderId, StringComparison.Ordinal)
                        && provider.fetchItems != null)
                    {
                        target = provider.fetchItems.Target;
                        break;
                    }
                }
                if (target == null)
                {
                    return;
                }
                if (!s_invalidateSceneLookedUp)
                {
                    s_invalidateSceneLookedUp = true;
                    s_invalidateScene = target.GetType().GetMethod("InvalidateScene",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                }
                if (s_invalidateScene != null && s_invalidateScene.DeclaringType.IsInstanceOfType(target))
                {
                    s_invalidateScene.Invoke(target, null);
                }
            }
            catch (Exception)
            {
                // best effort -- see the doc comment
            }
        }

        /// <summary>
        /// Pure: validates the agent-facing provider list. Null/empty means
        /// both; anything outside the vocabulary throws rather than
        /// silently searching nothing (Unity itself returns 0 hits for an
        /// unknown provider id without complaint -- verification report
        /// section 3).
        /// </summary>
        internal static List<string> ResolveProviders(JsonNode providersNode)
        {
            var result = new List<string>();
            if (providersNode == null || !providersNode.IsArray || providersNode.Count == 0)
            {
                result.Add(ProviderAsset);
                result.Add(ProviderScene);
                return result;
            }
            foreach (JsonNode entry in providersNode.Items)
            {
                string value = entry.AsString(null);
                if (!string.Equals(value, ProviderAsset, StringComparison.Ordinal)
                    && !string.Equals(value, ProviderScene, StringComparison.Ordinal))
                {
                    throw new ArgumentException("'providers' entries must be \"asset\" or \"scene\"; got: "
                        + (value ?? "<non-string>"));
                }
                if (!result.Contains(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        internal static string ToUnityProviderId(string agentFacingProvider)
        {
            return string.Equals(agentFacingProvider, ProviderScene, StringComparison.Ordinal)
                ? UnitySceneProviderId
                : UnityAssetDatabaseProviderId;
        }

        private static JsonNode DescribeItem(SearchItem item, SearchContext context)
        {
            string providerId = item.provider != null ? item.provider.id : string.Empty;
            string provider = string.Equals(providerId, UnitySceneProviderId, StringComparison.Ordinal)
                ? ProviderScene
                : ProviderAsset;
            string label = null;
            try
            {
                label = item.GetLabel(context, true);
            }
            catch (Exception)
            {
                // label is decoration; the path below is the contract
            }
            UnityEngine.Object obj = null;
            try
            {
                obj = item.ToObject();
            }
            catch (Exception)
            {
                // some providers resolve lazily; a null object just yields no path
            }
            string path = string.Empty;
            if (obj != null)
            {
                var go = obj as GameObject;
                if (go != null && go.scene.IsValid())
                {
                    path = UapAddressing.DescribeHierarchyPath(go.transform);
                }
                else
                {
                    var component = obj as Component;
                    if (component != null && component.gameObject.scene.IsValid())
                    {
                        path = UapAddressing.DescribeHierarchyPath(component.transform);
                    }
                    else
                    {
                        path = AssetDatabase.GetAssetPath(obj) ?? string.Empty;
                    }
                }
            }
            return JsonNode.NewObject()
                .Set("provider", provider)
                .Set("id", item.id ?? string.Empty)
                .Set("label", label ?? string.Empty)
                .Set("path", path);
        }
    }
}
