using System.Collections.Generic;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guard for the Settings "Reconnect now" hint
    /// (SettingsView.RefreshReconnectHint): only cliManualPath,
    /// subagentModel, allowedTools, disallowedTools,
    /// dangerouslySkipPermissions and (since docs/design-notes/2026-08-01-
    /// thinking-content-loss.md section 7) showThinking are next-spawn-only
    /// CLI arguments/env vars (ARCHITECTURE.md D2; docs/design-notes/
    /// 2026-08-01-model-settings-rework.md section 4.2 for subagentModel);
    /// every other PanelSettings field applies live and must NEVER trip
    /// this detector, or the hint would nag the user after a no-op edit
    /// (e.g. toggling Ctrl+Enter). `model` is deliberately NOT in that list
    /// (see the regression-pin section below).
    /// </summary>
    public class SettingsChangeDetectorTests
    {
        private static PanelSettings Make(string cliPath = "", string model = "",
            bool dangerous = false, bool showThinking = true, string subagentModel = "",
            ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto,
            SubagentCostPolicy subagentCostPolicy = SubagentCostPolicy.AgentDecides,
            bool uapOpsEnabled = true, bool uapScriptGateEnabled = true,
            bool extensionProfilesEnabled = true, List<string> approvedProfileHashes = null,
            params string[] allowed)
        {
            return new PanelSettings
            {
                cliManualPath = cliPath,
                model = model,
                dangerouslySkipPermissions = dangerous,
                showThinking = showThinking,
                subagentModel = subagentModel,
                claudeAuth = claudeAuth,
                subagentCostPolicy = subagentCostPolicy,
                uapOpsEnabled = uapOpsEnabled,
                uapScriptGateEnabled = uapScriptGateEnabled,
                extensionProfilesEnabled = extensionProfilesEnabled,
                approvedProfileHashes = approvedProfileHashes ?? new List<string>(),
                allowedTools = new List<string>(allowed),
                disallowedTools = new List<string>()
            };
        }

        [Test]
        public void IdenticalSnapshots_DoNotRequireReconnect()
        {
            PanelSettings a = Make("C:/claude.exe", "sonnet", false, allowed: new[] { "Read", "Edit" });
            PanelSettings b = Make("C:/claude.exe", "sonnet", false, allowed: new[] { "Read", "Edit" });
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentCliPath_RequiresReconnect()
        {
            PanelSettings a = Make("C:/old.exe");
            PanelSettings b = Make("C:/new.exe");
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentModel_DoesNotRequireReconnect()
        {
            // Regression pin (v0.9.0 default/in-use split, docs/design-
            // notes/2026-08-01-model-settings-rework.md section 4.1): model
            // now means "default model for NEW sessions" -- AgentHub.
            // StartClient only passes --model on a fresh spawn, so
            // reconnecting the EXISTING session can never apply a `model`
            // edit. Flagging it as reconnect-pending would be misleading
            // the same way agentModelOverrides already was (below).
            PanelSettings a = Make(model: "sonnet");
            PanelSettings b = Make(model: "opus");
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentSubagentModel_RequiresReconnect()
        {
            // docs/design-notes/2026-08-01-model-settings-rework.md section
            // 4.2: unlike model/agentModelOverrides, the measured
            // CLAUDE_CODE_SUBAGENT_MODEL env var is read fresh on every
            // spawn including --resume (R07 section 11, capture16 P2), so
            // this field genuinely IS reconnect-relevant.
            PanelSettings a = Make(subagentModel: "sonnet");
            PanelSettings b = Make(subagentModel: "haiku");
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void SubagentModelEmptyVsNonEmpty_RequiresReconnect()
        {
            PanelSettings a = Make(subagentModel: string.Empty);
            PanelSettings b = Make(subagentModel: "haiku");
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalSubagentModel_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(subagentModel: "haiku");
            PanelSettings b = Make(subagentModel: "haiku");
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        // -- claudeAuth (v0.40.0, docs/design-notes/2026-09-10-claude-api-
        // key-auth-passthrough.md): read fresh by ClaudeCliProcess.Start on
        // every spawn including --resume, exactly like subagentModel. -----

        [Test]
        public void DifferentClaudeAuth_RequiresReconnect()
        {
            PanelSettings a = Make(claudeAuth: ClaudeAuthMode.Auto);
            PanelSettings b = Make(claudeAuth: ClaudeAuthMode.SubscriptionOnly);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalClaudeAuth_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(claudeAuth: ClaudeAuthMode.SubscriptionOnly);
            PanelSettings b = Make(claudeAuth: ClaudeAuthMode.SubscriptionOnly);
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        // -- subagentCostPolicy (v0.11.0, docs/design-notes/2026-08-02-
        // subagent-model-precedence.md section 3.2): composed into the same
        // --append-system-prompt payload as custom instructions
        // (AgentHub.ComposeAppendSystemPrompt), so a changed policy is
        // exactly as reconnect-relevant as changed instructions text. -----

        [Test]
        public void DifferentSubagentCostPolicy_RequiresReconnect()
        {
            PanelSettings a = Make(subagentCostPolicy: SubagentCostPolicy.AgentDecides);
            PanelSettings b = Make(subagentCostPolicy: SubagentCostPolicy.HaikuForSimpleTasks);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalSubagentCostPolicy_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(subagentCostPolicy: SubagentCostPolicy.HaikuForSimpleTasks);
            PanelSettings b = Make(subagentCostPolicy: SubagentCostPolicy.HaikuForSimpleTasks);
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentDangerousFlag_RequiresReconnect()
        {
            PanelSettings a = Make(dangerous: false);
            PanelSettings b = Make(dangerous: true);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentShowThinking_RequiresReconnect()
        {
            // docs/design-notes/2026-08-01-thinking-content-loss.md section 7:
            // showThinking now drives --thinking-display summarized at spawn.
            PanelSettings a = Make(showThinking: true);
            PanelSettings b = Make(showThinking: false);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        // -- uapOpsEnabled (Phase 5a, docs/design-notes/2026-08-01-phase5-
        // unity-ops-design.md section 1.1/8.6): controls whether `--mcp-
        // config`/`--strict-mcp-config` are passed at all -- a spawn
        // argument exactly like dangerouslySkipPermissions above. ---------

        [Test]
        public void DifferentUapOpsEnabled_RequiresReconnect()
        {
            PanelSettings a = Make(uapOpsEnabled: true);
            PanelSettings b = Make(uapOpsEnabled: false);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalUapOpsEnabled_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(uapOpsEnabled: false);
            PanelSettings b = Make(uapOpsEnabled: false);
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        // -- uapOpsModules (2026-08-02 review fix, Stream C1 regression):
        // a module list change reaches the live tool catalog via
        // mcp_reconnect (UapOpsServer.SetEnabledModules), never a full CLI
        // respawn -- but Stream C1 also made this SAME list drive
        // AgentHub.ComposeUapOpsSteeringSection, baked into the once-per-
        // spawn --append-system-prompt payload with no live update path.
        // Without this comparison, a module edit could silently leave the
        // steering text advertising a stale family list for the rest of
        // the session even though the tool catalog itself had already
        // moved on -- see PanelSettings.uapOpsModules' own doc comment for
        // the full two-mechanism picture. This REPLACES the previous
        // DifferentUapOpsModules_DoesNotRequireReconnect regression pin,
        // which asserted the buggy behavior. ------------------------------

        [Test]
        public void DifferentUapOpsModules_RequiresReconnect()
        {
            PanelSettings a = Make();
            a.uapOpsModules = new List<string> { "core" };
            PanelSettings b = Make();
            b.uapOpsModules = new List<string> { "core", "prefab" };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalUapOpsModules_DoesNotRequireReconnect()
        {
            PanelSettings a = Make();
            a.uapOpsModules = new List<string> { "core", "prefab", "editor" };
            PanelSettings b = Make();
            b.uapOpsModules = new List<string> { "core", "prefab", "editor" };
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void RemovingAUapOpsModule_RequiresReconnect()
        {
            PanelSettings a = Make();
            a.uapOpsModules = new List<string> { "core", "prefab", "editor", "anim" };
            PanelSettings b = Make();
            b.uapOpsModules = new List<string> { "core", "prefab", "editor" };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        // -- uapScriptGateEnabled (design section 8.7 revision): controls
        // whether `--settings <path>` (the PreToolUse hook) is passed at
        // all -- a spawn argument exactly like uapOpsEnabled above, since
        // section 8.7 added the hook half of the gate. -----------------------

        [Test]
        public void DifferentUapScriptGateEnabled_RequiresReconnect()
        {
            PanelSettings a = Make(uapScriptGateEnabled: true);
            PanelSettings b = Make(uapScriptGateEnabled: false);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalUapScriptGateEnabled_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(uapScriptGateEnabled: false);
            PanelSettings b = Make(uapScriptGateEnabled: false);
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        // -- extensionProfilesEnabled/approvedProfileHashes (Phase 5b
        // stream C, design section 3b/8.2 B3): both feed the Extension
        // Profiles section of --append-system-prompt, a spawn argument
        // exactly like uapOpsEnabled/allowedTools above. -----------------

        [Test]
        public void DifferentExtensionProfilesEnabled_RequiresReconnect()
        {
            PanelSettings a = Make(extensionProfilesEnabled: true);
            PanelSettings b = Make(extensionProfilesEnabled: false);
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalExtensionProfilesEnabled_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(extensionProfilesEnabled: false);
            PanelSettings b = Make(extensionProfilesEnabled: false);
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentApprovedProfileHashes_RequiresReconnect()
        {
            PanelSettings a = Make(approvedProfileHashes: new List<string> { "hash-a" });
            PanelSettings b = Make(approvedProfileHashes: new List<string> { "hash-b" });
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void AddingApprovedProfileHash_RequiresReconnect()
        {
            PanelSettings a = Make(approvedProfileHashes: new List<string>());
            PanelSettings b = Make(approvedProfileHashes: new List<string> { "hash-a" });
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void IdenticalApprovedProfileHashes_DoesNotRequireReconnect()
        {
            PanelSettings a = Make(approvedProfileHashes: new List<string> { "hash-a", "hash-b" });
            PanelSettings b = Make(approvedProfileHashes: new List<string> { "hash-a", "hash-b" });
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentAllowedToolsOrder_RequiresReconnect()
        {
            // Order matters (it becomes wire-argument order); a reorder
            // still counts as a pending change.
            PanelSettings a = Make(allowed: new[] { "Read", "Edit" });
            PanelSettings b = Make(allowed: new[] { "Edit", "Read" });
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void AddingAllowedTool_RequiresReconnect()
        {
            PanelSettings a = Make(allowed: new[] { "Read" });
            PanelSettings b = Make(allowed: new[] { "Read", "Edit" });
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void NullSnapshot_VsNonNull_RequiresReconnect()
        {
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(null, Make()));
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(Make(), null));
        }

        [Test]
        public void BothNull_DoesNotRequireReconnect()
        {
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(null, null));
        }

        // -- 4-arg overload: custom instructions (docs/design-notes/2026-08-01-
        // settings-enrichment.md #1) -----------------------------------------------------

        [Test]
        public void CustomInstructionsChanged_RequiresReconnect()
        {
            PanelSettings a = Make();
            PanelSettings b = Make();
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b, "old text", "new text"));
        }

        [Test]
        public void CustomInstructionsUnchanged_PanelSettingsUnchanged_DoesNotRequireReconnect()
        {
            PanelSettings a = Make("C:/claude.exe", "sonnet", false, allowed: new[] { "Read" });
            PanelSettings b = Make("C:/claude.exe", "sonnet", false, allowed: new[] { "Read" });
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b, "same text", "same text"));
        }

        [Test]
        public void CustomInstructionsNullVsEmpty_AreEquivalent()
        {
            PanelSettings a = Make();
            PanelSettings b = Make();
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b, null, string.Empty));
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b, string.Empty, null));
        }

        [Test]
        public void CustomInstructionsUnchanged_ButPanelSettingsChanged_StillRequiresReconnect()
        {
            // The 4-arg overload must still catch every case the 2-arg
            // overload already catches -- it only ADDS a comparison, never
            // narrows the existing one. Uses subagentModel (reconnect-
            // relevant) rather than model (deliberately NOT, since v0.9.0 --
            // see DifferentModel_DoesNotRequireReconnect above).
            PanelSettings a = Make(subagentModel: "sonnet");
            PanelSettings b = Make(subagentModel: "haiku");
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(a, b, "same", "same"));
        }

        // -- agentModelOverrides (v0.8.0, docs/design-notes/2026-08-01-
        // model-settings.md section 2) -- REGRESSION PIN, not a "still
        // works" guard: this field used to trip RequiresReconnect (see git
        // history), but docs/research/07-model-configuration.md section
        // 10.8 ("capture15") found the CLI snapshots `.claude/agents/*.md`
        // at SESSION CREATION, so reconnecting (--resume of an EXISTING
        // session id) can never apply a table change made afterward, no
        // matter how many times it reconnects -- only a session created
        // after AgentDefinitionFileWriter.Sync wrote the new table (i.e. a
        // brand-new session) sees it. Flagging an agentModelOverrides edit
        // as "reconnect-pending" is therefore actively misleading, so
        // SettingsChangeDetector must NEVER trip on this field again,
        // however it differs. -----------------------------------------------

        [Test]
        public void IdenticalAgentModelOverrides_DoNotRequireReconnect()
        {
            PanelSettings a = Make();
            a.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            };
            PanelSettings b = Make();
            b.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            };
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void AddingAnAgentModelOverride_DoesNotRequireReconnect()
        {
            // Regression pin (was RequiresReconnect before the capture15
            // correction -- see the section comment above).
            PanelSettings a = Make();
            a.agentModelOverrides = new List<AgentModelOverride>();
            PanelSettings b = Make();
            b.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            };
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentModelAliasForSameAgent_DoesNotRequireReconnect()
        {
            // Regression pin (was RequiresReconnect before the capture15
            // correction -- see the section comment above).
            PanelSettings a = Make();
            a.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            };
            PanelSettings b = Make();
            b.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "sonnet" }
            };
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void DifferentAgentModelOverrideOrder_DoesNotRequireReconnect()
        {
            // Regression pin (was RequiresReconnect before the capture15
            // correction -- see the section comment above; order used to
            // matter because it decided which entry wins when two rows
            // share an agent name, mirroring
            // DifferentAllowedToolsOrder_RequiresReconnect, but that
            // reasoning never made the field reconnect-relevant in the
            // first place).
            PanelSettings a = Make();
            a.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" },
                new AgentModelOverride { agentName = "Explore", modelAlias = "haiku" }
            };
            PanelSettings b = Make();
            b.agentModelOverrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "Explore", modelAlias = "haiku" },
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            };
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void BothEmptyAgentModelOverrideLists_DoNotRequireReconnect()
        {
            PanelSettings a = Make();
            a.agentModelOverrides = new List<AgentModelOverride>();
            PanelSettings b = Make();
            b.agentModelOverrides = new List<AgentModelOverride>();
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }

        [Test]
        public void LiveOnlyFields_NeverTripTheDetector()
        {
            // permissionMode/ctrlEnterToSend/preferCjkUiFont/fontSizePx all
            // apply immediately (set_permission_mode, or plain UI reads) --
            // SettingsChangeDetector must ignore them entirely, even when
            // they differ, because CloneNextSpawnOnlyFields never copies
            // them onto the snapshot object in the first place.
            var a = new PanelSettings
            {
                permissionMode = "default",
                ctrlEnterToSend = false,
                preferCjkUiFont = false,
                fontSizePx = 12
            };
            var b = new PanelSettings
            {
                permissionMode = "plan",
                ctrlEnterToSend = true,
                preferCjkUiFont = true,
                fontSizePx = 16
            };
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(a, b));
        }
    }
}
