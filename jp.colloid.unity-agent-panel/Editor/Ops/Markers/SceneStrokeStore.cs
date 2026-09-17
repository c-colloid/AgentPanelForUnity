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
    /// The one list of user-drawn sketch strokes (docs/design-notes/
    /// 2026-09-17-scene-sketch-strokes.md, decisions S1 / S8). Static,
    /// main-thread only, and the same lifecycle as
    /// <see cref="SceneMarkerStore"/>: UI state, never written to a scene
    /// or an asset, carried across a domain reload through
    /// <see cref="SessionStateBridge.SceneStrokesJson"/>, dropped when the
    /// active scene changes or Play Mode starts. Editor restarts start
    /// empty.
    ///
    /// The pure list operations (add with the cap, numbering, JSON) are
    /// static functions over an explicit list so EditMode tests can pin
    /// them without the editor hooks.
    /// </summary>
    public static class SceneStrokeStore
    {
        /// <summary>Hard cap; adding past it evicts the OLDEST stroke.</summary>
        public const int MaxStrokes = 16;

        private static readonly List<SceneStroke> _strokes = new List<SceneStroke>();
        private static int _nextId = 1;
        private static bool _installed;

        /// <summary>Raised after any change to the set (add/remove/clear/restore).</summary>
        public static event Action Changed;

        public static int Count
        {
            get { return _strokes.Count; }
        }

        /// <summary>The id the next <see cref="Add"/> will assign (so a payload can name it before the add).</summary>
        public static int NextId
        {
            get { return _nextId; }
        }

        /// <summary>Copy of the strokes, oldest first.</summary>
        public static SceneStroke[] Snapshot()
        {
            return _strokes.ToArray();
        }

        public static SceneStroke Find(int id)
        {
            for (int i = 0; i < _strokes.Count; i++)
            {
                if (_strokes[i].Id == id)
                {
                    return _strokes[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Assigns the next id, the lowest free S number and CreatedAt,
        /// applies the cap, appends, persists and raises Changed. Returns
        /// the stroke (now with Id). Throws for a stroke without points.
        /// </summary>
        public static SceneStroke Add(SceneStroke stroke)
        {
            if (stroke == null)
            {
                throw new ArgumentNullException("stroke");
            }
            if (stroke.Points == null || stroke.Points.Count == 0)
            {
                throw new ArgumentException("A stroke needs at least one point.", "stroke");
            }
            stroke.Id = _nextId++;
            stroke.CreatedAt = EditorApplication.timeSinceStartup;
            if (stroke.Number <= 0)
            {
                stroke.Number = NextNumber(_strokes);
            }
            AddWithCap(_strokes, stroke, MaxStrokes);
            Persist();
            RaiseChanged();
            return stroke;
        }

        public static bool Remove(int id)
        {
            int removed = _strokes.RemoveAll(s => s.Id == id);
            if (removed == 0)
            {
                return false;
            }
            Persist();
            RaiseChanged();
            return true;
        }

        /// <summary>Removes every stroke. Returns how many went.</summary>
        public static int Clear()
        {
            int removed = _strokes.Count;
            if (removed == 0)
            {
                return 0;
            }
            _strokes.Clear();
            Persist();
            RaiseChanged();
            return removed;
        }

        // -- Pure list operations (EditMode-testable) ----------------------------------

        /// <summary>Appends; when the list is at <paramref name="max"/> the oldest stroke is evicted first. Returns the evicted stroke or null.</summary>
        public static SceneStroke AddWithCap(List<SceneStroke> list, SceneStroke stroke, int max)
        {
            SceneStroke evicted = null;
            if (max > 0 && list.Count >= max)
            {
                evicted = list[0];
                list.RemoveAt(0);
            }
            list.Add(stroke);
            return evicted;
        }

        /// <summary>The lowest positive number no current stroke uses (S numbers are reused after removal, like pin numbers).</summary>
        public static int NextNumber(IList<SceneStroke> list)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Number > 0)
                {
                    used.Add(list[i].Number);
                }
            }
            int number = 1;
            while (used.Contains(number))
            {
                number++;
            }
            return number;
        }

        /// <summary>Serializes a list plus the next id (the SessionState payload shape).</summary>
        public static string Serialize(IList<SceneStroke> list, int nextId)
        {
            var items = JsonNode.NewArray();
            for (int i = 0; i < list.Count; i++)
            {
                items.Add(list[i].ToJson());
            }
            return JsonWriter.Write(JsonNode.NewObject().Set("next", nextId).Set("items", items));
        }

        /// <summary>Inverse of <see cref="Serialize"/>; an empty/invalid payload yields an empty list and nextId 1.</summary>
        public static List<SceneStroke> Deserialize(string json, out int nextId)
        {
            nextId = 1;
            var result = new List<SceneStroke>();
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
                    SceneStroke stroke = SceneStroke.FromJson(items[i]);
                    if (stroke != null)
                    {
                        result.Add(stroke);
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
            List<SceneStroke> restored = Deserialize(SessionStateBridge.SceneStrokesJson, out _nextId);
            _strokes.Clear();
            _strokes.AddRange(restored);
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            SceneStrokeRenderer.Install();
            SceneStrokeSketch.Install();
            if (_strokes.Count > 0)
            {
                RaiseChanged();
            }
        }

        private static void OnActiveSceneChanged(Scene from, Scene to)
        {
            Clear();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                Clear();
            }
        }

        private static void Persist()
        {
            SessionStateBridge.SceneStrokesJson = Serialize(_strokes, _nextId);
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

        /// <summary>Forgets every stroke and resets the id counter (does not touch subscribers or SessionState).</summary>
        internal static void ResetForTests()
        {
            _strokes.Clear();
            _nextId = 1;
        }
    }
}
