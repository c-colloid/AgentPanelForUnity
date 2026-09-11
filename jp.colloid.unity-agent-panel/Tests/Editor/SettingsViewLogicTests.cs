using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

// UnityEngine.UIElements -- needed by the AddHint/AddWarning tests -- brings its
// own PanelSettings (the runtime UI Document asset) into scope, which collides
// with this package's settings model. The alias keeps every pre-existing test in
// this file meaning what it meant before the UIElements import arrived.
using PanelSettings = Colloid.AgentPanel.Model.PanelSettings;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic guards for SettingsView: the allowed/disallowedTools
    /// multiline-field &lt;-&gt; List&lt;string&gt; conversion, and
    /// PanelSettings.ClampFontSize (the font-size slider's range guard,
    /// also applied to whatever a hand-edited or future-version
    /// State.asset hands back at load time -- see
    /// docs/design-notes/2026-07-31-font-size-application.md).
    /// </summary>
    public class SettingsViewLogicTests
    {
        // -- SplitLines / JoinLines -------------------------------------------------

        [Test]
        public void SplitLines_OneToolPerLine()
        {
            List<string> result = SettingsView.SplitLines("Read\nEdit\nBash(git:*)");
            CollectionAssert.AreEqual(new[] { "Read", "Edit", "Bash(git:*)" }, result);
        }

        [Test]
        public void SplitLines_TrimsWhitespace_AndDropsEmptyLines()
        {
            List<string> result = SettingsView.SplitLines("  Read  \n\n\tEdit\n   \n");
            CollectionAssert.AreEqual(new[] { "Read", "Edit" }, result);
        }

        [Test]
        public void SplitLines_HandlesCrLf()
        {
            List<string> result = SettingsView.SplitLines("Read\r\nEdit\r\n");
            CollectionAssert.AreEqual(new[] { "Read", "Edit" }, result);
        }

        [Test]
        public void SplitLines_NullOrEmpty_ReturnsEmptyList_NeverNull()
        {
            Assert.IsNotNull(SettingsView.SplitLines(null));
            Assert.AreEqual(0, SettingsView.SplitLines(null).Count);
            Assert.AreEqual(0, SettingsView.SplitLines(string.Empty).Count);
            Assert.AreEqual(0, SettingsView.SplitLines("   \n  \n").Count);
        }

        [Test]
        public void JoinLines_NullList_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, SettingsView.JoinLines(null));
        }

        [Test]
        public void JoinLines_And_SplitLines_RoundTrip()
        {
            var original = new List<string> { "Read", "Edit", "Bash(npm run test:*)" };
            string joined = SettingsView.JoinLines(original);
            List<string> roundTripped = SettingsView.SplitLines(joined);
            CollectionAssert.AreEqual(original, roundTripped);
        }

        // -- PanelSettings.ClampFontSize ---------------------------------------------

        [Test]
        public void ClampFontSize_WithinRange_IsUnchanged()
        {
            Assert.AreEqual(11, PanelSettings.ClampFontSize(11));
            Assert.AreEqual(12, PanelSettings.ClampFontSize(12));
            Assert.AreEqual(16, PanelSettings.ClampFontSize(16));
            // UXIA-L5: the raised cap is in-range too.
            Assert.AreEqual(PanelSettings.MaxFontSizePx,
                PanelSettings.ClampFontSize(PanelSettings.MaxFontSizePx));
        }

        [Test]
        public void ClampFontSize_BelowMinimum_ClampsToMinimum()
        {
            Assert.AreEqual(PanelSettings.MinFontSizePx, PanelSettings.ClampFontSize(0));
            Assert.AreEqual(PanelSettings.MinFontSizePx, PanelSettings.ClampFontSize(-100));
            Assert.AreEqual(PanelSettings.MinFontSizePx, PanelSettings.ClampFontSize(10));
        }

        [Test]
        public void ClampFontSize_AboveMaximum_ClampsToMaximum()
        {
            Assert.AreEqual(PanelSettings.MaxFontSizePx,
                PanelSettings.ClampFontSize(PanelSettings.MaxFontSizePx + 1));
            Assert.AreEqual(PanelSettings.MaxFontSizePx, PanelSettings.ClampFontSize(1000));
        }

        [Test]
        public void DefaultFontSizePx_IsWithinRange()
        {
            // Regression guard: the default must itself be a legal,
            // already-clamped value, or a fresh PanelSettings would render
            // with a font-scale class AgentPanel.uss never defines.
            Assert.AreEqual(PanelSettings.DefaultFontSizePx,
                PanelSettings.ClampFontSize(PanelSettings.DefaultFontSizePx));
            Assert.GreaterOrEqual(PanelSettings.DefaultFontSizePx, PanelSettings.MinFontSizePx);
            Assert.LessOrEqual(PanelSettings.DefaultFontSizePx, PanelSettings.MaxFontSizePx);
        }

        [Test]
        public void NewPanelSettings_FontSizePx_DefaultsToDefaultFontSizePx()
        {
            var settings = new PanelSettings();
            Assert.AreEqual(PanelSettings.DefaultFontSizePx, settings.fontSizePx);
        }

        // -- BuildModelChoices / ResolveInitialModelChoice (v0.8.0, docs/
        // design-notes/2026-08-01-model-settings.md section 1) -----------------------

        [Test]
        public void BuildModelChoices_EmptyCatalogAndCurrentValue_YieldsOnlyTheDefaultSentinel()
        {
            List<string> choices = SettingsView.BuildModelChoices(new List<ModelCatalogEntry>(), string.Empty);
            CollectionAssert.AreEqual(new[] { string.Empty }, choices);
        }

        [Test]
        public void BuildModelChoices_NullCatalog_IsTreatedAsEmpty()
        {
            List<string> choices = SettingsView.BuildModelChoices(null, null);
            CollectionAssert.AreEqual(new[] { string.Empty }, choices);
        }

        [Test]
        public void BuildModelChoices_DefaultSentinelAlwaysFirst_ThenCatalogInOrder()
        {
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet" },
                new ModelCatalogEntry { value = "haiku", displayName = "Haiku" }
            };
            List<string> choices = SettingsView.BuildModelChoices(catalog, string.Empty);
            CollectionAssert.AreEqual(new[] { string.Empty, "sonnet", "haiku" }, choices);
        }

        [Test]
        public void BuildModelChoices_DuplicateCatalogValues_AreDeduplicated()
        {
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet" },
                new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet (dup)" }
            };
            List<string> choices = SettingsView.BuildModelChoices(catalog, string.Empty);
            CollectionAssert.AreEqual(new[] { string.Empty, "sonnet" }, choices);
        }

        [Test]
        public void BuildModelChoices_CurrentValueNotInCatalog_IsAppendedAtEnd()
        {
            // A persisted/manual value the live catalog no longer lists must
            // never silently vanish from the dropdown it is currently set
            // to (R07's catalog can change between CLI versions).
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet" }
            };
            List<string> choices = SettingsView.BuildModelChoices(catalog, "some-manual-alias");
            CollectionAssert.AreEqual(new[] { string.Empty, "sonnet", "some-manual-alias" }, choices);
        }

        [Test]
        public void BuildModelChoices_CurrentValueAlreadyInCatalog_IsNotDuplicated()
        {
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet" }
            };
            List<string> choices = SettingsView.BuildModelChoices(catalog, "sonnet");
            CollectionAssert.AreEqual(new[] { string.Empty, "sonnet" }, choices);
        }

        [Test]
        public void ResolveInitialModelChoice_CurrentValuePresent_ReturnsIt()
        {
            var choices = new List<string> { string.Empty, "sonnet", "haiku" };
            Assert.AreEqual("haiku", SettingsView.ResolveInitialModelChoice(choices, "haiku"));
        }

        [Test]
        public void ResolveInitialModelChoice_CurrentValueAbsent_FallsBackToFirstChoice()
        {
            var choices = new List<string> { string.Empty, "sonnet" };
            Assert.AreEqual(string.Empty, SettingsView.ResolveInitialModelChoice(choices, "opus"));
        }

        [Test]
        public void ResolveInitialModelChoice_NullOrEmptyChoices_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, SettingsView.ResolveInitialModelChoice(null, "sonnet"));
            Assert.AreEqual(string.Empty, SettingsView.ResolveInitialModelChoice(new List<string>(), "sonnet"));
        }

        [Test]
        public void ResolveInitialModelChoice_NullCurrentValue_IsTreatedAsEmpty()
        {
            var choices = new List<string> { string.Empty, "sonnet" };
            Assert.AreEqual(string.Empty, SettingsView.ResolveInitialModelChoice(choices, null));
        }

        // -- IsCompleteAgentOverride (fix round: gate the reconnect hint/
        // auto-apply on the row actually contributing to a generated
        // .claude/agents/*.md file) -------------------------------------------------

        [Test]
        public void IsCompleteAgentOverride_BothFieldsFilled_IsTrue()
        {
            var entry = new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" };
            Assert.IsTrue(SettingsView.IsCompleteAgentOverride(entry));
        }

        [Test]
        public void IsCompleteAgentOverride_OnlyNameFilled_IsFalse()
        {
            var entry = new AgentModelOverride { agentName = "general-purpose", modelAlias = string.Empty };
            Assert.IsFalse(SettingsView.IsCompleteAgentOverride(entry));
        }

        [Test]
        public void IsCompleteAgentOverride_OnlyModelFilled_IsFalse()
        {
            var entry = new AgentModelOverride { agentName = string.Empty, modelAlias = "haiku" };
            Assert.IsFalse(SettingsView.IsCompleteAgentOverride(entry));
        }

        [Test]
        public void IsCompleteAgentOverride_BothEmpty_IsFalse()
        {
            var entry = new AgentModelOverride { agentName = string.Empty, modelAlias = string.Empty };
            Assert.IsFalse(SettingsView.IsCompleteAgentOverride(entry));
        }

        [Test]
        public void IsCompleteAgentOverride_Null_IsFalse()
        {
            Assert.IsFalse(SettingsView.IsCompleteAgentOverride(null));
        }

        [Test]
        public void IsCompleteAgentOverride_NullFields_AreTreatedAsEmpty()
        {
            var entry = new AgentModelOverride { agentName = null, modelAlias = null };
            Assert.IsFalse(SettingsView.IsCompleteAgentOverride(entry));
        }

        // -- HasDuplicateAgentOverrideNames (fix round: warn instead of
        // silently collapsing to the last matching row, R07 sections 5/9.2) --------

        [Test]
        public void HasDuplicateAgentOverrideNames_NullOrEmptyOrSingle_IsFalse()
        {
            Assert.IsFalse(SettingsView.HasDuplicateAgentOverrideNames(null));
            Assert.IsFalse(SettingsView.HasDuplicateAgentOverrideNames(new List<AgentModelOverride>()));
            Assert.IsFalse(SettingsView.HasDuplicateAgentOverrideNames(new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            }));
        }

        [Test]
        public void HasDuplicateAgentOverrideNames_DistinctNames_IsFalse()
        {
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" },
                new AgentModelOverride { agentName = "Explore", modelAlias = "haiku" }
            };
            Assert.IsFalse(SettingsView.HasDuplicateAgentOverrideNames(overrides));
        }

        [Test]
        public void HasDuplicateAgentOverrideNames_SameNameTwice_IsTrue()
        {
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" },
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "sonnet" }
            };
            Assert.IsTrue(SettingsView.HasDuplicateAgentOverrideNames(overrides));
        }

        [Test]
        public void HasDuplicateAgentOverrideNames_SameNameWithWhitespaceDifference_IsTrue()
        {
            // AddAgentOverrideRow's nameField handler trims on every edit, so
            // a persisted/legacy row with untrimmed whitespace must still be
            // caught as a duplicate of its trimmed twin.
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" },
                new AgentModelOverride { agentName = "  general-purpose  ", modelAlias = "sonnet" }
            };
            Assert.IsTrue(SettingsView.HasDuplicateAgentOverrideNames(overrides));
        }

        [Test]
        public void HasDuplicateAgentOverrideNames_MultipleBlankNameRows_AreIgnored()
        {
            // A freshly-added row (OnAddAgentOverrideClicked) has an empty
            // agentName; several of those sitting in the list at once must
            // never be flagged as "duplicates" of each other.
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = string.Empty, modelAlias = string.Empty },
                new AgentModelOverride { agentName = string.Empty, modelAlias = string.Empty }
            };
            Assert.IsFalse(SettingsView.HasDuplicateAgentOverrideNames(overrides));
        }

        // -- BuildAgentTypeChoices (v0.9.0, docs/design-notes/2026-08-01-
        // model-settings-rework.md work item C) -------------------------------

        [Test]
        public void BuildAgentTypeChoices_AllSourcesEmpty_IsEmpty()
        {
            List<string> choices = SettingsView.BuildAgentTypeChoices(
                new List<string>(), null, new List<AgentModelOverride>());
            Assert.IsEmpty(choices);
        }

        [Test]
        public void BuildAgentTypeChoices_NullLists_AreTreatedAsEmpty()
        {
            Assert.IsEmpty(SettingsView.BuildAgentTypeChoices(null, null, null));
        }

        [Test]
        public void BuildAgentTypeChoices_CachedCatalogFirst_ThenLiveAgents_ThenOverrideNames()
        {
            var cached = new List<string> { "general-purpose", "Explore" };
            var live = new[] { "Plan" };
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "uap-worker", modelAlias = "haiku" }
            };
            List<string> choices = SettingsView.BuildAgentTypeChoices(cached, live, overrides);
            CollectionAssert.AreEqual(
                new[] { "general-purpose", "Explore", "Plan", "uap-worker" }, choices);
        }

        [Test]
        public void BuildAgentTypeChoices_Dedupes_AcrossAllThreeSources()
        {
            var cached = new List<string> { "general-purpose" };
            var live = new[] { "general-purpose", "Explore" };
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "Explore", modelAlias = "haiku" },
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "sonnet" }
            };
            List<string> choices = SettingsView.BuildAgentTypeChoices(cached, live, overrides);
            CollectionAssert.AreEqual(new[] { "general-purpose", "Explore" }, choices);
        }

        [Test]
        public void BuildAgentTypeChoices_KeepsExistingEntryName_EvenWhenCatalogAndLiveAreEmpty()
        {
            // A stale override the CLI no longer reports (or before any
            // connection this project has ever made) must stay selectable.
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "custom-legacy-agent", modelAlias = "haiku" }
            };
            List<string> choices = SettingsView.BuildAgentTypeChoices(
                new List<string>(), null, overrides);
            CollectionAssert.AreEqual(new[] { "custom-legacy-agent" }, choices);
        }

        [Test]
        public void BuildAgentTypeChoices_IgnoresNullOrEmptyOverrideNames()
        {
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = string.Empty, modelAlias = string.Empty },
                new AgentModelOverride { agentName = null, modelAlias = string.Empty }
            };
            List<string> choices = SettingsView.BuildAgentTypeChoices(
                new List<string>(), null, overrides);
            Assert.IsEmpty(choices);
        }

        // -- AreStringListsEqual (2026-08-01 review fix: OnHubChanged now
        // calls RefreshAgentOverrideRowsIfCatalogChanged, which uses this
        // comparison to decide whether an already-open Settings view's
        // agent-override rows need a rebuild when the live/cached agent-
        // type catalog changes -- previously OnHubChanged never rebuilt
        // them at all, so a catalog arriving after the view was already
        // built left the per-type override rows' name field permanently
        // stuck as a free-text TextField / with stale PopupField choices)
        // -----------------------------------------------------------------

        [Test]
        public void AreStringListsEqual_BothNull_IsTrue()
        {
            Assert.IsTrue(SettingsView.AreStringListsEqual(null, null));
        }

        [Test]
        public void AreStringListsEqual_OneNull_IsFalse()
        {
            Assert.IsFalse(SettingsView.AreStringListsEqual(new List<string> { "a" }, null));
            Assert.IsFalse(SettingsView.AreStringListsEqual(null, new List<string> { "a" }));
        }

        [Test]
        public void AreStringListsEqual_SameContentsSameOrder_IsTrue()
        {
            Assert.IsTrue(SettingsView.AreStringListsEqual(
                new List<string> { "Explore", "Plan" }, new List<string> { "Explore", "Plan" }));
        }

        [Test]
        public void AreStringListsEqual_DifferentOrder_IsFalse()
        {
            // Order matters here (BuildAgentTypeChoices' contract is
            // "stable order", so a reorder is itself a meaningful change
            // that must trigger a rebuild, not be treated as equal).
            Assert.IsFalse(SettingsView.AreStringListsEqual(
                new List<string> { "Explore", "Plan" }, new List<string> { "Plan", "Explore" }));
        }

        [Test]
        public void AreStringListsEqual_ExtraEntry_IsFalse()
        {
            // The exact scenario the fix targets: a live/cached catalog
            // arriving after the view was already built with an empty (or
            // smaller) catalog must be detected as a change.
            Assert.IsFalse(SettingsView.AreStringListsEqual(
                new List<string>(), new List<string> { "general-purpose" }));
        }

        [Test]
        public void AreStringListsEqual_BothEmpty_IsTrue()
        {
            Assert.IsTrue(SettingsView.AreStringListsEqual(new List<string>(), new List<string>()));
        }

        // -- HasSubagentModelPrecedenceConflict (v0.9.0, docs/design-notes/
        // 2026-08-01-model-settings-rework.md section 4.2) ---------------------

        [Test]
        public void HasSubagentModelPrecedenceConflict_SubagentModelEmpty_IsFalse()
        {
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "haiku" }
            };
            Assert.IsFalse(SettingsView.HasSubagentModelPrecedenceConflict(string.Empty, overrides));
            Assert.IsFalse(SettingsView.HasSubagentModelPrecedenceConflict(null, overrides));
        }

        [Test]
        public void HasSubagentModelPrecedenceConflict_SubagentModelSet_NoOverrides_IsFalse()
        {
            Assert.IsFalse(SettingsView.HasSubagentModelPrecedenceConflict("haiku", new List<AgentModelOverride>()));
            Assert.IsFalse(SettingsView.HasSubagentModelPrecedenceConflict("haiku", null));
        }

        [Test]
        public void HasSubagentModelPrecedenceConflict_SubagentModelSet_OnlyIncompleteOverrides_IsFalse()
        {
            // A blank placeholder row (just added, not yet filled in) is
            // not itself a real conflict.
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = string.Empty },
                new AgentModelOverride { agentName = string.Empty, modelAlias = "haiku" }
            };
            Assert.IsFalse(SettingsView.HasSubagentModelPrecedenceConflict("haiku", overrides));
        }

        [Test]
        public void HasSubagentModelPrecedenceConflict_SubagentModelSet_OneCompleteOverride_IsTrue()
        {
            var overrides = new List<AgentModelOverride>
            {
                new AgentModelOverride { agentName = "general-purpose", modelAlias = "sonnet" }
            };
            Assert.IsTrue(SettingsView.HasSubagentModelPrecedenceConflict("haiku", overrides));
        }

        // -- Account status line (docs/design-notes/2026-08-02-auth-in-panel.md;
        //    env-token live finding 2026-08-02) --------------------------------

        [Test]
        public void BuildLoggedInStatusText_WithEmail_UsesTheTwoPlaceholderFormat()
        {
            var status = new Colloid.AgentPanel.Core.Process.AuthStatus
            {
                IsAvailable = true, LoggedIn = true,
                Email = "user@example.com", SubscriptionType = "max", AuthMethod = "claude.ai"
            };
            string text = SettingsView.BuildLoggedInStatusText(status);
            StringAssert.Contains("user@example.com", text);
            StringAssert.Contains("max", text);
        }

        [Test]
        public void BuildLoggedInStatusText_EnvTokenAuth_NoEmail_FallsBackToPlainLoggedInLine()
        {
            // Measured live 2026-08-02: authMethod "oauth_token" carries no
            // email/subscription, so the format string would render
            // "Logged in as  (unknown plan)." without this fallback.
            var status = new Colloid.AgentPanel.Core.Process.AuthStatus
            {
                IsAvailable = true, LoggedIn = true, AuthMethod = "oauth_token"
            };
            string text = SettingsView.BuildLoggedInStatusText(status);
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsAccountLoggedInNoDetail, text);
            StringAssert.DoesNotContain("(", text);
        }

        // -- ShortenResolvedModel (v0.11.0, docs/design-notes/2026-08-02-
        // subagent-model-precedence.md section 3.1, work item C) -----------

        [Test]
        public void ShortenResolvedModel_BracketSuffix_MovesIntoParentheses()
        {
            Assert.AreEqual("opus-5 (1m)", SettingsView.ShortenResolvedModel("claude-opus-5[1m]"));
        }

        [Test]
        public void ShortenResolvedModel_NoBracket_StripsClaudePrefixOnly()
        {
            Assert.AreEqual("sonnet-5", SettingsView.ShortenResolvedModel("claude-sonnet-5"));
        }

        [Test]
        public void ShortenResolvedModel_LongVersionedId_StripsClaudePrefixOnly()
        {
            Assert.AreEqual("haiku-4-5-20251001",
                SettingsView.ShortenResolvedModel("claude-haiku-4-5-20251001"));
        }

        [Test]
        public void ShortenResolvedModel_NoClaudePrefix_LeftUnchanged()
        {
            Assert.AreEqual("some-other-model", SettingsView.ShortenResolvedModel("some-other-model"));
        }

        [Test]
        public void ShortenResolvedModel_NullOrEmpty_ReturnsNull()
        {
            Assert.IsNull(SettingsView.ShortenResolvedModel(null));
            Assert.IsNull(SettingsView.ShortenResolvedModel(string.Empty));
        }

        // -- FindCatalogEntry / FormatDefaultModelEntryLabel (v0.11.0,
        // work item C) -------------------------------------------------

        [Test]
        public void FindCatalogEntry_ValuePresent_ReturnsIt()
        {
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "sonnet", displayName = "Sonnet" },
                new ModelCatalogEntry { value = "default", displayName = "Default (recommended)" }
            };
            ModelCatalogEntry found = SettingsView.FindCatalogEntry(catalog, "default");
            Assert.IsNotNull(found);
            Assert.AreEqual("Default (recommended)", found.displayName);
        }

        [Test]
        public void FindCatalogEntry_ValueAbsent_ReturnsNull()
        {
            var catalog = new List<ModelCatalogEntry> { new ModelCatalogEntry { value = "sonnet" } };
            Assert.IsNull(SettingsView.FindCatalogEntry(catalog, "opus"));
        }

        [Test]
        public void FindCatalogEntry_NullCatalog_ReturnsNull()
        {
            Assert.IsNull(SettingsView.FindCatalogEntry(null, "sonnet"));
        }

        [Test]
        public void FormatDefaultModelEntryLabel_ResolvedModelPresent_ShowsShortLabel()
        {
            var entry = new ModelCatalogEntry
            {
                value = "default",
                displayName = "Default (recommended)",
                resolvedModel = "claude-opus-5[1m]"
            };
            string label = SettingsView.FormatDefaultModelEntryLabel(entry);
            StringAssert.Contains("opus-5 (1m)", label);
        }

        [Test]
        public void FormatDefaultModelEntryLabel_NoResolvedModel_FallsBackToDisplayNameUntouched()
        {
            // Old cache written before v0.11.0 (ModelCatalogEntry.resolvedModel
            // did not exist yet -- deserializes to empty string, R07 section 13
            // tolerance requirement).
            var entry = new ModelCatalogEntry { value = "default", displayName = "Default (recommended)" };
            Assert.AreEqual("Default (recommended)", SettingsView.FormatDefaultModelEntryLabel(entry));
        }

        [Test]
        public void FormatDefaultModelEntryLabel_NullEntry_FallsBackToTheLiteralCatalogValue()
        {
            Assert.AreEqual(SettingsView.DefaultCatalogEntryValue,
                SettingsView.FormatDefaultModelEntryLabel(null));
        }

        // -- ResolveDefaultModelHintText (v0.11.0, work item C) -------------

        [Test]
        public void ResolveDefaultModelHintText_EmptyCatalog_ReturnsNoCatalogHint()
        {
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsDefaultModelHintNoCatalog,
                SettingsView.ResolveDefaultModelHintText(new List<ModelCatalogEntry>(), string.Empty));
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsDefaultModelHintNoCatalog,
                SettingsView.ResolveDefaultModelHintText(null, string.Empty));
        }

        [Test]
        public void ResolveDefaultModelHintText_SelectedEntryHasDescription_ShowsIt()
        {
            var catalog = new List<ModelCatalogEntry>
            {
                new ModelCatalogEntry { value = "default", description = "Use the default model" }
            };
            Assert.AreEqual("Use the default model",
                SettingsView.ResolveDefaultModelHintText(catalog, "default"));
        }

        [Test]
        public void ResolveDefaultModelHintText_SelectedEntryHasNoDescription_FallsBackToGenericHint()
        {
            var catalog = new List<ModelCatalogEntry> { new ModelCatalogEntry { value = "sonnet" } };
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsDefaultModelHint,
                SettingsView.ResolveDefaultModelHintText(catalog, "sonnet"));
        }

        [Test]
        public void ResolveDefaultModelHintText_NoMatchingEntry_FallsBackToGenericHint()
        {
            var catalog = new List<ModelCatalogEntry> { new ModelCatalogEntry { value = "sonnet" } };
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsDefaultModelHint,
                SettingsView.ResolveDefaultModelHintText(catalog, "some-manual-alias"));
        }

        // -- FormatSubagentCostPolicyOption (v0.11.0, docs/design-notes/
        // 2026-08-02-subagent-model-precedence.md section 3.2) -------------

        [Test]
        public void FormatSubagentCostPolicyOption_AgentDecides_ReturnsExpectedLabel()
        {
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsSubagentCostPolicyOptionAgentDecides,
                SettingsView.FormatSubagentCostPolicyOption(SubagentCostPolicy.AgentDecides));
        }

        [Test]
        public void FormatSubagentCostPolicyOption_HaikuForSimpleTasks_ReturnsExpectedLabel()
        {
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsSubagentCostPolicyOptionHaikuForSimpleTasks,
                SettingsView.FormatSubagentCostPolicyOption(SubagentCostPolicy.HaikuForSimpleTasks));
        }

        [Test]
        public void IsEnvTokenAuth_OnlyForLoggedInOauthTokenMethod()
        {
            Assert.IsTrue(SettingsView.IsEnvTokenAuth(new Colloid.AgentPanel.Core.Process.AuthStatus
                { IsAvailable = true, LoggedIn = true, AuthMethod = "oauth_token" }));
            Assert.IsFalse(SettingsView.IsEnvTokenAuth(new Colloid.AgentPanel.Core.Process.AuthStatus
                { IsAvailable = true, LoggedIn = true, AuthMethod = "claude.ai" }));
            Assert.IsFalse(SettingsView.IsEnvTokenAuth(new Colloid.AgentPanel.Core.Process.AuthStatus
                { IsAvailable = true, LoggedIn = false, AuthMethod = "oauth_token" }));
            Assert.IsFalse(SettingsView.IsEnvTokenAuth(new Colloid.AgentPanel.Core.Process.AuthStatus
                { IsAvailable = false, LoggedIn = true, AuthMethod = "oauth_token" }));
            Assert.IsFalse(SettingsView.IsEnvTokenAuth(null));
        }

        // -- FormatClaudeAuthOption / IsApiKeyAuth (v0.40.0, docs/design-
        // notes/2026-09-10-claude-api-key-auth-passthrough.md) -------------

        [Test]
        public void FormatClaudeAuthOption_Auto_ReturnsExpectedLabel()
        {
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsClaudeAuthOptionAuto,
                SettingsView.FormatClaudeAuthOption(Colloid.AgentPanel.Core.Process.ClaudeAuthMode.Auto));
        }

        [Test]
        public void FormatClaudeAuthOption_SubscriptionOnly_ReturnsExpectedLabel()
        {
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.SettingsClaudeAuthOptionSubscriptionOnly,
                SettingsView.FormatClaudeAuthOption(
                    Colloid.AgentPanel.Core.Process.ClaudeAuthMode.SubscriptionOnly));
        }

        [Test]
        public void IsApiKeyAuth_OnlyForNonNoneApiKeySource()
        {
            Assert.IsTrue(SettingsView.IsApiKeyAuth(MakeSystemInitMessage("ANTHROPIC_API_KEY")));
            Assert.IsTrue(SettingsView.IsApiKeyAuth(MakeSystemInitMessage("apiKeyHelper")));
            Assert.IsFalse(SettingsView.IsApiKeyAuth(MakeSystemInitMessage("none")));
            Assert.IsFalse(SettingsView.IsApiKeyAuth(MakeSystemInitMessage(string.Empty)));
            Assert.IsFalse(SettingsView.IsApiKeyAuth(null));
        }

        private static SystemInitMessage MakeSystemInitMessage(string apiKeySource)
        {
            string json = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\","
                + "\"apiKeySource\":\"" + apiKeySource + "\"}";
            JsonNode node;
            string error;
            JsonParser.TryParse(json, out node, out error);
            return SystemInitMessage.FromJson(node, null);
        }

        // -- DescribeUloopCaveat (Phase 5c task item 2: caveat-code-to-text
        // mapping, including the "unknown code still renders something
        // honest" fallback) ---------------------------------------------------

        [Test]
        public void DescribeUloopCaveat_Null_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, SettingsView.DescribeUloopCaveat(null));
        }

        [Test]
        public void DescribeUloopCaveat_VccCode_MapsToVccText()
        {
            var caveat = new UloopInstallCaveat { Code = UloopInstaller.CaveatCodeVccProject };
            Assert.AreEqual(L10n.S.SettingsUloopCaveatVcc, SettingsView.DescribeUloopCaveat(caveat));
        }

        [Test]
        public void DescribeUloopCaveat_OfflineCode_MapsToOfflineText()
        {
            var caveat = new UloopInstallCaveat { Code = UloopInstaller.CaveatCodeOffline };
            Assert.AreEqual(L10n.S.SettingsUloopCaveatOffline, SettingsView.DescribeUloopCaveat(caveat));
        }

        [Test]
        public void DescribeUloopCaveat_ManifestUnreadableCode_MapsToManifestUnreadableText()
        {
            var caveat = new UloopInstallCaveat { Code = UloopInstaller.CaveatCodeManifestUnreadable };
            Assert.AreEqual(L10n.S.SettingsUloopCaveatManifestUnreadable,
                SettingsView.DescribeUloopCaveat(caveat));
        }

        [Test]
        public void DescribeUloopCaveat_ScopedRegistryConflictCode_MapsToRegistryConflictText()
        {
            var caveat = new UloopInstallCaveat { Code = UloopInstaller.CaveatCodeScopedRegistryConflict };
            Assert.AreEqual(L10n.S.SettingsUloopCaveatRegistryConflict,
                SettingsView.DescribeUloopCaveat(caveat));
        }

        [Test]
        public void DescribeUloopCaveat_UnknownCode_FallsBackToTheRawCode()
        {
            // "An UNKNOWN code must still render something honest rather
            // than an empty row" -- task spec. A future UloopInstaller
            // caveat this switch has not been updated for must stay
            // visible, not vanish.
            var caveat = new UloopInstallCaveat { Code = "some-future-caveat-code" };
            Assert.AreEqual("some-future-caveat-code", SettingsView.DescribeUloopCaveat(caveat));
        }

        [Test]
        public void DescribeUloopCaveat_EmptyCode_FallsBackToEmptyString()
        {
            var caveat = new UloopInstallCaveat { Code = string.Empty };
            Assert.AreEqual(string.Empty, SettingsView.DescribeUloopCaveat(caveat));
        }

        [Test]
        public void DescribeUloopCaveat_DetailPresent_IsAppendedInParentheses()
        {
            var caveat = new UloopInstallCaveat
            {
                Code = UloopInstaller.CaveatCodeManifestUnreadable,
                Detail = "C:/proj/Packages/manifest.json"
            };
            string result = SettingsView.DescribeUloopCaveat(caveat);
            StringAssert.StartsWith(L10n.S.SettingsUloopCaveatManifestUnreadable, result);
            StringAssert.Contains("C:/proj/Packages/manifest.json", result);
        }

        [Test]
        public void DescribeUloopCaveat_DetailEmpty_NoParenthesesAppended()
        {
            var caveat = new UloopInstallCaveat
            {
                Code = UloopInstaller.CaveatCodeOffline,
                Detail = string.Empty
            };
            Assert.AreEqual(L10n.S.SettingsUloopCaveatOffline, SettingsView.DescribeUloopCaveat(caveat));
        }

        // -- BuildUloopAllowedPatterns / BuildUloopDisallowedPatterns (Phase 5c
        // task item 3: the exact allow/disallow pattern strings the preset
        // button writes -- "getting this wrong re-enables a command that has
        // frozen this editor before") ------------------------------------------

        [Test]
        public void BuildUloopAllowedPatterns_ContainsBothFormsForEachSafeSubcommand()
        {
            List<string> patterns = SettingsView.BuildUloopAllowedPatterns();
            CollectionAssert.Contains(patterns, "Bash(uloop compile)");
            CollectionAssert.Contains(patterns, "Bash(uloop compile *)");
            CollectionAssert.Contains(patterns, "Bash(uloop get-logs)");
            CollectionAssert.Contains(patterns, "Bash(uloop get-logs *)");
            CollectionAssert.Contains(patterns, "Bash(uloop list)");
            CollectionAssert.Contains(patterns, "Bash(uloop list *)");
            CollectionAssert.Contains(patterns, "Bash(uloop run-tests)");
            CollectionAssert.Contains(patterns, "Bash(uloop run-tests *)");
            Assert.AreEqual(8, patterns.Count);
        }

        [Test]
        public void BuildUloopAllowedPatterns_NeverContainsABareWildcard()
        {
            // The preset used to write a single "Bash(uloop *)" and rely on
            // disallowedTools to carve the dangerous subcommands back out.
            // That makes safety depend on an unmeasured pattern grammar, and
            // fails OPEN when the grammar disagrees. An allowlist fails closed
            // (an unmatched pattern is merely a permission card), which is why
            // the wildcard must never come back.
            CollectionAssert.DoesNotContain(SettingsView.BuildUloopAllowedPatterns(), "Bash(uloop *)");
        }

        [Test]
        public void StripBroadUloopAllowEntries_RemovesTheWildcardTheOldPresetWrote()
        {
            // The upgrade path that made the allowlist rewrite worthless.
            // MergeToolListAdditions only appends, so without the strip the
            // eight narrow patterns land next to a surviving Bash(uloop *)
            // and change nothing -- execute-dynamic-code stays auto-allowed
            // (measured, design note section 9.2) while the button's help
            // text promises it always asks.
            var existing = new List<string> { "Read", "Bash(uloop *)", "Edit" };
            List<string> stripped = SettingsView.StripBroadUloopAllowEntries(existing);
            CollectionAssert.AreEqual(new[] { "Read", "Edit" }, stripped);
        }

        [Test]
        public void StripBroadUloopAllowEntries_RemovesSpacingAndCasingVariants()
        {
            var existing = new List<string>
            {
                "Bash(uloop*)", "bash(ULOOP  *)", "  Bash(uloop **)  ",
            };
            CollectionAssert.IsEmpty(SettingsView.StripBroadUloopAllowEntries(existing));
        }

        [Test]
        public void StripBroadUloopAllowEntries_KeepsNarrowAndUnrelatedEntries()
        {
            // Only a wildcard over the WHOLE uloop command is broad. The
            // preset's own entries must survive being re-applied, and a
            // deliberate narrow entry is the user's to keep.
            var existing = new List<string>
            {
                "Bash(uloop compile)", "Bash(uloop compile *)", "Bash(uloop update)",
                "Bash(git *)", "Read", "Bash(uloopx *)",
            };
            CollectionAssert.AreEqual(existing, SettingsView.StripBroadUloopAllowEntries(existing));
        }

        [Test]
        public void StripBroadUloopAllowEntries_NullExisting_ReturnsEmpty()
        {
            CollectionAssert.IsEmpty(SettingsView.StripBroadUloopAllowEntries(null));
        }

        [Test]
        public void PresetAppliedOverAPreExistingWildcard_LeavesNoWildcardBehind()
        {
            // End-to-end over the two pure halves the button actually calls,
            // in the same order. This is the assertion whose absence let the
            // regression ship: the existing coverage either inspected a
            // freshly built list or merged onto an empty one, and both stay
            // green with the wildcard present.
            var before = new List<string> { "Bash(uloop *)" };
            List<string> after = SettingsView.MergeToolListAdditions(
                SettingsView.StripBroadUloopAllowEntries(before),
                SettingsView.BuildUloopAllowedPatterns());

            CollectionAssert.DoesNotContain(after, "Bash(uloop *)");
            CollectionAssert.Contains(after, "Bash(uloop compile)");
            foreach (string entry in after)
            {
                StringAssert.DoesNotContain("execute-dynamic-code", entry);
            }
        }

        [Test]
        public void BuildUloopAllowedPatterns_NeverAllowsADangerousOrArbitraryCodeSubcommand()
        {
            // The load-bearing assertion of the whole preset: nothing that can
            // freeze this editor, and nothing that runs arbitrary code, may be
            // reachable without a permission card.
            string[] mustNeverBeAllowed = { "update", "sync", "launch", "execute-dynamic-code" };
            foreach (string pattern in SettingsView.BuildUloopAllowedPatterns())
            {
                foreach (string forbidden in mustNeverBeAllowed)
                {
                    StringAssert.DoesNotContain(forbidden, pattern);
                }
            }
        }

        [Test]
        public void BuildUloopDisallowedPatterns_ContainsBothFormsForEachDangerousSubcommand()
        {
            List<string> patterns = SettingsView.BuildUloopDisallowedPatterns();
            CollectionAssert.Contains(patterns, "Bash(uloop update)");
            CollectionAssert.Contains(patterns, "Bash(uloop update *)");
            CollectionAssert.Contains(patterns, "Bash(uloop sync)");
            CollectionAssert.Contains(patterns, "Bash(uloop sync *)");
            CollectionAssert.Contains(patterns, "Bash(uloop launch)");
            CollectionAssert.Contains(patterns, "Bash(uloop launch *)");
            Assert.AreEqual(6, patterns.Count);
        }

        [Test]
        public void BuildUloopDisallowedPatterns_NeverMentionsExecuteDynamicCode()
        {
            // execute-dynamic-code must always ASK, not be blocked outright --
            // it must be absent from disallowedTools entirely (see
            // BuildUloopDisallowedPatterns' own doc comment for why keeping
            // it out of BOTH lists is what makes it "always ask").
            List<string> patterns = SettingsView.BuildUloopDisallowedPatterns();
            foreach (string pattern in patterns)
            {
                StringAssert.DoesNotContain("execute-dynamic-code", pattern);
            }
        }

        // -- MergeToolListAdditions (Phase 5c task item 3: idempotent
        // allowedTools/disallowedTools append -- "pressing twice must not
        // duplicate entries") ---------------------------------------------------

        [Test]
        public void MergeToolListAdditions_NullExisting_ReturnsJustTheAdditions()
        {
            List<string> result = SettingsView.MergeToolListAdditions(null, new[] { "Bash(uloop *)" });
            CollectionAssert.AreEqual(new[] { "Bash(uloop *)" }, result);
        }

        [Test]
        public void MergeToolListAdditions_NullToAdd_ReturnsACopyOfExistingUnchanged()
        {
            var existing = new List<string> { "Read", "Edit" };
            List<string> result = SettingsView.MergeToolListAdditions(existing, null);
            CollectionAssert.AreEqual(new[] { "Read", "Edit" }, result);
            Assert.AreNotSame(existing, result, "must return a copy, never the same list instance");
        }

        [Test]
        public void MergeToolListAdditions_NewEntries_AreAppendedAfterExisting()
        {
            var existing = new List<string> { "Read" };
            List<string> result = SettingsView.MergeToolListAdditions(existing, new[] { "Bash(uloop *)" });
            CollectionAssert.AreEqual(new[] { "Read", "Bash(uloop *)" }, result);
        }

        [Test]
        public void MergeToolListAdditions_PressedTwice_DoesNotDuplicateEntries()
        {
            List<string> firstPass = SettingsView.MergeToolListAdditions(
                new List<string>(), SettingsView.BuildUloopDisallowedPatterns());
            List<string> secondPass = SettingsView.MergeToolListAdditions(
                firstPass, SettingsView.BuildUloopDisallowedPatterns());
            CollectionAssert.AreEqual(firstPass, secondPass);
            Assert.AreEqual(6, secondPass.Count);
        }

        [Test]
        public void MergeToolListAdditions_DoesNotMutateTheOriginalExistingList()
        {
            var existing = new List<string> { "Read" };
            SettingsView.MergeToolListAdditions(existing, new[] { "Edit" });
            CollectionAssert.AreEqual(new[] { "Read" }, existing);
        }

        [Test]
        public void MergeToolListAdditions_NullOrEmptyCandidates_AreSkipped()
        {
            List<string> result = SettingsView.MergeToolListAdditions(
                new List<string> { "Read" }, new[] { string.Empty, null, "Edit" });
            CollectionAssert.AreEqual(new[] { "Read", "Edit" }, result);
        }

        // -- AppendUloopGuidanceSnippet (Phase 5c task item 4: idempotent
        // custom-instructions append -- "never duplicate the snippet, never
        // clobber what the user wrote") -----------------------------------------

        [Test]
        public void AppendUloopGuidanceSnippet_EmptyExisting_ReturnsJustTheSnippet()
        {
            Assert.AreEqual(SettingsView.UloopGuidanceSnippet, SettingsView.AppendUloopGuidanceSnippet(string.Empty));
        }

        [Test]
        public void AppendUloopGuidanceSnippet_NullExisting_ReturnsJustTheSnippet()
        {
            Assert.AreEqual(SettingsView.UloopGuidanceSnippet, SettingsView.AppendUloopGuidanceSnippet(null));
        }

        [Test]
        public void AppendUloopGuidanceSnippet_ExistingTextWithNoSnippet_AppendsAfterOneBlankLine()
        {
            string result = SettingsView.AppendUloopGuidanceSnippet("My own custom instructions.");
            Assert.AreEqual("My own custom instructions.\n\n" + SettingsView.UloopGuidanceSnippet, result);
        }

        [Test]
        public void AppendUloopGuidanceSnippet_ExistingTextWithTrailingWhitespace_StillExactlyOneBlankLine()
        {
            string result = SettingsView.AppendUloopGuidanceSnippet("My own custom instructions.\n\n\n   ");
            Assert.AreEqual("My own custom instructions.\n\n" + SettingsView.UloopGuidanceSnippet, result);
        }

        [Test]
        public void AppendUloopGuidanceSnippet_AlreadyPresent_IsLeftCompletelyUnchanged()
        {
            string alreadyApplied = "My own custom instructions.\n\n" + SettingsView.UloopGuidanceSnippet;
            Assert.AreEqual(alreadyApplied, SettingsView.AppendUloopGuidanceSnippet(alreadyApplied));
        }

        [Test]
        public void AppendUloopGuidanceSnippet_PressedTwice_DoesNotDuplicate()
        {
            string once = SettingsView.AppendUloopGuidanceSnippet("Existing text.");
            string twice = SettingsView.AppendUloopGuidanceSnippet(once);
            Assert.AreEqual(once, twice);
        }

        [Test]
        public void AppendUloopGuidanceSnippet_NeverClobbersExistingText()
        {
            string existing = "The user's own hand-written notes, verbatim.";
            string result = SettingsView.AppendUloopGuidanceSnippet(existing);
            StringAssert.Contains(existing, result);
        }

        // -- BuildManifestDiffText (Phase 5c task item 2: "the diff is the
        // whole point of this UI ... do not collapse or truncate it into a
        // summary") --------------------------------------------------------

        [Test]
        public void BuildManifestDiffText_IdenticalTexts_EveryLineIsContext()
        {
            string diff = SettingsView.BuildManifestDiffText("a\nb\nc\n", "a\nb\nc\n");
            Assert.AreEqual("  a\n  b\n  c\n", diff);
        }

        [Test]
        public void BuildManifestDiffText_LineAppendedAtTheEnd_ShowsOnlyThatLineAsAdded()
        {
            string diff = SettingsView.BuildManifestDiffText("a\nb\n", "a\nb\nc\n");
            Assert.AreEqual("  a\n  b\n+ c\n", diff);
        }

        [Test]
        public void BuildManifestDiffText_LineInsertedInTheMiddle_KeepsSharedPrefixAndSuffixAsContext()
        {
            string diff = SettingsView.BuildManifestDiffText("a\nc\n", "a\nb\nc\n");
            Assert.AreEqual("  a\n+ b\n  c\n", diff);
        }

        [Test]
        public void BuildManifestDiffText_CompletelyDifferentTexts_ShowsEveryLineRemovedAndAdded_NothingDropped()
        {
            string diff = SettingsView.BuildManifestDiffText("x\ny\n", "p\nq\n");
            Assert.AreEqual("- x\n- y\n+ p\n+ q\n", diff);
        }

        [Test]
        public void BuildManifestDiffText_TrailingNewlineOnBothSides_DoesNotAddASpuriousBlankContextLine()
        {
            // Without the trailing-newline trim in SplitManifestTextIntoLines,
            // this would render "  a\n  \n" (a spurious blank context line).
            string diff = SettingsView.BuildManifestDiffText("a\n", "a\n");
            Assert.AreEqual("  a\n", diff);
        }

        [Test]
        public void BuildManifestDiffText_NoTrailingNewline_StillDiffsCorrectly()
        {
            string diff = SettingsView.BuildManifestDiffText("a\nb", "a\nb\nc");
            Assert.AreEqual("  a\n  b\n+ c\n", diff);
        }

        [Test]
        public void BuildManifestDiffText_BothNull_ReturnsEmptyStringWithoutThrowing()
        {
            Assert.AreEqual(string.Empty, SettingsView.BuildManifestDiffText(null, null));
        }

        [Test]
        public void BuildManifestDiffText_BeforeNull_TreatsItAsEmpty()
        {
            string diff = SettingsView.BuildManifestDiffText(null, "x\n");
            Assert.AreEqual("+ x\n", diff);
        }

        // -- AddHint / AddWarning (docs/design-notes/2026-08-04-settings-
        // annotation-load.md): the inline-hint-vs-tooltip mechanism. Every
        // assertion here reads the `.tooltip` PROPERTY directly rather
        // than dispatching a TooltipEvent -- the design note's own section
        // 3 measured that a TooltipEvent resolves to an empty string on an
        // UNATTACHED element (which is all a plain `new VisualElement()`
        // ever is in this EditMode-only suite; SettingsView has no test-
        // instantiation seam for a live, attached panel -- see AddHint's
        // own doc comment). Asserting the event payload here would produce
        // a test that passes whether or not the tooltip was ever set,
        // exactly the trap that note calls out by name. ------------------

        [Test]
        public void AddHint_TwoArg_AddsLabelWithHintClass_AndNeverTouchesTooltip()
        {
            var parent = new VisualElement();

            SettingsView.AddHint(parent, "Runs on port 53760.");

            Label hint = parent.Q<Label>(className: "uap-settings-hint");
            Assert.IsNotNull(hint);
            Assert.AreEqual("Runs on port 53760.", hint.text);
            Assert.AreEqual(1, parent.childCount);
            Assert.IsTrue(string.IsNullOrEmpty(parent.tooltip),
                "the 2-arg overload must never set a tooltip -- every existing call site relies on that");
        }

        [Test]
        public void AddHint_ThreeArg_NonEmptyText_AddsLabel_AndSetsTooltipOnParent()
        {
            var parent = new VisualElement();

            SettingsView.AddHint(parent, "Short inline line.", "The long explanation that used to be inline.");

            Label hint = parent.Q<Label>(className: "uap-settings-hint");
            Assert.IsNotNull(hint);
            Assert.AreEqual("Short inline line.", hint.text);
            Assert.AreEqual(1, parent.childCount);
            Assert.AreEqual("The long explanation that used to be inline.", parent.tooltip);
        }

        [Test]
        public void AddHint_ThreeArg_EmptyText_AddsNoLabel_ButStillSetsTooltip()
        {
            // A control whose explanation is entirely hover-only must not
            // leave an empty label eating vertical space -- the whole
            // point of moving the long text off the card in the first
            // place (design note section 4).
            var parent = new VisualElement();

            SettingsView.AddHint(parent, string.Empty, "Hover for the full explanation.");

            Assert.AreEqual(0, parent.childCount);
            Assert.AreEqual("Hover for the full explanation.", parent.tooltip);
        }

        [Test]
        public void AddHint_ThreeArg_NullText_AddsNoLabel_ButStillSetsTooltip()
        {
            var parent = new VisualElement();

            SettingsView.AddHint(parent, null, "Hover for the full explanation.");

            Assert.AreEqual(0, parent.childCount);
            Assert.AreEqual("Hover for the full explanation.", parent.tooltip);
        }

        [Test]
        public void AddHint_ThreeArg_EmptyTooltip_LeavesTooltipUnset()
        {
            var parent = new VisualElement();

            SettingsView.AddHint(parent, "Short inline line.", string.Empty);

            Assert.AreEqual(1, parent.childCount);
            Assert.IsTrue(string.IsNullOrEmpty(parent.tooltip));
        }

        [Test]
        public void AddWarning_TwoArg_AppliesBothHintAndWarningClasses()
        {
            var parent = new VisualElement();

            SettingsView.AddWarning(parent, "Enabling this skips the script-validation gate.");

            Label warning = parent.Q<Label>(className: "uap-settings-hint--warning");
            Assert.IsNotNull(warning);
            Assert.IsTrue(warning.ClassListContains("uap-settings-hint"),
                "a warning label must still carry the base hint class for shared sizing/spacing");
            Assert.IsTrue(warning.ClassListContains("uap-settings-hint--warning"));
            Assert.AreEqual(1, parent.childCount);
            Assert.IsTrue(string.IsNullOrEmpty(parent.tooltip));
        }

        [Test]
        public void AddWarning_ThreeArg_AddsLabel_AndSetsTooltipOnParent()
        {
            var parent = new VisualElement();

            SettingsView.AddWarning(parent, "Enabling this skips the script-validation gate.",
                "The full explanation of what the gate normally catches.");

            Label warning = parent.Q<Label>(className: "uap-settings-hint--warning");
            Assert.IsNotNull(warning);
            Assert.AreEqual(1, parent.childCount);
            Assert.AreEqual("The full explanation of what the gate normally catches.", parent.tooltip);
        }

        [Test]
        public void AddWarning_ThreeArg_EmptyTooltip_LeavesTooltipUnset()
        {
            var parent = new VisualElement();

            SettingsView.AddWarning(parent, "Enabling this skips the script-validation gate.", string.Empty);

            Assert.AreEqual(1, parent.childCount);
            Assert.IsTrue(string.IsNullOrEmpty(parent.tooltip));
        }

        // -- UICODE-4: coalesced hub refresh + disk-probe gates ---------------------

        [Test]
        public void ShouldResolveCliStatus_NeverResolved_True()
        {
            Assert.IsTrue(SettingsView.ShouldResolveCliStatus(null, string.Empty));
            Assert.IsTrue(SettingsView.ShouldResolveCliStatus(null, "C:/cli/claude.exe"));
        }

        [Test]
        public void ShouldResolveCliStatus_UnchangedPath_False()
        {
            Assert.IsFalse(SettingsView.ShouldResolveCliStatus(
                "C:/cli/claude.exe", "C:/cli/claude.exe"));
            Assert.IsFalse(SettingsView.ShouldResolveCliStatus(string.Empty, string.Empty));
        }

        [Test]
        public void ShouldResolveCliStatus_ChangedPath_True()
        {
            Assert.IsTrue(SettingsView.ShouldResolveCliStatus(
                "C:/cli/claude.exe", "D:/other/claude.exe"));
            Assert.IsTrue(SettingsView.ShouldResolveCliStatus(string.Empty, "C:/cli/claude.exe"));
        }

        [Test]
        public void ShouldResolveCliStatus_NullCurrent_MeansEmpty()
        {
            Assert.IsFalse(SettingsView.ShouldResolveCliStatus(string.Empty, null));
        }

        // -- Source scans (SettingsView cannot be constructed in EditMode
        // tests: RefreshAll's account refresh spawns the auth probe, which
        // the standing test rule forbids) ------------------------------------------

        private const string SettingsViewCsPath =
            "Packages/jp.colloid.unity-agent-panel/Editor/UI/SettingsView.cs";

        private static string ReadSettingsViewSource()
        {
            return System.IO.File.ReadAllText(System.IO.Path.GetFullPath(SettingsViewCsPath));
        }

        /// <summary>Method body from its signature line to the next
        /// same-indentation member declaration.</summary>
        private static string ExtractMember(string source, string signatureFragment)
        {
            int start = source.IndexOf(signatureFragment, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "member not found: " + signatureFragment);
            int end = source.IndexOf("\n        private ", start + signatureFragment.Length,
                System.StringComparison.Ordinal);
            int endInternal = source.IndexOf("\n        internal ", start + signatureFragment.Length,
                System.StringComparison.Ordinal);
            if (endInternal >= 0 && (end < 0 || endInternal < end))
            {
                end = endInternal;
            }
            return end >= 0 ? source.Substring(start, end - start) : source.Substring(start);
        }

        [Test]
        public void SourceScan_OnHubChanged_OnlyMarksDirty()
        {
            string body = ExtractMember(ReadSettingsViewSource(), "private void OnHubChanged()");
            StringAssert.Contains("_hubDirty = true", body);
            Assert.IsFalse(body.Contains("RefreshCliStatus"),
                "the per-delta handler must never stat the disk (UICODE-4)");
            Assert.IsFalse(body.Contains("RefreshUloopSection"),
                "the per-delta handler must never scan the manifest (UICODE-4)");
        }

        [Test]
        public void SourceScan_CoalescedTick_UsesTheGatedRefreshes()
        {
            string body = ExtractMember(ReadSettingsViewSource(), "private void RefreshIfHubDirty()");
            StringAssert.Contains("RefreshCliStatusIfPathChanged()", body);
            StringAssert.Contains("RefreshUloopSectionThrottled()", body);
        }

        // -- UXIA-3/4: the auto-approve level lives with the permission
        // controls -------------------------------------------------------------------

        [Test]
        public void SourceScan_AutoApproveField_BuiltInConversation_NotInUapOps()
        {
            string source = ReadSettingsViewSource();

            string conversation = ExtractMember(source,
                "private void BuildConversationSection(VisualElement parent)");
            StringAssert.Contains("BuildAutoApproveLevelField(section)", conversation,
                "the level must sit in the permission cluster, right after the mode");
            StringAssert.Contains("BuildDangerZone(section)", conversation);

            string uapOps = ExtractMember(source,
                "private void BuildUapOpsSection(VisualElement parent)");
            Assert.IsFalse(uapOps.Contains("PopupField<UapAutoApproveLevel>"),
                "UapOps must no longer build the level field");
            StringAssert.Contains("SettingsAutoApproveMovedHint", uapOps,
                "its old home keeps a cross-reference hint");
        }

        [Test]
        public void SourceScan_ExactlyOneAutoApproveFieldConstruction()
        {
            string source = ReadSettingsViewSource();
            int first = source.IndexOf("new PopupField<UapAutoApproveLevel>",
                System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(first, 0);
            Assert.AreEqual(-1, source.IndexOf("new PopupField<UapAutoApproveLevel>",
                first + 1, System.StringComparison.Ordinal),
                "one construction site (BuildAutoApproveLevelField) -- two would let "
                + "the surfaces drift apart again");
        }

        // -- UXO-6: login sub-card feedback ------------------------------

        // The expected value rides as a string: the enum is internal (repo
        // convention for view-state enums) and an NUnit test method must be
        // public, so the enum cannot appear in this signature (CS0051 --
        // measured in CI).
        [TestCase(false, false, false, "Idle")]
        [TestCase(false, true, true, "Idle")]
        [TestCase(true, false, false, "Starting")]
        [TestCase(true, true, false, "WaitingForCode")]
        [TestCase(true, true, true, "Verifying")]
        [TestCase(true, false, true, "Verifying")]
        public void ResolveAccountLoginFeedback_Table(
            bool inFlight, bool waiting, bool submitted, string expected)
        {
            Assert.AreEqual(expected,
                SettingsView.ResolveAccountLoginFeedback(inFlight, waiting, submitted).ToString());
        }

    }
}
