# 2026-08-23 -- Phase 3 batch H: the CLI transport stops racing itself

The CORE cluster around process lifecycle: three races/hangs in
ClaudeCliProcess and its one-shot siblings, plus two pure guards at the
edges (command-line size, JSON surrogates). Items: CORE-3/4/5/7/8/10.

## CORE-4: Start and instant-death stop racing (lifecycle-lock)

Process.Exited is subscribed (EnableRaisingEvents already true) BEFORE
Start() returns, so a process that dies instantly can run
OnProcessExited on a ThreadPool thread concurrently with Start's field
assignments. The losing interleaving: OnProcessExited's _running=false
lands first, Start's unconditional _running=true overwrites it --
"IsRunning yet dead", with _processId possibly still -1 so the
orphan-children sweep silently skips. Now both sides serialize on a
new _lifecycleLock: Start assigns fields under the lock and marks
running only via `ShouldMarkRunning(process.HasExited)` (the one pure
decision, unit-pinned); OnProcessExited sets _running=false under the
same lock and backfills _processId from the sender when the exit beat
Start to it. Lock discipline: never call out (events, killer, waits)
while holding it; no site holds it together with _stdinLock.

## CORE-5: the final result line outruns the death verdict

Process.Exited does not wait for the async stdout reader to flush, so
the CLI's final `result` line of a SUCCESSFUL turn could still be in
flight when AgentClient's pump sees "transport exited + Output empty"
and declares the turn Errored -- dropping the success that lands a
beat later. OnProcessExited now drains before raising Exited:
`WaitForExit(OutputFlushMillis)` -- Unity's Mono includes the async
readers' EOF in the timed overload's budget -- bounded at 1500ms so a
grandchild holding the inherited stdout handle cannot park the
ThreadPool thread (the CORE-3 hang, which is exactly why the
parameterless overload is not used). AgentClient's buffer-first death
check (:445) is untouched; the existing
ProcessExit_DeliversBufferedLinesBeforeErroring test already pins the
consumer-side ordering this guarantees the input for.

## CORE-3: one-shot runners get a bounded flush (OneShotCli)

AuthCli.RunOneShot and CliVersionProbe.RunProbeBlocking were
line-for-line duplicates, and both paired their timed WaitForExit with
a PARAMETERLESS WaitForExit() "to flush" -- an unbounded wait on
stdout EOF that a grandchild-inherited handle can hold open forever,
leaking a ThreadPool worker per probe. Both now delegate to a shared
OneShotCli.Run whose post-exit flush is WaitForExit(OutputFlushMillis).
The price of the bound: a line still in flight after 1500ms is lost,
which for `--version`/`auth status` means a retryable parse failure,
never a leak. The constants' finiteness is contract, pinned by test.

## CORE-10: Dispose sends EOF before the hammer

Dispose (the hard-teardown path) killed without ever closing stdin;
if Kill was a no-op (pid<=0, access denied) the child never saw EOF
and could linger. Dispose now calls CloseStdin() first, mirroring
Stop(); CloseStdin was already idempotent (_stdinClosed guard), and
the Stop->Dispose / Dispose->Dispose orderings are test-pinned.

## CORE-7: the Windows command-line cap fails loudly, before the spawn

CreateProcess caps executable+arguments at 32767 chars; a huge
custom-instructions sidecar (--append-system-prompt) or inline MCP
config blows it and the spawn fails opaquely or truncates. Start now
refuses first: `CommandLineWouldExceedLimit` (pure, threshold 32000
for margin) behind a Windows-only platform gate
(ForceCommandLineLimitCheckForTests seams it for CI on Linux), routed
through the existing HandleProcessDeath -> ProcessDied surface the hub
already renders, with the actual length and the remedy in the message.
Unix ARG_MAX is megabytes, so the guard deliberately does not fire
there -- an over-32k line that works on macOS/Linux keeps working.

## CORE-8: lone surrogates stop corrupting silently

JsonWriter passed ALL non-ASCII chars through raw. Correct for CJK and
real surrogate PAIRS (UTF-8 encodes them properly at the stdin write),
but a LONE half -- split emoji, corrupted paste -- becomes U+FFFD at
Encoding.UTF8.GetBytes, silently mangling the user's text. AppendEscaped
now emits valid pairs raw (unchanged bytes for real emoji) and
\uXXXX-escapes lone halves; JSON permits an unpaired \uXXXX and the
parser's ParseUnicodeEscape reads it straight back, so the round-trip
is lossless and the byte stream never contains a replacement char.
Pinned in JsonParserTests and the smoke tier (both compile the writer).

## Honest residuals

- The lifecycle-lock's cross-thread interleavings and the post-exit
  drain are validated by construction and by the UAP_LIVE_CLI-gated
  live suite, not by EditMode tests -- the project rule (no real
  subprocesses in unit tests) is deliberate and kept.
- The Mono-WaitForExit-drains-within-timeout behavior CORE-5 leans on
  is Unity-Mono-specific (.NET Framework reference source only drains
  on the infinite overload); if the runtime ever changes, the bound
  degrades to "usually already flushed", not to a hang.
