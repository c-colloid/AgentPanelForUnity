namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// Which agent CLI the panel drives (design note
    /// docs/design-notes/2026-09-10-acp-backends.md section 2). The value
    /// is persisted in PanelSettings.agentBackend, so the integer values
    /// are stable by contract: never renumber, only append.
    /// </summary>
    public enum AgentBackend
    {
        /// <summary>Claude Code CLI over its native bidirectional stream-json protocol (the original, default).</summary>
        ClaudeCode = 0,

        /// <summary>
        /// Google Gemini CLI in ACP mode (`gemini --experimental-acp`). A
        /// Gemini API key (GEMINI_API_KEY, or ~/.gemini/.env), or the
        /// Google sign-in of Gemini Code Assist Standard / Enterprise.
        /// Google sign-in for individuals (Gemini Code Assist for
        /// individuals, Google AI Pro/Ultra) ended on 2026-06-18:
        /// https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals
        /// </summary>
        GeminiCli = 1,

        /// <summary>OpenAI Codex through the codex-acp adapter (`codex-acp`). ChatGPT subscription login via `codex login`, or CODEX_API_KEY / OPENAI_API_KEY.</summary>
        CodexAcp = 2,

        /// <summary>Any other ACP (Agent Client Protocol) agent: the user supplies the command and arguments (Qwen Code, Kimi CLI, OpenCode, ...).</summary>
        AcpCustom = 3,

        /// <summary>xAI Grok Build in ACP mode (`grok agent stdio`). SuperGrok / X Premium+ subscription via `grok login`, or XAI_API_KEY.</summary>
        GrokBuild = 4
    }

    /// <summary>
    /// Pure helpers over <see cref="AgentBackend"/>: which backends ride the
    /// ACP bridge, and the per-backend launch defaults the Settings UI
    /// pre-fills. No Unity dependency (unit-tested directly).
    /// </summary>
    public static class AgentBackends
    {
        /// <summary>True for every backend that is NOT Claude Code, i.e. every backend that speaks ACP through AcpBridgeTransport.</summary>
        public static bool IsAcp(AgentBackend backend)
        {
            return backend != AgentBackend.ClaudeCode;
        }

        /// <summary>
        /// The command the backend is launched with when the user left the
        /// ACP command field empty. Bare names are resolved on PATH by
        /// AcpCommandResolver. Empty for AcpCustom (the user must fill it).
        /// </summary>
        public static string DefaultCommand(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return "gemini";
                case AgentBackend.CodexAcp:
                    return "codex-acp";
                case AgentBackend.GrokBuild:
                    return "grok";
                default:
                    return string.Empty;
            }
        }

        /// <summary>The argument string appended to the default command (Gemini CLI needs its ACP flag; Grok Build its agent subcommand).</summary>
        public static string DefaultArguments(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return "--experimental-acp";
                case AgentBackend.GrokBuild:
                    return "agent stdio";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Resolves the effective launch command: the user override when
        /// non-empty, otherwise the backend default. Whitespace-only counts
        /// as empty.
        /// </summary>
        public static string EffectiveCommand(AgentBackend backend, string userCommand)
        {
            return string.IsNullOrEmpty(userCommand) || userCommand.Trim().Length == 0
                ? DefaultCommand(backend)
                : userCommand.Trim();
        }

        /// <summary>
        /// Resolves the effective argument string: the user override when
        /// the user ALSO overrode the command (a custom command needs its
        /// own arguments), otherwise the user's arguments when given, else
        /// the backend default. Rule of thumb: an empty arguments field
        /// never strips Gemini CLI's mandatory `--experimental-acp` unless
        /// the command itself was replaced.
        /// </summary>
        public static string EffectiveArguments(AgentBackend backend, string userCommand, string userArguments)
        {
            bool commandOverridden = !string.IsNullOrEmpty(userCommand) && userCommand.Trim().Length > 0;
            bool argsGiven = !string.IsNullOrEmpty(userArguments) && userArguments.Trim().Length > 0;
            if (argsGiven)
            {
                return userArguments.Trim();
            }
            return commandOverridden ? string.Empty : DefaultArguments(backend);
        }

        /// <summary>Install hint shown on the first-run card when the command cannot be found.</summary>
        public static string InstallCommand(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return "npm install -g @google/gemini-cli";
                case AgentBackend.CodexAcp:
                    return "npm install -g @openai/codex @agentclientprotocol/codex-acp";
                case AgentBackend.GrokBuild:
                    return "curl -fsSL https://x.ai/cli/install.sh | bash";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The terminal login command for the backend (the ACP bridge asks
        /// the agent to authenticate itself, but a browser-based login is
        /// often easier from a terminal). Empty when unknown.
        /// </summary>
        public static string LoginCommand(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return "gemini";
                case AgentBackend.CodexAcp:
                    return "codex login";
                case AgentBackend.GrokBuild:
                    return "grok login";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The environment variable(s) the backend's own CLI reads an API
        /// key from. The panel never stores API keys (design note
        /// docs/design-notes/2026-09-10-acp-auth-guidance-and-method-display.md
        /// section 2) -- this is only what to tell the user to set. Empty
        /// for backends with no documented key variable.
        /// </summary>
        public static string ApiKeyEnvVars(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return "GEMINI_API_KEY";
                case AgentBackend.CodexAcp:
                    return "CODEX_API_KEY / OPENAI_API_KEY";
                case AgentBackend.GrokBuild:
                    return "XAI_API_KEY";
                default:
                    return string.Empty;
            }
        }

        /// <summary>The CLI's own dotenv/config file that can hold the key instead of an OS variable. Empty when the CLI reads only the environment.</summary>
        public static string ApiKeyConfigPath(AgentBackend backend)
        {
            return backend == AgentBackend.GeminiCli ? "~/.gemini/.env" : string.Empty;
        }

        /// <summary>
        /// Where the API key goes, as one token to drop into a sentence:
        /// "GEMINI_API_KEY (~/.gemini/.env)", "XAI_API_KEY". Empty when the
        /// backend has no documented key path, which is the caller's cue to
        /// leave the API-key sentence out entirely.
        /// </summary>
        public static string ApiKeyHint(AgentBackend backend)
        {
            string variables = ApiKeyEnvVars(backend);
            if (variables.Length == 0)
            {
                return string.Empty;
            }
            string path = ApiKeyConfigPath(backend);
            return path.Length == 0 ? variables : variables + " (" + path + ")";
        }

        /// <summary>
        /// The name the UI addresses the agent by in sentences ("Ask
        /// Gemini...", "Codex wants to use Bash"): the product's short
        /// name. Empty for a custom agent, whose name is only known once
        /// it introduces itself (L10n.AgentName falls back).
        /// </summary>
        public static string ShortName(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.ClaudeCode:
                    return "Claude";
                case AgentBackend.GeminiCli:
                    return "Gemini";
                case AgentBackend.CodexAcp:
                    return "Codex";
                case AgentBackend.GrokBuild:
                    return "Grok";
                default:
                    return string.Empty;
            }
        }

        /// <summary>Human-readable product name for headers/notes (not localized: proper nouns).</summary>
        public static string DisplayName(AgentBackend backend)
        {
            switch (backend)
            {
                case AgentBackend.ClaudeCode:
                    return "Claude Code";
                case AgentBackend.GeminiCli:
                    return "Gemini CLI";
                case AgentBackend.CodexAcp:
                    return "Codex (codex-acp)";
                case AgentBackend.AcpCustom:
                    return "ACP agent";
                case AgentBackend.GrokBuild:
                    return "Grok Build";
                default:
                    return backend.ToString();
            }
        }
    }
}
