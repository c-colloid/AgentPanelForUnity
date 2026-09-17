using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// Scene-view toolbar overlay with the two sketch toggles -- plane and
    /// surface (design note 2026-09-17-scene-sketch-strokes.md section 3).
    /// Each toggle mirrors <see cref="SceneStrokeSketch.IsArmedIn"/> for its
    /// mode in both directions, so the context bar's sketch menu and these
    /// stay in step; turning one on turns the other off through the shared
    /// mode. Like every overlay the user can hide it from the Scene view's
    /// overlay menu; the panel menu remains.
    /// </summary>
    [Overlay(typeof(SceneView), OverlayId, "Agent Sketch", true)]
    public sealed class SceneStrokeOverlay : ToolbarOverlay
    {
        public const string OverlayId = "uap-agent-sketch";

        public SceneStrokeOverlay() : base(SceneStrokePlaneToggle.ElementId, SceneStrokeSurfaceToggle.ElementId)
        {
        }
    }

    /// <summary>Shared body of the two toggles: icon or text, tooltip, and the two-way sync with the sketch state.</summary>
    internal abstract class SceneStrokeModeToggle : EditorToolbarToggle
    {
        private readonly SceneStrokeMode _mode;

        protected SceneStrokeModeToggle(SceneStrokeMode mode, string iconName, string label, string tip)
        {
            _mode = mode;
            var content = EditorGUIUtility.IconContent(iconName);
            if (content != null && content.image is Texture2D)
            {
                icon = (Texture2D)content.image;
            }
            else
            {
                text = label;
            }
            tooltip = tip;
            SetValueWithoutNotify(SceneStrokeSketch.IsArmedIn(_mode));
            this.RegisterValueChangedCallback(OnToggled);
            RegisterCallback<AttachToPanelEvent>(delegate { SceneStrokeSketch.ArmedChanged += Sync; Sync(); });
            RegisterCallback<DetachFromPanelEvent>(delegate { SceneStrokeSketch.ArmedChanged -= Sync; });
        }

        private void OnToggled(ChangeEvent<bool> evt)
        {
            if (evt.newValue)
            {
                SceneStrokeSketch.Arm(_mode);
            }
            else if (SceneStrokeSketch.IsArmedIn(_mode))
            {
                SceneStrokeSketch.Armed = false;
            }
        }

        private void Sync()
        {
            SetValueWithoutNotify(SceneStrokeSketch.IsArmedIn(_mode));
        }
    }

    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneStrokePlaneToggle : SceneStrokeModeToggle
    {
        public const string ElementId = "uap/agent-sketch-plane";

        public SceneStrokePlaneToggle()
            : base(SceneStrokeMode.Plane, "d_Grid.Default", L10n.S.CtxSketchPlaneToolbarLabel, L10n.S.CtxSketchPlaneTooltip)
        {
        }
    }

    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneStrokeSurfaceToggle : SceneStrokeModeToggle
    {
        public const string ElementId = "uap/agent-sketch-surface";

        public SceneStrokeSurfaceToggle()
            : base(SceneStrokeMode.Surface, "d_editicon.sml", L10n.S.CtxSketchSurfaceToolbarLabel, L10n.S.CtxSketchSurfaceTooltip)
        {
        }
    }
}
