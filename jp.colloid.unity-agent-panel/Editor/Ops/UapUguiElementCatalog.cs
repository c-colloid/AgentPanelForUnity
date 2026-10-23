using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Enumerates the uGUI elements an agent can act on (uap_editor_
    /// screenshot's annotate_ui, docs/design-notes/2026-09-22-uloop-
    /// phase1.md section 2.1): every component under an ACTIVE Canvas that
    /// derives from UnityEngine.UI.Selectable (Button, Toggle, Slider,
    /// Dropdown, InputField, Scrollbar, and the TMPro equivalents, which
    /// all derive from the SAME Selectable base -- matching it once covers
    /// every one of them) or implements IPointerClickHandler / IDragHandler
    /// / IDropHandler.
    ///
    /// Every uGUI/TextMeshPro type is resolved by REFLECTION (type name,
    /// walking the inheritance chain via Type.IsInstanceOfType), never
    /// referenced at compile time -- the same rule UapUguiEventAccess and
    /// UapRectTransformLayoutDriver follow, for the same reason: uGUI ships
    /// as the separate com.unity.ugui package (EventSystems included) and
    /// TextMeshPro as another optional package, and neither CI host project
    /// installs them. RectTransform / RectTransformUtility / GameObject /
    /// Transform / Component are used directly (compile-time): they live in
    /// Unity's core Transform system and the always-referenced UI module
    /// respectively, exactly as UapRectTransformSetTool and
    /// UapGamePointerTool already do (see UapGamePointerTool.WidgetCentre /
    /// CanvasCamera -- the screen-space math below duplicates that small
    /// private helper rather than editing that file, per this task's scope
    /// rule).
    ///
    /// Canvas itself goes through UapCanvasProbe (name-matched, since a
    /// project is allowed to strip UnityEngine.UIModule and this package
    /// must still compile), never a compile-time UnityEngine.Canvas
    /// reference.
    /// </summary>
    public static class UapUguiElementCatalog
    {
        private const string SelectableTypeName = "UnityEngine.UI.Selectable";
        private const string ClickHandlerTypeName = "UnityEngine.EventSystems.IPointerClickHandler";
        private const string DragHandlerTypeName = "UnityEngine.EventSystems.IDragHandler";
        private const string DropHandlerTypeName = "UnityEngine.EventSystems.IDropHandler";
        private const string LegacyTextTypeName = "UnityEngine.UI.Text";
        private const string TmpTextTypeName = "TMPro.TMP_Text";

        /// <summary>
        /// Hard ceiling on how many GameObjects a single call will ever
        /// visit, independent of how many end up matching -- the same
        /// safety valve UapQueryHierarchyTool.MaxWalkBudget uses, so a
        /// pathologically large scene cannot make annotate_ui do unbounded
        /// work even though the RESULT is capped much lower.
        /// </summary>
        internal const int MaxWalkBudget = 20000;

        /// <summary>
        /// Walks every loaded scene and returns every matching element
        /// (unsorted, unlabeled, uncapped -- UapUiAnnotationPolicy.
        /// BuildResult does that). <paramref name="warning"/> is set (and
        /// the list is empty) only when uGUI is not in the project at all;
        /// a project with uGUI but nothing to list returns an empty list
        /// with a null warning.
        /// </summary>
        public static List<UapUiElementCandidate> Collect(out string warning)
        {
            warning = null;
            var results = new List<UapUiElementCandidate>();

            Type selectableType = FindType(SelectableTypeName);
            Type clickType = FindType(ClickHandlerTypeName);
            Type dragType = FindType(DragHandlerTypeName);
            Type dropType = FindType(DropHandlerTypeName);
            if (selectableType == null && clickType == null && dragType == null && dropType == null)
            {
                warning = "uGUI (UnityEngine.UI / UnityEngine.EventSystems, package com.unity.ugui) is not in"
                    + " this project, so no interactive UI elements can be listed.";
                return results;
            }
            Type textType = FindType(LegacyTextTypeName);
            Type tmpTextType = FindType(TmpTextTypeName);

            var seen = new HashSet<Component>();
            int budget = MaxWalkBudget;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                Scene scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length && budget > 0; r++)
                {
                    Walk(roots[r].transform, selectableType, clickType, dragType, dropType, textType, tmpTextType,
                        seen, results, ref budget);
                }
            }
            return results;
        }

        private static void Walk(Transform t, Type selectableType, Type clickType, Type dragType, Type dropType,
            Type textType, Type tmpTextType, HashSet<Component> seen, List<UapUiElementCandidate> results, ref int budget)
        {
            if (budget <= 0)
            {
                return;
            }
            budget--;

            Component[] components = t.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null || seen.Contains(c))
                {
                    continue;
                }
                bool isSelectable = selectableType != null && selectableType.IsInstanceOfType(c);
                bool supportsClick = clickType != null && clickType.IsInstanceOfType(c);
                bool supportsDrag = dragType != null && dragType.IsInstanceOfType(c);
                bool supportsDrop = dropType != null && dropType.IsInstanceOfType(c);
                if (!isSelectable && !supportsClick && !supportsDrag && !supportsDrop)
                {
                    continue;
                }
                seen.Add(c);
                Component canvas;
                if (!IsUnderActiveCanvas(c.gameObject, out canvas))
                {
                    continue;
                }
                results.Add(BuildCandidate(c, canvas, isSelectable, supportsClick, supportsDrag, supportsDrop, textType, tmpTextType));
            }

            for (int i = 0; i < t.childCount && budget > 0; i++)
            {
                Walk(t.GetChild(i), selectableType, clickType, dragType, dropType, textType, tmpTextType, seen, results, ref budget);
            }
        }

        private static UapUiElementCandidate BuildCandidate(Component c, Component canvas, bool isSelectable,
            bool supportsClick, bool supportsDrag, bool supportsDrop, Type textType, Type tmpTextType)
        {
            bool interactable = true;
            if (isSelectable)
            {
                PropertyInfo interactableProperty = c.GetType().GetProperty("interactable", BindingFlags.Public | BindingFlags.Instance);
                object value = interactableProperty != null ? interactableProperty.GetValue(c, null) : null;
                interactable = !(value is bool) || (bool)value;
            }

            Camera camera = CanvasCamera(canvas);
            RectTransform rect = c.GetComponent<RectTransform>();
            var cornersX = new double[4];
            var cornersY = new double[4];
            double centreScreenX;
            double centreScreenY;
            if (rect != null)
            {
                var worldCorners = new Vector3[4];
                rect.GetWorldCorners(worldCorners);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, worldCorners[i]);
                    cornersX[i] = screen.x;
                    cornersY[i] = screen.y;
                }
                Vector3 centreWorld = (worldCorners[0] + worldCorners[2]) * 0.5f;
                Vector2 centreScreen = RectTransformUtility.WorldToScreenPoint(camera, centreWorld);
                centreScreenX = centreScreen.x;
                centreScreenY = centreScreen.y;
            }
            else
            {
                // No RectTransform (e.g. a plain-Transform IPointerClickHandler):
                // a single point stands in for all four corners, matching
                // UapGamePointerTool.WidgetCentre's own fallback.
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, c.transform.position);
                for (int i = 0; i < 4; i++)
                {
                    cornersX[i] = screen.x;
                    cornersY[i] = screen.y;
                }
                centreScreenX = screen.x;
                centreScreenY = screen.y;
            }

            return new UapUiElementCandidate
            {
                CanvasSortingOrder = ReadCanvasSortingOrder(canvas),
                SiblingPath = SiblingPath(c.transform),
                HierarchyPath = UapAddressing.DescribeHierarchyPath(c.transform),
                ComponentType = c.GetType().FullName,
                Interactable = interactable,
                Text = ReadVisibleText(c, textType, tmpTextType),
                ScreenCentreX = centreScreenX,
                ScreenCentreY = centreScreenY,
                CornerScreenX = cornersX,
                CornerScreenY = cornersY,
                SupportsClick = supportsClick,
                SupportsDrag = supportsDrag,
                SupportsDrop = supportsDrop
            };
        }

        private static string ReadVisibleText(Component c, Type textType, Type tmpTextType)
        {
            if (textType != null)
            {
                Component text = c.GetComponentInChildren(textType);
                string value = text != null ? ReadStringProperty(text, "text") : null;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            if (tmpTextType != null)
            {
                Component tmp = c.GetComponentInChildren(tmpTextType);
                string value = tmp != null ? ReadStringProperty(tmp, "text") : null;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }

        private static string ReadStringProperty(object target, string propertyName)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object value = property != null ? property.GetValue(target, null) : null;
                return value as string;
            }
            catch (Exception)
            {
                // Reading a diagnostic label is never worth failing the whole listing over.
                return null;
            }
        }

        /// <summary>
        /// Sibling index at each level from the scene root down to
        /// <paramref name="t"/>, for UapUiAnnotationPolicy.CompareOrder's
        /// document-order tie-break. Internal so tests can pin it directly
        /// with plain GameObjects (no uGUI needed).
        /// </summary>
        internal static int[] SiblingPath(Transform t)
        {
            var indices = new List<int>();
            for (Transform current = t; current != null; current = current.parent)
            {
                indices.Add(current.GetSiblingIndex());
            }
            indices.Reverse();
            return indices.ToArray();
        }

        /// <summary>
        /// True when <paramref name="go"/> is active in the hierarchy AND
        /// its nearest governing Canvas (if any) is itself enabled --
        /// "active in the hierarchy" already implies the canvas's own
        /// GameObject is active, since the canvas is necessarily an
        /// ancestor; only the Canvas COMPONENT's own enabled flag needs a
        /// separate check. Internal so tests can pin it with plain
        /// GameObjects (a GameObject with no Canvas ancestor at all always
        /// answers false, uGUI or not).
        /// </summary>
        internal static bool IsUnderActiveCanvas(GameObject go, out Component canvas)
        {
            canvas = null;
            if (go == null || !go.activeInHierarchy)
            {
                return false;
            }
            canvas = UapCanvasProbe.FindGoverningCanvas(go.transform);
            if (canvas == null)
            {
                return false;
            }
            // Canvas : Behaviour is a stable part of Unity's public API
            // shape (unlike the TYPE ITSELF, which UapCanvasProbe avoids
            // referencing because UnityEngine.UIModule is a strippable
            // module) -- this cast needs no reflection.
            var behaviour = canvas as Behaviour;
            return behaviour == null || behaviour.enabled;
        }

        /// <summary>
        /// Duplicated from UapGamePointerTool.CanvasCamera (private there;
        /// this task's scope rules say duplicate a small helper rather than
        /// edit that file): the camera a Screen Space - Camera or World
        /// Space canvas renders through, or null for Screen Space -
        /// Overlay, which is what RectTransformUtility.WorldToScreenPoint
        /// wants for it.
        /// </summary>
        private static Camera CanvasCamera(Component canvas)
        {
            if (canvas == null)
            {
                return Camera.main;
            }
            string renderMode = UapCanvasProbe.ReadRenderMode(canvas);
            if (renderMode == "ScreenSpaceOverlay")
            {
                return null;
            }
            PropertyInfo worldCamera = canvas.GetType().GetProperty("worldCamera", BindingFlags.Public | BindingFlags.Instance);
            Camera camera = worldCamera != null ? worldCamera.GetValue(canvas, null) as Camera : null;
            return camera != null ? camera : Camera.main;
        }

        private static int ReadCanvasSortingOrder(Component canvas)
        {
            if (canvas == null)
            {
                return 0;
            }
            PropertyInfo property = canvas.GetType().GetProperty("sortingOrder", BindingFlags.Public | BindingFlags.Instance);
            object value = property != null ? property.GetValue(canvas, null) : null;
            return value is int ? (int)value : 0;
        }

        /// <summary>
        /// Duplicated from UapUguiEventAccess.FindType (internal there --
        /// this task's scope rules say duplicate a small helper rather than
        /// take a dependency on that file): finds a type by full name
        /// across every loaded assembly.
        /// </summary>
        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type;
                try
                {
                    type = assemblies[i].GetType(fullName, false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }
}
