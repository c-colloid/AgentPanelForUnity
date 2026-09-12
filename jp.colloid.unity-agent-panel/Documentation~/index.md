# Agent Panel for Unity

Agent Panel for Unity embeds the Claude Code CLI as a dockable chat panel inside
the Unity Editor. Other subscription agents that implement the Agent Client
Protocol (Gemini CLI, Codex via `codex-acp`, Qwen Code, ...) can be selected
instead under Settings > CLI > Agent; they run through the ACP bridge
(`Editor/Core/Acp/`) and keep the same chat, permission cards and Unity ops.
Each of those agents keeps its own credentials: Codex takes a ChatGPT account
or `CODEX_API_KEY` / `OPENAI_API_KEY`, Grok Build a SuperGrok / X Premium+
sign-in or `XAI_API_KEY`, and Gemini CLI a Gemini API key (`GEMINI_API_KEY`, or
`~/.gemini/.env`) or the Google sign-in of Gemini Code Assist Standard /
Enterprise -- Google ended "Login with Google" for individuals on 2026-06-18.
The panel stores no API keys; Settings > Account reports which method the
connection actually used.
Full documentation (installation options, usage, troubleshooting) lives in
the repository README: https://github.com/c-colloid/UnityAgentPanel

## Setup

1. Install the Claude Code CLI (the native installer is recommended; see
   https://docs.claude.com/en/docs/claude-code). Version 2.1.218 or later.
2. Install this package: git URL with `?path=jp.colloid.unity-agent-panel`,
   or as an embedded folder under `Packages/`.
3. Open `Window > Agent Panel`. If the CLI is not found or you are not logged
   in, the setup card in the panel guides you; login can be completed inside
   the panel or with `claude` + `/login` in a terminal.
   A subscription login is the recommended path. An `ANTHROPIC_API_KEY` in
   the editor's environment is respected too and switches billing to that
   key; Settings > Account shows which one is active, and "API key
   authentication" there can be set to Subscription only to always use the
   subscription login instead.

## Architecture overview

- `Editor/Core/` -- pure C# (no UnityEngine/UnityEditor): JSON DOM and parser,
  stream-json protocol mappers, process management, client state machine,
  and the ACP bridge (`Core/Acp`: JSON-RPC <-> stream-json translation).
- `Editor/Model/` -- session and message data models, persistence.
- `Editor/UI/` -- UI Toolkit based EditorWindow, chat view, permission cards,
  settings and history views.
- `Editor/Integration/` -- domain reload lifecycle, Unity context providers,
  the `AgentHub` that owns the CLI session.
- `Editor/Ops/` -- UapOps: the built-in MCP server (loopback only, token
  authenticated) and the typed Unity operation tools it exposes. Since the
  2026-09-11 core/pro split, the prefab-overrides, lightmap/Bakery baking,
  animation/material editing and UI Toolkit automation tools -- plus the
  six bundled Extension Profiles -- live in the separate, proprietary
  `jp.colloid.agent-panel-pro` package; this package registers them through
  two seams, `IUapToolProvider` (`Editor/Ops/IUapToolProvider.cs`) and
  `IExtensionProfileProvider` (`Editor/Ops/Profiles/IExtensionProfileProvider.cs`),
  discovered via `UnityEditor.TypeCache` when Pro is installed.

The CLI runs as one resident process per chat
(`claude -p --input-format stream-json --output-format stream-json --verbose`).
Everything written to its stdin is produced by `OutboundMessages` +
`JsonWriter` (guaranteed single-line JSON); everything read from stdout is
parsed by `JsonParser` and dispatched by `StreamJsonMessage`. Unknown message
types are ignored for forward compatibility.

Design decisions and their rationale are recorded in `docs/ARCHITECTURE.md`
and `docs/design-notes/` in the repository.

## Tests

EditMode tests live under `Tests/Editor/` and validate the JSON and protocol
layers against real captured CLI output (`Tests/Editor/Fixtures/`).
Run them via `Window > General > Test Runner` (EditMode). Tests in the
`LiveCli` category spawn the real CLI and are skipped unless `UAP_LIVE_CLI=1`.
