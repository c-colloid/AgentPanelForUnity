using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Resolves a short type name (e.g. "BoxCollider") or a fully-qualified
    /// name (e.g. "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone") to a
    /// real compiled Type via TypeCache (design section 3b: "type discovery"
    /// -- component_add and asset_create both resolve a user/agent-supplied
    /// string this way instead of requiring a hardcoded whitelist). Backs
    /// uap_query_component_types too.
    /// </summary>
    public static class UapComponentTypeResolver
    {
        public static Type ResolveComponentType(string nameOrFqn, out string error)
        {
            return Resolve(TypeCache.GetTypesDerivedFrom<Component>(), nameOrFqn, "component", out error);
        }

        public static Type ResolveScriptableObjectType(string nameOrFqn, out string error)
        {
            return Resolve(TypeCache.GetTypesDerivedFrom<ScriptableObject>(), nameOrFqn, "ScriptableObject", out error);
        }

        private static Type Resolve(TypeCache.TypeCollection candidates, string nameOrFqn, string kindLabel,
            out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(nameOrFqn))
            {
                error = kindLabel + " type name is required.";
                return null;
            }
            bool looksQualified = nameOrFqn.IndexOf('.') >= 0;
            var shortNameMatches = new List<Type>();
            foreach (Type t in candidates)
            {
                if (looksQualified && string.Equals(t.FullName, nameOrFqn, StringComparison.Ordinal))
                {
                    return t;
                }
                if (!looksQualified && string.Equals(t.Name, nameOrFqn, StringComparison.Ordinal))
                {
                    shortNameMatches.Add(t);
                }
            }
            if (shortNameMatches.Count == 1)
            {
                return shortNameMatches[0];
            }
            if (shortNameMatches.Count > 1)
            {
                var sb = new StringBuilder();
                sb.Append("Ambiguous ").Append(kindLabel).Append(" type name '").Append(nameOrFqn)
                    .Append("'; specify the fully-qualified name: ");
                for (int i = 0; i < shortNameMatches.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(shortNameMatches[i].FullName);
                }
                error = sb.ToString();
                return null;
            }
            error = "Unknown " + kindLabel + " type: " + nameOrFqn;
            return null;
        }
    }
}
