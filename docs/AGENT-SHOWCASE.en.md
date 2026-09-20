# Agent usage examples

*日本語: [AGENT-SHOWCASE.md](AGENT-SHOWCASE.md)*

[Shared workflow](#shared-workflow-edit-and-review-a-scene) · [Image generation](#image-generation-create-and-apply-materials) · [X search](#x-search-bring-research-into-your-scene) · [Switching agents](#switch-agents-in-settings)

## Shared workflow: edit and review a scene

Use chat to arrange or resize objects, apply materials and adjust lights.

| | Claude Code | Codex | Grok Build |
|---|---|---|---|
| Example | Add a light and resize an object | Arrange and color objects | Adjust light color and intensity |
| Screenshot (click to enlarge) | [![Claude Code request and result: a warm PointLight added under Player and Crate scaled to 1.5 times its size](images/agents/claude-common-task-with-request.png)](images/agents/claude-common-task-with-request.png) | [![Codex request and result: three crates arranged in a circle and colored red, green and blue](images/agents/codex-common-task-with-request.png)](images/agents/codex-common-task-with-request.png) | [![Grok Build result: adjusted color and intensity of the Directional Light and Player's PointLight](images/agents/grok-x-search.png)](images/agents/grok-x-search.png) |

## Image-generation and search examples

### Image generation: create and apply materials

Specify the material to generate and the objects to apply it to in chat.

#### Codex

[![Codex generating a wooden-crate texture and applying it to three crates in Unity](images/agents/codex-imagegen-texture-with-request.png)](images/agents/codex-imagegen-texture-with-request.png)

#### Grok Build

[![Grok Build generating a cobblestone texture and applying it to Ground](images/agents/grok-imagegen-texture-with-request.png)](images/agents/grok-imagegen-texture-with-request.png)

### X search: bring research into your scene

Tell Grok Build what to research on X and which parts of the scene to edit. It can use the search results to make those changes.

[![Grok Build researching lighting on X and applying adjustments to the Unity scene, with a summary and scene markers](images/agents/grok-x-search-with-request.png)](images/agents/grok-x-search-with-request.png)

## Switch agents in Settings

1. Open **Settings > Agent** and select the agent you want to use.
2. If its CLI is missing, use **Install** in the same card.
3. **Sign in** when required, confirm the connection and send your request.

**Choose the agent in Settings; choose its model in the header.**

![Header model picker showing models offered by the connected agent; captured UI is in Japanese](images/guide/10-model.png)

[Setup and sign-in guide (Japanese)](USER-GUIDE.md#11-claude-以外のエージェントを使う) · [Model selection (Japanese)](USER-GUIDE.md#10-モデルの選択) · [Installation](../README.en.md)

## Other supported agents

Gemini CLI and other ACP-capable CLIs, such as Qwen Code and Kimi CLI, can also connect. See the [user guide (Japanese)](USER-GUIDE.md#11-claude-以外のエージェントを使う) for Gemini CLI access requirements and manual configuration for other CLIs.
