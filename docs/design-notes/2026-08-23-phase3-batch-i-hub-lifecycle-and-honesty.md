# 2026-08-23 -- Phase 3 batch I: the hub stops reporting what it did not observe

The HUB cluster is mostly one theme again, a level up from batch G's:
not "a tool reported a success it did not achieve" but "the hub
reported a RESULT nobody measured, or silently dropped work while the
UI implied otherwise". Items: HUB-4/5/6/7/8/9/10.

## HUB-8: the compile result now survives the reload it describes

The auto-continue-after-compile feature told the model "Compile result:
SUCCEEDED" by reading ConsoleErrorProvider AFTER the domain reload.
That provider's entries are plain statics -- they do not survive a
reload -- and compiler errors are delivered to the OLD domain
(assemblyCompilationFinished). So the query always saw an empty list.
The method's own doc comment rationalised this ("Unity does not reload
at all when a compile has errors, so reaching this method already
implies success"); the partial-failure case is the counterexample --
some assemblies compile and trigger the reload while others stay
broken -- and there the panel asserted SUCCEEDED, with an empty error
digest, on no evidence at all.

ReloadLifecycle.OnBeforeAssemblyReload now snapshots
ConsoleErrorProvider.VisibleCount>0 and FormatDigest() into two new
SessionState-backed fields (SessionStateBridge.LastCompileHadErrors /
LastCompileErrorDigest) while the old domain still holds the truth.
TryAutoContinueAfterCompile prefers that carry-over, consumes it, and
falls back to the live provider when nothing was carried (a manual
reload, an older session). VISIBLE count only, so the error-chip ignore
feature keeps working the way FormatDigest already respects it.

## HUB-6: continue-after-compile no longer races the resume

ReloadLifecycle.OnFirstUpdate called TryAutoContinueAfterCompile BEFORE
RestoreAfterReload, and that method sent synchronously. The send runs
EnsureStarted + SendUserMessage, so it opened a new turn ahead of the
resume: it clobbered the SessionStateBridge.TurnRunning flag
RestoreAfterReload reads to detect a mid-turn resume, and spawned the
CLI before the transcript/session restore had run. Split in two:
TryAutoContinueAfterCompile now only JUDGES and QUEUES (ticket
consumption stays unconditional -- that was a deliberate earlier fix),
and a new StartAutoContinueDrain releases the queue. OnFirstUpdate's
order is now load-bearing and documented: reconcile -> judge/queue ->
restore -> drain -> CompileGate.DrainPending. The existing suite was
updated to drive both halves (a ReloadSequence helper), because half
the sequence is not the sequence.

## HUB-5: a long compile no longer eats the drain window

The 60-second give-up timer for a queued continuation ran while Unity
was compiling -- but the send is BLOCKED during a compile by design, so
a two-minute compile alone could exhaust the window and make the panel
retract a continuation it had never had a chance to send. The tick now
re-bases its start while isCompiling (the idiom CompileGate.OnUpdate
has always used for its own wait), and the decision is a pure
AutoContinueAfterCompilePolicy.ShouldAbandonForTimeout -- compiling is
never a timeout, negative elapsed (clock moved backwards) is never a
timeout, and the boundary is pinned.

## HUB-7: background-thread errors are captured

ConsoleErrorProvider subscribed Application.logMessageReceived, which
Unity fires only for MAIN-thread logs. An Error/Exception from a Task,
a worker, or a third-party SDK thread never reached the error chip or
the compile digest -- invisible, in a feature whose whole job is to
notice errors. Now subscribes logMessageReceivedThreaded (one
subscription covering every thread; subscribing both would
double-count). The callback was already written for off-thread callers
(lock + enqueue, apply deferred to the main-thread pump), so nothing
downstream changed. The new test raises a real Debug.LogError from a
real background thread -- the one thing the *ForTests seams cannot
prove.

## HUB-9: an inert script gate says so

Two independent layers enforce the script validation gate: the
generated PreToolUse hook (Windows only) and the can_use_tool
pre-filter. `dangerouslySkipPermissions` removes the second by
construction -- the CLI stops asking -- so on macOS/Linux with that
setting on, a gate the Settings UI shows as ON blocks exactly nothing.
The gate's behavior is unchanged; what changes is that the hole is
stated out loud: a pure ScriptGateEffectivelyInert predicate (full
truth table pinned), a once-per-spawn Console log and a transcript
warning note, new L10n string in both catalogs. Believing in a
protection that is not running is worse than knowing it is off.

## HUB-10: a vetoed quit no longer kills the CLI

EditorApplication.wantsToQuit fires while the quit can still be vetoed
-- another package returning false, or Cancel on the unsaved-scenes
dialog -- and the handler called AgentHub.Shutdown() there. Cancel the
quit and the panel was silently disconnected (TurnRunning and all) in
an editor that went right on running. The kill is gone; wantsToQuit now
only refreshes the ZombieReaper record, and real teardown stays in
OnQuitting, which runs once the quit is settled. The leak this was
guarding against is already covered by that record plus ReapOrphansNow
on the next start.

## HUB-4: the single pending-permission slot, justified rather than assumed

The review flagged AgentHub._pendingPermission as a single slot that a
second concurrent can_use_tool would overwrite, orphaning the first
request forever. That failure is already closed -- upstream, by CORE-6
(batch D): AgentClient queues every can_use_tool and promotes exactly
one at a time, so the hub is never handed two. Rather than duplicate
the queue a layer up, the invariant is now documented at the slot and
asserted loudly: an overwrite with a different request id logs, because
if that client-side FIFO is ever weakened this is precisely where a
dropped request would resurface.

## Honest residuals

- HUB-9 surfaces the inert gate in the transcript and the Console. The
  review also suggested a Settings banner via SettingsViewLogic; the
  predicate is public and ready for it, but the banner itself is not in
  this batch.
- HUB-6's ordering is exercised through the hub's own seams
  (TryAutoContinueAfterCompile + StartAutoContinueDrain), not through a
  real ReloadLifecycle.OnFirstUpdate -- EditMode tests cannot perform an
  actual domain reload, and the file's existing suite documents that
  same boundary.
- HUB-8 trusts beforeAssemblyReload to run. A hard editor crash mid
  compile leaves no carry-over, and the fallback then reports whatever
  the fresh domain sees -- the same blind spot as before for that one
  case, now bounded to it.
