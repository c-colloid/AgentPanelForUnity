using System.Collections.Generic;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards PanelSettings.preferCjkUiFont's default -- the toggle behind
    /// the patchy-bold/faint Japanese glyph fix (FontLoader.JapaneseUiFont
    /// applied at the panel content root). The decision is exercised
    /// through the pure ComputeDefaultPreferCjkUiFont(SystemLanguage)
    /// function so every branch is testable without reading or mutating
    /// the real Application.systemLanguage (which is fixed for the whole
    /// batch-mode process).
    /// </summary>
    public class PanelSettingsTests
    {
        [Test]
        public void ComputeDefaultPreferCjkUiFont_Japanese_IsTrue()
        {
            Assert.IsTrue(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.Japanese));
        }

        [Test]
        public void ComputeDefaultPreferCjkUiFont_ChineseVariants_AreTrue()
        {
            Assert.IsTrue(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.Chinese));
            Assert.IsTrue(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.ChineseSimplified));
            Assert.IsTrue(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.ChineseTraditional));
        }

        [Test]
        public void ComputeDefaultPreferCjkUiFont_Korean_IsTrue()
        {
            Assert.IsTrue(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.Korean));
        }

        [Test]
        public void ComputeDefaultPreferCjkUiFont_English_IsFalse()
        {
            Assert.IsFalse(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.English));
        }

        [Test]
        public void ComputeDefaultPreferCjkUiFont_Unknown_IsFalse()
        {
            Assert.IsFalse(PanelSettings.ComputeDefaultPreferCjkUiFont(
                SystemLanguage.Unknown));
        }

        [Test]
        public void NewSettings_PreferCjkUiFont_DefaultsOffUntilDecided()
        {
            // REGRESSION GUARD (2026-07-31): the default must NOT be a
            // field initializer -- Application.systemLanguage throws inside
            // a ScriptableObject constructor/field initializer during
            // ScriptableSingleton load, which broke the entire window.
            // A fresh instance is therefore undecided/off until OnEnable
            // applies the one-time default.
            var settings = new PanelSettings();
            Assert.IsFalse(settings.preferCjkUiFont);
            Assert.IsFalse(settings.cjkUiFontDecided);
        }

        [Test]
        public void EnsureCjkUiFontDefault_AppliesOnce_AndPreservesUserChoice()
        {
            var settings = new PanelSettings();
            Assert.IsTrue(settings.EnsureCjkUiFontDefault(SystemLanguage.Japanese));
            Assert.IsTrue(settings.preferCjkUiFont);
            Assert.IsTrue(settings.cjkUiFontDecided);

            // User turns it off; a later OnEnable must NOT clobber that.
            settings.preferCjkUiFont = false;
            Assert.IsFalse(settings.EnsureCjkUiFontDefault(SystemLanguage.Japanese));
            Assert.IsFalse(settings.preferCjkUiFont);

            // Non-CJK default decides to off and stays decided.
            var en = new PanelSettings();
            Assert.IsTrue(en.EnsureCjkUiFontDefault(SystemLanguage.English));
            Assert.IsFalse(en.preferCjkUiFont);
            Assert.IsTrue(en.cjkUiFontDecided);
        }

        // -- Model settings (v0.8.0, docs/design-notes/2026-08-01-model-
        // settings.md): modelCatalog / agentModelOverrides must survive the
        // SAME Unity serialization path PanelStateStore uses for its
        // change-detection snapshot (JsonUtility.ToJson(Settings)) and for
        // the real UnityYAML asset write -- both rely on [Serializable]
        // nested classes with public fields, exactly like ModelCatalogEntry/
        // AgentModelOverride. -----------------------------------------------------

        [Test]
        public void ModelCatalogAndAgentOverrides_RoundTripThroughUnitySerialization()
        {
            var settings = new PanelSettings();
            settings.model = "sonnet";
            settings.modelCatalog.Add(new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet" });
            settings.modelCatalog.Add(new ModelCatalogEntry { value = "haiku", displayName = "Haiku" });
            settings.agentModelOverrides.Add(new AgentModelOverride
            {
                agentName = "general-purpose",
                modelAlias = "haiku"
            });

            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);

            Assert.AreEqual("sonnet", restored.model);
            Assert.AreEqual(2, restored.modelCatalog.Count);
            Assert.AreEqual("sonnet", restored.modelCatalog[0].value);
            Assert.AreEqual("Sonnet", restored.modelCatalog[0].displayName);
            Assert.AreEqual("haiku", restored.modelCatalog[1].value);
            Assert.AreEqual("Haiku", restored.modelCatalog[1].displayName);
            Assert.AreEqual(1, restored.agentModelOverrides.Count);
            Assert.AreEqual("general-purpose", restored.agentModelOverrides[0].agentName);
            Assert.AreEqual("haiku", restored.agentModelOverrides[0].modelAlias);
        }

        [Test]
        public void SlashCommandCatalog_RoundTripsThroughUnitySerialization()
        {
            var settings = new PanelSettings();
            settings.slashCommandCatalog.Add(new SlashCommandEntry
            {
                name = "compact",
                description = "Summarize",
                argumentHint = "[instructions]"
            });
            settings.slashCommandCatalog.Add(new SlashCommandEntry { name = "review" });

            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);

            Assert.AreEqual(2, restored.slashCommandCatalog.Count);
            Assert.AreEqual("compact", restored.slashCommandCatalog[0].name);
            Assert.AreEqual("Summarize", restored.slashCommandCatalog[0].description);
            Assert.AreEqual("[instructions]", restored.slashCommandCatalog[0].argumentHint);
            Assert.AreEqual("review", restored.slashCommandCatalog[1].name);
            Assert.AreEqual(string.Empty, restored.slashCommandCatalog[1].description);
        }

        [Test]
        public void NewPanelSettings_ModelCatalogAndAgentOverrides_AreEmptyNeverNull()
        {
            var settings = new PanelSettings();
            Assert.IsNotNull(settings.modelCatalog);
            Assert.AreEqual(0, settings.modelCatalog.Count);
            Assert.IsNotNull(settings.agentModelOverrides);
            Assert.AreEqual(0, settings.agentModelOverrides.Count);
        }

        // -- Model settings rework additions (v0.9.0, docs/design-notes/
        // 2026-08-01-model-settings-rework.md section 4.2) ---------------------

        [Test]
        public void NewPanelSettings_SubagentModel_DefaultsToEmptyString()
        {
            var settings = new PanelSettings();
            Assert.AreEqual(string.Empty, settings.subagentModel);
        }

        [Test]
        public void NewPanelSettings_AgentTypeCatalog_IsEmptyNeverNull()
        {
            var settings = new PanelSettings();
            Assert.IsNotNull(settings.agentTypeCatalog);
            Assert.AreEqual(0, settings.agentTypeCatalog.Count);
        }

        [Test]
        public void SubagentModelAndAgentTypeCatalog_RoundTripThroughUnitySerialization()
        {
            var settings = new PanelSettings();
            settings.subagentModel = "haiku";
            settings.agentTypeCatalog.Add("general-purpose");
            settings.agentTypeCatalog.Add("Explore");

            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);

            Assert.AreEqual("haiku", restored.subagentModel);
            CollectionAssert.AreEqual(new[] { "general-purpose", "Explore" }, restored.agentTypeCatalog);
        }

        // -- API key auth passthrough (v0.40.0, docs/design-notes/2026-09-
        // 10-claude-api-key-auth-passthrough.md) ------------------------------

        [Test]
        public void NewPanelSettings_ClaudeAuth_DefaultsToAuto()
        {
            var settings = new PanelSettings();
            Assert.AreEqual(ClaudeAuthMode.Auto, settings.claudeAuth);
        }

        [Test]
        public void ClaudeAuth_RoundTripsThroughUnitySerialization()
        {
            var settings = new PanelSettings { claudeAuth = ClaudeAuthMode.SubscriptionOnly };

            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);

            Assert.AreEqual(ClaudeAuthMode.SubscriptionOnly, restored.claudeAuth);
        }

        // -- Subagent model precedence rework (v0.11.0, docs/design-notes/
        // 2026-08-02-subagent-model-precedence.md) ---------------------------

        [Test]
        public void NewPanelSettings_SubagentCostPolicy_DefaultsToAgentDecides()
        {
            var settings = new PanelSettings();
            Assert.AreEqual(SubagentCostPolicy.AgentDecides, settings.subagentCostPolicy);
        }

        [Test]
        public void SubagentCostPolicy_RoundTripsThroughUnitySerialization()
        {
            var settings = new PanelSettings { subagentCostPolicy = SubagentCostPolicy.HaikuForSimpleTasks };
            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);
            Assert.AreEqual(SubagentCostPolicy.HaikuForSimpleTasks, restored.subagentCostPolicy);
        }

        [Test]
        public void ModelCatalogEntry_ResolvedModelAndDescription_DefaultToEmptyString()
        {
            var entry = new ModelCatalogEntry();
            Assert.AreEqual(string.Empty, entry.resolvedModel);
            Assert.AreEqual(string.Empty, entry.description);
        }

        [Test]
        public void ModelCatalogEntry_ResolvedModelAndDescription_RoundTripThroughUnitySerialization()
        {
            var settings = new PanelSettings();
            settings.modelCatalog.Add(new ModelCatalogEntry
            {
                value = "default",
                displayName = "Default (recommended)",
                resolvedModel = "claude-opus-5[1m]",
                description = "Use the default model"
            });

            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);

            Assert.AreEqual(1, restored.modelCatalog.Count);
            Assert.AreEqual("claude-opus-5[1m]", restored.modelCatalog[0].resolvedModel);
            Assert.AreEqual("Use the default model", restored.modelCatalog[0].description);
        }

        [Test]
        public void ModelCatalogEntry_CacheWrittenBeforeV0_11_0_DeserializesWithEmptyNewFields()
        {
            // Old cache JSON (pre-v0.11.0) carries only value/displayName --
            // JsonUtility must fill the new fields with their declared
            // defaults (empty string) rather than fail to load.
            const string oldCacheJson =
                "{\"modelCatalog\":[{\"value\":\"sonnet\",\"displayName\":\"Sonnet\"}]}";
            var restored = JsonUtility.FromJson<PanelSettings>(oldCacheJson);
            Assert.AreEqual(1, restored.modelCatalog.Count);
            Assert.AreEqual("sonnet", restored.modelCatalog[0].value);
            Assert.AreEqual(string.Empty, restored.modelCatalog[0].resolvedModel);
            Assert.AreEqual(string.Empty, restored.modelCatalog[0].description);
        }

        // -- UapOps (Phase 5a, docs/design-notes/2026-08-01-phase5-unity-
        // ops-design.md section 1.1/8.6) -------------------------------

        [Test]
        public void NewSettings_UapOpsEnabled_DefaultsOn()
        {
            Assert.IsTrue(new PanelSettings().uapOpsEnabled);
        }

        [Test]
        public void NewSettings_UapOpsModules_DefaultsToCorePrefabEditor()
        {
            // Phase 5b stream A: "prefab"/"editor" join "core" as default-ON
            // modules (design-notes kickoff section A1/A2).
            // 2026-09-07: "markers" joined the default-on set (generation 2).
            CollectionAssert.AreEqual(new[] { "core", "prefab", "editor", "markers" }, new PanelSettings().uapOpsModules);
        }

        [Test]
        public void UapOpsEnabled_SurvivesJsonUtilityRoundTrip()
        {
            var settings = new PanelSettings { uapOpsEnabled = false };
            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);
            Assert.IsFalse(restored.uapOpsEnabled);
        }

        [Test]
        public void UapOpsModules_SurvivesJsonUtilityRoundTrip()
        {
            var settings = new PanelSettings { uapOpsModules = new List<string> { "core", "prefab" } };
            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);
            CollectionAssert.AreEqual(new[] { "core", "prefab" }, restored.uapOpsModules);
        }

        // -- EnsureUapOpsModuleDefaults ------------------------------------
        // Live 2026-08-02: a settings asset written by v0.12.x kept
        // uapOpsModules = ["core"], so the whole Phase 5b prefab/editor
        // family was invisible to the agent even though the field
        // initializer defaults them on -- the persisted list deserializes
        // OVER the initializer. These guard the generation-gated migration.

        [Test]
        public void EnsureUapOpsModuleDefaults_LegacyCoreOnlyAsset_GainsPrefabAndEditor()
        {
            var settings = new PanelSettings
            {
                uapOpsModules = new List<string> { "core" },
                uapOpsModuleDefaultsGeneration = 0
            };

            Assert.IsTrue(settings.EnsureUapOpsModuleDefaults());
            CollectionAssert.AreEquivalent(
                new[] { "core", "prefab", "editor", "markers" }, settings.uapOpsModules);
            Assert.AreEqual(PanelSettings.CurrentModuleDefaultsGeneration,
                settings.uapOpsModuleDefaultsGeneration);
        }

        [Test]
        public void EnsureUapOpsModuleDefaults_Generation1Asset_GainsMarkersOnly()
        {
            // 2026-09-07: "markers" became default-ON as generation 2. An
            // asset written by a generation-1 build (which had removed
            // "prefab" by choice) gains markers and keeps prefab off.
            var settings = new PanelSettings
            {
                uapOpsModules = new List<string> { "core", "editor" },
                uapOpsModuleDefaultsGeneration = 1
            };

            Assert.IsTrue(settings.EnsureUapOpsModuleDefaults());
            CollectionAssert.AreEquivalent(new[] { "core", "editor", "markers" }, settings.uapOpsModules);
            Assert.AreEqual(2, settings.uapOpsModuleDefaultsGeneration);
        }

        [Test]
        public void EnsureUapOpsModuleDefaults_MarkersSwitchedOffAtGeneration2_StaysOff()
        {
            var settings = new PanelSettings
            {
                uapOpsModules = new List<string> { "core", "prefab", "editor" },
                uapOpsModuleDefaultsGeneration = 2
            };

            Assert.IsFalse(settings.EnsureUapOpsModuleDefaults());
            CollectionAssert.DoesNotContain(settings.uapOpsModules, "markers");
        }

        [Test]
        public void EnsureUapOpsModuleDefaults_RunsOnlyOnce_UserDisabledModuleStaysOff()
        {
            // The reason this is generation-gated instead of a union with
            // the defaults on every load.
            var settings = new PanelSettings
            {
                uapOpsModules = new List<string> { "core" },
                uapOpsModuleDefaultsGeneration = 0
            };
            settings.EnsureUapOpsModuleDefaults();
            settings.uapOpsModules.Remove("prefab");

            Assert.IsFalse(settings.EnsureUapOpsModuleDefaults(),
                "a second pass must be a no-op");
            CollectionAssert.DoesNotContain(settings.uapOpsModules, "prefab",
                "a module the user switched off must never be re-enabled by the migration");
        }

        [Test]
        public void EnsureUapOpsModuleDefaults_NullList_IsRepairedNotThrown()
        {
            var settings = new PanelSettings { uapOpsModules = null, uapOpsModuleDefaultsGeneration = 0 };

            Assert.IsTrue(settings.EnsureUapOpsModuleDefaults());
            CollectionAssert.AreEquivalent(
                new[] { "core", "prefab", "editor", "markers" }, settings.uapOpsModules);
        }

        [Test]
        public void EnsureUapOpsModuleDefaults_DoesNotAddDefaultOffModules()
        {
            var settings = new PanelSettings
            {
                uapOpsModules = new List<string> { "core" },
                uapOpsModuleDefaultsGeneration = 0
            };
            settings.EnsureUapOpsModuleDefaults();

            CollectionAssert.DoesNotContain(settings.uapOpsModules, "anim",
                "anim ships default-OFF (design 8.8) and must not be migrated on");
        }

        [Test]
        public void EnsureUapOpsModuleDefaults_ListAlreadyComplete_StillReportsChange()
        {
            // Pins WHY the method returns true unconditionally past the
            // generation gate. An asset can already hold every default
            // module while its generation stamp is still 0 (e.g. a user who
            // had enabled prefab/editor by hand before the migration
            // shipped). Nothing is added to the list -- but the stamp moves,
            // and the caller must persist it. Returning "did the list
            // change?" here (the dead `changed` flag CS0219 flagged) would
            // leave the stamp unsaved, so the migration would re-run next
            // start and THAT run would re-add any module switched off in
            // between -- defeating the whole point of the generation gate.
            var settings = new PanelSettings
            {
                uapOpsModules = new List<string> { "core", "prefab", "editor", "markers" },
                uapOpsModuleDefaultsGeneration = 0
            };

            Assert.IsTrue(settings.EnsureUapOpsModuleDefaults(),
                "stamping the generation is itself a change the caller must persist");
            Assert.AreEqual(PanelSettings.CurrentModuleDefaultsGeneration,
                settings.uapOpsModuleDefaultsGeneration);
            CollectionAssert.AreEquivalent(
                new[] { "core", "prefab", "editor", "markers" }, settings.uapOpsModules,
                "an already-complete list must not gain duplicates");
        }

        [Test]
        public void NewPanelSettings_ModuleDefaults_MatchTheMigrationTarget()
        {
            // A fresh asset must not need the migration at all -- and its
            // initializer must agree with what the migration produces.
            var fresh = new PanelSettings();
            var migrated = new PanelSettings
            {
                uapOpsModules = new List<string>(),
                uapOpsModuleDefaultsGeneration = 0
            };
            migrated.EnsureUapOpsModuleDefaults();

            CollectionAssert.AreEquivalent(fresh.uapOpsModules, migrated.uapOpsModules);
        }

        // -- autoApproveReadOnlyOps -> autoApproveLevel migration
        // (UapAutoApproveLevel rework). Mirrors the EnsureUapOpsModuleDefaults
        // tests above in shape: a legacy asset (generation 0) must seed the
        // new field from the old bool and stamp the generation so the seed
        // never re-runs and clobbers a later user choice. -------------------

        [Test]
        public void NewPanelSettings_AutoApproveLevel_DefaultsToReadOnly()
        {
            // Must match what a v0.14.0-era fresh asset got from
            // autoApproveReadOnlyOps's own default-ON initializer, so a
            // brand-new install and a migrated pre-existing asset land on
            // the identical level.
            Assert.AreEqual(UapAutoApproveLevel.ReadOnly, new PanelSettings().autoApproveLevel);
        }

        [Test]
        public void EnsureAutoApproveLevelMigrated_LegacyToggleTrue_SeedsReadOnly()
        {
            var settings = new PanelSettings
            {
                autoApproveReadOnlyOps = true,
                autoApproveLevelMigrationGeneration = 0
            };

            Assert.IsTrue(settings.EnsureAutoApproveLevelMigrated());
            Assert.AreEqual(UapAutoApproveLevel.ReadOnly, settings.autoApproveLevel);
            Assert.AreEqual(PanelSettings.CurrentAutoApproveLevelMigrationGeneration,
                settings.autoApproveLevelMigrationGeneration);
        }

        [Test]
        public void EnsureAutoApproveLevelMigrated_LegacyToggleFalse_SeedsAsk()
        {
            var settings = new PanelSettings
            {
                autoApproveReadOnlyOps = false,
                autoApproveLevelMigrationGeneration = 0
            };

            Assert.IsTrue(settings.EnsureAutoApproveLevelMigrated());
            Assert.AreEqual(UapAutoApproveLevel.Ask, settings.autoApproveLevel);
            Assert.AreEqual(PanelSettings.CurrentAutoApproveLevelMigrationGeneration,
                settings.autoApproveLevelMigrationGeneration);
        }

        [Test]
        public void EnsureAutoApproveLevelMigrated_RunsOnlyOnce_LaterUserChoiceStays()
        {
            // The reason this is generation-gated instead of re-seeding
            // from the legacy bool on every load, same as
            // EnsureUapOpsModuleDefaults_RunsOnlyOnce_UserDisabledModuleStaysOff.
            var settings = new PanelSettings
            {
                autoApproveReadOnlyOps = true,
                autoApproveLevelMigrationGeneration = 0
            };
            settings.EnsureAutoApproveLevelMigrated();
            settings.autoApproveLevel = UapAutoApproveLevel.AllUnityOps;

            Assert.IsFalse(settings.EnsureAutoApproveLevelMigrated(),
                "a second pass must be a no-op");
            Assert.AreEqual(UapAutoApproveLevel.AllUnityOps, settings.autoApproveLevel,
                "a level the user explicitly chose after the seed must never be overwritten"
                + " by a later re-seed from the legacy bool");
        }

        [Test]
        public void EnsureAutoApproveLevelMigrated_ListAlreadyComplete_StillReportsChange()
        {
            // Pins WHY the method returns true unconditionally past the
            // generation gate, mirroring
            // EnsureUapOpsModuleDefaults_ListAlreadyComplete_StillReportsChange:
            // the seeded value can equal the field initializer's own default
            // (true -> ReadOnly) while the generation stamp still needs to
            // be persisted, or the seed silently re-runs on every future
            // editor start.
            var settings = new PanelSettings
            {
                autoApproveReadOnlyOps = true,
                autoApproveLevel = UapAutoApproveLevel.ReadOnly,
                autoApproveLevelMigrationGeneration = 0
            };

            Assert.IsTrue(settings.EnsureAutoApproveLevelMigrated(),
                "stamping the generation is itself a change the caller must persist");
            Assert.AreEqual(PanelSettings.CurrentAutoApproveLevelMigrationGeneration,
                settings.autoApproveLevelMigrationGeneration);
            Assert.AreEqual(UapAutoApproveLevel.ReadOnly, settings.autoApproveLevel);
        }

        [Test]
        public void AutoApproveLevel_SurvivesJsonUtilityRoundTrip()
        {
            var settings = new PanelSettings { autoApproveLevel = UapAutoApproveLevel.Undoable };
            string json = JsonUtility.ToJson(settings);
            var restored = JsonUtility.FromJson<PanelSettings>(json);
            Assert.AreEqual(UapAutoApproveLevel.Undoable, restored.autoApproveLevel);
        }

        [Test]
        public void PreV0_15_0SettingsAsset_MissingAutoApproveLevel_MigratesToMatchOldToggle()
        {
            // Simulates a settings asset written by v0.15.x (before
            // autoApproveLevel existed): the persisted JSON carries only
            // autoApproveReadOnlyOps. JsonUtility must fill autoApproveLevel
            // with its declared default (ReadOnly) and
            // autoApproveLevelMigrationGeneration with 0 rather than fail to
            // load; EnsureAutoApproveLevelMigrated then brings it to the
            // value that reproduces the old toggle's behaviour exactly.
            const string oldAssetJsonToggleOn =
                "{\"autoApproveReadOnlyOps\":true}";
            var restoredOn = JsonUtility.FromJson<PanelSettings>(oldAssetJsonToggleOn);
            Assert.AreEqual(0, restoredOn.autoApproveLevelMigrationGeneration);
            Assert.IsTrue(restoredOn.EnsureAutoApproveLevelMigrated());
            Assert.AreEqual(UapAutoApproveLevel.ReadOnly, restoredOn.autoApproveLevel);

            const string oldAssetJsonToggleOff =
                "{\"autoApproveReadOnlyOps\":false}";
            var restoredOff = JsonUtility.FromJson<PanelSettings>(oldAssetJsonToggleOff);
            Assert.AreEqual(0, restoredOff.autoApproveLevelMigrationGeneration);
            Assert.IsTrue(restoredOff.EnsureAutoApproveLevelMigrated());
            Assert.AreEqual(UapAutoApproveLevel.Ask, restoredOff.autoApproveLevel);
        }
    }
}
