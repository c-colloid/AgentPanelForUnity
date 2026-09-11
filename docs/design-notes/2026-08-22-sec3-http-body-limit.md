# 2026-08-22 -- SEC-3: bound the UapOps request body (pre-auth OOM)

Phase 1 security fix (review finding SEC-3), immediately after SEC-2 (token
off the command line) on the same CI safety net.

## The problem

`UapOpsHttpServer.HandleContext` buffered the ENTIRE request body
(`StreamReader.ReadToEnd()`) into a managed string **before authentication**
-- the Bearer check lives inside `UapOpsRequestHandler.Handle`, which only
runs after the body is already in memory. Any local process (no token
required -- this is exactly the attacker the token cannot stop pre-auth)
could POST a multi-gigabyte body to `127.0.0.1:<port>/mcp` and balloon the
Unity Editor process into an OOM crash, taking unsaved scene work with it.

## The fix

- `MaxRequestBodyBytes = 4 MB` -- far above any real MCP JSON-RPC payload
  (tool calls, initialize handshakes; even a whole script file inside a tool
  argument is a fraction of it) while making the worst-case pre-auth
  allocation harmless.
- An honestly declared oversized `Content-Length` is rejected with **413**
  without reading a byte (optimization, not the guard).
- The real guard, `ReadBodyBounded`, reads in 8 KB chunks and bails with 413
  the moment the cumulative size would cross the limit -- the attacker's
  remaining bytes are never pulled off the socket. A lying `Content-Length`
  or a chunked stream is therefore caught the same way.
- Decoding runs a `StreamReader` over the buffered bytes, so BOM handling is
  byte-identical to the previous direct-StreamReader path (pinned by a test:
  a UTF-8 BOM-prefixed `{}` still decodes to `{}`).

## Testing

`UapOpsHttpServerBodyLimitTests` drives the internal reader over
`MemoryStream`/a counting stub -- no sockets, per the existing rule that
HTTP-socket integration stays out of the EditMode suite (thread/timing;
see `UapOpsHttpServer`'s doc comment). Coverage: intact small/empty/CJK
bodies, exact-limit accepted, limit+1 rejected with null body, the
stop-reading proof (a 64 KB stream is abandoned after ~limit bytes), BOM
stripping, null-encoding default, and an order-of-magnitude pin on the
constant.

Not added to the license-free smoke tier: compiling `UapOpsHttpServer.cs`
there would drag in the handler/session-store/tool-registry graph; the
EditMode suite now runs on every push (INFRA-1), which is exactly the net
this belongs in.

## Residual (out of SEC-3 scope, recorded)

A slow-drip client (slowloris-style) can still park one worker thread per
connection inside a bounded read; the bind stays loopback-only, which keeps
that a local-nuisance tier issue. If it ever matters, the fix is read
timeouts/connection caps at the listener layer, not a tighter body limit.
