# Subagent card state, idle progress, and typing mid-turn (2026-08-03)

Three live reports in one message:

1. A subagent card's expand/collapse and scroll state reset on every update.
2. While a subagent is not calling a tool there is no sign it is working.
3. Once everything is delegated the main model is idle -- let me type then,
   instead of having to stop everything and resend.

## 1. Card state resets

### 1.1 Root cause

The card is never updated in place. Any structural change replaces the
whole message row's element subtree:

`AgentHub` mutates a `SubagentRecord` and raises `Changed`
(AgentHub.cs:2229 appends a nested tool block, :2336 flips status) ->
`ChatView.OnHubChanged` sets a dirty flag (ChatView.cs:178) -> a 60 ms
scheduler calls `_list.Refresh` (ChatView.cs:132, :209) ->
`MessageListController.Refresh` compares a per-message signature and, on
any change, does `RemoveAt` + `Insert` of a freshly built element
(MessageListController.cs:123-128) -> `new SubagentCard(...)` rebuilds the
header and `PopulateDetails` builds a brand new nested `ScrollView` and
brand new nested `ToolActivityCard`s (SubagentCard.cs:321-374).

What survives and what does not:

| state | survives a rebuild? | why |
|---|---|---|
| OUTER card expanded | **yes** | mirrored into a static `Dictionary<string,bool> ExpandedByToolUseId` (SubagentCard.cs:44), restored in the constructor |
| NESTED tool card expanded | no | `ToolActivityCard._expanded` is instance-only (ToolActivityCard.cs:21); group rows and context blocks keep it in a closure local |
| nested scroll offset | no | `PopulateDetails` creates a new `ScrollView` each time (SubagentCard.cs:343); a new one starts at 0 |
| outer message-list scroll | no | `Refresh` never saves/restores `_scroll` around the swap |
| OUTER card expanded, across a domain reload | no | the dictionary is a plain `static` |

So the mechanism for preserving this already exists in the codebase and was
applied to exactly one of the five things that need it. `ToolCallRecord`
already carries a stable `toolUseId` (ToolCallRecord.cs:24), populated both
live and on restore, so the same keying works for the rest.

### 1.2 A second defect found in the same place

`ComputeSignature` hashes `subagent.status`, `subagent.blocks.Count` and
`droppedBlockCount` (MessageListController.cs:271-275) but never the nested
blocks' own status. A nested tool call **completing** therefore changes
nothing, so its spinner keeps spinning until some unrelated change forces a
rebuild. Reported symptom and this one pull in opposite directions -- one
wants fewer rebuilds, the other wants one more -- which is exactly why the
fix has to be "preserve state across rebuilds", not "rebuild less".

### 1.3 Aggravating factor

Expanding a restored card lazy-loads nested blocks and mutates
`blocks.Count` / `droppedBlockCount` (SubagentCard.cs:311-316) -- both
signature inputs -- so the act of expanding schedules an immediate rebuild
of the thing just expanded.

## 2. No sign of activity between tool calls

### 2.1 What the CLI actually sends, measured

The repo's only capture was a 2.1-second single-tool subagent with exactly
one `system/task_progress` event, which made it look like a one-shot
retrospective report. A long run was captured for this note: one subagent,
seven steps.

```
subagent spawned                    10972ms
task_progress #1  "Writing ...s1.txt"       Write  uses=1  tok=30698   16641ms
task_progress #2  "Reading ...s1.txt"       Read   uses=2  tok=32161   21069ms
task_progress #3  "Writing ...s2.txt"       Write  uses=3  tok=34657   25135ms
task_progress #4  "Reading ...s2.txt"       Read   uses=4  tok=35145   28232ms
task_progress #5  "Writing ...s3.txt"       Write  uses=5  tok=35424   31961ms
task_progress #6  "Reading ...s3.txt"       Read   uses=6  tok=35897   34400ms
task_progress #7  "Running List contents.." Bash   uses=7  tok=36176   37291ms
gaps: 4428 4066 3097 3729 2439 2891 ms      all 7 descriptions distinct
```

So `task_progress` **is** a per-step activity feed whose description
changes every time, every 2.4-4.4 seconds. The panel already parses and
renders it (SubagentCard.cs:376-400).

### 2.2 The actual causes of "I cannot tell if it is working"

1. **A 5.7-second blank at the start.** The subagent spawned at 10972 ms;
   the first progress event landed at 16641 ms. Before it,
   `progressLine`/`lastToolName`/`totalTokens` are all empty, and
   `BuildProgressText` returning empty sets the whole row to
   `display: None` (SubagentCard.cs:213-218). The card shows a spinner and
   nothing else during exactly the window the user is asking about.
2. **No staleness cue.** Between events the line simply freezes on the last
   report, indistinguishable from a stalled subagent.
3. **`toolUses` is captured, cached, restored -- and rendered nowhere.**
   Written at AgentHub.cs:2415, persisted (SessionCacheFile.cs:248),
   restored (TranscriptLoader.cs:373); a grep for it across `Editor/UI`
   returns nothing. It is the one field that says "7 steps done" while a
   description says only "Reading a file".
4. The descriptions are **long absolute paths**. Rendered raw they would
   blow out the row, so they need shortening as well as showing -- and per
   the layout rule adopted earlier today, the label must ellipsize rather
   than push anything off-screen.

Not available, so not promised: subagent text never streams (R02c), and
thinking-token events carry no subagent attribution.

## 3. Typing while the turn is running

### 3.1 Measured: the CLI accepts it and STEERS

Probe 1, plain streaming turn. Message 2 written to stdin at 4922 ms while
the first turn was still running (its result arrived at 22568 ms):

```
results received : 1
files on disk    : a.txt b.txt c.txt (turn 1)  AND d.txt (the mid-turn ask)
errors           : none
```

Probe 2, the user's literal scenario -- message 2 sent 195 ms after a
`Task` subagent spawned:

```
results received : 1        turn completed normally at 51206 ms
errors           : none
```

(Probe 2 cannot confirm the instruction was *acted on*: the subagent wrote
into its own scratch directory, so the file check looked in the wrong
place. Probe 1 already established the honouring; probe 2 establishes that
doing it during a subagent fan-out is safe.)

**Conclusion: a mid-turn user message is accepted and folded into the
running turn. One result, regardless of how many user messages were sent
during it.** This is steering, not queuing -- which is what the user wants,
and better than the queue this was expected to need.

### 3.2 What actually blocks the user today

Nothing in the send path. `TrySend` never checks turn state
(ComposerView.cs:363-368), so **pressing Enter mid-turn already works**.
The problem is that the only mouse-reachable control is swapped to "Stop"
while a turn runs, and clicking it interrupts (ComposerView.cs:116-121,
:349-361). The UI says "stop first, then resend", which is precisely the
workflow that was reported. The two input paths disagree with each other.

### 3.3 The correction this measurement forces

`AgentClient` and `TurnTracker` are built on "one result per user message"
(AgentClient.cs:992, TurnTracker.cs:30-46), and both cite research section
R02 4.1 for it. **R02 4.1 measured only SEQUENTIAL sends** -- a message
after the previous result. It never covered a mid-turn send, and the
measurement above shows the assumption is false for that case: two
messages, one result.

That matters concretely. If the panel counts an extra open turn on the
mid-turn send, the single result balances only one of them and the panel
stays "busy" forever. Making the composer send mid-turn without fixing the
accounting would trade one bug for a worse one.

`UapTurnScope`'s boolean latch (flagged during investigation as a
double-`AssetDatabase.Refresh` hazard) turns out **not** to be a hazard
here for the same reason: one result means `EndIfActive` fires once, at the
true end. It is still worth making the invariant explicit rather than
accidental.

## 4. Plan

1. **State preservation.** Extend the existing keyed-dictionary pattern
   from the outer card to nested tool cards and to both scroll offsets,
   keyed by `toolUseId`; restore the outer list scroll across the swap; and
   add nested tool status to the signature so a finished nested call stops
   spinning.
2. **Progress row.** Never hide it while running: fall back to "Working..."
   plus the elapsed timer, add the unrendered `toolUses` step count, add a
   "updated Ns ago" staleness cue (labelled as time since last update, not
   as subagent state), and shorten path-shaped descriptions.
3. **Mid-turn input.** Keep Stop, add a send affordance that stays
   reachable during a turn, and first correct the turn accounting so one
   result closes the turn no matter how many messages were sent into it.
   Amend R02 4.1 to record what was measured here, since three code sites
   currently cite it for something it does not say.
