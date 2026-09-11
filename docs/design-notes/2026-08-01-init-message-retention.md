# Design note: "CLI version: not connected" while Ready (InitMessage retention)

- Date: 2026-08-01 / Status: adopted
- Related: user question ("is this a bug?" -- yes); live diagnosis found
  `AgentHub.Client.State == Ready` with `AgentHub.Client.InitMessage ==
  null` at the same moment.

## Root cause (proven from checked-in code and an existing checked-in test, not guessed)

`AgentClient` has two independent paths into `AgentClientState.Ready`
from `Starting`:

1. `HandleSystemInit` (the `system/init` line) -- sets `InitMessage`,
   then transitions to Ready if still Starting.
2. `OnInitializeResolved` (the control_response for the "initialize"
   control_request `AgentClient.Start` sends immediately after spawn)
   -- transitions to Ready if still Starting, WITHOUT ever touching
   `InitMessage`.

The real captured CLI wire order (`docs/research/
02b-authenticated-captures.md`, line 13-16) is:

```
(out) control_request initialize
(out) user message
(in)  control_response  … initialize response (path 2, above)
(in)  system/init                              (path 1, above)
```

i.e. the control_response reliably arrives BEFORE system/init. This is
not a hypothetical race: it is already pinned by a checked-in test,
`AgentClientStateTests.InitializeControlResponse_AloneAlsoTransitionsToReady`,
whose own comment says "The captured stream really does deliver the
control_response BEFORE system/init; either order must reach Ready" --
and that test feeds ONLY the control_response, asserting `State ==
Ready` without `InitMessage` ever being set at all in that test. So
`Ready` reachable with a null `InitMessage` was already a known,
intentionally-supported state transition in this codebase; the defect
is not that this can happen momentarily, it is that nothing reliably
told any LISTENER once `InitMessage` did become available a moment
later:

- `HandleSystemInit`'s own `if (State == Starting) SetState(Ready)` is
  a same-state no-op once path 2 already got there first (`SetState`
  returns immediately when `previous == next`) -- no `StateChanged`
  event fires for that transition.
- `SessionIdChanged` only fires when `message.SessionId != SessionId`.
  For a brand-new `AgentClient` instance (`SessionId` starts null) this
  is reliably true on the FIRST system/init of that instance's life --
  but `AgentHub.StartClient` discards the OLD `AgentClient` object on
  every reconnect (`Reconnect()`, the auto-restart in `OnProcessDied`,
  `SwitchToSession`, `StartFresh`) and constructs a brand-new one each
  time, so in production this event DOES fire on every normal connect.
  What it does NOT do is give any OTHER consumer a way to learn
  "InitMessage changed" independent of a session-id change -- there was
  no dedicated signal for that fact at all, and relying on
  `SessionIdChanged` as a proxy for it is incidental, not a contract
  (a same-instance reconnect to the identical `--resume` session id,
  which the state machine's own `Start()` docs describe as a supported
  entry point (`"Valid from NotStarted or Errored"`), would keep
  `SessionId` unchanged and silently NOT fire it).

So the concrete, reproducible mechanism: on a normal reconnect (already
common -- `Reconnect()`, auto-restart after a crash, `SwitchToSession`,
`StartFresh`), the BRAND NEW client instance's own `InitMessage` starts
null and stays observably null to any UI reading `AgentHub.Client
.InitMessage` for however long it takes that instance's own
`system/init` line to be dispatched -- typically sub-second, but with
no ceiling guaranteed by the protocol, and criticaly: even after it DOES
arrive, nothing is guaranteed to tell a listener to re-read it if the
state was already Ready.

This is a client-side data/notification gap, not a UI-refresh gap:
`SettingsView.OnHubChanged`/`OnActivate` already call
`RefreshCliVersionLabel()` on every `AgentHub.Changed` while active (and
on activation) -- confirmed by reading the existing code before
touching anything, matching the same pattern
`RefreshDiagnostics`/`RefreshCliStatus` already use for the stderr
tail/CLI-path rows. Re-rendering more often cannot fix a value that is
still null in the underlying `AgentClient` at read time.

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| (a) Gate `Ready` on system/init only; remove `OnInitializeResolved`'s independent Ready transition | Eliminates the divergence entirely: `InitMessage != null` becomes an invariant of `Ready` | Rejected | Directly contradicts an existing, deliberately-authored and -tested contract (`InitializeControlResponse_AloneAlsoTransitionsToReady`'s own comment: "either order must reach Ready"). Whoever wrote that test had a reason to want Ready reachable from the control_response alone (most likely: don't block the panel becoming usable on whichever of the two messages happens to be slower); reversing it here, as a side effect of an About-panel display bug, is exactly the kind of undocumented behavior change the project's fix-process policy exists to prevent. |
| (b) Add a dedicated `AgentClient.InitMessageReceived` event fired unconditionally whenever `HandleSystemInit` sets `InitMessage`, plus `AgentHub`-level retention of the last value across reconnects, with `SettingsView` falling back to the retained value while connected | Closes the notification gap deterministically (no reliance on StateChanged/SessionIdChanged happening to also fire) and smooths the sub-second window even further via retention | **Adopted** | Additive-only: no existing event's firing contract changes, `Ready`'s two-path reachability is untouched, and the new event's own doc comment plus this note make the "why not just use StateChanged/SessionIdChanged" reasoning explicit for the next reader. Testable exactly like the existing suite (FakeCliProcess + fixture replay), matching this codebase's established AgentClient test style. |
| (c) Poll `AgentClient.InitMessage` on a timer instead of relying on any event | Sidesteps the missing-event problem entirely | Rejected | This codebase already has an edge-triggered, event-driven refresh pattern for every other live row (stderr tail, CLI path, reconnect hint); introducing a poll for exactly one field breaks that consistency for no benefit once (b)'s dedicated event exists. |

## Decision

- `AgentClient` gains `public event Action<SystemInitMessage>
  InitMessageReceived`, raised unconditionally at the top of
  `HandleSystemInit` (right after `InitMessage = message;`), independent
  of any state transition or session-id comparison.
- `AgentHub` subscribes (`client.InitMessageReceived +=
  OnInitMessageReceived` in `StartClient`, alongside its other
  subscriptions) and retains the value in a new static field,
  `_lastKnownInitMessage`, exposed as `AgentHub.LastKnownInitMessage`.
  This field is intentionally NEVER cleared by `TearDownClient`/
  `StartFresh`/`SwitchToSession` -- unlike `_lastModelUsage` (which IS
  cleared on those paths because it describes THIS conversation's
  numbers), CLI version/session metadata describes the installed CLI
  binary, which does not change across a reconnect or a fresh session,
  so retaining it indefinitely within the editor session is correct,
  not stale.
- `SettingsView.RefreshCliVersionLabel` still prefers the LIVE
  `client.InitMessage` when present (this connection's own, most
  authoritative value); only when that is null AND the client exists
  and is NOT `NotStarted`/`Errored` (i.e. genuinely connected or
  connecting, just without its own system/init processed yet) does it
  fall back to `AgentHub.LastKnownInitMessage`. A truly disconnected
  client (`null`, `NotStarted`, or `Errored`) still shows "not
  connected" -- preserving the original method's own documented intent
  ("shows 'not connected' before then/after a disconnect rather than a
  stale last-seen value").
- The About row's live-refresh wiring
  (`SettingsView.OnHubChanged`/`OnActivate` calling
  `RefreshCliVersionLabel()`) was ALREADY correct before this change
  (verified by reading the code, not assumed) and needed no edit; this
  note records that explicitly so a future reader does not go looking
  for a UI-refresh bug that was never there.

## Regression guard

`AgentClientStateTests.cs` gains a test reproducing the exact captured
order (control_response, THEN system/init) and asserting
`InitMessageReceived` fires exactly once, with the correct
`ClaudeCodeVersion`, even though `State` was ALREADY `Ready` by the time
system/init arrived -- pinning the specific gap this fix closes (the
existing `InitializeControlResponse_AloneAlsoTransitionsToReady` test
only proves Ready is reachable without system/init; it does not prove
anything about what happens once system/init arrives afterward, which
is the part that was broken).

## Verification

Compile + full EditMode suite (see the verification log for this
round's exact numbers).
