# Error flood, permission fatigue, and uLoop-instead-of-UapOps

Date: 2026-08-02
Status: approved (three live findings from one working session)
Ships as: v0.14.0

## 1. Error/warning flood: a self-amplifying loop (measured)

The live editor log holds **1106** copies of
`MissingReferenceException: The object of type 'Material' has been
destroyed...`. Two of our own defects chain together:

**Seed** -- `FontLoader.DestroyJapaneseUiAsset` runs on
`beforeAssemblyReload` and destroys the OS-derived `FontAsset`, but panel
labels still carry it as an inline `unityFontDefinition`. Any repaint in
that window hits `UIRStylePainter.DrawTextInfo -> Material.mainTexture`
on the destroyed material and throws once per text element.

**Amplifier** -- `ConsoleErrorProvider.OnLogMessage` (an
`Application.logMessageReceived` handler) calls `RaiseChanged()`
synchronously, which runs `ContextBarView.UpdateErrorChip` and assigns
`Label.text`. When the originating log came from inside a repaint, that
assignment throws
`InvalidOperationException: VisualElements cannot be marked for dirty
repaint ... during generateVisualContent`, which is itself logged, which
re-enters `OnLogMessage`. Captured stack confirms the exact chain.

### Decisions

- **Never destroy a font asset the UI may still reference.** Tag the
  created asset (`name` marker) and, after a domain reload, RE-FIND and
  reuse it instead of creating a second one; drop the
  `beforeAssemblyReload` destroy. Destroy leftovers only when they are
  provably unreferenced (extras found at resolve time) so nothing leaks
  per reload. Probe first: confirm a `HideAndDontSave` FontAsset survives
  a domain reload and is discoverable via `Resources.FindObjectsOfTypeAll`.
- **A log callback must never touch UI synchronously.** Coalesce into a
  flag and apply on the next `EditorApplication.update` tick (the pump
  idiom already used elsewhere), plus a re-entrancy guard so a log
  emitted while applying cannot recurse. This also fixes the unrelated
  hazard that `logMessageReceived` can fire off the main thread.

## 2. Permission fatigue and transcript bloat (measured)

One working session: 36 messages, of which **32 are system notes** and
every one is "<Tool> を許可しました" (approved). The chat is now mostly
approval receipts, and the user had to click ~30 prompts (24 of them
PowerShell).

### Decisions

- **Stop writing an approval receipt into the transcript.** The tool
  card already shows the call, its input and its result; a separate
  "approved" note adds nothing and crowds out the conversation. Keep the
  note for DENIALS (a refusal is real context the transcript should
  retain) and for the script-gate auto-deny.
- **Read-only UapOps tools stop prompting.** Add an explicit
  `ReadOnly` flag to `IUapTool` (query/inspect/list/find/get_overrides/
  screenshot) and auto-approve those requests panel-side, behind a
  Settings toggle (default ON) with a clear label. Anything that mutates
  the project keeps its card. This is safe by construction -- the flag is
  ours, and a read-only tool that ever gains a write path fails its
  metadata test.
- **"Always allow" for MCP tools.** When the CLI offers no
  `permission_suggestions` for an `mcp__...` request, synthesize the
  obvious rule (exact tool name) so the Always button is available, and
  persist accepted rules so the next session does not re-ask.

## 3. Animation work went through uLoop, not UapOps (measured)

The same session shows `PowerShell x24` -- "Generate humanoid walk clip",
"Create controller and assign to avatar", "Rebuild leg curves..." -- all
`uloop execute-dynamic-code`, while the anim module was ENABLED and its
tools were registered (the capability matrix suppresses nothing: its
covered-name set is empty).

So this is not a wiring bug: the agent simply reached for the
general-purpose escape hatch. Nothing told it the typed tools exist or
that they are preferred, and MCP tools are behind ToolSearch (name only
until searched), so the cheap path won.

### Decision

Ship the L2 steering snippet the Phase 5 design (section 2) always
intended, injected next to the cost-policy line:

- Unity edits should go through the `uap_*` tools when one fits.
- `uloop` / raw dynamic code is for what those tools cannot express, and
  it is the slower, confirmation-heavy path.
- Name the families so discovery does not depend on guessing:
  scene/component/property/asset, prefab overrides, anim/animator/
  material, screenshot/menu.

Deliberately NOT done: suppressing uloop, or auto-allowing dynamic code.
The escape hatch stays -- it is what made the walk-cycle work possible --
it just stops being the default reach.
