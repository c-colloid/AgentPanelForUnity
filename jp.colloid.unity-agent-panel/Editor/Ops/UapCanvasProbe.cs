using System;
using System.Reflection;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// "Which Canvas governs this RectTransform, and in which render mode?"
    /// -- the question that decides whether a layout write means pixels on a
    /// screen or metres in a room.
    ///
    /// It matters most in VR. A World Space canvas is a real object in the
    /// scene: its own RectTransform is authored, `sizeDelta` is a size in
    /// canvas-local units that `lossyScale` turns into metres, and writes to
    /// it stick. A Screen Space canvas is the opposite -- Unity recomputes
    /// the root rect from the screen every frame, so the same write is
    /// silently discarded. Reporting one as the other is worse than saying
    /// nothing, which is why the render mode is read rather than assumed.
    ///
    /// Read by REFLECTION, and the Canvas itself matched by type name, for
    /// the same reason <see cref="UapRectTransformLayoutDriver"/> does:
    /// Canvas lives in UnityEngine.UIModule, a built-in module a project is
    /// allowed to strip, and this package must keep compiling when it has
    /// been. The test assembly has no such constraint -- CI always installs
    /// com.unity.modules.ui -- so the tests drive a real Canvas.
    /// </summary>
    public static class UapCanvasProbe
    {
        private const string CanvasTypeName = "Canvas";
        private const string RenderModeProperty = "renderMode";

        /// <summary>Unity's RenderMode.WorldSpace, as ToString() spells it.</summary>
        public const string WorldSpaceMode = "WorldSpace";

        /// <summary>
        /// The nearest Canvas at or above <paramref name="start"/> -- the one
        /// uGUI would render this element under -- or null when the element
        /// is not under a Canvas at all.
        /// </summary>
        public static Component FindGoverningCanvas(Transform start)
        {
            for (Transform current = start; current != null; current = current.parent)
            {
                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null && IsCanvasType(components[i].GetType()))
                    {
                        return components[i];
                    }
                }
            }
            return null;
        }

        /// <summary>True when <paramref name="type"/>, or any base type, is called "Canvas".</summary>
        public static bool IsCanvasType(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(current.Name, CanvasTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The canvas's render mode as a string ("ScreenSpaceOverlay",
        /// "ScreenSpaceCamera" or "WorldSpace"), or null when it cannot be
        /// read -- a null means "unknown", never "screen space", and callers
        /// must hedge rather than assert.
        /// </summary>
        public static string ReadRenderMode(Component canvas)
        {
            if (canvas == null)
            {
                return null;
            }
            try
            {
                PropertyInfo property = canvas.GetType().GetProperty(RenderModeProperty,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null)
                {
                    return null;
                }
                object value = property.GetValue(canvas, null);
                return value == null ? null : value.ToString();
            }
            catch (Exception)
            {
                // A user type merely NAMED Canvas can have a renderMode of any
                // shape. Unknown is a safe answer; throwing here would fail a
                // layout call over a diagnostic.
                return null;
            }
        }

        /// <summary>True only when the render mode is known to be World Space (VR and in-world panels).</summary>
        public static bool IsWorldSpace(string renderMode)
        {
            return string.Equals(renderMode, WorldSpaceMode, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the render mode is known to be a SCREEN space one, i.e.
        /// Unity owns the root rect. Unknown returns false, so a caller never
        /// claims a write will be discarded on a guess.
        /// </summary>
        public static bool IsKnownScreenSpace(string renderMode)
        {
            return !string.IsNullOrEmpty(renderMode) && !IsWorldSpace(renderMode);
        }
    }
}
