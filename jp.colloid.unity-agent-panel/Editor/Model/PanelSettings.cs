using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Process;
using UnityEngine;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Serializable panel settings (persisted project-wide inside
    /// PanelStateStore; a few machine-wide values are mirrored to
    /// EditorPrefs by the settings UI in a later phase).
    /// </summary>
    [Serializable]
    public class PanelSettings
    {
        /// <summary>Manual CLI executable path (escape hatch, D1 step 1). Empty = auto-detect.</summary>
        public string cliManualPath = string.Empty;

        /// <summary>
        /// Claude Code CLI only: whether ANTHROPIC_API_KEY is removed from
        /// the spawned CLI's environment (docs/design-notes/2026-09-10-
        /// claude-api-key-auth-passthrough.md). Auto (DEFAULT, v0.40.0+)
        /// leaves it untouched -- the CLI picks auth exactly as it would in
        /// a terminal, per Anthropic's Claude Code legal terms requiring
        /// that a product running the CLI not remove/disable/restrict any
        /// of its built-in authentication methods. SubscriptionOnly is the
        /// panel's previous unconditional behaviour, now an explicit
        /// opt-in: it strips the variable so the stored subscription login
        /// is always used. Read by ClaudeCliProcess.ComputeEnvVarsToRemove;
        /// ignored entirely for ACP backends (see ICliTransport.Start's doc
        /// comment). Next-spawn-only -- SettingsChangeDetector.
        /// RequiresReconnect flags a change so it reaches the current
        /// session via the existing auto-apply-reconnect machinery.
        /// </summary>
        public ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto;

        /// <summary>
        /// Which agent CLI the panel drives (design note
        /// docs/design-notes/2026-09-10-acp-backends.md section 2). Claude
        /// Code is the default and the only backend the native stream-json
        /// client talks to directly; every other value rides the ACP bridge
        /// (AcpBridgeTransport). Next-spawn-only.
        /// </summary>
        public AgentBackend agentBackend = AgentBackend.ClaudeCode;

        /// <summary>
        /// ACP backends: the launch command (absolute path, or a bare name
        /// looked up on PATH). Empty = the backend's default
        /// (AgentBackends.DefaultCommand). Required for AcpCustom.
        /// </summary>
        public string acpCommand = string.Empty;

        /// <summary>
        /// ACP backends: the argument string. Empty = the backend default
        /// (Gemini CLI's `--experimental-acp`) unless the command itself was
        /// overridden -- see AgentBackends.EffectiveArguments.
        /// </summary>
        public string acpArguments = string.Empty;

        /// <summary>
        /// ACP backends: preferred `authenticate` method id when the agent
        /// asks for authentication (e.g. Gemini CLI "oauth-personal").
        /// Empty = the first method the agent advertises.
        /// </summary>
        public string acpAuthMethod = string.Empty;

        /// <summary>Model alias or full name for --model. Empty = CLI default.</summary>
        public string model = string.Empty;

        /// <summary>--permission-mode value (default/plan/acceptEdits/...).</summary>
        public string permissionMode = "default";

        /// <summary>When true, Enter inserts a newline and Ctrl+Enter sends (IME escape hatch).</summary>
        public bool ctrlEnterToSend;

        /// <summary>--allowedTools static list (Phase 2 settings UI).</summary>
        public List<string> allowedTools = new List<string>();

        /// <summary>--disallowedTools static list (Phase 2 settings UI).</summary>
        public List<string> disallowedTools = new List<string>();

        /// <summary>
        /// uap_web_fetch host allow list, one host per line ("example.com"
        /// covers its subdomains). Empty = any public host. Design note
        /// 2026-09-17-web-fetch-tool.md section 5.2 (stage 2).
        /// </summary>
        public List<string> webFetchAllowedHosts = new List<string>();

        /// <summary>uap_web_fetch host deny list, same format; deny wins over allow.</summary>
        public List<string> webFetchBlockedHosts = new List<string>();

        /// <summary>
        /// Explicit opt-in for --dangerously-skip-permissions. Default OFF
        /// and it must stay that way (ARCHITECTURE.md D3): the settings UI
        /// shows a warning before enabling.
        /// </summary>
        public bool dangerouslySkipPermissions;

        /// <summary>
        /// When true and FontLoader.JapaneseUiFont resolves a font on this
        /// machine, AgentPanelWindow/PermissionWindow assign it at their
        /// content root so it inherits across all body text -- the fix for
        /// the patchy-bold/faint Japanese glyph live feedback (per-glyph OS
        /// fallback mixing fonts/weights because the editor's default UI
        /// font, Inter, has no CJK coverage). Mono text (RobotoMono via
        /// MessageBlockFactory.ApplyMonoFont) is unaffected: it is set as
        /// an INLINE style on the leaf element, which always outranks an
        /// inherited value regardless of apply order.
        ///
        /// Defaults on for CJK system languages (see
        /// ComputeDefaultPreferCjkUiFont) and off otherwise; a settings UI
        /// toggle can still flip it either way, and that choice persists
        /// via PanelStateStore like every other field here.
        ///
        /// IMPORTANT: the default is NOT a field initializer.
        /// Application.systemLanguage is forbidden inside a
        /// ScriptableObject constructor / instance field initializer
        /// (UnityException during ScriptableSingleton load -- live
        /// regression 2026-07-31 broke the whole window). The default is
        /// applied exactly once in PanelStateStore.OnEnable via
        /// EnsureCjkUiFontDefault, gated by cjkUiFontDecided so a user
        /// choice is never overwritten.
        /// </summary>
        public bool preferCjkUiFont;

        /// <summary>
        /// True once preferCjkUiFont has been decided (by the one-time
        /// system-language default or by an explicit user choice); keeps
        /// the OnEnable default from clobbering a persisted user choice.
        /// </summary>
        public bool cjkUiFontDecided;

        /// <summary>
        /// Body font size in pixels, applied to each window's conversation-
        /// content root ONLY -- AgentPanelWindow's chat root and
        /// PermissionWindow's content root, NOT the shared window root the
        /// CJK font/theme go on (see
        /// docs/design-notes/2026-08-01-fontscale-scope.md: applying it at
        /// the window root used to rescale Settings/History and the window
        /// chrome too, breaking slider dragging by shifting the very
        /// control under the cursor) -- via a theme-independent
        /// `uap-fontscale-N` class rather than an inline style -- see
        /// docs/design-notes/2026-07-31-font-size-application.md for why a
        /// naive inline `style.fontSize` does NOT cascade here (every text
        /// class in AgentPanel.uss already sets its own font-size; only the
        /// underlying `--uap-font-size-*` CUSTOM PROPERTIES those classes
        /// read via var() can be overridden from a single class higher up
        /// the tree). Always clamp through ClampFontSize before assigning
        /// -- the Settings slider and the deserializer (a hand edited or
        /// future-version State.asset) are equally untrusted.
        /// </summary>
        public int fontSizePx = DefaultFontSizePx;

        /// <summary>Default body font size (matches the pre-feature hardcoded 12px).</summary>
        public const int DefaultFontSizePx = 12;

        /// <summary>Minimum selectable font size (R05 5.3 body baseline is 12; 11 is the smallest still legible step).</summary>
        public const int MinFontSizePx = 11;

        /// <summary>
        /// Maximum selectable font size. UXIA-L5 raised this from 16 to 20:
        /// 16 left users needing larger text (accessibility) with nowhere
        /// to go, and visual checks up to 20 keep the 4px spacing grid
        /// workable -- rows grow but nothing clips; beyond 20 the fixed
        /// paddings start truncating, so that is the measured stopping
        /// point. Every step in Min..Max MUST have a matching
        /// .uap-fontscale-N class in FontScale.uss (ApplyFontScale is a
        /// class lookup, so a missing step is a silent no-op) --
        /// FontScaleRangeTests enforces that pairing.
        /// </summary>
        public const int MaxFontSizePx = 20;

        /// <summary>
        /// Pure clamp used by both the Settings slider (live) and load-time
        /// validation (a corrupted or hand-edited State.asset must never
        /// hand an out-of-range value to the `uap-fontscale-N` class lookup,
        /// which only defines classes for the in-range steps).
        /// </summary>
        public static int ClampFontSize(int px)
        {
            if (px < MinFontSizePx)
            {
                return MinFontSizePx;
            }
            if (px > MaxFontSizePx)
            {
                return MaxFontSizePx;
            }
            return px;
        }

        /// <summary>
        /// Applies the one-time system-language default. Safe to call from
        /// OnEnable (never from a constructor). Returns true when it
        /// changed anything (caller decides whether/when to persist).
        /// </summary>
        public bool EnsureCjkUiFontDefault(SystemLanguage language)
        {
            if (cjkUiFontDecided)
            {
                return false;
            }
            preferCjkUiFont = ComputeDefaultPreferCjkUiFont(language);
            cjkUiFontDecided = true;
            return true;
        }

        /// <summary>
        /// Module-default generation this build ships. 1 = the Phase 5b
        /// default-on set {core, prefab, editor}; 2 = "markers" (2026-09-07
        /// Scene-view 3D markers) joined the default-on set; 3 = "web"
        /// (2026-09-17 uap_web_fetch) joined it. Increment (and extend
        /// <see cref="EnsureUapOpsModuleDefaults"/>) only when a module is
        /// added to the DEFAULT-ON set; adding a default-OFF module (like
        /// "anim") needs no migration.
        /// </summary>
        public const int CurrentModuleDefaultsGeneration = 3;

        /// <summary>
        /// Adds modules that became default-ON after this asset was last
        /// written, then records the generation so it runs exactly once.
        /// Deliberately ADDITIVE and generation-gated rather than a union
        /// with the current defaults on every load: a user who switched a
        /// module OFF must stay switched off, which a plain union would
        /// silently undo on every editor start. Returns true when it
        /// changed anything (caller decides whether/when to persist), same
        /// contract as <see cref="EnsureCjkUiFontDefault"/>.
        /// </summary>
        public bool EnsureUapOpsModuleDefaults()
        {
            if (uapOpsModuleDefaultsGeneration >= CurrentModuleDefaultsGeneration)
            {
                return false;
            }
            if (uapOpsModules == null)
            {
                uapOpsModules = new List<string>();
            }
            // Generation 1: prefab + editor joined core as default-on.
            // Gated on the asset's own generation so a later migration
            // never re-adds what a generation-1 user switched off.
            if (uapOpsModuleDefaultsGeneration < 1)
            {
                string[] generation1 = { "core", "prefab", "editor" };
                for (int i = 0; i < generation1.Length; i++)
                {
                    if (!uapOpsModules.Contains(generation1[i]))
                    {
                        uapOpsModules.Add(generation1[i]);
                    }
                }
            }
            // Generation 2: Scene-view markers (2026-09-07). Same additive
            // rule: a user who switched it off after this ran stays off.
            if (uapOpsModuleDefaultsGeneration < 2 && !uapOpsModules.Contains("markers"))
            {
                uapOpsModules.Add("markers");
            }
            // Generation 3: uap_web_fetch (2026-09-17, design note
            // docs/design-notes/2026-09-17-web-fetch-tool.md). Default ON
            // because every call still shows a permission card with the
            // URL; a user who switches it off after this stays off.
            if (uapOpsModuleDefaultsGeneration < 3 && !uapOpsModules.Contains("web"))
            {
                uapOpsModules.Add("web");
            }
            uapOpsModuleDefaultsGeneration = CurrentModuleDefaultsGeneration;
            // Unconditionally true past the generation gate above, even when
            // the module list itself needed no additions: stamping
            // uapOpsModuleDefaultsGeneration IS a change, and it is the one
            // that MUST reach disk. Returning "did the list change?" instead
            // (an earlier draft kept a `changed` flag for exactly that, which
            // is why CS0219 flagged it as unused) would leave the stamp
            // unpersisted whenever an asset already had every default module
            // -- so the migration would re-run on the next editor start, and
            // that later run WOULD re-add any module the user had switched
            // off in between. The generation gate only protects the user's
            // choice if the generation is actually saved.
            return true;
        }

        /// <summary>
        /// Pure decision function behind the preferCjkUiFont default,
        /// parameterized on SystemLanguage so tests can exercise every
        /// branch without reading (or being able to mutate)
        /// Application.systemLanguage.
        /// </summary>
        public static bool ComputeDefaultPreferCjkUiFont(SystemLanguage language)
        {
            return language == SystemLanguage.Japanese
                || language == SystemLanguage.Chinese
                || language == SystemLanguage.ChineseSimplified
                || language == SystemLanguage.ChineseTraditional
                || language == SystemLanguage.Korean;
        }

        // -- Settings enrichment (docs/design-notes/2026-08-01-settings-
        // enrichment.md #2/#4): plain bool toggles only -- YAML-safe, unlike
        // the custom-instructions text and quick-action prompts, which live
        // in their own sidecar files (CustomInstructionsFile/QuickActionStore)
        // for the same reason the transcript cache does not live here (see
        // SessionCacheFile's class doc comment).

        /// <summary>
        /// Show Thinking blocks in the transcript. Default ON
        /// (MessageBlockFactory skips them when off). Visibility of
        /// already-received blocks applies immediately, but ALSO drives
        /// the next-spawn-only `--thinking-display summarized` CLI
        /// argument (AgentClientOptions.ThinkingDisplaySummarized,
        /// AgentHub.StartClient) -- the only verified way to make the CLI
        /// send real thinking text instead of an empty string (docs/
        /// design-notes/2026-08-01-thinking-content-loss.md section 7).
        /// Reconnect-relevant: see SettingsChangeDetector.RequiresReconnect.
        /// </summary>
        public bool showThinking = true;

        /// <summary>
        /// Subagent cards start expanded when no per-card memory exists yet
        /// (SubagentCard.ExpandedByToolUseId still takes priority over this
        /// default once a card has been manually toggled). Default OFF
        /// (collapsed), matching the pre-existing behavior.
        /// </summary>
        public bool subagentDefaultExpanded;

        /// <summary>
        /// Show the dollar cost alongside token counts in the status bar
        /// and the usage popover. Default ON; OFF is useful on subscription
        /// auth, where the CLI-reported cost is not a meaningful number
        /// (R05).
        /// </summary>
        public bool showCostUsd = true;

        /// <summary>EditorApplication.Beep() when a permission request arrives while the panel is not focused. Default OFF.</summary>
        public bool permissionBeep;

        /// <summary>EditorApplication.Beep() when a turn completes while the panel is not focused. Default OFF.</summary>
        public bool turnCompleteBeep;

        // -- i18n (v0.5.0, docs/design-notes/2026-08-01-i18n.md) -----------

        /// <summary>
        /// UI display language. Default Auto. A plain enum field (YAML-safe,
        /// serializes as int) is enough -- unlike preferCjkUiFont there is
        /// no "decided" flag needed, since Auto is a legitimate persistent
        /// user choice, not a one-time default that must never be
        /// clobbered. Resolving Auto against Application.systemLanguage
        /// happens LAZILY in Colloid.AgentPanel.UI.L10n, never here or
        /// in any ScriptableObject ctor/field initializer -- see
        /// PanelLanguage's doc comment for why that specific mistake broke
        /// the whole window before (f445c22).
        /// </summary>
        public PanelLanguage language = PanelLanguage.Auto;

        // -- Model settings (v0.8.0, docs/design-notes/2026-08-01-model-
        // settings.md) -----------------------------------------------------

        /// <summary>
        /// Lightweight cache of the CLI's model catalog (system/init
        /// "initialize" control_response models[]), refreshed whenever a
        /// live connection actually reports one (AgentHub.
        /// OnInitMessageReceived) -- see ModelCatalogEntry's doc comment
        /// for why only value/displayName are kept. Lets the Settings
        /// "Default model" dropdown (and the per-agent override rows) keep
        /// working across an editor restart, before this session's own
        /// connection has re-supplied the live list; empty until the very
        /// first connection this project has ever made.
        /// </summary>
        public List<ModelCatalogEntry> modelCatalog = new List<ModelCatalogEntry>();

        /// <summary>
        /// Subagent name -&gt; model alias overrides (Settings "Model"
        /// section detail table). Materialized as one `.claude/agents/
        /// &lt;name&gt;.md` file per entry at spawn time when non-empty
        /// (Colloid.AgentPanel.Model.AgentDefinitionFileWriter.Sync,
        /// called from AgentHub.StartClient -- NOT a --agents CLI argument;
        /// R07 section 10 found that flag silently ignored whenever
        /// --resume is also passed); an agent name absent from this list
        /// always inherits the Default model above (model field, R07
        /// section 6). This list -- not anything reported by the CLI -- is
        /// the sole source of truth for which overrides are active (R07
        /// section 5/9.2: the CLI gives no reliable way to detect one after
        /// the fact).
        ///
        /// Effective timing (R07 section 10.8, "capture15"): the CLI
        /// snapshots `.claude/agents/*.md` at SESSION CREATION, so a
        /// change here only ever reaches a session created AFTER Sync next
        /// runs -- reconnecting the CURRENT session (`--resume` of its
        /// existing session id) can never pick it up, no matter how many
        /// times that happens. SettingsChangeDetector deliberately excludes
        /// this field from its reconnect-pending comparison for that
        /// reason; the Settings UI's own hint under the row list says
        /// "new sessions only", not "reconnect".
        /// </summary>
        public List<AgentModelOverride> agentModelOverrides = new List<AgentModelOverride>();

        // -- Model settings rework (v0.9.0, docs/design-notes/2026-08-01-
        // model-settings-rework.md) -----------------------------------------

        /// <summary>
        /// Blanket subagent model alias, applied via the
        /// CLAUDE_CODE_SUBAGENT_MODEL environment variable on every spawn
        /// (AgentClientOptions.SubagentModel, ClaudeCliProcess -- see
        /// docs/research/07-model-configuration.md section 11). Empty means
        /// "inherit" -- the variable is left completely untouched on the
        /// child process environment rather than force-cleared, so an
        /// operator-set machine-wide value is never silently removed.
        /// Unlike agentModelOverrides (snapshot-at-session-creation, R07
        /// section 10.8), the measured env var takes effect on a
        /// `--resume` spawn using its CURRENT value (R07 section 11, P2),
        /// so this field IS reconnect-relevant
        /// (SettingsChangeDetector.RequiresReconnect) and participates in
        /// the existing settings auto-apply machinery
        /// (AgentHub.RequestAutoApplyReconnect) -- changing it reaches the
        /// CURRENT session within seconds, no new-session caveat needed.
        /// Takes precedence over agentModelOverrides when both are set
        /// (R07 section 11, P3): SettingsView surfaces that as a warning
        /// rather than silently ignoring the now-inert per-type table.
        /// </summary>
        public string subagentModel = string.Empty;

        /// <summary>
        /// Cache of the CLI's self-reported subagent type names
        /// (system/init.agents[], e.g. "general-purpose", "Explore") --
        /// refreshed alongside modelCatalog on any live connection
        /// (AgentHub.RefreshAgentTypeCatalogCache). Feeds the per-type
        /// override rows' agent-name PopupField (SettingsView) so the user
        /// never has to free-type a magic type string; survives an editor
        /// restart the same way modelCatalog does, before this session's
        /// own connection has re-supplied the live list. Empty until the
        /// very first connection this project has ever made.
        /// </summary>
        public List<string> agentTypeCatalog = new List<string>();

        /// <summary>
        /// Cache of the CLI's slash-command catalog (initialize
        /// control_response commands[] -- name/description/argumentHint --
        /// merged with system/init slash_commands[] names), refreshed by
        /// AgentHub.RefreshSlashCommandCatalogCache on every live
        /// connection. Feeds the composer's "/" suggestion popup so it
        /// works right after an editor restart or domain reload, before
        /// this session's own connection has re-supplied the list (design
        /// note docs/design-notes/2026-09-07-slash-commands-and-
        /// compaction.md section 1.2). Empty until the very first
        /// connection; the popup then still offers /compact and /clear
        /// (SlashCommandCatalog.WithBuiltins).
        /// </summary>
        public List<SlashCommandEntry> slashCommandCatalog = new List<SlashCommandEntry>();

        // -- Subagent model precedence rework (v0.11.0, docs/design-notes/
        // 2026-08-02-subagent-model-precedence.md) --------------------------

        /// <summary>
        /// PRIMARY subagent-cost control (section 3.2): AgentDecides (the
        /// default) injects nothing extra into the spawn's append-system-
        /// prompt; HaikuForSimpleTasks appends one guidance line
        /// (AgentHub.ComposeAppendSystemPrompt) steering the agent's own
        /// per-call Task/Agent `model` parameter choice (R07 section 12,
        /// P4/P5) toward haiku for simple mechanical subtasks, leaving
        /// complex work to inherit the session model. Soft by design -- the
        /// agent can still up-model a task it judges complex, unlike
        /// subagentModel below which is a hard clamp.
        ///
        /// Composed into the SAME AppendSystemPrompt payload as the custom-
        /// instructions sidecar text (CustomInstructionsFile), joined after
        /// it with a blank line -- so this field participates in
        /// SettingsChangeDetector's reconnect-relevant comparison the same
        /// way that text does (see that class's doc comment), even though
        /// (unlike the sidecar text) it lives directly on PanelSettings and
        /// therefore only needs the 2-arg RequiresReconnect overload to
        /// cover it.
        /// </summary>
        public SubagentCostPolicy subagentCostPolicy = SubagentCostPolicy.AgentDecides;

        // -- UapOps (Phase 5a, docs/design-notes/2026-08-01-phase5-unity-
        // ops-design.md section 1/8.6) --------------------------------------

        /// <summary>
        /// Master toggle for the in-panel UapOps MCP server (design section
        /// 1.1/8.6 ADR: L1 keeps MCP). Default ON. Next-spawn-only: flipping
        /// this changes whether `--mcp-config`/`--strict-mcp-config` are
        /// passed at all (AgentHub.StartClient), so it participates in
        /// SettingsChangeDetector.RequiresReconnect exactly like
        /// allowedTools/dangerouslySkipPermissions. Turning it OFF also
        /// stops the resident HttpListener (Colloid.AgentPanel.Ops.
        /// UapOpsServer.Stop) -- an explicitly disabled feature must not
        /// leave a loopback socket open.
        /// </summary>
        public bool uapOpsEnabled = true;

        /// <summary>
        /// Enabled UapOps tool modules (design section 7.1: "tools/list
        /// returns only enabled modules"). Phase 5a shipped one module
        /// ("core", containing uap_ping and the rest of the core scene/
        /// component/property/asset/query tools); Phase 5b stream A adds
        /// "prefab" (design section 7.3 Revert/Apply family) and "editor"
        /// (design section 3c T2/1.2: screenshot + menu execution),
        /// defaulting BOTH on alongside "core" per the kickoff scope ("new
        /// module ... default ON -- add to the module defaults where core
        /// is"). Phase 5b stream B adds "anim" (uap_anim_create_clip/
        /// uap_animator_edit/uap_material_set/uap_asset_set_property/
        /// uap_avatar_configure) -- deliberately NOT in this default list
        /// (design section 8.8: "anim=OFF" by default; a settings toggle
        /// row lets the user opt in the same way the other module rows do).
        /// Phase 5c's "ui" (uap_editor_ui_*) and 2026-09-15's "authoring"
        /// (uap_profile_scaffold/uap_profile_validate, docs/design-notes/
        /// 2026-09-15-profile-authoring-tools.md), "avatar", "batch",
        /// "tests" (uap_test_run, docs/design-notes/
        /// 2026-09-15-test-run.md), "fx" (uap_particle_set,
        /// docs/design-notes/2026-09-15-particle-set.md) and "mesh"
        /// (uap_mesh_create, docs/design-notes/2026-09-15-mesh-create.md)
        /// default off for the same reason. Every module that defaults off ships its tools in Agent
        /// Panel Pro, so with Core alone its toggle row is disabled rather
        /// than merely unchecked. "tests" is additionally absent -- not just
        /// disabled -- in a project without com.unity.test-framework: the
        /// assembly holding uap_test_run is compiled out there, so the tool
        /// is never registered at all.
        /// Unlike uapOpsEnabled, a module list change is NOT
        /// next-spawn-only for TOOL AVAILABILITY: UapOpsServer.
        /// SetEnabledModules takes effect on the next tools/list call,
        /// reflected to an already-connected CLI via mcp_reconnect rather
        /// than a full respawn (design section 7.1, AgentHub.
        /// ApplyUapOpsModulesChanged).
        ///
        /// It IS, however, reconnect-relevant for a SECOND, independent
        /// reason (2026-08-02 review fix, Stream C1 regression): this same
        /// list also drives AgentHub.ComposeUapOpsSteeringSection, baked
        /// into the once-per-spawn --append-system-prompt payload. There is
        /// no live control_request that can update an already-sent
        /// --append-system-prompt (AgentClientOptions.AppendSystemPrompt's
        /// own doc comment), so without SettingsChangeDetector comparing
        /// this field, a module edit would leave the steering text -- which
        /// families of uap_* tools it advertises -- silently stale for the
        /// rest of the session even though the live tool catalog above had
        /// already moved on. SettingsChangeDetector.RequiresReconnect
        /// therefore DOES compare this field (see its class doc comment),
        /// and AgentHub.CloneNextSpawnOnlyFields clones it onto the
        /// last-spawned snapshot so that comparison is meaningful. The two
        /// mechanisms coexist deliberately: ApplyUapOpsModulesChanged keeps
        /// the tool catalog instantly correct via mcp_reconnect; the
        /// ordinary "Reconnect now"/auto-apply-reconnect path (SettingsView's
        /// module toggle handlers now call AgentHub.RequestAutoApplyReconnect
        /// too) eventually converges the steering text via a full respawn,
        /// exactly like any other spawn-argument field.
        /// </summary>
        public List<string> uapOpsModules = new List<string> { "core", "prefab", "editor", "markers", "web" };

        /// <summary>
        /// Phase 5c L3(3): after a turn's staged scripts compile and the
        /// domain reloads, automatically send a continuation turn so the
        /// agent can react to the compile result instead of the work simply
        /// stopping there.
        ///
        /// DEFAULT OFF, and it stays off unless the user asks for it. Every
        /// guardrail the design specified is a consequence of the same fact:
        /// this is the only feature in the panel that makes the agent act
        /// without a human sending anything. So it fires only when the
        /// reload is attributable to the agent's own .cs/.asmdef work,
        /// always leaves a visible system note (no silent background turn),
        /// and the continuation carries the compile result with it --
        /// resuming on the stale pre-compile assumption is the specific
        /// accident this exists to avoid. The original "at most ONCE per
        /// turn" guardrail (a continuation could not arm a second one) was
        /// removed on 2026-09-15 (docs/design-notes/2026-09-15-chained-auto-
        /// continue-after-compile.md): a continuation that commits again
        /// continues again, so a write-compile-fix loop runs unattended
        /// until the agent stops committing, the crash-loop guard suspends
        /// the connection, or the user presses Stop -- the same stops as
        /// <see cref="autoContinueInterruptedTurn"/>.
        ///
        /// Not part of SettingsChangeDetector.RequiresReconnect: it is read
        /// at the moment a reload completes, never baked into a spawn
        /// argument.
        /// </summary>
        public bool uapOpsAutoContinueAfterCompile;

        /// <summary>
        /// Auto-continue an INTERRUPTED turn (docs/design-notes/2026-09-06-
        /// domain-reload-resilience-and-hot-reload.md section 5-1): when a
        /// domain reload the agent did not cause -- the user saving a
        /// script in their IDE and coming back, a manual recompile,
        /// entering Play Mode -- cuts a running turn short, the panel
        /// resumes the session and, with this ON, sends the same "continue"
        /// message the ResumeBanner's button would have sent, instead of
        /// waiting for the click. DEFAULT OFF for the same reason as
        /// <see cref="uapOpsAutoContinueAfterCompile"/>: it is a turn the
        /// human did not send. Guardrails: only when the reload actually
        /// interrupted an open turn (AgentHub.ResumedMidTurn), never while
        /// the crash-loop guard holds the connection, always announced by a
        /// one-line transcript note (no user bubble -- design note
        /// 2026-09-10-auto-approve-all-tools-and-lean-auto-continue), and
        /// it carries the compile-error digest when the reload left errors
        /// behind. The former "at most three in a row" cap was removed by
        /// the same note. Read live when a reload completes; not a spawn
        /// argument, so not part of SettingsChangeDetector.
        /// </summary>
        public bool autoContinueInterruptedTurn;

        /// <summary>
        /// MIGRATION SOURCE ONLY as of the UapAutoApproveLevel rework --
        /// superseded by <see cref="autoApproveLevel"/>, which generalizes
        /// this single ReadOnly-only toggle into a four-level policy
        /// (Ask/ReadOnly/Undoable/AllUnityOps, see AutoApprovePolicy and
        /// UapAutoApproveLevel's doc comments for the measurements behind
        /// that shape). Kept present, rather than removed, purely because
        /// AgentHub.cs and SettingsView.cs still read/write this field
        /// directly at the time this rework landed; once those call sites
        /// move to <see cref="autoApproveLevel"/> (via AutoApprovePolicy.
        /// ShouldAutoApprove) this field becomes dead and can be deleted.
        /// Do NOT add new logic against this field -- <see
        /// cref="EnsureAutoApproveLevelMigrated"/> reads it exactly once
        /// (guarded by <see cref="autoApproveLevelMigrationGeneration"/>) to
        /// seed autoApproveLevel for an asset written before this rework,
        /// and every existing user's behaviour must come out identical:
        /// true -&gt; ReadOnly, false -&gt; Ask. Historical doc (unchanged):
        /// default ON, gated a can_use_tool auto-approve for any registered
        /// UapOps tool whose IUapTool.ReadOnly is true (2026-08-02 design
        /// note section 2 B2) -- query_hierarchy/query_component_types/
        /// component_list/object_inspect/asset_find/prefab_get_overrides/
        /// editor_screenshot -- purely live/read at decision time
        /// (AgentHub.OnPermissionRequested), not a spawn argument, so
        /// flipping it needed no reconnect. The same "purely live, no
        /// reconnect" property holds for autoApproveLevel: this whole
        /// policy is evaluated fresh on every permission request, so
        /// SettingsChangeDetector.RequiresReconnect must NOT gain a
        /// comparison for either field.
        /// </summary>
        public bool autoApproveReadOnlyOps = true;

        /// <summary>
        /// How much of the agent's Unity work is auto-approved without a
        /// permission card (Colloid.AgentPanel.Model.AutoApprovePolicy.
        /// ShouldAutoApprove is the pure decision function consuming this
        /// value; see UapAutoApproveLevel's doc comment for why this is
        /// panel-side policy rather than a CLI --permission-mode value).
        /// Default ReadOnly -- deliberately the SAME effective behaviour a
        /// brand-new v0.14.0-era settings asset got from
        /// <see cref="autoApproveReadOnlyOps"/>'s own default-ON field
        /// initializer, so a fresh install and a migrated pre-existing
        /// asset land on the identical level with no visible seam. An
        /// asset written before this field existed does NOT pick up this
        /// initializer -- see <see cref="EnsureAutoApproveLevelMigrated"/>.
        /// Purely live/read at decision time
        /// (AgentHub.OnPermissionRequested) -- not a spawn argument, so
        /// changing it needs no reconnect (see
        /// SettingsChangeDetector's class doc comment: this field is
        /// deliberately absent from RequiresReconnect for the same reason
        /// autoApproveReadOnlyOps always was).
        /// </summary>
        public UapAutoApproveLevel autoApproveLevel = UapAutoApproveLevel.ReadOnly;

        /// <summary>
        /// Migration generation this build's <see cref="autoApproveLevel"/>
        /// seeding logic implements. 1 = the one-time
        /// autoApproveReadOnlyOps -&gt; autoApproveLevel seed. Follows the
        /// exact shape of <see cref="CurrentModuleDefaultsGeneration"/> /
        /// <see cref="uapOpsModuleDefaultsGeneration"/> -- see that pair's
        /// doc comments for the full "why a generation stamp, not a plain
        /// bool" reasoning (a plain "already migrated" bool would work
        /// identically here since there is currently only one migration
        /// step, but the generation shape is kept for consistency with the
        /// sibling migration and in case a future level rework needs a
        /// second step).
        /// </summary>
        public const int CurrentAutoApproveLevelMigrationGeneration = 1;

        /// <summary>
        /// Highest autoApproveLevel migration generation this settings
        /// asset has been migrated through. Zero for both a brand-new
        /// asset (whose <see cref="autoApproveLevel"/> field initializer
        /// above already holds the correct value, so the migration is a
        /// harmless no-op past the gate) and an asset persisted by a
        /// version that predates <see cref="autoApproveLevel"/> entirely
        /// (whose deserialization leaves this at its default 0 and
        /// autoApproveLevel at its own default-ReadOnly initializer,
        /// exactly the value <see cref="EnsureAutoApproveLevelMigrated"/>
        /// would seed from autoApproveReadOnlyOps == true anyway -- but the
        /// seed must still run and still persist the stamp for the
        /// autoApproveReadOnlyOps == false case, where the pre-migration
        /// initializer value would otherwise be silently wrong).
        /// </summary>
        public int autoApproveLevelMigrationGeneration;

        /// <summary>
        /// Seeds <see cref="autoApproveLevel"/> from the legacy
        /// <see cref="autoApproveReadOnlyOps"/> toggle for an asset written
        /// before this rework, then records the generation so it runs
        /// exactly once -- same contract and same reasoning as
        /// <see cref="EnsureUapOpsModuleDefaults"/> (read that method's doc
        /// comment first; this one is deliberately parallel to it).
        /// Mapping (preserves every existing user's behaviour exactly):
        /// autoApproveReadOnlyOps == true -&gt; ReadOnly (the level that
        /// reproduces exactly what the old bool auto-approved: read-only
        /// UapOps tools only, nothing else); autoApproveReadOnlyOps ==
        /// false -&gt; Ask (the level that reproduces exactly what the old
        /// bool did when off: every UapOps tool still shows a card).
        /// Returns true when it changed anything (caller decides
        /// whether/when to persist), same contract as
        /// EnsureCjkUiFontDefault/EnsureUapOpsModuleDefaults.
        /// </summary>
        public bool EnsureAutoApproveLevelMigrated()
        {
            if (autoApproveLevelMigrationGeneration >= CurrentAutoApproveLevelMigrationGeneration)
            {
                return false;
            }
            autoApproveLevel = autoApproveReadOnlyOps ? UapAutoApproveLevel.ReadOnly : UapAutoApproveLevel.Ask;
            autoApproveLevelMigrationGeneration = CurrentAutoApproveLevelMigrationGeneration;
            // Unconditionally true past the generation gate above, for the
            // exact reason EnsureUapOpsModuleDefaults returns true
            // unconditionally (see that method's own doc comment): stamping
            // autoApproveLevelMigrationGeneration IS the change that MUST
            // reach disk, even on a run where the seeded level happens to
            // equal the field initializer's default (the true -&gt; ReadOnly
            // branch, which matches autoApproveLevel's own default). If this
            // returned "did autoApproveLevel actually change value?"
            // instead, that branch would report false, the stamp would go
            // unpersisted, and the migration would silently re-run on every
            // future editor start -- re-seeding over any choice the user
            // made via the new level's own Settings control in between. The
            // generation gate only protects a user's choice if the
            // generation is actually saved, regardless of what value was
            // computed on the run that saves it.
            return true;
        }

        /// <summary>
        /// Highest module-default generation this settings asset has been
        /// migrated through. A field initializer only applies to a FRESH
        /// PanelSettings -- an asset persisted by an older version
        /// deserializes its own stored list over the top, so a newly
        /// shipped default-ON module would never reach an existing user
        /// (live 2026-08-02: a v0.12.x asset kept `["core"]` and the whole
        /// Phase 5b prefab/editor family was invisible to the agent even
        /// though the code defaulted them on). Bump
        /// <see cref="CurrentModuleDefaultsGeneration"/> whenever a module
        /// is ADDED to the default-on set, and list it in
        /// <see cref="EnsureUapOpsModuleDefaults"/>.
        /// </summary>
        public int uapOpsModuleDefaultsGeneration;

        /// <summary>
        /// Master toggle for the script validation gate (design section
        /// 7.4/8.2 B1, revised 8.7). Default ON: while enabled, TWO
        /// independent layers deny the CLI's own Write/Edit/MultiEdit
        /// tools when they target `Assets/**/*.cs` or `*.asmdef`, directing
        /// the model to write under the `UapStaging/` folder (project
        /// root, outside Assets/) and call uap_scripts_commit instead:
        /// <list type="bullet">
        /// <item>AgentHub's can_use_tool pre-filter (TryAutoDenyForScriptGate)
        /// -- read LIVE off PanelStateStore on every can_use_tool, exactly
        /// as before; effective immediately in default/plan permission
        /// modes.</item>
        /// <item>a generated PreToolUse hook (Colloid.AgentPanel.Ops.
        /// GateHookInstaller), wired in via a NEXT-SPAWN-ONLY
        /// `--settings &lt;path&gt;` argument -- section 8.7's live-E2E finding
        /// was that can_use_tool alone never fires under
        /// `--permission-mode acceptEdits`, so this hook is the layer that
        /// actually enforces the gate there. Because this half of the
        /// toggle IS a spawn argument (like uapOpsEnabled's `--mcp-config`),
        /// SettingsChangeDetector.RequiresReconnect DOES compare this field
        /// now, unlike before section 8.7 (when the gate was can_use_tool-
        /// only and therefore fully live).</item>
        /// </list>
        /// Disabling this toggle restores the plain permission-card flow
        /// for direct Assets/ script writes AND omits `--settings` on the
        /// next spawn (an explicit opt-out, not the recommended setting).
        /// </summary>
        public bool uapScriptGateEnabled = true;

        // -- Extension Profiles (Phase 5b stream C, docs/design-notes/
        // 2026-08-01-phase5-unity-ops-design.md section 3b / 8.2 B3) --------

        /// <summary>
        /// Master toggle for Extension Profiles (design section 3b): while
        /// enabled, every DETECTED and TRUSTED profile (bundled always
        /// trusted; a user profile only once its content hash is in
        /// <see cref="approvedProfileHashes"/>) contributes a short "## Name"
        /// block to --append-system-prompt, appended after the subagent
        /// cost-policy line (Colloid.AgentPanel.Integration.AgentHub.
        /// ComposeAppendSystemPrompt / Colloid.AgentPanel.Ops.Profiles.
        /// ExtensionProfileService.ComposeAppendSection). Default ON.
        /// Reconnect-relevant exactly like uapOpsEnabled/allowedTools --
        /// see SettingsChangeDetector.RequiresReconnect -- since the
        /// composed text only takes effect on the next spawn.
        /// </summary>
        public bool extensionProfilesEnabled = true;

        /// <summary>
        /// Stream C of docs/design-notes/2026-09-10-unity-official-plugin-
        /// integration.md: when Unity's official plugin is installed, add a
        /// short --append-system-prompt note steering Claude to the
        /// /unity:* skills (and away from the `unity` CLI for Editor
        /// control). Default ON. Next-spawn-only like extensionProfilesEnabled.
        /// </summary>
        public bool unityPluginSteeringEnabled = true;

        /// <summary>
        /// Origin of the Agent Panel Pro update registry the Settings card
        /// writes into manifest.json / .upmconfig.toml (design note
        /// docs/design-notes/2026-09-12-pro-update-delivery.md section 3.3).
        /// Not a secret; the product key itself is never stored here --
        /// it goes straight to Unity's .upmconfig.toml. Empty means
        /// ProRegistryAccess.DefaultRegistryUrl.
        /// </summary>
        public string proRegistryUrl = string.Empty;

        /// <summary>
        /// Content hashes (lowercase hex SHA-256 of the raw file bytes,
        /// Colloid.AgentPanel.Ops.Profiles.UserExtensionProfileStore.
        /// ComputeContentHash) of user-supplied profiles
        /// (&lt;projectRoot&gt;/.uap-profiles/*.json) the user has explicitly
        /// approved for injection via the Settings "Extension profiles"
        /// card (design section 8.2 B3 -- "a user/third-party profile is
        /// only injected once its content hash is pinned via explicit
        /// approval; any change invalidates it"). A bundled profile is
        /// never represented here -- it does not need pinning, since it
        /// ships reviewed as part of this repository
        /// (Colloid.AgentPanel.Ops.Profiles.ExtensionProfileTrust.IsTrusted).
        /// Reconnect-relevant like extensionProfilesEnabled above: approving
        /// or revoking a profile only changes what the NEXT spawn's
        /// --append-system-prompt contains.
        /// </summary>
        public List<string> approvedProfileHashes = new List<string>();

        // -- Console error chip ignore (docs/design-notes/2026-08-13-
        // error-chip-ignore.md): replaces the count-based
        // _errorDismissedAtCount dismiss (ContextBarView) with a
        // content-keyed, project-persistent ignore so a compile-triggered
        // entry-count DROP can never resurrect a message the user already
        // dismissed, and so an SDK/extension that always re-logs the same
        // text on load stays hidden across editor restarts. ------------

        /// <summary>
        /// Exact Console error messages the user has permanently dismissed
        /// via the context-bar chip's X button. ConsoleErrorProvider.
        /// VisibleSnapshot/VisibleCount/FormatDigest filter these out
        /// (Ordinal exact match, ConsoleErrorIgnoreFilter.IsIgnored), but
        /// the RAW captured entry in ConsoleErrorProvider is never deleted
        /// -- removing a message from this list (the Settings management
        /// list's per-row remove button) immediately un-hides it again, no
        /// data loss. Bounded FIFO (oldest evicted first) via
        /// ConsoleErrorIgnoreFilter.AddIgnores/IgnoredConsoleErrorsMax so a
        /// project whose user keeps X-ing distinct SDK noise over months
        /// does not grow the store without limit.
        ///
        /// MODEL-4: [NonSerialized] -- the content is RAW Console error
        /// text (braces, quotes, stack traces, arbitrarily long lines),
        /// exactly the string class that round-trips unreliably through
        /// UnityYAML and used to corrupt the whole State.asset (the reason
        /// SessionCacheFile/SessionMetaFile exist; see PanelStateStore's
        /// class comment "never add message-content strings to this
        /// asset"). Persistence lives in the ConsoleIgnoreFile JSON sidecar
        /// instead: PanelStateStore.SaveNow writes it, OnEnable hydrates
        /// it, and this in-memory field stays the live working store every
        /// existing call site reads/mutates.
        /// </summary>
        [NonSerialized]
        public List<string> ignoredConsoleErrors = new List<string>();

        /// <summary>
        /// One Ordinal substring per line (ConsoleErrorIgnoreFilter.
        /// ParsePatterns trims each line and drops blanks): any captured
        /// error message CONTAINING one of these is hidden the same way an
        /// exact-ignored message is, even if it was never X-ed -- the
        /// free-text escape hatch for an SDK/extension that always logs
        /// slightly different text (design note decision 2: patterns
        /// "survive message variation" where an exact match cannot).
        /// Deliberately plain substring, not regex -- the design note's
        /// rejected alternatives explicitly call out an invalid regex
        /// throwing at filter time as a user-hostile failure mode for a
        /// free-text Settings field. Read live by ConsoleErrorProvider on
        /// every VisibleSnapshot/FormatDigest call, so editing this field
        /// needs no reconnect. [NonSerialized] for the same MODEL-4 reason
        /// as <see cref="ignoredConsoleErrors"/> (user free text out of
        /// UnityYAML); persisted via ConsoleIgnoreFile.
        /// </summary>
        [NonSerialized]
        public string ignoredConsoleErrorPatterns = string.Empty;

        /// <summary>
        /// Pre-MODEL-4 on-disk copies (the fields used to be serialized
        /// straight into State.asset). FormerlySerializedAs lets an asset
        /// written by an older build still deserialize its ignore data --
        /// into these private holders -- so
        /// <see cref="MigrateLegacyConsoleIgnores"/> can move it to the
        /// sidecar-backed live fields exactly once. Cleared after
        /// migration; the next asset write then drops the free text out of
        /// the YAML for good.
        /// </summary>
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("ignoredConsoleErrors")]
        private List<string> legacyIgnoredConsoleErrors = new List<string>();

        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("ignoredConsoleErrorPatterns")]
        private string legacyIgnoredConsoleErrorPatterns = string.Empty;

        /// <summary>
        /// One-time MODEL-4 upgrade: moves ignore data an older build
        /// serialized into State.asset over to the live fields, then clears
        /// the legacy holders so the free text leaves the YAML on the next
        /// asset write. Returns true when it changed anything (the caller
        /// schedules a clean asset rewrite plus a sidecar save, the same
        /// contract as <see cref="EnsureCjkUiFontDefault"/>).
        ///
        /// Precedence: entries migrate only where the live store has no
        /// value yet -- when a sidecar was already hydrated (this build ran
        /// before; the sidecar is the source of truth), missing exact
        /// entries are appended (bounded) and a non-empty live pattern
        /// text wins over the legacy one.
        /// </summary>
        public bool MigrateLegacyConsoleIgnores()
        {
            bool changed = false;
            if (legacyIgnoredConsoleErrors != null && legacyIgnoredConsoleErrors.Count > 0)
            {
                ConsoleErrorIgnoreFilter.AddIgnores(ignoredConsoleErrors,
                    legacyIgnoredConsoleErrors, ConsoleErrorIgnoreFilter.IgnoredConsoleErrorsMax);
                legacyIgnoredConsoleErrors.Clear();
                changed = true;
            }
            if (!string.IsNullOrEmpty(legacyIgnoredConsoleErrorPatterns))
            {
                if (string.IsNullOrEmpty(ignoredConsoleErrorPatterns))
                {
                    ignoredConsoleErrorPatterns = legacyIgnoredConsoleErrorPatterns;
                }
                legacyIgnoredConsoleErrorPatterns = string.Empty;
                changed = true;
            }
            return changed;
        }

        /// <summary>Test seam for <see cref="MigrateLegacyConsoleIgnores"/> (the holders are private by design).</summary>
        internal void SetLegacyConsoleIgnoresForTests(List<string> errors, string patterns)
        {
            legacyIgnoredConsoleErrors = errors ?? new List<string>();
            legacyIgnoredConsoleErrorPatterns = patterns ?? string.Empty;
        }
    }
}
