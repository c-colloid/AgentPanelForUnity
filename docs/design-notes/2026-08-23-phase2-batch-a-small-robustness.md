# 2026-08-23 -- Phase 2 batch A: six small robustness/clarity fixes

First Phase 2 slice of the full-package review remediation (Phase 1
landed in v0.25.0). Six S-effort items, each with its own regression
pin; none changes any protocol or persisted format.

## CORE-2: RawLineForLog raise isolated inside DispatchLine

The diagnostics fan-out ran BEFORE ParseLine with no isolation, so a
throwing subscriber (Settings diagnostics tail) escaped to the pump's
per-line catch and the line's parse + state transition were skipped
whole. A buffered `result` line lost that way wedged the turn until the
10-minute silence backstop. The raise now has a dedicated try/catch that
logs and continues; the pump catch stays as the last resort. The
existing "state transitions before subscriber raises" ordering in
HandleResult is now pinned by a comment and a test that observes
State == Ready from inside a throwing TurnCompleted subscriber.

## UICODE-2: StreamingLabelPump.Suspend/Resume instead of Clear on view switch

ChatView.OnDeactivate called `_pump.Clear()`, destroying every entry.
Text growth alone never re-Tracks a label (structural signature excludes
it), so switching Chat -> Settings -> Chat froze in-progress streaming
labels at the switch-away text until block completion. `Suspend()`
unhooks the update loop but KEEPS entries; `Resume()` re-hooks when any
exist. Labels stay parented under the hidden view (display:none keeps
panel != null), and OnUpdate's existing panel==null drop still cleans up
labels replaced by a rebuild. Clear() remains for true teardown.

## UICODE-5: re-entrant CreateGUI now serializes live chat state

The re-entry guard tore down subscriptions but skipped
`_chatView.SerializeState()`, which OnDisable performed -- so a rebuild
(language switch) dropped the live scroll offset and any composer draft
that had not gone through the change event. Both paths now share
`TeardownLiveState()` with OnDisable's exact ordering (SerializeState
before DeactivateView).

## UICODE-8: inline-vs-window decision id survives ChatView rebuild

`_autoWindowDecidedRequestId` (one-shot decision dedup) was an instance
field; a panel rebuild while a can_use_tool request was pending swapped
in a fresh ChatView with the field null, re-ran the size decision, and
reopened a PermissionWindow the user had deliberately closed. The id is
now `private static string s_autoWindowDecidedRequestId` -- a pending
permission never survives a domain reload (the CLI is killed first), so
no serialization is needed; it clears at the same point as before (hub
pending gone). A source-scan test keeps the field static.

## UICODE-14: OS-created mono Font stamped HideAndDontSave

The OS mono fallback branch returned `Font.CreateDynamicFontFromOSFont`
results with hideFlags=None; a NewScene unload destroys such unowned
objects and re-creates the MissingReference flood the TextCore path
fixed on 2026-08-03. `StampOsFont` (idempotent, internal) marks the
Font in that branch only -- editor-bundled RobotoMono and the default
label font are editor-owned and must not be stamped. The dynamic OS
font manages a single internal atlas, so stamping the parent is
sufficient (no recurring guard).

## UXA-5: not-undoable badge explains itself

The badge said "Not undoable" with no consequence or guidance. It now
carries a tooltip (new L10n pair PermUndoNotSupportedTooltip, EN+JA):
the operation cannot be reverted with Undo/Ctrl+Z, review before
allowing. Set once in BuildSkeleton; display toggling never clears it.
