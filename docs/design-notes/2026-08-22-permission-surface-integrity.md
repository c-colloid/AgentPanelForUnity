# 2026-08-22 -- UICODE-6/UICODE-1: permission-surface integrity

Two Phase 1 findings on the permission UI, fixed together on the CI net.

## UICODE-6: a stale Always-allow menu still wrote a permanent allow

The Always-allow GenericMenu callback fires only after the native menu
closes. By then the pending request can have moved on -- the other host
answered (inline card and floating window share one request), Y/N hotkeys,
an auto-approve level raise auto-answering, turn end, `AbortOpenTurn`,
process death, or a second `can_use_tool` superseding the slot.
`AgentHub.RespondToPendingPermission`'s request-id guard correctly DROPPED
the wire response (and its CLI-durable `updatedPermissions`) in every such
case -- but `PermissionCard.OnAlwaysClicked` called
`PersistAcceptedRuleIfApplicable` BEFORE responding, unconditionally. A
decision the user never completed still permanently widened
`PanelSettings.allowedTools`, which feeds `--allowedTools` on every future
spawn and suppresses all future prompts for that tool. The card's own
comment ("a suggestion captured for request A can never widen permissions
on a later request B") was true for only one of the two persistence
surfaces.

Fix: `RespondToPendingPermission` now returns bool -- TRUE only when the
decision was applied to the live pending request -- and the card persists
the durable rule only on TRUE. One staleness authority for both surfaces;
no second check invented. (A void-return reorder would not have worked:
the hub nulls `_pendingPermission` before returning on the success path,
so the caller cannot distinguish accepted-from-dropped by re-reading
state.)

Deliberately unchanged: persistence still ignores the CLI suggestion's
`destination` (the 2026-08-12 "always allow never stuck" WebFetch fix
depends on panel-side persistence even for `session`-scoped suggestions);
the only new condition is request liveness. `AgentClient.RespondToPermission`
still performs no id validation of its own -- today every caller either
uses the fresh request or goes through the hub guard; tightening the
client layer would disturb PermissionFlowTests' exact-transcript pins for
no live exposure, and is left for the CORE-6/HUB-4 FIFO work.

Tests: `AgentHubStalePermissionTests` -- matching id true+writes, stale id
false+no write+pending kept, nothing pending, supersession (old id false /
new id true), null/empty id.

## UICODE-1: PermissionWindow.CreateGUI had no re-entry guard

`RebuildAllOpenPanels` (the language-switch rebuild) re-invokes
`CreateGUI()` directly on every open window of BOTH types, and its doc
comment always claimed the path was "re-entry-safe" -- true only for
AgentPanelWindow, which tears down subscriptions/scheduler and
`root.Clear()`s when `_built` is already true. PermissionWindow -- open
exactly when a permission request is on screen -- appended a second
host+PermissionCard under the never-cleared root (duplicate cards, the
stale one frozen in the old language), subscribed `AgentHub.Changed` a
second time (OnDisable removes only one occurrence, leaking one past
teardown), and abandoned the first scheduled refresh loop.

Fix: mirror AgentPanelWindow's guard -- on re-entry unsubscribe, pause+null
the refresh loop, reset `_built`, then `root.Clear()` before rebuilding.
Stylesheets need no special handling: `LoadStyleSheets` is already
idempotent by asset identity.

Tests: `PermissionWindowRebuildTests` -- headless
`ScriptableObject.CreateInstance` + repeated `CreateGUI()` (the
AgentPanelWindowStyleSheetTests pattern): exactly one `uap-permwin-root`
host after any number of rebuilds, and no throw.
