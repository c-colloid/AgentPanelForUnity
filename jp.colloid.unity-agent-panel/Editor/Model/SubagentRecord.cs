using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Display record for one subagent spawn (an Agent/Task tool_use whose
    /// input carries "subagent_type"), nested inside the spawning
    /// ToolCallRecord (Phase 4 design note section 3). Unity-serializable.
    /// </summary>
    [Serializable]
    public class SubagentRecord
    {
        /// <summary>Nested-block cap: older blocks are dropped once this many
        /// have accumulated (design note section 3). Depth-1 only -- a tool
        /// call inside a subagent never gets its own nested SubagentRecord,
        /// so this list never contains a block whose toolCall.subagent is
        /// itself non-null.</summary>
        public const int MaxNestedBlocks = 120;

        /// <summary>task_started.task_id, or empty until it arrives.</summary>
        public string taskId = string.Empty;
        /// <summary>Correlates back to the spawning Agent/Task tool_use.id (the
        /// ToolCallRecord this SubagentRecord lives on carries the same id).</summary>
        public string toolUseId = string.Empty;
        /// <summary>e.g. "general-purpose".</summary>
        public string subagentType = string.Empty;
        public string description = string.Empty;
        /// <summary>
        /// True when the spawn's input carried run_in_background:true
        /// (docs/design-notes/2026-09-27-background-subagent-card.md). A
        /// background spawn's top-level tool_result arrives IMMEDIATELY
        /// ("launched"), long before the subagent finishes, so it is not the
        /// final authority on status the way a foreground result is: the
        /// record stays open (and keeps receiving parent-tagged messages
        /// and task_* events, across turn boundaries) until a terminal
        /// task_updated/task_notification arrives.
        /// </summary>
        public bool background;
        /// <summary>"running" | "completed" | "failed" | "stopped".</summary>
        public string status = "running";
        /// <summary>task_progress.description: the current one-line activity.</summary>
        public string progressLine = string.Empty;
        public string lastToolName = string.Empty;
        public long totalTokens;
        public int toolUses;
        public long durationMs;
        /// <summary>task_notification.summary -- Markdown, MODEL-authored/untrusted:
        /// render only through the existing markdown pipeline, never raw rich text.</summary>
        public string summaryMarkdown = string.Empty;
        /// <summary>Nested transcript: text/thinking/tool blocks from inside the subagent.</summary>
        public List<ChatMessageBlock> blocks = new List<ChatMessageBlock>();
        /// <summary>Count of older blocks dropped once <see cref="MaxNestedBlocks"/> was exceeded.</summary>
        public int droppedBlockCount;

        /// <summary>Appends a nested block, dropping the oldest once the cap is hit.</summary>
        public void AddBlock(ChatMessageBlock block)
        {
            if (block == null)
            {
                return;
            }
            if (blocks.Count >= MaxNestedBlocks)
            {
                blocks.RemoveAt(0);
                droppedBlockCount++;
            }
            blocks.Add(block);
        }
    }
}
