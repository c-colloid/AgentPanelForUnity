using System;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops.Markers;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Shows a labelled 3D marker in the Scene view (design note 2026-09-07
    /// section 1.3.3) so the agent can point at a place or object instead
    /// of describing it. Not ReadOnly: it changes nothing in the project,
    /// but it does change what is on the user's screen, which is exactly
    /// the kind of side effect the permission card should ask about.
    /// Not Undoable: markers are UI state, cleared with uap_marker_clear.
    /// </summary>
    public sealed class UapMarkerAddTool : IUapTool
    {
        public string Name
        {
            get { return "uap_marker_add"; }
        }

        public string Description
        {
            get
            {
                return "Shows a numbered, labelled 3D marker in the Unity Scene view to point at a"
                    + " world position or a scene object (point, arrow or box); returns its id."
                    + " Refer to it as [n] in prose. Markers are Scene-view overlays only: they"
                    + " never create scene objects and vanish on scene change or Play Mode."
                    + " Verify placement with uap_editor_screenshot.";
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
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                JsonNode vec3 = JsonNode.NewObject().Set("type", "array")
                    .Set("items", JsonNode.NewObject().Set("type", "number"))
                    .Set("minItems", 3).Set("maxItems", 3);
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("kind", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("point").Add("arrow").Add("box"))
                            .Set("description", "Marker shape. Default \"point\"."))
                        .Set("position", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "number"))
                            .Set("minItems", 3).Set("maxItems", 3)
                            .Set("description", "World position [x, y, z] (the tail for an arrow). Required unless target is given."))
                        .Set("to", vec3.Set("description", "Arrow head world position [x, y, z] (arrow only)."))
                        .Set("target", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path (e.g. \"Environment/Ground\") to follow instead of a fixed position; the marker sits on the object's bounds centre and moves with it."))
                        .Set("label", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Short one-line label shown next to the number (max 64 chars)."))
                        .Set("color", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "red | yellow | green | blue | cyan | magenta | white | orange | #RRGGBB. Default red."))
                        .Set("size", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Point radius / box edge in world units. Default 0.5."))
                        .Set("ttl_seconds", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Auto-remove after this many seconds. Default 0 = until cleared.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            SceneMarkerKind kind = SceneMarkerKind.Point;
            string kindArg = input["kind"].AsString(null);
            if (!string.IsNullOrEmpty(kindArg) && !SceneMarker.TryParseKind(kindArg, out kind))
            {
                throw new ArgumentException("kind must be one of point, arrow, box (got \"" + kindArg + "\").");
            }
            string target = input["target"].AsString(null);
            JsonNode positionNode = input["position"];
            bool hasPosition = positionNode != null && positionNode.IsArray;
            if (string.IsNullOrEmpty(target) && !hasPosition)
            {
                throw new ArgumentException("Either position [x, y, z] or target (hierarchy path) is required.");
            }
            Vector3 position = hasPosition ? ReadVector(positionNode, "position") : Vector3.zero;
            if (!string.IsNullOrEmpty(target))
            {
                string error;
                GameObject resolved = UapAddressing.ResolveHierarchyPath(null, target, out error);
                if (resolved == null)
                {
                    throw new InvalidOperationException("target could not be resolved: " + error);
                }
                target = UapAddressing.DescribeHierarchyPath(resolved.transform);
            }
            Vector3 to = Vector3.zero;
            if (kind == SceneMarkerKind.Arrow)
            {
                JsonNode toNode = input["to"];
                if (toNode == null || !toNode.IsArray)
                {
                    throw new ArgumentException("An arrow marker needs \"to\" [x, y, z] (the arrow head).");
                }
                to = ReadVector(toNode, "to");
            }
            Color color = Color.red;
            string colorArg = input["color"].AsString(null);
            if (!string.IsNullOrEmpty(colorArg) && !SceneMarker.TryParseColor(colorArg, out color))
            {
                throw new ArgumentException("color must be a named colour or #RRGGBB (got \"" + colorArg + "\").");
            }
            double size = input["size"].AsDouble(0.5);
            if (double.IsNaN(size) || size <= 0 || size > 1000)
            {
                throw new ArgumentException("size must be a positive number of world units (got " + size + ").");
            }
            double ttl = input["ttl_seconds"].AsDouble(0);
            if (double.IsNaN(ttl) || ttl < 0)
            {
                throw new ArgumentException("ttl_seconds must be 0 or a positive number of seconds.");
            }

            var marker = new SceneMarker
            {
                Kind = kind,
                Position = position,
                To = to,
                Target = target,
                Label = input["label"].AsString(string.Empty),
                Color = color,
                Size = (float)size,
                TtlSeconds = (float)ttl,
                Origin = SceneMarkerOrigin.Agent
            };
            int before = SceneMarkerStore.Count;
            SceneMarkerStore.Add(marker);

            Vector3 shownAt;
            SceneMarkerRenderer.TryResolvePosition(marker, out shownAt);
            string text = "{\"id\":" + marker.Id.ToString(CultureInfo.InvariantCulture) + "} Marker #" + marker.Id
                + " (" + kind.ToString().ToLowerInvariant() + ") is now shown in the Scene view at "
                + Format(shownAt) + (marker.HasTarget ? ", following " + marker.Target : string.Empty) + ".";
            if (before >= SceneMarkerStore.MaxMarkers)
            {
                text += " Warning: the marker cap (" + SceneMarkerStore.MaxMarkers
                    + ") was reached, so the oldest agent marker was removed.";
            }
            return UapToolResults.Text(text);
        }

        private static Vector3 ReadVector(JsonNode node, string name)
        {
            if (node.Count != 3)
            {
                throw new ArgumentException(name + " must be an array of exactly three numbers [x, y, z].");
            }
            float x = (float)node[0].AsDouble(double.NaN);
            float y = (float)node[1].AsDouble(double.NaN);
            float z = (float)node[2].AsDouble(double.NaN);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
            {
                throw new ArgumentException(name + " must contain three numbers.");
            }
            return new Vector3(x, y, z);
        }

        internal static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F2", CultureInfo.InvariantCulture) + ", "
                + v.y.ToString("F2", CultureInfo.InvariantCulture) + ", "
                + v.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
        }
    }
}
