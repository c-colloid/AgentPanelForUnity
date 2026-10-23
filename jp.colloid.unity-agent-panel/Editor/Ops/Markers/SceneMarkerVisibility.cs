using System;
using UnityEditor;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// The one on/off switch for everything the agent tooling draws over
    /// the Scene view -- agent markers, the user's pins, sketch strokes,
    /// and, in Pro, the Model state labels (design note
    /// docs/design-notes/2026-10-01-scene-overlay-visibility.md). Like the
    /// Scene view's own Gizmos button it hides without removing: the
    /// stores keep their contents, the agent can still list and add, and
    /// turning the switch back on shows exactly what was there. The
    /// value is an EditorPrefs preference (per user, every project), so a
    /// user who turned the labels off keeps them off across restarts; the
    /// Agent Tools toolbar toggle makes the state visible at a glance.
    /// </summary>
    public static class SceneMarkerVisibility
    {
        private const string PrefKey = "Colloid.AgentPanel.SceneOverlaysHidden";

        private static bool _loaded;
        private static bool _hidden;

        /// <summary>Raised after <see cref="Visible"/> changed (the toolbar toggle and the context bar mirror it).</summary>
        public static event Action Changed;

        /// <summary>True (the default) while the overlays draw. Setting the same value again is a no-op.</summary>
        public static bool Visible
        {
            get
            {
                Load();
                return !_hidden;
            }
            set
            {
                Load();
                bool hidden = !value;
                if (hidden == _hidden)
                {
                    return;
                }
                _hidden = hidden;
                EditorPrefs.SetBool(PrefKey, hidden);
                Action handler = Changed;
                if (handler != null)
                {
                    handler();
                }
                SceneView.RepaintAll();
            }
        }

        public static void Toggle()
        {
            Visible = !Visible;
        }

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            _hidden = EditorPrefs.GetBool(PrefKey, false);
        }
    }
}
