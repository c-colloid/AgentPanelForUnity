using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Exact-pin tests for the generated PreToolUse hook + `--settings`
    /// JSON (design section 8.7) -- both builders are pure functions of
    /// their inputs, so their generated text is pinned directly without
    /// touching disk. EnsureInstalled's disk-writing/idempotence behavior
    /// is covered separately below with a real temp directory.
    /// </summary>
    [TestFixture]
    public class GateHookInstallerTests
    {
        // -- BuildHookScript --------------------------------------------------

        /// <summary>
        /// The exact hook script body, built the SAME way
        /// GateHookInstaller.BuildHookScript assembles it (line-for-line),
        /// so a future accidental edit to either one is caught by a plain
        /// string diff instead of silently drifting.
        /// </summary>
        private static string ExpectedHookScript(string escapedReason)
        {
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

        [Test]
        public void BuildHookScript_SimpleReason_MatchesExactExpectedText()
        {
            string script = GateHookInstaller.BuildHookScript("Simple reason, no quotes.");
            Assert.AreEqual(ExpectedHookScript("Simple reason, no quotes."), script);
        }

        [Test]
        public void BuildHookScript_ReasonWithSingleQuote_DoublesIt()
        {
            string script = GateHookInstaller.BuildHookScript("Write under 'UapStaging/' instead.");
            Assert.AreEqual(ExpectedHookScript("Write under ''UapStaging/'' instead."), script);
        }

        [Test]
        public void BuildHookScript_NullReason_TreatedAsEmpty()
        {
            string script = GateHookInstaller.BuildHookScript(null);
            Assert.AreEqual(ExpectedHookScript(string.Empty), script);
        }

        [Test]
        public void BuildHookScript_IsAsciiOnly()
        {
            // This repo's source convention (task spec: "ASCII-only ps1
            // content") -- the CURRENT ScriptGate.DenyMessage is itself
            // ASCII, so this also guards against a future edit to that
            // message accidentally introducing a non-ASCII character.
            string script = GateHookInstaller.BuildHookScript(ScriptGate.DenyMessage);
            foreach (char c in script)
            {
                Assert.LessOrEqual((int)c, 0x7F, "non-ASCII character found: U+" + ((int)c).ToString("X4"));
            }
        }

        [Test]
        public void BuildHookScript_EmbedsTheSharedDenyMessage()
        {
            // Design section 8.7: the hook's reason text must come from the
            // SAME constant as the can_use_tool layer, so the two can never
            // drift apart.
            string script = GateHookInstaller.BuildHookScript(ScriptGate.DenyMessage);
            StringAssert.Contains(
                GateHookInstaller.EscapePowerShellSingleQuoted(ScriptGate.DenyMessage), script);
        }

        [Test]
        public void EscapePowerShellSingleQuoted_DoublesEverySingleQuote()
        {
            Assert.AreEqual("it''s a ''test''", GateHookInstaller.EscapePowerShellSingleQuoted("it's a 'test'"));
        }

        [Test]
        public void EscapePowerShellSingleQuoted_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, GateHookInstaller.EscapePowerShellSingleQuoted(null));
            Assert.AreEqual(string.Empty, GateHookInstaller.EscapePowerShellSingleQuoted(string.Empty));
        }

        // -- BuildInlineHookCommand + BuildSettingsJson -------------------------

        [Test]
        public void BuildInlineHookCommand_UsesEncodedCommand_ThatDecodesToTheHookScript()
        {
            // SEC-1: the hook runs inline via -EncodedCommand (base64 of the
            // UTF-16LE script), so no .ps1 lives on disk for an agent to
            // overwrite. The command must not reference a -File path.
            string command = GateHookInstaller.BuildInlineHookCommand(ScriptGate.DenyMessage);
            StringAssert.StartsWith(
                "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", command);
            StringAssert.DoesNotContain("-File", command);
            string base64 = command.Substring(command.LastIndexOf(' ') + 1);
            string decoded = Encoding.Unicode.GetString(System.Convert.FromBase64String(base64));
            Assert.AreEqual(GateHookInstaller.BuildHookScript(ScriptGate.DenyMessage), decoded);
        }

        [Test]
        public void BuildSettingsJson_WithoutDenyFallback_InlinesHookViaEncodedCommand_NoPs1()
        {
            string json = GateHookInstaller.BuildSettingsJson(ScriptGate.DenyMessage, includeDenyFallback: false);
            JsonNode root = JsonParser.Parse(json);
            Assert.AreEqual("Write|Edit|MultiEdit|Bash|PowerShell", root["hooks"]["PreToolUse"][0]["matcher"].AsString());
            string command = root["hooks"]["PreToolUse"][0]["hooks"][0]["command"].AsString();
            StringAssert.Contains("-EncodedCommand", command);
            StringAssert.DoesNotContain("-File", command);
            StringAssert.DoesNotContain(".ps1", command);
        }

        [Test]
        public void BuildSettingsJson_HookCommandDecodesBackToTheHookScript()
        {
            string json = GateHookInstaller.BuildSettingsJson(ScriptGate.DenyMessage, includeDenyFallback: false);
            JsonNode root = JsonParser.Parse(json);
            string command = root["hooks"]["PreToolUse"][0]["hooks"][0]["command"].AsString();
            string base64 = command.Substring(command.LastIndexOf(' ') + 1);
            string decoded = Encoding.Unicode.GetString(System.Convert.FromBase64String(base64));
            Assert.AreEqual(GateHookInstaller.BuildHookScript(ScriptGate.DenyMessage), decoded);
        }

        [Test]
        public void BuildSettingsJson_WithDenyFallback_EmitsOnlyPermissionsDeny_NoHook()
        {
            // Measured TWICE (implementation probe, then live E2E in the
            // real editor 2026-08-02): a permissions.deny rule
            // short-circuits the PreToolUse hook, so the model receives the
            // CLI's generic "denied by your permission settings" text
            // instead of the panel's staging redirect -- the live agent
            // then reported "the denial message offers no guidance" and
            // gave up rather than re-routing through staging. The two
            // shapes are therefore mutually exclusive: the deny fallback
            // ships INSTEAD OF the hook, only where the hook cannot run.
            string json = GateHookInstaller.BuildSettingsJson(ScriptGate.DenyMessage, includeDenyFallback: true);
            const string expected =
                "{\"permissions\":{\"deny\":[\"Edit(Assets/**/*.cs)\",\"Edit(Assets/**/*.asmdef)\"]}}";
            Assert.AreEqual(expected, json);
            StringAssert.DoesNotContain("PreToolUse", json);
            StringAssert.DoesNotContain("EncodedCommand", json);
        }

        [Test]
        public void BuildSettingsJson_DenyFallbackJson_ParsesToTheTwoNarrowGlobs()
        {
            string json = GateHookInstaller.BuildSettingsJson(ScriptGate.DenyMessage, includeDenyFallback: true);
            JsonNode root = JsonParser.Parse(json);
            Assert.AreEqual(2, root["permissions"]["deny"].Count);
            Assert.AreEqual("Edit(Assets/**/*.cs)", root["permissions"]["deny"][0].AsString());
            Assert.AreEqual("Edit(Assets/**/*.asmdef)", root["permissions"]["deny"][1].AsString());
        }

        // -- IsHookRunnable / ResolvePowerShellPath ------------------------------
        // Decides WHICH of the two mutually exclusive shapes above ships.

        [Test]
        public void ResolvePowerShellPath_NoSystemRootAndNoPath_ReturnsNull()
        {
            Assert.IsNull(GateHookInstaller.ResolvePowerShellPath(null, null));
            Assert.IsNull(GateHookInstaller.ResolvePowerShellPath(string.Empty, string.Empty));
        }

        [Test]
        public void ResolvePowerShellPath_NonExistentDirectories_ReturnsNull()
        {
            string bogus = Path.Combine(Path.GetTempPath(), "no-such-dir-" + System.Guid.NewGuid().ToString("N"));
            Assert.IsNull(GateHookInstaller.ResolvePowerShellPath(bogus, bogus));
        }

        [Test]
        public void ResolvePowerShellPath_FindsAnExecutableOnPath()
        {
            // A fake PATH entry holding a file named exactly like the
            // Windows PowerShell executable, so the scan is exercised
            // without depending on the host actually having PowerShell.
            string dir = Path.Combine(Path.GetTempPath(), "psprobe_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string exe = Path.Combine(dir, "powershell.exe");
                File.WriteAllText(exe, string.Empty);
                Assert.AreEqual(exe, GateHookInstaller.ResolvePowerShellPath(null, dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void ResolvePowerShellPath_SkipsMalformedPathEntriesWithoutThrowing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "psprobe_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "pwsh.exe"), string.Empty);
                // A quoted entry and an entry with an illegal character
                // both precede the good one -- neither may abort the scan.
                string pathVariable = "\"C:\\Program Files\\Nope\"" + Path.PathSeparator
                    + "C:\\bad|entry" + Path.PathSeparator + dir;
                Assert.AreEqual(Path.Combine(dir, "pwsh.exe"),
                    GateHookInstaller.ResolvePowerShellPath(null, pathVariable));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void BuildSettingsJson_NullDenyMessage_TreatedAsEmpty_StillValidEncodedCommand()
        {
            // A null deny message must not throw; BuildHookScript treats it
            // as empty, and the command is still a valid -EncodedCommand.
            string json = GateHookInstaller.BuildSettingsJson(null, includeDenyFallback: false);
            JsonNode root = JsonParser.Parse(json);
            string command = root["hooks"]["PreToolUse"][0]["hooks"][0]["command"].AsString();
            StringAssert.Contains("-EncodedCommand", command);
            string base64 = command.Substring(command.LastIndexOf(' ') + 1);
            string decoded = Encoding.Unicode.GetString(System.Convert.FromBase64String(base64));
            Assert.AreEqual(GateHookInstaller.BuildHookScript(null), decoded);
        }

        // -- EnsureInstalled (real disk, temp directory) -------------------------

        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "GateHookInstallerTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }

        [Test]
        public void EnsureInstalled_WritesOnlySettingsFile_NoHookPs1()
        {
            string settingsPath = GateHookInstaller.EnsureInstalled(_tempRoot);

            Assert.IsNotNull(settingsPath);
            string expectedDir = Path.Combine(_tempRoot, "UserSettings", "AgentPanel");
            Assert.AreEqual(Path.Combine(expectedDir, GateHookInstaller.SettingsFileName), settingsPath);
            Assert.IsTrue(File.Exists(settingsPath));
            // SEC-1: no executable .ps1 is written under the agent-writable tree.
            Assert.IsFalse(File.Exists(Path.Combine(expectedDir, GateHookInstaller.HookFileName)),
                "the hook is inlined via -EncodedCommand; no .ps1 must be written.");
        }

        [Test]
        public void EnsureInstalled_RemovesStaleHookPs1FromAnOlderVersion()
        {
            // Simulate a prior install that wrote the .ps1: EnsureInstalled
            // must delete it so no editable script lingers (SEC-1).
            string dir = Path.Combine(_tempRoot, "UserSettings", "AgentPanel");
            Directory.CreateDirectory(dir);
            string hookPath = Path.Combine(dir, GateHookInstaller.HookFileName);
            File.WriteAllText(hookPath, "# stale attacker-writable hook");

            GateHookInstaller.EnsureInstalled(_tempRoot);

            Assert.IsFalse(File.Exists(hookPath), "a stale hook .ps1 must be removed on install.");
        }

        [Test]
        public void EnsureInstalled_SettingsJsonInlinesTheHook_NoPathReference()
        {
            string settingsPath = GateHookInstaller.EnsureInstalled(_tempRoot);

            JsonNode root = JsonParser.Parse(File.ReadAllText(settingsPath));
            // May ship the deny-fallback shape on a machine with no
            // PowerShell; when it ships the hook, it must be inlined.
            if (root.HasKey("hooks"))
            {
                string command = root["hooks"]["PreToolUse"][0]["hooks"][0]["command"].AsString();
                StringAssert.Contains("-EncodedCommand", command);
                StringAssert.DoesNotContain(".ps1", command);
            }
        }

        [Test]
        public void EnsureInstalled_CalledTwiceWithNoChange_DoesNotRewriteSettingsFile()
        {
            GateHookInstaller.EnsureInstalled(_tempRoot);
            string settingsPath = Path.Combine(_tempRoot, "UserSettings", "AgentPanel", GateHookInstaller.SettingsFileName);
            System.DateTime settingsWriteTime1 = File.GetLastWriteTimeUtc(settingsPath);

            System.Threading.Thread.Sleep(50);
            GateHookInstaller.EnsureInstalled(_tempRoot);

            Assert.AreEqual(settingsWriteTime1, File.GetLastWriteTimeUtc(settingsPath),
                "identical content must not be rewritten (mtime must be untouched).");
        }

        [Test]
        public void EnsureInstalled_NullProjectRoot_ReturnsNull()
        {
            Assert.IsNull(GateHookInstaller.EnsureInstalled(null));
        }

        [Test]
        public void EnsureInstalled_EmptyProjectRoot_ReturnsNull()
        {
            Assert.IsNull(GateHookInstaller.EnsureInstalled(string.Empty));
        }

        // -- Non-ASCII projectRoot ------------------------------------------------
        // The hook path is no longer embedded in the JSON (it is inlined as a
        // base64 -EncodedCommand, pure ASCII), so a CJK project folder can no
        // longer corrupt the command. This still guards that a non-ASCII root
        // installs cleanly and the JSON round-trips.

        [Test]
        public void EnsureInstalled_NonAsciiProjectRoot_InstallsCleanly_ValidJson()
        {
            // U+30B3 U+30ED U+30A4 U+30C9 ("Koroido" in katakana), as escapes so this ASCII-only source file has no literal CJK.
            string nonAsciiRoot = Path.Combine(_tempRoot, "\u30b3\u30ed\u30a4\u30c9");
            Directory.CreateDirectory(nonAsciiRoot);

            string settingsPath = GateHookInstaller.EnsureInstalled(nonAsciiRoot);

            Assert.IsNotNull(settingsPath, "a non-ASCII project root must not fail installation.");
            string rawJson = File.ReadAllText(settingsPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            JsonNode root = JsonParser.Parse(rawJson);
            Assert.IsTrue(root.IsObject, "the settings file must be valid JSON.");
            Assert.IsFalse(File.Exists(Path.Combine(nonAsciiRoot, "UserSettings", "AgentPanel", GateHookInstaller.HookFileName)),
                "no .ps1 must be written even for a non-ASCII project root.");
        }

        [Test]
        public void EnsureInstalled_NonAsciiProjectRoot_CalledTwiceWithNoChange_DoesNotRewriteSettingsFile()
        {
            string nonAsciiRoot = Path.Combine(_tempRoot, "\u30b3\u30ed\u30a4\u30c9"); // "Koroido" (katakana) -- Unicode escapes to keep source ASCII-only.
            Directory.CreateDirectory(nonAsciiRoot);
            string settingsPath = GateHookInstaller.EnsureInstalled(nonAsciiRoot);
            System.DateTime settingsWriteTime1 = File.GetLastWriteTimeUtc(settingsPath);

            System.Threading.Thread.Sleep(50);
            GateHookInstaller.EnsureInstalled(nonAsciiRoot);

            Assert.AreEqual(settingsWriteTime1, File.GetLastWriteTimeUtc(settingsPath),
                "reading the non-ASCII settings file back with a mismatched encoding would corrupt the " +
                "comparison and make every call look like a change, defeating WriteIfChanged's mtime guard.");
        }
    }
}
