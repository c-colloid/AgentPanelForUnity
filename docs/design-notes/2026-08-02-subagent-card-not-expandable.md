# Subagent cards became non-expandable

Date: 2026-08-02
Status: fixing
Reported: user, live panel ("いつの間にか、サブエージェントのカードが展開できなくなってます")

## 1. Symptom

Completed subagent cards show no chevron and do not react to clicks.

## 2. Root cause A (proven): wire vs disk field-name mismatch

`SubagentCard` decides expandability up front:

```csharp
bool hasDetails = _subagent.blocks.Count > 0
    || !string.IsNullOrEmpty(_subagent.summaryMarkdown)
    || _subagent.status == "running";
```

When false the chevron is hidden AND the `ClickEvent` handler is never
registered -- the card is inert for the rest of its life.

Live measurement of the user's session (`3fd4e6ad`, 5 subagent records):
`blocks=0 summary=0 tok=0` for every one, so `hasDetails` is false.

Why the data is empty: `TranscriptLoader.RestoreToolResult` reads the Agent
tool_result envelope as `userNode["tool_use_result"]`, but that is the
**wire (stdout stream-json)** spelling. The **on-disk transcript JSONL**
spells the same envelope `toolUseResult`. Both verified today:

| source | spelling | evidence |
|---|---|---|
| stdout wire | `tool_use_result` | real captures `Tests/Editor/Fixtures/askuser_inbound.jsonl` etc. |
| disk transcript | `toolUseResult` | real session file: top-level keys include `toolUseResult`, `has snake tool_use_result at top? false` |

So on every restore (history switch, editor restart, domain reload,
`--resume` rebuild) `ApplySubagentCompletion` never runs: status, token
totals and the summary are all dropped. R02c section 1 point 4 documented
the wire shape; the loader adopted that name for a disk-format reader, and
`TranscriptLoaderTests` pinned it with a HAND-WRITTEN line using the wire
spelling -- so the suite stayed green while the real path never worked.

This is not a new regression; it has been wrong since Phase 4. It became
visible now because recent releases forced many reloads/restarts, making
restored (rather than live-streamed) cards the common case.

## 3. Root cause B: `hasDetails` ignores lazily-loadable content

`SubagentCard.TryLazyLoadNestedBlocks` can pull a completed subagent's
whole nested transcript from `subagents/agent-<taskId>.jsonl` on first
expand. But `hasDetails` only looks at what is ALREADY in memory, so a
card whose sidechain is sitting on disk still renders inert. Fixing A
alone would leave this trap for any record whose summary happens to be
empty (e.g. a subagent that returned only tool output).

## 4. Decisions

1. `TranscriptLoader`: read `toolUseResult` first, fall back to
   `tool_use_result`. Tolerating both costs one lookup and makes the
   reader correct for either serialization (the panel feeds it disk files
   today; the wire spelling stays valid if that ever changes).
2. `SubagentCard.hasDetails`: also true when the record has a
   `toolUseId` (a sidechain MAY be loadable). Expanding a card that turns
   out to have nothing shows an explicit "no details" line rather than an
   empty box, so the affordance never lies in either direction.
3. Regression guards must use REAL captured shapes, not hand-written
   JSON: pin the camelCase disk spelling against a fixture copied from an
   actual transcript line, and keep a wire-spelling case for the fallback.
4. Correct R02c: state explicitly that the envelope is `tool_use_result`
   on the wire and `toolUseResult` on disk.

Rejected: making `hasDetails` unconditionally true -- a genuinely
detail-free record (no toolUseId, e.g. a synthetic/legacy row) would then
offer an expander that can never show anything.

## 5. Process note

The hand-written fixture is the failure that matters here: a test authored
from the same wrong assumption as the code cannot catch that assumption.
Transcript-format tests must be pinned to captured lines.
