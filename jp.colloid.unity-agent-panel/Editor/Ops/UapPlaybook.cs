using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// One multi-tool procedure an agent can ask for through
    /// <c>uap_help topic:"&lt;id&gt;"</c> (design note
    /// docs/design-notes/2026-10-02-uap-help-playbooks.md). A tool's own
    /// description already reaches the agent through MCP tools/list; what no
    /// single description can carry is the ORDER several tools are used in,
    /// the decision points between them and what to do when a step fails.
    /// That is what a playbook holds, loaded only when asked for instead of
    /// riding the always-loaded steering text.
    ///
    /// Pure data, Unity-free (compiled by the license-free smoke harness).
    /// <see cref="UapPlaybookCatalog"/> owns validation, merging and the
    /// availability filter.
    /// </summary>
    public sealed class UapPlaybook
    {
        private static readonly string[] NoTools = new string[0];

        /// <summary>Stable kebab-case id, the value of uap_help's 'topic' (e.g. "verify-gameplay").</summary>
        public string Id { get; private set; }

        /// <summary>Short human-readable name.</summary>
        public string Title { get; private set; }

        /// <summary>One sentence: when to reach for this playbook.</summary>
        public string Summary { get; private set; }

        /// <summary>
        /// Wire names of the tools the procedure needs. The playbook is
        /// offered only when ALL of them are registered and belong to an
        /// enabled module (<see cref="UapPlaybookCatalog.Filter"/>), so a
        /// body can never send the agent to a tool that is missing.
        /// </summary>
        public string[] RequiredTools { get; private set; }

        /// <summary>Plain text / light markdown, English: the step list, decision points and failure handling.</summary>
        public string Body { get; private set; }

        public UapPlaybook(string id, string title, string summary, string[] requiredTools, string body)
        {
            Id = id;
            Title = title;
            Summary = summary;
            RequiredTools = requiredTools != null ? (string[])requiredTools.Clone() : NoTools;
            Body = body;
        }
    }
}
