using System;
using Colloid.AgentPanel.UI;
using UnityEditor;
using UnityEngine.UIElements;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Accepts drag &amp; drop from the Project/Hierarchy windows -- and,
    /// since the 2026-09-07 image-attachment work, image files from the
    /// OS file manager and Texture assets -- anywhere on the panel (the
    /// whole ChatView root: message list, context bar and composer) and
    /// reports the drop as a <see cref="DropPayload"/>: objects for the
    /// context bar's chips, image paths for the composer's attachments.
    ///
    /// While a drag with valid object references hovers the target, a
    /// full-panel overlay ("Drop to attach") appears. The overlay and all
    /// of its children use PickingMode.Ignore, so it can never intercept
    /// the drop or block clicks -- and it only becomes visible during a
    /// drag, so permission cards and buttons stay fully interactive the
    /// rest of the time. Drags without object references (and drags
    /// outside the panel) are untouched: the handler neither sets
    /// DragAndDrop.visualMode nor shows the overlay for them.
    ///
    /// Uses the editor DragAndDrop API through the UI Toolkit drag
    /// events; purely event-driven, nothing runs per frame.
    /// </summary>
    public sealed class DragDropAttachHandler
    {
        private readonly VisualElement _target;
        private readonly Action<DropPayload> _onDropped;
        private readonly VisualElement _overlayHost;
        private VisualElement _overlay;

        /// <summary>Registers drag handling on the target element.</summary>
        public DragDropAttachHandler(VisualElement target,
            Action<DropPayload> onDropped)
            : this(target, onDropped, null)
        {
        }

        /// <summary>
        /// Registers drag handling on the target element and shows a
        /// drop-target overlay inside overlayHost (usually the same
        /// element) while a valid drag hovers it. overlayHost null
        /// disables the overlay.
        /// </summary>
        public DragDropAttachHandler(VisualElement target,
            Action<DropPayload> onDropped, VisualElement overlayHost)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }
            _target = target;
            _onDropped = onDropped;
            _overlayHost = overlayHost;
            _target.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            _target.RegisterCallback<DragPerformEvent>(OnDragPerform);
            _target.RegisterCallback<DragLeaveEvent>(OnDragLeave);
            _target.RegisterCallback<DragExitedEvent>(OnDragExited);
        }

        /// <summary>Unregisters the callbacks (view teardown).</summary>
        public void Detach()
        {
            _target.UnregisterCallback<DragUpdatedEvent>(OnDragUpdated);
            _target.UnregisterCallback<DragPerformEvent>(OnDragPerform);
            _target.UnregisterCallback<DragLeaveEvent>(OnDragLeave);
            _target.UnregisterCallback<DragExitedEvent>(OnDragExited);
            if (_overlay != null && _overlay.parent != null)
            {
                _overlay.parent.Remove(_overlay);
            }
            _overlay = null;
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (!HasDroppable())
            {
                return;
            }
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            ShowOverlay(true);
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            ShowOverlay(false);
            if (!HasDroppable())
            {
                return;
            }
            DragAndDrop.AcceptDrag();
            evt.StopPropagation();
            if (_onDropped != null)
            {
                string projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(UnityEngine.Application.dataPath, ".."));
                DropPayload payload = DropPayloadClassifier.Classify(
                    DragAndDrop.objectReferences, DragAndDrop.paths, AssetDatabase.GetAssetPath, projectRoot);
                if (!payload.IsEmpty)
                {
                    _onDropped(payload);
                }
            }
        }

        private void OnDragLeave(DragLeaveEvent evt)
        {
            ShowOverlay(false);
        }

        /// <summary>Drag ended without a drop here (release/Escape).</summary>
        private void OnDragExited(DragExitedEvent evt)
        {
            ShowOverlay(false);
        }

        // -- Overlay ---------------------------------------------------------

        private void ShowOverlay(bool visible)
        {
            if (_overlayHost == null)
            {
                return;
            }
            if (_overlay == null)
            {
                if (!visible)
                {
                    return;
                }
                _overlay = BuildOverlay();
                _overlayHost.Add(_overlay);
            }
            _overlay.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
            {
                _overlay.BringToFront();
            }
        }

        /// <summary>
        /// Full-panel absolute overlay: border frame + centered
        /// "Drop to attach" label with a paperclip glyph. Every element
        /// ignores picking so the drop lands on the target below.
        /// </summary>
        private static VisualElement BuildOverlay()
        {
            var overlay = new VisualElement();
            overlay.AddToClassList("uap-droptarget");
            overlay.pickingMode = PickingMode.Ignore;

            var box = new VisualElement();
            box.AddToClassList("uap-droptarget-box");
            box.pickingMode = PickingMode.Ignore;

            // Attachment icon (the old paperclip U+1F4CE is outside the
            // editor font coverage; IconLoader supplies a built-in icon
            // with an ASCII text fallback).
            VisualElement glyph = IconLoader.CreateIcon(
                IconLoader.IconNameAttachment, IconLoader.GlyphAttachAscii,
                "uap-droptarget-icon", "uap-droptarget-glyph");
            glyph.pickingMode = PickingMode.Ignore;
            box.Add(glyph);

            var label = new Label(L10n.S.DragDropOverlayLabel);
            label.enableRichText = false;
            label.AddToClassList("uap-droptarget-label");
            label.pickingMode = PickingMode.Ignore;
            box.Add(label);

            overlay.Add(box);
            return overlay;
        }

        /// <summary>
        /// Objects from Project/Hierarchy, or image files from the OS (which
        /// arrive with no object references and only DragAndDrop.paths --
        /// the case the old objects-only check silently ignored).
        /// </summary>
        private static bool HasDroppable()
        {
            return DropPayloadClassifier.HasDroppable(DragAndDrop.objectReferences, DragAndDrop.paths);
        }
    }
}
