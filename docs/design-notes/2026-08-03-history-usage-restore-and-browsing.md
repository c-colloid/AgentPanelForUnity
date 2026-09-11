# History: usage restore + browsing at scale (2026-08-03)

Two requests from one session:

1. **Bug** — 「このセッションの使用状況」 shows nothing after restoring a
   session from History.
2. **Feature** — the History list becomes unusable as sessions pile up.
   Rework it, modelled on Claude Desktop.

## 1. Bug: usage is discarded on restore

### 1.1 Root cause

`AgentHub.SwitchToSession` builds a **brand new** `ChatSession` and copies
only the messages ([AgentHub.cs:870](../../jp.colloid.unity-agent-panel/Editor/Integration/AgentHub.cs)):

```csharp
var session = new ChatSession { sessionId = sessionId };
if (restoredMessages != null) { session.messages = restoredMessages; }
...
_lastModelUsage = new Dictionary<string, ModelUsage>();
```

so `totalInputTokens` / `totalOutputTokens` / `totalCostUsd` /
`completedTurns` are all zero and the per-model map is empty. Two surfaces
go blank:

| surface | source | after restore |
|---|---|---|
| status bar "N tok" | `session.total*` | `0 tok` |
| "Usage this session" popover | `AgentHub.LastModelUsage` | "No completed turn yet." |
| context meter | `LastModelUsage[..].ContextWindow` | hidden |

Nothing reads the transcript for usage; `AccumulateTurn` is only ever
called from the live `result` handler (AgentHub.cs:2494). The live session
survives a domain reload only because `SessionCacheFile` persists the
totals — a restore has no such cache.

### 1.2 What the transcript actually contains (measured)

18 real transcripts in
`~/.claude/projects/C--Unity-UnityProjects-DevelopmentProject`:

- **No `result` lines and no cost field of any kind.** `total_cost_usd` is
  stream-only, so **cost cannot be restored**. (Moot for subscription auth,
  where the CLI reports 0 anyway, and `showCostUsd` gates the display.)
- **No `contextWindow`.** The context meter's denominator is stream-only
  too, so the meter **stays hidden until the next turn completes**. That is
  already its "no data" behaviour, so nothing regresses; it just does not
  come back early.
- `assistant` lines carry `message.usage` with `input_tokens`,
  `output_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens`
  — everything the status-bar total needs.

### 1.3 The trap: usage lines are duplicated

One API response is written to the JSONL as **several lines** (one per
content block), each repeating the *same* usage object:

| file | assistant lines | distinct `message.id` | ids whose usage differs |
|---|---|---|---|
| `62fbbf7d` | 23 | 16 | 0 |
| `b1228ae1` | 5 | 3 | 0 |
| `e233e417` | 101 | 68 | 0 |

Naive summing over-counts badly — `62fbbf7d` reads 6679 output tokens
naively vs **3147** deduplicated. **Aggregation must dedupe by
`message.id`.**

### 1.4 Second trap: the `<synthetic>` model

`e233e417` reports two models: `claude-opus-5` and `<synthetic>` (the
placeholder on `isApiErrorMessage` lines, 0 tokens). It must be excluded or
the popover grows a meaningless row.

### 1.5 Design

Add a `TranscriptUsage` result to `TranscriptLoader`, filled during the
scan `Load` already performs over every line (no extra I/O):

- `TotalInput/Output/CacheRead/CacheCreation` — deduped by `message.id`
  over the whole file (**not** the `maxMessages`-trimmed view: the trim
  happens after the scan, and a truncated total would be wrong, not just
  partial).
- `LastTurnModelUsage` — per-model usage for the assistant messages after
  the last real user prompt, i.e. exactly what `AgentHub.LastModelUsage`
  means (the last turn), not a session-cumulative aggregate that the
  context meter would misread. `ContextWindow` stays 0 (§1.2).
- `UserPromptCount` → `completedTurns`; last line timestamp →
  `lastActivityTimestamp`.

`SwitchToSession` applies them. `Load`'s existing signature is kept and
delegates to a new overload, so no caller churns.

**Known gap, stated rather than papered over:** subagent tokens live in
separate transcript files and are not summed. None of the 18 sampled
transcripts spawned a subagent (`isSidechain` 0, `Task`/`Agent` tool_use 0
even in the 7 MB one), so the divergence from a live `result` total is
**unmeasured** — it is not claimed to be zero.

## 2. Feature: browsing at scale

Requested: search, date grouping, incremental paging, pin, archive,
delete, rename, and grouping by scene / project / custom group.

### 2.1 Data availability (measured)

| need | available? | evidence |
|---|---|---|
| Conversation title | **yes** | `ai-title` lines in **18/18** files, always at line index **6-8**, exactly **1 distinct title** per file |
| Working directory | **yes** | `cwd` present, always by line index **2** |
| Unity scene | **no** | zero scene references in any transcript; `ContextBlockFormatter` attaches none either |

The title/cwd scan folds into `SessionIndexEntry`'s existing bounded
500-line preview scan — same single pass, no extra I/O, and both keys are
found ~60x inside the cap. **Scene grouping has no historical data**: it
requires new recording, so pre-upgrade sessions group under "(未記録)".

### 2.2 Storage: `SessionMetaFile`

Pin / archive / rename / custom-group membership / recorded scene are our
own state keyed by session id. Persisted as a JSON sidecar at
`UserSettings/AgentPanel/SessionMeta.json`, following `SessionCacheFile`'s
established contract exactly (atomic tmp+replace, never throws, tolerant
load, pure `System.IO` + `Core/Json`).

**Not** the `State.asset` ScriptableSingleton: renamed titles are raw user
text, and UnityYAML round-trips such strings unreliably — the exact defect
that forced the transcript cache out of the asset (ARCHITECTURE.md D5).

### 2.3 Two decisions that need calling out

**Delete.** The transcript is the CLI's own canonical user data under
`~/.claude`. Rather than unlink it, delete **moves** the file to
`UserSettings/AgentPanel/DeletedSessions/<sessionId>.jsonl` behind the
existing confirm-bar pattern, and the confirm text says where it went.
Moving it out of `~/.claude/projects/<dir>` entirely (rather than into a
subfolder there) avoids assuming anything about how the CLI globs its own
session directory. Reversible by hand; escalating to a hard delete is a
one-line follow-up if that is what is wanted.

**Project grouping.** Every session the panel lists already belongs to one
Unity project (the projects directory is keyed by cwd), so the only
reading that adds anything is *cross-project* browsing. Implemented as: on
demand, enumerate `~/.claude/projects/*`, read each session's `cwd`, and
keep only directories that are real Unity projects
(`<cwd>/ProjectSettings/ProjectVersion.txt` exists) — this machine has 30+
throwaway probe directories that would otherwise flood the list. Foreign
sessions are **browse-only**: resuming another project's session against
this cwd is meaningless, so their rows explain that instead of switching.

### 2.4 UI shape (Claude Desktop as the reference)

Toolbar: search field, group-by dropdown (日付 / シーン / プロジェクト /
カスタム), and an archived-visibility toggle. Rows show the `ai-title` (or
the rename override) as the title with the first user message as a dimmer
sub-line, plus the existing relative-time/size caption. Pinned sessions
sort into a leading group regardless of the grouping mode. A per-row "..."
button opens pin / rename / group / archive / delete. Initial render caps
at 50 rows with a 「もっと見る」 button, so list cost stays flat.

Search matches title, rename override, first-user preview and session id,
case-insensitively; date groups are 今日 / 昨日 / 過去7日間 / 過去30日間 /
それ以前.

## 3. Regression guards

Pure, Unity-free logic is the test surface (this is why grouping, search,
sorting and paging are static methods taking explicit inputs):

- `TranscriptUsage` dedupe: a fixture with the same `message.id` on
  multiple lines must count once — pinned against **captured** real lines,
  not hand-written ones (the v0.12.2 lesson: a fixture written from the
  same wrong assumption as the code cannot catch that assumption).
- `<synthetic>` excluded from the per-model map.
- Totals computed over the whole file even when `maxMessages` trims.
- `SwitchToSession` populates totals and `LastModelUsage`.
- `SessionMetaFile` round-trip, tolerant load, atomic save (mirrors
  `SessionCacheFileTests`).
- Search/group/sort/page: pure-function tests including pinned-first
  ordering, archived hidden by default, and empty-query passthrough.

## 4. Verification

Sandbox `AITemp`: compile clean with no warnings from this package, full
EditMode suite **1541 total / 1536 passed / 0 failed / 5 skipped** (1466
before this change; +75 tests).

One failure was caught and fixed on the way: `UssHygieneTests` rejected
three new rules that used `var()` inside a `padding`/`margin` shorthand.
UI Toolkit does not expand custom properties inside multi-value shorthands,
so the repo has a standing scan for exactly that; the declarations were
split into longhand.

Live editor (`DevelopmentProject`), against the real 18-session history:

```
sessions on disk: 18   with ai-title: 18   with cwd: 18
largest transcript (7.1 MB): in=127 out=59781 cacheRead=5484673
                             cacheCreate=190248 prompts=4
status bar would show: '59.9k tok'   (before this fix: '0 tok')
date groups: today 12 / yesterday 2 / last-7-days 4
unity projects discovered: 2 (DevelopmentProject, AITemp)
```

Three things this confirms beyond the unit tests:

1. The reconstructed totals match the ground truth computed independently
   from the same file, on a transcript 160x larger than the fixture.
2. `ai-title` and `cwd` extraction succeeded for **18/18** real sessions,
   so the single-scan approach holds outside the fixtures.
3. The Unity-project filter kept 2 directories out of the 30+ under
   `~/.claude/projects` on this machine — without it, "group by project"
   would have listed 30 throwaway probe directories.

Note `lastTurn models=0` for that particular transcript: its final user
prompt was never answered (an interrupted turn), so there is genuinely no
last-turn usage to show and the popover correctly reads "No completed turn
yet". The populated case is pinned by the fixture test instead.
