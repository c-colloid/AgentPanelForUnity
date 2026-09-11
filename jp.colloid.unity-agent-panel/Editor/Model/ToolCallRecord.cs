using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>Lifecycle of one tool call shown as an activity card.</summary>
    [Serializable]
    public enum ToolCallStatus
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        Denied
    }

    /// <summary>
    /// Display record for one tool_use -&gt; tool_result round trip (the
    /// source data for Phase 2's ToolActivityCard). Unity-serializable.
    /// </summary>
    [Serializable]
    public class ToolCallRecord
    {
        /// <summary>tool_use block id ("toolu_..."), correlates the result.</summary>
        public string toolUseId = string.Empty;
        /// <summary>Tool name, e.g. "Bash", "Edit".</summary>
        public string toolName = string.Empty;
        /// <summary>One-line human summary of the input (ToolCardDescriber refines later).</summary>
        public string inputSummary = string.Empty;
        /// <summary>Raw single-line JSON of the input object (detail view).</summary>
        public string inputJson = string.Empty;
        public ToolCallStatus status = ToolCallStatus.Pending;
        /// <summary>UTC ticks when the tool_use was observed.</summary>
        public long startedAtUtcTicks;
        /// <summary>Milliseconds from start to result (0 while running).</summary>
        public long durationMs;
        /// <summary>Short summary of the result content.</summary>
        public string resultSummary = string.Empty;
        /// <summary>tool_result is_error flag.</summary>
        public bool isError;
        /// <summary>Non-null when this tool_use spawned a subagent (Agent/Task
        /// tool with a "subagent_type" input) -- null-object pattern for every
        /// ordinary tool call. See docs/design-notes/2026-07-31-subagent-display.md.</summary>
        public SubagentRecord subagent;

        /// <summary>Marks completion from a tool_result and computes the duration.</summary>
        public void Complete(bool error, string summary, long nowUtcTicks)
        {
            isError = error;
            status = error ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
            resultSummary = summary ?? string.Empty;
            if (startedAtUtcTicks > 0 && nowUtcTicks > startedAtUtcTicks)
            {
                durationMs = (nowUtcTicks - startedAtUtcTicks) / TimeSpan.TicksPerMillisecond;
            }
        }
    }
}
