using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// Per-model usage entry from result.modelUsage (context meter source).
    /// Field names are camelCase on the wire (unlike result.usage).
    /// </summary>
    public sealed class ModelUsage
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadInputTokens;
        public long CacheCreationInputTokens;
        public long WebSearchRequests;
        public double CostUsd;
        public long ContextWindow;
        public long MaxOutputTokens;

        /// <summary>
        /// The unsuffixed model id ("claude-opus-5") for a key that may
        /// carry a context-window suffix ("claude-opus-5[1m]"). Kept so the
        /// meter can still identify the running model's entry when
        /// AgentClient.CurrentModel and the modelUsage key disagree on the
        /// suffix. Empty when the CLI does not send it.
        /// </summary>
        public string CanonicalModel = string.Empty;

        public static ModelUsage FromJson(JsonNode node)
        {
            var usage = new ModelUsage();
            usage.InputTokens = node["inputTokens"].AsLong();
            usage.OutputTokens = node["outputTokens"].AsLong();
            usage.CacheReadInputTokens = node["cacheReadInputTokens"].AsLong();
            usage.CacheCreationInputTokens = node["cacheCreationInputTokens"].AsLong();
            usage.WebSearchRequests = node["webSearchRequests"].AsLong();
            usage.CostUsd = node["costUSD"].AsDouble();
            usage.ContextWindow = node["contextWindow"].AsLong();
            usage.MaxOutputTokens = node["maxOutputTokens"].AsLong();
            usage.CanonicalModel = node["canonicalModel"].AsString(string.Empty);
            return usage;
        }

        /// <summary>
        /// Inverse of <see cref="FromJson"/>, field names included -- the
        /// session cache persists the per-model usage snapshot through this
        /// (docs/design-notes/2026-08-05-boot-model-usage.md). The wire
        /// format never needs a writer; the cache does, and keeping it here
        /// next to the reader is what lets a round-trip test pin the two
        /// against each other instead of against two copies of the schema.
        /// </summary>
        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("inputTokens", InputTokens)
                .Set("outputTokens", OutputTokens)
                .Set("cacheReadInputTokens", CacheReadInputTokens)
                .Set("cacheCreationInputTokens", CacheCreationInputTokens)
                .Set("webSearchRequests", WebSearchRequests)
                .Set("costUSD", CostUsd)
                .Set("contextWindow", ContextWindow)
                .Set("maxOutputTokens", MaxOutputTokens);
        }
    }

    /// <summary>
    /// type=result -- always the last line of a turn. Ends the turn state.
    /// Captured subtypes: "success"; binary-analysis also lists
    /// "error_max_turns", "error_during_execution",
    /// "error_max_structured_output_retries". Unknown subtypes are kept
    /// verbatim in Subtype and must be treated as terminal by callers.
    /// No required fields beyond type; everything is optional-first.
    /// </summary>
    public sealed class ResultMessage : StreamJsonMessage
    {
        public string Subtype { get; private set; }
        public bool IsError { get; private set; }
        /// <summary>Final text ("result" field).</summary>
        public string ResultText { get; private set; }
        public double TotalCostUsd { get; private set; }
        public long DurationMs { get; private set; }
        public long DurationApiMs { get; private set; }
        public int NumTurns { get; private set; }
        public string StopReason { get; private set; }
        public UsageInfo Usage { get; private set; }
        public Dictionary<string, ModelUsage> ModelUsage { get; private set; }
        public PermissionDenial[] PermissionDenials { get; private set; }
        /// <summary>e.g. "api_error" (observed on auth failure). Null when absent.</summary>
        public string TerminalReason { get; private set; }

        private ResultMessage()
        {
            Type = InboundType.Result;
        }

        public static ResultMessage FromJson(JsonNode node, Action<string> logger)
        {
            var msg = new ResultMessage();
            msg.ReadCommonFields(node);
            msg.Subtype = node["subtype"].AsString();
            msg.IsError = node["is_error"].AsBool(false);
            msg.ResultText = node["result"].AsString();
            msg.TotalCostUsd = node["total_cost_usd"].AsDouble();
            msg.DurationMs = node["duration_ms"].AsLong();
            msg.DurationApiMs = node["duration_api_ms"].AsLong();
            msg.NumTurns = node["num_turns"].AsInt();
            msg.StopReason = node["stop_reason"].AsString();
            msg.Usage = UsageInfo.FromJson(node["usage"]);
            msg.TerminalReason = node["terminal_reason"].AsString();

            msg.ModelUsage = new Dictionary<string, ModelUsage>();
            foreach (var pair in node["modelUsage"].Properties)
            {
                if (pair.Value.IsObject)
                {
                    msg.ModelUsage[pair.Key] = Protocol.ModelUsage.FromJson(pair.Value);
                }
            }

            var denials = new List<PermissionDenial>();
            foreach (var entry in node["permission_denials"].Items)
            {
                if (entry.IsObject)
                {
                    denials.Add(PermissionDenial.FromJson(entry));
                }
            }
            msg.PermissionDenials = denials.ToArray();
            return msg;
        }
    }
}
