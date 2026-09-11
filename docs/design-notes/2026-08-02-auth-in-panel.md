# In-panel Claude Code login / logout

Date: 2026-08-02
Status: approved (direct user request: 「ClaudeCodeのログインログアウトをエージェント
パネル上で行えるようにしたい」)
Ships as: v0.10.0

## 1. Measured CLI facts (2026-08-02, CLI v2.1.220, scratchpad authbox/)

All probed against a sandboxed `CLAUDE_CONFIG_DIR` so the real credentials were
never touched:

- `claude auth status --json` (read-only, fast): logged-in shape
  `{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty",
  "email":…,"orgId":…,"orgName":…,"subscriptionType":"max"}`; logged-out shape
  `{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}`.
- `claude auth login` (subscription default; also `--console`, `--sso`,
  `--email <e>`): prints `Opening browser to sign in…`, then
  `If the browser didn't open, visit: <oauth-url>`, then waits on
  `Paste code here if prompted > ` reading the pasted authorization code from
  STDIN. It attempts to open the system browser itself. With stdin closed it
  waits indefinitely (probe killed via timeout). The OAuth redirect goes to
  platform.claude.com which displays a code for the user to copy — i.e. this
  is a paste-code flow, not a localhost callback.
- `claude auth logout`: non-interactive, immediate, idempotent (succeeds with
  exit 0 even when already logged out), prints a one-line confirmation.
- `CLAUDE_CONFIG_DIR` redirects the whole credential/config store — used by
  probes and by any test that dares to spawn the real binary. NEVER set in
  production spawns.
- UNVERIFIED (cannot complete OAuth headlessly): the exit code of a
  *successful* `auth login`. The design therefore never trusts the login exit
  code — after the login process exits for any reason, the panel re-runs
  `auth status --json` and treats THAT as the truth.

## 2. Design

### 2.1 Core: AuthCli (Editor/Core/Process, alongside CliVersionProbe)

- One-shot `Status(Action<AuthStatus>)`: spawns `<cli> auth status --json`
  with the async OutputDataReceived pattern (same as CliVersionProbe — no
  blocking ReadToEnd), 10 s timeout, tolerant JSON parse via the existing
  JsonNode layer. `AuthStatus { LoggedIn, Email, SubscriptionType, AuthMethod }`,
  parse failures => a NotAvailable result, never an exception.
- Interactive-lite `BeginLogin(...)`: spawns `<cli> auth login`, streams
  stdout, extracts the OAuth URL (first `https://` token in the output;
  fixture-pinned regex), raises state events: Starting -> UrlAvailable ->
  WaitingForCode -> Exited. `SubmitCode(string)` writes the pasted code +
  newline to stdin (UTF-8 to BaseStream, same CP932-avoidance rule as the
  main client). `Cancel()` kills the process tree. On process exit (ANY exit
  code) the owner re-queries Status — see the unverified-fact rule above.
- `Logout(Action<bool>)`: spawns `<cli> auth logout`, 15 s timeout.
- CLI path comes from the same resolution the panel already uses (manual
  path setting / detection); no new discovery logic.
- Domain reload: a live login process is killed by the same teardown path
  that already kills the resident CLI (registered with the reload lifecycle);
  the user simply restarts the flow. No resume across reloads (a pending
  OAuth state is one-time anyway).

### 2.2 UI

- Settings gains an **Account** card (above About): status row
  (logged in as `<email>` (`<subscriptionType>`) / not logged in / checking),
  a Login button (visible when logged out; also a "re-login" affordance when
  logged in), and a Logout button behind an EditorUtility.DisplayDialog
  confirmation (logout kills the resident session; the dialog says so).
- Login flow (inline card inside the Account section, not a separate window):
  instruction text, the OAuth URL as a selectable field + "Open browser" +
  "Copy" buttons (the CLI already tries to open the browser; the buttons are
  the fallback the CLI itself suggests), a code TextField + Submit, Cancel.
- The existing not-logged-in setup card in the chat view gains a "Log in"
  button that jumps to Settings > Account and starts the flow (reuse, not a
  second implementation).
- After a login attempt completes: re-run Status; on LoggedIn, refresh the
  Account card AND nudge the hub (reconnect/StartFresh path the setup card
  already uses) so the panel becomes usable without a manual restart. After
  logout: teardown the client first, then run logout, then refresh.

### 2.3 What is NOT done

- No credential text ever passes through the panel except the one-time OAuth
  authorization code the user pastes (that is the CLI's own designed flow).
- No CLAUDE_CONFIG_DIR in production. No auto-logout anywhere. Tests never
  spawn `auth logout`/`auth login` against the real config: unit tests use
  captured fixture strings only; the single live test (`auth status --json`,
  read-only) is UAP_LIVE_CLI-gated like the other live tests.

## 3. Verification limits (honest)

Sandbox tests + live E2E can cover: status display, login-flow UI up to the
code prompt, cancel (process killed, credentials untouched), URL extraction.
The final happy-path click-through (real OAuth completion, real logout) can
only be exercised by the user; called out in the release notes.

## 4. Regression guards

- AuthStatus JSON parse tests (logged-in/logged-out/malformed/empty fixtures).
- OAuth URL extraction test pinned to the captured probe output (including
  the `…` ellipsis and the `Paste code here if prompted > ` prompt with no
  trailing newline).
- Login state machine transitions as pure logic (no process).
- L10n parity auto-covers the new strings.
