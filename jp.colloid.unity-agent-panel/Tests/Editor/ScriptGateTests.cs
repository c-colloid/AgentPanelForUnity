using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure predicate tests for the script validation gate's can_use_tool
    /// pre-filter (design section 7.4/8.2 B1). Covers nested paths,
    /// .asmdef, the staging exemption, and Windows-style case-insensitivity
    /// per the stream's DoD.
    /// </summary>
    [TestFixture]
    public class ScriptGateTests
    {
        [TestCase("Write")]
        [TestCase("Edit")]
        [TestCase("MultiEdit")]
        public void ShouldAutoDeny_GatedTool_TopLevelCsFile_ReturnsTrue(string toolName)
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny(toolName, "Assets/Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_NestedCsFile_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "Assets/A/B/C/Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_AsmdefFile_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "Assets/A/MyAsm.asmdef"));
        }

        [Test]
        public void ShouldAutoDeny_AbsoluteWindowsPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny(
                "Edit", @"C:\Unity\UnityProjects\AITemp\Assets\Foo\Bar.cs"));
        }

        // -- Staging folder (design section 8.7: moved OUTSIDE Assets/
        // entirely -- `<projectRoot>/UapStaging/`, no longer
        // `Assets/UapStaging~/`) -- a genuine staging path therefore never
        // contains an "Assets/" segment at all, so it is never gated purely
        // because it is not under Assets/. A LEFTOVER path that happens to
        // textually resemble the OLD Assets/UapStaging~/ location is, by
        // contrast, just an ordinary Assets/**/*.cs file now -- there is no
        // special-casing of that name left, and it IS gated. ---------------

        [Test]
        public void ShouldAutoDeny_StagingFolderOutsideAssets_TopLevel_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", "UapStaging/Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_StagingFolderOutsideAssets_Nested_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", "UapStaging/Sub/Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_StagingFolderOutsideAssets_AbsoluteWindowsPath_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny(
                "Write", @"C:\Unity\UnityProjects\AITemp\UapStaging\Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_OldTildeStagingPathUnderAssets_IsNoLongerExempt_ReturnsTrue()
        {
            // The OLD Assets/UapStaging~/ location is no longer special --
            // it is a plain Assets/**/*.cs path like any other now that
            // staging has moved outside Assets/ entirely (section 8.7).
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "Assets/UapStaging~/Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_CaseInsensitiveExtensionAndAssetsSegment_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "assets/Foo/bar.CS"));
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "ASSETS/Foo/Bar.AsmDef"));
        }

        [Test]
        public void ShouldAutoDeny_NonScriptFile_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", "Assets/Foo/Bar.prefab"));
        }

        [Test]
        public void ShouldAutoDeny_UngatedToolName_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Read", "Assets/Foo.cs"));
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Bash", "Assets/Foo.cs"));
            Assert.IsFalse(ScriptGate.ShouldAutoDeny(null, "Assets/Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_NullOrEmptyFilePath_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", null));
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", string.Empty));
        }

        [Test]
        public void ShouldAutoDeny_PathOutsideAssets_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", "Packages/com.foo/Bar.cs"));
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", @"C:\Users\me\Desktop\Bar.cs"));
        }

        [Test]
        public void ShouldAutoDeny_MultiEditTargetsCsFile_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("MultiEdit", "Assets/Scripts/Foo.cs"));
        }

        [Test]
        public void DenyMessage_MentionsStagingFolderAndCommitTool()
        {
            string message = ScriptGate.DenyMessage;
            StringAssert.Contains(ScriptGate.StagingFolder, message);
            StringAssert.Contains("uap_scripts_commit", message);
        }

        /// <summary>Pins the exact deny message text (design line: "message text exact-pinned in a test").</summary>
        [Test]
        public void DenyMessage_IsExactlyPinned()
        {
            const string expected =
                "Direct Assets/**/*.cs and *.asmdef writes are blocked by the script validation gate."
                + " Write this file under the 'UapStaging/' folder at the project root (outside Assets/)"
                + " instead, then call uap_scripts_commit to validate and move it into Assets/.";
            Assert.AreEqual(expected, ScriptGate.DenyMessage);
        }

        [Test]
        public void StagingFolder_IsExactlyPinned()
        {
            Assert.AreEqual("UapStaging/", ScriptGate.StagingFolder);
        }

        // -- Path traversal (a ".." that resolves back under Assets/ from a
        // path that otherwise looks like it might not be) ------------------

        [Test]
        public void ShouldAutoDeny_DotDotResolvesUnderAssets_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "Assets/Sub/../Evil.cs"),
                "the resolved path lands directly under Assets/ -- must be gated.");
        }

        [Test]
        public void ShouldAutoDeny_DotDotResolvesUnderAssets_WindowsAbsolutePath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny(
                "Edit", @"C:\Unity\UnityProjects\AITemp\Assets\Sub\..\Evil.cs"));
        }

        [Test]
        public void ShouldAutoDeny_DotDotEscapesAssetsEntirely_ReturnsFalse()
        {
            // Resolves to "Evil.cs" at the project root, outside Assets/
            // altogether -- not this gate's concern (it never claimed to
            // cover writes outside Assets/).
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", "Assets/../Evil.cs"));
        }

        [Test]
        public void ShouldAutoDeny_DotDotFromStagingFolder_NeverEntersAssets_ReturnsFalse()
        {
            // "UapStaging/Sub/../Foo.cs" resolves to "UapStaging/Foo.cs" --
            // still outside Assets/ entirely, so still ungated.
            Assert.IsFalse(ScriptGate.ShouldAutoDeny("Write", "UapStaging/Sub/../Foo.cs"));
        }

        [Test]
        public void ShouldAutoDeny_SingleDotSegment_IsIgnored()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDeny("Write", "Assets/./Foo/./Bar.cs"));
        }

        // -- Bash redirection bypass -----------------------------------------

        [Test]
        public void ShouldAutoDenyBashCommand_RedirectToGatedCsPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand("echo 'x' > Assets/Evil.cs"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_HeredocRedirectToGatedCsPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand(
                "cat > Assets/Evil.cs <<'EOF'\npublic class Evil {}\nEOF"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_AppendRedirectToGatedAsmdefPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand("echo '{}' >> Assets/A/My.asmdef"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_TeeToGatedCsPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand("echo 'x' | tee Assets/Evil.cs"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_QuotedGatedPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand("echo 'x' > \"Assets/Foo Bar/Evil.cs\""));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_RedirectIntoStagingFolder_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand("echo 'x' > UapStaging/Foo.cs"),
                "staging-folder writes are the sanctioned path, even via Bash.");
        }

        [Test]
        public void ShouldAutoDenyBashCommand_RedirectToNonScriptFile_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand("echo 'x' > Assets/notes.txt"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_StderrMergeIsNotAWrite_ReturnsFalse()
        {
            // "2>&1" is fd duplication, not a write to a path named "1".
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand("some_command 2>&1"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_UnrelatedCommand_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand("git status"));
        }

        [Test]
        public void ShouldAutoDenyBashCommand_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand(null));
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand(string.Empty));
        }

        // -- SEC-4: argument-named write targets (sed -i / cp / mv / install / dd) --

        [TestCase("sed -i 's/a/b/' Assets/Foo.cs")]
        [TestCase("sed -i.bak 's/a/b/' Assets/Foo.cs")]
        [TestCase("sed --in-place -e 's/a/b/' Assets/Foo.cs")]
        [TestCase("cp /tmp/x.cs Assets/Evil.cs")]
        [TestCase("mv payload.cs Assets/Evil.cs")]
        [TestCase("install -m 644 payload.cs Assets/Evil.cs")]
        [TestCase("dd if=/dev/stdin of=Assets/Evil.cs")]
        [TestCase("echo x >| Assets/Evil.cs")]
        [TestCase("ls | head; mv payload.cs Assets/Evil.cs")]
        [TestCase("cp a.cs b.cs && cp payload.cs Assets/A/My.asmdef")]
        public void ShouldAutoDenyBashCommand_ArgumentNamedWriteToGatedPath_ReturnsTrue(string command)
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand(command));
        }

        [TestCase("cp Assets/Foo.cs /tmp/backup.cs")]
        [TestCase("mv Assets/A.txt Assets/B.txt")]
        [TestCase("sed -n 'p' Assets/Foo.cs")]
        [TestCase("sed -i 's|x|Assets/Evil.cs|' notes.txt")]
        [TestCase("cp /tmp/x.cs UapStaging/Foo.cs")]
        [TestCase("cp payload.cs")]
        [TestCase("dd if=Assets/Foo.cs of=/tmp/out.cs")]
        public void ShouldAutoDenyBashCommand_ArgumentIdioms_FalsePositiveRegressions_ReturnFalse(string command)
        {
            // The conservative rules: a gated SOURCE (copied OUT of
            // Assets/), a non -i sed, a quoted sed script that merely
            // MENTIONS a gated path, the sanctioned staging destination,
            // and an argument-less form must all stay allowed.
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand(command));
        }

        // -- PowerShell (design note 2026-09-15-script-gate-steering-and-
        // powershell.md section 3): the CLI's shell tool on Windows is
        // named "PowerShell" and its commands use cmdlets, not `>`/`tee`.
        // Same scan, same gated-path resolution. ---------------------------

        [TestCase("Bash")]
        [TestCase("PowerShell")]
        [TestCase("Shell")]
        public void IsShellToolName_KnownShellTools_ReturnsTrue(string toolName)
        {
            Assert.IsTrue(ScriptGate.IsShellToolName(toolName));
        }

        [TestCase("Write")]
        [TestCase("bash")]
        [TestCase("")]
        [TestCase(null)]
        public void IsShellToolName_Others_ReturnsFalse(string toolName)
        {
            Assert.IsFalse(ScriptGate.IsShellToolName(toolName));
        }

        [TestCase("Set-Content -Path Assets/Editor/Foo.cs -Value \"class A {}\"")]
        [TestCase("Set-Content Assets/Foo.cs -Value x")]
        [TestCase("set-content -LiteralPath Assets/Foo.cs -Value x")]
        [TestCase("Add-Content -Path:Assets/Foo.cs -Value x")]
        [TestCase("$code | Out-File -FilePath \"H:\\Unity Projects\\Kakure House\\Assets\\Editor\\SAMeshKit.cs\" -Encoding utf8")]
        [TestCase("$code | Out-File Assets/Foo.cs")]
        [TestCase("New-Item -ItemType File -Path Assets/Sub/../Foo.cs -Force")]
        [TestCase("$x | ac Assets/Foo.cs")]
        [TestCase("Set-Content -Force Assets/Foo.cs")]
        [TestCase("Set-Content -Encoding utf8 Assets/Foo.cs")]
        [TestCase("Copy-Item UapStaging/Foo.cs -Destination Assets/Foo.cs")]
        [TestCase("Move-Item UapStaging/Foo.cs Assets/Foo.cs")]
        [TestCase("Copy-Item Assets/A.cs Assets/B.cs")]
        [TestCase("[IO.File]::WriteAllText(\"Assets/Foo.cs\", $code)")]
        [TestCase("[System.IO.File]::WriteAllLines('Assets/Foo.asmdef', $lines)")]
        public void ShouldAutoDenyBashCommand_PowerShellWriteIdioms_ReturnTrue(string command)
        {
            Assert.IsTrue(ScriptGate.ShouldAutoDenyBashCommand(command), command);
        }

        [TestCase("Get-Content Assets/Foo.cs | Select-String x")]
        [TestCase("Set-Content -Path Assets/Foo.txt -Value x")]
        [TestCase("Set-Content -Path UapStaging/Foo.cs -Value x")]
        [TestCase("Move-Item Assets/Old.cs UapStaging/Old.cs")]
        [TestCase("Copy-Item Assets/Foo.cs -Destination UapStaging/Foo.cs")]
        [TestCase("uloop execute-dynamic-code --code-file p0.cs")]
        [TestCase("Test-Path Assets/Foo.cs")]
        public void ShouldAutoDenyBashCommand_PowerShellReadsAndStagingWrites_ReturnFalse(string command)
        {
            Assert.IsFalse(ScriptGate.ShouldAutoDenyBashCommand(command), command);
        }

        [Test]
        public void TryFindGatedBashTarget_OutFileNamedParameter_ReturnsThatPath()
        {
            string gated;
            Assert.IsTrue(ScriptGate.TryFindGatedBashTarget(
                "\"x\" | Out-File -Encoding utf8 -FilePath Assets/Foo.cs -Force", out gated));
            Assert.AreEqual("Assets/Foo.cs", gated);
        }

        [Test]
        public void TryFindGatedBashTarget_SedInPlace_ReturnsTheEditedFile()
        {
            string gatedPath;
            Assert.IsTrue(ScriptGate.TryFindGatedBashTarget("sed -i 's/a/b/' Assets/Foo.cs", out gatedPath));
            Assert.AreEqual("Assets/Foo.cs", gatedPath);
        }

        [Test]
        public void TryFindGatedBashTarget_CpDestination_ReturnsTheDestination()
        {
            string gatedPath;
            Assert.IsTrue(ScriptGate.TryFindGatedBashTarget("cp /tmp/x.cs Assets/Evil.cs", out gatedPath));
            Assert.AreEqual("Assets/Evil.cs", gatedPath);
        }

        [Test]
        public void TryFindGatedBashTarget_ReturnsTheOffendingPath()
        {
            string gatedPath;
            Assert.IsTrue(ScriptGate.TryFindGatedBashTarget("echo x > Assets/Evil.cs", out gatedPath));
            Assert.AreEqual("Assets/Evil.cs", gatedPath);
        }

        [Test]
        public void TryFindGatedBashTarget_NothingGated_ReturnsFalseAndNullPath()
        {
            string gatedPath;
            Assert.IsFalse(ScriptGate.TryFindGatedBashTarget("git status", out gatedPath));
            Assert.IsNull(gatedPath);
        }

        // -- IsProtectedGateFile (SEC-1 defense in depth) ------------------------

        [Test]
        public void IsProtectedGateFile_SettingsJson_ProjectRelative_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.IsProtectedGateFile("UserSettings/AgentPanel/uap-gate-settings.json"));
        }

        [Test]
        public void IsProtectedGateFile_HookPs1_AbsoluteWindowsPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.IsProtectedGateFile(
                @"C:\Proj\UserSettings\AgentPanel\uap-gate-hook.ps1"));
        }

        [Test]
        public void IsProtectedGateFile_AbsoluteUnixPath_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.IsProtectedGateFile(
                "/home/u/Proj/UserSettings/AgentPanel/uap-gate-settings.json"));
        }

        [Test]
        public void IsProtectedGateFile_CaseInsensitive_ReturnsTrue()
        {
            Assert.IsTrue(ScriptGate.IsProtectedGateFile("usersettings/agentpanel/UAP-GATE-SETTINGS.JSON"));
        }

        [Test]
        public void IsProtectedGateFile_DecoyFolderSuffix_ReturnsFalse()
        {
            // "evilUserSettings/" ends with "usersettings" textually but is
            // not the real gate directory -- the '/' boundary check rejects it.
            Assert.IsFalse(ScriptGate.IsProtectedGateFile("evilUserSettings/AgentPanel/uap-gate-settings.json"));
        }

        [Test]
        public void IsProtectedGateFile_UnrelatedPath_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.IsProtectedGateFile("Assets/Scripts/Player.cs"));
            Assert.IsFalse(ScriptGate.IsProtectedGateFile("UserSettings/AgentPanel/SessionCache.json"));
        }

        [Test]
        public void IsProtectedGateFile_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(ScriptGate.IsProtectedGateFile(null));
            Assert.IsFalse(ScriptGate.IsProtectedGateFile(string.Empty));
        }
    }
}
