# Design note: eliminating !important from USS (font-scale mechanism redesign)

- Date: 2026-07-31 / Status: adopted
- Related: LIVE USER TESTING defects (1) "overall layout broken, tables and
  the Settings view unreadable" and (4) "the transcript-bleed clip fix
  behaves intermittently"; supersedes the `!important`-based mechanism from
  docs/design-notes/2026-07-31-font-size-application.md (see that note's own
  "未検証事項" section, which already flagged this exact risk and had no way
  to check it at the time).

## Evidence gathered in the sandbox BEFORE changing anything

All runs below used the AITemp sandbox (`C:/Unity/UnityProjects/AITemp`,
Unity 2022.3.22f1, batch mode, the package folder symlinked in from this
repo) via a throwaway probe script
(`Assets/Editor/ProveUssDefect.cs` + `.asmdef`, sandbox-only, never part of
the package, deleted after use). Full logs:
`C:/Unity/UnityProjects/AITemp/Logs/prove-uss-defect.log`.

**1. Forced reimport of the CURRENT (pre-fix) AgentPanel.uss produced zero
warnings or errors.** `AssetDatabase.ImportAsset(path,
ImportAssetOptions.ForceUpdate | ForceSynchronousImport)` against the
`!important`-laden file logged only the routine "Start importing ..." line --
no `LogWarning`/`LogError` referencing AgentPanel.uss, the priority-flag
keyword, or any USS parse failure. This means the task's working hypothesis
("USS parse errors ... can drop the rest of the rule and historically more")
does **not** manifest as a visible/loggable parser failure in this exact
Unity patch version for this exact file's content -- the import "succeeds"
silently.

**2. Runtime measurement (real EditorWindow, not the no-op
CreateInstance-only path) tells the actual story.** `resolvedStyle` is never
computed for a window built via `ScriptableObject.CreateInstance` +
`CreateGUI()` alone (`root.panel` stays `null` -- no host view is ever
attached without `GetWindow`/`Show`), so an early probe attempt using that
technique (the one this repo's checked-in tests use for hygiene) read back
`0` for every case and was inconclusive. Switching the throwaway probe to
`EditorWindow.GetWindow<AgentPanelWindow>()` + `Show()` (never done in the
checked-in test suite, only in this disposable sandbox script) gave a real
`UnityEditor.UIElements.EditorPanel`, and forcing two style/layout passes via
reflection (`BaseVisualElementPanel.Update()`) made `resolvedStyle` mean
something. Measuring `.uap-header-title`'s resolved `font-size` (which reads
`var(--uap-font-size-body)`) while sweeping `uap-fontscale-11..16` on the
window's content root:

  | Condition | scale=11 | 12 | 13 | 14 | 15 | 16 |
  |---|---|---|---|---|---|---|
  | `uap-theme-dark` class ABSENT | 11 | 12 | 13 | 14 | 15 | 16 |
  | `uap-theme-dark` class PRESENT (real condition -- every window always has a theme class) | 12 | 12 | 12 | 12 | 12 | 12 |

  With no competing theme class, the `!important`-tagged custom-property
  override worked exactly as intended (proves the declarations themselves
  parse and store the correct value -- no corruption of THAT rule). With the
  theme class present -- the only condition that ever occurs in production,
  since `ApplyThemeAndStyles` always adds `uap-theme-dark`/`uap-theme-light`
  -- the resolved font-size is **pinned at the theme's own hardcoded value
  (12px) for every single scale selection**. The font-size slider is
  confirmed **100% inert** in every real configuration.

**3. Root cause, precisely.** `ThemeDark.uss`/`ThemeLight.uss` each define
the SAME `--uap-font-size-*` custom properties under their own
`.uap-theme-dark`/`.uap-theme-light` selector (e.g. `--uap-font-size-body:
12px;`, no priority flag). `AgentPanelWindow.LoadStyleSheets` loaded
`AgentPanel.uss` (which held the fontscale block) FIRST and the two theme
sheets AFTER. Both the fontscale selector and the theme selector are single
classes on the SAME element (the content root) -- equal specificity. Unity
USS resolves specificity ties the same way CSS does: by cascade/load order,
last declaration wins. Because Unity USS does not implement the priority-flag
keyword as an actual priority mechanism (item 1 above: it neither errors nor
does anything special -- it is inert token noise), the tie-break falls
straight through to load order, and the theme sheets ALWAYS load after
AgentPanel.uss, so they ALWAYS win regardless of what the fontscale class
says. This is a plain stylesheet-ordering bug, not a parser-corruption bug --
correcting the more dramatic "rules vanish" framing from the task brief with
what was actually measured, since asserting an unverified mechanism here
would be exactly the kind of mistake this whole fix round exists to close
out.

**4. The 5 shorthand-`var()` violations (padding/margin with a `var()` token
inside a multi-value shorthand) also measured CORRECTLY in this Unity
version** -- 1-value, 2-value, and mixed-literal 4-value shorthand forms all
resolved to the exact expected per-side pixel values with no warnings. They
are fixed anyway per the task's explicit landmine list: this behavior is not
documented/guaranteed by Unity, so a longhand-only rule removes a class of
"works today, breaks on the next editor point release" risk regardless of
whether today's release happens to tolerate it.

## Options considered (font-scale mechanism)

| Option | Description | Verdict | Why |
|---|---|---|---|
| (a) Separate stylesheet loaded after the theme sheets | Move the `uap-fontscale-N` blocks into a new `FontScale.uss`, loaded LAST by `LoadStyleSheets` | **Adopted** | Item 2's measurement directly confirms load-order is what Unity USS actually uses to break specificity ties -- putting the override sheet last is sufecient and requires touching only one file (a new one) plus a 4-element array. Zero change to any leaf text-class file, zero change to the "root class toggles custom properties, every text class reads them via `var()`" architecture that already works everywhere else. |
| (b) Per-scale longhand rules for every text class (`.uap-fontscale-14 .uap-text { font-size: 14px; }` etc.) | Explicit longhand override per scale per class | Rejected | The task's own framing calls this "verbose but unambiguous" -- verbose is an understatement: with ~10 text classes x 6 scales x ~1 property each this is 60 rules replacing 9, and every time a new text class is added elsewhere in the file it has to be manually added to 6 more selector lists here or it silently falls back to the theme default. The custom-property indirection is the entire point of the current architecture (one place to change 9 numbers); (b) throws that away for no benefit once (a) is proven to work. |
| (c) C#-side inline `style.fontSize` on every known text root | `FontScale` service walks leaf elements from `AgentPanelWindow`/`SettingsView`/etc. and sets `.style.fontSize` directly, no USS involved | Rejected for THIS pass | Immune to cascade subtleties entirely (this is genuinely its advantage), but requires touching every leaf-rendering file that emits text (`MessageBlockFactory`, `MarkdownRenderer`, `ToolActivityCard`, `PermissionCard`, `StatusBarView`, `HeaderView`, `ChatView`, `ComposerView`, `ContextBarView`, `SettingsView`, `HistoryView`, ...) to walk their labels/fields and apply+re-apply the inline style on every scale change, theme switch, and window reopen -- a MUCH larger, higher-regression-risk change than the task's stated scope ("eliminate !important ... redesign the font-scale mechanism") strictly requires once (a) is confirmed to work. Revisit only if a future Unity version changes cascade tie-break behavior and (a) stops working. |

## Decision

- Removed the entire `!important`-tagged fontscale block from
  `AgentPanel.uss` (0 occurrences of the priority-flag keyword remain
  anywhere under `Editor/UI/Uss/`).
- Added `Editor/UI/Uss/FontScale.uss` with the same 6
  `.uap-fontscale-11`..`.uap-fontscale-16` rules, values unchanged, priority
  flag removed (plain declarations).
- `AgentPanelWindow.LoadStyleSheets` now loads
  `{ "AgentPanel.uss", "ThemeDark.uss", "ThemeLight.uss", "FontScale.uss" }`
  -- FontScale.uss is LAST, so its declarations win the specificity tie
  against whichever theme class is active, verified against measurement #2
  above (this ordering is exactly what turned the "with-theme" column from a
  flat 12 into 11/12/13/14/15/16 in a follow-up measurement after the fix).
- `PermissionWindow` needed no change: it already builds its content root
  through the shared `AgentPanelWindow.ApplyThemeAndStyles`/`LoadStyleSheets`
  path, so it picks up the new sheet and load order automatically. Same for
  window reopen (`LoadStyleSheets` runs on every `CreateGUI`) and live slider
  changes (`ReapplyContentRootStyling` still just re-adds the
  `uap-fontscale-N` class; the sheet doing the winning is now a load-order
  fact, not something that has to be re-asserted per call).
- Fixed the 5 pre-existing shorthand-`var()` violations
  (`.uap-header-model-btn`/`.uap-header-history-btn` padding,
  `.uap-usage-popover` padding, `.uap-history-row-main` padding,
  `.uap-history-confirm` padding, `.uap-history-confirm-btn` margin) to
  explicit longhand per side. No value changes, purely mechanical.

## Regression guards (Tests/Editor/UssHygieneTests.cs)

1. `SourceScan_NoUssFile_ContainsThePriorityFlagKeyword` -- fails if the
   priority-flag keyword (spelled out in the test itself, not reproduced
   here to avoid the guard tripping on this very sentence) appears anywhere
   in any `.uss` file under the package's `Editor/UI/Uss/` folder, including
   inside comments (blunt on purpose -- the fix's own comments were reworded
   during this pass specifically to keep this guard green).
2. `SourceScan_NoShorthandProperty_ContainsAVarToken` -- fails if any of
   `padding`, `margin`, `border`, `border-radius`, `border-color`,
   `border-width`, `border-style` is declared with a value containing a
   `var(` token in any `.uss` file under the same folder. Longhand
   (`padding-top`, `border-top-color`, etc.) with `var()` is unaffected
   (that is the required, working form).

## What this does NOT explain

The measurements above fully explain and fix "the font-size slider does
nothing" (confirmed 100% inert) and give a mechanistic account for (4)'s
"behaves intermittently" being plausible (a tie-break silently decided by
incidental load order is exactly the kind of thing that "sometimes works"
across reimports/editor sessions if anything ever perturbs stylesheet load
order). They do NOT reproduce a "rest of the file drops" catastrophe for
THIS file's CURRENT content in Unity 2022.3.22f1 -- recorded here rather than
overclaimed. The broader "tables and Settings view unreadable" report may
have compounded from this bug (default font sizing looking cramped/wrong
next to other elements sized for a user's chosen larger scale) and/or have
contributing causes outside this investigation's scope; eliminating the
`!important` mechanism entirely (as directed) removes the one nonstandard,
now-measured-broken construct in the file regardless.
