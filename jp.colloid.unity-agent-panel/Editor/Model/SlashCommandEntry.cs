using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One slash command the CLI reports as available (design note
    /// docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 1). Mirrors one element of the initialize control_response
    /// commands[] array ({name, description, argumentHint}); a command
    /// known only by name (system/init slash_commands[]) has empty
    /// description/argumentHint. Unity-serializable (public fields) so
    /// PanelSettings.slashCommandCatalog survives an editor restart the
    /// same way modelCatalog does.
    /// </summary>
    [Serializable]
    public sealed class SlashCommandEntry
    {
        /// <summary>Command name WITHOUT the leading slash (e.g. "compact").</summary>
        public string name = string.Empty;

        /// <summary>CLI-supplied one-line description; may be empty.</summary>
        public string description = string.Empty;

        /// <summary>CLI-supplied argument hint (e.g. "[instructions]"); may be empty.</summary>
        public string argumentHint = string.Empty;
    }
}
