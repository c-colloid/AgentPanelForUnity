using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Finds <see cref="IUapPlaybookProvider"/> implementations the same way
    /// ToolRegistry finds <see cref="IUapToolProvider"/>s (design note
    /// docs/design-notes/2026-10-02-uap-help-playbooks.md): TypeCache over
    /// loaded assemblies, PUBLIC non-abstract classes with a public
    /// parameterless constructor only (test stubs are private nested classes
    /// and must not leak into production), sorted by full type name for a
    /// deterministic merge order. Built lazily by
    /// <see cref="UapOpsServer.Playbooks"/>, never at load time.
    /// </summary>
    public static class UapPlaybookProviderDiscovery
    {
        /// <summary>
        /// A provider that fails to enumerate or construct is logged and
        /// skipped, never fatal.
        /// </summary>
        public static List<IUapPlaybookProvider> Discover()
        {
            var result = new List<IUapPlaybookProvider>();
            List<Type> types;
            try
            {
                types = TypeCache.GetTypesDerivedFrom<IUapPlaybookProvider>()
                    .Where(t => !t.IsAbstract && !t.IsInterface && (t.IsPublic || t.IsNestedPublic)
                        && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogError("[UapPlaybooks] Failed to enumerate IUapPlaybookProvider types via TypeCache: " + ex);
                return result;
            }
            foreach (Type type in types)
            {
                try
                {
                    result.Add((IUapPlaybookProvider)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    Debug.LogError("[UapPlaybooks] Failed to instantiate playbook provider '" + type.FullName + "'; skipped: " + ex);
                }
            }
            return result;
        }
    }
}
