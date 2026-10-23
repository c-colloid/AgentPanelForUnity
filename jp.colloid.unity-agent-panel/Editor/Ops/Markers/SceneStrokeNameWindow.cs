using L10n = Colloid.AgentPanel.UI.L10n;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// The small utility window that names a sketch stroke ("plaza",
    /// "spawn area"; design note 2026-10-09-sketch-shapes-navigation-
    /// regions.md, decision T4). Opened from the S label in the Scene view,
    /// the chip's right-click menu, or N while sketching. Enter applies,
    /// Esc closes. Writes through <see cref="SceneStrokeStore.Rename"/> so
    /// the label, the chip and uap_stroke_list all see the name.
    /// </summary>
    public sealed class SceneStrokeNameWindow : EditorWindow
    {
        private const string FieldControl = "uap-stroke-name";

        private int _strokeId;
        private string _name = string.Empty;
        private bool _focused;

        /// <summary>Opens (or re-targets) the window for the stroke.</summary>
        public static void Open(int strokeId)
        {
            SceneStroke stroke = SceneStrokeStore.Find(strokeId);
            if (stroke == null)
            {
                return;
            }
            var window = GetWindow<SceneStrokeNameWindow>(true, L10n.F(L10n.S.CtxSketchNameWindowTitleFmt, stroke.DisplayNumber), true);
            window._strokeId = strokeId;
            window._name = stroke.Name ?? string.Empty;
            window._focused = false;
            window.minSize = new Vector2(320f, 72f);
            window.maxSize = new Vector2(640f, 72f);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    Apply();
                    e.Use();
                    Close();
                    return;
                }
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    Close();
                    return;
                }
            }
            EditorGUILayout.Space(6f);
            GUI.SetNextControlName(FieldControl);
            _name = EditorGUILayout.TextField(L10n.S.CtxSketchNameFieldLabel, _name ?? string.Empty);
            if (!_focused)
            {
                EditorGUI.FocusTextInControl(FieldControl);
                _focused = true;
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L10n.S.CtxSketchNameApplyButton, GUILayout.Width(80f)))
            {
                Apply();
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void Apply()
        {
            SceneStrokeStore.Rename(_strokeId, _name);
        }
    }
}
