using System;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using UnityEditor;
using L10n = Colloid.AgentPanel.UI.L10n;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// The one place that words a compaction for the transcript (design
    /// note docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 2.3), shared by the live path (AgentHub, on
    /// system/compact_boundary) and the restore path (TranscriptLoader, on
    /// the same line read back from the JSONL) so a session looks the
    /// same whether it was watched live or reopened from History. Lives
    /// in Integration, not Model, because it reads L10n (D9 one-way
    /// layering: Model must not reference UI); TranscriptLoader reaches it
    /// through its CompactionDescriber seam, installed at editor load the
    /// same way ImageAttachmentStore installs ImageSaver.
    /// </summary>
    public static class CompactionNote
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            TranscriptLoader.CompactionDescriber = Describe;
        }

        /// <summary>
        /// Localized note text. <paramref name="trigger"/> is the wire
        /// value ("manual" | "auto" | anything else, treated as auto: an
        /// unknown trigger still means the CLI did it, not the user);
        /// <paramref name="preTokens"/> &lt; 0 omits the token clause.
        /// </summary>
        public static string Describe(string trigger, long preTokens)
        {
            bool manual = string.Equals(trigger, SystemCompactBoundaryMessage.TriggerManual,
                StringComparison.Ordinal);
            if (preTokens < 0)
            {
                return manual ? L10n.S.HubCompactedManual : L10n.S.HubCompactedAuto;
            }
            string tokens = TokenCountFormat.Short(preTokens);
            return manual
                ? L10n.F(L10n.S.HubCompactedManualFmt, tokens)
                : L10n.F(L10n.S.HubCompactedAutoFmt, tokens);
        }
    }
}
