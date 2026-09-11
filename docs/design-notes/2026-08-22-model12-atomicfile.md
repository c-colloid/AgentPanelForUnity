# 2026-08-22 -- MODEL-1/MODEL-2: AtomicFile, the shared write/read/classify helper

The Phase 1 shared refactor the roadmap ordered before the remaining Model
fixes: one atomic-write helper + one load-failure classification, replacing
five copy-pasted implementations that all shared the same two data-loss
defects. Verified by the new CI (dotnet-smoke + full EditMode).

## MODEL-2: the copy-pasted atomic write had a total-loss crash window

All five persistence writers (SessionCacheFile, SessionMetaFile,
QuickActionStore, CustomInstructionsFile, AgentDefinitionFileWriter) staged
to `.tmp` and swapped via `File.Replace`, with a fallback of
`File.Delete(target)` then `File.Move(tmp, target)` whose comment claimed
"still leaves either the old or the new complete file". That claim is false
in the window between the two calls: a crash there loses BOTH generations
(old deleted, new stranded as a `.tmp` no reader ever opens).

`Core/FileIo/AtomicFile` replaces all five copies:

- Happy path unchanged: staged `.tmp` + `File.Replace`.
- The fallback (Replace unsupported by the filesystem) now renames the old
  file ASIDE as `.bak` instead of deleting it, moves the new file in, then
  drops the backup. Every crash window leaves a readable generation.
- `ReadAllText` restores a surviving `.bak` when the primary is missing (and
  never over an existing primary), so the worst crash costs one save, not
  the file. A stale `.bak` is cleaned by the next successful write.
- `WriteAllTextOrThrow` (AgentDefinitionFileWriter's per-file error
  handling) and never-throw `WriteAllText` (the sidecars) share the core;
  `WriteAllTextIfChanged` carries GateHookInstaller/UapOpsMcpConfig's
  mtime-preserving skip -- and upgrades those two from plain in-place
  `File.WriteAllText` (truncatable mid-crash) to the same atomic swap.

## MODEL-1: transient IO errors deleted user data

Every sidecar's `Load()` treated ANY exception as corruption and deleted the
file. A cloud-sync or antivirus lock -- `IOException`, gone a second later --
permanently deleted pins/renames/groups (SessionMetaFile), the transcript
cache (SessionCacheFile), quick actions, and, worst, `CustomInstructionsFile`,
whose content is PLAIN TEXT the user typed by hand and cannot even be
"corrupt" in the parse sense: its delete-on-failure was pure data loss.

`AtomicFile.IsCorruption(ex)` classifies:

- **Corruption (delete + rebuild)**: `JsonParseException`,
  `InvalidDataException`, `FormatException`, `OverflowException`,
  `DecoderFallbackException` -- the CONTENT is bad; deleting is the fix (and
  a surviving `.bak` previous generation is restored by the next read, since
  `TryDelete` deliberately keeps it).
- **Transient (keep the file, return nothing this attempt)**: `IOException`
  + subclasses, `UnauthorizedAccessException`.
- **Unknown**: NOT corruption -- the data-safe default.

The JSON sidecars delete only on the corruption class now;
CustomInstructionsFile never deletes at all.

## Behavior pins that did NOT change

- Truncated/garbage/non-object sidecars still delete (all parse-class) --
  the existing store tests pass unchanged.
- Missing file still silently returns the empty value.
- One log line per failure, no spam; second load after a delete is silent.
- SEC-1/SEC-2 generated-file behavior (write-if-changed, exact content) --
  smoke pins unchanged, now 67 assertions.

## Testing

- `AtomicFileTests` (EditMode): each crash-window state constructed by hand
  (backup restore, stale-backup cleanup, never-restore-over-primary), the
  classification matrix, mtime-preserving skip, TryDelete keeping `.bak`.
- `ci/SmokeTests`: the same core assertions license-free on every push
  (AtomicFile compiles there with its Ops consumers).
- Existing SessionMetaFile/SessionCacheFile/QuickActionStore/
  CustomInstructionsFile tests: unchanged, still green -- including
  CustomInstructionsFile's locked-file test, which now passes with the
  correct semantics (file KEPT) instead of accidentally (delete failing on
  the lock).

## Out of scope, recorded

- `ZombieReaper`'s state file keeps its plain write (crash-bookkeeping data;
  a torn write there is self-healing).
- MODEL-4 (`ignoredConsoleErrors` free text in YAML State.asset) is the next
  Model item and will reuse this helper for its JSON sidecar.
