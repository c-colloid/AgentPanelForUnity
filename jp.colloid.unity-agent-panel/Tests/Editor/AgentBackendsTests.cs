using System.Collections.Generic;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Backend selection plumbing (design note
    /// docs/design-notes/2026-09-10-acp-backends.md sections 2 and 4):
    /// launch defaults, the command probe, settings-change detection and
    /// the first-run card decision.
    /// </summary>
    [TestFixture]
    public class AgentBackendsTests
    {
        [Test]
        public void ClaudeCode_IsTheOnlyNonAcpBackend()
        {
            Assert.IsFalse(AgentBackends.IsAcp(AgentBackend.ClaudeCode));
            Assert.IsTrue(AgentBackends.IsAcp(AgentBackend.GeminiCli));
            Assert.IsTrue(AgentBackends.IsAcp(AgentBackend.CodexAcp));
            Assert.IsTrue(AgentBackends.IsAcp(AgentBackend.AcpCustom));
        }

        [Test]
        public void EnumValues_AreStable()
        {
            // Persisted in PanelSettings.agentBackend: never renumber.
            Assert.AreEqual(0, (int)AgentBackend.ClaudeCode);
            Assert.AreEqual(1, (int)AgentBackend.GeminiCli);
            Assert.AreEqual(2, (int)AgentBackend.CodexAcp);
            Assert.AreEqual(3, (int)AgentBackend.AcpCustom);
        }

        [Test]
        public void Gemini_DefaultsToExperimentalAcpFlag()
        {
            Assert.AreEqual("gemini", AgentBackends.EffectiveCommand(AgentBackend.GeminiCli, ""));
            Assert.AreEqual("--experimental-acp", AgentBackends.EffectiveArguments(AgentBackend.GeminiCli, "", ""));
            Assert.AreEqual("--experimental-acp", AgentBackends.EffectiveArguments(AgentBackend.GeminiCli, "  ", null));
        }

        [Test]
        public void OverriddenCommand_DropsTheDefaultArguments()
        {
            Assert.AreEqual("C:/tools/gemini.cmd",
                AgentBackends.EffectiveCommand(AgentBackend.GeminiCli, " C:/tools/gemini.cmd "));
            Assert.AreEqual(string.Empty,
                AgentBackends.EffectiveArguments(AgentBackend.GeminiCli, "C:/tools/gemini.cmd", ""));
            Assert.AreEqual("--experimental-acp --debug",
                AgentBackends.EffectiveArguments(AgentBackend.GeminiCli, "C:/tools/gemini.cmd", "--experimental-acp --debug"));
        }

        [Test]
        public void UserArguments_WinOverDefaults()
        {
            Assert.AreEqual("--acp", AgentBackends.EffectiveArguments(AgentBackend.GeminiCli, "", "--acp"));
        }

        [Test]
        public void Custom_HasNoDefaults()
        {
            Assert.AreEqual(string.Empty, AgentBackends.EffectiveCommand(AgentBackend.AcpCustom, ""));
            Assert.AreEqual(string.Empty, AgentBackends.EffectiveArguments(AgentBackend.AcpCustom, "", ""));
            Assert.AreEqual(string.Empty, AgentBackends.InstallCommand(AgentBackend.AcpCustom));
        }

        [Test]
        public void GrokBuild_RunsTheAgentSubcommand()
        {
            Assert.AreEqual(4, (int)AgentBackend.GrokBuild);
            Assert.IsTrue(AgentBackends.IsAcp(AgentBackend.GrokBuild));
            Assert.AreEqual("grok", AgentBackends.EffectiveCommand(AgentBackend.GrokBuild, ""));
            Assert.AreEqual("agent stdio", AgentBackends.EffectiveArguments(AgentBackend.GrokBuild, "", ""));
            Assert.AreEqual("grok login", AgentBackends.LoginCommand(AgentBackend.GrokBuild));
            Assert.AreEqual("Grok Build", AgentBackends.DisplayName(AgentBackend.GrokBuild));
        }

        [Test]
        public void Probe_BareName_AlsoChecksVendorInstallDirectories()
        {
            var unix = new AcpCommandProbe("grok", false, "/usr/bin", "/home/u", null);
            var candidates = new List<string>(unix.EnumerateCandidates());
            // The probe joins with Path.Combine (isWindows is a test seam; in
            // production it always matches the host), so the expectations are
            // built the same way to hold on a Windows host too.
            CollectionAssert.Contains(candidates, System.IO.Path.Combine("/home/u", ".grok", "bin", "grok"));
            CollectionAssert.Contains(candidates, System.IO.Path.Combine("/home/u", ".local", "bin", "grok"));
            Assert.AreEqual(System.IO.Path.Combine("/usr/bin", "grok"), candidates[0], "PATH entries come first");
            var windows = new AcpCommandProbe("grok", true, "", "C:\\Users\\u", "C:\\Users\\u\\AppData\\Roaming");
            var win = new List<string>(windows.EnumerateCandidates());
            CollectionAssert.Contains(win, System.IO.Path.Combine("C:\\Users\\u", ".grok", "bin", "grok.exe"));
            CollectionAssert.Contains(win, System.IO.Path.Combine("C:\\Users\\u\\AppData\\Roaming", "npm", "grok.cmd"));
        }

        [Test]
        public void Codex_DefaultCommandIsTheAdapter()
        {
            Assert.AreEqual("codex-acp", AgentBackends.DefaultCommand(AgentBackend.CodexAcp));
            Assert.AreEqual("codex login", AgentBackends.LoginCommand(AgentBackend.CodexAcp));
        }

        // -- Agent name in the UI (design note 2026-09-10-agent-name-in-ui.md) -------

        [Test]
        public void ShortName_NamesEveryPreset_AndLeavesCustomEmpty()
        {
            Assert.AreEqual("Claude", AgentBackends.ShortName(AgentBackend.ClaudeCode));
            Assert.AreEqual("Gemini", AgentBackends.ShortName(AgentBackend.GeminiCli));
            Assert.AreEqual("Codex", AgentBackends.ShortName(AgentBackend.CodexAcp));
            Assert.AreEqual("Grok", AgentBackends.ShortName(AgentBackend.GrokBuild));
            Assert.AreEqual(string.Empty, AgentBackends.ShortName(AgentBackend.AcpCustom));
        }

        [Test]
        public void AgentGlyph_IsDistinctPerPreset_AndEveryCodepointIsWhitelisted()
        {
            var backends = new[]
            {
                AgentBackend.ClaudeCode, AgentBackend.GeminiCli, AgentBackend.CodexAcp,
                AgentBackend.GrokBuild, AgentBackend.AcpCustom
            };
            var seen = new HashSet<string>();
            foreach (AgentBackend backend in backends)
            {
                string glyph = IconLoader.AgentGlyph(backend);
                Assert.IsTrue(seen.Add(glyph), backend + " shares its mark with another backend");
                Assert.AreEqual(1, glyph.Length);
                Assert.IsTrue(IconLoader.IsSafeGlyphCodepoint(glyph[0]), backend + " mark is not whitelisted");
                StringAssert.StartsWith("uap-agent--", IconLoader.AgentAccentClass(backend));
                CollectionAssert.Contains(IconLoader.AgentAccentClasses, IconLoader.AgentAccentClass(backend));
            }
            Assert.AreEqual(IconLoader.GlyphSpark, IconLoader.AgentGlyph(AgentBackend.ClaudeCode));
        }

        [Test]
        public void ApplyAgentAccent_PutsExactlyOneAgentClassOnTheRoot_AndSwapsIt()
        {
            var root = new UnityEngine.UIElements.VisualElement();
            root.AddToClassList("uap-agent--gemini");
            root.AddToClassList("uap-agent--codex");
            IconLoader.ApplyAgentAccent(root);
            int count = 0;
            foreach (string cls in IconLoader.AgentAccentClasses)
            {
                if (root.ClassListContains(cls))
                {
                    count++;
                }
            }
            Assert.AreEqual(1, count);
            Assert.IsTrue(root.ClassListContains(IconLoader.AgentAccentClass(AgentHub.CurrentBackend)));
            IconLoader.ApplyAgentAccent(null);
        }

        [Test]
        public void ResolveAgentName_PresetWins_ThenSelfReported_ThenGeneric()
        {
            Assert.AreEqual("Gemini", L10n.ResolveAgentName(AgentBackend.GeminiCli, "qwen-code", "Agent"));
            Assert.AreEqual("qwen-code", L10n.ResolveAgentName(AgentBackend.AcpCustom, "qwen-code", "Agent"));
            Assert.AreEqual("Agent", L10n.ResolveAgentName(AgentBackend.AcpCustom, null, "Agent"));
            Assert.AreEqual("Agent", L10n.ResolveAgentName(AgentBackend.AcpCustom, "", "Agent"));
        }

        [Test]
        public void ExtractAgentProductName_TakesTheNameBeforeTheVersion_NotABareVersion()
        {
            Assert.AreEqual("qwen-code", AgentHub.ExtractAgentProductName("qwen-code 1.2.0"));
            Assert.AreEqual("opencode", AgentHub.ExtractAgentProductName("opencode"));
            Assert.IsNull(AgentHub.ExtractAgentProductName("2.1.0"));
            Assert.IsNull(AgentHub.ExtractAgentProductName(""));
            Assert.IsNull(AgentHub.ExtractAgentProductName(null));
        }

        [Test]
        public void AgentPlaceholder_ExpandsInDirectStringsAndInFormat_ForTheOverriddenName()
        {
            string previous = L10n.AgentNameOverride;
            try
            {
                L10n.AgentNameOverride = "Grok";
                Assert.AreEqual("Grok", L10n.A(L10n.S.ChatRoleAssistant));
                StringAssert.Contains("Grok", L10n.A(L10n.S.EmptySubtitle));
                StringAssert.DoesNotContain("Claude", L10n.A(L10n.S.ComposerPlaceholderEnter));
                StringAssert.DoesNotContain("{agent}", L10n.A(L10n.S.PermKeyboardHint));
                string title = L10n.F(L10n.S.PermTitleFmt, "Bash");
                StringAssert.Contains("Grok", title);
                StringAssert.Contains("Bash", title);
                StringAssert.DoesNotContain("{agent}", L10n.F(L10n.S.PermTitleWithDescriptionFmt, "Bash", "ls"));
                Assert.AreEqual("plain", L10n.A("plain"));
                Assert.IsNull(L10n.A(null));
            }
            finally
            {
                L10n.AgentNameOverride = previous;
            }
        }

        // -- Backend switch: session ownership + crash counter (design note 2026-09-10-backend-switch-session.md) --

        [Test]
        public void ResumeAllowedForBackend_OnlyTheIssuingBackend_UnknownPasses()
        {
            Assert.IsTrue(AgentHub.ResumeAllowedForBackend((int)AgentBackend.ClaudeCode, AgentBackend.ClaudeCode));
            Assert.IsFalse(AgentHub.ResumeAllowedForBackend((int)AgentBackend.ClaudeCode, AgentBackend.GeminiCli));
            Assert.IsFalse(AgentHub.ResumeAllowedForBackend((int)AgentBackend.CodexAcp, AgentBackend.ClaudeCode));
            Assert.IsTrue(AgentHub.ResumeAllowedForBackend((int)AgentBackend.GrokBuild, AgentBackend.GrokBuild));
            Assert.IsTrue(AgentHub.ResumeAllowedForBackend(-1, AgentBackend.CodexAcp), "pre-field caches resume as before");
        }

        [Test]
        public void ShouldResetCrashCounterOnReady_OnlyAfterTheGrace_OrWithNoDeath()
        {
            long now = 1000L * System.TimeSpan.TicksPerSecond;
            Assert.IsTrue(AgentHub.ShouldResetCrashCounterOnReady(0, now));
            Assert.IsFalse(AgentHub.ShouldResetCrashCounterOnReady(now - 5 * System.TimeSpan.TicksPerSecond, now),
                "Ready seconds after a death keeps counting");
            Assert.IsTrue(AgentHub.ShouldResetCrashCounterOnReady(now - AgentHub.CrashCounterResetGraceTicks, now));
        }

        [Test]
        public void SessionCache_RoundTripsTheOwningBackend_AndDefaultsToUnknown()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "uap-backend-cache-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var cache = new SessionCacheFile(System.IO.Path.Combine(dir, "SessionCache.json"));
                var session = new ChatSession { sessionId = "s1", agentBackend = (int)AgentBackend.GeminiCli };
                cache.Save(session);
                ChatSession back = cache.Load();
                Assert.IsNotNull(back);
                Assert.AreEqual((int)AgentBackend.GeminiCli, back.agentBackend);
                Assert.AreEqual(-1, new ChatSession().agentBackend);
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        // -- AcpCommandProbe ---------------------------------------------------------

        [Test]
        public void Probe_BareName_ScansPathWithPlatformExtensions()
        {
            var windows = new AcpCommandProbe("gemini", true, "C:\\a;C:\\b");
            var candidates = new List<string>(windows.EnumerateCandidates());
            CollectionAssert.Contains(candidates, System.IO.Path.Combine("C:\\a", "gemini.exe"));
            CollectionAssert.Contains(candidates, System.IO.Path.Combine("C:\\a", "gemini.cmd"));
            CollectionAssert.Contains(candidates, System.IO.Path.Combine("C:\\b", "gemini.cmd"));

            // PATH entries come first, in PATH order; the vendor install
            // directories (Probe_BareName_AlsoChecksVendorInstallDirectories)
            // follow them.
            var unix = new AcpCommandProbe("gemini", false, "/usr/bin:/home/u/bin");
            var unixCandidates = new List<string>(unix.EnumerateCandidates());
            // Expectations use Path.Combine like the probe does, so they hold
            // on a Windows host as well.
            Assert.AreEqual(System.IO.Path.Combine("/usr/bin", "gemini"), unixCandidates[0]);
            Assert.AreEqual(System.IO.Path.Combine("/home/u/bin", "gemini"), unixCandidates[1]);
            Assert.AreEqual(System.IO.Path.Combine("/usr/local/bin", "gemini"), unixCandidates[2]);
        }

        [Test]
        public void Probe_PathLikeCommand_IsNotLookedUpOnPath()
        {
            var probe = new AcpCommandProbe("/opt/x/codex-acp", false, "/usr/bin");
            CollectionAssert.AreEqual(new[] { "/opt/x/codex-acp" }, new List<string>(probe.EnumerateCandidates()));
            Assert.IsTrue(AcpCommandProbe.IsPathLike("C:\\x"));
            Assert.IsTrue(AcpCommandProbe.IsPathLike("./x"));
            Assert.IsFalse(AcpCommandProbe.IsPathLike("x"));
        }

        [Test]
        public void Probe_EmptyCommand_ResolvesNothing()
        {
            var probe = new AcpCommandProbe("", false, "/usr/bin");
            Assert.IsNull(probe.Resolve());
            Assert.AreEqual(0, probe.DescribeCandidates().Length);
        }

        // -- SettingsChangeDetector / hub seams --------------------------------------

        [Test]
        public void BackendFields_RequireReconnect()
        {
            var a = new PanelSettings();
            var b = new PanelSettings { agentBackend = AgentBackend.GeminiCli };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
            var c = new PanelSettings { acpCommand = "gemini" };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, c));
            var d = new PanelSettings { acpArguments = "--x" };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, d));
            var e = new PanelSettings { acpAuthMethod = "oauth-personal" };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, e));
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, new PanelSettings()));
        }

        [Test]
        public void CloneNextSpawnOnlyFields_CopiesBackendFields()
        {
            var source = new PanelSettings
            {
                agentBackend = AgentBackend.CodexAcp,
                acpCommand = "codex-acp",
                acpArguments = "--v",
                acpAuthMethod = "chatgpt"
            };
            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);
            Assert.AreEqual(AgentBackend.CodexAcp, clone.agentBackend);
            Assert.AreEqual("codex-acp", clone.acpCommand);
            Assert.AreEqual("--v", clone.acpArguments);
            Assert.AreEqual("chatgpt", clone.acpAuthMethod);
        }

        [Test]
        public void ReaperProcessName_ClaudeStaysClaude_AcpUsesTheExecutableBaseName()
        {
            Assert.AreEqual("claude", AgentHub.ResolveReaperProcessName(false, "C:/x/claude.exe"));
            Assert.AreEqual("gemini", AgentHub.ResolveReaperProcessName(true, "C:/npm/gemini.cmd"));
            Assert.AreEqual("codex-acp", AgentHub.ResolveReaperProcessName(true, "/usr/local/bin/codex-acp"));
        }

        [Test]
        public void SettingsProbeKey_ChangesWithBackendAndCommand()
        {
            var claude = new PanelSettings { cliManualPath = "C:/claude.exe" };
            Assert.AreEqual("C:/claude.exe", SettingsView.ProbeKey(claude));
            var gemini = new PanelSettings { agentBackend = AgentBackend.GeminiCli };
            Assert.AreEqual("GeminiCli|gemini", SettingsView.ProbeKey(gemini));
            var custom = new PanelSettings { agentBackend = AgentBackend.AcpCustom, acpCommand = "qwen" };
            Assert.AreEqual("AcpCustom|qwen", SettingsView.ProbeKey(custom));
        }

        [Test]
        public void AcpCommandHint_NamesTheDefaultCommandOrAsksForOne()
        {
            StringAssert.Contains("gemini --experimental-acp", SettingsView.BuildAcpCommandHint(AgentBackend.GeminiCli));
            StringAssert.Contains("codex-acp", SettingsView.BuildAcpCommandHint(AgentBackend.CodexAcp));
            Assert.AreEqual(L10n.S.SettingsAcpCommandHintCustom, SettingsView.BuildAcpCommandHint(AgentBackend.AcpCustom));
        }

        [Test]
        public void FirstRunMode_AcpBackend_NeverShowsTheClaudeLoginCard()
        {
            var session = new ChatSession();
            var message = new ChatMessage { role = ChatMessage.RoleSystem };
            message.Add(ChatMessageBlock.MakeError("authentication failed, please login"));
            session.AddMessage(message);
            Assert.AreEqual(FirstRunView.Mode.NotLoggedIn,
                ChatView.ResolveFirstRunMode(null, null, session, null, true));
            // Claude's transcript heuristic and auth cache mean nothing to
            // an ACP agent: only the bridge's own sign-in verdict does.
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, session, null, false));
            Assert.AreEqual(FirstRunView.Mode.CliNotFound,
                ChatView.ResolveFirstRunMode("Gemini CLI command 'gemini' not found", null, session, null, false));
        }

        // -- In-panel sign-in for ACP backends (design note
        // docs/design-notes/2026-09-13-acp-feature-parity.md section 2) --

        [Test]
        public void FirstRunMode_AcpBackend_ShowsTheLoginCardOnlyWhenSignInIsRequired()
        {
            var session = new ChatSession();
            Assert.AreEqual(FirstRunView.Mode.NotLoggedIn,
                ChatView.ResolveFirstRunMode(null, null, session, null, false, true));
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, session, null, false, false));
            Assert.AreEqual(FirstRunView.Mode.CliNotFound,
                ChatView.ResolveFirstRunMode("codex-acp not found", null, session, null, false, true),
                "a missing CLI still wins over the sign-in card");
            Assert.AreEqual(FirstRunView.Mode.Hidden,
                ChatView.ResolveFirstRunMode(null, null, session, null, true, true),
                "the flag is ACP-only; Claude keeps its own auth-status rules");
        }

        [Test]
        public void LoginExecutable_IsTheCliTheLoginCommandRuns_EmptyWithoutOne()
        {
            Assert.AreEqual("codex", AgentBackends.LoginExecutable(AgentBackend.CodexAcp));
            Assert.AreEqual("login", AgentBackends.LoginArguments(AgentBackend.CodexAcp));
            Assert.AreEqual("grok", AgentBackends.LoginExecutable(AgentBackend.GrokBuild));
            Assert.AreEqual("login", AgentBackends.LoginArguments(AgentBackend.GrokBuild));
            // Gemini CLI signs in inside its own TUI; a custom agent's
            // command is unknown; Claude has its own flow.
            Assert.AreEqual(string.Empty, AgentBackends.LoginExecutable(AgentBackend.GeminiCli));
            Assert.AreEqual(string.Empty, AgentBackends.LoginExecutable(AgentBackend.AcpCustom));
            Assert.AreEqual(string.Empty, AgentBackends.LoginExecutable(AgentBackend.ClaudeCode));
            Assert.IsTrue(AgentBackends.HasInPanelLogin(AgentBackend.CodexAcp));
            Assert.IsTrue(AgentBackends.HasInPanelLogin(AgentBackend.GrokBuild));
            Assert.IsFalse(AgentBackends.HasInPanelLogin(AgentBackend.GeminiCli));
            Assert.IsFalse(AgentBackends.HasInPanelLogin(AgentBackend.AcpCustom));
        }

        [Test]
        public void LoginExecutableAndArguments_AgreeWithTheDisplayedLoginCommand()
        {
            foreach (AgentBackend backend in new[] { AgentBackend.CodexAcp, AgentBackend.GrokBuild })
            {
                string joined = AgentBackends.LoginExecutable(backend) + " " + AgentBackends.LoginArguments(backend);
                Assert.AreEqual(AgentBackends.LoginCommand(backend), joined.Trim());
            }
        }

        [Test]
        public void SubagentModelSteering_ForcedModelWins_ElseCompleteOverridesOnly()
        {
            Assert.AreEqual(string.Empty, AgentHub.ComposeSubagentModelSteering(null, null));
            Assert.AreEqual(string.Empty, AgentHub.ComposeSubagentModelSteering("  ", new List<AgentModelOverride>()));
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "Explore", modelAlias = "gemini-2.5-flash" },
                new AgentModelOverride { agentName = "half-typed", modelAlias = string.Empty },
                null
            };
            string perType = AgentHub.ComposeSubagentModelSteering(string.Empty, overrides);
            StringAssert.Contains("\"Explore\"", perType);
            StringAssert.Contains("\"gemini-2.5-flash\"", perType);
            StringAssert.DoesNotContain("half-typed", perType);
            string forced = AgentHub.ComposeSubagentModelSteering("gemini-2.5-flash-lite", overrides);
            StringAssert.Contains("\"gemini-2.5-flash-lite\"", forced);
            StringAssert.DoesNotContain("Explore", forced, "the blanket clamp supersedes the per-type rows");
        }

        [Test]
        public void AcpSystemPrompt_RewordsTheCostPolicyLine_AndAppendsTheSubagentSteering()
        {
            string claudeWorded = AgentHub.ComposeAppendSystemPrompt("Reply in Japanese.", SubagentCostPolicy.HaikuForSimpleTasks);
            StringAssert.Contains("haiku", claudeWorded);
            string acp = AgentHub.ComposeAcpSystemPrompt(claudeWorded, "flash", null);
            StringAssert.StartsWith("Reply in Japanese.", acp);
            StringAssert.DoesNotContain("haiku", acp);
            StringAssert.DoesNotContain("Agent (Task) tool", acp);
            StringAssert.Contains(AgentHub.AcpCheapModelForSimpleTasksInstructionLine, acp);
            StringAssert.EndsWith(AgentHub.ComposeSubagentModelSteering("flash", null), acp);
            Assert.AreEqual("Reply in Japanese.",
                AgentHub.ComposeAcpSystemPrompt("Reply in Japanese.", string.Empty, null),
                "nothing to add leaves the block untouched");
        }

        // -- API key guidance and the connected auth method (design note
        // docs/design-notes/2026-09-10-acp-auth-guidance-and-method-display.md) --

        [Test]
        public void ApiKeyEnvVars_AreTheVariablesEachCliActuallyReads()
        {
            Assert.AreEqual("GEMINI_API_KEY", AgentBackends.ApiKeyEnvVars(AgentBackend.GeminiCli));
            Assert.AreEqual("CODEX_API_KEY / OPENAI_API_KEY", AgentBackends.ApiKeyEnvVars(AgentBackend.CodexAcp));
            Assert.AreEqual("XAI_API_KEY", AgentBackends.ApiKeyEnvVars(AgentBackend.GrokBuild));
            Assert.AreEqual(string.Empty, AgentBackends.ApiKeyEnvVars(AgentBackend.ClaudeCode));
            Assert.AreEqual(string.Empty, AgentBackends.ApiKeyEnvVars(AgentBackend.AcpCustom));
        }

        [Test]
        public void ApiKeyHint_AddsTheConfigFileOnlyWhereTheCliHasOne()
        {
            // Gemini CLI reads ~/.gemini/.env as well as the environment;
            // Codex and Grok Build read the environment only.
            Assert.AreEqual("~/.gemini/.env", AgentBackends.ApiKeyConfigPath(AgentBackend.GeminiCli));
            Assert.AreEqual(string.Empty, AgentBackends.ApiKeyConfigPath(AgentBackend.CodexAcp));
            Assert.AreEqual("GEMINI_API_KEY (~/.gemini/.env)", AgentBackends.ApiKeyHint(AgentBackend.GeminiCli));
            Assert.AreEqual("XAI_API_KEY", AgentBackends.ApiKeyHint(AgentBackend.GrokBuild));
            Assert.AreEqual(string.Empty, AgentBackends.ApiKeyHint(AgentBackend.ClaudeCode));
            Assert.AreEqual(string.Empty, AgentBackends.ApiKeyHint(AgentBackend.AcpCustom));
        }

        [Test]
        public void ComposeAcpApiKeyGuidance_NamesTheVariableAndItsFile_EmptyWithoutOne()
        {
            string gemini = AgentHub.ComposeAcpApiKeyGuidance(AgentBackend.GeminiCli);
            StringAssert.Contains("GEMINI_API_KEY", gemini);
            StringAssert.Contains("~/.gemini/.env", gemini);
            StringAssert.Contains("Gemini CLI", gemini);
            StringAssert.Contains("XAI_API_KEY", AgentHub.ComposeAcpApiKeyGuidance(AgentBackend.GrokBuild));
            Assert.AreEqual(string.Empty, AgentHub.ComposeAcpApiKeyGuidance(AgentBackend.AcpCustom));
            Assert.AreEqual(string.Empty, AgentHub.ComposeAcpApiKeyGuidance(AgentBackend.ClaudeCode));
        }

        [Test]
        public void AppendSentence_JoinsWithOneSpace_AndToleratesEitherHalfMissing()
        {
            Assert.AreEqual("a b", AgentHub.AppendSentence("a", "b"));
            Assert.AreEqual("a", AgentHub.AppendSentence("a", string.Empty));
            Assert.AreEqual("a", AgentHub.AppendSentence("a", null));
            Assert.AreEqual("b", AgentHub.AppendSentence(null, "b"));
            Assert.AreEqual(string.Empty, AgentHub.AppendSentence(null, null));
        }

        [Test]
        public void AcpAuthSummary_NamesTheKeyVariable_InBothCatalogs()
        {
            var catalogs = new List<UiStrings> { new UiStrings(), UiStringsJa.Create() };
            foreach (UiStrings catalog in catalogs)
            {
                string gemini = L10n.ResolveAcpAuthSummary(AgentBackend.GeminiCli, catalog);
                StringAssert.Contains("GEMINI_API_KEY", gemini);
                StringAssert.Contains("~/.gemini/.env", gemini);
                StringAssert.Contains("CODEX_API_KEY",
                    L10n.ResolveAcpAuthSummary(AgentBackend.CodexAcp, catalog));
                StringAssert.Contains("XAI_API_KEY",
                    L10n.ResolveAcpAuthSummary(AgentBackend.GrokBuild, catalog));
                Assert.AreEqual(string.Empty, L10n.ResolveAcpAuthSummary(AgentBackend.ClaudeCode, catalog));
                Assert.AreEqual(string.Empty, L10n.ResolveAcpAuthSummary(AgentBackend.AcpCustom, catalog));
            }
            Assert.AreEqual(string.Empty, L10n.ResolveAcpAuthSummary(AgentBackend.GeminiCli, null));
        }

        [Test]
        public void AcpAuthSummary_StaysWithinTheInlineHintCap_InBothCatalogs()
        {
            // These render as the one inline line under the Agent picker, so
            // they are held to the same 110-char cap L10nTests applies to
            // Settings*Hint fields -- the long half belongs in the tooltip
            // (docs/design-notes/2026-08-04-settings-annotation-load.md #4).
            const int inlineHintCharCap = 110;
            var backends = new List<AgentBackend>
                { AgentBackend.GeminiCli, AgentBackend.CodexAcp, AgentBackend.GrokBuild };
            var catalogs = new List<UiStrings> { new UiStrings(), UiStringsJa.Create() };
            foreach (UiStrings catalog in catalogs)
            {
                foreach (AgentBackend backend in backends)
                {
                    string summary = L10n.ResolveAcpAuthSummary(backend, catalog);
                    Assert.LessOrEqual(summary.Length, inlineHintCharCap,
                        backend + " inline sign-in summary is " + summary.Length
                        + " chars -- move the explanation into its AcpAuthDetail* tooltip field.");
                }
            }
        }

        [Test]
        public void AcpAuthDetail_CarriesTheDeprecationDate_InBothCatalogs()
        {
            // Google ended "Login with Google" for individuals on
            // 2026-06-18; the tooltip is where that reason lives now.
            var catalogs = new List<UiStrings> { new UiStrings(), UiStringsJa.Create() };
            foreach (UiStrings catalog in catalogs)
            {
                StringAssert.Contains("2026-06-18",
                    L10n.ResolveAcpAuthDetail(AgentBackend.GeminiCli, catalog));
                StringAssert.Contains("CODEX_API_KEY",
                    L10n.ResolveAcpAuthDetail(AgentBackend.CodexAcp, catalog));
                StringAssert.Contains("XAI_API_KEY",
                    L10n.ResolveAcpAuthDetail(AgentBackend.GrokBuild, catalog));
                Assert.AreEqual(string.Empty, L10n.ResolveAcpAuthDetail(AgentBackend.AcpCustom, catalog));
            }
            Assert.AreEqual(string.Empty, L10n.ResolveAcpAuthDetail(AgentBackend.GeminiCli, null));
        }

        [Test]
        public void ComposeAcpAuthHint_JoinsBothHalves_AndToleratesEitherMissing()
        {
            Assert.AreEqual("summary hint", L10n.ComposeAcpAuthHint("summary", "hint"));
            Assert.AreEqual("summary", L10n.ComposeAcpAuthHint("summary", null));
            Assert.AreEqual("hint", L10n.ComposeAcpAuthHint(string.Empty, "hint"));
            Assert.AreEqual(string.Empty, L10n.ComposeAcpAuthHint(null, null));
        }

        [Test]
        public void AcpAuthHint_IsTheShortSummary_AndTheTooltipCarriesTheRest()
        {
            string hint = L10n.AcpAuthHint(AgentBackend.GeminiCli, L10n.S.SettingsAcpLoginHintFmt);
            Assert.AreEqual(L10n.S.AcpAuthSummaryGemini, hint, "the inline line is the short summary alone");

            string tooltip = L10n.AcpAuthTooltip(AgentBackend.GeminiCli, L10n.S.SettingsAcpLoginHintFmt);
            StringAssert.Contains(L10n.S.AcpAuthDetailGemini, tooltip);
            StringAssert.Contains(L10n.S.AcpAuthKeysNotStoredNote, tooltip);
            StringAssert.Contains("gemini", tooltip, "the terminal-login fallback moved here");

            // A custom ACP agent has no known sign-in paths and no login
            // command, so both halves stay empty (line hidden, no tooltip).
            Assert.AreEqual(string.Empty, L10n.AcpAuthHint(AgentBackend.AcpCustom, L10n.S.SettingsAcpLoginHintFmt));
            Assert.AreEqual(string.Empty, L10n.AcpAuthTooltip(AgentBackend.AcpCustom, L10n.S.SettingsAcpLoginHintFmt));
        }

        [Test]
        public void FormatAcpAuthMethodLine_NamesTheMethod_ElseSaysSavedSignIn()
        {
            StringAssert.Contains("Login with Google",
                SettingsView.FormatAcpAuthMethodLine("oauth-personal", "Login with Google"));
            StringAssert.Contains("gemini-api-key",
                SettingsView.FormatAcpAuthMethodLine("gemini-api-key", null));
            // No authenticate round trip happened: the method is unknowable,
            // so the card must not name one.
            Assert.AreEqual(L10n.S.SettingsAccountAcpAuthMethodStored,
                SettingsView.FormatAcpAuthMethodLine(null, null));
            Assert.AreEqual(L10n.S.SettingsAccountAcpAuthMethodStored,
                SettingsView.FormatAcpAuthMethodLine(string.Empty, string.Empty));
        }
    }
}
