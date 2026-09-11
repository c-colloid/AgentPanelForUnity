# Design note: switch position tracked label length (`.uap-switch` alignment)

- Date: 2026-08-01 / Status: adopted
- Related: user feedback ("switch position depends on label length,
  increases eye-tracking load"); the switch restyle itself is
  docs/design-notes/2026-08-01-settings-visual-refresh.md phase C.

## Root cause (confirmed by reading the actual rule, not guessed)

`.uap-switch` is added (via `AddToClassList`) to a stock Unity `Toggle`,
whose real element tree (verified against `Toggle.cs` in the 2022.3
`UnityCsReference` branch, already cited in R06 section 3.6) is:

```
Toggle (unity-toggle, uap-switch)
├─ Label (unity-toggle__label)      -- content-width, only when a label string was passed
└─ VisualElement (unity-toggle__input)  -- the switch track, contains unity-toggle__checkmark
```

`AgentPanel.uss`'s existing switch rule pins the track to a fixed 32x18
box:

```css
.uap-switch .unity-toggle__input {
    width: 32px;
    height: 18px;
    flex-grow: 0;
    flex-shrink: 0;
    ...
}
```

The comment already on that rule says why: "the default
`.unity-toggle__input` stretches to fill the control column" -- i.e. a
plain Unity `Toggle`'s input normally has `flex-grow` and fills
whatever row width is left after the label, which is how Unity gives an
inspector-style Toggle a comfortably large click target. Pinning
`flex-grow: 0`/`flex-shrink: 0` was necessary to keep the switch track a
fixed 32px instead of stretching edge-to-edge, but the row itself was
never given a `justify-content` (Unity's default for a `Toggle` row is
`flex-start`), so once the input stopped growing to fill the remainder
of the row, it just sat packed immediately after the label instead of
flush against the row's right edge. The label's own width is
content-width (no fixed min-width applies outside an Inspector
context), so the track's on-screen X position -- everything the eye
actually tracks -- shifted with however many characters the label
string happened to have.

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| (a) `justify-content: space-between` on `.uap-switch` + pin the label's own `flex-grow: 0` | Pushes the label to the row start and the (already fixed-width) track to the row end, independent of label length | **Adopted** | Matches exactly what regressed: the row's own distribution, not the track/label individually. Two lines of USS, zero C# changes, zero token changes (both themes already ship the switch color tokens). |
| (b) Fixed `min-width` on `.unity-toggle__label` (align labels like an Inspector) | Give every switch label a fixed column width so the track always starts at the same X | Rejected | Solves alignment ACROSS rows (all switches lining up in a column) but is a different problem than the one reported (`justify-content: space-between` already guarantees each row individually reads label-left/switch-right, which is what "eye-tracking load" was about); a fixed min-width would also clip/wrap longer labels for no benefit here since rows are not meant to visually align into a form-style column in this settings layout. |
| (c) Wrap each Toggle in a custom `VisualElement` row and rebuild the label/track split in C# | Full control, no dependency on Unity's internal Toggle structure | Rejected | The existing R06 section 3.6 finding -- that the vanilla Toggle already exposes stable, verified USS class hooks (`unity-toggle__input`/`__checkmark`/`__label`) -- is exactly what let phase C ship as "USS + one AddToClassList line" in the first place; (c) throws that away to fix a one-line `justify-content` omission. |

## Decision

- Added to `AgentPanel.uss`, in the existing "Toggle -> switch restyle"
  block:
  ```css
  .uap-switch {
      justify-content: space-between;
  }
  .uap-switch .unity-toggle__label {
      flex-grow: 0;
      flex-shrink: 1;
  }
  ```
- No C# changes: every `.uap-switch` Toggle (Ctrl+Enter, danger-zone
  skip-permissions, show-thinking, subagent-default-expanded,
  show-cost-usd, permission beep, turn-complete beep, CJK UI font -- 8
  rows across Conversation/Notifications/Appearance) picks this up
  automatically since they already carry the class.
- No token changes: both `ThemeDark.uss`/`ThemeLight.uss` already define
  the switch track/knob colors this rule does not touch.

## Verification

Compile + full EditMode suite (see the verification log for this
round's exact numbers) -- no existing test asserts on `.uap-switch`
layout (docs/design-notes/2026-08-01-settings-visual-refresh.md section
3 explicitly chose NOT to add a source-scan guard for the switch
restyle, calling it "too coupled to implementation detail"; the same
reasoning applies here, so this is a pure USS change with visual
verification only, consistent with that precedent).
