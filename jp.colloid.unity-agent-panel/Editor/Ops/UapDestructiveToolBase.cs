using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The confirm / dry_run gate every <see cref="IUapDestructiveTool"/>
    /// runs behind (design note 2026-09-09-jobs-and-destructive-confirm
    /// section 2). Subclasses supply the tool's own schema, a
    /// <see cref="Preview"/> and an <see cref="Apply"/>; this base owns the
    /// three-way dispatch in <see cref="Execute"/>:
    ///
    /// - dry_run:true  -> Preview only, prefixed "DRY RUN"; nothing changes.
    /// - no confirm    -> REFUSED (throws, so the CLI sees isError:true)
    ///                    with the same preview and the way to proceed;
    ///                    nothing changes. This is the default, so a call
    ///                    written without having read the description
    ///                    never destroys anything on its first try.
    /// - confirm:true  -> Apply.
    ///
    /// dry_run wins over confirm: a call carrying both only previews. The
    /// two flags are appended to the subclass schema by
    /// <see cref="AugmentSchema"/> so tools/list advertises them (the
    /// schemas use additionalProperties:false, so an undeclared flag would
    /// be rejected client-side). The permission card is unaffected: this
    /// gate is between the AGENT and the tool, the card between the USER
    /// and the agent; both stay.
    /// </summary>
    public abstract class UapDestructiveToolBase : IUapDestructiveTool
    {
        public const string ConfirmKey = "confirm";
        public const string DryRunKey = "dry_run";

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string Module { get; }
        public abstract bool Undoable { get; }

        /// <summary>Always false: a destructive tool is by definition not read-only.</summary>
        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get { return AugmentSchema(BuildInputSchema()); }
        }

        /// <summary>The tool's own schema, without the confirm/dry_run flags (added by the base).</summary>
        protected abstract JsonNode BuildInputSchema();

        public abstract string Preview(JsonNode input);

        /// <summary>The real operation. Only ever reached with confirm:true and no dry_run.</summary>
        protected abstract JsonNode Apply(JsonNode input);

        public JsonNode Execute(JsonNode input)
        {
            if (input == null)
            {
                input = JsonNode.NewObject();
            }
            bool dryRun = input[DryRunKey].AsBool(false);
            bool confirm = input[ConfirmKey].AsBool(false);
            if (dryRun)
            {
                return UapToolResults.Text(DescribeDryRun(Preview(input)));
            }
            if (!confirm)
            {
                throw new InvalidOperationException(DescribeRefusal(Name, Preview(input)));
            }
            return Apply(input);
        }

        /// <summary>The dry_run reply. Pure and public so the wording is pinned by tests.</summary>
        public static string DescribeDryRun(string preview)
        {
            return "DRY RUN -- nothing was changed. " + (preview ?? string.Empty)
                + " Re-issue with confirm:true (and without dry_run) to apply.";
        }

        /// <summary>The no-confirm refusal. Pure and public so the wording is pinned by tests.</summary>
        public static string DescribeRefusal(string toolName, string preview)
        {
            return "Refused: " + toolName + " is destructive and requires confirm:true. "
                + (preview ?? string.Empty)
                + " Nothing was changed. Re-issue with confirm:true to apply, or dry_run:true to only preview.";
        }

        /// <summary>
        /// Adds the confirm and dry_run boolean properties to
        /// <paramref name="schema"/> (an object schema with a "properties"
        /// object). Returns the same node. Missing/non-object properties
        /// are created rather than failing, so a subclass schema can never
        /// silently ship without the flags.
        /// </summary>
        public static JsonNode AugmentSchema(JsonNode schema)
        {
            if (schema == null || !schema.IsObject)
            {
                schema = JsonNode.NewObject().Set("type", "object");
            }
            JsonNode properties = schema["properties"];
            if (properties == null || !properties.IsObject)
            {
                properties = JsonNode.NewObject();
                schema.Set("properties", properties);
            }
            properties.Set(ConfirmKey, JsonNode.NewObject()
                .Set("type", "boolean")
                .Set("description", "Must be true to actually apply. Without it the call only reports"
                    + " what WOULD change and changes nothing."));
            properties.Set(DryRunKey, JsonNode.NewObject()
                .Set("type", "boolean")
                .Set("description", "Preview only: report what would change and change nothing"
                    + " (wins over confirm)."));
            return schema;
        }
    }
}
