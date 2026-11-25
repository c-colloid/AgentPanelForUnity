using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The playbook twin of <see cref="IUapToolProvider"/> (design note
    /// docs/design-notes/2026-10-02-uap-help-playbooks.md): an add-on
    /// package implements this on a PUBLIC, non-abstract class with a
    /// public parameterless constructor and
    /// <c>UapPlaybookProviderDiscovery</c> finds it through
    /// <c>UnityEditor.TypeCache</c> exactly the way
    /// <see cref="ToolRegistry.CreateDefault()"/> finds tool providers.
    /// Whatever it yields is merged into <see cref="UapPlaybookCatalog"/>
    /// after Core's own playbooks; an id that is already taken (by Core or
    /// by an earlier provider) is rejected with a log line, so an add-on
    /// can never replace a Core procedure.
    /// </summary>
    public interface IUapPlaybookProvider
    {
        /// <summary>
        /// Fresh <see cref="UapPlaybook"/> instances to merge. Called once
        /// per catalog build; list every tool the body names in
        /// <see cref="UapPlaybook.RequiredTools"/> so the playbook disappears
        /// when one of them is not available.
        /// </summary>
        IEnumerable<UapPlaybook> CreatePlaybooks();
    }
}
