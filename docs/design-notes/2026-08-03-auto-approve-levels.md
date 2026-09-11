# Permission fatigue: auto-approve levels (2026-08-03)

Live request: "running a large task gets interrupted by permission prompts
over and over -- can we have selectable permission modes like Claude
Desktop, especially an automatic one?"

The interesting part of this note is why the obvious implementation --
expose the CLI's own permission modes -- is wrong, and how that was
established rather than assumed.

## 1. The obvious answer, measured and rejected

`--permission-mode` accepts six values (recorded from `--help` in
`docs/research/02-claude-cli-protocol.md`), of which the panel exposed
three. There is literally a value called `auto`. Shipping it would have
taken one line.

So it was measured first: five modes, one real CLI run each, a PreToolUse
hook installed via `--settings`, one user turn asking for two file writes
(one at a path the hook denies), watching the wire.

| mode | `can_use_tool` for Write | hook fired | hook deny blocked | `system/init` echo | files written |
|---|---|---|---|---|---|
| `default` | yes | yes | yes | `default` | allowed only |
| `acceptEdits` | **no** | yes | yes | `acceptEdits` | allowed only |
| `auto` | yes | yes | yes | **`default`** | allowed only |
| `dontAsk` | **no** | yes | yes | `dontAsk` | **neither** |
| `manual` | yes | yes | yes | **`default`** | allowed only |

Three findings, each of which independently kills the one-line version:

1. **`auto` and `manual` resolve to `default`.** Two independent signals
   agree: the `system/init` echo, and the `permission_mode` the CLI passes
   into the hook payload. The flag is accepted and changes nothing.
   Exposing "auto" would have promised a behaviour that does not exist.
2. **`dontAsk` auto-DENIES.** The hook log shows the model attempted both
   writes; neither file appeared, including the one nothing was supposed to
   block. It stops the asking by refusing the work.
3. **`acceptEdits` genuinely skips the round trip -- but only for
   Write/Edit-family tools.** MCP tools still fire it (design note
   2026-08-01 section 8.7). Every Unity action this panel offers is an MCP
   (UapOps) tool, so `acceptEdits` barely touches the fatigue the user
   actually reported.

**The good news from the same run:** the PreToolUse hook fired AND its deny
still blocked in all five modes. The script-validation gate's hook layer is
mode-independent. That was previously unverified, and it is what makes any
relaxation here defensible at all.

## 2. What was built instead

A panel-side auto-approve level, evaluated per permission request. Four
levels, each including the one above it:

| level | auto-approves | of the real registry (29 tools) |
|---|---|---|
| Ask | nothing | 0/29 |
| Read-only Unity ops | UapOps tools with `ReadOnly` | 8/29 |
| Undoable Unity ops | + UapOps tools with `Undoable` | 18/29 |
| All Unity ops | + UapOps tools Undo cannot reverse | 29/29 |

`ReadOnly` is exactly what the v0.14.0 `autoApproveReadOnlyOps` toggle did,
so every existing user migrates onto it and nobody's behaviour changes on
upgrade (generation-gated, same shape as `EnsureUapOpsModuleDefaults`).

### 2.1 What no level ever covers

A non-UapOps tool is never auto-approved, at any level, for any
combination of flags. This is the load-bearing rule and it has its own
exhaustive test (4 levels x readOnly x undoable).

Two concrete reasons, not caution for its own sake:

- **The script gate depends on it.** Its `can_use_tool` layer only exists
  for tools that reach `can_use_tool`; Bash/Write/Edit must keep going
  through the card.
- **The general-purpose escape hatch was already ruled out.** The v0.14.0
  note explicitly declined to auto-allow uloop / dynamic code execution:
  *"The escape hatch stays -- it just stops being the default reach."*
  Nothing here reopens that.

Also unchanged: the script gate runs FIRST, before any auto-approve check,
so a gate denial still wins at `All Unity ops`; extension-profile hash
trust is a separate axis; and the end-of-turn "this turn ran operations
Undo cannot take back" warning still fires. At the top level that warning
becomes a notification after the fact rather than a chance to intervene --
which is the honest description of what the user chose.

### 2.2 It has to work on the request already on screen

The moment someone wants this is the moment a card is blocking them, so
"applies from the next request" would miss the point. Raising the level
re-evaluates the pending request through the same path a fresh one takes
and answers it if the new level covers it.

Deliberately NOT via reconnect: `permissionMode` is the only permission
setting the CLI accepts live, and this is not it -- but this policy needs
no CLI involvement at all, and a reconnect would kill the very turn the
user is trying to unblock.

### 2.3 Where the control lives

In the header, permanently visible, not only in Settings -- for the same
reason: making someone open Settings to escape an interruption is the
interruption. It also means "how much is this agent allowed to do
unattended" is never hidden state. The chip is tinted warning at
`Undoable` and error at `All Unity ops`.

The chip shows a SHORT label. Measured live: with the full names it was
squeezed to its 46 px min-width and rendered about three characters, and a
truncated level name reads as a different level. Full names stay in the
menu, the Settings dropdown and the tooltip.

## 3. Found while verifying

Counting the real registry to fill in the table in section 2 turned up
`uap_ping` classified as neither read-only nor undoable -- i.e. sorted into
the tier reserved for work Undo cannot take back. It echoes a string and
touches nothing. The `ReadOnly` flag was added in v0.14.0, after that tool
shipped in v0.12.0, and nobody revisited it; harmless while the flag only
suppressed a card, wrong now that it decides a tier. Reclassified, and the
pinned-set test moved with it.

That is the argument for verifying against the real registry rather than a
fixture: the numbers are what exposed it.

## 4. Verification

Sandbox: compile clean, **1632 tests / 0 failed** (1594 before).

Live, against the real 29-tool registry:

```
migration: legacy toggle True -> level ReadOnly, generation stamped 1
per level: Ask 0/29   ReadOnly 8/29   Undoable 18/29   AllUnityOps 29/29
non-UapOps tool at every level, both flag combinations: False
header chip: '読取', flex-shrink 1, max-width 132
```

Not covered here: a live end-to-end where the agent actually triggers a
card and the level change clears it. That path is unit-tested
(`RaisingLevel_ResolvesAnAlreadyPendingMutatingRequest` and its negative
cases) but has not been driven through a real turn -- worth doing the first
time it comes up naturally rather than manufacturing one.
