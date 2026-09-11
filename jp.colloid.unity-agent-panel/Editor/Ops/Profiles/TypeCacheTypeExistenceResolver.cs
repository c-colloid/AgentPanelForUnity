using System;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Production <see cref="IUapTypeExistenceResolver"/>: scans TypeCache
    /// the same way <see cref="Colloid.AgentPanel.Ops.UapComponentTypeResolver"/>
    /// and <see cref="Colloid.AgentPanel.Ops.UapQueryComponentTypesTool"/> do,
    /// but for plain existence rather than resolving to a single Type --
    /// detection only needs to know "does at least one compiled type match
    /// this name", so an ambiguous short name (several types sharing it)
    /// still counts as detected rather than erroring.
    /// </summary>
    public sealed class TypeCacheTypeExistenceResolver : IUapTypeExistenceResolver
    {
        public bool TypeExists(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }
            return MatchesAny(TypeCache.GetTypesDerivedFrom<Component>(), typeName)
                || MatchesAny(TypeCache.GetTypesDerivedFrom<ScriptableObject>(), typeName);
        }

        private static bool MatchesAny(TypeCache.TypeCollection types, string typeName)
        {
            bool qualified = typeName.IndexOf('.') >= 0;
            foreach (Type t in types)
            {
                if (qualified)
                {
                    if (string.Equals(t.FullName, typeName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (string.Equals(t.Name, typeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
