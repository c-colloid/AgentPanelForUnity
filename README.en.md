![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-333) ![Editor only](https://img.shields.io/badge/Editor--only-yes-333) ![License: MIT](https://img.shields.io/badge/License-MIT-333)

# Agent Panel for Unity

*日本語: [README.md](README.md)*

Agent Panel for Unity embeds the Claude Code CLI in the Unity Editor as a dockable chat panel. The CLI runs as a resident process speaking the bidirectional `stream-json` protocol, so the agent can write code, edit files and run shell commands while you stay inside the Editor. Every tool call goes through an inline permission card, and the session survives script recompilation (domain reload) and Editor restarts.

Inspired by [ComfyUI Agent Panel](https://github.com/artokun/comfyui-mcp-panel).

## Screenshots

| Chat | Permission card | Subagent card |
|---|---|---|
| ![Chat](docs/images/chat.png) | ![Permission card](docs/images/permission-card.png) | ![Subagent progress card](docs/images/subagent-card.png) |

| History browser | Settings | Markdown tables |
|---|---|---|
| ![Session history browser](docs/images/history.png) | ![Settings](docs/images/settings.png) | ![Markdown table rendering](docs/images/table.png) |

## Features

### Chat

- **Resident process + streaming** -- one CLI process per chat, responses rendered as they stream in.
- **Subscription agents other than Claude (ACP backends)** -- Settings > CLI > Agent switches the panel to Gemini CLI (`gemini --experimental-acp`), Codex (`codex-acp`), Grok Build (`grok agent stdio`) or any other agent that implements the Agent Client Protocol (Qwen Code, Kimi CLI, ...). Each CLI's own credentials are used as is -- Codex: your ChatGPT account (`codex login`) or `CODEX_API_KEY` / `OPENAI_API_KEY`; Grok Build: SuperGrok / X Premium+ (`grok login`) or `XAI_API_KEY`; Gemini CLI: a Gemini API key (`GEMINI_API_KEY`, or `~/.gemini/.env`) or the Google sign-in of Gemini Code Assist Standard / Enterprise, since Google ended "Login with Google" for individuals (Gemini Code Assist for individuals, Google AI Pro/Ultra) on 2026-06-18. The panel never stores API keys: they live in the CLI's own environment variables or config file. Permission cards, streaming and Unity ops work the same (the history browser, in-panel login, subagent model settings and the script-gate hook stay Claude Code only).
- **Slash commands** -- type `/` to get the CLI's command list (`/compact` etc.) with arrow-key and Tab/Enter completion.
- **Context compaction, made visible** -- a note marks where `/compact` or the CLI's automatic compaction happened; the context meter shows "compacted" until the next turn.
- **Thinking summaries** -- Claude's reasoning summaries in a collapsible block (toggle in Settings).
- **Subagent (Task tool) cards** -- nested cards with running / done / failed state; inner tool calls never leak into the top-level transcript.
- **AskUserQuestion cards** -- the agent's multiple-choice questions become clickable cards, with a free-text "Other" option.
- **Session history browser** -- lists and searches the transcripts the CLI already saves, and restores any of them with `--resume`.

### Permissions and safety

- **Inline permission cards** -- allow once / always allow (with scope) / deny with a reason, before any file edit or shell command. Narrow panels switch to a floating window automatically.
- **Auto-approve levels** -- from "read-only tools" up to "all tools" (never the default).
- **Script validation gate** -- C# the agent writes is staged and compiled first; only code that compiles reaches `Assets/`. Compile errors go straight back to the agent for self-correction. (The hook layer is Windows-only; other platforms keep the permission-card pre-filter.)
- **Destructive-operation gate** -- irreversible tools such as `uap_asset_delete` and `uap_prefab_apply_overrides` are refused without `confirm:true` and only report their blast radius (`dry_run:true` previews).

### Unity integration

- **Automatic Unity context** -- the selected Hierarchy/Project objects and Console errors appear as context chips you can attach with one click.
- **Drag & drop / image attachments** -- drop assets or scene objects onto the panel; attach images from files, drops, the clipboard or a screen capture.
- **Scene-view 3D markers** -- the agent can place spheres, arrows, wire boxes and labels in the Scene view; you can drop pins to point the agent at a location.
- **Domain-reload resilience** -- sessions reconnect automatically across recompiles and Editor restarts.
- **UITK Font Fix bridge** -- uses [UITK Font Fix](https://github.com/c-colloid/UITKFontFix)'s CJK font resolution when the package is present (no dependency; detected at startup).

### Unity operation tools (UapOps)

A built-in MCP server (loopback only, token-authenticated) lets the agent drive the Editor through typed tools instead of writing C#. Everything one turn does is undone with a single Undo.

- **Scene / asset ops** -- create objects, add components, set properties and transforms, manipulate assets, execute menu items, take Editor screenshots.
- **Prefab overrides** -- list, apply, revert (all or per property).
- **Animation / materials** -- create AnimationClips, edit AnimatorControllers, set material properties with shader-property discovery, change importer settings (off by default).
- **Lightmap baking** -- both the built-in lightmapper (`uap_lightmap_bake`: memory preflight, automatic optimisation, an optimisation playbook) and [Bakery GPU Lightmapper](https://assetstore.unity.com/packages/tools/level-design/bakery-gpu-lightmapper-122218) (`uap_bakery_bake`: read/write settings, presets, scopes), without blocking the Editor.
- **Jobs** -- calls that outlive the main-thread wait are kept as jobs; `uap_job_status` fetches the result later, even while the Editor is blocked.
- **Extension profiles** -- VRChat SDK3, UniVRM, MagicaCloth2, Final IK and Bakery are auto-detected and their essentials are added to Claude's instructions (bundled profiles only; project-specific profiles require review and approval).

See the notes under [docs/design-notes/](docs/design-notes/) for details on each tool.

### Settings and more

- **Model settings** -- separate default model (new sessions) and current-session model (header picker); a cost policy steers subagents (e.g. Haiku for simple tasks).
- **In-panel login / logout** -- the account card in Settings shows login state and runs the CLI's OAuth login without a terminal.
- **Custom instructions / quick actions** -- standing instructions appended to the system prompt; one-click insertion of frequently used prompts.
- **Appearance** -- font size and a CJK UI font, configurable in Settings.
- **Localisation (Japanese / English)** -- every UI string is localised; follows the OS language by default and switches instantly.

## Core and Pro

This repository holds two packages.

- **`jp.colloid.unity-agent-panel` (Core, MIT)** — the package this README describes: chat, permissions, the script validation gate, history, ACP backends, and the basic UapOps tools (scene/component/property/asset operations, search, screenshots, Editor menu execution, and more).
- **`jp.colloid.agent-panel-pro` (Pro, proprietary, sold separately)** — adds the following advanced UapOps tools plus bundled Extension Profiles for popular extension assets. It only works alongside Core (Core works fine on its own, just without these).
  - **prefab** — prefab creation and getting/applying/reverting overrides
  - **editor (lightmap/Bakery)** — async lightmap baking, preflight diagnostics, Bakery GPU Lightmapper integration
  - **anim** — animation clip/AnimatorController editing, material settings, asset property settings, avatar importer settings
  - **ui** — UI Toolkit window automation (list/dump/click/set value)
  - Extension Profiles for Bakery / Final IK / Magica Cloth 2 / UniVRM / VRChat SDK3

Core works fully without Pro installed; the Settings toggle for an affected module shows "Provided by Agent Panel Pro (not installed)" instead. Pro is distributed as a zip on BOOTH/Gumroad, extracted directly under your project's `Packages/` folder (a distribution link will be added later).

## Requirements

| Requirement | Details |
|---|---|
| Unity | 2022.3 LTS or later (verified on 2022.3.22f1) |
| OS | Developed and verified on Windows. CLI discovery and process-kill paths exist for macOS / Linux but are not exercised regularly |
| Claude Code CLI | v2.1.218 or later; the native installer is recommended |
| Auth | A Claude subscription (Pro/Max). Log in from the account card in the panel, or run `claude` and `/login` in a terminal |
| (Optional) other ACP agents | Codex (ChatGPT account, or `CODEX_API_KEY` / `OPENAI_API_KEY`, plus the `codex-acp` adapter), Grok Build (SuperGrok / X Premium+ sign-in, or `XAI_API_KEY`), Gemini CLI (a Gemini API key in `GEMINI_API_KEY` or `~/.gemini/.env`, or the Google sign-in of Gemini Code Assist Standard / Enterprise) or any ACP-capable CLI. Pick it under Settings > CLI > Agent; installing (npm-based ones need Node.js) and signing in (the agent opens your browser) both happen inside the panel. The panel stores no API keys -- set them in the CLI's own environment variable or config file |

> [!NOTE]
> A subscription (Pro/Max) login is the recommended path. An `ANTHROPIC_API_KEY` in the editor's environment is respected by the CLI's own auth selection too, and switches billing to pay-as-you-go API usage -- Settings > Account shows which one is actually active. To always use the subscription login regardless, set "API key authentication" there to Subscription only.

## Installation

The package lives in `jp.colloid.unity-agent-panel/` and has no package dependencies.

### Option 1: Git URL in the Package Manager (recommended)

`Window > Package Manager > + > Add package from git URL...`:

[https://github.com/c-colloid/UnityAgentPanel.git?path=jp.colloid.unity-agent-panel](https://github.com/c-colloid/UnityAgentPanel.git?path=jp.colloid.unity-agent-panel#v0.42.0)

The link above points at the latest release tag (`#v0.42.0`). Append a tag to pin a version; omit it to track `main`. Tags are listed in the [CHANGELOG](jp.colloid.unity-agent-panel/CHANGELOG.md).

### Option 2: embedded package under `Packages/`

Clone the repository and copy, symlink or junction `jp.colloid.unity-agent-panel` into your project's `Packages/` folder. `manifest.json` is not touched, so VPM/VCC-managed projects are unaffected.

### Option 3: zip download

Download the repository as a zip and place `jp.colloid.unity-agent-panel` under `Packages/`.

## First run

1. Open **Window > Agent Panel**.
2. If the CLI is missing or you are not logged in, a setup card walks you through it. Login happens inside the panel (browser auth, paste the code) or via `claude` + `/login` in a terminal.

## Usage notes

- Enter sends, Shift+Enter inserts a newline. Switch to **Ctrl+Enter to send** in Settings if IME confirmation keeps sending messages by accident.
- `/clear` is handled by the panel (same as "New chat"); every other slash command is forwarded to the CLI.
- Permission cards offer a tool-wide "always allow" rule ahead of the CLI's path-scoped suggestions; the auto-approve level lives in Settings.
- If a turn is interrupted by a domain reload, a banner offers to continue it with one click. "Auto-continue after interruption" in Settings automates that (announced in the transcript, stops after three consecutive interruptions).

## Troubleshooting

| Symptom | Fix |
|---|---|
| CLI not found | Set the executable path in Settings > CLI path |
| No token count / context meter with an ACP agent | Codex (codex-acp) reports the tokens and context usage of each turn's last model call. Grok Build is read from the `_meta` values it puts on its model list and notifications. Gemini CLI sends no context size, so only the token count shows and the meter stays hidden. If nothing shows at all, update the agent's own CLI |
| Not logged in / auth error | Log in from the account card (or `claude` + `/login`); an `ANTHROPIC_API_KEY` in the editor's environment takes priority, so check Settings > Account to see which auth is actually in use. An ACP agent opens your browser when it needs a sign-in; if none opens, use the link shown in the chat, or run `gemini` / `codex login` in a terminal and press Reconnect |
| Panel stops responding / process left after the Editor exits | Leftover processes (matched by PID and start time) are reaped automatically on the next panel start |
| Japanese text renders with mixed weights or as boxes | Pick a CJK font in Settings > Appearance, or install [UITK Font Fix](https://github.com/c-colloid/UITKFontFix) |
| Too many permission cards | Use the tool-wide "always allow" rule on the card, or raise the auto-approve level in Settings |

## Documentation

- [docs/USER-GUIDE.md](docs/USER-GUIDE.md) -- user guide (Japanese): panel layout, context chips and attachments, permission cards, history, Unity operation tools, settings reference
- [docs/README.md](docs/README.md) -- index of the architecture document, design notes, research reports and verification records (mostly Japanese)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) -- architecture decision document
- [CHANGELOG](jp.colloid.unity-agent-panel/CHANGELOG.md) (English)

## Contributing

Repository layout, how to run the tests and the release process are in [CONTRIBUTING.md](CONTRIBUTING.md). Issues and pull requests are welcome.

## License

[MIT License](LICENSE)
