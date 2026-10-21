namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Marker for a tool that must NEVER be silently allowed by ANY
    /// auto-approve level, including UapAutoApproveLevel.AllUnityOps and
    /// AllTools (2026-08-01 design note docs/design-notes/2026-08-01-
    /// phase5-unity-ops-design.md section 8: "never auto-approve dynamic
    /// code", carried forward by docs/design-notes/2026-09-28-uloop-
    /// phase3.md for uap_execute_code).
    ///
    /// Without this, AutoApprovePolicy.ShouldAutoApprove has no way to
    /// except a single tool from the coarse ReadOnly/Undoable/AllUnityOps
    /// ladder: AllUnityOps approves EVERY registered UapOps tool regardless
    /// of ReadOnly/Undoable, and AllTools approves every tool at all
    /// (including non-UapOps ones) -- a user who ever raises their
    /// auto-approve level that far would otherwise stop seeing a
    /// permission card for arbitrary C# execution, which is exactly the
    /// hazard uLoop's execute-dynamic-code was singled out for in the
    /// referenced design note. Implementing this interface is the ONLY
    /// thing that changes: AgentHub.TryAutoApproveUapOpsTool resolves the
    /// effective tool (uap_call unwrapping included, same resolution the
    /// permission card's own undo badge uses) and passes
    /// `resolved is IUapNeverAutoApproveTool` into
    /// AutoApprovePolicy.ShouldAutoApprove's neverAutoApprove parameter,
    /// which short-circuits to false before the level switch runs at all --
    /// no other tool's behaviour changes.
    ///
    /// A plain marker (no members) rather than a bool on IUapTool itself:
    /// adding a member to IUapTool would force every existing IUapTool
    /// implementation in this codebase to be touched for a flag every one
    /// of them would answer false to; an interface a tool opts INTO needs
    /// no such blast radius.
    /// </summary>
    public interface IUapNeverAutoApproveTool : IUapTool
    {
    }
}
