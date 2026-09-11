# Design note: scope the font-scale class to conversation content only

- Date: 2026-08-01 / Status: adopted
- Related: user-reported bug ("dragging the font-size slider is
  unusable"); supersedes nothing in the font-scale MECHANISM itself
  (docs/design-notes/2026-07-31-font-scale-cascade-fix.md's
  load-order fix stays exactly as-is) -- this note only changes WHICH
  ELEMENT receives the `uap-fontscale-N` class.

## Root cause (read from the actual code, not guessed)

`AgentPanelWindow.ApplyThemeAndStyles(VisualElement root)` is the single
shared entry point both `AgentPanelWindow.CreateGUI()` and
`PermissionWindow.CreateGUI()` call, always with `root ==
rootVisualElement` (the WHOLE window, header + view-switch strip +
Chat/Settings/History all included as children of the same
`viewContainer`). Before this fix it ended with:

```csharp
internal static void ApplyThemeAndStyles(VisualElement root)
{
    root.AddToClassList(EditorGUIUtility.isProSkin ? "uap-theme-dark" : "uap-theme-light");
    LoadStyleSheets(root);
    ApplyCjkUiFont(root);
    ApplyFontScale(root);   // <-- the whole window root
}
```

`ApplyFontScale` assigns a `uap-fontscale-N` class that redefines the
`--uap-font-size-*` CUSTOM PROPERTIES every text class in
`AgentPanel.uss` reads via `var()` (docs/design-notes/2026-07-31-
font-size-application.md). Custom properties are inherited, so putting
the class on the WINDOW root means it reaches every descendant --
Chat's transcript AND Settings' own field labels/hints AND History's
rows AND the view-switch strip -- not just the conversation the feature
was meant to resize.

`SettingsView.OnFontSizeChanged` calls
`AgentPanelWindow.ReapplyContentRootStyling()` on every single slider
`ChangeEvent` (i.e. continuously while dragging), which re-adds the
`uap-fontscale-N` class to the window root. Since Settings' own layout
(its field labels, hint text, section card padding-adjacent text) was
ALSO inside that same cascade, every drag tick reflowed the Settings
view the slider itself lives in -- shifting the very slider control
out from under the cursor mid-drag. This matches the reported symptom
exactly ("the settings layout shifts under the cursor and continuous
dragging is impossible") and needs no further runtime reproduction:
the cascade path is fully traceable from `ApplyThemeAndStyles`'s call
site down through `AgentPanel.uss`'s `var()` usage to prove it.

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| (a) Move the `uap-fontscale-N` anchor down to each window's conversation-content root (`AgentPanelWindow._chatRoot`, `PermissionWindow`'s card-wrapping content root) | Chat/permission-preview text keeps scaling; Settings/History/chrome stay fixed | **Adopted** | Custom-property inheritance needs the anchor to be an ancestor of the text that should scale -- it does NOT need to be the window root; `_chatRoot` already is an ancestor of every chat text element (message list, composer, context bar). Two call-site changes plus a small `ReapplyContentRootStyling` retarget; the FontScale.uss mechanism, its selectors, and its load order are all untouched. |
| (b) Add a `:not(.uap-settings) :not(.uap-history)` exclusion at the window root instead of moving the anchor | Keep the class on the window root, carve out Settings/History via negative selectors | Rejected | USS selector support for this pattern is unverified in this codebase (no existing precedent), and it would need updating every time a new top-level, non-scaling view is added (fragile by construction) versus (a), which scales however many "fixed" siblings viewContainer ever grows to hold, automatically, because they simply never receive the class. |
| (c) Give Settings/History their own explicit fixed-size override classes that pin every text class back to the base size, layered on top of keeping the fontscale class at the window root | Root stays the anchor; Settings/History opt back OUT explicitly | Rejected | Requires enumerating and overriding every text class Settings/History use (`.uap-text`, `.uap-settings-hint`, `.uap-settings-card-title`, ...) -- the same "60 rules replacing 9" verbosity the original cascade-fix note (2026-07-31-font-scale-cascade-fix.md option (b)) already rejected for a symmetric reason, and still leaves the window CHROME (header, view-switch strip) scaling, which the task explicitly wants fixed too. |

## Decision

- `AgentPanelWindow.ApplyThemeAndStyles` no longer calls `ApplyFontScale`
  (still applies the theme class, the 4 stylesheets, and the CJK font at
  the window root exactly as before -- only font-scale moved).
- `AgentPanelWindow.CreateGUI()` calls `ApplyFontScale(_chatRoot)` right
  after building the chat view, instead.
- `PermissionWindow` now keeps its own content root (the `VisualElement`
  wrapping `PermissionCard.Root`, previously a throwaway local named
  `host`) in a new `_contentRoot` field, exposed as internal
  `ContentRoot`, and calls `AgentPanelWindow.ApplyFontScale(host)` on it
  in `CreateGUI()` (its window root still goes through the shared
  `ApplyThemeAndStyles` unchanged).
- `AgentPanelWindow.ReapplyContentRootStyling()` (the live-update path
  the font-size slider and the CJK toggle both call on every change)
  retargets: CJK font still reapplies to each window's
  `rootVisualElement`, but font-scale now reapplies to
  `panel._chatRoot` / `permWindow.ContentRoot`.
- `ApplyFontScale` itself is now `internal` (was `private`) so
  `PermissionWindow` can call it directly, and its doc comment explains
  the "conversation-content root, never the window root" contract so a
  future caller does not reintroduce the window-root anchor by habit.
- `PanelSettings.fontSizePx`'s doc comment updated to match (it
  previously said the font size applies "at the same content roots that
  receive the CJK font", which was accurate before this fix and is now
  wrong -- CJK stays window-wide, font-scale does not).

## Side effect worth recording (not the point of the fix, but observed while making it)

Moving the anchor down to a DESCENDANT of the theme-class element
incidentally removes the load-order dependency FontScale.uss previously
needed for the elements that matter: the old mechanism relied on
FontScale.uss loading after ThemeDark/ThemeLight because both classes
redefined the same custom properties on the SAME element (a cascade
tie broken by load order -- 2026-07-31-font-scale-cascade-fix.md). Now
that `uap-fontscale-N` sits on `_chatRoot`, a descendant of the window
root the theme class stays on, there is no same-element tie there any
more: a descendant's own specified custom-property value always beats
whatever it would otherwise inherit, unconditionally, regardless of
stylesheet order. `FontScale.uss` is left loading last anyway (no
reason to touch it, and Settings/History's own theme-driven
`--uap-font-size-*` values still come from the same load-order-settled
cascade at the window root where the theme class lives).

## What this does NOT change

- The font-scale MECHANISM (custom-property override via a
  `uap-fontscale-N` class, `FontScale.uss` contents/load order) is
  untouched -- this note is purely about which element the class lands
  on.
- The live-update wiring (`SettingsView.OnFontSizeChanged` ->
  `ReapplyContentRootStyling()`) is untouched at the call-site level;
  only what `ReapplyContentRootStyling` itself targets changed.
- The font-size slider's own hint text
  (`_fontSizeValueLabel.text = FormatFontSize(clamped)`, formatted via
  `SettingsFontSizeValueFmt`) is set directly inside
  `OnFontSizeChanged`, independent of `ReapplyContentRootStyling` --
  confirmed still updates on every drag tick exactly as before, since
  that line was never touched.

## Verification

Compile + full EditMode suite (see the verification log for this
round's exact numbers). No prior checked-in test asserted the
fontscale class landed on `rootVisualElement` specifically (confirmed
by search before this change), so nothing needed updating for that
reason; new regression tests were added instead (see
`AgentPanelWindowStyleSheetTests.cs`/equivalent additions) pinning that
`ApplyFontScale` targets the passed-in element only, never a window's
`rootVisualElement`, and that `ApplyThemeAndStyles` no longer adds a
`uap-fontscale-*` class to the root it is given.
