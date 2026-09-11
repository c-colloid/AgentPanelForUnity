using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Transport-agnostic Unity-operations tool (Phase 5a design section
    /// 8.6 ADR: UapOps keeps a plain in-process registry/dispatcher so the
    /// MCP front end -- currently the only transport -- can be replaced
    /// without touching tool implementations).
    ///
    /// <see cref="Execute"/> is ALWAYS called on the Unity main thread: the
    /// HTTP layer marshals every tools/call through
    /// <see cref="UapMainThreadDispatcher"/> before ever invoking a tool, so
    /// implementations never need their own thread-safety. The one
    /// exception is a tool that opts into <see cref="IUapOffThreadTool"/>
    /// (uap_job_status), which the dispatcher runs on the HTTP worker and
    /// which therefore must not touch Unity at all. Throwing from
    /// Execute is the sanctioned way to report a tool-level failure -- the
    /// caller (UapOpsRequestHandler) catches it and reports isError:true to
    /// the CLI instead of ever letting an exception escape into the editor
    /// (design section 1.3).
    /// </summary>
    public interface IUapTool
    {
        /// <summary>
        /// Wire name (e.g. "uap_ping"), short and prefixed per design
        /// section 7.1 so ToolSearch/the permission card display a
        /// recognizable name. Must be unique within the registry.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// One-line description with the most search-relevant verb/noun
        /// FIRST (design section 1.4 -- ToolSearch indexes this text, not
        /// just the name).
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Tool module (e.g. "core") used for the tools/list enable/disable
        /// filter (design section 7.1). Tool module composition -- which
        /// modules exist and which are on by default -- lives in
        /// ToolRegistry/PanelSettings, not here.
        /// </summary>
        string Module { get; }

        /// <summary>
        /// True when the tool's mutation is covered by a Unity Undo
        /// operation (design section 1.3/8.2 B2). Surfaced to the
        /// permission card and the turn-undo-group warning; purely
        /// descriptive metadata -- this interface never performs Undo
        /// bookkeeping itself.
        /// </summary>
        bool Undoable { get; }

        /// <summary>
        /// True when this tool NEVER mutates the project/scene -- it only
        /// queries/inspects/finds/gets/captures (2026-08-02 design note
        /// section 2 B2). Default false everywhere except the small,
        /// individually-verified read-only set (query_hierarchy,
        /// query_component_types, component_list, object_inspect,
        /// asset_find, prefab_get_overrides, editor_screenshot -- see
        /// UapReadOnlyToolMetadataTests for the exact pinned set). Consumed
        /// by AgentHub to auto-approve a can_use_tool request for this tool
        /// (gated on PanelSettings.autoApproveReadOnlyOps) WITHOUT ever
        /// showing a permission card. This is the ONLY thing that makes
        /// auto-approval safe: a tool that writes ANYTHING -- even to a
        /// scratch/Temp location -- MUST return false here, and a new tool
        /// must default to false unless verified otherwise.
        /// </summary>
        bool ReadOnly { get; }

        /// <summary>JSON Schema (as a JsonNode object) describing the tools/call "arguments" shape.</summary>
        JsonNode InputSchema { get; }

        /// <summary>
        /// Executes the tool and returns the MCP "content" array (a
        /// JsonNode array of content blocks, e.g.
        /// [{"type":"text","text":"..."}]) -- NOT the full tools/call result
        /// envelope; the caller wraps it with "isError":false. Throw to
        /// signal failure instead of returning an error content block
        /// yourself. <paramref name="input"/> is the "arguments" object
        /// from the request (never null -- an absent arguments field is
        /// normalized to an empty object by the caller).
        /// </summary>
        JsonNode Execute(JsonNode input);
    }
}
