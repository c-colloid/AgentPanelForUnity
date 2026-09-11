using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// A tool whose effect Ctrl+Z cannot take back, or that writes through
    /// to a shared asset every other user of it will see (design note
    /// 2026-09-09-jobs-and-destructive-confirm section 2; UnityMCP's
    /// Destructive + confirm:true). Such a tool refuses to act unless the
    /// call carries confirm:true, and instead reports what WOULD change;
    /// dry_run:true asks for that report explicitly. The agent thereby has
    /// to have seen the blast radius (how many overrides, how many other
    /// instances, how many assets in the folder) before committing to it.
    /// Implement via <see cref="UapDestructiveToolBase"/>, which owns the
    /// gate; this interface exists so metadata tests and the permission
    /// surface can pin the set.
    /// </summary>
    public interface IUapDestructiveTool : IUapTool
    {
        /// <summary>
        /// One or two sentences saying exactly what Execute would change
        /// for <paramref name="input"/>, resolving targets the same way the
        /// real operation does (so an unresolvable target throws the same
        /// error here). MUST NOT mutate anything.
        /// </summary>
        string Preview(JsonNode input);
    }
}
