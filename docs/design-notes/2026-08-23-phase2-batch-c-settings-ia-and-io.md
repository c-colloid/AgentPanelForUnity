# 2026-08-23 -- Phase 2 batch C: permission controls in one place, settings IO off the delta path

## UXIA-3/4: the auto-approve level moved next to the permission mode

Three same-axis controls -- when to ask (permission mode), what
auto-approves (auto-approve level), skip everything (danger-zone
bypass) -- lived in two sections, with the level buried at the very
bottom of UapOps behind module toggles. The level's PopupField
construction is extracted into `BuildAutoApproveLevelField` (internal;
exactly ONE construction site, pinned by source scan) and called from
BuildConversationSection directly after the permission mode, with
BuildDangerZone moved up to follow it -- so the permission cluster
reads as one block, then the composer preference and the tool lists.
Nothing about the field changed: same AutoApproveLevelLabels
Ordered/Describe as the header chip, purely live (still absent from
SettingsChangeDetector.RequiresReconnect), same warning help, same
OnAutoApproveLevelChanged escalation confirm (UXA-3).

UapOps -- its old home, and the section whose tools the level governs
-- keeps a one-line cross-reference hint (new EN+JA pair
SettingsAutoApproveMovedHint/Tooltip) so a user who looks there is
pointed to the field instead of concluding the setting is gone.

## UICODE-4: hub-driven settings refreshes coalesce; disk probes gated

SettingsView.OnHubChanged ran synchronously on every AgentHub.Changed
raise -- per streaming delta -- and its refresh chain contained two
disk touches: RefreshCliStatus (ICliPathProbe.Resolve statting CLI
candidate paths) and RefreshUloopSection (UloopDetector.DetectInProject
reading manifest.json). With the Settings tab visible during a turn,
that was disk IO per delta.

Three layers, house patterns only:

1. **Coalesce**: OnHubChanged now only sets `_hubDirty`; a scheduled
   loop (`RefreshIfHubDirty`, 250 ms -- the ChatView/HistoryView
   pattern) does the work at most 4x/second, paused in OnDeactivate.
2. **CLI stat gate**: on the coalesced tick, RefreshCliStatus runs only
   when the manual path setting changed since the last resolve (pure
   `ShouldResolveCliStatus(lastResolvedFor, current)`, null sentinel =
   never resolved). The explicit actions (path field change, Re-detect,
   reconnect, RefreshAll) still call RefreshCliStatus unconditionally.
3. **Manifest scan floor**: on the coalesced tick, RefreshUloopSection
   runs at most at 2 Hz (`_uloopDetectLastEvalAt` twin of the existing
   install-progress throttle). Explicit paths keep calling it directly.

In-memory label refreshes (model, account, diagnostics...) ride the
coalesced tick unchanged.

## Tests

SettingsView cannot be constructed in EditMode tests (RefreshAll's
account refresh reaches the auth probe, which the standing test rule
forbids), so the structural facts are pinned by source scans in
SettingsViewLogicTests: OnHubChanged only marks dirty (no disk-touching
call in its body), the coalesced tick uses the gated refreshes, the
level field is built in Conversation and absent from UapOps with the
cross-reference present, and exactly one PopupField construction site
exists. ShouldResolveCliStatus is pinned directly as a pure table.
L10nTests' parity covers the new strings.
