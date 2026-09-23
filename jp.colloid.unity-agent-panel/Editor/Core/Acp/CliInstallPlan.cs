using System;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// What went wrong with an install run, classified so the UI can give
    /// a next step instead of an exit code (design note
    /// docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md section 1).
    /// </summary>
    public enum CliInstallFailureKind
    {
        None,
        /// <summary>npm (Node.js) is not on PATH -- the user must install Node.js first.</summary>
        NodeMissing,
        /// <summary>curl / PowerShell / bash could not be found or the shell refused to run.</summary>
        ShellMissing,
        /// <summary>The installer ran and failed; the last output line carries the detail.</summary>
        CommandFailed,
        /// <summary>The run exceeded the timeout and was killed.</summary>
        TimedOut
    }

    /// <summary>
    /// The exact process an in-panel install runs for one backend on one
    /// platform: pure data, computed by <see cref="CliInstallPlan.Build"/>
    /// and shown to the user verbatim before it runs. Claude Code uses
    /// Anthropic's official native installer (the same one the CLI docs
    /// give: `curl -fsSL https://claude.ai/install.sh | bash` and
    /// `irm https://claude.ai/install.ps1 | iex`); the ACP agents are npm
    /// packages installed globally. Unity-free.
    /// </summary>
    public sealed class CliInstallPlan
    {
        public const string ClaudeInstallShUrl = "https://claude.ai/install.sh";
        public const string ClaudeInstallPs1Url = "https://claude.ai/install.ps1";
        /// <summary>xAI's official Grok Build installer (installs to ~/.grok/bin; verified 2026-09-10).</summary>
        public const string GrokInstallShUrl = "https://x.ai/cli/install.sh";
        public const string GrokInstallPs1Url = "https://x.ai/cli/install.ps1";
        public const string NodeDownloadUrl = "https://nodejs.org/";

        /// <summary>Upper bound for one install run before it is killed (npm on a slow link).</summary>
        public const int TimeoutMillis = 600000;

        public AgentBackend Backend;
        /// <summary>Process to start (a shell, so PATH lookups happen the way a terminal does).</summary>
        public string FileName;
        /// <summary>Argument string for <see cref="FileName"/>.</summary>
        public string Arguments;
        /// <summary>The one-line command as a user would type it (shown for review and in the manual foldout).</summary>
        public string DisplayCommand;
        /// <summary>True when the command depends on npm being installed.</summary>
        public bool RequiresNpm;

        /// <summary>The npm package(s) for an ACP backend; empty for Claude Code and custom agents.</summary>
        public static string NpmPackages(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return "@google/gemini-cli";
                case AgentBackend.CodexAcp:
                    // The adapter needs the Codex CLI it wraps; both are
                    // published on npm (verified 2026-09-10: @openai/codex
                    // 0.154.0, @agentclientprotocol/codex-acp 1.11.0).
                    return "@openai/codex @agentclientprotocol/codex-acp";
                default:
                    return string.Empty;
            }
        }

        /// <summary>True for backends installed by a vendor script rather than npm.</summary>
        public static bool UsesNativeInstaller(AgentBackend backend)
        {
            return backend == AgentBackend.ClaudeCode || backend == AgentBackend.GrokBuild;
        }

        /// <summary>The one-line command for the backend on the platform, or empty when nothing can be installed (custom agent).</summary>
        public static string DisplayCommandFor(AgentBackend backend, bool isWindows)
        {
            if (UsesNativeInstaller(backend))
            {
                string sh = backend == AgentBackend.GrokBuild ? GrokInstallShUrl : ClaudeInstallShUrl;
                string ps1 = backend == AgentBackend.GrokBuild ? GrokInstallPs1Url : ClaudeInstallPs1Url;
                return isWindows
                    ? "irm " + ps1 + " | iex"
                    : "curl -fsSL " + sh + " | bash";
            }
            string packages = NpmPackages(backend);
            return packages.Length == 0 ? string.Empty : "npm install -g " + packages;
        }

        /// <summary>
        /// Null when the backend has no known installer (AcpCustom). The
        /// Windows plans go through cmd.exe / PowerShell and the Unix ones
        /// through a login bash so nvm-style PATH additions apply.
        /// </summary>
        public static CliInstallPlan Build(AgentBackend backend, bool isWindows)
        {
            string display = DisplayCommandFor(backend, isWindows);
            if (display.Length == 0)
            {
                return null;
            }
            var plan = new CliInstallPlan
            {
                Backend = backend,
                DisplayCommand = display,
                RequiresNpm = !UsesNativeInstaller(backend)
            };
            if (UsesNativeInstaller(backend))
            {
                if (isWindows)
                {
                    plan.FileName = "powershell.exe";
                    plan.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + display + "\"";
                }
                else
                {
                    plan.FileName = "/bin/bash";
                    plan.Arguments = "-lc \"" + display + "\"";
                }
                return plan;
            }
            if (isWindows)
            {
                plan.FileName = "cmd.exe";
                plan.Arguments = "/d /s /c \"" + display + "\"";
            }
            else
            {
                plan.FileName = "/bin/bash";
                plan.Arguments = "-lc \"" + display + "\"";
            }
            return plan;
        }

        /// <summary>
        /// `&lt;cliPath&gt; update`: Claude Code's own updater, which knows
        /// how the CLI was installed (native or npm) and replaces it in place
        /// (design note docs/design-notes/2026-09-23-cli-update-and-pro-version.md).
        /// Run through the resolved binary directly, not a shell, so it
        /// updates exactly the CLI the panel launches. Null for an empty path.
        /// </summary>
        public static CliInstallPlan BuildClaudeUpdate(string cliPath)
        {
            if (string.IsNullOrEmpty(cliPath))
            {
                return null;
            }
            return new CliInstallPlan
            {
                Backend = AgentBackend.ClaudeCode,
                FileName = cliPath,
                Arguments = "update",
                DisplayCommand = "claude update",
                RequiresNpm = false
            };
        }

        /// <summary>
        /// Classifies a finished run. <paramref name="output"/> is the
        /// combined stdout+stderr; null means the process could not be
        /// started or timed out (<paramref name="timedOut"/> says which).
        /// </summary>
        public static CliInstallFailureKind Classify(bool requiresNpm, bool started, bool timedOut,
            int exitCode, string output)
        {
            if (!started)
            {
                return CliInstallFailureKind.ShellMissing;
            }
            if (timedOut)
            {
                return CliInstallFailureKind.TimedOut;
            }
            if (exitCode == 0)
            {
                return CliInstallFailureKind.None;
            }
            string text = output ?? string.Empty;
            if (requiresNpm && LooksLikeMissingCommand(text, "npm", exitCode))
            {
                return CliInstallFailureKind.NodeMissing;
            }
            if (!requiresNpm && (LooksLikeMissingCommand(text, "curl", exitCode)
                || LooksLikeMissingCommand(text, "irm", exitCode)))
            {
                return CliInstallFailureKind.ShellMissing;
            }
            return CliInstallFailureKind.CommandFailed;
        }

        /// <summary>
        /// "command not found" in the three shells' own words: bash exits
        /// 127 and prints "npm: command not found"; cmd.exe exits 9009 and
        /// prints "'npm' is not recognized"; PowerShell prints "is not
        /// recognized as the name of a cmdlet". Pure.
        /// </summary>
        internal static bool LooksLikeMissingCommand(string output, string command, int exitCode)
        {
            string text = output ?? string.Empty;
            if (text.IndexOf(command + ": command not found", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(command + ": not found", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("'" + command + "' is not recognized", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("'" + command + "' " , StringComparison.OrdinalIgnoreCase) >= 0
                    && text.IndexOf("not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return (exitCode == 127 || exitCode == 9009) && text.IndexOf(command, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
