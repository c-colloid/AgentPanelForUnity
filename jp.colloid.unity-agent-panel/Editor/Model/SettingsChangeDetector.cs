using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure classification of which PanelSettings fields only take effect
    /// at the next CLI spawn (ARCHITECTURE.md D2: cliManualPath,
    /// allowedTools, disallowedTools, dangerouslySkipPermissions and (since
    /// docs/design-notes/2026-08-01-thinking-content-loss.md section 7)
    /// showThinking all become command-line arguments read once in
    /// AgentClient.BuildArguments/Start; subagentModel -- see below --
    /// becomes an environment variable instead) versus fields that apply
    /// immediately (permissionMode via set_permission_mode,
    /// ctrlEnterToSend, preferCjkUiFont, fontSizePx -- none of those are
    /// spawn arguments). showThinking's VISIBILITY effect (hiding/showing
    /// existing thinking blocks) still applies immediately -- only the
    /// `--thinking-display summarized` argument that makes new thinking
    /// blocks carry real text needs a reconnect.
    ///
    /// Backs the "Reconnect now" hint in SettingsView: comparing the
    /// settings snapshot captured at the last successful spawn
    /// (AgentHub.LastSpawnedSettingsSnapshot, which only carries the
    /// fields this class compares -- everything else on that snapshot is
    /// left at PanelSettings defaults and must not be read for any other
    /// purpose) against the CURRENT settings tells the UI whether a
    /// pending edit is actually inert until the user reconnects. See
    /// docs/design-notes/2026-07-31-settings-propagation.md for the full
    /// matrix.
    ///
    /// NOT covered here: PanelSettings.model. Reversed from v0.8.0's
    /// original design (docs/design-notes/2026-08-01-model-settings-
    /// rework.md section 4.1): `model` now means "default model for NEW
    /// sessions only" -- AgentHub.StartClient only ever passes `--model`
    /// on a fresh spawn (ResumeSessionId == null), and a `--resume` never
    /// re-applies it (R07 section 3: an OMITTED `--model` keeps the
    /// session's own model; the field simply is not read on that path at
    /// all). Flagging a `model` edit as "reconnect-pending" would
    /// therefore be misleading the same way agentModelOverrides was
    /// (below) -- reconnecting the CURRENT session can never apply it.
    /// The live, session-scoped switch is AgentHub.SwitchSessionModel via
    /// the header model picker, which needs no reconnect at all.
    ///
    /// NOT covered here either: PanelSettings.agentModelOverrides. It was
    /// originally wired into this comparison (v0.8.0 initial design,
    /// docs/design-notes/2026-08-01-model-settings.md section 2), but a
    /// follow-up capture ("capture15", docs/research/
    /// 07-model-configuration.md section 10.8) found the CLI snapshots
    /// `.claude/agents/*.md` at SESSION CREATION -- a `--resume` of an
    /// EXISTING session id can never pick up a table change made after
    /// that session was created, no matter how many times it reconnects.
    /// Only a session created after AgentDefinitionFileWriter.Sync has
    /// written the current table (i.e. a brand-new session, started from
    /// the header "+") sees it. Treating an agentModelOverrides edit as
    /// "reconnect-pending" was therefore actively misleading -- reconnect
    /// can never resolve it -- so it was removed from this comparison;
    /// SettingsView instead shows a dedicated always-on hint under the
    /// override list pointing at starting a new session.
    ///
    /// ADDED here (v0.9.0): PanelSettings.subagentModel. Unlike
    /// agentModelOverrides/model above, the measured
    /// CLAUDE_CODE_SUBAGENT_MODEL environment variable is read fresh at
    /// EVERY process start, including a `--resume` spawn (R07 section 11,
    /// capture16 P2) -- so, unlike those two, this field genuinely IS
    /// reconnect-relevant and plugs into the existing settings auto-apply
    /// machinery (AgentHub.RequestAutoApplyReconnect) the same way
    /// allowedTools/dangerouslySkipPermissions already do.
    ///
    /// ADDED here (v0.11.0, docs/design-notes/2026-08-02-subagent-model-
    /// precedence.md section 3.2): PanelSettings.subagentCostPolicy. It is
    /// composed into the SAME AppendSystemPrompt payload as the custom-
    /// instructions sidecar text (AgentHub.ComposeAppendSystemPrompt), so a
    /// changed policy is exactly as reconnect-relevant as a changed
    /// instructions text -- but since this field lives directly on
    /// PanelSettings (unlike the sidecar text, which needs the 4-arg
    /// overload below), it is compared right here in the 2-arg overload.
    ///
    /// ADDED here (Phase 5a, docs/design-notes/2026-08-01-phase5-unity-ops-
    /// design.md section 1.1/8.6): PanelSettings.uapOpsEnabled. It controls
    /// whether `--mcp-config`/`--strict-mcp-config` are passed at all
    /// (AgentClientOptions.McpConfigJson), a spawn argument exactly like
    /// allowedTools above.
    ///
    /// ADDED here (2026-08-02 review fix, Stream C1 regression):
    /// PanelSettings.uapOpsModules. Originally excluded from this
    /// comparison -- a module list edit reaches the live tool catalog via
    /// UapOpsServer.SetEnabledModules + mcp_reconnect, never a reconnect
    /// (AgentHub.ApplyUapOpsModulesChanged), so comparing it here was
    /// pointless at the time. Stream C1 then made the SAME list also drive
    /// AgentHub.ComposeUapOpsSteeringSection, baked into the once-per-spawn
    /// --append-system-prompt payload with no live update path -- so a
    /// module edit could silently leave the steering text advertising a
    /// stale family list for the rest of the session even though the tool
    /// catalog itself had already moved on. uapOpsModules is therefore now
    /// compared here too (see PanelSettings.uapOpsModules's own doc comment
    /// for the full two-mechanism picture); AgentHub.
    /// CloneNextSpawnOnlyFields was updated in the same fix to actually
    /// clone this list onto the last-spawned snapshot, or this comparison
    /// would always run against the class's default module list instead of
    /// what was truly spawned with.
    ///
    /// ADDED here (section 8.7 revision): PanelSettings.uapScriptGateEnabled.
    /// Before section 8.7 this field was purely live (read fresh on every
    /// can_use_tool) and deliberately excluded from this comparison -- see
    /// its own doc comment for why that changed: the hook half of the gate
    /// is wired in via a next-spawn-only `--settings` argument
    /// (AgentClientOptions.SettingsFilePath), exactly like uapOpsEnabled's
    /// `--mcp-config` above. (On a non-Windows platform this field has no
    /// actual spawn-argument effect at all -- AgentHub.
    /// ShouldInstallScriptGateHook gates the installer on
    /// RuntimePlatform.WindowsEditor -- so flagging a toggle there as
    /// "reconnect-pending" is a harmless over-approximation, not a
    /// correctness issue: this class has no platform parameter to know
    /// better, and the can_use_tool layer it also controls needs no
    /// reconnect either way.)
    ///
    /// ADDED here (Phase 5b stream C, docs/design-notes/2026-08-01-phase5-
    /// unity-ops-design.md section 3b/8.2 B3): PanelSettings.
    /// extensionProfilesEnabled and PanelSettings.approvedProfileHashes.
    /// Both feed the Extension Profiles section of --append-system-prompt
    /// (Colloid.AgentPanel.Ops.Profiles.ExtensionProfileService.
    /// ComposeAppendSection, composed into AgentHub.
    /// ComposeAppendSystemPrompt's 3-arg overload) -- a spawn argument
    /// exactly like uapOpsEnabled/allowedTools above, so both participate
    /// in this comparison (and therefore in RequestAutoApplyReconnect's
    /// debounced auto-apply) "like custom instructions": flipping the
    /// toggle or approving/revoking a user profile is inert until the next
    /// spawn, the same way an edited custom-instructions text is.
    ///
    /// ADDED here (v0.40.0, docs/design-notes/2026-09-10-claude-api-key-
    /// auth-passthrough.md): PanelSettings.claudeAuth. It decides which
    /// environment variable names ClaudeCliProcess.Start removes
    /// (ComputeEnvVarsToRemove), read fresh at EVERY process start
    /// including a --resume spawn -- exactly like subagentModel above, so
    /// it is reconnect-relevant the same way.
    /// </summary>
    public static class SettingsChangeDetector
    {
        /// <summary>
        /// True when any next-spawn-only field differs between the two
        /// snapshots. Either argument may be null (no successful spawn
        /// yet); null vs non-null always counts as a difference so a
        /// not-yet-connected panel never claims to be "up to date", and
        /// null vs null counts as no difference (nothing has ever been
        /// spawned and nothing is pending either). Deliberately does NOT
        /// compare model or agentModelOverrides -- see the class doc
        /// comment (reconnecting an existing session can never apply a
        /// change to either field, so flagging either as pending would be
        /// misleading).
        /// </summary>
        public static bool RequiresReconnect(PanelSettings spawnedWith, PanelSettings current)
        {
            if (spawnedWith == null || current == null)
            {
                return spawnedWith != current;
            }
            return !string.Equals(spawnedWith.cliManualPath, current.cliManualPath, StringComparison.Ordinal)
                || spawnedWith.agentBackend != current.agentBackend
                || !string.Equals(spawnedWith.acpCommand ?? string.Empty, current.acpCommand ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(spawnedWith.acpArguments ?? string.Empty, current.acpArguments ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(spawnedWith.acpAuthMethod ?? string.Empty, current.acpAuthMethod ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(spawnedWith.subagentModel, current.subagentModel, StringComparison.Ordinal)
                || spawnedWith.claudeAuth != current.claudeAuth
                || spawnedWith.subagentCostPolicy != current.subagentCostPolicy
                || spawnedWith.dangerouslySkipPermissions != current.dangerouslySkipPermissions
                || spawnedWith.showThinking != current.showThinking
                || spawnedWith.uapOpsEnabled != current.uapOpsEnabled
                || spawnedWith.uapScriptGateEnabled != current.uapScriptGateEnabled
                || spawnedWith.extensionProfilesEnabled != current.extensionProfilesEnabled
                || spawnedWith.unityPluginSteeringEnabled != current.unityPluginSteeringEnabled
                || !StringListsEqual(spawnedWith.allowedTools, current.allowedTools)
                || !StringListsEqual(spawnedWith.disallowedTools, current.disallowedTools)
                || !StringListsEqual(spawnedWith.approvedProfileHashes, current.approvedProfileHashes)
                || !StringListsEqual(spawnedWith.uapOpsModules, current.uapOpsModules);
        }

        /// <summary>
        /// Overload additionally covering the custom-instructions sidecar
        /// text (AgentClientOptions.AppendSystemPrompt, docs/design-notes/
        /// 2026-08-01-settings-enrichment.md #1). That text lives OUTSIDE
        /// PanelSettings (CustomInstructionsFile, never UnityYAML -- see
        /// that class's doc comment), so the PanelSettings-only overload
        /// above cannot see it; SettingsView passes the last-spawned and
        /// current sidecar text through here explicitly. Null and empty
        /// are treated as equivalent ("no custom instructions") on both
        /// sides.
        /// </summary>
        public static bool RequiresReconnect(PanelSettings spawnedWith, PanelSettings current,
            string spawnedCustomInstructions, string currentCustomInstructions)
        {
            if (RequiresReconnect(spawnedWith, current))
            {
                return true;
            }
            return !string.Equals(spawnedCustomInstructions ?? string.Empty,
                currentCustomInstructions ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool StringListsEqual(List<string> a, List<string> b)
        {
            int countA = a == null ? 0 : a.Count;
            int countB = b == null ? 0 : b.Count;
            if (countA != countB)
            {
                return false;
            }
            for (int i = 0; i < countA; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
