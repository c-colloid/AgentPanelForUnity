using System;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Ops.Markers
{
    /// <summary>
    /// The extension slot of the Agent Tools toolbar (design note
    /// docs/design-notes/2026-10-13-agent-tools-extension-slot.md): another
    /// package registers a factory for the element that fills
    /// <see cref="SceneAgentModelSlot"/>, so its tools sit in the one
    /// strip instead of a second overlay beside it. With nothing
    /// registered the slot shows a dim "Pro" word whose tooltip says what
    /// would be there, the one place the editions are told apart.
    /// </summary>
    public static class SceneAgentToolsExtensions
    {
        private static Func<VisualElement> _modelElement;

        /// <summary>Raised when a factory is registered or cleared, so a live slot rebuilds.</summary>
        public static event Action Changed;

        /// <summary>The factory of the Model element, or null.</summary>
        public static Func<VisualElement> ModelElement
        {
            get { return _modelElement; }
        }

        public static void RegisterModelElement(Func<VisualElement> factory)
        {
            _modelElement = factory;
            Action changed = Changed;
            if (changed != null) changed();
        }
    }

    /// <summary>The slot itself: the registered element, else the dim "Pro" word.</summary>
    [EditorToolbarElement(ElementId, typeof(SceneView))]
    internal sealed class SceneAgentModelSlot : VisualElement
    {
        public const string ElementId = "uap/agent-model";
        public const string BadgeClass = "uap-pro-badge";

        public SceneAgentModelSlot()
        {
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            Build();
            RegisterCallback<AttachToPanelEvent>(delegate { SceneAgentToolsExtensions.Changed += Build; Build(); });
            RegisterCallback<DetachFromPanelEvent>(delegate { SceneAgentToolsExtensions.Changed -= Build; });
        }

        internal void Build()
        {
            Clear();
            Func<VisualElement> factory = SceneAgentToolsExtensions.ModelElement;
            if (factory != null)
            {
                VisualElement element = null;
                try { element = factory(); }
                catch (Exception e) { Debug.LogException(e); }
                if (element != null)
                {
                    Add(element);
                    return;
                }
            }
            var badge = new Label("Pro") { tooltip = L10n.S.CtxProSlotTooltip };
            badge.AddToClassList(BadgeClass);
            badge.style.opacity = 0.45f;
            badge.style.fontSize = 10;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.paddingLeft = 6;
            badge.style.paddingRight = 6;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            Add(badge);
        }
    }
}
