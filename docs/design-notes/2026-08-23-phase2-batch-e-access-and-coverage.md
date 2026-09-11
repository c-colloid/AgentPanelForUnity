# 2026-08-23 -- Phase 2 batch E: keyboard access + three coverage holes closed

## UXIA-1: history restore is keyboard-operable

(Landed one commit earlier, recorded here with the batch.) The row body
is focusable (tabIndex 0) with an accent-user :focus ring over a
reserved transparent border, Enter/Space activate the same OnRowClicked
path as a click (KeyDown + PreventDefault, the PermissionCard house
pattern -- deliberately no NavigationSubmitEvent so one key press can
never double-activate and skip the switch confirm), and the row menu
leads with Open (disabled with a tooltip on foreign rows), dispatching
into OnRowClicked so the restore has exactly one code path.

## INFRA-2c: CompileGate's queue and drain decision are pure and pinned

CompileGate crossed a domain reload with user text in a SessionState
JSON string and decided its drain steps inline in OnUpdate -- none of
it directly testable. Two extractions, both wrapper-thin:

- `CompileGateQueue` (Serialize/Deserialize + Entry): the exact
  LoadQueue/SaveQueue rules verbatim -- legacy plain-string entries,
  the required "wire" key, attachment object filtering, empty-string
  as the store's nothing value, and the malformed-input discard signal
  (the caller still owns the warning log + store clear).
  CompileGateQueueTests tables the round-trip and every tolerance rule.
- `CompileGate.DecideDrainStep` -> DrainStep enum: the tick's decision
  with its load-bearing precedence -- empty queue over everything,
  compiling over suspension (the reload resolves it), suspension over
  sendability (HUB-2: never respawn-fight the suspension), timeout
  parks without dropping. OnUpdate now gathers live signals (including
  the EnsureStarted revival, still skipped while suspended or
  compiling) and switches on the decision.
  CompileGateDrainDecisionTests pins the table.

## INFRA-2d: ProjectSessionScanner has a hermetic suite

Pure C# with a projectsRootOverride and zero tests. New
ProjectSessionScannerTests (temp + Guid pattern, fake
~/.claude/projects tree): the ProjectVersion.txt-based Unity filter
(an Assets folder alone does not qualify), cwd probing through
malformed leading lines, the scan-cap exclusion, newest-project-first
ordering, last-segment display names, and the missing-root case.

## INFRA-2e: fixture provenance is recorded and machine-checked

Every protocol fixture is a capture of claude v2.1.218, and nothing
said so machine-checkably -- a CLI upgrade could leave the suite
pinning a wire shape the shipping CLI no longer speaks. New
`Tests/Editor/Fixtures/_fixtures.meta.json` records cliVersion,
verifiedOn, the capture era (exact per-file dates were not recorded at
capture time; the era bounds them honestly) and a source line per
fixture. FixtureProvenanceTests enforces: every fixture file has an
entry, every entry has a file, the recorded version equals the new
`FixtureLoader.ExpectedCliVersion`, and a verification older than 180
days logs a re-verify warning (never a failure -- age alone proves
nothing).

### Re-capture procedure (referenced by _fixtures.meta.json)

1. Run the target CLI version through the capture paths described in
   docs/research/02-claude-cli-protocol.md (print-mode, bidirectional
   stream-json, permission and AskUserQuestion turns, subagent turns)
   and copy the raw stdout/stdin logs over the matching fixture files.
2. Update `FixtureLoader.ExpectedCliVersion` AND `cliVersion` +
   `verifiedOn` in _fixtures.meta.json (the provenance test fails
   unless both move together); add entries for any new fixture files.
3. Run the EditMode suite: the protocol tests replaying the new bytes
   are the actual compatibility verification.

## Deliberately deferred (the remaining two INFRA-2 holes)

- **(a) ReloadReconciler extraction** (OnFirstUpdate's
  Reap -> Reconcile -> TryAutoContinue -> RestoreAfterReload ->
  DrainPending ordering behind a facade): this refactors the single
  most dangerous code path in the package (domain-reload
  reconciliation) purely to make its ordering assertable, and the CI
  lane cannot run a REAL reload to catch a mistake (the RequestScriptReload
  test the plan sketches is itself opt-in). High blast radius, verification
  gap exactly where the risk is -- deferred to its own focused change.
- **(b) UapOpsHttpServer socket tests** (real localhost round-trip
  proving ArmAccept re-arms before handling): valuable -- this exact
  pitfall shipped a real client-hang once -- but a live-socket suite
  needs its own flakiness budget (ports, timing) and lane decision.
  Deferred with the same "own focused change" reasoning.
