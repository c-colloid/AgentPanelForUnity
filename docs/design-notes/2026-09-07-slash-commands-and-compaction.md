# 2026-09-07 -- Slash-command input and context compaction

Two gaps that were both "the CLI already does this, the panel just does
not let you reach it / does not tell you about it":

1. The composer had no way to type a slash command with any confidence.
   The CLI reports its command catalog twice (system/init
   `slash_commands[]` names, initialize `commands[]` with descriptions),
   `SystemInitMessage.SlashCommands` even mapped it, and nothing read it.
2. `system/compact_boundary` mapped to the ignored `InboundType.System`
   bucket. A `/compact` (or the CLI's automatic compaction near the
   window limit) left no trace in the transcript, and the context meter
   -- whose whole job is to warn about imminent compaction -- kept
   showing the pre-compaction number, or worse.

## 1. Slash commands

### 1.1 Wire contract

In `--input-format stream-json` the CLI treats a user message whose text
is `/name args` as the slash command `name` (Agent SDK "Slash commands").
So the panel sends the typed text VERBATIM and invents no syntax of its
own. `SlashCommandCatalog.TryParse` is the sent-text grammar: leading
whitespace, `/`, one or more command characters (letters, digits, `-`,
`_`, `:` for namespaced skills), then whitespace or end. `/`, `/ x`,
`//`, `/usr/bin/x`, `1/2 of the scene` are prose.

Two commands get panel-side treatment:

- `/clear` never reaches the CLI. The CLI's own `/clear` starts a new
  session id underneath a panel that still believes it is showing the
  old one; the panel's New chat (`AgentHub.StartFresh`) is the same
  intent done honestly, so `/clear` maps to it.
- `/compact` goes to the CLI; section 2 is what happens next.

A sent command consumes NO context chips and NO pending images: appending
a delimited context block or an image block to `/compact` would turn the
command into prose. They stay pending for the next real message
(`ComposerView.SendSlashCommand`).

### 1.2 The catalog and its cache

`AgentHub.RefreshSlashCommandCatalogCache` runs on BOTH arrival points
(initialize resolved, system/init received -- their order differs between
a fresh spawn and a `--resume`) and stores
`SlashCommandCatalog.Merge(initialize commands[], init slash_commands[])`
in `PanelSettings.slashCommandCatalog`, the same shape as `modelCatalog` /
`agentTypeCatalog`: rich entries (description, argumentHint) win, names
the rich list lacks are appended bare, and an empty result never clobbers
a cached list. The cache is why the popup works right after a domain
reload or editor restart, before this connection's own init.

What the composer offers is `AgentHub.SlashCommands` =
`SlashCommandCatalog.WithBuiltins(cache, ...)`: `/compact` and `/clear`
are guaranteed at the front (every SDK-mode CLI this panel targets
supports both; `/clear` is local anyway). `/clear` ALWAYS carries the
panel's own description because it is the panel's New chat; `/compact`
keeps the CLI's description when one was reported.

### 1.3 The popup

`ComposerView` shows a suggestion list in normal flow directly above the
field (never absolutely positioned, so it cannot be clipped by the panel
edge or overlap the message list) while the WHOLE field text is `/` plus
a command prefix (`TryGetTypedPrefix`): a space, a newline or any other
character ends prefix mode, i.e. the user is typing arguments now.
Filtering (`Filter`) lists prefix matches first, then contains-matches,
ordinal-ignore-case, capped at 8.

Keys, all consumed only while the popup is open, and all breaking the
Enter keycode->char bridge like any other non-Enter key:

- Up/Down move the highlight (wrapping). The caret does not move.
- Tab completes the highlight to `/name ` (`CompleteText`; the trailing
  space leaves prefix mode so the popup closes through the ordinary
  value-changed path). Unity follows a Tab keycode event with a `'\t'`
  character event that the TextField would insert into the just-completed
  text; `_swallowNextTabChar` eats exactly that one event.
- Enter completes INSTEAD of sending while the typed prefix is not
  already the highlighted entry's full name (`ShouldCompleteOnEnter`);
  once it is, Enter sends. So a no-argument command is `/comp` Enter
  Enter, and nothing is ever sent that the user has not seen spelled out.
  The paired character event inherits `EnterAction.Ignore` through the
  existing pending protocol (the IME-confirm shape), so it neither sends
  nor inserts a newline. IME composition still wins: the Enter decision
  is resolved first, and only a resolved Send is redirected.
- Esc closes the popup without touching the text (and, with the popup
  closed, Esc is the interrupt it always was).
- The hint under the field reads the key legend while the popup is open.

The popup never hides on focus-out: a row click takes focus before the
click lands.

## 2. Compaction

### 2.1 Protocol

`system/compact_boundary` now maps to `SystemCompactBoundaryMessage`
(`InboundType.SystemCompactBoundary`, event
`AgentClient.CompactBoundaryReceived`). Wire shape (Agent SDK docs,
CLI v2.1.218):
`{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual"|"auto","pre_tokens":N}}`.
The on-disk transcript spells the same fact `compactMetadata.preTokens`;
`ReadMetadata` accepts both so `TranscriptLoader` shares the mapper and
the two cannot drift. Nothing in it is required: a bare boundary still
maps (trigger empty, PreTokens -1) -- the boundary is the fact, the
metadata decorates it. No repo fixture captures a real compaction yet;
the shape is pinned from the SDK documentation and guarded by the
tolerant mapping (a spelling drift degrades to "no token clause", never
to a dropped line).

### 2.2 The context meter

The meter's numerator is `result.usage.iterations[last]` (design note
2026-08-27). After a compaction that number is wrong in a specific way
depending on who compacted:

- MANUAL `/compact`: the turn IS the compaction. Its last iteration is
  the summarization call, which read the whole OLD context -- exactly the
  number the compaction just made obsolete.
- AUTO, mid-turn: the turn went on after the boundary; its last iteration
  ran against the compacted context -- exactly the post-compaction size.

`AgentHub` therefore records the trigger of a boundary seen since the
last result (`_pendingCompactTrigger`) and, at the next
`OnTurnCompleted`, keeps the reading for auto and discards it for manual
(`ApplyCompactionToContextReading`). From the boundary until a reading
is trusted, `ContextUnknownAfterCompaction` is set and
`StatusBarView.RefreshContextMeter` shows "compacted" at 0% with a
tooltip instead of a number: the old billing-sum fallback would re-read
the discarded context as ~100% right after the user asked for room. A
real reading always wins over the flag (`ShowCompactedState`) -- the flag
suppresses a fallback, never a measurement. The flag is per session
(cleared by StartFresh / SwitchToSession / ResetForTests) and not
persisted across a domain reload: after a reload the meter falls back one
rung until the next turn, which is the pre-existing, honest behavior.

### 2.3 The transcript note

`CompactionNote.Describe(trigger, preTokens)` (Integration -- it reads
L10n, which Model may not) is the single wording, shared by the live
path (`AgentHub.OnCompactBoundaryReceived`) and the restore path
(`TranscriptLoader.HandleCompactBoundaryLine`, through the
`TranscriptLoader.CompactionDescriber` seam that CompactionNote installs
at editor load, the ImageSaver pattern; a null seam falls back to plain
English so the boundary is never lost), so a session looks the same
watched live or reopened from History. Four strings, one
sentence each (the i18n Fmt rule): manual/auto x with/without a token
count; an unknown trigger reads as auto, never claiming a `/compact` the
user did not type. Token counts use `TokenCountFormat.Short` ("84.2k"),
which `StatusBarView.FormatTokens` now delegates to, so note and meter
agree on notation. The live handler closes the streaming bubble (text
after the boundary starts a new one) but does NOT demote open tool-call
records the way `FinalizeStreamingMessage` does: a compaction happens
between API calls, never while a tool is mid-flight, and the turn is
still running -- its own result closes it.

### 2.4 History restore

`TranscriptLoader.ProcessLine` now looks at `system` lines before the
`isMeta` gate (the CLI writes the boundary with `isMeta:false` today; the
boundary must survive if that ever flips) and turns `compact_boundary`
into the note above, closing the current assistant accumulator like a
user line does. The `isCompactSummary:true` user line the CLI injects
after the boundary ("This session is being continued from a previous
conversation...") is skipped like an `isMeta` line -- the person never
typed it and the model already has it -- and `SessionIndex` skips it for
the History preview for the same reason. Other system subtypes stay
ignored, and their timestamps still count toward "last activity" as
before.

## 3. What was verified here and what was not

EditMode pins: `ProtocolMappingTests` (both metadata spellings, bare
boundary, non-numeric tokens), `SlashCommandCatalogTests` (parse from the
real initialize capture, merge, builtins, the typed-text grammar, prefix
mode, filtering, Enter-completes), `CompactionHubTests` (note text and
the manual-vs-auto meter rule through a real AgentClient into AgentHub's
handlers), `TranscriptLoaderCompactionTests` (restore + preview),
`ComposerSlashCommandTests` (popup through the real value-changed and
OnKeyDown paths), `CompactionNoteTests`, `PanelSettingsTests` (cache
round-trip). This session had no Unity Editor or dotnet toolchain, so
the suite was not executed here; CI (editmode-tests.yml) is the
verification. Still to be watched live on a real CLI: the exact
`compact_boundary` line (to be captured into Tests/Editor/Fixtures), and
whether `/compact`'s own result carries the summarization usage as
assumed in 2.2 (if it does not, the only cost is one turn of "compacted"
in the meter where a number could have been shown).
