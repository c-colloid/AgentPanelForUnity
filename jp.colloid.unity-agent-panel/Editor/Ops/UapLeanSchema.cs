using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Pure, Unity-free "lean" form of a tool's JSON input schema, served
    /// ONLY in the compact tools/list (design note docs/design-notes/
    /// 2026-10-02-session-context-cost.md, section 4). A client that does
    /// not defer tool schemas (Codex, Gemini, any ACP agent behind the
    /// stdio relay) reads every listed schema at session start, and field
    /// descriptions are most of that text; a lean schema keeps everything
    /// the client needs to FORM a valid call (types, enums, required,
    /// bounds, nesting) and cuts each field description to its first
    /// sentence. The full schema stays one call away
    /// (<c>uap_help names:[...]</c> / <c>uap_tools names:[...]</c> return
    /// <see cref="IUapTool.InputSchema"/> untouched), and the full-mode
    /// tools/list never goes through this class at all.
    ///
    /// No UnityEngine/UnityEditor reference on purpose, so it is compiled
    /// verbatim into ci/SmokeTests (see LeanSchemaSmoke), like
    /// <see cref="UapToolSummary"/>, which it reuses for the sentence cut.
    /// </summary>
    public static class UapLeanSchema
    {
        /// <summary>
        /// Default cap for one field description. Shorter than
        /// <see cref="UapToolSummary.MaxChars"/> (a tool summary stands alone
        /// in a uap_tools result; a field description sits next to the field
        /// name and type it explains).
        /// </summary>
        public const int DefaultMaxDescriptionChars = 100;

        /// <summary>
        /// Nesting limit, only a guard against a self-referencing node tree
        /// built in code (a parsed document cannot have one): real schemas
        /// here are a handful of levels deep. Past it the branch is dropped
        /// to an empty object rather than recursing forever.
        /// </summary>
        private const int MaxDepth = 32;

        /// <summary>Keywords whose value is copied unchanged (constraints a client needs to form a valid call).</summary>
        private static readonly HashSet<string> VerbatimKeys = new HashSet<string>
        {
            "type", "enum", "required", "default", "const",
            "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
            "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern", "format",
            "$ref"
        };

        /// <summary>Keywords whose value is ONE sub-schema (or a boolean, for additionalProperties).</summary>
        private static readonly HashSet<string> SchemaKeys = new HashSet<string>
        {
            "items", "additionalProperties", "not"
        };

        /// <summary>Keywords whose value is an ARRAY of sub-schemas.</summary>
        private static readonly HashSet<string> SchemaListKeys = new HashSet<string>
        {
            "oneOf", "anyOf", "allOf"
        };

        /// <summary>Keywords whose value is an OBJECT mapping names to sub-schemas -- the names are data, never keywords.</summary>
        private static readonly HashSet<string> SchemaMapKeys = new HashSet<string>
        {
            "properties", "patternProperties", "$defs", "definitions"
        };

        /// <summary><see cref="Lean(JsonNode, int)"/> with <see cref="DefaultMaxDescriptionChars"/>.</summary>
        public static JsonNode Lean(JsonNode inputSchema)
        {
            return Lean(inputSchema, DefaultMaxDescriptionChars);
        }

        /// <summary>
        /// A deep copy of <paramref name="inputSchema"/> with, at every
        /// nesting level: the structural keywords above kept verbatim
        /// (<c>type</c>, <c>enum</c>, <c>items</c>, <c>properties</c>,
        /// <c>required</c>, <c>default</c>, <c>minimum</c>/<c>maximum</c>,
        /// <c>additionalProperties</c>, plus the other validation keywords
        /// in <see cref="VerbatimKeys"/>, which a client needs just as much
        /// to form a valid call), every <c>description</c> reduced to its
        /// first sentence capped at <paramref name="maxDescriptionChars"/>
        /// (<see cref="UapToolSummary.LeadText(string, int)"/>: the first
        /// sentence, extended past a bare label such as "start only." and
        /// cut before an open bracket or quote rather than inside it), and
        /// everything else (<c>examples</c>, <c>title</c>, unknown keys)
        /// dropped. Nothing is ever added or reordered, and the input is not
        /// modified. Idempotent: a lean schema leaned again is unchanged.
        /// Never throws: null becomes the Null node; a non-object input is
        /// returned as is (an array has each element leaned); a keyword
        /// whose value has an unexpected shape (a non-object "properties",
        /// a non-string "description") is copied as is, or dropped when it
        /// is a description.
        /// </summary>
        public static JsonNode Lean(JsonNode inputSchema, int maxDescriptionChars)
        {
            return LeanNode(inputSchema, maxDescriptionChars, 0);
        }

        private static JsonNode LeanNode(JsonNode node, int maxChars, int depth)
        {
            if (node == null)
            {
                return JsonNode.Null;
            }
            if (depth > MaxDepth)
            {
                return JsonNode.NewObject();
            }
            if (node.IsArray)
            {
                JsonNode list = JsonNode.NewArray();
                foreach (JsonNode item in node.Items)
                {
                    list.Add(LeanNode(item, maxChars, depth + 1));
                }
                return list;
            }
            if (!node.IsObject)
            {
                // Scalars are never mutated after creation (JsonNode has no
                // setter for them), so sharing one between the two trees is
                // indistinguishable from copying it -- and keeps a number's
                // raw text ("1.0") intact.
                return node;
            }

            JsonNode result = JsonNode.NewObject();
            foreach (KeyValuePair<string, JsonNode> pair in node.Properties)
            {
                string key = pair.Key;
                JsonNode value = pair.Value;
                if (key == "description")
                {
                    if (value.IsString)
                    {
                        string line = UapToolSummary.LeadText(value.AsString(), maxChars);
                        if (line.Length > 0)
                        {
                            result.Set(key, line);
                        }
                    }
                }
                else if (VerbatimKeys.Contains(key))
                {
                    result.Set(key, CopyVerbatim(value, depth + 1));
                }
                else if (SchemaKeys.Contains(key))
                {
                    result.Set(key, LeanNode(value, maxChars, depth + 1));
                }
                else if (SchemaListKeys.Contains(key))
                {
                    result.Set(key, value.IsArray ? LeanNode(value, maxChars, depth + 1) : CopyVerbatim(value, depth + 1));
                }
                else if (SchemaMapKeys.Contains(key))
                {
                    result.Set(key, value.IsObject ? LeanMap(value, maxChars, depth + 1) : CopyVerbatim(value, depth + 1));
                }
            }
            return result;
        }

        /// <summary>A name -> sub-schema map: names kept as they are (a property called "description" or "examples" is a property, not a keyword), each value leaned.</summary>
        private static JsonNode LeanMap(JsonNode map, int maxChars, int depth)
        {
            if (depth > MaxDepth)
            {
                return JsonNode.NewObject();
            }
            JsonNode result = JsonNode.NewObject();
            foreach (KeyValuePair<string, JsonNode> pair in map.Properties)
            {
                result.Set(pair.Key, LeanNode(pair.Value, maxChars, depth + 1));
            }
            return result;
        }

        private static JsonNode CopyVerbatim(JsonNode node, int depth)
        {
            if (node == null)
            {
                return JsonNode.Null;
            }
            if (depth > MaxDepth)
            {
                return JsonNode.Null;
            }
            if (node.IsArray)
            {
                JsonNode list = JsonNode.NewArray();
                foreach (JsonNode item in node.Items)
                {
                    list.Add(CopyVerbatim(item, depth + 1));
                }
                return list;
            }
            if (node.IsObject)
            {
                JsonNode copy = JsonNode.NewObject();
                foreach (KeyValuePair<string, JsonNode> pair in node.Properties)
                {
                    copy.Set(pair.Key, CopyVerbatim(pair.Value, depth + 1));
                }
                return copy;
            }
            return node;
        }
    }
}
