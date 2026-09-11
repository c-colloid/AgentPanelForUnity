# 2026-08-26 -- Phase 3 batch N: attention without interruption

The last four Phase 3 items (UXA-4/7, UXIA-2/5). Two are about WHERE the
panel asks for the user's attention when a tool needs permission (the
always-visible composer; a floating window instead of a view yank), two
about what the eye finds when scanning (fifteen equal-weight settings
cards; history rows that never said which model ran). Same discipline as
batches L/M: every decision is a pure helper with a table test.

## UXA-4: deny-with-alternative moves to the composer

The inline permission card's deny-reason field lived inside the card's
COLLAPSED details -- invisible unless the user expanded the card, which
the default tool variant does not. The ux-spec (section 3.7) had always
drawn this affordance in the always-visible composer, and ChatView's own
wiring comment promised "deny + type an alternative" there. Now it does:

- `ResolveDenyMessage(inCardText, externalText)` -- pure: the in-card
  field wins when non-blank (the Window host's only entry), else the
  composer text, else the protocol's default refusal line.
- The inline card builds NO in-card field any more; it consumes the
  composer text through `ConsumeExternalDenyMessage` (ChatView wires it
  to `ComposerView.ConsumeTextForDeny`). Consume-on-read: a non-blank
  read clears the field, so the same words cannot also be sent as a chat
  message by a later Enter. The read happens only when the in-card field
  cannot answer, so a Window-host deny never eats a chat draft.
- While a request is pending, the composer placeholder announces the
  role ("Type what to do instead, then press Deny..."), driven by
  ChatView's refresh so it tracks the request's lifetime.
- The floating PermissionWindow keeps the in-card field: it has no
  composer.

## UXA-7: a permission request stops yanking the active view

RefreshIfDirty force-switched to Chat whenever a can_use_tool arrived
with Settings/History active -- mid-keystroke, mid-rename. The REASON
was real: ChatView's refresh loop (and its inline/window hosting
decision) pauses while Chat is inactive, so the request would sit
invisible behind the title badge. The remedy was disproportionate. The
floating PermissionWindow is view-independent and self-driving (its own
AgentHub.Changed hook + 80ms refresh loop), so
`PermissionArrivalPolicy.Decide(activeView, windowAlreadyOpen)` now
returns OpenFloatingWindow for the non-Chat case -- opened with
autoOpened:true, which keeps keyboard Y/N unarmed per PermissionWindow's
existing guard -- and BadgeOnly when Chat is active (ChatView's hybrid
hosting decides) or a window is already up. Forcing a view switch is no
longer a possible outcome; the policy enum does not even offer it.

## UXIA-2: settings sections gain progressive disclosure

Fifteen always-expanded cards gave "which model runs" and "read-only
stderr tail" identical visual weight. `AddCollapsibleSection` -- the
Foldout-headed generalization of the danger-zone/agent-overrides pattern
that AddFoldoutHeaderIcon already serves -- now renders five
advanced/plumbing sections collapsed by default: UapOps modules,
Extension Profiles, uLoop, CLI, Diagnostics. Everyday sections
(Conversation, Model, Custom Instructions, Display, Quick Actions,
Notifications, Console Errors, Appearance, Account, About) stay plain
expanded cards, and Conversation/Model keep the lead slots the 08-14
polish pass gave them (SettingsViewSectionIconTests' index assumptions
hold). Open/closed state persists per editor session via SessionState
under `SectionDisclosureKey(sectionId)` -- the HistoryView
view-preference precedent, and the right lifetime for a disclosure
preference. Body toggles bubble ChangeEvent&lt;bool&gt; through the
foldout's handler, so the persistence write is target-guarded.

## UXIA-5: history rows say which model ran

The row meta line was relative-time + file size -- and size is a weak
proxy for "which conversation was this". The SessionIndex shared scan
(one bounded pass already extracting preview/ai-title/cwd) gained a
fourth latch: the first assistant line's `message.model`, skipping
TranscriptUsage.SyntheticModelName exactly as TranscriptUsage itself
does. A mid-session model switch keeps the FIRST real model -- "the
model it started on" is the honest one-line label. The meta line becomes
time + size + shortened model via `FormatRowMeta` (reusing
SettingsView.ShortenResolvedModel, the Settings dropdown's own rule),
with no dangling bullet when a transcript carries no assistant line.
Cost is deliberately absent: measured 2026-08-03,
`result.total_cost_usd` never reaches the transcript on disk, so it
cannot be restored -- shipping a guessed number would be worse than
none.

## Honest residuals

- UXA-4: with a permission pending and text typed, Enter still SENDS a
  chat message (unchanged, deliberate -- mid-turn steering is a real
  workflow); only the Deny button consumes the text as a refusal
  reason. The placeholder says which button completes the deny flow.
- UXA-7: on a machine where the floating window cannot open (exotic
  layouts), the title badge remains the only signal -- the same
  fallback the old code had AFTER its force-switch, minus the yank.
- UXIA-2: the five advanced foldouts indent their body by Unity's
  default foldout content margin; accepted rather than fighting the
  control's internal layout.
- UXIA-5: rows from `withText:false` builds hydrate ModelName only for
  VISIBLE rows (HydrateVisibleRows), same staging as preview/title --
  an off-screen row briefly has no model label, by design.
