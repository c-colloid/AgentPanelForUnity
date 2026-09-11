# Model settings rework: default/in-use split, subagent blanket model, row layout

Date: 2026-08-01
Status: approved (user feedback drove all three items)
Supersedes parts of: `2026-08-01-model-settings.md` (section 1 "single source of
truth" is REVERSED by this note; the rest stands)
Research: `docs/research/07-model-configuration.md` §11 (new; captures in
scratchpad `capture16/`)

## 1. User feedback (2026-08-01, verbatim intent)

1. 「デフォルトに設定してあるモデルを変更するのではなくて、使用するモデルを
   変更する設計になってしまっている」 — the Settings "Default model" dropdown
   must edit the *default* (what new sessions start with), not live-switch the
   running session.
2. サブエージェントのモデル行の PopupField が縮まず、削除ボタンが画面外に
   押し出される。
3. サブエージェントのモデルをタイプ名で指定するのは分かりづらく、実際に
   使われるモデルを制御しづらい。もっと直感的な手段がほしい。

## 2. Root causes (proven)

### 2.1 Default vs in-use conflation (item 1)

v0.8.0 deliberately made the header picker and the Settings dropdown share one
entry point, `AgentHub.SwitchModel`, which BOTH persists `PanelSettings.model`
AND live-applies via `set_model` (design note 2026-08-01-model-settings.md §1).
Consequences the user correctly rejects:

- Changing "Default model" in Settings immediately switches the running
  session (a *default* should not do that).
- Conversely, a one-off model switch from the header permanently rewrites the
  persisted default.

There is no bug here — the design itself conflated two concepts.

### 2.2 Remove button pushed off-screen (item 2)

UITK/Yoga defaults `flex-shrink: 0` (unlike web CSS's 1). In
`.uap-settings-model-row` the name field is `width:160px; flex-shrink:0`, the
picker is `flex-grow:1` with the **default shrink 0**, and the remove button is
`flex-shrink:0`. When the picker's intrinsic width (long model display name)
exceeds the remaining row width, nothing can shrink → the row overflows and the
trailing remove button leaves the viewport.

### 2.3 Name-based per-type table is the wrong primary UI (item 3)

The `.claude/agents/<name>.md` mechanism (v0.8.0) requires knowing magic type
strings, only covers the names you enumerate, and only applies to sessions
created after the files were written (snapshot-at-creation, R07 §10.8). All
three properties fight the user's mental model "run subagents on a cheap
model".

## 3. New measured facts (capture16, CLI v2.1.220)

Binary grep: `CLAUDE_CODE_SUBAGENT_MODEL` exists (8 hits). Probes (all via
`capture_bidi2.js`, real logged-in sessions, verified via `result.modelUsage`
keys — the only trustworthy per-model source, R07 §5):

| # | Setup | Result |
|---|-------|--------|
| P1 | fresh, `--model sonnet`, env `CLAUDE_CODE_SUBAGENT_MODEL=haiku`, one Task | usage = `claude-haiku-4-5…; claude-sonnet-5` → **env var works on fresh sessions** |
| P2 | `--resume` P1's session, env switched to `opus`, no `--model` | usage = `claude-sonnet-5; claude-opus-5` → **resume honors the CURRENT env value** (no snapshot-at-creation, unlike `--agents`/agent files); parent stayed sonnet → resume without `--model` keeps the session model (re-confirms R07 §6) |
| P3 | fresh, `--model haiku`, `.claude/agents/general-purpose.md` `model: sonnet`, env `opus` | usage = `haiku; opus` → **env var OVERRIDES `.claude/agents` files** |

Also: `system/init` carries an `agents` string array (e.g.
`["claude","claude-code-guide","Explore","general-purpose","Plan","statusline-setup"]`)
— the CLI self-reports the available subagent types for this project/user, so
the panel never needs hard-coded or free-typed type names.

## 4. Decisions

### 4.1 Split default from in-use (reverses v0.8.0 §1)

- `PanelSettings.model` keeps its serialized name but now means **"default
  model for new sessions"**. The Settings dropdown persists it and does
  NOTHING live. Hint text says new sessions (header "+") use it and points at
  the header picker for the current session.
- Header picker becomes **session-scoped**: live `set_model` only, no longer
  writes `PanelSettings.model`. `AgentHub.SwitchModel` splits into
  `SetDefaultModel` (persist only) and `SwitchSessionModel` (live only; keeps
  the `_lastModelUsage` reset from v0.8.0).
- Spawn passes `--model` **only when `ResumeSessionId == null`** (fresh).
  Resume passes no `--model`; the CLI keeps the session's own model (measured,
  P2 + R07 §6). This also removes the last way a reconnect could clobber a
  header-picked model (explicit `--model` wins on resume — R07 §6).
- `SettingsChangeDetector`: drop `model` from `RequiresReconnect` (a resume
  can no longer apply it; it is new-sessions-only, like agent override files).
- Display fallback when no client: header/status keep showing
  `PanelSettings.model` (the default) — once connected, `system/init.model` /
  `set_model` responses correct it (existing behavior).

Rejected alternative: keep shared source of truth but suppress the live apply
only for the Settings dropdown — rejected because the header would still
rewrite the default (half of the complaint).

### 4.2 Subagent model: blanket env var as PRIMARY, per-type table demoted

- New `PanelSettings.subagentModel` (alias string, empty = inherit). When
  non-empty, spawn sets `CLAUDE_CODE_SUBAGENT_MODEL=<alias>` on the child
  process environment. When empty, the variable is **left untouched**
  (inherited from the editor's environment, if the user set it globally) —
  never force-removed, least surprise.
- Applies to ALL subagent types including ones we cannot enumerate, and — per
  P2 — takes effect on the NEXT SPAWN even for the existing session, so it
  plugs into the existing auto-apply reconnect machinery
  (`SettingsChangeDetector` gains `subagentModel` as reconnect-relevant +
  `AgentHub.RequestAutoApplyReconnect` on change). Net UX: change the
  dropdown, it applies within seconds, current session included. No 「新しい
  セッションから有効」 caveat.
- The per-type table (`agentModelOverrides` + `AgentDefinitionFileWriter`)
  survives as **advanced, collapsed-by-default Foldout**. Its free-text name
  field becomes a PopupField fed by: live/cached `init.agents` catalog ∪
  names already present in the table. New `PanelSettings.agentTypeCatalog`
  cached alongside `modelCatalog` by the same refresh path.
- Precedence warning (P3): when `subagentModel` is set AND the table has
  rows, show a warn-styled hint that per-type entries have no effect while
  the blanket model is set (env var beats agent files — measured).

Rejected alternatives:
- Emulate a blanket policy by writing `.claude/agents` files for every known
  type — leaks unknown/custom types to the parent model, still
  snapshot-at-creation, strictly worse than the env var.
- Remove the per-type table entirely — loses real power-user value
  (e.g. Explore=haiku, Plan=sonnet) now that names come from a dropdown; the
  foldout keeps it out of the primary flow.

### 4.3 Row layout fix

`.uap-settings-model-picker { flex-shrink: 1; min-width: 80px; overflow: hidden; }`
and `.uap-settings-model-name { flex-shrink: 1; min-width: 80px; }` (keeps
`width:160px` as the preferred size), remove button stays `flex-shrink: 0` so
it can never be pushed out. Guarded by a UssHygieneTests source-scan (same
ExtractRuleBlock pattern as the perm-card crush guard).

## 5. Regression guards

- `SettingsChangeDetectorTests`: `model` change alone → NOT reconnect-
  relevant; `subagentModel` change → reconnect-relevant.
- AgentClient arg/env construction tests: fresh spawn includes `--model`,
  resume spawn omits it; `SubagentModel` set → env pair present, empty → env
  untouched (pure-function seam, no process spawn).
- `AgentHubModelTests`: `SetDefaultModel` persists without calling
  `SetModel`; `SwitchSessionModel` calls live `SetModel` without persisting.
- `SettingsViewLogicTests`: type-dropdown choice building (catalog ∪ existing
  entries, dedup, stable order); blanket-vs-table warning predicate.
- `UssHygieneTests`: model-row shrink guards.
- L10n reflection parity auto-covers the new strings (EN/JA, append at END).

## 6. Release

Ships as **v0.9.0** (behavior change + feature; Phase 5a shifts to v0.10.0).
