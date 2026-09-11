# 2026-08-22 -- UXA-3/UXO-1: escalation friction + proactive login card

The two High UX findings on onboarding/safety, after the permission-surface
pair (same CI net).

## UXA-3: zero friction into "auto-approve everything"

`UapAutoApproveLevel.AllUnityOps` is, by its own enum doc, the level where
"Undo CANNOT reverse" the auto-approved effects and the end-of-turn warning
becomes "a notification after the fact, not a chance to stop it" ("never a
default"). Yet BOTH write surfaces -- the header GenericMenu and the
Settings PopupField -- applied it with one un-confirmed click, and applying
it immediately auto-answers a permission request already waiting on screen
(`AgentHub.ApplyAutoApproveLevelChanged`), which cannot be un-answered.

Fix, shared by both surfaces via `AutoApproveLevelLabels` (the existing
"single place a level becomes text" chokepoint):

- `RequiresConfirmation(current, target)` -- pure, unit-tested: true only
  for the escalation INTO AllUnityOps. Downward moves stay frictionless
  (the dialog stops accidental escalation, not retreat to safety).
- `ConfirmEscalationIfNeeded(...)` -- the `EditorUtility.DisplayDialog`
  gate (4 dedicated L10n keys EN+JA, the package's established dialog
  pattern), called BEFORE the settings assignment at both sites. The
  Settings dropdown reverts on cancel via `SetValueWithoutNotify` (a plain
  value set would re-enter the handler).
- The header menu now separates AllUnityOps from the three safe levels
  (GenericMenu labels cannot carry styling; the separator is the strongest
  in-menu cue, and the chip's existing `--open` class already tiers the
  header). Labels themselves are untouched -- AutoApproveLevelLabelsTests'
  wording-parity pins stay green.

## UXO-1: the not-logged-in first run bounced the first send

On a fresh machine with the CLI installed but never logged in, boot spawns
the CLI successfully (no `LastError`), the transcript is empty, and
`ResolveFirstRunMode` -- which only read LastError + a transcript scan --
returned Hidden: a fully usable-looking composer whose FIRST send bounced
off `authentication_failed`, and only that error block made the login card
appear. `AgentHub.CurrentAuthStatus`'s own doc claimed "the chat setup
card" renders it; the wiring never existed, and `RefreshAuthStatus()` was
only ever called from the Settings tab.

Fix:

- **Boot probe**: `AgentPanelWindow.StartHubDeferred` (the one deferred
  boot call) now also calls `RefreshAuthStatus()` -- self-guarded,
  off-thread, raises Changed on completion, which ChatView already
  coalesces into a refresh. Deliberately NOT inside `EnsureStarted` (hot
  paths: CompileGate drain ticks, every send with a dead client).
- **Decision**: `ResolveFirstRunMode` is now internal and fully
  parameterized (lastError, client, session, auth) and additionally
  returns NotLoggedIn on a POSITIVE `IsAvailable && !LoggedIn`.
  Precedence pinned by tests: CliNotFound first; a recent transcript auth
  error wins even over a stale LoggedIn cache (env-token revoked
  mid-session); null status (query not done, 10 s worst case) and
  IsAvailable==false stay Hidden -- a healthy boot must never flash the
  login card.
- **Check again**: `RecoverConnection` also re-queries auth, so a user who
  logged in EXTERNALLY (terminal `/login`) is not pinned on the card by
  the stale logged-out cache.

Tests: `ChatViewFirstRunModeTests` (the full matrix, no subprocess --
honoring the project rule that tests never call RefreshAuthStatus/
BeginLogin directly), `AutoApproveLevelLabelsTests`' new
RequiresConfirmation cases.
