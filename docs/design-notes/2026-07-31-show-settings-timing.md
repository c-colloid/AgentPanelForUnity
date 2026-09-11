# Design note: AgentPanelWindow.ShowSettings() silently not switching the view

- Date: 2026-07-31 / Status: adopted
- Related: PM-reproduced defect (3) -- "AgentPanelWindow.ShowSettings() called via
  reflection-free dynamic code did NOT switch the view (and returned as if the
  snippet produced an empty result)"; docs/design-notes/2026-07-31-view-container-switching.md

## Root cause

`ShowSettings()`/`ShowChat()`/`ShowHistory()` were implemented as:

```csharp
public static void ShowSettings()
{
    AgentPanelWindow window = Open();       // GetWindow<AgentPanelWindow>(...) + Show()
    window.SetActiveView(PanelViewKind.Settings);
}
```

`SetActiveView` starts with:

```csharp
if (_activeView == kind || !_built) { return; }
```

`_built` only becomes `true` at the end of `CreateGUI()`. `Open()`'s
`GetWindow<AgentPanelWindow>(...)` call returns as soon as the window
*instance* exists (freshly `ScriptableObject.CreateInstance`'d and queued for
display) -- it does **not** guarantee `CreateGUI()` has already executed on
that instance. Unity's UI Toolkit host invokes `EditorWindow.CreateGUI()`
lazily, tied to the window's host view actually needing to draw/lay out its
content for the first time, not synchronously inside `GetWindow`/`Show`. This
codebase's own `AgentPanelWindowTests` comment already documents a related
fact about `CreateGUI` timing being outside caller control ("Unity can invoke
CreateGUI a second time on the SAME window instance without an intervening
OnDisable/OnEnable (observed defect)") -- the same underlying looseness in
when Unity chooses to call `CreateGUI` explains both defects.

Net effect: calling `ShowSettings()` against a panel that is being opened for
the first time in the editor session (the exact "reflection-free dynamic
code" repro -- a fresh call with no panel already docked) can land the
`SetActiveView` call in the window between "instance exists" and "CreateGUI
has populated `_built = true`". The guard clause makes this a **silent
no-op**: no exception, no log, `_activeView` stays `Chat`, and the caller
gets nothing back (a void method) -- exactly "did not switch the view...
returned as if the snippet produced an empty result".

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| A | Force `window.CreateGUI()` synchronously right after `Open()` before calling `SetActiveView` | Rejected | `CreateGUI` already has a re-entry guard because Unity may ALSO call it again later through its own normal lazy path; forcing it ourselves just means it can legitimately run a *second* time afterwards, and the re-entrant path unconditionally rebuilds Chat-first (`_activeView = PanelViewKind.Chat`) -- our forced Settings switch would get silently stomped back to Chat when Unity's own real CreateGUI call eventually fires. |
| B | Queue the requested view and apply it once `CreateGUI` finishes building (whenever that happens) | **Adopted** | Never fights Unity over when `CreateGUI` actually runs; whichever build ends up being the "real" one applies the queued view at its own tail end, so the request always eventually lands instead of racing a rebuild. |
| C | Poll/wait (e.g. `EditorApplication.delayCall` loop) for `_built` before calling `SetActiveView` | Rejected | Same outcome as B with more moving parts (bounded retry counters, potential dropped requests if the window is closed before it builds) for no benefit over a simple queued field applied at the one place that already knows when the build finished. |

## Decision (B)

- Added `_pendingView` (`PanelViewKind?`) to `AgentPanelWindow`.
- `ShowSettings/ShowChat/ShowHistory` now go through `RequestView(kind)` ->
  `window.RequestActiveView(kind)`, which applies the switch immediately when
  `_built` is already `true`, or stores it in `_pendingView` otherwise.
- At the tail of `CreateGUI()`, right after `_built = true;` and the initial
  `RefreshIfDirty()`, a pending view (if any) is applied via `SetActiveView`
  and cleared. This runs on every `CreateGUI` completion, including the
  re-entrant rebuild path, so a request queued before a re-entrant rebuild
  still lands after it rather than being wiped out by the rebuild's
  Chat-first reset.
- `SetActiveView` changed from `private` to `internal` (paired with a new
  `Editor/AssemblyInfo.cs` granting `InternalsVisibleTo(
  "Colloid.AgentPanel.Editor.Tests")`) so EditMode tests can drive the exact
  switching logic `ShowSettings` delegates to without going through
  `GetWindow`/`Show` -- this codebase's tests deliberately avoid real
  window-manager side effects (see `AgentPanelWindowTests`'s own rationale
  comment), so exercising the public static entry points end-to-end in a
  checked-in EditMode test is not attempted; the regression guard instead
  covers (1) `SetActiveView` toggling roots correctly on an already-built
  window, and (2) `RequestActiveView` called BEFORE `CreateGUI` being applied
  once `CreateGUI` completes -- the exact timing gap this fix closes.

## Non-goals

This does not change `CreateGUI`'s re-entry-guard behavior (still always
rebuilds Chat-first) -- only ensures a caller's requested view is not lost
across that rebuild.
