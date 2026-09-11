using System.Text;
using Colloid.AgentPanel.UI;
using UnityEditor;
using UnityEngine;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Turns the current Hierarchy/Project selection into a compact text
    /// summary for a context chip (R05 section 4.1). GameObjects yield
    /// name, scene path, active state and component type names; assets
    /// yield path and type. Summaries are capped at 2 KB. Main thread
    /// only (Selection/AssetDatabase); all work happens on demand -- no
    /// polling, no per-frame allocations.
    /// </summary>
    public static class SelectionContextProvider
    {
        private const int MaxObjects = 5;
        private const int MaxComponents = 24;
        private const int MaxChipLabelChars = 24;

        /// <summary>True when anything is selected (chip/attach gating).</summary>
        public static bool HasSelection
        {
            get { return Selection.activeObject != null; }
        }

        /// <summary>Short chip label: first object name plus a +N suffix.</summary>
        public static string BuildChipLabel()
        {
            Object active = Selection.activeObject;
            if (active == null)
            {
                return null;
            }
            string name = string.IsNullOrEmpty(active.name) ? L10n.S.CtxUnnamedFallback : active.name;
            if (name.Length > MaxChipLabelChars)
            {
                name = name.Substring(0, MaxChipLabelChars - 3) + "...";
            }
            int extra = Selection.objects != null ? Selection.objects.Length - 1 : 0;
            return extra > 0 ? name + L10n.F(L10n.S.CtxSelectionExtraSuffixFmt, extra) : name;
        }

        /// <summary>
        /// Full context block for the current selection, or null when
        /// nothing is selected. At most MaxObjects objects are described;
        /// the rest collapse into a count line. Capped at 2 KB.
        /// </summary>
        public static string BuildSummary()
        {
            Object[] objects = Selection.objects;
            if (objects == null || objects.Length == 0)
            {
                Object active = Selection.activeObject;
                if (active == null)
                {
                    return null;
                }
                objects = new[] { active };
            }

            var sb = new StringBuilder(256);
            sb.Append(objects.Length == 1
                ? L10n.S.CtxSelectionSummaryHeaderSingularFmt
                : L10n.F(L10n.S.CtxSelectionSummaryHeaderPluralFmt, objects.Length));
            int described = 0;
            for (int i = 0; i < objects.Length && described < MaxObjects; i++)
            {
                string entry = DescribeObject(objects[i]);
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }
                sb.Append('\n').Append(entry);
                described++;
            }
            if (described == 0)
            {
                return null;
            }
            if (objects.Length > described)
            {
                sb.Append('\n').Append(L10n.F(L10n.S.CtxSelectionMoreObjectsFmt,
                    objects.Length - described));
            }
            return ContextBlockFormatter.TruncateBlock(
                sb.ToString(), ContextBlockFormatter.MaxBlockChars);
        }

        /// <summary>
        /// One-object summary shared by the selection chip and drag-drop
        /// chips: scene GameObjects get the hierarchy treatment, anything
        /// with an asset path gets the path + type line. Null for null.
        /// </summary>
        public static string DescribeObject(Object obj)
        {
            if (obj == null)
            {
                return null;
            }
            var gameObject = obj as GameObject;
            if (gameObject != null && gameObject.scene.IsValid())
            {
                return DescribeGameObject(gameObject);
            }
            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path))
            {
                return L10n.F(L10n.S.CtxSelectionAssetFmt, path, obj.GetType().Name);
            }
            return L10n.F(L10n.S.CtxSelectionGenericFmt, obj.name, obj.GetType().Name);
        }

        private static string DescribeGameObject(GameObject gameObject)
        {
            var sb = new StringBuilder(128);
            sb.Append(L10n.F(L10n.S.CtxGameObjectTitleFmt, BuildHierarchyPath(gameObject)));
            string sceneName = string.IsNullOrEmpty(gameObject.scene.name)
                ? L10n.S.CtxSceneUntitledFallback : gameObject.scene.name;
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxSelectionSceneLabelFmt, sceneName));
            string activeLabel = gameObject.activeSelf ? L10n.S.CtxSelectionYes : L10n.S.CtxSelectionNo;
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxSelectionActiveLabelFmt, activeLabel));
            if (gameObject.activeSelf && !gameObject.activeInHierarchy)
            {
                sb.Append(L10n.S.CtxSelectionInactiveInHierarchy);
            }

            Component[] components = gameObject.GetComponents<Component>();
            sb.Append("\n  ").Append(L10n.S.CtxSelectionComponentsLabel);
            int listed = 0;
            for (int i = 0; i < components.Length && listed < MaxComponents; i++)
            {
                if (listed > 0)
                {
                    sb.Append(", ");
                }
                // A destroyed/missing script surfaces as a null slot.
                sb.Append(components[i] == null
                    ? L10n.S.CtxSelectionMissingScript : components[i].GetType().Name);
                listed++;
            }
            if (components.Length > MaxComponents)
            {
                sb.Append(L10n.F(L10n.S.CtxSelectionMoreComponentsFmt, components.Length - MaxComponents));
            }
            return sb.ToString();
        }

        private static string BuildHierarchyPath(GameObject gameObject)
        {
            var sb = new StringBuilder(64);
            Transform node = gameObject.transform;
            while (node != null)
            {
                if (sb.Length > 0)
                {
                    sb.Insert(0, '/');
                }
                sb.Insert(0, node.name);
                node = node.parent;
            }
            sb.Insert(0, '/');
            return sb.ToString();
        }
    }
}
