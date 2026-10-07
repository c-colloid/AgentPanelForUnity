# Sessions, history and models

[User guide](../USER-GUIDE.en.md) > Sessions, history and models

On this page: [Sessions and history](#sessions-and-history) / [Choosing a model](#choosing-a-model)

## Sessions and history

- **New session**: the **+** in the header. The current session stays in the history. Sending `/clear` from the input box does the same.
![History browser. A list grouped by date](../images/guide/09-history.png)

- **Open history**: "History" in the header. It lists the sessions of this project (sessions saved by Claude Code and ACP agent sessions saved by the panel; the latter have the agent name at the end of the row). If you open a session of another agent, the current agent carries the conversation log over into a new session without switching to that agent.
  - The search box at the top searches titles and contents.
  - "Group by" switches the ordering between Date / Scene / Project / Custom group.
  - The menu at the right end of a row offers "Open", "Pin", "Rename...", "Move to group" (including "New group..."), "Archive" and "Delete...". "Show archived" also lists archived sessions.
  - Select a row and press "Switch" to restore that session with `--resume` and redisplay its conversation log. Usage (tokens) and compaction notes are restored too.
- By default the session name is generated automatically from the start of the conversation. "Rename..." sets any title you like, and "Reset" returns to the automatic name.

## Choosing a model

- **Model for the current session**: the model picker in the header. You choose from the model list the CLI returns after connecting. Switching is immediate, and the result appears as a system note in the conversation log ("Model switched to ..." / "Model switch to ... failed").
  - If you choose one right after "+" or a reconnect, before the connection has finished, it is applied to this session as soon as the connection completes (the log says "Model switch to ... is waiting for the connection to finish").

  ![Model picker in the header](../images/guide/10-model.png)
- **Default model for new sessions**: "Default model" under Settings > Agent > Model.
- **Subagents**: under Settings > Agent > Model, choose "Subagent cost policy": "Agent decides (recommended)" or "Cost-saving: Haiku for simple tasks". You can also override per agent name with "Force subagent model" and "Per-type overrides (advanced)". These take effect from new sessions.

---

[← Permission cards, auto-approve and question cards](permissions.en.md) · [User guide contents](../USER-GUIDE.en.md) · [Unity tools (UapOps) →](unity-ops.en.md)
