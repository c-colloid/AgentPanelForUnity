using System;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// "Will a layout component overwrite what I just wrote?" -- the check
    /// <see cref="UapRectTransformSetTool"/> runs so an agent that sets
    /// sizeDelta on a ContentSizeFitter-driven object is TOLD its write is
    /// about to be undone by the next layout rebuild, instead of watching
    /// the value silently revert and concluding the tool is broken.
    ///
    /// Matching is by type NAME (walking base types), never by referencing
    /// UnityEngine.UI: uGUI ships as a separate package (com.unity.ugui)
    /// that this package does not depend on and that neither CI host
    /// project installs, so naming LayoutGroup/ContentSizeFitter as types
    /// would not compile. Name matching also answers the question the
    /// agent actually has -- WHICH component to go and change -- which
    /// RectTransform's own driven-property flags cannot: they say only
    /// that something drives the field, and they are populated by a
    /// deferred canvas rebuild, so they read stale in exactly the moment
    /// after an Edit-mode write.
    /// </summary>
    public static class UapRectTransformLayoutDriver
    {
        /// <summary>Type name of uGUI's shared layout-group base class; every Horizontal/Vertical/Grid group derives from it.</summary>
        private const string LayoutGroupTypeName = "LayoutGroup";

        private static readonly string[] SelfDriverTypeNames = { "ContentSizeFitter", "AspectRatioFitter" };

        /// <summary>
        /// True when <paramref name="type"/> (or any of its base types) is a
        /// component that drives the RectTransform it sits ON -- a
        /// ContentSizeFitter or an AspectRatioFitter.
        /// </summary>
        public static bool IsSelfDriverType(Type type)
        {
            return MatchedName(type, SelfDriverTypeNames) != null;
        }

        /// <summary>
        /// True when <paramref name="type"/> (or any of its base types) is a
        /// layout group -- a component that drives its CHILDREN's
        /// RectTransforms. One name covers Horizontal/Vertical/Grid and any
        /// user subclass, because all of them derive from LayoutGroup.
        /// </summary>
        public static bool IsGroupDriverType(Type type)
        {
            return MatchedName(type, new[] { LayoutGroupTypeName }) != null;
        }

        /// <summary>
        /// The tool's field names (as they appear in its own schema) that
        /// the named driver takes over. Empty for a name that drives
        /// nothing this tool can write.
        /// </summary>
        public static string[] DrivenFieldsFor(string driverTypeName)
        {
            if (string.Equals(driverTypeName, "ContentSizeFitter", StringComparison.Ordinal))
            {
                return new[] { "sizeDelta" };
            }
            if (string.Equals(driverTypeName, "AspectRatioFitter", StringComparison.Ordinal))
            {
                return new[] { "sizeDelta", "anchoredPosition", "anchorMin", "anchorMax" };
            }
            if (string.Equals(driverTypeName, LayoutGroupTypeName, StringComparison.Ordinal))
            {
                return new[] { "anchoredPosition", "sizeDelta", "anchorMin", "anchorMax", "pivot" };
            }
            return new string[0];
        }

        /// <summary>
        /// The FIRST component on <paramref name="go"/> that drives its own
        /// RectTransform, or null. <paramref name="matchedBaseName"/>
        /// receives the base-class name the match was made on (the key
        /// <see cref="DrivenFieldsFor"/> expects), while the returned
        /// component carries the concrete type the user actually added.
        /// </summary>
        public static Component FindSelfDriver(GameObject go, out string matchedBaseName)
        {
            return FindDriver(go, SelfDriverTypeNames, out matchedBaseName);
        }

        /// <summary>The first layout-group component on <paramref name="go"/> (meant to be the PARENT of the rect being written), or null.</summary>
        public static Component FindGroupDriver(GameObject go, out string matchedBaseName)
        {
            return FindDriver(go, new[] { LayoutGroupTypeName }, out matchedBaseName);
        }

        private static Component FindDriver(GameObject go, string[] names, out string matchedBaseName)
        {
            matchedBaseName = null;
            if (go == null)
            {
                return null;
            }
            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                // A missing script leaves a null entry behind; skip it
                // rather than throwing on an object the user is mid-repair.
                if (components[i] == null)
                {
                    continue;
                }
                string matched = MatchedName(components[i].GetType(), names);
                if (matched != null)
                {
                    matchedBaseName = matched;
                    return components[i];
                }
            }
            return null;
        }

        /// <summary>Walks <paramref name="type"/> and its base types, returning the first name from <paramref name="names"/> that any of them is called.</summary>
        private static string MatchedName(Type type, string[] names)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(current.Name, names[i], StringComparison.Ordinal))
                    {
                        return names[i];
                    }
                }
            }
            return null;
        }
    }
}
