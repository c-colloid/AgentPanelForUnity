using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>
    /// type=system, subtype in {task_started, task_progress, task_updated,
    /// task_notification} -- subagent lifecycle events (R02c, Phase 4
    /// design note section 4). Every field is optional-first: only the
    /// subtype that actually carries it is populated on any given
    /// instance, everything else defaults to empty/zero. Required: none
    /// beyond "subtype" itself, which StreamJsonMessage.FromNode already
    /// checked before dispatching here.
    /// </summary>
    public sealed class SystemTaskEventMessage : StreamJsonMessage
    {
        /// <summary>"task_started" | "task_progress" | "task_updated" | "task_notification".</summary>
        public string Subtype { get; private set; }
        public string TaskId { get; private set; }
        /// <summary>Correlates to the spawning Agent/Task tool_use.id (absent on task_updated).</summary>
        public string ToolUseId { get; private set; }
        /// <summary>task_started: the spawn description. task_progress: current activity line.</summary>
        public string Description { get; private set; }
        public string SubagentType { get; private set; }
        /// <summary>task_notification.status (e.g. "completed").</summary>
        public string Status { get; private set; }
        public string LastToolName { get; private set; }
        /// <summary>task_notification.summary -- Markdown, MODEL-authored/untrusted: render
        /// only through the existing markdown/escaping pipeline, never as raw rich text.</summary>
        public string SummaryMarkdown { get; private set; }
        public string OutputFile { get; private set; }
        public long TotalTokens { get; private set; }
        public int ToolUses { get; private set; }
        public long DurationMs { get; private set; }
        /// <summary>task_updated.patch.status.</summary>
        public string PatchStatus { get; private set; }

        private SystemTaskEventMessage()
        {
            Type = InboundType.SystemTaskEvent;
        }

        /// <summary>Maps a system/task_* line. Never drops (no required field).</summary>
        public static SystemTaskEventMessage FromJson(JsonNode node, Action<string> logger)
        {
            var msg = new SystemTaskEventMessage();
            msg.ReadCommonFields(node);
            msg.Subtype = node["subtype"].AsString(string.Empty);
            msg.TaskId = node["task_id"].AsString(string.Empty);
            msg.ToolUseId = node["tool_use_id"].AsString(string.Empty);
            msg.Description = node["description"].AsString(string.Empty);
            msg.SubagentType = node["subagent_type"].AsString(string.Empty);
            msg.Status = node["status"].AsString(string.Empty);
            msg.LastToolName = node["last_tool_name"].AsString(string.Empty);
            msg.SummaryMarkdown = node["summary"].AsString(string.Empty);
            msg.OutputFile = node["output_file"].AsString(string.Empty);
            JsonNode usage = node["usage"];
            msg.TotalTokens = usage["total_tokens"].AsLong();
            msg.ToolUses = usage["tool_uses"].AsInt();
            msg.DurationMs = usage["duration_ms"].AsLong();
            msg.PatchStatus = node["patch"]["status"].AsString(string.Empty);
            return msg;
        }
    }
}
