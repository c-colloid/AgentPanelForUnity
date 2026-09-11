# Design note: model picker label not updating immediately after a live switch

- Date: 2026-07-31 / Status: adopted
- Related: LIVE USER TESTING defect (2) -- "the model picker label does not
  update immediately after picking"; docs/design-notes/2026-07-31-live-model-tracking.md

## Root cause

`HeaderView.OnModelPickerClicked` calls `AgentHub.SwitchModel(option.Value)`.
`SwitchModel`:

```csharp
public static void SwitchModel(string model)
{
    ...
    if (_client != null && ...)
    {
        _client.SetModel(model);   // async: writes a set_model control_request, returns immediately
    }
    _lastModelUsage = new Dictionary<string, ModelUsage>();
    RaiseChanged();                // fires BEFORE the control_response arrives
}
```

`AgentClient.SetModel` only writes the outbound `control_request`; the
resolved model name is committed to the private `_liveModel` field (which
`CurrentModel` reads) inside a callback that fires later, when the matching
`control_response` line is actually pumped in `HandleControlResponse` ->
`PendingRequestMap.TryResolve`. So the `RaiseChanged()` inside `SwitchModel`
fires while `CurrentModel` is STILL the old value -- the header repaints
(via `AgentPanelWindow`'s `_dirty`/`RefreshIfDirty` loop, driven by
`AgentHub.Changed`) but has nothing new to show yet.

Once the `control_response` for `set_model` actually arrives (during a later
`AgentClient.Pump()` call), `_liveModel` DOES update and
`AgentClient.ControlRequestResolved` fires with `("set_model", success,
error)` -- but **nothing in `AgentHub` was subscribed to that event**.
`AgentHub.StartClient` wires up `StateChanged`, `TextDelta`,
`AssistantMessageCompleted`, `PermissionRequested`, `TurnCompleted`,
`TurnStalled`, `SessionIdChanged`, `ProcessDied`, `StderrLine` -- but not
`ControlRequestResolved`. So the moment `CurrentModel` actually becomes
correct, nothing marks the panel `_dirty`, and `HeaderView.Refresh()` /
`StatusBarView.Refresh()` are not called again until some UNRELATED event
happens to fire `AgentHub.Changed` (the next assistant turn's
`TurnCompleted`/`StateChanged`, a session switch, etc.) -- which reads as
"the label does not update immediately after picking" from the user's
perspective (it eventually catches up, just not until something else
repaints the panel).

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| A | Make `AgentClient.SetModel` synchronous (block until the response arrives) | Rejected | Violates the documented threading model (`Pump()`-driven, no blocking waits on the main thread inside a UI click handler) and would freeze the editor UI for the round-trip. |
| B | Have `HeaderView` poll `CurrentModel` every refresh tick regardless of `Changed` | Rejected | `HeaderView.Refresh()` already only runs when `AgentPanelWindow._dirty` is set from `AgentHub.Changed` -- polling unconditionally would mean repainting the whole header every 80ms tick even when nothing changed, for a problem that has a single, precise, already-existing event to hang off instead. |
| C | Subscribe `AgentHub` to `AgentClient.ControlRequestResolved` and call `RaiseChanged()` when `kind == "set_model"` | **Adopted** | `ControlRequestResolved` already fires exactly once, synchronously, on the pump thread at the moment `_liveModel` (and therefore `CurrentModel`) is updated -- see `AgentClientStateTests.SetModel_CommitsLiveModelOnSuccess_AndResolvesAliasViaModelsList`, which proves the AgentClient-side half of this contract already works. Relaying it through the SAME `RaiseChanged()`/`Changed` path every other mutation uses keeps the "events raise on the pump thread, main-thread marshaling already handled upstream by `EditorUpdatePump`" threading contract completely unchanged -- no new thread-hopping is introduced. |

## Decision (C)

- `AgentHub.StartClient` now also does
  `client.ControlRequestResolved += OnControlRequestResolved;` alongside the
  other per-client event wiring.
- `OnControlRequestResolved(string kind, bool success, string error)` calls
  `RaiseChanged()` when `kind == "set_model"` (both success and failure, so a
  failed switch also repaints -- `CurrentModel` itself only changes on
  success, but any future "switching..." affordance still needs to clear).
  Other kinds (`initialize`, `interrupt`, `set_permission_mode`) already have
  their own `RaiseChanged()` calls at the point they mutate observable state,
  so this stays scoped to `set_model` per the reported defect.
- Net effect: within one `AgentClient.Pump()` call after the CLI's
  `set_model` `control_response` line arrives, `AgentHub.Changed` fires,
  `AgentPanelWindow._dirty` is set, and the next 80ms `RefreshIfDirty` tick
  repaints `HeaderView` (model picker label + checkmark on next open) and
  `StatusBarView` (model name) with the new `CurrentModel` -- "one refresh
  tick of the success response" per the task requirement.

## Testability note

`AgentHub` is a static composition root tightly coupled to real CLI process
spawning and several other editor singletons (`PanelStateStore`,
`EditorUpdatePump`) with no dependency-injection seam for a fake client, so
it is not exercised directly by the EditMode suite (consistent with the rest
of this codebase -- no existing test drives `AgentHub.StartClient`). The
regression guard instead pins the exact event contract `OnControlRequestResolved`
depends on at the `AgentClient` layer (already the tested boundary for this
subsystem): `AgentClient.ControlRequestResolved` firing with
`("set_model", true, null)` and `CurrentModel` reflecting the resolved model
value immediately once the success `control_response` line is pumped, driven
through `FakeCliProcess` exactly like the existing
`AgentClientStateTests.SetModel_*` tests.
