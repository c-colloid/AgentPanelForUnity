# Live model tracking after set_model (AgentClient.CurrentModel)

## Root cause

`docs/design-notes/2026-07-31-history-restore-flow-and-model-picker.md`
section 3 documented (and the implementation followed) "the current model"
as `client.InitMessage.Model` -- the resolved model name captured once from
the `system/init` message at connect time. That was correct as long as
`AgentClient.InitMessage` was the only place a model could ever be recorded.

It stopped being correct the moment `AgentHub.SwitchModel` started sending a
live `set_model` control_request (same note, same section): a successful
`set_model` genuinely changes which model answers the next turn (R02 section
5.4), but nothing ever touched `InitMessage.Model` afterwards --
`AgentClient.HandleControlResponse` only logs failures and raises
`ControlRequestResolved(kind, success, error)`; it never mutates any
"current model" field. Every reader of "the current model"
(`StatusBarView.ResolveModelName`, `HeaderView`'s picker button label, its
`GenericMenu` checkmark, and `StatusBarView.RefreshContextMeter`'s
`currentResolvedModel` key for `SelectPrimaryModelUsage`) reads
`InitMessage.Model` directly, so all four kept showing the pre-switch model
until the next full reconnect re-ran `system/init`.

The ctx meter compounds this: `AgentHub._lastModelUsage` was also left
untouched by `SwitchModel` (only `StartFresh`/`SwitchToSession` cleared it),
so after the next turn completed under the new model, `SelectPrimaryModelUsage`'s
exact-match branch keyed off the stale `currentResolvedModel` failed to find
the new model's `modelUsage` entry and silently fell back to the
largest-token heuristic -- which can pick the wrong entry entirely when a
helper model (e.g. title generation) is also present in the same result.

## Options considered

1. **Have `set_model`'s control_response carry the resolved model name and
   read it off the response.** Rejected: no captured fixture or research
   note documents the `set_model` success response's payload shape (unlike
   `can_use_tool`/`initialize`, R02/02b never captured one against a live
   process). Depending on an unverified wire field would repeat exactly the
   mistake `AgentClient.cs`'s `--allowedTools` comment already warns about
   elsewhere in this file. `LiveCliIntegrationTests.SetModel_LiveRoundTrip`
   only asserts `success`/`error`, not response shape.
2. **Re-run the whole `initialize` handshake after every `set_model`.**
   Rejected: wasteful (a second full commands/agents/models round trip for a
   one-field change) and `set_model` is documented as a lightweight
   "keeps running" control request, not a resync point.
3. **Track the requested model value locally and resolve it against the
   ALREADY-CACHED `InitializeResponse.Response["models"]` array (the same
   `value` -> `resolvedModel` mapping `HeaderView.ParseModels` already uses
   to render the picker), committing it as the live model only once the
   matching `control_response` reports success.** Chosen.

## Decision

`AgentClient` gains:

- A private `_liveModel` field, reset to `null` in `Start()` (a fresh
  `system/init` is always authoritative again).
- `SetModel(model)` resolves `model` (the alias/value the caller passed,
  e.g. `"haiku"`) against `InitializeResponse.Response["models"]` the same
  way `HeaderView.ParseModels` does (`value` -> `resolvedModel`), falling
  back to the raw `model` string when no match is found (manual/unlisted
  model ids still get a best-effort label instead of silently keeping the
  old one). It then tracks the outbound request with a callback (the
  `PendingRequestMap.Track` callback parameter already existed and was
  unused here) that commits `_liveModel` ONLY when the resulting
  `control_response` is `Success == true`. A failed or timed-out
  `set_model` leaves `_liveModel` untouched, matching the CLI's own
  behavior of leaving the previous model running.
- A new public `CurrentModel` property: `_liveModel ?? InitMessage?.Model`.
  This is additive -- `InitMessage.Model` itself is untouched, so nothing
  that legitimately wants "the resolved model system/init reported" (none
  of the current call sites do) is affected.

`StatusBarView.ResolveModelName`, `StatusBarView.RefreshContextMeter`'s
`currentResolvedModel`, and `HeaderView.OnModelPickerClicked`'s `isCurrent`
comparison now all read `client.CurrentModel` instead of
`client.InitMessage.Model`. This supersedes the "現在選択中モデルのハイライトは
`client.InitMessage.Model`...と比較する" line in the 2026-07-31
history-restore-flow-and-model-picker.md note section 3 -- read `CurrentModel`
wherever that note says `InitMessage.Model` for "the current model" from now
on.

`AgentHub.SwitchModel` additionally resets `_lastModelUsage` to a fresh empty
dictionary (the same pattern `StartFresh`/`SwitchToSession` already use),
so the ctx meter shows "no data" (hidden) rather than a stale model's
percentage under the new model's label until the next turn's `modelUsage`
arrives.

## Tradeoffs accepted

- If the CLI ever silently ignores a `model` value that IS in the
  `models[]` list (accepts the control_request with `success:true` but does
  not actually switch), the panel would show the new label anyway. No
  evidence of this happening has been captured; the alternative (never
  trusting `success:true`) would make the picker permanently stuck showing
  the old model, which is strictly worse for the common case.
- `CurrentModel` resets to `InitMessage.Model` only on the next `Start()`
  (full reconnect), not on e.g. `Stop()` alone -- consistent with every
  other per-connection field this class already resets exactly once, in
  `Start()`.

## Regression-guard tests

All in `AgentClientStateTests.cs` (Core-layer coverage; `StatusBarView.ResolveModelName`
and `HeaderView`'s picker are thin one-line read-throughs of `AgentClient.CurrentModel`
with no `InternalsVisibleTo` from Tests/Editor into the UI assembly, so the behavior both
actually delegate to is exercised at the `AgentClient` level instead):

- `CurrentModel_MirrorsInitMessage_WhenNoLiveSwitchHasHappened`
- `SetModel_CommitsLiveModelOnSuccess_AndResolvesAliasViaModelsList` -- asserts the
  resolved name (`"claude-haiku-4-5-20251001"`), not the raw alias (`"haiku"`), only
  commits once the matching `control_response` arrives, and that `InitMessage.Model`
  itself stays untouched.
- `SetModel_DoesNotCommitLiveModelOnFailureOrTimeout`
- `SetModel_UnmatchedValue_FallsBackToRawValueOnSuccess`
- `Start_ResetsLiveModel_SoAFreshConnectIsAuthoritativeAgain`

`AgentHub.SwitchModel`'s `_lastModelUsage` reset was left without a dedicated test
(the field is private/static and only observable through `AgentHub.LastModelUsage`;
the pre-existing `StartFresh`/`SwitchToSession` clears of the same field were also
never given a dedicated regression test) -- consistent with this codebase's existing
test-coverage boundary for `AgentHub`'s static composition-root methods.
