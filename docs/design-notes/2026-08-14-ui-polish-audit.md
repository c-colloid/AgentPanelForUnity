# 2026-08-14 -- Panel-wide UI polish round (audit-driven)

## Method

User request: full-panel design brush-up, no specific complaints. Five
parallel critics (chat / settings / history / cards / cross-surface
consistency) reviewed window-content-only screenshots (GrabPixels; the
ReadScreenPixel screen-region approach was abandoned after it captured
unrelated screen content on 2026-08-13) cross-checked against
AgentPanel.uss + ThemeDark.uss ground truth. 28 findings; the
load-bearing factual claims were re-verified by the PM before adoption
(contrast ratio #909090-on-#404040 recomputed at ~3.26:1; zero
`.uap-perm-option--other` rules; exactly one `:focus` and zero
`:disabled` rules in the whole stylesheet; no title tooltip in
HistoryView.BuildRow).

User selected bundles A (color foundation), B+C (chat + cards), D
(history/settings). Bundle E (token hygiene: switch-track-on var,
ctx-chip-glyph--warn rename, pill radius token, button padding families)
is DEFERRED, not rejected -- revisit with the next theme change.

## Contract (shared vocabulary for the implementation streams)

New/changed tokens (ThemeDark.uss AND ThemeLight.uss):

- `--uap-text-secondary`: dark #909090 -> #ACACAC (implementation note:
  this note first suggested #A8A8A8, but the styles stream recomputed it
  at ~4.36:1 on --uap-bg-elevated #404040 -- SHORT of the 4.5:1 this
  note claimed; #ACACAC measures ~4.57:1 / ~5.17:1 on
  elevated/base). Light theme #555555 -> #535353 (was ~4.46:1 on its
  base, now ~4.60:1 / ~4.74:1).
- NEW `--uap-accent-caution`: standing-policy caution (dark: #C98A4B-ish
  rust amber -- distinct from --uap-status-warn yellow and
  --uap-status-error red; light: darker equivalent). Used ONLY by the
  auto-approve header chip's relaxed/open tiers. --uap-status-error
  returns to meaning "something is wrong RIGHT NOW".

New USS classes (AgentPanel.uss; names are final):

- `.uap-card-btn:disabled`, `.uap-card-btn--primary:disabled` -- themed
  disabled state (bg-elevated / border-subtle / text-disabled).
- `:focus` rules for `.uap-perm-option`, `.uap-perm-qtab`,
  `.uap-card-btn` -- accent-user border, mirroring the composer's
  existing focus treatment (the one pre-existing :focus rule).
- `.uap-perm-option--other` -- bg-sunken + secondary italic label (code
  already emits the class; it had NO rule).
- `.uap-card-btn--quiet` -- transparent/borderless secondary for the
  question card's Skip.
- `.uap-note--warn` -- warn-colored system note variant.
- `.uap-status-dot--reconnecting` -- warn-colored status dot while the
  client is gone but a reconnect is expected.
- `.uap-thinking > Toggle` header treatment -- same bordered-row
  language as tool cards ("bordered row = expandable").
- `.uap-settings-field--group-start` -- margin-top: --uap-space-m for a
  field that starts a new topic after another field's hint.
- `.uap-settings-subgroup-start` -- top border + spacing for the
  allow/deny list block inside the Conversation card.
- `.uap-perm-field-caption` -- small caption label above the deny /
  Other free-text fields.

C# behavior changes:

1. HeaderView auto-approve chip: relaxed/open modifiers switch from
   --uap-status-error to --uap-accent-caution (USS-only if the classes
   are already split by tier; verify).
2. System notes: `ChatMessageBlock.MakeSystemNote(text, warning)`
   overload (default false keeps every existing call compiling);
   AgentHub's process-exited/reconnecting notes pass warning:true;
   MessageBlockFactory adds uap-note--warn for warning notes.
3. StatusBarView: the client-null/no-error branch gets
   uap-status-dot--reconnecting instead of falling through to neutral.
4. ToolCardDescriber: AskUserQuestion branch (first question text as
   summary); ToolActivityCard skips the summary label when it equals
   the display name case-insensitively (belt and suspenders).
5. PermissionCard: Skip gets uap-card-btn--quiet (question variant
   only); window-host title uses PermTitleFmt (name only -- the
   description already renders as .uap-perm-desc in that host);
   caption labels above deny field and Other field (new L10n strings);
   .uap-perm-summary-title font-size -> --uap-font-size-title.
6. PermissionWindow.ComputeInitialSize: content-driven height for the
   question variant: base ~170pt chrome + ~175pt per visible question
   section (one at a time shows, so: one section) + tab strip when 2+
   questions; clamp [320, 520]. Pure function extended, existing tests
   updated + new cases (1q short, 4q tall clamps to 520).
7. HistoryView: titleLabel.tooltip = resolved title; sub.tooltip =
   preview; group header tooltip; search field max-width 220px (USS);
   group headers to --uap-font-size-meta; refresh button restyled to
   the header icon-button family.
8. SettingsView: section order Conversation, Model first; CLI moves
   down next to Diagnostics/Uloop (call-order change only);
   .uap-settings-card-title -> font-size-body + text-primary;
   group-start / subgroup-start classes applied at the topic breaks
   named by the audit.

Row-menu-to-UIToolkit-popup (history) is DEFERRED with bundle E: it is
the largest single item, needs its own interaction design, and the
GenericMenu keeps working meanwhile.

## Regression guards

- ToolCardDescriberTests: AskUserQuestion branch; equal-summary skip.
- PermissionWindowTests: ComputeInitialSize per question count/clamps.
- MessageBlock/system-note test: warning flag maps to uap-note--warn.
- HistoryView: tooltip presence via the row-build seam if one exists,
  else a source-scan test is NOT worth it -- cover by building a row
  detached and asserting .tooltip (detached tooltip resolution is fine
  for the property itself, per the 2026-08-05 tooltip facts).
- UssHygieneTests: no new allowlist entries needed (no new
  flex-grow/space-between rows), but every new class must resolve its
  vars in both themes (existing sweep covers automatically).
- L10nTests: new caption strings in both catalogs.

## Rejected in this round

- Renaming/merging button classes and radius/padding token work
  (bundle E) -- deferred.
- Replacing GenericMenu -- deferred (above).
- Auto-sizing the question window by measuring live layout -- the
  estimate table (175px/question) is already measured and good enough;
  a layout-callback resize loop is the kind of feedback machinery the
  2026-08-05 stepper note explicitly avoided.
