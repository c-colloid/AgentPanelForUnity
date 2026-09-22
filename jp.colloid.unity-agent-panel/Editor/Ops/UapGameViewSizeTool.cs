using System;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reads and sets the Game View's resolution (design note
    /// docs/design-notes/2026-09-21-console-clear-and-game-view-size.md
    /// section 3). It exists for one workflow: "show me what this looks like
    /// at 1080p / on a phone aspect" -- uap_editor_screenshot captures the
    /// Game View at whatever size it happens to be, and until now the only
    /// way to change that was to ask the user to drag a window or pick from
    /// a dropdown.
    ///
    /// The editor internals this needs are reached through
    /// <see cref="UapGameViewSizeAccess"/>, which reads the size back after
    /// writing it, so a version where the reflection stops working reports a
    /// failure rather than a resize that never happened.
    ///
    /// action "get" answers even when no Game View is open (open:false)
    /// rather than failing: "is one open?" is a legitimate question and the
    /// batch-mode / test-runner answer is simply no. action "set" in that
    /// state IS a failure -- the request cannot be honoured.
    /// </summary>
    public sealed class UapGameViewSizeTool : IUapTool
    {
        public const string ToolName = "uap_game_view_size";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Gets or sets the Unity Game View resolution, so a screenshot can be taken at a"
                    + " known size instead of whatever the window happens to be -- pair it with"
                    + " uap_editor_screenshot capture:\"game\" to show the user 1920x1080, a phone"
                    + " aspect, or any fixed size. action: get (default) / set with width and height in"
                    + " pixels (" + UapGameViewSizePolicy.MinDimension + " to "
                    + UapGameViewSizePolicy.MaxDimension + "). Setting a size that is not in the Game"
                    + " View's dropdown adds it there, labelled as this panel's, and reuses that entry"
                    + " next time. Requires an open Game View window (Window > General > Game); 'get'"
                    + " reports open:false instead of failing when there is none.";
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
                    .Set("properties", JsonNode.NewObject()
                        .Set("action", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("get").Add("set"))
                            .Set("description", "'get' (the default) reports the current size;"
                                + " 'set' requires width and height."))
                        .Set("width", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Width in pixels for action 'set'."))
                        .Set("height", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Height in pixels for action 'set'.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string action = (input["action"].AsString("get") ?? "get").Trim();
            if (string.Equals(action, "get", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteGet();
            }
            if (string.Equals(action, "set", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteSet(input["width"].AsInt(0), input["height"].AsInt(0));
            }
            throw new ArgumentException("Unknown action '" + action + "'. Valid actions: get, set.");
        }

        private static JsonNode ExecuteGet()
        {
            UapGameViewSizeInfo info;
            string error;
            bool ok = UapGameViewSizeAccess.TryGetSize(out info, out error);
            JsonNode result = JsonNode.NewObject()
                .Set("open", ok)
                .Set("message", ok ? "Game View size: " + DescribeSize(info) + "." : error);
            if (ok)
            {
                AddSizeFields(result, info);
            }
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static JsonNode ExecuteSet(int width, int height)
        {
            string validationError;
            if (!UapGameViewSizePolicy.ValidateDimensions(width, height, out validationError))
            {
                throw new ArgumentException(validationError);
            }

            UapGameViewSizeInfo info;
            string error;
            if (!UapGameViewSizeAccess.TrySetSize(width, height, out info, out error))
            {
                throw new InvalidOperationException(error);
            }

            JsonNode result = JsonNode.NewObject()
                .Set("open", true)
                .Set("message", "Game View set to " + DescribeSize(info) + ".");
            AddSizeFields(result, info);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static void AddSizeFields(JsonNode result, UapGameViewSizeInfo info)
        {
            result.Set("width", info.Width)
                .Set("height", info.Height)
                .Set("sizeType", info.SizeType ?? string.Empty)
                .Set("label", info.DisplayText ?? string.Empty)
                // The window's own size in points: what uap_editor_screenshot
                // capture:"window" grabs, and the number a person compares
                // against when the render resolution looks wrong.
                .Set("windowWidth", info.WindowWidth)
                .Set("windowHeight", info.WindowHeight);
        }

        /// <summary>Pure: the one-line shape "1920 x 1080 (Full HD)" used in every message this tool returns.</summary>
        public static string DescribeSize(UapGameViewSizeInfo info)
        {
            string size = info.Width + " x " + info.Height;
            return string.IsNullOrEmpty(info.DisplayText) ? size : size + " (" + info.DisplayText + ")";
        }
    }
}
