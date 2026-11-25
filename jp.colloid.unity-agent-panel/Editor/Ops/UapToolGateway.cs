using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The two "gateway" tools compact-mode tools/list serves instead of
    /// every module's real tool (design note docs/design-notes/
    /// 2026-09-22-uloop-phase1.md section 2.8 B): <c>uap_tools</c> (search/
    /// describe) and <c>uap_call</c> (run one by name). NEITHER is ever
    /// registered in <see cref="ToolRegistry"/> -- both are synthesized
    /// straight into the compact tools/list response by
    /// <see cref="UapOpsRequestHandler"/>, and <c>uap_call</c> is unwrapped
    /// by name there too, before the module-enabled check and the executor
    /// dispatch -- so an inner name of "uap_call" (a self-call) or
    /// "uap_tools" simply falls through to registry.Find returning null
    /// ("Unknown tool: ...") instead of ever recursing: neither gateway
    /// name can ever reach <see cref="ToolRegistry.Find"/> successfully.
    /// This is also why full-mode tools/list, which lists exactly
    /// <see cref="ToolRegistry.ListEnabled"/>, can never show either name:
    /// they exist nowhere the registry looks.
    /// </summary>
    public static class UapToolGateway
    {
        public const string ToolsToolName = "uap_tools";
        public const string CallToolName = "uap_call";

        /// <summary>
        /// The one module compact tools/list still lists directly (design
        /// section 2.8 B's hybrid: the core module stays listed as before and
        /// only the other modules move behind the gateway) -- everything else is reachable
        /// only through <see cref="ToolsToolName"/>/<see cref="CallToolName"/>.
        /// </summary>
        public const string DirectlyListedModule = "core";

        public const string ToolsDescription =
            "Searches or describes the UapOps tools not listed directly in this compact tools/list"
            + " (every enabled module besides '" + DirectlyListedModule + "') -- call it with query"
            + " and/or module to find a tool by keyword, then with names to get its full description"
            + " and inputSchema, then run it through " + CallToolName + ". Without 'names', returns"
            + " name/module/one-line summary/readOnly for every enabled tool whose name, module or"
            + " summary contains 'query' (case-insensitive substring) and, if given, whose module"
            + " equals 'module' exactly. With 'names', returns full detail for exactly those tools"
            + " instead of searching -- an unknown name or one whose module is disabled comes back"
            + " with found:false and a reason instead of an error, so a batch of names never fails as"
            + " a whole for one bad entry. Listed schemas carry one-line field descriptions;"
            + " uap_help names:[...] returns the full schema (as does 'names' here).";

        public const string CallDescription =
            "Runs a UapOps tool that this compact tools/list does not list directly (found through "
            + ToolsToolName + ") by its registered wire name -- input: name (e.g."
            + " 'uap_scene_create_object') and arguments (that tool's own input object, shown by "
            + ToolsToolName + " names:[...]). Behaves exactly like calling the tool directly: the same"
            + " permission card, module-enabled check and result shape apply, so read the target"
            + " tool's inputSchema first rather than guessing its arguments. Listed schemas carry"
            + " one-line field descriptions; uap_help names:[...] returns the full schema.";

        public static JsonNode ToolsInputSchema()
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("properties", JsonNode.NewObject()
                    .Set("query", JsonNode.NewObject().Set("type", "string")
                        .Set("description", "Case-insensitive substring matched against each"
                            + " tool's name, module and one-line summary."))
                    .Set("module", JsonNode.NewObject().Set("type", "string")
                        .Set("description", "Restrict results to exactly this module id"
                            + " (e.g. 'editor')."))
                    .Set("names", JsonNode.NewObject().Set("type", "array")
                        .Set("items", JsonNode.NewObject().Set("type", "string"))
                        .Set("description", "Return full description + inputSchema for exactly"
                            + " these tool names instead of searching.")))
                .Set("additionalProperties", false);
        }

        public static JsonNode CallInputSchema()
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("properties", JsonNode.NewObject()
                    .Set("name", JsonNode.NewObject().Set("type", "string")
                        .Set("description", "The target tool's registered wire name (from "
                            + ToolsToolName + "), e.g. 'uap_editor_screenshot'."))
                    .Set("arguments", JsonNode.NewObject().Set("type", "object")
                        .Set("description", "The target tool's own input object.")))
                .Set("required", JsonNode.NewArray().Add("name"))
                .Set("additionalProperties", false);
        }

        /// <summary>
        /// The tools/list entry for <see cref="ToolsToolName"/> (compact mode
        /// only, so its schema is the lean form like every other schema
        /// that list carries -- <see cref="UapLeanSchema"/>).
        /// </summary>
        public static JsonNode ToolsListingEntry()
        {
            return JsonNode.NewObject()
                .Set("name", ToolsToolName)
                .Set("description", ToolsDescription)
                .Set("inputSchema", UapLeanSchema.Lean(ToolsInputSchema()));
        }

        /// <summary>The tools/list entry for <see cref="CallToolName"/> (compact mode only; lean schema, as <see cref="ToolsListingEntry"/>).</summary>
        public static JsonNode CallListingEntry()
        {
            return JsonNode.NewObject()
                .Set("name", CallToolName)
                .Set("description", CallDescription)
                .Set("inputSchema", UapLeanSchema.Lean(CallInputSchema()));
        }

        /// <summary>
        /// Parses a uap_call invocation's arguments into the inner tool
        /// name + arguments -- true only when 'name' is a non-empty
        /// string. 'arguments' defaults to an empty object (matching every
        /// other tool's "absent arguments" normalization).
        /// </summary>
        public static bool TryUnwrapCall(JsonNode input, out string innerName, out JsonNode innerArguments)
        {
            innerName = null;
            innerArguments = JsonNode.NewObject();
            string name = input != null ? input["name"].AsString(string.Empty) : string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            JsonNode arguments = input["arguments"];
            innerName = name;
            innerArguments = (arguments != null && !arguments.IsNull) ? arguments : JsonNode.NewObject();
            return true;
        }

        /// <summary>
        /// Runs uap_tools: search (no 'names') or detail (with 'names').
        /// Returns the MCP "content" array, same shape as any IUapTool's
        /// Execute -- callers wrap it exactly like a normal tool result.
        /// </summary>
        public static JsonNode ExecuteTools(ToolRegistry registry, IEnumerable<string> enabledModules, JsonNode input)
        {
            if (registry == null)
            {
                throw new ArgumentNullException("registry");
            }
            List<string> modules = new List<string>();
            if (enabledModules != null)
            {
                foreach (string module in enabledModules)
                {
                    if (!string.IsNullOrEmpty(module))
                    {
                        modules.Add(module);
                    }
                }
            }

            JsonNode namesNode = input != null ? input["names"] : JsonNode.Null;
            if (namesNode != null && namesNode.IsArray && namesNode.Count > 0)
            {
                JsonNode details = JsonNode.NewArray();
                foreach (string requested in namesNode.AsStringArray())
                {
                    details.Add(ResolveToolDetail(registry, modules, requested));
                }
                return UapToolResults.Text(JsonWriter.Write(JsonNode.NewObject().Set("tools", details)));
            }

            string query = input != null ? input["query"].AsString(null) : null;
            string moduleFilter = input != null ? input["module"].AsString(null) : null;
            JsonNode matches = JsonNode.NewArray();
            List<IUapTool> enabledTools = registry.ListEnabled(modules);
            for (int i = 0; i < enabledTools.Count; i++)
            {
                IUapTool tool = enabledTools[i];
                if (moduleFilter != null && !string.Equals(tool.Module, moduleFilter, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!UapToolSummary.MatchesQuery(tool.Name, tool.Module, tool.Description, query))
                {
                    continue;
                }
                matches.Add(ResolveToolSummary(tool));
            }
            return UapToolResults.Text(JsonWriter.Write(JsonNode.NewObject().Set("tools", matches)));
        }

        /// <summary>
        /// Full detail for one tool name -- shared by uap_tools' 'names'
        /// path and uap_help, so "unknown name" / "module disabled" report
        /// the exact same shape from either entry point.
        /// </summary>
        public static JsonNode ResolveToolDetail(ToolRegistry registry, IEnumerable<string> enabledModules, string name)
        {
            JsonNode entry = JsonNode.NewObject().Set("name", name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
            {
                return entry.Set("found", false).Set("reason", "empty tool name");
            }
            IUapTool tool = registry.Find(name);
            if (tool == null)
            {
                return entry.Set("found", false).Set("reason", "unknown tool");
            }
            if (!IsModuleEnabled(tool.Module, enabledModules))
            {
                return entry.Set("found", false).Set("module", tool.Module)
                    .Set("reason", "module '" + tool.Module + "' is disabled");
            }
            return entry.Set("found", true)
                .Set("module", tool.Module)
                .Set("description", tool.Description)
                .Set("inputSchema", tool.InputSchema)
                .Set("readOnly", tool.ReadOnly)
                .Set("undoable", tool.Undoable);
        }

        /// <summary>One search-result row: name/module/one-line summary/readOnly (no schema -- see ResolveToolDetail for that).</summary>
        public static JsonNode ResolveToolSummary(IUapTool tool)
        {
            return JsonNode.NewObject()
                .Set("name", tool.Name)
                .Set("module", tool.Module)
                .Set("summary", UapToolSummary.FirstSentence(tool.Description))
                .Set("readOnly", tool.ReadOnly);
        }

        private static bool IsModuleEnabled(string module, IEnumerable<string> enabledModules)
        {
            if (enabledModules == null)
            {
                return false;
            }
            foreach (string candidate in enabledModules)
            {
                if (string.Equals(candidate, module, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
