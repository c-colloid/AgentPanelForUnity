# 2026-08-23 -- Phase 2 batch B: the approval surface says what it is asking

Three High findings from the UX review, all about the same failure: the
places where the user grants or destroys something did not show what
that something WAS.

## UXA-1: the collapsed permission card shows the operation target

`Allow Write?` told the approver which TOOL wanted to run and nothing
about what it would touch. ToolCardDescriber gained a
`Describe(string, JsonNode)` overload (shared core, no
re-serialize/re-parse of the request's already-parsed Input) and
PermissionCard's summary row gained a `_summaryTarget` label
(`.uap-perm-summary-target`, mono, ellipsized, capped at 45% width)
filled from the SAME describer table the activity rows use -- so the
approval surface and the history agree on how a target is written.
Suppressed when the describer can only echo the tool name back (empty
input) and for the AskUserQuestion variant.

## UXA-2: Edit/MultiEdit approvals render a +/- diff

The details pane dumped `old_string:` and `new_string:` as two raw
blocks; the ux-spec calls a diff preview for Edit tools mandatory. New
pure class `PermissionEditPreview` (Unity-API-free, like
PermissionCardLayout) classifies lines: common head/tail = Context
(elided to 2 lines nearest the change with a `...` gap marker), the
middle = Remove then Add; MultiEdit concatenates per-edit diffs with a
separator line. Deliberately a prefix/suffix split, NOT an LCS: an
Edit's old/new pair is already a localized snippet, and a
clever-but-wrong alignment on the APPROVAL surface would misrepresent
what gets executed. CRLF is normalized before splitting so an invisible
trailing CR never fabricates a remove+add pair.

PermissionCard renders the typed lines with per-kind classes
(`uap-perm-diff-del/add/ctx/gap/sep`, backgrounds reuse the existing
`--uap-diff-*-bg` theme tokens), truncated at the existing
PreviewMaxLines with a "Show all N lines" button (new L10n pair
PermDiffShowAllFmt) that re-renders unbounded in place. An Edit input
WITHOUT the old/new shape falls back to the generic key: value preview
-- never a wrong diff.

## UXO-3: the error chip's X no longer permanently silences errors

The X called ConsoleErrorProvider.IgnoreCurrentlyVisible() -- the
PanelSettings-persisted forever-ignore -- while looking identical to
the harmless attachment-chip X, with no confirmation and no undo. The
two intents are now separate controls:

- **Bare X = dismiss for now.** New transient
  `_dismissedForNowErrorMessages` set (same content-keyed semantics as
  the fix button's acknowledged set, and NO digest cap -- nothing is
  being sent, the user asked to close the chip). Never persisted; a new
  error message or a domain reload shows the chip again. The pure
  `CountUnacknowledgedVisible` gained a third parameter (either set
  hides); the 2-arg overload stays for the existing pins.
- **Menu = ignore permanently.** A chevron menu on the chip holds the
  explicit "Ignore these errors from now on" action. It still calls
  IgnoreCurrentlyVisible() -- which now RETURNS the persisted messages
  -- and shows a 10-second undo notice ("Ignored N console errors."
  [Undo]). Undo calls the new `ConsoleErrorProvider.Unignore(messages)`
  (exact-match removal from PanelSettings.ignoredConsoleErrors + SaveNow
  + Changed), which is a subset of the Settings management list's
  existing un-ignore behavior, not a new mechanism.

The 2026-08-13 design note carries a supersession section; its storage,
filter and management-list decisions are unchanged.

## Tests

- ToolCardDescriberTests: node-overload equivalence table + null-node
  behavior.
- PermissionCardTests: target label per tool shape (file name, command,
  hidden on empty input / question variant); Edit renders diff classes
  and NOT the raw preview; missing edit shape falls back; 40-line diff
  truncates behind the expander and the internal re-render shows all.
- PermissionEditPreviewTests: classification table (context/remove/add,
  head/tail elision with gap markers, CRLF normalization, MultiEdit
  separator, empty-shape fallbacks, pure removal).
- ConsoleErrorVisibilityTests: IgnoreCurrentlyVisible returns the
  persisted set; Unignore restores visibility from the intact raw list
  and stays quiet for unknown messages.
- ContextBarViewLogicTests: DismissVisibleForNow hides exactly the
  visible set, has no digest cap, never persists; either transient set
  hides a message; new distinct messages re-show the chip.
