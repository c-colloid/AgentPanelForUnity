using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Pure builder behind the tool card's file-change view (design note
    /// 2026-09-13-toolcard-vertex-limit.md section 4): a completed
    /// Write/Edit/MultiEdit card shows WHAT changed in the file as +/-
    /// lines instead of the raw input JSON with its escaped "\n" content.
    ///
    /// Edit/MultiEdit reuse PermissionEditPreview's prefix/suffix diff (the
    /// same lines the approval card showed, so the two views agree). Write
    /// has no "before" text -- the CLI overwrites the whole file -- so its
    /// content renders as one all-added block, which is exactly the
    /// unified diff of a new file. Unity-API-free; ToolActivityCard owns
    /// the rendering.
    /// </summary>
    public static class ToolCardFileDiff
    {
        /// <summary>Write plus the Edit family: the tools whose input is a file change.</summary>
        public static bool IsFileChangeTool(string toolName)
        {
            return Is(toolName, "Write") || PermissionEditPreview.IsEditTool(toolName);
        }

        /// <summary>
        /// The file the change targets, or null when the input carries no
        /// path (the caller then omits the path row, never the diff).
        /// </summary>
        public static string FilePathOf(JsonNode input)
        {
            if (input == null || !input.IsObject)
            {
                return null;
            }
            string path = input["file_path"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                path = input["path"].AsString(null);
            }
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// The raw text a Copy button should hand to the clipboard: the
        /// written content for Write, null for the Edit family (their
        /// old/new snippets are not a file).
        /// </summary>
        public static string CopyTextOf(string toolName, JsonNode input)
        {
            if (!Is(toolName, "Write") || input == null || !input.IsObject)
            {
                return null;
            }
            return input["content"].AsString(null);
        }

        /// <summary>
        /// Diff lines for the tool's input, or an EMPTY list when the tool
        /// is not a file change or its input lacks the expected shape --
        /// the caller then falls back to the raw input section, never to a
        /// wrong diff.
        /// </summary>
        public static List<PermissionEditPreview.DiffLine> BuildLines(string toolName, JsonNode input)
        {
            var lines = new List<PermissionEditPreview.DiffLine>();
            if (input == null || !input.IsObject)
            {
                return lines;
            }
            if (PermissionEditPreview.IsEditTool(toolName))
            {
                return PermissionEditPreview.BuildEditDiff(input);
            }
            if (!Is(toolName, "Write"))
            {
                return lines;
            }
            string content = input["content"].AsString(null);
            if (content == null)
            {
                return lines;
            }
            string[] split = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = split.Length;
            // A trailing newline is not an extra empty line of the file.
            if (count > 1 && split[count - 1].Length == 0)
            {
                count--;
            }
            for (int i = 0; i < count; i++)
            {
                lines.Add(new PermissionEditPreview.DiffLine
                {
                    Kind = PermissionEditPreview.LineKind.Add,
                    Text = split[i]
                });
            }
            return lines;
        }

        /// <summary>Same overload shape as ToolCardDescriber.Describe: unparseable JSON yields no diff.</summary>
        public static List<PermissionEditPreview.DiffLine> BuildLines(string toolName, string inputJson)
        {
            JsonNode node;
            if (!TryParse(inputJson, out node))
            {
                return new List<PermissionEditPreview.DiffLine>();
            }
            return BuildLines(toolName, node);
        }

        /// <summary>Parses a tool input JSON object; false for empty/invalid/non-object text.</summary>
        public static bool TryParse(string inputJson, out JsonNode node)
        {
            node = null;
            if (string.IsNullOrEmpty(inputJson))
            {
                return false;
            }
            string error;
            return JsonParser.TryParse(inputJson, out node, out error) && node != null && node.IsObject;
        }

        private static bool Is(string toolName, string candidate)
        {
            return string.Equals(toolName, candidate, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
