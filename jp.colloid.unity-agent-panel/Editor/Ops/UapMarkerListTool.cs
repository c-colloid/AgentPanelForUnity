using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops.Markers;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Lists the markers currently shown in the Scene view, including pins
    /// the user placed (design note 2026-09-07 section 1.3.3). ReadOnly:
    /// it only reads the store, so it may be auto-approved like the other
    /// query tools (pinned by UapReadOnlyToolMetadataTests).
    /// </summary>
    public sealed class UapMarkerListTool : IUapTool
    {
        public string Name
        {
            get { return "uap_marker_list"; }
        }

        public string Description
        {
            get
            {
                return "Lists the 3D markers currently shown in the Unity Scene view -- the agent's own"
                    + " (uap_marker_add) and pins the user placed -- with id, kind, label, world position"
                    + " and followed object. Read-only.";
            }
        }

        public string Module
        {
            get { return "markers"; }
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
                    .Set("properties", JsonNode.NewObject())
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            SceneMarker[] markers = SceneMarkerStore.Snapshot();
            if (markers.Length == 0)
            {
                return UapToolResults.Text("No markers are shown in the Scene view.");
            }
            var sb = new StringBuilder(128);
            sb.Append("Scene view markers (").Append(markers.Length).Append("):");
            for (int i = 0; i < markers.Length; i++)
            {
                SceneMarker marker = markers[i];
                Vector3 position;
                bool resolves = SceneMarkerRenderer.TryResolvePosition(marker, out position);
                sb.Append("\n  #").Append(marker.Id)
                  .Append(' ').Append(marker.Kind.ToString().ToLowerInvariant())
                  .Append(' ').Append(marker.Origin == SceneMarkerOrigin.User
                      ? "user-pin P" + (marker.PinNumber > 0 ? marker.PinNumber : marker.Id) : "agent")
                  .Append(" \"").Append(marker.Label).Append('"');
                if (resolves)
                {
                    sb.Append(" at ").Append(UapMarkerAddTool.Format(position));
                }
                else
                {
                    sb.Append(" (target no longer exists, not drawn)");
                }
                if (marker.HasTarget)
                {
                    sb.Append(" follows ").Append(marker.Target);
                }
                if (marker.Kind == SceneMarkerKind.Arrow)
                {
                    sb.Append(" -> ").Append(UapMarkerAddTool.Format(marker.To));
                }
                if (marker.TtlSeconds > 0f)
                {
                    sb.Append(" ttl=").Append(marker.TtlSeconds).Append('s');
                }
            }
            return UapToolResults.Text(sb.ToString());
        }
    }
}
