using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Unit-tests AgentHub.ResolveSpawnModel -- the pure decision behind the
    /// fresh-vs-resume --model argument (docs/design-notes/2026-08-01-model-
    /// settings-rework.md section 4.1) -- without spawning a real CLI
    /// process. AgentHub.StartClient itself is private and resolves a real
    /// CLI executable path before ever building AgentClientOptions, so it
    /// cannot be exercised end-to-end in this test environment; this seam
    /// covers the exact conditional that used to live inline in StartClient.
    /// </summary>
    [TestFixture]
    public class AgentHubStartClientArgTests
    {
        [Test]
        public void FreshSpawn_ResumeSessionIdNull_PassesTheDefaultModel()
        {
            Assert.AreEqual("sonnet", AgentHub.ResolveSpawnModel(null, "sonnet"));
        }

        [Test]
        public void FreshSpawn_EmptyDefaultModel_PassesEmpty()
        {
            Assert.AreEqual(string.Empty, AgentHub.ResolveSpawnModel(null, string.Empty));
        }

        [Test]
        public void ResumeSpawn_OmitsTheModelEvenWhenADefaultIsSet()
        {
            // The measured contract (R07 section 3): --model always
            // overrides a resumed session's own saved model, so passing
            // the persisted default on every --resume would silently drag
            // a running session back to it every time it reconnects.
            Assert.IsNull(AgentHub.ResolveSpawnModel("existing-session-id", "sonnet"));
        }

        [Test]
        public void ResumeSpawn_EmptyDefaultModel_StillOmits()
        {
            Assert.IsNull(AgentHub.ResolveSpawnModel("existing-session-id", string.Empty));
        }

        // -- AgentHub.ResolveEnsureStartedResumeId (2026-08-01 review fix)
        // -----------------------------------------------------------------
        // EnsureStarted used to pass its computed resumeId straight into
        // StartClient without normalizing empty-string to null, unlike
        // every other StartClient caller. Since ChatSession.sessionId and
        // SessionStateBridge.CurrentSessionId both default to
        // string.Empty (never null), a genuinely fresh spawn ended up
        // calling StartClient("") -- which ResolveSpawnModel's
        // `resumeSessionId == null` check would misclassify as a resume,
        // silently dropping --model and ignoring the persisted default.

        [Test]
        public void EnsureStartedResumeId_BothSourcesEmpty_ResolvesToNull()
        {
            // The fresh/domain-reload-restart case the bug misclassified:
            // neither ChatSession.sessionId nor SessionStateBridge.
            // CurrentSessionId has ever been populated (both "", their
            // documented default), so this must feed ResolveSpawnModel
            // "fresh" (null), not "resume".
            Assert.IsNull(AgentHub.ResolveEnsureStartedResumeId(string.Empty, string.Empty));
        }

        [Test]
        public void EnsureStartedResumeId_BothSourcesNull_ResolvesToNull()
        {
            Assert.IsNull(AgentHub.ResolveEnsureStartedResumeId(null, null));
        }

        [Test]
        public void EnsureStartedResumeId_SessionSessionIdSet_PrefersIt()
        {
            Assert.AreEqual("session-a",
                AgentHub.ResolveEnsureStartedResumeId("session-a", "session-b"));
        }

        [Test]
        public void EnsureStartedResumeId_SessionSessionIdEmpty_FallsBackToBridge()
        {
            Assert.AreEqual("bridge-session",
                AgentHub.ResolveEnsureStartedResumeId(string.Empty, "bridge-session"));
        }

        [Test]
        public void EnsureStartedResumeId_BridgeAlsoEmpty_ResolvesToNull()
        {
            Assert.IsNull(AgentHub.ResolveEnsureStartedResumeId(string.Empty, string.Empty));
        }

        // -- AgentHub.ComposeAppendSystemPrompt (v0.11.0, docs/design-notes/
        // 2026-08-02-subagent-model-precedence.md section 3.2): the
        // --append-system-prompt payload composition, pure and unit tested
        // without spawning a real CLI process the same way ResolveSpawnModel
        // is above. -----------------------------------------------------

        [Test]
        public void ComposeAppendSystemPrompt_AgentDecides_NoCustomInstructions_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeAppendSystemPrompt(string.Empty, SubagentCostPolicy.AgentDecides));
        }

        [Test]
        public void ComposeAppendSystemPrompt_AgentDecides_WithCustomInstructions_ReturnsUnchanged()
        {
            // AgentDecides injects nothing -- a panel that never touches
            // the new policy field must behave exactly like every version
            // before it existed.
            Assert.AreEqual("Always reply in Japanese.",
                AgentHub.ComposeAppendSystemPrompt("Always reply in Japanese.", SubagentCostPolicy.AgentDecides));
        }

        [Test]
        public void ComposeAppendSystemPrompt_AgentDecides_NullCustomInstructions_TreatedAsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeAppendSystemPrompt(null, SubagentCostPolicy.AgentDecides));
        }

        [Test]
        public void ComposeAppendSystemPrompt_HaikuForSimpleTasks_NoCustomInstructions_ReturnsOnlyPolicyLine()
        {
            string result = AgentHub.ComposeAppendSystemPrompt(string.Empty, SubagentCostPolicy.HaikuForSimpleTasks);
            Assert.AreEqual(AgentHub.HaikuForSimpleTasksInstructionLine, result);
        }

        [Test]
        public void ComposeAppendSystemPrompt_HaikuForSimpleTasks_NullCustomInstructions_ReturnsOnlyPolicyLine()
        {
            string result = AgentHub.ComposeAppendSystemPrompt(null, SubagentCostPolicy.HaikuForSimpleTasks);
            Assert.AreEqual(AgentHub.HaikuForSimpleTasksInstructionLine, result);
        }

        [Test]
        public void ComposeAppendSystemPrompt_HaikuForSimpleTasks_WithCustomInstructions_JoinsWithBlankLine()
        {
            string result = AgentHub.ComposeAppendSystemPrompt(
                "Always reply in Japanese.", SubagentCostPolicy.HaikuForSimpleTasks);
            Assert.AreEqual(
                "Always reply in Japanese." + "\n\n" + AgentHub.HaikuForSimpleTasksInstructionLine,
                result);
        }

        [Test]
        public void ComposeAppendSystemPrompt_HaikuForSimpleTasksInstructionLine_MentionsHaikuAndModelParameter()
        {
            // Loose content guard (not an exact-text pin, unlike the join
            // tests above): the line must actually tell the agent to pass
            // model:"haiku" for simple subtasks and to omit it otherwise,
            // per the approved design note wording.
            StringAssert.Contains("model: \"haiku\"", AgentHub.HaikuForSimpleTasksInstructionLine);
            StringAssert.Contains("Agent", AgentHub.HaikuForSimpleTasksInstructionLine);
            // 2026-08-02 live E2E: the original soft phrasing was ignored
            // twice in a row by a sonnet parent (args provably carried the
            // line), so the wording was strengthened to an imperative rule.
            // Guard the imperative marker so a future rewording does not
            // silently soften it back.
            StringAssert.Contains("MUST", AgentHub.HaikuForSimpleTasksInstructionLine);
        }

        // -- AgentHub.ComposeAppendSystemPrompt 3-arg overload (Phase 5b
        // stream C, docs/design-notes/2026-08-01-phase5-unity-ops-design.md
        // section 3b/C3): adds the Extension Profiles section AFTER the
        // cost-policy line -- ordering is custom instructions -> cost
        // policy -> profiles. -------------------------------------------

        [Test]
        public void ComposeAppendSystemPrompt3Arg_AllEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeAppendSystemPrompt(string.Empty, SubagentCostPolicy.AgentDecides, string.Empty));
        }

        [Test]
        public void ComposeAppendSystemPrompt3Arg_NullProfilesSection_TreatedAsEmpty()
        {
            Assert.AreEqual("Always reply in Japanese.",
                AgentHub.ComposeAppendSystemPrompt("Always reply in Japanese.", SubagentCostPolicy.AgentDecides, null));
        }

        [Test]
        public void ComposeAppendSystemPrompt3Arg_OnlyProfilesSection_ReturnsItUnchanged()
        {
            Assert.AreEqual("## VRChat SDK3\nsome instructions",
                AgentHub.ComposeAppendSystemPrompt(string.Empty, SubagentCostPolicy.AgentDecides,
                    "## VRChat SDK3\nsome instructions"));
        }

        [Test]
        public void ComposeAppendSystemPrompt3Arg_CustomInstructionsAndProfiles_JoinsWithBlankLine()
        {
            // AgentDecides -> no cost-policy line -- ordering collapses to
            // just custom instructions then profiles.
            string result = AgentHub.ComposeAppendSystemPrompt(
                "Always reply in Japanese.", SubagentCostPolicy.AgentDecides, "## VRChat SDK3\nline1");
            Assert.AreEqual("Always reply in Japanese." + "\n\n" + "## VRChat SDK3\nline1", result);
        }

        [Test]
        public void ComposeAppendSystemPrompt3Arg_FullOrdering_CustomThenCostPolicyThenProfiles()
        {
            string result = AgentHub.ComposeAppendSystemPrompt(
                "Always reply in Japanese.", SubagentCostPolicy.HaikuForSimpleTasks, "## VRChat SDK3\nline1");
            string expected = "Always reply in Japanese." + "\n\n" + AgentHub.HaikuForSimpleTasksInstructionLine
                + "\n\n" + "## VRChat SDK3\nline1";
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ComposeAppendSystemPrompt3Arg_CostPolicyOnlyNoCustomInstructions_ProfilesStillAppendedAfter()
        {
            string result = AgentHub.ComposeAppendSystemPrompt(
                string.Empty, SubagentCostPolicy.HaikuForSimpleTasks, "## FinalIK\nline1");
            string expected = AgentHub.HaikuForSimpleTasksInstructionLine + "\n\n" + "## FinalIK\nline1";
            Assert.AreEqual(expected, result);
        }

        // -- AgentHub.ComposeUapOpsSteeringSection / 4-arg
        // ComposeAppendSystemPrompt overload (Stream C1, 2026-08-02 design
        // note section 3): steers Unity edits toward uap_* tools, injected
        // between the cost-policy line and the profiles section. Gated on
        // the UapOps master toggle; the family list is derived from the
        // ENABLED module list only, so a disabled module is never
        // advertised. --------------------------------------------------

        [Test]
        public void SteeringSection_UapOpsDisabled_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeUapOpsSteeringSection(false, new[] { "core", "prefab", "anim", "editor" }));
        }

        [Test]
        public void SteeringSection_UapOpsEnabled_NoModulesEnabled_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeUapOpsSteeringSection(true, new string[0]));
        }

        [Test]
        public void SteeringSection_UapOpsEnabled_NullModuleList_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, AgentHub.ComposeUapOpsSteeringSection(true, null));
        }

        [Test]
        public void SteeringSection_OnlyCoreEnabled_MentionsOnlyCoreFamily_NotOthers()
        {
            string section = AgentHub.ComposeUapOpsSteeringSection(true, new[] { "core" });

            StringAssert.Contains("scene objects/components/properties/assets", section);
            StringAssert.DoesNotContain("prefab overrides", section);
            StringAssert.DoesNotContain("animation clips", section);
            StringAssert.DoesNotContain("screenshots", section);
        }

        [Test]
        public void SteeringSection_AnimModuleOff_NeverMentionsAnimationFamily()
        {
            // The exact live-measured defect this stream fixes: the anim
            // module was ON in that session, but this test additionally
            // pins the OFF case, since C1 explicitly requires the text to
            // never promise a disabled tool.
            string section = AgentHub.ComposeUapOpsSteeringSection(true,
                new[] { "core", "prefab", "editor" });

            StringAssert.DoesNotContain("animation", section);
            StringAssert.DoesNotContain("animator", section);
        }

        [Test]
        public void SteeringSection_AllModulesEnabled_MentionsAllFourFamilies_InDesignOrder()
        {
            string section = AgentHub.ComposeUapOpsSteeringSection(true,
                new[] { "core", "prefab", "anim", "editor" });

            int core = section.IndexOf("scene objects/components/properties/assets", System.StringComparison.Ordinal);
            int prefab = section.IndexOf("prefab overrides", System.StringComparison.Ordinal);
            int anim = section.IndexOf("animation clips", System.StringComparison.Ordinal);
            int editor = section.IndexOf("screenshots", System.StringComparison.Ordinal);

            Assert.Greater(core, -1);
            Assert.Greater(prefab, -1);
            Assert.Greater(anim, -1);
            Assert.Greater(editor, -1);
            Assert.Less(core, prefab, "design section 3's family order: scene/component/property/asset first");
            Assert.Less(prefab, anim, "then prefab overrides");
            Assert.Less(anim, editor, "then anim/animator/material, then screenshot/menu last");
        }

        [Test]
        public void SteeringSection_NeverForbidsOrDiscouragesUloop_NamesItAsTheFallback()
        {
            string section = AgentHub.ComposeUapOpsSteeringSection(true, new[] { "core" });

            StringAssert.Contains("uloop", section);
            StringAssert.DoesNotContain("never use uloop", section);
            StringAssert.DoesNotContain("forbidden", section);
            StringAssert.DoesNotContain("do not use uloop", section);
        }

        [Test]
        public void SteeringSection_StaysWithinTenLines()
        {
            // 2026-09-08: the core-module addendum (deferred-loading,
            // uap_transform_set-vs-uap_property_set, main-thread-timeout
            // guidance) added three lines on top of the original six-line
            // budget when "core" is enabled, so the cap moved from six to
            // ten to keep some headroom rather than pinning the exact count.
            string section = AgentHub.ComposeUapOpsSteeringSection(true,
                new[] { "core", "prefab", "anim", "editor" });
            int lineCount = section.Split('\n').Length;
            Assert.LessOrEqual(lineCount, 10);
        }

        [Test]
        public void SteeringSection_CoreEnabled_MentionsDeferredLoadingAndTransformSetAndTimeoutGuidance()
        {
            // 2026-09-08 measured motivation: the agent edited a Camera
            // transform, a TextMeshPro text and an object's rotation
            // through `uloop execute-dynamic-code` instead of the typed
            // uap_* tools -- because ToolSearch-loading, uap_transform_set
            // and the two different main-thread-timeout failure modes were
            // never explained.
            string section = AgentHub.ComposeUapOpsSteeringSection(true, new[] { "core" });

            StringAssert.Contains("mcp__unity-ops__uap_transform_set", section);
            StringAssert.Contains("uap_transform_set", section);
            StringAssert.Contains("was never started", section);
        }

        [Test]
        public void SteeringSection_CoreDisabled_OtherModulesEnabled_NeverMentionsTransformSet()
        {
            string section = AgentHub.ComposeUapOpsSteeringSection(true,
                new[] { "prefab", "anim", "editor" });

            StringAssert.DoesNotContain("uap_transform_set", section);
        }

        [Test]
        public void ComposeAppendSystemPrompt4Arg_FullOrdering_CustomThenCostPolicyThenSteeringThenProfiles()
        {
            string steering = "STEERING-SECTION";
            string result = AgentHub.ComposeAppendSystemPrompt(
                "Always reply in Japanese.", SubagentCostPolicy.HaikuForSimpleTasks,
                steering, "## VRChat SDK3\nline1");

            string expected = "Always reply in Japanese." + "\n\n" + AgentHub.HaikuForSimpleTasksInstructionLine
                + "\n\n" + steering + "\n\n" + "## VRChat SDK3\nline1";
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ComposeAppendSystemPrompt4Arg_EmptySteeringSection_MatchesThreeArgResultExactly()
        {
            // The toggle-off case (uapOpsEnabled:false -> empty steering
            // section): the 4-arg call must produce EXACTLY what the
            // existing 3-arg overload produces, i.e. inserting nothing.
            string with3Arg = AgentHub.ComposeAppendSystemPrompt(
                "Always reply in Japanese.", SubagentCostPolicy.HaikuForSimpleTasks, "## FinalIK\nline1");
            string with4ArgEmptySteering = AgentHub.ComposeAppendSystemPrompt(
                "Always reply in Japanese.", SubagentCostPolicy.HaikuForSimpleTasks,
                string.Empty, "## FinalIK\nline1");

            Assert.AreEqual(with3Arg, with4ArgEmptySteering);
        }

        [Test]
        public void ComposeAppendSystemPrompt4Arg_NullSteeringSection_TreatedAsEmpty()
        {
            Assert.AreEqual("Always reply in Japanese.",
                AgentHub.ComposeAppendSystemPrompt(
                    "Always reply in Japanese.", SubagentCostPolicy.AgentDecides, null, null));
        }

        [Test]
        public void ComposeAppendSystemPrompt4Arg_AllEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty,
                AgentHub.ComposeAppendSystemPrompt(
                    string.Empty, SubagentCostPolicy.AgentDecides, string.Empty, string.Empty));
        }

        // -- AgentHub.ComputeMcpConfigValue (Phase 5a, docs/design-notes/
        // 2026-08-01-phase5-unity-ops-design.md section 1.1/8.1; SEC-2
        // reshape): the arg-plumbing decision behind
        // AgentClientOptions.McpConfigJson, unit tested without starting a
        // real UapOpsServer/HttpListener, the same way ResolveSpawnModel is
        // above. The file half uses a real temp directory, like
        // GateHookInstallerTests' EnsureInstalled coverage. ----------------

        [Test]
        public void ComputeMcpConfigValue_Disabled_ReturnsNull()
        {
            Assert.IsNull(AgentHub.ComputeMcpConfigValue(false, "/tmp/x", 12345, "tok", null));
        }

        [Test]
        public void ComputeMcpConfigValue_Disabled_ReturnsNull_EvenWithAValidPortAndToken()
        {
            // The master toggle must win regardless of whether a server
            // happens to already be listening -- StartClient never passes
            // a real port/token through when uapOpsEnabled is false in the
            // first place, but this pins the decision function's own
            // contract independent of that caller behavior.
            Assert.IsNull(AgentHub.ComputeMcpConfigValue(false, "/tmp/x", 54321, "real-token", null));
        }

        [Test]
        public void ComputeMcpConfigValue_Enabled_WritesTheFile_AndReturnsItsAbsolutePath()
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "uap_mcparg_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                string value = AgentHub.ComputeMcpConfigValue(true, temp, 54321, "tok-xyz", null);
                Assert.IsNotNull(value);
                Assert.IsTrue(Path.IsPathRooted(value),
                    "the CLI resolves --mcp-config relative to its own cwd,"
                    + " so the value must be an absolute path");
                Assert.IsTrue(File.Exists(value), "config file must exist at " + value);
                JsonNode root = JsonParser.Parse(File.ReadAllText(value));
                JsonNode server = root["mcpServers"][UapOpsMcpConfig.ServerName];
                Assert.AreEqual("http://127.0.0.1:54321/mcp", server["url"].AsString());
                Assert.AreEqual("Bearer tok-xyz", server["headers"]["Authorization"].AsString());
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        [Test]
        public void ComputeMcpConfigValue_Enabled_TheReturnedValueNeverContainsTheToken()
        {
            // THE SEC-2 pin: the returned value is what BuildArguments puts
            // on the spawned process's command line, which every local
            // process can read (/proc/*/cmdline on Linux is world-readable
            // regardless of umask; WMI/Task Manager on Windows). The token
            // must live only inside the file.
            string temp = Path.Combine(Path.GetTempPath(),
                "uap_mcparg_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                string value = AgentHub.ComputeMcpConfigValue(
                    true, temp, 54321, "secret-token-do-not-leak", null);
                Assert.IsNotNull(value);
                StringAssert.DoesNotContain("secret-token-do-not-leak", value);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        [Test]
        public void ComputeMcpConfigValue_Enabled_UnwritableRoot_FallsBackToTheInlineJson()
        {
            // A null projectRoot makes EnsureConfigFileWritten return null
            // (its own contract) -- the fallback must keep UapOps working
            // by returning the inline payload rather than disabling the
            // feature. This is the one path that still carries the token
            // on the command line, by design (see ComputeMcpConfigValue's
            // doc comment).
            string value = AgentHub.ComputeMcpConfigValue(true, null, 54321, "tok-xyz", null);
            Assert.IsNotNull(value);
            JsonNode root = JsonParser.Parse(value);
            JsonNode server = root["mcpServers"][UapOpsMcpConfig.ServerName];
            Assert.AreEqual("http://127.0.0.1:54321/mcp", server["url"].AsString());
            Assert.AreEqual("Bearer tok-xyz", server["headers"]["Authorization"].AsString());
        }

        // -- AgentHub.ShouldSendUapOpsMcpReconnect (Phase 5a, docs/research/
        // 08-mcp-transport.md section 3.4's mcp_reconnect safety net):
        // pure decision extracted from OnInitMessageReceived's wiring, unit
        // tested without a real AgentClient/UapOpsServer. -----------------

        private static SystemInitMessage MakeInitMessage(string serverName, string status)
        {
            JsonNode node = JsonNode.NewObject().Set("session_id", "sess-1");
            if (serverName != null)
            {
                node.Set("mcp_servers", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("name", serverName).Set("status", status)));
            }
            return SystemInitMessage.FromJson(node, null);
        }

        [Test]
        public void ShouldSendUapOpsMcpReconnect_ServerFailed_ServerRunning_ReturnsTrue()
        {
            SystemInitMessage message = MakeInitMessage(UapOpsMcpConfig.ServerName, "failed");
            Assert.IsTrue(AgentHub.ShouldSendUapOpsMcpReconnect(message, uapOpsServerRunning: true));
        }

        [Test]
        public void ShouldSendUapOpsMcpReconnect_ServerFailed_ButUnityServerNotRunning_ReturnsFalse()
        {
            // Sending mcp_reconnect while the Unity-side server is still
            // down would just fail again for no reason.
            SystemInitMessage message = MakeInitMessage(UapOpsMcpConfig.ServerName, "failed");
            Assert.IsFalse(AgentHub.ShouldSendUapOpsMcpReconnect(message, uapOpsServerRunning: false));
        }

        [Test]
        public void ShouldSendUapOpsMcpReconnect_ServerConnected_ReturnsFalse()
        {
            SystemInitMessage message = MakeInitMessage(UapOpsMcpConfig.ServerName, "connected");
            Assert.IsFalse(AgentHub.ShouldSendUapOpsMcpReconnect(message, uapOpsServerRunning: true));
        }

        [Test]
        public void ShouldSendUapOpsMcpReconnect_DifferentServerFailed_ReturnsFalse()
        {
            // Only the UapOps server name is this panel's responsibility --
            // a user-configured third-party MCP server failing is not
            // something AgentHub should ever try to fix.
            SystemInitMessage message = MakeInitMessage("some-other-server", "failed");
            Assert.IsFalse(AgentHub.ShouldSendUapOpsMcpReconnect(message, uapOpsServerRunning: true));
        }

        [Test]
        public void ShouldSendUapOpsMcpReconnect_NoMcpServersAtAll_ReturnsFalse()
        {
            SystemInitMessage message = MakeInitMessage(null, null);
            Assert.IsFalse(AgentHub.ShouldSendUapOpsMcpReconnect(message, uapOpsServerRunning: true));
        }

        [Test]
        public void ShouldSendUapOpsMcpReconnect_NullMessage_ReturnsFalse()
        {
            Assert.IsFalse(AgentHub.ShouldSendUapOpsMcpReconnect(null, uapOpsServerRunning: true));
        }

        // -- AgentHub.ShouldInstallScriptGateHook (design section 8.7
        // point 5: "non-Windows is v1 can_use_tool-only") -- the arg-
        // plumbing decision behind AgentClientOptions.SettingsFilePath,
        // pure and unit tested without touching disk, the same way
        // ComputeMcpConfigValue is above. -----------------------------------

        [Test]
        public void ShouldInstallScriptGateHook_EnabledOnWindows_ReturnsTrue()
        {
            Assert.IsTrue(AgentHub.ShouldInstallScriptGateHook(true, RuntimePlatform.WindowsEditor));
        }

        [Test]
        public void ShouldInstallScriptGateHook_DisabledOnWindows_ReturnsFalse()
        {
            Assert.IsFalse(AgentHub.ShouldInstallScriptGateHook(false, RuntimePlatform.WindowsEditor));
        }

        [Test]
        public void ShouldInstallScriptGateHook_EnabledOnOSX_ReturnsFalse()
        {
            Assert.IsFalse(AgentHub.ShouldInstallScriptGateHook(true, RuntimePlatform.OSXEditor));
        }

        [Test]
        public void ShouldInstallScriptGateHook_EnabledOnLinux_ReturnsFalse()
        {
            Assert.IsFalse(AgentHub.ShouldInstallScriptGateHook(true, RuntimePlatform.LinuxEditor));
        }

        // -- HUB-9: AgentHub.ScriptGateEffectivelyInert -- the combination
        // where the gate is ON and yet NOTHING enforces it. Two layers
        // exist (the Windows-only PreToolUse hook, and the can_use_tool
        // pre-filter); dangerouslySkipPermissions removes the second by
        // construction, so off-Windows that leaves zero while the UI still
        // says the gate is on. -------------------------------------------

        [Test]
        public void ScriptGateEffectivelyInert_AllThreeConditions_ReturnsTrue()
        {
            Assert.IsTrue(AgentHub.ScriptGateEffectivelyInert(true, true, RuntimePlatform.OSXEditor));
            Assert.IsTrue(AgentHub.ScriptGateEffectivelyInert(true, true, RuntimePlatform.LinuxEditor));
        }

        [Test]
        public void ScriptGateEffectivelyInert_OnWindows_ReturnsFalse()
        {
            // The hook layer is installed there, so the gate really enforces.
            Assert.IsFalse(AgentHub.ScriptGateEffectivelyInert(true, true, RuntimePlatform.WindowsEditor));
        }

        [Test]
        public void ScriptGateEffectivelyInert_WithoutSkipPermissions_ReturnsFalse()
        {
            // can_use_tool still reaches TryAutoDenyForScriptGate.
            Assert.IsFalse(AgentHub.ScriptGateEffectivelyInert(true, false, RuntimePlatform.OSXEditor));
        }

        [Test]
        public void ScriptGateEffectivelyInert_GateDisabled_ReturnsFalse()
        {
            // Nothing to warn about: the user turned the gate off knowingly.
            Assert.IsFalse(AgentHub.ScriptGateEffectivelyInert(false, true, RuntimePlatform.OSXEditor));
            Assert.IsFalse(AgentHub.ScriptGateEffectivelyInert(false, false, RuntimePlatform.LinuxEditor));
        }

        [Test]
        public void ShouldInstallScriptGateHook_DisabledOnOSX_ReturnsFalse()
        {
            Assert.IsFalse(AgentHub.ShouldInstallScriptGateHook(false, RuntimePlatform.OSXEditor));
        }

        // -- AgentHub.CloneNextSpawnOnlyFields uapOpsModules handling
        // (2026-08-02 review fix, Stream C1 regression): before this fix
        // the method never copied uapOpsModules at all, so
        // _lastSpawnedSettingsSnapshot.uapOpsModules always held whatever
        // `new PanelSettings()`'s field initializer happened to be
        // (["core","prefab","editor"]) regardless of what was actually
        // spawned with -- silently breaking SettingsChangeDetector.
        // RequiresReconnect's now-added uapOpsModules comparison (either
        // false-positive-nagging or, worse, false-negative-hiding a real
        // module change, depending on what the two lists happened to be).
        // -------------------------------------------------------------

        [Test]
        public void CloneNextSpawnOnlyFields_CopiesUapOpsModulesContents()
        {
            var source = new PanelSettings { uapOpsModules = new List<string> { "core", "anim" } };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            CollectionAssert.AreEqual(new[] { "core", "anim" }, clone.uapOpsModules);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_UapOpsModules_IsAnIndependentListInstance()
        {
            // The Settings UI's module toggle handlers mutate
            // PanelStateStore.instance.Settings.uapOpsModules IN PLACE
            // (List.Add/Remove) -- a shallow reference copy here would let
            // a LATER toggle silently mutate this already-taken snapshot
            // too, permanently hiding the very drift the detector now
            // exists to catch (mirrors CloneAgentModelOverrides' own
            // reasoning for its list of mutable entries).
            var sourceModules = new List<string> { "core" };
            var source = new PanelSettings { uapOpsModules = sourceModules };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);
            sourceModules.Add("prefab");

            CollectionAssert.AreEqual(new[] { "core" }, clone.uapOpsModules);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_NullUapOpsModules_BecomesEmptyList()
        {
            var source = new PanelSettings { uapOpsModules = null };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            Assert.IsNotNull(clone.uapOpsModules);
            Assert.AreEqual(0, clone.uapOpsModules.Count);
        }

        // -- extensionProfilesEnabled / approvedProfileHashes (2026-08-03)
        // -------------------------------------------------------------
        // Same class of defect as uapOpsModules above, but by OMISSION:
        // both fields were absent from the object initializer entirely, so
        // every snapshot carried PanelSettings' own field-initializer
        // values (extensionProfilesEnabled == true, an EMPTY hash list)
        // instead of what StartClient actually spawned with -- while
        // SettingsChangeDetector.RequiresReconnect compares both. Turning
        // profiles off, or approving one project-local profile, therefore
        // produced a "settings changed, reconnect to apply" state that
        // survived the reconnect (the rebuilt snapshot repeated the same
        // wrong defaults) and could never settle.
        //
        // SettingsChangeDetectorTests could not catch this: its Make()
        // builds both sides with `new PanelSettings { ... }` and sets the
        // fields by hand, never routing through the real clone. These
        // exercise the clone itself.

        [Test]
        public void CloneNextSpawnOnlyFields_CopiesExtensionProfilesEnabled_WhenDisabled()
        {
            // false is the load-bearing direction: the field initializer
            // defaults to TRUE, so an omitted copy is only observable once
            // the user turns profiles off.
            var source = new PanelSettings { extensionProfilesEnabled = false };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            Assert.IsFalse(clone.extensionProfilesEnabled);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_CopiesExtensionProfilesEnabled_WhenEnabled()
        {
            var source = new PanelSettings { extensionProfilesEnabled = true };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            Assert.IsTrue(clone.extensionProfilesEnabled);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_CopiesClaudeAuth_WhenSubscriptionOnly()
        {
            // SubscriptionOnly is the load-bearing direction: the field
            // initializer defaults to Auto, so an omitted copy is only
            // observable once the user opts into SubscriptionOnly (docs/
            // design-notes/2026-09-10-claude-api-key-auth-passthrough.md).
            var source = new PanelSettings { claudeAuth = ClaudeAuthMode.SubscriptionOnly };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            Assert.AreEqual(ClaudeAuthMode.SubscriptionOnly, clone.claudeAuth);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_CopiesApprovedProfileHashesContents()
        {
            var source = new PanelSettings
            {
                approvedProfileHashes = new List<string> { "hash-a", "hash-b" }
            };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            CollectionAssert.AreEqual(new[] { "hash-a", "hash-b" }, clone.approvedProfileHashes);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_ApprovedProfileHashes_IsAnIndependentListInstance()
        {
            // The Settings approve/revoke handlers edit this list in place,
            // exactly like the module toggles do for uapOpsModules -- a
            // shared reference would let a later approval mutate the
            // already-taken snapshot and hide the drift.
            var sourceHashes = new List<string> { "hash-a" };
            var source = new PanelSettings { approvedProfileHashes = sourceHashes };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);
            sourceHashes.Add("hash-b");

            CollectionAssert.AreEqual(new[] { "hash-a" }, clone.approvedProfileHashes);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_NullApprovedProfileHashes_BecomesEmptyList()
        {
            var source = new PanelSettings { approvedProfileHashes = null };

            PanelSettings clone = AgentHub.CloneNextSpawnOnlyFields(source);

            Assert.IsNotNull(clone.approvedProfileHashes);
            Assert.AreEqual(0, clone.approvedProfileHashes.Count);
        }

        [Test]
        public void CloneNextSpawnOnlyFields_ThenDetector_ReportsNoDrift_ForProfileFields()
        {
            // The end-to-end shape of the bug: snapshot the settings the
            // way StartClient does, change NOTHING, and the detector must
            // see no reason to reconnect. Before the fix this failed for a
            // profiles-off / hashes-approved configuration.
            var settings = new PanelSettings
            {
                extensionProfilesEnabled = false,
                approvedProfileHashes = new List<string> { "hash-a" }
            };

            PanelSettings snapshot = AgentHub.CloneNextSpawnOnlyFields(settings);

            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(snapshot, settings),
                "a freshly taken snapshot must never report drift against the very "
                + "settings it was taken from");
        }

        [Test]
        public void CloneNextSpawnOnlyFields_ThenDetector_StillCatchesRealProfileDrift()
        {
            // The guard must not have been bought by making the detector
            // blind: a genuine post-spawn change is still drift.
            var settings = new PanelSettings
            {
                extensionProfilesEnabled = true,
                approvedProfileHashes = new List<string>()
            };
            PanelSettings snapshot = AgentHub.CloneNextSpawnOnlyFields(settings);

            settings.approvedProfileHashes.Add("hash-approved-after-spawn");
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(snapshot, settings),
                "approving a profile after the spawn must still require a reconnect");

            settings.approvedProfileHashes.Clear();
            settings.extensionProfilesEnabled = false;
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(snapshot, settings),
                "toggling profiles off after the spawn must still require a reconnect");
        }
    }
}
