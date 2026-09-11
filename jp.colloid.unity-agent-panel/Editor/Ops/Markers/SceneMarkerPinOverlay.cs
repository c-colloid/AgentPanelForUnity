using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// Scene-view toolbar overlay with the single "Pin" toggle (design note
    /// 2026-09-07 section 1.3.4, corrected in 1.5: a ToolbarOverlay shown
    /// by default -- the panel-type Overlay of the spike registered but
    /// stayed hidden). The toggle mirrors <see cref="SceneMarkerPin.Armed"/>
    /// in both directions so the context bar's pin button and this stay
    /// in step. Like every overlay the user can hide it from the Scene
    /// view's overlay menu; the panel button remains.
    /// </summary>
    [Overlay(typeof(SceneView), OverlayId, "Agent Pin", true)]
    public sealed class SceneMarkerPinOverlay : ToolbarOverlay
    {
        public const string OverlayId = "uap-agent-pin";

        public SceneMarkerPinOverlay() : base(SceneMarkerPinToggle.ElementId)
        {
        }
    }

    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneMarkerPinToggle : EditorToolbarToggle
    {
        public const string ElementId = "uap/agent-pin";

        public SceneMarkerPinToggle()
        {
            // Icon-only like Unity's own toolbar toggles (a text toggle
            // renders as an oversized, misaligned button in the toolbar).
            var content = EditorGUIUtility.IconContent("d_Transform Icon");
            if (content != null && content.image is Texture2D)
            {
                icon = (Texture2D)content.image;
            }
            else
            {
                text = L10n.S.CtxPinToolbarLabel;
            }
            tooltip = L10n.S.CtxPinTooltip;
            SetValueWithoutNotify(SceneMarkerPin.Armed);
            this.RegisterValueChangedCallback(OnToggled);
            RegisterCallback<AttachToPanelEvent>(delegate { SceneMarkerPin.ArmedChanged += Sync; Sync(); });
            RegisterCallback<DetachFromPanelEvent>(delegate { SceneMarkerPin.ArmedChanged -= Sync; });
        }

        private void OnToggled(ChangeEvent<bool> evt)
        {
            SceneMarkerPin.Armed = evt.newValue;
        }

        private void Sync()
        {
            SetValueWithoutNotify(SceneMarkerPin.Armed);
        }
    }
}
