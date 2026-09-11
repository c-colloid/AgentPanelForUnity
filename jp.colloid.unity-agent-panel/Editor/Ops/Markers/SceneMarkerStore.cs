using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// The one list of Scene-view markers (docs/design-notes/2026-09-07-
    /// scene-markers-image-attachments-error-chip.md section 1.3.1 / M4).
    /// Static, main-thread only. Markers are UI state, not project state:
    /// they are never written to a scene or an asset, they survive a
    /// domain reload through <see cref="SessionStateBridge.SceneMarkersJson"/>
    /// (saved on every change, restored by <see cref="Install"/>), and
    /// they are dropped when the active scene changes or Play Mode is
    /// entered -- a marker only means something in the scene it was placed
    /// in. Editor restarts start empty (SessionState semantics).
    ///
    /// The pure list operations (add with the cap, remove, clear by origin,
    /// TTL expiry) are also exposed as static functions over an explicit
    /// list so EditMode tests can pin them without the editor hooks.
    /// </summary>
    public static class SceneMarkerStore
    {
        /// <summary>Hard cap; adding past it evicts the OLDEST agent marker (user pins are never evicted this way).</summary>
        public const int MaxMarkers = 32;

        private static readonly List<SceneMarker> _markers = new List<SceneMarker>();
        private static int _nextId = 1;
        private static bool _installed;

        /// <summary>Raised after any change to the set (add/remove/clear/expiry/restore).</summary>
        public static event Action Changed;

        public static int Count
        {
            get { return _markers.Count; }
        }

        /// <summary>Copy of the markers, oldest first.</summary>
        public static SceneMarker[] Snapshot()
        {
            return _markers.ToArray();
        }

        public static SceneMarker Find(int id)
        {
            for (int i = 0; i < _markers.Count; i++)
            {
                if (_markers[i].Id == id)
                {
                    return _markers[i];
                }
            }
            return null;
        }

        /// <summary>Number of markers with the given origin (the context-bar chip counts agent markers).</summary>
        public static int CountByOrigin(SceneMarkerOrigin origin)
        {
            int n = 0;
            for (int i = 0; i < _markers.Count; i++)
            {
                if (_markers[i].Origin == origin)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// Assigns the next id and CreatedAt, applies the cap, appends,
        /// persists and raises Changed. Returns the marker (now with Id).
        /// The label is normalized here so every entry point (tool, pin)
        /// gets the same one-line, capped text.
        /// </summary>
        public static SceneMarker Add(SceneMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException("marker");
            }
            marker.Id = _nextId++;
            marker.CreatedAt = EditorApplication.timeSinceStartup;
            marker.Label = SceneMarker.NormalizeLabel(marker.Label);
            if (marker.Origin == SceneMarkerOrigin.User && marker.PinNumber <= 0)
            {
                marker.PinNumber = NextPinNumber(_markers);
            }
            AddWithCap(_markers, marker, MaxMarkers);
            Persist();
            RaiseChanged();
            return marker;
        }

        public static bool Remove(int id)
        {
            int removed = _markers.RemoveAll(m => m.Id == id);
            if (removed == 0)
            {
                return false;
            }
            Persist();
            RaiseChanged();
            return true;
        }

        /// <summary>Removes agent markers, or everything when <paramref name="includeUserPins"/>. Returns how many went.</summary>
        public static int Clear(bool includeUserPins)
        {
            int removed = ClearByOrigin(_markers, includeUserPins);
            if (removed > 0)
            {
                Persist();
                RaiseChanged();
            }
            return removed;
        }

        // -- Pure list operations (EditMode-testable) ----------------------------------

        /// <summary>
        /// Appends <paramref name="marker"/>; when the list is already at
        /// <paramref name="max"/>, the oldest AGENT marker is evicted first
        /// (user pins are the user's, never silently dropped). If every
        /// slot is a user pin the oldest pin goes instead so the add can
        /// never fail. Returns the evicted marker or null.
        /// </summary>
        public static SceneMarker AddWithCap(List<SceneMarker> list, SceneMarker marker, int max)
        {
            SceneMarker evicted = null;
            if (max > 0 && list.Count >= max)
            {
                int index = list.FindIndex(m => m.Origin == SceneMarkerOrigin.Agent);
                if (index < 0)
                {
                    index = 0;
                }
                evicted = list[index];
                list.RemoveAt(index);
            }
            list.Add(marker);
            return evicted;
        }

        /// <summary>The lowest positive pin number no current user pin uses (so numbers are reused after removal).</summary>
        public static int NextPinNumber(IList<SceneMarker> list)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Origin == SceneMarkerOrigin.User && list[i].PinNumber > 0)
                {
                    used.Add(list[i].PinNumber);
                }
            }
            int number = 1;
            while (used.Contains(number))
            {
                number++;
            }
            return number;
        }

        /// <summary>Removes agent markers (or all). Returns the count removed.</summary>
        public static int ClearByOrigin(List<SceneMarker> list, bool includeUserPins)
        {
            return list.RemoveAll(m => includeUserPins || m.Origin == SceneMarkerOrigin.Agent);
        }

        /// <summary>Removes every marker whose TTL elapsed at <paramref name="now"/>. Returns the count removed.</summary>
        public static int RemoveExpired(List<SceneMarker> list, double now)
        {
            return list.RemoveAll(m => m.IsExpired(now));
        }

        /// <summary>Serializes a list plus the next id (the SessionState payload shape).</summary>
        public static string Serialize(IList<SceneMarker> list, int nextId)
        {
            var items = JsonNode.NewArray();
            for (int i = 0; i < list.Count; i++)
            {
                items.Add(list[i].ToJson());
            }
            return JsonWriter.Write(JsonNode.NewObject().Set("next", nextId).Set("items", items));
        }

        /// <summary>Inverse of <see cref="Serialize"/>; an empty/invalid payload yields an empty list and nextId 1.</summary>
        public static List<SceneMarker> Deserialize(string json, out int nextId)
        {
            nextId = 1;
            var result = new List<SceneMarker>();
            if (string.IsNullOrEmpty(json))
            {
                return result;
            }
            JsonNode root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception)
            {
                return result;
            }
            if (root == null || !root.IsObject)
            {
                return result;
            }
            nextId = Math.Max(1, root["next"].AsInt(1));
            JsonNode items = root["items"];
            if (items != null && items.IsArray)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    SceneMarker marker = SceneMarker.FromJson(items[i]);
                    if (marker != null)
                    {
                        result.Add(marker);
                    }
                }
            }
            return result;
        }

        // -- Editor lifecycle ----------------------------------------------------------

        [InitializeOnLoadMethod]
        private static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            // Restore first so the renderer's first repaint after a reload
            // already shows what was there (M4).
            List<SceneMarker> restored = Deserialize(SessionStateBridge.SceneMarkersJson, out _nextId);
            _markers.Clear();
            _markers.AddRange(restored);
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            SceneMarkerRenderer.Install();
            SceneMarkerPin.Install();
            if (_markers.Count > 0)
            {
                RaiseChanged();
            }
        }

        private static void OnActiveSceneChanged(Scene from, Scene to)
        {
            Clear(true);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                Clear(true);
            }
        }

        private static double _nextTtlCheck;

        private static void Tick()
        {
            // TTL is rare; check twice a second, and only walk the list when
            // something could actually expire.
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextTtlCheck || _markers.Count == 0)
            {
                return;
            }
            _nextTtlCheck = now + 0.5;
            if (RemoveExpired(_markers, now) > 0)
            {
                Persist();
                RaiseChanged();
            }
        }

        private static void Persist()
        {
            SessionStateBridge.SceneMarkersJson = Serialize(_markers, _nextId);
        }

        private static void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                handler();
            }
            SceneView.RepaintAll();
        }

        // -- Test seams ----------------------------------------------------------------

        /// <summary>Forgets every marker and resets the id counter (does not touch subscribers or SessionState).</summary>
        internal static void ResetForTests()
        {
            _markers.Clear();
            _nextId = 1;
        }
    }
}
