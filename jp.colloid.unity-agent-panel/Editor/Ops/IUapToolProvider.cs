using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The open-core registration seam (docs/design-notes/2026-09-11-core-pro-split.md
    /// section "seam 1"): an add-on package (Agent Panel Pro, or any other
    /// third party) implements this on a PUBLIC, non-abstract class with a public
    /// parameterless constructor, and <see cref="ToolRegistry.CreateDefault(bool)"/>
    /// discovers it via <c>UnityEditor.TypeCache.GetTypesDerivedFrom&lt;IUapToolProvider&gt;()</c>
    /// and registers every tool it yields through the same
    /// <c>RegisterUnlessCovered</c> path Core's own tools go through -- a
    /// provider tool competes for module toggles, uLoop coverage, and name
    /// uniqueness exactly like a built-in one.
    /// </summary>
    public interface IUapToolProvider
    {
        /// <summary>
        /// Fresh <see cref="IUapTool"/> instances to register. Called once
        /// per <see cref="ToolRegistry.CreateDefault(bool)"/> call; a
        /// provider should return new instances each time rather than
        /// caching a shared list, since a registry only ever registers a
        /// tool object once.
        /// </summary>
        IEnumerable<IUapTool> CreateTools();
    }
}
