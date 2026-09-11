# 2026-08-23 -- Phase 3 batch J: model-layer integrity (small/medium items)

The MODEL cluster's S/M items: shared-mutable state, layering, name
safety, cost arithmetic, and one many-to-one attribution bug. The two
remaining L/M transcript items (MODEL-3 size guard, MODEL-6 terminal
session canary) are batch K. Items: MODEL-5/9/10/11/13/14, plus one
found-in-passing file hygiene fix.

## MODEL-11: SessionMetaStore.Get stops sharing a mutable Empty

Get() handed out one static `Empty` SessionMeta for every miss,
"read-only by convention". SessionMeta is a mutable field bag, so a
single caller writing `store.Get(id).pinned = true` on a missing id
would have poisoned the shared instance for every later miss --
silently, everywhere. The miss path now returns a fresh instance (a
tiny allocation on a rare path); the static is gone, so the convention
no longer needs believing. Tests pin: mutating a miss result affects
nothing, and Get never creates entries (Edit remains the creating
accessor).

## MODEL-13: session cost is monotonic across --resume

result.total_cost_usd is the CLI PROCESS's running total. A --resume
spawns a fresh process whose total restarts near zero, and the old
plain overwrite made the session's displayed cumulative cost go
BACKWARDS. AccumulateTurn now takes Math.Max -- non-decreasing -- with
the documented trade that a resumed session under-counts until the new
process's own total passes the old high-water mark. Zero-reporting
(subscription auth) stays zero. New ChatSessionTests pin all three
shapes plus the token sums.

## MODEL-5: HistoryRowMenuModel moves to the UI layer (D9)

The class sat in Colloid.AgentPanel.Model while referencing L10n (UI
layer) -- the one violation of the one-way UI -> Model -> Core rule.
It is a view model by design (labels resolved in-model so tests can
pin real catalog strings; 2026-08-14 note), so it moved to
Editor/UI with its GUID preserved and namespace changed; UI -> Model
(SessionGroup) is the legal direction. `grep "using
Colloid.AgentPanel.UI" Editor/Model Editor/Core` is now empty. The
label-keys alternative stays recorded in the file header for the day
Model purity is enforced mechanically.

## MODEL-9: agent definition names -- case folding + reserved devices

BuildDesiredMap keyed agent names with Ordinal while the filesystems
it writes to (and Sync's own keepPaths set) are case-insensitive:
'Explore' and 'explore' were two map keys colliding on one Explore.md,
with the keep-set disagreeing about what existed. The map is now
OrdinalIgnoreCase; case variants fold, last row wins -- the same
documented rule as exact duplicates. IsValidAgentName additionally
refuses Windows reserved device names (CON/PRN/AUX/NUL/COM1-9/LPT1-9,
case-insensitive -- CreateFile resolves the DEVICE for those,
extension or not) and degenerate all-punctuation names ('-', '__--').
CONSOLE and COM10 stay valid: only the exact device names are magic.

## MODEL-10: modelAlias must survive as a bare YAML scalar

BuildFileContent emits `model: <alias>` as an unquoted plain scalar.
An alias containing ' #' turns the rest into a YAML comment; ': '
re-keys the line; a leading indicator character changes the parse --
each silently corrupts what the CLI reads back. IsYamlSafeModelAlias
refuses those shapes with a logged skip (rejection over quoting: the
bare form is the one measured against the CLI in research 07 section
10.6, and no real alias -- haiku, claude-haiku-4, a full model id --
contains any of them).

## MODEL-14: sessions attributed by their OWN cwd, not the directory's first file

The CLI's cwd -> directory-name transform replaces every
non-alphanumeric with '-', so distinct working directories (Col_lide /
Col-lide) collide into one directory under ~/.claude/projects.
ProjectSessionScanner stamped the FIRST file's cwd onto every session
in the directory -- misattributing the rest, and its own comment
claimed the collision could not happen "by construction". Attribution
now comes from each entry's own transcript (SessionIndexEntry.Cwd,
lazily read and cached alongside the title/preview History reads
anyway); the single directory probe survives only as the cheap early
skip. One directory can therefore yield several ProjectSessions -- one
per distinct Unity-project cwd -- while the no-collision case produces
byte-identical output to before. Non-Unity cwds colliding into the
name are dropped (the scanner's overall filter), and an
unreadable-transcript entry stays listed, attributed to the probed cwd
rather than vanishing.

## Found in passing: a raw NUL byte in TranscriptUsage.cs

The `"\0noid"` sentinel prefix was committed (v0.15.0) with a LITERAL
0x00 byte in the string instead of the two-character escape --
runtime-identical, but it made grep and friends treat the source file
as binary. Replaced with the `\0` escape; the file is plain ASCII
again and the runtime string is unchanged.

## Honest residuals

- MODEL-14 accepts one lazy transcript read per entry when the
  project-grouped History view is used (bounded by the existing scan
  cap, cached with the title/preview reads). The old one-read-per-
  directory claim was cheaper and wrong.
- MODEL-9's reserved-name list is the classic Win32 set; NT namespace
  oddities beyond it (trailing dots/spaces) cannot arise because the
  character class already excludes them.
