# Getting started (opening the panel and first-time setup)

[User guide](../USER-GUIDE.en.md) > Getting started

For package installation and requirements, see the [README](../../README.en.md#installation). This page covers what happens after installation, up to your first message.

## 1. Open the panel

Choose **Window > Agent Panel** from the menu. The panel can be docked like any other editor window.

![Agent Panel in the Window menu](../images/install/upm-window-menu.png)

## 2. Choose an agent

The default is **Claude Code**. To use Codex, Grok Build or another agent, pick it under Settings (the ⚙ in the header) > Connect > "Agent".

| Agent | Sign in with | Steps |
|---|---|---|
| **Claude Code** (default) | A Claude subscription (Pro / Max, etc.) or an API key | [Signing in to Claude Code](agents/claude-code.en.md) |
| **Codex** | A ChatGPT account or an API key | [Signing in to Codex](agents/codex.en.md) |
| **Grok Build** | A SuperGrok / X Premium+ account or an API key | [Signing in to Grok Build](agents/grok-build.en.md) |
| **Gemini CLI** | A Gemini API key (personal Google sign-in is no longer offered) | [Setting up Gemini CLI](agents/gemini-cli.en.md) |
| **Other ACP agents** | Depends on the CLI | [Other ACP agents](agents/custom-acp.en.md) |

Differences between agents (history, subagent handling and so on) are summarized in [Agents: installing and signing in](agents/README.en.md).

## 3. Install and sign in to the CLI

On first run the panel shows a setup card that matches your state. Use its buttons to install the CLI and sign in. Screenshot-based steps for each agent are behind the links in the table above.

- **CLI not found**: use "Install" on the card to install the CLI (the command that will run is shown before you press it), or install it yourself and press "Re-detect".
- **Not signed in**: pressing "Log in" opens the Agent card in Settings and starts the browser authentication flow.

![Setup card shown when the CLI is not found](../images/guide/01-cli-missing.png)

## 4. Ready

When the status bar shows "Idle", you are ready. Type your request in the input box and press **Enter** to send it.

## Read next

- [Layout and chat](chat.en.md) — names of each part of the panel, and sending, stopping and slash commands
- [Permission cards and auto-approve](permissions.en.md) — the confirmation before the agent uses a tool
- [Passing Unity context](context.en.md) — pass the selected object or an error

---

[User guide contents](../USER-GUIDE.en.md) · [Agents: installing and signing in →](agents/README.en.md)
