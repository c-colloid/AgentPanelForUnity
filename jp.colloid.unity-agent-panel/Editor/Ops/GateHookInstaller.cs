using System;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Generates and installs the script validation gate's PreToolUse hook
    /// (design section 8.7 -- the mechanism that actually fires under
    /// `--permission-mode acceptEdits`, unlike the can_use_tool pre-filter
    /// alone, per the section's live-E2E finding). Writes ONE file under
    /// `&lt;projectRoot&gt;/UserSettings/AgentPanel/`:
    /// <list type="bullet">
    /// <item><see cref="SettingsFileName"/> -- the `--settings` JSON. When a
    /// PowerShell interpreter exists it wires an INLINE hook: the whole
    /// PowerShell script (<see cref="BuildHookScript"/>, which denies -- via
    /// stdout JSON, exit 0 -- a gated Assets/**/*.cs|*.asmdef target and
    /// exits 0 silently otherwise) is base64-encoded into a
    /// `-EncodedCommand` command line (<see cref="BuildInlineHookCommand"/>),
    /// so NO executable .ps1 is written to the agent-writable UserSettings
    /// tree (SEC-1). Otherwise it emits the narrow `permissions.deny` glob
    /// as a degraded fallback (never both -- see BuildSettingsJson) so an
    /// unrunnable hook still stops the write with a generic message instead
    /// of silently allowing it through.</item>
    /// </list>
    /// A stale <see cref="HookFileName"/> from an older version is deleted
    /// on install so the executable script never lingers on disk.
    ///
    /// Both file bodies are PURE functions of their inputs
    /// (BuildHookScript/BuildSettingsJson) -- no UnityEditor/disk
    /// dependency -- so their exact generated text is unit-tested directly.
    /// Only <see cref="EnsureInstalled"/> touches disk, and it rewrites a
    /// file only when its computed content actually differs from what is
    /// already there (plain content comparison after a read -- equivalent
    /// to a hash compare for this purpose, without pulling in a hashing
    /// dependency for two small text files), so a spawn that runs this on
    /// every StartClient call does not needlessly bump either file's mtime.
    ///
    /// Windows-only (design section 8.7 point 5): PowerShell -File is the
    /// v1 mechanism; a non-Windows platform gets the can_use_tool layer
    /// only (AgentHub gates this class's call site on
    /// RuntimePlatform.WindowsEditor, mirroring every other Windows-only
    /// check in this codebase -- see WindowsCliPathProbe's precedent).
    /// </summary>
    public static class GateHookInstaller
    {
        /// <summary>PowerShell hook script filename, under DefaultRelativeDirectory.</summary>
        public const string HookFileName = "uap-gate-hook.ps1";

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
        private const string HookMatcher = "Write|Edit|MultiEdit";

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
            var sb = new StringBuilder(2048);
            sb.Append("$ErrorActionPreference = 'Stop'\n");
            sb.Append("try {\n");
            sb.Append("    $stdinText = [Console]::In.ReadToEnd()\n");
            sb.Append("    $req = $stdinText | ConvertFrom-Json\n");
            sb.Append("    $toolName = $req.tool_name\n");
            sb.Append("    if ($toolName -ne 'Write' -and $toolName -ne 'Edit' -and $toolName -ne 'MultiEdit') {\n");
            sb.Append("        exit 0\n");
            sb.Append("    }\n");
            sb.Append("    $filePath = $req.tool_input.file_path\n");
            sb.Append("    if ([string]::IsNullOrEmpty($filePath)) {\n");
            sb.Append("        exit 0\n");
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
            sb.Append("        exit 0\n");
            sb.Append("    }\n");
            sb.Append("    $lowerRelative = $assetsRelative.ToLowerInvariant()\n");
            sb.Append("    if (-not ($lowerRelative.EndsWith('.cs') -or $lowerRelative.EndsWith('.asmdef'))) {\n");
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
        /// Builds the PowerShell command line that runs the gate hook
        /// INLINE, with no .ps1 file on disk. The entire hook script
        /// (<see cref="BuildHookScript"/>) is base64-encoded as UTF-16LE and
        /// passed via `-EncodedCommand`, exactly the encoding PowerShell's
        /// -EncodedCommand expects (Convert.ToBase64String over
        /// Encoding.Unicode bytes). Security (SEC-1, the acceptEdits
        /// privilege escalation this closes): the previous `-File
        /// &lt;hook.ps1&gt;` form kept an executable script under
        /// UserSettings/AgentPanel/ that ScriptGate does NOT protect, so
        /// under `--permission-mode acceptEdits` (where the can_use_tool
        /// pre-filter never fires) an agent could overwrite that .ps1 and
        /// have the CLI run arbitrary PowerShell on the next Write/Edit. With
        /// the script inlined into the `--settings` JSON there is no hook
        /// file to overwrite, and settings.json is read only at spawn (so a
        /// mid-session rewrite cannot affect the running process, and the
        /// next spawn regenerates it). base64 is pure ASCII and contains no
        /// whitespace, so it needs no shell quoting.
        /// </summary>
        public static string BuildInlineHookCommand(string denyMessage)
        {
            string script = BuildHookScript(denyMessage);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        }

        /// <summary>
        /// Builds the `--settings` JSON wiring the INLINE PowerShell hook
        /// (<see cref="BuildInlineHookCommand"/>, built from
        /// <paramref name="denyMessage"/>) into a PreToolUse hook for
        /// Write/Edit/MultiEdit, via JsonWriter (the same escaping-authority
        /// used for every other outbound JSON payload here). When
        /// <paramref name="includeDenyFallback"/> is true, emits the narrow
        /// `permissions.deny` glob confirmed by the pre-implementation
        /// probe (design section 8.7 point 3) INSTEAD OF the hook: it
        /// blocks a gated Assets/**/*.cs write, does not over-block a
        /// non-script Assets/ write, and does not block a write outside
        /// Assets/ (the staging folder).
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
        public static string BuildSettingsJson(string denyMessage, bool includeDenyFallback)
        {
            JsonNode root = JsonNode.NewObject();
            if (includeDenyFallback)
            {
                root.Set("permissions", JsonNode.NewObject()
                    .Set("deny", JsonNode.NewArray()
                        .Add("Edit(Assets/**/*.cs)")
                        .Add("Edit(Assets/**/*.asmdef)")));
                return JsonWriter.Write(root);
            }
            string command = BuildInlineHookCommand(denyMessage);
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
        /// Ensures the `--settings` JSON exists under
        /// `&lt;projectRoot&gt;/UserSettings/AgentPanel/` with up-to-date
        /// content, rewriting it only when its freshly computed content
        /// differs from what is already on disk (so a spawn that calls this
        /// every time does not needlessly touch its mtime). The PowerShell
        /// hook is now INLINED into that JSON via `-EncodedCommand`
        /// (<see cref="BuildInlineHookCommand"/>), so NO .ps1 is written --
        /// this closes SEC-1, the acceptEdits escalation where a writable
        /// hook file let an agent run arbitrary PowerShell. Any stale
        /// <see cref="HookFileName"/> left by an older version is deleted so
        /// the executable script never lingers on disk.
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
            string hookPath = Path.Combine(directory, HookFileName);
            string settingsPath = Path.Combine(directory, SettingsFileName);

            string settingsJson = BuildSettingsJson(ScriptGate.DenyMessage, !IsHookRunnable());

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to create '" + directory + "': " + ex.Message);
                return null;
            }

            // The hook is inlined into settings.json now; remove any .ps1 a
            // previous version wrote so no editable executable script is
            // left under the agent-writable UserSettings tree (SEC-1).
            TryDeleteStaleHook(hookPath, log);

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
        /// settings JSON body is pure ASCII today; UTF-8 is headroom), only
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
