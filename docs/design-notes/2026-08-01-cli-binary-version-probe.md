# Design note: About's "CLI version" still sticks on "not connected" (binary version probe)

- Date: 2026-08-01 / Status: adopted
- Related: docs/design-notes/2026-08-01-init-message-retention.md (the
  previous attempt at this exact user-visible symptom, adopted the same
  day, superseded here -- not reverted, its retention mechanism is still
  correct and still used as tier 2 below).

## Root cause (measured, not guessed)

Raw-wire capture on the live panel: for a freshly spawned or `--resume`d
connection that the user has not yet sent a first message to, **only the
`initialize` `control_response` arrives** -- `system/init` is never sent
by the CLI until the connection's first user message has actually been
processed. That is not a timing race that eventually resolves itself; on
an idle connection it never resolves, because nothing ever triggers the
CLI to emit it.

The previous fix (init-message-retention.md) closed a real, different gap
-- a `StateChanged`/`SessionIdChanged` notification gap for the case where
`system/init` DOES eventually arrive after `Ready` was already reached
from the control_response alone. It could not, by construction, help the
idle-connection case: `AgentHub.LastKnownCliVersion` and the live
`AgentClient.InitMessage` are both still legitimately empty, because
`system/init` was never emitted at all, not merely not-yet-processed. This
is exactly the state the user kept reporting: panel visibly connected and
usable, About row stuck on "not connected".

## Options considered

| Option | Description | Verdict | Why |
|---|---|---|---|
| (a) Send a lightweight synthetic/no-op message on connect to force `system/init` early | Makes the CLI emit `system/init` sooner, closing the gap through the existing protocol path | Rejected | Sends the CLI a message the user never asked for, on every connect -- pollutes the transcript/session history and could have side effects depending on what the CLI treats as a "first message" (cost, context-window consumption, visible turn). A version probe must not be observable as a conversation turn. |
| (b) Read the version straight from the resolved CLI binary (`&lt;cliPath&gt; --version`), independent of the stream-json protocol entirely | Answers "what CLI version is this" from the one place that is always true regardless of connection/session state: the installed binary | **Adopted** | Zero protocol interaction, works for a connection that has never sent a message and even before any connection exists at all (right after CLI-path resolution). Matches how a user would answer this question themselves (`claude --version` in a terminal). |
| (c) Poll `AgentClient.InitMessage` less naively (e.g. wait N seconds before giving up) | Simple, no new subprocess | Rejected | Does not actually solve the idle case -- there is no amount of waiting that produces a `system/init` line the CLI is never going to send until a message is sent. Would only mask the defect as "eventually times out to not connected" instead of fixing it. |

## Decision

- New `Colloid.AgentPanel.Core.Process.CliVersionProbe`: runs
  `<cliPath> --version` on a `ThreadPool` worker (never the editor main
  thread), redirecting only stdout (no deadlock risk from an undrained
  second redirected stream), with a 10s timeout. The result is marshaled
  back onto the editor main thread through a `ConcurrentQueue<Action>`
  drained on `EditorApplication.update` -- the same producer/consumer
  idiom `LineChannel`/`EditorUpdatePump` already use for the live CLI
  transport's stdout/stderr, reused rather than introducing a second
  threading pattern for one more subprocess. `ParseVersionOutput` (pure,
  unit tested) keeps the first non-blank trimmed line of stdout verbatim
  (e.g. `"2.1.218 (Claude Code)"`), matching what the About row already
  displays for the live/persisted tiers.
- Per-path caching lives in two new `SessionStateBridge` keys,
  `CliBinaryVersion` + `CliBinaryVersionPath` (the path it was probed
  for) -- a changed CLI path is therefore always a cache miss, and a
  reader must compare the recorded path against the currently resolved
  one before trusting the value (`SettingsView
  .GetCachedBinaryVersionForCurrentCliPath` does this). An in-memory
  "already attempted this domain load" set inside `CliVersionProbe`
  itself (separate from the `SessionStateBridge` cache) prevents
  `SettingsView.RefreshCliStatus` -- which now triggers the probe on
  every call, including from the high-frequency `AgentHub.Changed` refresh
  during a streaming turn -- from spawning a new `--version` subprocess
  more than once per distinct resolved path.
- `SettingsView.RefreshCliVersionLabel`'s precedence becomes: (1) the live
  `AgentClient.InitMessage.ClaudeCodeVersion`; (2) `AgentHub
  .LastKnownCliVersion` (the previous fix's retained/persisted
  `system/init` value) while the client is connected/connecting; (3) the
  new binary-probe value for the currently-resolved path; (4) "not
  connected" only once none of the three resolve to anything (which now
  means, in practice, no CLI path resolves at all -- a resolved path
  always at least attempts the probe). The precedence itself is a pure
  function, `SettingsView.ResolveCliVersionDisplay`, unit tested
  independent of any live `AgentClient`/CLI process.
- New L10n string `SettingsCliVersionUnconfirmedFmt` ("CLI version: {0}
  (not yet connected)" / "CLIバージョン: {0}(未接続)") is used only when
  tier (3) is what won, so the About row never implies a confirmed live
  session when it is actually reporting what the binary itself claims.

## Regression guard

`CliVersionProbeTests.cs` pins `ParseVersionOutput` against the real
captured stdout shape (including the CRLF/blank-line/whitespace-only
edge cases) and `ResolveCliVersionDisplay`'s full precedence order,
including the specific case this note exists for: no live version, no
persisted version, only a binary-probe version -- asserting the result is
flagged `fromBinaryProbeOnly` so the "not yet connected" format variant is
selected rather than silently reusing the confirmed-live text.

## Verification

Compile + full EditMode suite runs in this round's combined verify stage
(see the verification log for exact numbers) -- not run standalone here
per this round's instruction to avoid two agents deadlocking on the
shared Unity batch-mode/AITemp sandbox lock.
