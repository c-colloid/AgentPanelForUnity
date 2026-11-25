using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// In-panel install and ACP sign-in (design note
    /// docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md): the
    /// install plans per backend/platform, failure classification, the
    /// status line, the sign-in URL scan and the bridge's sign-in events.
    /// </summary>
    [TestFixture]
    public class CliInstallPlanTests
    {
        [Test]
        public void Claude_UsesTheOfficialNativeInstaller_PerPlatform()
        {
            CliInstallPlan windows = CliInstallPlan.Build(AgentBackend.ClaudeCode, true);
            Assert.AreEqual("powershell.exe", windows.FileName);
            StringAssert.Contains("irm https://claude.ai/install.ps1 | iex", windows.Arguments);
            StringAssert.Contains("-ExecutionPolicy Bypass", windows.Arguments);
            Assert.IsFalse(windows.RequiresNpm);

            CliInstallPlan unix = CliInstallPlan.Build(AgentBackend.ClaudeCode, false);
            Assert.AreEqual("/bin/bash", unix.FileName);
            Assert.AreEqual("-lc \"curl -fsSL https://claude.ai/install.sh | bash\"", unix.Arguments);
            Assert.AreEqual("curl -fsSL https://claude.ai/install.sh | bash", unix.DisplayCommand);
        }

        [Test]
        public void AcpBackends_InstallTheirNpmPackagesGlobally()
        {
            CliInstallPlan gemini = CliInstallPlan.Build(AgentBackend.GeminiCli, true);
            Assert.AreEqual("cmd.exe", gemini.FileName);
            Assert.AreEqual("/d /s /c \"npm install -g @google/gemini-cli\"", gemini.Arguments);
            Assert.IsTrue(gemini.RequiresNpm);

            CliInstallPlan codex = CliInstallPlan.Build(AgentBackend.CodexAcp, false);
            Assert.AreEqual("/bin/bash", codex.FileName);
            Assert.AreEqual("npm install -g @openai/codex @agentclientprotocol/codex-acp", codex.DisplayCommand);
        }

        [Test]
        public void GrokBuild_UsesXaiNativeInstaller()
        {
            CliInstallPlan unix = CliInstallPlan.Build(AgentBackend.GrokBuild, false);
            Assert.AreEqual("/bin/bash", unix.FileName);
            Assert.AreEqual("curl -fsSL https://x.ai/cli/install.sh | bash", unix.DisplayCommand);
            Assert.IsFalse(unix.RequiresNpm);
            CliInstallPlan windows = CliInstallPlan.Build(AgentBackend.GrokBuild, true);
            Assert.AreEqual("powershell.exe", windows.FileName);
            StringAssert.Contains("irm https://x.ai/cli/install.ps1 | iex", windows.Arguments);
        }

        [Test]
        public void CustomAgent_HasNoPlan()
        {
            Assert.IsNull(CliInstallPlan.Build(AgentBackend.AcpCustom, true));
            Assert.AreEqual(string.Empty, CliInstallPlan.DisplayCommandFor(AgentBackend.AcpCustom, false));
        }

        [Test]
        public void Classify_DistinguishesNodeMissingFromOtherFailures()
        {
            Assert.AreEqual(CliInstallFailureKind.None,
                CliInstallPlan.Classify(true, true, false, 0, "added 1 package"));
            Assert.AreEqual(CliInstallFailureKind.NodeMissing,
                CliInstallPlan.Classify(true, true, false, 127, "/bin/bash: line 1: npm: command not found"));
            Assert.AreEqual(CliInstallFailureKind.NodeMissing,
                CliInstallPlan.Classify(true, true, false, 9009,
                    "'npm' is not recognized as an internal or external command"));
            Assert.AreEqual(CliInstallFailureKind.CommandFailed,
                CliInstallPlan.Classify(true, true, false, 1, "npm ERR! code EACCES"));
            Assert.AreEqual(CliInstallFailureKind.ShellMissing,
                CliInstallPlan.Classify(false, true, false, 127, "bash: curl: command not found"));
            Assert.AreEqual(CliInstallFailureKind.ShellMissing,
                CliInstallPlan.Classify(true, false, false, -1, null));
            Assert.AreEqual(CliInstallFailureKind.TimedOut,
                CliInstallPlan.Classify(true, true, true, -1, "still downloading"));
            // A non-npm failure that merely mentions npm somewhere is not "Node missing".
            Assert.AreEqual(CliInstallFailureKind.CommandFailed,
                CliInstallPlan.Classify(true, true, false, 1, "npm WARN deprecated; ERR network"));
        }

        [Test]
        public void InstallStatusLine_CoversEveryState()
        {
            long tick = System.TimeSpan.TicksPerSecond;
            string running = FirstRunView.DescribeInstallState(true, null, "Gemini CLI", 100 * tick, 107 * tick);
            StringAssert.Contains("Gemini CLI", running);
            StringAssert.Contains("7", running);
            Assert.IsNull(FirstRunView.DescribeInstallState(false, null, "x", 0, 0));
            var ok = new CliInstallResult { Success = true };
            StringAssert.Contains("Gemini CLI", FirstRunView.DescribeInstallState(false, ok, "Gemini CLI", 0, 0));
            var node = new CliInstallResult { Success = false, Failure = CliInstallFailureKind.NodeMissing };
            Assert.AreEqual(L10n.S.InstallNodeMissing, FirstRunView.DescribeInstallState(false, node, "x", 0, 0));
            var failed = new CliInstallResult
            {
                Success = false, Failure = CliInstallFailureKind.CommandFailed, ExitCode = 1, LastLine = "boom"
            };
            string text = FirstRunView.DescribeInstallState(false, failed, "x", 0, 0);
            StringAssert.Contains("1", text);
            StringAssert.Contains("boom", text);
        }

        // -- The finished-install line clears once it stops being news (2026-09-29) --

        [Test]
        public void InstallResult_SuccessLine_HiddenOnceConnectionSettles()
        {
            var ok = new CliInstallResult { Backend = AgentBackend.CodexAcp, Success = true };
            Assert.IsTrue(FirstRunView.ShouldShowInstallResult(ok, AgentBackend.CodexAcp, false));
            Assert.IsFalse(FirstRunView.ShouldShowInstallResult(ok, AgentBackend.CodexAcp, true));
        }

        [Test]
        public void InstallResult_HiddenForAnotherBackend()
        {
            var ok = new CliInstallResult { Backend = AgentBackend.CodexAcp, Success = true };
            Assert.IsFalse(FirstRunView.ShouldShowInstallResult(ok, AgentBackend.GrokBuild, false));
            var failed = new CliInstallResult { Backend = AgentBackend.CodexAcp, Failure = CliInstallFailureKind.NodeMissing };
            Assert.IsFalse(FirstRunView.ShouldShowInstallResult(failed, AgentBackend.GrokBuild, false));
            Assert.IsFalse(FirstRunView.ShouldShowInstallResult(null, AgentBackend.GrokBuild, false));
        }

        [Test]
        public void InstallResult_FailureLine_StaysAfterSettling()
        {
            var failed = new CliInstallResult { Backend = AgentBackend.GeminiCli, Failure = CliInstallFailureKind.TimedOut };
            Assert.IsTrue(FirstRunView.ShouldShowInstallResult(failed, AgentBackend.GeminiCli, true));
        }

        [Test]
        public void ConnectionSettled_Table()
        {
            Assert.IsFalse(FirstRunView.IsConnectionSettled(null, false, false));
            Assert.IsFalse(FirstRunView.IsConnectionSettled(Colloid.AgentPanel.Core.Client.AgentClientState.NotStarted, false, false));
            Assert.IsFalse(FirstRunView.IsConnectionSettled(Colloid.AgentPanel.Core.Client.AgentClientState.Starting, false, false));
            Assert.IsTrue(FirstRunView.IsConnectionSettled(Colloid.AgentPanel.Core.Client.AgentClientState.Ready, false, false));
            Assert.IsTrue(FirstRunView.IsConnectionSettled(Colloid.AgentPanel.Core.Client.AgentClientState.Errored, false, false));
            // "Sign-in required" is an answer too.
            Assert.IsTrue(FirstRunView.IsConnectionSettled(Colloid.AgentPanel.Core.Client.AgentClientState.Starting, true, false));
            Assert.IsTrue(FirstRunView.IsConnectionSettled(null, false, true));
        }

        [Test]
        public void SignInUrl_IsExtractedFromAStderrLine()
        {
            Assert.AreEqual("https://accounts.google.com/o/oauth2/auth?client_id=1&scope=x",
                AgentHub.ExtractFirstUrl("Open this URL: https://accounts.google.com/o/oauth2/auth?client_id=1&scope=x."));
            Assert.AreEqual("http://localhost:1455/auth", AgentHub.ExtractFirstUrl("visit \"http://localhost:1455/auth\" now"));
            Assert.IsNull(AgentHub.ExtractFirstUrl("no link here"));
            Assert.IsNull(AgentHub.ExtractFirstUrl(null));
        }

        [Test]
        public void LoginCommandHint_IsTheFirstBacktickedCommandOfTheBridgeReason()
        {
            Assert.AreEqual("copilot login", AgentHub.ExtractLoginCommandHint(
                "This agent signs in from a terminal, not through the panel: run `copilot login` once, then press Reconnect (Settings > Account)."));
            Assert.AreEqual("kimi login", AgentHub.ExtractLoginCommandHint(
                "sign-in failed for every method the agent offers (Browser: closed). The remaining method must be completed in a terminal: run `kimi login` or `kimi --login` once, then press Reconnect (Settings > Account)."));
            Assert.IsNull(AgentHub.ExtractLoginCommandHint("sign-in failed for every method the agent offers (ChatGPT: cancelled). Sign in to the agent's own CLI once, then press Sign in or Reconnect (Settings > Account)."));
            Assert.IsNull(AgentHub.ExtractLoginCommandHint("odd `` empty"));
            Assert.IsNull(AgentHub.ExtractLoginCommandHint(null));
        }

        [Test]
        public void Bridge_RaisesSignInEvents_AroundAuthenticate()
        {
            var toAgent = new System.Collections.Generic.List<string>();
            var toPanel = new System.Collections.Generic.List<string>();
            var bridge = new AcpProtocolBridge(new AcpLaunchSpec(), "/p", toAgent.Add, toPanel.Add, null);
            string startedMethod = null;
            bool? finishedOk = null;
            bridge.AuthenticationStarted += delegate(string id, string name) { startedMethod = id + "/" + name; };
            bridge.AuthenticationFinished += delegate(bool ok, string error) { finishedOk = ok; };

            bridge.OnPanelLine(Core.Protocol.OutboundMessages.Initialize("req_1"));
            AcpInbound init = AcpJsonRpc.TryParse(toAgent[toAgent.Count - 1]);
            bridge.OnAgentLine(AcpJsonRpc.Response(init.Id, JsonNode.NewObject()
                .Set("protocolVersion", 1)
                .Set("agentCapabilities", JsonNode.NewObject())
                .Set("authMethods", JsonNode.NewArray().Add(JsonNode.NewObject()
                    .Set("id", "oauth-personal").Set("name", "Login with Google")))));
            AcpInbound newSession = AcpJsonRpc.TryParse(toAgent[toAgent.Count - 1]);
            bridge.OnAgentLine(AcpJsonRpc.ErrorResponse(newSession.Id, AcpJsonRpc.AuthRequired, "Authentication required"));
            Assert.AreEqual("oauth-personal/Login with Google", startedMethod);
            Assert.IsNull(finishedOk);
            AcpInbound auth = AcpJsonRpc.TryParse(toAgent[toAgent.Count - 1]);
            Assert.AreEqual("authenticate", auth.Method);
            bridge.OnAgentLine(AcpJsonRpc.Response(auth.Id, JsonNode.NewObject()));
            Assert.IsTrue(finishedOk.HasValue && finishedOk.Value);
            Assert.AreEqual("session/new", AcpJsonRpc.TryParse(toAgent[toAgent.Count - 1]).Method);
        }

        [Test]
        public void DeathAfterSignInFailure_IsNotAutoReconnected()
        {
            Assert.IsTrue(AgentHub.ShouldAutoReconnectAfterDeath(null));
            Assert.IsFalse(AgentHub.ShouldAutoReconnectAfterDeath("CODEX_API_KEY is not set"));
            Assert.IsFalse(AgentHub.ShouldAutoReconnectAfterDeath(string.Empty));
            // An ACP agent that died before its handshake completed is not respawned either.
            Assert.IsFalse(AgentHub.ShouldAutoReconnectAfterDeath(null, true, false));
            Assert.IsTrue(AgentHub.ShouldAutoReconnectAfterDeath(null, true, true));
            // Claude Code keeps the bounded retry for early deaths.
            Assert.IsTrue(AgentHub.ShouldAutoReconnectAfterDeath(null, false, false));
        }

        [Test]
        public void AcpBackend_GetsTheLongInitializeTimeout()
        {
            Assert.Greater(AgentHub.AcpInitializeTimeoutSeconds, Core.Client.PendingRequestMap.DefaultTimeoutSeconds);
        }
    }
}
