using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Opt-in extension for a tool whose EXECUTING body must span more than
    /// one Unity main-thread tick without ever blocking it -- currently only
    /// uap_scripts_commit (design section 7.4/8.1 P1: AssemblyBuilder
    /// compiles asynchronously; busy-waiting for it inside a single
    /// IUapTool.Execute call would freeze the editor for the whole compile).
    /// UapMainThreadDispatcher checks for this interface BEFORE falling back
    /// to the synchronous IUapTool.Execute path (see its Pump()); a tool
    /// implementing it still MUST implement IUapTool.Execute too (for
    /// DirectUapToolExecutor / any caller that bypasses the dispatcher), but
    /// production HTTP dispatch never calls it once this interface is
    /// present.
    /// </summary>
    public interface IUapPollableTool : IUapTool
    {
        /// <summary>
        /// Called once per main-thread tick until it returns true.
        /// <paramref name="state"/> is opaque dispatcher-owned storage:
        /// null on the FIRST call for a given tool invocation, and whatever
        /// this method last wrote to it on every subsequent call for the
        /// SAME invocation -- use it to carry the in-progress compile
        /// handle/accumulated messages across ticks. Must never block
        /// (no Thread.Sleep/spin-wait): a "not done yet" tick returns false
        /// immediately so the dispatcher can return control to
        /// EditorApplication.update. Throwing propagates to the caller
        /// exactly like IUapTool.Execute throwing.
        /// </summary>
        bool Poll(JsonNode input, ref object state, out JsonNode result);
    }
}
