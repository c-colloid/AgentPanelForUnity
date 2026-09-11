# 2026-08-22 -- SEC-2: UapOps Bearer token off the process command line

Phase 1 security fix (review finding SEC-2), building on the roadmap's CI
safety net (dotnet-smoke + EditMode, both green before this change).

## The problem

The UapOps HTTP server (design section 1.1) authenticates with a per-session
Bearer token (`UapOpsAuth`). That token was embedded in the inline
`--mcp-config` JSON and passed as a **command-line argument** to the spawned
CLI process (`AgentClient.BuildArguments`).

A process's command line is readable by other local processes:

- Linux: `/proc/<pid>/cmdline` is world-readable **regardless of umask** --
  every user on the machine can read every process's arguments.
- Windows: WMI `Win32_Process.CommandLine` / Task Manager expose it (same
  user always; administrators for all processes).

Any local reader could lift the token and drive the UapOps server directly
(asset writes, scene edits, uloop -- whatever modules are enabled), bypassing
the panel's permission UI. Loopback binding does not help against a *local*
attacker, which is exactly who the token exists to keep out.

## The fix

`UapOpsMcpConfig.EnsureConfigFileWritten(projectRoot, port, token)` writes the
same payload to `UserSettings/AgentPanel/uap-mcp-config.json` (the package's
one well-known generated-file directory, same as the script gate's files) and
returns the **absolute path**; `AgentHub.ComputeMcpConfigValue` (replacing
`ComputeMcpConfigJson`) passes that path as the `--mcp-config` value. The CLI
auto-detects file path vs inline string and connects identically either way --
measured in docs/research/08-mcp-transport.md section 1.2 (both shapes
produced the same `system/init` `mcp_servers` result).

`AgentClient` needed **no plumbing change**: `McpConfigJson` now carries the
path, `--strict-mcp-config` pairing and the null-omits-both contract are
unchanged, and `QuoteArg` already handles paths (space-containing paths get
quoted; the trailing-backslash rule is covered by existing CORE-1 tests).

Write behavior mirrors `GateHookInstaller`: create-directory, then
write-only-when-content-differs (the token rotates per server start, not per
spawn, so reconnect spawns skip the write). The small write-if-changed helper
is deliberately a local copy pending MODEL-2's shared write utility.

## Fallback: inline JSON when the file cannot be written

If the directory/file write fails (logged), `ComputeMcpConfigValue` falls
back to the old inline JSON rather than disabling UapOps. Rationale: forcing
that failure requires local file control -- an attacker who already has user
rights and has nothing to gain from the fallback -- while a broken
`UserSettings` directory should degrade availability-first for the developer.
The fallback is pinned by a test as the ONE path that may still carry the
token inline.

## Considered and rejected

- **Write-protecting the config file via `ScriptGate.IsProtectedGateFile`**:
  adds ~zero security. The file is regenerated from live server state before
  every spawn, so an agent edit never survives to a process start; and the
  token grants the agent nothing it does not already have through the CLI's
  own authorized MCP connection. Left unprotected to keep the gate's deny
  message accurate ("script validation gate") and the diff tight.
- **Deleting the file when UapOps is disabled**: a stale file holds a dead
  token (server stopped -> token gone with it), so it is inert. Skipped to
  avoid extra IO/seams; the next enabled spawn rewrites it.
- **chmod 600 on Unix**: `File.SetUnixFileMode` is .NET 7+, unavailable on
  Unity 2022.3's profile. The file inherits the project directory's ACLs,
  which the user already controls for all project content -- the win over a
  world-readable command line stands regardless.

## Test coverage

- `UapOpsMcpConfigTests`: payload equality, absolute path under the gate
  directory, token-rotation rewrite, same-content idempotence, null-root
  null.
- `AgentHubStartClientArgTests.ComputeMcpConfigValue_*`: disabled -> null,
  file write + shape, **the SEC-2 pin** (returned command-line value never
  contains the token), unwritable-root inline fallback.
- `ci/SmokeTests` (license-free tier): same assertions against the verbatim
  production source, so a regression fails in seconds on every push.
