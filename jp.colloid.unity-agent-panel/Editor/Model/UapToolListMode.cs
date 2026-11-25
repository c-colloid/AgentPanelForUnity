namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Which shape tools/list serves for a spawned agent (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 2.8 B): Full
    /// lists every enabled tool with its full description, exactly as
    /// before this feature existed; Compact lists only the "core" module
    /// directly and reaches every other enabled module through the
    /// uap_tools/uap_call gateway (Ops/UapToolGateway.cs), trading tool-
    /// selection precision for a much smaller per-connection tools/list
    /// payload. Auto resolves per backend at spawn time
    /// (UapToolListModePolicy.ResolveCompact): Full for Claude Code, whose
    /// own ToolSearch already defers loading a tool's full schema until it
    /// is likely needed, and Compact for every other backend, which has no
    /// such client-side deferral and therefore pays the full tools/list
    /// cost on every connection (design section 1.2: an agent without client-side
    /// deferred tool loading pays for every description on every session).
    ///
    /// A plain enum (YAML-safe, serializes as int), same convention as
    /// SubagentCostPolicy/UapAutoApproveLevel -- Auto is a legitimate
    /// persistent default, not a one-time default that must never be
    /// clobbered.
    /// </summary>
    public enum UapToolListMode
    {
        Auto = 0,
        Full = 1,
        Compact = 2
    }
}
