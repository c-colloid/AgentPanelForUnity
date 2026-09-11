# Per-row controls must form a straight column (2026-08-03)

Live report: in the new History list, the "..." menu button's position
shifts with the title length, so finding it costs an eye movement on every
row. The user also raised it as a **standing rule**, not just this bug:
when the same control is repeated once per row in a list, it has to line up
in one column (and at one offset within its row), or usability drops.

This is the **second** time this package has shipped this defect class, so
the fix includes a regression guard and a written rule, not just a
declaration.

## 1. Root cause, measured

`resolvedStyle` and geometry read out of the live editor with 18 real
sessions on screen (panel 315.5 px wide):

```
resolved flex-grow on .uap-history-row-main = 0
row  0  btnX=279.5  mainW=275.5  rowW=300.5   "Show scene hierarchy and lis~"
row  7  btnX=140.0  mainW=137.0  rowW=300.5   (short CJK title)
row 14  btnX=193.0  mainW=190.0  rowW=300.5
row 16  btnX=174.0  mainW=171.0  rowW=300.5
BUTTON X SPREAD = 139.5 px
```

Every row is the same width (300.5 px). The content column is not: it
ranges 137.0-276.5 px, and the button — its next sibling in a
`flex-direction: row` container — is dragged along with it. The spread is
**139.5 px, roughly half the panel width.**

The cause is UI Toolkit's default: `flex-grow` is 0, so a child sizes to
its content unless told otherwise. `.uap-history-row-main` never declared
it. (UITK also defaults `flex-shrink` to 0, unlike web CSS — already
recorded in R05 — which is the other half of this same trap.)

## 2. Prior occurrence

v0.9.0, Settings: a subagent-model `PopupField` with `flex-shrink: 0` sat
next to a delete button; a long model name pushed the **delete button off
the panel entirely**. Same root shape — a variable-width sibling deciding
where a fixed control lands — with a worse outcome.

Two occurrences of one shape is a missing rule, not two mistakes.

## 3. The rule

> In a `flex-direction: row` container that is rendered once per list item,
> the **content** sibling declares `flex-grow: 1` (normally with
> `flex-shrink: 1`), the container declares `justify-content: space-between`,
> and every **trailing control** declares `flex-shrink: 0`. The trailing
> control's position must never depend on content length. Vertically,
> prefer `align-self: flex-start` over centering when rows can differ in
> height, so the control keeps a constant offset from its row's top edge.

### 3.1 Why both `justify-content` and `flex-grow`

Raised by the user while this was being fixed: the parent's
`justify-content` can position the children too. It can, and the two are
not alternatives — they cover overlapping halves of the problem, so both
are declared:

| | what it does | what it does NOT do |
|---|---|---|
| `justify-content: space-between` on the container | Distributes whatever free space exists, pinning the trailing control to the right edge **whether or not any child grows**. | Nothing when there is no free space; the content column stays at its natural width, so hover/click only cover the text. |
| `flex-grow: 1` on the content sibling | Leaves no free space: the content fills the row, so the hover highlight and click target cover the whole row and `text-overflow: ellipsis` has the full width to work against. | Nothing if a later edit removes it -- and then the button silently drifts again. |

So `justify-content` is the **safety net** (a future edit that drops
`flex-grow` degrades to "still aligned" instead of "back to the 139.5 px
zig-zag"), and `flex-grow` is what makes the row behave like one target.
With `flex-grow` present, `justify-content` has nothing to distribute and
no visible effect today — which is precisely why it is worth keeping and
worth a comment saying so, or someone will delete it as dead style.

## 4. Fix

- `.uap-history-row-main` gains `flex-grow: 1; flex-shrink: 1;`.
- `.uap-history-row-menu-btn` gains `min-width: 22px` so the button's own
  width — and therefore the column its left edge forms — cannot drift if
  the label glyph ever changes. `align-self: flex-start` is kept
  deliberately (see the rule above; rows differ in height because only some
  carry a sub-line).
- Both carry comments recording the measurement, so the next person to
  "tidy up" the stylesheet sees why the declarations are load-bearing.

Side benefit, not the motivation: with the content column filling the row,
the hover highlight and the click target now cover the whole row instead of
just the text.

## 5. Regression guard

`UssHygieneTests` gains a curated list of class selectors that MUST declare
`flex-grow: 1`, seeded with `.uap-history-row-main` and extended with
whatever the package-wide audit turns up (section 6). A listed class whose
rule block has gone missing fails too — a silently skipped entry is a guard
that has quietly stopped guarding.

Why a stylesheet scan rather than a real layout assertion: computing UITK
layout in an EditMode test needs a live panel and a layout pass, which is
flaky in batch mode. The stylesheet scan is deterministic and catches the
actual regression (someone deleting the declaration); the geometry itself
is verified live, as in section 1.

## 5.1 Verification

Same live measurement as section 1, re-run after the fix, same 18 sessions
in the same panel:

```
resolved flex-grow on .uap-history-row-main = 1
row  0  btnX=277.5      row  7  btnX=277.5      row 14  btnX=277.5
row  1  btnX=277.5      row  8  btnX=277.5      row 15  btnX=277.5
...every row identical...
BUTTON X SPREAD = 0.0 px      (was 139.5 px)
```

Content-column width is now uniform at 274.5 px regardless of whether the
title is a long English sentence or a short CJK one, and the button forms
an exact vertical column.

## 6. Package-wide audit

A read-only audit correlated the C# element construction against the USS
rule blocks for every list-like surface: History, subagent cards, tool
activity cards, permission cards, attachment chips, extension-profile rows,
subagent model override rows, quick actions, context bar chips, status bar,
composer, header, banners, first-run and empty-state views, plus all four
stylesheets.

**Confirmed defects outside the History row: none.** Every other repeated
row already gives its content sibling `flex-grow: 1` and its trailing
control `flex-shrink: 0` -- `.uap-toolcard-summary`, `.uap-subcard-desc`,
`.uap-perm-summary-title`, `.uap-banner-text`, the quick-action rows, and
the v0.9.0 model row.

Three classes of near-miss were found and all three are fixed here.

### 6.1 An unshrinkable LEADING label (real, currently live)

`.uap-toolcard-name` and `.uap-subcard-type` declared `flex-shrink: 0` with
no cap and no ellipsis, and they render the **raw wire tool name**. That
was safe while every tool was called `Read` or `Bash`. It stopped being
safe when this package started shipping its own MCP server:

```
on screen right now:  mcp__unity-ops__uap_query_component_types   (41 chars)
also in the registry: mcp__uap-ops__uap_prefab_revert_added_gameobject (48)
MeasureTextSize at the live label style (fontSize 11): 48 chars -> 300.5 px
panel content column: ~300 px
```

An unshrinkable 300.5 px label in a ~300 px row consumes the entire header,
starves the summary column, and pushes the time and expand chevron out of a
container that clips (`overflow: hidden`) -- so the card cannot be expanded
at all. This is the same defect shape as sections 1 and 2 with the roles
swapped: the *leading* sibling, not the trailing control, is the one whose
width is unbounded.

Fixed in two halves, because either alone is insufficient:

- **Layout (USS):** `flex-shrink: 1`, `max-width: 50%`, `nowrap` +
  `text-overflow: ellipsis`. This makes overflow structurally impossible
  regardless of what any future tool is named.
- **Content (C#):** the `mcp__<server>__` prefix is stripped for display,
  with the full wire name kept in the tooltip. Necessary because UITK's
  ellipsis only truncates at the END, so a capped
  `mcp__uap-ops__uap_prefab_revert_added_gameobject` would render as
  `mcp__uap-ops__uap_pref...` -- hiding the only part that says what the
  tool does.

### 6.2 Shared button classes relying on an implicit default

`.uap-card-btn` and `.uap-settings-btn` are reused across a dozen unrelated
call sites and never declared `flex-shrink`. They were correct only because
UITK's default happens to be 0, and two rows (the extension-profile
Approve/Revoke button, FirstRunView's Browse button) had no other
protection. Both classes now state `flex-shrink: 0` explicitly -- relying
on an implicit default means one unrelated edit silently regresses rows
nobody was thinking about, which is exactly how v0.9.0 happened.

### 6.3 No safety net anywhere but History

Of every row container in the package, only `.uap-history-row-top` (this
fix) and `.uap-composer-row` had `justify-content: space-between`.
`.uap-toolcard-header`, `.uap-subcard-header`, `.uap-perm-summary`,
`.uap-banner`, `.uap-settings-qa-row` and `.uap-settings-model-row` all
relied on `flex-grow` alone. All six now carry the safety net. None of them
has free space to distribute today, so there is no visual change --
section 3.1 explains why that is the point rather than an argument against.

### 6.4 Two silent gaps in EXISTING tests (see also section 7)

`SourceScan_SettingsModelRow_KeepsShrinkGuards` and
`SourceScan_SettingsProfileRow_KeepsInlineLayoutGuards` assert
`flex-shrink`, `min-width` and ellipsis on those rows but never
`flex-grow: 1` -- deleting that one line failed nothing. Both classes, plus
the four other confirmed-correct content columns, are now on the
section 5 list so they are pinned rather than merely correct.

## 7. Adversarial review of this round

Five independent review lenses over the round's diff (permission-surface
trust, transform correctness, USS regression, guard strength, history
fallout), each finding then handed to a separate agent whose job was to
refute it. 16 raised, 15 refuted. Acting only on the verdicts would have
been wrong in three places, so each was re-checked by hand against the
source. What survived:

**7.1 Provenance on the approval surface (a refutation that was wrong).**
The round shortened `mcp__<server>__<tool>` to `<tool>` for display, and
applied it to `PermissionCard` as well as the informational cards. Two
reviewers flagged the loss of the server id on an approval surface; both
were refuted. The code says otherwise: `PermissionCard` sets
`_summaryTitle.tooltip = _summaryTitle.text`, i.e. the tooltip carries the
*shortened* title, so the raw wire name is not recoverable from the card at
all -- only by opening the "Always" menu.

That matters because Extension Profiles can introduce third-party MCP
servers, so more than one server is a designed scenario. Two servers can
each expose a tool id like `delete_all`, and both would render as
"Allow delete_all?". Resolved by splitting the transform in two:

- informational cards (tool activity, subagent) keep the plain shortening;
- the permission card uses `<server>: <tool>` -- plumbing removed,
  provenance kept -- and its tooltip is built from the RAW wire name so the
  exact string the CLI sent is always one hover away.

The two transforms carry comments explaining why they differ, so nobody
later "unifies" them and quietly re-loses the provenance.

**7.2 Paging bounded the wrong cost (also refuted, also real).** The 50-row
cap bounds VisualElement construction, but `BuildRows` touched
`AiTitle`/`FirstUserTextPreview`/`Cwd` on every entry, and each of those is
a lazy per-file scan -- so a render cost one file scan per session on disk
regardless of the cap, contradicting the v0.15.0 claim that History costs
the same at 20 sessions and 2000. Rows are now assembled without the
transcript-derived text; filtering, grouping, sorting and paging run on the
timestamp and sidecar metadata alone, and only the surviving page is
hydrated. The two cases that genuinely cannot defer -- a non-empty search
query, and project grouping (which keys off `cwd`) -- still scan
everything, which is the honest trade for an explicit find action.

**7.3 A raw wire name left in the subagent progress line** (refuted,
real): `SubagentCard` shortened its header but not
`subagent.lastToolName` in the progress line, so the same card showed a
clean name at the top and `mcp__unity-ops__uap_query_hierarchy` a few lines
down. Shortened too.

**7.4 Stale tooltip across requests** (the one finding confirmed by the
verifiers). `_summaryTitle` is built once and the card instance is reused
for every request; `BuildToolVariant` sets `.tooltip`, `BuildQuestionVariant`
sets only `.text`, and neither `Build()` nor `Hide()` cleared it -- so after
a tool prompt, an AskUserQuestion card showed the previous tool's tooltip on
hover. Cleared in `Build()` alongside the other per-request resets, matching
what the file already does for `_undoBadge`.

**7.5 Two cheap guards taken from refuted findings.** Delete is no longer
offered on the live session (the CLI still holds that transcript open; the
move happens to fail with a sharing violation today, but that is the file
system saving us rather than a decision this view made), and a queued
refresh now defers while a rename or new-group field is open -- otherwise a
turn completing in the background records the active scene, saves metadata,
raises `Changed`, and silently wipes what the user was typing. Both are
pure methods (`CanDeleteRow`, `ShouldDeferRefresh`) purely so they can be
pinned by tests, since neither a `GenericMenu` nor a scheduler tick is
reachable from an EditMode test.

The remaining 12 refutations were checked and accepted. One known-cosmetic
issue is left open on purpose: when every session is archived and the
filter is off, the empty state says "no sessions match this search" even
with an empty search box.

## 8. The same mistake again, in the filter bar this round added

Reported immediately after v0.15.1 shipped: picking "project" or "custom
group" in History stretched the view horizontally, producing a pointless
horizontal scrollbar and a blank strip.

It was not the group list. It was the filter bar that section 4's own
release introduced -- **the rule written in section 3 was violated in the
same release that wrote it**, with the same widget as v0.9.0. Worth
recording plainly rather than quietly fixing.

### 8.1 Measured

```
filterbar width 302.5
  search field   w= 60.0  right= 64.0   grow 1  shrink 1  min-width 60
  label "group"  w= 33.5  right=101.5   shrink 0
  PopupField     w=295.5  right=402.0   shrink 0  min-width 84   <-- 295.5 px
  Toggle         w= 83.0  right=492.0   shrink 0
ScrollView: scrollOffset.x = 189.5   (190 px of the list scrolled out of view)
hScroller: display=Flex, 302.5 x 13   (the blank strip)
```

A `PopupField` reports a preferred width driven by its **current value**,
and `flex-shrink: 0` means it can give none of it back. The localized
choices differ enough in length that the short one fits and the long ones
do not -- which is exactly why it looked like a bug in the project/custom
grouping rather than a bug in the bar.

### 8.2 Two things were wrong, and both are fixed

1. **The bar could not reflow.** Four controls do not fit on one line in a
   ~300 px docked panel, whatever their shrink settings. The container now
   declares `flex-wrap: wrap`, so it degrades by moving a control to a
   second line instead of overflowing.
2. **The popup could not shrink and had no ceiling.** Now
   `flex-shrink: 1` plus `max-width: 130px`. The cap matters independently
   of the shrink: it bounds the damage even for a future value longer than
   anything tested.

Plus a defensive third: the History `ScrollView` is created with
`ScrollViewMode.Vertical`, which does **not** stop a horizontal scrollbar
appearing -- `horizontalScrollerVisibility` stays `Auto`. It is now
explicitly `Hidden`. A session list is a vertical list; sideways scrolling
is never the right answer for it, and leaving the affordance on turns any
future overflow into "the whole view slides off-screen" instead of a
contained clip.

### 8.3 After

```
hVis=Hidden      scrollOffset=(0, 0)      hScroller display=None (0 x 0)
line 1: search field                      right=298.5
line 2: label -> PopupField (capped 123)  -> Toggle right=251.5
```

Everything inside the 302.5 px viewport; no overflow, no scrollbar, no
blank strip.

### 8.4 The rule, extended

Section 3 covered content columns and trailing controls. The missing half:

> A `BaseField`-derived control (`PopupField`, `Toggle`, `TextField`,
> `EnumField`, ...) in a row sizes itself from its own current content, so
> it must declare `flex-shrink: 1` **and** a `max-width`. A row of such
> controls in a panel that can dock narrow must additionally declare
> `flex-wrap: wrap` -- shrinking alone cannot make four controls fit in
> 300 px, it only decides which one gets crushed.

Three occurrences of the `PopupField` + `flex-shrink: 0` shape (v0.9.0,
and twice in v0.15.x) is enough evidence that the implicit default is a
trap in this codebase specifically, where every visible string is
localized and Japanese runs much longer than the English it was laid out
against.

### 8.5 Second audit: every BaseField in a row

The section 6 audit looked at content columns and trailing controls. This
one asked a different question -- where else does a `BaseField` sit in a
row with an implicit `flex-shrink: 0`? It confirmed that `TextField`,
`ToolbarSearchField`, `PopupField<T>`, `Toggle` and `SliderInt` are the
only `BaseField` types this package constructs, across six files, and
correlated every construction site with its USS.

**One serious finding, worse than the bug that triggered the audit.**
`.uap-card-path` -- the CLI-path `TextField` on the first-run card
(`FirstRunView.cs:63`) -- declares `flex-grow: 1` and nothing else, so its
`flex-shrink` is the implicit 0. It shares a row with the Browse button
(`.uap-card-btn`, `flex-shrink: 0`), and it holds a machine-supplied
absolute path: an npm global install on Windows routinely runs 60-90+
characters. Unlike the filter bar, this row's ancestor
`.uap-view-container` sets `overflow: hidden`, so there is no scrollbar to
recover with -- **the Browse button would simply be clipped away**, on the
one screen the user is looking at precisely because their CLI path needs
fixing. Now `flex-shrink: 1` with a `min-width: 80px` floor.

The irony worth recording: the comment added to `.uap-card-btn` earlier
this same release says this row's Browse button "has no other protection".
Only the button side was ever hardened. Nobody looked at the field.

**One defensive hardening.** `.uap-settings-qa-prompt` is the only other
row-hosted field without an explicit `flex-shrink`; its
`#unity-text-input` is `white-space: normal`, so wrapping probably already
saves it, but "probably safe via a side effect of another rule" is exactly
the footing all three shipped occurrences were standing on. Made explicit.

**Everything else is safe, for two structural reasons** worth knowing
before adding controls: fields added directly to a settings section body
are in a *column*, where Yoga's default `align-items: stretch` sizes them
to the container regardless of content; and `.uap-settings-qa-label` uses
an explicit `width: 110px`, which overrides the content-driven measure so
its `flex-shrink: 0` is correct rather than accidental.

**Scope of the standing rule, settled by the audit.** `flex-shrink: 1`
plus a `min-width` floor is the non-negotiable baseline for every
`BaseField` in a row -- that is what the working two- and three-control
rows in Settings already do, without wrapping. `flex-wrap: wrap` is an
*additional* layer for rows with more than two interactive children, which
is where shrinking alone stops being enough: four controls' minimum widths
simply exceed a ~300 px dock. Add `max-width` on top when the content is an
open-ended localized choice, as `.uap-history-groupby` now does.

A third `UssHygieneTests` list enforces `flex-shrink: 1` on all eight
row-hosted fields, and its doc comment records the deliberate exclusions
(the fixed-width label, the archived toggle, and every button class) so
nobody later "completes" the list with entries that belong on the other
side of the rule.
