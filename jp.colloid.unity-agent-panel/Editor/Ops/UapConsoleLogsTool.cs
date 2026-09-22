using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reads the Unity Console lines captured since the last domain reload
    /// (design note docs/design-notes/2026-09-21-play-mode-and-console-log-
    /// tools.md). The gap this closes, measured 2026-09-21: an agent driving
    /// Play Mode, a menu item or a third-party importer could see WHAT it
    /// ran but never what Unity said about it -- the panel surfaces console
    /// ERRORS to the user (the context-bar chip, and the compile digest
    /// carried across a reload), yet the agent had no way to read a warning,
    /// a Debug.Log from the game it just started, or the errors of a turn it
    /// was not part of. Without this the only route was `uloop get-logs`,
    /// which is why a project that wanted nothing else from uLoop still had
    /// to install it.
    ///
    /// ReadOnly: it reads a buffer this package fills from
    /// Application.logMessageReceivedThreaded and touches nothing else, so
    /// it is safe to auto-approve (UapReadOnlyToolMetadataTests pins it).
    ///
    /// Deliberately NOT the Console window's own entry list: see
    /// UapConsoleLogBuffer's class comment for why reflection into
    /// UnityEditor.LogEntries is the worse bet. The practical differences
    /// are stated in the tool description rather than hidden: the buffer
    /// starts at the last domain reload, keeps the newest
    /// <see cref="UapConsoleLogStore.Capacity"/> lines, and does not follow
    /// the user pressing Clear.
    /// </summary>
    public sealed class UapConsoleLogsTool : IUapTool
    {
        public const string ToolName = "uap_console_logs";

        /// <summary>Entries returned when the caller does not say.</summary>
        public const int DefaultCount = UapConsoleLogStore.DefaultSelectionCount;

        /// <summary>Upper bound for 'count' -- the whole buffer, at most.</summary>
        public const int MaxCount = UapConsoleLogStore.Capacity;

        /// <summary>The type names 'types' accepts, in severity order.</summary>
        public static readonly string[] KnownTypes = { "exception", "error", "assert", "warning", "log" };

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Reads Unity Console output (errors, exceptions, warnings, Debug.Log) captured since"
                    + " the last domain reload -- use it after uap_play_mode to see what the running game"
                    + " logged, after uap_editor_execute_menu to see what an importer or SDK reported, and"
                    + " whenever a call \"succeeded\" but the result looks wrong. Filter with types"
                    + " (exception/error/assert/warning/log), contains, and count (default " + DefaultCount
                    + ", max " + MaxCount + "); identical lines are collapsed with an occurrence count unless"
                    + " collapse:false. Poll during Play Mode by passing the previous reply's lastId as"
                    + " since_id to get only what is new. Stack traces are omitted unless"
                    + " include_stack_trace:true. Not a view of the Console window: it holds the newest "
                    + UapConsoleLogStore.Capacity + " lines of this domain, is emptied by any compile or"
                    + " Play Mode transition, and ignores the user pressing Clear.";
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
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("types", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "string")
                                .Set("enum", BuildTypeEnum()))
                            .Set("description", "Log types to return: exception, error, assert, warning, log."
                                + " Omit for every type; [\"error\",\"exception\"] is the usual 'what broke' query."))
                        .Set("count", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "How many of the NEWEST matching entries to return (default "
                                + DefaultCount + ", max " + MaxCount + ")."))
                        .Set("contains", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Only entries whose message contains this text (case-insensitive)."))
                        .Set("since_id", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Only entries newer than this id. Pass the lastId of the previous"
                                + " reply to poll for what has been logged since, without re-reading it all."))
                        .Set("include_stack_trace", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Include the stack trace of errors, exceptions and asserts"
                                + " (default false -- they are long, and the message usually identifies the cause)."))
                        .Set("collapse", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Fold identical lines into one entry with an occurrence count"
                                + " (default true). Pass false to see each repetition in order.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            var selection = new UapConsoleLogSelection
            {
                Types = ReadTypes(input["types"]),
                Contains = input["contains"].AsString(null),
                SinceId = input["since_id"].AsInt(0),
                Count = ClampCount(input["count"].AsInt(DefaultCount)),
                Collapse = input["collapse"].AsBool(true)
            };
            bool includeStackTrace = input["include_stack_trace"].AsBool(false);

            List<UapConsoleLogEntry> all = UapConsoleLogStore.Snapshot();
            List<UapConsoleLogEntry> selected = UapConsoleLogStore.Select(all, selection);

            JsonNode entries = JsonNode.NewArray();
            for (int i = 0; i < selected.Count; i++)
            {
                UapConsoleLogEntry entry = selected[i];
                JsonNode node = JsonNode.NewObject()
                    .Set("id", entry.Id)
                    .Set("type", entry.Type)
                    .Set("message", entry.Message);
                if (entry.Occurrences > 1)
                {
                    node.Set("occurrences", entry.Occurrences);
                }
                if (includeStackTrace && !string.IsNullOrEmpty(entry.StackTrace))
                {
                    node.Set("stackTrace", entry.StackTrace);
                }
                entries.Add(node);
            }

            JsonNode counts = JsonNode.NewObject();
            Dictionary<string, int> byType = UapConsoleLogStore.CountByType(all);
            for (int i = 0; i < KnownTypes.Length; i++)
            {
                int value;
                byType.TryGetValue(KnownTypes[i], out value);
                counts.Set(KnownTypes[i], value);
            }

            JsonNode result = JsonNode.NewObject()
                .Set("returned", selected.Count)
                .Set("entries", entries)
                .Set("capturedInBuffer", all.Count)
                .Set("countsInBuffer", counts)
                .Set("lastId", UapConsoleLogStore.LastId(all))
                .Set("capacity", UapConsoleLogStore.Capacity)
                .Set("droppedOldest", UapConsoleLogStore.Dropped)
                .Set("capturing", UapConsoleLogStore.Capturing);
            string note = Note(all.Count, selected.Count, UapConsoleLogStore.Dropped,
                UapConsoleLogStore.Capturing);
            if (!string.IsNullOrEmpty(note))
            {
                result.Set("note", note);
            }
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        /// <summary>Pure: the wording rule for the reply's 'note', or empty when the plain numbers say enough.</summary>
        public static string Note(int captured, int returned, int dropped, bool capturing)
        {
            if (!capturing)
            {
                return "Console capture is not running (UapOps is off, or the Editor is between domain"
                    + " reloads), so this is whatever was captured before it stopped.";
            }
            if (captured == 0)
            {
                return "Nothing has been logged since the last domain reload (a compile, or entering or"
                    + " leaving Play Mode, empties this buffer). It is not a view of the Console window,"
                    + " so lines from before that reload are gone even if the user can still see them.";
            }
            if (returned == 0)
            {
                return "No entry matched the filter; " + captured + " line(s) are buffered. Widen 'types',"
                    + " drop 'contains', or raise 'since_id' less.";
            }
            if (dropped > 0)
            {
                return dropped + " older line(s) fell out of the " + UapConsoleLogStore.Capacity
                    + "-entry buffer. Read logs sooner, or filter with 'types' to keep what matters.";
            }
            return string.Empty;
        }

        /// <summary>Pure: 'count' clamped to 1..<see cref="MaxCount"/>, with 0/absent meaning the default.</summary>
        public static int ClampCount(int requested)
        {
            if (requested <= 0)
            {
                return DefaultCount;
            }
            return requested > MaxCount ? MaxCount : requested;
        }

        /// <summary>
        /// Pure: validates and returns the requested type names. Refuses an
        /// unknown name rather than silently returning everything -- a
        /// typo'd "errors" that quietly widened the answer to every Debug.Log
        /// would be read as "there were no errors".
        /// </summary>
        public static List<string> ReadTypes(JsonNode node)
        {
            var result = new List<string>();
            if (node == null || !node.IsArray)
            {
                return result;
            }
            foreach (JsonNode item in node.Items)
            {
                string value = item.AsString(null);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                string canonical = Canonical(value);
                if (canonical == null)
                {
                    throw new ArgumentException("Unknown log type '" + value + "'. Valid types: "
                        + string.Join(", ", KnownTypes) + ".");
                }
                if (!result.Contains(canonical))
                {
                    result.Add(canonical);
                }
            }
            return result;
        }

        private static string Canonical(string value)
        {
            for (int i = 0; i < KnownTypes.Length; i++)
            {
                if (string.Equals(KnownTypes[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return KnownTypes[i];
                }
            }
            return null;
        }

        private static JsonNode BuildTypeEnum()
        {
            JsonNode values = JsonNode.NewArray();
            for (int i = 0; i < KnownTypes.Length; i++)
            {
                values.Add(KnownTypes[i]);
            }
            return values;
        }
    }
}
