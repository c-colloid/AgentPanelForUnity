using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Reads the MCP servers Claude Code itself knows about, for the
    /// Settings "Import from Claude Code" button (design note docs/design-
    /// notes/2026-09-27-mcp-servers-in-panel.md section 3): the user's
    /// `~/.claude.json` (its top-level `mcpServers` and the project-scoped
    /// `projects[&lt;root&gt;].mcpServers` that `claude mcp add` writes) and the
    /// project's `.mcp.json`. Read-only: the panel never writes either
    /// file. Import is a copy into PanelSettings.mcpServers; later edits
    /// in either place do not track each other.
    /// </summary>
    public static class McpServerImport
    {
        /// <summary>
        /// Every server found, project file first, in file order. Files
        /// that do not exist or cannot be parsed contribute nothing.
        /// <paramref name="homeDirectory"/> null falls back to the user
        /// profile folder.
        /// </summary>
        public static List<McpServerConfig> ReadFromDisk(string projectRoot, string homeDirectory = null)
        {
            var result = new List<McpServerConfig>();
            if (!string.IsNullOrEmpty(projectRoot))
            {
                result.AddRange(McpServerConfig.ReadAll(ReadText(Path.Combine(projectRoot, ".mcp.json")), null));
            }
            string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                result.AddRange(McpServerConfig.ReadAll(ReadText(Path.Combine(home, ".claude.json")), projectRoot));
            }
            return result;
        }

        /// <summary>
        /// Appends the entries of <paramref name="found"/> whose name is not
        /// already in <paramref name="into"/>; returns how many were added.
        /// </summary>
        public static int MergeNew(List<McpServerConfig> into, List<McpServerConfig> found)
        {
            if (into == null || found == null)
            {
                return 0;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < into.Count; i++)
            {
                if (into[i] != null)
                {
                    names.Add((into[i].name ?? string.Empty).Trim());
                }
            }
            int added = 0;
            for (int i = 0; i < found.Count; i++)
            {
                McpServerConfig entry = found[i];
                if (entry == null || !names.Add((entry.name ?? string.Empty).Trim()))
                {
                    continue;
                }
                into.Add(entry);
                added++;
            }
            return added;
        }

        private static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
