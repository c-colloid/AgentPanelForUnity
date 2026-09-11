# 2026-08-22 -- HUB-1: AbortOpenTurn, the one non-clean turn-exit cleanup

The last High finding of the Phase 1 review: turn-end cleanup was scattered
across four terminal paths, and the newest path (the silence backstop) had
forgotten most of it.

## The defect

A turn holds open: the streaming message + running tool/subagent records, a
possible pending permission card, the UapTurnScope (one Undo group + the
AssetDatabase auto-refresh suppression window), the per-turn non-undoable
warning set, the scripts-commit attribution flag, and (for continuation
turns) `SessionStateBridge.AutoContinueTurnIsContinuation`.

Only OnTurnCompleted -- a clean ResultMessage -- released all of it. The
other exits each hand-picked a subset:

| path | had | missing |
|---|---|---|
| OnTurnStalled | Finalize | scope, permission, warning set, attribution, continuation flag |
| OnStateChanged(Errored) | scope, continuation flag | Finalize, permission, warning set, attribution |
| OnProcessDied | Finalize, permission, continuation flag | scope*, warning set, attribution |
| TearDownClient | everything except | continuation flag (correctly -- see below) |

(*covered indirectly by the Errored transition when it fires.)

The worst case was the stall: `TurnTracker.EndTurn` runs BEFORE
`TurnStalled` is raised, so the stalled turn can never reach
OnTurnCompleted -- yet the handler only finalized the streaming message.
The UapTurnScope leak left **AssetDatabase auto-refresh disallowed (and the
Undo group open) for the rest of the editor session** (the finding's
headline symptom), the stalled turn's non-undoable warning attached itself
to the next unrelated turn, a pending permission card stayed up
unanswerable, and a stalled continuation turn stranded the continuation
flag (defect 5's mechanics through a path defect 5 never covered). The
crash-loop-suspended branch had the same "no teardown ever follows"
exposure for the warning set and attribution flag.

The project's own defect history (4, 5, 6) is a list of "a new terminal
path forgot one clear" -- the roadmap ordered this consolidation before any
further terminal-path work for exactly that reason.

## The fix

`AgentHub.AbortOpenTurn(bool clearContinuationFlag)` performs all of the
above, idempotently (several terminal paths legitimately fire for one exit:
ProcessDied AND the Errored transition AND an eventual teardown). All four
paths now call it and nothing else hand-picks clears.

`clearContinuationFlag` semantics (the review's flagged foot-gun):
- **true** -- stall/errored/died: the turn will never complete, a stranded
  flag makes the next human-prompted turn refuse to arm auto-continue.
- **false** -- teardown: TearDownClient underlies ShutdownForReload, which
  runs on the very domain reload the flag exists to survive.

Kept intentionally: the duplicate cleanup in BOTH the Errored transition
and OnProcessDied (each covers exits the other misses); TearDownClient's
early `UapTurnScope.EndIfActive` before the client dispose (the release
must not depend on dispose succeeding); `_taskIdToToolUseId`'s full clear
stays teardown-only (death/stall demotion prunes open entries already).

Behavioral upgrades riding along:
- Teardown now FINALIZES the streaming message (blocks marked
  not-streaming) instead of dropping the reference with the session's
  blocks left spinning.
- The errored path drops a pending permission card (previously only the
  death path did; an Errored-without-death exit left it up).

## Tests

`AgentHubAbortOpenTurnTests`: direct contract (scope release, flag
semantics both ways, idempotence) plus wired stall replays through a real
AgentClient over FakeCliProcess (SilenceTimeoutSeconds shrunk to force the
backstop): the stall releases the scope, clears a continuation flag, and
the stalled turn's non-undoable tools do NOT warn on the next completed
turn.
