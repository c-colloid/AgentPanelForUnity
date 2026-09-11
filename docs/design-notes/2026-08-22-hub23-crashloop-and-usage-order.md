# 2026-08-22 -- HUB-2/HUB-3: drain vs crash-loop guard; usage save order

Two Medium AgentHub findings from the Phase 1 review, fixed on the CI net
(dotnet-smoke + full EditMode).

## HUB-2: CompileGate's drain defeated the crash-loop suspension

`OnProcessDied` stops auto-reconnecting once `_consecutiveDeaths` passes
`MaxConsecutiveRestarts` (3) and posts its "connection suspended" note. The
auto-continue path already respects that (the defect-4 fix), but
`CompileGate.OnUpdate` -- the queued-send drain that runs every editor
update tick while messages are pending -- called `AgentHub.EnsureStarted()`
unconditionally whenever the client was dead. With a CLI that dies on
launch (bad path, broken install), that is a spawn -> instant death ->
respawn loop at update-tick frequency, fighting the very suspension the
panel just reported.

Fix: `AgentHub.IsCrashLoopSuspended` (internal, one source of truth --
the auto-continue guard now reads it too) checked at the top of the drain
tick. While suspended: warn once, unhook, KEEP the queue -- the same
never-drop contract as the drain's existing timeout branch. A successful
manual reconnect resets the counter (existing Starting->Ready behavior)
and the next SendOrQueue/DrainPending re-arms.

Unlike the auto-continue path (which ABANDONS its synthetic message with a
retraction note, correct for machine-generated text), the drain preserves
its queue: it holds text the USER typed.

Tests: `CompileGateCrashLoopTests` -- threshold pin (3 restarts still
allowed, 4th suspends), suspended tick keeps the queue and never reaches
the respawn branch (LogAssert would flag a real spawn attempt's errors),
re-arm behavior. New seams: `CompileGate.OnUpdate` internal,
`CompileGate.ResetForTests`.

## HUB-3: fresh/switched sessions saved with the previous session's usage

`StartFresh` and `SwitchToSession` both wrote `SessionCache.Save(_session,
_lastModelUsage)` BEFORE resetting/seeding `_lastModelUsage`, so the new
session's cache was persisted still paired with the PREVIOUS conversation's
per-model usage. Kill the editor before the next natural Save and the next
boot restores a fresh/switched session showing the old session's token
numbers -- exactly the lingering the field's own doc comment forbids.

Fix: reorder -- reset (StartFresh) / seed from the transcript
(SwitchToSession) first, then Save. Two-line moves; the surrounding
lifecycle order is untouched. `SwitchSessionModel`'s clear needs no such
change (it writes no cache pairing itself).

No dedicated test: both methods tear down and spawn a real CLI process,
which the EditMode environment cannot do (the same reason StartClient has
seam-level tests only). The ordering will come under test when HUB-1's
consolidation extracts the session-reset step into a seam.
