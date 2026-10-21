using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Looks up one or more registered UapOps tools' full metadata by name
    /// (design note docs/design-notes/2026-09-22-uloop-phase1.md section
    /// 2.8 A): name, module, full description, inputSchema, readOnly and
    /// undoable, for every requested name that resolves to a tool in an
    /// ENABLED module. Unlike <c>uap_tools</c> (Ops/UapToolGateway.cs) this
    /// tool is registered normally, in the "core" module, so it is always
    /// reachable regardless of tools/list mode -- it exists for the case
    /// where a client already knows which tool it wants (from a prior
    /// full-mode tools/list, from this package's own docs, or simply from
    /// memory) and just needs that one tool's exact argument shape without
    /// a round trip through search.
    ///
    /// An unknown name or one whose module is currently disabled is
    /// reported with found:false and a reason (the same shape
    /// UapToolGateway.ResolveToolDetail gives uap_tools' 'names' path) --
    /// never thrown, so one bad name in a batch of ten does not fail the
    /// other nine.
    ///
    /// 2026-10-02 (design note docs/design-notes/2026-10-02-uap-help-
    /// playbooks.md): the second job, <c>topic</c> -- the body of a
    /// <see cref="UapPlaybook"/> (the ordered, multi-tool procedure no
    /// single tool description can carry), served on demand instead of
    /// riding the always-loaded steering text. A topic whose required tools
    /// are not all registered and enabled is "unavailable", and so is an
    /// unknown id: both answer with the list of topics that ARE available
    /// (plain text, never an exception). The <c>names</c> reply is
    /// unchanged byte for byte.
    ///
    /// ReadOnly: this tool only reads registry/settings state that is
    /// already this package's own (which tools exist, which modules are
    /// enabled) -- it never touches the project, the scene, editor state
    /// or OS focus, so it is safe to auto-approve exactly like
    /// uap_query_hierarchy or uap_object_inspect.
    /// </summary>
    public sealed class UapHelpTool : IUapTool
    {
        public const string ToolName = "uap_help";

        public string Name { get { return ToolName; } }

        public string Description
        {
            get
            {
                return "Returns the full description and inputSchema of UapOps tools by name, or a"
                    + " step-by-step playbook for a multi-tool task by topic id."
                    + " Input: names (an array of tool names) or name (a single tool name) for tool"
                    + " metadata -- module, readOnly and undoable too; use it when you already know a"
                    + " tool's name (from a prior tools/list, docs, or memory) and just need its exact"
                    + " argument shape (a compact tools/list carries one-line field descriptions; names"
                    + " here returns the full schema). An unknown name or one whose module is currently"
                    + " disabled is reported with found:false and a reason instead of failing the whole call."
                    + " topic (a playbook id such as \"verify-gameplay\" or \"ui-click\", named in the"
                    + " steering text) returns that procedure's ordered steps with exact tool names and"
                    + " arguments, decision points and failure handling; an unknown topic, or one whose"
                    + " tools are not available (module disabled), returns the list of available topics"
                    + " instead of failing.";
            }
        }

        public string Module { get { return "core"; } }

        public bool Undoable { get { return false; } }

        public bool ReadOnly { get { return true; } }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("names", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "string"))
                            .Set("description", "Wire names of the tools to describe, e.g."
                                + " [\"uap_scene_create_object\", \"uap_property_set\"]."))
                        .Set("name", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Convenience for a single tool name instead of 'names'."))
                        .Set("topic", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "A playbook id (e.g. \"verify-gameplay\"): returns that"
                                + " multi-tool procedure. An unknown or unavailable id returns the list of"
                                + " available topics.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            return Execute(input, UapOpsServer.Registry, UapOpsServer.EnabledModules(),
                UapOpsServer.Playbooks, UapOpsServer.CompactToolList);
        }

        /// <summary>
        /// The composition seam: everything uap_help reads is passed in, so a
        /// test can drive it with a stub registry, a fake provider's catalog
        /// and a chosen module list without touching the editor's statics.
        /// </summary>
        internal JsonNode Execute(JsonNode input, ToolRegistry registry, IList<string> enabledModules,
            UapPlaybookCatalog playbooks, bool compactToolList)
        {
            List<string> requested = new List<string>();
            JsonNode namesNode = input["names"];
            if (namesNode.IsArray)
            {
                foreach (string name in namesNode.AsStringArray())
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        requested.Add(name);
                    }
                }
            }
            string single = input["name"].AsString(null);
            if (!string.IsNullOrEmpty(single))
            {
                requested.Add(single);
            }
            string topic = input["topic"].AsString(null);
            bool hasTopic = !string.IsNullOrEmpty(topic);

            if (requested.Count == 0 && !hasTopic)
            {
                string index = UapPlaybookCatalog.FormatIndex(
                    AvailablePlaybooks(registry, enabledModules, playbooks, compactToolList));
                throw new System.ArgumentException(
                    "uap_help requires 'names' (an array of tool names), 'name' (a single tool name) or"
                    + " 'topic' (a playbook id)." + (index.Length > 0 ? " " + index + "." : string.Empty));
            }

            string toolsText = null;
            if (requested.Count > 0)
            {
                JsonNode results = JsonNode.NewArray();
                for (int i = 0; i < requested.Count; i++)
                {
                    results.Add(UapToolGateway.ResolveToolDetail(registry, enabledModules, requested[i]));
                }
                toolsText = JsonWriter.Write(JsonNode.NewObject().Set("tools", results));
            }
            if (!hasTopic)
            {
                return UapToolResults.Text(toolsText);
            }
            string topicText = DescribeTopic(topic,
                AvailablePlaybooks(registry, enabledModules, playbooks, compactToolList));
            return UapToolResults.Text(toolsText == null ? topicText : toolsText + "\n\n" + topicText);
        }

        private static List<UapPlaybook> AvailablePlaybooks(ToolRegistry registry, IList<string> enabledModules,
            UapPlaybookCatalog playbooks, bool compactToolList)
        {
            return playbooks.Filter(UapPlaybookCatalog.AvailabilityFor(enabledModules, registry.ModuleOf, compactToolList));
        }

        /// <summary>
        /// Plain text, not JSON: the body is a step list the agent reads
        /// as-is, and JSON-escaping its newlines and quotes would only make
        /// it harder to read. A topic that is not in <paramref name="available"/>
        /// (unknown, or one of its required tools is missing) gets the list
        /// of those that are.
        /// </summary>
        private static string DescribeTopic(string topic, List<UapPlaybook> available)
        {
            for (int i = 0; i < available.Count; i++)
            {
                if (string.Equals(available[i].Id, topic, System.StringComparison.Ordinal))
                {
                    UapPlaybook playbook = available[i];
                    return "Playbook \"" + playbook.Id + "\": " + playbook.Title + "\n"
                        + playbook.Summary + "\n"
                        + "Required tools: " + string.Join(", ", playbook.RequiredTools) + "\n\n"
                        + playbook.Body;
                }
            }
            if (available.Count == 0)
            {
                return "No playbook \"" + topic + "\" is available, and no playbooks are available at all"
                    + " (the modules they need are disabled).";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("No playbook \"").Append(topic).Append("\" is available (unknown id, or a tool it needs is"
                + " not enabled). Available topics (uap_help topic:\"<id>\"):");
            for (int i = 0; i < available.Count; i++)
            {
                sb.Append("\n- ").Append(available[i].Id).Append(": ").Append(available[i].Summary);
            }
            return sb.ToString();
        }
    }
}
