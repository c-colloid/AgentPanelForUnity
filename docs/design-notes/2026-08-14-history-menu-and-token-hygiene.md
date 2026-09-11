# 2026-08-14 -- History row menu goes UIToolkit + token hygiene (deferred bundle E)

User approved both items deferred from the 0.23.0 audit round, in this
order: (2) replace the history row's GenericMenu with a panel-styled
popup, then (1) the five token-hygiene items.

## Part 2 -- History row menu

### Decision: ShowAsDropDown host + UIToolkit content

The audit's complaint was visual (native IMGUI menu chrome inside a
fully custom-styled panel), not behavioral. The panel already owns this
exact pattern: StatusBarView.UsagePopover is an EditorWindow shown via
`ShowAsDropDown(GUIUtility.GUIToScreenRect(anchor.worldBound), size)`
with ApplyThemeAndStyles + uap-* classes inside. ShowAsDropDown keeps
everything GenericMenu gave us for free that is HARD to rebuild
(click-outside auto-close, screen-edge clamping, borderless chrome),
while the content becomes ours.

Rejected alternatives:
- In-panel absolute-positioned overlay: needs its own outside-click
  capture, z-order management over the scroll view, and clipping at the
  panel edge -- all solved by ShowAsDropDown already.
- Keeping GenericMenu + inline Pin/Archive icon buttons: adds two more
  controls to every row (density regression) and still leaves the
  native menu for the rest.

### Structure

`HistoryRowMenuPopover : EditorWindow` (nested in HistoryView, same as
UsagePopover in StatusBarView), two swappable pages:

- Main: Pin/Unpin (checked = pinned), Rename, Archive/Unarchive
  (checked = archived), separator, "Group >" (navigates to page 2),
  separator, Delete.
- Group page: back row ("< Group"), "(none)" + every SessionGroup +
  "New group..." with a check on the current assignment.

Page swap rebuilds the content in place and resizes via
minSize/maxSize + position (dropdown windows honor it); no reopen
flicker. Item rows are Buttons with a FIXED-WIDTH leading check slot so
labels align in one column whether checked or not (2026-08-03 standing
rule); check glyph comes from IconLoader.

Delete's two disabled cases (foreign transcript / live session) now
carry per-reason tooltips -- an improvement GenericMenu could not
express (its disabled items cannot explain themselves). New L10n:
HistoryDeleteDisabledForeignTooltip, HistoryDeleteDisabledLiveTooltip,
HistoryMenuGroupBack. The '/'-flattening of group names dies with the
submenu (no path semantics in our own list); names render verbatim
through the same sanitize chokepoint as the rows.

### Pure model for tests

`HistoryRowMenuModel` (Editor/Model): builds the item lists as data --
`BuildMainItems(pinned, archived, canDelete)` and
`BuildGroupItems(currentGroupId, groups)` returning
`Item { Id, Label, Checked, Enabled, IsSeparator, TooltipKey }`-shaped
entries (exact shape up to the implementer, but pure and
list-order-deterministic). Tests pin: pin/unpin + archive/unarchive
label-and-check by state; delete disabled with the right tooltip per
reason; group check follows CustomGroupId ("" checks none); New group
is always last; separators in the documented positions. HistoryView
maps Item.Id -> the SAME handlers ShowRowMenu uses today (TogglePinned,
rename mode, ToggleArchived, AssignGroup, new-group mode, delete
confirm); the popover only reports the chosen id and closes.

### USS (new classes)

.uap-rowmenu (container: bg-elevated, border-subtle, radius),
.uap-rowmenu-item (+ :hover, :disabled), .uap-rowmenu-item--back,
.uap-rowmenu-check (fixed-width slot), .uap-rowmenu-label,
.uap-rowmenu-sep. All colors/radii via existing tokens; both themes
must resolve every var (UssHygieneTests sweeps automatically).

## Part 1 -- Token hygiene (audit bundle E)

1. `--uap-switch-track-on` becomes `var(--uap-accent-link)` in BOTH
   themes (the comment already claims the equivalence; enforce it).
2. `.uap-ctx-chip-glyph--warn` renamed to `.uap-ctx-chip-glyph--error`
   (the only warn-named rule resolving to error red); the matching
   AddToClassList in ContextBarView renames with it.
3. New `--uap-radius-pill: 10px` token (both themes); .uap-jump-pill,
   .uap-chip, .uap-attach-chip, .uap-ctx-chip and .uap-pill all point
   at it. .uap-pill visibly changes 8 -> 10px; accepted (that drift was
   the finding).
4. `.uap-card` adopts `--uap-radius-card` so the class named card and
   the token named card mean the same shape (3 -> 6px on the first-run
   card, matching the settings cards).
5. Button padding families: DOCUMENT rather than redesign -- each
   small-button class keeps its measured padding but gains a comment
   naming its family (compact chip 1/6, bar button 1/8, standard 3/10,
   send 2/14), and quickactions-btn (2/10) converges to standard 3/10
   (the one true stray). The audit's full consolidation onto longhand
   token references is NOT worth the churn while the values themselves
   are stable -- revisit only if a density retune ever happens.
   card-btn/settings-btn stay separate classes; their fills render
   differently against the surfaces under them (card-btn: darker inset
   on elevated bodies; settings-btn: border-only on its same-toned
   body), so unifying the fill is a visible design change, not hygiene
   -- the USS comment above .uap-settings-btn now records exactly this.

## Regression guards

- HistoryRowMenuModelTests (pure, exhaustive over the state matrix).
- Existing UssHygieneTests var-resolution sweep covers the new token
  and classes in both themes automatically.
- GlyphAudit/L10n tests cover the new strings and check glyph.
