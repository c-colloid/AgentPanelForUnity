# Agents: installing and signing in

[User guide](../../USER-GUIDE.en.md) > Agents: installing and signing in

Besides the default **Claude Code**, the panel can switch to any CLI that supports ACP (Agent Client Protocol). Switching agents, installing the CLI and signing in are all done in Settings (⚙ in the header) > **Connection** > **Agent** card.

## Sign-in by agent

| Agent | What you sign in with | Using an API key | Steps |
|---|---|---|---|
| **Claude Code** (default) | A Claude subscription (recommended) | Environment variable `ANTHROPIC_API_KEY` | [Signing in to Claude Code](claude-code.en.md) |
| **Codex** | A ChatGPT account (recommended) | Environment variable `CODEX_API_KEY` or `OPENAI_API_KEY` | [Signing in to Codex](codex.en.md) |
| **Grok Build** | A SuperGrok / X Premium+ account (recommended) | Environment variable `XAI_API_KEY` | [Signing in to Grok Build](grok-build.en.md) |
| **Gemini CLI** | Gemini Code Assist Standard / Enterprise | `GEMINI_API_KEY` or `~/.gemini/.env` | [Setting up Gemini CLI](gemini-cli.en.md) |
| **Other ACP-compatible CLIs** (Qwen Code, Kimi CLI and so on) | Depends on the CLI | Depends on the CLI | [Other ACP agents](custom-acp.en.md) |

## Switching agents in three steps

1. Under Settings > Connection > Agent, choose the agent to use in **"Agent"** (up to v0.55.1 this was under Settings > CLI). The chosen agent is used from the next connection.
2. If the CLI is not installed, press **"Install"** on the same card (anything installed through npm needs Node.js; the command that will run is shown before you press).
3. If sign-in is needed, press **"Log in"** (Claude Code) or **"Sign in"** (others) and authenticate in the browser. When it finishes the panel reconnects, and the card shows the method actually used.

The card is in one of these 4 states. The status pill at the right end of the heading ("Signed in" and so on) shows it too.

| State | What the card shows | What you can do |
|---|---|---|
| Signed out | "Not connected." and sign-in guidance | "Sign in" starts the login command |
| Signing in | "Waiting for the ... sign-in in your browser...", the sign-in URL and the CLI output | "Open browser", "Copy", "Cancel" |
| Connecting | "Connecting to ..." | Wait |
| Connected | "Connected to ..." and the sign-in method used | "Switch account" |

## Common notes

### About API keys

- The panel does not store API keys. Put them in an OS environment variable or in each CLI's own settings file.
- OS environment variables take effect only after you restart the editor (and Unity Hub too, if you launch from Unity Hub).

### Differences between agents

Permission cards, chips and attachments, Unity tools, the script validation gate, thinking blocks, the history browser, in-panel login and the subagent model settings work the same with every agent. There are three differences.

- **History**: the panel itself saves ACP agent conversations under `UserSettings/AgentPanel/Sessions/`, and they appear in the same list as Claude Code sessions (labeled with the agent name). An agent that does not support `session/load` starts a new session on resume and receives the earlier conversation log along with your next message (a note about this appears in the conversation).
- **Subagent model settings**: "Force subagent model" and "Per-type overrides (advanced)" are sent as instructions at the start of a new session (they cannot be passed through environment variables or `.claude/agents` as with Claude Code). They are followed when the agent can choose subagent models.
- **The script validation gate hook** (the `PreToolUse` hook) is Claude Code only. With ACP, only the panel-side `uap_scripts_commit` gate applies.

Available models vary with the CLI, authentication method and account; choose one in the header's model picker ([Choosing a model](../sessions-and-models.en.md#choosing-a-model)). Real examples for each agent are in [Agent usage examples](../../AGENT-SHOWCASE.en.md).

---

[← Getting started](../getting-started.en.md) · [User guide contents](../../USER-GUIDE.en.md) · [Signing in to Claude Code →](claude-code.en.md)
