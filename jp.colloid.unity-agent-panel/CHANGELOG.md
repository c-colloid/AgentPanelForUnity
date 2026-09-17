# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

(nothing yet)

## [0.56.2] - 2026-09-17

### Fixed

- **The Agent card no longer offers a primary "Sign in" while an ACP agent
  is already connected.** The card now has four distinct shapes: signed
  out (primary "Sign in" plus the how-to hint), signing in (status,
  Cancel and the browser link), connecting ("Connecting to {agent}...",
  no buttons) and signed in (the sign-in method line and a plain "Switch
  account"). Design note
  `docs/design-notes/2026-09-17-acp-account-card-phases.md`.

## [0.56.1] - 2026-09-17

### Fixed

- **Sketch strokes and pins no longer see the hidden main scene while a
  prefab is open.** In Prefab Mode the depth contour traced the room
  behind the prefab, and a pin or a surface stroke could land on it:
  the renderer scan returned the main scene's objects even though the
  stage does not draw them. Picking and the contour now keep only what
  the current stage contains. Design note
  `docs/design-notes/2026-09-17-scene-sketch-strokes.md` section 9.
- **The wheel now moves an axis-locked sketch plane from any viewpoint.**
  A Z plane seen from above did not respond, because the depth step fell
  back to the view direction when it was nearly perpendicular to the
  plane; an axis plane now always moves along its own normal. Same note.
- **The sketch plane's readout (depth, axis, key hints) sits at the Scene
  view's top-left** instead of next to the cursor, so it is visible for
  every plane and view. Same note.

## [0.56.0] - 2026-09-17

### Added

- **Sketch strokes: draw in the Scene view and hand the line to the
  agent.** The context bar's new "Sketch" menu (and an "Agent Sketch"
  toolbar overlay in the Scene view) arms one of two modes; a left drag
  then draws a stroke (S1, S2, ...) that becomes a context chip carrying
  the world-space points, and the new read-only `uap_stroke_list` tool
  (module `markers`) returns every stroke as JSON -- points, per-point
  normals and the objects a surface stroke was drawn on, length, closed
  flag, bounds and the sketch plane. **Plane mode** draws on a plane at
  a depth you choose: the Scene view shows the plane's section contour
  on every mesh it cuts, a depth-tested grid patch under the cursor, and
  a readout of the depth and of how far the surface under the cursor is
  in front of or behind the plane; the wheel and `[` `]` move the depth,
  F snaps it to the surface under the cursor, X/Y/Z lock a world-axis
  plane, C returns to camera-facing, Shift draws a straight line.
  **Surface mode** draws on the mesh surface under the cursor. Strokes
  are Scene-view overlays only (also drawn into camera-mode screenshots),
  survive a domain reload, and vanish on scene change or Play Mode; the
  chip's X, the "N scene markers" chip's clear button, or Esc handles
  them like pins. Design note
  `docs/design-notes/2026-09-17-scene-sketch-strokes.md`.

## [0.55.2] - 2026-09-17

### Changed

- **The CLI card and the Account card are now one "Agent" card.** It
  reads top to bottom as: which agent, whether its command was found
  (with the install button when not), whether it is signed in, the
  sign-in method, the sign-in buttons, and a closed-by-default
  "Advanced (executable / command)" foldout holding the former CLI
  fields, which opens itself while the command is not found. One
  "Reconnect" button replaces "Re-detect", "Reconnect now" and the ACP
  "Reconnect". The "Diagnostics" card is renamed "CLI output (stderr)".
  Settings values and the reconnect-banner behavior are unchanged.
  Design notes:
  `docs/design-notes/2026-09-17-account-card-agent-picker-and-acp-init.md`
  (the picker's move out of the collapsed CLI card, the first step) and
  `docs/design-notes/2026-09-17-agent-card-merge.md` (the merge).
- **"API key authentication" (Claude Code only) and "Auth method id"
  (ACP only) are now one "Sign-in method" row** whose shape follows the
  selected agent: Claude Code offers "Auto (leave it to the CLI)" /
  "Subscription only", an ACP agent takes its authenticate method id.
  Switching to an ACP agent no longer leaves the Claude hint line
  behind. Design note `2026-09-17-agent-card-merge.md`, section 3.

### Fixed

- **"Connected with an API key (acp)" no longer shows under a
  "Subscription only" picker.** After switching from an ACP agent back to
  Claude Code, the Account card read the ACP bridge's synthesized
  system/init (which carried `apiKeySource: "acp"`) as Claude API-key
  billing until Claude's own init arrived. An init an ACP bridge produced
  is never counted as Claude API-key auth, and the bridge now reports
  `apiKeySource: "none"`. Design note
  `docs/design-notes/2026-09-17-account-card-agent-picker-and-acp-init.md`.
## [0.55.1] - 2026-09-17

### Fixed

- **A Console error caused by the agent's own tool call no longer raises
  the "ask the agent to fix these" chip.** Measured with Codex: it passed
  `uap_editor_execute_menu` a menu path that does not exist
  (`GameObject/Duplicate`); the tool answered `found:false` and the agent
  worked around it, but Unity also logs an Error for that call, so the red
  chip appeared, stayed for the rest of the session and was then offered
  to the next agent as well ("Ask Grok to fix") although nothing was wrong
  with the project. Errors logged on the main thread while a UapOps tool is
  running are now attributed to that call: they are appended to the tool
  result (`Unity Console errors logged during this call:`, at most 5
  lines) so the agent still sees them, and they stay off the chip. Errors
  from other threads, and anything the project logs again after the call,
  reach the chip as before. `uap_editor_execute_menu` additionally checks
  that the menu item exists before running it, so that particular Error is
  no longer logged at all. Design note:
  `docs/design-notes/2026-09-17-tool-caused-console-errors.md`.

## [0.55.0] - 2026-09-17

### Added

- **`uap_scene_save`: save a scene without any dialog.** Saves the active
  (or a named) loaded scene in place, or to `path` (`Assets/.../Name.unity`,
  folders created) with `saveAsCopy` / `overwrite`. An untitled scene
  without `path` is refused up front instead of reaching Unity, which
  would open the Save Scene dialog. There was no save tool before, so
  agents ran the `File/Save` menu instead -- see below.
- **`uap_scene_open`: open a scene file without any dialog.** The
  counterpart of `uap_scene_save` for the refused `File/Open Scene` menu
  (whose refusal now points here). `mode:"single"` (default) replaces the
  loaded scenes and is refused -- naming them -- while one has unsaved
  changes, because the Editor API drops those without asking; save first,
  pass `discardUnsaved:true`, or use `mode:"additive"`. Paths under
  `Assets/` or `Packages/`; refused in Play Mode. Design note:
  `docs/design-notes/2026-09-17-modal-menu-and-base64-scan.md` (1.6).

### Fixed

- **`uap_editor_execute_menu` no longer parks the Editor behind a native
  modal dialog.** A live Codex session ran `File/Save` on an untitled
  scene: Unity opened the native Save Scene dialog inside the call, the
  Editor loop did not tick once until a person closed it (288 s), every
  other `uap_*` call died "never started" and the panel stopped answering
  permission requests. The menu items measured to open such a dialog
  (`File/Save` while a scene is untitled, `File/Save As...`,
  `File/Open Scene`, `File/Build And Run`, `File/Exit`,
  `Assets/Import New Asset...`, `Assets/Import Package/Custom Package...`)
  are now refused before they run, with the alternative in the message.
  Build Settings, Project Settings, Preferences and the rest are ordinary
  windows and keep working.
- **A main-thread timeout now says when a modal dialog is the cause**
  (Windows). For dialogs a third-party menu or tool opens, the timeout
  text names the dialog and says only a person can close it, instead of
  "will finish on its own"; calls issued while it is open fail in 8 s
  with the same note.
- **A screenshot returned as text no longer freezes the Editor for
  minutes.** Codex hands an MCP result over as one JSON string, base64
  picture included; the tool card's image-path detection ran two regexes
  that were quadratic on such text (measured: 431 s of "not responding"
  for a 498 KB screenshot, 256 s in the live session, shown as "Hold on
  ... EditorUpdatePump.OnEditorUpdate"). The scan is now linear (same
  input: 1 ms) and finds the same paths. Design note:
  `docs/design-notes/2026-09-17-modal-menu-and-base64-scan.md`.

## [0.54.8] - 2026-09-17

### Fixed

- **Codex: a picture returned by an MCP tool now shows in the tool card,
  and its base64 no longer travels as result text.** codex-acp delivers an
  MCP tool result only as `rawOutput` =
  `{"result":{"content":[...]},"error":null}` with an empty `content`; the
  ACP bridge stringified that whole object, so a
  `uap_editor_screenshot(return_image)` result became a ~500 KB JSON
  string and the screenshot appeared only when its file path happened to
  resolve on disk. The bridge now translates the MCP blocks like regular
  ACP content: text becomes the result text, image blocks become embedded
  images (at most 4 per result, 8 MiB of base64 each; anything beyond
  leaves an `[image]` stand-in). Any other `rawOutput` shape is shown as
  before. Design note:
  `docs/design-notes/2026-09-17-acp-mcp-rawoutput-images.md`.

## [0.54.7] - 2026-09-17

### Fixed

- **Grok / Codex: UapOps tool calls are recognized again, so the
  auto-approve level applies.** Grok Build calls MCP tools through its
  own `use_tool` dispatcher and names them `unity-ops__uap_ping`; the
  ACP bridge missed that shape (it fixes the name on the first frame,
  whose title is `use_tool`, never read `rawInput.tool_name`, and
  rejected a `uap_` preceded by `_`), mapped the call to "Tool", and a
  permission card titled `unity-ops__uap_ping` appeared for every
  `uap_*` call even at "All Unity operations". These calls now map to
  `mcp__unity-ops__uap_*` like Claude's, the card reads
  "unity-ops: uap_ping", and the tool's own arguments are shown instead
  of the agent's `tool_input` / `arguments` envelope.
- **A tool call that merely mentions a `uap_*` id is no longer treated
  as that UapOps tool.** The bridge used to search the agent's free-text
  title for `uap_<name>`; with Codex the title of a shell call is the
  command line, so `uap_ping && <anything>` would have been
  auto-approved as the read-only `uap_ping`. The id is now only read
  from fields that hold a tool id (leading title token, or
  `tool_name` / `tool` / `toolName` with the unity-ops server), never
  for a shell call.
- **Agent-specific tools show their own name instead of "Tool".** Grok's
  tool search and other tools of ACP kind `other` (or none) are titled
  with the agent's tool title; Grok's shell calls are recognized as Bash
  from the first frame (command shown, script gate applies); a Codex
  call to another MCP server shows as that MCP tool instead of "Bash".
  Design note: `docs/design-notes/2026-09-17-acp-tool-name-mapping.md`.

## [0.54.6] - 2026-09-17

### Fixed

- **Switching the model right after "+" (or a reconnect) now applies to
  that session.** In the seconds before the initialize handshake
  answered, the header picker (and the narrow-panel chip) fell back to
  the cached catalog menu, whose pick only wrote the next-session
  default; the freshly spawned session kept its old model and the chip
  briefly showed the new name before flipping back. A pick made while a
  client is alive is now held and sent as `set_model` the moment the
  handshake resolves; the chip shows the picked model meanwhile.
- **Every model switch now confirms itself in the transcript.** A system
  note says when a switch is waiting for the connection, when it
  applied, and when it failed or timed out (the session then keeps its
  previous model). A timed-out control request now resolves like a
  failed one instead of vanishing. Design note:
  `docs/design-notes/2026-09-17-model-switch-before-init.md`.

## [0.54.5] - 2026-09-16

### Fixed

- **Shrinking the panel below the inline minimum no longer leaves an
  expanded permission or question card pushing the composer out of the
  window.** The inline-vs-window choice runs once when a request
  arrives, so a card decided inline in a large panel kept its expanded
  body and its 220px question floor after the panel was dragged below
  380px tall or 300px wide; with no outer scrollbar the composer and
  status bar were pushed off-screen until the request was answered. The
  chat view now collapses the card to its summary row when the panel
  becomes too small (the same answer "Show here" already gave there) and
  re-expands it when the panel grows back, without overriding a card the
  user opened or closed themselves. Design note:
  `docs/design-notes/2026-09-16-inline-card-refit-on-shrink.md`.

## [0.54.4] - 2026-09-16

### Fixed

- **Resizing the panel no longer re-wraps the whole transcript on every
  frame of the drag.** UI Toolkit re-measures every wrapped label whose
  width changes, and a resize drag changed the width each frame, so a
  long conversation (up to the 300 rendered messages, plus the Settings
  and History views) had the panel's repaint dominating the editor frame
  while the edge was dragged. The transcript, Settings and History
  scroll views now pin their content width for the duration of the drag
  and reflow once, 120 ms after the width stops changing. Design note:
  `docs/design-notes/2026-09-16-resize-reflow-throttle.md`.

## [0.54.3] - 2026-09-16

### Fixed

- **Detaching an AskUserQuestion card no longer leaves a tall blank card
  over the transcript.** After "Open in window" (or the automatic
  hand-off to the floating window on a small panel), the inline card is
  meant to shrink to the slim "Waiting for permission - shown in window
  [Show here]" bar. For question cards it instead kept its 220px
  usability floor (introduced in 0.42.5 so a question always shows a few
  options), so an empty card the height of the question stayed in the
  panel and hid the last messages until the window was answered. The
  same floor also held a collapsed question card open at 220px instead
  of its single summary row. The floor now follows the body: it applies
  only while the card is inline, expanded and not shown in the window,
  exactly like the tool card's floor. Design note:
  `docs/design-notes/2026-09-16-askuserquestion-detached-ghost-card.md`.

## [0.54.2] - 2026-09-15

### Changed

- **"Continue automatically after a compile" now chains.** A
  continuation turn that itself commits more staged scripts (via
  `uap_scripts_commit`) is continued again after that reload, instead
  of stopping with "this turn was itself an automatic continuation, so
  auto-continue will not fire a second time in a row -- you will need
  to prompt again". The original once-per-turn guardrail handed the
  write -> commit -> compile -> fix -> commit loop back to the human
  every other hop, which is the loop the feature exists to automate.
  What still ends a chain: the agent stops committing (no fresh
  attribution ticket, no continuation), the crash-loop guard suspends
  the CLI connection, or you press Stop -- the same three stops
  "Continue automatically after an interruption" has had since 0.36.0
  removed its streak cap. Attribution (only the agent's own
  .cs/.asmdef commit), the five-minute ticket expiry, and the visible
  system note are unchanged. The Settings help/tooltip and the
  pending-reload note now say so (design note
  `docs/design-notes/2026-09-15-chained-auto-continue-after-compile.md`).

## [0.54.1] - 2026-09-15

### Fixed

- **The "Mesh creation" row's inline hint in Settings > Unity operations
  (UapOps) was seven tool descriptions long** (382 characters in English,
  239 in Japanese), which broke the 110-character inline-annotation guard
  (`L10nTests.SettingsHintFields_StayShort_InBothCatalogs`) and so left
  EditMode tests red on `main` since v0.54.0. The inline line is now one
  sentence and the tool-by-tool detail is the row's hover tooltip
  (`SettingsUapOpsModuleMeshTooltip`), the split every other long
  Settings annotation got in
  `docs/design-notes/2026-08-04-settings-annotation-load.md`. Design note
  `docs/design-notes/2026-09-15-mesh-ci-fixes.md` section 2.1.

## [0.54.0] - 2026-09-15

### Added

- **A "Mesh creation" module row in Settings > Unity operations
  (UapOps)**, for the mesh tools that ship in Agent Panel Pro
  (`uap_mesh_create` / `uap_mesh_inspect` / `uap_mesh_edit` /
  `uap_mesh_boolean` / `uap_mesh_validate` / `uap_mesh_repair` /
  `uap_scene_zfight_scan`, design notes
  `docs/design-notes/2026-09-15-mesh-create.md`,
  `2026-09-15-mesh-edit-sdf-boolean.md` and
  `2026-09-15-mesh-validate-repair-zfight.md`). The module is `mesh`
  and defaults OFF like `anim`, `ui`, `authoring`, `avatar`, `batch`,
  `tests` and `fx`; with Core alone its toggle is disabled with the
  usual "requires the separately sold Agent Panel Pro package" hint. It
  is its own row rather than an addition to `editor` for the reason the
  `authoring` and `fx` rows were: a module toggle is a statement about
  what the agent may make, and the row's one line has to say it.
  Core's own behaviour is unchanged -- `uap_scene_create_object` still
  makes Unity's six primitives exactly as before.

## [0.53.1] - 2026-09-15

### Changed

- Settings > Extension profiles: the "bundled profiles ship in Agent Panel
  Pro" tooltip (EN/JA), the README and the user guide now list ProBuilder
  among the bundled profiles (pro-v0.10.0 adds it). Design note
  `docs/design-notes/2026-09-15-probuilder-profile-and-mesh-route.md`.
  No behaviour change in Core.

## [0.53.0] - 2026-09-15

### Added

- **The script validation gate now tells the agent about staging up
  front.** When the gate is on, the system prompt carries two lines saying
  that `*.cs` / `*.asmdef` never go into `Assets/` directly, that they are
  written under `UapStaging/` (mirroring the intended `Assets/` sub-path)
  and moved in by `uap_scripts_commit`, and that no compile or refresh is
  to be triggered by hand for them. Until now the agent learned about
  `UapStaging/` only from the deny message after its first `Assets/` write
  failed, once per session, compaction or resume, which read as "the agent
  keeps ignoring staging" (design note
  `docs/design-notes/2026-09-15-script-gate-steering-and-powershell.md`).
  The gate itself is unchanged as the enforcement; the text is empty when
  the gate is off.

### Fixed

- **The gate now catches script writes made through the CLI's PowerShell
  tool.** On Windows the CLI's shell tool is named `PowerShell`, and the
  can_use_tool pre-filter compared the tool name against `Bash` alone, so
  `Set-Content -Path Assets/Foo.cs ...` went through ungated. Bash,
  PowerShell and Shell are now treated alike, and the write-target scan
  understands `Set-Content` / `Add-Content` / `Out-File` / `New-Item`
  (named `-Path` / `-LiteralPath` / `-FilePath` or the first positional
  argument), `Copy-Item` / `Move-Item` (`-Destination` or the second
  positional argument, so moving a script OUT of `Assets/` is not gated)
  and `[IO.File]::WriteAllText` / `WriteAllLines` / `WriteAllBytes`. The
  generated PreToolUse hook matches `Bash|PowerShell` too and applies the
  same scan to the command, so the gate holds even when a user's
  "always allow" rule lets the CLI skip the permission round trip.
- **The gate's "blocked a direct script write" note appears next to the
  denied call.** It was appended as a separate message after the whole
  assistant turn, so it showed up below tool calls that ran later and
  looked as if the gate had fired late.

## [0.52.0] - 2026-09-15

### Added

- **`uap_rect_transform_set` now has a World Space canvas path**, for VR and
  other in-world UI (design note
  `docs/design-notes/2026-09-15-rect-transform-layout-tool.md`, section "VR
  (World Space Canvas) の経路"). A Screen Space canvas has its root rect
  recomputed from the screen by Unity every frame, so writes to it are
  discarded; a World Space canvas is an ordinary authored object whose rect
  sticks. v0.51.0 did not tell the two apart and warned that writes "will
  not stick" for *every* root Canvas -- which meant the most ordinary VR
  edit there is was reported as ineffective while it was in fact working.
  The governing Canvas (nearest at or above the element) is now found and
  its render mode read, and that warning fires only when the mode is known
  to be a screen-space one. A render mode that cannot be read counts as
  unknown, never as screen space, so the false warning cannot come back by
  way of a failed probe.
  The read-back gains `canvas` (`path` / `type` / `renderMode` /
  `worldSpace` / `scope`, or null outside any Canvas) and `worldSize` -- the
  resolved rect scaled by `lossyScale`, which for a World Space canvas is
  metres, and in VR is what decides whether a panel is readable and
  reachable at all.
  `worldSize` is also writable, so a panel can be sized the way VR work
  actually describes it ("1.2m wide") instead of in canvas-local
  `sizeDelta`: the tool divides by `lossyScale` and subtracts the span the
  anchors already imply, which is the step that makes a stretched child
  need `sizeDelta.x` of -500 rather than 500 -- exactly the arithmetic this
  tool exists to keep out of the agent's head. It is mutually exclusive
  with `sizeDelta` and the offsets, and a `lossyScale` of 0 on an axis is
  refused before any write rather than dividing by zero.
  `keepRect` and the anchor math are unchanged: they work in parent-rect
  space, which does not depend on the render mode, so VR needed no separate
  branch there.
  The Canvas is probed by type name and reflection, like the layout
  drivers, so the package still compiles for a project that has stripped
  the built-in UI module.

## [0.51.0] - 2026-09-15

### Added

- **`uap_rect_transform_set`** -- reads or writes a uGUI RectTransform's
  anchor-relative layout in one call: `anchorMin` / `anchorMax` / `pivot` /
  `anchoredPosition` / `sizeDelta` / `offsetMin` / `offsetMax` (design note
  `docs/design-notes/2026-09-15-rect-transform-layout-tool.md`).
  `uap_transform_set` writes `localPosition` / `localEulerAngles` /
  `localScale` and stops there, which on a RectTransform writes almost
  nothing that lasts: `localPosition` is a value Unity recomputes from the
  anchors every layout pass, so an agent that moved an anchored element
  with it watched the move revert and had no way to tell a silent failure
  from a broken tool. The only correct route was `uap_property_set`, one
  serialized property per call -- up to five of them -- preceded by anchor
  math (parent rect size, anchor fractions, resulting offsets) the agent
  had to do by hand, which is exactly what sends it to dynamic code
  instead. Rotation and scale deliberately stay with `uap_transform_set`;
  the line between the two tools is "anchor-relative or not".
  Every field takes a partial `{x}` / `{y}` and merges with the current
  value. Anchors and pivot are written **raw**, the way Unity's own setters
  behave -- nothing compensates the way the Inspector's anchor widget does,
  so they move the visible rect -- and `keepRect: true` covers the common
  case by preserving the rect exactly across an anchor and/or pivot change.
  `preset` names the 16 cells of the Inspector's anchor grid as
  `<vertical>-<horizontal>` (`top-left` ... `stretch-stretch`) and sets the
  pivot too, with `keepPivot: true` to opt out. Redundant combinations are
  rejected rather than silently ordered -- `offsetMin`/`offsetMax` are the
  same four numbers as `anchoredPosition`/`sizeDelta`, and picking a winner
  would mix axes from both -- and all validation runs before any write, so
  a rejected call leaves the rect untouched. The read-back adds `rect` (the
  resolved size, which is not `sizeDelta` once an axis is stretched),
  `parentRect`, any `LayoutGroup` / `ContentSizeFitter` that will overwrite
  the write, and a `warnings` array. Omit every write field for a pure
  read. In the `core` module, undoable, prefab-stage guarded, and **not**
  auto-approved as read-only.

## [0.50.0] - 2026-09-15

### Added

- **A "Profile authoring" module row in Settings > Unity operations
  (UapOps)**, and `ToolRegistry.AllNames()` behind it. Both exist for the
  Extension Profile authoring tools that ship in Agent Panel Pro
  (`uap_profile_scaffold` / `uap_profile_validate`, design note
  `docs/design-notes/2026-09-15-profile-authoring-tools.md`): the module is
  `authoring`, defaults OFF like `anim` and `ui`, and with Core alone its
  toggle is disabled with the usual "requires the separately sold Agent
  Panel Pro package" hint. `AllNames()` returns every registered tool's
  wire name without asking for a module list, which is what a caller
  checking "does a tool by this name exist" needs -- the profile validator
  reads it to reject an instruction line naming a tool nothing answers to.
  Writing a project profile by hand into `.uap-profiles/*.json` and
  approving its content hash works exactly as before, Core-only included.

- **An "Avatar stats" module row in Settings > Unity operations (UapOps)**,
  for the avatar measurement tool that ships in Agent Panel Pro
  (`uap_avatar_stats`, design note
  `docs/design-notes/2026-09-15-avatar-stats.md`). The module is `avatar`,
  defaults OFF like `anim`, `ui` and `authoring`, and with Core alone its
  toggle is disabled with the usual "requires the separately sold Agent
  Panel Pro package" hint. Core's own behaviour is unchanged.

- **A "Batched calls" module row in Settings > Unity operations (UapOps)**,
  and `UapOpsServer.EnabledModules()` behind it, for the batching tool that
  ships in Agent Panel Pro (`uap_batch`, design note
  `docs/design-notes/2026-09-15-batch-tool.md`). The module is `batch` and
  defaults OFF -- deliberately opt-in, because it changes what a single
  approval covers. `EnabledModules()` returns a copy of the module list
  tools/list currently serves: the module filter is applied when tools/list
  is built, so a tool reached BY NAME through another tool would otherwise
  sidestep a module the user switched off, and the batching tool has to be
  able to check. With Core alone the row is disabled with the usual
  Pro-absent hint and nothing else changes.

- **Settings > Extension profiles now names the installed packages no
  profile covers**, with a button that copies a ready-made request to have
  one drafted. The question a user actually has is which of their installed
  SDKs the agent is flying blind about, and the detected-profile list is
  only the inverse of that. `ExtensionProfileGapFinder` answers it:
  Library/PackageCache's `<id>@<version>` spelling is normalised (otherwise
  the same package reads as both covered and missing), the panel's own
  packages are excluded, and Unity's first-party packages sort last rather
  than being dropped -- Timeline and Cinemachine are reasonable subjects,
  just never the ones you are hunting for among forty. The block appears
  only when Agent Panel Pro's authoring tools are installed to act on it;
  a list of gaps with no way to close them is a complaint, not a feature.
  Known limit, stated in the tooltip: a profile that detects by type name
  rather than package id is not counted, so this is a hint, not a verdict.
  The request goes to the CLIPBOARD rather than into the composer, so the
  card can never overwrite a message the user was part-way through typing.

### Changed

- **The "no bundled profiles installed" tooltip and the docs now name all
  twelve bundled profiles**, not the nine that existed before pro-v0.9.0
  added VRCFury, lilycalInventory and lilToon. Core-only text: the
  profiles themselves ship in Agent Panel Pro, and nothing about writing
  your own `.uap-profiles/*.json` changes.

## [0.49.1] - 2026-09-15

### Changed

- **"Agent Panel Pro updates" moved from the Unity group to Connection and
  account** in Settings, directly under Account (design note
  `docs/design-notes/2026-09-15-panel-ux-followups.md` section 2). The
  Unity group holds Unity-side machinery -- what the agent may touch in the
  editor, which SDKs it recognizes, which helper CLIs are installed. This
  card is none of that: it takes the registry URL and product key a
  purchase came with and writes them to the package manager's credentials,
  which is the same subject as the sign-in card it now follows. It was also
  hard to find where it was, which a card a buyer reaches exactly once,
  right after paying, cannot afford. The section's collapse state carries
  over -- its id did not change.
- **A long settings tooltip no longer pops from anywhere on its row.** Once
  a row carries the "?" mark, the paragraph moves onto the mark: hover or
  click the "?" to read it, in a popover where it wraps (design note
  section 3). Settings tooltips are set on a field's whole scope so that
  hovering either the label or the control shows them, which also made the
  hover target the size of the row -- reading down the settings page
  dropped a paragraph-sized balloon over the next control every time the
  pointer crossed one. Short captions ("applies on the next reconnect")
  never earn a mark and hover exactly as before. The mark threshold is now
  measured in display width rather than characters, so a Japanese sentence
  earns a mark at half the character count an English one needs -- in
  Japanese the longest explanations were the ones missing their mark.

### Fixed

- **The "ask the agent to fix these errors" chip no longer flashes up on
  every compile** (design note section 1). Compiler errors were published
  from `assemblyCompilationFinished`, which fires once per assembly *in the
  middle of* a run, so the chip showed a run's partial results and then
  cleared itself -- when the same refresh queued another run, or when a
  successful compile reloaded the domain. A warning that goes away on its
  own is not a warning. A run's compiler errors are now held until
  `compilationFinished` and published in one batch, a run superseded before
  it finishes publishes nothing, and the chip (with the empty-state "fix
  the console errors" suggestion) stays down while a compile is in flight.
  What the panel captures is unchanged, so the post-compile verdict sent to
  the agent and the ignore list behave exactly as before.
- **`uap_scripts_commit`'s validation build no longer feeds the error
  chip.** Staged scripts are compiled outside the project before anything
  is moved into `Assets/`; whatever that build reports goes back to the
  agent verbatim in the tool result and names files under `UapStaging/`,
  which the user cannot open in Unity. Those diagnostics are now kept out
  of the panel's captured errors entirely.

## [0.49.0] - 2026-09-15

### Added

- **`uap_editor_select`** -- sets the Editor's object selection, so a menu
  command that acts on "the current selection" gets the right target
  (design note `docs/design-notes/2026-09-14-selection-set-tool.md`).
  `uap_editor_execute_menu` shipped as the universal lever for third-party
  extensions that expose no other stable API, but a great many of the menu
  items worth driving take no argument at all and read `Selection` instead
  -- NDMF's and Modular Avatar's "Manual bake avatar", the VRChat SDK build
  panel, UniVRM's exporters, Bakery's selected-scope bake, RPG Maker
  Unite's editors. Until now the panel could only read the selection
  (the context bar), never set it, so any of those was a step the agent had
  to ask a human to perform by hand mid-task, with no way to verify they
  had. Takes one of `path` (one scene object), `paths` (several, max 256),
  `assetPath` (a project asset) or `clear: true`; every path is resolved
  before `Selection` is touched, so a bad path throws with the previous
  selection intact instead of leaving a half-applied one for the next call
  to act on. A single target lands in `Selection.activeGameObject`
  specifically, which is the field those menu items read. Returns what
  ended up selected. In the `editor` module, and **not** auto-approved as
  read-only: it writes no project or scene data, but it decides what the
  next call acts on.

### Fixed

- **Extension-profile detection now sees VPM/embedded packages.** A
  profile's `packageIds` were only matched against `Packages/manifest.json`
  dependencies and `Library/PackageCache` directory names. VCC / ALCOM
  install a VPM package by copying it into `Packages/<id>/` and recording
  it in `Packages/vpm-manifest.json`, so it appears in neither -- meaning
  the package half of detection had never once fired for the VRChat
  ecosystem (the VRChat SDK, NDMF, Modular Avatar, UniVRM), which was
  reaching detection only through the `typeNames` fallback.
  `ExtensionProfileDetectionCache` now reads the `Packages/` directory
  listing alongside `Library/PackageCache`. The `<id>@version` prefix rule
  is unchanged, so a sibling package that merely shares a prefix
  (`nadena.dev.ndmf-experimental` vs `nadena.dev.ndmf`) still does not
  match. Design note
  `docs/design-notes/2026-09-14-ndmf-modular-avatar-profiles.md`.

- **A "Test runner" module row in Settings > Unity operations (UapOps)**,
  for the EditMode test runner that ships in Agent Panel Pro
  (`uap_test_run`, design note `docs/design-notes/2026-09-15-test-run.md`).
  The module is `tests` and defaults OFF -- a test is the project's own
  code and running it really runs it, so it is opt-in like `authoring`,
  `avatar` and `batch`. This row is the one whose disabled state has TWO
  possible reasons, and it says which: `uap_test_run` ships in an assembly
  that only compiles where `com.unity.test-framework` is installed, so a
  user who HAS Agent Panel Pro can still find the row greyed out, and
  telling them to buy Pro would be an answer they cannot act on. With Pro
  present the hint and tooltip name the Test Framework package and where
  to get it; without Pro they read like every other Pro-provided row.
  Core's own behaviour is unchanged.

- **A "Particle systems" module row in Settings > Unity operations
  (UapOps)**, for the ParticleSystem module editor that ships in Agent
  Panel Pro (`uap_particle_set`, design note
  `docs/design-notes/2026-09-15-particle-set.md`). The module is `fx` and
  defaults OFF like `anim`, `ui`, `authoring`, `avatar`, `batch` and
  `tests`, and with Core alone its toggle is disabled with the usual
  "requires the separately sold Agent Panel Pro package" hint. It is its
  own row rather than an addition to `anim` because a module toggle is a
  statement about what the agent may touch: a row labelled "animation"
  that also covered particle systems would make that choice unusable.
  Core's own behaviour is unchanged -- `uap_property_set` reaches a
  ParticleSystem's serialized fields exactly as before.

### Changed

- **Settings > Extension profiles names NDMF, Modular Avatar and Avatar
  Optimizer** in its tooltip and in the "no bundled profiles" note
  (English and Japanese), alongside the SDKs already listed. Agent Panel
  Pro ships those bundled profiles; Core's behaviour is unchanged.

- **The prefab-stage guard's message names the tool that can act on it.**
  Every write tool refuses a call aimed outside an open prefab stage with
  "close prefab mode first", which until now asked for something no tool
  in either package could do. Agent Panel Pro's new `uap_prefab_stage`
  closes it, and the guard now says so. Core's behaviour is unchanged --
  the refusal, and what triggers it, are the same.

- **The "Avatar stats" module row now describes the whole module.** It
  named only `uap_avatar_stats` while the module had grown to also hold
  NDMF baking and, now, VRChat expression-menu editing (English and
  Japanese). No behaviour change; the row, the module id and the default
  are the same.

## [0.48.0] - 2026-09-14

### Added

- **`uap_query_component_types` reports a `kind` for every hit** --
  `component` (attach with `uap_component_add`), `stateMachineBehaviour`
  (attach with Agent Panel Pro's new `uap_animator_behaviour`; these are
  NOT components) or `scriptableObject` (create with `uap_asset_create`).
  A StateMachineBehaviour derives from ScriptableObject, so this search
  already returned VRChat's `VRCAnimatorTrackingControl` and friends, but
  nothing in the result said which tool could actually use them -- the
  natural next call was `uap_component_add`, which fails with a bare
  "Unknown component type". Existing fields are unchanged, so the added
  field breaks nothing. `UapComponentTypeResolver` also gained a
  StateMachineBehaviour-only resolver, used by the Pro package's animator
  tools. Design note
  `docs/design-notes/2026-09-14-animator-layers-blendtree-behaviours.md`.


## [0.47.1] - 2026-09-14

### Changed

- **"Continue automatically after an interruption" no longer claims a
  three-in-a-row cap.** The Settings tooltip (English and Japanese) and
  the user guide still described the limit that v0.36 removed (design note
  `docs/design-notes/2026-09-10-auto-approve-all-tools-and-lean-auto-continue.md`
  section 3); the behaviour did not change. Both now say what actually
  stops the automatic continuation: the crash-loop suspension after
  repeated CLI exits, or your own Interrupt. Core and Pro share this code
  path; Pro adds no reload logic of its own.
- **User guide screenshots.** `docs/USER-GUIDE.md` now carries captures
  for the setup cards, the panel layout, chips and attachments, the
  permission card, the auto-approve menu, the question card, tool and
  subagent cards, the history browser, the model picker, slash-command
  completion, the Unity-tools settings, the resume banner and the
  reconnect banner (`docs/images/guide/`, captured with the
  `ci/shop-images/` kit on Unity 6000.0).

## [0.47.0] - 2026-09-13

### Added

- **History for ACP agents** (design note
  `docs/design-notes/2026-09-13-acp-feature-parity.md`, section 1). The panel
  now keeps its own copy of every Gemini CLI / Codex / Grok Build / custom
  ACP session under `UserSettings/AgentPanel/Sessions/<id>.json`
  (`PanelSessionStore`, same format as the session cache), and History lists
  them next to Claude Code's transcripts with the agent's name on the row.
  Opening one resumes it through `session/load`; an agent that cannot resume
  starts a new session, says so in the transcript, and receives the
  conversation so far with your next message. The stored file follows the
  new id, so an agent without `session/load` no longer leaves one row per
  reconnect. Opening a Claude transcript while another agent is selected now
  records the owner, so the existing cross-backend handover applies instead
  of feeding the other agent an id it never issued.
- **In-panel sign-in for Codex and Grok Build** (section 2). Settings >
  Account gains a Sign in button that runs the agent's own login command
  (`codex login` / `grok login`) inside the panel: the browser link appears
  with Open browser / Copy, the command's last output line is shown, Cancel
  kills it, and the panel reconnects when it exits. When the bridge reports
  a failed sign-in for such a backend, the chat shows a "Sign in to {agent}"
  card with the same button and a terminal fallback. The former
  "Sign in / Reconnect" button is now "Reconnect".
- **Subagent model settings for ACP agents** (section 3). "Force subagent
  model" and the per-type overrides are sent to an ACP agent as instructions
  at the start of each new session (ACP has no equivalent of
  `CLAUDE_CODE_SUBAGENT_MODEL` / `.claude/agents`), the cost-policy line is
  worded without Claude's tool and model names, and the Model section says
  so while an ACP backend is selected.

### Fixed

- **Thinking blocks with ACP agents** (section 4) kept their order: a thought
  streamed after a paragraph of text was folded ahead of that text. The
  text is now folded into its own message first. The "Show thinking
  blocks" tooltip and the ACP limitations hint no longer call thinking
  blocks, History, in-panel login or the subagent model settings Claude
  Code only.

## [0.46.0] - 2026-09-13

### Added

- **File-change view on Write/Edit/MultiEdit tool cards** (design note
  `docs/design-notes/2026-09-13-toolcard-vertex-limit.md`, section 4).
  Expanding a completed file tool now shows the target path and the
  change as `+`/`-` lines in the approval card's colors -- Edit and
  MultiEdit reuse the same diff the permission card showed, Write
  renders its content as an all-added block -- instead of the raw input
  JSON with its escaped `\n` content. Long changes start at 40 lines
  with a "Show all N lines" button; a Write card has a Copy button that
  puts the raw file content on the clipboard.

### Fixed

- **Tool card details rendered blank for large inputs** (same design
  note, sections 1-3). Expanding a Write (or any tool whose input or
  result exceeded roughly 16,000 characters) logged
  `A VisualElement must not allocate more than 65535 vertices` and drew
  nothing, because the whole text sat in one Label. Tool card sections,
  markdown code blocks and attachment payloads now stack one element
  per 8,000-character chunk (`LongTextChunker`); a section past 200,000
  characters shows the first part with a "... N more characters" footer
  and a Copy button carrying the full text.

## [0.45.0] - 2026-09-13

### Added

- **Add to VCC / ALCOM** button on the Agent Panel Pro updates card
  (design note `docs/design-notes/2026-09-12-pro-update-delivery.md`,
  Phase 2). The update registry now also serves a VPM repository
  (`<registry origin>/vpm/index.json` plus per-version zips) behind the
  same product key, so VRChat Creator Companion and ALCOM users can install
  and update Pro from their package manager. The button opens a
  `vcc://vpm/addRepo` deep link carrying the listing URL and the key as
  the repository's `Authorization` header; the key comes from the key
  field, or is read back from `.upmconfig.toml` after a Save key. The
  status line shows the listing URL for a manual add and never the key.

## [0.44.0] - 2026-09-12

### Added

- **Settings > Agent Panel Pro updates** (design note
  `docs/design-notes/2026-09-12-pro-update-delivery.md`, Phase 1). Paste the
  registry URL and the product key that came with a Pro purchase once and
  press Save key: the panel writes the token into Unity's own credential
  file (`~/.upmconfig.toml`, or `UPM_USER_CONFIG_DIR`) as an
  `[npmAuth."<registry>"]` block, adds the registry to the project's
  `Packages/manifest.json` as a scoped registry for
  `jp.colloid.agent-panel-pro` (an existing entry at the same URL just gains
  the scope; another registry already claiming the scope is reported, not
  clobbered), then asks the Package Manager to resolve. From then on Pro
  updates through Window > Package Manager > My Registries like any other
  package. The panel keeps no copy of the key; the URL is stored in settings
  (`proRegistryUrl`) and is not a secret. The key field is cleared after a
  successful save.

## [0.43.0] - 2026-09-12

### Added

- **Pictures a tool returns now show up in the conversation** (design note
  `docs/design-notes/2026-09-12-tool-result-image-preview.md`). A
  `uap_editor_screenshot` capture (with or without `return_image:true`),
  a `Read` of a PNG, or an image an MCP generation tool hands back used to
  be visible to the model only; the tool card now carries a thumbnail
  strip under its header, outside the collapsible details, one thumbnail
  per picture (click opens the file, file name and pixel size as the
  caption, a "no longer available" placeholder once the store's 7-day
  retention removed it). Embedded image blocks are decoded into
  `Library/AgentPanel/Attachments` like a pasted image; a PNG/JPEG path
  the result text names is shown when the file exists. Cards with a
  picture stay out of the "N tools" fold, the pictures survive the
  session cache and history restore, and ACP agents' image content is
  forwarded as a picture instead of the `[image]` stand-in text.

### Fixed

- **Tool cards for array-shaped results show a Result section.** Every
  MCP tool (and `Read` on an image) returns `content` as an array; the
  live path only summarized string results, so those cards opened to an
  Input section alone. The first text block is now the summary, the same
  rule history restore already applied.

## [0.42.5] - 2026-09-12

### Changed

- **Settings explain the Core/Pro line to someone who only has Core**
  (design note `docs/design-notes/2026-09-12-core-only-wording.md`). A
  module toggle whose tools ship in the separately sold Agent Panel Pro
  add-on now keeps its own "what these tools do" hint and appends
  "Requires the Agent Panel Pro add-on (sold separately; not installed)."
  instead of the bare "Provided by Agent Panel Pro (not installed).", with
  a hover tooltip on the row saying what Pro is, that the panel is
  complete without it, and that the toggle comes back after the next
  domain reload once Pro sits under `Packages/`. The Extension profiles
  card no longer claims "No supported SDK detected" when there was
  nothing to detect with: with no bundled profile installed it reads "No
  bundled SDK profiles are installed. Project profiles in
  `.uap-profiles/*.json` still work." (tooltip names the SDKs whose
  bundled profiles ship with Pro), and its tooltip now says bundled
  profiles come from add-on packages. README / README.en / USER-GUIDE mark
  every Pro-only feature with **(Pro)** in the feature list, state that
  everything unmarked works with Core alone, and describe what the
  Settings show without Pro.
- **Every link a Core user can reach now points at the public repository**
  (`https://github.com/c-colloid/AgentPanelForUnity`): the README install
  URL and `git clone` line, the Package Manager documentation page, the
  USER-GUIDE's issue-tracker link and the GitHub button in Settings > About
  used to open the private development monorepo -- a 404 for anyone who
  installed the package from the mirror. `docs/README.md`, CONTRIBUTING and
  the architecture document now say which folders and features exist only
  in the monorepo (Pro, `ci/`, `docs/research/`, `docs/verify/`), and the
  agent steering text no longer mentions `uap_lightmap_bake` /
  `uap_bakery_bake` when those Pro tools are not registered.
- **A "UI automation" module toggle (default OFF) joins Settings > Unity
  operations (UapOps).** The `ui` module (UI Toolkit window list / dump /
  click / set value, `uap_editor_ui_*`) has existed since Phase 5c and is
  documented in USER-GUIDE section 12, but never had a switch; it now has
  one shaped like Anim, disabled with the Pro-absent hint when the add-on
  is not installed.

### Fixed

- **The question in an AskUserQuestion card is readable again** (design
  note `docs/design-notes/2026-09-12-askuserquestion-prompt-visibility.md`).
  Since the summary row started wrapping (v0.28.0 review fix), the
  question text -- the first row INSIDE the scrolling details, body size,
  regular weight, identical to the option labels -- left the 40 percent
  inline cap's viewport after one scroll step and was hard to tell from
  its own options even before that. The current question's header and
  text now sit in a pinned block (`uap-perm-qprompt`, never shrinks)
  between the summary row / tab strip and the scrolling options, the
  text is bold, and with 2+ questions the block follows the stepper
  (the header is left to the tab that already shows it).
- **A question card holds its size while the conversation is long.**
  Measured on a real editor: once the transcript was taller than the
  panel, the expanded AskUserQuestion card never reached its 40 percent
  cap -- Yoga shrank it to the tool card's 96px floor, so a three-question
  card at a 560px-tall panel was 138px with an 11px options viewport and
  zero options visible. The question variant now carries
  `uap-perm--question`: a 220px floor (summary + three-line question +
  two options; still leaves the transcript its own floor at the 380px
  inline minimum) and no shrinking (the cap already bounds it). Tool cards
  are unchanged.
- **The "Compacting the context..." row no longer paints over its
  neighbours** (design note
  `docs/design-notes/2026-09-12-compacting-row-crush.md`). With a long
  transcript in a short panel the row was the element Yoga shrank (24px
  to 16px, its 16px label to 9px), so its text bled into the message list
  above and the context bar below. It now keeps `flex-shrink: 0`; the
  message list, which has its own minimum, gives the height back.
- **Pro-only module toggles were clickable with Pro absent.** The
  Settings card disabled them when built, but `RefreshUapOpsStatus`
  (called at the end of the same build and on every master-switch
  change) re-enabled every module switch from the master switch alone.
  A switch now stays greyed while no package registers tools for its
  module; the Scene-view markers switch also greys out with the master
  switch like the others.

## [0.42.4] - 2026-09-12

### Fixed

- **`uap_scripts_commit` no longer rejects a file that Unity itself
  compiles.** The gate's `AssemblyBuilder.defaultReferences` carries a
  project plugin's *runtime* DLL but drops its *editor-only* half (e.g.
  `LibForUniteForEditor.dll`), so an Editor script whose base type lives
  there failed with CS0012 in the gate while `Assembly-CSharp-Editor`
  built clean. `GatherAdditionalReferences` now sweeps
  `CompilationPipeline.GetPrecompiledAssemblyNames()` and appends every
  precompiled assembly whose file name is missing from the default set
  (never a duplicate, so the CS0433 shape cannot come back). Design note:
  `docs/design-notes/2026-09-12-scripts-commit-editor-plugin-refs.md`.

## [0.42.3] - 2026-09-12

### Fixed

- **The conversation no longer disappears when Unity compiles.** Reported
  as "every compile switches the panel to a new session and the history is
  gone", and measured in the wild: six `Sharing violation` failures in one
  editor session, one per domain reload, every one of them on
  `SessionCache.json` right after the pre-reload save had rewritten it.
  Three things were wrong and all three are fixed (design note
  docs/design-notes/2026-09-12-session-cache-transient-read-failure.md):
  - `AtomicFile.ReadAllText` now opens with
    `FileShare.ReadWrite | FileShare.Delete` and briefly retries (MODEL-3).
    The old `File.ReadAllText` refused to open a file that any other handle
    held with WRITE access -- which is exactly what an antivirus, search
    indexer or sync agent does to a file we just replaced. Every sidecar
    (session meta, quick actions, custom instructions, the Ops generated
    files) reads through this helper, so all of them gain it.
  - **A cache that could not be READ is never overwritten.**
    `SessionCacheFile.Load` now reports *why* it returned null, and every
    save in `AgentHub` goes through a single guarded path, so a lock that
    lasts milliseconds can no longer be turned into permanent transcript
    loss by the next save.
  - The panel recovers within the same domain: the load is retried and the
    restored transcript is spliced back in front of whatever arrived while
    the cache was unreadable (nothing is dropped). If it still cannot be
    read, the transcript now says so, instead of leaving an empty panel
    that is indistinguishable from deleted history.

## [0.42.2] - 2026-09-11

### Fixed

- **Unity 6.4 / 6.5 compile**: `Object.GetInstanceID()` is obsolete from
  6.4 and an error from 6.5 (replaced by `EntityId`), and the
  `FindObjectsSortMode` overload of `FindObjectsByType` is obsolete from
  6.4. Both are now spelled once in the new `Editor/Ops/UnityObjectCompat.cs`
  (`UnityObjectCompat.FindAll<T>()` and the version-neutral
  `UnityObjectId` identity struct, `EntityId` on 6.3+ and the instance id
  before) and used by `FontLoader`, `UapScreenCapture`, `SceneMarkerPin`
  and the tests, so no other file carries a version conditional. CI now
  also runs the suite on 6000.3.24f1 and 6000.5.11f1 (design note
  docs/design-notes/2026-09-11-unity-6-support.md, "Unity 6.3 / 6.4 / 6.5").

## [0.42.1] - 2026-09-11

### Changed

- **Unity 6 (6000.x) is now a supported and CI-verified target** (design
  note docs/design-notes/2026-09-11-unity-6-support.md). The obsolete
  `Object.FindObjectOfType`/`FindObjectsOfType` calls in
  `UapScreenCapture` and `SceneMarkerPin` (and the test suite's scene
  cleanup) were replaced with `FindFirstObjectByType`/`FindObjectsByType`
  so the package compiles without deprecation warnings on Unity 6; every
  editor-internal reflection site (`LogEntries.GetCountsByType`,
  `HandleUtility.IntersectRayMesh`, `Clickable.Invoke`,
  `SceneProvider.InvalidateScene`) was checked against the 6000.0 source
  and is unchanged. The EditMode workflow now runs the whole suite on
  both 2022.3.22f1 and 6000.0.83f1. The minimum stays 2022.3 LTS.

## [0.42.0] - 2026-09-11

### Added

- **Two provider seams let an add-on package register into Core without
  Core depending on it** (design note
  docs/design-notes/2026-09-11-core-pro-split.md): `IUapToolProvider`
  (`Editor/Ops/IUapToolProvider.cs`) is discovered via
  `UnityEditor.TypeCache` by `ToolRegistry.CreateDefault` and registers
  every tool a provider yields through the same duplicate-name/uLoop-
  coverage path built-in tools go through (a pure `RegisterProviderTools`
  overload lets tests verify this without TypeCache); `IExtensionProfileProvider`
  (`Editor/Ops/Profiles/IExtensionProfileProvider.cs`) is discovered the
  same way by `ExtensionProfileCatalog.LoadBundled`, which now merges
  Core's own (now-empty) profiles directory with every provider's
  directory, deduplicated by profile id.
- **A UapOps module toggle whose tools are all provided by an absent
  add-on package is disabled with a one-line hint** ("Provided by Agent
  Panel Pro (not installed)." / 「Agent Panel Pro(未導入)で提供。」) instead
  of silently doing nothing, via the new `ToolRegistry.HasToolsInModule`
  query against the live registry.

### Changed

- **The advanced UapOps tools and the five bundled Extension Profiles
  moved out of this package into the separate, proprietary
  `jp.colloid.agent-panel-pro` package** (design note
  docs/design-notes/2026-09-11-core-pro-split.md): the entire `prefab` and
  `anim` modules, the entire `ui` module, the `uap_lightmap_bake`/
  `uap_bakery_bake` tools from the `editor` module (`uap_editor_screenshot`/
  `uap_editor_execute_menu` stay here), and the bundled Extension Profiles
  for Bakery, Final IK, Magica Cloth 2, UniVRM and VRChat SDK3. Installing
  `jp.colloid.agent-panel-pro` alongside this package restores every one
  of them with no behavior change; this package alone no longer includes
  them.

## [0.41.0] - 2026-09-10

### Added

- **The Account card names the sign-in method an ACP agent actually
  connected with** (design note
  docs/design-notes/2026-09-10-acp-auth-guidance-and-method-display.md
  section 4). With Gemini CLI, Codex or Grok Build selected, Settings >
  Account now reads "Signed in with: ..." for as long as the connection
  lasts, and falls back to "Connected with a saved sign-in." when the
  agent opened the session without an `authenticate` round trip (the
  method is genuinely not knowable then, so the panel does not guess).
  When the method that got in is an API key or gateway one, the card
  adds that usage is billed to that key rather than to a subscription,
  and the transcript gets the same heads-up once per connection --
  mirroring what v0.40.0 does for Claude Code's `apiKeySource`.

### Changed

- **Codex and Grok Build now state their API key path alongside the
  subscription one** (same design note, section 3) in the Settings >
  CLI hint, the first-run card, the README and the user guide: Codex
  takes a ChatGPT account or `CODEX_API_KEY` / `OPENAI_API_KEY`, Grok
  Build a SuperGrok / X Premium+ sign-in or `XAI_API_KEY`. The
  subscription path is listed first as the recommended one. In Settings
  and the first-run card the sign-in paths are one short inline line and
  the reasoning is a hover away, per the repository's inline-hint rule;
  every place that mentions a key also says that the panel never stores
  API keys -- they belong in the agent CLI's own environment variable or
  config file.

### Fixed

- **The Gemini CLI preset no longer claims a Google account works**
  (same design note, sections 2 and 3). Google ended "Login with
  Google" for Gemini Code Assist for individuals, Google AI Pro and
  Google AI Ultra on 2026-06-18, leaving individuals with an API key
  (or Vertex AI) only. The panel described the backend as "Google
  account (AI Pro/Ultra) or free tier", which was simply wrong, and the
  sign-in-failed note only suggested re-running the browser login --
  advice that cannot succeed for an individual account. Settings, the
  first-run card and the docs now describe the real paths (a Gemini API
  key in `GEMINI_API_KEY` or `~/.gemini/.env`, or the Google sign-in of
  Gemini Code Assist Standard / Enterprise, which needs a Google Cloud
  project), with the deprecation itself explained in the hover tooltip,
  and when every sign-in
  method the agent offers has failed the transcript note names the
  environment variable, where it goes, and that an OS variable needs
  the editor restarted before it is picked up.

## [0.40.0] - 2026-09-10

### Added

- **API key authentication is no longer blocked** (design note
  docs/design-notes/2026-09-10-claude-api-key-auth-passthrough.md):
  Anthropic's Claude Code legal terms require that a product running the
  CLI not remove, disable or restrict any of its built-in authentication
  methods, including the user's own API key. A new Settings > Account
  control, "API key authentication" (`PanelSettings.claudeAuth`), lets
  you choose between **Auto** (default: `ANTHROPIC_API_KEY` is left
  completely untouched, so the CLI authenticates exactly as it would in
  a terminal) and **Subscription only** (removes the variable so the
  stored subscription login is always used, matching the panel's
  previous behaviour). When the CLI reports an API key is actually
  authenticating the session, the Account card shows a note ("Connected
  with an API key (...) -- usage is billed to that key, not to a
  subscription...") and the chat transcript gets a one-line heads-up the
  first time it happens each session.

### Changed

- **`ANTHROPIC_API_KEY` is no longer stripped by default** (same design
  note as above): previously the panel always removed
  `ANTHROPIC_API_KEY` from the spawned CLI's environment; it now only
  does so when "API key authentication" is explicitly set to
  Subscription only. `CLAUDECODE`, `CLAUDE_CODE_ENTRYPOINT` and
  `CLAUDE_CODE_SESSION_ID` are still always removed. The "never set
  ANTHROPIC_API_KEY" warnings in the README, user guide and first-run
  screen were replaced with neutral wording: subscription login is
  recommended, an API key in the environment is respected, and the
  panel shows which one is active.

## [0.39.2] - 2026-09-10

### Changed

- **Product name is now "Agent Panel for Unity"** (design note
  docs/design-notes/2026-09-10-product-name-agent-panel-for-unity.md): the
  Package Manager display name, the empty-state title, the ACP
  `clientInfo.title`, the standing-instructions heading, the script gate's
  refusal message and the READMEs use the new name. Identifiers are
  unchanged -- the package id `jp.colloid.unity-agent-panel`, the
  `Colloid.AgentPanel` namespace, the `Window > Agent Panel` menu and the
  GitHub repository URL (so install URLs keep working).

## [0.39.1] - 2026-09-10

### Fixed

- **Token and context usage with ACP backends** (design note
  docs/design-notes/2026-09-10-acp-usage-display.md): with Codex
  (codex-acp), Gemini CLI or another ACP agent the status bar always read
  "0 tok" and the context meter never appeared, because the bridge filled
  `result.usage` with zeros. It now translates the agent's ACP
  `usage_update` (tokens in context, context window size, USD cost) and
  `PromptResponse.usage` -- falling back to the `_meta.quota.token_count`
  extension Gemini CLI and codex-acp write -- into the result's usage,
  `modelUsage` (context window on the current model, Gemini's per-model
  rows for the popover) and the live reading on assistant lines, so the
  token counter, the context meter and the usage popover work as with
  Claude Code. Grok Build, which reports through `_meta` instead (the
  model catalog's `totalContextTokens`, `totalTokens` on every
  notification and the whole-prompt `usage` bill on the prompt response),
  is read the same way. Codex reports its last model call per turn; Gemini
  CLI sends no context size, so its meter stays hidden and only tokens
  show.
- **The UI no longer calls every agent "Claude"** (design note
  docs/design-notes/2026-09-10-agent-name-in-ui.md): with another
  backend selected, the assistant role label, the empty-state line, the
  composer placeholder, the "Ask Claude to fix" button, the permission
  card/window titles and hints, the reconnect tooltip and the settings
  hints that address the agent now use the selected backend's name
  (Gemini / Codex / Grok; a custom ACP agent's own name once it has
  introduced itself, else "Agent"). Catalog strings carry an `{agent}`
  placeholder that `L10n.F` and the new `L10n.A` expand at render time,
  so switching backends is reflected on the next repaint. The spark
  mark in the header, the empty state and the reply role rows, and the
  agent accent colour behind it, follow the backend too (Gemini diamond
  / Codex ring / Grok X / custom outline, each with its own accent).
  Claude-only sections (Unity plugin, logout, subagent model policy,
  script gate) keep saying Claude Code.
- **Endless "process exited, reconnecting" after switching agents
  mid-conversation** (design note
  docs/design-notes/2026-09-10-backend-switch-session.md): the reconnect
  handed the previous agent's session id to the new one, and Claude Code
  exits on an unknown `--resume` id, while the crash counter was reset by
  every short-lived Ready, so the loop never ended. A session now records
  the backend that issued it; starting a different backend drops the
  resume, and the conversation is not lost: the first message to the new
  agent carries the transcript so far (user and assistant text, one line
  per tool call, newest 24k characters) so it can continue where the
  previous agent left off; a note in the chat says so. The crash counter is only reset by a Ready that comes at
  least a minute after the last death or by a completed turn, so a
  process that dies right after starting is suspended after three rounds
  as intended.

### Added

- **Grok Build backend** (design note
  docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md section
  2.5): xAI's Grok Build (SuperGrok / X Premium+ subscription) joins the
  Agent picker as a preset -- `grok agent stdio` over the ACP bridge, the
  official `x.ai/cli/install.sh` / `install.ps1` installer behind the
  Install button, `grok login` as the terminal fallback. The ACP command
  probe now also checks the vendor install directories (`~/.grok/bin`,
  `~/.local/bin`, npm's global bin) after PATH, so a binary installed from
  the panel is found without restarting the editor.

### Fixed

- **ACP sign-in picked the wrong method and then looped** (design note
  docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md section
  2.4). Live report with Codex: codex-acp advertises "API Key" before
  "ChatGPT", the bridge took the first method, it failed
  (`CODEX_API_KEY or OPENAI_API_KEY is not set`), the process exited and
  the hub's auto-reconnect repeated the same failure four times. The bridge
  now ranks the agent's auth methods (explicit setting, then browser/OAuth
  account logins, then device-code, then the rest, key/gateway methods
  last) and falls back to the next method when one fails; a death right
  after a failed sign-in is no longer auto-reconnected -- one note names
  the next step (Sign in / Reconnect, or the agent's own login command).
- An ACP agent that exits before its handshake completes (Grok Build does
  so with AuthorizationRequired when not signed in) is no longer respawned
  three times; one note explains the next step instead.
- The "process exited" notes no longer say "Claude CLI" for other agents.

## [0.38.0] - 2026-09-10

### Added

- **In-panel install and sign-in -- no terminal needed** (design note
  docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md): the
  first-run card and Settings > CLI gain an "Install <agent>" button that
  runs the backend's installer in the background (Claude Code: Anthropic's
  official native installer; Gemini CLI / Codex: `npm install -g`), after
  a confirmation dialog that shows the exact command. Progress and the
  result are shown inline (a missing npm gets a "Get Node.js" button), and
  a successful install reconnects on its own. ACP agents now sign in from
  the panel: the bridge's `authenticate` drives the agent's own browser
  flow, the chat/status bar/account card say "complete the sign-in in
  your browser", a sign-in URL the agent prints is offered as a link when
  no browser opened, and the initialize handshake waits up to 10 minutes
  for it (`AgentClientOptions.InitializeTimeoutSeconds`). The account card
  is shown for ACP backends too, with a "Sign in / Reconnect" button.
  Terminal instructions remain as a collapsed fallback.

### Added

- **Unity official plugin detection (Stream A)** (design note
  docs/design-notes/2026-09-10-unity-official-plugin-integration.md
  section 1): pure detection of Unity's official Claude Code plugin
  (`unity@unity-agent-plugin`) from the two sources a real install writes
  (`~/.claude/plugins/installed_plugins.json`, `settings.json`
  `enabledPlugins`) and from the session's own `system/init` `plugins[]` /
  `plugin_errors[]`, honouring `CLAUDE_CONFIG_DIR` the way the CLI does.
  `SystemInitMessage` now exposes `Plugins` and `PluginErrors`;
  `AgentClientOptions.PluginDirs` composes `--plugin-dir` (unused by
  production today -- measured 2026-09-10: a marketplace-installed plugin
  loads under the panel's exact `-p` argument list without it). The Settings card
  is Stream B below; the steering line is Stream C.

- **Unity official plugin: Settings card with one-click install (Stream B)**
  (same design note, section 2): a new "Unity official plugin" section
  after the uLoop one shows the detector's verdict (not installed /
  disabled / installed / loaded in this chat / not loaded by this chat /
  load error, with the version), an Install button that runs
  `claude plugin marketplace add Unity-Technologies/unity-agent-plugin`
  then `claude plugin install unity@unity-agent-plugin --scope user --yes`
  on a worker thread with a live "Installing... Ns" line (a run
  interrupted by a domain reload re-enters that line from SessionState and
  reads as stalled after five minutes), a details foldout with the
  license and the uninstall command, and the "Tell Claude how to use the
  official skills" toggle whose steering line lands with Stream C.
  Measured 2026-09-10: both stages exit 0 in about four seconds and are
  idempotent, so the card needs no already-installed handling.

- **Unity official plugin: steering line (Stream C)** (same design note,
  section 3): when the plugin is installed in the CLI's user config and the
  card's toggle is on, `--append-system-prompt` gains up to three lines
  between the UapOps steering and the Extension Profiles sections: route UI,
  package and lookup work through the `/unity:*` skills; with UapOps on,
  keep Editor control on the `uap_*` tools and never install or run the
  `unity` CLI / `unity mcp` / `com.unity.pipeline` even when
  `/unity:unity-cli` suggests it; on editors older than Unity 6 (or an
  unparseable version) skip Unity 6-only guidance. Detection is static at
  spawn time; the line applies from the next chat.

- **`uap_search` (Stream D)** (same design note, section 4): runs a Unity
  Search query -- the syntax `/unity:generate-editor-search-query` produces --
  over project assets (`asset`, backed by Unity's non-indexed `adb`
  provider so freshly created assets are found) and/or the open scenes
  (`scene`) and returns hits as the hierarchy / asset paths the other
  `uap_*` tools take as targets, with `totalMatches` and `truncated`.
  Before a scene query the tool invalidates the scene provider's cache
  (measured: it otherwise never sees objects created after its first
  query). The plugin steering line now tells Claude to run such queries
  with `uap_search` instead of opening the Search window.

## [0.37.1] - 2026-09-10

### Fixed

- **The panel no longer looks frozen while the context is being compacted**
  (design note docs/design-notes/2026-09-10-compacting-indicator.md). A
  `/compact` (or the CLI's automatic compaction) is one long summarization
  call with no streamed output, and the panel showed nothing until the
  `compact_boundary` arrived. The CLI's `system/status` line is now mapped
  (`SystemStatusMessage`); while its status is `compacting` -- or from the
  moment the panel itself sends `/compact` -- the status bar reads
  "Compacting context..." and a spinner row is pinned under the transcript.
  The indicator ends with the boundary, an explicit status clear, or the
  turn's own end (result, stall, crash).

## [0.37.0] - 2026-09-10

### Added

- **Agent backends other than Claude Code** (design note
  docs/design-notes/2026-09-10-acp-backends.md): Settings > CLI gains an
  "Agent" picker with Claude Code (default), Gemini CLI, Codex (codex-acp)
  and "Other ACP agent (custom command)". Every non-Claude backend runs
  through a new Agent Client Protocol bridge (`Editor/Core/Acp/`,
  `AcpBridgeTransport` + `AcpProtocolBridge`): the agent is spawned over
  JSON-RPC/stdio and its stream is translated into the Claude stream-json
  dialect the panel already speaks, so chat streaming, thinking blocks, tool
  cards, permission cards ("allow once" / "always allow" / deny+reason),
  interrupt, model switching, custom instructions and the UapOps MCP server
  (registered as an http server with the Bearer token in session/new) all
  work unchanged. Authentication is the agent CLI's own subscription login
  (Google account for Gemini CLI, `codex login` for Codex); the bridge
  answers an "authentication required" session/new with the agent's
  `authenticate` method once and reports failure in the chat. Session
  resume across domain reloads uses `session/load` when the agent
  advertises it.
- Backend-aware first-run card (install command, login hint, command field)
  and a Settings hint listing what stays Claude Code only: the session
  history browser, in-panel login, subagent model settings and the
  script-gate hook.

### Changed

- `ClaudeCliProcess` accepts an optional stdout interceptor (the ACP bridge
  reuses its spawn/kill/orphan-sweep lifecycle); `ZombieReaper` records the
  spawned process name instead of assuming "claude".
- The "process exited" banner no longer names Claude Code.
- **Package README and `Documentation~/index.md` rewritten for public
  release** (design note
  docs/design-notes/2026-09-10-docs-cleanup-for-public-release.md): the
  in-package docs now describe the current layout (including `Editor/Ops`),
  point at the repository README (Japanese and English) with absolute links
  that survive the Package Manager view, and drop the outdated npm-based
  setup steps. No code or behavior change.

## [0.36.0] - 2026-09-10

### Added

- **Auto-approve level "All tools"** (design note
  docs/design-notes/2026-09-10-auto-approve-all-tools-and-lean-auto-continue.md
  section 1): a fifth, never-default level that approves every permission
  request -- Bash, Edit/Write, Read/Glob/Grep, WebFetch, other MCP servers,
  all Unity ops -- except questions the agent asks you (AskUserQuestion),
  which still wait for your answer. The script-validation gate's auto-deny
  still runs first and the end-of-turn non-undoable warning still fires.
  Escalating into it asks for confirmation like "All Unity ops" does. Live
  report: at "All Unity ops" a single user action still produced 10+
  permission cards, because that level never covered the CLI's own tools.
- **Tool-wide "Always allow" entry for every tool** (same note, section
  2): the Always menu now starts with a plain "always allow <Tool>" rule
  for the tool on the card, ahead of whatever the CLI suggested. The CLI's
  suggestions are path- or prefix-scoped (`Read(//project/**)`,
  `Bash(git status:*)`) or a mode switch, so accepting one never stopped
  the next card for the same tool. The `--allowedTools` wire form the
  panel already used ("one value per argument") matches the CLI 2.1.220
  `--help` text ("comma or space-separated list").
- **Permission queue depth on the card** (design note
  docs/design-notes/2026-09-10-permission-queue-depth-and-reload-dropped-permission.md
  section 1): when parallel tool_use blocks queue several `can_use_tool`
  requests behind the one on screen, the card (inline and floating window)
  now says "N more waiting" instead of surprising you with a new card after
  every answer. The single-active model is unchanged; only the depth is
  reported (`AgentHub.PendingPermissionQueueDepth`).
- **Play Mode reload readout in Settings** (same note, section 4): under
  "Continue automatically after an interruption", the UapOps section shows
  whether this project's Enter Play Mode Options will reload the domain on
  Play (and so interrupt a running turn), with a button that opens Project
  Settings > Editor. Read-only; the panel never changes the project setting.

### Changed

- **Auto-continue is leaner** (design note
  docs/design-notes/2026-09-10-auto-approve-all-tools-and-lean-auto-continue.md
  section 3). The continuation sent after a reload shrank from about 600
  characters to two sentences (the "verify before re-running side
  effects" instruction stays), the compile continuation to one line. The
  automatic send no longer adds a user bubble to the transcript; the
  one-line "Auto-continue: sent ..." note is the only trace, and it was
  shortened too. The "at most three interrupted continuations in a row"
  cap is gone: it stopped legitimate commit-reload-continue loops while
  the crash-loop suspension and your own Interrupt already cover a
  runaway. `SessionStateBridge.AutoContinueInterruptedStreak` and
  `AutoContinueAfterCompilePolicy.MaxInterruptedContinuationsInARow` were
  removed.

### Fixed

- **A permission card discarded by a domain reload vanished without a
  trace** (design note
  docs/design-notes/2026-09-10-permission-queue-depth-and-reload-dropped-permission.md
  section 3). The reload teardown correctly drops the pending
  `can_use_tool` (the process that asked is gone), but nothing said so:
  Deny leaves a note, a reload left nothing. The tool's display name is
  now carried across the reload in SessionState, a transcript note reports
  that the request was neither allowed nor denied and the tool did not
  run, and the opt-in interrupted-turn auto-continue message tells the
  agent the same so it re-asks instead of assuming the call happened.
- **The orphaned-grandchild sweep could skip the domain-reload teardown**
  (same note, section 5). It ran only from the process Exited callback,
  which `ClaudeCliProcess.Dispose` unsubscribes -- so on every Stop ->
  Dispose (every reload) whether it ran was a ThreadPool race, and a CLI
  that exited on its own right after the reload grace left its MCP/shell
  children behind, holding the inherited stdout write handle that keeps a
  reader thread parked in native ReadFile across the domain unload.
  Dispose now sweeps synchronously as well (idempotent with the callback).
- **Script-gate-before-auto-approve ordering is now pinned by a test**
  (same note, section 2): `AgentHubPermissionOrderTests` fails if a future
  reorder lets the AllUnityOps auto-approve run ahead of the script gate.

- **Thinking foldout, "N tools" group row and context-attachment chip
  re-closed on every transcript update** (design note
  docs/design-notes/2026-09-10-foldouts-reset-on-transcript-update.md).
  MessageListController rebuilds a message element whenever a block is
  appended or a tool's status changes -- every few seconds during a turn
  -- and those three kept their open state only on the discarded element,
  so reading a tool result mid-turn was nearly impossible. A new
  `ExpandStateMemory` (keyed by message id + block index, the same
  editor-session mechanism ToolActivityCard/SubagentCard already use per
  toolUseId) restores them. A group row that folds in a card you had open
  now opens itself instead of hiding that card behind a closed header.

## [0.35.0] - 2026-09-09

### Added

- **Jobs for calls that outlive their wait: `uap_job_status`** (design note
  docs/design-notes/2026-09-09-jobs-and-destructive-confirm.md section 1;
  pattern borrowed from isuzu-shiranui/UnityMCP's `syncWaitMs` +
  `job_status`). When a uap_* call times out while its tool is ALREADY
  running on the main thread ("is still running on the Unity main
  thread"), the dispatcher now keeps the call as a job and names its id in
  the message; the tool's own result (or error) is retrievable with
  `uap_job_status` (`job_id`, optional `wait_ms` up to 10 s), which runs
  OFF the main thread and therefore answers while the Editor is still
  blocked by the job. Without `job_id` it lists the session's jobs. Calls
  that finish in time, were never started, or are abandoned pollables
  leave no job. The ledger keeps at most 32 jobs (oldest completed first
  to go) and is cleared by a domain reload. New `IUapOffThreadTool`
  marker: the dispatcher runs such a tool inline on the HTTP worker.
- **Destructive tools require `confirm:true` and support `dry_run`**
  (same note, section 2; UnityMCP's `Destructive` + `confirm`).
  `uap_asset_delete` and `uap_prefab_apply_overrides` now refuse a call
  without `confirm:true`, replying with what WOULD change (the asset type
  or the number of assets inside a folder; the override count and the
  other loaded instances of the prefab that would follow) and nothing
  changed; `dry_run:true` asks for that preview alone and wins over
  `confirm`. Shared gate in `UapDestructiveToolBase` /
  `IUapDestructiveTool`; the two flags are appended to the tools' schemas
  so `tools/list` advertises them. The permission card is unchanged (this
  gate sits between the agent and the tool, the card between the user and
  the agent). The steering prompt tells the agent about both mechanisms.

## [0.34.0] - 2026-09-09

### Added

- **`uap_transform_set`** (design note
  docs/design-notes/2026-09-08-menu-timeout-and-tool-steering.md section 3):
  one call sets position / rotation (Euler) / scale of any GameObject by
  hierarchy path, in local or world space, with partial `{x,y,z}` merges;
  omitting all three reads the current local and world values, which
  `uap_object_inspect` cannot report. Live report: the agent edited a
  Camera's transform through `uloop execute-dynamic-code` because
  `uap_property_set` only writes local values and needed two calls.
- **`uap_editor_execute_menu` action `status`** (same note, section 2):
  lists the last menu runs recorded on the main thread (menuPath, found,
  startedUtc, finishedUtc, durationSeconds) so a menu item that outlived
  the dispatcher's wait can be confirmed without grepping Editor.log. The
  record is cleared by a domain reload and says so.
- **Sub-asset object references and LayerMask in `uap_property_set`**
  (same note, section 4): an object-reference value may be
  `Assets/Path.asset#SubAssetName` (a Material inside a TextMeshPro font
  asset, a Mesh inside an FBX, a Sprite inside a texture); a plain path
  whose main asset does not fit the field picks the single compatible
  sub-asset automatically. LayerMask properties (Camera / Light
  `m_CullingMask`) take an int, a layer name, or an array of names.
  `uap_object_inspect` prints sub-asset references as `path#name` so its
  output round-trips.
- **Steering for the deferred uap_* tools** (same note, section 3): the
  appended system prompt now explains that the uap_* tools are MCP tools of
  server `unity-ops` loaded on demand through ToolSearch (with the
  `select:mcp__unity-ops__...` form), names `uap_transform_set` /
  `uap_property_set` for transform, Camera and TextMeshPro edits, and
  tells the agent what to do on the two main-thread timeout outcomes.
  `uap_property_set` / `uap_object_inspect` descriptions carry the
  common property paths (Transform, RectTransform, Camera, TextMeshPro).

- **Lightmap bake memory preflight and auto-optimizer** (`uap_lightmap_bake`
  action `preflight`, and `start` options `autoOptimize` / `force`; design
  note docs/design-notes/2026-09-08-lightmap-memory-preflight.md). Live
  report: "Progressive Lightmapper: Material render job skipped - out of
  system memory" flooding the Console. Unity drops the material (black in
  GI) instead of failing the bake, so nothing told the agent the result
  was wrong. `preflight` scans the loaded scenes (Contribute-GI renderers
  and terrains, their distinct materials, the textures those reference and
  their sizes, surface area x Lightmap Resolution), reads the lighting
  settings and the machine's free RAM (GlobalMemoryStatusEx on Windows,
  /proc/meminfo on Linux, total minus Editor reservation elsewhere) and
  returns a low / medium / high risk with the estimate, warnings and
  concrete recommendations (which textures to shrink, Auto Generate,
  4096 atlases, restart the Editor). `start` runs the same preflight first;
  with `autoOptimize` (default) it turns Auto Generate off, clamps Max
  Lightmap Size to 2048 and lowers Lightmap Resolution only when that is
  what makes the bake fit, and unloads unused assets before baking; every
  change is listed in the reply. A bake the optimizer cannot make fit is
  refused with the report unless `force:true`. Pure risk model in
  `UapLightmapMemoryEstimate` (pinned by `UapLightmapMemoryEstimateTests`),
  scene/machine scan and settings application in `UapLightmapPreflight`.
- **Per-object Scale In Lightmap** (`uap_lightmap_bake` action
  `object_scales`, and part of `start` autoOptimize). A global resolution
  drop blurs every wall equally, while a few large surfaces (terrain,
  ground, roofs) usually own most of the texels. The preflight now
  accounts texels per Contribute-GI renderer/terrain (Receive GI = Light
  Probes renderers own none) and lists `scene.topTexelConsumers` with each
  one's share; when the lightmap side does not fit, the optimizer proposes
  per-object Scale In Lightmap values by water-fill (only the dominant
  renderers drop, all to one common texel cap, never below `minScale`
  0.25) BEFORE lowering the global resolution. `object_scales` lists the
  ranking and proposals (dry run), `apply:true` writes them
  (Undo-recorded), explicit `scales` {path: value} sets chosen renderers
  directly (0 excludes from lightmaps while still contributing bounce).
- **Lightmap optimization playbook** (`uap_lightmap_bake` action `guide`,
  `UapLightmapGuide`). The same report showed the agent tuning bakes by
  guesswork: it did not know what the Progressive Lightmapper allocates
  (material render jobs sized after each material's largest texture,
  per-atlas working sets), which knobs cost memory (resolution, Max
  Lightmap Size, Contribute GI scope, Scale In Lightmap, texture size,
  material count) versus which only cost time (samples, bounces,
  denoiser), or which serialized properties to change with which uap_*
  tool. `guide` returns that playbook as Markdown; `preflight` and a
  refused `start` point at it, and the tool-steering line tells the agent
  to read it before tuning. Tool-delivered rather than injected into the
  system prompt so it costs no context until a bake is on the table.

### Fixed

- **Status bar context meter and token counter now move during a turn**
  (design note docs/design-notes/2026-09-09-live-usage-during-turn.md).
  Both were refreshed only by the turn's `result` line, so a long
  tool-heavy turn left them frozen at the previous turn's numbers for
  minutes. Each top-level `assistant` line's usage now updates the
  meter (that API call's context) and adds the turn's tokens so far to
  the counter; the result still has the last word and nothing is
  counted twice. Subagent lines are excluded, and a manual `/compact`'s
  own summarization call is ignored as before.
- **Four EditMode tests failed only in a full-suite run** (design note
  docs/design-notes/2026-09-08-editmode-suite-order-dependence.md). Two
  leaks: FontFixBridgeTests left FontLoader's 2-second heal throttle
  armed, so FontLoaderTests' rebuild test (which NUnit runs first, its
  order being case-insensitive) was throttled and cascaded into the two
  re-stamp tests with a destroyed material; and the fixtures that build a
  PermissionWindow / AgentPanelWindow applied the sandbox's persisted
  `Auto` language (Japanese on a Japanese OS) and never restored it, so
  SceneMarkerPinTests asserted an English label against the Japanese
  catalog. Every window-building fixture now restores L10n in TearDown,
  both font fixtures reset the heal throttle, and the two failing
  fixtures establish their own preconditions.
- **Main-thread timeouts now say what happened** (design note
  docs/design-notes/2026-09-08-menu-timeout-and-tool-steering.md section
  2). Live report: after every domain reload the agent re-ran a scene-build
  menu, got "timed out waiting for the Unity main thread (15 s)" and polled
  Editor.log for its own completion marker. That one message covered two
  opposite situations: the menu had started and ran for 9 minutes, or the
  reload-busy Editor never picked it up and the call was discarded. The
  dispatcher settles which with one Interlocked exchange on the work item
  and reports "is still running on the Unity main thread ... do not
  re-issue it, poll uap_ping" or "was never started ... NOTHING ran, issue
  it again" (pollables keep their SEC-7 "abandoned" wording). A dropped
  item can no longer run after its caller was told nothing ran.
- **Interrupted-turn continuation no longer says "re-run anything whose
  result you did not receive"**: it now tells the model to check (uap_ping,
  a query tool, or the tool's status action) before re-running anything
  with side effects, since a long action started before the reload may
  have completed or caused the reload.
- **`uap_property_set` reported success on an object reference Unity had
  silently nulled** (measured: a ScriptableObject path written into a
  MeshRenderer material slot left the slot empty and returned "Set ...").
  A post-assignment null check now throws naming the given type and the
  expected `PPtr<...>` type. Enum values are matched ignoring case and
  non-alphanumerics, so the C# name `SolidColor` reaches Camera's
  "Solid Color" entry.
- **`uap_object_inspect` rounded vectors to two decimals and hid array
  elements** (measured: position y=0.005 printed as 0.01; `m_Materials`
  printed as `<Generic>`). Values now use round-trip precision, arrays
  list `.Array.size` and `.Array.data[i]` (32 elements), nested structs
  expand one level, LayerMask prints the mask and its layer names.
- **Creating a New Scene with UITK Font Fix installed threw
  `NullReferenceException` from `UIRStylePainter.DrawTextInfo` and left
  the panel garbled** (live report; the log said the font was "rebuilt
  it as fontfix:..." but nothing on screen recovered). Two gaps: the
  atlas guard skipped FontFix-owned font assets, so every atlas page
  TextCore added while the panel rendered stayed unflagged and died with
  the scene; and the heal re-read FontFix, which repairs the same
  instance in place, so UI Toolkit never regenerated the labels already
  drawn from the destroyed atlas. The guard now protects a bridged asset
  like the panel's own (accepting FontFix's `DontSave` stamp as
  protection), a broken bridged asset is healed through FontFix -- an
  in-place repair is accepted only when it swapped the atlas material
  (which is what makes UI Toolkit regenerate; FontFix 0.4.1 always
  does), otherwise `FontFix.ResetCaches` rebuilds it so every window
  gets a new instance -- and the guard also runs from the
  new-scene/scene-opened callbacks so it heals before the first repaint
  (docs/design-notes/2026-09-08-fontfix-asset-newscene-flood.md).

## [0.33.0] - 2026-09-08

### Added

- **Bakery GPU Lightmapper integration** (`uap_bakery_bake`, editor
  module; design note
  docs/design-notes/2026-09-08-bakery-integration.md). When Bakery is
  installed the agent can bake through it instead of Unity's lightmapper,
  with the settings surface Bakery has and the built-in tool does not:
  `get_settings` lists every `ftRenderLightmap` render setting (bounces,
  samplesDirect/samplesGI, texelsPerUnit, renderMode, renderDirMode,
  denoise, fixSeams, ...) with enum options; `set_settings` applies a
  subset and/or a `preview` / `balanced` / `final` preset and saves it
  into the scene's Bakery storage; `start` bakes the full scene, the
  selected objects, light probes or reflection probes (optionally applying
  settings first) and returns at once; `status` reports installed /
  running / progress text; `cancel` stops the bake. Bakery has no UPM id
  and no asmdef, so it is reached purely through reflection
  (`UapBakeryReflection`) -- nothing here references it at compile time,
  every action degrades to a clear "not installed" error, and Bakery's
  own message boxes are suppressed (showMsg:false) so a bake can never
  park the agent behind a modal dialog.
- **`bakery` extension profile**: detected via Bakery's compiled component
  types (BakeryPointLight, BakeryDirectLight, BakerySkyLight,
  BakeryLightMesh, ftLightmapsStorage); tells the agent about Bakery's
  separate light components, the per-object group/volume components, the
  Contribute GI requirement, and to bake with `uap_bakery_bake` rather
  than by clicking through Bakery's window.

### Fixed

- **Bakery bake: "RenderLightProbes error: lightingDataAsset was not
  generated"** (live report). Bakery's light-probe step runs Unity's
  lightmapper once to create the LightingDataAsset it then fills; with no
  LightingSettings, Auto Generate on, or Baked GI off that bake is a
  no-op and Bakery logs the error. `uap_bakery_bake start` now runs a
  preflight (`autoFixLightingSettings`, default true) that creates a
  LightingSettings when the scene has none, turns Auto Generate off and
  Baked GI on, refuses an unsaved scene and a probes-only bake without a
  LightProbeGroup with actionable messages, and reports what it changed
  under `preflight` (plus the `lightProbeMode: L1` fallback in its
  warnings). `status` reports the same scene facts under `scene`.
- **Panel text flood during a Bakery bake** (MissingReferenceException
  "Texture2D has been destroyed" from UIRStylePainter on every repaint).
  Bakery's bake loop reloads scenes and refreshes/unloads assets between
  Editor update ticks, so a font atlas texture could be destroyed before
  the atlas guard's count compare ever saw it, and a FontAsset with a
  destroyed atlas cannot be repaired. The guard now also re-stamps a used
  atlas texture/material that lost HideAndDontSave without a count
  change, and rebuilds a broken CJK UI FontAsset (throttled, once per 2 s)
  and re-applies it to every open panel instead of throwing forever.

### Changed

- `uap_lightmap_bake` status now reports `bakeryAvailable` and, when
  Bakery is installed, a note pointing at `uap_bakery_bake` (a Bakery-lit
  scene baked with Unity's lightmapper silently ignores every Bakery
  light). The UapOps steering text names the Bakery tool for the same
  reason.

## [0.32.0] - 2026-09-07

### Added

- **Slash commands in the composer** (design note
  docs/design-notes/2026-09-07-slash-commands-and-compaction.md section 1).
  Typing `/` opens a suggestion popup above the field listing the CLI's
  own command catalog (initialize `commands[]` merged with system/init
  `slash_commands[]`, cached in `PanelSettings.slashCommandCatalog` so it
  works right after a reload); Up/Down choose, Tab or Enter complete,
  Esc closes, a second Enter sends. A sent `/name args` goes to the CLI
  verbatim -- context chips and pending images stay for the next real
  message. `/compact` and `/clear` are always offered; `/clear` is handled
  by the panel as New chat instead of letting the CLI start a new session
  id underneath the open one.
- **Context compaction is visible** (section 2). `system/compact_boundary`
  now maps to a typed message (`SystemCompactBoundaryMessage`, wire
  `compact_metadata` and on-disk `compactMetadata` both read). The
  transcript gets a localized note at the boundary saying who compacted
  (you via `/compact`, or the CLI automatically) and how large the
  context was; History restore renders the same note from the JSONL and
  no longer shows the CLI-authored `isCompactSummary` summary as if the
  user had typed it (nor as a session preview).

### Changed

- **Context meter after a compaction**: instead of re-reading the
  just-discarded context as ~100% (the billing-sum fallback), the meter
  shows "compacted" from the boundary until the first completed turn
  that measured the new window. A manual `/compact`'s own result is
  deliberately not trusted for this (its last iteration is the
  summarization call over the old context); an automatic mid-turn
  compaction's result is.

## [0.31.0] - 2026-09-07

### Added

- **Scene-view 3D markers** (`markers` module, default ON; design note
  docs/design-notes/2026-09-07-scene-markers-image-attachments-error-chip.md
  section 1). The agent can point at a place or object instead of
  describing coordinates: `uap_marker_add` shows a numbered point, arrow
  or box with a label (fixed position or following a hierarchy path),
  `uap_marker_list` reads them back, `uap_marker_clear` removes one, the
  agent's, or everything. Markers are overlays only -- drawn with Handles
  for the user and with `Graphics.DrawMesh` into the Scene camera so the
  agent's own `uap_editor_screenshot` shows them -- never scene objects,
  never dirtying or saving anything. They survive a domain reload
  (SessionState), expire on an optional TTL, and clear on scene change or
  Play Mode. The context bar shows an "n scene markers" chip with a
  one-click clear while the agent has markers up; Settings gains a
  "Scene-view markers" module toggle; the steering text tells the model
  to use markers for "where" and to refer to them as [n].
- **User pins.** A "Pin" toggle in the Scene view toolbar (an overlay,
  shown by default) and a matching button in the panel's context bar arm
  the next left click in the Scene view to drop a magenta pin where it
  lands (collider hit, then the surface under the cursor, then 5 m along
  the view ray; Shift keeps placing, Esc cancels). Each pin becomes a
  context chip -- world position, hit object, the three nearest objects
  and the Scene camera -- so "put the stairs here" carries "here". Pins
  are markers like any other (uap_marker_list shows them as user-pin, a
  reload keeps them) but the agent's default clear leaves them alone;
  the chip's X removes the pin. Pins are numbered P1, P2, ... with the
  lowest free number (removing P1 makes the next pin P1 again), drawn as
  small screen-constant needles, previewed under the cursor while the
  Pin toggle is armed, and always reachable from the panel: the context
  bar rebuilds a chip for every pin it does not have (a pin dropped while
  the panel was hidden, or after a reload), and the "n scene markers"
  chip's X now clears pins too.
- **Image attachments (core).** The composer's "+" menu attaches an
  image file (PNG/JPEG) or the Scene/Game view as it is now (camera
  render, or the Scene view exactly as displayed). Images are downscaled
  to a 1568 px long edge on the CPU, encoded as PNG (JPEG above 1.5 MB),
  refused above 5 MB, stored once under their content hash in
  Library/AgentPanel/Attachments (7-day retention), and sent to the CLI
  as base64 image content blocks after the text -- the stream-json shape
  the real CLI accepts. Up to four per message; an image-only message is
  allowed. The pending strip survives a domain reload, the CompileGate
  queue carries images by path, and the transcript shows a clickable
  thumbnail per sent image (a placeholder when the file is gone).
- **Image drops.** Dropping PNG/JPEG files from the OS file manager onto
  the panel -- which used to be ignored outright, since only object
  references were checked -- attaches them as images, and so does
  dropping a Texture asset from the Project window (its image file is
  read directly, whatever the importer settings). Everything else
  dropped keeps becoming a context chip; one drop can carry both.
- **Console Clear clears the error chip.** The panel polls the Console's
  own error count (the internal `LogEntries` API, through reflection,
  twice a second) and forgets its captured errors whenever that count
  drops -- Clear, Clear on Play/Recompile/Build -- so the chip and its
  fix button only ever offer what the Console still shows. A first
  observation never clears, rises are ignored (capture stays on the log
  callback), and if the internal API ever stops resolving the panel
  simply behaves as before.
- **Images come back with the history.** Restoring a session whose
  transcript carries user image blocks writes the base64 payload back
  into the attachment store and shows the same thumbnails; undecodable
  or foreign (URL) image blocks are skipped without breaking the
  restore.
- **`uap_editor_screenshot capture:"window"`** reads the Scene view
  exactly as displayed -- grid, gizmos, Handles and marker labels
  included -- instead of rendering the camera offscreen (still the
  default, now labelled `capture:"camera"`). Both modes append a
  "Markers in view" block with each marker's pixel position in the
  image, so the model can match what it placed with what it sees. The
  window capture reads the camera area itself (not the window rect, which
  starts at the tab bar), needs the Scene view visible on screen, and
  refuses the Game view with a structured error. The camera and window
  capture code moved to a shared `UapScreenCapture` for the upcoming
  "attach the current view" composer button.
- **`uap_editor_screenshot return_image:true`** returns the PNG itself as
  an MCP image content block next to the path text, so the model sees
  the capture without a second Read call. The image goes through the
  attachment encoder first (long edge 1568, JPEG fallback above 1.5 MB),
  and when encoding fails the path text is still returned with a warning
  line. Verified end to end: the real CLI connected to the in-editor MCP
  server, received the image block and described the scene.
- **Paste an image from the clipboard.** Ctrl/Cmd+V in the composer
  attaches the clipboard image (a screenshot, a copied picture from a
  browser or an image editor) or copied PNG/JPEG files (a file-manager
  copy) when the clipboard holds no text; text paste is unchanged. The
  "+" menu gains "Paste image from clipboard" for the same thing with a
  dialog on an empty clipboard. Unity's clipboard API is text-only, so a
  short helper process reads the bitmap per OS (PowerShell's
  Windows.Forms Clipboard, osascript on macOS, xclip or wl-paste on
  Linux) and writes a temp PNG that is imported like a dropped file.

### Fixed

- **User pins land on the mesh under the cursor, not the floor behind
  it.** The pin used Physics.Raycast, so anything without a collider --
  most of what an editor scene is made of -- was invisible to it and the
  pin fell through to the y=0 plane. The click (and the hover ghost) now
  also intersects the ray with the rendered meshes themselves
  (MeshFilter and baked SkinnedMeshRenderer, via the editor's own
  IntersectRayMesh) and takes the nearer of collider and mesh; the hover
  ghost lies on the hit surface's normal.

- **The console-error chip's fix button sends exactly what the chip
  shows.** It used to send the digest of every visible error, so errors
  already sent (or closed with the X) went out again, the attachment
  title carried the wrong count, and -- because the digest keeps the
  oldest ten -- a new error behind ten already-sent ones was never sent
  at all while the chip kept saying "1 console error". The chip's count,
  the payload, its title and the post-send acknowledgment now all derive
  from one list (`ContextBarView.SelectSendable`).

### Added (docs)

- Design note + verified spike for scene-view 3D markers, image
  attachments and Console-window sync
  (docs/design-notes/2026-09-07-scene-markers-image-attachments-error-chip.md).

## [0.30.0] - 2026-09-06

### Added

- **`uap_lightmap_bake`** (editor module): starts a lightmap bake with
  `Lightmapping.BakeAsync`, polls it, or cancels it, always returning at
  once. Asking the agent for a light bake used to freeze the Editor in
  "Hold on" until the bake finished, because the synchronous
  `Lightmapping.Bake()` reachable through dynamic code runs the whole
  bake inside one main-thread call (and starves the panel's own
  dispatcher meanwhile). The UapOps steering text now tells the model to
  use this tool and never the synchronous call.

### Changed

- The pre-reload exit grace for the CLI process is 150 ms instead of
  500 ms: it sat synchronously inside every domain reload, the session is
  resumed with `--resume` either way, and the tree kill that follows is
  what actually ends the old process.

## [0.29.0] - 2026-09-06

Domain-reload resilience, re-audited and verified on a real editor
(docs/design-notes/2026-09-06-domain-reload-resilience-and-hot-reload.md).

### Added

- **Continue automatically after an interruption** (Settings, default
  OFF). When a domain reload the agent did not cause -- a script saved in
  the IDE, a manual recompile, entering Play Mode -- cuts a running turn
  short, the panel resumes the session and sends the same "continue" the
  banner button would. Only when a turn was actually interrupted, never
  while the crash-loop guard holds, never on top of a queued compile
  continuation, at most three times in a row without a turn completing,
  always announced in the transcript, and carrying the compiler-error
  digest when the reload left errors behind.
- **Live domain-reload tests.** `ReloadLifecycleLiveTests` spawns a
  stand-in CLI (`ci/FakeCli/claude`) through the production transport,
  reloads the domain for real (`RequestScriptReload`, Play Mode entry and
  exit) mid-turn, and asserts the kill + `--resume` + mid-turn-flag
  contract. Runs in the EditMode CI job.
- **Play Mode throttle notice for uap_* tools.** While Play Mode runs with
  the Editor unfocused (Run In Background off) the Editor loop ticks
  sporadically; a main-thread timeout now says so -- tick age, cause and
  the fix (focus the window) -- and successful `tools/call` results carry
  the same warning as a trailing text block, as uloop's tools do. The wait
  is shortened while throttled so this message reaches the model before
  the CLI's own generic timeout.

### Fixed

- **Post-reload reconciliation no longer depends on which callback runs
  first.** The panel window's deferred boot could start the CLI before
  the reload lifecycle's first update tick, rewriting the mid-turn flag
  and spending the auto-continue ticket before they were read -- a
  mid-turn reload then reconnected silently with no "interrupted" nudge.
  The sequence is now an idempotent entry point both paths run in the
  same order.
- **A CLI that survives the pre-reload tree kill is reaped after the
  reload** instead of the record being deleted on the assumption the kill
  worked.
- **Reload hooks survive a throwing initializer.** `ReloadLifecycle` and
  `UapTurnScope` subscribe from `[InitializeOnLoadMethod]` instead of a
  static constructor (which would poison the type), and every
  reconciliation step runs guarded so one failure cannot skip the
  session restore.

### Changed

- The UapOps steering text tells the model that `uap_ping` is the
  authoritative Editor liveness check and that a uloop `focus-window`
  "No running Unity process found" is a detection failure whenever
  `uap_ping` still answers, never grounds to relaunch the Editor.

## [0.28.0] - 2026-09-06

- **UITK Font Fix is honoured when installed.** The panel used its own
  hard-coded CJK family chain even in a project that had configured
  `jp.colloid.uitk-font-fix`, so that package's settings (family list,
  face style, bold-face wiring) never reached the panel. A reflection
  bridge (`FontFixBridge`, no package dependency) now hands FontFix's
  resolved CJK UI FontAsset to every panel window when the package is
  present, re-applies it on FontFix's cache invalidation (settings
  edits), and the Appearance diagnostic reads "Detected font: ... (via
  UITK Font Fix)". Without the package nothing changes.

UI redesign, phase 1 -- "one control vocabulary"
(docs/design-notes/2026-09-05-ui-redesign.md). No new features; the
panel's controls now share one set of dimensions and one set of
interaction states, and three places where two different facts wore the
same colour are told apart.

### Changed

- **Every button has hover / pressed / focus / disabled states.** The
  stylesheet had 41 hover rules, zero pressed rules, five focus rules and
  three disabled rules; the header chips, Send/Stop, Quick actions,
  settings buttons, history buttons, chips and pills all get the missing
  three. Pressed sinks one step (`--uap-bg-active`), focus draws the user
  accent ring (`--uap-focus-ring`), disabled flattens to the same
  no-accent surface the permission buttons already used.
- **Button dimensions are tokens.** `--uap-btn-pad-y/x`,
  `--uap-control-h` and `--uap-icon-btn-size` replace six copy-pasted
  `3px 10px` paddings and four hand-written 20/22px squares.
- **A denied tool no longer looks like a failed one.** Denied cards and
  compressed groups get Test Runner's "ignored" icon in the caution
  amber; failed keeps the red cross. A collapsed group now carries the
  accent bar of its worst child (failed outranks denied) instead of
  hiding it.
- **Deny reads as "stop this".** The permission card's Deny keeps the
  plain shell so it never outranks Allow, but its text is error red and
  the border joins it on hover; it is no longer a visual twin of Always.
- **History badges have three colours for three meanings.** Current
  keeps the user accent, Pinned turns caution amber, Archived goes
  neutral.
- **Narrow docks fold their secondary chrome.** Below 340px the root
  gains `uap-narrow`: the status bar drops the model name, separators
  and usage figures (all still reachable from the header picker and the
  usage popover), the composer hint hides, and the header chips cap
  tighter, so a 300px dock keeps the session title readable.
- The hairline between the context bar and the composer is gone: they
  are one input region and now look like it.
- **Settings reads as four topics, not fifteen boxes.** Group headings
  (Agent / Panel / Unity integration / Connection & account) sit between the cards; Conversation and Model
  keep the lead slots, Appearance moves next to Display.
- **The reconnect-pending notice is a banner above the settings scroll**
  with its own Reconnect now button, instead of a line inside the CLI
  card that is collapsed by default.
- Settings cards no longer light up on hover (they are not clickable);
  only a collapsible section's header row does.
- On a narrow dock, settings fields stack the label over the control so
  the control gets the full width; switch rows stay side by side.
- **The permission card's summary row wraps instead of hiding its
  target.** Title + target stay on the first line; the Allow / Always /
  Deny row drops to a second line when the card is too narrow for both
  (it used to ellipsize the target to "pa..." at 600px and lose it
  entirely at 300px). The inline title is now the bare tool name; the
  "Claude wants to use" prefix stays on the floating window only.
- **Long explanations get a visible "?".** Settings rows whose tooltip
  is a paragraph now end with Unity's component-header help mark: hover
  shows the tooltip as before, click opens it in a popover that wraps.
  Section-level hints without a field row keep the mark at the end of
  the hint line instead of on a line of its own. Every settings card
  reserves a fixed right-hand gutter for the mark, so a marked row's
  control ends exactly where an unmarked one does. Short tooltips are
  unchanged.
- **Narrow dock: switch labels wrap** instead of being cut mid-word.
- **Warning colour follows the value.** The auto-approve, script-gate
  and auto-continue lines stay visible but turn amber only when the
  chosen value is the risky one, instead of colouring the recommended
  default.
- **Settings copy: one vocabulary for apply timing** ("Applies
  immediately." / "Applies after the next reconnect."), kept in
  tooltips only now that the reconnect banner announces pending changes.
- **Five settings section icons were silently falling back to text**
  because the icon loader never found the "<Type> Icon" built-ins;
  the loader now tries EditorGUIUtility.Load second. Duplicate icons
  (Conversation vs the header gear, Quick actions vs the header +,
  Diagnostics vs About) got their own.
- Collapsible settings cards wear the same header strip as plain cards;
  UapOps module switches indent under the master switch and grey out
  with it; status lines read as facts (italic, monospace path) rather
  than help; the detected-font line no longer leaks its internal tag;
  "Log in" reads "Switch account" while logged in; the per-type override
  caveat hides when there are no overrides.
- On a narrow dock the header's model and auto-approve chips fold into
  one chip carrying the model name, whose menu opens either; the
  session title gets the width back. Tool-card durations hide there too,
  so the tool name is what survives.

### Removed

- Four dead stylesheet rules (`.uap-tool` row container and its
  name/summary/time labels) that no code had instantiated since the
  tool-card/subagent-card split.

## [0.27.0] - 2026-08-26

The Phase 3 remediation of the full-package review: 53 findings across
the UapOps write tools, the CLI transport, the hub's lifecycle, the
model layer, transcript loading and the UX surfaces, landed as nine
batches (F-N). The dominant defect class this phase closed is the FALSE
SUCCESS: a Unity or CLI API that refuses work by returning null or
doing nothing, wrapped by code that reported success anyway. Final
suite state on GitHub-hosted Unity 2022.3.22f1: 2503 tests, 2485
passed, 0 failed, 18 environment-skipped, plus the license-free smoke
tier (100 assertions) on every push. Each batch carries a dated design
note under docs/design-notes/ (2026-08-23 for F-L, 2026-08-26 for M-N)
recording the mechanism, what CI measured, and the honest residuals.

### Security

- **Asset-creating and asset-deleting tools now refuse write paths
  that escape the project's Assets folder.** `Assets/../Evil.mat`,
  `Packages/...` and `ProjectSettings/...` all reached
  AssetDatabase unchecked before; every create tool and
  `uap_asset_delete` now normalise the path first and refuse anything
  that resolves outside Assets/.
- **The script validation gate now catches file writes named as
  command arguments, not just shell redirection.** `sed -i`,
  `cp`/`mv`/`install` with a destination under Assets/, and `dd of=`
  sailed past a gate that only knew `>`, `>>` and `tee`. The gate also
  learned `>|`. A static scan cannot catch an interpreter one-liner or
  a variable-built path -- that limit is documented in the gate itself,
  and the Bash permission card remains the real backstop.
- **Extension-profile approvals are bound to the machine that granted
  them.** The trust store kept raw content hashes in a settings asset
  inside the project folder, so a project could arrive from someone
  else with its profiles already marked approved and have their
  instructions injected into the system prompt. Approvals are now
  HMAC tokens keyed by a per-user salt kept outside every project
  tree, so a transplanted approval simply fails. UPGRADING: profiles
  you approved before this release show as pending once and need a
  single re-approval -- as do all of them if you move machines or
  clear this editor's preferences.
- **The panel says so when the script gate is switched on but cannot
  actually block anything.** The gate has two enforcement layers: a
  generated pre-tool hook that is installed on Windows only, and a
  permission pre-filter that skipping permission prompts removes by
  construction -- so on macOS or Linux with prompts skipped, a toggle
  the Settings UI showed as ON blocked nothing. The gate's behaviour
  is unchanged; the hole is now stated out loud, once per session, in
  the Console and in the transcript.
- **Agent definition names that differ only in letter case, or that
  would write to a Windows device name, no longer produce a broken or
  colliding file.** Case-insensitive collisions collapse to one entry
  and reserved device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9) are
  refused rather than written.

### Fixed

- **Component listings now report the index the component tools
  actually accept.** The listing showed a component's position among
  ALL components on the object, while remove, inspect, property_set and
  prefab_revert_override interpret the index as the ordinal within that
  component TYPE -- so acting on "BoxCollider at index 2" could delete a
  different component entirely. Listings now carry both `index` and
  `typeIndex`, and the four consuming tools ask for `typeIndex` in
  their schemas.
- **Prefab override apply and revert refuse a nested instance loudly
  instead of hitting the outer one.** Aimed at an inner instance, apply
  used to write the OUTER prefab asset and revert used to wipe every
  override on the entire outer instance. CI measured that Unity 2022.3
  cannot apply overrides at a nested instance root at all (it returns
  without error and applies nothing), so both tools now detect the
  nested case and refuse, naming the outermost root to address and the
  single-override tool as the alternative. Successful runs now also
  name the instance root they resolved and how many overrides they
  swept.
- **Adding a component Unity refuses now reports the failure instead of
  claiming success.** `AddComponent` returns null with only a Console
  error for a `[DisallowMultipleComponent]` duplicate, an unsatisfiable
  `[RequireComponent]`, or a natively abstract built-in; the tool
  passed that null along and answered "Added ...". Abstract types are
  refused by name up front and a null return now throws with the likely
  causes named.
- **A property write with a wrong-shaped value is refused instead of
  writing a default or nothing at all.** A boolean on an int property
  wrote 0 through a lenient accessor, and a bare number on a
  Vector/Color/Rect property resolved every component to its CURRENT
  value -- a no-op reported as success. Numeric and struct writes now
  check the value's shape first. Integer properties map a boolean to
  1/0 deliberately: Unity serialises plenty of conceptually-boolean
  settings as ints, and the old code turned "enable mipmaps" into a
  silent write of OFF.
- **Reverting a prefab override that is not actually an override now
  reports an error instead of "Reverted".** The three revert kinds are
  silent no-ops in Unity when the target carries no override; each now
  verifies the override exists first.
- **Deleting a scene object reports failure when Unity refuses the
  delete.** The tool now checks the object actually died, the same
  defence the component-remove tool has had since its own review fix.
- **An animator edit that fails validation no longer leaves an empty
  controller behind.** The tool auto-created the .controller file
  BEFORE checking the operation name and its required arguments, and
  the creation was not undoable.
- **Objects created while a prefab stage is open land under the prefab
  root.** With no explicit parent they became a second root of the
  preview scene -- and a prefab stage saves only its single root, so the
  object silently vanished on save.
- **Writing one asset saves only that asset.** Six write tools called
  the argument-less save, flushing every other unsaved change in the
  session to disk with it.
- **A CLI session that dies the instant it launches is reported as
  stopped.** A process that exited before the panel finished wiring up
  its handlers left the panel showing a live session that was not
  there.
- **A turn that succeeded is no longer flipped to an error because the
  CLI exited before its last output line was read.** The exit path now
  waits briefly for the output pipes to flush before deciding the turn
  failed.
- **The CLI version probe and the auth-status check can no longer hang
  forever or leak a worker per run.** Both now go through one shared
  runner with a bounded wait.
- **Panel teardown closes the CLI's input before killing it,** so a
  process that survives the kill still exits on its own instead of
  lingering.
- **On Windows, a launch whose command line would exceed the operating
  system limit is refused up front** with the actual length in the
  message, rather than failing deep inside the process start with an
  opaque error.
- **A message containing a split emoji or a corrupted paste is sent
  intact.** Lone surrogates are escaped on the wire instead of being
  mangled into replacement characters; valid pairs pass through
  untouched.
- **After an automatic continue-on-compile, the panel tells the model
  the REAL compile outcome.** It reported success unconditionally, so a
  continuation could follow a failed compile claiming everything had
  built. The compile result is now captured before the domain reload
  and carried across it.
- **A continuation queued after a compile no longer starts before the
  session is restored,** and a long compile no longer causes the panel
  to abandon a continuation it never had the chance to send (the
  timeout now only counts time when Unity is not compiling).
- **Errors logged from background threads reach the error chip and the
  compile-error digest.** Only main-thread logs were observed before.
- **Cancelling an editor quit no longer disconnects the panel.**
- **Restoring a huge session from History no longer freezes the
  editor.** Transcript loading is bounded (16 MB, tail-window reads for
  anything larger) and the in-memory message list is pruned in chunks
  rather than one message at a time.
- **Sessions run from the terminal restore showing what you typed.**
  Slash commands arrive wrapped in CLI scaffolding tags; the panel now
  unwraps them instead of showing the plumbing.
- **Per-session settings can no longer leak between sessions that have
  no saved metadata.** A shared empty instance was handed out and
  written through.
- **A session's displayed cost no longer jumps backwards when the
  session is resumed.**
- **History lists each past session under the project it actually ran
  in.** Cross-project scanning bucketed every session in a folder under
  whichever project owned the first one, so directory-name collisions
  mixed projects together.
- **An Enter press whose follow-up character event never arrives no
  longer replays its send decision on a later, unrelated newline.**
- **Quick action buttons stop re-reading their settings from disk every
  time the Unity selection or the console errors change,** and the
  model picker stops rebuilding the whole model list on every agent
  update (once per streaming delta at the hottest).
- **Icons switch skin immediately when you flip the editor between
  light and dark themes.** The icon cache keyed on name alone, so it
  kept serving the old skin's texture.
- **A reply that cuts off right after a code fence's opening line still
  renders as a code block** instead of disappearing.
- **The crash-loop message points at Reconnect, a button that exists.**
  It previously named an action with no counterpart in the panel.
- **Permission-mode choices in the dropdown are translated, and an
  unrecognised mode displays as the safest option** rather than as raw
  wire text.
- **The header model picker works before the first connection.** With
  no live session it was a dead button that swallowed clicks; it now
  offers the cached model catalog (writing the default for the next
  session) and, with no cache either, says models can be chosen after
  connecting.
- **Submitting a login code visibly acknowledges it, and a rejected
  code says so.** The CLI gives no per-code acknowledgement, so
  pressing Submit changed nothing on screen, and a wrong or expired
  code ended as a silently vanished card -- indistinguishable from
  success until the status line disappointed.
- **The Always-allow menu names tools in plain language** and spells
  out when a grant covers every call to that tool. The menu previously
  printed the raw wire rule; what gets persisted is still the exact
  rule, only the label changed.
- **Denying a tool with an alternative instruction works from the
  message composer.** The deny-reason field lived inside the permission
  card's collapsed details, where nobody found it. The always-visible
  composer now carries it (its placeholder says so while a request is
  pending), and the text is consumed by the deny so it cannot also be
  sent as a chat message. The floating permission window keeps its own
  field.
- **A permission request no longer yanks you out of Settings or
  History.** It used to switch the whole panel to Chat mid-edit; the
  view-independent floating permission window now carries the request
  and the active view is left alone.

### Changed

- **The composer says what its primary button will do while a turn is
  running.** The button flips between Send and Stop as you type, which
  was previously a silent change in the user's peripheral vision.
- **The first-run login card leads with the one-click Log in button.**
  The terminal walkthrough that used to open the card is still there,
  demoted into a collapsed "another way" section with its Check again
  button.
- **Settings opens with its five advanced sections collapsed** (UapOps
  modules, Extension Profiles, uLoop, CLI, Diagnostics), so the
  everyday sections stand out. Open/closed state is remembered for the
  editor session.
- **The font-size setting goes up to 20 instead of stopping at 16,**
  and every step in the range now actually changes the text.
- **A tool-permission request that overwrites a pending one is logged
  loudly** rather than vanishing without trace.
- **The architecture's layering rules are enforced by a test.** The
  one-way UI to Model to Core dependency rule, and the engine-free
  status of the Core JSON/protocol/client/file-IO modules, were
  previously convention only.

### Added

- **A connection taking longer than ten seconds escalates to "Still
  connecting..." with an inline Reconnect button,** instead of spinning
  indefinitely with text that looks the same at two seconds and two
  minutes.
- **History rows say which model the conversation ran on,** next to the
  timestamp and file size. Cost is deliberately absent: it never
  reaches the transcript on disk, so it cannot be restored.

## [0.26.0] - 2026-08-23

The Phase 2 remediation of the full-package review: 18 findings across
the approval surface, information architecture, UI correctness/
performance, client robustness, commit-pipeline security and test
infrastructure. Final suite state on a real Unity 2022.3.22f1 editor:
2295 tests, 2277 passed, 0 failed, 18 environment-skipped. Each batch
carries a dated design note under docs/design-notes/ (2026-08-23,
batches A-E).

### Security

- **`uap_scripts_commit` now moves the VALIDATED mirror bytes into
  Assets/, not the original staged files.** AssemblyBuilder spans
  editor ticks, so an agent could rewrite a staged file between
  validation and commit and have unvalidated bytes land in Assets/
  wearing a "validated, 0 compile errors" label (TOCTOU). All staged
  files (.asmdef included) mirror up front, the asmdef JSON check
  reads the mirror, and cleanup runs only after the move.
- **A tool call whose HTTP worker timed out is abandoned, never
  silently completed.** The dispatcher used to leave a timed-out
  pollable re-enqueueing itself until `uap_scripts_commit` eventually
  moved files into Assets/ with the caller long gone; a cancelled
  work item is now discarded on next pickup, matching the tool's
  "failure leaves the stage untouched" contract.

### Fixed

- **Concurrent permission requests no longer wedge the turn.** A
  second `can_use_tool` arriving while one was pending used to
  overwrite it, orphaning the first request forever (the CLI waited
  until the 10-minute silence backstop). Requests now queue FIFO and
  surface one at a time; synchronous auto-approve drains the queue
  with constant stack depth.
- **A throwing diagnostics subscriber can no longer swallow a result
  line.** The `RawLineForLog` raise is isolated inside DispatchLine,
  so a faulty listener cannot skip the line's parse and state
  transition (previously: turn wedged until the silence backstop).
- **Switching views mid-stream no longer freezes the streaming
  message.** Deactivating the chat view suspends the typewriter pump
  (entries kept) instead of clearing it; reactivating resumes.
- **Long transcripts stop full-rebuilding on every message.** Past the
  300-message render cap, every append used to rebuild all 300 rows
  and reset the scroll. The window anchor now holds within a
  50-message slack (appends stay incremental) and prunes the oldest
  rows in one batch when exhausted; full rebuilds preserve scroll
  intent.
- **The Settings tab stops doing disk IO per streaming delta.**
  Hub-driven refreshes coalesce on a 250 ms loop, the CLI path stat
  runs only when the manual path changed, and the uloop manifest scan
  is floored at 2 Hz.
- **A panel rebuild (language switch) no longer loses the composer
  draft and scroll position**, and no longer reopens a permission
  window the user had deliberately closed.
- **The OS-fallback monospace font survives NewScene** (stamped
  HideAndDontSave like the CJK font assets already were).

### Changed

- **The permission card says what it is asking.** The collapsed
  summary row now always shows the operation target (file name /
  command summary) via the same describer the activity rows use;
  Edit/MultiEdit approvals render a real +/- line diff (context
  elided to the lines nearest the change, "Show all N lines"
  expander) instead of raw old/new block dumps; the not-undoable
  badge explains itself with a tooltip.
- **The error chip's X no longer permanently silences errors.** It
  now hides the current errors for this session only; the permanent
  ignore moved to an explicit chip menu action with a 10-second undo
  notice backed by the new `ConsoleErrorProvider.Unignore`.
- **The three permission controls sit together.** The auto-approve
  level moved from the bottom of the UapOps section to directly under
  the permission mode, with the danger-zone bypass following; UapOps
  keeps a cross-reference hint.
- **Session history is keyboard-operable.** History rows are
  focusable with a visible focus ring, Enter/Space restore the
  session exactly like a click, and the row menu leads with an Open
  item (disabled with an explanation on foreign-project rows).

### Added

- **CI prints compiler errors in the job log** when a run dies before
  producing results.xml (previously they lived only in the artifact).
- **Test coverage for three previously unguarded paths**: CompileGate's
  reload-surviving send queue and drain decision (extracted pure,
  table-tested), ProjectSessionScanner (hermetic fake-directory
  suite), and fixture provenance (`_fixtures.meta.json` records the
  captured CLI version per fixture; a test keeps files and record in
  lockstep and warns when verification goes stale). The remaining two
  INFRA-2 holes (reload reconciler extraction, live socket tests) are
  deliberately deferred with rationale in the batch E design note.

## [0.25.0] - 2026-08-23

The Phase 1 remediation of the full-package review: 16 findings across
security, data integrity, hub lifecycle and the permission/UX surfaces --
every one landed on a new CI safety net that did not exist before this
release. Final suite state on a real Unity 2022.3.22f1 editor: 2210 tests,
2192 passed, 0 failed, 18 environment-skipped; plus a 67-assertion
license-free smoke tier on every push. Each fix carries a dated design
note under docs/design-notes/ recording the mechanism, the rejected
alternatives and the honest residuals.

### Security

- **The script-validation gate's PowerShell hook is no longer an
  agent-editable `.ps1` on disk.** It is inlined into the generated
  `--settings` JSON via `powershell -EncodedCommand`, closing the
  acceptEdits-mode escalation where editing the hook file meant arbitrary
  code execution; stale hook files from older builds are deleted, and the
  gate's own config files auto-deny agent edits.
- **The UapOps Bearer token left the process command line.** The
  `--mcp-config` payload is written to
  `UserSettings/AgentPanel/uap-mcp-config.json` and only the absolute path
  is passed -- `/proc/*/cmdline` is world-readable on Linux regardless of
  umask, and WMI/Task Manager expose it on Windows, so any local process
  could previously lift the token and drive the Unity-operations server
  directly. Inline JSON survives only as the write-failure fallback.
- **The UapOps HTTP server no longer buffers unbounded request bodies
  before authentication.** A 4 MB cap rejects oversize with 413: declared
  oversize without reading a byte, lying/chunked clients the moment the
  bounded reader crosses the limit -- an unauthenticated local process
  could previously OOM the editor (and its unsaved scenes) with one POST.
- **`--append-system-prompt` values containing a tab are now quoted.**
  CommandLineToArgvW splits on tab exactly like space, so a CJK custom
  instruction with an embedded tab used to reach the CLI as two arguments.

### Fixed

- **Transient IO errors no longer permanently delete user data.** Every
  persistence sidecar treated ANY load exception as corruption and deleted
  the file -- a cloud-sync or antivirus lock (gone a second later) erased
  pins/renames/groups, the transcript cache, quick actions, and hand-typed
  custom instructions. Failures are now classified: only parse/shape
  corruption deletes; IO-class failures keep the file and skip the load.
- **The atomic-write crash window that could lose BOTH file generations is
  closed.** Five copy-pasted writers shared a fallback of delete-then-move
  whose comment claimed it was safe; the shared `AtomicFile` helper parks
  the previous generation as `.bak` instead, and reads restore it -- every
  crash window now keeps a readable generation. The gate/MCP generated
  files ride the same atomic swap instead of truncatable in-place writes.
- **A stalled turn no longer disables AssetDatabase auto-refresh for the
  rest of the editor session.** The silence backstop force-closes the turn
  client-side, but the handler only finalized the streaming message --
  leaking the turn scope (auto-refresh suppression + open Undo group),
  stale not-undoable warnings onto the next unrelated turn, an
  unanswerable permission card, and a stranded auto-continue flag. All
  four non-clean exits (stall/error/death/teardown) now share one
  idempotent `AbortOpenTurn` cleanup.
- **A dying CLI is no longer respawned every editor tick.** The queued-send
  drain called EnsureStarted unconditionally while messages were pending,
  defeating the crash-loop suspension the panel had just announced; it now
  checks the shared guard, warns once, and keeps the queue -- typed text is
  never dropped.
- **A fresh or switched session no longer boots showing the previous
  conversation's token usage.** Both paths saved the session cache before
  resetting the per-model usage snapshot; the order is reversed.
- **A stale "Always allow" menu can no longer write a permanent allow
  rule.** The menu callback runs after the native menu closes, by which
  time the request may have been answered elsewhere, superseded, or the
  turn ended -- the wire response was correctly dropped, but the durable
  `allowedTools` persist ran first, unconditionally. Persistence is now
  gated on the hub actually accepting the decision for the live request.
- **A language switch no longer stacks duplicate permission cards.** The
  floating permission window's CreateGUI gained the same re-entry guard
  the main panel already had (the rebuild path re-invokes CreateGUI on
  every open window of both types).
- **Corrupt-store recovery got gentler in one more place:** raw Console
  error text (braces, quotes, stack traces) moved out of the UnityYAML
  `State.asset` -- the exact string class that historically corrupted the
  whole asset and every setting in it -- into a JSON sidecar, with a
  one-time migration that preserves existing ignore lists.

### Added

- **A confirmation dialog before escalating auto-approve to "All Unity
  ops"** (EN+JA), on BOTH write surfaces, gated before the assignment
  because applying the level immediately auto-answers a waiting request.
  The header menu visually separates the dangerous level; downward moves
  stay frictionless.
- **The not-logged-in setup card now appears BEFORE the first send
  bounces.** A one-shot boot-time `auth status` probe feeds the first-run
  decision; "Check again" also re-queries, so logging in from a terminal
  is picked up. A pending or failed probe never flashes the card.
- **A fail-safe IME composition-confirm Enter guard** (the UX spec's "most
  important detail for Japanese users"): while composition text is present,
  Enter neither sends nor inserts, on both KeyDownEvent shapes. Where the
  composition signal never fills, behavior is byte-identical to before --
  the guard can only prevent mis-sends, never eat a real one. The
  real-IME manual pass (risk 11) and the Ctrl+Enter escape hatch remain.
- **CI (INFRA-1).** `dotnet-smoke.yml`: seconds-per-push license-free tier
  compiling the Unity-free package sources verbatim. `editmode-tests.yml`:
  the full EditMode suite inside the GameCI editor image, activating a
  Personal seat with the Unity Licensing Client from email+password alone
  (no serial, no .ulf) and returning the seat afterward. `ci/HostProject/`
  hosts the package deterministically. ~15 new or extended test files.

## [0.24.1] - 2026-08-14

Reported within hours of 0.24.0, both against the new history row menu
(design note 2026-08-14-history-menu-and-token-hygiene.md, amended):
the popup is visibly taller than its rows, and opening "Move to group"
teleports it to the screen's top-left corner.

### Fixed

- **The popup no longer jumps to the screen origin on the page swap.**
  Setting geometry (minSize/maxSize/position) on a live ShowAsDropDown
  window relocates it to (0,0) -- measured: position read back fine at
  (482,282), the set landed at the origin. The swap is now a
  close-and-reopen at the original anchor, which also re-runs the
  dropdown's own screen-edge clamping for the new page's size -- the
  in-place path never clamped at all.
- **The popup is sized to its rows.** The first release estimated
  24pt/row, 9pt/separator, 8pt chrome; the rendered boxes measure 19/5/6,
  so every menu carried ~25% of dead space. The constants are now the
  measured values: the 5-action main page went 146pt -> 111pt, and both
  fixes were verified live from a real dispatched click (swap position
  identical to the anchor position, sizes matching the formula exactly).

The two items deferred from the 0.23.0 audit, implemented in the order the
user chose (docs/design-notes/2026-08-14-history-menu-and-token-hygiene.md).

### Changed

- **The history row's "..." menu is now the panel's own popup, not a
  native OS menu.** Same actions, same handlers, same delete gating --
  but styled like everything around it, and better in two ways the
  native menu could not manage: a disabled Delete now explains WHY on
  hover (another project's transcript vs the currently running
  conversation), and the group submenu became a second page of the same
  popup, so user-typed group names no longer need their '/' characters
  flattened away. Item order, checked states and the disabled reasons
  live in a pure tested model (26 tests). The adversarial review caught
  the group page growing without bound with the user's group count --
  the popup now caps at 320pt and scrolls (the native menu's one real
  advantage, reimplemented). Live-verified opening at the computed size
  from a real dispatched click; holding open under a real mouse click
  rides the same ShowAsDropDown mechanics as the long-shipped usage
  popover.
- **Token hygiene (audit bundle E).** The Settings toggle's ON color now
  actually references the link accent it always claimed to match (the
  first token-to-token var() in the codebase -- engine resolution
  live-measured at exactly #4C7EFF, because no static test can prove the
  resolver follows the chain); one pill shape token replaces four 10px
  literals and .uap-pill's odd 8px; the first-run card finally uses the
  card radius token; the one warn-named class that resolved to error red
  is renamed to what it is; the one stray button padding joined the
  standard family; and the near-twin button classes now carry a comment
  explaining why merging them would be a visible design change, not
  cleanup.

Requested: a panel-wide UI design brush-up. Five parallel critics audited
window-content screenshots against the stylesheet's real values; the user
selected three of the four resulting bundles
(docs/design-notes/2026-08-14-ui-polish-audit.md; token-hygiene bundle E
deferred).

### Changed

- **Color foundation (every surface).** Secondary text now clears WCAG AA
  contrast on both themes (dark #909090 -> #ACACAC, measured ~4.6:1;
  light #555555 -> #535353) -- tool summaries, role labels, history
  subtitles, option descriptions and tab labels all read better. The
  always-visible auto-approve header chip's strongest tier moved off the
  ERROR red onto a new dedicated caution token, so full-saturation red
  again means "something is wrong right now", not "a setting is on".
  Disabled buttons are now themed (previously the editor's unthemed
  fallback), and the keyboard-driven permission surfaces finally show a
  focus ring -- the card that advertises Y/N hotkeys had no visible focus
  indicator anywhere.
- **Chat surface honesty.** The CLI process-died/reconnecting note now
  renders as a warning (icon color + tone) instead of the same grey as
  routine narration, the status dot turns warn-colored while a reconnect
  is in flight, and the flag survives the domain reload that usually
  accompanies exactly that note (a review catch: the first implementation
  lost it in the session cache round-trip). Tool cards for
  AskUserQuestion now summarize the actual question instead of printing
  the tool name twice, and the thinking foldout shares the tool cards'
  "bordered row = expandable" language.
- **Cards.** The synthetic "Other..." option now looks like a
  compose-your-own entry (sunken, italic) rather than a fifth prewritten
  answer -- the modifier class existed since 0.22.0 with no styling rule
  attached. The question window's height now follows its content
  (one-question cards no longer open with ~45% dead space; clamp ceiling
  unchanged). Skip is visually quieter than Submit, the floating tool
  card no longer truncates its title over a description it also renders
  in full one line below, both free-text fields gained persistent
  captions (2022.3 TextFields cannot render placeholders), and the card
  headline finally uses the same title size as every other headline.
- **History and Settings.** Truncated history titles/previews are
  readable at last via tooltips (the audit's one critical: two real
  sessions differing by one clipped word were indistinguishable), the
  search field no longer strands the archive toggle on its own row, group
  headers read as landmarks, and the refresh button joins the icon-button
  family. Settings now leads with Conversation and Model -- the sections
  a working install actually revisits -- with CLI moved down beside
  Diagnostics; section titles are brighter and bigger than the content
  they label, and topic breaks inside cards got real spacing.

## [0.22.0] - 2026-08-14

Reported: (1) the script-apply tool is broken -- every commit fails with
CS0012 "You must add a reference to assembly 'UnityEngine.CoreModule'"
(docs/design-notes/2026-08-13-scripts-commit-cs0012.md); (2) once a console
error appears, the fix button stays on the panel forever -- including for
SDK/extension noise the user never intends to fix
(docs/design-notes/2026-08-13-error-chip-ignore.md).

### Fixed

- **uap_scripts_commit no longer fails with CS0012 the moment a staged
  script touches an existing project type.** AssemblyBuilder's default
  reference set carries only the monolithic UnityEngine.dll facade (246
  refs, measured live), while every already-compiled project assembly --
  Assembly-CSharp, package assemblies, SDK dlls -- is compiled against the
  engine MODULE dlls, so deriving from (or using) any of their types
  demanded UnityEngine.CoreModule by name and failed. The staged validation
  now compiles with ReferencesOptions.UseEngineModules (312 refs, CoreModule
  included); both the self-contained MonoBehaviour case and the
  derive-from-compiled-type case were probe-verified clean, the historical
  CS0433 duplicate-type failure does not return, and a regression test
  stages a UnityEngine.UI.Selectable subclass -- the exact shape the old
  fixture never covered, which is why the bug shipped.
- **A compile no longer resurrects a dismissed error chip.** The old
  dismiss remembered only the error COUNT, so any count change -- including
  the count DROP every compile causes by superseding old compiler errors --
  brought the chip back with stale errors. Chip visibility is now keyed by
  message content.

### Added

- **"Do not fix" is now a real state for console errors.** The error chip's
  X permanently ignores every error it currently shows (per exact message,
  stored in the project, survives editor restarts); a new Console errors
  settings section manages the ignored list (per-row remove, clear all) and
  adds free-text ignore patterns (one substring per line) for SDK/extension
  errors whose wording varies. Ignored errors never re-show the chip, are
  excluded from the fix prompt sent to Claude, no longer flip the
  post-compile auto-continue verdict to "failed", and no longer trigger the
  empty-state "fix the console errors" suggestion. Pressing the fix button
  hides the chip only while no NEW error arrives -- deliberately not
  persistent, so a failed fix brings the chip back.
- AskUserQuestion gains a free-text "Other..." answer on every question
  (panel-side collection is test-pinned; the wire echo of a free-text answer
  rides the next real panel use).

## [0.21.1] - 2026-08-12

Reported: "Always allow" on WebFetch does not stick -- the panel asks again
every time (docs/design-notes/2026-08-12-always-allow-rulecontent.md).

### Fixed

- **Accepting a domain-scoped Always-allow rule now persists the rule the
  user actually accepted.** The suggestion extraction silently discarded
  ruleContent, which broke in both directions at once: the menu labeled a
  domain-scoped WebFetch rule as if it covered the whole tool, and the
  panel-side persistence -- the half that survives the CLI restarts this
  panel performs on every domain reload and settings auto-apply -- stored
  either nothing or a BARE tool name, a wider grant than the accepted one.
  Scoped rules now persist in the settings-file grammar
  (WebFetch(domain:example.com)), all rules of a suggestion are kept rather
  than the first, and a dedicated regression test pins that the bare name is
  never derived from a scoped rule -- that regression would be a silent
  permission widening.
- Why the panel forgets what a terminal remembers: a destination:"session"
  grant lives only as long as the CLI process, and the panel restarts the
  CLI far more often than a terminal user ever would. Persisting the
  accepted rule into allowedTools (passed as --allowedTools on every spawn)
  is what makes Always mean always here.

## [0.21.0] - 2026-08-12

Reported: nothing on screen changes between pressing the uLoop install button
and the install landing, and the confirmation UI that opens is over-
explanatory (docs/design-notes/2026-08-12-uloop-install-progress.md).

### Added

- **Live install progress.** The panel now polls the Package Manager request
  it has been carrying since 0.18.1 and shows "Installing... Ns" with real
  elapsed time, survives the domain reload a successful install triggers (a
  SessionState marker keeps the state alive; older than 5 minutes degrades to
  "check the Package Manager window"), and -- for the first time -- shows a
  resolve FAILURE inside the panel, with Package Manager's own error message.
  Previously a failed resolve looked identical to a successful dispatch: a
  static "install requested" sentence and a status stuck on "not installed"
  forever. The install button is disabled while a request is in flight, so a
  second Client.Add cannot race the first.
- Verified against two real installs in a throwaway project: one that
  genuinely failed (a transient file lock -- Installing then FailedAsync at
  26.5s, an outcome that used to be invisible) and one that succeeded
  (Installing then Succeeded at 21.8s). On the success, the manifest's
  dependency key appeared BEFORE the request object reported completion,
  which is why "installed" is only ever derived from the on-disk detector,
  never from the request status.

### Changed

- **The confirmation card went from 222px to 99px** (same synthetic plan,
  measured both times). The route sentence no longer carries two full file
  paths inline -- one short line, paths on its hover tooltip -- and the raw
  JSON diff sits in a collapsed "Show changes" foldout instead of always
  expanding. The caveat warnings stay visible; a warning you must hover to
  read is not a warning. No progress bar: Client.Add exposes no fraction, and
  drawing a fake one would report an outcome nobody observed -- honest
  elapsed seconds instead.

## [0.20.3] - 2026-08-05

The user refuted 0.20.2's "wait for the next turn" answer by pointing out
that switching to another History entry and back fills the usage popover in
immediately. They were right about the implication: that workaround works by
rebuilding the breakdown from the CLI transcript -- so the data was on disk
and the rebuild code already existed, and only the boot path declined to use
them (docs/design-notes/2026-08-05-boot-model-usage.md section 6).

### Fixed

- **Boot now backfills the per-model breakdown from the transcript** when the
  cache has none to offer but the session has completed work -- the same
  rebuild History switching performs, plus a save so the next boot takes the
  cache fast path. The scan only ever runs when the breakdown is missing;
  fresh projects skip it outright and steady state never pays it. Verified on
  the exact reported session: after a domain reload, the claude-opus-5 row
  came back with no new turn, and the cache now carries the key.
- Known limit, same as History restore since 0.15.0: transcripts carry no
  context-window figure, so the context meter's percentage may still wait for
  the first live turn even though the popover rows are populated.

## [0.20.2] - 2026-08-05

A follow-up caught by the user's screenshot after 0.20.1 shipped: a session
restored from a pre-0.20.1 cache showed "2.0k tokens" in the status bar while
the usage popover said "No completed turn yet." -- false, and measured live:
the running code was 0.20.1, but the on-disk cache had been written by 0.20.0
and so carries no per-model breakdown until the next turn completes (the
documented one-time upgrade state).

### Fixed

- The popover's empty state now distinguishes its two truths: a session with
  no turns at all keeps the old message, and a session with restored totals
  but no per-model breakdown says the breakdown appears after the next
  completed turn -- instead of denying, next to a visible token total, that
  any turn ever happened.

## [0.20.1] - 2026-08-05

Reported: opening the Agent Panel for the first time after launching the
project shows the session's tokens as absent
(docs/design-notes/2026-08-05-boot-model-usage.md).

### Fixed

- **Per-model usage now survives an editor restart.** Reproduced with a real
  two-process experiment (plant a known cache, read it from a fresh Unity):
  the token TOTAL restored fine ("19.1k tok"), but the per-model snapshot --
  the only data source for the usage popover and the context meter -- lived
  in a plain static and came back with 0 entries, leaving both empty until
  the first turn completed. Restoring a session from History was never
  affected (it rebuilds the snapshot from the transcript, since v0.15.0),
  which is exactly why the gap only showed at first open. The snapshot is
  now persisted in the session cache and restored on boot; the same
  experiment now returns the planted per-model numbers in the fresh process.
  Caches written by older versions read as "no per-turn data yet", not as an
  error.

## [0.20.0] - 2026-08-05

Reported: multiple AskUserQuestion questions try to render in one screen and do
not fit; the floating window has no working scroll; the options make the card
too cramped (docs/design-notes/2026-08-05-askuserquestion-stepper.md).

Measured with a real-shaped 4-question request in the 420x360 window: the card
was 736px tall -- twice the window -- and the ScrollView that has been there
all along never engaged, because nothing in the chain above it constrains its
height (UI Toolkit defaults flex-shrink to 0; third recurrence of that exact
class in this package). Each question costs ~175px, so stacking four costs
699px.

### Added

- **Claude Desktop-style stepper for multiple questions.** Two or more
  questions render as a tab strip (the question's header, or a numbered
  fallback) with ONE question visible at a time. Tabs navigate freely and mark
  answered questions; a single-select answer auto-advances to the next
  unanswered question. multiSelect questions never auto-advance -- the UI
  cannot know when a toggle set is complete. Single-question requests render
  exactly as before. Submit/Skip semantics are unchanged.
- The floating window opens at 560x520 for question requests (options carry a
  label plus a description; at 420px the description wraps to three lines).
  Tool requests keep 420x360, and a window the user already has open is never
  resized.

### Fixed

- **The floating window now actually scrolls.** The whole chain from the
  window host down to the details area opts into shrinking, so the ScrollView's
  viewport is bounded by the window instead of growing past it. Verified live:
  in a deliberately tiny 240px window the viewport (169px) now sits below the
  content (238px) and scrolls; before the fix the same card simply overflowed.
  This also fixes plain tool-permission cards in the window, which had the
  same defect.
- Switching questions resets the shared scroll position, so a question opened
  after scrolling deep into the previous one starts at its header rather than
  mid-options.
- Hiding a question section blurs any focus left inside it -- the same
  keyboard hazard this card already guards against in its wait-bar path.
- An emoji-only question header falls back to the numbered tab label instead
  of rendering a blank tab.

The diff itself went through a three-dimension adversarial review (10 findings,
9 survived three-lens verification): three product fixes above came out of it,
four of its findings hardened the new tests -- including source scans that a
comment inside the rule block could satisfy -- and one was rejected with the
rationale recorded in the design note.

## [0.19.0] - 2026-08-04

Reported: the Settings panel carries more annotation than it needs, and reading
it is work (docs/design-notes/2026-08-04-settings-annotation-load.md).

Measured in the live editor before changing anything: the panel scrolled 3158px
in a 574px viewport, and 1180px of that -- 40% of the cards' height, 2.1
screenfuls -- was explanatory labels. 49 of them for 54 controls. The load was
not spread evenly either: two sections held 51% of the text on 17% of the
controls, and the longest single string was 402 characters of prose sitting
next to a checkbox. Reading the text showed why: design rationale had been
written into the UI.

After: 2686px total, 728px of annotation, 29% of the cards. The panel is a
screenful shorter and the annotation is down by more than a third.

### Changed

- **Inline hints are one short line.** Nothing was deleted -- the long form
  moved into 28 new hover tooltips, attached to the field's row so both the
  label and the control show it. Longest inline hint is now 109 characters in
  English (was 402) and 70 in Japanese (was 235).
- **The three safety annotations stay on screen**, in a new warning style: the
  script-validation gate, the auto-approve level, and auto-continue after
  compile. Their text says what enabling them lets the agent do, and a warning
  nobody can read without hovering is not a warning.
- **Apply-timing sentences moved to tooltips.** The same "applies on the next
  reconnect" idea was written thirteen different ways, one per field, costing
  256px. The panel already shows a pending-changes indicator driven by an
  actual comparison against the last-spawned settings, which is both shorter
  and more accurate than a static per-field claim.
- A length cap on hint strings is now enforced in both catalogs, so the next
  feature cannot quietly grow another 400-character hint. The three warnings
  are exempt by an explicit allowlist -- itself checked for staleness -- and
  still capped, more loosely.

### Fixed

- Two dead localization strings for a control that no longer exists
  (`SettingsUapOpsAutoApproveReadOnly*`, superseded by the auto-approve level).
- The uLoop preset's description did not mention that it now removes a broad
  `Bash(uloop *)` from the allowed tools, which 0.18.1 made it do.

## [0.18.2] - 2026-08-04

The four confirmed review findings 0.18.1 left on the table
(docs/design-notes/2026-08-01-phase5-unity-ops-design.md section 9.4.4).

### Fixed

- **A path replayed under the wrong `includeInternal` mode could operate a
  different control and pass every check.** The two modes number children
  differently, so the same path can address two elements -- and the type/name
  fingerprint cannot tell, because the candidates are often the same type with
  the same empty name. A Foldout whose first content child is a Toggle is the
  concrete case: `"0/0"` is the foldout's own header toggle under
  `includeInternal:true` and the user's toggle under the default. When the
  caller does not state the mode, both are now resolved and the call is
  refused if they disagree.
- **The out-of-range path error blamed the wrong thing.** It said the tree had
  changed and to re-run the dump, so an agent re-dumped in its original mode,
  got the identical path, and looped. It now names the mode it walked and the
  one to try.
- **Two windows of the same type and title were indistinguishable.** The
  ambiguity error rendered them as identical text while asking the caller to
  pick one, and `uap_editor_ui_list_windows` emitted no index to pick *with*.
  The listing now reports `windowIndex`, the same number the other ui tools
  take, and each candidate in the error carries its index, screen position and
  focus.
- **Installing uLoop rewrote every line of a CRLF `manifest.json`.** The
  pretty-printer always emits LF, so on Windows git reported the whole file
  changed for what the confirmation card presented as a three-line insert --
  and the card could not show it, because it normalized both sides before
  diffing. The writer now matches the source file's line endings.

## [0.18.1] - 2026-08-04

An adversarial review of the Phase 5c code that had never had one, plus the
first real run of the uLoop installer. 42 findings raised, 32 survived
three-lens verification, one of them a regression shipped hours earlier in
0.18.0 (docs/design-notes/2026-08-01-phase5-unity-ops-design.md section 9.4).

### Fixed

- **The 0.18.0 uLoop allowlist did nothing for the people it was written
  for.** Applying the preset only ever appended, so on any machine where an
  earlier version of the same button had written `Bash(uloop *)`, the eight
  narrow patterns landed beside a surviving wildcard and changed nothing --
  and `uloop execute-dynamic-code`, which compiles and runs arbitrary C#
  inside the editor, stayed auto-allowed. The users who pressed the old
  button are exactly the uLoop users, so 0.18.0 protected only the people who
  never needed it, while its help text told everyone otherwise. The preset now
  strips wildcards over the whole `uloop` command before merging. The existing
  tests passed throughout because one inspected a freshly built list and the
  other merged onto an empty one; neither went through the upgrade path.
- **`uap_editor_ui_click` still reported successes it had not observed.** The
  0.18.0 check subscribed a probe to `Clickable.clicked` -- so on a control
  with nothing wired to it, the probe was the only subscriber and fired every
  time. Measured live: an inert Button and a Toggle both reported
  `clicked: true` while nothing happened. `Clickable` also has a second
  handler channel (`clickedWithEventInfo`), which is where Toggle keeps its
  behaviour. The pre-existing delegates are now wrapped rather than joined, so
  only code that was already there can set the flag, and bool-valued controls
  are refused outright naming `uap_editor_ui_set_value` -- a dispatched click
  provably cannot change them.
- **`uap_editor_ui_set_value` reported the value it asked for, not the one the
  control kept.** A `SliderInt(11, 16)` given 100 clamps to 16, throws
  nothing, and the tool said 100. It now reads the value back and fails loudly
  on a mismatch.
- **Wrong-typed values were written as defaults and reported as successes.**
  `"abc"` on an IntegerField wrote 0; `"yes"` on a Toggle wrote false; an
  object on a TextField blanked it; `99` on a 4-member EnumField was written
  verbatim. Every scalar branch now checks the JSON type and refuses, matching
  the contract the enum-by-name branch already had. The same gap in
  `uap_property_set`'s numeric enum path -- found in passing -- is fixed too.
- **A DropdownField accepted a value absent from its `choices`**, leaving a
  control displaying nothing valid.
- **An invisible control could be clicked** and reported as clicked: the guard
  checked layout but not `visibility`, which the dumper already reported
  correctly.
- **A depth-truncated `uap_editor_ui_dump` reported `truncated: false`**, so an
  agent concluded the element it wanted did not exist.
- **`"windowIndex": null` silently meant index 0**, turning the loud
  "several windows match" refusal into acting on whichever window came first.
- **uLoop was reported installed the moment the install began**, and stayed
  reported even if it never resolved: the detector matched the package id
  anywhere in manifest.json, and the installer writes that id into
  `scopedRegistries[].scopes` itself. Only a dependency key counts now. The
  test suite had pinned this as intended behaviour.
- **The installer could destroy a manifest edited after the plan was made.**
  Apply wrote plan-time text over whatever was on disk and backed up the stale
  content, so a package added between Plan and Apply vanished from both. Apply
  now re-reads and refuses if the file changed.
- **Scoped-registry matching used the registry's display name**, so a project
  already on OpenUPM under a different name got a duplicate entry, and a
  trailing slash in the URL was reported as a blocking conflict. Matching is
  now by normalized URL, and an existing entry is merged into. A registry at a
  *different* URL that already claims the uLoop scope now blocks, since two
  registries claiming one scope resolve unpredictably.
- **Every install failure said "Nothing was changed"**, including the two
  codes that mean the manifest write failed *and* the undo failed. Those now
  say so, name the backup, and do not offer a retry.
- **Auto-continue could fire on a reload the user caused.** The attribution
  ticket had no expiry, so a script commit whose real compile failed (no
  reload) left it armed until the user hand-fixed the code hours later --
  whereupon the agent resumed on a fix it had not made. Tickets now expire.
  They are also consumed on every reload rather than only those where the
  client had been running, while the *send* stays gated on an actually
  interrupted turn.
- **The "the panel is sending the next message automatically" note was written
  before the send was attempted** and never retracted when it was dropped.
- **A queued continuation respawned a CLI the crash-loop guard had
  suspended**, once per editor update for a full minute.
- **A continuation turn that died left a flag** that silently disabled the next
  genuine auto-continue while blaming a setting that was switched on.
- Auto-continue's skip note no longer says "off in Settings" when the setting
  is on and the once-per-turn guardrail is what stopped it.

### Changed

- The install confirmation says the package resolves in the background (~30s,
  measured) rather than promising a reload that may not come, and the route
  description no longer claims a dependency edit the installer does not make.

## [0.18.0] - 2026-08-04

Phase 5c: UI Toolkit automation, uLoop integration, and auto-continue after
compile (docs/design-notes/2026-08-01-phase5-unity-ops-design.md sections 3c,
2, 3 and the new section 9).

### Added

- **`ui` module (default OFF): `uap_editor_ui_list_windows`, `_dump`,
  `_click`, `_set_value`.** Lets the agent drive editor windows that expose no
  menu path or API -- the third-party extensions Phase 5c exists for. UI
  Toolkit only: an IMGUI window gets a structured refusal that names
  `uap_editor_execute_menu` as the alternative, rather than a misleading empty
  dump. One address scheme round-trips `_dump` -> `_click`/`_set_value`, and a
  type/name fingerprint mismatch refuses loudly instead of acting on whatever
  element now sits at that path.
- **uLoop integration section in Settings**: install status, one-click install
  behind a confirmation card showing the real manifest diff, an allowed-tools
  preset, and a usage-guidance snippet. The installer writes only the scoped
  registry and lets Unity resolve the package version, so there is no version
  constant to go stale -- two machines here already disagreed (beta.48 vs
  beta.71), which is how that was found.
- **Auto-continue after compile (default OFF).** When the agent's own script
  commit triggers a domain reload, the turn resumes by itself instead of
  stranding the user. Guardrails: at most once per turn, only when the reload
  is attributable to the agent's commit, fail-closed when attribution is
  unknown, always a visible transcript note, and the continuation message
  carries the compile result so the agent never resumes on its stale
  pre-compile assumption.

### Fixed

- **`uap_editor_ui_click` reported `"clicked": true` while the button's
  callback never ran.** The implementation dispatched a synthetic
  MouseDown+MouseUp pair -- correct as far as Unity's source goes, since
  `Clickable` subscribes to exactly those events. Measured against a real
  laid-out button in a live editor, it did nothing. Nine dispatch strategies
  were tried; only invoking the `Clickable` manipulator works. **Synthetic
  event dispatch does not drive `Clickable` in 2022.3.** The tool now
  subscribes to the manipulator's `clicked` event around the invocation and
  reports what was actually observed, so a future regression surfaces as
  "dispatched but not observed" instead of a lie. Second instance of that
  defect class in as many releases (see 0.15.3, `uap_material_set`).

### Changed

- **The uLoop allowed-tools preset is now an allowlist**, not a broad
  `Bash(uloop *)` with the dangerous subcommands excluded. Measured against a
  live CLI: the exclusions did work -- a bare `uloop update` was refused under
  both shapes -- but the broad pattern silently auto-allowed
  `uloop execute-dynamic-code`, which runs arbitrary C# in the editor and is
  specified to always prompt. Deny-listing can only exclude the dangers
  someone already enumerated. The preset now allows `compile`, `get-logs`,
  `list` and `run-tests` and nothing else; everything absent still works, it
  just asks first.

## [0.17.0] - 2026-08-03

Three subagent/composer reports in one message
(docs/design-notes/2026-08-03-subagent-ux-and-midturn-input.md).

### Fixed

- **Subagent cards no longer forget their state every time they update.**
  The row is rebuilt wholesale on any structural change, and the mechanism
  for surviving that already existed in the code -- applied to exactly one
  of the five things that needed it. Nested tool cards' expand state and
  the card's inner scroll offset now survive too, keyed the same way, and
  the message list keeps its scroll position across the swap (staying
  pinned to the bottom if that is where you were).
- **A nested tool call finishing now actually updates the card.** Its
  status was not part of the rebuild signature, so a completed nested call
  kept spinning until something unrelated forced a redraw. Fixed with the
  minimum addition -- deliberately not the progress fields, which were
  excluded on purpose to avoid rebuilding the card on every progress tick.
- **A running subagent always shows what it is doing.** Measured on a long
  run: progress events arrive per tool step with a genuinely changing
  description every 2.4-4.4s -- but the first one only lands **5.7 seconds
  after the subagent spawns**, and the row used to hide itself entirely
  while it had nothing, which is exactly the window where you cannot tell
  work from a stall. The row now always shows something while running
  ("Working..." until the first report), adds the step count -- a field the
  panel was already storing, caching and restoring but rendering nowhere --
  and says how long since the last update so a frozen line is
  distinguishable from a stalled subagent. Long path descriptions are
  shortened rather than blowing out the row.

### Added

- **You can type while a turn is running.** The send button now stays
  "Send" whenever the field has text, and only shows "Stop" during a turn
  when the field is empty. Nothing was ever blocking the send path --
  pressing Enter mid-turn already worked -- but the only mouse-reachable
  control turned into "Stop", so the UI told you to stop and resend. Esc
  still interrupts unconditionally regardless of what you have typed.

  Measured against the real CLI, twice including during a subagent
  fan-out: a mid-turn message is **folded into the running turn** and acted
  on. It steers; it does not queue. So no queue was built -- one would only
  have delayed the input you want delivered now.

### Notes

- **A latent bug was fixed on the way in, and it would have been worse than
  the one being fixed.** The client counted an open turn per user message,
  citing a research section for "one result per message". That section only
  ever measured *sequential* sends. For a mid-turn send the CLI emits **one
  result for the whole turn**, so the counter would never have balanced and
  the panel would have stayed busy forever. The research note now records
  both probes, and an existing test that had encoded the wrong assumption
  was corrected rather than left to keep passing.
- Known limitation, unchanged: the preserved expand/scroll state lives in
  memory for the editor session, so a script compile still collapses cards.
- Known limitation, newly documented rather than fixed: a second steering
  message sent while an earlier interrupt is still unresolved is
  miscounted, because the interrupt flag is global rather than per-turn.
  Neither probe covered that nesting, and fixing it means redesigning the
  interrupt fence.

## [0.16.0] - 2026-08-03

Requested: long tasks get interrupted by permission prompts over and over
-- selectable permission modes like Claude Desktop's, especially an
automatic one
(docs/design-notes/2026-08-03-auto-approve-levels.md).

### Added

- **An auto-approve level, in the header and in Settings.** Four levels,
  each including the one above it, measured against the real 29-tool
  registry:

  | level | auto-approves | tools |
  |---|---|---|
  | Ask every time | nothing | 0/29 |
  | Read-only Unity ops | tools that cannot change anything | 8/29 |
  | Undoable Unity ops | + tools Undo can reverse | 18/29 |
  | All Unity ops | + tools Undo cannot reverse | 29/29 |

  Existing installs migrate onto **Read-only Unity ops**, which is exactly
  what the old "auto-approve read-only ops" toggle did, so nothing changes
  on upgrade.

  **No level ever auto-approves a non-Unity tool.** Shell commands, file
  edits and uloop always ask, at every level, for two concrete reasons: the
  script-validation gate's enforcement runs on the very round trip that
  auto-approval would skip, and auto-allowing the general-purpose escape
  hatch was already considered and declined in v0.14.0. The script gate
  still runs *before* any auto-approve check, so a gate denial wins even at
  the top level, and the end-of-turn warning about operations Undo cannot
  take back still fires.

  The control sits in the header rather than only in Settings because the
  moment you want it is the moment a card is blocking you -- and raising
  the level **resolves the request already waiting**, instead of only
  helping from the next one. No reconnect: that would kill the turn you are
  trying to unblock.

### Fixed

- `uap_ping` was classified as a mutating, non-undoable tool. It echoes a
  string and touches nothing; the read-only flag was introduced after that
  tool shipped and it was never revisited. Harmless while the flag only
  suppressed a card, wrong once it decides an auto-approve tier -- a tool
  that changes nothing was sitting in the tier reserved for work Undo
  cannot take back. Found by counting the real registry while verifying
  the levels.

### Notes

- **The CLI's own permission modes were measured and deliberately not
  used.** One run per mode against the real binary, watching the wire:
  `auto` and `manual` are accepted but resolve to `default` (both the
  `system/init` echo and the mode passed to the hook say so), so exposing
  them would promise a behaviour that does not happen; `dontAsk` auto-
  **denies** (the hook saw both writes attempted and neither landed);
  `acceptEdits` does skip the round trip, but only for Write/Edit-family
  tools, never for MCP tools -- and every Unity action here is an MCP tool.
  None of them addresses the reported problem.
- The same measurement established something previously unverified and
  reassuring: the script gate's PreToolUse hook fired **and its deny still
  blocked** in all five modes. That enforcement does not depend on the
  permission mode.
- `UssHygieneTests` gained a fourth guard: every `var(--x)` in the
  stylesheets must resolve to a definition, and a custom property defined
  in only one theme is flagged too. Added after finding a rule that
  referenced a property defined nowhere -- UI Toolkit silently ignores an
  unresolvable `var()`, so an error label had been shipping with no error
  colour and no test noticed.

## [0.15.3] - 2026-08-03

### Fixed

- **`uap_material_set` no longer reports success without setting the
  colour.** Reproduced by driving the real tool against a Standard-shader
  material and reading the material back after each call:

  | `value` sent | tool said | material afterwards |
  |---|---|---|
  | `{r,g,b,a}` | OK | correct |
  | `{r,g,b}` | OK | correct, alpha kept |
  | `[1,0,0,1]` | **OK** | **unchanged** |
  | `"#FF0000"` | **OK** | **unchanged** |
  | `{r:255,g:0,b:0}` | **OK** | **`RGBA(255,0,0,255)`** |
  | `"red"` | **OK** | **unchanged** |

  The colour branch read each component with the *current* colour as its
  default. That is what made alpha optional -- and it also meant any value
  without `r`/`g`/`b` keys resolved to the existing colour, wrote it back
  unchanged, and returned "success". `kind="vector"` had the same
  structure; `kind="float"` silently turned a non-numeric value into 0.

  The tool now accepts the shapes a model actually produces -- `{r,g,b,a}`,
  `[r,g,b,a]`/`[r,g,b]`, and `"#RRGGBB"`/`"#RRGGBBAA"` for colour, object
  or array for vector -- and **rejects anything else with an error naming
  the accepted forms and echoing what it received**. Omitted components of
  a recognised shape still inherit the current value, which was never the
  problem.

  Values outside 0-1 are applied exactly as given, because HDR and emission
  colours legitimately exceed 1 and guessing the unit would break them; the
  result now says so and points at the 0-255 mistake instead.

  The accepted shapes are also stated in the tool's input schema, which is
  what the model reads before choosing a shape at all.

- **Tool results are now verifiable.** `uap_material_set` reports the value
  it actually wrote and the previous one, e.g. `Set color property
  '_Color' on Assets/Foo.mat to RGBA(1.000, 0.000, 0.000, 1.000) (was
  RGBA(1.000, 1.000, 1.000, 1.000)).` A tool that says only "success" gives
  an agent nothing to check against, which is how this defect survived to a
  user report.

## [0.15.2] - 2026-08-03

### Fixed

- **Picking "project" or "custom group" in History no longer stretches the
  view sideways.** It was not the group list -- it was the filter bar that
  0.15.1 itself introduced, and it is the same widget and the same mistake
  as v0.9.0. A `PopupField` reports a preferred width driven by its
  *current value*, and it was declared `flex-shrink: 0`, so it could not
  give that width back. Measured live: the bar was 302.5 px wide while its
  children ran out to x=492 -- 190 px of overflow, which UI Toolkit turned
  into a horizontal scrollbar plus a blank strip, with the ScrollView
  actually sitting at `scrollOffset.x = 189.5`. Only the longer localized
  choices triggered it, which is why it looked like a bug in those two
  grouping modes.

  Three fixes, because one was not enough: the bar now wraps
  (`flex-wrap`) -- four controls simply do not fit on one line in a ~300 px
  docked panel whatever their shrink settings; the popup is now shrinkable
  and capped at 130 px, so no future value can drive the layout; and the
  History list's `ScrollView` now explicitly hides its horizontal scroller
  (`ScrollViewMode.Vertical` does **not** do that on its own -- the trap
  that let the whole list slide off-screen). After: no overflow, no
  scrollbar, no blank strip, and the filter bar reflows to a second line
  when the panel is narrow.

- **The first-run card's Browse button could be clipped away entirely.**
  Found by auditing every `BaseField` in a row for the same defect. The CLI
  path `TextField` declared `flex-grow: 1` and nothing else, so its
  `flex-shrink` was the implicit 0 -- and a machine-supplied path (an npm
  global install on Windows routinely runs 60-90+ characters) would push
  the Browse button out of the row. Unlike the filter bar there is no
  scrollbar to recover with: that row's ancestor sets `overflow: hidden`,
  so the button would be **unreachable**, on the one screen a user sees
  precisely because their CLI path needs fixing. Now shrinkable with a
  floor. `.uap-settings-qa-prompt` was hardened the same way, defensively.

### Changed

- `UssHygieneTests` gained a third curated guard: row-hosted fields that
  must declare `flex-shrink: 1`, seeded with all eight of them. Its doc
  comment records the three shipped occurrences and, deliberately, the
  exclusions -- the fixed-width label, the archived toggle and every button
  class are on the *opposite* side of this rule and must not be "completed"
  into the list later.

  The standing rule is now written down in full
  (docs/design-notes/2026-08-03-list-row-control-alignment.md section 8.4):
  `flex-shrink: 1` plus a `min-width` floor on every `BaseField` in a row,
  `max-width` as well when its content is an open-ended localized choice,
  and `flex-wrap: wrap` on the container once a row holds more than two
  interactive children. Worth stating plainly: this defect is invisible in
  English. Every occurrence has surfaced only on the longer Japanese
  strings.

## [0.15.1] - 2026-08-03

Reported live: in the new History list the "..." menu button's position
shifted with the title length, costing an eye movement on every row. The
user raised it as a standing rule -- a control repeated once per row must
form a straight column -- so this release fixes the row, adopts the rule,
audits the whole package against it, and adds a guard
(docs/design-notes/2026-08-03-list-row-control-alignment.md).

### Fixed

- **History's per-row "..." button now forms a straight column.** The
  content column beside it never declared `flex-grow`, and UI Toolkit
  defaults it to 0, so the column shrank to its content and dragged the
  button with it. Measured live with 18 real sessions: every row 300.5 px
  wide, the content column anywhere from 137.0 to 276.5 px, and the button
  spread across **139.5 px**. Now 0.0 px -- identical on every row. The row
  also became one hover/click target instead of just its text.
- **Long MCP tool names no longer break the tool and subagent cards.** The
  package-wide audit found the leading name label was unshrinkable and
  uncapped while rendering raw wire names -- and this package ships its own
  MCP server, so `mcp__unity-ops__uap_query_component_types` (41 chars) is
  already on screen and a 48-char name measures 300.5 px against a ~300 px
  column. It consumed the whole header, starved the summary and pushed the
  expand chevron out of a container that clips, making the card
  unexpandable. The label is now shrinkable, capped and ellipsized, and the
  `mcp__<server>__` prefix is stripped for display with the full name kept
  in the tooltip. The subagent card's progress line got the same treatment,
  so a card no longer shows a clean name at the top and raw plumbing below.
- **Permission prompts keep showing which MCP server a tool comes from.**
  An adversarial review flagged, and two rounds of verification wrongly
  dismissed, that shortening the name on the *approval* card removes the
  server id -- and its tooltip carried the shortened text, so the raw name
  was unrecoverable there. Since extension profiles can introduce
  third-party MCP servers, two servers can each expose a `delete_all` and
  both would have read as "Allow delete_all?". The approval card now shows
  `<server>: <tool>` and its tooltip carries the raw wire name. The
  informational cards keep the plain shortening; both transforms document
  why they differ.
- **Opening History no longer scans every transcript on disk.** The 50-row
  page cap added in 0.15.0 bounded element construction but not I/O: row
  assembly touched a lazy per-file property on every session, so the claim
  that History costs the same at 20 sessions and 2000 did not hold. Rows
  are now assembled from timestamps and panel-owned metadata alone, and
  only the page that survives filtering is read from disk. A non-empty
  search and project grouping still have to read everything -- they match
  and key off file contents by definition.
- Delete is no longer offered on the session that is currently running (the
  CLI still holds that transcript open), and a queued list refresh now
  waits while a rename or new-group field is open -- previously a turn
  completing in the background could silently discard what was being typed.
- An `AskUserQuestion` card no longer inherits the previous tool prompt's
  tooltip; the permission card reused one label across requests and only
  the tool variant reset it.

### Changed

- Row containers across the package (tool cards, subagent cards, permission
  summary, banners, quick-action and model-override rows) now also declare
  `justify-content: space-between`, and the shared button classes state
  `flex-shrink: 0` explicitly instead of relying on UI Toolkit's implicit
  default. Neither changes anything visible today -- they are the safety net
  that keeps a future edit from silently reintroducing the drift.
- `UssHygieneTests` gained two curated-list guards: classes that must
  declare `flex-grow: 1`, and row containers that must declare
  `justify-content: space-between`. Seeded with every class the audit
  confirmed, including two that were already correct but which the existing
  row-specific tests never actually checked.

## [0.15.0] - 2026-08-03

History rework, from two reports in one session: restoring a session lost
its usage numbers, and the list becomes unusable as sessions pile up.
Modelled on Claude Desktop's chat list
(docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md).

### Fixed

- **Restoring a session from History no longer resets its usage to zero.**
  `SwitchToSession` built a fresh session object and copied only the
  messages, so the status bar read "0 tok" and the "Usage this session"
  popover read "No completed turn yet" for a conversation that had clearly
  used tokens. The transcript is now scanned for usage during the load it
  already performs, at no extra I/O. Verified live on a real 7.1 MB
  transcript: 59.9k tokens where it previously showed 0.

  The measurement that shaped this: one API response is written to the
  transcript as SEVERAL lines, each repeating the same usage object (23
  assistant lines for 16 distinct message ids in one file; 101 for 68 in
  another). Summing per line inflates every number by roughly 2x, so usage
  is deduplicated by message id. Locally-generated `<synthetic>`
  placeholder messages are excluded so they cannot appear as a model in the
  popover.

  Two things genuinely cannot be restored and are not faked: **cost**
  (no cost field exists anywhere on disk -- it is stream-only) and the
  **context-window percentage** (same). The context meter stays hidden
  until the next turn completes, which is already its "no data" behaviour.
  Subagent tokens, which live in separate transcript files, are not summed;
  how far that diverges from a live total is unmeasured rather than assumed
  to be zero.

### Added

- **Session search.** Incremental, case-insensitive, matching the title,
  the first user message and the session id.
- **Conversation titles from the CLI.** Rows now lead with the CLI's own
  generated title (`ai-title`), with the first user message as a dimmer
  sub-line, instead of showing raw first-message text as the heading. All
  18 transcripts on the development machine carried one, always within the
  first handful of lines, so it folds into the existing bounded preview
  scan at no extra cost.
- **Grouping**, switchable between date (Today / Yesterday / Previous 7
  days / Previous 30 days / Older), Unity scene, Unity project, and custom
  user-defined groups. Pinned sessions form a leading group in every mode.
- **Pin, archive, rename and delete** per row. Archived sessions are hidden
  behind a toggle; rename overrides the CLI title and can be reset back to
  it.
- **Incremental paging** -- 50 rows, then "Show N more" -- so opening
  History costs the same whether you have 20 sessions or 2000.
- **Cross-project browsing** in project-grouping mode, restricted to real
  Unity projects (a `ProjectSettings/ProjectVersion.txt` check keeps
  throwaway CLI working directories out; it cut 30+ down to 2 on the
  development machine). Sessions belonging to another project are
  browse-only and say so: resuming one against this project's working
  directory would hand the CLI a transcript whose file references and MCP
  servers belong somewhere else.

### Notes

- Deleting a session **moves** its transcript to
  `UserSettings/AgentPanel/DeletedSessions/` rather than unlinking it, and
  the confirmation names that folder. The transcript is the CLI's own
  canonical record of a real conversation, living under the user's
  `~/.claude`; destroying it irreversibly on one click is not a call this
  panel makes on its own.
- Scene grouping only covers sessions from this version onward. Transcripts
  carry no scene information whatsoever -- verified across every sampled
  file -- so the panel now records the active scene per session itself, and
  older sessions group under "Scene not recorded". That is a data
  limitation, not a defect.
- Pin/archive/rename/group state lives in a new
  `UserSettings/AgentPanel/SessionMeta.json` sidecar, not in the
  ScriptableSingleton asset: renamed titles are raw user text, and
  UnityYAML round-trips such strings unreliably -- the same defect that
  forced the transcript cache out of that asset.

## [0.14.1] - 2026-08-03

### Fixed

- **Creating a New Scene no longer breaks the panel's Japanese font.**
  Reported live: `File > New Scene` refilled the console with
  `MissingReferenceException` / `ArgumentNullException` /
  `NullReferenceException` out of TextCore on every repaint of the panel.
  `FontAsset.CreateFontAsset` hands back a material and atlas textures with
  no hide flags, and v0.14.0 marked only the FontAsset itself
  `HideAndDontSave` -- so a scene unload destroyed all of its children
  while the asset survived holding dangling references to them. Measured
  in the sandbox with a controlled pair of assets: `NewScene` destroys 9/9
  unstamped atlas textures plus the material, and 0/9 stamped ones
  (`Resources.UnloadUnusedAssets`, the intuitive suspect, destroys
  neither). The children are now marked too. Because TextCore creates each
  *additional* atlas texture unmarked as glyphs are rasterised, an
  `EditorApplication.update` guard -- an `int` comparison per frame in the
  steady state -- re-marks them whenever the atlas count changes; verified
  live, where two textures added mid-session read unmarked in that frame
  and marked in a later one.
- **An already-broken font asset now heals on update instead of lasting
  the whole editor session.** A font asset broken this way cannot be
  repaired (`TryAddCharacters` returns false) and, carrying
  `HideAndDontSave`, it survived every subsequent domain reload -- so the
  resolver kept handing the same unusable asset back and the errors never
  stopped. The resolver now health-checks what it finds and replaces it.
  Verified live: the editor that was flooding recovered on the domain
  reload that installed the fix, no restart needed, console errors 12 → 0.
  (The health check counts only the atlas slots actually in use: the array
  is over-allocated with legitimately null spares, and checking its full
  length would report every healthy asset as broken.)
- Turning extension profiles off, or approving a project-local profile,
  left the panel permanently claiming "settings changed -- reconnect to
  apply", and reconnecting never cleared it. The snapshot of "what the
  running session was spawned with" omitted `extensionProfilesEnabled`
  and `approvedProfileHashes`, so it always carried their defaults
  (profiles ON, no approvals) instead of the values actually used --
  while the change detector compares both. Every reconnect rebuilt the
  snapshot with the same wrong defaults, so the state could never
  settle. Both fields are now copied (the hash list deep-copied, since
  the approve/revoke UI edits it in place). Regression tests exercise the
  real snapshot path; verified by reverting the one-line fix and watching
  exactly those guards fail.
- Removed a leftover unused variable in the module-defaults migration that
  the compiler flagged (CS0219). The method still reports "changed"
  unconditionally on purpose -- stamping the migration generation is
  itself the change that must be saved, and a test now pins that so the
  warning cannot be "fixed" into a real bug where the stamp never
  persists and the migration re-enables modules the user turned off.

## [0.14.0] - 2026-08-03

Three problems reported from one real working session, all traced to
measurements before anything was changed
(docs/design-notes/2026-08-02-noise-permissions-and-uloop-preference.md).

### Fixed

- **The console no longer floods with errors.** The live log held 1106
  copies of one exception, produced by two of our defects feeding each
  other: the panel destroyed its Japanese UI font asset just before a
  domain reload while labels still referenced it (every repaint in that
  window threw on the destroyed material), and the console-error watcher
  then reacted to those log lines by editing a label *during* a repaint,
  which threw again, which logged again. The font asset is now tagged and
  reused across reloads instead of destroyed (measured: it survives
  intact, and re-creating it blindly leaks one per reload), and log
  handling is deferred to the next editor tick behind a re-entrancy guard
  (also making it safe when Unity raises logs off the main thread).
  Verified live: a reload that previously produced a burst now adds zero.
- **The transcript is no longer buried in approval receipts.** A single
  session had 32 of its 36 messages taken up by "<tool> approved" notes.
  Allow decisions no longer write a note at all -- the tool card already
  shows the call, its input and its result. Denials (including the script
  gate's) still do, because a refusal is context worth keeping.
- **Read-only Unity tools stop asking for permission.** Inspecting the
  hierarchy, listing components, querying component types, finding
  assets, reading prefab overrides and taking a screenshot cannot change
  the project, so they are auto-approved (Settings > Unity operations,
  on by default). Everything that mutates still shows its card.
  Verified live: a two-tool read-only turn now completes with zero
  prompts and zero notes, where the same work used to interrupt twice.
- **"Always allow" now works for the panel's own Unity tools.** When the
  CLI offers no rule suggestion for an `mcp__…` call, the panel
  synthesizes one for that exact tool name, and an accepted rule is
  remembered so later sessions do not ask again.
- Read-only tools are no longer named in the "this turn ran operations
  Ctrl+Z cannot undo" warning: they change nothing, so there is nothing
  to undo (caught in live verification of this very release).

### Changed

- **Claude is now told to prefer the panel's Unity tools.** In the same
  session, animation work went through `uloop`/dynamic code even though
  the typed anim tools were enabled -- nothing had ever told the agent
  they exist or were preferred. A short instruction block (derived from
  the *enabled* modules, so it never advertises a disabled tool) now
  names the tool families and marks raw dynamic code as the fallback for
  what they cannot express. uLoop is deliberately NOT restricted: it is
  what made that walk-cycle work possible; it just stops being the first
  reach.
- Tests: 1393 -> 1450 (0 failed).

## [0.13.3] - 2026-08-02

### Fixed

- **Shift+Enter really inserts a newline now.** v0.13.2 aimed at the wrong
  cause; an event/focus trace captured from a real keypress showed what
  actually happens (docs/research/05-ux-spec.md section 7): UI Toolkit's
  multiline TextField maps **Enter to newline and Shift+Enter to "end
  editing"** -- the inverse of this panel's convention -- so Shift+Enter
  moved focus out of the text element (the caret vanishing the user
  reported) and inserted nothing. Since the panel owns Enter for sending,
  it can never inherit UI Toolkit's newline either; both routes were
  blocked. The composer now performs the edit itself: the event is
  stopped so the end-editing path never runs, the selection is replaced
  with a line break, and the caret moves past it. Selection replacement,
  reversed selections, out-of-range carets and multibyte text are pinned
  by tests.
  (The v0.13.2 change -- letting the keycode event decide for the paired
  character event -- was based on a theory the trace disproved: the Shift
  flag IS present on both events. It is correct by construction so it
  stays, now documented honestly.)
- Tests: 1386 -> 1393 (0 failed).

## [0.13.2] - 2026-08-02

### Fixed

- **Shift+Enter inserted no newline in the composer**
  (docs/design-notes/2026-08-02-composer-newline-and-profile-row.md).
  Unity delivers one Enter press as two key events -- a keycode event and
  a paired character event -- and the handler re-derived "send or
  newline?" from the modifier flags on BOTH. When the character event
  arrives without the Shift flag, that second evaluation flipped to
  "send", swallowed the event, and then sent nothing (only the keycode
  branch sends), so the line break vanished. The keycode event now makes
  the decision once and the character event obeys it; the fallback for a
  character event with no preceding keycode event (some IME paths) is
  unchanged. Regression tests pin both event shapes, in both Enter-sends
  and Ctrl+Enter-sends modes.
- **Extension-profile rows were laid out with borrowed classes**: the SDK
  name used the Quick Actions column style (a fixed 110px non-shrinking
  box) so longer names were clipped, and the status used the block hint
  style (wrapping text with vertical margins) so it sat off the row's
  baseline. Profile rows now have their own styles -- the name shrinks and
  ellipsizes, the status is an inline chip, and the approve/revoke button
  keeps its no-shrink anchor. Measured live: the name column went from a
  clamped 110px to 253px and the row lost its stray extra height.
- Tests: 1374 -> 1386 (0 failed).

## [0.13.1] - 2026-08-02

### Fixed

- **Emoji in Claude's output flooded the Unity console with font
  warnings** ("The character with Unicode value ... was not found in the
  [] font asset") while a turn was running
  (docs/design-notes/2026-08-02-emoji-font-warnings.md). Unity's text
  generator logs once per missing character on EVERY text-generation
  pass, and two panel paths reassign their label text on a timer while
  streaming (the message pump at ~80 ms and the subagent progress line at
  500 ms), so one visible emoji kept re-emitting for as long as it stayed
  on screen. Model text is now sanitized before display: common emoji map
  to the panel's existing font-safe glyphs (check, cross, spark, chevrons,
  "!" / "?") so the meaning survives, and anything still uncoverable is
  dropped. Japanese, accented Latin, box-drawing (markdown tables) and
  math symbols are preserved untouched. Verified live: a reply containing
  the whole emoji set produced zero new warnings.
- The audit behind that fix also found several places where model- or
  user-authored text reached a label with no glyph sanitizing at all --
  permission-card descriptions and AskUserQuestion option text (including
  its tooltip), code-fence language tags, tool names, the session title,
  and the history preview row. All now go through the same sanitizer, and
  a source scan fails the test suite if a new render path skips it.
- Tests: 1355 -> 1374 (0 failed).

## [0.13.0] - 2026-08-02

Phase 5b: prefab override workflows, animation/material/importer tools, and
per-SDK knowledge profiles.

### Added

- **Prefab module** (default ON): `uap_prefab_create`,
  `uap_prefab_get_overrides` (lists property / added-component /
  added-GameObject overrides), `uap_prefab_apply_overrides` (instance ->
  ASSET) and, per the override-diff requirement, `uap_prefab_revert_overrides`
  (whole instance) plus `uap_prefab_revert_override` (one override at a
  time) -- so a prefab's overrides can be pushed *and* rolled back. Verified
  live end-to-end (create -> override -> list -> revert -> re-list).
  Undo behavior was MEASURED before implementation rather than assumed
  (docs/research/09-editor-api-surface.md section 9.5): reverting an added
  component or child undoes cleanly, but a property-override revert
  restores the value WITHOUT restoring Unity's override bookkeeping, so
  those tools declare themselves non-undoable rather than promising a
  Ctrl+Z that half-works. The probe also confirmed
  `PrefabUtility.RevertAddedGameObject` exists in 2022.3 (previously an
  open question) and that every tool must open its own undo group.
- **Editor module** (default ON): `uap_editor_screenshot` (SceneView /
  GameView capture), `uap_editor_execute_menu` (menu-item execution -- the
  reach-into-custom-UI path from the design's tier 2).
- **Anim module** (default OFF, opt in from Settings):
  `uap_anim_create_clip`, `uap_animator_edit` (parameters / states /
  transitions / listing), `uap_material_set` (with shader-property
  discovery, so third-party shaders like liltoon are workable),
  `uap_asset_set_property` (incl. importer settings) and a minimal
  `uap_avatar_configure`.
- **Extension profiles**: when a supported SDK is detected, a short factual
  briefing about it is added to Claude's instructions -- bundled profiles
  for VRChat SDK3, UniVRM, MagicaCloth2 and Final IK. Detection works via
  package id AND type scanning, so SDKs installed straight into `Assets/`
  (Final IK) are found too. Per the design's trust model, only bundled
  profiles inject automatically; a project-local profile under
  `.uap-profiles/` is shown in full and must be approved, and its content
  hash is pinned so any later edit revokes the approval until re-reviewed.

### Fixed

- New default-ON tool modules never reached settings written by an earlier
  version (the stored module list deserializes over the code default), so
  the prefab/editor families were invisible to the agent on any existing
  install -- caught by live E2E. Module defaults now migrate once, without
  re-enabling a module the user deliberately switched off.
- Adversarial review before release: `uap_animator_edit` no longer mutates
  the controller graph before finishing argument validation (a rejected
  call used to leave a half-built state/transition behind), and
  `uap_asset_set_property`'s importer mode refuses asset types whose
  reimport would trigger a script recompile.
- Tests: 1133 -> 1355 (0 failed).

## [0.12.2] - 2026-08-02

### Fixed

- **Subagent cards could not be expanded** once they came from a restored
  session (history switch, editor restart, domain reload) -- no chevron,
  clicks ignored (docs/design-notes/2026-08-02-subagent-card-not-
  expandable.md). Two causes, both fixed:
  - `TranscriptLoader` read the Agent tool_result envelope under its
    **stdout-wire** spelling `tool_use_result`, but the **on-disk**
    transcript spells it `toolUseResult` -- so every restored subagent
    lost its status, token totals and summary, leaving the card with
    nothing to show and therefore inert. Both spellings are now accepted.
    (Measured today; the existing test pinned the wire spelling in
    hand-written JSON, which is why the suite never caught it -- the new
    guard uses a sanitized copy of a real transcript line instead.)
  - Expandability was judged only on already-loaded content, ignoring the
    nested transcript the card lazy-loads from
    `subagents/agent-<id>.jsonl` on first expand. A completed subagent is
    now expandable whenever that sidechain lookup is possible, and an
    expand that genuinely finds nothing says so instead of opening a
    blank box.
- Tests: 1130 -> 1133 (0 failed).

## [0.12.1] - 2026-08-02

### Fixed

- Six tools from the Phase 5a core catalog (design note section 1.2) were
  missing from the shipped tool set -- found in the field within minutes
  when object DELETION turned out to have no tool. Added:
  `uap_scene_destroy_object`, `uap_component_remove` (with a proactive
  [RequireComponent] dependency check so a Unity-blocked removal can never
  report false success), `uap_scene_reparent` (cycle-safe, with
  worldPositionStays), `uap_scene_rename`, `uap_scene_place_asset`
  (prefab connection preserved via PrefabUtility.InstantiatePrefab), and
  read-only `uap_query_hierarchy` (depth/node caps plus an internal walk
  budget so huge scenes cannot blow up the call). All write tools carry
  the prefab-stage guard and full Undo support; verified live end-to-end:
  a six-operation chat turn (create x2, rename, reparent, inspect,
  destroy) reverted completely with a single Ctrl+Z.
- Tests: 1059 -> 1130 (0 failed).

## [0.12.0] - 2026-08-02

Phase 5a: the panel can now operate the Unity Editor directly through typed
tools, and agent-written C# is compile-checked before it can reach the
project (docs/design-notes/2026-08-01-phase5-unity-ops-design.md sections
1, 7, 8).

### Added

- **UapOps: an in-editor MCP server.** The panel hosts a loopback-only
  (127.0.0.1, dynamic port, per-session bearer token) Streamable HTTP MCP
  server and hands it to the CLI via `--mcp-config` on every spawn, so
  Claude can operate the editor through typed tools instead of writing
  throwaway scripts. Tool execution is marshaled to the Unity main thread;
  every tool call still goes through the panel's own permission card.
  Recovers after a domain reload via an `mcp_reconnect` control request
  (both the bearer round-trip and reconnect-on-resume were measured
  against the real CLI before implementation -- docs/research/08-mcp-
  transport.md section 5).
- **Core tool module**: create scene objects, add components, set
  properties, inspect objects/components, query component types, create /
  find / delete assets (deletion goes to the trash, so it is restorable).
  Each turn's operations are collapsed into ONE Undo group -- a whole
  turn's scene edits revert with a single Ctrl+Z (verified live). Tools
  that Unity cannot undo (asset creation, importer settings) are marked as
  such: their permission card shows a "not undoable" badge and the turn
  ends with a note naming them.
- **Script validation gate** (on by default): agent writes to
  `Assets/**/*.cs|*.asmdef` are blocked before touching disk; the agent is
  redirected to `<project>/UapStaging/` and the `uap_scripts_commit` tool,
  which compiles the staged files with AssemblyBuilder and only promotes
  them into Assets when they build. Broken code therefore never triggers a
  domain reload -- it comes back as compiler errors (file/line/column) the
  agent can fix. Enforced by a generated PreToolUse hook passed via
  `--settings`, because the CLI's permission-prompt hook is bypassed
  entirely under `--permission-mode acceptEdits` (measured; see Fixed).
- Settings gains a "Unity operations (UapOps)" card: master toggle, module
  toggles, script-gate toggle, and the server's running state/port.
- uLoop capability matrix: when uLoop is installed, UapOps skips
  registering tools uLoop already covers, so its CLI stays the one way to
  do those operations and no tokens are spent listing duplicates.

### Fixed

- The script gate originally relied on the permission-prompt round trip
  (`can_use_tool`), which live E2E proved is **skipped entirely for
  Write/Edit under `--permission-mode acceptEdits`** -- the gate never
  fired and a deliberately broken file landed in Assets. Re-implemented on
  a PreToolUse hook, which fires regardless of permission mode and
  delivers the panel's own actionable redirect text to the agent.
- A `permissions.deny` fallback shipped alongside the hook turned out to
  short-circuit it, replacing the actionable redirect with the CLI's
  generic denial (the live agent then gave up instead of using staging).
  The two are now mutually exclusive: the hook ships where PowerShell can
  run it, the deny rule only where it cannot.

## [0.11.0] - 2026-08-02

### Changed

- **Subagent model controls reordered around the measured precedence chain**
  (docs/design-notes/2026-08-02-subagent-model-precedence.md; measured:
  `CLAUDE_CODE_SUBAGENT_MODEL` env var > the agent's own per-call `model`
  parameter on the Agent/Task tool > `.claude/agents` per-type files >
  session-model inherit, R07 sections 12-13). The blanket env-var dropdown
  -- which crushes the agent's own per-task judgment -- moved into the
  advanced foldout as "Force subagent model", explicitly framed as a hard
  cost clamp normally left empty. The per-type table is reframed from
  "override" to "per-type default" (the agent's explicit per-call choice
  wins over it, which is the desired direction).

### Added

- **Subagent cost policy** (new primary control): "Agent decides
  (recommended)" or "Cost-saving: Haiku for simple tasks". The latter
  injects one imperative guidance line into the system prompt so the agent
  passes `model: "haiku"` on simple mechanical subtasks (searches, file
  listings, bulk renames, log scans) and keeps the session model for
  complex work. Verified live end-to-end: the parent attaches the model
  parameter and the subagent executes on Haiku. Honest caveat surfaced in
  the hint (and measured, R07 section 12.1): sessions created by OLDER CLI
  versions silently ignore the per-call model parameter -- a session
  started fresh from the header "+" always honors it.
- **Default-model display honesty**: the catalog's "Default (recommended)"
  entry now shows what it resolves to (e.g. "Default (recommended:
  opus-5 (1m))" -- the target is chosen server-side per subscription and
  cannot be changed by the panel, measured in R07 section 13); the hint
  under the dropdown shows the selected entry's live description; the
  header model menu marks the panel-default row with a "(default)" suffix
  independent of the current-session checkmark.

### Fixed

- ModelCatalogEntry cache now carries `resolvedModel`/`description`
  (additive; caches written by older versions keep working).

## [0.10.0] - 2026-08-02

### Added

- **In-panel Claude Code login / logout**
  (docs/design-notes/2026-08-02-auth-in-panel.md). Settings gains an
  Account card: authentication status (email + subscription plan when the
  CLI reports them), a Login button driving the CLI's own `claude auth
  login` paste-code OAuth flow entirely inside the panel (OAuth URL field +
  open-browser/copy buttons + authorization-code field), and a Logout
  button behind a confirmation dialog (the resident session is stopped
  first). The chat view's not-logged-in setup card gains a Login button
  that jumps straight to the Account card. Login success is never inferred
  from the login process exit code -- the panel re-queries
  `claude auth status --json` and trusts only that.
- Environment-token awareness: when authentication comes from
  `CLAUDE_CODE_OAUTH_TOKEN` in the editor's environment (measured live:
  `auth status` then reports `authMethod: "oauth_token"` with no
  email/plan), the Account card shows a clean "Logged in." line plus an
  honest note that in-panel login/logout changes the stored credentials
  but may not affect sessions while that token stays set.

### Fixed (during pre-release adversarial review)

- A pending `auth login` child process is now terminated on editor quit
  and domain reload, its handles disposed after exit/cancel, and
  post-login status queries can no longer race a concurrent refresh.
- The chat-view Login button now scrolls Settings to the Account card
  instead of dumping the user at the top of the settings list.

## [0.9.0] - 2026-08-01

### Changed

- **Default model vs current-session model are now separate concepts**
  (docs/design-notes/2026-08-01-model-settings-rework.md; reverses the
  v0.8.0 "single source of truth" design after user feedback). The
  Settings "Default model" dropdown now ONLY edits the persisted default
  used when a NEW session is started from the header "+"; it no longer
  live-switches the running session. Conversely, the header model picker
  now ONLY live-switches the current session (`set_model`) and no longer
  rewrites the persisted default. Spawns pass `--model` exclusively on
  fresh sessions; `--resume` spawns omit it so the CLI keeps the session's
  own model (measured: an explicit `--model` on resume would override it).
- The per-type subagent override table moved into a collapsed
  "Per-type overrides (advanced)" foldout, and its free-text agent-name
  field became a dropdown fed by the agent types the CLI itself reports
  (`system/init.agents[]`, cached as `PanelSettings.agentTypeCatalog`)
  plus any names already in the table.

### Added

- **Subagent model** dropdown (Settings > Model): one setting that applies
  to ALL subagents -- including custom types the panel cannot enumerate --
  via the `CLAUDE_CODE_SUBAGENT_MODEL` environment variable on the spawned
  CLI process. Unlike the per-type `.claude/agents` files (which the CLI
  snapshots at session creation), the environment variable is read fresh
  on every spawn including `--resume`, so changing this setting auto-
  applies to the CURRENT session within seconds through the existing
  idle-reconnect machinery (measured facts in
  docs/research/07-model-configuration.md section 11). When left at
  "(same as default)" the variable is not touched, so a value inherited
  from the editor's environment still applies.
- A warning under the per-type table when both it and the Subagent model
  are set: the environment variable overrides `.claude/agents` files
  outright (measured), so per-type entries are inert while the blanket
  setting is active.

### Fixed

- Settings > Model per-type rows: the model dropdown could not shrink
  (UI Toolkit's default `flex-shrink` is 0), so a long model name pushed
  the remove button off-screen. The name and model fields now shrink with
  sensible minimum widths; the remove button keeps `flex-shrink: 0` and
  always stays visible. Guarded by a UssHygieneTests source scan.
- A genuinely fresh spawn that went through `EnsureStarted` with an
  empty-string (not null) session id was misclassified as a resume and
  silently dropped `--model` (found by adversarial review; normalization
  now shared with the other spawn paths and unit-tested).

## [0.8.0] - 2026-08-01

### Added

- Settings > Model section (v0.8.0): a "Default model" dropdown sharing the
  exact same persisted field and live-apply path (`AgentHub.SwitchModel`,
  `set_model`) the header model picker already used -- picking a model here
  applies immediately when Claude is connected, or is used as `--model` at
  the next connection otherwise, with no separate apply mechanism. Choices
  come from the CLI's own model catalog (the "initialize" control_response's
  `models[]`), preferring the live list while connected and falling back to
  a lightweight cache (`PanelSettings.modelCatalog`, value/displayName only)
  refreshed on every `system/init` so the dropdown still works right after
  an editor restart, before this session's own connection has reported one.
- A subagent model override table in the same section: assign a different
  model to specific agents (e.g. `general-purpose`, `Explore`); agents left
  unlisted (or set to "(Default)") inherit the Default model above. Applied
  by generating one `<project>/.claude/agents/<name>.md` file per entry
  (`Colloid.AgentPanel.Model.AgentDefinitionFileWriter`, run before every
  spawn), NOT a `--agents` CLI argument -- see the Fixed entries below for
  why, and for why this only takes effect on a session *created* after
  that file write, never on reconnecting an already-running one. Persisted
  as `PanelSettings.agentModelOverrides` (docs/design-notes/2026-08-01-
  model-settings.md).

### Fixed

- The subagent model override table above originally shipped as a single
  `--agents '{"<name>":{"model":"<alias>"}}'` spawn argument. Follow-up
  end-to-end verification found the CLI silently ignores `--agents`
  whenever `--resume` is also passed (no error anywhere -- `exitCode:0`,
  `is_error:false`, `result.modelUsage` just never shows the overridden
  model) -- and the panel always spawns with `--resume` once a session
  exists, so the shipped mechanism never actually worked in real use. It is
  replaced with `.claude/agents/<name>.md` files (verified against a live
  CLI, including overriding a built-in agent name like `general-purpose`
  while keeping its full tool access -- docs/research/
  07-model-configuration.md section 10).
- The override table's UX also assumed that editing it and then
  reconnecting (`SettingsChangeDetector`/the existing settings auto-apply
  flow) would apply the change to the current session, the same as every
  other next-spawn-only Settings field. A further round of verification
  (`capture15`, docs/research/07-model-configuration.md section 10.8) found
  the CLI actually snapshots `.claude/agents/*.md` at SESSION CREATION --
  `--resume` of an EXISTING session id replays that snapshot and never
  rereads the directory, so reconnecting an already-running session can
  never apply an override change no matter how many times it happens; only
  a session created after `AgentDefinitionFileWriter.Sync` next runs (i.e.
  a brand-new session, started from the header "+") sees it.
  `agentModelOverrides` no longer triggers `SettingsChangeDetector`/the
  settings auto-apply reconnect; the Settings UI now shows a permanent
  hint under the row list saying so instead of implying "Reconnect now"
  would help.

### Research

- Ran three follow-up `--agents` probes to close the design note's
  remaining open questions before the (since-replaced) first implementation:
  `description`/`prompt` keys can be omitted entirely from an override
  entry (the minimal `{"<name>":{"model":"<alias>"}}` shape is enough and
  takes effect); multiple agents can be overridden in one `--agents` value;
  and a genuinely malformed `--agents` JSON is neither rejected at startup
  nor surfaced through `result.is_error` -- the CLI silently ignores the
  whole `--agents` value and starts with the built-in agent set instead,
  with no error the panel could detect (docs/research/
  07-model-configuration.md section 9). A further round (section 10) found
  `--agents` itself silently ignored whenever `--resume` is also passed,
  and verified the `.claude/agents/*.md` file-based mechanism the panel
  uses instead survives `--resume` -- though only when the file already
  existed before that session was first created. A follow-up round
  (`capture15`, section 10.8) isolated that precondition: the CLI
  snapshots `.claude/agents/*.md` at session-creation time rather than
  rereading it on every process start, so a change made after a session
  already exists never reaches that session, no matter how it reconnects.

## [0.7.0] - 2026-08-01

### Added

- Real thinking summaries now display when "Show thinking blocks" (Settings
  > Display) is on: the toggle now also passes `--thinking-display
  summarized` to the CLI at spawn, which is the only verified way to make
  the headless stream-json protocol emit non-empty thinking text instead of
  an empty string (docs/design-notes/2026-08-01-thinking-content-loss.md
  section 4c/7). No new UI -- the existing toggle now does double duty, and
  the panel's foldout rendering for text-bearing thinking blocks already
  supported this forward-compatibly. Toggling the setting triggers a quick
  automatic reconnect via the existing settings auto-apply path (0.6.0);
  requires Claude Code CLI v2.1.218 or later (older CLIs reject the unknown
  flag at spawn, surfacing the normal CLI-start error banner).
- Redacted thinking blocks (`redacted_thinking`, sent when the model flags a
  reasoning step as safety-sensitive) now map to a known protocol type
  instead of being silently dropped as Unknown, and render as a subtle
  one-line note ("Some thinking was redacted for safety.") using the
  existing thinking-indicator style instead of an empty foldout. Persists
  through the session cache (additive `thinkingRedacted` flag, backward
  compatible with older caches) (docs/design-notes/2026-08-01-thinking-
  content-loss.md section 6).

### Research

- Found a working, previously-untested path to real Claude Code CLI
  thinking body text: passing `--thinking-display summarized` directly as
  a CLI argument (not through `--settings`) produces non-empty
  `thinking_delta` text and a fully populated finalized `thinking` block,
  reversing this project's prior "no verified means exist" conclusion
  (docs/design-notes/2026-08-01-thinking-content-loss.md section 4c). The
  panel's rendering path already supports this forward-compatibly with no
  code changes. Now wired up -- see the Added entry above and the design
  note's section 7 for the single-toggle decision and follow-up scope.

### Fixed

- About's "CLI version" no longer sticks on "not connected" for a freshly
  spawned/resumed connection that has not sent a first message yet: the
  CLI only emits `system/init` once a connection's first user message has
  actually been processed, so the previous (0.6.0) retention-based fix
  could not help an idle connection -- there was nothing to retain. The
  panel now also probes the resolved CLI binary directly
  (`<cli> --version`, off the main thread, cached per resolved path) and
  falls back to it whenever no live or previously-retained connection
  version is known, showing "not connected" only when no CLI path
  resolves at all (docs/design-notes/2026-08-01-cli-binary-version-probe.md).

## [0.6.0] - 2026-08-01

### Added

- **Settings auto-apply**: changes to CLI path, allowed/disallowed tools,
  the dangerouslySkipPermissions danger-zone toggle, and custom instructions
  no longer require clicking "Reconnect now" to take effect. A 1.5s debounce
  coalesces rapid edits, then the panel reconnects automatically (same
  session id, via the existing kill+`--resume` path) the moment Claude is
  idle; while a turn is running or a permission is pending, the change is
  deferred until `TurnCompleted` and the pending pill reads "Apply after
  turn ends" instead. The status bar shows a transient "Applying
  settings..." while the auto-reconnect is in flight. The manual "Reconnect
  now" button is unchanged. The debounce/idle-gate decision is a pure,
  independently unit-tested policy (`AutoApplySettingsPolicy`)
  (docs/design-notes/2026-08-01-settings-auto-apply.md).

### Fixed

- Settings switch toggles (Ctrl+Enter, danger zone, notifications, CJK UI
  font, ...) no longer sit at a position that shifts with the label's
  character count -- `.uap-switch` rows now use
  `justify-content: space-between` so the switch is always flush right
  regardless of label length (docs/design-notes/2026-08-01-switch-alignment.md).
- Dragging the font-size slider no longer rescales the whole window: the
  `uap-fontscale-N` class moved from the window root to each window's own
  conversation-content root (AgentPanelWindow's chat view, PermissionWindow's
  card host), so Settings/History and the window chrome stay fixed size
  while only chat/permission-preview text responds to the slider
  (docs/design-notes/2026-08-01-fontscale-scope.md).
- About's "CLI version" no longer sticks on "not connected" after a
  reconnect even though the panel is Ready: `AgentClient` now raises a
  dedicated `InitMessageReceived` event whenever `system/init` populates
  `InitMessage` (independent of the state transition, since Ready is
  reachable from the "initialize" control_response alone before
  `system/init` even arrives), and `AgentHub` retains the last known
  value across reconnects for the About row to fall back on
  (docs/design-notes/2026-08-01-init-message-retention.md).
- Thinking blocks no longer show a misleading empty foldout that expands to
  nothing: measured against real CLI captures, the headless stream-json
  `thinking_delta`/finalized thinking content is ALWAYS empty text (a
  signature only) -- the content itself is withheld by the CLI by design,
  not a panel bug. An empty-text Thinking block now renders a compact
  one-line indicator instead ("Thinking... (~N tokens)" while streaming,
  "Thought (~N tokens)" once done), driven by the `system/thinking_tokens`
  estimate the panel previously ignored; the block also survives
  finalization instead of being dropped, so the indicator (and its token
  count) persists after the turn ends and across a reload. A Thinking block
  that DOES carry text (forward-compat, should the CLI ever start sending
  it) still renders the original foldout unchanged
  (docs/design-notes/2026-08-01-thinking-content-loss.md).
- The persisted language setting is now applied at panel BUILD time
  (AgentPanelWindow/PermissionWindow CreateGUI): previously only the
  Settings dropdown ever called `L10n.ApplyFromSettings`, so every domain
  reload / editor start rendered English regardless of the setting until
  the user visited Settings (live-diagnosed: setting=Auto,
  effective=English). String-asserting test fixtures now pin their catalog
  language explicitly per the i18n determinism rule.
- The About row's CLI version now also survives domain reloads: the version
  string persists via `SessionStateBridge` because a resumed connection can
  sit in Ready without ever re-emitting `system/init`, and the in-memory
  init message dies with the old domain.

## [0.5.0] - 2026-08-01

### Added

- **Full UI localization (English / Japanese)** with an Auto/English/日本語
  language picker in the Appearance settings (Auto follows the OS language,
  resolved lazily -- never in serialization paths). 253 user-visible strings
  across 23 files moved into a typed catalog (`Editor/UI/L10n/`): a missing
  translation is a COMPILE error (named-argument constructor), and
  `L10nTests` enforce non-empty parity plus `{N}`-placeholder-set equality
  between both catalogs. Sentences are format fields, never concatenation.
  A language switch rebuilds every open panel/permission window immediately
  (deferred out of the dropdown's own event dispatch). English remains the
  default and fallback until the user's setting is applied; tests pin the
  language explicitly via an internal override seam so suites stay
  deterministic on non-English machines. GlyphAuditTests' ASCII-source-byte
  scan excludes exactly the one Japanese catalog file (documented in both
  places); all other glyph rules are unchanged, and catalog text still flows
  through the same CJK-font and variation-selector-strip display chokepoints
  as every other string.

### Fixed

- Japanese copy review round: counter-word and politeness consistency,
  "確認したパス" phrasing, doubled-colon in answered-questions summaries.

## [0.4.0] - 2026-08-01

### Added

- **Settings enrichment** (docs/design-notes/2026-08-01-settings-enrichment.md):
  - Custom instructions appended to the CLI's system prompt
    (`--append-system-prompt`), persisted as a plain-text sidecar
    (`UserSettings/AgentPanel/CustomInstructions.txt`, never UnityYAML) and
    covered by the reconnect-pending hint.
  - Display section: show/hide thinking blocks (applies immediately to the
    current transcript), expand-subagent-cards-by-default, and show-cost-in-USD
    (off = token counts only, for subscription auth where cost is meaningless).
  - Quick actions: user-defined prompt shortcuts (label + prompt) edited in
    Settings, stored in a JSON sidecar (`QuickActions.json`), surfaced at the
    top of the empty-state suggestions and in the composer's Quick menu
    (inserting only -- never auto-sent).
  - Notifications: optional beeps on permission request / turn completion,
    fired only while the panel is unfocused.
  - About section: package version, live CLI version, CHANGELOG/GitHub links.
- **Settings visual refresh** (docs/design-notes/2026-08-01-settings-visual-refresh.md,
  grounded in docs/research/06-modern-editor-ui.md): every section is now a
  rounded card with an icon + header strip and a subtle hover transition;
  boolean toggles restyled as sliding switches (pure USS, `translate`
  animation); the danger-zone warning became a native HelpBox with an
  always-visible warn icon on the foldout; reconnect-pending gained a warn
  pill next to the text (color + text double coding); About uses neutral
  version pills and restrained link-style buttons. New theme tokens in both
  dark and light.

### Fixed

- View-switch lockout: rebuilding the panel UI (re-entrant CreateGUI, or the
  OnDisable/OnEnable path) hardcoded chat-visible while `_activeView` could
  still point at Settings/History -- the switch strip and the displayed view
  disagreed, and `SetActiveView`'s same-view early return then made every
  later `ShowSettings()` a permanent no-op. Build-time visibility now always
  derives from `_activeView` (and the teardown no longer force-resets it to
  Chat), regression-pinned by `AgentPanelWindowViewStateTests`. See
  docs/design-notes/2026-08-01-view-state-desync.md.
- `AgentClient.QuoteArg` now implements the Win32/CRT backslash-run quoting
  rule (2N+1 before an embedded quote, 2N before the closing quote); a
  custom-instructions value ending in a backslash (e.g. a Windows path) no
  longer corrupts the spawned CLI's entire argument tail. Caught by
  adversarial review before shipping.

## [0.3.0] - 2026-08-01

### Added

- **Subagent display (Phase 4)**: spawning a subagent through the Agent/Task
  tool now renders a dedicated nested card anchored at the tool call instead
  of the subagent's traffic bleeding into the top-level transcript.
  - While running: spinner, subagent-type badge, live activity line
    (current tool + description from `system/task_progress`), token count
    and elapsed time, all updated in place every 500ms without rebuilding
    the card (expand state survives; hot fields are deliberately excluded
    from the row-diff signature).
  - On completion/failure/stop: status icon plus the final summary rendered
    through the existing markdown pipeline; expanding reveals the nested
    tool cards and subagent text (120-block cap with a "N earlier steps
    omitted" note).
  - Domain reload: a still-running subagent restores as "stopped", never a
    forever-spinner. History restore links the main transcript's Agent
    tool_use to `<session>/subagents/agent-*.jsonl` via `meta.json`
    (toolUseId) and lazy-loads the nested transcript on first expand,
    degrading silently to summary-only when the files are missing.
  - Forward compatible: unknown `task_*` system subtypes and messages with
    an unknown `parent_tool_use_id` fall back to the previous top-level
    rendering. Grounded in the measured protocol capture
    (`docs/research/02c-subagent-captures.md`; the spawn tool's real wire
    name is `Agent` while `system/init` still lists `Task` -- both are
    handled, plus a `subagent_type`-input heuristic).
- Protocol layer: new `SystemTaskEventMessage`
  (`task_started`/`task_progress`/`task_updated`/`task_notification`);
  `AgentClient` events now carry `parent_tool_use_id`, a new
  `TaskEventReceived` event, and a forward-compat guard dropping
  parent-tagged stream deltas from the top-level streaming buffer.
- `SessionCacheFile`: additive `subagent` object on tool-call blocks
  (old caches load unchanged with `subagent == null`).
- Full Japanese README rewrite (features, three install routes, usage,
  troubleshooting, developer guide) plus six live-captured screenshots
  under `docs/images/`; package README pointer.
- Real-capture fixtures for the subagent stream
  (`task_subagent_inbound/outbound.jsonl`, `subagent_sidechain.*`) and 40+
  new regression tests across protocol mapping, hub grouping (replaying
  the real capture through `AgentClient` + `AgentHub`), cache round-trip/
  backward-compat, transcript restore, signature stability and expand-state
  memory.

### Fixed

- Collapsed permission / AskUserQuestion cards no longer crush to ~2px and
  bleed over the context-chip bar with 0-height button hitboxes under
  height pressure: the collapsed card is now `flex-shrink: 0` (shrink plus
  the `var(--uap-perm-card-min)` floor remain expanded-state-only), with
  `overflow: hidden` as bleed defense. Regression-pinned by
  `UssHygieneTests.SourceScan_PermCard_KeepsCollapsedCrushGuards`; see
  `docs/design-notes/2026-08-01-perm-card-collapsed-crush.md` (root cause
  measured live: card root h=2px, summary row overflowing 18px into the
  chip bar).
- `ToolActivityCard`/`SubagentCard` elapsed-time tickers no longer keep
  firing every 500ms after their card is discarded by a row rebuild or
  message pruning: the `IVisualElementScheduledItem` is now stored and
  paused on `DetachFromPanelEvent`.

## [0.2.0] - 2026-07-31

### Fixed (post-Phase-3 live user testing round)

- **Fixed the Settings/History/markdown-table layout-freeze (overlapping,
  crushed-looking text)**: `AgentPanelWindow.CreateGUI()`'s re-entry teardown
  called `root.styleSheets.Clear()`, which on a real `EditorWindow` root
  wholesale-removes not just the package's own 4 stylesheets but also
  Unity's own implicitly attached editor default stylesheet
  (`DefaultCommonDark_inter.uss`/`DefaultCommonLight_inter.uss`) -- the sole
  source of `.unity-scroll-view__content-container { flex-shrink: 0; }`.
  Without it, every `ScrollView`'s content container fell back to UI
  Toolkit's initial `flex-shrink: 1`; since Yoga implements no min-content
  floor, content taller/wider than the viewport got flex-crushed to
  viewport size instead of scrolling, and `flex-shrink: 1` children
  (Settings/History labels, the horizontal markdown-table column wrap) were
  squeezed to ~1-4px boxes while text still painted at full glyph size --
  the reported overlap garbage. Fonts were never involved (proven live: all
  paths resolved the same healthy `FontAsset` instance and `MeasureTextSize`
  returned correct values on the crushed labels). `CreateGUI` now calls a
  new `RemovePackageStyleSheets` that removes only the 4 sheets this window
  itself adds (re-resolved by asset path, matched by identity), leaving any
  foreign sheet -- including Unity's implicit default -- untouched.
  `AgentPanel.uss` also gains an explicit, redundant
  `.unity-scroll-view__content-container { flex-shrink: 0; }` baseline rule
  as a second line of defense for any host root that never had the implicit
  sheet to begin with. New regression guards
  (`Tests/Editor/AgentPanelWindowStyleSheetTests.cs`) assert a foreign
  stylesheet survives `CreateGUI()` (including re-entrantly), that the
  package's own sheets are never duplicated across a re-entrant call, and
  ban the literal `styleSheets.Clear()` call from reappearing in
  `AgentPanelWindow.cs`. See
  `docs/design-notes/2026-07-31-settings-layout-freeze-investigation.md`
  (root-cause proof) and
  `docs/design-notes/2026-07-31-scroll-container-shrink-fix.md` (this fix's
  options/decision).
- **Eliminated `!important` from USS entirely** (it is not a supported
  priority mechanism in Unity 2022.3 USS -- it silently does nothing rather
  than erroring, which is what let the font-scale mechanism ship
  non-functional in the first place). The `uap-fontscale-11`..`16` rules,
  previously `!important`-tagged inside `AgentPanel.uss` (loaded FIRST),
  moved verbatim (values unchanged, flag removed) into a new
  `Editor/UI/Uss/FontScale.uss`, now loaded LAST by
  `AgentPanelWindow.LoadStyleSheets` -- after `ThemeDark.uss`/
  `ThemeLight.uss`, whose `.uap-theme-dark`/`.uap-theme-light` rules define
  the SAME `--uap-font-size-*` custom properties at equal specificity.
  Sandbox measurement proved the OLD mechanism was **100% inert** whenever a
  theme class was present (always, in production) -- every font-size slider
  position resolved to the same hardcoded 12px regardless of the selected
  scale; the new load-order-only mechanism was verified to correctly
  resolve all 6 scale steps with a theme class active. See
  `docs/design-notes/2026-07-31-font-scale-cascade-fix.md`.
- Fixed the 5 pre-existing `var()`-inside-shorthand violations in
  `AgentPanel.uss` (`.uap-header-model-btn`/`.uap-header-history-btn`,
  `.uap-usage-popover`, `.uap-history-row-main`, `.uap-history-confirm`
  padding; `.uap-history-confirm-btn` margin) to explicit longhand
  declarations, one per side. No value changes. New regression guards
  (`Tests/Editor/UssHygieneTests.cs`) ban both the `!important` keyword and
  `var()` inside any shorthand `padding`/`margin`/`border-*` declaration,
  anywhere under `Editor/UI/Uss/`.
- `AgentPanelWindow.ShowSettings()`/`ShowChat()`/`ShowHistory()` no longer
  silently no-op when called against a panel that was just opened this
  editor session: `EditorWindow.GetWindow<T>()` returning does not guarantee
  `CreateGUI()` has already run on a freshly created window instance, and
  the view-switch guard (`!_built`) was swallowing the request in that
  window with no error, no log, and no visible effect -- exactly the
  reported "`ShowSettings()` did nothing" defect. The requested view is now
  queued (`_pendingView`) and applied as soon as `CreateGUI()` finishes
  building instead of being dropped. See
  `docs/design-notes/2026-07-31-show-settings-timing.md`.
- The header model picker's label/checkmark and the status bar no longer
  wait for some unrelated later event (the next turn's `TurnCompleted`,
  a state change, ...) to pick up a live model switch: `AgentHub` was never
  subscribed to `AgentClient.ControlRequestResolved`, so the moment the
  `set_model` `control_response` actually landed and `CurrentModel` became
  correct, nothing marked the panel dirty. `AgentHub.StartClient` now
  subscribes and relays `kind == "set_model"` straight into
  `RaiseChanged()`, so the header/status bar repaint within the very next
  refresh tick after the CLI's response arrives -- no change to the
  existing pump-thread-raises/main-thread-marshals threading model. See
  `docs/design-notes/2026-07-31-model-picker-refresh-gap.md`.

### Fixed (Phase 3 review round)

- The header model picker's label/checkmark and the status bar's context
  meter no longer keep showing the PRE-SWITCH model after a live model
  switch (`AgentHub.SwitchModel` / `set_model`) succeeds: they both read
  `AgentClient.InitMessage.Model` -- a one-time snapshot from `system/init`
  at connect time -- which nothing ever updated after a successful live
  switch, so the panel misrepresented which model the NEXT turn would
  actually run under until a full reconnect. `AgentClient` gains a
  `CurrentModel` property (a `_liveModel` override, committed only once the
  matching `set_model` `control_response` reports success, resolved
  through the same `InitializeResponse.Response["models"]` `value ->
  resolvedModel` mapping `HeaderView.ParseModels` already uses); `HeaderView`
  and `StatusBarView` now read it instead of `InitMessage.Model` directly.
  `AgentHub.SwitchModel` also clears the cached per-model usage snapshot
  (matching `StartFresh`/`SwitchToSession`) so the context meter shows "no
  data" rather than a stale model's percentage under the new label until
  the next turn's usage arrives. See
  `docs/design-notes/2026-07-31-live-model-tracking.md`.
- `AgentPanelWindow.CreateGUI()` is now safe against re-entry: if Unity
  ever invokes it a second time on the same window instance without an
  intervening `OnDisable`/`OnEnable`, the previous `ChatView`/
  `SettingsView`/`HistoryView` instances and their `AgentHub.Changed`/
  `EditorUpdatePump.RepaintRequested`/scheduled-refresh subscriptions are
  torn down (same steps as `OnDisable`) before rebuilding, and the root is
  always cleared first so a repeat build can never append a second
  skeleton onto the first.

### Added (Phase 3: Settings + view container)

- `AgentPanelWindow` now hosts multiple switchable views behind
  `IAgentPanelView` (`PanelViewKind`: Chat / History stub / Settings)
  instead of embedding `ChatView` directly. Every view's root is built
  once and kept alive for the window's lifetime; switching away only
  toggles `OnActivate`/`OnDeactivate` (pausing UI refresh loops and event
  subscriptions) -- the CLI client, session and transcript DOM are never
  restarted or rebuilt by a view switch (`AgentHub` is unaffected
  entirely). A permission request that starts waiting while Settings/
  History is on screen automatically switches back to Chat so the card
  is never stranded behind an invisible tab. `HeaderView.cs`/
  `StatusBarView.cs` are untouched this round (owned by a later stage);
  an interim gear/back strip lives in the ViewContainer instead, and
  `AgentPanelWindow.ShowSettings()`/`ShowChat()` are ready for the
  header's gear button to call once it is wired. See
  `docs/design-notes/2026-07-31-view-container-switching.md`.
- New Settings view (`SettingsView.cs`): CLI manual path + re-detect +
  resolved-path diagnostics + a "Reconnect now" action that respawns the
  CLI client with the SAME session id (`AgentHub.Reconnect()`, unlike the
  session-discarding `StartFresh`); permission-mode dropdown (applies
  immediately via `set_permission_mode` when connected, and is
  remembered for the next spawn); Ctrl+Enter toggle; allowed/disallowed
  tool lists (one per line); a `dangerouslySkipPermissions` opt-in behind
  a collapsed "Danger zone" foldout with explicit warning copy (default
  OFF, ARCHITECTURE.md D3); a font-size slider (11-16px, default 12) and
  the CJK-UI-font toggle (both apply live to every open panel/permission
  window via `AgentPanelWindow.ReapplyContentRootStyling()`); and a
  read-only CLI stderr tail (`AgentClient.StderrLine` -> `AgentHub`'s
  in-memory ring buffer, never persisted) with Copy/Clear.
  `AgentClientOptions` gained `AllowedTools`/`DisallowedTools`/
  `DangerouslySkipPermissions`, wired into `BuildArguments` additively
  (an empty/default value never changes the spawned argument string).
  A dynamic "some changes will apply the next time you reconnect" hint
  (`SettingsChangeDetector.RequiresReconnect`, pure and EditMode tested)
  appears only when a next-spawn-only field actually differs from what
  the running client was started with. See
  `docs/design-notes/2026-07-31-settings-propagation.md`.
- Font-size application mechanism: naively mirroring the CJK-font
  content-root trick with an inline `style.fontSize` would have almost
  no visible effect, because every text class in `AgentPanel.uss`
  already sets its own `font-size` explicitly. Instead, six
  `uap-fontscale-11`..`uap-fontscale-16` root classes (one `!important`
  override per selectable size) redefine the `--uap-font-size-*` custom
  properties every text class already reads through `var()` -- the same
  mechanism the dark/light theme classes already use, just toggled from
  Settings instead of `EditorGUIUtility.isProSkin`. See
  `docs/design-notes/2026-07-31-font-size-application.md`.
- New EditMode suites: `SettingsChangeDetectorTests`,
  `PermissionModeMappingTests`, `SettingsViewLogicTests` (tool-list
  parsing, `PanelSettings.ClampFontSize`), `AgentClientOptionsExtensionTests`
  (the new `BuildArguments` flags, including the byte-identical-when-
  unset regression guard).

### Fixed (live user feedback round)

- Code block content no longer renders EMPTY ("Unable to load font face
  for [Consolas]" + "Can't Generate Mesh, No Font Asset has been
  assigned"): assigning a monospace font via
  `Font.CreateDynamicFontFromOSFont` with a NAME ARRAY produced a Font
  whose TextCore face UI Toolkit could not load. The new
  `Editor/UI/FontLoader.cs` (cached static, single source for every
  mono surface: code blocks, attachment payloads, tool-card
  input/result previews, permission input preview) now resolves the
  EDITOR-BUNDLED RobotoMono TTF first
  (`EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf")`,
  path verified empirically by `FontLoaderTests` in the sandbox), then
  single-name OS fonts (Consolas -> Menlo -> DejaVu Sans Mono ->
  Courier New) each gated by a TextCore face probe
  (`FontEngine.LoadFontFace`), then the default editor label font --
  never null, and a broken face can never reach a `FontDefinition`
  again. `MessageBlockFactory.ApplyMonoFont` now delegates to
  FontLoader; the theme sheets document that no `-unity-font` /
  font-family may appear in USS.
- Glyph audit: eliminated every runtime-constructed glyph outside the
  editor font coverage (users saw placeholder squares plus continuous
  "\\U0001F4CE not found in [Inter-Regular SDF]" / "\\uFE0F not found"
  console spam). The paperclip (U+1F4CE) on attachment chips and the
  drag & drop overlay is replaced by the built-in `d_Linked` editor
  icon with a pure-ASCII `[@]` text fallback; the failure cross moved
  from U+2717 to whitelisted U+2715. No U+FE0F is constructed by the
  package itself -- the selector arrives in MODEL text (e.g. "warning
  sign" emoji sequences), so U+FE0F/U+FE0E are now stripped at every
  display chokepoint: `InlineMarkupConverter.Escape` (all finalized
  markdown), plain labels (`MessageBlockFactory`), the streaming pump,
  code block display (Copy still yields the RAW text) and tool-card
  previews. All glyph constants are centralized in `IconLoader` behind
  a `SafeGlyphCodepoints` whitelist; the new `GlyphAuditTests` suite
  scans every `Editor/` source and fails on any constructed codepoint
  outside the whitelist, any variation-selector escape or literal, and
  any non-ASCII source byte.
- Context attachment chips no longer render as a full-width bar with a
  far-right chevron (looked like a broken Foldout): the chip is now
  compact and content-hugging (`align-self: flex-start`, 60%
  max-width, icon + title + chevron in one row, title ellipsis, pill
  radius and padding tokens) and the expanded payload appears BELOW
  the chip as its own full-width sunken box (border + radius, internal
  scroll cap unchanged).
- Markdown table columns are no longer estimated too narrow: the
  per-column min-width estimate now adds inline-code chip padding,
  skips emphasis markers/backticks/variation selectors (they do not
  render), keeps CJK double-width (halfwidth forms single), scales
  bold header text by 1.1x, and clamps to 60..320px before the
  horizontal table scroll kicks in. Inline-code `<mark>` chips no
  longer break across lines untidily: SHORT spans (<= 24 visible
  chars) get internal spaces replaced with NO-BREAK SPACE (U+00A0,
  present in the editor fonts) so the chip wraps as one word; long
  spans keep normal wrapping so they can never overflow the panel
  (documented in `InlineMarkupConverter.NoWrapShortCodeSpan`). New
  `TableWidthEstimateTests` + `FontLoaderTests` suites.
- Japanese text no longer renders with PATCHY BOLD glyphs (some kanji
  heavier than surrounding text) and slightly faint/washed-out strokes:
  the editor's default UI font (Inter) has no CJK coverage, so every
  Japanese glyph fell back to per-glyph OS font substitution, and when
  more than one installed font/weight could supply a glyph, TextCore's
  dynamic fallback SDF atlas mixed them. `FontLoader` gains a cached
  `JapaneseUiFont` resolver mirroring the mono pipeline's OS-probe half
  (`Font.GetOSInstalledFontNames()` membership + the same
  `FontEngine.LoadFontFace` TextCore probe), trying "Yu Gothic UI" ->
  "Yu Gothic" -> "Meiryo UI" -> "Meiryo" -> "Noto Sans CJK JP" -> "MS UI
  Gothic" in order and returning null (panel stays on the editor
  default, unchanged) when none load. New `PanelSettings.preferCjkUiFont`
  (default on for Japanese/Chinese/Korean `Application.systemLanguage`,
  via the testable pure function `ComputeDefaultPreferCjkUiFont`,
  persisted through the existing `PanelStateStore` path) gates applying
  it. `AgentPanelWindow.ApplyThemeAndStyles` -- already shared with
  `PermissionWindow` for theme class + stylesheets -- now also assigns
  the resolved font at each window's content ROOT via the inherited
  `-unity-font-definition` USS property, so one assignment reaches every
  body-text element below it (chat message list, composer, context bar,
  permission card, status bar, header, first-run/empty views) without
  visiting each individually, and is re-applied on every window
  open/reopen. Mono code surfaces are untouched: `ApplyMonoFont` assigns
  RobotoMono as an INLINE style on the leaf label/field, which always
  outranks an inherited value regardless of apply order (verified by new
  `FontLoaderTests` cases). Rich-text `<b>` emphasis on CJK is a separate
  root-cause class and is intentionally left unchanged here. New
  `FontLoaderTests` (JapaneseUiFont resolution/caching/apply) and
  `PanelSettingsTests` (the language-decision function) suites.

### Added

- Markdown pipe tables in assistant output (`MarkdownTableParser` +
  `MarkdownRenderer`): a header row followed by a `|---|`-style
  separator (with optional `:` alignment markers) renders as a real
  table -- sunken container in the code-block family, bold header row
  with a bottom border, optional zebra striping via the
  `--uap-md-zebra-bg` token, left/center/right column alignment, and a
  per-table horizontal ScrollView so wide tables scroll instead of
  overflowing the panel. Pipes inside inline-code spans and escaped
  pipes (`\|`) never split cells; ragged rows are tolerated (short rows
  padded, overflow cells joined into the last column); anything without
  the header+separator shape falls back to plain paragraphs exactly as
  before. Every cell passes through the `InlineMarkupConverter`
  chokepoint (injection strings like `List<int>` stay neutralized).
  New `MarkdownTableTests` suite (detection, splitting, alignment,
  ragged rows, false positives, CJK cells, injection).
- Whole-panel drag & drop: `DragDropAttachHandler` now registers on the
  ChatView root (message list + context bar + composer), so dropping
  Project/Hierarchy objects anywhere on the panel attaches them as
  context chips. During a drag with valid object references a
  full-panel "Drop to attach" overlay appears (accent frame + centered
  paperclip label, `--uap-drop-overlay-bg`/`--uap-drop-border` tokens);
  the overlay ignores picking and exists only while a drag hovers, so
  permission cards and buttons stay fully clickable otherwise, and
  editor drags outside the panel are unaffected.

### Changed

- Attached Unity context no longer renders as raw delimited text in the
  transcript. The wire format is unchanged (the CLI still receives the
  full `ContextBlockFormatter.Compose` form), but wire and display are
  now decoupled: user messages store the typed text plus one structured
  `contextAttachment` block per chip (`ChatBlockKind.ContextAttachment`,
  title + payload), rendered by `MessageBlockFactory` as a compact
  collapsed chip row (paperclip + title + chevron) that expands into a
  sunken monospace box with an internal scroll cap
  (`--uap-attach-pre-max` token). Covers the composer send, the
  "Ask Claude to fix" console-error action, and queued sends: the
  `CompileGate` queue now persists wire text, display text and
  attachments together across domain reloads (legacy plain-string queue
  entries still load). `SessionCacheFile` round-trips the new block
  kind (`title` field added; old caches without it load with an empty
  title, and old cached messages containing raw delimiter text keep
  rendering as-is -- no migration parsing).

### Fixed

- Markdown table wrappers no longer trap the transcript's vertical
  mouse wheel: 2022.3 `ScrollView(Horizontal)` maps a plain vertical
  wheel to horizontal panning and stops propagation, so hovering a
  wide table froze transcript scrolling. A trickle-down `WheelEvent`
  interceptor now routes vertical-intent wheels to the enclosing
  vertical ScrollView; Shift+wheel (or a dominant horizontal delta)
  still pans the table.
- Markdown tables no longer show a permanent horizontal scrollbar: the
  minimum table width is now taken from the ScrollView's
  `contentViewport` instead of the wrap rect (which includes the 1px
  borders and left every table ~2px overflowed).
- Deterministic `UserSettings/AgentPanel/State.asset` corruption
  ("Unable to parse file ...: [Parser Failure at line N: Expected
  closing '}']" from `ScriptableSingleton.Save`): transcript text (code
  blocks, brace-heavy JSON, very long lines) no longer goes through
  Unity YAML serialization at all. The chat display cache now persists
  as plain JSON via the new `SessionCacheFile`
  (`UserSettings/AgentPanel/SessionCache.json`, written with the
  package's own `JsonWriter`): atomic writes (tmp file + replace, no
  partial files), tolerant loads (any IO/parse failure logs one line,
  deletes the bad cache and starts empty -- never throws, never wedges
  the panel). `PanelStateStore` keeps only `PanelSettings` plus a
  layout-version scalar, `SaveNow()` skips disk writes when the
  settings did not change, and already-corrupted or legacy
  transcript-carrying assets are rewritten cleanly once on load
  (version check in `OnEnable`). New `SessionCacheFileTests` round-trip
  poison suite (leading `{`/`}` lines, 50-deep nested braces, a 100 KB
  single line, CJK/emoji, quote/backslash soup, CRLF mixes, YAML
  flow-map lookalikes, null/empty blocks, corrupt-file recovery) plus a
  reflection guard asserting no transcript type can ever re-enter
  `PanelStateStore`'s serialized graph.

## [0.1.0] - 2026-07-29

### Added

- Package skeleton (UPM, Editor-only, zero dependencies, MIT).
- `Core/Json`: dependency-free JSON DOM (`JsonNode`), tolerant parser
  (`JsonParser`), and single-line serializer (`JsonWriter`).
- `Core/Protocol`: typed mappers for the Claude Code CLI stream-json protocol
  (system/init, assistant, user echo, result, stream_event, control
  request/response) with forward-compatible unknown-type handling, plus
  `OutboundMessages` as the single stdin payload factory.
- EditMode test suites (`JsonParserTests`, `ProtocolMappingTests`) running
  against real captured CLI v2.1.218 output fixtures.
- Unity context providers (`Integration/`): selection summaries
  (`SelectionContextProvider`), Console error capture with an
  "Ask Claude to fix" digest (`ConsoleErrorProvider`; errors are captured
  from panel load onward via `Application.logMessageReceived` plus the
  compilation pipeline -- pre-existing Console entries are not visible),
  active-scene summaries (`SceneContextProvider`), and drag & drop from
  Project/Hierarchy onto the composer (`DragDropAttachHandler`).
- Context chip strip above the composer (`ContextBarView`): attach
  selection, scene toggle, dropped-object chips (removable), and an
  automatic Console-error chip with a one-click fix prompt. Chips
  serialize into the outgoing message as a delimited context section
  (`ContextBlockFormatter`, per-block 2 KB truncation) composed by the
  caller and sent through `CompileGate.SendOrQueue` (no send API bypasses
  the compile/permission queue).
- Asset ping links (context side): `AssetLinkResolver` implements the
  `AssetLinkHub` contract -- existence check via AssetDatabase (only
  real `Assets/`/`Packages/` paths become links), single click pings,
  quick second click opens the asset (honoring `#L42` line markers).
- Context-aware empty-state suggestions ("Fix the console errors",
  "Explain the selected object") driven by selection/error events.
- EditMode test suites for the pure context logic
  (`ContextBlockFormatterTests`, `ConsoleErrorDigestTests`).
- Markdown rendering for finalized assistant messages (`UI/Markdown/`,
  zero dependencies): headings, 2-level lists, blockquotes, horizontal
  rules, paragraphs and fenced code blocks. `InlineMarkupConverter` is
  the single escape chokepoint -- it neutralizes every literal `<` with
  a self-closing `<noparse>` pair before emitting any rich-text tag
  (bold/italic/inline code/links), making it the security boundary now
  that finalized Labels enable rich text. (`>` and `&` stay raw: the
  2022.3 TextCore parser decodes no HTML entities and only reacts to
  `<`, verified against UnityCsReference.)
  Streaming output stays plain (`enableRichText = false`) and the full
  markdown tree is swapped in once per message on finalization.
- Clickable links in assistant output: `<link>` tags +
  `PointerDownLinkTagEvent` (2022.3 ships it under
  `UnityEngine.UIElements.Experimental`). http(s) links open the
  browser; `Assets/...`/`Packages/...` paths are gated through
  `AssetLinkHub.PathExists` (non-existing paths never linkify) and
  clicks are raised via `AssetLinkHub.LinkClicked`.
- Code blocks upgraded (`CodeBlockElement`): language badge, Copy
  button, monospace, horizontal scrolling and selectable text via a
  flat-styled read-only TextField.
- Collapsible tool activity cards (`ToolActivityCard`): spinner ->
  check/cross status, tool icon, one-line summary (`ToolCardDescriber`
  table: file name for Read/Write/Edit, command word for
  Bash/PowerShell, pattern for Grep/Glob, host/query for
  WebFetch/WebSearch, subagent description for Task), duration (live
  while running), expandable input/result preview with an internal
  scroll cap, and automatic collapse on completion. Runs of 3+
  consecutive completed tool calls compress into one expandable
  "N tools" group row.
- EditMode test suites for the rendering layer: `MarkdownConverterTests`
  (rich-text injection regression suite: `List<int>`, `<color>`/`<b>`/
  `<link>` injection, `&lt;` double-escape, CJK passthrough, fence edge
  cases, path-link gating) and `ToolCardDescriberTests`.
