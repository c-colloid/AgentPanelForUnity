# Composer newline swallowed + extension-profile row layout

Date: 2026-08-02
Status: fixing (v0.13.2)
Reported: user, live panel ("入力欄で改行ができない" / "拡張プロファイルのレイアウトが崩れている")

## 1. Composer: Shift+Enter inserts nothing

Measured live: `ctrlEnterToSend=False`, `TextField.multiline=True`, so
Shift+Enter is the newline gesture and plain Enter sends.

`ComposerView.OnKeyDown` handles TWO events per Enter press -- Unity
delivers a keycode event (`keyCode == Return`, no character) and a paired
character event (`character == '\n'`, `keyCode == None`). The handler
recomputes the send/newline decision independently for each:

```csharp
bool send = settings.ctrlEnterToSend ? modifier : !evt.shiftKey;
if (!send) { return; }          // let the default handler insert \n
evt.StopPropagation(); evt.PreventDefault();
if (isEnterKey) { TrySend(); }  // char event: swallow only
```

The swallow branch exists so a sent message does not also leave a stray
newline in the field. But it depends on the paired CHARACTER event
reporting the same modifiers as the keycode event. When the character
event arrives without the Shift flag, the second evaluation flips to
`send == true`, the handler swallows it (`PreventDefault`), and because
`isEnterKey` is false nothing is sent either: **the newline silently
disappears**, which is exactly the reported symptom.

### Decision

Stop deriving the decision twice. The keycode event decides; the paired
character event obeys:

- keycode Enter: compute send/newline from settings + modifiers, REMEMBER
  it, then act (send -> swallow + TrySend, newline -> let through).
- character '\n'/'\r': if a decision is pending from the keycode event,
  apply it and clear it (send -> swallow silently; newline -> let
  through). Only when no decision is pending (a character event with no
  preceding keycode event, e.g. some IME paths) fall back to computing
  from modifiers as before.

This is correct whether or not Unity carries modifiers on the character
event, so it does not rest on an unverified platform detail -- the
regression tests exercise BOTH shapes explicitly. The decision is
extracted as a pure static seam so it is unit-testable without a window.

## 1b. The ACTUAL root cause (measured after the first fix failed)

The v0.13.2 fix above did not fix the bug. The user retested: still no
newline, and additionally **the caret disappears and comes back on a
second press** -- a focus symptom, not a text-insertion one. So the
modifier-loss theory was WRONG (it was a plausible reading of the code,
not a measured fact -- recorded here as the mistake it was).

An event/focus logger installed on the live panel captured one real
Shift+Enter press:

```
KEY-trickle key=Return ch=0  shift=True  target=TextElement
KEY-trickle key=None   ch=10 shift=True  target=TextElement
FOCUS-OUT  from=TextElement to=TextField[uap-composer-field]
BLUR       TextElement
(no ChangeEvent at all)
KEY-trickle key=Return ch=0  shift=True  target=TextField      <- redelivered
FOCUS-IN   to=TextElement                                       <- caret returns
```

Two facts fall out:

1. `shift=True` IS present on the character event. The modifier never
   went missing; the earlier fix addressed a non-problem (it is still
   correct-by-construction, so it stays, now with honest reasoning).
2. UI Toolkit's editing engine responds to Shift+Enter in a MULTILINE
   field by ending the edit session -- focus moves from the inner
   TextElement to the composite root (the outer TextField), which is
   exactly the caret vanishing, and no text is inserted. Verified state:
   `TextField.multiline == true` AND the inner `TextInput.multiline ==
   true`, so this is UITK's intended mapping (Enter = newline,
   Shift+Enter = commit/leave), i.e. the INVERSE of the convention this
   panel implements (Enter = send, Shift+Enter = newline).

The panel deliberately owns Enter (send), so it can never inherit UITK's
newline; and it cannot delegate Shift+Enter either, because UITK spends
that gesture on ending the edit.

### Decision (v0.13.3)

Stop delegating the newline. On a Newline decision the handler now stops
the event (`StopPropagation` + `PreventDefault`, so the end-edit path
never runs) and performs the edit itself: replace the current selection
with `"\n"` via `TextField.value` and move both `cursorIndex` and
`selectIndex` past it with `SelectRange` (both are public settable in
2022.3 -- probed). Only the keycode event inserts; the paired character
event is swallowed, so a press can never produce two breaks. The pure
text edit (`ComputeNewlineInsertion`) is unit-tested, including
selection replacement and clamped/stale caret indices.

## 2. Extension-profile rows are laid out with the wrong classes

Measured live (settings view, panel width 285): row children are

| element | class | width |
|---|---|---|
| name "VRChat SDK3 (Avatars)" | `uap-settings-qa-label` | **110 (fixed, flex-shrink 0)** |
| status "同梱" | `uap-settings-hint` | 24 |

`uap-settings-qa-label` is the Quick Actions column label: a fixed 110px
non-shrinking box sized for short user-authored action names. An SDK
display name is longer than that, so it is clipped. `uap-settings-hint`
is a BLOCK hint style (`white-space: normal`, top/bottom margins) being
used as an inline status chip, so it wraps and sits on a different
baseline than the name -- the ragged look reported.

### Decision

Give profile rows their own classes rather than borrowing two unrelated
ones (the same mistake class as the v0.9.0 model-row overflow):

- `.uap-settings-profile-name`: `flex-grow: 1; flex-shrink: 1;
  min-width: 0; white-space: nowrap; overflow: hidden;
  text-overflow: ellipsis;` -- long names shorten instead of clipping the
  row apart.
- `.uap-settings-profile-status`: small secondary text like the hint but
  `flex-shrink: 0` with no vertical margins, so it stays on the row's
  baseline.
- Buttons keep `flex-shrink: 0` (already the case) so approve/revoke can
  never be pushed off-screen -- the v0.9.0 failure mode.

Guarded by a UssHygieneTests source scan, matching the existing
model-row shrink guard.
