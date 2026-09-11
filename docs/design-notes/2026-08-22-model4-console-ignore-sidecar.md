# 2026-08-22 -- MODEL-4: Console-ignore free text out of State.asset

The last Model finding of the Phase 1 review: `ignoredConsoleErrors` and
`ignoredConsoleErrorPatterns` -- RAW Console error messages and user free
text -- were serialized straight into the ScriptableSingleton State.asset,
violating the project's own written rule ("never add message-content
strings to this asset"). One X-click on a sufficiently hostile compiler
error (braces, embedded quotes, very long lines -- exactly the strings
UnityYAML's writer emits and its own parser then rejects) could corrupt
the WHOLE asset, taking every panel setting down with it. This is the same
failure class that forced transcripts into SessionCacheFile and rename
titles into SessionMetaFile; the ignore store shipped after both and
missed the rule.

## The fix

- The two PanelSettings fields are now **[NonSerialized]**: the text can
  no longer reach Unity serialization at all (pinned by a test that
  JsonUtility.ToJson carries none of it). They remain the live in-memory
  working store, so ConsoleErrorProvider / SettingsView / the filter
  tests keep their exact call sites -- zero consumer churn.
- Persistence moves to **`ConsoleIgnoreFile`**
  (`UserSettings/AgentPanel/ConsoleIgnore.json`), an AtomicFile-backed
  sidecar with SessionMetaFile's exact contracts: atomic
  write-if-changed saves, never-throws, MODEL-1 corruption
  classification on load (parse-class deletes, IO-class keeps the file),
  formatVersion written but not gated.
- **PanelStateStore** wires the two together: `OnEnable` hydrates the
  fields from the sidecar; `SaveNow` saves the sidecar FIRST and
  unconditionally -- the fields are invisible to the asset snapshot, so
  gating the sidecar behind the snapshot's changed-check would drop
  exactly the saves the ignore feature calls SaveNow for
  (write-if-changed inside keeps hot paths free).

## Migration (no user data loss)

An older build's asset still carries the ignore data in YAML. Two private
holders with `[FormerlySerializedAs]` catch it on load, and
`PanelSettings.MigrateLegacyConsoleIgnores()` folds it into the live
store exactly once -- sidecar data wins where both exist (missing exact
entries append via the same bounded/deduped `AddIgnores`; a non-empty
live pattern text wins) -- then clears the holders and schedules one
clean asset rewrite, so the free text leaves the YAML for good.

## Tests

`ConsoleIgnoreFileTests`: hostile-text round trip (brace soup, embedded
quotes, newlines), missing-file silence, corrupt-file delete+log, the
**core MODEL-4 pin** (`JsonUtility.ToJson` never contains ignore text),
and the migration matrix (move+clear+once, sidecar-wins precedence,
nothing-to-do). Existing `ConsoleErrorVisibilityTests` pass unchanged --
they drive the same live fields through the provider's injection seam.
