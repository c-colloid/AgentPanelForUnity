namespace Colloid.AgentPanel.Core.Client
{
    /// <summary>
    /// AgentClient lifecycle states (ARCHITECTURE.md 3.4):
    /// NotStarted -&gt; Starting (spawn + initialize pending) -&gt; Ready
    /// Ready --user send--&gt; Streaming --tool_use--&gt; ToolRunning
    ///   --can_use_tool--&gt; WaitingPermission
    /// Streaming/ToolRunning/WaitingPermission --result--&gt; Ready
    /// any --process exit--&gt; Errored --resume restart--&gt; Starting
    /// </summary>
    public enum AgentClientState
    {
        NotStarted,
        Starting,
        Ready,
        Streaming,
        ToolRunning,
        WaitingPermission,
        Errored
    }

    /// <summary>
    /// Transition validation for AgentClientState. The stream is the source
    /// of truth, so AgentClient applies even "invalid" transitions after
    /// logging them; this table exists to surface protocol surprises in the
    /// log rather than to fight them.
    /// </summary>
    public static class AgentClientStateTransitions
    {
        public static bool IsValid(AgentClientState from, AgentClientState to)
        {
            if (from == to)
            {
                return true;
            }
            // Universal edges: any state can error out (process death) and
            // any state can be torn down to NotStarted (Stop()).
            if (to == AgentClientState.Errored || to == AgentClientState.NotStarted)
            {
                return true;
            }
            switch (from)
            {
                case AgentClientState.NotStarted:
                    return to == AgentClientState.Starting;
                case AgentClientState.Starting:
                    return to == AgentClientState.Ready;
                case AgentClientState.Ready:
                    return to == AgentClientState.Streaming;
                case AgentClientState.Streaming:
                    return to == AgentClientState.ToolRunning
                        || to == AgentClientState.WaitingPermission
                        || to == AgentClientState.Ready;
                case AgentClientState.ToolRunning:
                    return to == AgentClientState.Streaming
                        || to == AgentClientState.WaitingPermission
                        || to == AgentClientState.Ready;
                case AgentClientState.WaitingPermission:
                    return to == AgentClientState.ToolRunning
                        || to == AgentClientState.Streaming
                        || to == AgentClientState.Ready;
                case AgentClientState.Errored:
                    return to == AgentClientState.Starting;
                default:
                    return false;
            }
        }
    }
}
