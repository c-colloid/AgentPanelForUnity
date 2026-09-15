using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Pure predicate + pinned deny message behind the script validation
    /// gate (design section 7.4/8.2 B1, revised 8.7): when the CLI's OWN
    /// Write/Edit/MultiEdit tool targets a `.cs`/`.asmdef` file under
    /// Assets/ (anywhere, any depth), the gate denies before the write
    /// reaches disk, directing the model to the staging folder +
    /// uap_scripts_commit instead. This SAME predicate/message backs TWO
    /// enforcement layers: AgentHub's can_use_tool pre-filter (default/plan
    /// permission modes) AND the generated PreToolUse hook script
    /// (GateHookInstaller -- the mechanism that actually fires under
    /// acceptEdits, see section 8.7's live-E2E finding that can_use_tool
    /// alone does not).
    ///
    /// Section 8.7 moved the staging folder OUTSIDE Assets/ entirely
    /// (`&lt;projectRoot&gt;/UapStaging/`, no trailing '~' needed -- anything
    /// outside Assets/ is already outside Unity's AssetDatabase). A path
    /// under staging therefore never resolves to an "Assets/"-relative
    /// path in the first place, so IsGatedScriptPath needs no staging
    /// exemption of its own anymore: "not under Assets/" already excludes
    /// it. No UnityEditor/UnityEngine dependency -- pure string logic,
    /// fully unit tested without an editor.
    /// </summary>
    public static class ScriptGate
    {
        /// <summary>
        /// The sanctioned staging folder, relative to the project root and
        /// OUTSIDE Assets/ (design section 8.7 revision -- moved out of
        /// Assets/UapStaging~/ so it is never even scanned by Unity's
        /// asset importer, and so it can never collide with an Assets/**
        /// deny glob). Used only for display/instructional text here; the
        /// actual absolute staging directory is `Path.Combine(projectRoot,
        /// "UapStaging")` (UapScriptsCommitTool).
        /// </summary>
        public const string StagingFolder = "UapStaging/";

        private static readonly HashSet<string> GatedToolNames =
            new HashSet<string>(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit" };

        /// <summary>
        /// The CLI's shell tools, whose `command` string the gate inspects
        /// for a script write (<see cref="TryFindGatedBashTarget"/>). "Bash"
        /// is the tool on macOS/Linux (and Git Bash on Windows); "PowerShell"
        /// is the tool the CLI exposes on Windows, which is where most of
        /// this panel's users run -- until 2026-09-15 the pre-filter compared
        /// against "Bash" alone, so every `Set-Content Assets/Foo.cs` went
        /// through ungated (design note 2026-09-15-script-gate-steering-and-
        /// powershell.md). "Shell" is the generic name ToolCardDescriber
        /// already accepts.
        /// </summary>
        private static readonly HashSet<string> ShellToolNames =
            new HashSet<string>(StringComparer.Ordinal) { "Bash", "PowerShell", "Shell" };

        /// <summary>
        /// PowerShell cmdlets (and their built-in aliases) whose first
        /// positional argument, or whose -Path/-LiteralPath/-FilePath
        /// argument, names the file they write. Matched case-insensitively
        /// (PowerShell command names are).
        /// </summary>
        private static readonly HashSet<string> PowerShellContentWriters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Set-Content", "Add-Content", "Out-File", "New-Item", "ac", "ni"
            };

        /// <summary>PowerShell copy/move cmdlets: the destination is -Destination or the second positional argument.</summary>
        private static readonly HashSet<string> PowerShellCopyMovers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Copy-Item", "Move-Item", "cpi", "mi", "copy", "move"
            };

        /// <summary>
        /// Matches a .NET file-writing call in a PowerShell (or any) command
        /// string -- `[IO.File]::WriteAllText("Assets/Foo.cs", ...)`,
        /// `[System.IO.File]::WriteAllLines('...')`, `WriteAllBytes(...)` --
        /// capturing the first (path) argument.
        /// </summary>
        private static readonly Regex DotNetFileWritePattern = new Regex(
            "::WriteAll(?:Text|Lines|Bytes)\\s*\\(\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)')",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>True for the CLI's own Write/Edit/MultiEdit tools -- the file-path half of the gate.</summary>
        public static bool IsGatedToolName(string toolName)
        {
            return toolName != null && GatedToolNames.Contains(toolName);
        }

        /// <summary>True for the CLI's shell tools (Bash, PowerShell, Shell) -- the command-string half of the gate.</summary>
        public static bool IsShellToolName(string toolName)
        {
            return toolName != null && ShellToolNames.Contains(toolName);
        }

        /// <summary>
        /// True when <paramref name="filePath"/> (absolute or project-
        /// relative, either slash style) resolves to a `.cs`/`.asmdef` file
        /// somewhere under an `Assets/` tree. Case-insensitive throughout
        /// (Windows filesystem semantics). The staging folder lives outside
        /// Assets/ entirely (see the class doc comment), so no separate
        /// exemption is needed here: a genuine staging path simply never
        /// contains an "Assets/" segment and ToAssetsRelativePath returns
        /// null for it.
        /// </summary>
        public static bool IsGatedScriptPath(string filePath)
        {
            string assetsRelative = ToAssetsRelativePath(filePath);
            if (assetsRelative == null)
            {
                return false;
            }
            return assetsRelative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || assetsRelative.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The combined decision the can_use_tool pre-filter acts on.</summary>
        public static bool ShouldAutoDeny(string toolName, string filePath)
        {
            return IsGatedToolName(toolName) && IsGatedScriptPath(filePath);
        }

        /// <summary>
        /// True when <paramref name="filePath"/> is one of the gate's own
        /// config files under UserSettings/AgentPanel/ (the `--settings`
        /// JSON or a stale hook .ps1). Defense in depth for SEC-1: editing
        /// these is how an agent would try to disable the gate (rewrite the
        /// settings JSON) or -- before the hook was inlined via
        /// -EncodedCommand -- escalate to arbitrary code execution by
        /// overwriting the .ps1. The can_use_tool pre-filter denies a
        /// Write/Edit/MultiEdit to them (default/plan modes). Matched
        /// case-insensitively against the tail of a normalized, dot-collapsed
        /// path, requiring a `/` boundary (or an exact project-relative
        /// match) so a decoy folder like "evilUserSettings/" is not caught.
        /// </summary>
        public static bool IsProtectedGateFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }
            string normalized = CollapseDotSegments(filePath.Replace('\\', '/')).ToLowerInvariant();
            string dir = GateHookInstaller.DefaultRelativeDirectory.ToLowerInvariant();
            string hookRel = dir + "/" + GateHookInstaller.HookFileName.ToLowerInvariant();
            string settingsRel = dir + "/" + GateHookInstaller.SettingsFileName.ToLowerInvariant();
            return normalized == hookRel
                || normalized == settingsRel
                || normalized.EndsWith("/" + hookRel, StringComparison.Ordinal)
                || normalized.EndsWith("/" + settingsRel, StringComparison.Ordinal);
        }

        /// <summary>
        /// Deny message for a Write/Edit to a <see cref="IsProtectedGateFile"/>
        /// path. Distinct from <see cref="DenyMessage"/> (which redirects
        /// script writes to staging) because the fix here is "don't touch
        /// this", not "write it elsewhere".
        /// </summary>
        public const string ProtectedGateFileDenyMessage =
            "This file belongs to the Agent Panel for Unity script validation gate and cannot be modified by the agent."
            + " It is regenerated automatically at the start of each session.";

        /// <summary>
        /// The EXACT auto-deny message text (pinned by a test -- design
        /// section 8.2 B1: "the model can self-correct from the message").
        /// Fixed/unparameterized on purpose so there is exactly one string
        /// to pin. Section 8.7 requires this SAME text to back BOTH
        /// enforcement layers: AgentHub's can_use_tool pre-filter reads
        /// this property directly, and GateHookInstaller.BuildHookScript
        /// embeds it verbatim into the generated PowerShell hook's
        /// `permissionDecisionReason` -- so the two layers can never drift
        /// apart. No apostrophes anywhere in this text besides the two
        /// deliberately placed around "UapStaging/": the hook generator
        /// embeds it inside a single-quoted PowerShell string literal by
        /// doubling every `'` (GateHookInstaller.EscapePowerShellSingleQuoted),
        /// so an unexpected extra apostrophe here would silently need the
        /// SAME escaping to stay correct -- keep this text apostrophe-free
        /// except for that one deliberate pair.
        /// </summary>
        public static string DenyMessage
        {
            get
            {
                return "Direct Assets/**/*.cs and *.asmdef writes are blocked by the script validation gate."
                    + " Write this file under the '" + StagingFolder + "' folder at the project root"
                    + " (outside Assets/) instead, then call uap_scripts_commit to validate and move it"
                    + " into Assets/.";
            }
        }

        /// <summary>
        /// Normalizes to forward slashes, COLLAPSES "." / ".." segments
        /// (<see cref="CollapseDotSegments"/> -- fixes the traversal bypass
        /// where e.g. "Assets/Sub/../Evil.cs" textually looks like it might
        /// resolve elsewhere but actually lands directly under Assets/),
        /// then returns the substring starting at the "Assets/" path
        /// segment (case-insensitive), preserving the original casing of
        /// that substring. Null when no such segment exists (e.g. a path
        /// entirely outside the project, or one that collapses to entirely
        /// outside "Assets/" -- which is exactly what makes a genuine
        /// UapStaging/ path, now that it lives outside Assets/ entirely per
        /// section 8.7, resolve to null here with no special-casing).
        /// </summary>
        private static string ToAssetsRelativePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }
            string normalized = CollapseDotSegments(filePath.Replace('\\', '/'));
            string lower = normalized.ToLowerInvariant();
            if (lower.StartsWith("assets/", StringComparison.Ordinal))
            {
                return normalized;
            }
            int idx = lower.IndexOf("/assets/", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return normalized.Substring(idx + 1);
            }
            return null;
        }

        /// <summary>
        /// Collapses "." and ".." segments out of a '/'-separated path
        /// WITHOUT touching the filesystem or depending on the current
        /// working directory (unlike Path.GetFullPath) -- pure string
        /// logic, safe to run on a path from an untrusted tool_use request.
        /// A ".." with no preceding real segment to pop (already at the
        /// root, or another unresolved "..") is kept literally rather than
        /// throwing, matching common normpath semantics. Leading empty
        /// segments (a UNIX-style absolute path) collapse away like any
        /// other segment; that loses the leading '/', but every caller here
        /// only cares whether the RESULT contains an "Assets/" segment, not
        /// whether it is absolute.
        /// </summary>
        private static string CollapseDotSegments(string normalizedPath)
        {
            // The implementation moved to the shared UapAssetPath (OPS-3,
            // SR-asset-path) so the gate and the asset-writing tools can
            // never disagree on what a dot segment resolves to; this
            // wrapper keeps the gate's own call sites unchanged.
            return UapAssetPath.CollapseDotSegments(normalizedPath);
        }

        // -- Bash command inspection (design section 8.3's "mechanism, not
        // instruction" principle extended to the Bash tool: a shell
        // redirection/heredoc can create or overwrite an Assets/**/*.cs or
        // *.asmdef file with zero involvement from the CLI's own
        // Write/Edit/MultiEdit tools, which is all IsGatedToolName ever
        // inspected before this was added) --------------------------------

        /// <summary>
        /// Matches shell output-redirection targets: "&gt;" / "&gt;&gt;" NOT
        /// immediately followed by "&amp;" (excludes fd-duplication like
        /// "2&gt;&amp;1", while still matching a plain fd-prefixed file
        /// redirect like "2&gt;Assets/Evil.cs"), then a quoted or bare path
        /// token.
        /// </summary>
        private static readonly Regex RedirectionTargetPattern = new Regex(
            "(?:>{1,2}\\|?)(?!&)\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s|&;]+))",
            RegexOptions.Compiled);

        /// <summary>Matches `tee [-a] &lt;path&gt;` output targets.</summary>
        private static readonly Regex TeeTargetPattern = new Regex(
            "\\btee\\b\\s+(?:-a\\s+)?(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s|&;]+))",
            RegexOptions.Compiled);

        /// <summary>
        /// Best-effort extraction of every file path a shell
        /// <paramref name="command"/> string could WRITE to. Covered idioms
        /// (SEC-4 widened the original redirection-only set): output
        /// redirection (`&gt;`, `&gt;&gt;`, noclobber-override `&gt;|`),
        /// `tee`, `sed -i`/`--in-place` (the non-flag arguments after the
        /// script), the LAST argument of `cp`/`mv`/`install` (conservative:
        /// only the final token can be a destination, so a gated SOURCE
        /// being copied OUT of Assets/ never trips the gate), and
        /// `dd of=&lt;path&gt;`.
        ///
        /// Deliberately NOT a full shell parser, and provably cannot be
        /// one: an interpreter one-liner (`python -c`, `node -e`, a
        /// heredoc piped INTO an interpreter), command substitution, or a
        /// variable-built path all write files no static token scan can
        /// see. This layer exists to make the gate inconvenient to bypass
        /// by accident, matching how IsGatedScriptPath closes
        /// Write/Edit/MultiEdit; the backstop for a deliberately hostile
        /// command remains the permission card the Bash tool itself goes
        /// through. Candidates all flow into the SAME traversal-safe
        /// IsGatedScriptPath resolution.
        /// </summary>
        private static IEnumerable<string> ExtractBashWriteTargets(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                yield break;
            }
            foreach (Match match in RedirectionTargetPattern.Matches(command))
            {
                string target = FirstGroupValue(match);
                if (target != null)
                {
                    yield return target;
                }
            }
            foreach (Match match in TeeTargetPattern.Matches(command))
            {
                string target = FirstGroupValue(match);
                if (target != null)
                {
                    yield return target;
                }
            }
            foreach (Match match in DotNetFileWritePattern.Matches(command))
            {
                string target = FirstGroupValue(match);
                if (target != null)
                {
                    yield return target;
                }
            }
            foreach (string target in ExtractCommandWriteTargets(command))
            {
                yield return target;
            }
        }

        /// <summary>
        /// SEC-4: the token-based half of ExtractBashWriteTargets -- write
        /// destinations named as command ARGUMENTS (sed -i / cp / mv /
        /// install / dd of=) rather than via redirection syntax. Works per
        /// pipeline segment; the command word is matched bare ("sed", not
        /// "/bin/sed" or "sudo sed" -- documented limit, same best-effort
        /// stance as the class doc).
        /// </summary>
        private static IEnumerable<string> ExtractCommandWriteTargets(string command)
        {
            foreach (string[] tokens in CommandSegments(command))
            {
                if (tokens.Length < 2)
                {
                    continue;
                }
                string cmd = tokens[0];
                if (cmd == "sed")
                {
                    bool inPlace = false;
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        if (tokens[i].StartsWith("-i", StringComparison.Ordinal)
                            || tokens[i] == "--in-place"
                            || tokens[i].StartsWith("--in-place=", StringComparison.Ordinal))
                        {
                            inPlace = true;
                            break;
                        }
                    }
                    if (!inPlace)
                    {
                        continue;
                    }
                    // The first non-flag argument is the sed SCRIPT; every
                    // non-flag argument after it is an in-place-edited file.
                    bool skippedScript = false;
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        string token = tokens[i];
                        if (token.Length == 0 || token[0] == '-')
                        {
                            continue;
                        }
                        if (!skippedScript)
                        {
                            skippedScript = true;
                            continue;
                        }
                        yield return token;
                    }
                }
                else if (cmd == "cp" || cmd == "mv" || cmd == "install")
                {
                    // Conservative: flags and multi-source forms make any
                    // deeper destination guess false-positive-prone; only
                    // the LAST token can be the destination, and a command
                    // with fewer than source+destination args writes nothing.
                    string last = tokens[tokens.Length - 1];
                    if (tokens.Length >= 3 && last.Length > 0 && last[0] != '-')
                    {
                        yield return last;
                    }
                }
                else if (cmd == "dd")
                {
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        if (tokens[i].StartsWith("of=", StringComparison.Ordinal))
                        {
                            yield return tokens[i].Substring(3);
                        }
                    }
                }
                else if (PowerShellContentWriters.Contains(cmd))
                {
                    // `Set-Content Assets/Foo.cs -Value ...`, `"..." | Out-File
                    // -FilePath Assets/Foo.cs`, `New-Item -Path Assets/Foo.cs
                    // -ItemType File`: the target is the named path parameter
                    // when present, else the first positional argument (a
                    // token that neither starts with '-' nor is the value of
                    // some other -Parameter).
                    string named = NamedParameterValue(tokens,
                        "-Path", "-LiteralPath", "-FilePath", "-PSPath");
                    if (named != null)
                    {
                        yield return named;
                        continue;
                    }
                    string positional = FirstPositionalArgument(tokens);
                    if (positional != null)
                    {
                        yield return positional;
                    }
                }
                else if (PowerShellCopyMovers.Contains(cmd))
                {
                    // `Copy-Item src -Destination Assets/Foo.cs` or
                    // `Move-Item src Assets/Foo.cs`: -Destination when named,
                    // else the SECOND positional argument (the first is the
                    // source, which may legitimately be a gated path being
                    // copied OUT of Assets/).
                    string named = NamedParameterValue(tokens, "-Destination");
                    if (named != null)
                    {
                        yield return named;
                        continue;
                    }
                    string positional = NthPositionalArgument(tokens, 1);
                    if (positional != null)
                    {
                        yield return positional;
                    }
                }
            }
        }

        /// <summary>
        /// The token following the first occurrence of any of
        /// <paramref name="names"/> (case-insensitive, PowerShell parameter
        /// names are), or the value after ':' in the `-Path:value` form;
        /// null when none is present or the parameter has no value.
        /// </summary>
        private static string NamedParameterValue(string[] tokens, params string[] names)
        {
            for (int i = 1; i < tokens.Length; i++)
            {
                string token = tokens[i];
                for (int n = 0; n < names.Length; n++)
                {
                    if (string.Equals(token, names[n], StringComparison.OrdinalIgnoreCase))
                    {
                        return i + 1 < tokens.Length ? tokens[i + 1] : null;
                    }
                    if (token.StartsWith(names[n] + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        return token.Substring(names[n].Length + 1);
                    }
                }
            }
            return null;
        }

        private static string FirstPositionalArgument(string[] tokens)
        {
            return NthPositionalArgument(tokens, 0);
        }

        /// <summary>
        /// The <paramref name="index"/>-th (0-based) argument that is neither
        /// a `-Parameter` nor the value bound to the preceding `-Parameter`.
        /// A switch-vs-value ambiguity is resolved conservatively: every
        /// `-Parameter` is assumed to consume the next token, so a positional
        /// path can only be missed (fail-open), never mis-taken from a
        /// parameter value.
        /// </summary>
        private static string NthPositionalArgument(string[] tokens, int index)
        {
            int seen = 0;
            for (int i = 1; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.Length > 0 && token[0] == '-')
                {
                    // `-Path:value` binds inline; a bare `-Path` binds the
                    // next token. Known switches (no value) are skipped alone.
                    if (token.IndexOf(':') < 0 && !IsPowerShellSwitch(token))
                    {
                        i++;
                    }
                    continue;
                }
                if (seen == index)
                {
                    return token;
                }
                seen++;
            }
            return null;
        }

        private static readonly HashSet<string> PowerShellSwitches =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-Force", "-NoNewline", "-Append", "-NoClobber", "-Recurse", "-PassThru",
                "-Confirm", "-WhatIf", "-Raw", "-AsByteStream", "-Container"
            };

        private static bool IsPowerShellSwitch(string token)
        {
            return PowerShellSwitches.Contains(token);
        }

        /// <summary>
        /// Splits a shell command into pipeline/list segments (at unquoted
        /// ';', '|', '&amp;', newline) and each segment into
        /// whitespace-separated tokens with single/double quotes stripped.
        /// No expansion, no escapes -- just enough structure for
        /// ExtractCommandWriteTargets' per-command argument scan.
        /// </summary>
        private static IEnumerable<string[]> CommandSegments(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inSingle = false;
            bool inDouble = false;
            bool hasToken = false;
            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (inSingle)
                {
                    if (c == '\'') { inSingle = false; } else { current.Append(c); }
                    continue;
                }
                if (inDouble)
                {
                    if (c == '"') { inDouble = false; } else { current.Append(c); }
                    continue;
                }
                if (c == '\'')
                {
                    inSingle = true;
                    hasToken = true;
                    continue;
                }
                if (c == '"')
                {
                    inDouble = true;
                    hasToken = true;
                    continue;
                }
                bool isSeparator = c == ';' || c == '|' || c == '&' || c == '\n';
                if (isSeparator || char.IsWhiteSpace(c))
                {
                    if (hasToken)
                    {
                        tokens.Add(current.ToString());
                        current.Length = 0;
                        hasToken = false;
                    }
                    if (isSeparator && tokens.Count > 0)
                    {
                        yield return tokens.ToArray();
                        tokens.Clear();
                    }
                    continue;
                }
                current.Append(c);
                hasToken = true;
            }
            if (hasToken)
            {
                tokens.Add(current.ToString());
            }
            if (tokens.Count > 0)
            {
                yield return tokens.ToArray();
            }
        }

        private static string FirstGroupValue(Match match)
        {
            if (match.Groups["dq"].Success)
            {
                return match.Groups["dq"].Value;
            }
            if (match.Groups["sq"].Success)
            {
                return match.Groups["sq"].Value;
            }
            if (match.Groups["bare"].Success)
            {
                return match.Groups["bare"].Value;
            }
            return null;
        }

        /// <summary>
        /// True when a shell <paramref name="command"/> (Bash or PowerShell
        /// syntax -- both tools' commands flow through the same scan) writes
        /// to a gated script path -- the shell counterpart of
        /// <see cref="ShouldAutoDeny"/>. Does not check the tool name;
        /// callers must already know they are looking at a shell tool_use
        /// (<see cref="IsShellToolName"/>).
        /// </summary>
        public static bool ShouldAutoDenyBashCommand(string command)
        {
            string ignored;
            return TryFindGatedBashTarget(command, out ignored);
        }

        /// <summary>
        /// Same predicate as <see cref="ShouldAutoDenyBashCommand"/>, but
        /// also returns the FIRST offending target path found (for the
        /// auto-deny transcript note) -- null when nothing is gated.
        /// </summary>
        public static bool TryFindGatedBashTarget(string command, out string gatedPath)
        {
            foreach (string candidate in ExtractBashWriteTargets(command))
            {
                if (IsGatedScriptPath(candidate))
                {
                    gatedPath = candidate;
                    return true;
                }
            }
            gatedPath = null;
            return false;
        }
    }
}
