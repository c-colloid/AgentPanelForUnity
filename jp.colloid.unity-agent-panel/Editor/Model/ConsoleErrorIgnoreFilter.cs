using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure decision logic behind the Console-error chip's content-based
    /// ignore feature (docs/design-notes/2026-08-13-error-chip-ignore.md):
    /// whether a captured error message should stay hidden from the chip
    /// and its "Ask Claude to fix" digest, and the bookkeeping behind the
    /// two ways a message gets there -- an exact match persisted by the
    /// chip's X button (PanelSettings.ignoredConsoleErrors), or a free-text
    /// substring pattern configured in Settings
    /// (PanelSettings.ignoredConsoleErrorPatterns). Kept separate from
    /// ConsoleErrorProvider/PanelSettings (no UnityEditor/ScriptableObject
    /// dependency at all) so every branch here is EditMode-testable without
    /// either the editor log pipeline or a live PanelStateStore singleton.
    /// </summary>
    public static class ConsoleErrorIgnoreFilter
    {
        /// <summary>
        /// FIFO bound on PanelSettings.ignoredConsoleErrors (design note
        /// decision 1): a project whose user keeps X-ing distinct SDK/
        /// extension noise over months must not grow the settings asset
        /// without limit -- once full, the OLDEST ignored message is
        /// evicted to make room for a newly ignored one.
        /// </summary>
        public const int IgnoredConsoleErrorsMax = 200;

        /// <summary>
        /// True when `message` should be hidden: an Ordinal EXACT match
        /// against `exactIgnores` (the X-button store) OR an Ordinal
        /// SUBSTRING match against any non-empty entry of `patterns` (the
        /// free-text Settings field). Ordinal throughout, never culture-
        /// aware -- the design note rejected anything fancier (regex is a
        /// user-hostile failure mode for a free-text field; SDK noise is
        /// plain ASCII exception text that does not need locale-aware
        /// comparison, and Ordinal is the one comparison that cannot vary
        /// by machine locale). Null/empty `message` is never ignored (there
        /// is nothing to match).
        /// </summary>
        public static bool IsIgnored(string message, IList<string> exactIgnores, IList<string> patterns)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }
            if (exactIgnores != null)
            {
                for (int i = 0; i < exactIgnores.Count; i++)
                {
                    if (string.Equals(exactIgnores[i], message, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            if (patterns != null)
            {
                for (int i = 0; i < patterns.Count; i++)
                {
                    string pattern = patterns[i];
                    if (!string.IsNullOrEmpty(pattern)
                        && message.IndexOf(pattern, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Splits the Settings free-text patterns field into one substring
        /// per non-blank, trimmed line -- the same shape as SettingsView.
        /// SplitLines (allowed/disallowedTools) but kept as an independent
        /// copy here so this Model-layer class carries no dependency on
        /// Editor/UI. Null/empty input yields an empty list, never null.
        /// </summary>
        public static List<string> ParsePatterns(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(raw))
            {
                return result;
            }
            string[] lines = raw.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
            return result;
        }

        /// <summary>
        /// The chip X button's persistence step: appends every distinct,
        /// non-empty message in `messages` to `store` (skipping ones
        /// already present, Ordinal via List&lt;string&gt;.Contains's default
        /// comparer), then evicts from the FRONT (oldest first, matching
        /// ConsoleErrorProvider.Snapshot's own "oldest first" ordering)
        /// until `store` is back within `maxEntries`. Mutates `store` in
        /// place -- the caller (ContextBarView) still owns persisting it via
        /// PanelStateStore.SaveNow(). `maxEntries` &lt;= 0 is clamped to 0
        /// (evicts everything) rather than looping forever removing from an
        /// already-empty list.
        /// </summary>
        public static void AddIgnores(List<string> store, IEnumerable<string> messages, int maxEntries)
        {
            if (store == null || messages == null)
            {
                return;
            }
            foreach (string message in messages)
            {
                if (!string.IsNullOrEmpty(message) && !store.Contains(message))
                {
                    store.Add(message);
                }
            }
            int cap = maxEntries < 0 ? 0 : maxEntries;
            while (store.Count > cap)
            {
                store.RemoveAt(0);
            }
        }
    }
}
