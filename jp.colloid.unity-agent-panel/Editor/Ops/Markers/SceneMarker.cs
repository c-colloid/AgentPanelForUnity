using System;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>What a marker draws (design note 2026-09-07 section 1.3.1).</summary>
    public enum SceneMarkerKind
    {
        Point,
        Arrow,
        Box
    }

    /// <summary>Who placed the marker: the agent (via uap_marker_add) or the user (a pin).</summary>
    public enum SceneMarkerOrigin
    {
        Agent,
        User
    }

    /// <summary>
    /// One labelled 3D marker shown in the Scene view (docs/design-notes/
    /// 2026-09-07-scene-markers-image-attachments-error-chip.md section
    /// 1.3.1). Pure data: never a scene object, never serialized by Unity
    /// -- it lives in <see cref="SceneMarkerStore"/> and round-trips
    /// through the package's own JSON (<see cref="ToJson"/> /
    /// <see cref="FromJson"/>) for the SessionState sidecar that carries
    /// markers across a domain reload.
    /// </summary>
    public sealed class SceneMarker
    {
        /// <summary>Longest label kept; longer input is cut with an ellipsis.</summary>
        public const int MaxLabelChars = 64;

        /// <summary>1-based, unique for the editor session; the "[n]" the agent writes in prose.</summary>
        public int Id;
        public SceneMarkerKind Kind = SceneMarkerKind.Point;
        /// <summary>World position (Arrow: the tail). Ignored while <see cref="Target"/> resolves.</summary>
        public Vector3 Position;
        /// <summary>Arrow head position (world).</summary>
        public Vector3 To;
        /// <summary>Optional hierarchy path to follow (bounds centre of the object); null/empty = fixed position.</summary>
        public string Target;
        public string Label = string.Empty;
        public Color Color = Color.red;
        /// <summary>Point radius / Box edge (world units).</summary>
        public float Size = 0.5f;
        /// <summary>0 = until cleared; &gt; 0 = auto-removed that many seconds after <see cref="CreatedAt"/>.</summary>
        public float TtlSeconds;
        public SceneMarkerOrigin Origin = SceneMarkerOrigin.Agent;
        /// <summary>EditorApplication.timeSinceStartup at creation (drives TTL).</summary>
        public double CreatedAt;
        /// <summary>
        /// User pins only: the number shown as "P&lt;n&gt;". Unlike <see cref="Id"/>
        /// (unique for the session, never reused) pin numbers are the
        /// lowest free number, so removing P1 lets the next pin be P1 again
        /// -- the user counts pins, the agent refers to ids.
        /// </summary>
        public int PinNumber;
        /// <summary>
        /// User pins only: the context-chip payload captured at placement
        /// (hit object, nearest objects, camera), kept with the marker so
        /// the panel can rebuild the chip after a reload or when the pin
        /// was dropped while the panel was not showing.
        /// </summary>
        public string Note = string.Empty;

        /// <summary>True when this marker follows an object rather than a fixed point.</summary>
        public bool HasTarget
        {
            get { return !string.IsNullOrEmpty(Target); }
        }

        /// <summary>True when a TTL is set and has elapsed at <paramref name="now"/>.</summary>
        public bool IsExpired(double now)
        {
            return TtlSeconds > 0f && now - CreatedAt > TtlSeconds;
        }

        /// <summary>
        /// Chip/label text: "3  Spawn point" for agent markers, "P2  pin"
        /// for user pins -- the number is what the agent refers to.
        /// </summary>
        public string DisplayLabel
        {
            get
            {
                string number = Origin == SceneMarkerOrigin.User
                    ? "P" + (PinNumber > 0 ? PinNumber : Id).ToString(CultureInfo.InvariantCulture)
                    : Id.ToString(CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(Label) ? number : number + "  " + Label;
            }
        }

        /// <summary>Trims/caps a label to <see cref="MaxLabelChars"/>, one line, never null.</summary>
        public static string NormalizeLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }
            string line = label.Replace("\r", " ").Replace("\n", " ").Trim();
            if (line.Length > MaxLabelChars)
            {
                line = line.Substring(0, MaxLabelChars - 3) + "...";
            }
            return line;
        }

        /// <summary>
        /// Parses the tool's colour argument: a named colour (red, yellow,
        /// green, blue, cyan, magenta, white, orange) or "#RRGGBB"/"#RRGGBBAA".
        /// Case-insensitive. Returns false for anything else.
        /// </summary>
        public static bool TryParseColor(string text, out Color color)
        {
            color = Color.red;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            switch (text.Trim().ToLowerInvariant())
            {
                case "red": color = new Color(1f, 0.25f, 0.2f); return true;
                case "yellow": color = new Color(1f, 0.85f, 0.1f); return true;
                case "green": color = new Color(0.25f, 1f, 0.4f); return true;
                case "blue": color = new Color(0.35f, 0.55f, 1f); return true;
                case "cyan": color = new Color(0.2f, 0.9f, 1f); return true;
                case "magenta": color = new Color(1f, 0.3f, 0.95f); return true;
                case "white": color = Color.white; return true;
                case "orange": color = new Color(1f, 0.55f, 0.1f); return true;
            }
            return ColorUtility.TryParseHtmlString(text.Trim(), out color);
        }

        /// <summary>Parses a "kind" argument (case-insensitive). False for unknown values.</summary>
        public static bool TryParseKind(string text, out SceneMarkerKind kind)
        {
            kind = SceneMarkerKind.Point;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            switch (text.Trim().ToLowerInvariant())
            {
                case "point": kind = SceneMarkerKind.Point; return true;
                case "arrow": kind = SceneMarkerKind.Arrow; return true;
                case "box": kind = SceneMarkerKind.Box; return true;
            }
            return false;
        }

        // -- JSON (SessionState sidecar) ----------------------------------------------

        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("id", Id)
                .Set("kind", (int)Kind)
                .Set("origin", (int)Origin)
                .Set("px", Position.x).Set("py", Position.y).Set("pz", Position.z)
                .Set("tx", To.x).Set("ty", To.y).Set("tz", To.z)
                .Set("target", Target ?? string.Empty)
                .Set("label", Label ?? string.Empty)
                .Set("color", "#" + ColorUtility.ToHtmlStringRGBA(Color))
                .Set("size", Size)
                .Set("ttl", TtlSeconds)
                .Set("createdAt", CreatedAt)
                .Set("pin", PinNumber)
                .Set("note", Note ?? string.Empty);
        }

        /// <summary>Inverse of <see cref="ToJson"/>; null for a node that is not an object.</summary>
        public static SceneMarker FromJson(JsonNode node)
        {
            if (node == null || !node.IsObject)
            {
                return null;
            }
            Color color;
            if (!ColorUtility.TryParseHtmlString(node["color"].AsString("#FF0000FF"), out color))
            {
                color = Color.red;
            }
            return new SceneMarker
            {
                Id = node["id"].AsInt(0),
                Kind = ClampEnum<SceneMarkerKind>(node["kind"].AsInt(0)),
                Origin = ClampEnum<SceneMarkerOrigin>(node["origin"].AsInt(0)),
                Position = new Vector3((float)node["px"].AsDouble(0), (float)node["py"].AsDouble(0), (float)node["pz"].AsDouble(0)),
                To = new Vector3((float)node["tx"].AsDouble(0), (float)node["ty"].AsDouble(0), (float)node["tz"].AsDouble(0)),
                Target = node["target"].AsString(string.Empty),
                Label = node["label"].AsString(string.Empty),
                Color = color,
                Size = (float)node["size"].AsDouble(0.5),
                TtlSeconds = (float)node["ttl"].AsDouble(0),
                CreatedAt = node["createdAt"].AsDouble(0),
                PinNumber = node["pin"].AsInt(0),
                Note = node["note"].AsString(string.Empty)
            };
        }

        private static T ClampEnum<T>(int raw) where T : struct
        {
            Array values = Enum.GetValues(typeof(T));
            return raw >= 0 && raw < values.Length ? (T)values.GetValue(raw) : default(T);
        }
    }
}
