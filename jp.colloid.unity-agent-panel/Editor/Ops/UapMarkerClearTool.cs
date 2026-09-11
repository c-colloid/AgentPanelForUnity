using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops.Markers;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Removes Scene-view markers (design note 2026-09-07 section 1.3.3):
    /// one by id, every agent marker (default), or everything including
    /// the user's pins with all:true. Not ReadOnly (it changes the user's
    /// screen), not Undoable (UI state).
    /// </summary>
    public sealed class UapMarkerClearTool : IUapTool
    {
        public string Name
        {
            get { return "uap_marker_clear"; }
        }

        public string Description
        {
            get
            {
                return "Removes 3D markers from the Unity Scene view: one marker by id, every marker the"
                    + " agent added (default), or everything including the user's pins with all:true.";
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
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("id", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Remove just this marker (from uap_marker_add / uap_marker_list)."))
                        .Set("all", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true also removes pins the user placed. Default false.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            int id = input["id"].AsInt(0);
            if (id > 0)
            {
                return UapToolResults.Text(SceneMarkerStore.Remove(id)
                    ? "Removed marker #" + id + "."
                    : "No marker #" + id + " is shown (nothing removed).");
            }
            bool all = input["all"].AsBool(false);
            int removed = SceneMarkerStore.Clear(all);
            return UapToolResults.Text("Removed " + removed + (all ? " marker(s), including user pins." : " agent marker(s)."));
        }
    }
}
