# 2026-08-13 -- Console error chip: content-based, persistent ignore

## Problem (user report, 2026-08-13)

"Once a console error appears, the fix button stays on the panel forever.
Some errors (SDK/extension bugs) do not need fixing; I want to NOT deal
with the ones that do not need dealing with."

## Measured root cause (code inspection, ContextBarView.cs)

The chip's dismiss remembers only the error COUNT at dismiss time
(`_errorDismissedAtCount`). Every count change "un-dismisses" it:

1. Any new distinct error re-shows the chip (intended), but so does a
   count DECREASE -- `OnCompilationStarted` removes compiler entries, so
   every compile resurrects the chip showing stale runtime errors.
2. `ConsoleErrorProvider` accumulates for the whole domain lifetime;
   clearing the Console window does not clear it, and nothing expires.
3. After a domain reload the provider restarts empty, but SDKs/extensions
   that re-log the same error on load re-add it as a brand-new entry.
4. `FormatDigest` (the fix button's payload) includes ALL entries --
   noise the user explicitly does not want fixed still gets sent.

There is no notion of "this error does not need fixing" anywhere; count
tracking is a proxy that cannot express it.

## Decision (user-selected via AskUserQuestion, 2026-08-13)

- Persistence: PROJECT-PERSISTENT ignore, message-keyed, with a
  management list (un-ignore) in Settings. Editor restarts keep it.
- Granularity: chip-level batch ignore (the X ignores everything
  currently visible) plus free-text substring patterns in Settings for
  SDK-specific noise. No per-error popover (rejected: new UI surface
  for marginal gain; patterns cover the "this SDK always spams X" case
  better because they survive message variation).

## Design

Two independent mechanisms replace `_errorDismissedAtCount`:

1. **Persistent ignore (the X button)**: X adds every currently VISIBLE
   entry's exact Message to `PanelSettings.ignoredConsoleErrors`
   (bounded FIFO, `IgnoredConsoleErrorsMax = 200`, oldest evicted).
   Settings gains a management list with one aligned remove button per
   row (standing rule: docs/design-notes/2026-08-03-list-row-control-
   alignment.md) and a clear-all button.
2. **Ignore patterns (Settings free text)**: `ignoredConsoleErrorPatterns`
   -- one substring per line, trimmed, case-sensitive Ordinal contains;
   blank lines dropped. Matches are ignored even if never X-ed.
3. **Transient acknowledged set (the fix button)**: sending the fix
   prompt records the visible message set in memory only. The chip stays
   hidden while visible is a subset of acknowledged; a genuinely new
   message shows it again. Deliberately NOT persistent: if the fix
   fails and the same error returns after a reload, the chip must
   return -- auto-persisting on send would silently eat real errors.

Provider-side filtering (`ConsoleErrorProvider`):

- `VisibleSnapshot()` / `VisibleCount` filter `_entries` through the
  ignore store; `FormatDigest()` formats VISIBLE entries only, so the
  fix prompt never contains ignored noise.
- The raw list stays intact so un-ignoring in Settings immediately
  restores the entry (no data loss).
- Ignore checks are pure static functions over (message, exactSet,
  patterns) -- EditMode-testable without the editor log pipeline.

## Rejected alternatives

- Session-only ignore (SessionState): SDK noise recurs every editor
  session; the user picked permanence explicitly.
- Regex patterns: overkill + user-hostile failure mode (invalid regex
  throws); Ordinal substring covers the observed SDK-noise case.
- Auto-expiry / console-clear sync: Unity 2022.3 has no public console
  read/clear hook; expiry by time would hide real unfixed errors.

## Regression guards (as implemented)

- ConsoleErrorIgnoreFilterTests: exact ignore, pattern ignore, pattern
  parsing, FIFO bound (incl. non-positive cap), null-safety.
- ConsoleErrorVisibilityTests (settings injected via
  ConsoleErrorProvider.SettingsSourceForTests): visible filtering both
  ways, digest excludes ignored / all-ignored digests null, un-ignore
  restores without re-log, IgnoreCurrentlyVisible (the REAL X-press
  production path) persists + hides + raises Changed.
- ContextBarViewLogicTests: compile-start entry drop can NOT resurrect
  the chip (pins the old count bug); new distinct error re-shows;
  AcknowledgeSentMessages caps at the digest limit and never persists.
- AgentHubAutoContinueAfterCompileTests now injects an isolated
  settings source -- review finding 2026-08-14: without it, a user
  exercising the ignore feature in the sandbox project would silently
  flip the failed-continuation cases green via the REAL State.asset.
- UssHygieneTests: .uap-settings-errignore-msg appended to
  FlexGrowContentColumns and .uap-settings-errignore-row to
  JustifyContentSpaceBetweenContainers (the 2026-08-03 standing rule's
  curated allowlists -- review finding 2026-08-14 caught this note
  claiming the guard before the entries existed).

## Superseded in part (2026-08-23, UXO-3)

Decision 1's binding of the PERMANENT ignore to the chip's bare X is
superseded: the X now only dismisses-for-now (transient, content-keyed,
like the fix button's acknowledged set), and the permanent ignore moved
to an explicit menu action on the chip with an in-place undo notice
(ConsoleErrorProvider.Unignore). The persistence mechanism, storage
(PanelSettings.ignoredConsoleErrors) and Settings management list are
unchanged. See 2026-08-23-phase2-batch-b-approval-surface.md.
