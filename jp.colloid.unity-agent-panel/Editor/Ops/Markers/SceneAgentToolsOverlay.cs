using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// The one Scene-view toolbar overlay for the user's marking tools:
    /// the "Pin" toggle (design note 2026-09-07 section 1.3.4, corrected in
    /// 1.5: a ToolbarOverlay shown by default) and the two sketch toggles,
    /// plane and surface (design note 2026-09-17-scene-sketch-strokes.md
    /// section 3). Until 2026-09-18 the pin and the sketch toggles were two
    /// overlays ("Agent Pin" and "Agent Sketch") that each took their own
    /// strip of the Scene view; one strip reads as one tool set
    /// (docs/design-notes/2026-09-18-agent-tools-overlay.md).
    ///
    /// Each toggle mirrors its state (<see cref="SceneMarkerPin.Armed"/>,
    /// <see cref="SceneStrokeSketch.IsArmedIn"/>) in both directions, so
    /// the context bar's pin button and sketch menu stay in step; the three
    /// are mutually exclusive through <see cref="SceneMarkerPin"/> and
    /// <see cref="SceneStrokeSketch"/> disarming each other. Like every
    /// overlay the user can hide it from the Scene view's overlay menu; the
    /// panel controls remain. Icons (surveyed on 2022.3.62f3, design note
    /// section 4): a pushpin-like pivot handle for the pin, a flat grid
    /// for the plane sketch and the terrain "raise" brush for the surface
    /// sketch; the earlier "d_Grid.Default" drew as a plain cursor arrow.
    /// </summary>
    [Overlay(typeof(SceneView), OverlayId, "Agent Tools", true)]
    public sealed class SceneAgentToolsOverlay : ToolbarOverlay
    {
        public const string OverlayId = "uap-agent-tools";

        public SceneAgentToolsOverlay()
            : base(SceneMarkerPinToggle.ElementId, SceneStrokePlaneToggle.ElementId, SceneStrokeSurfaceToggle.ElementId)
        {
        }
    }

    /// <summary>Shared body of the toggles: icon or text, tooltip, and the two-way sync with an armed state.</summary>
    internal abstract class SceneAgentToolToggle : EditorToolbarToggle
    {
        protected SceneAgentToolToggle(string iconName, string label, string tip)
        {
            // Icon-only like Unity's own toolbar toggles (a text toggle
            // renders as an oversized, misaligned button in the toolbar).
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
            SetValueWithoutNotify(IsArmed());
            this.RegisterValueChangedCallback(OnToggled);
            RegisterCallback<AttachToPanelEvent>(delegate { Subscribe(Sync); Sync(); });
            RegisterCallback<DetachFromPanelEvent>(delegate { Unsubscribe(Sync); });
        }

        protected abstract bool IsArmed();
        protected abstract void SetArmed(bool armed);
        protected abstract void Subscribe(System.Action sync);
        protected abstract void Unsubscribe(System.Action sync);

        private void OnToggled(ChangeEvent<bool> evt)
        {
            SetArmed(evt.newValue);
        }

        private void Sync()
        {
            SetValueWithoutNotify(IsArmed());
        }
    }

    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneMarkerPinToggle : SceneAgentToolToggle
    {
        public const string ElementId = "uap/agent-pin";

        public SceneMarkerPinToggle()
            : base("d_ToolHandlePivot", L10n.S.CtxPinToolbarLabel, L10n.S.CtxPinTooltip)
        {
        }

        protected override bool IsArmed()
        {
            return SceneMarkerPin.Armed;
        }

        protected override void SetArmed(bool armed)
        {
            SceneMarkerPin.Armed = armed;
        }

        protected override void Subscribe(System.Action sync)
        {
            SceneMarkerPin.ArmedChanged += sync;
        }

        protected override void Unsubscribe(System.Action sync)
        {
            SceneMarkerPin.ArmedChanged -= sync;
        }
    }

    /// <summary>Shared body of the two sketch toggles: one mode each; turning one on turns the other off through the shared mode.</summary>
    internal abstract class SceneStrokeModeToggle : SceneAgentToolToggle
    {
        private readonly SceneStrokeMode _mode;

        protected SceneStrokeModeToggle(SceneStrokeMode mode, string iconName, string label, string tip)
            : base(iconName, label, tip)
        {
            _mode = mode;
            // The base constructor read IsArmed() before _mode was set.
            SetValueWithoutNotify(IsArmed());
        }

        protected override bool IsArmed()
        {
            return SceneStrokeSketch.IsArmedIn(_mode);
        }

        protected override void SetArmed(bool armed)
        {
            if (armed)
            {
                SceneStrokeSketch.Arm(_mode);
            }
            else if (SceneStrokeSketch.IsArmedIn(_mode))
            {
                SceneStrokeSketch.Armed = false;
            }
        }

        protected override void Subscribe(System.Action sync)
        {
            SceneStrokeSketch.ArmedChanged += sync;
        }

        protected override void Unsubscribe(System.Action sync)
        {
            SceneStrokeSketch.ArmedChanged -= sync;
        }
    }

    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneStrokePlaneToggle : SceneStrokeModeToggle
    {
        public const string ElementId = "uap/agent-sketch-plane";

        public SceneStrokePlaneToggle()
            : base(SceneStrokeMode.Plane, "d_Mesh Icon", L10n.S.CtxSketchPlaneToolbarLabel, L10n.S.CtxSketchPlaneTooltip)
        {
        }
    }

    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneStrokeSurfaceToggle : SceneStrokeModeToggle
    {
        public const string ElementId = "uap/agent-sketch-surface";

        public SceneStrokeSurfaceToggle()
            : base(SceneStrokeMode.Surface, "d_TerrainInspector.TerrainToolRaise", L10n.S.CtxSketchSurfaceToolbarLabel, L10n.S.CtxSketchSurfaceTooltip)
        {
        }
    }
}
