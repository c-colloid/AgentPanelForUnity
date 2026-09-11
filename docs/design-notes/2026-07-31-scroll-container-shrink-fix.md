# Design note: fixing the Settings/History/markdown-table layout-freeze
  (`root.styleSheets.Clear()` removal)

- Date: 2026-07-31 / Status: adopted
- Related: docs/design-notes/2026-07-31-settings-layout-freeze-investigation.md
  (the PROVEN root-cause investigation this note implements the fix for).
  This note covers only the fix -- options considered, the decision, and
  verification -- not the causal proof itself.

## Recap of the proven root cause (see the investigation note for evidence)

`AgentPanelWindow.CreateGUI` called `root.styleSheets.Clear()` (added in
commit 746156c, Phase 3) to tear down the window root before rebuilding it
on a re-entrant `CreateGUI` call. On a real `EditorWindow` root, `Clear()`
removes not just the package's own 4 stylesheets but also Unity's own
implicitly attached editor default stylesheet
(`DefaultCommonDark_inter.uss` / `DefaultCommonLight_inter.uss`), which this
window never added and does not own. That default sheet is the sole source
of `.unity-scroll-view__content-container { flex-shrink: 0; }`. Without it,
every `ScrollView`'s content container falls back to UI Toolkit's initial
`flex-shrink: 1`; Yoga implements no min-content floor, so content taller or
wider than the viewport gets flex-crushed to viewport size instead of
overflowing/scrolling, and `flex-shrink: 1` children (Settings/History
labels, the horizontal `uap-md-tablewrap` markdown-table columns) get
crushed to ~1-4px boxes while text still paints at full glyph size --
producing the reported overlap garbage. Proven bidirectionally in the live
editor (see the investigation note's probes 4 and 5).

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| (a) Targeted removal of only the package's own 4 sheets | Re-resolve the same 4 `StyleSheet` assets `LoadStyleSheets` adds (by `AssetDatabase.LoadAssetAtPath`, which returns the same instance within a domain) and `Remove` them individually instead of `Clear()`ing the whole collection | **Adopted** | Directly targets the proven mechanism: the re-entry guard only ever needed to undo what THIS window added, never what it did not. `AssetDatabase` identity guarantees `styleSheets.Remove(sheet)` matches the exact instance `LoadStyleSheets` added on the prior pass, so this is exact, not a heuristic. Leaves any foreign sheet (Unity's implicit default, or anything else attached by a host/inspector in the future) completely untouched. |
| (b) Defensive USS rule only (`.unity-scroll-view__content-container { flex-shrink: 0; }` in `AgentPanel.uss`) without touching `CreateGUI` | Keep `Clear()`, but re-assert the one rule that matters in the package's own sheet, reloaded right after | Rejected as the sole fix | The investigation note is explicit that the default sheet's loss is not just a `flex-shrink` problem -- it also un-styles `TextField`/`Toggle`/`ScrollView` scroller visuals and any other implicit editor-default rule the window silently depended on. A single re-asserted rule would mask the Settings-crush symptom while leaving native-control chrome (and any other implicit rule nobody has audited) broken. Still valuable as a second line of defense (see below), never sufficient alone. |
| (c) Track added sheets in an explicit field (e.g. a `List<StyleSheet>` populated by `LoadStyleSheets`) instead of re-resolving by path | Store references from the add pass, iterate that list on teardown | Rejected | Functionally equivalent to (a) for a single-window-instance lifetime, but adds a mutable field that has to be kept in sync with `PackageStyleSheetFiles` by hand and can silently drift (e.g. a future contributor adds a sheet via a different code path that forgets to append to the tracked list). Re-resolving by path off the single `PackageStyleSheetFiles` array (already the source of truth `LoadStyleSheets` itself iterates) means there is exactly one place that lists "the sheets this window owns" and both load and teardown read the same array -- no second source of truth to drift. |
| (d) Stop clearing anything; rely on `root.Clear()` (element tree) alone and let `LoadStyleSheets`'s own `Contains` guard prevent duplicate stylesheet entries | Drop the teardown call entirely | Rejected | `root.Clear()` only removes child *elements*, not stylesheets -- stylesheets live on the `VisualElement.styleSheets` collection independently of its children. Without SOME removal step, a re-entrant `CreateGUI` would leave stale package sheets attached forever if their asset paths ever changed between calls (not currently possible, but removal-then-add is the robust invariant, and it is also exactly what the regression test `CreateGUI_ReentrantCall_DoesNotDuplicateThePackageStyleSheets` checks for). `LoadStyleSheets`'s `Contains` guard already prevents *duplicate* entries for the unchanged-path case, but doesn't replace the need for a real teardown step when re-entry is meant to fully rebuild state. |

## Decision

1. **`AgentPanelWindow.CreateGUI`** no longer calls `root.styleSheets.Clear()`.
   It calls the new `RemovePackageStyleSheets(root)` instead, which
   re-resolves each of the 4 `PackageStyleSheetFiles` paths via
   `AssetDatabase.LoadAssetAtPath<StyleSheet>` (the same call
   `LoadStyleSheets` uses to add them) and removes only the ones present on
   `root.styleSheets`. `PackageStyleSheetFiles` is the single source of
   truth both methods read, so add and remove can never drift out of sync
   with each other.
2. **Defensive USS baseline retained/added regardless**: `AgentPanel.uss`
   now carries its own explicit
   `.unity-scroll-view__content-container { flex-shrink: 0; }` rule (tokens-
   free, structural only) as a second line of defense per option (b)'s
   rejected-as-sole-fix reasoning -- it protects any future host root that
   never had Unity's implicit default sheet in the first place (a bare
   `VisualElement` panel, a future headless test harness, or a Unity version
   that changes what ships implicitly), without pretending it is the real
   fix for the reported defect.
3. No change to `FontLoader`, view-activation timing, or forced re-measure
   logic. The investigation note explicitly disproved the stale-font
   hypothesis (H1) and the hidden-build hypothesis (H2) with live probes
   (same live `FontAsset` instance on every path; a visibly-built vanilla
   `ScrollView` also collapsed under `Clear()`) -- there is nothing in
   those areas to fix for this defect. Chasing font-asset lifetime or lazy
   view construction here would not touch the actual mechanism and was
   correctly ruled out before this note was written.

## Regression guards (`Tests/Editor/AgentPanelWindowStyleSheetTests.cs`)

All headless, via `ScriptableObject.CreateInstance<AgentPanelWindow>()` +
`CreateGUI()` (the same no-`GetWindow`/no-`Show` pattern
`AgentPanelWindowTests` already uses -- see that file and
docs/design-notes/2026-07-31-font-scale-cascade-fix.md for why this
codebase's checked-in suite avoids `GetWindow`/`Show`). A plain in-memory
`StyleSheet` instance stands in for Unity's implicit default sheet: without
a real attached panel, no style *resolution* runs against its (empty)
contents, but `styleSheets.Add`/`Remove`/`Contains` -- exactly what
`CreateGUI`'s teardown logic touches -- work identically to a real window.

1. `CreateGUI_DoesNotRemoveAForeignStyleSheet_ItDidNotAddItself` -- adds a
   foreign sheet directly to `rootVisualElement.styleSheets` before calling
   `CreateGUI()`; asserts it is still present afterward. This is the direct
   regression guard for the proven defect.
2. `CreateGUI_ReentrantCall_LeavesTheForeignStyleSheetUntouched` -- same
   setup, but calls `CreateGUI()` twice (the re-entry scenario the teardown
   logic exists for) and asserts the foreign sheet survives both passes.
3. `CreateGUI_ReentrantCall_DoesNotDuplicateThePackageStyleSheets` -- asserts
   `styleSheets.count` is identical after a first and a second `CreateGUI()`
   call, i.e. the package's own 4 sheets are removed-then-re-added, never
   accumulated.
4. `SourceScan_AgentPanelWindow_DoesNotCallStyleSheetsClear` -- a source-text
   guard (mirroring the `SourceScan_*` pattern in `UssHygieneTests.cs`) that
   fails if the literal `styleSheets.Clear()` call ever reappears in
   `AgentPanelWindow.cs`, even via an edit that does not touch this design
   note or the investigation note.

## What this does NOT cover

- The literal text of `.unity-scroll-view__content-container { flex-shrink:
  0; }` inside `DefaultCommonDark_inter.uss` was never read directly from
  the asset (the investigation note flags this as unproven-but-functionally-
  verified). Not re-checked here; the fix does not depend on the exact
  wording, only on not removing the sheet.
- Real Yoga flex-shrink/layout-crush behavior on a live window (does
  Settings/History/the markdown table actually render at natural size
  again with a real docked panel) is out of reach for a headless EditMode
  test under this codebase's established no-`GetWindow` convention (see
  above). This was confirmed live during the investigation (probe 4: the
  in-memory `flexShrink=0` mitigation on the live Settings content
  container, screenshot `docs/verify/Agent Panel_20260731_140400_270.png`)
  and is exactly what the code fix removes the need for permanently. A
  follow-up manual/live check after this fix lands should confirm Settings,
  History, the markdown-table horizontal wrap, and the chat transcript
  ScrollView all render and scroll normally with a freshly opened panel (no
  memory-only mitigation involved) -- see "Verification" below.
- The chat-message-list `ScrollView`'s scroll-amount computation
  (`vscrollerHigh`) under the pre-fix `contentShrink=1` state was flagged as
  suspected-but-untested in the investigation note. This fix removes the
  shared cause (the missing default sheet), so it is expected to be resolved
  as a side effect, but was not independently re-measured here.

## Verification

- Regression suite above (headless, EditMode) exercises the exact
  add/remove mechanism.
- `UssHygieneTests.cs`'s existing `!important`/shorthand-`var()` guards are
  unaffected by this change (different rules, same file).
- Live verification (manual, sandbox `C:/Unity/UnityProjects/AITemp`):
  compile the package, open the Agent Panel window fresh (no memory-only
  mitigation), open Settings and confirm labels/TextField/Toggle render at
  natural size with a working vertical scroller when content exceeds the
  viewport; open a session with a markdown table wide enough to require the
  `uap-md-tablewrap` horizontal scroll and confirm columns render at natural
  width; trigger a re-entrant `CreateGUI` (dock/undock or the documented
  double-CloneTree repro) and confirm no duplicate stylesheet entries and no
  regression of the fix.
