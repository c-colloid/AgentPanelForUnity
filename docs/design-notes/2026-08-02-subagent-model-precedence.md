# Subagent model precedence rework + default-model display honesty

Date: 2026-08-02
Status: approved (user feedback on v0.9.0)
Research: docs/research/07-model-configuration.md §12-13 (capture17)
Ships as: v0.11.0 (implementation starts after the v0.10.0 auth workflow lands
-- both touch SettingsView/L10n/AgentHub, so strictly sequential)

## 1. User feedback (2026-08-02)

1. 「選択モデル一覧の Default (Recommended) に設定されるモデルを変更するのは
   不可能なのか。デフォルトモデルを変更しても対象のモデルの表記は変更されない」
2. 「基本は Sonnet で、単純作業だけ Haiku にしたい。現状の環境変数が全部の設定を
   上書きしてしまう設定作用が逆だ」

## 2. Measured ground truth (capture17; full detail in R07 §12-13)

- The Task/Agent tool accepts a per-call `model` parameter and the MAIN AGENT
  already uses it when instructed (P4).
- Subagent execution model precedence:
  `CLAUDE_CODE_SUBAGENT_MODEL` env var  >  per-call `model` param  >
  `.claude/agents/<type>.md`  >  parent-session inherit (P5, P6, P3).
- The catalog's "Default (recommended)" entry is a server/plan-side
  recommendation (`resolvedModel`/`description` are provided, e.g.
  "Opus 5 with 1M context" on Max). Nothing -- not even
  `.claude/settings.json` `"model"` -- relabels or retargets it (P7).

## 3. Decisions

### 3.1 Q1: display honesty (cannot change the target, CAN show the truth)

- Answer to the user: NO, the "Default (recommended)" target is plan-driven
  and server-defined; no client-side mechanism changes it.
- ModelCatalogEntry gains additive `resolvedModel` + `description` fields
  (cache refresh already rewrites entries; old caches simply lack them).
- Settings "Default model" dropdown: the `default` catalog entry renders as
  "Default (recommended: <short resolved name>)" via the existing formatter
  (short name derived from description's leading token or resolvedModel);
  a hint line under the field shows the selected entry's `description`.
- Header model menu: the row matching the persisted panel default gets a
  "(default)" suffix (localized) in addition to the existing current-session
  checkmark, so "which is my default" vs "which is running now" are both
  visible in one place.

### 3.2 Q2: subagent controls reordered around the measured precedence

The blanket env var crushes the agent's own per-task judgment (P5), which is
exactly backwards for 「基本 Sonnet、単純作業だけ Haiku」. The mechanisms for
that goal already exist natively: inherit (= parent Sonnet) + per-call
`model:"haiku"` chosen by the agent. The panel's job is to steer and to stay
out of the way:

New Settings > Model layout:

1. Default model (unchanged, plus 3.1 display fixes).
2. NEW "Subagent cost policy" dropdown (primary control):
   - 「おまかせ(推奨)」/ "Agent decides (recommended)" -- injects nothing.
   - 「コスト重視: 単純作業は Haiku」/ "Cost-saving: Haiku for simple tasks"
     -- injects ONE instruction line into the spawn's append-system-prompt
     (joined after custom instructions): the agent is told to pass
     model:"haiku" on Agent/Task calls for simple mechanical subtasks and to
     omit the param for complex work. Applies via the existing
     next-spawn+auto-apply machinery (same as custom instructions).
     Soft by design: the agent can always up-model a task it judges complex
     (per-call beats nothing here -- nothing hard is set).
3. Advanced foldout (existing):
   - Per-type table: REFRAMED from "override" to "per-type default" -- hint
     now states the agent's explicit per-call choice wins over these entries
     (P6), which is the desired direction; "new sessions only" hint stays.
   - Blanket dropdown: renamed to 「全サブエージェントを強制固定」/
     "Force model for ALL subagents", MOVED into the foldout, with a warning
     hint that it overrides BOTH the per-type defaults and the agent's
     per-call choices (P5) and is meant as a hard cost clamp, not a default.
     Mechanism unchanged (env var; immediate via auto-apply).
   - The v0.9.0 precedence warning (blanket set + table rows) stays.

Rejected alternatives:
- Reimplementing the blanket as `.claude/agents` files for every cataloged
  type (to sit BELOW per-call choices): loses unknown-type coverage, returns
  to new-sessions-only semantics, and duplicates the per-type table's
  mechanism -- the instruction-injection policy achieves the user's stated
  goal with none of that.
- Dropping the env var entirely: it remains the only guaranteed cost clamp
  (e.g. a runaway loop spawning expensive subagents), so it stays, clearly
  labeled as force.

## 4. Regression guards

- Instruction-snippet composition test (policy line joined with/without
  custom instructions; absent when policy = agent-decides).
- SettingsChangeDetector: policy field participates exactly like custom
  instructions (reconnect-relevant, auto-apply).
- Formatter tests: "default" entry rendering with/without resolvedModel /
  description (old caches without the new fields must not crash or mislabel).
- Header menu: default-suffix row selection logic as pure seam.
- L10n parity (EN/JA) via existing reflection tests.
