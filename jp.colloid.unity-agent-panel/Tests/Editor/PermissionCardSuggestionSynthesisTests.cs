using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-seam tests for the 2026-08-02 design note section 2 B3 "Always
    /// allow for MCP tools" fix, extended by design note
    /// 2026-09-10-auto-approve-all-tools-and-lean-auto-continue section 2:
    /// PermissionCard.CollectSuggestions/SynthesizeMcpAlwaysAllowRule/
    /// ContainsUnscopedRuleFor/TryExtractAddRuleToolName/
    /// PersistAcceptedRuleIfApplicable are internal statics exercised
    /// directly here (InternalsVisibleTo), no UI construction needed.
    ///
    /// The 2026-09-10 change widened synthesis from "mcp__* tools only"
    /// to "every non-question tool": CollectSuggestions now inserts a
    /// tool-wide addRules suggestion at index 0 for ANY tool (Bash, Read,
    /// Edit, WebFetch, mcp__* -- everything) unless the CLI's own
    /// suggestions already contain an UNSCOPED rule for that exact tool
    /// name (ContainsUnscopedRuleFor). A scoped CLI suggestion (a path or
    /// command prefix) does NOT count as unscoped, so the tool-wide rule
    /// still gets synthesized alongside it. A question card
    /// (RequiresUserInteraction) never gets synthesis -- it is answered,
    /// not allowed.
    /// </summary>
    [TestFixture]
    public class PermissionCardSuggestionSynthesisTests
    {
        private List<string> _originalAllowedTools;

        [SetUp]
        public void SetUp()
        {
            _originalAllowedTools = new List<string>(PanelStateStore.instance.Settings.allowedTools);
        }

        [TearDown]
        public void TearDown()
        {
            PanelStateStore.instance.Settings.allowedTools = _originalAllowedTools;
        }

        private static CanUseToolRequest MakeRequest(string toolName, JsonNode permissionSuggestions)
        {
            return MakeRequest(toolName, permissionSuggestions, requiresUserInteraction: false);
        }

        private static CanUseToolRequest MakeRequest(
            string toolName, JsonNode permissionSuggestions, bool requiresUserInteraction)
        {
            JsonNode node = JsonNode.NewObject()
                .Set("tool_name", toolName)
                .Set("input", JsonNode.NewObject())
                .Set("requires_user_interaction", requiresUserInteraction);
            if (permissionSuggestions != null)
            {
                node.Set("permission_suggestions", permissionSuggestions);
            }
            return CanUseToolRequest.FromJson(node);
        }

        // -- CollectSuggestions synthesis --------------------------------------

        [Test]
        public void CollectSuggestions_McpToolWithNoSuggestions_SynthesizesOneRule()
        {
            CanUseToolRequest request = MakeRequest("mcp__unity-ops__uap_ping", null);

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("addRules", result[0]["type"].AsString(null));
        }

        [Test]
        public void CollectSuggestions_McpToolWithEmptySuggestionsArray_SynthesizesOneRule()
        {
            CanUseToolRequest request = MakeRequest("mcp__unity-ops__uap_ping", JsonNode.NewArray());

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void CollectSuggestions_McpToolWithExistingSetModeSuggestion_AlsoSynthesizesToolWideRuleFirst()
        {
            // Design note 2026-09-10 section 2: a setMode suggestion is not
            // an UNSCOPED addRules rule for this tool, so it does not stop
            // synthesis -- the tool-wide rule is still inserted, and goes
            // FIRST, so the CLI's own (narrower/mode-switching) suggestion
            // stays available as the second menu entry.
            JsonNode cliSuggestions = JsonNode.NewArray().Add(
                JsonNode.NewObject().Set("type", "setMode").Set("mode", "acceptEdits")
                    .Set("destination", "session"));
            CanUseToolRequest request = MakeRequest("mcp__unity-ops__uap_ping", cliSuggestions);

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(2, result.Count,
                "the synthesized tool-wide rule is added ALONGSIDE the CLI's setMode suggestion");
            Assert.AreEqual("addRules", result[0]["type"].AsString(null),
                "the synthesized rule must come first");
            Assert.AreEqual("mcp__unity-ops__uap_ping", result[0]["rules"][0]["toolName"].AsString(null));
            Assert.AreEqual("setMode", result[1]["type"].AsString(null));
        }

        [Test]
        public void CollectSuggestions_McpToolWithExistingAddRulesSuggestion_DoesNotDuplicate()
        {
            JsonNode cliSuggestions = JsonNode.NewArray().Add(
                JsonNode.NewObject().Set("type", "addRules")
                    .Set("rules", JsonNode.NewArray().Add(
                        JsonNode.NewObject().Set("toolName", "mcp__toy__ping")))
                    .Set("behavior", "allow").Set("destination", "localSettings"));
            CanUseToolRequest request = MakeRequest("mcp__toy__ping", cliSuggestions);

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void CollectSuggestions_NonMcpToolWithNoSuggestions_SynthesizesToolWideRule()
        {
            // Design note 2026-09-10 section 2: synthesis widened from
            // "mcp__* tools only" to every non-question tool -- a non-MCP
            // tool like Bash with no CLI suggestions now ALSO gets a
            // tool-wide "always allow Bash" rule, so the Always button
            // appears for it too.
            CanUseToolRequest request = MakeRequest("Bash", null);

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("addRules", result[0]["type"].AsString(null));
            Assert.AreEqual("Bash", result[0]["rules"][0]["toolName"].AsString(null));
        }

        [Test]
        public void CollectSuggestions_NullRequest_ReturnsEmpty()
        {
            Assert.AreEqual(0, PermissionCard.CollectSuggestions(null).Count);
        }

        [Test]
        public void CollectSuggestions_RequiresUserInteraction_NeverSynthesizes()
        {
            // A question card (AskUserQuestion) is answered, not allowed --
            // design note 2026-09-10 section 2 explicitly excludes it.
            CanUseToolRequest request = MakeRequest("AskUserQuestion", null, requiresUserInteraction: true);

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void CollectSuggestions_ExistingScopedRuleForSameTool_StillSynthesizesToolWideRuleFirst()
        {
            // A SCOPED CLI suggestion (here, a path-restricted Read rule)
            // is not an unscoped rule for "Read" -- ContainsUnscopedRuleFor
            // must not treat it as one, so the tool-wide rule is still
            // synthesized and placed first.
            JsonNode cliSuggestions = JsonNode.NewArray().Add(
                JsonNode.NewObject().Set("type", "addRules")
                    .Set("rules", JsonNode.NewArray().Add(
                        JsonNode.NewObject().Set("toolName", "Read").Set("ruleContent", "//x/**")))
                    .Set("behavior", "allow").Set("destination", "localSettings"));
            CanUseToolRequest request = MakeRequest("Read", cliSuggestions);

            List<JsonNode> result = PermissionCard.CollectSuggestions(request);

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("Read", result[0]["rules"][0]["toolName"].AsString(null));
            Assert.IsNull(result[0]["rules"][0]["ruleContent"].AsString(null),
                "the synthesized rule must be unscoped -- no ruleContent");
        }

        // -- ContainsUnscopedRuleFor -----------------------------------------

        [Test]
        public void ContainsUnscopedRuleFor_UnscopedRuleForSameTool_ReturnsTrue()
        {
            var suggestions = new List<JsonNode>
            {
                PermissionCard.SynthesizeMcpAlwaysAllowRule("Read")
            };

            Assert.IsTrue(PermissionCard.ContainsUnscopedRuleFor(suggestions, "Read"));
        }

        [Test]
        public void ContainsUnscopedRuleFor_ScopedRuleForSameTool_ReturnsFalse()
        {
            JsonNode scoped = JsonNode.NewObject()
                .Set("type", "addRules")
                .Set("rules", JsonNode.NewArray().Add(
                    JsonNode.NewObject().Set("toolName", "Read").Set("ruleContent", "//x/**")))
                .Set("behavior", "allow").Set("destination", "localSettings");

            Assert.IsFalse(PermissionCard.ContainsUnscopedRuleFor(
                new List<JsonNode> { scoped }, "Read"),
                "a path-scoped rule does not satisfy the unscoped check for the same tool");
        }

        [Test]
        public void ContainsUnscopedRuleFor_UnscopedRuleForADifferentTool_ReturnsFalse()
        {
            var suggestions = new List<JsonNode>
            {
                PermissionCard.SynthesizeMcpAlwaysAllowRule("Write")
            };

            Assert.IsFalse(PermissionCard.ContainsUnscopedRuleFor(suggestions, "Read"));
        }

        [Test]
        public void ContainsUnscopedRuleFor_EmptyList_ReturnsFalse()
        {
            Assert.IsFalse(PermissionCard.ContainsUnscopedRuleFor(new List<JsonNode>(), "Read"));
        }

        [Test]
        public void ContainsUnscopedRuleFor_NonAddRulesSuggestion_ReturnsFalse()
        {
            JsonNode setMode = JsonNode.NewObject().Set("type", "setMode")
                .Set("mode", "acceptEdits").Set("destination", "session");

            Assert.IsFalse(PermissionCard.ContainsUnscopedRuleFor(
                new List<JsonNode> { setMode }, "Read"));
        }

        // -- SynthesizeMcpAlwaysAllowRule ---------------------------------------

        [Test]
        public void SynthesizeMcpAlwaysAllowRule_MatchesMeasuredWireShape()
        {
            // docs/research/08-mcp-transport.md: a real captured CLI-native
            // suggestion for an MCP tool has exactly this shape.
            JsonNode rule = PermissionCard.SynthesizeMcpAlwaysAllowRule("mcp__unity-ops__uap_ping");

            Assert.AreEqual("addRules", rule["type"].AsString(null));
            Assert.AreEqual("allow", rule["behavior"].AsString(null));
            Assert.AreEqual("localSettings", rule["destination"].AsString(null));
            Assert.IsTrue(rule["rules"].IsArray);
            Assert.AreEqual("mcp__unity-ops__uap_ping", rule["rules"][0]["toolName"].AsString(null));
        }

        // -- TryExtractAddRuleToolName -------------------------------------------

        [Test]
        public void TryExtractAddRuleToolName_ExtractsFromAddRulesShape()
        {
            JsonNode rule = PermissionCard.SynthesizeMcpAlwaysAllowRule("mcp__unity-ops__uap_asset_find");

            string toolName;
            bool found = PermissionCard.TryExtractAddRuleToolName(rule, out toolName);

            Assert.IsTrue(found);
            Assert.AreEqual("mcp__unity-ops__uap_asset_find", toolName);
        }

        [Test]
        public void TryExtractAddRuleToolName_ReturnsFalse_ForSetModeShape()
        {
            JsonNode setMode = JsonNode.NewObject().Set("type", "setMode")
                .Set("mode", "acceptEdits").Set("destination", "session");

            string toolName;
            bool found = PermissionCard.TryExtractAddRuleToolName(setMode, out toolName);

            Assert.IsFalse(found);
            Assert.IsNull(toolName);
        }

        [Test]
        public void TryExtractAddRuleToolName_ReturnsFalse_ForNull()
        {
            string toolName;
            Assert.IsFalse(PermissionCard.TryExtractAddRuleToolName(null, out toolName));
        }

        // -- PersistAcceptedRuleIfApplicable -------------------------------------

        [Test]
        public void PersistAcceptedRuleIfApplicable_AddsToolName_WhenAbsent()
        {
            PanelStateStore.instance.Settings.allowedTools = new List<string>();
            JsonNode rule = PermissionCard.SynthesizeMcpAlwaysAllowRule("mcp__unity-ops__uap_ping");

            PermissionCard.PersistAcceptedRuleIfApplicable(rule);

            CollectionAssert.Contains(PanelStateStore.instance.Settings.allowedTools,
                "mcp__unity-ops__uap_ping");
        }

        [Test]
        public void PersistAcceptedRuleIfApplicable_DoesNotDuplicate_WhenAlreadyPresent()
        {
            PanelStateStore.instance.Settings.allowedTools =
                new List<string> { "mcp__unity-ops__uap_ping" };
            JsonNode rule = PermissionCard.SynthesizeMcpAlwaysAllowRule("mcp__unity-ops__uap_ping");

            PermissionCard.PersistAcceptedRuleIfApplicable(rule);

            int count = 0;
            foreach (string t in PanelStateStore.instance.Settings.allowedTools)
            {
                if (t == "mcp__unity-ops__uap_ping")
                {
                    count++;
                }
            }
            Assert.AreEqual(1, count, "the same tool name must never be persisted twice");
        }

        [Test]
        public void PersistAcceptedRuleIfApplicable_NoOp_ForNonAddRulesSuggestion()
        {
            PanelStateStore.instance.Settings.allowedTools = new List<string>();
            JsonNode setMode = JsonNode.NewObject().Set("type", "setMode")
                .Set("mode", "acceptEdits").Set("destination", "session");

            PermissionCard.PersistAcceptedRuleIfApplicable(setMode);

            Assert.AreEqual(0, PanelStateStore.instance.Settings.allowedTools.Count);
        }

        // -- ruleContent (2026-08-12: "always allow on WebFetch never sticks") --

        private static JsonNode WebFetchDomainSuggestion(string destination)
        {
            // The domain-scoped shape WebFetch suggestions carry. ruleContent
            // used to be discarded wholesale by the extraction, which broke
            // two ways at once: the persisted rule (when one was persisted at
            // all) was the bare tool name -- WIDER than what the user
            // accepted -- and the menu label hid the scope.
            return JsonNode.NewObject()
                .Set("type", "addRules")
                .Set("rules", JsonNode.NewArray()
                    .Add(JsonNode.NewObject()
                        .Set("toolName", "WebFetch")
                        .Set("ruleContent", "domain:docs.unity3d.com")))
                .Set("behavior", "allow")
                .Set("destination", destination);
        }

        [Test]
        public void ExtractAddRuleStrings_RuleContent_ProducesScopedRuleString()
        {
            List<string> rules = PermissionCard.ExtractAddRuleStrings(
                WebFetchDomainSuggestion("session"));

            CollectionAssert.AreEqual(new[] { "WebFetch(domain:docs.unity3d.com)" }, rules,
                "the settings-file grammar is Tool(content); dropping the content grants MORE"
                + " than the user accepted");
        }

        [Test]
        public void ExtractAddRuleStrings_NoRuleContent_ProducesBareToolName()
        {
            JsonNode bare = PermissionCard.SynthesizeMcpAlwaysAllowRule("mcp__x__y");
            CollectionAssert.AreEqual(new[] { "mcp__x__y" },
                PermissionCard.ExtractAddRuleStrings(bare));
        }

        [Test]
        public void ExtractAddRuleStrings_MultipleRules_ReturnsAllInOrder()
        {
            JsonNode suggestion = JsonNode.NewObject()
                .Set("type", "addRules")
                .Set("rules", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("toolName", "WebFetch")
                        .Set("ruleContent", "domain:a.example"))
                    .Add(JsonNode.NewObject().Set("toolName", "WebSearch")))
                .Set("behavior", "allow")
                .Set("destination", "localSettings");

            CollectionAssert.AreEqual(new[] { "WebFetch(domain:a.example)", "WebSearch" },
                PermissionCard.ExtractAddRuleStrings(suggestion));
        }

        [Test]
        public void PersistAcceptedRuleIfApplicable_ScopedRule_PersistsTheScopedForm()
        {
            // The panel-side persistence is what makes "always allow" outlive
            // the CLI process. This panel restarts the CLI on every domain
            // reload and settings auto-apply, so a destination:"session"
            // grant that would last a terminal user for hours dies here in
            // minutes -- persisting into allowedTools (spawned as
            // --allowedTools every time) is the durable half.
            PanelStateStore.instance.Settings.allowedTools = new List<string>();

            PermissionCard.PersistAcceptedRuleIfApplicable(WebFetchDomainSuggestion("session"));

            CollectionAssert.AreEqual(new[] { "WebFetch(domain:docs.unity3d.com)" },
                PanelStateStore.instance.Settings.allowedTools);
        }

        [Test]
        public void PersistAcceptedRuleIfApplicable_NeverPersistsTheBareNameForAScopedRule()
        {
            // The old behavior: bare "WebFetch" -- a wider grant than the
            // accepted rule. Pinned as its own test because a regression
            // here is a silent permission WIDENING, the worst direction.
            PanelStateStore.instance.Settings.allowedTools = new List<string>();

            PermissionCard.PersistAcceptedRuleIfApplicable(WebFetchDomainSuggestion("session"));

            CollectionAssert.DoesNotContain(
                PanelStateStore.instance.Settings.allowedTools, "WebFetch");
        }

        [Test]
        public void PersistAcceptedRuleIfApplicable_ScopedRule_IsIdempotent()
        {
            PanelStateStore.instance.Settings.allowedTools = new List<string>();

            PermissionCard.PersistAcceptedRuleIfApplicable(WebFetchDomainSuggestion("session"));
            PermissionCard.PersistAcceptedRuleIfApplicable(WebFetchDomainSuggestion("session"));

            Assert.AreEqual(1, PanelStateStore.instance.Settings.allowedTools.Count);
        }

        // -- UXA-6: the Always menu speaks human; persistence speaks wire

        [Test]
        public void FormatRuleForDisplay_McpRule_NoScope_NamesServerAndSaysAllCalls()
        {
            Assert.AreEqual(
                L10n.F(L10n.S.PermRuleAllCallsSuffixFmt, "unity-ops: uap_scene_create_object"),
                PermissionCard.FormatRuleForDisplay("mcp__unity-ops__uap_scene_create_object"));
        }

        [Test]
        public void FormatRuleForDisplay_McpRule_WithScope_KeepsTheScopeVerbatim()
        {
            Assert.AreEqual("unity-ops: uap_x(foo:bar)",
                PermissionCard.FormatRuleForDisplay("mcp__unity-ops__uap_x(foo:bar)"));
        }

        [Test]
        public void FormatRuleForDisplay_NonMcpRules_PassThroughUnchanged()
        {
            Assert.AreEqual("WebFetch", PermissionCard.FormatRuleForDisplay("WebFetch"));
            Assert.AreEqual("WebFetch(domain:example.com)",
                PermissionCard.FormatRuleForDisplay("WebFetch(domain:example.com)"));
            Assert.AreEqual("Bash(git status:*)",
                PermissionCard.FormatRuleForDisplay("Bash(git status:*)"));
        }

        /// <summary>Same all-or-nothing malformed contract as the parser underneath: never half-formatted, never an all-calls suffix on a shape that did not parse.</summary>
        [Test]
        public void FormatRuleForDisplay_MalformedShapes_ComeBackUntouched()
        {
            Assert.AreEqual("mcp__server__", PermissionCard.FormatRuleForDisplay("mcp__server__"));
            Assert.AreEqual("mcp____tool", PermissionCard.FormatRuleForDisplay("mcp____tool"));
            Assert.AreEqual(string.Empty, PermissionCard.FormatRuleForDisplay(null));
            Assert.AreEqual(string.Empty, PermissionCard.FormatRuleForDisplay(string.Empty));
        }

        /// <summary>
        /// The wire-vs-display split UXA-6 must never blur: what gets
        /// PERSISTED for an accepted suggestion is still the raw rule
        /// grammar, not the pretty label.
        /// </summary>
        [Test]
        public void FormatRuleForDisplay_NeverLeaksIntoExtraction()
        {
            JsonNode suggestion = PermissionCard.SynthesizeMcpAlwaysAllowRule(
                "mcp__unity-ops__uap_ping");
            string toolName;
            Assert.IsTrue(PermissionCard.TryExtractAddRuleToolName(suggestion, out toolName));
            Assert.AreEqual("mcp__unity-ops__uap_ping", toolName,
                "persistence must keep the wire name");
        }

        // -- Always menu labels never contain '/' (GenericMenu submenu separator)

        [Test]
        public void FlattenMenuLabel_ReplacesEverySlash_WithTheStandIn()
        {
            string flat = PermissionCard.FlattenMenuLabel("Always allow Read(/home/user/**)");
            Assert.AreEqual(-1, flat.IndexOf('/'), "a '/' would open a submenu");
            Assert.AreEqual("Always allow Read(" + IconLoader.GlyphMenuSlash
                + "home" + IconLoader.GlyphMenuSlash + "user"
                + IconLoader.GlyphMenuSlash + "**)", flat);
        }

        [Test]
        public void FlattenMenuLabel_NoSlash_IsUnchanged()
        {
            Assert.AreEqual("Always allow Bash(git status:*)",
                PermissionCard.FlattenMenuLabel("Always allow Bash(git status:*)"));
            Assert.AreEqual(string.Empty, PermissionCard.FlattenMenuLabel(null));
            Assert.AreEqual(string.Empty, PermissionCard.FlattenMenuLabel(string.Empty));
        }

        /// <summary>
        /// The flattening is display-only: the rule persisted for a
        /// path-scoped suggestion keeps its real slashes, or the stored
        /// rule would never match the CLI's grammar.
        /// </summary>
        [Test]
        public void FlattenMenuLabel_NeverLeaksIntoPersistence()
        {
            PanelStateStore.instance.Settings.allowedTools = new List<string>();
            JsonNode suggestion = JsonNode.NewObject()
                .Set("type", "addRules")
                .Set("rules", JsonNode.NewArray().Add(JsonNode.NewObject()
                    .Set("toolName", "Read")
                    .Set("ruleContent", "/home/user/**")))
                .Set("behavior", "allow")
                .Set("destination", "localSettings");

            PermissionCard.PersistAcceptedRuleIfApplicable(suggestion);

            Assert.AreEqual(1, PanelStateStore.instance.Settings.allowedTools.Count);
            Assert.AreEqual("Read(/home/user/**)", PanelStateStore.instance.Settings.allowedTools[0]);
        }
    }
}
