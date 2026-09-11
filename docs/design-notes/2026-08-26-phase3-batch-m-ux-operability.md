# 2026-08-26 -- Phase 3 batch M: the panel explains itself

Five UX-operability items (UXO-4/5/6/7/8) plus one authorization-surface
label fix (UXA-6). The through-line: states the panel already had but
never narrated -- a hung connect, a submitted login code, a disabled
picker, a Send button that silently became Stop -- each now says what is
happening and what the user can do about it. Every decision is a pure
helper with a table test; the views only render the answer.

## UXO-8: the composer narrates its own primary action

ResolvePrimaryAction (deliberate, test-pinned) flips the primary button
Send<->Stop with the field's emptiness while a turn runs -- but the flip
itself was silent: nothing explained that Enter would now append to the
RUNNING turn. `DescribeActionHint(turnActive, fieldHasText)` drives the
hint line under the field ("Enter sends into the running turn; Esc stops
it" when text is present, the existing "Esc to stop" when not), and the
button carries a per-state tooltip. Four-way table in
ComposerPrimaryActionTests.

## UXO-7: a slow connect escalates instead of spinning forever

"Connecting..." looked identical at 2 seconds and at 2 minutes.
AgentHub now stamps `StartingSinceUtcTicks` on every state change
(non-zero only while Starting); StatusBarView re-renders once a second
(schedule.Execute -- an unchanged hub state never re-raises Changed, so
the 10s threshold needs wall-clock ticks) and past
`SlowConnectThresholdMs` (10s, the UX spec's escalation rule) swaps in
"Still connecting..." plus an inline Reconnect button. The decision is
`ShouldShowSlowConnect(state, elapsedMs, thresholdMs)` -- pure, and a
zero/unset start tick computes elapsed as -1, which fails toward the
calm text.

## UXO-5: the model picker works before the first connect

The header picker was a DEAD BUTTON until the first initialize response
(and stayed one when the CLI was down) -- clicks silently ignored, no
explanation. `ResolveModelPickerState(hasLiveOptions, hasCachedCatalog,
streaming)` now decides {Enabled, Live/Cache/Empty}: live options win
(disabled only mid-stream, where set_model would race the turn); with no
live options the persisted catalog cache (PanelSettings.modelCatalog,
refreshed on every live connection) backs the menu, the checkmark marks
the persisted panel default, and a selection goes through
AgentHub.SetDefaultModel -- the Settings dropdown's next-spawn-only
write, NEVER SwitchSessionModel (a silent no-op with no client); with
neither, the button stays enabled and the click shows one disabled item
("Models can be chosen after connecting") instead of swallowing the
click. The default-suffix is skipped in cache mode: with no running
model, "default" and "current" collapse into one fact and the checkmark
already states it.

## UXO-6: the login code flow acknowledges the code

Submitting a code changed NOTHING on screen (the CLI gives no per-code
acknowledgement), and a rejected code ended as a silently vanished
sub-card -- indistinguishable from success until the status line
disappointed. Two pieces:

- `ResolveAccountLoginFeedback(loginInFlight, waitingForCode,
  codeSubmitted)` -- pure; a submitted code means Verifying (field and
  Submit lock, instruction says "Verifying the code...") until the
  process exits.
- The failure verdict CANNOT be decided in flight: AgentHub clears
  CurrentLoginSession the moment the process exits and re-queries auth
  status, so RefreshAccountSection remembers the AuthStatus INSTANCE
  visible at exit time and judges on the first tick that shows a
  different one (reference identity, the same exact-key trick the model
  memoization uses). Not logged in -> a red failed-login notice under
  the card, cleared by the next Log in press or a successful login. A
  deliberate Cancel suppresses the verdict -- cancelling is not failing.

## UXO-4: the first-run login card leads with the one-click path

The not-logged-in card OPENED with a terminal walkthrough (run `claude`,
type `/login`) and buried its own in-panel login button below, next to a
same-weight Check-again. Inverted: the card leads with the primary
Log in button and a lead sentence saying exactly what pressing it does
(the Settings Account card opens with the browser link and code field;
the panel reconnects itself on success). The terminal path survives,
demoted into a collapsed "Another way" foldout together with its
Check-again button. Tests pin exactly one uap-card-btn--primary per
setup card and the foldout's collapsed default.

## UXA-6: the Always menu speaks human, persistence speaks wire

"Always allow mcp__unity-ops__uap_scene_create_object" -- protocol
plumbing on the one label whose whole job is telling the user what they
are granting. `FormatRuleForDisplay` reformats the menu label only: the
tool name goes through ToolCardDescriber.FormatMcpNameWithServer (server
kept, per that method's authorisation-surface contract), a
"(ruleContent)" scope rides along verbatim, and an MCP rule with NO
scope -- the synthesized every-argument grant -- now says "(all calls to
this tool)" instead of looking narrower than it is. The persisted rule
string is untouched wire grammar; a test pins that the pretty label
never leaks into TryExtractAddRuleToolName.

## Honest residuals

- UXO-6 cannot detect the CLI re-prompting for a SECOND code after a
  wrong one (AuthLoginSession raises WaitingForCode once); the locked
  field stays locked until exit, so a re-prompting CLI requires
  Cancel + Log in again. Measured behavior so far is exit-on-bad-code,
  which this handles.
- UXO-5's cached catalog can be stale (models added/removed server-side
  since the last connection); the first live initialize replaces it, and
  a stale pick still round-trips through the CLI's own validation at
  spawn.
- UXO-7's 1s scheduler tick runs while the status bar is attached
  regardless of state -- one Refresh per second was measured as noise-
  level (the same Refresh already runs per Changed event, far hotter
  during streaming).
