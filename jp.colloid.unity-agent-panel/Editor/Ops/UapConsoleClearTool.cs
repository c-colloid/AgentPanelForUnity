using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Clears the Unity Console -- the window's entries, this package's
    /// capture buffer behind uap_console_logs, and the panel's own error
    /// chip -- in one call (design note docs/design-notes/2026-09-21-
    /// console-clear-and-game-view-size.md section 2).
    ///
    /// Why all three at once: they are three views of the same thing, and
    /// leaving any of them behind is worse than not clearing at all. An
    /// agent that cleared only the window would still read the old lines
    /// back through uap_console_logs and conclude its fix had not worked;
    /// the user would still see an error chip for entries the Console no
    /// longer holds.
    ///
    /// NOT ReadOnly, and deliberately not something to reach for by habit:
    /// clearing destroys evidence the user may still need, and
    /// uap_console_logs already has since_id paging, which answers "what is
    /// new since my last look" WITHOUT throwing anything away. The tool
    /// description says so, because the cheapest way to avoid a destructive
    /// habit is to name the alternative at the point of use.
    /// </summary>
    public sealed class UapConsoleClearTool : IUapTool
    {
        public const string ToolName = "uap_console_clear";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Clears the Unity Console: the Console window, the log buffer uap_console_logs"
                    + " reads, and the panel's error chip. Use it to start a run from a clean slate --"
                    + " for example right before uap_play_mode action:start so the next uap_console_logs"
                    + " shows only what this run logged. Prefer since_id on uap_console_logs when you"
                    + " just want what is NEW: that keeps the history the user may still need. Takes no"
                    + " arguments and cannot be undone.";
            }
        }

        public string Module
        {
            get { return "editor"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject())
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            List<UapConsoleLogEntry> buffered = UapConsoleLogStore.Snapshot();
            int bufferedCount = buffered.Count;

            bool consoleCleared = Colloid.AgentPanel.Integration.ConsoleWindowSync.TryClearConsole();
            UapConsoleLogStore.Clear();
            Colloid.AgentPanel.Integration.ConsoleErrorProvider.Clear();

            JsonNode result = JsonNode.NewObject()
                .Set("message", Describe(consoleCleared, bufferedCount))
                .Set("consoleWindowCleared", consoleCleared)
                .Set("bufferedEntriesDiscarded", bufferedCount);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        /// <summary>
        /// Pure: the wording rule. When the Console window itself could not
        /// be cleared (the internal API moved on a future editor), the reply
        /// says which half happened -- reporting a full clear would leave
        /// the agent believing the window is empty when the user can still
        /// see every line.
        /// </summary>
        public static string Describe(bool consoleCleared, int bufferedCount)
        {
            string buffer = "Cleared the agent log buffer (" + bufferedCount + " entr"
                + (bufferedCount == 1 ? "y" : "ies") + ") and the panel's error chip.";
            if (consoleCleared)
            {
                return "Cleared the Unity Console window. " + buffer;
            }
            return "The Unity Console window could NOT be cleared (this editor does not expose the"
                + " internal API for it), so its entries are still on screen. " + buffer;
        }
    }
}
