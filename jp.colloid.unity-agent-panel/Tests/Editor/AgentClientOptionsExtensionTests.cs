using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Process;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards the Settings-driven AgentClientOptions additions
    /// (AllowedTools/DisallowedTools/DangerouslySkipPermissions,
    /// ARCHITECTURE.md D3): every case with the new fields left at their
    /// default (null/empty/false) must produce the EXACT SAME argument
    /// string as before these fields existed -- see
    /// AgentClientStateTests.BuildArguments_AppendsResumeModelAndPermissionMode
    /// for the base string this suite extends.
    /// </summary>
    public class AgentClientOptionsExtensionTests
    {
        private const string ExpectedBaseArgs =
            "-p --input-format stream-json --output-format stream-json --verbose"
            + " --include-partial-messages --replay-user-messages"
            + " --permission-prompt-tool stdio";

        [Test]
        public void DefaultOptions_OmitEveryNewFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y"
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void EmptyToolLists_OmitTheFlagsEntirely()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AllowedTools = new List<string>(),
                DisallowedTools = new List<string>()
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void DangerouslySkipPermissions_AppendsTheSingleFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                DangerouslySkipPermissions = true
            });
            Assert.AreEqual(ExpectedBaseArgs + " --dangerously-skip-permissions", args);
        }

        [Test]
        public void AllowedTools_AppendedAfterTheFlag_InOrder()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AllowedTools = new List<string> { "Read", "Edit" }
            });
            Assert.AreEqual(ExpectedBaseArgs + " --allowedTools Read Edit", args);
        }

        [Test]
        public void DisallowedTools_AppendedAfterTheFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                DisallowedTools = new List<string> { "Bash" }
            });
            Assert.AreEqual(ExpectedBaseArgs + " --disallowedTools Bash", args);
        }

        [Test]
        public void ToolNameWithoutSpaces_IsNotQuoted()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AllowedTools = new List<string> { "Bash(git:*)" }
            });
            Assert.AreEqual(ExpectedBaseArgs + " --allowedTools Bash(git:*)", args);
        }

        [Test]
        public void ToolNameContainingSpace_IsQuoted()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AllowedTools = new List<string> { "has space" }
            });
            Assert.AreEqual(ExpectedBaseArgs + " --allowedTools \"has space\"", args);
        }

        [Test]
        public void AllFlags_TogetherPreserveDocumentedOrder()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                ResumeSessionId = "abc-123",
                Model = "sonnet",
                PermissionMode = "plan",
                DangerouslySkipPermissions = true,
                AllowedTools = new List<string> { "Read" },
                DisallowedTools = new List<string> { "Bash" }
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --resume abc-123 --model sonnet --permission-mode plan"
                + " --dangerously-skip-permissions"
                + " --allowedTools Read --disallowedTools Bash", args);
        }

        // -- AppendSystemPrompt (docs/design-notes/2026-08-01-settings-
        // enrichment.md #1: Settings UI "Custom instructions") --------------------------

        [Test]
        public void AppendSystemPromptUnset_OmitsTheFlag_ArgsByteIdentical()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y"
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void AppendSystemPromptEmptyString_OmitsTheFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = string.Empty
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void AppendSystemPromptSimpleText_AppendsUnquoted()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = "Always-be-concise"
            });
            Assert.AreEqual(ExpectedBaseArgs + " --append-system-prompt Always-be-concise", args);
        }

        [Test]
        public void AppendSystemPromptWithSpaces_IsQuoted()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = "Always reply in Japanese."
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --append-system-prompt \"Always reply in Japanese.\"", args);
        }

        [Test]
        public void AppendSystemPromptWithEmbeddedQuotes_IsQuotedAndEscaped()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = "Say \"hello\" first"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --append-system-prompt \"Say \\\"hello\\\" first\"", args);
        }

        [Test]
        public void AppendSystemPromptWithNewlines_IsQuoted()
        {
            // A multiline Custom instructions field must not be
            // indistinguishable from an argument boundary once quoted.
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = "Line one\nLine two\r\nLine three"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --append-system-prompt \"Line one\nLine two\r\nLine three\"", args);
        }

        [Test]
        public void AppendSystemPromptWithCjkText_AppendsUnquoted_NoSpaceOrQuoteToEscape()
        {
            // Japanese "always respond in Japanese" -- no space/quote/
            // newline present, so QuoteArg must leave it bare, exactly
            // like any other single-token argument.
            string cjk = "常に日本語で回答して";
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = cjk
            });
            Assert.AreEqual(ExpectedBaseArgs + " --append-system-prompt " + cjk, args);
        }

        [Test]
        public void AppendSystemPromptWithTabNoAsciiSpace_IsQuoted()
        {
            // Regression (CORE-1): CommandLineToArgvW treats TAB as an
            // argument delimiter just like SPACE. A CJK custom-instruction
            // line can legitimately contain a tab with NO ASCII space
            // ("重要:<TAB>Unityのみ使用") -- the previous
            // space/quote/newline-only guard left it UNQUOTED, so the CLI
            // saw two arguments and the tail was reinterpreted as a stray
            // positional (or a spawn error). It must now be quoted. The
            // value deliberately has no space, so only the tab can trigger
            // quoting here.
            string tabbed = "重要:\tUnityのみ使用";
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = tabbed
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --append-system-prompt \"重要:\tUnityのみ使用\"", args);
        }

        [Test]
        public void AppendSystemPromptEndingInBackslash_DoublesTheTrailingBackslash()
        {
            // Regression: a naive QuoteArg that only did
            // value.Replace("\"", "\\\"") left a trailing backslash run
            // un-doubled, so the appended closing quote got escaped away
            // (Win32 CommandLineToArgvW rule: an odd backslash run before
            // a quote escapes it into a literal quote instead of a
            // terminator), corrupting every argument after it.
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = "Save build output to C:\\Temp\\"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --append-system-prompt \"Save build output to C:\\Temp\\\\\"", args);
        }

        [Test]
        public void AppendSystemPromptWithBackslashesBeforeEmbeddedQuote_EscapesBoth()
        {
            // Two backslashes immediately before an embedded quote must
            // become five backslashes-then-quote (2N+1 rule: 2*2+1), not
            // the single un-doubled backslash a naive replace would
            // produce. Built with new string(...) rather than hand-typed
            // escape literals so the backslash counts cannot be
            // miscounted by eye.
            string input = "path C:" + new string('\\', 2) + "\"quoted\"" + new string('\\', 2) + " end";
            string expectedQuoted = "\"" + "path C:" + new string('\\', 5) + "\"" + "quoted"
                + new string('\\', 1) + "\"" + new string('\\', 2) + " end" + "\"";
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AppendSystemPrompt = input
            });
            Assert.AreEqual(ExpectedBaseArgs + " --append-system-prompt " + expectedQuoted, args);
        }

        [Test]
        public void AppendSystemPrompt_PlacedAfterPermissionMode_BeforeDangerousFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                PermissionMode = "plan",
                AppendSystemPrompt = "custom",
                DangerouslySkipPermissions = true
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --permission-mode plan --append-system-prompt custom"
                + " --dangerously-skip-permissions", args);
        }

        // -- ThinkingDisplaySummarized (docs/design-notes/2026-08-01-thinking-
        // content-loss.md section 4c/7: Settings "Show thinking blocks" toggle
        // wired to `--thinking-display summarized`) --------------------------

        [Test]
        public void ThinkingDisplaySummarizedUnset_OmitsTheFlag_ArgsByteIdentical()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y"
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void ThinkingDisplaySummarizedFalse_OmitsTheFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                ThinkingDisplaySummarized = false
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void ThinkingDisplaySummarizedTrue_AppendsTheFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                ThinkingDisplaySummarized = true
            });
            Assert.AreEqual(ExpectedBaseArgs + " --thinking-display summarized", args);
        }

        [Test]
        public void ThinkingDisplaySummarized_PlacedAfterAppendSystemPrompt_BeforeDangerousFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                PermissionMode = "plan",
                AppendSystemPrompt = "custom",
                ThinkingDisplaySummarized = true,
                DangerouslySkipPermissions = true
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --permission-mode plan --append-system-prompt custom"
                + " --thinking-display summarized"
                + " --dangerously-skip-permissions", args);
        }

        // -- SubagentModel plumbing (docs/design-notes/2026-08-01-model-
        // settings-rework.md section 4.2): NOT a BuildArguments flag --
        // AgentClientOptions.SubagentModel is plumbed straight through to
        // ICliTransport.Start's own subagentModel parameter instead (env
        // var, not a CLI argument), so these tests go through
        // AgentClient.Start + FakeCliProcess rather than BuildArguments. --

        [Test]
        public void SubagentModelUnset_TransportSeesNull()
        {
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions { CliPath = "x", WorkingDirectory = "y" });
                Assert.IsNull(fake.StartedSubagentModel);
            }
        }

        [Test]
        public void SubagentModelSet_TransportReceivesTheExactValue()
        {
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = "x",
                    WorkingDirectory = "y",
                    SubagentModel = "haiku"
                });
                Assert.AreEqual("haiku", fake.StartedSubagentModel);
            }
        }

        [Test]
        public void SubagentModelSet_NeverAppearsInTheArgumentString()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                SubagentModel = "haiku"
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        // -- ClaudeAuth plumbing (v0.40.0, docs/design-notes/2026-09-10-
        // claude-api-key-auth-passthrough.md): NOT a BuildArguments flag
        // either -- AgentClientOptions.ClaudeAuth is plumbed straight
        // through to ICliTransport.Start's own claudeAuth parameter (a set
        // of environment variable NAMES to remove, not a CLI argument),
        // exactly like SubagentModel above. -------------------------------

        [Test]
        public void ClaudeAuthUnset_TransportSeesTheDefaultAutoValue()
        {
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions { CliPath = "x", WorkingDirectory = "y" });
                Assert.AreEqual(ClaudeAuthMode.Auto, fake.StartedClaudeAuth);
            }
        }

        [Test]
        public void ClaudeAuthSubscriptionOnly_TransportReceivesTheExactValue()
        {
            var fake = new FakeCliProcess();
            using (var client = new AgentClient(fake))
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = "x",
                    WorkingDirectory = "y",
                    ClaudeAuth = ClaudeAuthMode.SubscriptionOnly
                });
                Assert.AreEqual(ClaudeAuthMode.SubscriptionOnly, fake.StartedClaudeAuth);
            }
        }

        [Test]
        public void ClaudeAuthSet_NeverAppearsInTheArgumentString()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                ClaudeAuth = ClaudeAuthMode.SubscriptionOnly
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        // -- McpConfigJson (Phase 5a, docs/design-notes/2026-08-01-phase5-
        // unity-ops-design.md section 1.1/8.1/8.6): `--mcp-config`/
        // `--strict-mcp-config` for the UapOps server. --------------------

        [Test]
        public void McpConfigJsonUnset_OmitsBothFlags_ArgsByteIdentical()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y"
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void McpConfigJsonEmptyString_OmitsBothFlags()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                McpConfigJson = string.Empty
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void McpConfigJsonSet_AppendsMcpConfigThenStrictMcpConfig()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                McpConfigJson = "{\"mcpServers\":{}}"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --mcp-config \"{\\\"mcpServers\\\":{}}\" --strict-mcp-config", args);
        }

        [Test]
        public void McpConfigJson_PlacedAfterToolLists()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                AllowedTools = new List<string> { "Read" },
                McpConfigJson = "cfg"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --allowedTools Read --mcp-config cfg --strict-mcp-config", args);
        }

        // -- SettingsFilePath (design section 8.7: the script validation
        // gate's PreToolUse hook -- GateHookInstaller). ---------------------

        [Test]
        public void SettingsFilePathUnset_OmitsTheFlag_ArgsByteIdentical()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y"
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void SettingsFilePathEmptyString_OmitsTheFlag()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                SettingsFilePath = string.Empty
            });
            Assert.AreEqual(ExpectedBaseArgs, args);
        }

        [Test]
        public void SettingsFilePathSet_AppendsTheFlag_NoSpacesLeftUnquoted()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                SettingsFilePath = "C:\\Proj\\UserSettings\\AgentPanel\\uap-gate-settings.json"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --settings C:\\Proj\\UserSettings\\AgentPanel\\uap-gate-settings.json", args);
        }

        [Test]
        public void SettingsFilePathWithSpaces_IsQuoted()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                SettingsFilePath = "C:\\My Project\\UserSettings\\AgentPanel\\uap-gate-settings.json"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --settings \"C:\\My Project\\UserSettings\\AgentPanel\\uap-gate-settings.json\"", args);
        }

        [Test]
        public void SettingsFilePath_PlacedAfterMcpConfig_LastInArgumentOrder()
        {
            string args = AgentClient.BuildArguments(new AgentClientOptions
            {
                CliPath = "x",
                WorkingDirectory = "y",
                McpConfigJson = "cfg",
                SettingsFilePath = "settings.json"
            });
            Assert.AreEqual(ExpectedBaseArgs
                + " --mcp-config cfg --strict-mcp-config --settings settings.json", args);
        }

        // NOTE: AgentClientOptions.AgentModelOverrides / the `--agents`
        // argument this suite used to cover here was REMOVED (docs/
        // research/07-model-configuration.md section 10: --agents is
        // silently ignored by the CLI whenever --resume is also passed,
        // which the panel always does once a session exists). The
        // replacement mechanism -- Colloid.AgentPanel.Model.
        // AgentDefinitionFileWriter, which materializes the same
        // Settings-table entries as `.claude/agents/<name>.md` files
        // instead of a spawn argument -- is unit-tested directly in
        // AgentDefinitionFileWriterTests.cs, not here.
    }
}
