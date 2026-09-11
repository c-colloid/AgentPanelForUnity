# 2026-08-23 -- Phase 3 batch L: nine small UI fixes and the layering guard

The UICODE S-items, two localization/wording items, the font-size
ceiling, and the source-scan test that makes MODEL-5's class of bug
impossible to reintroduce silently. Items: UICODE-7/9/10/11/12,
UXO-13, UXIA-7, UXIA-L5, INFRA-4.

## UICODE-7: the Enter keycode->char bridge resets on any other key

_pendingEnterAction hands one keycode Enter's decision to its paired
character event. When the pair never completes (some IME/platform
paths suppress the char event after PreventDefault), the stale action
sat armed until the next ORPHAN Enter character -- an IME newline
commit -- and replayed the old Send decision on it. Any unrelated key
now clears the bridge. Tested through OnKeyDown itself via a new test
seam (panel-less SendEvent is a no-op in EditMode), with synthetic
KeyDownEvents.

## UICODE-9: quick actions stop being re-read from disk per selection change

RebuildChips read QuickActions.json on every Selection.selectionChanged
/ ConsoleErrorProvider.Changed while the empty state was visible --
disk IO for data that only changes in Settings. Cached; reloaded on
construction and on each SHOW (cheap, and it picks up Settings edits
made while a conversation was displayed). The dynamic selection/error
chips keep rebuilding from their in-memory providers.

## UICODE-10: the model picker stops re-parsing per Changed event

RefreshModelPicker ran ParseModels -- a fresh List + option objects --
on every AgentHub.Changed (per streaming delta at the hottest) just to
answer Count>0. GetModels memoizes on the InitializeResponse
REFERENCE, which is an exact key: the models node is immutable per
response instance, and a reconnect replaces the instance. Tests pin
same-reference -> same list, different response -> re-parse.

## UICODE-11: the icon cache keys on the editor skin

Find's candidate order depends on EditorGUIUtility.isProSkin, but the
cache keyed on name alone -- after a live theme switch, every already-
cached icon kept its old-skin texture until the next domain reload.
The key is now skin-qualified (CacheKey(name, isProSkin), pure and
pinned); a switch resolves into the other slot on the next Find with
no manual invalidation.

## UICODE-12: an unterminated fence with an empty body still renders

The defensive tail flush ("render the tail as code so nothing is
silently lost") guarded on fenceBody.Length > 0 -- so output cut right
after "```lang" dropped the fence AND its language signal entirely,
contradicting the comment above it. The guard is gone; the honest
rendering of that cut is an empty read-only code block.

## UXO-13: the crash-loop note points at a button that exists

"Restart from the panel" named no actual control; the recovery action
is the Reconnect button. Both catalogs now say press "Reconnect" /
「再接続」-- the same word as bannerReconnectButton.

## UXIA-7: permission-mode labels are localized (and leave the Model layer)

The dropdown labels were English literals in Model-layer
PermissionModeMapping.DisplayName -- unlocalizable without violating
D9 (Model cannot reference L10n). New UI-layer PermissionModeLabels
(AutoApproveLevelLabels' shape: per-option catalog strings, unknown
values fall back to the SAFEST label -- Default, ask every time);
SettingsView's formatter switched; DisplayName deleted with its only
non-test caller; three new strings in both catalogs, parity-checked.

## UXIA-L5: the font-size ceiling rises 16 -> 20

16 left users needing larger text with nowhere to go. The cap is now
20 (visual reasoning on the constant: the 4px grid stays workable to
20; beyond it fixed paddings start truncating), FontScale.uss gains
.uap-fontscale-17..20 with the same per-variable arithmetic as the
existing steps, and -- because ApplyFontScale is a class LOOKUP where
a missing step is a silent no-op -- new FontScaleRangeTests asserts
every size in Min..Max has its class, so raising the constant without
the classes can never ship again. The clamp tests now track the
constants instead of hardcoding 16.

## INFRA-4: the layering rules become tests

The asmdef layout (one Editor assembly) cannot compiler-enforce
ARCHITECTURE section 2, and MODEL-5 proved the rules erode silently.
An asmdef split was evaluated and rejected (Core/Process touches
UnityEditor by design -- EditorApplication pumps, AssemblyReloadEvents
-- and splitting would force file moves). LayerHygieneTests instead
scans sources the UssHygieneTests way: Core/Json, Core/Protocol,
Core/Client and Core/FileIo must contain no UnityEngine/UnityEditor
token; Model must not reference the UI namespace or L10n/UiStrings.
Comments are stripped first (every historical near-miss was a doc
comment legally MENTIONING the other layer), offenders list as
file:line, the forbidden tokens are assembled by concatenation so the
test never trips itself, and a negative self-test proves the detector
detects.

## Honest residuals

- UICODE-7's seam drives OnKeyDown directly; the real
  trickle-down-from-panel dispatch is exercised only live. The seam
  calls the exact private handler the registration points at.
- INFRA-4 deliberately leaves Core/Process unscanned (documented in
  the test); moving its editor-touching files to make it scannable is
  a larger refactor than the guard is worth today.
