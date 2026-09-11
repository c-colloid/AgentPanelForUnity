# 2026-08-22 -- UXO-2: fail-safe IME composition-confirm Enter guard

The last Phase 1 High finding, and the one the project had explicitly
deferred (phase1-verification.md: "unverifiable in batchmode, a wrong
guard risks breaking the default path"). The UX spec calls this "the most
important detail for Japanese users": an Enter that CONFIRMS an IME
composition must not send.

## Why it was deferred, and why this shape is safe to ship

Unity 2022.3's UI Toolkit exposes no `isComposing` anywhere -- not on
KeyDownEvent, not on TextField, not on the public text interfaces (the
spec's "isComposing 判定必須" borrows web-DOM vocabulary with no UITK
equivalent). The only editor-side signal is
`UnityEngine.Input.compositionString`, and its timing relative to the
confirm event is platform-dependent and unmeasured; the integration
research even claims the confirm produces no event at all in UITK 2022.3.
No repo capture of a real composition-confirm Enter exists.

So the guard is **fail-safe by construction**: it can only ever SUPPRESS
an Enter while composition text is actually present. When the signal is
empty, already cleared, or throwing (batchmode/CI), the probe returns
false and every decision is byte-identical to before -- pinned by keeping
`imeComposing` a defaulted parameter so the entire pre-existing
ResolveEnterAction test matrix compiles and passes unchanged. The
`ctrlEnterToSend` escape hatch stays the documented fallback (README) for
machines where the signal never fires.

## The mechanics

- `EnterAction.Ignore`: composing means the user is confirming, not
  sending and not asking for a line break (inserting would corrupt the
  uncommitted text). The event pair is swallowed like every other Enter
  (the editing engine must not double-handle it); the IME's commit reaches
  the field through the text engine, not through this event.
- `ResolveEnterAction(..., bool imeComposing = false)`: composing
  short-circuits to Ignore on BOTH event shapes -- the keycode event AND
  the character-only '\n' event ("some IME paths" deliver only the
  latter; its modifier fallback used to resolve straight to Send).
- The keycode-decides/char-obeys pending protocol keeps the pair
  consistent across the composition boundary in both directions: a
  keycode Ignore is obeyed by its char event even though the commit
  cleared the signal in between, and a keycode Send decided while NOT
  composing is not re-classified by a composition starting in the gap.
- `IsImeCompositionActive()`: try/catch probe of
  `Input.compositionString`; no fake-probe seam on purpose -- tests pin
  the pure decision through the parameter.

## Honest residuals (risk 11 stands)

- Whether `compositionString` is still non-empty when the confirm
  KeyDownEvent reaches TrickleDown is exactly the ordering fact only a
  real Japanese IME on a real editor can measure. If it is already
  cleared, the guard is inert there and the pre-existing behavior (and
  escape hatch) applies -- the guard never makes anything worse.
- The char-event-only path is protected only while the signal is live at
  that instant; same measurement caveat.
- A manual real-IME pass (Windows + macOS editors) remains on the
  verification list; the risk-11 row and README workaround stay until it
  happens.
