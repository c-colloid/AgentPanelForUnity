using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Opens the OS terminal with the interactive Claude Code CLI on the
    /// panel's own MCP config (design note docs/design-notes/2026-09-27-
    /// mcp-servers-in-panel.md section 4): the one thing the CLI's /mcp
    /// screen does that stream-json mode cannot -- an OAuth login for a
    /// remote server -- has to happen in a real terminal, and the token it
    /// stores is then used by the panel's CLI too. <see cref="Build"/> is
    /// the pure, per-platform command line (unit-tested);
    /// <see cref="Launch"/> starts it.
    /// </summary>
    public static class TerminalLauncher
    {
        public struct Launch
        {
            public string FileName;
            public string Arguments;
        }

        /// <summary>
        /// The interactive command the terminal runs: the CLI on the
        /// panel's config with `--strict-mcp-config`, so the terminal
        /// session sees exactly the servers the panel session sees (and
        /// no others), and a `/mcp` there authenticates the right one.
        /// Quoted for the target shell.
        /// </summary>
        public static string BuildCliCommand(RuntimePlatform platform, string cliPath, string mcpConfigPath)
        {
            bool windows = platform == RuntimePlatform.WindowsEditor;
            var sb = new StringBuilder();
            sb.Append(Quote(cliPath, windows));
            if (!string.IsNullOrEmpty(mcpConfigPath))
            {
                sb.Append(" --mcp-config ").Append(Quote(mcpConfigPath, windows)).Append(" --strict-mcp-config");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Windows: a new cmd window that keeps running the CLI (`start`
        /// + `cmd /k`). macOS: Terminal.app via osascript, which also
        /// brings it to front. Linux: `x-terminal-emulator` (the Debian
        /// alternatives name most distributions provide), the command
        /// wrapped in a shell so the window stays open after the CLI
        /// exits.
        /// </summary>
        public static Launch Build(RuntimePlatform platform, string cliPath, string mcpConfigPath)
        {
            string command = BuildCliCommand(platform, cliPath, mcpConfigPath);
            switch (platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return new Launch
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c start \"Claude Code\" cmd.exe /k " + command
                    };
                case RuntimePlatform.OSXEditor:
                    return new Launch
                    {
                        FileName = "/usr/bin/osascript",
                        Arguments = "-e " + Quote("tell application \"Terminal\" to do script "
                            + AppleScriptString(command), false)
                            + " -e " + Quote("tell application \"Terminal\" to activate", false)
                    };
                default:
                    return new Launch
                    {
                        FileName = "x-terminal-emulator",
                        Arguments = "-e " + Quote("bash -c " + Quote(command + "; exec bash", false), false)
                    };
            }
        }

        /// <summary>Starts <see cref="Build"/>'s command; returns null on success, else the failure text.</summary>
        public static string Start(RuntimePlatform platform, string cliPath, string mcpConfigPath)
        {
            Launch launch = Build(platform, cliPath, mcpConfigPath);
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = launch.FileName,
                    Arguments = launch.Arguments,
                    UseShellExecute = false,
                    CreateNoWindow = platform != RuntimePlatform.WindowsEditor
                };
                using (Process process = Process.Start(info))
                {
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string Quote(string value, bool windows)
        {
            value = value ?? string.Empty;
            if (windows)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";
        }

        private static string AppleScriptString(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
