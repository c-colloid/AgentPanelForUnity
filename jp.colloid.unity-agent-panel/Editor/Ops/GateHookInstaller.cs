using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Generates and installs the script validation gate's PreToolUse hook
    /// (design section 8.7 -- the mechanism that actually fires under
    /// `--permission-mode acceptEdits`, unlike the can_use_tool pre-filter
    /// alone, per the section's live-E2E finding). Writes TWO files under
    /// `&lt;projectRoot&gt;/UserSettings/AgentPanel/`:
    /// <list type="bullet">
    /// <item>The hook script (<see cref="BuildHookScript"/>, which denies --
    /// via stdout JSON, exit 0 -- a gated Assets/**/*.cs|*.asmdef target and
    /// exits 0 silently otherwise), under a CONTENT-ADDRESSED name
    /// (<see cref="HookFileNameFor"/>).</item>
    /// <item><see cref="SettingsFileName"/> -- the `--settings` JSON. When a
    /// PowerShell interpreter exists it wires a short INLINE bootstrap
    /// (<see cref="BuildBootstrapScript"/>, passed as `-EncodedCommand`)
    /// that reads the hook script once, compares its SHA-256 with the value
    /// PINNED in the command line itself, and only then runs the bytes it
    /// hashed. Otherwise it emits the narrow `permissions.deny` glob as a
    /// degraded fallback (never both -- see BuildSettingsJson) so an
    /// unrunnable hook still stops the write with a generic message instead
    /// of silently allowing it through.</item>
    /// </list>
    ///
    /// Why not the whole script inline (the 2026-08-22 SEC-1 shape): once
    /// the shell-command scan was added (2026-09-15) the base64 of the
    /// UTF-16 script passed 17,000 characters, more than twice cmd.exe's
    /// 8,191-character command line limit, and the CLI refused to run the
    /// hook at all -- the gate's only layer under acceptEdits was gone.
    /// The bootstrap stays around 2,000 characters plus the path, however
    /// large the script grows. Why this keeps SEC-1 closed: the script
    /// file is agent-writable again, but a rewritten file no longer matches
    /// the pinned hash, so the bootstrap DENIES the tool call instead of
    /// running it (fail closed), and the hash lives in the settings JSON the
    /// CLI reads only at spawn. Design note:
    /// docs/design-notes/2026-11-26-gate-hook-hash-pinned-bootstrap.md.
    ///
    /// The file bodies are PURE functions of their inputs -- no
    /// UnityEditor/disk dependency -- so their exact generated text is
    /// unit-tested directly. Only <see cref="EnsureInstalled"/> touches
    /// disk, and it rewrites a file only when its computed content actually
    /// differs from what is already there, so a spawn that runs this on
    /// every StartClient call does not needlessly bump either file's mtime.
    ///
    /// Windows-only (design section 8.7 point 5): a non-Windows platform
    /// gets the can_use_tool layer only (AgentHub gates this class's call
    /// site on RuntimePlatform.WindowsEditor, mirroring every other
    /// Windows-only check in this codebase -- see WindowsCliPathProbe's
    /// precedent).
    /// </summary>
    public static class GateHookInstaller
    {
        /// <summary>
        /// The hook script filename of the versions before the hook was
        /// inlined (2026-08-22). Never written anymore; a leftover one is
        /// still deleted on install and still counts as a protected gate
        /// file (<see cref="IsHookFileName"/>).
        /// </summary>
        public const string HookFileName = "uap-gate-hook.ps1";

        /// <summary>Prefix of the content-addressed hook script filename (<see cref="HookFileNameFor"/>).</summary>
        public const string HookFilePrefix = "uap-gate-hook-";

        /// <summary>Extension of the hook script file.</summary>
        public const string HookFileExtension = ".ps1";

        /// <summary>
        /// Longest hook command line this class will wire. cmd.exe refuses
        /// anything over 8,191 characters, and that is the shortest limit a
        /// hook command can meet on Windows (CreateProcess allows 32,767);
        /// the rest is margin. A command over this is NOT shipped -- the
        /// settings JSON falls back to the `permissions.deny` glob, because
        /// a hook the CLI refuses to start gates nothing.
        /// </summary>
        public const int MaxHookCommandLength = 8000;

        /// <summary>
        /// What the bootstrap answers when the hook script is missing or no
        /// longer matches the pinned hash. It cannot tell a gated write from
        /// a harmless one without the script, so it denies every matched
        /// tool call and says how to recover. ASCII, and free of quotes and
        /// backslashes by convention (it is JSON-encoded and then embedded
        /// in a single-quoted PowerShell literal; both steps escape, this
        /// just keeps the generated text readable).
        /// </summary>
        public const string UnverifiedHookDenyMessage =
            "The Agent Panel for Unity script validation gate could not verify its hook script"
            + " (the file under UserSettings/AgentPanel/ is missing or was modified), so this tool call"
            + " was blocked. Tell the user to start a new session from the Agent Panel; that regenerates"
            + " the file. Do not try to recreate or edit it.";

        /// <summary>`--settings` JSON filename, under DefaultRelativeDirectory.</summary>
        public const string SettingsFileName = "uap-gate-settings.json";

        /// <summary>
        /// Directory the two files live under, relative to the project
        /// root -- UserSettings/AgentPanel is already panel-written
        /// territory (cli-process.json, SessionCache.json).
        /// </summary>
        public const string DefaultRelativeDirectory = "UserSettings/AgentPanel";

        /// <summary>
        /// The `--settings`-wired PowerShell command line, matcher, and
        /// deny-fallback glob all reference the SAME tool/extension set the
        /// can_use_tool layer gates (ScriptGate) -- see BuildHookScript's
        /// doc comment for why the predicate itself is reimplemented in
        /// PowerShell rather than shared code.
        /// </summary>
        private const string HookMatcher = "Write|Edit|MultiEdit|Bash|PowerShell";

        // -- Pure builders (unit-tested directly, no disk access) ---------------

        /// <summary>
        /// Builds the exact PowerShell hook script text (ASCII-only, per
        /// this repo's source convention). Reads the ENTIRE stdin payload
        /// (`[Console]::In.ReadToEnd()`), parses it as the PreToolUse hook
        /// JSON (`{tool_name, tool_input, ...}` -- design section 8.7's
        /// measured contract), and denies (prints the deny JSON to stdout,
        /// exit 0) exactly when `tool_name` is Write/Edit/MultiEdit AND
        /// `tool_input.file_path` resolves to a `.cs`/`.asmdef` file under
        /// an `Assets/` tree -- the SAME predicate as
        /// <see cref="ScriptGate.IsGatedScriptPath"/>, reimplemented here in
        /// PowerShell (segment-by-segment '.'/'..' collapse, case-
        /// insensitive "assets/" segment search) because the hook runs as
        /// an independent child process with no access to this assembly's
        /// managed code. MultiEdit is covered by the SAME top-level
        /// `tool_input.file_path` check -- MultiEdit edits multiple
        /// locations WITHIN one file, not multiple files, so (per the
        /// measured wire shape) it carries file_path at the top level just
        /// like Write/Edit, and no separate `tool_input.edits[]` walk is
        /// needed. Any parse failure or missing file_path exits 0 (allow)
        /// silently -- fail-open at the hook level, matching design section
        /// 8.7 point 5 ("hook must never throw on malformed stdin"); the
        /// deny-glob fallback and can_use_tool layer remain as the safety
        /// net for a hook that cannot even start.
        /// </summary>
        public static string BuildHookScript(string denyMessage)
        {
            string escapedReason = EscapePowerShellSingleQuoted(denyMessage ?? string.Empty);
            var sb = new StringBuilder(4096);
            sb.Append("$ErrorActionPreference = 'Stop'\n");
            sb.Append("function Test-UapGatedPath([string]$filePath) {\n");
            sb.Append("    if ([string]::IsNullOrEmpty($filePath)) {\n");
            sb.Append("        return $false\n");
            sb.Append("    }\n");
            sb.Append("    $normalized = $filePath -replace '\\\\', '/'\n");
            sb.Append("    $segments = $normalized -split '/'\n");
            sb.Append("    $stack = New-Object 'System.Collections.Generic.List[string]'\n");
            sb.Append("    foreach ($segment in $segments) {\n");
            sb.Append("        if ($segment -eq '' -or $segment -eq '.') {\n");
            sb.Append("            continue\n");
            sb.Append("        }\n");
            sb.Append("        if ($segment -eq '..') {\n");
            sb.Append("            if ($stack.Count -gt 0 -and $stack[$stack.Count - 1] -ne '..') {\n");
            sb.Append("                $stack.RemoveAt($stack.Count - 1)\n");
            sb.Append("            } else {\n");
            sb.Append("                $stack.Add($segment)\n");
            sb.Append("            }\n");
            sb.Append("            continue\n");
            sb.Append("        }\n");
            sb.Append("        $stack.Add($segment)\n");
            sb.Append("    }\n");
            sb.Append("    $collapsed = [string]::Join('/', $stack.ToArray())\n");
            sb.Append("    $lower = $collapsed.ToLowerInvariant()\n");
            sb.Append("    $assetsRelative = $null\n");
            sb.Append("    if ($lower.StartsWith('assets/')) {\n");
            sb.Append("        $assetsRelative = $collapsed\n");
            sb.Append("    } else {\n");
            sb.Append("        $idx = $lower.IndexOf('/assets/')\n");
            sb.Append("        if ($idx -ge 0) {\n");
            sb.Append("            $assetsRelative = $collapsed.Substring($idx + 1)\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    if ($null -eq $assetsRelative) {\n");
            sb.Append("        return $false\n");
            sb.Append("    }\n");
            sb.Append("    $lowerRelative = $assetsRelative.ToLowerInvariant()\n");
            sb.Append("    return ($lowerRelative.EndsWith('.cs') -or $lowerRelative.EndsWith('.asmdef'))\n");
            sb.Append("}\n");
            sb.Append("function Get-UapShellWriteTargets([string]$command) {\n");
            sb.Append("    $targets = New-Object 'System.Collections.Generic.List[string]'\n");
            sb.Append("    if ([string]::IsNullOrEmpty($command)) {\n");
            sb.Append("        return $targets\n");
            sb.Append("    }\n");
            sb.Append("    $quotedOrBare = '(?:\"(?<dq>[^\"]*)\"|''(?<sq>[^'']*)''|(?<bare>[^\\s|&;]+))'\n");
            sb.Append("    $patterns = @(\n");
            sb.Append("        ('(?:>{1,2}\\|?)(?!&)\\s*' + $quotedOrBare),\n");
            sb.Append("        ('\\btee\\b\\s+(?:-a\\s+)?' + $quotedOrBare),\n");
            sb.Append("        '::WriteAll(?:Text|Lines|Bytes)\\s*\\(\\s*(?:\"(?<dq>[^\"]*)\"|''(?<sq>[^'']*)'')'\n");
            sb.Append("    )\n");
            sb.Append("    foreach ($pattern in $patterns) {\n");
            sb.Append("        foreach ($m in [regex]::Matches($command, $pattern, 'IgnoreCase')) {\n");
            sb.Append("            foreach ($g in @('dq', 'sq', 'bare')) {\n");
            sb.Append("                if ($m.Groups[$g].Success) { $targets.Add($m.Groups[$g].Value); break }\n");
            sb.Append("            }\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    $contentWriters = @('set-content', 'add-content', 'out-file', 'new-item', 'ac', 'ni')\n");
            sb.Append("    $copyMovers = @('copy-item', 'move-item', 'cpi', 'mi', 'copy', 'move')\n");
            sb.Append("    $switches = @('-force', '-nonewline', '-append', '-noclobber', '-recurse', '-passthru', '-confirm', '-whatif', '-raw', '-asbytestream', '-container')\n");
            sb.Append("    $segments = New-Object 'System.Collections.Generic.List[object]'\n");
            sb.Append("    $tokens = New-Object 'System.Collections.Generic.List[string]'\n");
            sb.Append("    foreach ($m in [regex]::Matches($command, '\"(?<dq>[^\"]*)\"|''(?<sq>[^'']*)''|(?<sep>[;|&\\n])|(?<bare>[^\\s;|&]+)')) {\n");
            sb.Append("        if ($m.Groups['sep'].Success) {\n");
            sb.Append("            if ($tokens.Count -gt 0) { $segments.Add($tokens.ToArray()); $tokens.Clear() }\n");
            sb.Append("            continue\n");
            sb.Append("        }\n");
            sb.Append("        foreach ($g in @('dq', 'sq', 'bare')) {\n");
            sb.Append("            if ($m.Groups[$g].Success) { $tokens.Add($m.Groups[$g].Value); break }\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    if ($tokens.Count -gt 0) { $segments.Add($tokens.ToArray()) }\n");
            sb.Append("    foreach ($seg in $segments) {\n");
            sb.Append("        if ($seg.Count -lt 2) { continue }\n");
            sb.Append("        $cmd = $seg[0].ToLowerInvariant()\n");
            sb.Append("        if ($cmd -eq 'sed') {\n");
            sb.Append("            $inPlace = $false\n");
            sb.Append("            foreach ($t in $seg) { if ($t.StartsWith('-i') -or $t -eq '--in-place' -or $t.StartsWith('--in-place=')) { $inPlace = $true } }\n");
            sb.Append("            if (-not $inPlace) { continue }\n");
            sb.Append("            $skipped = $false\n");
            sb.Append("            for ($i = 1; $i -lt $seg.Count; $i++) {\n");
            sb.Append("                $t = $seg[$i]\n");
            sb.Append("                if ($t.Length -eq 0 -or $t[0] -eq '-') { continue }\n");
            sb.Append("                if (-not $skipped) { $skipped = $true; continue }\n");
            sb.Append("                $targets.Add($t)\n");
            sb.Append("            }\n");
            sb.Append("        } elseif ($cmd -eq 'cp' -or $cmd -eq 'mv' -or $cmd -eq 'install') {\n");
            sb.Append("            $last = $seg[$seg.Count - 1]\n");
            sb.Append("            if ($seg.Count -ge 3 -and $last.Length -gt 0 -and $last[0] -ne '-') { $targets.Add($last) }\n");
            sb.Append("        } elseif ($cmd -eq 'dd') {\n");
            sb.Append("            foreach ($t in $seg) { if ($t.StartsWith('of=')) { $targets.Add($t.Substring(3)) } }\n");
            sb.Append("        } elseif ($contentWriters -contains $cmd -or $copyMovers -contains $cmd) {\n");
            sb.Append("            $isCopy = $copyMovers -contains $cmd\n");
            sb.Append("            $names = if ($isCopy) { @('-destination') } else { @('-path', '-literalpath', '-filepath', '-pspath') }\n");
            sb.Append("            $named = $null\n");
            sb.Append("            $positional = New-Object 'System.Collections.Generic.List[string]'\n");
            sb.Append("            for ($i = 1; $i -lt $seg.Count; $i++) {\n");
            sb.Append("                $t = $seg[$i]\n");
            sb.Append("                $lt = $t.ToLowerInvariant()\n");
            sb.Append("                if ($t.Length -gt 0 -and $t[0] -eq '-') {\n");
            sb.Append("                    $colon = $lt.IndexOf(':')\n");
            sb.Append("                    if ($colon -ge 0) {\n");
            sb.Append("                        if ($null -eq $named -and $names -contains $lt.Substring(0, $colon)) { $named = $t.Substring($colon + 1) }\n");
            sb.Append("                        continue\n");
            sb.Append("                    }\n");
            sb.Append("                    if ($switches -contains $lt) { continue }\n");
            sb.Append("                    if ($null -eq $named -and $names -contains $lt -and $i + 1 -lt $seg.Count) { $named = $seg[$i + 1] }\n");
            sb.Append("                    $i++\n");
            sb.Append("                    continue\n");
            sb.Append("                }\n");
            sb.Append("                $positional.Add($t)\n");
            sb.Append("            }\n");
            sb.Append("            if ($null -ne $named) {\n");
            sb.Append("                $targets.Add($named)\n");
            sb.Append("            } else {\n");
            sb.Append("                $want = if ($isCopy) { 1 } else { 0 }\n");
            sb.Append("                if ($positional.Count -gt $want) { $targets.Add($positional[$want]) }\n");
            sb.Append("            }\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    return $targets\n");
            sb.Append("}\n");
            sb.Append("try {\n");
            sb.Append("    $stdinText = [Console]::In.ReadToEnd()\n");
            sb.Append("    $req = $stdinText | ConvertFrom-Json\n");
            sb.Append("    $toolName = $req.tool_name\n");
            sb.Append("    $gated = $false\n");
            sb.Append("    if ($toolName -eq 'Write' -or $toolName -eq 'Edit' -or $toolName -eq 'MultiEdit') {\n");
            sb.Append("        $gated = Test-UapGatedPath $req.tool_input.file_path\n");
            sb.Append("    } elseif ($toolName -eq 'Bash' -or $toolName -eq 'PowerShell' -or $toolName -eq 'Shell') {\n");
            sb.Append("        $command = $req.tool_input.command\n");
            sb.Append("        if ([string]::IsNullOrEmpty($command)) { $command = $req.tool_input.script }\n");
            sb.Append("        foreach ($target in (Get-UapShellWriteTargets $command)) {\n");
            sb.Append("            if (Test-UapGatedPath $target) { $gated = $true; break }\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    if (-not $gated) {\n");
            sb.Append("        exit 0\n");
            sb.Append("    }\n");
            sb.Append("    $reason = '").Append(escapedReason).Append("'\n");
            sb.Append("    $result = [ordered]@{\n");
            sb.Append("        hookSpecificOutput = [ordered]@{\n");
            sb.Append("            hookEventName = 'PreToolUse'\n");
            sb.Append("            permissionDecision = 'deny'\n");
            sb.Append("            permissionDecisionReason = $reason\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("    $result | ConvertTo-Json -Depth 5 -Compress\n");
            sb.Append("    exit 0\n");
            sb.Append("} catch {\n");
            sb.Append("    exit 0\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Doubles every `'` so <paramref name="value"/> can be embedded
        /// verbatim inside a PowerShell single-quoted string literal (the
        /// only escaping single-quoted strings need or support -- unlike
        /// double-quoted strings, `$`/backtick are never special inside
        /// them, so no other character needs touching).
        /// </summary>
        internal static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        /// <summary>
        /// SHA-256 of <paramref name="script"/> as it is stored on disk
        /// (UTF-8, no BOM -- the bytes AtomicFile writes), as 64 uppercase
        /// hex digits: the form `[BitConverter]::ToString(...)` minus its
        /// dashes produces in the bootstrap.
        /// </summary>
        public static string ComputeScriptHash(string script)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(script ?? string.Empty);
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The hook script's filename for a given <see cref="ComputeScriptHash"/>
        /// value: the prefix, the first 16 hex digits (lowercase), `.ps1`.
        /// Content-addressed so two script versions never share a file: a
        /// CLI process that outlives a package update (it is re-attached
        /// across domain reloads) keeps finding the file ITS command line
        /// pinned, instead of being denied everything because a newer spawn
        /// rewrote a shared file under it. The name is only an address --
        /// the bootstrap checks the full 256-bit hash, not the name.
        /// </summary>
        public static string HookFileNameFor(string hashHex)
        {
            string hex = (hashHex ?? string.Empty).ToLowerInvariant();
            if (hex.Length > 16)
            {
                hex = hex.Substring(0, 16);
            }
            return HookFilePrefix + hex + HookFileExtension;
        }

        /// <summary>
        /// True when <paramref name="fileName"/> (a bare name, no directory)
        /// is a hook script this class wrote, in either naming: the legacy
        /// fixed name or a content-addressed one. Case-insensitive.
        /// </summary>
        public static bool IsHookFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }
            if (string.Equals(fileName, HookFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return fileName.Length > HookFilePrefix.Length + HookFileExtension.Length
                && fileName.StartsWith(HookFilePrefix, StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(HookFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the bootstrap the hook command line carries. It reads the
        /// hook script's bytes ONCE, hashes those bytes, and runs those same
        /// bytes -- so there is no window between the check and the use in
        /// which the file could be swapped. Anything that goes wrong before
        /// the script is verified (file missing, unreadable, hash mismatch)
        /// prints a deny decision and exits 0: fail CLOSED, unlike the hook
        /// script's own fail-open on malformed stdin, because here the
        /// failure means the gate itself is not the code the panel wrote.
        /// The script runs OUTSIDE the try, so its own errors keep the
        /// semantics <see cref="BuildHookScript"/> documents.
        /// </summary>
        public static string BuildBootstrapScript(string hookAbsolutePath, string hashHex)
        {
            string denyJson = JsonWriter.Write(JsonNode.NewObject()
                .Set("hookSpecificOutput", JsonNode.NewObject()
                    .Set("hookEventName", "PreToolUse")
                    .Set("permissionDecision", "deny")
                    .Set("permissionDecisionReason", UnverifiedHookDenyMessage)));
            var sb = new StringBuilder(1024);
            sb.Append("$ErrorActionPreference = 'Stop'\n");
            sb.Append("try {\n");
            sb.Append("    $bytes = [IO.File]::ReadAllBytes('")
                .Append(EscapePowerShellSingleQuoted(hookAbsolutePath)).Append("')\n");
            sb.Append("    $sha = [Security.Cryptography.SHA256]::Create()\n");
            sb.Append("    $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')\n");
            sb.Append("    if ($hash -ne '").Append(EscapePowerShellSingleQuoted(hashHex))
                .Append("') { throw 'hash mismatch' }\n");
            sb.Append("    $block = [scriptblock]::Create([Text.Encoding]::UTF8.GetString($bytes))\n");
            sb.Append("} catch {\n");
            sb.Append("    '").Append(EscapePowerShellSingleQuoted(denyJson)).Append("'\n");
            sb.Append("    exit 0\n");
            sb.Append("}\n");
            sb.Append("& $block\n");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the PowerShell command line of the hook: the bootstrap
        /// (<see cref="BuildBootstrapScript"/>) base64-encoded as UTF-16LE
        /// and passed via `-EncodedCommand`, exactly the encoding PowerShell
        /// expects (Convert.ToBase64String over Encoding.Unicode bytes).
        /// base64 is pure ASCII with no whitespace, so it needs no shell
        /// quoting, and a non-ASCII project path survives intact inside it.
        /// </summary>
        public static string BuildInlineHookCommand(string hookAbsolutePath, string hashHex)
        {
            string bootstrap = BuildBootstrapScript(hookAbsolutePath, hashHex);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
            return "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        }

        /// <summary>
        /// Builds the `--settings` JSON wiring the bootstrap command
        /// (<see cref="BuildInlineHookCommand"/>) into a PreToolUse hook for
        /// Write/Edit/MultiEdit and the shell tools, via JsonWriter (the
        /// same escaping-authority used for every other outbound JSON
        /// payload here). When <paramref name="includeDenyFallback"/> is
        /// true -- or the command would exceed
        /// <see cref="MaxHookCommandLength"/> (a pathologically long project
        /// path) -- emits the narrow `permissions.deny` glob confirmed by
        /// the pre-implementation probe (design section 8.7 point 3) INSTEAD
        /// OF the hook: it blocks a gated Assets/**/*.cs write, does not
        /// over-block a non-script Assets/ write, and does not block a write
        /// outside Assets/ (the staging folder).
        ///
        /// The two are mutually exclusive because the deny rule
        /// SHORT-CIRCUITS the hook: measured twice (implementation probe,
        /// then live E2E 2026-08-02 in the real editor) -- with both
        /// present the write is still blocked, but the model receives the
        /// CLI's generic "denied by your permission settings" text and NOT
        /// the hook's actionable staging guidance, and the live agent
        /// explicitly reported "the denial message offers no guidance" and
        /// gave up instead of re-routing through staging. Self-correction
        /// is the whole point of the gate (design 8.2 B1), so a runnable
        /// hook must never be shadowed by the deny glob; the glob is only
        /// for machines where the hook could not run at all (see
        /// <see cref="IsHookRunnable"/>).
        /// </summary>
        public static string BuildSettingsJson(string hookAbsolutePath, string hashHex, bool includeDenyFallback)
        {
            JsonNode root = JsonNode.NewObject();
            string command = includeDenyFallback ? null : BuildInlineHookCommand(hookAbsolutePath, hashHex);
            if (command == null || command.Length > MaxHookCommandLength)
            {
                root.Set("permissions", JsonNode.NewObject()
                    .Set("deny", JsonNode.NewArray()
                        .Add("Edit(Assets/**/*.cs)")
                        .Add("Edit(Assets/**/*.asmdef)")));
                return JsonWriter.Write(root);
            }
            root.Set("hooks", JsonNode.NewObject()
                .Set("PreToolUse", JsonNode.NewArray()
                    .Add(JsonNode.NewObject()
                        .Set("matcher", HookMatcher)
                        .Set("hooks", JsonNode.NewArray()
                            .Add(JsonNode.NewObject()
                                .Set("type", "command")
                                .Set("command", command))))));
            return JsonWriter.Write(root);
        }

        // -- Installer (touches disk) --------------------------------------------

        /// <summary>
        /// Ensures the hook script and the `--settings` JSON exist under
        /// `&lt;projectRoot&gt;/UserSettings/AgentPanel/` with up-to-date
        /// content, rewriting each only when its freshly computed content
        /// differs from what is already on disk (so a spawn that calls this
        /// every time does not needlessly touch its mtime -- and so a hook
        /// script an agent tampered with is put back at the next spawn).
        /// The script is written BEFORE the settings that pin its hash; if
        /// it cannot be written, or no PowerShell exists, the settings carry
        /// the `permissions.deny` fallback instead of a hook that would deny
        /// everything. A legacy fixed-name <see cref="HookFileName"/> is
        /// deleted. Content-addressed scripts of OTHER versions are left
        /// alone on purpose: a CLI process started by an older package
        /// version may still be running against one (see
        /// <see cref="HookFileNameFor"/>); they are a few KB each and appear
        /// only when the script text itself changes.
        ///
        /// Returns the settings JSON file's absolute path (for
        /// AgentClientOptions.SettingsFilePath -- the CLI resolves
        /// `--settings` relative to its own cwd, so an ABSOLUTE path is
        /// required per design section 8.7's measured contract) on success,
        /// or null when the file could not be written (logged; the caller
        /// falls back to omitting `--settings` for this spawn -- the
        /// can_use_tool layer still applies).
        /// </summary>
        public static string EnsureInstalled(string projectRoot, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return null;
            }
            string directory = Path.Combine(projectRoot,
                DefaultRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            string legacyHookPath = Path.Combine(directory, HookFileName);
            string settingsPath = Path.Combine(directory, SettingsFileName);

            string script = BuildHookScript(ScriptGate.DenyMessage);
            string hash = ComputeScriptHash(script);
            string hookPath = Path.Combine(directory, HookFileNameFor(hash));

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to create '" + directory + "': " + ex.Message);
                return null;
            }

            TryDeleteStaleHook(legacyHookPath, log);

            bool hookReady = IsHookRunnable() && WriteIfChanged(hookPath, script, log);
            if (hookReady && BuildInlineHookCommand(hookPath, hash).Length > MaxHookCommandLength)
            {
                Log(log, "The hook command line would exceed " + MaxHookCommandLength
                    + " characters (project path too long); using the permissions.deny fallback.");
            }
            string settingsJson = BuildSettingsJson(hookPath, hash, !hookReady);

            if (!WriteIfChanged(settingsPath, settingsJson, log))
            {
                return null;
            }
            return settingsPath;
        }

        private static void TryDeleteStaleHook(string hookPath, Action<string> log)
        {
            try
            {
                if (File.Exists(hookPath))
                {
                    File.Delete(hookPath);
                }
            }
            catch (Exception ex)
            {
                Log(log, "Failed to remove stale hook '" + hookPath + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Whether a PowerShell interpreter this machine can actually run
        /// the generated hook with exists. Decides which of the two
        /// mutually exclusive enforcement shapes
        /// <see cref="BuildSettingsJson"/> emits (hook when runnable, the
        /// generic deny glob when not) -- they must never both ship,
        /// because a present deny rule short-circuits the hook and costs
        /// the agent its actionable redirect (measured; see that method's
        /// doc comment).
        ///
        /// Probes the same way <see cref="Colloid.AgentPanel.Core.Process.WindowsCliPathProbe"/>
        /// resolves an executable: the well-known System32 location first
        /// (the only one that matters on a stock Windows install), then a
        /// PATH scan for either the Windows PowerShell or PowerShell Core
        /// executable name. Pure enough to unit test through
        /// <see cref="ResolvePowerShellPath"/>; this wrapper only supplies
        /// the real environment.
        /// </summary>
        internal static bool IsHookRunnable()
        {
            return ResolvePowerShellPath(
                Environment.GetEnvironmentVariable("SystemRoot"),
                Environment.GetEnvironmentVariable("PATH")) != null;
        }

        /// <summary>
        /// Pure resolution behind <see cref="IsHookRunnable"/>: returns the
        /// first existing PowerShell executable path, or null when neither
        /// the System32 well-known location nor any PATH entry holds one.
        /// Null/empty inputs are tolerated (treated as "nothing to search").
        /// </summary>
        internal static string ResolvePowerShellPath(string systemRoot, string pathVariable)
        {
            if (!string.IsNullOrEmpty(systemRoot))
            {
                string wellKnown = Path.Combine(systemRoot,
                    "System32" + Path.DirectorySeparatorChar
                    + "WindowsPowerShell" + Path.DirectorySeparatorChar
                    + "v1.0" + Path.DirectorySeparatorChar + "powershell.exe");
                if (SafeFileExists(wellKnown))
                {
                    return wellKnown;
                }
            }
            if (string.IsNullOrEmpty(pathVariable))
            {
                return null;
            }
            string[] directories = pathVariable.Split(Path.PathSeparator);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i].Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }
                for (int n = 0; n < PowerShellExecutableNames.Length; n++)
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(directory, PowerShellExecutableNames[n]);
                    }
                    catch (ArgumentException)
                    {
                        // Invalid characters in a PATH entry -- skip it
                        // rather than failing the whole probe.
                        break;
                    }
                    if (SafeFileExists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            return null;
        }

        private static readonly string[] PowerShellExecutableNames =
            { "powershell.exe", "pwsh.exe" };

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes text to <paramref name="path"/> as UTF-8 with no BOM (the
        /// hook script is pure ASCII; the settings JSON is too, its base64
        /// carrying any non-ASCII path), only
        /// when it differs from the file's current content -- delegated to
        /// the shared MODEL-2 helper, which also makes the write ATOMIC
        /// (staged tmp + swap) instead of the previous in-place
        /// File.WriteAllText a crash could truncate mid-write.
        /// </summary>
        private static bool WriteIfChanged(string path, string content, Action<string> log)
        {
            return Colloid.AgentPanel.Core.FileIo.AtomicFile.WriteAllTextIfChanged(
                path, content, delegate (string message) { Log(log, message); });
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[GateHookInstaller] " + message);
            }
        }
    }
}
