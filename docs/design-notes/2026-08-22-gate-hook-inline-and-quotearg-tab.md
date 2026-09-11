# 2026-08-22 -- Gate hook inlined (SEC-1) + QuoteArg tab (CORE-1)

First implementation batch off the review remediation roadmap. Both are
Phase 1 items; both are pure-function-heavy and unit-tested. Unity was not
available to compile in this environment, so verification rests on the
EditMode tests added/updated here plus close reading -- CI (INFRA-1) is the
planned mechanism to run them.

## SEC-1 -- script validation gate hook no longer lives on disk

### Problem (review finding, High / security)

The PreToolUse hook is `uap-gate-hook.ps1`, written under
`<projectRoot>/UserSettings/AgentPanel/` and invoked by the CLI as
`powershell ... -ExecutionPolicy Bypass -File "<hook.ps1>"` on every
Write/Edit/MultiEdit. But `ScriptGate` only protects `Assets/**/*.cs|*.asmdef`,
so under `--permission-mode acceptEdits` (where the can_use_tool pre-filter
never fires -- the very reason the hook exists) an agent could overwrite that
.ps1 with arbitrary PowerShell and have the CLI run it, with
`-ExecutionPolicy Bypass`, on the next edit. `EnsureInstalled` re-validated
content only at spawn, so a mid-session overwrite went undetected. An edit-only
trust level (acceptEdits) thus escalated to arbitrary native code execution,
bypassing the Bash approval gate.

### Decision

Eliminate the on-disk hook entirely by inlining the PowerShell via
`-EncodedCommand` (base64 of the UTF-16LE script). With no `.ps1` on disk there
is nothing to overwrite; `settings.json` is read only at spawn, so a
mid-session rewrite cannot affect the running process, and the next spawn
regenerates it. Chosen over an ACL/hash approach because C# code is not on the
hook execution path (it cannot hash-check the file each run), and the inline
form removes the attack surface rather than guarding it.

### Design

- `GateHookInstaller.BuildInlineHookCommand(denyMessage)` (new, pure) returns
  `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand <base64>`.
  base64 is pure ASCII with no whitespace, so no shell quoting is needed and
  the previous "escape the absolute path" concern disappears.
- `BuildSettingsJson(denyMessage, includeDenyFallback)` (signature changed from
  `hookAbsolutePath` to `denyMessage`) embeds that command. The deny-glob
  fallback (no PowerShell on the machine) is unchanged.
- `EnsureInstalled` writes only the settings JSON now, and `File.Delete`s any
  stale `uap-gate-hook.ps1` a prior version left, so no editable executable
  lingers. Return contract (settings absolute path, or null on failure) is
  unchanged, so its caller (`AgentHub.StartClient`) needs no change.
- Defense in depth: `ScriptGate.IsProtectedGateFile(path)` +
  `ProtectedGateFileDenyMessage` (new). `AgentHub.TryAutoDenyForScriptGate`
  denies a Write/Edit/MultiEdit targeting the gate's own config files with a
  "don't touch this" message (covers default/plan modes; under acceptEdits the
  settings rewrite is already inert/regenerated). The predicate matches the
  gate dir + filename at a `/` boundary so a decoy folder like
  `evilUserSettings/` is not caught.

### Tests

- GateHookInstallerTests: `-File` shape pins replaced with `-EncodedCommand`
  round-trip (decode base64 -> `Encoding.Unicode` -> equals BuildHookScript);
  `EnsureInstalled_WritesOnlySettingsFile_NoHookPs1`,
  `EnsureInstalled_RemovesStaleHookPs1FromAnOlderVersion`; non-ASCII-root test
  repurposed (no path embedded now).
- ScriptGateTests: `IsProtectedGateFile` -- project-relative, absolute
  Windows/Unix, case-insensitive, decoy-folder rejection, unrelated, null.
- ShouldAutoDeny semantics are unchanged, so all existing gate pins still hold.

### Not done here (follow-ups from the roadmap)

- SEC-2 (Bearer token on the command line) is a separate Phase 1 item.
- The acceptEdits path still relies on the hook for Assets/**/*.cs; that is
  unchanged and correct -- this fix only removes the *file* that made the hook
  itself an escalation vector.

## CORE-1 -- QuoteArg quotes TAB (and other control chars)

### Problem (review finding, Medium / Japanese-first)

`AgentClient.QuoteArg` triggered quoting only on space / `"` / `\n` / `\r`.
CommandLineToArgvW also splits on TAB. A CJK custom-instruction line can
contain a tab with no ASCII space (e.g. `"重要:<TAB>Unityのみ使用"`), which the
old guard left unquoted -- the CLI then saw two arguments and reinterpreted the
tail as a stray positional (or failed to spawn).

### Design

Replaced the four-char guard with `NeedsQuoting(value)`, which quotes on
`"`, space, or any control character `< 0x20` (covers tab, newline, CR). Empty
stays unquoted, matching prior behavior. The backslash/quote escaping body is
unchanged.

### Tests

`AgentClientOptionsExtensionTests.AppendSystemPromptWithTabNoAsciiSpace_IsQuoted`
(a tabbed CJK value with no space must now be quoted). The existing CJK,
space, newline, quote, and backslash pins are unaffected.
