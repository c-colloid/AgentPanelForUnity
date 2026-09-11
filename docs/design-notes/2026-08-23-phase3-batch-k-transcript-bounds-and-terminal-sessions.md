# 2026-08-23 -- Phase 3 batch K: the transcript loader gets bounds and honesty about terminal sessions

The two remaining MODEL items, both in TranscriptLoader: unbounded
synchronous work on the main thread (MODEL-3) and terminal-session
scaffolding rendered as the user's own words (MODEL-6).

## MODEL-3: a pathological transcript can no longer freeze the editor

Load() ran File.ReadLines over the WHOLE file and accumulated every
ChatMessage before trimming to the last 300 -- synchronous, on the
main thread, O(file) in both time and memory. A runaway session's
multi-hundred-MB transcript froze the History restore for its entire
length to produce 300 messages.

Two bounds, both exact-behavior-preserving under them:

- A byte cap (DefaultMaxTranscriptBytes = 16 MB; sizing reasoning on
  the constant -- capture fixtures run ~0.4-4 KB/line, so 300 messages
  fit in well under 4 MB, 16 MB is 4x that and two orders of magnitude
  under the pathology). Over the cap, Load seeks to the tail window,
  discards the first (partial) line, and parses only the tail. The
  reconstructed usage totals then describe the tail, not the whole
  file -- documented on the overload; a fast, slightly-partial number
  over a frozen editor is the deliberate trade. An internal overload
  injects the cap so tests use tiny files.
- Bounded retention: the message list is head-pruned in chunks during
  the scan (never above 2x maxMessages), instead of holding the whole
  transcript to trim at the end. CurrentAssistant is always the newest
  message and open ToolCallRecords are reached through the map (a
  record whose head message was pruned mutates harmlessly), so the
  returned window is byte-identical to the old end-trim -- a test
  pins that equivalence.

## MODEL-6: terminal-session scaffolds stop rendering as user prose

History lists sessions the user ran from the TERMINAL, and those
transcripts wrap slash commands and their local output in scaffold
tags. HandleUserLine treated every non-tool_result item as
user-authored text, so a restored terminal session showed
"<command-name>/model</command-name>..." -- CLI plumbing -- as
something the person said.

TryNormalizeCliScaffold (pure, unit-tabled): a command wrapper
renders as the honest minimal form the person typed ("/model
claude-fable-5"); <local-command-stdout>/<local-command-stderr>/
<local-command-caveat> blocks vanish. The tag literals are constants
in the loader for the same D9 reason as the context markers: Model
cannot reference the CLI's definitions, so drift detection is a
CANARY FIXTURE instead -- terminal_session_disk.jsonl (hand-authored,
modeled line-for-line on a real terminal transcript observed
2026-08-23; provenance record says exactly that and that it is not a
byte capture). One test pins the rendered output against the fixture;
a second asserts the fixture still CONTAINS the literals the loader
keys on, so a re-modeled fixture that silently drops the tags fails
loudly (the ContextMarkers_StaySynced pattern).

## Honest residuals

- The 16 MB cap is a sizing judgment from this package's own capture
  fixtures, not from a corpus of large real-world transcripts; the
  constant's doc says so, and the internal overload makes re-tuning a
  one-line change.
- MODEL-6 normalizes the scaffold families observed in real terminal
  transcripts (command-*, local-command-*). Other CLI-internal shapes
  (pasted-content stubs, system-reminder blocks inside user content)
  still render as-is; the canary pattern is the hook for adding them
  when observed.
- The canary fixture is modeled, not captured -- this environment has
  no interactive terminal CLI session to capture from. The provenance
  entry is explicit about that, and re-capturing from a real terminal
  session remains the stronger replacement.
