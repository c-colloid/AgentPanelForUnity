using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Pure diff builder behind the Edit/MultiEdit approval preview
    /// (UXA-2; ux-spec section 3.7 "Edit tools need a diff preview").
    /// Unity-API-free like PermissionCardLayout/ToolCardDescriber so the
    /// line classification is directly unit-testable; PermissionCard owns
    /// the rendering (colors, truncation, the show-all expander).
    ///
    /// The diff is deliberately the SIMPLE prefix/suffix split, not an
    /// LCS: an Edit's old_string/new_string pair is already a localized
    /// snippet, so "common head = context, common tail = context,
    /// everything between = removed then added" reads exactly like the
    /// unified diffs reviewers know, with no risk of a clever-but-wrong
    /// alignment misrepresenting what gets approved. Context runs longer
    /// than <see cref="ContextLines"/> on either side are elided with a
    /// gap marker so the actual change stays inside the card's visible
    /// line budget.
    /// </summary>
    public static class PermissionEditPreview
    {
        /// <summary>Kept context lines adjacent to the changed span, per side.</summary>
        public const int ContextLines = 2;

        /// <summary>Gap marker text for elided context (diff convention).</summary>
        public const string GapMarker = "...";

        public enum LineKind
        {
            Context,
            Remove,
            Add,
            /// <summary>Elided-context marker line ("...").</summary>
            Gap,
            /// <summary>Separator between MultiEdit edits.</summary>
            Separator
        }

        public struct DiffLine
        {
            public LineKind Kind;
            public string Text;
        }

        /// <summary>
        /// The tools whose input carries old_string/new_string (or a
        /// MultiEdit edits[] of them). Matches ToolCardDescriber.Is's
        /// case-insensitive comparison.
        /// </summary>
        public static bool IsEditTool(string toolName)
        {
            return Is(toolName, "Edit") || Is(toolName, "MultiEdit");
        }

        /// <summary>
        /// Builds the typed diff lines for one Edit input
        /// ({old_string, new_string}) or a MultiEdit input
        /// ({edits: [{old_string, new_string}, ...]}). Returns an EMPTY
        /// list when the input has neither shape -- the caller falls back
        /// to the generic key: value preview, so a malformed or future
        /// input variant is never rendered as a wrong diff.
        /// </summary>
        public static List<DiffLine> BuildEditDiff(JsonNode input)
        {
            var lines = new List<DiffLine>();
            if (input == null || !input.IsObject)
            {
                return lines;
            }

            JsonNode edits = input["edits"];
            if (edits.IsArray)
            {
                for (int i = 0; i < edits.Count; i++)
                {
                    JsonNode edit = edits[i];
                    if (!edit.IsObject)
                    {
                        continue;
                    }
                    if (i > 0 && lines.Count > 0)
                    {
                        lines.Add(new DiffLine { Kind = LineKind.Separator, Text = string.Empty });
                    }
                    AppendOneEdit(lines, edit);
                }
                return lines;
            }

            AppendOneEdit(lines, input);
            return lines;
        }

        private static void AppendOneEdit(List<DiffLine> lines, JsonNode edit)
        {
            string oldText = edit["old_string"].AsString(null);
            string newText = edit["new_string"].AsString(null);
            if (oldText == null && newText == null)
            {
                return;
            }
            string[] oldLines = SplitLines(oldText ?? string.Empty);
            string[] newLines = SplitLines(newText ?? string.Empty);

            // Common head/tail (the tail never overlaps the head).
            int prefix = 0;
            int maxPrefix = System.Math.Min(oldLines.Length, newLines.Length);
            while (prefix < maxPrefix
                && string.Equals(oldLines[prefix], newLines[prefix], System.StringComparison.Ordinal))
            {
                prefix++;
            }
            int suffix = 0;
            int maxSuffix = System.Math.Min(oldLines.Length, newLines.Length) - prefix;
            while (suffix < maxSuffix
                && string.Equals(oldLines[oldLines.Length - 1 - suffix],
                    newLines[newLines.Length - 1 - suffix], System.StringComparison.Ordinal))
            {
                suffix++;
            }

            // Head context, elided beyond ContextLines (keep the lines
            // CLOSEST to the change).
            if (prefix > ContextLines)
            {
                lines.Add(new DiffLine { Kind = LineKind.Gap, Text = GapMarker });
            }
            for (int i = System.Math.Max(0, prefix - ContextLines); i < prefix; i++)
            {
                lines.Add(new DiffLine { Kind = LineKind.Context, Text = oldLines[i] });
            }

            for (int i = prefix; i < oldLines.Length - suffix; i++)
            {
                lines.Add(new DiffLine { Kind = LineKind.Remove, Text = oldLines[i] });
            }
            for (int i = prefix; i < newLines.Length - suffix; i++)
            {
                lines.Add(new DiffLine { Kind = LineKind.Add, Text = newLines[i] });
            }

            // Tail context (the lines closest to the change), elided beyond
            // ContextLines.
            int tailShown = System.Math.Min(suffix, ContextLines);
            for (int i = 0; i < tailShown; i++)
            {
                lines.Add(new DiffLine
                {
                    Kind = LineKind.Context,
                    Text = oldLines[oldLines.Length - suffix + i]
                });
            }
            if (suffix > ContextLines)
            {
                lines.Add(new DiffLine { Kind = LineKind.Gap, Text = GapMarker });
            }
        }

        private static string[] SplitLines(string text)
        {
            // Normalize CRLF so a Windows-authored old_string diffs cleanly
            // against an LF new_string (and vice versa) -- the comparison
            // is per LINE, and a trailing '\r' is invisible in the card.
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static bool Is(string toolName, string candidate)
        {
            return string.Equals(toolName, candidate, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
