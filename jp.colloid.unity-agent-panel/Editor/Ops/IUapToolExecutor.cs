using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Seam between the pure UapOpsRequestHandler and actual tool
    /// execution, which the production implementation
    /// (UapMainThreadDispatcher) marshals onto the Unity main thread and
    /// bounds with a timeout -- HttpListener worker threads must never call
    /// an IUapTool directly (design section 1: "HttpListener threads must
    /// never touch Unity APIs directly"). Implementations MAY block the
    /// calling thread; MAY throw (the handler catches it and reports
    /// isError:true).
    /// </summary>
    public interface IUapToolExecutor
    {
        JsonNode Execute(IUapTool tool, JsonNode input);
    }

    /// <summary>
    /// Runs the tool synchronously on the calling thread -- correct only
    /// when the caller is already the Unity main thread (tests; a future
    /// same-thread transport). Production HTTP dispatch must use
    /// UapMainThreadDispatcher instead.
    /// </summary>
    public sealed class DirectUapToolExecutor : IUapToolExecutor
    {
        public JsonNode Execute(IUapTool tool, JsonNode input)
        {
            return tool.Execute(input);
        }
    }
}
