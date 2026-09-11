# 2026-08-23 -- Phase 2 batch D: four correctness fixes (queueing, pruning, TOCTOU, abandonment)

## CORE-6: concurrent can_use_tool requests queue FIFO

HandleControlRequest overwrote the single pending pair on every
can_use_tool, so when the CLI requested permissions for parallel
tool_use blocks concurrently, the second evicted the first -- which
then could never be answered (RespondToPermission id mismatch) and the
CLI waited on it until the silence backstop. New
`Queue<ControlRequestMessage> _permissionQueue`: arrivals always
enqueue; `PromoteNextPermission()` surfaces the head only while no
request is active, and RespondToPermission promotes the next after
resolving the current one. The promote loop is FLAT (while + a
`_promotingPermissions` re-entrancy guard) because auto-approve answers
synchronously from inside the PermissionRequested raise -- N queued
auto-approved requests drain in one pass with constant stack depth.
Every existing pending-clear site (Start, Stop, HandleResult,
HandleProcessDeath) also clears the queue: a permission never straddles
a result boundary, and a queued request must not be promoted for a tool
call that no longer exists. The upper layers' single-active-permission
model (AgentHub.PendingPermission) is unchanged.

## UICODE-3: the 300-message cap prunes incrementally with hysteresis

The window anchor was recomputed as `count - 300` on every refresh, so
past the cap EVERY append moved it by one, failed the rendered-prefix
check, and full-rebuilt all 300 rows per message -- and FullRebuild
also reset the scroller, yanking an up-scrolled reader to the top.
Three changes:

- `ComputePruneAnchor(count, renderedStart, max, slack)` (pure,
  internal, PruneSlack = 50): the anchor HOLDS while the rendered
  window fits within max + slack, so ordinary appends stay on the
  O(delta) incremental path; it re-anchors to the newest max only when
  the slack is exhausted, or when nothing is rendered / the transcript
  shrank below the old anchor (session switch).
- A new droppable-head branch onto the incremental path: when the
  anchor moved forward and the remaining rendered ids still prefix the
  window, `PruneHeadRows` removes exactly the dropped head rows
  (O(drop)) and updates/creates the prune note in place, inside the
  same scroll capture/restore window as every other Refresh mutation.
- FullRebuild now captures wasSticking/savedOffset before Clear() and
  restores through the shared ComputeRestoredScrollValue, same as the
  incremental path.

## SEC-5: the commit moves the VALIDATED mirror bytes

BeginCommit copied staged .cs files to a Temp/ mirror and compiled the
mirror -- but FinishCommit moved the ORIGINALS from UapStaging/ into
Assets/, and AssemblyBuilder spans editor ticks, so a staged file
rewritten mid-compile smuggled unvalidated bytes into Assets/ wearing
a "validated, 0 compile errors" label. Now ALL staged files (.asmdef
included) are mirrored up front, the asmdef JSON check reads the
mirrored copy, and on success the MIRROR files are copied into Assets/
while the originals are simply deleted (commit consumes the stage,
whatever its content by then). CleanupBuildArtifacts moved from before
the move to after it -- in the old order it would have deleted the
mirror right before the mirror became the move source. A mirror-copy
failure before AssemblyBuilder owns the folder best-effort-deletes the
orphaned src_<id> and rethrows.

## SEC-7: a timed-out pollable is abandoned, not left to commit silently

Execute's 15 s wait threw TimeoutException but left the WorkItem alive;
a pollable (uap_scripts_commit) kept re-enqueueing itself every tick
and eventually ran FinishCommit -- files moved into Assets/ while the
HTTP caller had long been told the call failed. `WorkItem.Cancelled`
(volatile, set by the timed-out waiter) makes ProcessOnce discard the
item on next pickup: no poll, no re-enqueue, no terminal side effect --
matching the tool's documented "failure leaves the stage untouched"
semantics. The internal 15 s wall stays the EXECUTE-blocking bound;
the CLI-side MCP_TOOL_TIMEOUT/HTTP layer remains the outer budget for
pollables. Narrow race: a timeout landing during the item's terminal
tick can still complete it -- the flag closes the every-later-tick
window, not that single tick (commented in code).

## Tests

- AgentClientStateTests: second concurrent request queues (not raised)
  until the first is answered, both responses hit the wire with their
  own ids; result discards queued requests; a synchronous auto-approve
  subscriber drains a 2-deep queue flat.
- MessageListControllerScrollTests: ComputePruneAnchor table (hold
  within slack, re-anchor past it, shrink re-anchor) + a source scan
  pinning FullRebuild's capture/restore.
- UapScriptsCommitToolTests: staged file rewritten mid-compile --
  Assets/ receives the validated bytes, the stage is consumed. (The
  asmdef TOCTOU variant is deliberately not staged: importing a real
  .asmdef into the host project's Assets/ would hijack its assembly
  layout; the mechanism is shared with the .cs path.)
- UapMainThreadDispatcherPollingTests: timed-out pollable is discarded
  (no poll, no requeue, side-effect flag never set); the ordinary
  completion path still commits.
